import Foundation

/// 사용자가 정한 시각에 메이트에게 시킬 일.
public struct Alarm: Identifiable, Codable, Equatable {
    public enum Repeat: Codable, Equatable {
        case daily
        case weekdays                 // 월~금
        case days([Int])              // Calendar weekday: 1=일 … 7=토
        case once(String)             // yyyy-MM-dd

        public var description: String {
            switch self {
            case .daily: return "매일"
            case .weekdays: return "평일"
            case .days(let days):
                let names = ["", "일", "월", "화", "수", "목", "금", "토"]
                return days.sorted().map { names[$0] }.joined(separator: "·")
            case .once(let day): return day
            }
        }
    }

    /// prompt 가 이 값이면 shared/prompts/daily-report.md 내용을 쓴다.
    public static let dailyReportPrompt = "{{daily-report}}"

    public var id: UUID
    public var title: String
    public var prompt: String
    public var hour: Int
    public var minute: Int
    public var repeatRule: Repeat
    public var enabled: Bool
    public var lastRunDay: String?
    /// 있으면 시각 대신 이 이벤트가 생길 때 울린다 (hour·minute·repeatRule 은 쓰지 않음).
    public var trigger: Trigger?
    /// 알림 그룹 이름. nil 이면 "그룹 없음".
    public var group: String?
    /// 받을 곳. nil 이면 켜짐 (예전 알림 파일 호환)
    public var notifyMac: Bool?
    public var notifyPanel: Bool?
    public var allowWrite: Bool?
    public var command: String?
    public var timeoutMinutes: Int?
    public var useWorkSessions: Bool?
    public var useRolePipeline: Bool?

    public var workSessions: Bool { trigger != nil && canWrite && (useWorkSessions ?? false) }
    public var rolePipeline: Bool { workSessions && (useRolePipeline ?? false) }

    public static let timeoutChoices = [30, 60, 90, 120]

    public var automationTimeout: TimeInterval {
        TimeInterval(max(1, timeoutMinutes ?? Int(ClaudeRunner.automationTimeout / 60)) * 60)
    }

    public static func displayCommand(_ command: String, home: String = NSHomeDirectory(), limit: Int = 40) -> String {
        let trimmed = command.trimmingCharacters(in: .whitespacesAndNewlines)
        let uv = try! NSRegularExpression(pattern: #"^(?:\S*/)?uv\s+--directory\s+(?:"[^"]*"|'[^']*'|\S+)\s+run\s+(?:--quiet\s+)?(.+)$"#)
        var shown = trimmed
        if let m = uv.firstMatch(in: trimmed, range: NSRange(trimmed.startIndex..., in: trimmed)),
           let r = Range(m.range(at: 1), in: trimmed) {
            shown = String(trimmed[r])
        } else if !home.isEmpty {
            shown = trimmed.replacingOccurrences(of: home, with: "~")
        }
        return shown.count > limit ? String(shown.prefix(limit)) + "…" : shown
    }

    public var shellCommand: String? {
        guard trigger == nil, let c = command?.trimmingCharacters(in: .whitespacesAndNewlines), !c.isEmpty else { return nil }
        return c
    }

    /// Mac 알림(알림 센터 배너)으로 받을지
    public var toMac: Bool { notifyMac ?? true }
    /// 미리 알림 창(화면 오른쪽 아래 카드)으로 받을지
    public var toPanel: Bool { notifyPanel ?? true }
    public var canWrite: Bool { allowWrite ?? false }

    public init(id: UUID = UUID(), title: String, prompt: String, hour: Int, minute: Int,
                repeatRule: Repeat, enabled: Bool = true, lastRunDay: String? = nil, trigger: Trigger? = nil,
                group: String? = nil) {
        self.trigger = trigger
        self.group = group
        self.id = id
        self.title = title
        self.prompt = prompt
        self.hour = hour
        self.minute = minute
        self.repeatRule = repeatRule
        self.enabled = enabled
        self.lastRunDay = lastRunDay
    }

    public var timeText: String { String(format: "%02d:%02d", hour, minute) }

    public static func defaultReviewRequestTrigger() -> Alarm {
        Alarm(title: "GitHub · 리뷰 요청", prompt: "", hour: 0, minute: 0, repeatRule: .daily,
              trigger: Trigger(system: "github", event: "github.review_requested"))
    }

    public static func defaultDailyReport(hour: Int = 10, minute: Int = 0, lastRunDay: String? = nil) -> Alarm {
        Alarm(title: "미완료 Jira 보고", prompt: dailyReportPrompt, hour: hour, minute: minute,
              repeatRule: .daily, lastRunDay: lastRunDay)
    }

    /// 지금 실행할지. 오늘이 반복 대상 날이고, 시각이 지났고, 오늘 아직 안 돌렸으면 실행한다
    /// (잠자기로 시각을 놓쳤어도 깨어난 뒤 그날 1회).
    public func shouldRun(now: Date, calendar: Calendar) -> Bool {
        guard enabled, trigger == nil, appliesOn(now, calendar: calendar) else { return false }
        return ReportSchedule.shouldRun(now: now, lastRunDay: lastRunDay, hour: hour, minute: minute, calendar: calendar)
    }

    /// 새로 만들거나 시각을 바꾼 알림. 오늘 시각이 이미 지났으면 오늘은 건너뛰고 다음부터 울린다
    /// (오후에 "매일 9시" 를 등록하자마자 울리지 않게).
    public mutating func resetSchedule(now: Date, calendar: Calendar) {
        let passed = (calendar.component(.hour, from: now), calendar.component(.minute, from: now)) >= (hour, minute)
        lastRunDay = passed ? ReportSchedule.dayString(now, calendar: calendar) : nil
    }

    public func appliesOn(_ date: Date, calendar: Calendar) -> Bool {
        let weekday = calendar.component(.weekday, from: date)
        switch repeatRule {
        case .daily: return true
        case .weekdays: return (2...6).contains(weekday)
        case .days(let days): return days.contains(weekday)
        case .once(let day): return ReportSchedule.dayString(date, calendar: calendar) == day
        }
    }
}

public enum AlarmStore {
    public static var file: URL { AppPaths.localFile("alarms.json") }

