import Foundation

/// 팀 하나의 지식 저장소 정보. `kb` 는 팀 KB 경로, `repoMap` 은 `{domain}`·`{repo}` 가 든 레포 설명 경로 패턴.
public struct TeamEntry: Equatable, Sendable {
    public var kb: String
    public var repoMap: String?
    public var domains: [String]

    public init(kb: String, repoMap: String?, domains: [String]) {
        self.kb = kb
        self.repoMap = repoMap
        self.domains = domains
    }
}

/// `local/teams.json`. 내 팀(myTeams) 순서대로 도메인 후보를 만든다.
public struct TeamConfig: Equatable, Sendable {
    public var myTeams: [String]
    public var repoGuide: String?
    public var teams: [String: TeamEntry]

    public init(myTeams: [String], repoGuide: String?, teams: [String: TeamEntry]) {
        self.myTeams = myTeams
        self.repoGuide = repoGuide
        self.teams = teams
    }

    public static var file: URL { AppPaths.localFile("teams.json") }

    /// 객체가 아니면 nil. `kb` 가 빈 팀은 뺀다. `repoMap` 이 쓸 수 없는 패턴(`usableRepoMap`)이면 nil.
    public static func parse(_ data: Data?) -> TeamConfig? {
        guard let data, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        func items(_ value: Any?) -> [String] {
            ((value as? [String]) ?? []).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        }
        func text(_ value: Any?) -> String? {
            guard let s = (value as? String)?.trimmingCharacters(in: .whitespaces), !s.isEmpty else { return nil }
            return s
        }
        var teams: [String: TeamEntry] = [:]
        for (name, value) in (object["teams"] as? [String: Any]) ?? [:] {
            let key = name.trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty, let body = value as? [String: Any], let kb = text(body["kb"]) else { continue }
            var repoMap = text(body["repoMap"])
            if let pattern = repoMap, !usableRepoMap(pattern) { repoMap = nil }
            teams[key] = TeamEntry(kb: kb, repoMap: repoMap, domains: items(body["domains"]))
        }
        return TeamConfig(myTeams: items(object["myTeams"]), repoGuide: text(object["repoGuide"]), teams: teams)
    }

    /// `{domain}`·`{repo}` 가 다 있고, `{domain}` 이 마지막 경로 조각(문서 이름)에 없어야 한다 (`{domain}` 은 폴더 자리).
    public static func usableRepoMap(_ pattern: String) -> Bool {
        guard pattern.contains("{domain}"), pattern.contains("{repo}") else { return false }
        let parts = pattern.replacingOccurrences(of: "\\", with: "/").split(separator: "/").map(String.init).filter { $0 != "." }
        guard let last = parts.last else { return false }
        return !last.contains("{domain}")
    }

    /// 파일이 없거나 읽을 수 없으면 nil
    public static func load(from file: URL = file) -> TeamConfig? {
        parse(try? Data(contentsOf: file))
    }

    /// `myTeams` 순서대로, `teams` 에 있는 팀만
    public func mine() -> [(team: String, entry: TeamEntry)] {
        myTeams.compactMap { name -> (team: String, entry: TeamEntry)? in
            guard let entry = teams[name] else { return nil }
            return (name, entry)
        }
    }

    /// 내 팀 도메인의 합집합(순서 유지, 중복 제거). `map.domains` 에 없는 도메인은 뺀다. 비면 `map.domains` 전체(이름순).
    public func candidates(map: DomainMap) -> [String] {
        var list: [String] = []
        for m in mine() {
            for domain in m.entry.domains where map.domains[domain] != nil && !list.contains(domain) { list.append(domain) }
        }
        return list.isEmpty ? map.domains.keys.sorted() : list
    }

    /// 그 도메인을 가진 첫 팀 (`mine()` 순서)
    public func teamOf(_ domain: String) -> String? {
        mine().first { $0.entry.domains.contains(domain) }?.team
    }
}
