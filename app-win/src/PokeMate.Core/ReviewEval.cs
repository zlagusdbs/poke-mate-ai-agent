using System.Text.Json.Nodes;

namespace PokeMate.Core;

/// 리뷰 기준 문제 한 건: 실제 PR 주소(`Url`)이거나, 저장소에 둔 가상 레포 문제(`Fixture`, `shared\eval\fixtures\{이름}`).
public sealed record ReviewEvalCase(string Id, string Url, string? Domain = null, string? Fixture = null);

/// 가상 레포 문제의 `case.json`
public sealed record ReviewEvalFixture(string Key, string Title, string Repo, string Branch, string Brief, string Note);

/// 리뷰 기준 문제 묶음 평가 (`PokeMate.Eval`)의 로직: 읽기 전용 권한, 도메인 고르기, 프롬프트, 세션 이어 쓰기 기록.
public static class ReviewEval
{
    public static readonly IReadOnlyList<string> ReadOnlyCommands = new[] { "gh-get", "gh-diff", "gh-file", "git-read", "jira-get", "jira-comments" };
    public static readonly IReadOnlyList<string> Skills = new[] { "Skill(domain-review)", "Skill(code-review)" };
    public const string BlockTag = "pokemate-eval";

    static string[] ResultBlock() => new[]
    {
        "```" + BlockTag, "확인한 범위: …", "참고한 근거: …", "점검표: …", "결함: …", "미확인(질문): …", "버린 후보와 이유: …", "제안: …", "판정: 승인 | 댓글 | 확신할 수 없음", "```",
    };

    /// 실제 리뷰 프롬프트 뒤에 붙이는 평가 끝맺음 (PR 리뷰 문제)
    public static readonly string Tail = string.Join("\n", new[]
    {
        "## 평가 실행",
        "이번은 리뷰 기준 문제 평가 실행이다. 2절 리뷰만 domain-review 스킬대로 끝까지 한다.",
        "- PR 이 닫혔거나 머지됐어도, 이미 리뷰한 기록이 있어도 리뷰한다. 1절의 상태·requested_reviewers·review-state 확인과 시작 알림은 건너뛴다.",
        "- 3절 등록(write-note·gh-review), 끝 알림, review-state 저장, domain-note 는 하지 않는다(막혀 있다).",
        "- 답 끝에 스킬 6절 결과 문서 전체를 아래 블록 하나에 넣는다.",
    }.Concat(ResultBlock()));

    /// Jira 리뷰어 프롬프트 뒤에 붙이는 평가 끝맺음 (가상 레포 문제)
    public static readonly string PipelineTail = string.Join("\n", new[]
    {
        "## 평가 실행",
        "이번은 리뷰 기준 문제 평가 실행이다. 위 지시대로 domain-review 스킬로 끝까지 리뷰한다. domain-note 는 하지 않는다(막혀 있다).",
        "답 끝 블록 바로 앞에 스킬 6절 결과 문서 전체를 아래 블록 하나에 넣는다.",
    }.Concat(ResultBlock()));

    /// 문제 묶음(JSON 배열)을 읽는다. id 가 없는 것, 주소가 PR 이 아닌 것, 경로를 넘는 가상 레포 이름(`/`·`\`·`:`·`.` 시작)은 뺀다.
    public static List<ReviewEvalCase> LoadCases(string json)
    {
        var cases = new List<ReviewEvalCase>();
        if (Json.ParseArray(json) is not JsonArray rows || rows.Any(r => r is not JsonObject)) return cases;
        foreach (var row in rows.Cast<JsonObject>())
        {
            var id = SwiftText.TrimSpaces(Json.Str(row["id"]) ?? "");
            if (id.Length == 0) continue;
            var domain = Json.Str(row["domain"]) is string d ? SwiftText.TrimSpaces(d) : null;
            var fixture = Json.Str(row["fixture"]) is string f ? SwiftText.TrimSpaces(f) : null;
            if (!string.IsNullOrEmpty(fixture))
            {
                if (fixture.Contains('/') || fixture.Contains('\\') || fixture.Contains(':') || fixture.StartsWith('.')) continue;
                cases.Add(new ReviewEvalCase(id, "", domain?.Length == 0 ? null : domain, fixture));
                continue;
            }
            if (Json.Str(row["url"]) is not string url || ReviewTarget.Parse(url) == null) continue;
            cases.Add(new ReviewEvalCase(id, url, domain?.Length == 0 ? null : domain));
        }
        return cases;
    }

    /// 읽기 명령과 스킬만 주는 권한 (쓰기 도구·알림·domain-note 없음). 같은 폴더는 한 번만.
    public static ClaudeCommand.WriteAccess Access(string helper, IEnumerable<string> dirs)
    {
        var unique = new List<string>();
        foreach (var dir in dirs)
            if (!unique.Contains(dir, StringComparer.OrdinalIgnoreCase)) unique.Add(dir);
        return new ClaudeCommand.WriteAccess(ReadOnlyCommands.Select(c => $"Bash({helper} {c}:*)").Concat(Skills).ToList(), unique);
    }

