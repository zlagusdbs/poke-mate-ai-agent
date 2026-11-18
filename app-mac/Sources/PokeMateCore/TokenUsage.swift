import Foundation

public struct TokenBucket: Codable, Equatable, Sendable {
    public var input = 0
    public var output = 0
    public var cacheWrite = 0
    public var cacheRead = 0

    public init(input: Int = 0, output: Int = 0, cacheWrite: Int = 0, cacheRead: Int = 0) {
        self.input = input
        self.output = output
        self.cacheWrite = cacheWrite
        self.cacheRead = cacheRead
    }

    public var total: Int { input + output + cacheWrite + cacheRead }

    public mutating func add(_ other: TokenBucket) {
        input += other.input
        output += other.output
        cacheWrite += other.cacheWrite
        cacheRead += other.cacheRead
    }
}

public enum ClaudePricing {
    struct Rate {
        let input: Double
        let output: Double
        let cacheWrite: Double
        let cacheRead: Double
        static func perMillion(_ i: Double, _ o: Double, _ w: Double, _ r: Double) -> Rate {
            Rate(input: i / 1_000_000, output: o / 1_000_000, cacheWrite: w / 1_000_000, cacheRead: r / 1_000_000)
        }
    }

    static let table: [String: Rate] = [
        "claude-opus-5": .perMillion(5, 25, 6.25, 0.5),
        "claude-opus-5-5": .perMillion(4, 20, 5, 0.2),
        "claude-sonnet-5": .perMillion(2, 10, 2.5, 0.2),
        "claude-opus-4-20250514": .perMillion(15, 75, 18.75, 1.5),
        "claude-sonnet-4-20250514": .perMillion(3, 15, 3.75, 0.3),
        "claude-sonnet-4-5-20250929": .perMillion(3, 15, 3.75, 0.3),
        "claude-opus-4-8": .perMillion(5, 25, 6.25, 0.5),
        "claude-opus-4-7": .perMillion(5, 25, 6.25, 0.5),
        "claude-sonnet-4-6": .perMillion(3, 15, 3.75, 0.3),
        "claude-haiku-4-5-20251001": .perMillion(1, 5, 1.25, 0.1),
        "claude-fable-5": .perMillion(10, 50, 12.5, 1.0),
        "claude-fable-5-1": .perMillion(10, 50, 12.5, 0.25),
        "claude-fable-5-2": .perMillion(8, 40, 10, 0.2),
        "claude-sonnet-5-5": .perMillion(2, 10, 2.5, 0.1),
    ]

    static let aliases: [String: String] = [
        "claude-sonnet-4": "claude-sonnet-4-20250514",
        "claude-opus-4": "claude-opus-4-20250514",
        "claude-sonnet-4-5": "claude-sonnet-4-5-20250929",
        "claude-haiku-4-5": "claude-haiku-4-5-20251001",
    ]

    public static func key(_ model: String) -> String {
        var key = model.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if key.hasPrefix("anthropic/") { key = String(key.dropFirst("anthropic/".count)) }
        return aliases[key] ?? key
    }

    public static func cost(model: String, bucket: TokenBucket) -> Double? {
        guard let r = table[key(model)] else { return nil }
        return Double(bucket.input) * r.input + Double(bucket.output) * r.output
            + Double(bucket.cacheWrite) * r.cacheWrite + Double(bucket.cacheRead) * r.cacheRead
    }
}

public struct DailyUsage: Equatable, Sendable, Identifiable {
    public let day: String
    public let total: Int
    public let cost: Double
    public var byModel: [String: Int]

    public var id: String { day }

    public init(day: String, total: Int, cost: Double, byModel: [String: Int] = [:]) {
        self.day = day
        self.total = total
        self.cost = cost
        self.byModel = byModel
    }
}

public struct UsageSummary: Equatable, Sendable {
    public var today = TokenBucket()
    public var week = TokenBucket()
    public var month = TokenBucket()
    public var allTime = 0
    public var todayCost = 0.0
    public var weekCost = 0.0
    public var monthCost = 0.0
    public var unpricedModels: [String] = []
    public var todayByModel: [(model: String, bucket: TokenBucket)] = []
    public var daily: [DailyUsage] = []

    public init() {}

    public static func == (a: UsageSummary, b: UsageSummary) -> Bool {
        a.today == b.today && a.week == b.week && a.month == b.month && a.todayCost == b.todayCost
            && a.daily == b.daily
    }
}

extension UsageSummary {
    public static var snapshotFile: URL { AppPaths.dataDir.appendingPathComponent("usage.json") }

