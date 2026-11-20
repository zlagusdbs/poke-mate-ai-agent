using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 리뷰할 PR 하나 (`https://호스트/{owner}/{repo}/pull/{번호}`)
public sealed record ReviewTarget(string Owner, string Repo, int Number, string Url)
{
    public string FullName => $"{Owner}/{Repo}";
    public string Label => $"{Repo}#{Number}";

    /// 주소에서 읽는다. `…/pull/번호` 꼴이 아니면 null (뒤에 `/files`·`?x=1` 이 붙어도 됨).
    public static ReviewTarget? Parse(string url)
    {
        var path = url;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var slash = path.IndexOf('/', scheme + 3);
            path = slash < 0 ? "" : path[slash..];
        }
        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) path = path[..cut];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (parts.Count < 4 || parts[2] != "pull" || !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return null;
        return new ReviewTarget(parts[0], parts[1], number, url);
    }
}

/// 레포마다 정해 둔 도메인 (PR 내용으로 정한 것). `review-domains.json` 의 한 칸.
public sealed record ReviewDomainChoice(string Domain, string Why, DateTimeOffset At);

/// `automation\state\review-domains.json`: `{owner}/{repo}` → 정한 도메인. 날짜는 ISO 8601, 키 정렬. 깨졌거나 없으면 빈 값.
public sealed record ReviewDomainBook
{
    public Dictionary<string, ReviewDomainChoice> Repos { get; set; } = new();

    public ReviewDomainBook() { }

    public ReviewDomainBook(IDictionary<string, ReviewDomainChoice> repos) => Repos = new(repos);

    public static string File => Path.Combine(AppPaths.DataDir, "automation", "state", "review-domains.json");

    public static ReviewDomainBook Load(string? from = null)
    {
        try
        {
            var book = JsonSerializer.Deserialize<ReviewDomainBook>(Paths.ReadShared(from ?? File), Json.Iso8601);
            if (book == null) return new ReviewDomainBook();
            book.Repos ??= new();
            return book.Repos.Values.Any(c => c == null) ? new ReviewDomainBook() : book;
        }
        catch { return new ReviewDomainBook(); }
    }

    /// 실패하면 false (이전 파일은 그대로)
    public bool Save(string? to = null)
    {
        try
        {
            AppPaths.WriteAtomic(to ?? File, Json.SortedPretty(this, Json.Iso8601));
            return true;
        }
        catch { return false; }
    }

    public bool Equals(ReviewDomainBook? o) =>
        o is not null && Repos.Count == o.Repos.Count && Repos.All(kv => o.Repos.TryGetValue(kv.Key, out var other) && kv.Value == other);

    public override int GetHashCode() => Repos.Count;
}

/// 맥의 `DomainReview.Source` 의 `rawValue` (사람에게 보이는 글)
public static class DomainReviewSourceExtensions
{
    public static string Raw(this DomainReview.Source source) => source switch
    {
        DomainReview.Source.Explicit => "domains.json 의 repos",
        DomainReview.Source.Learned => "예전에 정한 기록",
        _ => "이번에 PR 내용으로 정함",
    };
}

/// PR 리뷰를 도메인 리뷰어 세션(`{도메인}:reviewer`)에 맡기는 흐름: 도메인 정하기와 리뷰 프롬프트.
public static class DomainReview
{
    public const string Tag = "pokemate-domain";
    public const string Fallback = DomainMap.DefaultKey;

    /// 도메인을 어디서 알았나
    public enum Source { Explicit, Learned, Chosen }

