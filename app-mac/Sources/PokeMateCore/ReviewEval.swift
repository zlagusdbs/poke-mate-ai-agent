import Foundation

public struct ReviewEvalCase: Codable, Equatable, Sendable {
    public var id: String
    public var url: String
    public var domain: String?
    public var fixture: String?

    public init(id: String, url: String, domain: String? = nil, fixture: String? = nil) {
        self.id = id
        self.url = url
        self.domain = domain
        self.fixture = fixture
    }
}

public struct ReviewEvalFixture: Codable, Equatable, Sendable {
    public var key: String
    public var title: String
    public var repo: String
    public var branch: String
    public var brief: String
    public var note: String
}

public enum ReviewEval {
    public static let readOnlyCommands = ["gh-get", "gh-diff", "gh-file", "git-read", "jira-get", "jira-comments"]
    public static let skills = ["Skill(domain-review)", "Skill(code-review)"]
    public static let blockTag = "pokemate-eval"

    public static let tail = """
    ## 평가 실행
    이번은 리뷰 기준 문제 평가 실행이다. 2절 리뷰만 domain-review 스킬대로 끝까지 한다.
    - PR 이 닫혔거나 머지됐어도, 이미 리뷰한 기록이 있어도 리뷰한다. 1절의 상태·requested_reviewers·review-state 확인과 시작 알림은 건너뛴다.
    - 3절 등록(write-note·gh-review), 끝 알림, review-state 저장, domain-note 는 하지 않는다(막혀 있다).
    - 답 끝에 스킬 6절 결과 문서 전체를 아래 블록 하나에 넣는다.
    ```\(blockTag)
    확인한 범위: …
    참고한 근거: …
    점검표: …
    결함: …
    미확인(질문): …
    버린 후보와 이유: …
    제안: …
    판정: 승인 | 댓글 | 확신할 수 없음
    ```
    """

    public static func loadCases(_ data: Data) -> [ReviewEvalCase] {
        guard let rows = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        return rows.compactMap { row in
            guard let id = (row["id"] as? String)?.trimmingCharacters(in: .whitespaces), !id.isEmpty else { return nil }
            let domain = (row["domain"] as? String)?.trimmingCharacters(in: .whitespaces)
            let fixture = (row["fixture"] as? String)?.trimmingCharacters(in: .whitespaces)
            if let fixture, !fixture.isEmpty, !fixture.contains("/"), !fixture.hasPrefix(".") {
                return ReviewEvalCase(id: id, url: "", domain: domain?.isEmpty == true ? nil : domain, fixture: fixture)
            }
            guard let url = row["url"] as? String, ReviewTarget.parse(url: url) != nil else { return nil }
            return ReviewEvalCase(id: id, url: url, domain: domain?.isEmpty == true ? nil : domain)
        }
    }

    public static func access(helper: String, dirs: [String]) -> ClaudeCommand.WriteAccess {
        var unique: [String] = []
        for dir in dirs where !unique.contains(dir) { unique.append(dir) }
        return ClaudeCommand.WriteAccess(tools: readOnlyCommands.map { "Bash(\(helper) \($0):*)" } + skills, dirs: unique)
    }

    public static func environment(target: ReviewTarget) -> [String: String] {
        ["POKEMATE_EVAL": "1", "POKEMATE_REVIEW_REPO": target.fullName]
    }

    /// 도메인: 문제에 적은 것 → domains.json 의 repos → default
    public static func domain(for item: ReviewEvalCase, target: ReviewTarget, map: DomainMap) -> (domain: String, why: String, source: DomainReview.Source) {
        if let fixed = item.domain, DomainReview.valid(fixed, map: map) {
            return (fixed, "평가 문제에 정한 도메인", .chosen)
        }
        if let hit = DomainReview.listed(target, map: map) { return hit }
        return (DomainReview.fallback, "평가 문제에 도메인이 없고 domains.json 의 repos 에도 없음", .fallback)
    }

