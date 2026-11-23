using PokeMate.Core;

namespace PokeMate.CoreChecks;

// Review Focus 2: claude.exe 가 UTF-8 로 내는 한글과 CRLF 줄 끝이 stream-json 해석을 깨지 않는다
public static class ClaudeStreamChecks
{
    public static async Task Run()
    {
        var crlf = ClaudeStream.Object("{\"type\":\"result\",\"result\":\"한글 ✔\"}\r");
        Check.That(Json.Str(crlf?["result"]) == "한글 ✔", "stream: CR 이 남은 줄도 JSON 으로 읽음");
        Check.That(ClaudeStream.Object("") == null && ClaudeStream.Object("[1]") == null && ClaudeStream.Object("{깨짐") == null, "stream: 객체가 아닌 줄은 무시");
        const string stream = "{\"type\":\"system\"}\r\n{\"type\":\"result\",\"is_error\":false,\"result\":\"줄1\\r\\n줄2\",\"session_id\":\"s1\"}\r\n";
        Check.That(ClaudeResponse.Parse(stream, 0) == new ClaudeResponse.Success("줄1\r\n줄2", "s1"), "stream: CRLF stream-json 의 result 줄");

        var (exe, prefix) = ProcessRunnerChecks.Self();
        var activities = new List<string>();
        var requests = new List<PermissionRequest>();
        var lines = new List<string>();
        var interactive = new InteractiveProcess();
        var output = await interactive.Run(exe, prefix.Append("--fake-claude").ToList(),
            firstLine: ClaudeCommand.InputLine("한글 질문 \"따옴표\"", Array.Empty<ImageAttachment>()),
            workDir: null, environment: null, timeout: TimeSpan.FromSeconds(60),
            onLine: async (line, io) =>
            {
                lines.Add(line);
                var obj = ClaudeStream.Object(line);
                if (obj == null) return;
                if (ClaudeStream.PermissionRequest(obj) is { } request)
                {
                    requests.Add(request);
                    await Task.Yield();
                    io.Send(ClaudeStream.Response(request, new PermissionDecision.Deny("안 돼요")));
                    return;
                }
                foreach (var use in ClaudeStream.ToolUses(obj)) activities.Add(ClaudeStream.Activity(use.Name, use.Input));
                if (Json.Str(obj["type"]) == "result") io.CloseInput();
            });
        Check.That(!output.TimedOut && output.ExitCode == 0 && lines.Count == 4 && lines.All(l => !l.EndsWith("\r")), "stream: CRLF 줄 끝을 떼고 한 줄씩 받음");
        Check.That(requests.Count == 1 && requests[0].Summary == "dir \"C:\\Users\\홍 길동\"" && requests[0].Detail == "목록 보기", "stream: 한글 권한 요청 읽기");
        Check.That(activities.SequenceEqual(new[] { @"Read 읽는 중: C:\Users\홍 길동\메모.md" }), "stream: 한글 도구 진행 한 줄");
        Check.That(ClaudeResponse.Parse(output.Stdout, output.ExitCode) == new ClaudeResponse.Success("답: 한글 질문 \"따옴표\" / deny", "s-한글"),
                   "stream: 표준 입력으로 보낸 한글 질문과 권한 응답이 그대로 전달됨");
        Check.That(RunUsage.Parse(output.Stdout)?.Total == 10 && RunUsage.Parse(output.Stdout)?.CostUSD == 0.5, "stream: 실행 사용량");
    }
}