    public func snapshot(updated: Date) -> Data {
        func bucket(_ b: TokenBucket) -> [String: Int] {
            ["input": b.input, "output": b.output, "cacheWrite": b.cacheWrite, "cacheRead": b.cacheRead, "total": b.total]
        }
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = .current
        let object: [String: Any] = [
            "updated": formatter.string(from: updated),
            "costNote": "모델 단가로 환산한 추정 비용(USD). 구독료·실제 청구액이 아님",
            "today": ["tokens": bucket(today), "costUSD": todayCost],
            "week": ["tokens": bucket(week), "costUSD": weekCost],
            "month": ["tokens": bucket(month), "costUSD": monthCost],
            "allTimeTokens": allTime,
            "todayByModel": todayByModel.map { ["model": $0.model, "tokens": bucket($0.bucket)] },
            "daily": daily.map { ["day": $0.day, "tokens": $0.total, "costUSD": $0.cost, "byModel": $0.byModel] },
            "unpricedModels": unpricedModels,
        ]
        return (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])) ?? Data()
    }
}

public final class ClaudeUsageReader: @unchecked Sendable {
    struct FileState: Codable {
        var size: UInt64
        var offset: UInt64
        var ids: [String]
        var buckets: [String: [String: TokenBucket]]
    }

    struct Cache: Codable {
        var version = 1
        var files: [String: FileState] = [:]
    }

    public let roots: [URL]
    private let cacheFile: URL
    private var cache: Cache
    private var owner: [String: String] = [:]
    private let lock = NSLock()

    public static func defaultRoots(home: String = NSHomeDirectory()) -> [URL] {
        [
            "\(home)/.claude/projects",
            "\(home)/.config/claude/projects",
            "\(home)/Library/Application Support/poke-mate/claude-config/projects",
        ].map { URL(fileURLWithPath: $0, isDirectory: true) }
    }

    public init(roots: [URL] = ClaudeUsageReader.defaultRoots(), cacheFile: URL) {
        self.roots = roots
        self.cacheFile = cacheFile
        if let data = try? Data(contentsOf: cacheFile), let cache = try? JSONDecoder().decode(Cache.self, from: data), cache.version == 1 {
            self.cache = cache
        } else {
            self.cache = Cache()
        }
        for (path, state) in cache.files {
            for id in state.ids where owner[id] == nil { owner[id] = path }
        }
    }

    private func forget(_ path: String) {
        guard let state = cache.files[path] else { return }
        for id in state.ids where owner[id] == path { owner[id] = nil }
    }

    public func refresh(now: Date = Date(), calendar: Calendar = .current) -> UsageSummary {
        lock.lock()
        defer { lock.unlock() }
        let files = Self.jsonlFiles(in: roots)
        let present = Set(files.map(\.path))
        for path in cache.files.keys where !present.contains(path) { forget(path) }
        cache.files = cache.files.filter { present.contains($0.key) }
        for file in files {
            update(file, calendar: calendar)
        }
        save()
        return summary(now: now, calendar: calendar)
    }

