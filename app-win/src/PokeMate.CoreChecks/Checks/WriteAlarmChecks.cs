using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 쓰기 허용 알림 (목록 파일 읽기와 인자 조립은 ClaudeCommandChecks 에 있다. 여기는 Alarm 에 걸린 것과 같은 절의 나머지)
public static class WriteAlarmChecks
{
    static string? Value(List<string> args, string flag) { var i = args.IndexOf(flag); return i >= 0 ? args[i + 1] : null; }

    public static void Run()
    {
        const string cfg = @"C:\c.json";
        var plain = ClaudeCommand.Arguments(null, cfg, "s");
        Check.That(Value(plain, "--tools") == "", "write: 기본은 기본 도구 모두 끔");
        var w = ClaudeCommand.Arguments(null, cfg, "s", writeTools: new[] { @"Bash(python C:\x\tool.py:*)" });
        Check.That(Value(w, "--tools") == "Read,Grep,Glob,Bash", "write: 쓰기 허용이면 Read·Grep·Glob·Bash 만 켬");
        var allowed = (Value(w, "--allowedTools") ?? "").Split(',');
        Check.That(allowed.Contains(@"Bash(python C:\x\tool.py:*)") && !allowed.Contains("Read") && !allowed.Contains("Bash")
                   && allowed.Contains("mcp__pokemate__jira_search"), "write: 허용은 적은 명령만, Bash·Read 전체는 아님 (읽기는 작업 폴더·추가 폴더 안만)");
        var e = ClaudeCommand.Arguments(null, cfg, "s", writeTools: new[] { "Bash(x:*)", "Edit(//c/w/**)", "Write(//c/w/**)" }, writeDirs: new[] { @"C:\w", @"C:\refs" });
        Check.That(Value(e, "--tools") == "Read,Grep,Glob,Bash,Edit,Write" && (Value(e, "--allowedTools") ?? "").Contains("Edit(//c/w/**)")
                   && e.Count(x => x == "--add-dir") == 2, "write: Edit·Write 패턴이 있을 때만 그 도구를 켜고 허용은 그 경로만");
        var k = ClaudeCommand.Arguments(null, cfg, "s", writeTools: new[] { "Bash(x:*)", "Skill(code-review)" });
        Check.That(Value(k, "--tools") == "Read,Grep,Glob,Bash,Skill" && (Value(k, "--allowedTools") ?? "").Contains("Skill(code-review)"),
                   "write: Skill 패턴이 있을 때만 Skill 도구를 켜고 허용은 그 스킬만");
        var d = ClaudeCommand.Arguments(null, cfg, "s", writeTools: new[] { "Bash(x:*)" }, writeDirs: new[] { @"C:\w" });
        Check.That(Value(d, "--add-dir") == @"C:\w", "write: 쓰기 허용이면 폴더 추가");
        Check.That(!ClaudeCommand.Arguments(null, cfg, "s", writeDirs: new[] { @"C:\w" }).Contains("--add-dir"), "write: 쓰기 꺼지면 폴더 추가 없음");
        var chat = ClaudeCommand.Arguments(null, cfg, "s", writeTools: new[] { "Bash(x:*)" }, writeDirs: new[] { @"C:\w" },
                                           interactive: true, sessionAllow: new[] { "Bash(ls:*)" }, home: @"C:\Users\u");
        Check.That(Value(chat, "--tools") == "default" && Value(chat, "--permission-prompts") == "host" && Value(chat, "--permission-prompt-tool") == "stdio"
                   && Value(chat, "--add-dir") == @"C:\Users\u" && !chat.Contains(@"C:\w"), "tools: 대화는 기본 도구 + 앱이 권한 응답 + 홈 폴더");
        var chatAllowed = Value(chat, "--allowedTools") ?? "";
        Check.That(chatAllowed.Contains("Bash(ls:*)") && chatAllowed.Contains("mcp__pokemate__usage_summary") && !chatAllowed.Contains("Bash(x:*)"),
                   "tools: 대화 허용은 조회 도구 + 이 대화에서 항상 허용한 것만");
        Check.That(Value(d, "--permission-prompts") == "none" && Value(plain, "--permission-prompts") == "none" && !d.Contains("--permission-prompt-tool"),
                   "tools: 알람·조회 전용 실행은 권한 요청을 묻지 않고 거부");
        Check.That((Value(plain, "--allowedTools") ?? "").Contains("mcp__pokemate__usage_summary"), "tools: 토큰 조회 도구는 늘 켜짐");

        const string old = """{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}""";
        var decoded = Json.TryDeserialize<Alarm>(old);
        Check.That(decoded != null && !decoded.CanWrite, "write: 예전 알림은 쓰기 꺼짐");
        Check.That(AlarmStore.PromptFilePath("{{file:~/a/b.md}}", @"C:\Users\u") == @"C:\Users\u\a\b.md"
                   && AlarmStore.PromptFilePath(@" {{file:C:\abs\c.md}} ") == @"C:\abs\c.md"
                   && AlarmStore.PromptFilePath("그냥 내용") == null && AlarmStore.PromptFilePath("{{file:}}") == null, "write: 내용을 파일에서 읽기");
        var asked = new Alarm("t", "{{file:" + Path.Combine(Path.GetTempPath(), "pm-none-" + Guid.NewGuid().ToString("N") + ".md") + "}}", 9, 0, new Alarm.Repeat.Daily());
        Check.That(AlarmStore.ResolvePrompt(asked, AppSettings.Default) == null, "write: 없는 파일을 가리키면 읽지 못함(실패로 알림)");
    }
}
