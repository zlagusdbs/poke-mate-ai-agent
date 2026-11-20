using System.Windows.Threading;

namespace PokeMate;

/// 토큰 사용량, 키우는 포켓몬, 도감. 맥 RootView.swift 의 UsageStore. UI 스레드에서만 쓴다.
public sealed class UsageStore : NotifyObject
{
    public static UsageStore Shared { get; } = new();

    UsageSummary summary = new();
    DateTimeOffset? updated;
    bool loading;
    MateState mate = new();
    string? mateMessage;
    IReadOnlyList<MateSpecies> allSpecies = Array.Empty<MateSpecies>();
    IReadOnlyList<MateSpecies> knownSpecies = Array.Empty<MateSpecies>();
    string? pokedexError;
    PokedexProgress? pokedexProgress;
    PokedexRange range;
    PokedexRange? pokedexLoading;
    readonly ClaudeUsageReader reader;
    readonly string mateFile;
    DispatcherTimer? timer;

    public UsageSummary Summary { get => summary; private set => Set(ref summary, value); }
    public DateTimeOffset? Updated { get => updated; private set => Set(ref updated, value); }
    public bool Loading { get => loading; private set => Set(ref loading, value); }
    public MateState Mate { get => mate; private set => Set(ref mate, value); }
    public string? MateMessage { get => mateMessage; private set => Set(ref mateMessage, value); }
    public IReadOnlyList<MateSpecies> AllSpecies { get => allSpecies; private set => Set(ref allSpecies, value); }
    public IReadOnlyList<MateSpecies> KnownSpecies { get => knownSpecies; private set => Set(ref knownSpecies, value); }
    public string? PokedexError { get => pokedexError; private set => Set(ref pokedexError, value); }
    public PokedexProgress? PokedexProgress { get => pokedexProgress; private set => Set(ref pokedexProgress, value); }
    public PokedexRange Range { get => range; private set => Set(ref range, value); }

    UsageStore()
    {
        var dir = AppPaths.DataDir;
        reader = new ClaudeUsageReader(null, Path.Combine(dir, "token-cache.json"));
        mateFile = Path.Combine(dir, "mates.json");
        mate = MateStore.Load(mateFile);
        range = AppPaths.LoadSettings().PokedexRange;
        InstallCached();
    }

    public MateSpecies? Species(string id) => MateSpecies.Find(id);

    public bool HasRangeCatalog => PokedexLoader.LoadCached(Range) != null;

    public void ApplyRange(PokedexRange value)
    {
        if (value == Range) return;
        Range = value;
        PokedexError = null;
        InstallCached();
        LoadPokedex();
    }

    void InstallCached()
    {
        var catalogs = PokedexRangeExtensions.AllCases
            .Select(r => (Range: r, Catalog: PokedexLoader.LoadCached(r)))
            .Where(x => x.Catalog != null)
            .Select(x => (x.Range, Catalog: x.Catalog!))
            .ToList();
        var pool = catalogs.FirstOrDefault(c => c.Range == Range) is { Catalog: not null } match
            ? match.Catalog
            : catalogs.Select(c => c.Catalog).FirstOrDefault();
        if (pool == null) return;
        Install(pool, catalogs.Select(c => c.Catalog).ToList());
    }

    /// 지금 키우는 포켓몬의 이름 (알이면 null)
    public string? PokemonName
    {
        get
        {
            if (Mate.Current is not { } current || Species(current.Species) is not { } species) return null;
            return species.ShownForm(current.Tokens, current.Form, current.Shown)?.Name;
        }
    }

    public string MateName(AppSettings settings)
    {
        if (PokemonName is { } name) return name;
        var assistant = settings.AssistantName.Trim();
        return assistant.Length == 0 ? AppSettings.DefaultAssistantName : assistant;
    }

    /// 상태를 복사한다. MateState 는 참조 형식이고 Advance 가 자기 자신을 바꾸므로, 바꾸기 전과 비교하려면 복사본이 필요하다.
    static MateState Copy(MateState state) => new(
        state.Current is null ? null : state.Current with { },
        state.Party.Select(p => p with { }),
        state.Collected.Select(c => c with { }),
        state.LastAllTime);