    /// 평가 실행의 환경 변수 (서버가 쓰기 명령을 막는다)
    public static Dictionary<string, string> Environment(ReviewTarget target) =>
        new() { ["POKEMATE_EVAL"] = "1", ["POKEMATE_REVIEW_REPO"] = target.FullName };

    /// 도메인: 문제에 적은 것 → domains.json 의 repos → default
    public static (string Domain, string Why, DomainReview.Source Source) Domain(ReviewEvalCase item, ReviewTarget target, DomainMap map)
    {
        if (item.Domain is string fixedDomain && DomainReview.Valid(fixedDomain, map)) return (fixedDomain, "평가 문제에 정한 도메인", DomainReview.Source.Chosen);
        if (DomainReview.Listed(target, map) is { } hit) return hit;
        return (DomainReview.Fallback, "평가 문제에 도메인이 없고 domains.json 의 repos 에도 없음", DomainReview.Source.Fallback);
    }

    public static string Prompt(ReviewEvalCase item, ReviewTarget target, string domain, string why, DomainReview.Source source, DomainNotes notes,
                                string helper, string template, string knowledge, bool firstRun = true)
    {
        var evt = $"다음 이벤트가 생겼다.\n- [리뷰 요청] {target.Label}\n- 평가 문제 {item.Id}\n- 링크: {item.Url}";
        var body = template.Replace(DomainMap.Placeholder, knowledge).Replace("{{helper}}", helper);
        return DomainReview.ReviewPrompt(target, domain, why, source, firstRun, notes, helper, $"{evt}\n\n요청: {body}\n\n{Tail}");
    }

    /// 가상 레포 문제 폴더 (`local`, `shared` 의 `eval\fixtures\{이름}`, `case.json` 이 있는 첫 곳)
    public static string? FixtureDir(string name, string? local = null, string? shared = null) =>
        new[] { local ?? AppPaths.LocalDir, shared ?? AppPaths.SharedDir }
            .Select(root => Path.Combine(root, "eval", "fixtures", name))
            .FirstOrDefault(dir => System.IO.File.Exists(Path.Combine(dir, "case.json")));

    /// `case.json` 읽기. 없거나 깨졌거나 빠진 값이 있으면 null.
    public static ReviewEvalFixture? LoadFixture(string dir)
    {
        try
        {
            var fixture = Json.Deserialize<ReviewEvalFixture>(System.IO.File.ReadAllText(Path.Combine(dir, "case.json")));
            return fixture is { Key: not null, Title: not null, Repo: not null, Branch: not null, Brief: not null, Note: not null } ? fixture : null;
        }
        catch { return null; }
    }

    public static string FixtureDomain(ReviewEvalCase item, ReviewEvalFixture fixture, DomainMap map) =>
        item.Domain is string fixedDomain && DomainReview.Valid(fixedDomain, map) ? fixedDomain : map.Domain(fixture.Repo);

    /// 가상 레포 문제는 리뷰어 단계에서 시작하는 Jira 상태로 만든다
    public static PipelineState FixtureState(ReviewEvalFixture fixture, string domain, string worktree, DateTimeOffset now)
    {
        var evt = $"다음 이벤트가 생겼다.\n- [{fixture.Key}] {fixture.Title}\n- 나에게 할당된 이슈\n- 링크: https://jira.example.com/browse/{fixture.Key}";
        return new PipelineState(fixture.Key, fixture.Title, evt, domain, fixture.Repo, 40, now)
        {
            Branch = fixture.Branch,
            Worktree = worktree,
            Brief = fixture.Brief,
            Stage = PipelineState.StageKind.Reviewer,
            TestsPassed = true,
            From = PipelineRole.Pm.Raw(),
            Note = fixture.Note,
        };
    }

    /// 가상 레포 문제의 리뷰어 권한: 읽기·스킬만 (domain-note·domain-map·쓰기 없음), 폴더는 `dirs` 와 작업 폴더
    public static ClaudeCommand.WriteAccess ReviewerAccess(string helper, string worktree, IReadOnlyList<string> dirs)
    {
        var table = PipelineAccess.Defaults(helper, dirs.Count > 0 ? dirs[0] : worktree, dirs.Count > 1 ? dirs[1] : worktree, worktree);
        var access = PipelineAccess.Access(PipelineRole.Reviewer, worktree, table);
        var tools = access.Tools.Where(t => !t.Contains(" domain-note:") && !t.Contains(" domain-map:")).ToList();
        var all = access.Dirs.ToList();
        foreach (var dir in dirs.Append(worktree))
            if (!all.Contains(dir, StringComparer.OrdinalIgnoreCase)) all.Add(dir);
        return new ClaudeCommand.WriteAccess(tools, all);
    }

