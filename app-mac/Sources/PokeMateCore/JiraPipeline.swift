import Foundation

public enum PipelineRole: String, Codable, CaseIterable, Sendable {
    case pm, dev, tester, reviewer

    public var label: String {
        switch self {
        case .pm: return "PM"
        case .dev: return "개발"
        case .tester: return "테스터"
        case .reviewer: return "리뷰어"
        }
    }

    public static func sessionKey(domain: String, role: PipelineRole) -> String { "\(domain):\(role.rawValue)" }

    public static func split(_ key: String) -> (domain: String, role: PipelineRole)? {
        guard let i = key.lastIndex(of: ":"), let role = PipelineRole(rawValue: String(key[key.index(after: i)...])) else { return nil }
        let domain = String(key[..<i])
        return domain.isEmpty ? nil : (domain, role)
    }
}

public struct DomainMap: Equatable, Sendable {
    public var domains: [String: [String]]
    public var knowledge: [String: [String]]
    public var defaultKnowledge: [String]
    public var descriptions: [String: String] = [:]
    public var keywords: [String: [String]] = [:]

    public init(domains: [String: [String]] = [:], knowledge: [String: [String]] = [:], defaultKnowledge: [String] = [],
                descriptions: [String: String] = [:], keywords: [String: [String]] = [:]) {
        self.domains = domains
        self.knowledge = knowledge
        self.defaultKnowledge = defaultKnowledge
        self.descriptions = descriptions
        self.keywords = keywords
    }

    public static let defaultKey = "default"

    public static var file: URL { AppPaths.localFile("domains.json") }

    public static func parse(_ data: Data) -> DomainMap {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return DomainMap() }
        func list(_ value: Any?, _ field: String) -> [String] {
            (((value as? [String: Any])?[field] as? [String]) ?? []).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        }
        var map: [String: [String]] = [:]
        var knowledge: [String: [String]] = [:]
        var descriptions: [String: String] = [:]
        var keywords: [String: [String]] = [:]
        for (name, value) in object {
            let key = name.trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty, !key.contains(":"), key != defaultKey else { continue }
            map[key] = list(value, "repos")
            if (value as? [String: Any])?["knowledge"] != nil { knowledge[key] = list(value, "knowledge") }
            if let text = ((value as? [String: Any])?["description"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
                descriptions[key] = text
            }
            let words = list(value, "keywords")
            if !words.isEmpty { keywords[key] = words }
        }
        return DomainMap(domains: map, knowledge: knowledge, defaultKnowledge: list(object[defaultKey], "knowledge"),
                         descriptions: descriptions, keywords: keywords)
    }

    public static func load(from file: URL = file) -> DomainMap {
        guard let data = try? Data(contentsOf: file) else { return DomainMap() }
        return parse(data)
    }

    public func domain(for repo: String) -> String {
        domains.keys.sorted().first { domains[$0]?.contains(repo) == true } ?? repo
    }

    public func knowledgePaths(for domain: String, home: String = NSHomeDirectory()) -> [String] {
        (knowledge[domain] ?? defaultKnowledge).map { item in
            if item.hasPrefix("~/") { return home + "/" + item.dropFirst(2) }
            if item.hasPrefix("/") { return item }
            return home + "/Workspace/" + item
        }
    }

    public func knowledgeText(for domain: String, home: String = NSHomeDirectory()) -> String {
        let paths = knowledgePaths(for: domain, home: home)
        guard !paths.isEmpty else { return "   (정한 지식 저장소 없음)" }
        return paths.map { "   - `\($0.hasPrefix(home + "/") ? "~" + $0.dropFirst(home.count) : $0)`" }.joined(separator: "\n")
    }

    public static let placeholder = "{{knowledge}}"
}

public struct PipelineBranch: Codable, Equatable, Sendable {
    public var branch: String
    public var from: String

    public init(branch: String, from: String) {
        self.branch = branch
        self.from = from
    }
}

