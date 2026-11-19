import Foundation

public struct RunUsage: Codable, Equatable, Sendable {
    public var input: Int
    public var output: Int
    public var cacheRead: Int
    public var cacheWrite: Int
    public var costUSD: Double
    public var contextWindow: Int?

    public init(input: Int = 0, output: Int = 0, cacheRead: Int = 0, cacheWrite: Int = 0, costUSD: Double = 0, contextWindow: Int? = nil) {
        self.input = input
        self.output = output
        self.cacheRead = cacheRead
        self.cacheWrite = cacheWrite
        self.costUSD = costUSD
        self.contextWindow = contextWindow
    }

    public var total: Int { input + output + cacheRead + cacheWrite }
    public var context: Int { input + cacheRead + cacheWrite }

    public mutating func add(_ other: RunUsage) {
        input += other.input
        output += other.output
        cacheRead += other.cacheRead
        cacheWrite += other.cacheWrite
        costUSD += other.costUSD
        contextWindow = other.contextWindow ?? contextWindow
    }

    public static func parse(stdout: Data) -> RunUsage? {
        let text = String(decoding: stdout, as: UTF8.self)
        guard let result = text.split(separator: "\n").reversed().compactMap({ line -> [String: Any]? in
            guard let o = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any], o["type"] as? String == "result" else { return nil }
            return o
        }).first else { return nil }
        let usage = result["usage"] as? [String: Any] ?? [:]
        func int(_ key: String) -> Int { (usage[key] as? NSNumber)?.intValue ?? 0 }
        let windows = (result["modelUsage"] as? [String: Any] ?? [:]).values.compactMap { ($0 as? [String: Any])?["contextWindow"] as? NSNumber }
        return RunUsage(input: int("input_tokens"), output: int("output_tokens"), cacheRead: int("cache_read_input_tokens"),
                        cacheWrite: int("cache_creation_input_tokens"), costUSD: (result["total_cost_usd"] as? NSNumber)?.doubleValue ?? 0,
                        contextWindow: windows.map(\.intValue).max())
    }

    public var shortText: String {
        "\(TokenFormat.compact(total)) · \(TokenFormat.usd(costUSD))"
    }
}

public struct WorkSession: Codable, Equatable, Identifiable, Sendable {
    public var id: String
    public var repo: String
    public var createdAt: Date
    public var rotatedAt: Date
    public var lastUsed: Date
    public var keys: [String]
    public var contextTokens: Int
    public var contextWindow: Int?
    public var usage: RunUsage
    public var summaryFrom: String?
    public var contextAlerted: Bool?
    public var freshReason: String?

    public init(id: String, repo: String, now: Date, summaryFrom: String? = nil) {
        self.id = id
        self.repo = repo
        self.createdAt = now
        self.rotatedAt = now
        self.lastUsed = now
        self.keys = []
        self.contextTokens = 0
        self.contextWindow = nil
        self.usage = RunUsage()
        self.summaryFrom = summaryFrom
    }

    public var contextPercent: Int? {
        guard let window = contextWindow, window > 0 else { return nil }
        return min(100, contextTokens * 100 / window)
    }
}

public struct SessionReuse: Equatable, Sendable {
    public static let defaultMinutes = 55
    public static let defaultTokens = 120_000

    public static let reviewerReason = "리뷰어는 늘 새로"

    public var idleMinutes: Int
    public var maxTokens: Int
    public var freshReviewer: Bool

    public init(idleMinutes: Int = SessionReuse.defaultMinutes, maxTokens: Int = SessionReuse.defaultTokens, freshReviewer: Bool = true) {
        self.idleMinutes = idleMinutes
        self.maxTokens = maxTokens
        self.freshReviewer = freshReviewer
    }

    public init(settings: AppSettings) {
        self.init(idleMinutes: settings.sessionReuseMinutes, maxTokens: settings.sessionReuseMaxTokens, freshReviewer: settings.reviewerAlwaysFresh)
    }

    public static let always = SessionReuse(idleMinutes: 0, maxTokens: 0, freshReviewer: false)

