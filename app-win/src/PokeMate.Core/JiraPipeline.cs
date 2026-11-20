using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// Jira 티켓 하나를 나눠 처리하는 역할. JSON 값은 pm·dev·tester·reviewer.
public enum PipelineRole { Pm, Dev, Tester, Reviewer }

/// 맥의 `PipelineRole` 의 속성·정적 함수.
public static class PipelineRoleExtensions
{
    public static IReadOnlyList<PipelineRole> AllCases { get; } = Enum.GetValues<PipelineRole>();

    /// 맥의 `rawValue` (pm·dev·tester·reviewer)
    public static string Raw(this PipelineRole role) => role switch
    {
        PipelineRole.Pm => "pm",
        PipelineRole.Dev => "dev",
        PipelineRole.Tester => "tester",
        _ => "reviewer",
    };

    public static PipelineRole? FromRaw(string? raw) => AllCases.Cast<PipelineRole?>().FirstOrDefault(r => r!.Value.Raw() == raw);

    public static string Label(this PipelineRole role) => role switch
    {
        PipelineRole.Pm => "PM",
        PipelineRole.Dev => "개발",
        PipelineRole.Tester => "테스터",
        _ => "리뷰어",
    };

    /// 역할 세션의 키: `{도메인}:{역할}` (sessions.json 의 키)
    public static string SessionKey(string domain, PipelineRole role) => $"{domain}:{role.Raw()}";

    /// 키를 도메인과 역할로 나눈다 (마지막 `:` 기준). 역할이 아니거나 도메인이 비면 null (그냥 레포 이름).
    public static (string Domain, PipelineRole Role)? Split(string key)
    {
        var i = key.LastIndexOf(':');
        if (i < 0 || FromRaw(key[(i + 1)..]) is not PipelineRole role) return null;
        var domain = key[..i];
        return domain.Length == 0 ? null : (domain, role);
    }
}

/// `local\domains.json`: 도메인 → 레포·지식 저장소·설명·키워드.
public sealed record DomainMap
{
    public Dictionary<string, List<string>> Domains { get; set; } = new();
    public Dictionary<string, List<string>> Knowledge { get; set; } = new();
    public List<string> DefaultKnowledge { get; set; } = new();
    public Dictionary<string, string> Descriptions { get; set; } = new();
    public Dictionary<string, List<string>> Keywords { get; set; } = new();

    public DomainMap() { }

    public DomainMap(IDictionary<string, List<string>>? domains = null, IDictionary<string, List<string>>? knowledge = null, IEnumerable<string>? defaultKnowledge = null,
                     IDictionary<string, string>? descriptions = null, IDictionary<string, List<string>>? keywords = null)
    {
        Domains = domains == null ? new() : new(domains);
        Knowledge = knowledge == null ? new() : new(knowledge);
        DefaultKnowledge = defaultKnowledge?.ToList() ?? new();
        Descriptions = descriptions == null ? new() : new(descriptions);
        Keywords = keywords == null ? new() : new(keywords);
    }

    public const string DefaultKey = "default";
    public const string Placeholder = "{{knowledge}}";

    public static string File => AppPaths.LocalFile("domains.json");

    /// 깨졌거나 객체가 아니면 빈 지도. 이름이 비었거나 `:` 가 들었거나 `default` 인 도메인은 뺀다.
    public static DomainMap Parse(string? json)
    {
        if (Json.ParseObject(json) is not JsonObject obj) return new DomainMap();
        static List<string> Items(JsonNode? value, string field) =>
            (Json.Strings((value as JsonObject)?[field]) ?? new List<string>()).Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();
        var map = new DomainMap();
        foreach (var (name, value) in obj)
        {
            var key = SwiftText.TrimSpaces(name);
            if (key.Length == 0 || key.Contains(':') || key == DefaultKey) continue;
            var body = value as JsonObject;
            map.Domains[key] = Items(value, "repos");
            if (body != null && body.ContainsKey("knowledge")) map.Knowledge[key] = Items(value, "knowledge");
            if (Json.Str(body?["description"]) is string description && SwiftText.Trim(description) is { Length: > 0 } text) map.Descriptions[key] = text;
            var words = Items(value, "keywords");
            if (words.Count > 0) map.Keywords[key] = words;
        }
        map.DefaultKnowledge = Items(obj[DefaultKey], "knowledge");
        return map;
    }

    /// 파일이 없거나 읽을 수 없으면 빈 지도
    public static DomainMap Load(string? from = null)
    {
        try { return Parse(Paths.ReadShared(from ?? File)); }
        catch { return new DomainMap(); }
    }

    /// 레포가 속한 도메인 (이름순으로 먼저 찾은 것). 등록이 없으면 레포 이름이 곧 도메인.
    public string Domain(string repo) =>
        Domains.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(k => Domains[k].Contains(repo)) ?? repo;

    /// 도메인의 지식 저장소 경로. `~\`·`~/` 는 홈, 절대 경로는 그대로, 나머지는 `홈\Workspace\` 아래.
    public List<string> KnowledgePaths(string domain, string? home = null)
    {
        home ??= Paths.Home;
        var items = Knowledge.TryGetValue(domain, out var own) ? own : DefaultKnowledge;
        return items.Select(item =>
            item.StartsWith("~/", StringComparison.Ordinal) || item.StartsWith("~\\", StringComparison.Ordinal) ? Paths.ExpandTilde(item, home)
            : item.StartsWith('/') || Paths.IsWindowsAbsolute(item) ? item
            : Path.Combine(home, "Workspace", item.Replace('/', '\\'))).ToList();
    }