public struct PipelineNext: Equatable, Sendable {
    public enum Kind: String, Codable, Sendable { case tester, dev, pm, reviewer, push, stop }

    public var kind: Kind
    public var note: String
    public var repo: String?
    public var branch: String?
    public var create: [PipelineBranch]
    public var message: String?
    public var waiting: Bool

    public init(kind: Kind, note: String = "", repo: String? = nil, branch: String? = nil, create: [PipelineBranch] = [],
                message: String? = nil, waiting: Bool = false) {
        self.kind = kind
        self.note = note
        self.repo = repo
        self.branch = branch
        self.create = create
        self.message = message
        self.waiting = waiting
    }

    public static let tag = "pokemate-next"

    public static func extract(from text: String) -> PipelineNext? {
        guard let regex = try? NSRegularExpression(pattern: "```\(tag)[ \\t]*\\n(.*?)```", options: [.dotMatchesLineSeparators]),
              let m = regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).last,
              let r = Range(m.range(at: 1), in: text),
              let object = try? JSONSerialization.jsonObject(with: Data(text[r].utf8)) as? [String: Any],
              let raw = object["next"] as? String, let kind = Kind(rawValue: raw.trimmingCharacters(in: .whitespaces).lowercased()) else { return nil }
        func string(_ k: String) -> String? {
            let v = (object[k] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            return v.isEmpty ? nil : v
        }
        let create = (object["create"] as? [[String: Any]] ?? []).compactMap { item -> PipelineBranch? in
            guard let b = item["branch"] as? String, let f = item["from"] as? String, !b.isEmpty, !f.isEmpty else { return nil }
            return PipelineBranch(branch: b, from: f)
        }
        return PipelineNext(kind: kind, note: string("note") ?? "", repo: string("repo"), branch: string("branch"), create: create,
                            message: string("message"), waiting: object["waiting"] as? Bool ?? false)
    }

    public static func strip(_ text: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: "```\(tag)[ \\t]*\\n.*?```", options: [.dotMatchesLineSeparators]) else { return text }
        return regex.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

public struct PipelineState: Codable, Equatable, Sendable {
    public enum Stage: String, Codable, Sendable { case pm, dev, tester, reviewer, branch, push, waiting, done, stopped }

    public struct Step: Codable, Equatable, Sendable {
        public var role: String
        public var next: String
        public var note: String
        public var at: Date
    }

    public enum Outcome: Equatable, Sendable { case next, finished, blocked(String) }

    public struct Inflight: Codable, Equatable, Sendable {
        public var role: String
        public var sessionId: String
        public var startedAt: Date

        public init(role: String, sessionId: String, startedAt: Date) {
            self.role = role
            self.sessionId = sessionId
            self.startedAt = startedAt
        }
    }

    public static let backLimit = 2

    public var key: String
    public var title: String
    public var event: String
    public var domain: String
    public var repo: String?
    public var branch: String?
    public var create: [PipelineBranch] = []
    public var worktree: String?
    public var commitMessage: String?
    public var brief: String?
    public var stage: Stage = .pm
    public var note: String = ""
    public var summary: String = ""
    public var from: String?
    public var testerBacks = 0
    public var reviewerBacks = 0
    public var testsPassed = false
    public var mustStop = false
    public var pushed: String?
    public var reason: String?
    public var inflight: Inflight?
    public var costs: [String: Double] = [:]
    public var history: [Step] = []
    public var timeoutMinutes: Int
    public var createdAt: Date
    public var updatedAt: Date

    public init(key: String, title: String, event: String, domain: String, repo: String?, timeoutMinutes: Int, now: Date) {
        self.key = key
        self.title = title
        self.event = event
        self.domain = domain
        self.repo = repo
        self.timeoutMinutes = timeoutMinutes
        self.createdAt = now
        self.updatedAt = now
    }

    public var isActive: Bool { [.pm, .dev, .tester, .reviewer, .branch, .push].contains(stage) }

    public enum Arrival: Equatable, Sendable { case start, resume, skip(String) }

