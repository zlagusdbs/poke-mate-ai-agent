using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 토큰 집계
public static class TokenUsageChecks
{
    static string Line(string id, string ts, string model = "claude-opus-5-5", int input = 10, int output = 5) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + ts + "\",\"requestId\":\"r" + id + "\",\"message\":{\"id\":\"m" + id + "\",\"model\":\"" + model
        + "\",\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":" + output + ",\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":100}}}";

    public static void Run()
    {
        var root = Check.TempDir("pm-usage");
        var proj = Path.Combine(root, "projects", "p");
        Directory.CreateDirectory(proj);
        var seoul = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var a = Path.Combine(proj, "a.jsonl");
        var b = Path.Combine(proj, "b.jsonl");
        File.WriteAllText(a, Line("1", "2026-11-17T01:00:00.000Z") + "\n" + Line("1", "2026-11-17T01:00:00.000Z") + "\n"
                             + Line("s", "2026-11-17T01:00:00.000Z", model: "<synthetic>") + "\n");
        // Windows 에서 쓴 줄 끝(CRLF)도 같이 읽는다
        File.WriteAllText(b, Line("1", "2026-11-17T01:00:00.000Z") + "\r\n" + Line("2", "2026-11-16T14:30:00.000Z") + "\r\n");
        var cacheFile = Path.Combine(root, "cache.json");
        var now = DateTimeOffset.Parse("2026-11-17T05:00:00Z");
        var reader = new ClaudeUsageReader(new[] { Path.Combine(root, "projects") }, cacheFile);
        var s1 = reader.Refresh(now, seoul);
        Check.That(s1.Today.Total == 115 && s1.Month.Total == 230, "usage: 같은 메시지는 파일이 달라도 한 번만, synthetic 제외 (KST 날짜)");
        var expectedCost = 160.0 / 1_000_000;
        Check.That(Math.Abs(s1.TodayCost - expectedCost) < 1e-12, "usage: opus-5-5 단가로 비용");
        using (var h = new FileStream(b, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Line("3", "2026-11-17T02:00:00.000Z", input: 1, output: 1) + "\n" + """{"type":"assistant","timestamp":""");
            h.Write(bytes);
        }
        var s2 = new ClaudeUsageReader(new[] { Path.Combine(root, "projects") }, cacheFile).Refresh(now, seoul);
        Check.That(s2.Today.Total == 217, "usage: 캐시 이어 읽기, 끝이 잘린 줄은 다음에");
        Check.That(s2.Month.Total == 332, "usage: 이번 달 합계");
        Check.That(s2.Daily.Count == ClaudeUsageReader.DailyDays && s2.Daily[^1].Day == "2026-11-17" && s2.Daily[^1].Total == 217
                   && s2.Daily[^2].Total == 115 && s2.Daily[0].Total == 0, "usage: 최근 30일 일별 (빈 날은 0)");
        Check.That(TokenFormat.Compact(1_234) == "1.2K" && TokenFormat.Compact(2_500_000) == "2.5M" && TokenFormat.Usd(1.5) == "$1.50" && TokenFormat.Compact(999) == "999",
                   "usage: 짧은 숫자 표기 (문화권과 무관하게 점)");
        var snap = Json.ParseObject(s2.Snapshot(now));
        Check.That(Json.Int(snap?["today"]?["tokens"]?["total"]) == 217 && Json.Str(snap?["updated"])?.Length > 0, "usage: usage.json 스냅숏");
        Check.Remove(root);

        var run = RunUsage.Parse("{\"type\":\"system\"}\r\n{\"type\":\"result\",\"usage\":{\"input_tokens\":3,\"output_tokens\":4,\"cache_read_input_tokens\":5,\"cache_creation_input_tokens\":6},\"total_cost_usd\":0.25,\"modelUsage\":{\"m\":{\"contextWindow\":200000}}}\r\n");
        Check.That(run == new RunUsage(input: 3, output: 4, cacheRead: 5, cacheWrite: 6, costUSD: 0.25, contextWindow: 200000) && run.Total == 18 && run.Context == 14,
                   "usage: 한 번 실행 사용량 (result 줄)");
        Check.That(RunUsage.Parse("not json") == null, "usage: result 줄이 없으면 없음");
    }
}