    public func freshReason(for session: WorkSession, now: Date) -> String? {
        if freshReviewer, PipelineRole.split(session.repo)?.role == .reviewer {
            return Self.reviewerReason
        }
        let idle = Int(now.timeIntervalSince(session.lastUsed) / 60)
        if idleMinutes > 0 && idle >= idleMinutes {
            return "식음 \(idle)분 (기준 \(idleMinutes)분)"
        }
        if maxTokens > 0 && session.contextTokens >= maxTokens {
            return "문맥 \(TokenFormat.compact(session.contextTokens)) (기준 \(TokenFormat.compact(maxTokens)))"
        }
        return nil
    }
}

public struct WorkSessionBook: Codable, Equatable, Sendable {
    public static let operations = "운영"

    public var sessions: [String: WorkSession] = [:]
    public var archived: [WorkSession] = []

    public init() {}

    enum CodingKeys: String, CodingKey { case sessions, archived }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        sessions = try c.decodeIfPresent([String: WorkSession].self, forKey: .sessions) ?? [:]
        archived = try c.decodeIfPresent([WorkSession].self, forKey: .archived) ?? []
    }

    public static func repoKey(_ repo: String?) -> String {
        let trimmed = (repo ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? operations : trimmed
    }

    public mutating func contextAlert(repo: String, threshold: Int?) -> WorkSession? {
        guard var s = sessions[repo], let percent = s.contextPercent else { return nil }
        guard let threshold, threshold > 0 else {
            if s.contextAlerted == true { s.contextAlerted = nil; sessions[repo] = s }
            return nil
        }
        if percent >= threshold {
            guard s.contextAlerted != true else { return nil }
            s.contextAlerted = true
            sessions[repo] = s
            return s
        }
        if s.contextAlerted == true {
            s.contextAlerted = nil
            sessions[repo] = s
        }
        return nil
    }

    public mutating func reuseId(_ repo: String, policy: SessionReuse, now: Date) -> String? {
        guard let s = sessions[repo], !s.id.isEmpty else { return nil }
        guard let reason = policy.freshReason(for: s, now: now) else { return s.id }
        archived.append(s)
        var fresh = WorkSession(id: "", repo: repo, now: now)
        fresh.freshReason = reason
        sessions[repo] = fresh
        return nil
    }

    public mutating func record(repo: String, sessionId: String, key: String?, usage: RunUsage?, now: Date) {
        var s = sessions[repo] ?? WorkSession(id: sessionId, repo: repo, now: now)
        if s.id.isEmpty { s.id = sessionId }
        if s.id != sessionId {
            archived.append(s)
            s = WorkSession(id: sessionId, repo: repo, now: now)
        }
        s.lastUsed = now
        if let key, !key.isEmpty, !s.keys.contains(key) { s.keys.append(key) }
        if let usage {
            s.usage.add(usage)
            s.contextTokens = usage.context
            s.contextWindow = usage.contextWindow ?? s.contextWindow
        }
        sessions[repo] = s
    }

    public mutating func replace(repo: String, with sessionId: String, summaryFrom old: String?, now: Date) {
        if let current = sessions[repo] { archived.append(current) }
        sessions[repo] = WorkSession(id: sessionId, repo: repo, now: now, summaryFrom: old)
    }

    public mutating func archive(repo: String) {
        if let current = sessions.removeValue(forKey: repo) { archived.append(current) }
    }

    public static var file: URL { AppPaths.dataDir.appendingPathComponent("automation/sessions.json") }

    public static func load(from file: URL = file) -> WorkSessionBook {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file), let book = try? decoder.decode(WorkSessionBook.self, from: data) else { return WorkSessionBook() }
        return book
    }

    public func save(to file: URL = file) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? encoder.encode(self).write(to: file, options: .atomic)
    }
}

public enum WorkRoute {
    public static let tag = "pokemate-route"

    public static func extract(from text: String) -> String?? {
        guard let regex = try? NSRegularExpression(pattern: "```\(tag)[ \\t]*\\n(.*?)```", options: [.dotMatchesLineSeparators]),
              let m = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let r = Range(m.range(at: 1), in: text),
              let object = try? JSONSerialization.jsonObject(with: Data(text[r].utf8)) as? [String: Any] else { return nil }
        if object["skip"] as? Bool == true { return .some(nil) }
        return .some(WorkSessionBook.repoKey(object["repo"] as? String))
    }
}