    public static func arrival(of existing: PipelineState?) -> Arrival {
        guard let existing else { return .start }
        if existing.stage == .waiting { return .resume }
        if existing.isActive { return .skip("이미 처리 중: \(existing.statusText)") }
        return .skip("이미 처리함: \(existing.statusText)\(existing.reason.map { " · \($0)" } ?? "")")
    }

    public var role: PipelineRole? { PipelineRole(rawValue: stage.rawValue) }

    public var totalCost: Double { costs.values.reduce(0, +) }

    public var statusText: String {
        switch stage {
        case .tester: return "\(key) · tester \(testerBacks)/\(Self.backLimit)"
        case .reviewer: return "\(key) · reviewer \(reviewerBacks)/\(Self.backLimit)"
        case .waiting: return "\(key) · 답 기다림"
        case .stopped: return "\(key) · 멈춤"
        default: return "\(key) · \(stage.rawValue)"
        }
    }

    public func allowed(for role: PipelineRole) -> [PipelineNext.Kind] {
        switch role {
        case .pm:
            if mustStop { return [.stop] }
            return testsPassed ? [.reviewer, .dev, .stop] : [.dev, .stop]
        case .dev: return [.tester, .pm]
        case .tester: return [.dev, .pm]
        case .reviewer: return [.dev, .push, .pm]
        }
    }

    public mutating func stop(_ why: String, now: Date) -> Outcome {
        stage = .stopped
        reason = why
        updatedAt = now
        return .blocked(why)
    }

    public mutating func apply(role: PipelineRole, next: PipelineNext?, summary text: String, now: Date) -> Outcome {
        guard let next else { return stop("\(role.label) 답에 다음 단계 블록이 없거나 깨졌어요", now: now) }
        guard allowed(for: role).contains(next.kind) else {
            return stop("\(role.label) 은 지금 \(next.kind.rawValue) 로 넘길 수 없어요", now: now)
        }
        history.append(Step(role: role.rawValue, next: next.kind.rawValue, note: String(next.note.prefix(300)), at: now))
        if let message = next.message { commitMessage = message }
        note = next.note
        summary = String(PipelineNext.strip(text).suffix(1500))
        from = role.rawValue
        updatedAt = now
        switch (role, next.kind) {
        case (_, .stop):
            stage = next.waiting ? .waiting : .done
            return .finished
        case (.pm, .dev):
            brief = next.note
            if worktree == nil {
                guard let repo = next.repo ?? repo, let branch = next.branch else { return stop("PM 이 레포·브랜치를 정하지 않았어요", now: now) }
                self.repo = repo
                self.branch = branch
                create = next.create
                stage = .branch
            } else {
                stage = .dev
            }
        case (.dev, .tester):
            testsPassed = false
            stage = .tester
        case (.tester, .pm):
            testsPassed = true
            stage = .pm
        case (.tester, .dev):
            testerBacks += 1
            testsPassed = false
            stage = testerBacks > Self.backLimit ? limit("테스터→개발") : .dev
        case (.reviewer, .dev):
            reviewerBacks += 1
            testsPassed = false
            stage = reviewerBacks > Self.backLimit ? limit("리뷰어→개발") : .dev
        case (.reviewer, .push):
            stage = .push
        default:
            stage = PipelineState.Stage(rawValue: next.kind.rawValue) ?? .stopped
        }
        return .next
    }

    private mutating func limit(_ which: String) -> Stage {
        mustStop = true
        note = "\(which) 되돌림이 \(Self.backLimit)번을 넘어 멈춘다. 마지막 지적: \(note)\nJira 에 확인 필요(또는 개발 중단) 댓글을 남기고 blocked 알림, jira-state 를 한 뒤 stop 한다."
        return .pm
    }

    public mutating func branchReady(worktree path: String, now: Date) {
        worktree = path
        stage = .dev
        updatedAt = now
    }

    public mutating func handBack(_ text: String, now: Date) {
        mustStop = true
        note = text
        from = "pokemate"
        stage = .pm
        updatedAt = now
    }

