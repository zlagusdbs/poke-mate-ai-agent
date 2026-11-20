using PokeMate.Core;

namespace PokeMate.CoreChecks;

/// 맥 검사의 `SplitMix` (검사에서 쓰는 고정 난수원)
sealed class SplitMix : IRandomSource
{
    ulong state;
    public SplitMix(ulong seed) { state = seed; }

    public ulong Next()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

// MARK: 캐릭터 성장
public static class MateGrowthChecks
{
    static PokemonForm Form(int id, string name) => new(id, name, "", "green");
    static PokemonLine Line(int b, int[][] paths) => new(b, paths, 45, false);

    public static void Run()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var empty = new MateState();
        Check.That(empty.Advance(5_000_000_000, t0).Count == 0 && empty.Current == null && empty.LastAllTime == null,
                   "mate: 도감을 아직 못 받았으면 기다림 (토큰 기록도 안 넘김)");
        var forms = new List<PokemonForm>();
        var lines = new List<PokemonLine>();
        var rates = new[] { 255, 255, 120, 120, 45, 45 };
        for (var i = 0; i < rates.Length; i++)
        {
            var b = i * 10 + 1;
            forms.AddRange(new[] { Form(b, $"기본{b}"), Form(b + 1, $"중간{b}"), Form(b + 2, $"최종{b}"), Form(b + 3, $"갈래{b}") });
            lines.Add(new PokemonLine(b, new[] { new[] { b, b + 1, b + 2 }, new[] { b, b + 3 } }, rates[i], false));
        }
        forms.Add(Form(99, "전설"));
        lines.Add(new PokemonLine(99, new[] { new[] { 99 } }, 3, true));
        MateSpecies.Install(new PokedexCatalog(151, forms, lines));
        var first = MateSpecies.All[0].Id;
        var m = new MateState();
        Check.That(m.Advance(5_000_000_000, t0).SequenceEqual(new MateEvent[] { new MateEvent.Started(first) }) && m.Tokens == 0, "mate: 처음엔 지금까지 쓴 토큰과 상관없이 1번 알");
        Check.That(new[] { 3, 4 }.Contains(m.Current?.Form ?? 0), "mate: 시작할 때 진화 갈래를 정해 둠");
        m.Current!.Form = 3;
        Check.That(m.Advance(5_000_000_000 + 29_999_999, t0).Count == 0 && MateSpecies.All[0].Form(m.Tokens, 3) == null, "mate: 3천만 전은 알");
        Check.That(m.Advance(5_030_000_000, t0).SequenceEqual(new MateEvent[] { new MateEvent.Hatched(first) }), "mate: 3천만에 부화");
        Check.That(MateSpecies.All[0].Form(m.Tokens, 3)?.Name == "기본1", "mate: 부화하면 기본형부터");
        Check.That(m.Advance(4_000_000_000L, t0).Count == 0 && m.Tokens == 30_000_000, "mate: 기록이 지워져 합계가 줄어도 뒤로 안 감");
        Check.That(m.Advance(4_000_000_000L + 270_000_000, t0).SequenceEqual(new MateEvent[] { new MateEvent.Evolved(first, 2) }), "mate: 3억에 중간 진화");
        Check.That(m.Advance(4_000_000_000L + 570_000_000, t0).SequenceEqual(new MateEvent[] { new MateEvent.Evolved(first, 3) }), "mate: 6억에 최종 진화");
        var later = t0.AddSeconds(86_400 * 2);
        var jump = new MateState();
        jump.Advance(0, t0);
        jump.Current!.Form = 3;
        var ev = jump.Advance(1_130_000_000, later);
        var next = jump.Current?.Species ?? "";
        Check.That(ev.SequenceEqual(new MateEvent[]
                   {
                       new MateEvent.Hatched(first), new MateEvent.Evolved(first, 2), new MateEvent.Evolved(first, 3),
                       new MateEvent.Completed(first), new MateEvent.Started(next), new MateEvent.Hatched(next),
                   }) && MateSpecies.Find(next) != null, "mate: 한 번에 넘어도 부화·진화 차례로·등록·다음 알 순서 (단계 안 건너뜀)");
        Check.That(jump.Tokens == 130_000_000 && jump.Collected[0].Days == 3 && jump.LastForm(first) == 3, "mate: 넘친 토큰은 다음 알로, 함께한 날 수, 진화 모습 기록");
        Check.That(jump.SeenDex().SetEquals(new[] { 1, 2, 3, MateSpecies.Find(next)!.Base.Id }), "dex: 등록한 친구는 지나온 단계 모두, 키우는 친구는 지금 단계까지 만남");
        var raising = new MateState
        {
            Current = new CurrentMate(first, 400_000_000, t0, 3),
            Party = new List<CurrentMate> { new(first, 10_000_000, t0, 4) },
        };
        Check.That(raising.SeenDex().SetEquals(new[] { 1, 2 }), "dex: 1진화 중이면 기본형·중간형만, 알은 아직 안 만남");
        raising.Collected = new List<CollectedMate> { new(first, t0, 1, 4) };
        Check.That(raising.SeenDex().SetEquals(new[] { 1, 2, 4 }) && raising.CompletedCount(4) == 1 && raising.CompletedCount(3) == 0,
                   "dex: 갈래 진화로 등록하면 그 갈래 최종형이 만남·등록 횟수");
        var line1 = MateSpecies.Find(first)!;
        var pick = new MateState { Current = new CurrentMate(first, 400_000_000, t0, 3) };
        Check.That(pick.Show(first, 1) && pick.Current!.Shown == 1 && line1.ShownForm(400_000_000, 3, 1)?.Name == "기본1" && pick.Tokens == 400_000_000,
                   "dex: 지나온 단계를 대표 모습으로 (성장은 그대로)");
        Check.That(!pick.Show(first, 3) && pick.Current!.Shown == 1, "dex: 아직 안 된 단계는 고를 수 없음");
        Check.That(line1.ShownForm(700_000_000, 3, 1)?.Name == "기본1" && line1.ShownForm(700_000_000, 3, null)?.Name == "최종1", "dex: 진화해도 고른 모습 유지, 안 고르면 지금 모습");
        Check.That(pick.Show(first, 2) && pick.Current!.Shown == null, "dex: 지금 단계를 고르면 원래 모습으로");
        var partyPick = new MateState
        {
            Current = new CurrentMate("x", 0, t0),
            Party = new List<CurrentMate> { new(first, 400_000_000, t0, 3) },
        };
        Check.That(partyPick.Show(first, 1) && partyPick.Current!.Species == first && partyPick.Current.Shown == 1 && partyPick.Party[0].Species == "x",
                   "dex: 파티 포켓몬의 지나온 단계를 고르면 그 계열이 대표가 되고 모습만 바뀜");
        var dexEntries = MateDex.Entries(MateSpecies.All, 151);
        Check.That(dexEntries.Count == 25 && dexEntries[0].Form.Id == 1 && dexEntries[0].Species.Id == first && dexEntries.Select(e => e.Id).Distinct().Count() == dexEntries.Count,
                   "dex: 도감 칸은 포켓몬 한 마리마다 (계열 6개·4마리씩 + 전설 1)");
        Check.That(MateDex.Entries(MateSpecies.All, 40).Count == 16, "dex: 범위 밖 번호는 칸에서 뺌");
        var sp = MateSpecies.Find(first)!;
        Check.That(sp.Form(400_000_000, 4)?.Name == "갈래1" && sp.Form(700_000_000, 4)?.Name == "갈래1" && sp.Form(400_000_000, 3)?.Name == "중간1",
                   "mate: 2단계 갈래는 3억에 최종, 3단계는 중간을 거침");
        var legend = MateSpecies.Find("99")!;
        Check.That(legend.Form(900_000_000, null)?.Name == "전설" && MateRules.GoalLabel(900_000_000, 1) == "도감 등록", "mate: 진화 없는 포켓몬은 그대로");
        var counts = new Dictionary<MateRarity, int>();
        var lcg = new SplitMix(42);
        var picker = new MateState { Collected = new List<CollectedMate> { new(first, t0, 1) } };
        for (var i = 0; i < 10_000; i++)
        {
            var r = MateSpecies.Find(picker.NextSpecies(lcg))!.Rarity;
            counts[r] = counts.GetValueOrDefault(r) + 1;
        }
        int C(MateRarity r) => counts.GetValueOrDefault(r);
        Check.That(C(MateRarity.Common) > 6_500 && C(MateRarity.Common) < 7_500 && C(MateRarity.Uncommon) > 1_700 && C(MateRarity.Uncommon) < 2_300
                   && C(MateRarity.Rare) > 600 && C(MateRarity.Rare) < 1_000 && C(MateRarity.Legendary) > 100 && C(MateRarity.Legendary) < 300,
                   "mate: 다음 알은 희귀도 가중 무작위 (일반≈70%, 고급≈20%, 희귀≈8%, 전설≈2%)");
        Check.That(Math.Abs(MateRules.StageProgress(165_000_000, 3) - 0.5) < 1e-9, "mate: 단계 안 진행률");
        var old = new MateState { Current = new CurrentMate("mochi", 51_756_346, t0), LastAllTime = 10 };
        Check.That(old.AdoptUnknown() && MateSpecies.Find(old.Current!.Species) != null && old.Tokens == 51_756_346
                   && old.Current.StartedAt == t0 && !old.AdoptUnknown(), "mate: 옛 캐릭터는 토큰 그대로 포켓몬으로 바꿈 (한 번만)");
        var party = new MateState
        {
            Current = new CurrentMate("1", 50_000_000, t0),
            Party = new List<CurrentMate> { new("21", 30_000_000, t0) },
            LastAllTime = 100,
        };
        Check.That(party.MakeRepresentative("21") && party.Current!.Species == "21" && party.Party.Select(p => p.Species).SequenceEqual(new[] { "1" })
                   && party.Party[0].Tokens == 50_000_000, "mate: 대표 바꾸면 이전 대표는 토큰 그대로 쉬기");
        Check.That(!party.MakeRepresentative("21") && !party.MakeRepresentative("41"), "mate: 이미 대표거나 키우지 않는 포켓몬은 안 바뀜");
        party.Advance(1_100, t0);
        Check.That(party.Current!.Tokens == 30_001_000 && party.Party[0].Tokens == 50_000_000, "mate: 토큰은 대표만 먹음");
        var decoded = Json.TryDeserialize<MateState>("""{"collected":[],"lastAllTime":5}""");
        Check.That(decoded?.Party.Count == 0 && decoded.LastAllTime == 5, "mate: party 없는 옛 파일도 읽음");
        Check.That(new int?[] { null, 0, 1, 2 }.Select(MateRules.Label).SequenceEqual(new[] { "알", "기본", "1진화", "2진화" }), "mate: 단계 표기는 공식 용어");

