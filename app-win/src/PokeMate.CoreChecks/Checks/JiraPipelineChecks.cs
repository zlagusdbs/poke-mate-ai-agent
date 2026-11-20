using PokeMate.Core;

namespace PokeMate.CoreChecks;

/// 맥 `actor PipelineScript` 자리: 역할 답을 차례로 내주고, helper 호출·세션 기록·알림을 모아 둔다 (진짜 claude·Jira·git 은 부르지 않는다).
public sealed class PipelineScript
{
    public const string Worktree = @"C:\tmp\acme-ws\.jira-auto-worktrees\ACME-1";

    readonly object gate = new();
    readonly Dictionary<PipelineRole, List<string>> answers;
    readonly List<(PipelineRole Role, string Prompt, ClaudeCommand.WriteAccess Access)> prompts = new();
    readonly List<List<string>> helpers = new();
    readonly Dictionary<string, string> sessions = new();
    readonly List<(string? Resume, string? Fresh)> runs = new();
    readonly List<string> notices = new();

    public bool FailPush { get; set; }
    public bool FailResume { get; set; }
    /// `worktree-add` 가 찍는 글 (Windows 의 파이썬은 CRLF 와 앞 줄 경고를 같이 낼 수 있다)
    public string WorktreeOutput { get; set; } = Worktree;

    public PipelineScript(IDictionary<PipelineRole, string[]> answers) =>
        this.answers = answers.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

    public List<(PipelineRole Role, string Prompt, ClaudeCommand.WriteAccess Access)> Prompts { get { lock (gate) return prompts.ToList(); } }
    public List<List<string>> Helpers { get { lock (gate) return helpers.Select(h => h.ToList()).ToList(); } }
    public Dictionary<string, string> Sessions { get { lock (gate) return new(sessions); } }
    public List<(string? Resume, string? Fresh)> Runs { get { lock (gate) return runs.ToList(); } }
    public List<string> Notices { get { lock (gate) return notices.ToList(); } }

    public void Noticed(string title) { lock (gate) notices.Add(title); }

    public PipelineRoleResult Answer(PipelineRole role, string prompt, ClaudeCommand.WriteAccess access, string? resume, string? fresh)
    {
        lock (gate)
        {
            runs.Add((resume, fresh));
            if (FailResume && resume == "s-cut") return new PipelineRoleResult(Failure: "No conversation found");
            prompts.Add((role, prompt, access));
            if (!answers.TryGetValue(role, out var list) || list.Count == 0) return new PipelineRoleResult(Failure: "답 없음");
            var text = list[0];
            list.RemoveAt(0);
            return new PipelineRoleResult(Text: text, SessionId: $"s-{role.Raw()}", Usage: new RunUsage(costUSD: 0.5));
        }
    }

    public (bool Ok, string Output) Helper(IReadOnlyList<string> args)
    {
        lock (gate)
        {
            helpers.Add(args.ToList());
            if (args.FirstOrDefault() == "worktree-add") return (true, WorktreeOutput);
            if (args.FirstOrDefault() == "commit-push") return FailPush ? (false, "push rejected") : (true, "{\"commit\": \"abc\"}");
            return (true, "ok");
        }
    }

    public void Record(string key, string id) { lock (gate) sessions[key] = id; }
}

// MARK: Jira 역할 파이프라인
public static class JiraPipelineChecks
{
    static string NextBlock(string next, string extra = "") =>
        "답 본문\n```pokemate-next\n{\"next\": \"" + next + "\", \"note\": \"" + next + " 로 넘김\"" + extra + "}\n```";

    const string PmDev = ", \"repo\": \"acme-sample-service\", \"branch\": \"feature/ACME-1\", \"create\": [{\"branch\": \"feature/ACME-1\", \"from\": \"origin/main\"}], \"message\": \"feat: sample change\"";

    public static PipelineIO MakeIO(PipelineScript script, SessionReuse? policy = null) =>
        new(runRole: (role, prompt, resume, fresh, access) => Task.FromResult(script.Answer(role, prompt, access, resume, fresh)),
            helper: args => Task.FromResult(script.Helper(args)),
            diff: _ => Task.FromResult<string?>("diff --git a/x b/x"),
            recordSession: (key, id, _, _) => { script.Record(key, id); return Task.CompletedTask; },
            save: _ => Task.CompletedTask,
            notice: (title, _) => { script.Noticed(title); return Task.CompletedTask; },
            policy: policy == null ? null : () => policy);

    static IDictionary<PipelineRole, string[]> Answers(string[]? pm = null, string[]? dev = null, string[]? tester = null, string[]? reviewer = null)
    {
        var d = new Dictionary<PipelineRole, string[]>();
        if (pm != null) d[PipelineRole.Pm] = pm;
        if (dev != null) d[PipelineRole.Dev] = dev;
        if (tester != null) d[PipelineRole.Tester] = tester;
        if (reviewer != null) d[PipelineRole.Reviewer] = reviewer;
        return d;
    }

