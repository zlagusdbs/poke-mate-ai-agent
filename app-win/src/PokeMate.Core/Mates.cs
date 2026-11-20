using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 무작위 값을 주입하는 자리 (맥의 `RandomNumberGenerator`). 검사는 고정된 값을 주고, 앱은 `RandomSource.System` 을 쓴다.
public interface IRandomSource
{
    ulong Next();
}

public sealed class SystemRandomSource : IRandomSource
{
    public ulong Next() => (ulong)Random.Shared.NextInt64() << 1 ^ (ulong)Random.Shared.NextInt64();
}

public static class RandomSource
{
    public static IRandomSource System { get; } = new SystemRandomSource();

    /// 0 이상 upper 미만의 실수
    public static double NextDouble(this IRandomSource random, double upper) => (random.Next() >> 11) * (1.0 / (1UL << 53)) * upper;

    /// 0 이상 count 미만의 정수
    public static int NextIndex(this IRandomSource random, int count) => (int)(random.Next() % (ulong)count);
}

public enum MateRarity { Legendary, Rare, Uncommon, Common }

public static class MateRarityExtensions
{
    public static IReadOnlyList<MateRarity> AllCases { get; } = new[] { MateRarity.Legendary, MateRarity.Rare, MateRarity.Uncommon, MateRarity.Common };

    public static string Label(this MateRarity rarity) => rarity switch
    {
        MateRarity.Legendary => "전설",
        MateRarity.Rare => "희귀",
        MateRarity.Uncommon => "고급",
        _ => "일반",
    };

    public static double Weight(this MateRarity rarity) => rarity switch
    {
        MateRarity.Legendary => 2,
        MateRarity.Rare => 8,
        MateRarity.Uncommon => 20,
        _ => 70,
    };
}

/// 키울 수 있는 포켓몬 한 계열 (기본형과 진화 경로들).
public sealed record MateSpecies
{
    public string Id { get; }
    public int Number { get; }
    public MateRarity Rarity { get; }
    public PokemonForm Base { get; }
    public IReadOnlyList<IReadOnlyList<PokemonForm>> Paths { get; }

    public MateSpecies(string id, int number, MateRarity rarity, PokemonForm @base, IReadOnlyList<IReadOnlyList<PokemonForm>> paths)
    {
        Id = id;
        Number = number;
        Rarity = rarity;
        Base = @base;
        Paths = paths.Count == 0 ? new List<IReadOnlyList<PokemonForm>> { new List<PokemonForm> { @base } } : paths;
    }

    public string Name => Base.Name;
    public string Blurb => Base.Flavor;
    public uint MainColor => Colors.TryGetValue(Base.Color, out var c) ? c : 0x999999;
    public IReadOnlyList<PokemonForm> Finals => Paths.Where(p => p.Count > 0).Select(p => p[^1]).ToList();

    public bool Equals(MateSpecies? o) =>
        o is not null && Id == o.Id && Number == o.Number && Rarity == o.Rarity && Base == o.Base
        && Paths.Count == o.Paths.Count && Paths.Zip(o.Paths).All(p => p.First.SequenceEqual(p.Second));

    public override int GetHashCode() => HashCode.Combine(Id, Number);

    /// 정해 둔 최종 모습(final 은 최종형 번호)까지의 경로. 없으면 그 번호를 지나는 경로를 거기까지, 그것도 없으면 첫 경로.
    public IReadOnlyList<PokemonForm> Path(int? final)
    {
        if (Paths.FirstOrDefault(p => p.Count > 0 && p[^1].Id == final) is { } exact) return exact;
        foreach (var path in Paths)
        {
            var i = path.ToList().FindIndex(f => f.Id == final);
            if (i >= 0) return path.Take(i + 1).ToList();
        }
        return Paths[0];
    }

    public PokemonForm Final(int? dex) => Path(dex).LastOrDefault() ?? Base;

    /// 지금 토큰으로 선 진화 단계 (알이면 null). 0 이 기본형.
    public int? Step(long tokens, int? final) => MateRules.Step(tokens, Path(final).Count);

