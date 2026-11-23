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

/// 자동 처리 세션을 이어 쓸지 새로 시작할지 정하는 단 하나의 규칙 (설정의 "세션 이어 쓰기": 쉰 시간·문맥 크기).
/// 세션 단위는 작업 한 건(`jira:{KEY}`, `pr:{owner}/{repo}#{number}`)이다. 같은 건 안에서만 이어 쓰고, 건이 바뀌면 늘 새로 시작한다.
/// 모든 자동 처리 진입점(Jira 역할 처리·GitHub 리뷰·Trial·Eval)이 `decide` 만 부른다. 기능별 예외는 두지 않는다.
public struct SessionPolicy: Equatable, Sendable {
    public static let defaultMinutes = 55
    public static let defaultTokens = 120_000

    public var idleMinutes: Int
    public var maxTokens: Int

    public init(idleMinutes: Int = SessionPolicy.defaultMinutes, maxTokens: Int = SessionPolicy.defaultTokens) {
        self.idleMinutes = idleMinutes
        self.maxTokens = maxTokens
    }

    public init(settings: AppSettings) {
        self.init(idleMinutes: settings.sessionReuseMinutes, maxTokens: settings.sessionReuseMaxTokens)
    }

    /// 같은 건 안에서는 늘 이어 쓰기 (둘 다 0)
    public static let always = SessionPolicy(idleMinutes: 0, maxTokens: 0)

    /// 그 건·역할의 이전 세션. 다른 건의 것일 수도 있다 (작업 탭 기록에서 온 것: 그때는 새로 시작하고 이유만 남는다).
    public struct Previous: Equatable, Sendable {
        public var caseKey: String
        public var id: String
        public var lastUsed: Date
        public var contextTokens: Int

        public init(caseKey: String, id: String, lastUsed: Date, contextTokens: Int) {
            self.caseKey = caseKey
            self.id = id
            self.lastUsed = lastUsed
            self.contextTokens = contextTokens
        }
    }

    /// 이어 쓸 세션 id (새로 시작하면 nil)와 새로 시작하는 이유 (처음이면 nil)
    public struct Decision: Equatable, Sendable {
        public var resumeId: String?
        public var reason: String?

        public init(resumeId: String?, reason: String?) {
            self.resumeId = resumeId
            self.reason = reason
        }

        public var resumed: Bool { resumeId != nil }
    }

    /// 이어 쓰기: 이전 세션이 같은 건이고, 마지막 사용이 idleMinutes 안이고, 문맥이 maxTokens 미만. 0 이면 그 기준은 끈다.
    /// 다른 건의 세션은 어떤 설정에서도 이어 쓰지 않는다. `role` 은 기록용이고 판단을 바꾸지 않는다 (역할별 예외 없음).
    public func decide(caseKey: String, role: String, previous: Previous?, now: Date) -> Decision {
        guard let previous, !previous.id.isEmpty else { return Decision(resumeId: nil, reason: nil) }
        if !Self.sameCase(previous.caseKey, caseKey) {
            return Decision(resumeId: nil, reason: previous.caseKey.isEmpty ? nil : Self.newCaseReason(caseKey: caseKey, previous: previous.caseKey))
        }
        let idle = Int(now.timeIntervalSince(previous.lastUsed) / 60)
        if idleMinutes > 0 && idle >= idleMinutes {
            return Decision(resumeId: nil, reason: "식음 \(idle)분 (기준 \(idleMinutes)분)")
        }
        if maxTokens > 0 && previous.contextTokens >= maxTokens {
            return Decision(resumeId: nil, reason: "문맥 \(TokenFormat.compact(previous.contextTokens)) (기준 \(TokenFormat.compact(maxTokens)))")
        }
        return Decision(resumeId: previous.id, reason: nil)
    }

    public static let jiraPrefix = "jira:"
    public static let prPrefix = "pr:"

    /// Jira 티켓 한 건의 키: `jira:{KEY}`
    public static func jiraKey(_ ticket: String) -> String { jiraPrefix + ticket }

    /// PR 한 건의 키: `pr:{owner}/{repo}#{number}`
    public static func prKey(owner: String, repo: String, number: Int) -> String { "\(prPrefix)\(owner)/\(repo)#\(number)" }

    /// 화면·작업 탭 기록(`keys`)에 쓰는 짧은 이름: `jira:ACME-101` → `ACME-101`, `pr:acme/svc#12` → `svc#12`. 접두어가 없으면 그대로.
    public static func label(_ caseKey: String) -> String {
        if caseKey.hasPrefix(jiraPrefix) { return String(caseKey.dropFirst(jiraPrefix.count)) }
        if caseKey.hasPrefix(prPrefix) {
            let rest = String(caseKey.dropFirst(prPrefix.count))
            guard let slash = rest.lastIndex(of: "/") else { return rest }
            return String(rest[rest.index(after: slash)...])
        }
        return caseKey
    }