public final class SerialQueue: @unchecked Sendable {
    private let lock = NSLock()
    private var tails: [String: Task<Void, Never>] = [:]

    public init() {}

    public func run(_ key: String, _ job: @escaping @Sendable () async -> Void) -> Task<Void, Never> {
        lock.lock()
        let previous = tails[key]
        let task = Task {
            await previous?.value
            await job()
        }
        tails[key] = task
        lock.unlock()
        return task
    }
}

public struct TranscriptEntry: Equatable, Identifiable, Sendable {
    public enum Kind: String, Sendable { case user, assistant, tool, result }
    public let id: Int
    public let kind: Kind
    public let time: String
    public let text: String
}

public enum SessionTranscript {
    public static func parse(_ data: Data, limit: Int = 400) -> [TranscriptEntry] {
        var rows: [TranscriptEntry] = []
        for line in String(decoding: data, as: UTF8.self).split(separator: "\n") {
            guard let o = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                  let type = o["type"] as? String, type == "user" || type == "assistant",
                  let message = o["message"] as? [String: Any] else { continue }
            let time = String((o["timestamp"] as? String ?? "").prefix(19)).replacingOccurrences(of: "T", with: " ")
            func add(_ kind: TranscriptEntry.Kind, _ text: String) {
                let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !t.isEmpty else { return }
                rows.append(TranscriptEntry(id: rows.count, kind: kind, time: time, text: t.count > 1200 ? String(t.prefix(1200)) + "…" : t))
            }
            if let text = message["content"] as? String {
                add(type == "user" ? .user : .assistant, text)
                continue
            }
            for c in message["content"] as? [[String: Any]] ?? [] {
                switch c["type"] as? String {
                case "text": add(type == "user" ? .user : .assistant, c["text"] as? String ?? "")
                case "tool_use": add(.tool, ClaudeStream.activity(tool: c["name"] as? String ?? "", input: c["input"] as? [String: Any] ?? [:]))
                case "tool_result":
                    let content = c["content"]
                    let text = (content as? String) ?? ((content as? [[String: Any]])?.compactMap { $0["text"] as? String }.joined(separator: "\n") ?? "")
                    add(.result, (c["is_error"] as? Bool == true ? "오류: " : "") + String(text.prefix(300)))
                default: break
                }
            }
        }
        return Array(rows.suffix(limit))
    }
}

public enum TranscriptUsage {
    public static func total(_ data: Data, since: Date? = nil) -> (usage: RunUsage, lastContext: Int) {
        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let plain = ISO8601DateFormatter()
        var seen = Set<String>()
        var usage = RunUsage()
        var last = 0
        for line in String(decoding: data, as: UTF8.self).split(separator: "\n") {
            guard let o = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any], o["type"] as? String == "assistant",
                  let message = o["message"] as? [String: Any], let u = message["usage"] as? [String: Any] else { continue }
            if let since {
                let stamp = o["timestamp"] as? String ?? ""
                guard let date = iso.date(from: stamp) ?? plain.date(from: stamp), date >= since else { continue }
            }
            let id = (message["id"] as? String) ?? (o["uuid"] as? String) ?? UUID().uuidString
            guard seen.insert(id).inserted else { continue }
            func int(_ k: String) -> Int { (u[k] as? NSNumber)?.intValue ?? 0 }
            var bucket = TokenBucket()
            bucket.input = int("input_tokens")
            bucket.output = int("output_tokens")
            bucket.cacheRead = int("cache_read_input_tokens")
            bucket.cacheWrite = int("cache_creation_input_tokens")
            let model = message["model"] as? String ?? ""
            usage.add(RunUsage(input: bucket.input, output: bucket.output, cacheRead: bucket.cacheRead, cacheWrite: bucket.cacheWrite,
                               costUSD: ClaudePricing.cost(model: model, bucket: bucket) ?? 0))
            last = bucket.input + bucket.cacheRead + bucket.cacheWrite
        }
        return (usage, last)
    }
}

extension WorkSessionBook {
    public mutating func applyTranscript(repo: String, usage: RunUsage, lastContext: Int) {
        guard var s = sessions[repo] else { return }
        let window = s.usage.contextWindow ?? s.contextWindow
        s.usage = usage
        s.usage.contextWindow = window
        s.contextTokens = lastContext
        sessions[repo] = s
    }
}