    /// 프롬프트에 넣는 지식 저장소 목록. 홈 아래는 `~/…`, 나머지는 `/` 구분으로 보여 준다 (Git Bash·Claude 가 그대로 읽는다).
    public string KnowledgeText(string domain, string? home = null)
    {
        home ??= Paths.Home;
        var paths = KnowledgePaths(domain, home);
        if (paths.Count == 0) return "   (정한 지식 저장소 없음)";
        var prefix = home.TrimEnd('\\', '/');
        string Shown(string path) =>
            path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                ? "~" + Paths.ForwardSlashes(path[prefix.Length..])
                : Paths.ForwardSlashes(path);
        return string.Join("\n", paths.Select(p => $"   - `{Shown(p)}`"));
    }
}

public sealed record PipelineBranch(string Branch, string From);

/// 역할 답 끝의 ```pokemate-next 블록: 다음에 누구에게 넘길지.
public sealed record PipelineNext
{
    /// 맥의 중첩 enum `Kind` 는 속성 `Kind` 와 겹쳐 `NextKind` 로 둔다 (JSON 값은 tester·dev·pm·reviewer·push·stop).
    public enum NextKind { Tester, Dev, Pm, Reviewer, Push, Stop }

    public NextKind Kind { get; set; }
    public string Note { get; set; } = "";
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public List<PipelineBranch> Create { get; set; } = new();
    public string? Message { get; set; }
    public bool Waiting { get; set; }

    public PipelineNext(NextKind kind, string note = "", string? repo = null, string? branch = null, IEnumerable<PipelineBranch>? create = null,
                        string? message = null, bool waiting = false)
    {
        Kind = kind;
        Note = note;
        Repo = repo;
        Branch = branch;
        Create = create?.ToList() ?? new();
        Message = message;
        Waiting = waiting;
    }

    public const string Tag = "pokemate-next";

    static readonly Regex Block = new("```" + Tag + @"[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);

    static NextKind? ParseKind(string? raw) => SwiftText.TrimSpaces(raw ?? "").ToLowerInvariant() switch
    {
        "tester" => NextKind.Tester,
        "dev" => NextKind.Dev,
        "pm" => NextKind.Pm,
        "reviewer" => NextKind.Reviewer,
        "push" => NextKind.Push,
        "stop" => NextKind.Stop,
        _ => null,
    };

    /// 답에서 마지막 블록을 읽는다. 블록이 없거나 깨졌거나 모르는 next 면 null.
    public static PipelineNext? Extract(string text)
    {
        var m = Block.Matches(text).LastOrDefault();
        if (m == null || Json.ParseObject(m.Groups[1].Value) is not JsonObject obj) return null;
        if (Json.Str(obj["next"]) is not string raw || ParseKind(raw) is not NextKind kind) return null;
        string? Field(string k)
        {
            var v = SwiftText.Trim(Json.Str(obj[k]) ?? "");
            return v.Length == 0 ? null : v;
        }
        var create = new List<PipelineBranch>();
        if (obj["create"] is JsonArray items && items.All(i => i is JsonObject))
            foreach (var item in items.Cast<JsonObject>())
                if (Json.Str(item["branch"]) is { Length: > 0 } b && Json.Str(item["from"]) is { Length: > 0 } f) create.Add(new PipelineBranch(b, f));
        return new PipelineNext(kind, Field("note") ?? "", Field("repo"), Field("branch"), create, Field("message"), Json.Bool(obj["waiting"]) ?? false);
    }

    /// 답에서 블록을 모두 뗀 글 (앞뒤 공백 정리)
    public static string Strip(string text) => SwiftText.Trim(Block.Replace(text, ""));

    public bool Equals(PipelineNext? o) =>
        o is not null && Kind == o.Kind && Note == o.Note && Repo == o.Repo && Branch == o.Branch && Create.SequenceEqual(o.Create)
        && Message == o.Message && Waiting == o.Waiting;

    public override int GetHashCode() => HashCode.Combine(Kind, Note, Repo, Branch);
}

public static class PipelineNextExtensions
{
    public static string Raw(this PipelineNext.NextKind kind) => kind switch
    {
        PipelineNext.NextKind.Tester => "tester",
        PipelineNext.NextKind.Dev => "dev",
        PipelineNext.NextKind.Pm => "pm",
        PipelineNext.NextKind.Reviewer => "reviewer",
        PipelineNext.NextKind.Push => "push",
        _ => "stop",
    };
}

/// 티켓 하나의 진행 상태. `automation\pipelines\{KEY}.json` (맥과 같은 키·ISO 8601 날짜·키 정렬).
/// 값이 클래스라서 맥의 값 복사가 필요한 곳(여러 번 시작하는 상태 등)은 `Copy()` 를 쓴다.
public sealed record PipelineState
{
    /// 맥의 중첩 enum `Stage` 는 속성 `Stage` 와 겹쳐 `StageKind` 로 둔다 (JSON 값은 pm·dev·tester·reviewer·branch·push·waiting·done·stopped).
    public enum StageKind { Pm, Dev, Tester, Reviewer, Branch, Push, Waiting, Done, Stopped }

    public sealed record Step(string Role, string Next, string Note, DateTimeOffset At);

    public abstract record Outcome
    {
        public sealed record Next : Outcome;
        public sealed record Finished : Outcome;
        public sealed record Blocked(string Reason) : Outcome;
    }

    /// 맥의 중첩 struct `Inflight` 는 속성 `Inflight` 와 겹쳐 `InflightRun` 으로 둔다.
    public sealed record InflightRun(string Role, string SessionId, DateTimeOffset StartedAt);

    public abstract record Arrival
    {
        public sealed record Start : Arrival;
        public sealed record Resume : Arrival;
        public sealed record Skip(string Reason) : Arrival;
    }