    public PokemonForm? Form(long tokens, int? final) => Step(tokens, final) is int step ? Path(final)[step] : null;

    public PokemonForm DisplayForm(long tokens, int? final) => Form(tokens, final) ?? Base;

    /// 대표 모습으로 고른(shown) 지나온 단계가 있으면 그것을, 아니면 지금 모습. 알이면 null.
    public PokemonForm? ShownForm(long tokens, int? final, int? shown)
    {
        if (Step(tokens, final) is not int step) return null;
        var line = Path(final);
        if (shown is int s)
        {
            var i = line.ToList().FindIndex(f => f.Id == s);
            if (i >= 0 && i <= step) return line[i];
        }
        return line[step];
    }

    public List<string> Names() => Paths.SelectMany(p => p.Select(f => f.Name)).Distinct().ToList();

    static readonly Dictionary<string, uint> Colors = new()
    {
        ["black"] = 0x4A4A55, ["blue"] = 0x5B8DEF, ["brown"] = 0xA86B3C, ["gray"] = 0x9A9AA5, ["green"] = 0x5DBB63,
        ["pink"] = 0xF29BC0, ["purple"] = 0xA77BD6, ["red"] = 0xE35D5B, ["white"] = 0xD9D6CF, ["yellow"] = 0xF2C94C,
    };

    /// 알 그림 (14x14, k=테두리 f=바탕 s=무늬)
    public static readonly IReadOnlyList<string> EggGrid = new[]
    {
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
    };

    public Dictionary<char, uint> EggPalette() => new() { ['k'] = 0x3A3A4A, ['f'] = 0xF3EADB, ['s'] = MainColor };

    static readonly object Gate = new();
    static List<MateSpecies> installed = new();
    static Dictionary<string, MateSpecies> lookup = new();

    /// 고를 수 있는 범위의 포켓몬 (설정의 도감 범위)
    public static IReadOnlyList<MateSpecies> All
    {
        get { lock (Gate) return installed.ToList(); }
    }

    /// 범위와 상관없이 알고 있는 모든 포켓몬 (번호 순)
    public static IReadOnlyList<MateSpecies> Known
    {
        get { lock (Gate) return lookup.Values.OrderBy(s => s.Number).ToList(); }
    }

    /// 카탈로그를 설치한다. lookup 은 이미 키우던 포켓몬을 찾을 수 있게 하는 다른 범위의 카탈로그들 (최대 번호가 큰 것이 우선).
    public static void Install(PokedexCatalog catalog, IEnumerable<PokedexCatalog>? lookup = null)
    {
        var pool = catalog.Species;
        var table = new Dictionary<string, MateSpecies>();
        foreach (var other in (lookup ?? Enumerable.Empty<PokedexCatalog>()).Append(catalog).OrderBy(c => c.MaxDex))
            foreach (var species in other.Species) table[species.Id] = species;
        lock (Gate)
        {
            installed = pool;
            MateSpecies.lookup = table;
        }
    }

    public static MateSpecies? Find(string id)
    {
        lock (Gate) return lookup.GetValueOrDefault(id);
    }

    public static MateSpecies? InPool(string id) => All.FirstOrDefault(s => s.Id == id);
}

/// 성장 규칙: 3천만 토큰에 부화, 3억·6억에 진화, 10억에 도감 등록.
public static class MateRules
{
    public const long HatchAt = 30_000_000;
    public static readonly IReadOnlyList<long> EvolveAt = new long[] { 300_000_000, 600_000_000 };
    public const long CompleteAt = 1_000_000_000;

    public static List<long> Thresholds(int count) =>
        new List<long> { HatchAt }.Concat(EvolveAt.Take(Math.Max(0, count - 1))).Append(CompleteAt).ToList();

    public static int? Step(long tokens, int count)
    {
        if (tokens < HatchAt) return null;
        return EvolveAt.Take(Math.Max(0, count - 1)).Count(t => tokens >= t);
    }

    public static long NextGoal(long tokens, int count) => Thresholds(count).Where(t => t > tokens).Select(t => (long?)t).FirstOrDefault() ?? CompleteAt;

