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

/// 도메인 정하기 3단계(PR 판단)에 넘기는 읽을거리. `files` 는 바뀐 파일, `sharedDocs` 는 팀 KB 공용 문서(`/` 구분 전체 경로),
/// `mapDomains` 는 1단계(팀 KB 지도·domains.json repos)에서 나온 도메인들, `ruleCounts` 는 2단계 경로 규칙의 도메인 → 파일 수.
public struct RouteHints: Equatable, Sendable {
    public var files: [String]
    public var sharedDocs: [String]
    public var mapDomains: [String]
    public var ruleCounts: [String: Int]

    public init(files: [String] = [], sharedDocs: [String] = [], mapDomains: [String] = [], ruleCounts: [String: Int] = [:]) {
        self.files = files
        self.sharedDocs = sharedDocs
        self.mapDomains = mapDomains
        self.ruleCounts = ruleCounts
    }
}

/// PR 리뷰를 도메인 리뷰어 세션(`{도메인}:reviewer`)에 맡기는 흐름: 도메인 정하기와 리뷰 프롬프트.
/// 도메인 정하기: 1. 팀 KB 지도·domains.json repos → 2. 경로 규칙 → 3. PR 판단(Claude) → default. 결과는 저장하지 않는다.
public enum DomainReview {
    public static let tag = "pokemate-domain"
    public static let fallback = DomainMap.defaultKey
    /// 3단계 안내에 넣는 바뀐 파일 수 상한
    public static let fileLimit = 200

    /// 도메인을 어디서 알았나 (단계). `rawValue` 는 근거 줄의 단계 이름.
    public enum Source: String, Sendable {
        case teamMap = "팀 KB 지도"
        case listed = "domains.json 의 repos"
        case pathRule = "경로 규칙"
        case chosen = "PR 판단"
        case fallback = "기본값"
    }

    /// 정한 도메인, 근거, 단계
    public typealias Decision = (domain: String, why: String, source: Source)

    /// 지도 전체 기준 (평가 문제의 고정 도메인 확인용)
    public static func valid(_ domain: String, map: DomainMap) -> Bool {
        domain == fallback || map.domains[domain] != nil
    }

    /// 도메인 후보: 내 팀들의 도메인(`TeamConfig.candidates`), 팀 설정이 없으면 domains.json 의 모든 도메인(이름순). default 는 따로.
    public static func candidates(map: DomainMap, team: TeamConfig?) -> [String] {
        team?.candidates(map: map) ?? map.domains.keys.sorted()
    }

    /// domains.json 의 repos 에 이 레포가 있는 도메인 (이름순 첫 번째, 레포 이름은 대소문자 무시). 평가용.
    public static func listed(_ target: ReviewTarget, map: DomainMap) -> Decision? {
        listedHits(target, map: map, candidates: map.domains.keys.sorted()).first
    }

    static func listedHits(_ target: ReviewTarget, map: DomainMap, candidates: [String]) -> [Decision] {
        let repo = target.repo.lowercased()
        return candidates
            .filter { domain in map.domains[domain]?.contains { $0.lowercased() == repo } == true }
            .map { domain -> Decision in (domain, "domains.json 의 \(domain) repos 에 \(target.repo) 가 있음", .listed) }
    }