    public static func prompt(item: ReviewEvalCase, target: ReviewTarget, domain: String, why: String, source: DomainReview.Source,
                              notes: DomainNotes, helper: String, template: String, knowledge: String, firstRun: Bool = true) -> String {
        let event = "다음 이벤트가 생겼다.\n- [리뷰 요청] \(target.label)\n- 평가 문제 \(item.id)\n- 링크: \(item.url)"
        let body = template.replacingOccurrences(of: DomainMap.placeholder, with: knowledge).replacingOccurrences(of: "{{helper}}", with: helper)
        return DomainReview.reviewPrompt(target: target, domain: domain, why: why, source: source, firstRun: firstRun, notes: notes, helper: helper,
                                         context: "\(event)\n\n요청: \(body)\n\n\(tail)")
    }

    public static let pipelineTail = """
    ## 평가 실행
    이번은 리뷰 기준 문제 평가 실행이다. 위 지시대로 domain-review 스킬로 끝까지 리뷰한다. domain-note 는 하지 않는다(막혀 있다).
    답 끝 블록 바로 앞에 스킬 6절 결과 문서 전체를 아래 블록 하나에 넣는다.
    ```\(blockTag)
    확인한 범위: …
    참고한 근거: …
    점검표: …
    결함: …
    미확인(질문): …
    버린 후보와 이유: …
    제안: …
    판정: 승인 | 댓글 | 확신할 수 없음
    ```
    """