    /// 평가 인자의 세션 이어 쓰기 규칙: `always` · `default` · `{분},{토큰}`. 그 밖은 null.
    public static SessionPolicy? Policy(string text)
    {
        switch (text)
        {
            case "always": return SessionPolicy.Always;
            case "default": return new SessionPolicy();
            default:
                var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => long.TryParse(SwiftText.TrimSpaces(p), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? (long?)n : null)
                    .OfType<long>().ToList();
                return parts.Count == 2 && parts.All(n => n >= 0) ? new SessionPolicy((int)Math.Min(parts[0], int.MaxValue), parts[1]) : null;
        }
    }

    /// `--reuse` 로 돌릴 때 문제 하나의 세션 고르기: 앱과 같은 `SessionPolicy.Decide` (건 = 문제의 PR `pr:…` 또는 가상 레포 티켓 `jira:…`).
    /// 이전 세션은 평가 세션 기록의 `{도메인}:reviewer` 칸에서 온다. 다른 건이면 늘 새로, 새로 시작하면 칸에 이유를 남긴다.
    public static SessionPolicy.Decision ChooseSession(WorkSessionBook book, string slot, string caseKey, SessionPolicy policy, DateTimeOffset now)
    {
        var decision = policy.Decide(caseKey, PipelineRole.Reviewer.Raw(), book.Previous(slot), now);
        if (!decision.Resumed) book.StartFresh(slot, caseKey, decision.Reason, now);
        return decision;
    }

    /// 성공한 실행만 세션 기록에 남기고, 대화 기록에서 다시 센 값이 있으면 덮어쓴다. 실패한 실행은 기록하지 않는다 (다음 문제는 새로 시작).
    /// `caseId` 는 칸의 `keys` 에 남기는 건 이름이다 (`SessionPolicy.Label`: `svc#12`·`ACME-1`).
    public static void RecordSession(WorkSessionBook book, string key, string sessionId, bool ok, string caseId, RunUsage? usage,
                                     (RunUsage Usage, long LastContext)? transcript, DateTimeOffset now)
    {
        if (!ok) return;
        book.Record(key, sessionId, caseId, usage, now);
        if (transcript is { } t) book.ApplyTranscript(key, t.Usage, t.LastContext);
    }

    /// 앞의 `count` 줄을 뺀 나머지 (이번 실행에 새로 생긴 기록)
    public static string NewLines(string transcript, int count) => string.Join("\n", transcript.Split('\n').Skip(count));

    /// 대화 기록에서 부른 스킬 이름들 (스킬 안에서 부른 것 포함)
    public static List<string> SkillsUsed(string transcript)
    {
        var names = new List<string>();
        foreach (var line in transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Json.ParseObject(line) is not JsonObject o || Json.Str(o["type"]) != "assistant"
                || (o["message"] as JsonObject)?["content"] is not JsonArray content || content.Any(c => c is not JsonObject)) continue;
            foreach (var item in content.Cast<JsonObject>())
                if (Json.Str(item["type"]) == "tool_use" && Json.Str(item["name"]) == "Skill" && Json.Str((item["input"] as JsonObject)?["skill"]) is string name) names.Add(name);
        }
        return names;
    }

    /// Claude 설정 폴더의 `projects\<폴더>\<세션 id>.jsonl`
    public static string? TranscriptFile(string configDir, string sessionId)
    {
        try
        {
            return Directory.GetFileSystemEntries(Path.Combine(configDir, "projects"))
                .Select(folder => Path.Combine(folder, $"{sessionId}.jsonl"))
                .FirstOrDefault(System.IO.File.Exists);
        }
        catch { return null; }
    }

    /// 평가 결과 한 줄의 메타 (비용·시간·스킬·세션·토큰 …). 값이 없는 항목은 키를 넣지 않는다.
    public static JsonObject Meta(double costUSD, double seconds, IReadOnlyList<string> skills, string sessionId, RunUsage? usage, bool ok, string domain,
                                  bool resumed = false, string? freshReason = null, long? contextAfter = null)
    {
        var row = new JsonObject
        {
            ["costUSD"] = costUSD,
            ["seconds"] = seconds,
            ["skills"] = new JsonArray(skills.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["sessionId"] = sessionId,
            ["ok"] = ok,
            ["domain"] = domain,
            ["resumed"] = resumed,
        };
        if (freshReason != null) row["freshReason"] = freshReason;
        if (contextAfter != null) row["contextAfter"] = contextAfter;
        if (usage != null)
        {
            row["input"] = usage.Input;
            row["output"] = usage.Output;
            row["cacheRead"] = usage.CacheRead;
            row["cacheWrite"] = usage.CacheWrite;
        }
        return row;
    }
}