    public static double StageProgress(long tokens, int count)
    {
        var start = Thresholds(count).Where(t => t <= tokens).Select(t => (long?)t).LastOrDefault() ?? 0;
        var goal = NextGoal(tokens, count);
        if (goal <= start) return 1;
        return Math.Min(1, Math.Max(0, (double)(tokens - start) / (goal - start)));
    }

    public static string Label(int? step) => step is not int s ? "알" : s == 0 ? "기본" : $"{s}진화";

    public static string GoalLabel(long tokens, int count)
    {
        var goal = NextGoal(tokens, count);
        return goal == HatchAt ? "부화" : goal == CompleteAt ? "도감 등록" : "진화";
    }
}

/// 지금 키우는 포켓몬 (대표와 파티). tokens 는 이 포켓몬이 먹은 토큰.
public sealed record CurrentMate
{
    public string Species { get; set; } = "";
    public long Tokens { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    /// 정해 둔 최종 모습(번호)
    public int? Form { get; set; }
    /// 대표 모습으로 고른 지나온 단계(번호). 없으면 지금 모습.
    public int? Shown { get; set; }

    [JsonConstructor]
    public CurrentMate() { }

    public CurrentMate(string species, long tokens, DateTimeOffset startedAt, int? form = null, int? shown = null)
    {
        Species = species;
        Tokens = tokens;
        StartedAt = startedAt;
        Form = form;
        Shown = shown;
    }
}

/// 도감에 등록한(10억을 채운) 포켓몬
public sealed record CollectedMate
{
    public string Species { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; }
    public int Days { get; set; }
    public int? Form { get; set; }

    [JsonConstructor]
    public CollectedMate() { }

    public CollectedMate(string species, DateTimeOffset completedAt, int days, int? form = null)
    {
        Species = species;
        CompletedAt = completedAt;
        Days = days;
        Form = form;
    }
}

public sealed record DexEntry(PokemonForm Form, MateSpecies Species)
{
    public int Id => Form.Id;
}

public static class MateDex
{
    /// 도감 칸: 포켓몬 한 마리마다 하나 (번호 순). 범위(maxDex) 밖 번호는 뺀다.
    public static List<DexEntry> Entries(IEnumerable<MateSpecies> lines, int? maxDex)
    {
        var taken = new HashSet<int>();
        var output = new List<DexEntry>();
        foreach (var line in lines)
            foreach (var form in line.Paths.SelectMany(p => p))
                if ((maxDex is not int m || form.Id <= m) && taken.Add(form.Id)) output.Add(new DexEntry(form, line));
        return output.OrderBy(e => e.Form.Id).ToList();
    }
}

public abstract record MateEvent
{
    public sealed record Started(string Species) : MateEvent;
    public sealed record Hatched(string Species) : MateEvent;
    /// 진화했다 (Form 은 진화한 모습의 번호)
    public sealed record Evolved(string Species, int Form) : MateEvent;
    public sealed record Completed(string Species) : MateEvent;
}

/// 대표 포켓몬·파티·도감 등록 기록. 토큰 합계(allTime)가 늘어난 만큼 대표가 성장한다.
/// record 는 참조 형식이고 메서드가 자기 자신을 바꾼다 (Swift 의 mutating).
public sealed record MateState
{
    public CurrentMate? Current { get; set; }
    List<CurrentMate> party = new();
    List<CollectedMate> collected = new();
    public List<CurrentMate> Party { get => party; set => party = value ?? new(); }
    public List<CollectedMate> Collected { get => collected; set => collected = value ?? new(); }
    public long? LastAllTime { get; set; }

    public MateState() { }

    public MateState(CurrentMate? current = null, IEnumerable<CurrentMate>? party = null, IEnumerable<CollectedMate>? collected = null, long? lastAllTime = null)
    {
        Current = current;
        this.party = party?.ToList() ?? new();
        this.collected = collected?.ToList() ?? new();
        LastAllTime = lastAllTime;
    }

    public bool Equals(MateState? o) =>
        o is not null && Equals(Current, o.Current) && Party.SequenceEqual(o.Party) && Collected.SequenceEqual(o.Collected) && LastAllTime == o.LastAllTime;

