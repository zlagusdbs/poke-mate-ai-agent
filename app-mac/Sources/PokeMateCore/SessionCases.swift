import Foundation

/// 인계 메모 한 줄: 역할이 한 차례를 마칠 때 앱이 작업 건 파일(`pipelines/{KEY}.json`·`prs/{owner}__{repo}__{n}.json`)의 `handoffs` 에 남긴다.
/// JSON: `{"role", "at"(ISO 8601), "summary", "findings"(리뷰어만: 결함·미확인 항목 제목)}`
public struct CaseHandoff: Codable, Equatable, Sendable {
    public var role: String
    public var at: Date
    public var summary: String
    public var findings: [String]?

    public init(role: String, at: Date, summary: String, findings: [String]? = nil) {
        self.role = role
        self.at = at
        self.summary = summary
        self.findings = findings
    }
}

/// 세션 맥락은 대화 기록이 아니라 파일로 넘긴다: 인계 메모 만들기·"이번 건 인계" 블록·리뷰어 지적 뽑기.
public enum Handoffs {
    /// 블록에 넣는 최근 메모 수
    public static let maxNotes = 8
    /// 블록 전체 글자 수 상한
    public static let maxChars = 4000
    /// 파일에 남기는 메모 수 상한 (오래된 것부터 버림)
    public static let keep = 50
    public static let summaryLimit = 600
    public static let findingLimit = 160
    public static let maxFindings = 20

    public static let title = "## 이번 건 인계"
    public static let intro = "이 건의 앞 차례들이 남긴 인계 메모다(오래된 것부터). 이 세션은 새로 시작해 앞 대화를 모르니, 이 메모와 파일(작업 건 파일·도메인 노트·지도)로 맥락을 잡는다."
    public static let findingsTitle = "### 내 이전 지적 (리뷰어)"

    /// 메모 요약: 다음 단계에게 넘긴 note 와 답 앞부분 (새로 묻지 않는다). 600자 안.
    public static func summary(note: String, answer: String) -> String {
        let n = note.trimmingCharacters(in: .whitespacesAndNewlines)
        let a = PipelineNext.strip(answer).trimmingCharacters(in: .whitespacesAndNewlines)
        let text: String
        if n.isEmpty { text = a } else if a.isEmpty || a == n { text = n } else { text = n + "\n답: " + String(a.prefix(300)) }
        return String(text.prefix(summaryLimit))
    }

