import Foundation

public struct PokemonForm: Codable, Equatable, Sendable {
    public let id: Int
    public let name: String
    public let flavor: String
    public let color: String

    public init(id: Int, name: String, flavor: String, color: String) {
        self.id = id
        self.name = name
        self.flavor = flavor
        self.color = color
    }
}

public struct PokemonLine: Codable, Equatable, Sendable {
    public let base: Int
    public let paths: [[Int]]
    public let captureRate: Int
    public let legendary: Bool

    public var finals: [Int] { paths.compactMap(\.last) }

    public init(base: Int, paths: [[Int]], captureRate: Int, legendary: Bool) {
        self.base = base
        self.paths = paths
        self.captureRate = captureRate
        self.legendary = legendary
    }

    public var rarity: MateRarity {
        if legendary { return .legendary }
        if captureRate <= 45 { return .rare }
        if captureRate <= 190 { return .uncommon }
        return .common
    }
}

public struct PokedexCatalog: Codable, Equatable, Sendable {
    public static let currentVersion = 2
    public var version = PokedexCatalog.currentVersion
    public var maxDex: Int
    public var forms: [PokemonForm]
    public var lines: [PokemonLine]

    public init(maxDex: Int, forms: [PokemonForm], lines: [PokemonLine]) {
        self.maxDex = maxDex
        self.forms = forms
        self.lines = lines
    }

    public var species: [MateSpecies] {
        let byId = Dictionary(forms.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return lines.compactMap { line in
            guard let base = byId[line.base] else { return nil }
            let paths = line.paths.map { $0.compactMap { byId[$0] } }.filter { $0.first == base }
            return MateSpecies(id: String(line.base), number: line.base, rarity: line.rarity, base: base,
                               paths: paths.isEmpty ? [[base]] : paths)
        }
        .sorted { $0.number < $1.number }
    }
}

public enum PokedexRange: String, Codable, CaseIterable, Sendable {
    case gen1, all

    public var label: String {
        switch self {
        case .gen1: return "1세대 (#1~151)"
        case .all: return "전체"
        }
    }

    public var fixedMaxDex: Int? { self == .gen1 ? 151 : nil }

    public var fileName: String { self == .gen1 ? "catalog.json" : "catalog-all.json" }
}

public struct PokedexProgress: Equatable, Sendable {
    public let step: String
    public let done: Int
    public let total: Int

    public init(step: String, done: Int, total: Int) {
        self.step = step
        self.done = done
        self.total = total
    }

    public var text: String { "\(step) \(done)/\(total)" }
}

public enum PokedexLoader {
    public static let maxDex = 151
    static let api = URL(string: "https://pokeapi.co/api/v2/")!

    public static var folder: URL { AppPaths.dataDir.appendingPathComponent("pokemon", isDirectory: true) }
    public static var catalogFile: URL { catalogFile(.gen1) }
    public static func catalogFile(_ range: PokedexRange) -> URL { folder.appendingPathComponent(range.fileName) }
    public static var spriteFolder: URL { folder.appendingPathComponent("sprites", isDirectory: true) }

    public static func spriteURL(_ id: Int) -> URL {
        URL(string: "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/\(id).png")!
    }

    public static func spriteFile(_ id: Int) -> URL { spriteFolder.appendingPathComponent("\(id).png") }

    public static func loadCached(_ range: PokedexRange = .gen1, from file: URL? = nil) -> PokedexCatalog? {
        guard let data = try? Data(contentsOf: file ?? catalogFile(range)),
              let catalog = try? JSONDecoder().decode(PokedexCatalog.self, from: data),
              catalog.version == PokedexCatalog.currentVersion, !catalog.lines.isEmpty,
              range.fixedMaxDex.map({ $0 == catalog.maxDex }) ?? true else { return nil }
        return catalog
    }

    public static func save(_ catalog: PokedexCatalog, to file: URL = catalogFile) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? encoder.encode(catalog).write(to: file, options: .atomic)
    }

    public struct SpeciesInfo: Equatable, Sendable {
        public let form: PokemonForm
        public let captureRate: Int
        public let legendary: Bool
        public let evolvesFrom: Int?
        public let chain: URL?
    }

    static func idFromURL(_ s: String?) -> Int? {
        guard let s else { return nil }
        return s.split(separator: "/").last.flatMap { Int($0) }
    }

    public static func parseSpecies(_ data: Data) -> SpeciesInfo? {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = obj["id"] as? Int else { return nil }
        func ko(_ key: String, _ field: String) -> String? {
            (obj[key] as? [[String: Any]])?.first { (($0["language"] as? [String: Any])?["name"] as? String) == "ko" }?[field] as? String
        }
        let english = (obj["name"] as? String) ?? "#\(id)"
        let name = ko("names", "name") ?? english
        let flavor = (ko("flavor_text_entries", "flavor_text") ?? "")
            .components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
        let color = ((obj["color"] as? [String: Any])?["name"] as? String) ?? "gray"
        let legendary = (obj["is_legendary"] as? Bool ?? false) || (obj["is_mythical"] as? Bool ?? false)
        let from = idFromURL((obj["evolves_from_species"] as? [String: Any])?["url"] as? String)
        let chain = ((obj["evolution_chain"] as? [String: Any])?["url"] as? String).flatMap(URL.init(string:))
        return SpeciesInfo(form: PokemonForm(id: id, name: name, flavor: flavor, color: color),
                           captureRate: obj["capture_rate"] as? Int ?? 255, legendary: legendary,
                           evolvesFrom: from, chain: chain)
    }