    public override int GetHashCode() => HashCode.Combine(Current?.Species, Party.Count, Collected.Count, LastAllTime);

    /// 키우는 중인 포켓몬(대표 또는 파티)
    public CurrentMate? Raising(string species) => Current?.Species == species ? Current : Party.FirstOrDefault(m => m.Species == species);

    /// 파티의 포켓몬을 대표로. 이전 대표는 토큰 그대로 파티로 쉰다. 이미 대표거나 키우지 않으면 false.
    public bool MakeRepresentative(string species)
    {
        var i = Party.FindIndex(m => m.Species == species);
        if (Current?.Species == species || i < 0) return false;
        var chosen = Party[i];
        Party.RemoveAt(i);
        if (Current != null) Party.Insert(i, Current);
        Current = chosen;
        return true;
    }

    /// 지나온 단계(form 은 번호)를 대표 모습으로 (성장은 그대로). 아직 안 된 단계는 false.
    public bool Show(string species, int form, Func<string, MateSpecies?>? find = null)
    {
        find ??= MateSpecies.Find;
        if (Raising(species) is not { } mate || find(species) is not { } line || line.Step(mate.Tokens, mate.Form) is not int step) return false;
        var i = line.Path(mate.Form).ToList().FindIndex(f => f.Id == form);
        if (i < 0 || i > step) return false;
        if (Current?.Species != species) MakeRepresentative(species);
        Current!.Shown = i == step ? null : form;
        return true;
    }

    public int Count(string species) => Collected.Count(c => c.Species == species);

    /// 만난(지나온) 포켓몬 번호: 키우는 친구는 지금 단계까지, 등록한 친구는 지나온 단계 모두
    public HashSet<int> SeenDex(Func<string, MateSpecies?>? find = null)
    {
        find ??= MateSpecies.Find;
        var seen = new HashSet<int>();
        foreach (var mate in new[] { Current }.OfType<CurrentMate>().Concat(Party))
        {
            if (find(mate.Species) is not { } line || line.Step(mate.Tokens, mate.Form) is not int step) continue;
            seen.UnionWith(line.Path(mate.Form).Take(step + 1).Select(f => f.Id));
        }
        foreach (var done in Collected)
        {
            if (find(done.Species) is not { } line) continue;
            seen.UnionWith(line.Path(done.Form).Select(f => f.Id));
        }
        return seen;
    }

    /// 그 최종형(번호)으로 등록한 횟수
    public int CompletedCount(int form, Func<string, MateSpecies?>? find = null)
    {
        find ??= MateSpecies.Find;
        return Collected.Count(done => find(done.Species) is { } line && line.Final(done.Form).Id == form);
    }

    [JsonIgnore] public long Tokens => Current?.Tokens ?? 0;

    public int? LastForm(string species) => Collected.LastOrDefault(c => c.Species == species)?.Form;

    /// 새 알. 진화 갈래는 이때 정해 둔다 (random 이 없으면 시스템 난수).
    public static CurrentMate NewMate(string id, DateTimeOffset now, long tokens = 0, IRandomSource? random = null)
    {
        random ??= RandomSource.System;
        var finals = (MateSpecies.InPool(id) ?? MateSpecies.Find(id))?.Finals;
        int? form = finals is { Count: > 0 } ? finals[random.NextIndex(finals.Count)].Id : null;
        return new CurrentMate(id, tokens, now, form);
    }

    /// 옛 캐릭터(포켓몬이 아닌 이름)를 토큰 그대로 포켓몬으로 바꾼다. 번호로 된 것이나 도감에 있는 것은 그대로 두고 false.
    public bool AdoptUnknown(IRandomSource? random = null)
    {
        if (MateSpecies.All.Count == 0 || Current is not { } mate || int.TryParse(mate.Species, out _) || MateSpecies.Find(mate.Species) != null) return false;
        Current = NewMate(NextSpecies(random), mate.StartedAt, mate.Tokens, random);
        return true;
    }

