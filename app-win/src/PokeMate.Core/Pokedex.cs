using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 포켓몬 한 마리(한 모습). JSON 키는 맥 카탈로그 파일과 같다.
public sealed record PokemonForm(int Id, string Name, string Flavor, string Color);

/// 기본형 하나에서 갈라지는 진화 경로들. Paths 는 기본형부터 최종형까지의 번호 목록 여러 개.
public sealed record PokemonLine(int Base, IReadOnlyList<IReadOnlyList<int>> Paths, int CaptureRate, bool Legendary)
{
    [JsonIgnore] public IReadOnlyList<int> Finals => Paths.Where(p => p.Count > 0).Select(p => p[^1]).ToList();

    [JsonIgnore]
    public MateRarity Rarity
    {
        get
        {
            if (Legendary) return MateRarity.Legendary;
            if (CaptureRate <= 45) return MateRarity.Rare;
            if (CaptureRate <= 190) return MateRarity.Uncommon;
            return MateRarity.Common;
        }
    }

    public bool Equals(PokemonLine? o) =>
        o is not null && Base == o.Base && CaptureRate == o.CaptureRate && Legendary == o.Legendary
        && Paths.Count == o.Paths.Count && Paths.Zip(o.Paths).All(p => p.First.SequenceEqual(p.Second));

    public override int GetHashCode() => HashCode.Combine(Base, CaptureRate, Legendary, Paths.Count);
}

/// 도감 카탈로그 파일 (`pokemon\catalog.json`, 전체 범위는 `catalog-all.json`).
public sealed record PokedexCatalog
{
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;
    public int MaxDex { get; init; }
    public IReadOnlyList<PokemonForm> Forms { get; init; } = new List<PokemonForm>();
    public IReadOnlyList<PokemonLine> Lines { get; init; } = new List<PokemonLine>();

    [JsonConstructor]
    public PokedexCatalog() { }

    public PokedexCatalog(int maxDex, IEnumerable<PokemonForm> forms, IEnumerable<PokemonLine> lines)
    {
        MaxDex = maxDex;
        Forms = forms.ToList();
        Lines = lines.ToList();
    }

    public bool Equals(PokedexCatalog? o) =>
        o is not null && Version == o.Version && MaxDex == o.MaxDex && Forms.SequenceEqual(o.Forms) && Lines.SequenceEqual(o.Lines);

    public override int GetHashCode() => HashCode.Combine(Version, MaxDex, Forms.Count, Lines.Count);

    /// 기본형마다 한 줄로 만든 키울 수 있는 포켓몬 목록 (번호 순)
    [JsonIgnore]
    public List<MateSpecies> Species
    {
        get
        {
            var byId = new Dictionary<int, PokemonForm>();
            foreach (var form in Forms) byId.TryAdd(form.Id, form);
            var result = new List<MateSpecies>();
            foreach (var line in Lines)
            {
                if (!byId.TryGetValue(line.Base, out var baseForm)) continue;
                var paths = line.Paths
                    .Select(p => (IReadOnlyList<PokemonForm>)p.Where(byId.ContainsKey).Select(i => byId[i]).ToList())
                    .Where(p => p.Count > 0 && p[0] == baseForm).ToList();
                result.Add(new MateSpecies(line.Base.ToString(), line.Base, line.Rarity, baseForm,
                                           paths.Count == 0 ? new List<IReadOnlyList<PokemonForm>> { new List<PokemonForm> { baseForm } } : paths));
            }
            return result.OrderBy(s => s.Number).ToList();
        }
    }
}

/// 설정의 도감 범위. JSON 값은 Swift rawValue 그대로 ("gen1", "all").
public enum PokedexRange { Gen1, All }

public static class PokedexRangeExtensions
{
    public static IReadOnlyList<PokedexRange> AllCases { get; } = new[] { PokedexRange.Gen1, PokedexRange.All };

    public static string Label(this PokedexRange range) => range switch
    {
        PokedexRange.Gen1 => "1세대 (#1~151)",
        _ => "전체",
    };

    public static int? FixedMaxDex(this PokedexRange range) => range == PokedexRange.Gen1 ? 151 : null;

    public static string FileName(this PokedexRange range) => range == PokedexRange.Gen1 ? "catalog.json" : "catalog-all.json";
}

public sealed record PokedexProgress(string Step, int Done, int Total)
{
    public string Text => $"{Step} {Done}/{Total}";
}

/// PokéAPI(https://pokeapi.co)에서 도감을 받아 `pokemon\` 폴더에 캐시한다.
public static class PokedexLoader
{
    public const int MaxDex = 151;
    const string Api = "https://pokeapi.co/api/v2/";

    public static string Folder => Path.Combine(AppPaths.DataDir, "pokemon");
    public static string CatalogFile(PokedexRange range = PokedexRange.Gen1) => Path.Combine(Folder, range.FileName());
    public static string SpriteFolder => Path.Combine(Folder, "sprites");