    public static async Task Run()
    {
        var map = DomainMap.Parse("""{"acme": {"repos": ["acme-sample-service", " acme-sample-web "]}, "bad:name": {"repos": ["x"]}}""");
        Check.That(map.Domain("acme-sample-web") == "acme" && map.Domain("sample-tool") == "sample-tool" && !map.Domains.ContainsKey("bad:name"),
                   "pipeline: 도메인 지도 — 등록된 레포는 도메인, 없으면 레포 이름");
        var split = PipelineRoleExtensions.Split("acme:tester");
        Check.That(split is { Domain: "acme", Role: PipelineRole.Tester } && PipelineRoleExtensions.Split("acme-sample-service") == null
                   && PipelineRoleExtensions.Split(":dev") == null && PipelineRoleExtensions.Split("acme:nobody") == null
                   && PipelineRoleExtensions.SessionKey("acme", PipelineRole.Dev) == "acme:dev", "pipeline: 세션 키는 {도메인}:{역할}");
        var two = "```pokemate-next\n{\"next\": \"dev\"}\n```\n끝\n```pokemate-next\n{\"next\": \"Tester\", \"note\": \"n\", \"create\": [{\"branch\": \"feature/A-1\", \"from\": \"origin/main\"}]}\n```";
        var parsed = PipelineNext.Extract(two);
        Check.That(parsed?.Kind == PipelineNext.NextKind.Tester && parsed.Note == "n" && parsed.Create.SequenceEqual(new[] { new PipelineBranch("feature/A-1", "origin/main") }),
                   "pipeline: 다음 단계 블록은 마지막 것을 읽음");
        Check.That(PipelineNext.Extract("```pokemate-next\n{\"next\": \"deploy\"}\n```") == null && PipelineNext.Extract("```pokemate-next\nnot json\n```") == null
                   && PipelineNext.Extract("블록 없음") == null, "pipeline: 모르는 next·깨진 JSON·블록 없음은 nil");
        Check.That(PipelineNext.Extract("답\r\n```pokemate-next\r\n{\"next\": \"stop\", \"waiting\": true}\r\n```\r\n") is { Kind: PipelineNext.NextKind.Stop, Waiting: true }
                   && PipelineNext.Strip("앞\n```pokemate-next\n{\"next\": \"stop\"}\n```\n") == "앞", "pipeline: CRLF 줄 끝의 블록도 읽고, 답 요약에서는 블록을 뗌");

        Check.That(PipelineNext.Extract("```pokemate-next\n{\"next\": \"dev\", \"note\": \"a\", \"note\": \"b\"}\n```")?.Note == "b", "pipeline: 같은 키가 두 번 나오면 마지막 값 (맥 JSONSerialization 과 같음)");

        var home = @"C:\home\u";
        var table = PipelineAccess.Parse("""{"pm": {"tools": ["Bash(h jira-get:*)", "Bash(h commit-push:*)", "Edit(//x/**)"], "dirs": ["~/w"]}, "tester": {"tools": ["Bash(h run-tests:*)"]}}""", home);
        var pmAccess = PipelineAccess.Access(PipelineRole.Pm, @"C:\w\A-1", table);
        Check.That(pmAccess.Tools.SequenceEqual(new[] { "Bash(h jira-get:*)" }) && pmAccess.Dirs.SequenceEqual(new[] { @"C:\home\u\w" }), "pipeline: 권한 파일 — push 명령·PM 의 Edit 는 빠짐");
        var defaults = PipelineAccess.Defaults("h", @"C:\ws", @"C:\refs", @"C:\p");
        var devAccess = PipelineAccess.Access(PipelineRole.Dev, @"C:\w\A-1", defaults);
        Check.That(devAccess.Tools.Contains("Edit(//c/w/A-1/**)") && devAccess.Tools.Contains("Write(//c/w/A-1/**)") && devAccess.Dirs.Contains(@"C:\w\A-1")
                   && !devAccess.Tools.Any(t => t.Contains("jira-comment") || t.Contains("commit-push")), "pipeline: 개발은 W 안만 수정, Jira 쓰기·push 없음");
        var testerAccess = PipelineAccess.Access(PipelineRole.Tester, @"C:\w\A-1", defaults);
        var reviewerAccess = PipelineAccess.Access(PipelineRole.Reviewer, @"C:\w\A-1", defaults);
        Check.That(testerAccess.Tools.SequenceEqual(new[] { "Bash(h run-tests:*)", "Bash(h git-read:*)" }) && reviewerAccess.Tools.Contains("Skill(code-review)")
                   && !reviewerAccess.Tools.Any(t => t.StartsWith("Edit(")) && reviewerAccess.Dirs.Contains(@"C:\p"), "pipeline: 테스터·리뷰어는 읽기·실행만");
        var reviewerArgs = ClaudeCommand.Arguments(null, @"C:\c.json", "s", writeTools: reviewerAccess.Tools);
        var testerArgs = ClaudeCommand.Arguments(null, @"C:\c.json", "s", writeTools: testerAccess.Tools);
        var devArgs = ClaudeCommand.Arguments(null, @"C:\c.json", "s", writeTools: devAccess.Tools);
        Check.That(reviewerArgs[reviewerArgs.IndexOf("--tools") + 1] == "Read,Grep,Glob,Bash,Skill"
                   && testerArgs[testerArgs.IndexOf("--tools") + 1] == "Read,Grep,Glob,Bash"
                   && devArgs[devArgs.IndexOf("--tools") + 1] == "Read,Grep,Glob,Bash,Edit,Write", "pipeline: 역할별 --tools 강제");
        // 맥 경로로 적힌 권한 파일의 /w 도 맥과 같은 모양으로 (드라이브 글자 없이 앞에 / 하나 더)
        Check.That(PipelineAccess.Access(PipelineRole.Dev, "/w/A-1", defaults).Tools.Contains("Edit(//w/A-1/**)"), "pipeline: 드라이브 글자 없는 W 경로는 맥과 같은 규칙 모양");
        var encoded = Json.ParseObject(PipelineAccess.Encode(table));
        Check.That(Json.Strings(encoded?["pm"]?["tools"])!.SequenceEqual(new[] { "Bash(h jira-get:*)", "Edit(//x/**)" }) && Json.Strings(encoded?["pm"]?["dirs"])!.SequenceEqual(new[] { @"C:\home\u\w" })
                   && PipelineAccess.Parse(PipelineAccess.Encode(table), home)[PipelineRole.Tester].Tools.Count == 1, "pipeline: 권한 파일 쓰기·읽기 왕복");

        // helper 명령: Git Bash 가 받는 모양 (/ 구분 경로, 따옴표). 공백·한글 경로도.
        var settings = AppSettings.Default;
        settings.UvPath = @"C:\Users\홍 길동\.local\bin\uv.exe";
        settings.RepoDir = @"C:\Users\홍 길동\Workspace\poke-mate-ai-agent";
        Check.That(PipelineAccess.Helper(settings) == "\"C:/Users/홍 길동/.local/bin/uv.exe\" --directory \"C:/Users/홍 길동/Workspace/poke-mate-ai-agent/mcp-server\" run --quiet pokemate auto",
                   "pipeline: helper 명령은 / 구분 경로를 따옴표로 묶음");
        var words = ShellWords(PipelineAccess.Helper(settings) + " jira-get --key ACME-1");
        Check.That(words.SequenceEqual(new[] { "C:/Users/홍 길동/.local/bin/uv.exe", "--directory", "C:/Users/홍 길동/Workspace/poke-mate-ai-agent/mcp-server", "run", "--quiet", "pokemate", "auto", "jira-get", "--key", "ACME-1" }),
                   "pipeline: helper 명령 뒤에 인자를 붙여도 셸 낱말 나누기에서 공백·한글 경로가 한 낱말 (Git Bash 가 받는 모양)");
        Check.That(!PipelineAccess.Helper(settings).Contains((char)92) && PipelineAccess.Defaults(PipelineAccess.Helper(settings), @"C:\w", @"C:\r", @"C:\p")[PipelineRole.Dev].Tools.Contains("Bash(" + PipelineAccess.Helper(settings) + " run-tests:*)"),
                   "pipeline: helper 명령에 역슬래시가 없고 권한 규칙은 helper 명령 뒤에 명령 이름을 붙인 모양");

        var dir = Path.Combine(Path.GetTempPath(), $"pm-pipe-{Guid.NewGuid():N}");
        var config = new PipelineConfig(new Dictionary<PipelineRole, string> { [PipelineRole.Pm] = "PM 지시", [PipelineRole.Dev] = "개발 지시", [PipelineRole.Tester] = "테스터 지시", [PipelineRole.Reviewer] = "리뷰어 지시" },
                                        "공통", defaults, dir);
        var story = new PipelineScript(Answers(
            pm: new[] { NextBlock("dev", PmDev), NextBlock("reviewer"), NextBlock("reviewer"), NextBlock("stop") },
            dev: new[] { NextBlock("tester"), NextBlock("tester"), NextBlock("tester") },
            tester: new[] { NextBlock("dev"), NextBlock("pm"), NextBlock("pm") },
            reviewer: new[] { NextBlock("dev"), NextBlock("push") }));
        var start = new PipelineState("ACME-1", "sample", "다음 이벤트가 생겼다.\n- ACME-1", "acme", "acme-sample-service", 30, DateTimeOffset.Now);
        var done = await JiraPipeline.Run(start, MakeIO(story), config);
        var helpers = story.Helpers;
        var prompts = story.Prompts;
        Check.That(done.Stage == PipelineState.StageKind.Done && done.TesterBacks == 1 && done.ReviewerBacks == 1 && done.Pushed != null,
                   "pipeline: Story 핑퐁 — 테스터·리뷰어 되돌림 한 번씩 뒤 push, 끝");
        Check.That(start.Stage == PipelineState.StageKind.Pm && start.History.Count == 0 && start.Worktree == null, "pipeline: 받은 상태는 고치지 않고 복사해서 진행 (맥 값 복사와 같음)");
        var events = helpers.Where(h => h[0] == "event").ToList();
        Check.That(events.Count == 2 && events.All(h => h.Contains("changed") && h.Contains("핑퐁"))
                   && story.Notices.SequenceEqual(new[] { "핑퐁 테스터→개발 1/2 · ACME-1", "핑퐁 리뷰어→개발 1/2 · ACME-1" }), "pipeline: 되돌림마다 Teams(event changed)·패널 알림");
        var storyRuns = story.Runs;
        Check.That(storyRuns[0].Resume == null && storyRuns[0].Fresh != null && storyRuns[5].Resume == "s-pm" && storyRuns[5].Fresh == null && done.Inflight == null
                   && Guid.TryParse(storyRuns[0].Fresh, out _) && storyRuns[0].Fresh == storyRuns[0].Fresh!.ToLowerInvariant(),
                   "pipeline: 새 역할 세션은 id 를 미리 정해 실행, 이어 쓰기는 --resume, 끝나면 실행 중 기록 지움");
        var nonEvent = helpers.Where(h => h[0] != "event").ToList();
        Check.That(nonEvent.Select(h => h[0]).SequenceEqual(new[] { "create-branch", "worktree-add", "commit-push" })
                   && nonEvent[2].SequenceEqual(new[] { "commit-push", "--worktree", PipelineScript.Worktree, "--message", "feat: sample change" }),
                   "pipeline: 브랜치·작업 폴더·push 는 PokeMate 가 직접 실행");
        Check.That(prompts.Select(p => p.Role).SequenceEqual(new[] { PipelineRole.Pm, PipelineRole.Dev, PipelineRole.Tester, PipelineRole.Dev, PipelineRole.Tester, PipelineRole.Pm,
                                                                    PipelineRole.Reviewer, PipelineRole.Dev, PipelineRole.Tester, PipelineRole.Pm, PipelineRole.Reviewer, PipelineRole.Pm }), "pipeline: 역할 순서");
        var devPrompt = prompts[1].Prompt;
        Check.That(devPrompt.Contains("- 작업 폴더 W: C:/tmp/acme-ws/.jira-auto-worktrees/ACME-1") && !devPrompt.Contains(PipelineScript.Worktree) && devPrompt.Contains("dev 로 넘김") && devPrompt.Contains("공통")
                   && devPrompt.Contains("개발 지시") && prompts[1].Access.Tools.Contains("Edit(//c/tmp/acme-ws/.jira-auto-worktrees/ACME-1/**)"),
                   "pipeline: 개발 프롬프트에 W·앞 단계 말·공통 지시, 권한은 W 수정");
        Check.That(prompts[2].Prompt.Contains("## PM 개발 지시서\ndev 로 넘김") && prompts[6].Prompt.Contains("## PM 개발 지시서") && !prompts[5].Prompt.Contains("## PM 개발 지시서"),
                   "pipeline: PM 개발 지시서는 개발·테스터·리뷰어에게 전달");
        var promptDir = Check.TempDir("pm-prompts");
        File.WriteAllText(Path.Combine(promptDir, "jira-common.md"), "H = `{{helper}}`");
        File.WriteAllText(Path.Combine(promptDir, "jira-dev.md"), "dev");
        var fallbackDir = Check.TempDir("pm-prompts-shared");
        File.WriteAllText(Path.Combine(fallbackDir, "jira-pm.md"), "공유 PM {{helper}}");
        File.WriteAllText(Path.Combine(fallbackDir, "jira-dev.md"), "공유 dev");
        var loaded = PipelineConfig.Load(promptDir, defaults, dir, "uv --directory C:/x run pokemate auto");
        Check.That(loaded.Common == "H = `uv --directory C:/x run pokemate auto`" && loaded.Prompts[PipelineRole.Dev] == "dev" && !loaded.Prompts.ContainsKey(PipelineRole.Pm),
                   "pipeline: 역할 프롬프트 파일 읽기, {{helper}} 바꾸기");
        var withFallback = PipelineConfig.Load(promptDir, defaults, dir, "HH", fallbackDir);
        Check.That(withFallback.Prompts[PipelineRole.Pm] == "공유 PM HH" && withFallback.Prompts[PipelineRole.Dev] == "dev", "pipeline: local 에 없는 역할 프롬프트는 shared 에서");
        Check.Remove(promptDir);
        Check.Remove(fallbackDir);
        Check.That(prompts[6].Prompt.Contains("ACME-1.diff") && prompts[3].Prompt.Contains("되돌림: 테스터→개발 1/2") && prompts[0].Prompt.Contains("\"repo\""),
                   "pipeline: 리뷰어는 diff 파일, 되돌림 횟수 전달");
        Check.That(File.ReadAllText(Path.Combine(dir, "ACME-1.diff")) == "diff --git a/x b/x", "pipeline: diff 파일은 진행 폴더에 UTF-8 로 저장");
        Check.That(prompts[11].Prompt.Contains("커밋·push 했다") && prompts[11].Prompt.Contains("next 는 다음 중 하나: stop") && !prompts[0].Access.Tools.Any(t => t.Contains("commit-push")),
                   "pipeline: push 뒤 PM 은 stop 만, 어떤 역할에도 push 권한 없음");
        Check.That(story.Sessions.OrderBy(kv => kv.Key).SequenceEqual(new Dictionary<string, string> { ["acme:dev"] = "s-dev", ["acme:pm"] = "s-pm", ["acme:reviewer"] = "s-reviewer", ["acme:tester"] = "s-tester" }.OrderBy(kv => kv.Key))
                   && prompts[5].Prompt.Contains(JiraPipeline.ResumeNote) && !prompts[0].Prompt.Contains(JiraPipeline.ResumeNote) && Math.Abs(done.TotalCost - 6.0) < 0.001,
                   "pipeline: 역할 세션은 {도메인}:{역할} 로 기록·이어 쓰기, 역할별 비용 합산");

        // 티켓별 역할 세션: 같은 티켓 안에서만 이어 쓰고, 다른 티켓은 같은 도메인이어도 새로 시작
        Check.That(done.Sessions is { Count: 4 } && done.Sessions["dev"].Id == "s-dev" && done.Sessions["pm"].ContextTokens == 0 && done.Sessions.Keys.OrderBy(k => k).SequenceEqual(new[] { "dev", "pm", "reviewer", "tester" })
                   && start.Sessions == null, "ticket-session: 티켓 상태에 역할별 세션(id·마지막 사용·문맥 토큰)이 기록됨");
        var reviewerRuns = prompts.Select((p, i) => (p.Role, Run: storyRuns[i])).Where(x => x.Role == PipelineRole.Reviewer).ToList();
        Check.That(reviewerRuns.Count == 2 && reviewerRuns.All(x => x.Run.Resume == null && x.Run.Fresh != null) && storyRuns[4].Resume == "s-tester" && storyRuns[3].Resume == "s-dev",
                   "ticket-session: 같은 티켓 안에서 테스터·개발·PM 은 --resume, 리뷰어는 늘 새로 (기본 설정)");
        PipelineState Resumed(PipelineState from, PipelineState.StageKind stage)
        {
            var c = from.Copy();
            c.Stage = stage;
            c.Worktree = PipelineScript.Worktree;
            c.Repo = "acme-sample-service";
            c.MustStop = false;
            return c;
        }
        var second = new PipelineState("ACME-2", "other ticket", "e", "acme", "acme-sample-service", 30, DateTimeOffset.Now);
        second.Stage = PipelineState.StageKind.Dev;
        second.Worktree = PipelineScript.Worktree;
        var otherScript = new PipelineScript(Answers(dev: new[] { NextBlock("tester") }, tester: new[] { NextBlock("pm") }, pm: new[] { NextBlock("stop") }));
        var otherDone = await JiraPipeline.Run(second, MakeIO(otherScript), config);
        Check.That(otherDone.Stage == PipelineState.StageKind.Done && otherScript.Runs.Count == 3 && otherScript.Runs.All(r => r.Resume == null && r.Fresh != null),
                   "ticket-session: 같은 도메인의 다른 티켓은 역할 세션을 새로 시작 (--session-id)");
        var again = new PipelineScript(Answers(dev: new[] { NextBlock("pm") }, pm: new[] { NextBlock("stop") }));
        var againDone = await JiraPipeline.Run(Resumed(otherDone, PipelineState.StageKind.Dev), MakeIO(again), config);
        Check.That(againDone.Stage == PipelineState.StageKind.Done && again.Runs.Select(r => r.Resume).SequenceEqual(new[] { "s-dev", "s-pm" }) && again.Runs.All(r => r.Fresh == null),
                   "ticket-session: 같은 티켓의 다음 실행(55분 안)은 그 티켓 세션을 이어 씀");
        var stale = Resumed(otherDone, PipelineState.StageKind.Dev);
        stale.Sessions!["dev"] = stale.Sessions["dev"] with { LastUsed = DateTimeOffset.Now.AddMinutes(-61) };
        stale.Sessions["pm"] = stale.Sessions["pm"] with { ContextTokens = 130_000 };
        var staleScript = new PipelineScript(Answers(dev: new[] { NextBlock("pm") }, pm: new[] { NextBlock("stop") }));
        var staleDone = await JiraPipeline.Run(stale, MakeIO(staleScript), config);
        Check.That(staleScript.Runs.Count == 2 && staleScript.Runs.All(r => r.Resume == null && r.Fresh != null) && staleDone.Sessions!["dev"].Id == "s-dev",
                   "ticket-session: 같은 티켓이라도 55분이 지나거나 문맥이 12만 토큰 이상이면 새로 시작");
        var always = new PipelineScript(Answers(dev: new[] { NextBlock("pm") }, pm: new[] { NextBlock("stop") }));
        await JiraPipeline.Run(stale, MakeIO(always, new SessionReuse(0, 0, true)), config);
        Check.That(always.Runs.Select(r => r.Resume).SequenceEqual(new[] { "s-dev", "s-pm" }), "ticket-session: 분·토큰 기준을 0(끔)으로 두면 같은 티켓 안에서는 늘 이어 씀");
        var nowT = DateTimeOffset.Now;
        var pol = new SessionReuse();
        Check.That(new PipelineState("ACME-3", "t", "e", "acme", null, 30, nowT).ReuseSession(PipelineRole.Dev, pol, nowT) == (null, null)
                   && stale.ReuseSession(PipelineRole.Dev, pol, nowT).Reason!.StartsWith("식음 6") && stale.ReuseSession(PipelineRole.Pm, pol, nowT).Reason == "문맥 130.0K (기준 120.0K)"
                   && otherDone.ReuseSession(PipelineRole.Reviewer, pol, nowT) == (null, null) && otherDone.ReuseSession(PipelineRole.Dev, pol, nowT).Id == "s-dev",
                   "ticket-session: 티켓 세션 고르기 — 없으면 (null,null), 기준에 걸리면 이유");
        var rv = Resumed(otherDone, PipelineState.StageKind.Reviewer);
        rv.RecordSession(PipelineRole.Reviewer, "s-r", null, nowT);
        Check.That(rv.ReuseSession(PipelineRole.Reviewer, pol, nowT) == (null, SessionReuse.ReviewerReason) && rv.ReuseSession(PipelineRole.Reviewer, new SessionReuse(freshReviewer: false), nowT).Id == "s-r",
                   "ticket-session: 리뷰어는 늘 새로 (설정을 끄면 같은 기준)");

        // 작업 탭 기록 {도메인}:{역할}: 다른 티켓이 새로 시작하면 앞 세션은 보관하고 "새 티켓 (앞: KEY)"
        var tab = new WorkSessionBook();
        var tt = DateTimeOffset.Now;
        tab.Record("acme:dev", "s-a", "ACME-100", new RunUsage(costUSD: 1), tt);
        Check.That(!tab.StartFresh("acme:pm", "ACME-100", null, tt) && tab.StartFresh("acme:dev", "ACME-101", null, tt)
                   && tab.Sessions["acme:dev"].Id == "" && tab.Sessions["acme:dev"].FreshReason == "새 티켓 (앞: ACME-100)" && tab.Archived.Select(s => s.Id).SequenceEqual(new[] { "s-a" }),
                   "ticket-session: 작업 탭 기록은 새 티켓이면 앞 세션 보관 + 이유 '새 티켓 (앞: KEY)'");
        tab.Record("acme:dev", "s-b", "ACME-101", null, tt);
        Check.That(tab.StartFresh("acme:dev", "ACME-101", "식음 61분 (기준 55분)", tt) && tab.Sessions["acme:dev"].FreshReason == "식음 61분 (기준 55분)" && tab.Archived.Count == 2,
                   "ticket-session: 같은 티켓이 기준에 걸리면 그 이유 그대로 (기존 문구)");

        // 새 필드가 없는 옛 진행 파일도 읽고, 있으면 왕복
        var sessionDir = Check.TempDir("pm-ticket-sessions");
        otherDone.Save(sessionDir);
        var withSessions = File.ReadAllText(PipelineState.File("ACME-2", sessionDir));
        var sessionBack = PipelineState.Load("ACME-2", sessionDir);
        Check.Remove(sessionDir);
        Check.That(withSessions.Contains("\"sessions\": {") && withSessions.Contains("\"contextTokens\"") && withSessions.Contains("\"lastUsed\": \"")
                   && withSessions.IndexOf("\"reviewerBacks\"", StringComparison.Ordinal) < withSessions.IndexOf("\"sessions\"", StringComparison.Ordinal)
                   && sessionBack?.Sessions?["dev"].Id == "s-dev" && Math.Abs((sessionBack.Sessions["dev"].LastUsed - otherDone.Sessions!["dev"].LastUsed).TotalSeconds) < 1,
                   "ticket-session: 진행 파일 sessions {역할: {id, lastUsed, contextTokens}} 저장·읽기");

        var limit = new PipelineScript(Answers(
            pm: new[] { NextBlock("dev", PmDev), NextBlock("stop", ", \"waiting\": true") },
            dev: new[] { NextBlock("tester"), NextBlock("tester"), NextBlock("tester") },
            tester: new[] { NextBlock("dev"), NextBlock("dev"), NextBlock("dev") }));
        var limited = await JiraPipeline.Run(start, MakeIO(limit), config);
        var limitPrompts = limit.Prompts;
        var limitNotices = limit.Notices;
        Check.That(limitNotices.Count == 3 && limitNotices[^1] == "핑퐁 상한 초과 · ACME-1", "pipeline: 상한 초과도 알림");
        Check.That(limited.Stage == PipelineState.StageKind.Waiting && limited.TesterBacks == 3 && limitPrompts[^1].Role == PipelineRole.Pm
                   && limitPrompts[^1].Prompt.Contains("되돌림이 2번을 넘어") && limitPrompts[^1].Prompt.Contains("next 는 다음 중 하나: stop"),
                   "pipeline: 되돌림 상한 2번 넘으면 PM 이 멈추고 답을 기다림");
        var waiting = limited.Copy();
        waiting.Resume(DateTimeOffset.Now);
        Check.That(waiting.Stage == PipelineState.StageKind.Pm && waiting.TesterBacks == 0 && !waiting.MustStop, "pipeline: 확인 필요 답이 오면 PM 부터 다시, 되돌림 0");

        var broken = new PipelineScript(Answers(pm: new[] { "블록 없는 답" }));
        var stopped = await JiraPipeline.Run(start, MakeIO(broken), config);
        var brokenHelpers = broken.Helpers;
        Check.That(stopped.Stage == PipelineState.StageKind.Stopped && brokenHelpers.Select(h => h[0]).SequenceEqual(new[] { "event", "jira-state" }) && brokenHelpers[0].Contains("blocked"),
                   "pipeline: 블록이 없으면 멈추고 blocked 알림·jira-state");

        var early = new PipelineScript(Answers(pm: new[] { NextBlock("reviewer") }));
        var earlyState = await JiraPipeline.Run(start, MakeIO(early), config);
        Check.That(earlyState.Stage == PipelineState.StageKind.Stopped && earlyState.Reason?.Contains("reviewer") == true, "pipeline: 테스트 통과 전엔 PM 이 리뷰로 못 넘김");

        var task = new PipelineScript(Answers(pm: new[] { NextBlock("stop") }));
        var taskState = await JiraPipeline.Run(new PipelineState("ACME-2", "ops", "e", WorkSessionBook.Operations, null, 30, DateTimeOffset.Now), MakeIO(task), config);
        Check.That(taskState.Stage == PipelineState.StageKind.Done && task.Helpers.Count == 0 && task.Prompts.Count == 1, "pipeline: Task 는 PM 하나로 끝");

        var failing = new PipelineScript(Answers(
            pm: new[] { NextBlock("dev", PmDev), NextBlock("reviewer"), NextBlock("stop") },
            dev: new[] { NextBlock("tester") }, tester: new[] { NextBlock("pm") }, reviewer: new[] { NextBlock("push") })) { FailPush = true };
        var failed = await JiraPipeline.Run(start, MakeIO(failing), config);
        Check.That(failed.Stage == PipelineState.StageKind.Done && failed.Pushed == null && failing.Prompts[^1].Prompt.Contains("push 하지 못했다"), "pipeline: push 실패면 PM 이 개발 중단 처리");

        var noPrompt = new PipelineConfig(new Dictionary<PipelineRole, string> { [PipelineRole.Pm] = "x" }, "", defaults, dir);
        var missing = await JiraPipeline.Run(start, MakeIO(new PipelineScript(Answers(pm: new[] { NextBlock("dev", PmDev) }))), noPrompt);
        Check.That(missing.Stage == PipelineState.StageKind.Stopped && missing.Reason?.Contains("jira-dev.md") == true, "pipeline: 역할 프롬프트 파일이 없으면 멈춤");

        var cut = start.Copy();
        cut.Stage = PipelineState.StageKind.Dev;
        cut.Repo = "acme-sample-service";
        cut.Worktree = PipelineScript.Worktree;
        cut.Inflight = new PipelineState.InflightRun("dev", "s-cut", DateTimeOffset.Now);
        var resumeScript = new PipelineScript(Answers(dev: new[] { NextBlock("pm") }, pm: new[] { NextBlock("stop") }));
        var resumed = await JiraPipeline.Run(cut, MakeIO(resumeScript), config);
        Check.That(resumed.Stage == PipelineState.StageKind.Done && resumeScript.Runs[0].Resume == "s-cut" && resumeScript.Prompts[0].Prompt.StartsWith(JiraPipeline.InterruptedNote),
                   "pipeline: 끊긴 역할 실행은 그 세션을 --resume 해 남은 것만 이어 감");
        var lostScript = new PipelineScript(Answers(dev: new[] { NextBlock("pm") }, pm: new[] { NextBlock("stop") })) { FailResume = true };
        var lost = await JiraPipeline.Run(cut, MakeIO(lostScript), config);
        var lostRuns = lostScript.Runs;
        Check.That(lost.Stage == PipelineState.StageKind.Done && lostRuns.Count == 3 && lostRuns[0].Resume == "s-cut" && lostRuns[1].Resume == null && lostRuns[1].Fresh != null
                   && lostScript.Prompts[0].Prompt.StartsWith(JiraPipeline.InterruptedNote), "pipeline: 끊긴 세션이 없으면 새 세션으로, 이미 한 일 확인 지시와 함께");
        var quitIO = new PipelineIO(runRole: (_, _, _, _, _) => Task.FromResult(new PipelineRoleResult(Interrupted: true)),
                                    helper: _ => Task.FromResult((true, "")), diff: _ => Task.FromResult<string?>(""),
                                    recordSession: (_, _, _, _) => Task.CompletedTask, save: _ => Task.CompletedTask);
        var quit = await JiraPipeline.Run(cut, quitIO, config);
        Check.That(quit.Stage == PipelineState.StageKind.Dev && quit.Inflight?.Role == "dev" && quit.Reason == null, "pipeline: 앱 종료로 끊기면 멈춤 처리 없이 실행 중 기록을 남김");
        var fresh = ClaudeCommand.Arguments(null, @"C:\c.json", "s", newSessionId: "abc");
        var both = ClaudeCommand.Arguments("old", @"C:\c.json", "s", newSessionId: "abc");
        Check.That(fresh.Contains("--session-id") && fresh.Contains("abc") && both.Contains("--resume") && !both.Contains("--session-id"), "pipeline: 새 실행은 --session-id, 이어 쓰기는 --resume 만");

        // Windows 의 helper 출력: CRLF, 앞 줄 경고, 뒤 빈 줄이 있어도 작업 폴더는 마지막 절대 경로 줄
        var crlf = new PipelineScript(Answers(pm: new[] { NextBlock("dev", PmDev), NextBlock("stop") }, dev: new[] { NextBlock("tester") }, tester: new[] { NextBlock("pm") }))
        {
            WorktreeOutput = "Preparing worktree\r\nwarning: LF will be replaced by CRLF\r\n" + PipelineScript.Worktree + "\r\n\r\n",
        };
        var crlfState = await JiraPipeline.Run(start, MakeIO(crlf), config);
        Check.That(crlfState.Stage == PipelineState.StageKind.Done && crlfState.Worktree == PipelineScript.Worktree, "pipeline: worktree-add 출력이 CRLF·경고 줄이 섞여도 마지막 절대 경로를 W 로");
        var posix = new PipelineScript(Answers(pm: new[] { NextBlock("dev", PmDev), NextBlock("stop") }, dev: new[] { NextBlock("tester") }, tester: new[] { NextBlock("pm") })) { WorktreeOutput = "/c/tmp/w/ACME-1\n" };
        Check.That((await JiraPipeline.Run(start, MakeIO(posix), config)).Worktree == "/c/tmp/w/ACME-1", "pipeline: Git Bash 식 / 경로 출력도 W 로 받음 (맥과 같음)");
        var noPath = new PipelineScript(Answers(pm: new[] { NextBlock("dev", PmDev), NextBlock("stop") })) { WorktreeOutput = "worktree 를 만들었어요" };
        var noPathState = await JiraPipeline.Run(start, MakeIO(noPath), config);
        Check.That(noPathState.Stage == PipelineState.StageKind.Done && noPath.Prompts[^1].Prompt.Contains("작업 폴더를 만들지 못했다") && noPathState.Worktree == null,
                   "pipeline: 경로가 안 보이면 PM 이 개발 중단 처리");

        var kmap = DomainMap.Parse("""{"acme": {"repos": ["acme-sample-service"], "knowledge": ["acme-kb", "~/.claude/references/acme-wiki"]}, "beta": {"repos": ["beta-api"], "knowledge": []}, "default": {"knowledge": ["sample-notes"]}}""");
        var u = @"C:\u";
        Check.That(!kmap.Domains.ContainsKey("default") && kmap.KnowledgePaths("acme", u).SequenceEqual(new[] { @"C:\u\Workspace\acme-kb", @"C:\u\.claude\references\acme-wiki" })
                   && kmap.KnowledgePaths("beta", u).Count == 0 && kmap.KnowledgePaths("sample-tool", u).SequenceEqual(new[] { @"C:\u\Workspace\sample-notes" })
                   && kmap.KnowledgeText("acme", u) == "   - `~/Workspace/acme-kb`\n   - `~/.claude/references/acme-wiki`"
                   && kmap.KnowledgeText("beta", u) == "   (정한 지식 저장소 없음)", "knowledge: 도메인별 지식 저장소, 없으면 default");
        var absolute = DomainMap.Parse("""{"acme": {"repos": ["r"], "knowledge": ["D:\\kb\\acme", "/c/kb", "~\\wiki"]}}""");
        Check.That(absolute.KnowledgePaths("acme", u).SequenceEqual(new[] { @"D:\kb\acme", "/c/kb", @"C:\u\wiki" }) && absolute.KnowledgeText("acme", u).Contains("`D:/kb/acme`"),
                   "knowledge: 절대 경로(드라이브·/)는 그대로, ~\\ 도 홈으로");
        var kconfig = new PipelineConfig(new Dictionary<PipelineRole, string> { [PipelineRole.Pm] = "지식:\n{{knowledge}}", [PipelineRole.Dev] = "d" }, "c {{knowledge}}", defaults, dir, domains: kmap, home: u) { MaxSteps = 1 };
        var kscript = new PipelineScript(Answers(pm: new[] { NextBlock("stop") }));
        await JiraPipeline.Run(start, MakeIO(kscript), kconfig);
        var kprompts = kscript.Prompts;
        Check.That(kprompts[0].Prompt.Contains("지식:\n   - `~/Workspace/acme-kb`") && !kprompts[0].Prompt.Contains("{{knowledge}}")
                   && kprompts[0].Access.Dirs.Contains(@"C:\u\Workspace\acme-kb"), "knowledge: 프롬프트 자리표시 치환, 읽기 폴더에 추가");
        Check.That(kprompts[0].Prompt.Contains("점검표 `C:/u/.claude/references/notes/acme/checklist.md` (없음)")
                   && kprompts[0].Prompt.Contains("$H domain-note --domain acme --init")
                   && kprompts[0].Prompt.Contains("--source \"ACME-1\"")
                   && kprompts[0].Access.Dirs.Contains(@"C:\u\.claude\references\notes"), "notes: 역할 프롬프트에 공유 점검표·노트 안내, 노트 폴더는 읽기 폴더");
        var review = defaults[PipelineRole.Reviewer];
        Check.That(review.Tools.Contains("Skill(domain-review)") && review.Tools.Contains("Skill(code-review)") && review.Tools.Contains("Bash(h domain-note:*)")
                   && review.Dirs.Contains(@"C:\refs") && defaults[PipelineRole.Dev].Dirs.Contains(@"C:\refs") && defaults[PipelineRole.Pm].Tools.Contains("Bash(h domain-note:*)")
                   && !defaults[PipelineRole.Dev].Tools.Contains("Bash(h domain-note:*)") && !defaults[PipelineRole.Tester].Dirs.Contains(@"C:\refs"),
                   "notes: 리뷰어는 domain-review 스킬과 domain-note, 개발은 읽기만, 테스터는 그대로");
        var unsure = start.Copy();
        unsure.Stage = PipelineState.StageKind.Reviewer;
        unsure.TestsPassed = true;
        var unsureOutcome = unsure.Apply(PipelineRole.Reviewer, new PipelineNext(PipelineNext.NextKind.Pm, "미확인 2건"), "", DateTimeOffset.Now);
        Check.That(unsureOutcome is PipelineState.Outcome.Next && unsure.Stage == PipelineState.StageKind.Pm && unsure.ReviewerBacks == 0 && unsure.Note == "미확인 2건",
                   "pipeline: 리뷰어가 미확인만 있으면 PM 으로 (되돌림 횟수는 안 셈)");

        var bc = new PipelineScript(Answers(pm: new[] { NextBlock("stop", ", \"note\": \"프로젝트 단위 개발 건: 자동 처리 안 함\"") }));
        var bcState = await JiraPipeline.Run(start, MakeIO(bc), config);
        Check.That(bcState.Stage == PipelineState.StageKind.Done && bc.Helpers.Count == 0 && bc.Notices.Count == 0 && bc.Prompts.Count == 1,
                   "pipeline: PM 이 처리 안 하고 stop 하면 정상 종료 (앱은 blocked 알림을 따로 보내지 않음, 알림은 PM 이 한 번)");
        Check.That(PipelineState.ArrivalOf(null) is PipelineState.Arrival.Start && PipelineState.ArrivalOf(limited) is PipelineState.Arrival.Resume
                   && PipelineState.ArrivalOf(bcState) is PipelineState.Arrival.Skip { Reason: "이미 처리함: ACME-1 · done" }
                   && PipelineState.ArrivalOf(cut) is PipelineState.Arrival.Skip { Reason: "이미 처리 중: ACME-1 · dev" }
                   && PipelineState.ArrivalOf(stopped) is PipelineState.Arrival.Skip, "pipeline: 끝난 티켓은 다시 잡지 않음, 답 기다리던 티켓만 이어 감");

        var shown = start.Copy();
        shown.Stage = PipelineState.StageKind.Tester;
        shown.TesterBacks = 2;
        Check.That(shown.StatusText == "ACME-1 · tester 2/2", "pipeline: 작업 탭 단계 표시");

        // 진행 상태 파일: pipelines\{KEY}.json (ISO 8601 날짜, 키 정렬)
        done.Save(dir);
        var reloaded = PipelineState.Load("ACME-1", dir);
        Check.That(reloaded?.Stage == done.Stage && reloaded.History.Count == done.History.Count && reloaded.Worktree == done.Worktree
                   && reloaded.Costs.OrderBy(kv => kv.Key).SequenceEqual(done.Costs.OrderBy(kv => kv.Key)) && PipelineState.LoadAll(dir).Count == 1, "pipeline: 진행 상태 저장·읽기");
        var text = File.ReadAllText(PipelineState.File("ACME-1", dir));
        Check.That(text.Contains("\"stage\": \"done\"") && System.Text.RegularExpressions.Regex.IsMatch(text, "\"createdAt\": \"\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ\"")
                   && text.IndexOf("\"brief\"", StringComparison.Ordinal) < text.IndexOf("\"costs\"", StringComparison.Ordinal) && text.IndexOf("\"costs\"", StringComparison.Ordinal) < text.IndexOf("\"stage\"", StringComparison.Ordinal)
                   && !text.Contains("\"inflight\"") && !text.Contains("\"reason\""), "pipeline: 상태 파일은 키 정렬·ISO 8601 날짜·값 없는 키 생략, stage 는 맥과 같은 글자");
        var macText = string.Join("\n", "{", "  \"branch\" : \"feature/ACME-1\",", "  \"costs\" : {", "    \"pm\" : 0.5", "  },", "  \"create\" : [", "    {", "      \"branch\" : \"feature/ACME-1\",",
            "      \"from\" : \"origin/main\"", "    }", "  ],", "  \"createdAt\" : \"2026-11-17T08:00:00Z\",", "  \"domain\" : \"acme\",", "  \"event\" : \"e\",", "  \"history\" : [", "    {", "      \"at\" : \"2026-11-17T08:01:00Z\",",
            "      \"next\" : \"dev\",", "      \"note\" : \"n\",", "      \"role\" : \"pm\"", "    }", "  ],", "  \"inflight\" : {", "    \"role\" : \"dev\",", "    \"sessionId\" : \"abc\",", "    \"startedAt\" : \"2026-11-17T08:02:00Z\"", "  },",
            "  \"key\" : \"ACME-9\",", "  \"mustStop\" : false,", "  \"note\" : \"\",", "  \"reviewerBacks\" : 0,", "  \"stage\" : \"dev\",", "  \"summary\" : \"\",", "  \"testerBacks\" : 1,", "  \"testsPassed\" : false,",
            "  \"timeoutMinutes\" : 30,", "  \"title\" : \"t\",", "  \"updatedAt\" : \"2099-01-01T00:00:00Z\"", "}");
        File.WriteAllText(PipelineState.File("ACME-9", dir), macText);
        var fromMac = PipelineState.Load("ACME-9", dir);
        Check.That(fromMac?.Stage == PipelineState.StageKind.Dev && fromMac.Inflight?.SessionId == "abc" && fromMac.Create[0].From == "origin/main" && fromMac.History[0].Next == "dev"
                   && fromMac.Sessions == null && fromMac.TesterBacks == 1 && fromMac.Repo == null && fromMac.UpdatedAt == DateTimeOffset.Parse("2099-01-01T00:00:00Z") && PipelineState.LoadAll(dir).Select(s => s.Key).SequenceEqual(new[] { "ACME-9", "ACME-1" }),
                   "pipeline: 맥이 쓴 진행 상태 파일을 읽음 (목록은 갱신 시각 최신순)");
        File.WriteAllText(PipelineState.File("BROKEN-1", dir), "{\"key\":\"BROKEN-1\",\"sta");
        Check.That(PipelineState.Load("BROKEN-1", dir) == null && PipelineState.Load("NONE-1", dir) == null && PipelineState.LoadAll(dir).Count == 2, "pipeline: 깨진·없는 상태 파일은 건너뜀");
        Check.Remove(dir);
    }

    /// POSIX 셸(Git Bash)이 명령 한 줄을 낱말로 나누는 규칙(공백으로 나누고, 큰따옴표 안은 한 낱말)으로 helper 명령을 나눠 본다.
    /// 공백·한글 경로가 한 낱말로 남고, 뒤에 붙이는 인자가 따로 떨어지면 Git Bash 가 그대로 실행한다.
    static List<string> ShellWords(string line)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var inWord = false;
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; inWord = true; }
            else if (ch == ' ' && !quoted) { if (inWord) words.Add(current.ToString()); current.Clear(); inWord = false; }
            else { current.Append(ch); inWord = true; }
        }
        if (inWord) words.Add(current.ToString());
        return words;
    }
}
