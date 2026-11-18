import Foundation

public struct LocalNames: Equatable, Sendable {
    public struct Entry: Equatable, Sendable {
        public let id: String
        public let name: String
        public let aliases: [String]
    }

    public var entries: [Entry] = []
    public var worklogWords: [String] = []

    public init(entries: [Entry] = [], worklogWords: [String] = []) {
        self.entries = entries
        self.worklogWords = worklogWords
    }

    public static func parse(_ data: Data) -> LocalNames {
        guard let rows = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return LocalNames() }
        var names = LocalNames()
        for row in rows {
            let strings = { (key: String) in (row[key] as? [String] ?? []).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } }
            if row["kind"] as? String == "settings" {
                if row["id"] as? String == "automation" {
                    let title = (row["worklogCalendarTitle"] as? String ?? "").trimmingCharacters(in: .whitespaces)
                    names.worklogWords = (title.isEmpty ? [] : [title]) + strings("worklogAliases")
                }
                continue
            }
            guard let id = row["id"] as? String else { continue }
            names.entries.append(Entry(id: id, name: row["name"] as? String ?? id, aliases: strings("aliases")))
        }
        return names
    }

    public static func file(repoDir: String) -> URL {
        let repo = URL(fileURLWithPath: repoDir)
        let shared = repo.appendingPathComponent("shared")
        let candidates = [repo.appendingPathComponent("local/connections.json"), shared.appendingPathComponent("connections.local.json")]
        return candidates.first { FileManager.default.fileExists(atPath: $0.path) } ?? shared.appendingPathComponent("connections.example.json")
    }

    public static func load(repoDir: String) -> LocalNames {
        guard let data = try? Data(contentsOf: file(repoDir: repoDir)) else { return LocalNames() }
        return parse(data)
    }

    nonisolated(unsafe) private static var cached: (repoDir: String, names: LocalNames)?
    private static let lock = NSLock()

    public static var current: LocalNames {
        let repoDir = AppPaths.loadSettings().repoDir
        lock.lock()
        defer { lock.unlock() }
        if let cached, cached.repoDir == repoDir { return cached.names }
        let names = load(repoDir: repoDir)
        cached = (repoDir, names)
        return names
    }

    public func name(_ id: String) -> String? {
        entries.first { $0.id == id }?.name
    }

    public static func compact(_ text: String) -> String {
        text.replacingOccurrences(of: " ", with: "").lowercased()
    }

    public func promptLine(tools: [String] = ClaudeCommand.allowedTools) -> String {
        var lines: [String] = []
        for entry in entries {
            let spoken = ([entry.name] + entry.aliases).filter { Self.compact($0) != Self.compact(entry.id) }
            guard !spoken.isEmpty else { continue }
            let prefix = "mcp__pokemate__\(entry.id)_"
            let owned = tools.contains { $0.hasPrefix(prefix) }
            let target = owned ? "\(entry.id) (\(entry.id)_ 로 시작하는 도구)" : "\(entry.id) (조회 도구 없음, 연결 화면의 이름)"
            lines.append("  - " + spoken.map { "\"\($0)\"" }.joined(separator: ", ") + " → " + target)
        }
        if !worklogWords.isEmpty {
            lines.append("  - " + worklogWords.map { "\"\($0)\"" }.joined(separator: ", ") + " → worklog 캘린더 (work_day_check, jira_my_worklogs)")
        }
        guard !lines.isEmpty else { return "" }
        return "\n- 사내 시스템 이름: 사용자는 시스템을 아래 이름으로 부른다. 그렇게 부르면 오른쪽 연결의 도구를 쓰고, 답할 때도 사용자가 부른 이름으로 말한다.\n"
            + lines.joined(separator: "\n") + "\n"
    }
}
