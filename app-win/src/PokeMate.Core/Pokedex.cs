namespace PokeMate.Core;

// Task 2 는 AppSettings 가 쓰는 PokedexRange 만 옮겼다. 나머지(MateSpecies, PokedexLoader …)는 Task 3 에서 채운다.

/// JSON 값은 Swift rawValue 그대로 ("gen1", "all").
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
