import Foundation

public enum SidebarItem: String, CaseIterable, Identifiable, Sendable {
    case chat, alarms, work, notes, collection, connections, settings
    public var id: String { rawValue }

    public static let tabs: [SidebarItem] = [.chat, .alarms, .work, .notes, .collection]
    public static let tools: [SidebarItem] = [.connections, .settings]
    public static let workSessionsTab: SidebarItem = .work

    public static func target(open: String?) -> SidebarItem {
        open == "workSessions" ? workSessionsTab : .chat
    }

    public var symbol: String {
        switch self {
        case .chat: return "house"
        case .notes: return "note.text"
        case .connections: return "powerplug"
        case .alarms: return "bell"
        case .work: return "rectangle.stack"
        case .collection: return "square.grid.3x3"
        case .settings: return "gearshape"
        }
    }

    public var title: String {
        switch self {
        case .chat: return "홈"
        case .notes: return "메모"
        case .connections: return "연결"
        case .alarms: return "알람"
        case .work: return "작업"
        case .collection: return "컬렉션"
        case .settings: return "설정"
        }
    }
}

public struct ChatRouting: Codable, Equatable, Sendable {
    public private(set) var repo: String?
    public private(set) var mateNotes: [String] = []

    public init(repo: String? = nil) { self.repo = repo }

    public var isMate: Bool { repo == nil }

    public mutating func choose(_ repo: String?) {
        let trimmed = repo?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.repo = (trimmed?.isEmpty ?? true) ? nil : trimmed
    }

    public mutating func reset() {
        repo = nil
        mateNotes = []
    }

    public static func summary(_ text: String, limit: Int = 60) -> String {
        let line = text.split(whereSeparator: \.isNewline).first.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? ""
        return line.count > limit ? String(line.prefix(limit)) + "…" : line
    }

    public mutating func noteSent(repo: String, text: String) {
        mateNotes.append("작업 세션 \(repo) 로 보냄: \(Self.summary(text))")
    }

    public mutating func takeMatePrefix() -> String {
        guard !mateNotes.isEmpty else { return "" }
        defer { mateNotes = [] }
        return "[앱 기록] 아래는 사용자가 이 대화에서 작업 세션으로 직접 보낸 것이다. 너에게 온 말이 아니고, 답은 그 작업 세션이 했다.\n"
            + mateNotes.map { "- \($0)" }.joined(separator: "\n") + "\n\n"
    }
}

public enum RouteSuggestion {
    public static let tag = "pokemate-route-suggest"

    public static func extract(from text: String) -> (text: String, repo: String?) {
        guard let regex = try? NSRegularExpression(pattern: "\\n?```\(tag)[ \\t]*\\n(.*?)```[ \\t]*", options: [.dotMatchesLineSeparators]),
              let m = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let whole = Range(m.range, in: text), let body = Range(m.range(at: 1), in: text) else { return (text, nil) }
        let object = try? JSONSerialization.jsonObject(with: Data(text[body].utf8)) as? [String: Any]
        let repo = (object?["repo"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
        var clean = text
        clean.removeSubrange(whole)
        clean = clean.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let repo, !repo.isEmpty, !repo.contains("/") else { return (clean, nil) }
        return (clean, repo)
    }
}

public enum WorkspaceRepos {
    public static var root: URL { URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Workspace", isDirectory: true) }

    public static func list(root: URL = root) -> [String] {
        let fm = FileManager.default
        let dirs = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        return dirs.filter { fm.fileExists(atPath: $0.appendingPathComponent(".git").path) }
            .map(\.lastPathComponent)
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    public static func bootstrap(repo: String, root: URL = root) -> String {
        if repo == WorkSessionBook.operations {
            return "이 작업 세션은 레포가 없는 운영 건 전용이다. 이번이 이 세션의 첫 메시지다. 아래 요청을 처리하고, 이후 이 세션에서 이미 읽은 것은 다시 읽지 않는다.\n\n"
        }
        let path = root.appendingPathComponent(repo).path
        return """
        이 작업 세션은 레포 \(repo) (\(path)) 전용이다. 이번이 이 세션의 첫 메시지라 먼저 지식을 모은다: 레포 루트 CLAUDE.md 와 그 안에서 가리키는 문서, README, 빌드·테스트 방법, 최근 git log(20개 안팎). 읽은 것을 짧게 요약한 뒤 아래 요청을 처리한다. 이후 이 세션에서는 이미 읽은 것을 다시 읽지 않는다.


        """
    }
}

extension WorkSessionBook {
    public mutating func addEmpty(repo: String, now: Date) -> Bool {
        guard sessions[repo] == nil else { return false }
        sessions[repo] = WorkSession(id: "", repo: repo, now: now)
        return true
    }

    public func resumeId(_ repo: String) -> String? {
        guard let id = sessions[repo]?.id, !id.isEmpty else { return nil }
        return id
    }
}
