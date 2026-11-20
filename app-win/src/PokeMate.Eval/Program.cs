using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PokeMate.Core;

// 맥 PokeMateEval: 리뷰 기준 문제 묶음(local\eval\reviews.json)을 도메인 리뷰어 프롬프트로 차례로 돌리고 채점한다.
// 사용: PokeMate.Eval [cases.json] [--case id]... [--out 결과 폴더] [--reuse always|default|분,토큰]
// 보통 scripts\review-eval.ps1 로 돌린다.

Console.OutputEncoding = new UTF8Encoding(false);
var settings = AppPaths.LoadSettings();
var rest = new Queue<string>(args);
var only = new List<string>();
string? outDir = null;
var casesFile = Path.Combine(settings.RepoDir, "local", "eval", "reviews.json");
SessionReuse? reuse = null;
while (rest.Count > 0)
{
    var arg = rest.Dequeue();
    switch (arg)
    {
        case "--case":
            if (rest.Count > 0) only.Add(rest.Dequeue());
            break;
        case "--out":
            if (rest.Count > 0) outDir = rest.Dequeue();
            break;
        case "--reuse":
            if (rest.Count == 0 || ReviewEval.Policy(rest.Dequeue()) is not { } policy)
            {
                Console.WriteLine("--reuse 는 always | default | 분,토큰 (예: 55,120000)");
                return 2;
            }
            reuse = policy;
            break;
        case "-h":
        case "--help":
            Console.WriteLine("usage: PokeMate.Eval [cases.json] [--case id]... [--out 결과 폴더] [--reuse always|default|분,토큰]");
            Console.WriteLine("  --reuse 를 주면 문제를 차례로 돌리며 실제처럼 {도메인}:reviewer 세션을 그 규칙으로 이어 쓴다. 없으면 문제마다 새 세션");
            return 0;
        default:
            casesFile = arg;
            break;
    }
}

string? ReadOrNull(string path)
{
    try { return File.ReadAllText(path); } catch { return null; }
}

void Line(string text)
{
    Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {text}");
    Console.Out.Flush();
}

if (ReadOrNull(casesFile) is not { } casesText)
{
    Console.WriteLine($"문제 묶음을 읽지 못했어요: {casesFile}");
    return 2;
}
var cases = ReviewEval.LoadCases(casesText).Where(c => only.Count == 0 || only.Contains(c.Id)).ToList();
if (cases.Count == 0)
{
    Console.WriteLine($"돌릴 문제가 없어요: {casesFile}");
    return 2;
}
var templateFile = Rules.ResolvedPrompt("github-review");
if (templateFile == null || ReadOrNull(templateFile) is not { } template)
{
    Console.WriteLine("github-review 프롬프트를 찾지 못했어요");
    return 2;
}
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var outPath = outDir ?? Path.Combine(settings.RepoDir, "local", "eval", "runs", stamp);
try { Directory.CreateDirectory(outPath); } catch { }
var helper = PipelineAccess.Helper(settings);
var map = DomainMap.Load();
var domainBook = ReviewDomainBook.Load();
var notes = DomainNotes.Load();
var home = Paths.Home;
var bookFile = Path.Combine(outPath, "sessions.json");
var sessions = WorkSessionBook.Load(bookFile);
var environmentBase = AppPaths.ProcessEnvironment(settings);
Line($"문제 {cases.Count}개 · 프롬프트 {templateFile} · 결과 {outPath}");

async Task<(bool Ok, string Out)> Git(params string[] gitArgs)
{
    try
    {
        var result = await new ProcessRunner().Run("git", gitArgs, environment: environmentBase, timeout: TimeSpan.FromSeconds(120));
        return (result.ExitCode == 0, result.Stdout + result.Stderr);
    }
    catch { return (false, ""); }
}

// source 아래 파일을 target 으로 덮어 복사한다 (이미 있으면 지우고)
void Overlay(string source, string target)
{
    if (!Directory.Exists(source)) return;
    foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
}

