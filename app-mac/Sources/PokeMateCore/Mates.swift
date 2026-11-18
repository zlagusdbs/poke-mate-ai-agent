import Foundation

public enum MateRarity: Int, CaseIterable, Sendable {
    case legendary, rare, uncommon, common

    public var label: String {
        switch self {
        case .legendary: return "전설"
        case .rare: return "희귀"
        case .uncommon: return "고급"
        case .common: return "일반"
        }
    }

    public var weight: Double {
        switch self {
        case .legendary: return 2
        case .rare: return 8
        case .uncommon: return 20
        case .common: return 70
        }
    }
}

public struct MateSpecies: Identifiable, Equatable, Sendable {
    public let id: String
    public let number: Int
    public let rarity: MateRarity
    public let base: PokemonForm
    public let paths: [[PokemonForm]]

    public init(id: String, number: Int, rarity: MateRarity, base: PokemonForm, paths: [[PokemonForm]]) {
        self.id = id
        self.number = number
        self.rarity = rarity
        self.base = base
        self.paths = paths.isEmpty ? [[base]] : paths
    }

    public var name: String { base.name }
    public var blurb: String { base.flavor }
    public var mainColor: UInt32 { Self.colors[base.color] ?? 0x999999 }
    public var finals: [PokemonForm] { paths.compactMap(\.last) }

    public func path(_ final: Int?) -> [PokemonForm] {
        if let exact = paths.first(where: { $0.last?.id == final }) { return exact }
        for path in paths {
            if let i = path.firstIndex(where: { $0.id == final }) { return Array(path[...i]) }
        }
        return paths[0]
    }

    public func final(_ dex: Int?) -> PokemonForm { path(dex).last ?? base }

    public func step(tokens: Int, final: Int?) -> Int? { MateRules.step(for: tokens, count: path(final).count) }

    public func form(tokens: Int, final: Int?) -> PokemonForm? {
        step(tokens: tokens, final: final).map { path(final)[$0] }
    }

    public func displayForm(tokens: Int, final: Int?) -> PokemonForm { form(tokens: tokens, final: final) ?? base }

    public func names() -> [String] { Array(Set(paths.flatMap { $0.map(\.name) })) }

    static let colors: [String: UInt32] = [
        "black": 0x4A4A55, "blue": 0x5B8DEF, "brown": 0xA86B3C, "gray": 0x9A9AA5, "green": 0x5DBB63,
        "pink": 0xF29BC0, "purple": 0xA77BD6, "red": 0xE35D5B, "white": 0xD9D6CF, "yellow": 0xF2C94C,
    ]

    public static let eggGrid = [
        "..............",
        "..............",
        "......kk......",
        ".....kffk.....",
        "....kffffk....",
        "...kffsfffk...",
        "..kfffffssfk..",
        "..kffssffffk..",
        "..kfffffsffk..",
        "..kffffffffk..",
        "...kffsfffk...",
        "....kkkkkk....",
        "..............",
        "..............",
    ]

    public func eggPalette() -> [Character: UInt32] {
        ["k": 0x3A3A4A, "f": 0xF3EADB, "s": mainColor]
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var installed: [MateSpecies] = []
    nonisolated(unsafe) private static var lookup: [String: MateSpecies] = [:]

    public static var all: [MateSpecies] {
        lock.lock()
        defer { lock.unlock() }
        return installed
    }

    public static var known: [MateSpecies] {
        lock.lock()
        defer { lock.unlock() }
        return lookup.values.sorted { $0.number < $1.number }
    }

    public static func install(_ catalog: PokedexCatalog, lookup extra: [PokedexCatalog] = []) {
        let pool = catalog.species
        var table: [String: MateSpecies] = [:]
        for other in (extra + [catalog]).sorted(by: { $0.maxDex < $1.maxDex }) {
            for species in other.species { table[species.id] = species }
        }
        lock.lock()
        installed = pool
        lookup = table
        lock.unlock()
    }

    public static func find(_ id: String) -> MateSpecies? {
        lock.lock()
        defer { lock.unlock() }
        return lookup[id]
    }

    public static func inPool(_ id: String) -> MateSpecies? { all.first { $0.id == id } }
}

public enum MateRules {
    public static let hatchAt = 30_000_000
    public static let evolveAt = [300_000_000, 600_000_000]
    public static let completeAt = 1_000_000_000

    public static func thresholds(count: Int) -> [Int] {
        [hatchAt] + evolveAt.prefix(max(0, count - 1)) + [completeAt]
    }

    public static func step(for tokens: Int, count: Int) -> Int? {
        guard tokens >= hatchAt else { return nil }
        return evolveAt.prefix(max(0, count - 1)).filter { tokens >= $0 }.count
    }

    public static func nextGoal(for tokens: Int, count: Int) -> Int {
        thresholds(count: count).first { $0 > tokens } ?? completeAt
    }

    public static func stageProgress(for tokens: Int, count: Int) -> Double {
        let start = thresholds(count: count).last { $0 <= tokens } ?? 0
        let goal = nextGoal(for: tokens, count: count)
        guard goal > start else { return 1 }
        return min(1, max(0, Double(tokens - start) / Double(goal - start)))
    }

    public static func label(step: Int?) -> String {
        guard let step else { return "알" }
        return step == 0 ? "기본" : "\(step)진화"
    }

    public static func goalLabel(for tokens: Int, count: Int) -> String {
        let goal = nextGoal(for: tokens, count: count)
        return goal == hatchAt ? "부화" : goal == completeAt ? "도감 등록" : "진화"
    }
}

public struct CurrentMate: Codable, Equatable, Sendable {
    public var species: String
    public var tokens: Int
    public var startedAt: Date
    public var form: Int?