    public mutating func pushDone(_ output: String, now: Date) {
        pushed = output
        handBack("PokeMate 가 커밋·push 했다.\n\(output)\nIn Review 로 바꾸고 결과 댓글, processed 알림, jira-state 를 한 뒤 stop 한다.", now: now)
    }

    public mutating func resume(now: Date) {
        stage = .pm
        testerBacks = 0
        reviewerBacks = 0
        mustStop = false
        reason = nil
        from = "pokemate"
        note = "확인 필요 댓글에 답이 달렸다. 공통 지시의 이어 가기 규칙대로 처리한다."
        updatedAt = now
    }

    public static func dir(base: URL = AppPaths.dataDir) -> URL { base.appendingPathComponent("automation/pipelines", isDirectory: true) }

    public static func file(key: String, in dir: URL = dir()) -> URL { dir.appendingPathComponent("\(key).json") }

    public static func load(key: String, in dir: URL = dir()) -> PipelineState? {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file(key: key, in: dir)) else { return nil }
        return try? decoder.decode(PipelineState.self, from: data)
    }

    public static func loadAll(in dir: URL = dir()) -> [PipelineState] {
        let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "json" }.compactMap { load(key: $0.deletingPathExtension().lastPathComponent, in: dir) }
            .sorted { $0.updatedAt > $1.updatedAt }
    }

    public func save(in dir: URL = dir()) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try? encoder.encode(self).write(to: Self.file(key: key, in: dir), options: .atomic)
    }
}

public enum PipelineAccess {
    public static var file: URL { AppPaths.localFile("pipeline-tools.json") }

    public static func helper(settings: AppSettings) -> String {
        "uv --directory \(settings.repoDir)/mcp-server run --quiet pokemate auto"
    }

    public static func defaults(helper: String, workDir: String, references: String, pipelines: String) -> [PipelineRole: ClaudeCommand.WriteAccess] {
        func bash(_ names: [String]) -> [String] { names.map { "Bash(\(helper) \($0):*)" } }
        return [
            .pm: ClaudeCommand.WriteAccess(tools: bash(["jira-get", "jira-search", "jira-comments", "jira-links", "jira-transition", "jira-comment",
                                                        "write-note", "event", "jira-state", "git-read", "domain-note"]), dirs: [workDir, references]),
            .dev: ClaudeCommand.WriteAccess(tools: bash(["run-tests", "git-read"]), dirs: [workDir, references]),
            .tester: ClaudeCommand.WriteAccess(tools: bash(["run-tests", "git-read"]), dirs: [workDir]),
            .reviewer: ClaudeCommand.WriteAccess(tools: bash(["git-read", "domain-note"]) + ["Skill(domain-review)", "Skill(code-review)"],
                                                 dirs: [workDir, pipelines, references]),
        ]
    }

    public static func parse(_ data: Data, home: String = NSHomeDirectory()) -> [PipelineRole: ClaudeCommand.WriteAccess] {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        var out: [PipelineRole: ClaudeCommand.WriteAccess] = [:]
        for role in PipelineRole.allCases {
            guard let item = object[role.rawValue] as? [String: Any] else { continue }
            let clean: (Any?) -> [String] = { (($0 as? [String]) ?? []).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } }
            let dirs = clean(item["dirs"]).map { $0.hasPrefix("~/") ? home + "/" + $0.dropFirst(2) : $0 }
            out[role] = ClaudeCommand.WriteAccess(tools: clean(item["tools"]).filter { !forbidden($0) }, dirs: dirs)
        }
        return out
    }

    public static func forbidden(_ rule: String) -> Bool {
        rule.contains("commit-push") || rule.contains("create-branch") || rule.contains("worktree-add") || rule.contains("gh-review")
    }

    public static func access(for role: PipelineRole, worktree: String?, table: [PipelineRole: ClaudeCommand.WriteAccess]) -> ClaudeCommand.WriteAccess {
        var access = table[role] ?? ClaudeCommand.WriteAccess()
        access.tools = access.tools.filter { rule in
            !forbidden(rule) && (role == .dev || !(rule.hasPrefix("Edit(") || rule.hasPrefix("Write(") || rule.hasPrefix("MultiEdit(")))
        }
        if role == .dev, let w = worktree, !w.isEmpty {
            access.tools += ["Edit(/\(w)/**)", "Write(/\(w)/**)"]
            access.dirs.append(w)
        }
        return access
    }

    public static func encode(_ table: [PipelineRole: ClaudeCommand.WriteAccess]) -> Data {
        var object: [String: Any] = [:]
        for (role, access) in table { object[role.rawValue] = ["tools": access.tools, "dirs": access.dirs] }
        return (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])) ?? Data()
    }
}

