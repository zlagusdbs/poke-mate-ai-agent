using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PokeMate.Core;

namespace PokeMate.Trial;

/// 시험용 가짜 claude: 시험 폴더의 data\settings.json 의 claudePath 를 이 프로그램(PokeMate.Trial.exe)으로 두면, claude 처럼 불렸을 때
/// (`-p … --input-format stream-json …`, POKEMATE_TRIAL 이 있을 때) 실제 Claude 대신 정해 둔 답을 낸다. 실제 계정·Jira·GitHub 는 쓰지 않는다.
/// 받은 프롬프트는 `<시험 폴더>\fake-claude\{번호}-{건}-{역할}.md` 에 남긴다 (세션 인자 포함). 세션 id 는 --resume / --session-id 값을 그대로 돌려준다.
///
/// 답 (역할·티켓별 차례로):
///   PM: push 뒤(stop 만 허용) stop · 테스트 통과면 reviewer · ACME-103 은 처음엔 확인 필요로 stop(waiting), 답이 달린 뒤엔 stop · W 가 없으면 dev(레포·브랜치)
///   개발: W 의 pricing.py 에 함수를 더하고 tester · 테스터: ACME-101 의 첫 차례는 dev 로 되돌림, 그 밖은 pm · 리뷰어: push
///   PR 리뷰: 1·2번째는 댓글(결함·미확인 하나씩), 3번째부터 승인
static class FakeClaude
{
    public static bool Matches(string[] args) =>
        Environment.GetEnvironmentVariable("POKEMATE_TRIAL") is { Length: > 0 } && args.Contains("-p") && args.Contains("--input-format");

    public static int Run(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        Console.InputEncoding = utf8;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        var root = Environment.GetEnvironmentVariable("POKEMATE_TRIAL")!;
        var first = Json.ParseObject(stdin.ReadLine());
        var prompt = Json.Str((first?["message"]?["content"] as JsonArray)?.LastOrDefault()?["text"]) ?? "";
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var resume = Arg("--resume");
        var sessionId = resume ?? Arg("--session-id") ?? Guid.NewGuid().ToString();

        var head = prompt.Split('\n').FirstOrDefault() ?? "";
        string caseName, role;
        var jira = Regex.Match(head, @"^이번 건: (\S+) · 역할: (\S+)");
        var review = Regex.Match(head, @"^이번 건: (\S+) 리뷰");
        if (jira.Success) (caseName, role) = (jira.Groups[1].Value, jira.Groups[2].Value);
        else if (review.Success) (caseName, role) = (review.Groups[1].Value, "리뷰");
        else (caseName, role) = ("알수없음", "알수없음");
        var turn = Count(root, $"{caseName}:{role}");

        var dir = Path.Combine(root, "fake-claude");
        Directory.CreateDirectory(dir);
        var number = Directory.GetFiles(dir, "*.md").Length + 1;
        var safe = Regex.Replace($"{caseName}-{role}", @"[^\w\-가-힣]", "_");
        File.WriteAllText(Path.Combine(dir, $"{number:D2}-{safe}.md"),
                          $"<!-- {(resume != null ? "--resume" : "--session-id")} {sessionId} · {role} {turn}번째 -->\n{prompt}", utf8);

        var text = role switch
        {
            "PM" => Pm(caseName, prompt),
            "개발" => Dev(prompt),
            "테스터" => caseName == "ACME-101" && turn == 1 ? Next("dev", "음수 가격 테스트가 없다. tests/test_pricing.py 에 추가할 것") : Next("pm", "테스트 3건 통과"),
            "리뷰어" => Next("push", "결함: 없음\n미확인(질문): 없음\n판정: 승인"),
            "리뷰" => turn <= 2
                ? $"{caseName} · 댓글 · 결함 1건\n결함:\n- pricing.py:3 음수 가격 검사가 없다\n미확인(질문):\n- 할인 상한 정책을 확인해야 한다"
                : $"{caseName} · 승인 · 결함 없음",
            _ => "알 수 없는 요청",
        };
        stdout.WriteLine(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId }.ToJsonString(Json.Compact));
        var result = new JsonObject
        {
            ["type"] = "result", ["is_error"] = false, ["result"] = text, ["session_id"] = sessionId,
            ["usage"] = new JsonObject { ["input_tokens"] = 1000, ["cache_read_input_tokens"] = 3000 * turn, ["output_tokens"] = 200 },
            ["total_cost_usd"] = 0.01,
        };
        stdout.WriteLine(result.ToJsonString(Json.Compact));
        return 0;
    }

    static string Next(string next, string note, string extra = "") =>
        $"가짜 답\n```pokemate-next\n{new JsonObject { ["next"] = next, ["note"] = note }.ToJsonString(Json.Compact).TrimEnd('}')}{extra}}}\n```";

    static string Pm(string key, string prompt)
    {
        if (prompt.Contains("next 는 다음 중 하나: stop")) return Next("stop", key == "ACME-103" ? "답을 확인했다. 이번 건은 개발 없이 닫는다" : "결과 댓글·알림을 남겼다");
        if (prompt.Contains("- 테스트 통과: 예")) return Next("reviewer", "테스트 통과. 리뷰를 맡긴다");
        if (key == "ACME-103")
            return prompt.Contains("확인 필요 댓글에 답이 달렸다") ? Next("stop", "답을 확인했다. 이번 건은 개발 없이 닫는다")
                                                          : Next("stop", "할인 상한을 확인 필요 댓글로 물었다", ", \"waiting\": true");
        if (prompt.Contains("- 작업 폴더 W: 아직 없음"))
            return Next("dev", "pricing.py 에 discount_price 를 추가한다",
                        $", \"repo\": \"acme-sample-service\", \"branch\": \"feature/{key}\", \"create\": [{{\"branch\": \"feature/{key}\", \"from\": \"origin/main\"}}], \"message\": \"feat: add discount price\"");
        return Next("dev", "지적을 고친다");
    }

    static string Dev(string prompt)
    {
        var w = Regex.Match(prompt, @"- 작업 폴더 W: (.+)");
        if (w.Success && Directory.Exists(w.Groups[1].Value.Trim()))
            File.AppendAllText(Path.Combine(w.Groups[1].Value.Trim(), "pricing.py"),
                               "\n\ndef discount_price(price, percent):\n    return price * (100 - percent) // 100\n", new UTF8Encoding(false));
        return Next("tester", "pricing.py 에 discount_price 를 더했다");
    }

    /// 건·역할마다 몇 번째 부름인지 (시험 폴더의 fake-claude\counts.json)
    static int Count(string root, string key)
    {
        var file = Path.Combine(root, "fake-claude", "counts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var counts = (File.Exists(file) ? Json.ParseObject(File.ReadAllText(file)) : null) ?? new JsonObject();
        var n = (int)(Json.Long(counts[key]) ?? 0) + 1;
        counts[key] = n;
        File.WriteAllText(file, counts.ToJsonString(Json.Options));
        return n;
    }
}
