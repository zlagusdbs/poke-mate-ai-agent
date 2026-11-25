import Foundation

/// 경로 규칙 하나. `path` 는 glob, `domain` 은 그 경로가 속한 도메인.
public struct PathRule: Equatable, Sendable {
    public var path: String
    public var domain: String

    public init(path: String, domain: String) {
        self.path = path
        self.domain = domain
    }
}

/// 경로 규칙 판정 결과. `counts` 는 도메인 → 그 도메인 규칙에 처음 맞은 파일 수. `domain` 은 센 도메인이 정확히 하나일 때만 채운다.
public struct PathVerdict: Equatable, Sendable {
    public var domain: String?
    public var counts: [String: Int]

    public init(domain: String?, counts: [String: Int]) {
        self.domain = domain
        self.counts = counts
    }
}

/// `local/path-rules.json`. 여러 도메인이 한 저장소에 섞인 곳에서 바뀐 파일 경로로 도메인을 가른다.
/// 저장소 키는 대소문자 무시: 소문자로 바꿔 둔다.
public struct PathRules: Equatable, Sendable {
    public private(set) var repos: [String: [PathRule]]

    public init(repos: [String: [PathRule]] = [:]) {
        var lowered: [String: [PathRule]] = [:]
        for (key, list) in repos { lowered[key.lowercased()] = list }
        self.repos = lowered
    }

    public static var file: URL { AppPaths.localFile("path-rules.json") }

    /// 객체가 아니면 빈 규칙. `path`·`domain` 이 빈 항목은 뺀다. 저장소 키는 대소문자 무시.
    public static func parse(_ data: Data?) -> PathRules {
        var repos: [String: [PathRule]] = [:]
        guard let data, let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return PathRules(repos: repos) }
        for (name, value) in object {
            let key = name.trimmingCharacters(in: .whitespaces)
            guard !key.isEmpty, let items = value as? [Any] else { continue }
            var list: [PathRule] = []
            for item in items {
                guard let body = item as? [String: Any] else { continue }
                let path = (body["path"] as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
                let domain = (body["domain"] as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
                if !path.isEmpty && !domain.isEmpty { list.append(PathRule(path: path, domain: domain)) }
            }
            if !list.isEmpty { repos[key.lowercased()] = list }
        }
        return PathRules(repos: repos)
    }

    /// 파일이 없거나 읽을 수 없으면 빈 규칙
    public static func load(from file: URL = file) -> PathRules {
        parse(try? Data(contentsOf: file))
    }

    /// 그 저장소(`{owner}/{repo}`, 대소문자 무시)의 규칙. 없으면 빈 목록.
    public func rules(for fullName: String) -> [PathRule] {
        repos[fullName.lowercased()] ?? []
    }

    /// `\` → `/`, 앞의 `./`·`/` 제거
    static func clean(_ text: String) -> String {
        var s = text.replacingOccurrences(of: "\\", with: "/")
        while true {
            if s.hasPrefix("./") { s.removeFirst(2) }
            else if s.hasPrefix("/") { s.removeFirst() }
            else { return s }
        }
    }

    /// glob 을 정규식으로 바꿔 대소문자 무시로 비교. `**/` 는 `(.*/)?`, `**` 는 `.*`, `*` 는 `[^/]*`, `?` 는 `[^/]`.
    public static func matches(_ glob: String, _ path: String) -> Bool {
        let g = Array(clean(glob))
        var pattern = "^"
        var i = 0
        while i < g.count {
            let c = g[i]
            if c == "*" && i + 1 < g.count && g[i + 1] == "*" {
                if i + 2 < g.count && g[i + 2] == "/" {
                    pattern += "(.*/)?"
                    i += 2
                } else {
                    pattern += ".*"
                    i += 1
                }
            } else if c == "*" {
                pattern += "[^/]*"
            } else if c == "?" {
                pattern += "[^/]"
            } else {
                pattern += NSRegularExpression.escapedPattern(for: String(c))
            }
            i += 1
        }
        pattern += "$"
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else { return false }
        let target = clean(path)
        return regex.firstMatch(in: target, range: NSRange(target.startIndex..., in: target)) != nil
    }

    /// 후보가 아닌 도메인 규칙은 없는 것으로 본다. 파일마다 처음 맞는 규칙의 도메인을 센다. 센 도메인이 정확히 하나면 `domain` 에 넣는다.
    public func judge(fullName: String, files: [String], candidates: [String]) -> PathVerdict {
        var counts: [String: Int] = [:]
        let active = rules(for: fullName).filter { candidates.contains($0.domain) }
        for file in files {
            if let hit = active.first(where: { Self.matches($0.path, file) }) { counts[hit.domain, default: 0] += 1 }
        }
        return PathVerdict(domain: counts.count == 1 ? counts.keys.first : nil, counts: counts)
    }
}
