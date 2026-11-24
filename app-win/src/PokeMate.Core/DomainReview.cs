using System.Globalization;
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

/// 도메인 정하기 3단계(PR 판단)에 넘기는 읽을거리. `Files` 는 바뀐 파일, `SharedDocs` 는 팀 KB 공용 문서(`/` 구분 전체 경로),
/// `MapDomains` 는 1단계(팀 KB 지도·domains.json repos)에서 나온 도메인들, `RuleCounts` 는 2단계 경로 규칙의 도메인 → 파일 수.
public sealed record RouteHints(IReadOnlyList<string> Files, IReadOnlyList<string> SharedDocs, IReadOnlyList<string> MapDomains, IReadOnlyDictionary<string, int> RuleCounts);

/// 맥의 `DomainReview.Source` 의 `rawValue` (근거 줄의 단계 이름)
public static class DomainReviewSourceExtensions
{
    public static string Raw(this DomainReview.Source source) => source switch
    {
        DomainReview.Source.TeamMap => "팀 KB 지도",
        DomainReview.Source.Listed => "domains.json 의 repos",
        DomainReview.Source.PathRule => "경로 규칙",
        DomainReview.Source.Chosen => "PR 판단",
        _ => "기본값",
    };
}

/// PR 리뷰를 도메인 리뷰어 세션(`{도메인}:reviewer`)에 맡기는 흐름: 도메인 정하기와 리뷰 프롬프트.
/// 도메인 정하기: 1. 팀 KB 지도·domains.json repos → 2. 경로 규칙 → 3. PR 판단(Claude) → default. 결과는 저장하지 않는다.
public static class DomainReview
{
    public const string Tag = "pokemate-domain";
    public const string Fallback = DomainMap.DefaultKey;
    /// 3단계 안내에 넣는 바뀐 파일 수 상한
    public const int FileLimit = 200;

    /// 도메인을 어디서 알았나 (단계)
    public enum Source { TeamMap, Listed, PathRule, Chosen, Fallback }