    /// 1단계. 내 팀마다 `repoMap` 의 `{domain}` 자리 폴더를 훑어 `{repo}` 문서를 대소문자 무시로 찾는다.
    /// 폴더가 그 팀의 후보 도메인이면 Hit(`teamMap`), 아니면 그 문서를 공용 문서(`sharedDocs`, `/` 구분 전체 경로)로 넘긴다. 팀 KB 폴더가 없으면 건너뛴다.
    /// 쓸 수 있는 팀 도메인(domains.json 에 있는 것)이 하나도 없으면 팀 KB 는 훑지 않는다.
    /// 이어서 후보 도메인의 domains.json repos 에 레포가 있으면 Hit(`listed`).
    public static func mapLookup(target: ReviewTarget, map: DomainMap, team: TeamConfig?, candidates: [String],
                                 home: String = NSHomeDirectory()) -> (hits: [Decision], sharedDocs: [String]) {
        var hits: [Decision] = []
        var shared: [String] = []
        // 쓸 수 있는 팀(domains.json 에 있는 도메인을 가진 팀)이 하나도 없으면 팀 KB 지도는 건너뛰고 domains.json repos 만 본다 (스펙 4-2)
        var mine = team?.mine() ?? []
        if !mine.contains(where: { m in m.entry.domains.contains { map.domains[$0] != nil } }) { mine = [] }
        for m in mine {
            guard let pattern = m.entry.repoMap else { continue }
            for doc in repoDocs(kb: DomainMap.resolvePath(m.entry.kb, home: home), pattern: pattern, repo: target.repo) {
                let folder = doc.folder.lowercased()
                if let domain = m.entry.domains.first(where: { candidates.contains($0) && $0.lowercased() == folder }) {
                    let hit: Decision = (domain, "\(m.team) 팀 KB \(doc.relative)", .teamMap)
                    hits.append(hit)
                } else if !shared.contains(doc.full) {
                    shared.append(doc.full)
                }
            }
        }
        hits += listedHits(target, map: map, candidates: candidates)
        return (hits, shared)
    }

    /// 팀 KB 에서 `pattern` 의 `{domain}` 자리 폴더(이름순)마다 `{repo}` 문서를 찾는다. 경로 조각은 대소문자 무시, 결과는 실제 이름.
    /// `folder` 는 `{domain}` 자리 값, `relative` 는 팀 KB 안의 `/` 구분 경로. `{domain}` 이 없는 패턴·없는 폴더는 빈 목록.
    static func repoDocs(kb: String, pattern: String, repo: String) -> [(folder: String, relative: String, full: String)] {
        var found: [(folder: String, relative: String, full: String)] = []
        let parts = pattern.replacingOccurrences(of: "\\", with: "/").split(separator: "/").map(String.init).filter { $0 != "." }
        guard let at = parts.firstIndex(where: { $0.contains("{domain}") }), at != parts.count - 1 else { return found }
        var dir = kb
        var head: [String] = []
        for i in 0..<at {
            guard let name = child(dir, parts[i].replacingOccurrences(of: "{repo}", with: repo), file: false) else { return found }
            dir = (dir as NSString).appendingPathComponent(name)
            head.append(name)
        }
        let slot = parts[at]
        guard let k = slot.range(of: "{domain}") else { return found }
        let pre = String(slot[..<k.lowerBound]).replacingOccurrences(of: "{repo}", with: repo)
        let post = String(slot[k.upperBound...]).replacingOccurrences(of: "{repo}", with: repo)
        guard let entries = try? FileManager.default.contentsOfDirectory(atPath: dir) else { return found }
        for folder in entries.filter({ isDirectory((dir as NSString).appendingPathComponent($0)) }).sorted() {
            guard folder.count > pre.count + post.count, folder.lowercased().hasPrefix(pre.lowercased()),
                  folder.lowercased().hasSuffix(post.lowercased()) else { continue }
            let domain = String(folder.dropFirst(pre.count).dropLast(post.count))
            var current = (dir as NSString).appendingPathComponent(folder)
            var relative = head + [folder]
            var ok = true
            for j in (at + 1)..<parts.count {
                let want = parts[j].replacingOccurrences(of: "{repo}", with: repo).replacingOccurrences(of: "{domain}", with: domain)
                guard let name = child(current, want, file: j == parts.count - 1) else {
                    ok = false
                    break
                }
                current = (current as NSString).appendingPathComponent(name)
                relative.append(name)
            }
            if ok { found.append((domain, relative.joined(separator: "/"), current)) }
        }
        return found
    }