public struct PipelineRoleResult: Sendable {
    public var text: String?
    public var sessionId: String?
    public var usage: RunUsage?
    public var failure: String?
    public var interrupted: Bool

    public init(text: String? = nil, sessionId: String? = nil, usage: RunUsage? = nil, failure: String? = nil, interrupted: Bool = false) {
        self.text = text
        self.sessionId = sessionId
        self.usage = usage
        self.failure = failure
        self.interrupted = interrupted
    }
}

public struct PipelineIO: Sendable {
    public var runRole: @Sendable (PipelineRole, String, String?, String?, ClaudeCommand.WriteAccess) async -> PipelineRoleResult
    public var helper: @Sendable ([String]) async -> (ok: Bool, output: String)
    public var diff: @Sendable (String) async -> String?
    public var sessionId: @Sendable (String) async -> String?
    public var recordSession: @Sendable (String, String, String, RunUsage?) async -> Void
    public var save: @Sendable (PipelineState) async -> Void
    public var notice: @Sendable (String, String) async -> Void

    public init(runRole: @escaping @Sendable (PipelineRole, String, String?, String?, ClaudeCommand.WriteAccess) async -> PipelineRoleResult,
                helper: @escaping @Sendable ([String]) async -> (ok: Bool, output: String),
                diff: @escaping @Sendable (String) async -> String?,
                sessionId: @escaping @Sendable (String) async -> String?,
                recordSession: @escaping @Sendable (String, String, String, RunUsage?) async -> Void,
                save: @escaping @Sendable (PipelineState) async -> Void,
                notice: @escaping @Sendable (String, String) async -> Void = { _, _ in }) {
        self.runRole = runRole
        self.notice = notice
        self.helper = helper
        self.diff = diff
        self.sessionId = sessionId
        self.recordSession = recordSession
        self.save = save
    }
}

public struct PipelineConfig: Sendable {
    public var prompts: [PipelineRole: String]
    public var common: String
    public var access: [PipelineRole: ClaudeCommand.WriteAccess]
    public var dir: URL
    public var maxSteps: Int
    public var domains: DomainMap
    public var home: String
    public var notes: DomainNotes

    public init(prompts: [PipelineRole: String], common: String, access: [PipelineRole: ClaudeCommand.WriteAccess], dir: URL, maxSteps: Int = 40,
                domains: DomainMap = DomainMap(), home: String = NSHomeDirectory(), notes: DomainNotes? = nil) {
        self.prompts = prompts
        self.common = common
        self.access = access
        self.dir = dir
        self.maxSteps = maxSteps
        self.domains = domains
        self.home = home
        self.notes = notes ?? DomainNotes(root: DomainNotes.root(fromConnections: nil, home: home))
    }

    public static func promptName(_ role: PipelineRole) -> String { "jira-\(role.rawValue)" }
    public static let commonName = "jira-common"