    /// 다음 알. 처음이면 1번 줄, 그 뒤로는 희귀도 가중 무작위 (일반 70·고급 20·희귀 8·전설 2, 같은 희귀도 안에서는 균등).
    public string NextSpecies(IRandomSource? random = null)
    {
        random ??= RandomSource.System;
        var pool = MateSpecies.All;
        if (pool.Count == 0) return "";
        if (Collected.Count == 0 && Current == null && Party.Count == 0) return pool[0].Id;
        var sizes = pool.GroupBy(s => s.Rarity).ToDictionary(g => g.Key, g => g.Count());
        var weights = pool.Select(s => s.Rarity.Weight() / sizes.GetValueOrDefault(s.Rarity, 1)).ToList();
        var pick = random.NextDouble(weights.Sum());
        for (var i = 0; i < pool.Count; i++)
        {
            if (pick < weights[i]) return pool[i].Id;
            pick -= weights[i];
        }
        return pool[0].Id;
    }

    /// 토큰 합계(allTime)가 늘어난 만큼 대표를 키운다. 부화·진화·도감 등록·다음 알 순서대로 사건을 돌려준다 (한 번에 넘어도 단계를 건너뛰지 않음).
    /// 도감을 아직 못 받았으면(MateSpecies.All 이 비었으면) 아무것도 안 하고 토큰 기록도 넘기지 않는다.
    public List<MateEvent> Advance(long allTime, DateTimeOffset now, TimeZoneInfo? timeZone = null, IRandomSource? random = null)
    {
        var events = new List<MateEvent>();
        if (MateSpecies.All.Count == 0) return events;
        var delta = LastAllTime is long last ? Math.Max(0, allTime - last) : 0;
        LastAllTime = allTime;
        if (Current == null)
        {
            var id = NextSpecies(random);
            Current = NewMate(id, now, 0, random);
            events.Add(new MateEvent.Started(id));
        }
        if (Current is not { } mate || delta <= 0) return events;
        var remaining = delta;
        while (remaining > 0)
        {
            var room = MateRules.CompleteAt - mate.Tokens;
            var step = Math.Min(room, remaining);
            var before = mate.Tokens;
            mate.Tokens += step;
            remaining -= step;
            var path = MateSpecies.Find(mate.Species)?.Path(mate.Form) ?? new List<PokemonForm>();
            var was = MateRules.Step(before, path.Count);
            var reached = MateRules.Step(mate.Tokens, path.Count);
            if (was == null && reached != null) events.Add(new MateEvent.Hatched(mate.Species));
            if (reached is int r && r > (was ?? 0))
                for (var i = (was ?? 0) + 1; i <= r; i++)
                    if (i < path.Count) events.Add(new MateEvent.Evolved(mate.Species, path[i].Id));
            if (mate.Tokens >= MateRules.CompleteAt)
            {
                var zone = timeZone ?? TimeZoneInfo.Local;
                var days = Math.Max(1, (TimeZoneInfo.ConvertTime(now, zone).Date - TimeZoneInfo.ConvertTime(mate.StartedAt, zone).Date).Days + 1);
                Collected.Add(new CollectedMate(mate.Species, now, days, mate.Form));
                events.Add(new MateEvent.Completed(mate.Species));
                var id = NextSpecies(random);
                mate = NewMate(id, now, 0, random);
                Current = mate;
                events.Add(new MateEvent.Started(id));
            }
        }
        Current = mate;
        return events;
    }
}

/// `mates.json`: 대표·파티·등록 기록. 날짜는 ISO 8601, 키 정렬·들여쓰기 (맥과 같은 형식).
public static class MateStore
{
    /// 읽을 수 없거나 깨졌으면 빈 상태
    public static MateState Load(string file)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<MateState>(Paths.ReadShared(file), Json.Iso8601) ?? new MateState(); }
        catch { return new MateState(); }
    }

    /// 실패하면 false (이전 파일은 그대로)
    public static bool Save(MateState state, string file)
    {
        try { AppPaths.WriteAtomic(file, Json.SortedPretty(state, Json.Iso8601)); return true; }
        catch { return false; }
    }
}