    static readonly Regex Block = new("```" + Tag + @"[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);

    public static bool Valid(string domain, DomainMap map) => domain == Fallback || map.Domains.ContainsKey(domain);

    /// 이미 아는 도메인: domains.json 의 repos → 예전에 정해 둔 기록(아직 있는 도메인일 때). 모르면 null (도메인 정하기로).
    public static (string Domain, string Why, Source Source)? Known(ReviewTarget target, DomainMap map, ReviewDomainBook book)
    {
        var domain = map.Domains.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(k => map.Domains[k].Contains(target.Repo));
        if (domain != null) return (domain, $"domains.json 의 {domain} repos 에 {target.Repo} 가 있음", Source.Explicit);
        if (book.Repos.TryGetValue(target.FullName, out var learned) && Valid(learned.Domain, map)) return (learned.Domain, learned.Why, Source.Learned);
        return null;
    }

    /// 리뷰하지 않고 도메인만 정하게 하는 프롬프트 (읽기만)
    public static string ChoosePrompt(ReviewTarget target, DomainMap map, string helper)
    {
        var lines = map.Domains.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(name =>
        {
            var parts = new List<string> { $"- {name}" };
            if (map.Descriptions.TryGetValue(name, out var text)) parts.Add(text);
            if (map.Keywords.TryGetValue(name, out var words) && words.Count > 0) parts.Add("키워드: " + string.Join(", ", words));
            if (map.Domains.TryGetValue(name, out var repos) && repos.Count > 0) parts.Add("레포: " + string.Join(", ", repos));
            return string.Join(" · ", parts);
        }).Append($"- {Fallback} · 위 어느 도메인에도 맞지 않을 때");
        return string.Join("\n",
            "지금은 리뷰하지 말고, 아래 PR 이 어느 도메인 일인지만 정한다. 읽기만 한다(GitHub·Jira 쓰기, 알림, 파일 쓰기 금지).",
            $"- PR: {target.Url}",
            "- 볼 것 (명령은 한 번에 하나씩, 전체 명령 그대로):",
            $"  `{helper} gh-get repos/{target.FullName}/pulls/{target.Number} --jq '{{title,body}}'`",
            $"  `{helper} gh-get repos/{target.FullName}/pulls/{target.Number}/files --jq '[.[].filename]'`",
            $"  제목·본문에 Jira 키가 있으면 `{helper} jira-get --key {{KEY}}` 의 요약",
            "- 도메인 목록:",
            string.Join("\n", lines),
            $"PR 의 제목·본문·파일 경로·Jira 요약에 나온 단어가 어느 도메인의 설명·키워드·레포와 맞는지로 고른다. 애매하면 더 많이 맞는 쪽, 하나도 안 맞으면 {Fallback}.",
            "답 끝에 아래 블록 하나만 붙인다.",
            "```" + Tag,
            "{\"domain\": \"도메인 이름\", \"why\": \"고른 이유 한 줄 (어떤 단어·파일이 어느 설명·키워드와 맞는지)\"}",
            "```");
    }

    /// 도메인 정하기 답에서 마지막 블록을 읽는다. 없는 도메인이면 null (default 는 됨).
    public static (string Domain, string Why)? Extract(string text, DomainMap map)
    {
        var m = Block.Matches(text).LastOrDefault();
        if (m == null || Json.ParseObject(m.Groups[1].Value) is not JsonObject obj) return null;
        if (Json.Str(obj["domain"]) is not string raw) return null;
        var domain = SwiftText.TrimSpaces(raw);
        if (!Valid(domain, map)) return null;
        var why = SwiftText.Trim(Json.Str(obj["why"]) ?? "");
        return (domain, why.Length == 0 ? "이유 없음" : why);
    }

    public static string SessionKey(string domain) => PipelineRoleExtensions.SessionKey(domain, PipelineRole.Reviewer);

    public static string ReviewPrompt(ReviewTarget target, string domain, string why, Source source, bool firstRun, DomainNotes notes, string helper, string context)
    {
        var session = SessionKey(domain);
        var start = firstRun
            ? $"이 세션은 도메인 {domain} 의 리뷰어 세션이다(Jira 역할별 처리의 리뷰어와 같은 세션). 이번이 첫 실행이니 domain-review 스킬 1절의 지식을 처음부터 읽는다."
            : $"이 세션은 도메인 {domain} 의 리뷰어 세션으로 이전 리뷰·처리를 해 왔다. 이미 읽은 점검표·노트·지식은 다시 읽지 말고 이번 PR 에 새로 필요한 것만 찾는다. 점검표 답은 이번 PR 기준으로 새로 단다.";
        return string.Join("\n",
            $"이번 건: {target.Label} 리뷰 · 도메인 {domain} ({source.Raw()}: {why}) · 세션 {session}",
            start,
            notes.PromptLines(domain, helper, target.Label),
            $"- 리뷰 본문 \"확인한 범위\" 첫 줄에 \"도메인: {domain} — {why}\" 를 적고, 시작 알림 result 끝에도 \" · 도메인 {domain}\" 을 붙인다.",
            $"- 주변 코드: 로컬 레포 폴더(~/Workspace/{target.Repo})가 없으면 `{helper} gh-file --repo {target.FullName} --ref {{head sha}} --path {{파일 경로}}` 로 읽는다(이 PR 저장소만 된다). 그래도 못 읽으면 \"확인한 범위\" 에 적는다.",
            "",
            context);
    }
}

/// 도메인별 공유 노트 폴더 (`…\references\notes\{도메인}\checklist.md`·`notes.md`·`maps\`). 점검표·노트를 읽고 남기는 방법을 프롬프트에 안내한다.
/// 안내에 적는 경로는 항상 `/` 구분이다 (Git Bash·Claude 가 그대로 읽는다). 권한용 폴더(`Root`)는 Windows 경로 그대로.
public sealed record DomainNotes
{
    public const string DefaultRoot = "~/.claude/references/notes";
    public static readonly IReadOnlyList<string> Kinds = new[] { "용어집", "업무 규칙", "장애", "함정", "결정", "동작" };
    public const int MapLimit = 20;

