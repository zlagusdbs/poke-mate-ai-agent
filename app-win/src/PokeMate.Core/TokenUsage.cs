using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PokeMate.Core;

public sealed record TokenBucket
{
    // 토큰 수는 64비트다 (맥 Swift Int). 캐시 읽기만으로 하루에 수억, 전체 합계는 수십억을 넘는다.
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheWrite { get; set; }
    public long CacheRead { get; set; }

    public TokenBucket() { }

    public TokenBucket(long input = 0, long output = 0, long cacheWrite = 0, long cacheRead = 0)
    {
        Input = input;
        Output = output;
        CacheWrite = cacheWrite;
        CacheRead = cacheRead;
    }

    [JsonIgnore] public long Total => Input + Output + CacheWrite + CacheRead;

    public void Add(TokenBucket other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheWrite += other.CacheWrite;
        CacheRead += other.CacheRead;
    }
}

public static class ClaudePricing
{
    internal sealed record Rate(double Input, double Output, double CacheWrite, double CacheRead)
    {
        public static Rate PerMillion(double i, double o, double w, double r) => new(i / 1_000_000, o / 1_000_000, w / 1_000_000, r / 1_000_000);
    }

    internal static readonly Dictionary<string, Rate> Table = new()
    {
        ["claude-opus-5"] = Rate.PerMillion(5, 25, 6.25, 0.5),
        ["claude-opus-5-5"] = Rate.PerMillion(4, 20, 5, 0.2),
        ["claude-sonnet-5"] = Rate.PerMillion(2, 10, 2.5, 0.2),
        ["claude-opus-4-20250514"] = Rate.PerMillion(15, 75, 18.75, 1.5),
        ["claude-sonnet-4-20250514"] = Rate.PerMillion(3, 15, 3.75, 0.3),
        ["claude-sonnet-4-5-20250929"] = Rate.PerMillion(3, 15, 3.75, 0.3),
        ["claude-opus-4-8"] = Rate.PerMillion(5, 25, 6.25, 0.5),
        ["claude-opus-4-7"] = Rate.PerMillion(5, 25, 6.25, 0.5),
        ["claude-sonnet-4-6"] = Rate.PerMillion(3, 15, 3.75, 0.3),
        ["claude-haiku-4-5-20251001"] = Rate.PerMillion(1, 5, 1.25, 0.1),
        ["claude-fable-5"] = Rate.PerMillion(10, 50, 12.5, 1.0),
        ["claude-fable-5-1"] = Rate.PerMillion(10, 50, 12.5, 0.25),
        ["claude-fable-5-2"] = Rate.PerMillion(8, 40, 10, 0.2),
        ["claude-sonnet-5-5"] = Rate.PerMillion(2, 10, 2.5, 0.1),
    };

    internal static readonly Dictionary<string, string> Aliases = new()
    {
        ["claude-sonnet-4"] = "claude-sonnet-4-20250514",
        ["claude-opus-4"] = "claude-opus-4-20250514",
        ["claude-sonnet-4-5"] = "claude-sonnet-4-5-20250929",
        ["claude-haiku-4-5"] = "claude-haiku-4-5-20251001",
    };

    public static string Key(string model)
    {
        var key = model.Trim().ToLowerInvariant();
        if (key.StartsWith("anthropic/", StringComparison.Ordinal)) key = key["anthropic/".Length..];
        return Aliases.TryGetValue(key, out var alias) ? alias : key;
    }

    public static double? Cost(string model, TokenBucket bucket)
    {
        if (!Table.TryGetValue(Key(model), out var r)) return null;
        return bucket.Input * r.Input + bucket.Output * r.Output + bucket.CacheWrite * r.CacheWrite + bucket.CacheRead * r.CacheRead;
    }
}

public sealed record DailyUsage(string Day, long Total, double Cost, IReadOnlyDictionary<string, long> ByModel)
{
    public string Id => Day;

    public bool Equals(DailyUsage? o) =>
        o is not null && Day == o.Day && Total == o.Total && Cost == o.Cost
        && ByModel.Count == o.ByModel.Count && ByModel.All(kv => o.ByModel.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode() => HashCode.Combine(Day, Total);
}

public sealed class UsageSummary : IEquatable<UsageSummary>
{
    public TokenBucket Today { get; set; } = new();
    public TokenBucket Week { get; set; } = new();
    public TokenBucket Month { get; set; } = new();
    public long AllTime { get; set; }
    public double TodayCost { get; set; }
    public double WeekCost { get; set; }
    public double MonthCost { get; set; }
    public List<string> UnpricedModels { get; set; } = new();
    public List<(string Model, TokenBucket Bucket)> TodayByModel { get; set; } = new();
    public List<DailyUsage> Daily { get; set; } = new();

