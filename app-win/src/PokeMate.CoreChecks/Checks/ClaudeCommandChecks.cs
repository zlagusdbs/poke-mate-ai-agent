using System.Text.Json;
using System.Text.Json.Nodes;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: ClaudeCommand
public static class ClaudeCommandChecks
{
    static string Value(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

    public static void Run()
    {
        const string cfg = @"C:\c.json";
        var a = ClaudeCommand.Arguments(sessionId: null, mcpConfigPath: cfg, systemPrompt: "sys");
        Check.That(a[0] == "-p" && a.Contains("stream-json") && !a.Contains("질문"), "arguments: 질문은 인자가 아니라 표준 입력으로");
        var line = ClaudeCommand.InputLine(prompt: "질문", images: new[] { new ImageAttachment("image/png", new byte[] { 1, 2 }, @"C:\x") });
        var parsed = Json.ParseObject(line);
        var content = parsed?["message"]?["content"] as JsonArray;
        Check.That(content?.Count == 2 && Json.Str(content[0]?["type"]) == "image" && Json.Str(content[1]?["text"]) == "질문"
                   && line.EndsWith("\n") && line.IndexOf('\n') == line.Length - 1, "inputLine: 이미지 먼저, 질문 뒤, 한 줄");
        Check.That(a.Contains("--mcp-config"), "arguments: 메이트 MCP 설정");
        var i = a.IndexOf("--allowedTools");
        if (i >= 0)
        {
            var allowed = a[i + 1].Split(',');
            Check.That(allowed.Contains("mcp__pokemate__jira_search") && allowed.Contains("mcp__pokemate__wiki_search")
                       && allowed.Contains("mcp__claude_ai_Microsoft_365__outlook_email_search")
                       && allowed.All(t => t.StartsWith("mcp__pokemate__") || t.StartsWith("mcp__claude_ai_Microsoft_365__")), "arguments: 허용 도구는 메이트·M365 조회만");
        }
        else Check.That(false, "arguments: 허용 도구 옵션");
        Check.That(!a.Contains("--resume"), "arguments: 세션 없으면 resume 없음");
        Check.That(!a.Contains("--model") && !a.Contains("--effort") && !a.Contains("--settings"), "arguments: 값을 안 주면 모델·추론·홈 지침 옵션 없음");
        Check.That(ClaudeCommand.Arguments(null, cfg, "s", homeExcludes: ClaudeCommand.HomeExcludes(AppSettings.Default, automation: false)).Contains("--settings")
                   && AppSettings.Default.UseHomeInstructions == false, "arguments: 앱 기본 설정은 홈 지침을 뺌");
        var homeOn = AppSettings.Default;
        homeOn.UseHomeInstructions = true;
        var ruleFiles = new[] { "cli-usage.md", "commit-pr.md", "domain-knowledge.md", "new-rule.md", "production-env.md" };
        const string u = @"C:\u";
        Check.That(ClaudeCommand.HomeExcludes(homeOn, automation: false, home: u, rules: ruleFiles).Count == 0
                   && ClaudeCommand.Arguments(null, cfg, "s", homeExcludes: Array.Empty<string>()).Contains("--settings") == false,
                   "home: 대화는 개인 지침 전부 읽기");
        Check.That(ClaudeCommand.HomeExcludes(homeOn, automation: true, home: u, rules: ruleFiles)
                   .SequenceEqual(new[] { "C:/u/.claude/CLAUDE.md", "C:/u/CLAUDE.md", "C:/u/.claude/rules/*/**", "C:/u/.claude/rules/cli-usage.md", "C:/u/.claude/rules/new-rule.md" }),
                   "home: 자동 처리는 목차 빼고 체크한 rules 만 (새 파일은 해제 상태)");
        homeOn.HomeInAutomation = false;
        homeOn.HomeInChat = false;
        Check.That(ClaudeCommand.HomeExcludes(homeOn, automation: true, home: u, rules: ruleFiles).SequenceEqual(new[] { "C:/u/.claude/**", "C:/u/CLAUDE.md" })
                   && ClaudeCommand.HomeExcludes(homeOn, automation: false, home: u, rules: ruleFiles).SequenceEqual(new[] { "C:/u/.claude/**", "C:/u/CLAUDE.md" }),
                   "home: 끄면 전부 뺌");
        var oldOn = Json.TryDeserialize<AppSettings>("""{"useHomeInstructions": true}""");
        var oldOff = Json.TryDeserialize<AppSettings>("""{"useHomeInstructions": false}""");
        Check.That(oldOn?.HomeInChat == true && oldOn.HomeInAutomation == true && oldOn.AutomationRules.SequenceEqual(new[] { "commit-pr.md", "domain-knowledge.md", "production-env.md" })
                   && oldOff?.HomeInChat == false && oldOff.HomeInAutomation == false, "home: 예전 설정 이전 (켜져 있으면 기본값, 꺼져 있으면 둘 다 끔)");
        var tuned = ClaudeCommand.Arguments(null, cfg, "s", model: "sonnet", effort: "low", homeExcludes: new[] { "/x" });
        Check.That(Value(tuned, "--model") == "sonnet" && Value(tuned, "--effort") == "low", "arguments: 모델·추론 수준");
        Check.That(ClaudeCommand.HomeInstructionExcludes(home: @"C:\Users\u") == """{"claudeMdExcludes":["C:/Users/u/.claude/**","C:/Users/u/CLAUDE.md"]}""", "arguments: 홈 지침 빼기");
        var wrong = ClaudeCommand.Arguments(null, cfg, "s", model: "gpt", effort: "ultra");
        Check.That(!wrong.Contains("--model") && !wrong.Contains("--effort"), "arguments: 모르는 값은 안 넘김");
        var b = ClaudeCommand.Arguments("s1", cfg, "sys");
        var r = b.IndexOf("--resume");
        if (r >= 0) Check.That(b[r + 1] == "s1", "arguments: resume 뒤 세션 ID");
        else Check.That(false, "arguments: 세션 있으면 resume");
        var fresh = ClaudeCommand.Arguments(null, cfg, "sys", newSessionId: "n1");
        Check.That(Value(fresh, "--session-id") == "n1" && !ClaudeCommand.Arguments("s1", cfg, "sys", newSessionId: "n1").Contains("--session-id"),
                   "arguments: 새 세션 ID 는 resume 이 없을 때만");

        // 쓰기 허용 목록 파일 (ClaudeCommand 의 Windows 경로 풀이)
        var dir = Check.TempDir("pm-write-tools");
        var file = Path.Combine(dir, "write-tools.json");
        File.WriteAllText(file, """[" Bash(git -C:*) ", ""]""");
        Check.That(ClaudeCommand.LoadWriteTools(file).SequenceEqual(new[] { "Bash(git -C:*)" }), "write: 목록 파일 읽기 (공백·빈 값 제거)");
        Check.That(ClaudeCommand.LoadWriteTools(file + ".none").Count == 0, "write: 파일 없으면 빈 목록");
        File.WriteAllText(file, """{"tools":["Bash(x:*)"],"dirs":["~/Workspace"," C:\\abs "]}""");
        Check.That(ClaudeCommand.LoadWriteAccess(file, home: @"C:\Users\u") == new ClaudeCommand.WriteAccess(new[] { "Bash(x:*)" }, new[] { @"C:\Users\u\Workspace", @"C:\abs" }),
                   "write: 도구·폴더 목록 형식 (~/ 는 홈 폴더로)");
        Check.Remove(dir);
    }
}