    public static string SpriteUrl(int id) => $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{id}.png";
    public static string SpriteFile(int id) => Path.Combine(SpriteFolder, $"{id}.png");

    /// 캐시를 읽는다. 없거나 깨졌거나 옛 버전이거나 비었거나 1세대인데 최대 번호가 다르면 null.
    public static PokedexCatalog? LoadCached(PokedexRange range = PokedexRange.Gen1, string? from = null)
    {
        try
        {
            var catalog = Json.Deserialize<PokedexCatalog>(PokeMate.Core.Paths.ReadShared(from ?? CatalogFile(range)));
            if (catalog == null || catalog.Version != PokedexCatalog.CurrentVersion || catalog.Lines.Count == 0) return null;
            if (range.FixedMaxDex() is int fixedMax && fixedMax != catalog.MaxDex) return null;
            return catalog;
        }
        catch { return null; }
    }

    /// 키 정렬·들여쓰기. 실패하면 false.
    public static bool Save(PokedexCatalog catalog, string? file = null)
    {
        try { AppPaths.WriteAtomic(file ?? CatalogFile(), Json.SortedPretty(catalog)); return true; }
        catch { return false; }
    }

    public sealed record SpeciesInfo(PokemonForm Form, int CaptureRate, bool Legendary, int? EvolvesFrom, string? Chain);

    static int? IdFromUrl(string? s)
    {
        if (s == null) return null;
        var last = s.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last != null && int.TryParse(last, out var id) ? id : null;
    }