    public bool Equals(UsageSummary? b) =>
        b is not null && Today == b.Today && Week == b.Week && Month == b.Month && TodayCost == b.TodayCost && Daily.SequenceEqual(b.Daily);

    public override bool Equals(object? obj) => Equals(obj as UsageSummary);
    public override int GetHashCode() => HashCode.Combine(Today.Total, Month.Total);

    public static string SnapshotFile => Path.Combine(AppPaths.DataDir, "usage.json");

    /// MCP 서버가 읽는 usage.json. 키 정렬·들여쓰기.
    public string Snapshot(DateTimeOffset updated)
    {
        static JsonObject Bucket(TokenBucket b) => new()
        {
            ["input"] = b.Input, ["output"] = b.Output, ["cacheWrite"] = b.CacheWrite, ["cacheRead"] = b.CacheRead, ["total"] = b.Total,
        };
        var obj = new JsonObject
        {
            ["updated"] = Iso8601Local(updated),
            ["costNote"] = "모델 단가로 환산한 추정 비용(USD). 구독료·실제 청구액이 아님",
            ["today"] = new JsonObject { ["tokens"] = Bucket(Today), ["costUSD"] = TodayCost },
            ["week"] = new JsonObject { ["tokens"] = Bucket(Week), ["costUSD"] = WeekCost },
            ["month"] = new JsonObject { ["tokens"] = Bucket(Month), ["costUSD"] = MonthCost },
            ["allTimeTokens"] = AllTime,
            ["todayByModel"] = new JsonArray(TodayByModel.Select(m => (JsonNode?)new JsonObject { ["model"] = m.Model, ["tokens"] = Bucket(m.Bucket) }).ToArray()),
            ["daily"] = new JsonArray(Daily.Select(d => (JsonNode?)new JsonObject
            {
                ["day"] = d.Day, ["tokens"] = d.Total, ["costUSD"] = d.Cost,
                ["byModel"] = new JsonObject(d.ByModel.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            }).ToArray()),
            ["unpricedModels"] = new JsonArray(UnpricedModels.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
        };
        return Json.Sorted(obj)!.ToJsonString(Json.Options);
    }

    /// Swift `ISO8601DateFormatter` (timeZone = .current): 2026-11-17T14:00:00+09:00, UTC 면 Z
    internal static string Iso8601Local(DateTimeOffset date)
    {
        var local = TimeZoneInfo.ConvertTime(date, TimeZoneInfo.Local);
        return local.Offset == TimeSpan.Zero
            ? local.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}

/// `~/.claude/projects/**/*.jsonl` 의 사용량을 모은다. 파일마다 읽은 위치를 캐시해 이어 읽는다.
public sealed class ClaudeUsageReader
{
    internal sealed class FileState
    {
        public ulong Size { get; set; }
        public ulong Offset { get; set; }
        public List<string> Ids { get; set; } = new();
        public Dictionary<string, Dictionary<string, TokenBucket>> Buckets { get; set; } = new();
    }

    internal sealed class Cache
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, FileState> Files { get; set; } = new();
    }

    public IReadOnlyList<string> Roots { get; }
    readonly string cacheFile;
    Cache cache;
    readonly Dictionary<string, string> owner = new();
    readonly object gate = new();

    public static List<string> DefaultRoots(string? home = null)
    {
        var h = home ?? Paths.Home;
        return new List<string>
        {
            Path.Combine(h, ".claude", "projects"),
            Path.Combine(h, ".config", "claude", "projects"),
            Path.Combine(AppPaths.ClaudeConfigDir, "projects"),
        };
    }

    public ClaudeUsageReader(IReadOnlyList<string>? roots, string cacheFile)
    {
        Roots = roots ?? DefaultRoots();
        this.cacheFile = cacheFile;
        Cache? loaded = null;
        try { loaded = Json.Deserialize<Cache>(File.ReadAllText(cacheFile)); } catch { }
        cache = loaded is { Version: 1 } ? loaded : new Cache();
        foreach (var (path, state) in cache.Files)
            foreach (var id in state.Ids)
                owner.TryAdd(id, path);
    }

    void Forget(string path)
    {
        if (!cache.Files.TryGetValue(path, out var state)) return;
        foreach (var id in state.Ids)
            if (owner.TryGetValue(id, out var p) && p == path) owner.Remove(id);
    }

    public UsageSummary Refresh(DateTimeOffset? now = null, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var at = now ?? DateTimeOffset.Now;
        lock (gate)
        {
            var files = JsonlFiles(Roots);
            var present = new HashSet<string>(files);
            foreach (var path in cache.Files.Keys.Where(p => !present.Contains(p)).ToList())
            {
                Forget(path);
                cache.Files.Remove(path);
            }
            foreach (var file in files) Update(file, tz);
            Save();
            return Summary(at, tz);
        }
    }

    internal static List<string> JsonlFiles(IEnumerable<string> roots)
    {
        var output = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                output.AddRange(Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(p => string.Equals(Path.GetExtension(p), ".jsonl", StringComparison.Ordinal)));
            }
            catch { }
        }
        return output;
    }