        var gen1 = new PokedexCatalog(151, new[] { Form(25, "피카츄"), Form(26, "라이츄"), Form(111, "뿔카노"), Form(112, "코뿌리") },
                                      new[] { Line(25, new[] { new[] { 25, 26 } }), Line(111, new[] { new[] { 111, 112 } }) });
        var whole = new PokedexCatalog(1025, new[] { Form(172, "피츄"), Form(25, "피카츄"), Form(26, "라이츄"), Form(111, "뿔카노"), Form(112, "코뿌리"), Form(464, "거대코뿌리") },
                                       new[] { Line(172, new[] { new[] { 172, 25, 26 } }), Line(111, new[] { new[] { 111, 112, 464 } }) });
        MateSpecies.Install(whole, new[] { gen1 });
        Check.That(MateSpecies.All.Select(s => s.Id).SequenceEqual(new[] { "111", "172" }) && MateSpecies.Find("25")?.Name == "피카츄", "range: 고르는 범위는 전체, 1세대에만 있는 줄도 찾음");
        Check.That(MateSpecies.Find("111")!.Path(112).Select(f => f.Id).SequenceEqual(new[] { 111, 112 })
                   && MateSpecies.Find("111")!.Path(464).Select(f => f.Id).SequenceEqual(new[] { 111, 112, 464 }),
                   "range: 정해 둔 최종 모습까지만 진화 (1세대에서 정한 코뿌리는 거대코뿌리로 안 늘어남)");
        Check.That(MateSpecies.Find("111")!.Form(900_000_000, 112)?.Name == "코뿌리", "range: 전체로 바꿔도 뿔카노는 코뿌리에서 멈춤");
        MateSpecies.Install(gen1, new[] { whole });
        Check.That(MateSpecies.All.Select(s => s.Id).SequenceEqual(new[] { "25", "111" }) && MateSpecies.Find("172")?.Name == "피츄", "range: 1세대로 좁혀도 범위 밖 포켓몬을 찾음");
        Check.That(Enumerable.Range(0, 50).All(_ => MateState.NewMate("111", t0).Form == 112), "range: 새 알의 진화 경로는 고른 범위에서");
        var outside = new MateState { Current = new CurrentMate("172", 40_000_000, t0, 26), LastAllTime = 1 };
        Check.That(!outside.AdoptUnknown() && outside.Current!.Species == "172", "range: 범위 밖 포켓몬을 다른 포켓몬으로 바꾸지 않음");
        var unknownNumber = new MateState { Current = new CurrentMate("999", 5, t0), LastAllTime = 1 };
        Check.That(!unknownNumber.AdoptUnknown(), "range: 번호로 된 포켓몬은 도감에 없어도 그대로 둠");
        Check.That(MateSpecies.EggGrid.Count == 14 && MateSpecies.EggGrid.All(r => r.Length == 14), "mate: 알 그림 14x14");

        // 저장 파일: 날짜는 .iso8601, 키는 정렬 (맥 앱·옛 파일과 같은 형식)
        var dir = Check.TempDir("pm-mates");
        var file = Path.Combine(dir, "mates.json");
        var state = new MateState { Current = new CurrentMate("25", 123, t0, 26), LastAllTime = 7, Collected = new List<CollectedMate> { new("25", t0, 2, 26) } };
        MateStore.Save(state, file);
        var text = File.ReadAllText(file);
        Check.That(MateStore.Load(file) == state && text.Contains("\"startedAt\": \"2027-01-15T08:00:00Z\"")
                   && text.IndexOf("\"collected\"", StringComparison.Ordinal) < text.IndexOf("\"current\"", StringComparison.Ordinal),
                   "mate: 저장하고 다시 읽음 (날짜 ISO 8601, 키 정렬)");
        File.WriteAllText(file, "{ 깨진");
        Check.That(MateStore.Load(file) == new MateState(), "mate: 깨진 파일이면 빈 상태");
        Check.Remove(dir);

        // 이후 검사에 이 포켓몬 목록이 남지 않게 비움
        MateSpecies.Install(new PokedexCatalog(151, new List<PokemonForm>(), new List<PokemonLine>()));
    }
}