    /// `pokemon-species/<id>` 응답. 한국어 이름·설명(공백 정리)·색·포획률·전설 여부·이전 진화·진화 사슬 주소.
    public static SpeciesInfo? ParseSpecies(string json)
    {
        var obj = Json.ParseObject(json);
        if (obj == null || Json.Int(obj["id"]) is not int id) return null;
        string? Ko(string key, string field) =>
            (obj[key] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(e => Json.Str((e["language"] as JsonObject)?["name"]) == "ko") is { } entry ? Json.Str(entry[field]) : null;
        var english = Json.Str(obj["name"]) ?? $"#{id}";
        var name = Ko("names", "name") ?? english;
        var flavor = string.Join(" ", (Ko("flavor_text_entries", "flavor_text") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var color = Json.Str((obj["color"] as JsonObject)?["name"]) ?? "gray";
        var legendary = (Json.Bool(obj["is_legendary"]) ?? false) || (Json.Bool(obj["is_mythical"]) ?? false);
        var from = IdFromUrl(Json.Str((obj["evolves_from_species"] as JsonObject)?["url"]));
        var chain = Json.Str((obj["evolution_chain"] as JsonObject)?["url"]);
        return new SpeciesInfo(new PokemonForm(id, name, flavor, color), Json.Int(obj["capture_rate"]) ?? 255, legendary, from, chain);
    }

    /// 진화 사슬에서 기본형(base)부터 최종형까지의 경로들. 범위(maxDex) 밖 포켓몬은 뺀다.
    public static List<List<int>> Paths(string chainJson, int @base, int maxDex)
    {
        var fallback = new List<List<int>> { new() { @base } };
        if ((Json.ParseObject(chainJson)?["chain"]) is not JsonObject root) return fallback;
        static int? IdOf(JsonObject node) => IdFromUrl(Json.Str((node["species"] as JsonObject)?["url"]));
        static IEnumerable<JsonObject> Kids(JsonObject node) => (node["evolves_to"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
        JsonObject? Find(JsonObject node)
        {
            if (IdOf(node) == @base) return node;
            foreach (var child in Kids(node))
                if (Find(child) is { } hit) return hit;
            return null;
        }
        List<List<int>> Walk(JsonObject node, List<int> prefix)
        {
            if (IdOf(node) is not int me) return new List<List<int>>();
            var kids = Kids(node).Where(k => (IdOf(k) ?? int.MaxValue) <= maxDex).ToList();
            var path = prefix.Append(me).ToList();
            if (kids.Count == 0) return new List<List<int>> { path };
            return kids.SelectMany(k => Walk(k, path)).ToList();
        }
        if (Find(root) is not { } start) return fallback;
        var result = Walk(start, new List<int>());
        return result.Count == 0 ? fallback : result;
    }

    public static List<int> Finals(string chainJson, int @base, int maxDex) => Paths(chainJson, @base, maxDex).Select(p => p[^1]).ToList();

    /// 받아 온 포켓몬 정보와 진화 사슬(주소 → 응답)로 카탈로그를 만든다. 진화해서 나오는 포켓몬은 줄로 세지 않는다.
    public static PokedexCatalog Build(IEnumerable<SpeciesInfo> species, IReadOnlyDictionary<string, string> chains, int maxDex)
    {
        var inRange = species.Where(s => s.Form.Id <= maxDex).OrderBy(s => s.Form.Id).ToList();
        var lines = new List<PokemonLine>();
        foreach (var info in inRange)
        {
            if (info.EvolvesFrom is int from && from <= maxDex) continue;
            var paths = info.Chain != null && chains.TryGetValue(info.Chain, out var chain)
                ? Paths(chain, info.Form.Id, maxDex) : new List<List<int>> { new() { info.Form.Id } };
            lines.Add(new PokemonLine(info.Form.Id, paths.Select(p => (IReadOnlyList<int>)p).ToList(), info.CaptureRate, info.Legendary));
        }
        return new PokedexCatalog(maxDex, inRange.Select(s => s.Form), lines);
    }

    static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    static async Task<string> GetText(string url, HttpClient client)
    {
        Exception last = new HttpRequestException("알 수 없는 오류");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if ((int)response.StatusCode != 200) throw new HttpRequestException($"서버 응답 {(int)response.StatusCode}");
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay((attempt + 1) * 500);
            }
        }
        throw last;
    }

    static async Task<byte[]> GetBytes(string url, HttpClient client)
    {
        Exception last = new HttpRequestException("알 수 없는 오류");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if ((int)response.StatusCode != 200) throw new HttpRequestException($"서버 응답 {(int)response.StatusCode}");
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay((attempt + 1) * 500);
            }
        }
        throw last;
    }

    /// 여러 주소를 동시에 width 개씩 받는다. 하나라도 끝내 실패하면 예외.
    static async Task<Dictionary<string, string>> FetchAll(IReadOnlyList<string> urls, HttpClient client, int width = 8, Action<int, int>? progress = null)
    {
        var result = new ConcurrentDictionary<string, string>();
        using var gate = new SemaphoreSlim(width);
        var done = 0;
        await Task.WhenAll(urls.Select(async url =>
        {
            await gate.WaitAsync();
            try
            {
                result[url] = await GetText(url, client);
                var count = Interlocked.Increment(ref done);
                if (count % 10 == 0 || count == urls.Count) progress?.Invoke(count, urls.Count);
            }
            finally { gate.Release(); }
        }));
        return new Dictionary<string, string>(result);
    }

    /// 전체 범위의 최대 번호 (`pokemon-species` 개수)
    public static async Task<int> SpeciesCount(HttpClient? session = null)
    {
        var text = await GetText(Api + "pokemon-species?limit=1", session ?? SharedClient);
        if (Json.Int(Json.ParseObject(text)?["count"]) is int count && count > 0) return count;
        throw new HttpRequestException("도감 개수를 읽지 못했어요");
    }

    public static async Task<PokedexCatalog> Fetch(PokedexRange range, HttpClient? session = null, Action<PokedexProgress>? progress = null)
    {
        var maxDex = range.FixedMaxDex() ?? 0;
        if (maxDex == 0) maxDex = await SpeciesCount(session);
        return await Fetch(maxDex, session, progress);
    }

    public static async Task<PokedexCatalog> Fetch(int maxDex = MaxDex, HttpClient? session = null, Action<PokedexProgress>? progress = null)
    {
        var client = session ?? SharedClient;
        var speciesUrls = Enumerable.Range(1, maxDex).Select(i => $"{Api}pokemon-species/{i}/").ToList();
        progress?.Invoke(new PokedexProgress("도감", 0, maxDex));
        var speciesData = await FetchAll(speciesUrls, client, progress: (d, t) => progress?.Invoke(new PokedexProgress("도감", d, t)));
        var infos = speciesData.Values.Select(ParseSpecies).OfType<SpeciesInfo>().ToList();
        if (infos.Count != maxDex) throw new HttpRequestException("도감 응답을 읽지 못했어요");
        var chainUrls = infos.Where(i => (i.EvolvesFrom ?? int.MaxValue) > maxDex).Select(i => i.Chain).OfType<string>().Distinct().ToList();
        var chains = await FetchAll(chainUrls, client, progress: (d, t) => progress?.Invoke(new PokedexProgress("진화 정보", d, t)));
        return Build(infos, chains, maxDex);
    }

    /// 스프라이트 PNG. 캐시 폴더에 있으면 그것을, 없으면 받아서 저장한다. 받지 못하면 null.
    public static async Task<byte[]?> Sprite(int id, HttpClient? session = null)
    {
        var file = SpriteFile(id);
        try { if (File.Exists(file)) return await File.ReadAllBytesAsync(file); } catch { }
        try
        {
            var data = await GetBytes(SpriteUrl(id), session ?? SharedClient);
            try
            {
                Directory.CreateDirectory(SpriteFolder);
                await File.WriteAllBytesAsync(file, data);
            }
            catch { }
            return data;
        }
        catch { return null; }
    }
}