    /// 폴더 안에서 이름이 대소문자 무시로 같은 파일(또는 폴더)의 실제 이름. 없으면 nil.
    static func child(_ dir: String, _ name: String, file: Bool) -> String? {
        guard let entries = try? FileManager.default.contentsOfDirectory(atPath: dir) else { return nil }
        let want = name.lowercased()
        return entries.filter { isDirectory((dir as NSString).appendingPathComponent($0)) != file }.sorted().first { $0.lowercased() == want }
    }

    static func isDirectory(_ path: String) -> Bool {
        var flag: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &flag) && flag.boolValue
    }

    /// 1·2단계로 도메인을 정한다. 1단계 도메인이 정확히 하나면 첫 Hit, 아니면 경로 규칙이 한 도메인이면 `pathRule`.
    /// 못 정하면 `decided` 는 nil 이고 `hints` 를 3단계(`choosePrompt`)에 넘긴다.
    public static func route(target: ReviewTarget, map: DomainMap, team: TeamConfig?, rules: PathRules, files: [String],
                             home: String = NSHomeDirectory()) -> (decided: Decision?, hints: RouteHints) {
        let cands = Self.candidates(map: map, team: team)
        let lookup = mapLookup(target: target, map: map, team: team, candidates: cands, home: home)
        var domains: [String] = []
        for hit in lookup.hits where !domains.contains(hit.domain) { domains.append(hit.domain) }
        if domains.count == 1 {
            let first: Decision = lookup.hits[0]
            return (first, RouteHints(files: files, sharedDocs: lookup.sharedDocs, mapDomains: domains, ruleCounts: [:]))
        }
        let verdict = rules.judge(fullName: target.fullName, files: files, candidates: cands)
        let routeHints = RouteHints(files: files, sharedDocs: lookup.sharedDocs, mapDomains: domains, ruleCounts: verdict.counts)
        if let domain = verdict.domain {
            let byRule: Decision = (domain, ruleWhy(rules, fullName: target.fullName, files: files, candidates: cands, domain: domain), .pathRule)
            return (byRule, routeHints)
        }
        return (nil, routeHints)
    }

    /// 경로 규칙 근거: 그 도메인 규칙 중 처음 맞은 파일이 가장 많은 규칙(같으면 앞 규칙)의 와일드카드 앞 폴더와 그 규칙의 파일 수.
    /// 예: `src/Areas/Item 아래 1개 파일`
    static func ruleWhy(_ rules: PathRules, fullName: String, files: [String], candidates: [String], domain: String) -> String {
        let active = rules.rules(for: fullName).filter { candidates.contains($0.domain) }
        var counts = Array(repeating: 0, count: active.count)
        for file in files {
            if let i = active.firstIndex(where: { PathRules.matches($0.path, file) }) { counts[i] += 1 }
        }
        var best = -1
        for i in active.indices where active[i].domain == domain && (best < 0 || counts[i] > counts[best]) { best = i }
        guard best >= 0 else { return "" }
        let glob = active[best].path
        if let folder = ruleFolder(glob) { return "\(folder) 아래 \(counts[best])개 파일" }
        return "\(glob) 에 맞는 파일 \(counts[best])개"
    }

    /// glob 의 첫 와일드카드(`*`·`?`) 앞 폴더. 와일드카드 앞에 폴더가 없으면(`Coupon*/**`·`**/x.cs`) nil.
    static func ruleFolder(_ glob: String) -> String? {
        var g = glob.replacingOccurrences(of: "\\", with: "/")
        while g.hasPrefix("./") || g.hasPrefix("/") { g.removeFirst(g.hasPrefix("./") ? 2 : 1) }
        let head = g.firstIndex(where: { $0 == "*" || $0 == "?" }).map { String(g[..<$0]) } ?? g
        guard let slash = head.lastIndex(of: "/"), slash > head.startIndex else { return nil }
        return String(head[..<slash])
    }

    /// 3단계: 리뷰하지 않고 도메인만 정하게 하는 프롬프트 (읽기만). 후보는 `candidates` 와 default.
    public static func choosePrompt(target: ReviewTarget, map: DomainMap, team: TeamConfig?, hints: RouteHints, helper: String) -> String {
        let lines = candidates(map: map, team: team).map { name -> String in
            var parts: [String] = []
            if let owner = team?.teamOf(name) { parts.append("- \(name) (\(owner) 팀)") } else { parts.append("- \(name)") }
            if let text = map.descriptions[name] { parts.append(text) }
            if let words = map.keywords[name], !words.isEmpty { parts.append("키워드: " + words.joined(separator: ", ")) }
            return parts.joined(separator: " · ")
        } + ["- \(fallback) · 위 어느 도메인에도 맞지 않을 때"]
        var output = [
            "지금은 리뷰하지 말고, 아래 PR 이 어느 도메인 일인지만 정한다. 읽기만 한다(GitHub·Jira 쓰기, 알림, 파일 쓰기 금지).",
            "- PR: \(target.url)",
            "- 볼 것 (명령은 한 번에 하나씩, 전체 명령 그대로):",
            "  `\(helper) gh-get repos/\(target.fullName)/pulls/\(target.number) --jq '{title,body}'`",
            "  제목·본문에 Jira 키가 있으면 `\(helper) jira-get --key {KEY}` 의 요약",
        ]
        if hints.files.isEmpty {
            output.append("  `\(helper) gh-get repos/\(target.fullName)/pulls/\(target.number)/files --jq '[.[].filename]'`")
        }
        if let guide = team?.repoGuide {
            output.append("  전사 레포 지도의 이 레포 문서(없으면 건너뛴다): `\(helper) gh-get repos/\(guide)/contents/maps/\(target.owner)/\(target.repo).md --jq '.content | @base64d'`")
        }
        output += hints.sharedDocs.map { "  팀 KB 공용 문서: `\($0.replacingOccurrences(of: "\\", with: "/"))`" }
        if !hints.files.isEmpty {
            output.append("- 바뀐 파일 (\(hints.files.count)개):")
            output += hints.files.prefix(fileLimit).map { "  - \($0)" }
            if hints.files.count > fileLimit { output.append("  - 외 \(hints.files.count - fileLimit)개") }
        }
        if !hints.ruleCounts.isEmpty {
            let ordered = hints.ruleCounts.sorted { a, b in a.value != b.value ? a.value > b.value : a.key < b.key }
            output.append("- 경로 규칙 후보: " + ordered.map { "\($0.key) \($0.value)개" }.joined(separator: ", "))
        }
        if !hints.mapDomains.isEmpty {
            output.append("- 팀 KB 지도 후보: " + hints.mapDomains.joined(separator: ", "))
        }
        output += [
            "- 도메인 목록:",
            lines.joined(separator: "\n"),
            "PR 의 제목·본문·파일 경로·Jira 요약·레포 문서에 나온 단어가 어느 도메인의 설명·키워드와 맞는지로 고른다. 애매하면 더 많이 맞는 쪽, 하나도 안 맞으면 \(fallback).",
            "조회가 실패하면 레포 이름만으로 고르지 말고 \(fallback) 로 답한다.",
            "답 끝에 아래 블록 하나만 붙인다.",
            "```" + tag,
            #"{"domain": "도메인 이름", "why": "고른 이유 한 줄 (어떤 단어·파일이 어느 설명·키워드와 맞는지)"}"#,
            "```",
        ]
        return output.joined(separator: "\n")
    }

    /// 도메인 정하기 답에서 마지막 블록을 읽는다. 후보가 아니면 nil (default 는 됨).
    public static func extract(from text: String, candidates: [String]) -> (domain: String, why: String)? {
        guard let regex = try? NSRegularExpression(pattern: "```\(tag)[ \\t]*\\r?\\n(.*?)```", options: [.dotMatchesLineSeparators]),
              let m = regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).last,
              let r = Range(m.range(at: 1), in: text),
              let object = try? JSONSerialization.jsonObject(with: Data(text[r].utf8)) as? [String: Any],
              let domain = (object["domain"] as? String)?.trimmingCharacters(in: .whitespaces),
              domain == fallback || candidates.contains(domain) else { return nil }
        let why = (object["why"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return (domain, why.isEmpty ? "이유 없음" : why)
    }

    /// 도메인을 정하지 못했을 때의 근거 (3단계 실패·판정 실패)
    public static let undecided = "도메인을 정하지 못해 " + fallback + " 로 리뷰"

    /// 3단계 답(실행 실패면 nil)으로 정한 도메인. 후보면 `chosen`, default 면 `fallback`(이유가 없으면 `undecided`), 못 읽으면 default·`undecided`.
    public static func decide(_ answer: String?, candidates: [String]) -> Decision {
        guard let answer, let choice = extract(from: answer, candidates: candidates) else { return (fallback, undecided, .fallback) }
        if choice.domain != fallback { return (choice.domain, choice.why, .chosen) }
        return (fallback, choice.why == "이유 없음" ? undecided : choice.why, .fallback)
    }

    public static func sessionKey(_ domain: String) -> String { PipelineRole.sessionKey(domain: domain, role: .reviewer) }

    /// 리뷰어 프롬프트. 세션은 PR 한 건마다 따로다(`SessionPolicy`). `firstRun` 은 새 세션인지, `handoff` 는 새 세션일 때 붙이는 "이번 건 인계" 블록.
    public static func reviewPrompt(target: ReviewTarget, domain: String, why: String, source: Source, firstRun: Bool,
                                    notes: DomainNotes, helper: String, context: String, handoff: String? = nil) -> String {
        let session = sessionKey(domain)
        var start = firstRun
            ? "이 세션은 이 PR 의 리뷰어 세션이다(PR 마다 따로 시작한다). 새 세션이니 domain-review 스킬 1절의 지식을 처음부터 읽는다."
            : "이 세션은 이 PR 의 리뷰어 세션으로 앞 차례 리뷰를 해 왔다. 이미 읽은 점검표·노트·지식은 다시 읽지 말고 이번 차례에 새로 필요한 것만 찾는다. 점검표 답은 지금 PR 기준으로 새로 단다."
        if firstRun, let handoff { start += "\n\n" + handoff + "\n" }
        return """
        이번 건: \(target.label) 리뷰 · 도메인 \(domain) (\(source.rawValue): \(why)) · 세션 \(session)
        \(start)
        \(notes.promptLines(domain: domain, helper: helper, source: target.label))
        - 리뷰 본문 "확인한 범위" 첫 줄에 "도메인: \(domain) — \(source.rawValue): \(why)" 를 적고, 시작 알림 result 끝에도 " · 도메인 \(domain)" 을 붙인다.
        - 주변 코드: 로컬 레포 폴더(~/Workspace/\(target.repo))가 없으면 `\(helper) gh-file --repo \(target.fullName) --ref {head sha} --path {파일 경로}` 로 읽는다(이 PR 저장소만 된다). 그래도 못 읽으면 "확인한 범위" 에 적는다.

        \(context)
        """
    }
}

