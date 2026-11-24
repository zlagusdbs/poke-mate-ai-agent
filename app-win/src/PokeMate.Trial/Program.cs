using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PokeMate.Core;
using PokeMate.Trial;

// 맥 PokeMateTrial: 가짜 Jira·로컬 git 으로 Jira 역할별 처리(PM → 개발 → 테스터 → 리뷰어 → push)를 처음부터 끝까지 돌려 본다.
// 사용: POKEMATE_TRIAL=<폴더> PokeMate.Trial <폴더> <KEY> <도메인> <레포> [프롬프트 폴더]
//       POKEMATE_TRIAL=<폴더> PokeMate.Trial <폴더> --review <PR 주소> <도메인>   (GitHub 리뷰 한 차례: PR 파일 prs\·세션 규칙·인계 블록)
// 보통 scripts\pipeline-trial.ps1 이 시험 폴더를 만든 뒤 이 프로그램을 돌린다. claude 는 설정의 claudePath 를 쓴다
// (이 프로그램 자신을 claudePath 로 두면 가짜 claude 로 동작한다: FakeClaude.cs).

if (FakeClaude.Matches(args)) return FakeClaude.Run(args);
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length < 4 || Environment.GetEnvironmentVariable("POKEMATE_TRIAL") == null)
{
    Console.WriteLine("usage: POKEMATE_TRIAL=<folder> PokeMate.Trial <folder> <KEY> <domain> <repo> [promptsDir]");
    Console.WriteLine("       POKEMATE_TRIAL=<folder> PokeMate.Trial <folder> --review <PR url> <domain>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var settings = AppPaths.LoadSettings();
var helperText = PipelineAccess.Helper(settings);
var bookFile = Path.Combine(root, "sessions.json");
var book = new Book(bookFile);
if (args[1] == "--review") return await Review(args[2], args[3]);

string key = args[1], domain = args[2], repo = args[3];
var promptsDir = args.Length > 4 ? args[4] : Path.Combine(root, "prompts");
var pipelinesDir = Path.Combine(root, "pipelines");
var table = PipelineAccess.Defaults(helperText, Path.Combine(root, "workspace"), Path.Combine(Paths.Home, ".claude", "references"), pipelinesDir);
var config = PipelineConfig.Load(promptsDir, table, pipelinesDir, helperText);
config.Domains = DomainMap.Load(Path.Combine(root, "domains.json"));
config.Notes = new DomainNotes(Path.Combine(root, "notes"));

var issue = Json.ParseObject(ReadOrNull(Path.Combine(root, "jira", $"{key}.json")));
var summary = Json.Str((issue?["issue"] as JsonObject)?["fields"] is JsonObject fields ? fields["summary"] : null) ?? key;
var evt = $"다음 이벤트가 생겼다.\n- [{key}] {summary}\n- 나에게 할당된 이슈\n- 링크: https://jira.example.com/browse/{key}";

var io = new PipelineIO(
    runRole: async (role, prompt, sessionId, newSessionId, access) =>
    {
        var handoff = prompt.Contains(Handoffs.Title + "\n" + Handoffs.Intro, StringComparison.Ordinal) ? " · 인계 블록 있음" : "";
        Line($"{role.Label()} 시작 (세션 {sessionId ?? newSessionId ?? "-"} · {(sessionId == null ? "새로" : "이어 쓰기")}{handoff}{(prompt.StartsWith(JiraPipeline.InterruptedNote, StringComparison.Ordinal) ? " · 끊긴 실행 이어 가기" : "")})");
        var runner = new ClaudeRunner();
        var response = await runner.Run(prompt, sessionId, settings, writeTools: access.Tools, writeDirs: access.Dirs,
                                        timeout: TimeSpan.FromSeconds(1800), newSessionId: newSessionId);
        switch (response)
        {
            case ClaudeResponse.Success success:
                Line($"{role.Label()} 끝 · {TokenFormat.Usd(runner.LastUsage?.CostUSD ?? 0)}");
                return new PipelineRoleResult(Text: success.Text, SessionId: success.SessionId.Length == 0 ? null : success.SessionId, Usage: runner.LastUsage);
            case ClaudeResponse.Failure failure:
                Line($"{role.Label()} 실패 · {failure.Reason.Message}");
                return new PipelineRoleResult(Usage: runner.LastUsage, Failure: failure.Reason.Message);
            default:
                return new PipelineRoleResult(Usage: runner.LastUsage, Failure: "알 수 없는 응답");
        }
    },
    helper: async helperArgs =>
    {
        var baseArgs = new List<string> { "--directory", Path.Combine(settings.RepoDir, "mcp-server"), "run", "--quiet", "pokemate", "auto" };
        baseArgs.AddRange(helperArgs);
        try
        {
            var output = await new ProcessRunner().Run(settings.UvPath, baseArgs, environment: AppPaths.ProcessEnvironment(settings), timeout: TimeSpan.FromSeconds(1500));
            var text = (output.Stdout + "\n" + output.Stderr).Trim();
            Line($"PokeMate {(helperArgs.Count > 0 ? helperArgs[0] : "")} → {(output.ExitCode == 0 ? "ok" : "실패")}");
            return (output.ExitCode == 0, Tail(text, 3000));
        }
        catch { return (false, "helper 실행 실패"); }
    },
    diff: async worktree =>
    {
        try
        {
            var runner = new ProcessRunner();
            var environment = AppPaths.ProcessEnvironment(settings);
            var add = await runner.Run("git", new[] { "-C", worktree, "add", "-A", "-N" }, environment: environment, timeout: TimeSpan.FromSeconds(120));
            if (add.ExitCode != 0) return null;
            var diff = await runner.Run("git", new[] { "-C", worktree, "diff", "HEAD" }, environment: environment, timeout: TimeSpan.FromSeconds(120));
            return diff.ExitCode == 0 ? diff.Stdout : null;
        }
        catch { return null; }
    },
    recordSession: (k, id, ticket, usage) =>
    {
        book.Record(k, id, ticket, usage);
        return Task.CompletedTask;
    },
    save: state =>
    {
        state.Save(pipelinesDir);
        var last = state.History.Count > 0 ? $"{state.History[^1].Role}→{state.History[^1].Next}" : "-";
        Line($"단계 {state.StatusText} · 마지막 {last} · 비용 {TokenFormat.Usd(state.TotalCost)}{(state.Inflight is { } f ? $" · 실행 중 {f.Role} {f.SessionId}" : "")}");
        return Task.CompletedTask;
    },
    notice: (title, body) =>
    {
        Line($"알림 {title}: {body}");
        return Task.CompletedTask;
    },
    policy: () => new SessionPolicy(AppPaths.LoadSettings()),
    sessionChosen: (k, caseKey, resumeId, reason) =>
    {
        var tab = book.Chose(k, caseKey, resumeId, reason);
        if (resumeId == null) Line($"세션 고름 {k}: 새로{(reason != null ? $" ({reason})" : " (처음)")} · 작업 탭 이유: {tab ?? "-"}");
        return Task.CompletedTask;
    },
    archiveCase: (ticket, keys) =>
    {
        if (book.ArchiveCase(keys, ticket)) Line($"끝난 건 보관: {ticket} 의 역할 세션");
        return Task.CompletedTask;
    });

PipelineState start;
if (PipelineState.Load(key, pipelinesDir) is { } saved)
{
    if (saved.Stage == PipelineState.StageKind.Waiting) saved.Resume(DateTimeOffset.Now);
    start = saved;
}
else
{
    start = new PipelineState(key, summary, evt, domain, repo, 30, DateTimeOffset.Now);
}
var final = await JiraPipeline.Run(start, io, config);
Line($"끝: {final.Stage.Raw()} · {final.Reason ?? ""} · 되돌림 테스터 {final.TesterBacks} 리뷰어 {final.ReviewerBacks} · 인계 메모 {final.Handoffs?.Count ?? 0}개");
foreach (var role in PipelineRoleExtensions.AllCases)
    Line($"비용 {role.Label()}: {TokenFormat.Usd(final.Costs.GetValueOrDefault(role.Raw()))}");
Line($"합계 {TokenFormat.Usd(final.TotalCost)}");
return final.Stage is PipelineState.StageKind.Done or PipelineState.StageKind.Waiting ? 0 : 1;

// GitHub 리뷰 한 차례 (앱의 ProcessReview 와 같은 순서): PR 파일로 세션 고르기 → 새 세션이면 인계 블록 → 실행 → 인계 메모·승인이면 보관
async Task<int> Review(string url, string reviewDomain)
{
    if (ReviewTarget.Parse(url) is not { } target)
    {
        Console.WriteLine($"PR 주소가 아니에요: {url}");
        return 2;
    }
    var prsDir = Path.Combine(root, "prs");
    var slot = DomainReview.SessionKey(reviewDomain);
    var (review, decision) = ReviewCase.Begin(target, reviewDomain, new SessionPolicy(AppPaths.LoadSettings()), prsDir, DateTimeOffset.Now);
    review.Save(prsDir);
    var resumeId = decision.ResumeId;
    var freshId = resumeId == null ? Guid.NewGuid().ToString().ToLowerInvariant() : null;
    var tabReason = resumeId == null ? book.Chose(slot, review.Key, null, decision.Reason) : null;
    var handoff = review.HandoffBlock(decision);
    Line($"리뷰 {target.Label} 세션 {(resumeId == null ? $"새로{(decision.Reason != null ? $" ({decision.Reason})" : " (처음)")}" : "이어 쓰기")} · {resumeId ?? freshId}{(handoff != null ? " · 인계 블록 있음" : "")}{(tabReason != null ? $" · 작업 탭 이유: {tabReason}" : "")}");
    var context = $"다음 이벤트가 생겼다.\n- [리뷰 요청] {target.Label}\n- 링크: {url}\n\n요청: 시험 리뷰";
    var prompt = DomainReview.ReviewPrompt(target, reviewDomain, "시험 도메인", DomainReview.Source.Listed, resumeId == null,
                                           new DomainNotes(Path.Combine(root, "notes")), helperText, context, handoff);
    var runner = new ClaudeRunner();
    var response = await runner.Run(prompt, resumeId, settings, timeout: TimeSpan.FromSeconds(1800), newSessionId: freshId);
    var text = (response as ClaudeResponse.Success)?.Text;
    var usedId = response is ClaudeResponse.Success { SessionId.Length: > 0 } ok ? ok.SessionId : runner.LastUsage != null ? resumeId ?? freshId : null;
    if (usedId != null) book.Record(slot, usedId, target.Label, runner.LastUsage);
    var done = review.Finish(usedId, runner.LastUsage, text, DateTimeOffset.Now);
    review.Save(prsDir);
    if (done != null && book.ArchiveCase(new[] { slot }, target.Label)) Line($"끝난 건 보관: {target.Label} ({done.Value.ToString().ToLowerInvariant()})");
    Line($"리뷰 끝 · {(text ?? "실패").Split('\n')[0]} · 상태 {review.Status.ToString().ToLowerInvariant()} · 인계 메모 {review.Handoffs?.Count ?? 0}개");
    return text != null ? 0 : 1;
}

static void Line(string text)
{
    Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {text}");
    Console.Out.Flush();
}

static string? ReadOrNull(string path)
{
    try { return File.ReadAllText(path); } catch { return null; }
}

static string Tail(string text, int count)
{
    if (text.Length <= count) return text;
    var start = text.Length - count;
    if (char.IsLowSurrogate(text[start])) start++;
    return text[start..];
}

/// 시험용 세션 기록 (sessions.json 은 시험 폴더 안). 여러 역할이 동시에 부르지 않지만 맥처럼 잠근다.
sealed class Book
{
    readonly object gate = new();
    readonly WorkSessionBook book;
    readonly string file;

    public Book(string file)
    {
        this.file = file;
        book = WorkSessionBook.Load(file);
    }

    /// 앱과 같은 방식: 새로 시작하면 앞 세션을 보관하고 이유(새 티켓·새 PR·식음·문맥)를 남긴다. 작업 탭 칸에 남은 이유를 돌려준다.
    public string? Chose(string key, string caseKey, string? resumeId, string? reason)
    {
        if (resumeId != null) return null;
        lock (gate)
        {
            if (book.StartFresh(key, caseKey, reason, DateTimeOffset.Now)) book.Save(file);
            return book.Sessions.TryGetValue(key, out var s) ? s.FreshReason : null;
        }
    }

    public void Record(string key, string id, string ticket, RunUsage? usage)
    {
        lock (gate)
        {
            book.Record(key, id, ticket, usage, DateTimeOffset.Now);
            book.Save(file);
        }
    }

    /// 끝난 건의 역할 세션을 보관한다 (앱과 같은 방식)
    public bool ArchiveCase(IEnumerable<string> keys, string label)
    {
        lock (gate)
        {
            if (!book.ArchiveCase(keys, label)) return false;
            book.Save(file);
            return true;
        }
    }
}