// 문제 폴더의 base 로 가상 레포를 만들고, head 를 얹은 작업 폴더(worktree)와 그 diff 파일을 만든다
async Task<(string Worktree, string Diff)?> PrepareFixture(ReviewEvalFixture fixture, string dir, string work)
{
    var repo = Path.Combine(work, fixture.Repo);
    var tree = Path.Combine(work, $"worktree-{fixture.Key}");
    try
    {
        Directory.CreateDirectory(repo);
        Overlay(Path.Combine(dir, "base"), repo);
    }
    catch { return null; }
    var steps = new[]
    {
        new[] { "init", "-q", "-b", "main", repo },
        new[] { "-C", repo, "config", "user.name", "Sample Bot" },
        new[] { "-C", repo, "config", "user.email", "sample-bot@example.com" },
        new[] { "-C", repo, "add", "-A" },
        new[] { "-C", repo, "commit", "-q", "-m", "feat: base" },
        new[] { "-C", repo, "worktree", "add", "-q", "-b", fixture.Branch, tree, "main" },
    };
    foreach (var step in steps)
        if (!(await Git(step)).Ok) return null;
    try { Overlay(Path.Combine(dir, "head"), tree); } catch { return null; }
    if (!(await Git("-C", tree, "add", "-A", "-N")).Ok) return null;
    var diff = await Git("-C", tree, "diff", "HEAD");
    if (!diff.Ok) return null;
    var file = Path.Combine(work, $"{fixture.Key}.diff");
    try { File.WriteAllText(file, diff.Out, new UTF8Encoding(false)); } catch { return null; }
    return (tree, file);
}