public struct DomainNotes: Equatable, Sendable {
    public static let defaultRoot = "~/.claude/references/notes"
    public static let kinds = ["용어집", "업무 규칙", "장애", "함정", "결정", "동작"]

    public let root: String
    public let lines: @Sendable (String) -> Int?
    public let maps: @Sendable (String) -> [String]

    public init(root: String, lines: @escaping @Sendable (String) -> Int? = DomainNotes.countLines,
                maps: @escaping @Sendable (String) -> [String] = DomainNotes.listMaps) {
        self.root = root
        self.lines = lines
        self.maps = maps
    }

    public static let mapLimit = 20

    public static func listMaps(_ folder: String) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: folder)) ?? [])
            .filter { $0.hasSuffix(".md") && !$0.hasPrefix(".") }
            .map { String($0.dropLast(3)) }
            .sorted()
    }

    public static func == (a: DomainNotes, b: DomainNotes) -> Bool { a.root == b.root }

    public static func countLines(_ path: String) -> Int? {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return nil }
        return text.split(separator: "\n", omittingEmptySubsequences: false).count
    }

    public static func root(fromConnections data: Data?, home: String = NSHomeDirectory()) -> String {
        let rows = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [[String: Any]] } ?? []
        let configured = rows.first { $0["kind"] as? String == "settings" && $0["id"] as? String == "automation" }?["domainNotesDir"] as? String
        let raw = configured?.trimmingCharacters(in: .whitespaces).nilIfEmpty ?? defaultRoot
        return raw.hasPrefix("~/") ? home + "/" + raw.dropFirst(2) : raw
    }

    public static func load(repoDir: String = AppPaths.loadSettings().repoDir, home: String = NSHomeDirectory()) -> DomainNotes {
        if let override = ProcessInfo.processInfo.environment["POKEMATE_NOTES_DIR"], !override.isEmpty { return DomainNotes(root: resolved(override)) }
        return DomainNotes(root: resolved(root(fromConnections: try? Data(contentsOf: LocalNames.file(repoDir: repoDir)), home: home)))
    }

    public static func resolved(_ path: String) -> String {
        URL(fileURLWithPath: path).resolvingSymlinksInPath().path
    }

    public func checklist(_ domain: String) -> String { root + "/" + domain + "/checklist.md" }
    public func notes(_ domain: String) -> String { root + "/" + domain + "/notes.md" }
    public func mapsFolder(_ domain: String) -> String { root + "/" + domain + "/maps" }

    public func promptLines(domain: String, helper: String, source: String, canWrite: Bool = true) -> String {
        guard domain != DomainMap.defaultKey else {
            return "- 도메인 노트: 도메인을 정하지 못해(\(domain)) 점검표·노트가 없다. 결과 문서 점검표 절에 \"도메인 미정, 점검표 없음\" 이라고 적고, 노트는 남기지 않는다."
        }
        let describe: (String) -> String = { path in lines(path).map { "\($0)줄" } ?? "없음" }
        let missing = lines(checklist(domain)) == nil || lines(notes(domain)) == nil
        var out = [
            "- 도메인 노트: 점검표 `\(checklist(domain))` (\(describe(checklist(domain)))), 노트 `\(notes(domain))` (\(describe(notes(domain)))). domain-review 스킬이 말하는 `~/.claude/references/notes/{도메인}/` 은 이 경로다. 노트는 이번 건 키워드로 Grep 한다.",
        ]
        let topics = maps(mapsFolder(domain))
        if !topics.isEmpty {
            let shown = topics.prefix(Self.mapLimit).joined(separator: ", ")
            let more = topics.count > Self.mapLimit ? " 외 \(topics.count - Self.mapLimit)개" : ""
            out.append("- 지도: `\(mapsFolder(domain))/` 아래 \(shown)\(more). 이번 건 주제와 맞는 지도가 있으면 코드를 찾기 전에 먼저 읽는다.")
        }
        guard canWrite else {
            out.append("- 노트·점검표는 읽기만 한다(쓰기는 PM·리뷰어가 한다).\(missing ? " 아직 없으면 점검표 없이 진행하고 지시서의 점검표 답을 따른다." : "")")
            return out.joined(separator: "\n")
        }
        if missing {
            out.append("- 점검표·노트가 없다: 먼저 `\(helper) domain-note --domain \(domain) --init` 로 빈 틀을 만들고, 결과 문서와 끝 알림 result 에 \"이 도메인의 점검표가 아직 없음\" 을 적는다.")
        }
        out.append("- 노트 남기기(스킬 7절): 노트 파일을 직접 고치지 않는다(막혀 있다). `\(helper) domain-note --domain \(domain) --kind {\(Self.kinds.joined(separator: "|"))} --source \"\(source)\" --text \"사실 (근거: 파일 경로:줄 · 위키 pageId · 티켓 키)\"` 로 한 줄씩 남긴다. 추측·미확인·이번 건에만 해당하는 내용은 쓰지 않는다.")
        return out.joined(separator: "\n")
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