    public void MakeRepresentative(string id, int? form = null)
    {
        var state = Copy(Mate);
        var changed = form is int f ? state.Show(id, f) : state.MakeRepresentative(id);
        if (!changed) return;
        Mate = state;
        MateMessage = null;
        MateStore.Save(state, mateFile);
    }

    void Install(PokedexCatalog catalog, List<PokedexCatalog> lookup)
    {
        MateSpecies.Install(catalog, lookup);
        AllSpecies = MateSpecies.All;
        KnownSpecies = MateSpecies.Known;
        var state = Copy(Mate);
        if (state.AdoptUnknown())
        {
            Mate = state;
            MateStore.Save(state, mateFile);
        }
        foreach (var mate in new[] { state.Current }.OfType<CurrentMate>().Concat(state.Party))
            if (Species(mate.Species) is { } species)
                foreach (var form in species.Path(mate.Form)) PokemonSprites.Shared.Load(form.Id);
    }

    public async void LoadPokedex()
    {
        var target = Range;
        if (HasRangeCatalog || pokedexLoading == target) return;
        pokedexLoading = target;
        PokedexError = null;
        PokedexProgress = null;
        try
        {
            var context = System.Threading.SynchronizationContext.Current;
            var catalog = await PokedexLoader.Fetch(target, null, progress =>
                context?.Post(_ => { if (pokedexLoading == target) PokedexProgress = progress; }, null));
            PokedexLoader.Save(catalog, PokedexLoader.CatalogFile(target));
            InstallCached();
            if (Updated != null) ApplyMate(Summary.AllTime);
        }
        catch (Exception e)
        {
            if (Range == target) PokedexError = $"{target.Label()} 도감을 받지 못했어요: {e.Message}";
        }
        if (pokedexLoading == target)
        {
            pokedexLoading = null;
            PokedexProgress = null;
        }
    }

    void ApplyMate(long allTime)
    {
        var state = Copy(Mate);
        var events = state.Advance(allTime, DateTimeOffset.Now);
        var last = events.LastOrDefault(e => e is not MateEvent.Started);
        if (last != null)
        {
            string Base(string id) => MateSpecies.Find(id)?.Name ?? id;
            string Final(string id)
            {
                var form = id == state.Current?.Species ? state.Current?.Form : state.LastForm(id);
                return MateSpecies.Find(id)?.Final(form).Name ?? id;
            }
            string Evolved(string id, int to)
            {
                var form = state.LastForm(id) ?? state.Current?.Form;
                var species = MateSpecies.Find(id);
                var path = (species?.Paths.FirstOrDefault(p => p.Any(f => f.Id == to)) ?? species?.Path(form) ?? new List<PokemonForm>()).ToList();
                var i = path.FindIndex(f => f.Id == to);
                return i > 0 ? $"{path[i - 1].Name}(이)가 {path[i].Name}(으)로 진화했어요!" : $"{Base(id)}(이)가 진화했어요!";
            }
            switch (last)
            {
                case MateEvent.Hatched hatched: MateMessage = $"알에서 {Base(hatched.Species)}(이)가 나왔어요!"; break;
                case MateEvent.Evolved evolved: MateMessage = Evolved(evolved.Species, evolved.Form); break;
                case MateEvent.Completed completed: MateMessage = $"{Final(completed.Species)}(이)가 도감에 등록됐어요. 새 알이 왔어요."; break;
            }
        }
        if (!state.Equals(Mate))
        {
            Mate = state;
            MateStore.Save(state, mateFile);
        }
    }

    public void Start()
    {
        if (timer != null) return;
        LoadPokedex();
        Refresh();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    public async void Refresh()
    {
        if (Loading) return;
        Loading = true;
        try
        {
            var s = await Task.Run(() => reader.Refresh());
            ApplyMate(s.AllTime);
            Summary = s;
            var now = DateTimeOffset.Now;
            Updated = now;
            var snapshot = s.Snapshot(now);
            _ = Task.Run(() => { try { AppPaths.WriteAtomic(UsageSummary.SnapshotFile, snapshot); } catch { } });
        }
        catch (Exception e)
        {
            App.Log($"토큰 집계 실패: {e}");
        }
        finally { Loading = false; }
    }
}