    public const int BackLimit = 2;

    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Event { get; set; } = "";
    public string Domain { get; set; } = "";
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public List<PipelineBranch> Create { get; set; } = new();
    public string? Worktree { get; set; }
    public string? CommitMessage { get; set; }
    public string? Brief { get; set; }
    public StageKind Stage { get; set; } = StageKind.Pm;
    public string Note { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? From { get; set; }
    public int TesterBacks { get; set; }
    public int ReviewerBacks { get; set; }
    public bool TestsPassed { get; set; }
    public bool MustStop { get; set; }
    public string? Pushed { get; set; }
    public string? Reason { get; set; }
    public InflightRun? Inflight { get; set; }
    public Dictionary<string, double> Costs { get; set; } = new();
    public List<Step> History { get; set; } = new();
    public int TimeoutMinutes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public PipelineState() { }

    public PipelineState(string key, string title, string @event, string domain, string? repo, int timeoutMinutes, DateTimeOffset now)
    {
        Key = key;
        Title = title;
        Event = @event;
        Domain = domain;
        Repo = repo;
        TimeoutMinutes = timeoutMinutes;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// 맥의 값 복사 (목록·표는 따로)
    public PipelineState Copy() => this with { Create = Create.ToList(), Costs = new Dictionary<string, double>(Costs), History = History.ToList() };

    [JsonIgnore]
    public bool IsActive => Stage is StageKind.Pm or StageKind.Dev or StageKind.Tester or StageKind.Reviewer or StageKind.Branch or StageKind.Push;

    /// 이미 처리한 티켓을 다시 잡지 않는다. 답을 기다리던(waiting) 티켓만 이어 간다. (맥의 `arrival(of:)`: 이름이 중첩 형식 `Arrival` 과 겹쳐 `ArrivalOf`)
    public static Arrival ArrivalOf(PipelineState? existing)
    {
        if (existing == null) return new Arrival.Start();
        if (existing.Stage == StageKind.Waiting) return new Arrival.Resume();
        if (existing.IsActive) return new Arrival.Skip($"이미 처리 중: {existing.StatusText}");
        return new Arrival.Skip($"이미 처리함: {existing.StatusText}{(existing.Reason is string r ? $" · {r}" : "")}");
    }

    /// 지금 단계의 역할 (branch·push·waiting·done·stopped 는 역할이 아님)
    [JsonIgnore]
    public PipelineRole? Role => Stage switch
    {
        StageKind.Pm => PipelineRole.Pm,
        StageKind.Dev => PipelineRole.Dev,
        StageKind.Tester => PipelineRole.Tester,
        StageKind.Reviewer => PipelineRole.Reviewer,
        _ => null,
    };

    [JsonIgnore] public double TotalCost => Costs.Values.Sum();

    [JsonIgnore]
    public string StatusText => Stage switch
    {
        StageKind.Tester => $"{Key} · tester {TesterBacks}/{BackLimit}",
        StageKind.Reviewer => $"{Key} · reviewer {ReviewerBacks}/{BackLimit}",
        StageKind.Waiting => $"{Key} · 답 기다림",
        StageKind.Stopped => $"{Key} · 멈춤",
        _ => $"{Key} · {Stage.Raw()}",
    };

    /// 이 역할이 다음으로 넘길 수 있는 곳
    public List<PipelineNext.NextKind> Allowed(PipelineRole role)
    {
        switch (role)
        {
            case PipelineRole.Pm:
                if (MustStop) return new() { PipelineNext.NextKind.Stop };
                return TestsPassed
                    ? new() { PipelineNext.NextKind.Reviewer, PipelineNext.NextKind.Dev, PipelineNext.NextKind.Stop }
                    : new() { PipelineNext.NextKind.Dev, PipelineNext.NextKind.Stop };
            case PipelineRole.Dev: return new() { PipelineNext.NextKind.Tester, PipelineNext.NextKind.Pm };
            case PipelineRole.Tester: return new() { PipelineNext.NextKind.Dev, PipelineNext.NextKind.Pm };
            default: return new() { PipelineNext.NextKind.Dev, PipelineNext.NextKind.Push, PipelineNext.NextKind.Pm };
        }
    }

    public Outcome Stop(string why, DateTimeOffset now)
    {
        Stage = StageKind.Stopped;
        Reason = why;
        UpdatedAt = now;
        return new Outcome.Blocked(why);
    }

    /// 역할의 답(`next` 블록)을 반영해 다음 단계로. 블록이 없거나 못 넘기는 곳이면 멈춘다.
    public Outcome Apply(PipelineRole role, PipelineNext? next, string summary, DateTimeOffset now)
    {
        if (next == null) return Stop($"{role.Label()} 답에 다음 단계 블록이 없거나 깨졌어요", now);
        if (!Allowed(role).Contains(next.Kind)) return Stop($"{role.Label()} 은 지금 {next.Kind.Raw()} 로 넘길 수 없어요", now);
        History.Add(new Step(role.Raw(), next.Kind.Raw(), SwiftText.Prefix(next.Note, 300), now));
        if (next.Message != null) CommitMessage = next.Message;
        Note = next.Note;
        Summary = SwiftText.Suffix(PipelineNext.Strip(summary), 1500);
        From = role.Raw();
        UpdatedAt = now;
        if (next.Kind == PipelineNext.NextKind.Stop)
        {
            Stage = next.Waiting ? StageKind.Waiting : StageKind.Done;
            return new Outcome.Finished();
        }
        switch (role, next.Kind)
        {
            case (PipelineRole.Pm, PipelineNext.NextKind.Dev):
                Brief = next.Note;
                if (Worktree == null)
                {
                    var repo = next.Repo ?? Repo;
                    if (repo == null || next.Branch == null) return Stop("PM 이 레포·브랜치를 정하지 않았어요", now);
                    Repo = repo;
                    Branch = next.Branch;
                    Create = next.Create.ToList();
                    Stage = StageKind.Branch;
                }
                else Stage = StageKind.Dev;
                break;
            case (PipelineRole.Dev, PipelineNext.NextKind.Tester):
                TestsPassed = false;
                Stage = StageKind.Tester;
                break;
            case (PipelineRole.Tester, PipelineNext.NextKind.Pm):
                TestsPassed = true;
                Stage = StageKind.Pm;
                break;
            case (PipelineRole.Tester, PipelineNext.NextKind.Dev):
                TesterBacks += 1;
                TestsPassed = false;
                Stage = TesterBacks > BackLimit ? Limit("테스터→개발") : StageKind.Dev;
                break;
            case (PipelineRole.Reviewer, PipelineNext.NextKind.Dev):
                ReviewerBacks += 1;
                TestsPassed = false;
                Stage = ReviewerBacks > BackLimit ? Limit("리뷰어→개발") : StageKind.Dev;
                break;
            case (PipelineRole.Reviewer, PipelineNext.NextKind.Push):
                Stage = StageKind.Push;
                break;
            default:
                Stage = next.Kind switch
                {
                    PipelineNext.NextKind.Tester => StageKind.Tester,
                    PipelineNext.NextKind.Dev => StageKind.Dev,
                    PipelineNext.NextKind.Pm => StageKind.Pm,
                    PipelineNext.NextKind.Reviewer => StageKind.Reviewer,
                    PipelineNext.NextKind.Push => StageKind.Push,
                    _ => StageKind.Stopped,
                };
                break;
        }
        return new Outcome.Next();
    }

    StageKind Limit(string which)
    {
        MustStop = true;
        Note = $"{which} 되돌림이 {BackLimit}번을 넘어 멈춘다. 마지막 지적: {Note}\nJira 에 확인 필요(또는 개발 중단) 댓글을 남기고 blocked 알림, jira-state 를 한 뒤 stop 한다.";
        return StageKind.Pm;
    }

    /// 브랜치·작업 폴더가 준비됨 → 개발로
    public void BranchReady(string worktree, DateTimeOffset now)
    {
        Worktree = worktree;
        Stage = StageKind.Dev;
        UpdatedAt = now;
    }

    /// PokeMate 가 직접 하는 일(브랜치·push)이 막혔을 때 PM 에게 정리를 넘긴다
    public void HandBack(string text, DateTimeOffset now)
    {
        MustStop = true;
        Note = text;
        From = "pokemate";
        Stage = StageKind.Pm;
        UpdatedAt = now;
    }

    public void PushDone(string output, DateTimeOffset now)
    {
        Pushed = output;
        HandBack($"PokeMate 가 커밋·push 했다.\n{output}\nIn Review 로 바꾸고 결과 댓글, processed 알림, jira-state 를 한 뒤 stop 한다.", now);
    }

    /// 확인 필요에 답이 달림 → PM 부터 다시
    public void Resume(DateTimeOffset now)
    {
        Stage = StageKind.Pm;
        TesterBacks = 0;
        ReviewerBacks = 0;
        MustStop = false;
        Reason = null;
        From = "pokemate";
        Note = "확인 필요 댓글에 답이 달렸다. 공통 지시의 이어 가기 규칙대로 처리한다.";
        UpdatedAt = now;
    }

    public static string Dir(string? baseDir = null) => Path.Combine(baseDir ?? AppPaths.DataDir, "automation", "pipelines");

    public static string File(string key, string? dir = null) => Path.Combine(dir ?? Dir(), $"{key}.json");

    /// 읽을 수 없거나 깨졌으면 null. 다른 프로세스가 열어 둔 파일도 읽는다.
    public static PipelineState? Load(string key, string? dir = null)
    {
        try
        {
            var state = JsonSerializer.Deserialize<PipelineState>(Paths.ReadShared(File(key, dir)), Json.Iso8601);
            if (state == null) return null;
            state.Create ??= new();
            state.Costs ??= new();
            state.History ??= new();
            return state;
        }
        catch { return null; }
    }

    /// 폴더의 모든 상태 파일 (갱신 시각 최신순). 깨진 파일은 건너뛴다.
    public static List<PipelineState> LoadAll(string? dir = null)
    {
        dir ??= Dir();
        string[] files;
        try { files = Directory.GetFiles(dir, "*.json"); } catch { return new(); }
        return files.Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
            .Select(f => Load(Path.GetFileNameWithoutExtension(f), dir))
            .OfType<PipelineState>()
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    /// 키 정렬·들여쓰기·ISO 8601 날짜. 실패하면 false.
    public bool Save(string? dir = null)
    {
        try
        {
            AppPaths.WriteAtomic(File(Key, dir), Json.SortedPretty(this, Json.Iso8601));
            return true;
        }
        catch { return false; }
    }
}

public static class PipelineStageExtensions
{
    public static string Raw(this PipelineState.StageKind stage) => stage switch
    {
        PipelineState.StageKind.Pm => "pm",
        PipelineState.StageKind.Dev => "dev",
        PipelineState.StageKind.Tester => "tester",
        PipelineState.StageKind.Reviewer => "reviewer",
        PipelineState.StageKind.Branch => "branch",
        PipelineState.StageKind.Push => "push",
        PipelineState.StageKind.Waiting => "waiting",
        PipelineState.StageKind.Done => "done",
        _ => "stopped",
    };
}

/// 역할별로 Claude 에게 줄 도구·폴더 권한 (`local\pipeline-tools.json` 또는 기본값). push·브랜치·worktree 만들기는 어떤 역할에도 주지 않는다 (PokeMate 가 직접 한다).
public static class PipelineAccess
{
    public static string File => AppPaths.LocalFile("pipeline-tools.json");

    /// 역할 프롬프트의 `{{helper}}` 자리에 들어가는 명령. Claude 의 Bash 는 Windows 에서 Git Bash 라서 경로는 `/` 구분, 공백·한글 경로를 견디게 따옴표로 묶는다.
    /// 예: `"C:/Users/u/.local/bin/uv.exe" --directory "C:/Users/u/Workspace/poke-mate-ai-agent/mcp-server" run --quiet pokemate auto`
    public static string Helper(AppSettings settings) =>
        $"\"{Paths.ForwardSlashes(settings.UvPath)}\" --directory \"{Paths.ForwardSlashes(Path.Combine(settings.RepoDir, "mcp-server"))}\" run --quiet pokemate auto";

    public static Dictionary<PipelineRole, ClaudeCommand.WriteAccess> Defaults(string helper, string workDir, string references, string pipelines)
    {
        List<string> Bash(params string[] names) => names.Select(n => $"Bash({helper} {n}:*)").ToList();
        return new Dictionary<PipelineRole, ClaudeCommand.WriteAccess>
        {
            [PipelineRole.Pm] = new(Bash("jira-get", "jira-search", "jira-comments", "jira-links", "jira-transition", "jira-comment",
                                         "write-note", "event", "jira-state", "git-read", "domain-note", "domain-map"), new[] { workDir, references }),
            [PipelineRole.Dev] = new(Bash("run-tests", "git-read"), new[] { workDir, references }),
            [PipelineRole.Tester] = new(Bash("run-tests", "git-read"), new[] { workDir }),
            [PipelineRole.Reviewer] = new(Bash("git-read", "domain-note").Concat(new[] { "Skill(domain-review)", "Skill(code-review)" }).ToList(), new[] { workDir, pipelines, references }),
        };
    }

    /// 권한 파일을 읽는다. 깨졌으면 빈 표. 금지된 명령은 뺀다. 폴더의 `~/` 는 홈 폴더로.
    public static Dictionary<PipelineRole, ClaudeCommand.WriteAccess> Parse(string? json, string? home = null)
    {
        var table = new Dictionary<PipelineRole, ClaudeCommand.WriteAccess>();
        if (Json.ParseObject(json) is not JsonObject obj) return table;
        static List<string> Clean(JsonNode? node) => (Json.Strings(node) ?? new List<string>()).Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();
        foreach (var role in PipelineRoleExtensions.AllCases)
        {
            if (obj[role.Raw()] is not JsonObject item) continue;
            var dirs = Clean(item["dirs"]).Select(d => d.StartsWith("~/", StringComparison.Ordinal) || d.StartsWith("~\\", StringComparison.Ordinal) ? Paths.ExpandTilde(d, home) : d).ToList();
            table[role] = new ClaudeCommand.WriteAccess(Clean(item["tools"]).Where(t => !Forbidden(t)).ToList(), dirs);
        }
        return table;
    }

    public static bool Forbidden(string rule) =>
        rule.Contains("commit-push") || rule.Contains("create-branch") || rule.Contains("worktree-add") || rule.Contains("gh-review");

    /// 이 역할이 이번 티켓에서 쓸 권한. 개발만 작업 폴더(W) 수정 권한을 더한다 (`Edit(//c/…/**)`).
    public static ClaudeCommand.WriteAccess Access(PipelineRole role, string? worktree, IReadOnlyDictionary<PipelineRole, ClaudeCommand.WriteAccess> table)
    {
        var access = table.TryGetValue(role, out var found) ? found : new ClaudeCommand.WriteAccess();
        var tools = access.Tools.Where(rule => !Forbidden(rule)
            && (role == PipelineRole.Dev || !(rule.StartsWith("Edit(", StringComparison.Ordinal) || rule.StartsWith("Write(", StringComparison.Ordinal) || rule.StartsWith("MultiEdit(", StringComparison.Ordinal)))).ToList();
        var dirs = access.Dirs.ToList();
        if (role == PipelineRole.Dev && !string.IsNullOrEmpty(worktree))
        {
            tools.Add($"Edit({Paths.ClaudePattern(worktree)}/**)");
            tools.Add($"Write({Paths.ClaudePattern(worktree)}/**)");
            dirs.Add(worktree);
        }
        return new ClaudeCommand.WriteAccess(tools, dirs);
    }

    /// 권한 파일 글 (키 정렬·들여쓰기)
    public static string Encode(IReadOnlyDictionary<PipelineRole, ClaudeCommand.WriteAccess> table)
    {
        var obj = new JsonObject();
        foreach (var (role, access) in table)
            obj[role.Raw()] = new JsonObject
            {
                ["tools"] = new JsonArray(access.Tools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["dirs"] = new JsonArray(access.Dirs.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
            };
        return Json.ToText(Json.Sorted(obj), indented: true);
    }
}

/// 역할 한 번 실행한 결과
public sealed record PipelineRoleResult(string? Text = null, string? SessionId = null, RunUsage? Usage = null, string? Failure = null, bool Interrupted = false);

/// 파이프라인이 바깥 세계(claude·helper·세션 기록·저장)를 부르는 자리. 검사는 가짜 실행기를 넣는다.
public sealed class PipelineIO
{
    /// (역할, 프롬프트, 이어 쓸 세션 id, 새로 시작할 세션 id, 권한)
    public Func<PipelineRole, string, string?, string?, ClaudeCommand.WriteAccess, Task<PipelineRoleResult>> RunRole { get; set; }
    /// helper 명령 (`event`·`jira-state`·`create-branch`·`worktree-add`·`commit-push` …)의 인자 → (성공 여부, 출력)
    public Func<IReadOnlyList<string>, Task<(bool Ok, string Output)>> Helper { get; set; }
    /// 작업 폴더의 변경(diff). 만들지 못하면 null
    public Func<string, Task<string?>> Diff { get; set; }
    /// 역할 세션 키(`{도메인}:{역할}`)로 이어 쓸 세션 id
    public Func<string, Task<string?>> SessionId { get; set; }
    /// (세션 키, 세션 id, 티켓 키, 사용량)
    public Func<string, string, string, RunUsage?, Task> RecordSession { get; set; }
    public Func<PipelineState, Task> Save { get; set; }
    /// (제목, 내용): 앱 안 알림
    public Func<string, string, Task> Notice { get; set; }

    public PipelineIO(Func<PipelineRole, string, string?, string?, ClaudeCommand.WriteAccess, Task<PipelineRoleResult>> runRole,
                      Func<IReadOnlyList<string>, Task<(bool Ok, string Output)>> helper,
                      Func<string, Task<string?>> diff,
                      Func<string, Task<string?>> sessionId,
                      Func<string, string, string, RunUsage?, Task> recordSession,
                      Func<PipelineState, Task> save,
                      Func<string, string, Task>? notice = null)
    {
        RunRole = runRole;
        Helper = helper;
        Diff = diff;
        SessionId = sessionId;
        RecordSession = recordSession;
        Save = save;
        Notice = notice ?? ((_, _) => Task.CompletedTask);
    }
}

/// 파이프라인 설정: 역할 프롬프트·공통 지시·권한·진행 폴더·도메인 지도·노트.
public sealed class PipelineConfig
{
    public Dictionary<PipelineRole, string> Prompts { get; set; }
    public string Common { get; set; }
    public Dictionary<PipelineRole, ClaudeCommand.WriteAccess> Access { get; set; }
    public string Dir { get; set; }
    public int MaxSteps { get; set; }
    public DomainMap Domains { get; set; }
    public string Home { get; set; }
    public DomainNotes Notes { get; set; }

    public PipelineConfig(IDictionary<PipelineRole, string> prompts, string common, IDictionary<PipelineRole, ClaudeCommand.WriteAccess> access, string dir,
                          int maxSteps = 40, DomainMap? domains = null, string? home = null, DomainNotes? notes = null)
    {
        Prompts = new(prompts);
        Common = common;
        Access = new(access);
        Dir = dir;
        MaxSteps = maxSteps;
        Domains = domains ?? new DomainMap();
        Home = home ?? Paths.Home;
        Notes = notes ?? new DomainNotes(DomainNotes.RootFromConnections(null, Home));
    }

    public static string PromptName(PipelineRole role) => $"jira-{role.Raw()}";
    public const string CommonName = "jira-common";

    /// 역할 프롬프트(`jira-pm.md` …)와 공통 지시(`jira-common.md`)를 읽는다. `promptsDir` 에 없으면 `fallbackDir`(shared)에서. `{{helper}}` 는 helper 명령으로 바꾼다.
    public static PipelineConfig Load(string promptsDir, IDictionary<PipelineRole, ClaudeCommand.WriteAccess> access, string dir, string helper, string? fallbackDir = null)
    {
        string? Read(string name)
        {
            foreach (var folder in new[] { promptsDir, fallbackDir })
            {
                if (folder == null) continue;
                try { return System.IO.File.ReadAllText(Rules.PromptFile(name, folder)).Replace("{{helper}}", helper); } catch { }
            }
            return null;
        }
        var prompts = new Dictionary<PipelineRole, string>();
        foreach (var role in PipelineRoleExtensions.AllCases)
            if (Read(PromptName(role)) is string text) prompts[role] = text;
        return new PipelineConfig(prompts, Read(CommonName) ?? "", access, dir);
    }
}

/// Jira 티켓 하나를 PM → 개발 → 테스터 → (PM) → 리뷰어 → push 로 돌리는 진행기.
public static class JiraPipeline
{
    public const string InterruptedNote = "앱이 꺼져서 이 역할 실행이 중간에 끊겼다. 이미 한 일(Jira 댓글·상태·알림·jira-state·W 파일 수정)을 Jira·W·기록으로 먼저 확인하고, 이미 한 것은 다시 하지 말고 남은 것만 이어서 한다. 끝나면 아래 지시대로 답 끝 블록을 붙인다.";

    public const string ResumeNote = "이 세션은 같은 도메인에서 같은 역할로 이전 티켓을 처리하던 세션이다. 이미 읽은 지식은 다시 읽지 말고 이번 티켓에 새로 필요한 것만 찾아라.";

    /// 역할에게 줄 프롬프트. 경로(작업 폴더 W·diff 파일)는 `/` 구분으로 적는다: Claude 의 Bash 는 Windows 에서 Git Bash 라서 역슬래시 경로는 따옴표 없이 쓰면 깨진다.
    public static string Prompt(PipelineRole role, PipelineState state, PipelineConfig config, bool resumed, string? diffFile)
    {
        var s = state;
        var parts = new List<string>
        {
            $"이번 건: {s.Key} · 역할: {role.Label()} ({PipelineRoleExtensions.SessionKey(s.Domain, role)})",
        };
        if (resumed) parts.Add(ResumeNote);
        parts.Add(s.Event);
        var knowledge = config.Domains.KnowledgeText(s.Domain, config.Home);
        if (config.Common.Length > 0) parts.Add("## 공통 지시\n" + config.Common.Replace(DomainMap.Placeholder, knowledge));
        parts.Add($"## 역할 지시 ({role.Label()})\n" + (config.Prompts.TryGetValue(role, out var prompt) ? prompt : "").Replace(DomainMap.Placeholder, knowledge));
        var now = new List<string>
        {
            "## 지금 상황", $"- 도메인: {s.Domain}", $"- 레포: {s.Repo ?? "아직 정하지 않음"}", $"- 브랜치: {s.Branch ?? "아직 없음"}",
            $"- 작업 폴더 W: {(s.Worktree == null ? "아직 없음" : Paths.ForwardSlashes(s.Worktree))}", $"- 커밋 메시지(예정): {s.CommitMessage ?? "아직 없음"}",
            $"- 테스트 통과: {(s.TestsPassed ? "예" : "아니오")}",
            $"- 되돌림: 테스터→개발 {s.TesterBacks}/{PipelineState.BackLimit}, 리뷰어→개발 {s.ReviewerBacks}/{PipelineState.BackLimit}",
        };
        if (s.History.Count > 0) now.Add("- 지나온 단계: " + string.Join(", ", s.History.Select(h => $"{h.Role}→{h.Next}")));
        if (diffFile != null) now.Add($"- 리뷰할 변경(diff 파일): {Paths.ForwardSlashes(diffFile)}");
        if (role != PipelineRole.Tester) now.Add(config.Notes.PromptLines(s.Domain, "$H", s.Key, canWrite: role != PipelineRole.Dev));
        parts.Add(string.Join("\n", now));
        if (role != PipelineRole.Pm && !string.IsNullOrEmpty(s.Brief)) parts.Add($"## PM 개발 지시서\n{s.Brief}");
        if (s.From is string from)
        {
            var handoff = $"## 앞 단계({PipelineRoleExtensions.FromRaw(from)?.Label() ?? from})가 넘긴 말\n{(s.Note.Length == 0 ? "(없음)" : s.Note)}";
            if (s.Summary.Length > 0 && from != "pokemate") handoff += $"\n\n### 앞 단계 답 요약\n{s.Summary}";
            parts.Add(handoff);
        }
        var allowed = string.Join(" | ", s.Allowed(role).Select(k => k.Raw()));
        var tail = string.Join("\n",
            "## 답 끝 블록",
            $"답 끝에 아래 블록 하나만 붙인다. next 는 다음 중 하나: {allowed}",
            "```" + PipelineNext.Tag,
            "{\"next\": \"...\", \"note\": \"다음 단계에게 넘길 말\"}",
            "```");
        if (role == PipelineRole.Pm && s.Worktree == null && !s.MustStop)
            tail += "\ndev 로 넘길 때는 \"repo\"(레포 폴더 이름), \"branch\"(작업 브랜치), \"create\"([{\"branch\": …, \"from\": …}] 만들 순서대로), \"message\"(커밋 메시지 한 줄)를 함께 넣는다.";
        if (role == PipelineRole.Pm) tail += "\nstop 하면서 확인 필요 답을 기다리면 \"waiting\": true 를 넣는다.";
        if (role == PipelineRole.Dev) tail += "\n커밋 메시지를 바꿔야 하면 \"message\" 를 넣는다.";
        parts.Add(tail);
        return string.Join("\n\n", parts);
    }

    /// 상태가 끝(done·waiting·stopped)이 될 때까지 단계를 돌린다. 받은 상태는 고치지 않고 복사해서 진행한다.
    /// 앱이 꺼져 역할 실행이 끊기면 멈춤 처리 없이 실행 중 기록을 남긴 상태를 돌려준다 (다시 시작하면 이어 간다).
    public static async Task<PipelineState> Run(PipelineState start, PipelineIO io, PipelineConfig config, Func<DateTimeOffset>? now = null)
    {
        now ??= () => DateTimeOffset.Now;
        var s = start.Copy();
        var steps = 0;
        while (s.IsActive)
        {
            steps += 1;
            if (steps > config.MaxSteps)
            {
                s.Stop($"단계가 {config.MaxSteps}번을 넘었어요", now());
                break;
            }
            switch (s.Stage)
            {
                case PipelineState.StageKind.Branch:
                    await MakeBranch(s, io, now);
                    break;
                case PipelineState.StageKind.Push:
                    await Push(s, io, now);
                    break;
                default:
                    if (s.Role is not PipelineRole role) break;
                    if (!await RunRole(role, s, io, config, now)) return s;
                    break;
            }
            await io.Save(s.Copy());
        }
        if (s.Stage == PipelineState.StageKind.Stopped)
        {
            var why = s.Reason ?? "멈춤";
            await io.Helper(new[] { "event", "--notify", "--source", "jira", "--task", "pokemate-jira", "--event", "blocked", "--key", s.Key, "--title", s.Title, "--result", why });
            await io.Helper(new[] { "jira-state", "--key", s.Key, "--type", "기타", "--result", $"멈춤: {why}" });
            await io.Save(s.Copy());
        }
        return s;
    }

    static bool IsPath(string line) => line.StartsWith('/') || Paths.IsWindowsAbsolute(line);

    static async Task MakeBranch(PipelineState s, PipelineIO io, Func<DateTimeOffset> now)
    {
        if (s.Repo is not string repo || s.Branch is not string branch)
        {
            s.Stop("레포·브랜치가 없어요", now());
            return;
        }
        foreach (var b in s.Create)
        {
            var r = await io.Helper(new[] { "create-branch", "--repo", repo, "--branch", b.Branch, "--from", b.From });
            if (!r.Ok)
            {
                s.HandBack($"PokeMate 가 브랜치 {b.Branch} 를 만들지 못했다.\n{r.Output}\n개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now());
                return;
            }
        }
        var w = await io.Helper(new[] { "worktree-add", "--repo", repo, "--branch", branch });
        // 마지막으로 보이는 절대 경로 줄 (Windows 의 파이썬 출력은 CRLF 이고 앞에 경고 줄이 섞일 수 있다)
        var path = w.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(IsPath);
        if (!w.Ok || path == null)
        {
            s.HandBack($"PokeMate 가 작업 폴더를 만들지 못했다.\n{w.Output}\n개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now());
            return;
        }
        s.BranchReady(path, now());
    }

    static async Task Push(PipelineState s, PipelineIO io, Func<DateTimeOffset> now)
    {
        if (s.Worktree is not string w || s.CommitMessage is not string message)
        {
            s.HandBack("커밋 메시지나 작업 폴더가 없어 push 하지 못했다. 개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now());
            return;
        }
        var r = await io.Helper(new[] { "commit-push", "--worktree", w, "--message", message });
        if (r.Ok) s.PushDone(r.Output, now());
        else s.HandBack($"PokeMate 가 커밋·push 하지 못했다.\n{r.Output}\n개발 중단 댓글, blocked 알림, jira-state 를 한 뒤 stop 한다.", now());
    }

    /// 역할 한 번. 앱이 꺼져 끊겼으면 false (상태에 실행 중 기록이 남아 있다).
    static async Task<bool> RunRole(PipelineRole role, PipelineState s, PipelineIO io, PipelineConfig config, Func<DateTimeOffset> now)
    {
        if (!config.Prompts.ContainsKey(role))
        {
            s.Stop($"{role.Label()} 역할 프롬프트({PipelineConfig.PromptName(role)}.md)가 없어요", now());
            return true;
        }
        string? diffFile = null;
        if (role == PipelineRole.Reviewer)
        {
            string? diff = null;
            if (s.Worktree is string worktree) diff = await io.Diff(worktree);
            if (diff == null)
            {
                s.Stop("리뷰할 변경을 만들지 못했어요", now());
                return true;
            }
            diffFile = Path.Combine(config.Dir, $"{s.Key}.diff");
            try
            {
                Directory.CreateDirectory(config.Dir);
                System.IO.File.WriteAllText(diffFile, diff, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
        var sessionKey = PipelineRoleExtensions.SessionKey(s.Domain, role);
        var known = await io.SessionId(sessionKey);
        var access = PipelineAccess.Access(role, s.Worktree, config.Access);
        var dirs = access.Dirs.ToList();
        dirs.AddRange(config.Domains.KnowledgePaths(s.Domain, config.Home).Append(config.Notes.Root)
            .Where(d => !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList());
        access = new ClaudeCommand.WriteAccess(access.Tools, dirs);
        var interrupted = s.Inflight?.Role == role.Raw() ? s.Inflight : null;
        PipelineRoleResult result;
        string usedId;
        if (interrupted != null)
        {
            var text = InterruptedNote + "\n\n" + Prompt(role, s, config, true, diffFile);
            result = await io.RunRole(role, text, interrupted.SessionId, null, access);
            usedId = interrupted.SessionId;
            if (result.Interrupted) return false;
            if (result.Text == null)
            {
                s.Costs[role.Raw()] = s.Costs.GetValueOrDefault(role.Raw()) + (result.Usage?.CostUSD ?? 0);
                var fresh = Guid.NewGuid().ToString().ToLowerInvariant();
                s.Inflight = new PipelineState.InflightRun(role.Raw(), fresh, now());
                await io.Save(s.Copy());
                var again = InterruptedNote + "\n\n" + Prompt(role, s, config, false, diffFile);
                result = await io.RunRole(role, again, null, fresh, access);
                usedId = fresh;
            }
        }
        else
        {
            var fresh = known == null ? Guid.NewGuid().ToString().ToLowerInvariant() : null;
            usedId = known ?? fresh ?? "";
            s.Inflight = new PipelineState.InflightRun(role.Raw(), usedId, now());
            await io.Save(s.Copy());
            var text = Prompt(role, s, config, known != null, diffFile);
            result = await io.RunRole(role, text, known, fresh, access);
        }
        if (result.Interrupted) return false;
        s.Inflight = null;
        var id = result.SessionId ?? (usedId.Length == 0 ? null : usedId);
        if (!string.IsNullOrEmpty(id)) await io.RecordSession(sessionKey, id, s.Key, result.Usage);
        s.Costs[role.Raw()] = s.Costs.GetValueOrDefault(role.Raw()) + (result.Usage?.CostUSD ?? 0);
        if (result.Failure != null || result.Text is not string answer)
        {
            s.Stop($"{role.Label()} 실행 실패: {result.Failure ?? "답 없음"}", now());
            return true;
        }
        var testerBefore = s.TesterBacks;
        var reviewerBefore = s.ReviewerBacks;
        s.Apply(role, PipelineNext.Extract(answer), answer, now());
        if (s.TesterBacks > testerBefore || s.ReviewerBacks > reviewerBefore)
            await BounceNotice(s, io, tester: s.TesterBacks > testerBefore);
        return true;
    }

    static async Task BounceNotice(PipelineState s, PipelineIO io, bool tester)
    {
        var which = tester ? "테스터→개발" : "리뷰어→개발";
        var count = tester ? s.TesterBacks : s.ReviewerBacks;
        var why = SwiftText.Prefix(s.History.LastOrDefault()?.Note ?? "", 200);
        var over = count > PipelineState.BackLimit;
        var result = over ? $"{which} 되돌림 상한 {PipelineState.BackLimit}번 넘음 → PM 이 멈춤 처리: {why}"
                          : $"{which} 되돌림 {count}/{PipelineState.BackLimit}: {why}";
        await io.Helper(new[] { "event", "--notify", "--source", "jira", "--task", "pokemate-jira", "--event", "changed", "--key", s.Key, "--title", s.Title, "--kind", "핑퐁", "--result", result });
        await io.Notice(over ? $"핑퐁 상한 초과 · {s.Key}" : $"핑퐁 {which} {count}/{PipelineState.BackLimit} · {s.Key}", result);
    }
}