foreach (var item in cases)
{
    var sessionId = Guid.NewGuid().ToString().ToLowerInvariant();
    string? resumeId = null;
    string? freshReason = null;
    var prompt = "";
    var access = new ClaudeCommand.WriteAccess();
    var environment = new Dictionary<string, string> { ["POKEMATE_EVAL"] = "1" };
    var domain = DomainReview.Fallback;
    if (item.Fixture is { } name)
    {
        if (ReviewEval.FixtureDir(name) is not { } dir || ReviewEval.LoadFixture(dir) is not { } fixture)
        {
            Line($"{item.Id} 건너뜀 · 문제 파일 {name} 을 찾지 못했어요");
            continue;
        }
        var work = Path.Combine(outPath, $"{item.Id}-work");
        if (await PrepareFixture(fixture, dir, work) is not { } prepared)
        {
            Line($"{item.Id} 건너뜀 · 가상 레포를 만들지 못했어요");
            continue;
        }
        domain = ReviewEval.FixtureDomain(item, fixture, map);
        if (reuse != null)
        {
            resumeId = sessions.ReuseId(DomainReview.SessionKey(domain), reuse, DateTimeOffset.Now);
            freshReason = sessions.Sessions.GetValueOrDefault(DomainReview.SessionKey(domain))?.FreshReason;
        }
        var config = PipelineConfig.Load(Rules.PromptsDir, new Dictionary<PipelineRole, ClaudeCommand.WriteAccess>(), work, helper,
                                         Rules.Folder(Rules.Kind.Prompt, Rules.Layer.Shared));
        config.Domains = map;
        config.Notes = notes;
        var state = ReviewEval.FixtureState(fixture, domain, prepared.Worktree, DateTimeOffset.Now);
        prompt = JiraPipeline.Prompt(PipelineRole.Reviewer, state, config, resumeId != null, prepared.Diff) + "\n\n" + ReviewEval.PipelineTail;
        var dirs = new List<string> { Path.Combine(home, "Workspace"), Path.Combine(home, ".claude", "references"), notes.Root, work };
        dirs.AddRange(map.KnowledgePaths(domain));
        access = ReviewEval.ReviewerAccess(helper, prepared.Worktree, dirs);
        Line($"{item.Id} 시작 · 가상 레포 {fixture.Repo} · Jira 리뷰어 경로 · 도메인 {domain}");
    }
    else
    {
        if (ReviewTarget.Parse(item.Url) is not { } target) continue;
        var chosen = ReviewEval.Domain(item, target, map, domainBook);
        domain = chosen.Domain;
        if (reuse != null)
        {
            resumeId = sessions.ReuseId(DomainReview.SessionKey(domain), reuse, DateTimeOffset.Now);
            freshReason = sessions.Sessions.GetValueOrDefault(DomainReview.SessionKey(domain))?.FreshReason;
        }
        var dirs = new List<string> { Path.Combine(home, "Workspace"), Path.Combine(home, ".claude", "references"), notes.Root };
        dirs.AddRange(map.KnowledgePaths(chosen.Domain));
        access = ReviewEval.Access(helper, dirs);
        prompt = ReviewEval.Prompt(item, target, chosen.Domain, chosen.Why, chosen.Source, notes, helper, template, map.KnowledgeText(chosen.Domain), resumeId == null);
        environment = ReviewEval.Environment(target);
        Line($"{item.Id} 시작 · 도메인 {chosen.Domain}");
    }
    if (resumeId != null) sessionId = resumeId;
    Line($"{item.Id} 세션 {(resumeId == null ? "새로" + (freshReason != null ? $" ({freshReason})" : "") : "이어 쓰기")} · {sessionId}");
    var before = ReviewEval.TranscriptFile(AppPaths.ClaudeConfigDir, sessionId) is { } beforeFile && ReadOrNull(beforeFile) is { } beforeText ? beforeText.Split('\n').Length : 0;
    var started = DateTimeOffset.Now;
    var runner = new ClaudeRunner();
    var response = await runner.Run(prompt, resumeId, settings, writeTools: access.Tools, writeDirs: access.Dirs,
                                    timeout: TimeSpan.FromSeconds(2400), newSessionId: resumeId == null ? sessionId : null, automation: true,
                                    extraEnvironment: environment);
    var seconds = (DateTimeOffset.Now - started).TotalSeconds;
    var text = "";
    var ok = false;
    switch (response)
    {
        case ClaudeResponse.Success success:
            text = success.Text;
            ok = true;
            break;
        case ClaudeResponse.Failure failure:
            text = $"실행 실패: {failure.Reason.Message}";
            break;
    }
    var transcript = ReviewEval.TranscriptFile(AppPaths.ClaudeConfigDir, sessionId) is { } afterFile ? ReadOrNull(afterFile) ?? "" : "";
    var skills = ReviewEval.SkillsUsed(ReviewEval.NewLines(transcript, before));
    (RunUsage Usage, long LastContext)? totals = transcript.Length == 0 ? null : TranscriptUsage.Total(transcript);
    var contextAfter = totals?.LastContext ?? 0;
    if (reuse != null)
    {
        ReviewEval.RecordSession(sessions, DomainReview.SessionKey(domain), sessionId, ok, item.Id, runner.LastUsage, totals, DateTimeOffset.Now);
        sessions.Save(bookFile);
    }
    var cost = runner.LastUsage?.CostUSD ?? 0;
    try { File.WriteAllText(Path.Combine(outPath, $"{item.Id}.md"), text, new UTF8Encoding(false)); } catch { }
    var meta = ReviewEval.Meta(cost, seconds, skills, sessionId, runner.LastUsage, ok, domain,
                               resumed: resumeId != null, freshReason: resumeId == null ? freshReason : null, contextAfter: contextAfter);
    try { File.WriteAllText(Path.Combine(outPath, $"{item.Id}.json"), Json.Sorted(meta)!.ToJsonString(Json.Options), new UTF8Encoding(false)); } catch { }
    Line($"{item.Id} 끝 · {(ok ? "성공" : "실패")} · {TokenFormat.Usd(cost)} · {(int)(seconds / 60)}분 · 스킬 {(skills.Count == 0 ? "-" : string.Join(",", skills))}");
}

var scoreArgs = new List<string>
{
    "--directory", Path.Combine(settings.RepoDir, "mcp-server"), "run", "--quiet", "pokemate", "auto", "eval-score",
    "--cases", casesFile, "--results", outPath,
};
foreach (var id in only) { scoreArgs.Add("--case"); scoreArgs.Add(id); }
ProcessOutput? score = null;
try { score = await new ProcessRunner().Run(settings.UvPath, scoreArgs, environment: environmentBase, timeout: TimeSpan.FromSeconds(300)); } catch { }
Console.WriteLine(score?.Stdout ?? "");
Console.WriteLine(score?.Stderr ?? "");
return score?.ExitCode == 0 ? 0 : 1;