    static readonly Regex Block = new("```" + Tag + @"[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);

    /// 지도 전체 기준 (평가 문제의 고정 도메인 확인용)
    public static bool Valid(string domain, DomainMap map) => domain == Fallback || map.Domains.ContainsKey(domain);

    /// 도메인 후보: 내 팀들의 도메인(`TeamConfig.Candidates`), 팀 설정이 없으면 domains.json 의 모든 도메인(이름순). default 는 따로.
    public static IReadOnlyList<string> Candidates(DomainMap map, TeamConfig? team) =>
        team?.Candidates(map) ?? map.Domains.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// domains.json 의 repos 에 이 레포가 있는 도메인 (이름순 첫 번째, 레포 이름은 대소문자 무시). 평가용.
    public static (string Domain, string Why, Source Source)? Listed(ReviewTarget target, DomainMap map) =>
        ListedHits(target, map, map.Domains.Keys.OrderBy(k => k, StringComparer.Ordinal)).Select(h => ((string, string, Source)?)h).FirstOrDefault();

    static IEnumerable<(string Domain, string Why, Source Source)> ListedHits(ReviewTarget target, DomainMap map, IEnumerable<string> candidates) =>
        candidates.Where(d => map.Domains.TryGetValue(d, out var repos) && repos.Any(r => string.Equals(r, target.Repo, StringComparison.OrdinalIgnoreCase)))
            .Select(d => (d, $"domains.json 의 {d} repos 에 {target.Repo} 가 있음", Source.Listed));

    /// 1단계. 내 팀마다 `repoMap` 의 `{domain}` 자리 폴더를 훑어 `{repo}` 문서를 대소문자 무시로 찾는다.
    /// 폴더가 그 팀의 후보 도메인이면 Hit(`TeamMap`), 아니면 그 문서를 공용 문서(`SharedDocs`, `/` 구분 전체 경로)로 넘긴다. 팀 KB 폴더가 없으면 건너뛴다.
    /// 쓸 수 있는 팀 도메인(domains.json 에 있는 것)이 하나도 없으면 팀 KB 는 훑지 않는다.
    /// 이어서 후보 도메인의 domains.json repos 에 레포가 있으면 Hit(`Listed`).
    public static (List<(string Domain, string Why, Source Source)> Hits, List<string> SharedDocs) MapLookup(ReviewTarget target, DomainMap map, TeamConfig? team,
                                                                                                         IReadOnlyList<string> candidates, string? home = null)
    {
        home ??= Paths.Home;
        var hits = new List<(string Domain, string Why, Source Source)>();
        var shared = new List<string>();
        // 쓸 수 있는 팀(domains.json 에 있는 도메인을 가진 팀)이 하나도 없으면 팀 KB 지도는 건너뛰고 domains.json repos 만 본다 (스펙 4-2)
        var mine = team?.Mine() ?? new List<(string Team, TeamEntry Entry)>();
        if (!mine.Any(m => m.Entry.Domains.Any(map.Domains.ContainsKey))) mine.Clear();
        foreach (var (name, entry) in mine)
        {
            if (entry.RepoMap is not string pattern) continue;
            foreach (var doc in RepoDocs(DomainMap.ResolvePath(entry.Kb, home), pattern, target.Repo))
            {
                var domain = entry.Domains.FirstOrDefault(d => candidates.Contains(d) && string.Equals(d, doc.Folder, StringComparison.OrdinalIgnoreCase));
                if (domain != null) hits.Add((domain, $"{name} 팀 KB {doc.Relative}", Source.TeamMap));
                else if (!shared.Contains(doc.Full)) shared.Add(doc.Full);
            }
        }
        hits.AddRange(ListedHits(target, map, candidates));
        return (hits, shared);
    }

    /// 팀 KB 에서 `pattern` 의 `{domain}` 자리 폴더(이름순)마다 `{repo}` 문서를 찾는다. 경로 조각은 대소문자 무시, 결과는 실제 이름.
    /// `Folder` 는 `{domain}` 자리 값, `Relative` 는 팀 KB 안의 `/` 구분 경로. `{domain}` 이 없는 패턴·없는 폴더는 빈 목록.
    static List<(string Folder, string Relative, string Full)> RepoDocs(string kb, string pattern, string repo)
    {
        var found = new List<(string Folder, string Relative, string Full)>();
        var parts = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
        var at = Array.FindIndex(parts, p => p.Contains("{domain}", StringComparison.Ordinal));
        if (at < 0 || at == parts.Length - 1) return found;
        try
        {
            var dir = kb;
            var head = new List<string>();
            for (var i = 0; i < at; i++)
            {
                if (Child(dir, parts[i].Replace("{repo}", repo), file: false) is not string name) return found;
                dir = Path.Combine(dir, name);
                head.Add(name);
            }
            var slot = parts[at];
            var k = slot.IndexOf("{domain}", StringComparison.Ordinal);
            var pre = slot[..k].Replace("{repo}", repo);
            var post = slot[(k + "{domain}".Length)..].Replace("{repo}", repo);
            foreach (var folder in Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal))
            {
                if (folder.Length <= pre.Length + post.Length || !folder.StartsWith(pre, StringComparison.OrdinalIgnoreCase)
                    || !folder.EndsWith(post, StringComparison.OrdinalIgnoreCase)) continue;
                var domain = folder[pre.Length..(folder.Length - post.Length)];
                var current = Path.Combine(dir, folder);
                var relative = new List<string>(head) { folder };
                var ok = true;
                for (var j = at + 1; j < parts.Length && ok; j++)
                {
                    var want = parts[j].Replace("{repo}", repo).Replace("{domain}", domain);
                    if (Child(current, want, file: j == parts.Length - 1) is string name)
                    {
                        current = Path.Combine(current, name);
                        relative.Add(name);
                    }
                    else ok = false;
                }
                if (ok) found.Add((domain, string.Join("/", relative), Paths.ForwardSlashes(current)));
            }
        }
        catch { }
        return found;
    }