    public static func load(settings: AppSettings, file: URL = file, ready: Bool = AppPaths.localReady) -> [Alarm] {
        guard ready else { return [] }
        if let data = try? Data(contentsOf: file), let alarms = try? JSONDecoder().decode([Alarm].self, from: data) {
            return alarms
        }
        return [.defaultDailyReport(hour: settings.reportHour, minute: settings.reportMinute,
                                    lastRunDay: settings.lastReportDay)]
    }

    public static func save(_ alarms: [Alarm], file: URL = file, ready: Bool = AppPaths.localReady) {
        guard ready else { return }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? encoder.encode(alarms).write(to: file, options: .atomic)
    }

    public static var groupsFile: URL { AppPaths.localFile("alarm-groups.json") }

    public static func loadGroups() -> [String] {
        guard AppPaths.localReady else { return [] }
        return (try? Data(contentsOf: groupsFile)).flatMap { try? JSONDecoder().decode([String].self, from: $0) } ?? []
    }

    public static func promptFilePath(_ prompt: String, home: String = NSHomeDirectory()) -> String? {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("{{file:"), trimmed.hasSuffix("}}") else { return nil }
        let raw = String(trimmed.dropFirst(7).dropLast(2)).trimmingCharacters(in: .whitespaces)
        guard !raw.isEmpty else { return nil }
        return raw.hasPrefix("~/") ? home + "/" + raw.dropFirst(2) : raw
    }

    public static func saveGroups(_ groups: [String]) {
        guard AppPaths.localReady else { return }
        try? JSONEncoder().encode(groups).write(to: groupsFile, options: .atomic)
    }

    public static func promptName(_ prompt: String) -> String? {
        let trimmed = prompt.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("{{prompt:"), trimmed.hasSuffix("}}") else { return nil }
        let raw = String(trimmed.dropFirst(9).dropLast(2)).trimmingCharacters(in: .whitespaces)
        return raw.isEmpty ? nil : raw
    }

    public static func resolvePrompt(_ alarm: Alarm, settings: AppSettings, promptsDir: URL? = nil) -> String? {
        if let name = promptName(alarm.prompt) {
            let url = promptsDir.map { Rules.promptFile(name, in: $0) } ?? Rules.resolvedPrompt(name)
            return url.flatMap { try? String(contentsOf: $0, encoding: .utf8) }
        }
        if let path = promptFilePath(alarm.prompt) {
            return try? String(contentsOfFile: path, encoding: .utf8)
        }
        guard alarm.prompt == Alarm.dailyReportPrompt else { return alarm.prompt }
        return try? String(contentsOf: AppPaths.sharedFile("app/daily-report.md", settings: settings), encoding: .utf8)
    }
}