    public static func paths(chain data: Data, base: Int, maxDex: Int) -> [[Int]] {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let root = obj["chain"] as? [String: Any] else { return [[base]] }
        func id(_ node: [String: Any]) -> Int? { idFromURL((node["species"] as? [String: Any])?["url"] as? String) }
        func children(_ node: [String: Any]) -> [[String: Any]] {
            ((node["evolves_to"] as? [[String: Any]]) ?? []).filter { (id($0) ?? .max) <= maxDex }
        }
        func find(_ node: [String: Any]) -> [String: Any]? {
            if id(node) == base { return node }
            for child in (node["evolves_to"] as? [[String: Any]]) ?? [] {
                if let hit = find(child) { return hit }
            }
            return nil
        }
        func walk(_ node: [String: Any], _ prefix: [Int]) -> [[Int]] {
            guard let me = id(node) else { return [] }
            let kids = children(node)
            if kids.isEmpty { return [prefix + [me]] }
            return kids.flatMap { walk($0, prefix + [me]) }
        }
        guard let start = find(root) else { return [[base]] }
        let result = walk(start, [])
        return result.isEmpty ? [[base]] : result
    }

    public static func finals(chain data: Data, base: Int, maxDex: Int) -> [Int] {
        paths(chain: data, base: base, maxDex: maxDex).compactMap(\.last)
    }

    public static func build(species: [SpeciesInfo], chains: [URL: Data], maxDex: Int) -> PokedexCatalog {
        let inRange = species.filter { $0.form.id <= maxDex }.sorted { $0.form.id < $1.form.id }
        let lines: [PokemonLine] = inRange.compactMap { info in
            if let from = info.evolvesFrom, from <= maxDex { return nil }
            let paths = info.chain.flatMap { chains[$0] }.map { Self.paths(chain: $0, base: info.form.id, maxDex: maxDex) } ?? [[info.form.id]]
            return PokemonLine(base: info.form.id, paths: paths, captureRate: info.captureRate, legendary: info.legendary)
        }
        return PokedexCatalog(maxDex: maxDex, forms: inRange.map(\.form), lines: lines)
    }

    static func get(_ url: URL, session: URLSession) async throws -> Data {
        var lastError: Error = URLError(.unknown)
        for attempt in 0..<3 {
            do {
                let (data, response) = try await session.data(from: url)
                if let http = response as? HTTPURLResponse, http.statusCode != 200 { throw URLError(.badServerResponse) }
                return data
            } catch {
                lastError = error
                try? await Task.sleep(nanoseconds: UInt64(attempt + 1) * 500_000_000)
            }
        }
        throw lastError
    }

    static func fetchAll(_ urls: [URL], session: URLSession, width: Int = 8,
                         progress: (@Sendable (Int, Int) -> Void)? = nil) async throws -> [URL: Data] {
        var result: [URL: Data] = [:]
        var queue = urls[...]
        try await withThrowingTaskGroup(of: (URL, Data).self) { group in
            for _ in 0..<width {
                guard let url = queue.popFirst() else { break }
                group.addTask { (url, try await get(url, session: session)) }
            }
            while let (url, data) = try await group.next() {
                result[url] = data
                if result.count % 10 == 0 || result.count == urls.count { progress?(result.count, urls.count) }
                if let next = queue.popFirst() {
                    group.addTask { (next, try await get(next, session: session)) }
                }
            }
        }
        return result
    }

    public static func speciesCount(session: URLSession = .shared) async throws -> Int {
        let data = try await get(URL(string: "https://pokeapi.co/api/v2/pokemon-species?limit=1")!, session: session)
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let count = obj["count"] as? Int, count > 0 else { throw URLError(.cannotParseResponse) }
        return count
    }

    public static func fetch(_ range: PokedexRange, session: URLSession = .shared,
                             progress: (@Sendable (PokedexProgress) -> Void)? = nil) async throws -> PokedexCatalog {
        var maxDex = range.fixedMaxDex ?? 0
        if maxDex == 0 { maxDex = try await speciesCount(session: session) }
        return try await fetch(maxDex: maxDex, session: session, progress: progress)
    }

    public static func fetch(maxDex: Int = maxDex, session: URLSession = .shared,
                             progress: (@Sendable (PokedexProgress) -> Void)? = nil) async throws -> PokedexCatalog {
        let speciesURLs = (1...maxDex).map { api.appendingPathComponent("pokemon-species/\($0)/") }
        progress?(PokedexProgress(step: "도감", done: 0, total: maxDex))
        let speciesData = try await fetchAll(speciesURLs, session: session) { progress?(PokedexProgress(step: "도감", done: $0, total: $1)) }
        let infos = speciesData.values.compactMap(parseSpecies)
        guard infos.count == maxDex else { throw URLError(.cannotParseResponse) }
        let chainURLs = Array(Set(infos.filter { ($0.evolvesFrom ?? .max) > maxDex }.compactMap(\.chain)))
        let chains = try await fetchAll(chainURLs, session: session) { progress?(PokedexProgress(step: "진화 정보", done: $0, total: $1)) }
        return build(species: infos, chains: chains, maxDex: maxDex)
    }

    public static func sprite(_ id: Int, session: URLSession = .shared) async -> Data? {
        let file = spriteFile(id)
        if let data = try? Data(contentsOf: file) { return data }
        guard let data = try? await get(spriteURL(id), session: session) else { return nil }
        try? FileManager.default.createDirectory(at: spriteFolder, withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
        return data
    }
}