    /// 폴더 안에서 이름이 대소문자 무시로 같은 파일(또는 폴더)의 실제 이름. 없으면 null.
    static string? Child(string dir, string name, bool file)
    {
        try
        {
            return (file ? Directory.GetFiles(dir) : Directory.GetDirectories(dir)).Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// 1·2단계로 도메인을 정한다. 1단계 도메인이 정확히 하나면 첫 Hit, 아니면 경로 규칙이 한 도메인이면 `PathRule`.
    /// 못 정하면 `Decided` 는 null 이고 `Hints` 를 3단계(`ChoosePrompt`)에 넘긴다.
    public static ((string Domain, string Why, Source Source)? Decided, RouteHints Hints) Route(ReviewTarget target, DomainMap map, TeamConfig? team, PathRules rules,
                                                                                               IReadOnlyList<string> files, string? home = null)
    {
        var candidates = Candidates(map, team);
        var (hits, shared) = MapLookup(target, map, team, candidates, home);
        var domains = hits.Select(h => h.Domain).Distinct().ToList();
        if (domains.Count == 1) return (hits[0], new RouteHints(files, shared, domains, new Dictionary<string, int>()));
        var verdict = rules.Judge(target.FullName, files, candidates);
        var hints = new RouteHints(files, shared, domains, verdict.Counts);
        if (verdict.Domain is string domain) return ((domain, RuleWhy(rules, target.FullName, files, candidates, domain), Source.PathRule), hints);
        return (null, hints);
    }

    /// 경로 규칙 근거: 그 도메인 규칙 중 처음 맞은 파일이 가장 많은 규칙(같으면 앞 규칙)의 와일드카드 앞 폴더와 그 규칙의 파일 수.
    /// 예: `src/Areas/Item 아래 1개 파일`
    static string RuleWhy(PathRules rules, string fullName, IReadOnlyList<string> files, IReadOnlyCollection<string> candidates, string domain)
    {
        var active = rules.Repos.TryGetValue(fullName, out var all) ? all.Where(r => candidates.Contains(r.Domain)).ToList() : new List<PathRule>();
        var counts = new int[active.Count];
        foreach (var file in files)
        {
            var i = active.FindIndex(r => PathRules.Matches(r.Path, file));
            if (i >= 0) counts[i]++;
        }
        var best = -1;
        for (var i = 0; i < active.Count; i++)
            if (active[i].Domain == domain && (best < 0 || counts[i] > counts[best])) best = i;
        if (best < 0) return "";
        var glob = active[best].Path;
        return RuleFolder(glob) is string folder ? $"{folder} 아래 {counts[best]}개 파일" : $"{glob} 에 맞는 파일 {counts[best]}개";
    }

    /// glob 의 첫 와일드카드(`*`·`?`) 앞 폴더. 와일드카드 앞에 폴더가 없으면(`Coupon*/**`·`**/x.cs`) null.
    static string? RuleFolder(string glob)
    {
        var g = glob.Replace('\\', '/');
        while (g.StartsWith("./", StringComparison.Ordinal) || g.StartsWith("/", StringComparison.Ordinal)) g = g[(g[0] == '.' ? 2 : 1)..];
        var cut = g.IndexOfAny(new[] { '*', '?' });
        var head = cut < 0 ? g : g[..cut];
        var slash = head.LastIndexOf('/');
        return slash > 0 ? head[..slash] : null;
    }

    /// 3단계: 리뷰하지 않고 도메인만 정하게 하는 프롬프트 (읽기만). 후보는 `Candidates` 와 default.
    public static string ChoosePrompt(ReviewTarget target, DomainMap map, TeamConfig? team, RouteHints hints, string helper)
    {
        var lines = Candidates(map, team).Select(name =>
        {
            var parts = new List<string> { team?.TeamOf(name) is string owner ? $"- {name} ({owner} 팀)" : $"- {name}" };
            if (map.Descriptions.TryGetValue(name, out var text)) parts.Add(text);
            if (map.Keywords.TryGetValue(name, out var words) && words.Count > 0) parts.Add("키워드: " + string.Join(", ", words));
            return string.Join(" · ", parts);
        }).Append($"- {Fallback} · 위 어느 도메인에도 맞지 않을 때");
        var output = new List<string>
        {
            "지금은 리뷰하지 말고, 아래 PR 이 어느 도메인 일인지만 정한다. 읽기만 한다(GitHub·Jira 쓰기, 알림, 파일 쓰기 금지).",
            $"- PR: {target.Url}",
            "- 볼 것 (명령은 한 번에 하나씩, 전체 명령 그대로):",
            $"  `{helper} gh-get repos/{target.FullName}/pulls/{target.Number} --jq '{{title,body}}'`",
            $"  제목·본문에 Jira 키가 있으면 `{helper} jira-get --key {{KEY}}` 의 요약",
        };
        if (hints.Files.Count == 0)
            output.Add($"  `{helper} gh-get repos/{target.FullName}/pulls/{target.Number}/files --jq '[.[].filename]'`");
        if (team?.RepoGuide is string guide)
            output.Add($"  전사 레포 지도의 이 레포 문서(없으면 건너뛴다): `{helper} gh-get repos/{guide}/contents/maps/{target.Owner}/{target.Repo}.md --jq '.content | @base64d'`");
        output.AddRange(hints.SharedDocs.Select(doc => $"  팀 KB 공용 문서: `{Paths.ForwardSlashes(doc)}`"));
        if (hints.Files.Count > 0)
        {
            output.Add($"- 바뀐 파일 ({hints.Files.Count}개):");
            output.AddRange(hints.Files.Take(FileLimit).Select(f => $"  - {f}"));
            if (hints.Files.Count > FileLimit) output.Add($"  - 외 {hints.Files.Count - FileLimit}개");
        }
        if (hints.RuleCounts.Count > 0)
            output.Add("- 경로 규칙 후보: " + string.Join(", ", hints.RuleCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}개")));
        if (hints.MapDomains.Count > 0)
            output.Add("- 팀 KB 지도 후보: " + string.Join(", ", hints.MapDomains));
        output.AddRange(new[]
        {
            "- 도메인 목록:",
            string.Join("\n", lines),
            $"PR 의 제목·본문·파일 경로·Jira 요약·레포 문서에 나온 단어가 어느 도메인의 설명·키워드와 맞는지로 고른다. 애매하면 더 많이 맞는 쪽, 하나도 안 맞으면 {Fallback}.",
            $"조회가 실패하면 레포 이름만으로 고르지 말고 {Fallback} 로 답한다.",
            "답 끝에 아래 블록 하나만 붙인다.",
            "```" + Tag,
            "{\"domain\": \"도메인 이름\", \"why\": \"고른 이유 한 줄 (어떤 단어·파일이 어느 설명·키워드와 맞는지)\"}",
            "```",
        });
        return string.Join("\n", output);
    }

    /// 도메인 정하기 답에서 마지막 블록을 읽는다. 후보가 아니면 null (default 는 됨).
    public static (string Domain, string Why)? Extract(string text, IReadOnlyCollection<string> candidates)
    {
        var m = Block.Matches(text).LastOrDefault();
        if (m == null || Json.ParseObject(m.Groups[1].Value) is not JsonObject obj) return null;
        if (Json.Str(obj["domain"]) is not string raw) return null;
        var domain = SwiftText.TrimSpaces(raw);
        if (domain != Fallback && !candidates.Contains(domain)) return null;
        var why = SwiftText.Trim(Json.Str(obj["why"]) ?? "");
        return (domain, why.Length == 0 ? "이유 없음" : why);
    }

    /// 도메인을 정하지 못했을 때의 근거 (3단계 실패·판정 실패)
    public const string Undecided = "도메인을 정하지 못해 " + Fallback + " 로 리뷰";

    /// 3단계 답(실행 실패면 null)으로 정한 도메인. 후보면 `Chosen`, default 면 `Fallback`(이유가 없으면 `Undecided`), 못 읽으면 default·`Undecided`.
    public static (string Domain, string Why, Source Source) Decide(string? answer, IReadOnlyCollection<string> candidates)
    {
        if (answer == null || Extract(answer, candidates) is not { } choice) return (Fallback, Undecided, Source.Fallback);
        if (choice.Domain != Fallback) return (choice.Domain, choice.Why, Source.Chosen);
        return (Fallback, choice.Why == "이유 없음" ? Undecided : choice.Why, Source.Fallback);
    }

    public static string SessionKey(string domain) => PipelineRoleExtensions.SessionKey(domain, PipelineRole.Reviewer);

    /// 리뷰어 프롬프트. 세션은 PR 한 건마다 따로다(`SessionPolicy`). `firstRun` 은 새 세션인지, `handoff` 는 새 세션일 때 붙이는 "이번 건 인계" 블록.
    public static string ReviewPrompt(ReviewTarget target, string domain, string why, Source source, bool firstRun, DomainNotes notes, string helper, string context,
                                      string? handoff = null)
    {
        var session = SessionKey(domain);
        var start = firstRun
            ? "이 세션은 이 PR 의 리뷰어 세션이다(PR 마다 따로 시작한다). 새 세션이니 domain-review 스킬 1절의 지식을 처음부터 읽는다."
            : "이 세션은 이 PR 의 리뷰어 세션으로 앞 차례 리뷰를 해 왔다. 이미 읽은 점검표·노트·지식은 다시 읽지 말고 이번 차례에 새로 필요한 것만 찾는다. 점검표 답은 지금 PR 기준으로 새로 단다.";
        if (firstRun && handoff != null) start += "\n\n" + handoff + "\n";
        return string.Join("\n",
            $"이번 건: {target.Label} 리뷰 · 도메인 {domain} ({source.Raw()}: {why}) · 세션 {session}",
            start,
            notes.PromptLines(domain, helper, target.Label),
            $"- 리뷰 본문 \"확인한 범위\" 첫 줄에 \"도메인: {domain} — {source.Raw()}: {why}\" 를 적고, 시작 알림 result 끝에도 \" · 도메인 {domain}\" 을 붙인다.",
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