    public string Root { get; }
    /// 파일의 줄 수 (없으면 null)
    public Func<string, int?> Lines { get; }
    /// 폴더의 지도 주제 이름들
    public Func<string, List<string>> Maps { get; }

    public DomainNotes(string root, Func<string, int?>? lines = null, Func<string, List<string>>? maps = null)
    {
        Root = root;
        Lines = lines ?? CountLines;
        Maps = maps ?? ListMaps;
    }

    public bool Equals(DomainNotes? o) => o is not null && Root == o.Root;

    public override int GetHashCode() => Root.GetHashCode();

    /// 폴더의 `.md` 파일 이름(확장자 뺀 것)을 이름순으로. 숨김(`.`으로 시작)은 뺀다. 폴더가 없으면 빈 목록.
    public static List<string> ListMaps(string folder)
    {
        try
        {
            return Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>()
                .Where(n => n.EndsWith(".md", StringComparison.Ordinal) && !n.StartsWith('.'))
                .Select(n => n[..^3])
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// 줄 수 (줄바꿈 수 + 1, CRLF 도 같음). 읽을 수 없으면 null.
    public static int? CountLines(string path)
    {
        try { return System.IO.File.ReadAllText(path).Split('\n').Length; }
        catch { return null; }
    }

    /// 노트 폴더: `connections.json` 의 automation 설정 `domainNotesDir`, 없으면 `~/.claude/references/notes`. `~/` 는 홈으로.
    public static string RootFromConnections(string? connectionsJson, string? home = null)
    {
        var rows = Json.ParseArray(connectionsJson);
        string? configured = null;
        if (rows != null && rows.All(r => r is JsonObject))
            configured = Json.Str(rows.Cast<JsonObject>().FirstOrDefault(r => Json.Str(r["kind"]) == "settings" && Json.Str(r["id"]) == "automation")?["domainNotesDir"]);
        var raw = configured == null || SwiftText.TrimSpaces(configured).Length == 0 ? DefaultRoot : SwiftText.TrimSpaces(configured);
        return Paths.ExpandTilde(raw, home);
    }

    /// 환경 변수 `POKEMATE_NOTES_DIR` 가 있으면 그 폴더, 없으면 연결 설정의 폴더 (링크는 실제 경로로).
    public static DomainNotes Load(string? repoDir = null, string? home = null)
    {
        var overridden = Environment.GetEnvironmentVariable("POKEMATE_NOTES_DIR");
        if (!string.IsNullOrEmpty(overridden)) return new DomainNotes(Resolved(overridden));
        repoDir ??= AppPaths.LoadSettings().RepoDir;
        string? rows = null;
        try { rows = System.IO.File.ReadAllText(LocalNames.File(repoDir)); } catch { }
        return new DomainNotes(Resolved(RootFromConnections(rows, home)));
    }

    /// 경로 안의 링크(디렉터리 정션·심볼릭 링크)를 실제 경로로 푼다. Claude 의 권한은 실제 경로로 판단한다. 없는 경로는 그대로.
    public static string Resolved(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
            }
            return current;
        }
        catch { return path; }
    }

