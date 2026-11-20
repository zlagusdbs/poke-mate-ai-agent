using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PokeMate.Core;

// 맥 PokeMateTrial: 가짜 Jira·로컬 git 으로 Jira 역할별 처리(PM → 개발 → 테스터 → 리뷰어 → push)를 처음부터 끝까지 돌려 본다.
// 사용: POKEMATE_TRIAL=<폴더> PokeMate.Trial <폴더> <KEY> <도메인> <레포> [프롬프트 폴더]
// 보통 scripts\pipeline-trial.ps1 이 시험 폴더를 만든 뒤 이 프로그램을 돌린다. claude 는 설정의 claudePath 를 쓴다.

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length < 4 || Environment.GetEnvironmentVariable("POKEMATE_TRIAL") == null)
{
    Console.WriteLine("usage: POKEMATE_TRIAL=<folder> PokeMate.Trial <folder> <KEY> <domain> <repo> [promptsDir]");
    return 2;
}
var root = Path.GetFullPath(args[0]);
string key = args[1], domain = args[2], repo = args[3];
var promptsDir = args.Length > 4 ? args[4] : Path.Combine(root, "prompts");

var settings = AppPaths.LoadSettings();
var helperText = PipelineAccess.Helper(settings);
var pipelinesDir = Path.Combine(root, "pipelines");
var bookFile = Path.Combine(root, "sessions.json");
var table = PipelineAccess.Defaults(helperText, Path.Combine(root, "workspace"), Path.Combine(Paths.Home, ".claude", "references"), pipelinesDir);
var config = PipelineConfig.Load(promptsDir, table, pipelinesDir, helperText);
config.Domains = DomainMap.Load(Path.Combine(root, "domains.json"));
config.Notes = new DomainNotes(Path.Combine(root, "notes"));

var issue = Json.ParseObject(ReadOrNull(Path.Combine(root, "jira", $"{key}.json")));
var summary = Json.Str((issue?["issue"] as JsonObject)?["fields"] is JsonObject fields ? fields["summary"] : null) ?? key;
var evt = $"다음 이벤트가 생겼다.\n- [{key}] {summary}\n- 나에게 할당된 이슈\n- 링크: https://jira.example.com/browse/{key}";

var book = new Book(bookFile);

var io = new PipelineIO(
    runRole: async (role, prompt, sessionId, newSessionId, access) =>
    {
        Line($"{role.Label()} 시작 (세션 {sessionId ?? newSessionId ?? "-"} · {(sessionId == null ? "새로" : "이어 쓰기")}{(prompt.StartsWith(JiraPipeline.InterruptedNote, StringComparison.Ordinal) ? " · 끊긴 실행 이어 가기" : "")})");
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
    sessionChosen: (k, ticket, resumeId, reason) =>
    {
        book.Chose(k, ticket, resumeId, reason);
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
Line($"끝: {final.Stage.Raw()} · {final.Reason ?? ""} · 되돌림 테스터 {final.TesterBacks} 리뷰어 {final.ReviewerBacks}");
foreach (var role in PipelineRoleExtensions.AllCases)
    Line($"비용 {role.Label()}: {TokenFormat.Usd(final.Costs.GetValueOrDefault(role.Raw()))}");
Line($"합계 {TokenFormat.Usd(final.TotalCost)}");
return final.Stage is PipelineState.StageKind.Done or PipelineState.StageKind.Waiting ? 0 : 1;

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

    /// 앱과 같은 방식: 새로 시작하면 앞 세션을 보관하고 이유(새 티켓 등)를 남긴다
    public void Chose(string key, string ticket, string? resumeId, string? reason)
    {
        if (resumeId != null) return;
        lock (gate)
        {
            if (book.StartFresh(key, ticket, reason, DateTimeOffset.Now)) book.Save(file);
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
}