    static func jsonlFiles(in roots: [URL]) -> [URL] {
        var out: [URL] = []
        let fm = FileManager.default
        for root in roots where fm.fileExists(atPath: root.path) {
            guard let e = fm.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey]) else { continue }
            for case let url as URL in e where url.pathExtension == "jsonl" {
                out.append(url)
            }
        }
        return out
    }

    private func update(_ url: URL, calendar: Calendar) {
        let path = url.path
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let size = (attrs[.size] as? NSNumber)?.uint64Value else { return }
        var state = cache.files[path] ?? FileState(size: 0, offset: 0, ids: [], buckets: [:])
        if size < state.offset {
            forget(path)
            state = FileState(size: 0, offset: 0, ids: [], buckets: [:])
        }
        guard size > state.offset, let handle = try? FileHandle(forReadingFrom: url) else {
            state.size = size
            cache.files[path] = state
            return
        }
        defer { try? handle.close() }
        try? handle.seek(toOffset: state.offset)
        guard let data = try? handle.readToEnd(), let lastNewline = data.lastIndex(of: 0x0A) else {
            cache.files[path] = state
            return
        }
        let complete = data[data.startIndex...lastNewline]
        for line in complete.split(separator: 0x0A) {
            guard let entry = Self.parse(line: Data(line), calendar: calendar) else { continue }
            if let id = entry.id {
                if owner[id] != nil { continue }
                owner[id] = path
                state.ids.append(id)
            }
            state.buckets[entry.day, default: [:]][entry.model, default: TokenBucket()].add(entry.bucket)
        }
        state.offset += UInt64(complete.count)
        state.size = size
        cache.files[path] = state
    }

    struct Entry {
        let id: String?
        let day: String
        let model: String
        let bucket: TokenBucket
    }

    private static let usageMarker = Data("\"usage\"".utf8)
    private static let isoFull: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    private static let isoPlain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parse(line: Data, calendar: Calendar) -> Entry? {
        guard line.range(of: usageMarker) != nil,
              let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              obj["type"] as? String == "assistant",
              let message = obj["message"] as? [String: Any],
              let usage = message["usage"] as? [String: Any],
              let stamp = obj["timestamp"] as? String,
              let date = isoFull.date(from: stamp) ?? isoPlain.date(from: stamp) else { return nil }
        let model = message["model"] as? String ?? "unknown"
        if model == "<synthetic>" { return nil }
        let int: (String) -> Int = { (usage[$0] as? NSNumber)?.intValue ?? 0 }
        let bucket = TokenBucket(input: int("input_tokens"), output: int("output_tokens"),
                                 cacheWrite: int("cache_creation_input_tokens"), cacheRead: int("cache_read_input_tokens"))
        guard bucket.total > 0 else { return nil }
        var id: String?
        if let mid = message["id"] as? String {
            id = mid + ":" + ((obj["requestId"] as? String) ?? "")
        }
        return Entry(id: id, day: dayString(date, calendar: calendar), model: model, bucket: bucket)
    }

    static func dayString(_ date: Date, calendar: Calendar) -> String {
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    public static let dailyDays = 30

    func summary(now: Date, calendar: Calendar) -> UsageSummary {
        var byDay: [String: [String: TokenBucket]] = [:]
        for state in cache.files.values {
            for (day, models) in state.buckets {
                for (model, bucket) in models {
                    byDay[day, default: [:]][model, default: TokenBucket()].add(bucket)
                }
            }
        }
        let today = Self.dayString(now, calendar: calendar)
        let weekStart = calendar.dateInterval(of: .weekOfYear, for: now).map { Self.dayString($0.start, calendar: calendar) } ?? today
        let monthStart = calendar.dateInterval(of: .month, for: now).map { Self.dayString($0.start, calendar: calendar) } ?? today
        var s = UsageSummary()
        var unpriced = Set<String>()
        for (day, models) in byDay where day >= monthStart && day <= today {
            for (model, bucket) in models {
                let cost = ClaudePricing.cost(model: model, bucket: bucket)
                if cost == nil { unpriced.insert(model) }
                s.month.add(bucket)
                s.monthCost += cost ?? 0
                if day >= weekStart {
                    s.week.add(bucket)
                    s.weekCost += cost ?? 0
                }
                if day == today {
                    s.today.add(bucket)
                    s.todayCost += cost ?? 0
                }
            }
        }
        s.allTime = byDay.values.reduce(0) { sum, models in sum + models.values.reduce(0) { $0 + $1.total } }
        s.unpricedModels = unpriced.sorted()
        s.todayByModel = (byDay[today] ?? [:]).map { ($0.key, $0.value) }.sorted { $0.bucket.total > $1.bucket.total }
        let start = calendar.startOfDay(for: now)
        s.daily = (0..<Self.dailyDays).reversed().compactMap { back in
            guard let date = calendar.date(byAdding: .day, value: -back, to: start) else { return nil }
            let day = Self.dayString(date, calendar: calendar)
            let models = byDay[day] ?? [:]
            let cost = models.reduce(0.0) { $0 + (ClaudePricing.cost(model: $1.key, bucket: $1.value) ?? 0) }
            return DailyUsage(day: day, total: models.values.reduce(0) { $0 + $1.total }, cost: cost,
                              byModel: models.mapValues(\.total))
        }
        return s
    }

    private func save() {
        try? FileManager.default.createDirectory(at: cacheFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(cache) {
            try? data.write(to: cacheFile, options: .atomic)
        }
    }
}

public enum TokenFormat {
    public static func compact(_ n: Int) -> String {
        let d = Double(n)
        switch n {
        case 1_000_000_000...: return String(format: "%.2fB", d / 1_000_000_000)
        case 1_000_000...: return String(format: "%.1fM", d / 1_000_000)
        case 1_000...: return String(format: "%.1fK", d / 1_000)
        default: return "\(n)"
        }
    }

    public static func usd(_ v: Double) -> String { String(format: "$%.2f", v) }
}
