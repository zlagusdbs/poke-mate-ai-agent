import Foundation

public struct AlarmRunRecord: Codable, Equatable, Sendable {
    public var lastAt: Date
    public var result: String
    public var failed: Bool
    public var day: String
    public var todayCount: Int
    public var output: String?
    public var usage: RunUsage?

    public init(lastAt: Date, result: String, failed: Bool, day: String, todayCount: Int, output: String? = nil, usage: RunUsage? = nil) {
        self.lastAt = lastAt
        self.result = result
        self.failed = failed
        self.day = day
        self.todayCount = todayCount
        self.output = output
        self.usage = usage
    }
}

public enum AlarmRunLog {
    public static var file: URL { AppPaths.dataDir.appendingPathComponent("alarm-runs.json") }

    public static func short(_ message: String, failed: Bool) -> String {
        if failed { return "실패" }
        if message.contains("새로 생긴 것 없") { return "새 건 없음" }
        if message.contains("기준") { return "기준 잡음" }
        if message.hasPrefix("새로 "), let n = message.dropFirst(3).split(separator: "건").first, Int(n) != nil { return "새 건 \(n)" }
        if message.contains("알림 창에 보냈") { return "알림 보냄" }
        return "성공"
    }

    public static func counts(_ result: String) -> Bool { result != "새 건 없음" && result != "기준 잡음" }

    public static func record(_ log: [UUID: AlarmRunRecord], id: UUID, message: String, failed: Bool,
                              now: Date, output: String? = nil, usage: RunUsage? = nil, count: Bool = true,
                              calendar: Calendar = .current) -> [UUID: AlarmRunRecord] {
        var log = log
        let result = short(message, failed: failed)
        let day = ReportSchedule.dayString(now, calendar: calendar)
        let before = log[id].flatMap { $0.day == day ? $0.todayCount : nil } ?? 0
        log[id] = AlarmRunRecord(lastAt: now, result: result, failed: failed, day: day,
                                 todayCount: before + (count && counts(result) ? 1 : 0), output: output, usage: usage)
        return log
    }

    public static func todayTotal(_ log: [UUID: AlarmRunRecord], ids: Set<UUID>, now: Date, calendar: Calendar = .current) -> Int {
        let day = ReportSchedule.dayString(now, calendar: calendar)
        return log.filter { ids.contains($0.key) && $0.value.day == day }.reduce(0) { $0 + $1.value.todayCount }
    }

    public static func load(from file: URL = file) -> [UUID: AlarmRunRecord] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file),
              let raw = try? decoder.decode([String: AlarmRunRecord].self, from: data) else { return [:] }
        return Dictionary(uniqueKeysWithValues: raw.compactMap { k, v in UUID(uuidString: k).map { ($0, v) } })
    }

    public static func save(_ log: [UUID: AlarmRunRecord], to file: URL = file) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let raw = Dictionary(uniqueKeysWithValues: log.map { ($0.key.uuidString, $0.value) })
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? encoder.encode(raw).write(to: file, options: .atomic)
    }
}
