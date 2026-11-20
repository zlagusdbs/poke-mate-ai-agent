using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 포켓몬 도감
public static class PokedexChecks
{
    static string Species(int id, string name, int rate, int? from = null, int chain = 1, bool legendary = false)
    {
        var fromJson = from is int f ? $$$"""{"name":"x","url":"https://pokeapi.co/api/v2/pokemon-species/{{{f}}}/"}""" : "null";
        return $$$"""{"id":{{{id}}},"name":"en{{{id}}}","capture_rate":{{{rate}}},"is_legendary":{{{(legendary ? "true" : "false")}}},"is_mythical":false,"color":{"name":"green"},"names":[{"language":{"name":"en"},"name":"E"},{"language":{"name":"ko"},"name":"{{{name}}}"}],"flavor_text_entries":[{"language":{"name":"ko"},"flavor_text":"줄\n바꿈\u000c설명"}],"evolves_from_species":{{{fromJson}}},"evolution_chain":{"url":"https://pokeapi.co/api/v2/evolution-chain/{{{chain}}}/"}}""";
    }

    static string Node(int id, string kids = "") =>
        $$$"""{"species":{"name":"x","url":"https://pokeapi.co/api/v2/pokemon-species/{{{id}}}/"},"evolves_to":[{{{kids}}}]}""";

    static string Chain(string root) => "{\"chain\":" + root + "}";

    public static void Run()
    {
        var pikachu = PokedexLoader.ParseSpecies(Species(25, "피카츄", rate: 190, from: 172, chain: 10));
        Check.That(pikachu?.Form == new PokemonForm(25, "피카츄", "줄 바꿈 설명", "green") && pikachu.EvolvesFrom == 172,
                   "pokedex: 한국어 이름·설명(줄바꿈 정리)·이전 진화");
        var eevee = Chain(Node(133, string.Join(",", new[] { 134, 135, 136, 196 }.Select(i => Node(i)))));
        Check.That(PokedexLoader.Finals(eevee, 133, 151).SequenceEqual(new[] { 134, 135, 136 }), "pokedex: 갈래 진화는 범위 안 끝 단계 전부");
        var pichu = Chain(Node(172, Node(25, Node(26))));
        Check.That(PokedexLoader.Finals(pichu, 25, 151).SequenceEqual(new[] { 26 }), "pokedex: 범위 밖 아기 포켓몬 아래에서 기본형 찾기");
        var oddish = Chain(Node(43, Node(44, Node(45) + "," + Node(182))));
        Check.That(PokedexLoader.Finals(oddish, 43, 151).SequenceEqual(new[] { 45 }), "pokedex: 범위 밖 갈래는 뺌");
        Check.That(Same(PokedexLoader.Paths(oddish, 43, 151), new[] { new[] { 43, 44, 45 } }), "pokedex: 기본형→중간→최종 경로");
        Check.That(Same(PokedexLoader.Paths(eevee, 133, 151), new[] { new[] { 133, 134 }, new[] { 133, 135 }, new[] { 133, 136 } }), "pokedex: 갈래마다 경로");
        var infos = new[]
        {
            Species(1, "이상해씨", rate: 45, chain: 1), Species(2, "이상해풀", rate: 45, from: 1, chain: 1),
            Species(3, "이상해꽃", rate: 45, from: 2, chain: 1), Species(128, "켄타로스", rate: 45, chain: 60),
        }.Select(PokedexLoader.ParseSpecies).Where(i => i != null).Select(i => i!).ToList();
        var chains = new Dictionary<string, string>
        {
            ["https://pokeapi.co/api/v2/evolution-chain/1/"] = Chain(Node(1, Node(2, Node(3)))),
            ["https://pokeapi.co/api/v2/evolution-chain/60/"] = Chain(Node(128)),
        };
        var built = PokedexLoader.Build(infos, chains, 151);
        Check.That(built.Lines.SequenceEqual(new[]
                   {
                       new PokemonLine(1, new[] { new[] { 1, 2, 3 } }, 45, false),
                       new PokemonLine(128, new[] { new[] { 128 } }, 45, false),
                   }) && built.Forms.Count == 4, "pokedex: 기본형마다 한 줄, 진화 없는 포켓몬은 자기 자신");
        PokemonLine L(int rate, bool legendary = false) => new(1, new[] { new[] { 1 } }, rate, legendary);
        Check.That(new[] { L(255).Rarity, L(190).Rarity, L(45).Rarity, L(3, true).Rarity }
                       .SequenceEqual(new[] { MateRarity.Common, MateRarity.Uncommon, MateRarity.Rare, MateRarity.Legendary }),
                   "pokedex: 포획률로 희귀도");

        // 캐시 파일 (임시 폴더): 저장하고 읽기, 버전·범위가 다르면 버림
        var dir = Check.TempDir("pm-dex");
        var file = Path.Combine(dir, "pokemon", "catalog.json");
        PokedexLoader.Save(built, file);
        Check.That(PokedexLoader.LoadCached(PokedexRange.All, file) == built, "pokedex: 저장하고 읽음 (전체 범위는 최대 번호가 달라도 받음)");
        var wide = new PokedexCatalog(151, built.Forms, built.Lines);
        PokedexLoader.Save(wide, file);
        Check.That(PokedexLoader.LoadCached(PokedexRange.Gen1, file) == wide, "pokedex: 1세대 캐시 읽음");
        PokedexLoader.Save(built with { MaxDex = 1025 }, file);
        Check.That(PokedexLoader.LoadCached(PokedexRange.Gen1, file) == null, "pokedex: 1세대 캐시는 최대 번호 151 이 아니면 버림");
        File.WriteAllText(file, """{"version":1,"maxDex":151,"forms":[],"lines":[]}""");
        Check.That(PokedexLoader.LoadCached(PokedexRange.Gen1, file) == null, "pokedex: 옛 버전·빈 캐시는 버림");
        File.WriteAllText(file, "깨진");
        Check.That(PokedexLoader.LoadCached(PokedexRange.Gen1, file) == null, "pokedex: 깨진 캐시는 버림");
        Check.That(PokedexLoader.CatalogFile(PokedexRange.Gen1).StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(PokedexLoader.CatalogFile(PokedexRange.All)) == "catalog-all.json", "pokedex: 캐시 위치는 데이터 폴더 pokemon\\");
        Check.Remove(dir);
    }

    static bool Same(IReadOnlyList<IReadOnlyList<int>> a, int[][] b) =>
        a.Count == b.Length && a.Zip(b).All(p => p.First.SequenceEqual(p.Second));
}