    /// 결과 문서의 "결함"·"미확인(질문)" 절에서 항목 제목을 뽑는다 (`결함: …`, `미확인: …`). 없음·건수 줄은 빼고, 같은 것은 한 번만, 20개까지.
    public static func findings(_ text: String) -> [String] {
        var found: [String] = []
        var current: String?
        for raw in text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false).map(String.init) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            let head = trimLeading(line.replacingOccurrences(of: "**", with: ""), "#* ").trimmingCharacters(in: .whitespacesAndNewlines)
            if let hit = header(head) {
                current = hit.kind
                if !hit.tail.isEmpty && !empty(hit.tail) { add(&found, hit.kind, hit.tail) }
                continue
            }
            guard !line.isEmpty, let kind = current else { continue }
            if let item = bullet(line) {
                if !empty(item) { add(&found, kind, item) }
            } else if raw.hasPrefix(" ") || raw.hasPrefix("\t") {
                continue
            } else {
                current = nil
            }
        }
        return found
    }

    private static func trimLeading(_ s: String, _ chars: String) -> String {
        String(s.drop { chars.contains($0) })
    }

    private static func header(_ head: String) -> (kind: String, tail: String)? {
        for kind in ["결함", "미확인"] {
            guard head.hasPrefix(kind) else { continue }
            var rest = String(head.dropFirst(kind.count))
            if rest.hasPrefix("(") {
                guard let close = rest.firstIndex(of: ")") else { return nil }
                rest = String(rest[rest.index(after: close)...])
            }
            rest = trimLeading(rest, " \t")
            if rest.isEmpty { return (kind, "") }
            if rest.hasPrefix(":") || rest.hasPrefix("：") {
                let tail = String(rest.dropFirst()).trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "* "))
                return (kind, tail)
            }
            return nil
        }
        return nil
    }

    private static func bullet(_ line: String) -> String? {
        for mark in ["- ", "* ", "• "] where line.hasPrefix(mark) {
            return String(line.dropFirst(mark.count)).replacingOccurrences(of: "**", with: "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let digits = line.prefix { $0.isASCII && $0.isNumber }.count
        let chars = Array(line)
        if digits > 0, digits + 1 < chars.count, chars[digits] == "." || chars[digits] == ")", chars[digits + 1] == " " {
            return String(chars[(digits + 2)...]).replacingOccurrences(of: "**", with: "").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return nil
    }

    /// "없음", "0건"·"2 건" 같은 건수만 있는 줄, "-"
    private static func empty(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty || t == "-" || t.hasPrefix("없음") { return true }
        let digits = t.prefix { $0.isASCII && $0.isNumber }.count
        return digits > 0 && t.dropFirst(digits).trimmingCharacters(in: CharacterSet(charactersIn: " ")) == "건"
    }

    private static func add(_ found: inout [String], _ kind: String, _ item: String) {
        guard found.count < maxFindings else { return }
        let title = "\(kind): \(item.prefix(findingLimit))"
        if !found.contains(title) { found.append(title) }
    }

    /// 새 세션 프롬프트 앞에 붙이는 "이번 건 인계" 블록. 같은 건의 앞 차례가 없으면 nil (처음).
    /// 최근 메모 8개(오래된 것부터), 리뷰어에게는 자신의 마지막 지적. 블록 전체 4,000자 안: 넘치면 오래된 메모부터 뺀다.
    public static func block(_ handoffs: [CaseHandoff]?, role: String) -> String? {
        guard let handoffs, !handoffs.isEmpty else { return nil }
        let mine = role == PipelineRole.reviewer.rawValue
            ? handoffs.last(where: { $0.role == role && !($0.findings ?? []).isEmpty })?.findings
            : nil
        var notes = Array(handoffs.suffix(maxNotes))
        while true {
            var parts = [title, intro] + notes.map(line)
            if let mine {
                parts.append(findingsTitle)
                parts += mine.map { "- \($0)" }
            }
            let text = parts.joined(separator: "\n")
            if text.count <= maxChars { return text }
            if notes.count > 1 {
                notes.removeFirst()
                continue
            }
            return String(text.prefix(maxChars - 1)) + "…"
        }
    }

    private static func line(_ h: CaseHandoff) -> String {
        let who = PipelineRole(rawValue: h.role)?.label ?? h.role
        let summary = h.summary.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }.joined(separator: " / ")
        return "- \(who): \(summary.isEmpty ? "(메모 없음)" : summary)"
    }

    /// 메모를 덧붙인다 (오래된 것부터 50개까지)
    public static func append(_ list: [CaseHandoff]?, _ handoff: CaseHandoff) -> [CaseHandoff] {
        var all = list ?? []
        all.append(handoff)
        if all.count > keep { all.removeFirst(all.count - keep) }
        return all
    }
}

/// GitHub PR 한 건의 작업 건 파일 `automation/prs/{owner}__{repo}__{number}.json` (Jira 의 `pipelines/{KEY}.json` 과 같은 모양의 `sessions`·`handoffs`).
/// 리뷰를 APPROVE 로 올렸거나 PR 이 닫힌·머지된 것을 확인하면 보관(`archivedAt`)한다. 같은 PR 에 다시 리뷰 요청이 오면 다시 연다.
public struct ReviewCase: Codable, Equatable, Sendable {
    /// open(리뷰 중) · approved(승인함) · closed(닫힘·머지됨)
    public enum Status: String, Codable, Sendable { case open, approved, closed }

    public static let reviewerRole = "reviewer"

    public var key: String
    public var owner: String
    public var repo: String
    public var number: Int
    public var url: String
    public var domain: String
    public var status: Status = .open
    /// 역할(reviewer) → 이 PR 의 그 역할 세션
    public var sessions: [String: PipelineSession]?
    public var handoffs: [CaseHandoff]?
    public var archivedAt: Date?
    public var createdAt: Date
    public var updatedAt: Date

    public init(target: ReviewTarget, domain: String, now: Date) {
        key = SessionPolicy.prKey(owner: target.owner, repo: target.repo, number: target.number)
        owner = target.owner
        repo = target.repo
        number = target.number
        url = target.url
        self.domain = domain
        createdAt = now
        updatedAt = now
    }

    /// 작업 탭 기록(`keys`)에 쓰는 이름 `repo#12`
    public var label: String { "\(repo)#\(number)" }

    public var isArchived: Bool { archivedAt != nil }

    /// 그 역할의 이 PR 세션 (`SessionPolicy.decide` 의 입력). 없으면 nil.
    public func previousSession(for role: String) -> SessionPolicy.Previous? {
        guard let s = sessions?[role], !s.id.isEmpty else { return nil }
        return SessionPolicy.Previous(caseKey: key, id: s.id, lastUsed: s.lastUsed, contextTokens: s.contextTokens)
    }

    public mutating func recordSession(role: String, id: String, usage: RunUsage?, now: Date) {
        var all = sessions ?? [:]
        let before = all[role].flatMap { $0.id == id ? $0.contextTokens : nil } ?? 0
        all[role] = PipelineSession(id: id, lastUsed: now, contextTokens: usage?.context ?? before)
        sessions = all
        updatedAt = now
    }

    /// 리뷰 한 차례 시작: PR 파일을 읽거나(보관했으면 다시 열고) 만들고, `SessionPolicy` 로 이어 쓸지 고른다.
    public static func begin(target: ReviewTarget, domain: String, policy: SessionPolicy, in dir: URL = dir(), now: Date) -> (review: ReviewCase, decision: SessionPolicy.Decision) {
        var c = load(target: target, in: dir) ?? ReviewCase(target: target, domain: domain, now: now)
        c.domain = domain
        c.url = target.url
        if c.isArchived {
            c.archivedAt = nil
            c.status = .open
        }
        c.updatedAt = now
        return (c, policy.decide(caseKey: c.key, role: reviewerRole, previous: c.previousSession(for: reviewerRole), now: now))
    }

    /// 새 세션이면 붙이는 "이번 건 인계" 블록 (이어 쓰면·처음이면 nil)
    public func handoffBlock(_ decision: SessionPolicy.Decision) -> String? {
        decision.resumed ? nil : Handoffs.block(handoffs, role: Self.reviewerRole)
    }

    /// 리뷰 한 차례가 끝남: 세션·인계 메모(답 첫 줄 요약 + 결함·미확인)를 남기고, 승인·닫힘이면 보관한다. 바뀐 상태를 돌려준다.
    @discardableResult
    public mutating func finish(sessionId: String?, usage: RunUsage?, answer: String?, now: Date) -> Status? {
        if let sessionId, !sessionId.isEmpty { recordSession(role: Self.reviewerRole, id: sessionId, usage: usage, now: now) }
        updatedAt = now
        guard let answer else { return nil }
        handoffs = Handoffs.append(handoffs, CaseHandoff(role: Self.reviewerRole, at: now, summary: Handoffs.summary(note: "", answer: answer),
                                                         findings: Handoffs.findings(answer)))
        guard let done = Self.outcome(answer) else { return nil }
        status = done
        archivedAt = now
        return done
    }

    /// 마지막 답 첫 줄(`레포#번호 · 결과 · …`)의 결과: "승인" → approved, "닫힘" → closed. 그 밖은 nil (리뷰 중 그대로).
    public static func outcome(_ answer: String) -> Status? {
        let first = PipelineNext.strip(answer).replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.first { !$0.isEmpty } ?? ""
        let parts = first.split(separator: "·", omittingEmptySubsequences: false)
            .map { $0.replacingOccurrences(of: "**", with: "").trimmingCharacters(in: .whitespacesAndNewlines) }
        if parts.contains("승인") { return .approved }
        if parts.contains("닫힘") { return .closed }
        return nil
    }

    public static func dir(base: URL = AppPaths.dataDir) -> URL { base.appendingPathComponent("automation/prs", isDirectory: true) }

    public static func fileName(_ target: ReviewTarget) -> String { "\(target.owner)__\(target.repo)__\(target.number).json" }

    public static func file(target: ReviewTarget, in dir: URL = dir()) -> URL { dir.appendingPathComponent(fileName(target)) }

    /// 읽을 수 없거나 깨졌으면 nil
    public static func load(target: ReviewTarget, in dir: URL = dir()) -> ReviewCase? {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file(target: target, in: dir)) else { return nil }
        return try? decoder.decode(ReviewCase.self, from: data)
    }

    /// 키 정렬·들여쓰기·ISO 8601 날짜 (윈도우와 같은 모양)
    public func save(in dir: URL = dir()) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try? encoder.encode(self).write(to: dir.appendingPathComponent("\(owner)__\(repo)__\(number).json"), options: .atomic)
    }
}
