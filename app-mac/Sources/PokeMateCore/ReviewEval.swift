import Foundation

public struct ReviewEvalCase: Codable, Equatable, Sendable {
    public var id: String
    public var url: String
    public var domain: String?

    public init(id: String, url: String, domain: String? = nil) {
        self.id = id
        self.url = url
        self.domain = domain
    }
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
            guard let id = (row["id"] as? String)?.trimmingCharacters(in: .whitespaces), !id.isEmpty,
                  let url = row["url"] as? String, ReviewTarget.parse(url: url) != nil else { return nil }
            let domain = (row["domain"] as? String)?.trimmingCharacters(in: .whitespaces)
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

    public static func domain(for item: ReviewEvalCase, target: ReviewTarget, map: DomainMap, book: ReviewDomainBook) -> (domain: String, why: String, source: DomainReview.Source) {
        if let fixed = item.domain, DomainReview.valid(fixed, map: map) {
            return (fixed, "평가 문제에 정한 도메인", .chosen)
        }
        if let hit = DomainReview.known(target, map: map, book: book) { return hit }
        return (DomainReview.fallback, "평가 문제에 도메인이 없고 기록도 없음", .chosen)
    }

    public static func prompt(item: ReviewEvalCase, target: ReviewTarget, domain: String, why: String, source: DomainReview.Source,
                              notes: DomainNotes, helper: String, template: String, knowledge: String) -> String {
        let event = "다음 이벤트가 생겼다.\n- [리뷰 요청] \(target.label)\n- 평가 문제 \(item.id)\n- 링크: \(item.url)"
        let body = template.replacingOccurrences(of: DomainMap.placeholder, with: knowledge).replacingOccurrences(of: "{{helper}}", with: helper)
        return DomainReview.reviewPrompt(target: target, domain: domain, why: why, source: source, firstRun: true, notes: notes, helper: helper,
                                         context: "\(event)\n\n요청: \(body)\n\n\(tail)")
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
                            domain: String) -> [String: Any] {
        var row: [String: Any] = ["costUSD": costUSD, "seconds": seconds, "skills": skills, "sessionId": sessionId, "ok": ok, "domain": domain]
        if let usage {
            row["input"] = usage.input
            row["output"] = usage.output
            row["cacheRead"] = usage.cacheRead
            row["cacheWrite"] = usage.cacheWrite
        }
        return row
    }
}
