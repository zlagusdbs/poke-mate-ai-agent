using System.Text;
using System.Text.Json.Nodes;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

/// 검사에서 하위 프로세스로 띄우는 작은 도구들. 이 실행 파일을 인자 하나로 다시 부른다.
///   --echo-args a b …   인자를 한 줄씩 (UTF-8)
///   --echo-stdin        표준 입력을 그대로
///   --sleep <pidFile>   PID 를 파일에 쓰고 30초 기다림
///   --fake-claude       stream-json 을 CRLF 줄 끝으로 내는 가짜 claude (권한 요청 한 번)
public static class FakeTools
{
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal)) return null;
        var utf8 = new UTF8Encoding(false);
        switch (args[0])
        {
            case "--echo-args":
                Console.OutputEncoding = utf8;
                foreach (var a in args.Skip(1)) Console.Out.Write(a + "\n");
                Console.Out.Write("cwd=" + Environment.CurrentDirectory + "\n");
                Console.Out.Write("env=" + Environment.GetEnvironmentVariable("PM_CHECK_VALUE") + "\n");
                return 0;
            case "--echo-stdin":
                Console.InputEncoding = utf8;
                Console.OutputEncoding = utf8;
                Console.Out.Write(Console.In.ReadToEnd());
                return 0;
            case "--sleep":
                File.WriteAllText(args[1], Environment.ProcessId.ToString());
                Thread.Sleep(30_000);
                return 0;
            case "--fake-claude":
                return FakeClaude(utf8);
            default:
                return null;
        }
    }

    static int FakeClaude(Encoding utf8)
    {
        Console.InputEncoding = utf8;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\r\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        var first = Json.ParseObject(stdin.ReadLine());
        var question = Json.Str((first?["message"]?["content"] as JsonArray)?.LastOrDefault()?["text"]) ?? "?";
        stdout.WriteLine("""{"type":"system","subtype":"init","cwd":"C:\\Users\\홍 길동"}""");
        stdout.WriteLine("""{"type":"control_request","request_id":"q1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"dir \"C:\\Users\\홍 길동\"","description":"목록 보기"},"permission_suggestions":[]}}""");
        var answer = Json.ParseObject(stdin.ReadLine());
        var behavior = Json.Str(answer?["response"]?["response"]?["behavior"]) ?? "none";
        stdout.WriteLine("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"C:\\Users\\홍 길동\\메모.md"}}]}}""");
        var result = new JsonObject
        {
            ["type"] = "result", ["is_error"] = false, ["result"] = $"답: {question} / {behavior}", ["session_id"] = "s-한글",
            ["usage"] = new JsonObject { ["input_tokens"] = 7, ["output_tokens"] = 3 }, ["total_cost_usd"] = 0.5,
        };
        stdout.WriteLine(result.ToJsonString(Json.Compact));
        stdin.ReadToEnd();   // 앱이 result 를 받고 입력을 닫을 때까지
        return 0;
    }
}