    public static func load(promptsDir: URL, access: [PipelineRole: ClaudeCommand.WriteAccess], dir: URL, helper: String,
                            fallbackDir: URL? = nil) -> PipelineConfig {
        func read(_ name: String) -> String? {
            let text = [promptsDir, fallbackDir].compactMap { $0 }.lazy.compactMap { try? String(contentsOf: Rules.promptFile(name, in: $0), encoding: .utf8) }.first
            return text?.replacingOccurrences(of: "{{helper}}", with: helper)
        }
        var prompts: [PipelineRole: String] = [:]
        for role in PipelineRole.allCases {
            if let text = read(promptName(role)) { prompts[role] = text }
        }
        let common = read(commonName) ?? ""
        return PipelineConfig(prompts: prompts, common: common, access: access, dir: dir)
    }
}

public enum JiraPipeline {
    public static let interruptedNote = "앱이 꺼져서 이 역할 실행이 중간에 끊겼다. 이미 한 일(Jira 댓글·상태·알림·jira-state·W 파일 수정)을 Jira·W·기록으로 먼저 확인하고, 이미 한 것은 다시 하지 말고 남은 것만 이어서 한다. 끝나면 아래 지시대로 답 끝 블록을 붙인다."

    public static let resumeNote = "이 세션은 같은 도메인에서 같은 역할로 이전 티켓을 처리하던 세션이다. 이미 읽은 지식은 다시 읽지 말고 이번 티켓에 새로 필요한 것만 찾아라."

    public static func prompt(role: PipelineRole, state s: PipelineState, config: PipelineConfig, resumed: Bool, diffFile: String?) -> String {
        var parts: [String] = []
        parts.append("이번 건: \(s.key) · 역할: \(role.label) (\(PipelineRole.sessionKey(domain: s.domain, role: role)))")
        if resumed { parts.append(resumeNote) }
        parts.append(s.event)
        let knowledge = config.domains.knowledgeText(for: s.domain, home: config.home)
        if !config.common.isEmpty { parts.append("## 공통 지시\n" + config.common.replacingOccurrences(of: DomainMap.placeholder, with: knowledge)) }
        parts.append("## 역할 지시 (\(role.label))\n" + (config.prompts[role] ?? "").replacingOccurrences(of: DomainMap.placeholder, with: knowledge))
        var now = ["## 지금 상황", "- 도메인: \(s.domain)", "- 레포: \(s.repo ?? "아직 정하지 않음")", "- 브랜치: \(s.branch ?? "아직 없음")",
                   "- 작업 폴더 W: \(s.worktree ?? "아직 없음")", "- 커밋 메시지(예정): \(s.commitMessage ?? "아직 없음")",
                   "- 테스트 통과: \(s.testsPassed ? "예" : "아니오")",
                   "- 되돌림: 테스터→개발 \(s.testerBacks)/\(PipelineState.backLimit), 리뷰어→개발 \(s.reviewerBacks)/\(PipelineState.backLimit)"]
        if !s.history.isEmpty { now.append("- 지나온 단계: " + s.history.map { "\($0.role)→\($0.next)" }.joined(separator: ", ")) }
        if let diffFile { now.append("- 리뷰할 변경(diff 파일): \(diffFile)") }
        if role != .tester { now.append(config.notes.promptLines(domain: s.domain, helper: "$H", source: s.key, canWrite: role != .dev)) }
        parts.append(now.joined(separator: "\n"))
        if role != .pm, let brief = s.brief, !brief.isEmpty { parts.append("## PM 개발 지시서\n\(brief)") }
        if let from = s.from {
            var handoff = "## 앞 단계(\(PipelineRole(rawValue: from)?.label ?? from))가 넘긴 말\n\(s.note.isEmpty ? "(없음)" : s.note)"
            if !s.summary.isEmpty && from != "pokemate" { handoff += "\n\n### 앞 단계 답 요약\n\(s.summary)" }
            parts.append(handoff)
        }
        let allowed = s.allowed(for: role).map(\.rawValue).joined(separator: " | ")
        var tail = """
        ## 답 끝 블록
        답 끝에 아래 블록 하나만 붙인다. next 는 다음 중 하나: \(allowed)
        ```\(PipelineNext.tag)
        {"next": "...", "note": "다음 단계에게 넘길 말"}
        ```
        """
        if role == .pm && s.worktree == nil && !s.mustStop {
            tail += "\ndev 로 넘길 때는 \"repo\"(레포 폴더 이름), \"branch\"(작업 브랜치), \"create\"([{\"branch\": …, \"from\": …}] 만들 순서대로), \"message\"(커밋 메시지 한 줄)를 함께 넣는다."
        }
        if role == .pm { tail += "\nstop 하면서 확인 필요 답을 기다리면 \"waiting\": true 를 넣는다." }
        if role == .dev { tail += "\n커밋 메시지를 바꿔야 하면 \"message\" 를 넣는다." }
        parts.append(tail)
        return parts.joined(separator: "\n\n")
    }