    void Update(string path, TimeZoneInfo tz)
    {
        ulong size;
        try { size = (ulong)new FileInfo(path).Length; } catch { return; }
        if (!cache.Files.TryGetValue(path, out var state)) state = new FileState();
        if (size < state.Offset)
        {
            Forget(path);
            state = new FileState();
        }
        if (size <= state.Offset)
        {
            state.Size = size;
            cache.Files[path] = state;
            return;
        }
        byte[] data;
        try
        {
            // Claude Code 가 쓰는 중인 파일도 읽도록 공유 모드로 연다
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek((long)state.Offset, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
        }
        catch
        {
            state.Size = size;
            cache.Files[path] = state;
            return;
        }
        var lastNewline = Array.LastIndexOf(data, (byte)0x0A);
        if (lastNewline < 0)
        {
            cache.Files[path] = state;
            return;
        }
        var start = 0;
        for (var i = 0; i <= lastNewline; i++)
        {
            if (data[i] != 0x0A) continue;
            if (i > start && Parse(new ReadOnlySpan<byte>(data, start, i - start), tz) is Entry entry)
            {
                if (entry.Id != null)
                {
                    if (owner.ContainsKey(entry.Id)) { start = i + 1; continue; }
                    owner[entry.Id] = path;
                    state.Ids.Add(entry.Id);
                }
                if (!state.Buckets.TryGetValue(entry.Day, out var models)) state.Buckets[entry.Day] = models = new Dictionary<string, TokenBucket>();
                if (!models.TryGetValue(entry.Model, out var bucket)) models[entry.Model] = bucket = new TokenBucket();
                bucket.Add(entry.Bucket);
            }
            start = i + 1;
        }
        state.Offset += (ulong)(lastNewline + 1);
        state.Size = size;
        cache.Files[path] = state;
    }

    internal sealed record Entry(string? Id, string Day, string Model, TokenBucket Bucket);

    static readonly byte[] UsageMarker = Encoding.UTF8.GetBytes("\"usage\"");

    internal static Entry? Parse(ReadOnlySpan<byte> line, TimeZoneInfo tz)
    {
        if (line.IndexOf(UsageMarker) < 0) return null;
        JsonObject? obj;
        try { obj = System.Text.Json.Nodes.JsonNode.Parse(line.ToArray()) as JsonObject; } catch { return null; }
        if (obj == null || Json.Str(obj["type"]) != "assistant" || obj["message"] is not JsonObject message
            || message["usage"] is not JsonObject usage || Json.Str(obj["timestamp"]) is not string stamp
            || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return null;
        var model = Json.Str(message["model"]) ?? "unknown";
        if (model == "<synthetic>") return null;
        long Int(string key) => Json.Long(usage[key]) ?? 0;
        var bucket = new TokenBucket(Int("input_tokens"), Int("output_tokens"), Int("cache_creation_input_tokens"), Int("cache_read_input_tokens"));
        if (bucket.Total <= 0) return null;
        string? id = null;
        if (Json.Str(message["id"]) is string mid) id = mid + ":" + (Json.Str(obj["requestId"]) ?? "");
        return new Entry(id, DayString(date, tz), model, bucket);
    }

    internal static string DayString(DateTimeOffset date, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(date, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public const int DailyDays = 30;

    internal UsageSummary Summary(DateTimeOffset now, TimeZoneInfo tz)
    {
        var byDay = new Dictionary<string, Dictionary<string, TokenBucket>>();
        foreach (var state in cache.Files.Values)
            foreach (var (day, models) in state.Buckets)
                foreach (var (model, bucket) in models)
                {
                    if (!byDay.TryGetValue(day, out var m)) byDay[day] = m = new Dictionary<string, TokenBucket>();
                    if (!m.TryGetValue(model, out var b)) m[model] = b = new TokenBucket();
                    b.Add(bucket);
                }
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var todayDate = local.Date;
        var today = todayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // 그레고리력 기본: 주는 일요일부터
        var weekStart = todayDate.AddDays(-(int)todayDate.DayOfWeek).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var monthStart = new DateTime(todayDate.Year, todayDate.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var s = new UsageSummary();
        var unpriced = new HashSet<string>();
        foreach (var (day, models) in byDay)
        {
            if (string.CompareOrdinal(day, monthStart) < 0 || string.CompareOrdinal(day, today) > 0) continue;
            foreach (var (model, bucket) in models)
            {
                var cost = ClaudePricing.Cost(model, bucket);
                if (cost == null) unpriced.Add(model);
                s.Month.Add(bucket);
                s.MonthCost += cost ?? 0;
                if (string.CompareOrdinal(day, weekStart) >= 0)
                {
                    s.Week.Add(bucket);
                    s.WeekCost += cost ?? 0;
                }
                if (day == today)
                {
                    s.Today.Add(bucket);
                    s.TodayCost += cost ?? 0;
                }
            }
        }
        s.AllTime = byDay.Values.Sum(models => models.Values.Sum(b => b.Total));
        s.UnpricedModels = unpriced.OrderBy(m => m, StringComparer.Ordinal).ToList();
        s.TodayByModel = (byDay.TryGetValue(today, out var todayModels) ? todayModels : new Dictionary<string, TokenBucket>())
            .Select(kv => (kv.Key, kv.Value with { })).OrderByDescending(x => x.Item2.Total).ToList();
        s.Daily = Enumerable.Range(0, DailyDays).Reverse().Select(back =>
        {
            var day = todayDate.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var models = byDay.TryGetValue(day, out var m) ? m : new Dictionary<string, TokenBucket>();
            var cost = models.Sum(kv => ClaudePricing.Cost(kv.Key, kv.Value) ?? 0);
            return new DailyUsage(day, models.Values.Sum(b => b.Total), cost, models.ToDictionary(kv => kv.Key, kv => kv.Value.Total));
        }).ToList();
        return s;
    }

    /// 마지막 저장이 성공했는지 (실패하면 다음 새로 고침에서 다시 시도한다)
    public bool LastSaveOk { get; private set; } = true;

    bool Save()
    {
        try { AppPaths.WriteAtomic(cacheFile, Json.Serialize(cache, indented: false)); LastSaveOk = true; }
        catch { LastSaveOk = false; }
        return LastSaveOk;
    }
}

public static class TokenFormat
{
    public static string Compact(long n)
    {
        double d = n;
        var inv = CultureInfo.InvariantCulture;
        if (n >= 1_000_000_000) return (d / 1_000_000_000).ToString("0.00", inv) + "B";
        if (n >= 1_000_000) return (d / 1_000_000).ToString("0.0", inv) + "M";
        if (n >= 1_000) return (d / 1_000).ToString("0.0", inv) + "K";
        return n.ToString(inv);
    }

    public static string Usd(double v) => "$" + v.ToString("0.00", CultureInfo.InvariantCulture);
}

/// `claude -p` 한 번 실행의 사용량 (result 줄). 맥에서는 WorkSessions.swift 에 있다.
public sealed record RunUsage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public double CostUSD { get; set; }
    public long? ContextWindow { get; set; }

    public RunUsage() { }

    public RunUsage(long input = 0, long output = 0, long cacheRead = 0, long cacheWrite = 0, double costUSD = 0, long? contextWindow = null)
    {
        Input = input;
        Output = output;
        CacheRead = cacheRead;
        CacheWrite = cacheWrite;
        CostUSD = costUSD;
        ContextWindow = contextWindow;
    }

    [JsonIgnore] public long Total => Input + Output + CacheRead + CacheWrite;
    [JsonIgnore] public long Context => Input + CacheRead + CacheWrite;

    public void Add(RunUsage other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheWrite += other.CacheWrite;
        CostUSD += other.CostUSD;
        ContextWindow = other.ContextWindow ?? ContextWindow;
    }

    public static RunUsage? Parse(string stdout)
    {
        var result = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse()
            .Select(line => Json.ParseObject(line))
            .FirstOrDefault(o => o != null && Json.Str(o["type"]) == "result");
        if (result == null) return null;
        var usage = result["usage"] as JsonObject ?? new JsonObject();
        long Int(string key) => Json.Long(usage[key]) ?? 0;
        var windows = (result["modelUsage"] as JsonObject ?? new JsonObject())
            .Select(kv => Json.Long((kv.Value as JsonObject)?["contextWindow"])).Where(w => w != null).Select(w => w!.Value).ToList();
        return new RunUsage(Int("input_tokens"), Int("output_tokens"), Int("cache_read_input_tokens"), Int("cache_creation_input_tokens"),
                            Json.Double(result["total_cost_usd"]) ?? 0, windows.Count == 0 ? null : windows.Max());
    }

    [JsonIgnore] public string ShortText => $"{TokenFormat.Compact(Total)} · {TokenFormat.Usd(CostUSD)}";
}
