import Foundation

public struct ReviewTarget: Equatable, Sendable {
    public let owner: String
    public let repo: String
    public let number: Int
    public let url: String

    public init(owner: String, repo: String, number: Int, url: String) {
        self.owner = owner
        self.repo = repo
        self.number = number
        self.url = url
    }

    public var fullName: String { "\(owner)/\(repo)" }
    public var label: String { "\(repo)#\(number)" }

    public static func parse(url: String) -> ReviewTarget? {
        guard let parts = URL(string: url)?.pathComponents.filter({ $0 != "/" }), parts.count >= 4, parts[2] == "pull",
              let number = Int(parts[3]) else { return nil }
        return ReviewTarget(owner: parts[0], repo: parts[1], number: number, url: url)
    }
}

public struct ReviewDomainChoice: Codable, Equatable, Sendable {
    public var domain: String
    public var why: String
    public var at: Date

    public init(domain: String, why: String, at: Date) {
        self.domain = domain
        self.why = why
        self.at = at
    }
}

public struct ReviewDomainBook: Codable, Equatable, Sendable {
    public var repos: [String: ReviewDomainChoice] = [:]

    public init(repos: [String: ReviewDomainChoice] = [:]) { self.repos = repos }

    public static var file: URL { AppPaths.dataDir.appendingPathComponent("automation/state/review-domains.json") }

    public static func load(from file: URL = file) -> ReviewDomainBook {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file), let book = try? decoder.decode(ReviewDomainBook.self, from: data) else { return ReviewDomainBook() }
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

public enum DomainReview {
    public static let tag = "pokemate-domain"
    public static let fallback = DomainMap.defaultKey

    public enum Source: String, Sendable {
        case explicit = "domains.json 의 repos"
        case learned = "예전에 정한 기록"
        case chosen = "이번에 PR 내용으로 정함"
    }

    public static func valid(_ domain: String, map: DomainMap) -> Bool {
        domain == fallback || map.domains[domain] != nil
    }

    public static func known(_ target: ReviewTarget, map: DomainMap, book: ReviewDomainBook) -> (domain: String, why: String, source: Source)? {
        if let domain = map.domains.keys.sorted().first(where: { map.domains[$0]?.contains(target.repo) == true }) {
            return (domain, "domains.json 의 \(domain) repos 에 \(target.repo) 가 있음", .explicit)
        }
        if let learned = book.repos[target.fullName], valid(learned.domain, map: map) {
            return (learned.domain, learned.why, .learned)
        }
        return nil
    }

    public static func choosePrompt(target: ReviewTarget, map: DomainMap, helper: String) -> String {
        let lines = map.domains.keys.sorted().map { name -> String in
            var parts = ["- \(name)"]
            if let text = map.descriptions[name] { parts.append(text) }
            if let words = map.keywords[name], !words.isEmpty { parts.append("키워드: " + words.joined(separator: ", ")) }
            if let repos = map.domains[name], !repos.isEmpty { parts.append("레포: " + repos.joined(separator: ", ")) }
            return parts.joined(separator: " · ")
        } + ["- \(fallback) · 위 어느 도메인에도 맞지 않을 때"]
        return """
        지금은 리뷰하지 말고, 아래 PR 이 어느 도메인 일인지만 정한다. 읽기만 한다(GitHub·Jira 쓰기, 알림, 파일 쓰기 금지).
        - PR: \(target.url)
        - 볼 것 (명령은 한 번에 하나씩, 전체 명령 그대로):
          `\(helper) gh-get repos/\(target.fullName)/pulls/\(target.number) --jq '{title,body}'`
          `\(helper) gh-get repos/\(target.fullName)/pulls/\(target.number)/files --jq '[.[].filename]'`
          제목·본문에 Jira 키가 있으면 `\(helper) jira-get --key {KEY}` 의 요약
        - 도메인 목록:
        \(lines.joined(separator: "\n"))
        PR 의 제목·본문·파일 경로·Jira 요약에 나온 단어가 어느 도메인의 설명·키워드·레포와 맞는지로 고른다. 애매하면 더 많이 맞는 쪽, 하나도 안 맞으면 \(fallback).
        답 끝에 아래 블록 하나만 붙인다.
        ```\(tag)
        {"domain": "도메인 이름", "why": "고른 이유 한 줄 (어떤 단어·파일이 어느 설명·키워드와 맞는지)"}
        ```
        """
    }

    public static func extract(from text: String, map: DomainMap) -> (domain: String, why: String)? {
        guard let regex = try? NSRegularExpression(pattern: "```\(tag)[ \\t]*\\n(.*?)```", options: [.dotMatchesLineSeparators]),
              let m = regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).last,
              let r = Range(m.range(at: 1), in: text),
              let object = try? JSONSerialization.jsonObject(with: Data(text[r].utf8)) as? [String: Any],
              let domain = (object["domain"] as? String)?.trimmingCharacters(in: .whitespaces), valid(domain, map: map) else { return nil }
        let why = (object["why"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return (domain, why.isEmpty ? "이유 없음" : why)
    }

    public static func sessionKey(_ domain: String) -> String { PipelineRole.sessionKey(domain: domain, role: .reviewer) }

    public static func memoFile(_ domain: String, local: URL = AppPaths.localDir) -> URL {
        local.appendingPathComponent("domains/\(domain).md")
    }

    public static func reviewPrompt(target: ReviewTarget, domain: String, why: String, source: Source, firstRun: Bool,
                                    memo: URL, memoLines: Int, helper: String, context: String) -> String {
        let session = sessionKey(domain)
        let start = firstRun
            ? "이 세션은 도메인 \(domain) 의 리뷰어 세션이다(Jira 역할별 처리의 리뷰어와 같은 세션). 이번이 첫 실행이니 리뷰 전에 아래 지식 저장소의 목차(README·index)와 도메인 메모를 먼저 읽고, 이 도메인에서 리뷰할 때 알아야 할 것을 10~20줄로 요약해 둔 뒤 리뷰한다."
            : "이 세션은 도메인 \(domain) 의 리뷰어 세션으로 이전 리뷰·처리를 해 왔다. 이미 읽은 지식은 다시 읽지 말고 이번 PR 에 새로 필요한 것만 찾는다."
        let memoLine = memoLines > 0 ? "도메인 메모: \(memo.path) (\(memoLines)줄). 이번 PR 키워드로 Grep 한다." : "도메인 메모: \(memo.path) (아직 없음)."
        return """
        이번 건: \(target.label) 리뷰 · 도메인 \(domain) (\(source.rawValue): \(why)) · 세션 \(session)
        \(start)
        \(memoLine)
        - 리뷰 본문 "확인한 범위" 첫 줄에 "도메인: \(domain) — \(why)" 를 적고, 시작 알림 result 끝에도 " · 도메인 \(domain)" 을 붙인다.
        - 주변 코드: 로컬 레포 폴더(~/Workspace/\(target.repo))가 없으면 `\(helper) gh-file --repo \(target.fullName) --ref {head sha} --path {파일 경로}` 로 읽는다(이 PR 저장소만 된다). 그래도 못 읽으면 "확인한 범위" 에 적는다.
        - 마무리: 이번 리뷰에서 코드·문서로 직접 확인한 도메인 사실이 있으면 0~3줄을 `\(helper) domain-note --domain \(domain) --source "\(target.label)" --text "사실 (근거: 파일 경로·위키 pageId·티켓 키)"` 로 남긴다. 추측·미확인·이번 PR 에만 해당하는 내용은 쓰지 않는다. 도메인 메모는 지식 저장소가 아니다.

        \(context)
        """
    }
}