    public static func run(_ start: PipelineState, io: PipelineIO, config: PipelineConfig, now: @Sendable () -> Date = { Date() }) async -> PipelineState {
        var s = start
        var steps = 0
        while s.isActive {
            steps += 1
            if steps > config.maxSteps {
                _ = s.stop("단계가 \(config.maxSteps)번을 넘었어요", now: now())
                break
            }
            switch s.stage {
            case .branch:
                await makeBranch(&s, io: io, now: now)
            case .push:
                await push(&s, io: io, now: now)
            default:
                guard let role = s.role else { break }
                guard await runRole(role, &s, io: io, config: config, now: now) else { return s }
            }
            await io.save(s)
        }
        if s.stage == .stopped {
            let why = s.reason ?? "멈춤"
            _ = await io.helper(["event", "--notify", "--source", "jira", "--task", "pokemate-jira", "--event", "blocked", "--key", s.key,
                                 "--title", s.title, "--result", why])
            _ = await io.helper(["jira-state", "--key", s.key, "--type", "기타", "--result", "멈춤: \(why)"])
            await io.save(s)
        }
        return s
    }

    private static func makeBranch(_ s: inout PipelineState, io: PipelineIO, now: @Sendable () -> Date) async {
        guard let repo = s.repo, let branch = s.branch else {
            _ = s.stop("레포·브랜치가 없어요", now: now())
            return
        }
        for b in s.create {
            let r = await io.helper(["create-branch", "--repo", repo, "--branch", b.branch, "--from", b.from])
            if !r.ok {
                s.handBack("PokeMate 가 브랜치 \(b.branch) 를 만들지 못했다.\n\(r.output)\n개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now: now())
                return
            }
        }
        let w = await io.helper(["worktree-add", "--repo", repo, "--branch", branch])
        let path = w.output.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.last { $0.hasPrefix("/") }
        guard w.ok, let path else {
            s.handBack("PokeMate 가 작업 폴더를 만들지 못했다.\n\(w.output)\n개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now: now())
            return
        }
        s.branchReady(worktree: path, now: now())
    }

    private static func push(_ s: inout PipelineState, io: PipelineIO, now: @Sendable () -> Date) async {
        guard let w = s.worktree, let message = s.commitMessage else {
            s.handBack("커밋 메시지나 작업 폴더가 없어 push 하지 못했다. 개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now: now())
            return
        }
        let r = await io.helper(["commit-push", "--worktree", w, "--message", message])
        if r.ok {
            s.pushDone(r.output, now: now())
        } else {
            s.handBack("PokeMate 가 커밋·push 하지 못했다.\n\(r.output)\n개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now: now())
        }
    }

    @discardableResult
    private static func runRole(_ role: PipelineRole, _ s: inout PipelineState, io: PipelineIO, config: PipelineConfig, now: @Sendable () -> Date) async -> Bool {
        guard config.prompts[role] != nil else {
            _ = s.stop("\(role.label) 역할 프롬프트(\(PipelineConfig.promptName(role)).md)가 없어요", now: now())
            return true
        }
        var diffFile: String?
        if role == .reviewer {
            guard let w = s.worktree, let diff = await io.diff(w) else {
                _ = s.stop("리뷰할 변경을 만들지 못했어요", now: now())
                return true
            }
            let file = config.dir.appendingPathComponent("\(s.key).diff")
            try? FileManager.default.createDirectory(at: config.dir, withIntermediateDirectories: true)
            try? diff.write(to: file, atomically: true, encoding: .utf8)
            diffFile = file.path
        }
        let sessionKey = PipelineRole.sessionKey(domain: s.domain, role: role)
        let known = await io.sessionId(sessionKey)
        var access = PipelineAccess.access(for: role, worktree: s.worktree, table: config.access)
        access.dirs += (config.domains.knowledgePaths(for: s.domain, home: config.home) + [config.notes.root]).filter { !access.dirs.contains($0) }
        let interrupted = s.inflight?.role == role.rawValue ? s.inflight : nil
        var result: PipelineRoleResult
        var usedId: String
        if let interrupted {
            let text = interruptedNote + "\n\n" + prompt(role: role, state: s, config: config, resumed: true, diffFile: diffFile)
            result = await io.runRole(role, text, interrupted.sessionId, nil, access)
            usedId = interrupted.sessionId
            if result.interrupted { return false }
            if result.text == nil {
                s.costs[role.rawValue, default: 0] += result.usage?.costUSD ?? 0
                let fresh = UUID().uuidString.lowercased()
                s.inflight = PipelineState.Inflight(role: role.rawValue, sessionId: fresh, startedAt: now())
                await io.save(s)
                let again = interruptedNote + "\n\n" + prompt(role: role, state: s, config: config, resumed: false, diffFile: diffFile)
                result = await io.runRole(role, again, nil, fresh, access)
                usedId = fresh
            }
        } else {
            let fresh = known == nil ? UUID().uuidString.lowercased() : nil
            usedId = known ?? fresh ?? ""
            s.inflight = PipelineState.Inflight(role: role.rawValue, sessionId: usedId, startedAt: now())
            await io.save(s)
            let text = prompt(role: role, state: s, config: config, resumed: known != nil, diffFile: diffFile)
            result = await io.runRole(role, text, known, fresh, access)
        }
        if result.interrupted { return false }
        s.inflight = nil
        if let id = result.sessionId ?? (usedId.isEmpty ? nil : usedId), !id.isEmpty { await io.recordSession(sessionKey, id, s.key, result.usage) }
        s.costs[role.rawValue, default: 0] += result.usage?.costUSD ?? 0
        guard result.failure == nil, let answer = result.text else {
            _ = s.stop("\(role.label) 실행 실패: \(result.failure ?? "답 없음")", now: now())
            return true
        }
        let testerBefore = s.testerBacks, reviewerBefore = s.reviewerBacks
        _ = s.apply(role: role, next: PipelineNext.extract(from: answer), summary: answer, now: now())
        if s.testerBacks > testerBefore || s.reviewerBacks > reviewerBefore {
            await bounceNotice(s, io: io, tester: s.testerBacks > testerBefore)
        }
        return true
    }

    private static func bounceNotice(_ s: PipelineState, io: PipelineIO, tester: Bool) async {
        let which = tester ? "테스터→개발" : "리뷰어→개발"
        let count = tester ? s.testerBacks : s.reviewerBacks
        let why = String((s.history.last?.note ?? "").prefix(200))
        let over = count > PipelineState.backLimit
        let result = over ? "\(which) 되돌림 상한 \(PipelineState.backLimit)번 넘음 → PM 이 멈춤 처리: \(why)"
                          : "\(which) 되돌림 \(count)/\(PipelineState.backLimit): \(why)"
        _ = await io.helper(["event", "--notify", "--source", "jira", "--task", "pokemate-jira", "--event", "changed", "--key", s.key,
                             "--title", s.title, "--kind", "핑퐁", "--result", result])
        await io.notice(over ? "핑퐁 상한 초과 · \(s.key)" : "핑퐁 \(which) \(count)/\(PipelineState.backLimit) · \(s.key)", result)
    }
}