    string Folder(string domain) => $"{Paths.ForwardSlashes(Root).TrimEnd('/')}/{domain}";

    public string Checklist(string domain) => Folder(domain) + "/checklist.md";
    public string NotesFile(string domain) => Folder(domain) + "/notes.md";
    public string MapsFolder(string domain) => Folder(domain) + "/maps";

    /// 역할·리뷰 프롬프트에 넣는 노트 안내. 도메인이 default 면 점검표·노트가 없다는 안내.
    /// `canWrite` 가 false 면 읽기만 (개발 역할).
    public string PromptLines(string domain, string helper, string source, bool canWrite = true)
    {
        if (domain == DomainMap.DefaultKey)
            return $"- 도메인 노트: 도메인을 정하지 못해({domain}) 점검표·노트가 없다. 결과 문서 점검표 절에 \"도메인 미정, 점검표 없음\" 이라고 적고, 노트는 남기지 않는다.";
        string Describe(string path) => Lines(path) is int n ? $"{n}줄" : "없음";
        var checklist = Checklist(domain);
        var notes = NotesFile(domain);
        var missing = Lines(checklist) == null || Lines(notes) == null;
        var output = new List<string>
        {
            $"- 도메인 노트: 점검표 `{checklist}` ({Describe(checklist)}), 노트 `{notes}` ({Describe(notes)}). domain-review 스킬이 말하는 `~/.claude/references/notes/{{도메인}}/` 은 이 경로다. 노트는 이번 건 키워드로 Grep 한다.",
        };
        var topics = Maps(MapsFolder(domain));
        if (topics.Count > 0)
        {
            var shown = string.Join(", ", topics.Take(MapLimit));
            var more = topics.Count > MapLimit ? $" 외 {topics.Count - MapLimit}개" : "";
            output.Add($"- 지도: `{MapsFolder(domain)}/` 아래 {shown}{more}. 이번 건 주제와 맞는 지도가 있으면 코드를 찾기 전에 먼저 읽는다.");
        }
        if (!canWrite)
        {
            output.Add($"- 노트·점검표는 읽기만 한다(쓰기는 PM·리뷰어가 한다).{(missing ? " 아직 없으면 점검표 없이 진행하고 지시서의 점검표 답을 따른다." : "")}");
            return string.Join("\n", output);
        }
        if (missing)
            output.Add($"- 점검표·노트가 없다: 먼저 `{helper} domain-note --domain {domain} --init` 로 빈 틀을 만들고, 결과 문서와 끝 알림 result 에 \"이 도메인의 점검표가 아직 없음\" 을 적는다.");
        output.Add($"- 노트 남기기(스킬 7절): 노트 파일을 직접 고치지 않는다(막혀 있다). `{helper} domain-note --domain {domain} --kind {{{string.Join("|", Kinds)}}} --source \"{source}\" --text \"사실 (근거: 파일 경로:줄 · 위키 pageId · 티켓 키)\"` 로 한 줄씩 남긴다. 추측·미확인·이번 건에만 해당하는 내용은 쓰지 않는다.");
        return string.Join("\n", output);
    }
}