    public static func fixtureDir(_ name: String, local: URL = AppPaths.localDir, shared: URL = AppPaths.sharedDir) -> URL? {
        [local, shared].map { $0.appendingPathComponent("eval/fixtures/\(name)", isDirectory: true) }
            .first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("case.json").path) }
    }

    public static func loadFixture(_ dir: URL) -> ReviewEvalFixture? {
        guard let data = try? Data(contentsOf: dir.appendingPathComponent("case.json")) else { return nil }
        return try? JSONDecoder().decode(ReviewEvalFixture.self, from: data)
    }

    public static func fixtureDomain(_ item: ReviewEvalCase, fixture: ReviewEvalFixture, map: DomainMap) -> String {
        if let fixed = item.domain, DomainReview.valid(fixed, map: map) { return fixed }
        return map.domain(for: fixture.repo)
    }

    public static func fixtureState(_ fixture: ReviewEvalFixture, domain: String, worktree: String, now: Date) -> PipelineState {
        let event = "다음 이벤트가 생겼다.\n- [\(fixture.key)] \(fixture.title)\n- 나에게 할당된 이슈\n- 링크: https://jira.example.com/browse/\(fixture.key)"
        var state = PipelineState(key: fixture.key, title: fixture.title, event: event, domain: domain, repo: fixture.repo, timeoutMinutes: 40, now: now)
        state.branch = fixture.branch
        state.worktree = worktree
        state.brief = fixture.brief
        state.stage = .reviewer
        state.testsPassed = true
        state.from = PipelineRole.pm.rawValue
        state.note = fixture.note
        return state
    }

    public static func reviewerAccess(helper: String, worktree: String, dirs: [String]) -> ClaudeCommand.WriteAccess {
        let table = PipelineAccess.defaults(helper: helper, workDir: dirs.first ?? worktree, references: dirs.count > 1 ? dirs[1] : worktree, pipelines: worktree)
        var access = PipelineAccess.access(for: .reviewer, worktree: worktree, table: table)
        access.tools = access.tools.filter { !$0.contains(" domain-note:") && !$0.contains(" domain-map:") }
        for dir in dirs + [worktree] where !access.dirs.contains(dir) { access.dirs.append(dir) }
        return access
    }

    public static func policy(_ text: String) -> SessionPolicy? {
        switch text {
        case "always": return .always
        case "default": return SessionPolicy()
        default:
            let parts = text.split(separator: ",").compactMap { Int($0.trimmingCharacters(in: .whitespaces)) }
            return parts.count == 2 && parts.allSatisfy { $0 >= 0 } ? SessionPolicy(idleMinutes: parts[0], maxTokens: parts[1]) : nil
        }
    }

    /// `--reuse` 로 돌릴 때 문제 하나의 세션 고르기: 앱과 같은 `SessionPolicy.decide` (건 = 문제의 PR `pr:…` 또는 가상 레포 티켓 `jira:…`).
    /// 이전 세션은 평가 세션 기록의 `{도메인}:reviewer` 칸에서 온다. 다른 건이면 늘 새로, 새로 시작하면 칸에 이유를 남긴다.
    public static func chooseSession(_ book: inout WorkSessionBook, slot: String, caseKey: String, policy: SessionPolicy, now: Date) -> SessionPolicy.Decision {
        let decision = policy.decide(caseKey: caseKey, role: PipelineRole.reviewer.rawValue, previous: book.previous(slot), now: now)
        if !decision.resumed { book.startFresh(repo: slot, caseKey: caseKey, reason: decision.reason, now: now) }
        return decision
    }

    /// 성공한 실행만 세션 기록에 남기고, 대화 기록에서 다시 센 값이 있으면 덮어쓴다. 실패한 실행은 기록하지 않는다 (다음 문제는 새로 시작).
    /// `caseId` 는 칸의 `keys` 에 남기는 건 이름이다 (`SessionPolicy.label`: `svc#12`·`ACME-1`).
    public static func recordSession(_ book: inout WorkSessionBook, key: String, sessionId: String, ok: Bool, caseId: String,
                                     usage: RunUsage?, transcript: (usage: RunUsage, lastContext: Int)?, now: Date) {
        guard ok else { return }
        book.record(repo: key, sessionId: sessionId, key: caseId, usage: usage, now: now)
        if let transcript { book.applyTranscript(repo: key, usage: transcript.usage, lastContext: transcript.lastContext) }
    }

    public static func newLines(_ transcript: String, after count: Int) -> String {
        transcript.split(separator: "\n", omittingEmptySubsequences: false).dropFirst(count).joined(separator: "\n")
    }

    public static func skillsUsed(transcript: String) -> [String] {
        var names: [String] = []
        for line in transcript.split(separator: "\n") {
            guard let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                  object["type"] as? String == "assistant",
                  let content = (object["message"] as? [String: Any])?["content"] as? [[String: Any]] else { continue }
            for item in content where item["type"] as? String == "tool_use" && item["name"] as? String == "Skill" {
                if let name = (item["input"] as? [String: Any])?["skill"] as? String { names.append(name) }
            }
        }
        return names
    }

    public static func transcriptFile(configDir: URL, sessionId: String) -> URL? {
        let projects = configDir.appendingPathComponent("projects", isDirectory: true)
        let folders = (try? FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil)) ?? []
        return folders.map { $0.appendingPathComponent("\(sessionId).jsonl") }.first { FileManager.default.fileExists(atPath: $0.path) }
    }

    public static func meta(costUSD: Double, seconds: Double, skills: [String], sessionId: String, usage: RunUsage?, ok: Bool,
                            domain: String, resumed: Bool = false, freshReason: String? = nil, contextAfter: Int? = nil) -> [String: Any] {
        var row: [String: Any] = ["costUSD": costUSD, "seconds": seconds, "skills": skills, "sessionId": sessionId, "ok": ok, "domain": domain, "resumed": resumed]
        if let freshReason { row["freshReason"] = freshReason }
        if let contextAfter { row["contextAfter"] = contextAfter }
        if let usage {
            row["input"] = usage.input
            row["output"] = usage.output
            row["cacheRead"] = usage.cacheRead
            row["cacheWrite"] = usage.cacheWrite
        }
        return row
    }
}