    private static func prefixed(_ key: String) -> Bool { key.hasPrefix(jiraPrefix) || key.hasPrefix(prPrefix) }

    /// 같은 건인지. 둘 다 접두어가 있으면 키 그대로, 한쪽이 작업 탭 기록의 짧은 이름이면 짧은 이름끼리 비교한다.
    public static func sameCase(_ a: String, _ b: String) -> Bool {
        if a == b { return true }
        if prefixed(a) && prefixed(b) { return false }
        return label(a) == label(b)
    }

    /// 건이 바뀌어 새로 시작하는 이유: PR 이면 "새 PR (앞: repo#12)", 아니면 "새 티켓 (앞: ACME-101)"
    public static func newCaseReason(caseKey: String, previous: String) -> String {
        caseKey.hasPrefix(prPrefix) ? newPRReason(previous: label(previous)) : newTicketReason(previous: label(previous))
    }

    public static func newTicketReason(previous: String) -> String { "새 티켓 (앞: \(previous))" }

    public static func newPRReason(previous: String) -> String { "새 PR (앞: \(previous))" }
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

    /// 레포 작업 세션(알림 자동 처리의 레포 세션)을 이어 쓸 id. 레포 세션은 그 레포 자체가 한 건이다 (`SessionPolicy.decide` 로 시간·문맥만 본다).
    /// 새로 시작해야 하면 옛 세션을 보관하고 이유를 남긴 빈 세션을 두고 nil.
    public mutating func reuseId(_ repo: String, policy: SessionPolicy, now: Date) -> String? {
        guard let s = sessions[repo], !s.id.isEmpty else { return nil }
        let decision = policy.decide(caseKey: repo, role: "", previous: SessionPolicy.Previous(caseKey: repo, id: s.id, lastUsed: s.lastUsed, contextTokens: s.contextTokens), now: now)
        if let id = decision.resumeId { return id }
        archived.append(s)
        var fresh = WorkSession(id: "", repo: repo, now: now)
        fresh.freshReason = decision.reason
        sessions[repo] = fresh
        return nil
    }

    /// 작업 탭 칸(`{도메인}:{역할}`)의 마지막 세션: 지금 세션, 없으면(끝나 보관했으면) 가장 최근에 보관한 그 칸의 세션. 건 키는 그 세션의 마지막 `keys`.
    public func previous(_ repo: String) -> SessionPolicy.Previous? {
        let live = sessions[repo].flatMap { $0.id.isEmpty ? nil : $0 }
        guard let s = live ?? lastArchived(repo) else { return nil }
        return SessionPolicy.Previous(caseKey: s.keys.last ?? "", id: s.id, lastUsed: s.lastUsed, contextTokens: s.contextTokens)
    }

    /// 그 칸에서 가장 최근에 보관한 세션 (실행한 적 있는 것만)
    public func lastArchived(_ repo: String) -> WorkSession? {
        var best: WorkSession?
        for a in archived where a.repo == repo && !a.id.isEmpty {
            if best == nil || a.lastUsed >= best!.lastUsed { best = a }
        }
        return best
    }

    /// 역할 세션을 새로 시작할 때 작업 탭 기록을 맞춘다. 앞 세션은 보관하고 이유를 남긴 빈 세션을 둔다. 바뀌었으면 true.
    /// `reason` 이 없으면 그 칸의 마지막 세션(지금 것 또는 보관한 것)으로 `SessionPolicy.decide` 가 이유를 정한다 (다른 건이면 "새 티켓 (앞: KEY)"·"새 PR (앞: repo#12)").
    @discardableResult
    public mutating func startFresh(repo: String, caseKey: String, reason: String?, now: Date) -> Bool {
        let current = sessions[repo]
        let live = current.map { !$0.id.isEmpty } ?? false
        let reason = reason ?? SessionPolicy().decide(caseKey: caseKey, role: "", previous: previous(repo), now: now).reason
        if !live && reason == nil { return false }
        if !live, let current, current.freshReason == reason { return false }
        if live, let current { archived.append(current) }
        var fresh = WorkSession(id: "", repo: repo, now: now)
        fresh.freshReason = reason
        sessions[repo] = fresh
        return true
    }

    /// 끝난 건(`label`: 티켓 키 또는 `repo#12`)의 역할 세션을 보관한다: 그 칸의 지금 세션이 이 건의 것이면 보관, 아직 실행하지 못한 빈 칸은 지운다.
    /// 보관한 세션은 작업 탭 목록에서 빠지고 기록(`archived`)은 남는다. 바뀌었으면 true.
    @discardableResult
    public mutating func archiveCase(_ repos: [String], label: String) -> Bool {
        var changed = false
        for repo in repos {
            guard let s = sessions[repo] else { continue }
            if s.id.isEmpty {
                sessions.removeValue(forKey: repo)
                changed = true
            } else if s.keys.last == label {
                sessions.removeValue(forKey: repo)
                archived.append(s)
                changed = true
            }
        }
        return changed
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