    public init(species: String, tokens: Int, startedAt: Date, form: Int? = nil) {
        self.species = species
        self.tokens = tokens
        self.startedAt = startedAt
        self.form = form
    }
}

public struct CollectedMate: Codable, Equatable, Sendable {
    public var species: String
    public var completedAt: Date
    public var days: Int
    public var form: Int?

    public init(species: String, completedAt: Date, days: Int, form: Int? = nil) {
        self.species = species
        self.completedAt = completedAt
        self.days = days
        self.form = form
    }
}

public enum MateEvent: Equatable, Sendable {
    case started(String)
    case hatched(String)
    case evolved(String, Int)
    case completed(String)
}

public struct MateState: Codable, Equatable, Sendable {
    public var current: CurrentMate?
    public var party: [CurrentMate] = []
    public var collected: [CollectedMate] = []
    public var lastAllTime: Int?

    public init(current: CurrentMate? = nil, party: [CurrentMate] = [], collected: [CollectedMate] = [], lastAllTime: Int? = nil) {
        self.current = current
        self.party = party
        self.collected = collected
        self.lastAllTime = lastAllTime
    }

    enum CodingKeys: String, CodingKey { case current, party, collected, lastAllTime }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        current = try c.decodeIfPresent(CurrentMate.self, forKey: .current)
        party = try c.decodeIfPresent([CurrentMate].self, forKey: .party) ?? []
        collected = try c.decodeIfPresent([CollectedMate].self, forKey: .collected) ?? []
        lastAllTime = try c.decodeIfPresent(Int.self, forKey: .lastAllTime)
    }

    public func raising(_ species: String) -> CurrentMate? {
        current?.species == species ? current : party.first { $0.species == species }
    }

    public mutating func makeRepresentative(_ species: String) -> Bool {
        guard current?.species != species, let i = party.firstIndex(where: { $0.species == species }) else { return false }
        let chosen = party.remove(at: i)
        if let current { party.insert(current, at: i) }
        current = chosen
        return true
    }

    public func count(of species: String) -> Int { collected.filter { $0.species == species }.count }

    public var tokens: Int { current?.tokens ?? 0 }

    public func lastForm(of species: String) -> Int? { collected.last { $0.species == species }?.form }

    public static func newMate(_ id: String, tokens: Int = 0, now: Date) -> CurrentMate {
        CurrentMate(species: id, tokens: tokens, startedAt: now,
                    form: (MateSpecies.inPool(id) ?? MateSpecies.find(id))?.finals.randomElement()?.id)
    }

    public mutating func adoptUnknown() -> Bool {
        guard !MateSpecies.all.isEmpty, let mate = current, Int(mate.species) == nil, MateSpecies.find(mate.species) == nil else { return false }
        current = Self.newMate(nextSpecies(), tokens: mate.tokens, now: mate.startedAt)
        return true
    }

    public func nextSpecies() -> String {
        var rng = SystemRandomNumberGenerator()
        return nextSpecies(using: &rng)
    }

    public func nextSpecies<G: RandomNumberGenerator>(using rng: inout G) -> String {
        let pool = MateSpecies.all
        guard !pool.isEmpty else { return "" }
        if collected.isEmpty && current == nil && party.isEmpty { return pool[0].id }
        let sizes = Dictionary(grouping: pool, by: \.rarity).mapValues(\.count)
        let weights = pool.map { $0.rarity.weight / Double(sizes[$0.rarity] ?? 1) }
        var pick = Double.random(in: 0..<weights.reduce(0, +), using: &rng)
        for (species, w) in zip(pool, weights) {
            if pick < w { return species.id }
            pick -= w
        }
        return pool[0].id
    }

    public mutating func advance(allTime: Int, now: Date, calendar: Calendar = .current) -> [MateEvent] {
        var events: [MateEvent] = []
        guard !MateSpecies.all.isEmpty else { return events }
        let delta = lastAllTime.map { max(0, allTime - $0) } ?? 0
        lastAllTime = allTime
        if current == nil {
            let id = nextSpecies()
            current = Self.newMate(id, now: now)
            events.append(.started(id))
        }
        guard var mate = current, delta > 0 else { return events }
        var remaining = delta
        while remaining > 0 {
            let room = MateRules.completeAt - mate.tokens
            let step = min(room, remaining)
            let before = mate.tokens
            mate.tokens += step
            remaining -= step
            let path = MateSpecies.find(mate.species)?.path(mate.form) ?? []
            let was = MateRules.step(for: before, count: path.count)
            let reached = MateRules.step(for: mate.tokens, count: path.count)
            if was == nil && reached != nil { events.append(.hatched(mate.species)) }
            if let reached, reached > (was ?? 0) {
                for i in ((was ?? 0) + 1)...reached where i < path.count { events.append(.evolved(mate.species, path[i].id)) }
            }
            if mate.tokens >= MateRules.completeAt {
                let days = max(1, (calendar.dateComponents([.day], from: calendar.startOfDay(for: mate.startedAt),
                                                           to: calendar.startOfDay(for: now)).day ?? 0) + 1)
                collected.append(CollectedMate(species: mate.species, completedAt: now, days: days, form: mate.form))
                events.append(.completed(mate.species))
                current = mate
                let id = nextSpecies()
                mate = Self.newMate(id, now: now)
                events.append(.started(id))
            }
        }
        current = mate
        return events
    }
}

public enum MateStore {
    public static func load(from file: URL) -> MateState {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: file), let s = try? decoder.decode(MateState.self, from: data) else { return MateState() }
        return s
    }

    public static func save(_ state: MateState, to file: URL) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? encoder.encode(state).write(to: file, options: .atomic)
    }
}
