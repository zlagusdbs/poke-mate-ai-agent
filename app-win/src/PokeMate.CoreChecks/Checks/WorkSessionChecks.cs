using System.Text.Json;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 작업 세션 (sessions.json 은 임시 폴더에 쓴다. 다른 프로세스가 열어 둔 파일·깨진 파일 읽기는 Review Focus 3)
public static class WorkSessionChecks
{
    static RunUsage Cache(long cacheRead) => new(0, 0, cacheRead, 0, 0, 1000);

    public static async Task Run()
    {
        var line = """{"type":"result","is_error":false,"result":"ok","session_id":"s1","total_cost_usd":0.25,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":300,"cache_creation_input_tokens":40},"modelUsage":{"m":{"contextWindow":1000}}}""";
        var usage = RunUsage.Parse("{\"type\":\"system\"}\n" + line + "\n");
        Check.That(usage == new RunUsage(input: 10, output: 20, cacheRead: 300, cacheWrite: 40, costUSD: 0.25, contextWindow: 1000) && usage?.Context == 350 && usage?.Total == 370,
                   "session: 결과 줄에서 토큰·비용·문맥 창 읽기");
        Check.That(RunUsage.Parse("not json") == null, "session: 결과 줄 없으면 nil");
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var book = new WorkSessionBook();
        Check.That(WorkSessionBook.RepoKey(null) == "운영" && WorkSessionBook.RepoKey(" r ") == "r", "session: 레포 없으면 운영 세션");
        book.Record("r", "s1", "K-1", usage, t0);
        book.Record("r", "s1", "K-2", new RunUsage(input: 1, output: 1, cacheRead: 500, cacheWrite: 0, costUSD: 0.1, contextWindow: 1000), t0.AddSeconds(60));
        Check.That(book.Sessions["r"].Keys.SequenceEqual(new[] { "K-1", "K-2" }) && book.Sessions["r"].Usage.Total == 872 && book.Sessions["r"].ContextPercent == 50,
                   "session: 같은 세션에 티켓·토큰 누적, 문맥 % 는 마지막 실행 기준");

        var alerts = new WorkSessionBook();
        alerts.Record("a", "x", "K", Cache(650), t0);
        Check.That(alerts.ContextAlert("a", 70) == null, "alert: 문턱 아래면 알림 없음");
        alerts.Record("a", "x", "K2", Cache(720), t0);
        Check.That(alerts.ContextAlert("a", 70)?.ContextPercent == 72, "alert: 처음 넘으면 1번 알림");
        alerts.Record("a", "x", "K3", Cache(800), t0);
        Check.That(alerts.ContextAlert("a", 70) == null && alerts.Sessions["a"].ContextAlerted == true, "alert: 넘은 채로는 다시 안 보냄 (sessions.json 에 기록)");
        alerts.Record("a", "x", null, Cache(300), t0);
        Check.That(alerts.ContextAlert("a", 70) == null && alerts.Sessions["a"].ContextAlerted == null, "alert: 다시 내려가면 기록을 풂");
        alerts.Record("a", "x", null, Cache(900), t0);
        Check.That(alerts.ContextAlert("a", 70) != null, "alert: 내려갔다 다시 넘으면 다시 알림");
        alerts.Record("a", "y", null, Cache(100), t0);
        Check.That(alerts.Sessions["a"].ContextAlerted == null && alerts.ContextAlert("a", 0) == null, "alert: 새 세션은 기록 없음, 끄기(0)면 알림 없음");
        var oldSettings = Json.Deserialize<AppSettings>("""{"workSessionContextPercent":60,"workSessionDays":7}""");
        Check.That(oldSettings?.WorkSessionAlertPercent == 70, "alert: 예전 교체 설정은 무시, 기본 70%");

        book.Replace("r", "s2", "s1", t0);
        Check.That(book.Sessions["r"].Id == "s2" && book.Sessions["r"].SummaryFrom == "s1" && book.Archived.Select(s => s.Id).SequenceEqual(new[] { "s1" }), "session: 교체하면 옛 세션 보관");
        book.Record("r", "s3", "K-3", null, t0);
        Check.That(book.Sessions["r"].Id == "s3" && book.Archived.Select(s => s.Id).SequenceEqual(new[] { "s1", "s2" }), "session: 다른 id 로 기록되면 새 세션으로 보고 보관");

        // sessions.json: 임시 폴더. 폴더가 없어도 만든다.
        var dir = Check.TempDir("pm-sessions");
        var file = Path.Combine(dir, "automation", "sessions.json");
        Check.That(book.Save(file) && WorkSessionBook.Load(file).Equals(book), "session: sessions.json 저장·읽기");
        File.WriteAllText(file, """{"sessions":{}}""");
        Check.That(WorkSessionBook.Load(file).Archived.Count == 0, "session: archived 없는 예전 파일도 읽음");

        // 맥(Swift JSONEncoder .iso8601 + .sortedKeys + .prettyPrinted)이 쓴 모양 그대로 읽는다. 값이 없는 키는 없다.
        var swiftText = string.Join("\n", "{", "  \"archived\" : [", "", "  ],", "  \"sessions\" : {", "    \"r\" : {", "      \"contextTokens\" : 501,",
            "      \"contextWindow\" : 1000,", "      \"createdAt\" : \"2027-01-15T08:00:00Z\",", "      \"id\" : \"s1\",", "      \"keys\" : [", "        \"K-1\"", "      ],",
            "      \"lastUsed\" : \"2027-01-15T08:01:00Z\",", "      \"repo\" : \"r\",", "      \"rotatedAt\" : \"2027-01-15T08:00:00Z\",", "      \"usage\" : {",
            "        \"cacheRead\" : 500,", "        \"cacheWrite\" : 0,", "        \"contextWindow\" : 1000,", "        \"costUSD\" : 0.1,", "        \"input\" : 1,", "        \"output\" : 1", "      }", "    }", "  }", "}");
        File.WriteAllText(file, swiftText);
        var fromMac = WorkSessionBook.Load(file);
        Check.That(fromMac.Sessions.Count == 1 && fromMac.Sessions["r"].Id == "s1" && fromMac.Sessions["r"].ContextPercent == 50 && fromMac.Sessions["r"].LastUsed == t0.AddSeconds(60)
                   && fromMac.Sessions["r"].Usage == new RunUsage(1, 1, 500, 0, 0.1, 1000) && fromMac.Sessions["r"].SummaryFrom == null, "session: 맥이 쓴 sessions.json 을 읽음");

        // 쓰는 모양: ISO 8601 날짜(Z), 키 정렬(archived 가 sessions 앞), 없는 값은 키 자체가 없음
        var plain = new WorkSessionBook();
        plain.Record("r", "s1", "K-1", new RunUsage(1, 1, 500, 0, 0.1, 1000), t0);
        plain.Save(file);
        var written = File.ReadAllText(file);
        var order = new[] { "\"archived\"", "\"sessions\"", "\"contextTokens\"", "\"createdAt\"", "\"id\"", "\"keys\"", "\"lastUsed\"", "\"repo\"", "\"rotatedAt\"", "\"usage\"" }
            .Select(k => written.IndexOf(k, StringComparison.Ordinal)).ToList();
        Check.That(written.Contains("\"createdAt\": \"2027-01-15T08:00:00Z\"") && order.All(i => i >= 0) && order.SequenceEqual(order.OrderBy(i => i))
                   && !written.Contains("summaryFrom") && !written.Contains("freshReason") && !written.Contains("contextAlerted"), "session: sessions.json 은 ISO 8601 날짜·키 정렬·값 없는 키 생략");

        // 파이썬 서버가 쓰는 도중 읽거나(깨진 JSON) 열어 둔 파일은 앱이 죽지 않는다 (Review Focus 3)
        File.WriteAllText(file, "{\"sessions\":{\"r\":{\"id\":\"s");
        Check.That(WorkSessionBook.Load(file).Sessions.Count == 0 && WorkSessionBook.Load(Path.Combine(dir, "없음.json")).Sessions.Count == 0, "session: 깨진·없는 sessions.json 은 빈 값");
        plain.Save(file);
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Check.That(WorkSessionBook.Load(file).Equals(plain), "session: 다른 프로세스가 쓰기로 열어 둔 sessions.json 도 읽음");
            Check.That(!book.Save(file) && WorkSessionBook.Load(file).Equals(plain), "session: 열려 있어 저장하지 못하면 false, 이전 파일은 그대로");
        }
        Check.That(book.Save(file) && WorkSessionBook.Load(file).Equals(book), "session: 닫힌 뒤에는 저장됨");
        Check.Remove(dir);

        Check.That(WorkRoute.Extract("레포 정함\n```pokemate-route\n{\"repo\":\"price-service\"}\n```")?.Repo == "price-service"
                   && WorkRoute.Extract("```pokemate-route\n{\"repo\":\"\"}\n```")?.Repo == "운영"
                   && WorkRoute.Extract("```pokemate-route\n{\"skip\":true}\n```") is { Repo: null }
                   && WorkRoute.Extract("없음") == null, "session: 레포 정하기 결과 읽기 (없으면 운영, skip 이면 처리 안 함)");

        var jsonl = string.Join("\n",
            """{"type":"user","timestamp":"2026-11-17T08:07:24.000Z","message":{"role":"user","content":"이번 건: K-1"}}""",
            """{"type":"assistant","timestamp":"2026-11-17T08:07:30.000Z","message":{"content":[{"type":"text","text":"계획"},{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}""",
            """{"type":"user","timestamp":"2026-11-17T08:07:31.000Z","message":{"content":[{"type":"tool_result","content":"a.txt","is_error":false}]}}""",
            """{"type":"summary","summary":"x"}""");
        var entries = SessionTranscript.Parse(jsonl);
        Check.That(entries.Select(e => e.Kind).SequenceEqual(new[] { TranscriptEntry.EntryKind.User, TranscriptEntry.EntryKind.Assistant, TranscriptEntry.EntryKind.Tool, TranscriptEntry.EntryKind.Result })
                   && entries[2].Text == "Bash 실행 중: ls" && entries[0].Time == "2026-11-17 08:07:24", "session: 대화 기록을 사용자·답·도구·결과 줄로");
        // Windows 의 claude 가 쓴 기록은 CRLF 줄 끝일 수 있다
        var crlf = SessionTranscript.Parse(jsonl.Replace("\n", "\r\n") + "\r\n");
        Check.That(crlf.Count == 4 && crlf[1].Text == "계획" && crlf[2].Text == "Bash 실행 중: ls", "session: CRLF 줄 끝의 대화 기록도 읽음");
        var errorRow = SessionTranscript.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","content":[{"type":"text","text":"실패"}],"is_error":true}]}}""");
        Check.That(errorRow.Count == 1 && errorRow[0].Kind == TranscriptEntry.EntryKind.Result && errorRow[0].Text == "오류: 실패", "session: 도구 오류 결과는 '오류: ' 를 붙임");
        var long1300 = SessionTranscript.Parse("{\"type\":\"user\",\"message\":{\"content\":\"" + new string('가', 1300) + "\"}}");
        Check.That(long1300.Count == 1 && new System.Globalization.StringInfo(long1300[0].Text).LengthInTextElements == 1201 && long1300[0].Text.EndsWith("…"), "session: 긴 글은 1200자 + …");

        var usageLines = string.Join("\n",
            """{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}}""",
            """{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":100,"cache_creation_input_tokens":20}}}""",
            """{"type":"user","message":{"content":"x"}}""",
            """{"type":"assistant","message":{"id":"m2","model":"claude-opus-5-5","usage":{"input_tokens":1,"output_tokens":2,"cache_read_input_tokens":300,"cache_creation_input_tokens":0}}}""");
        var fromLog = TranscriptUsage.Total(usageLines);
        Check.That(fromLog.Usage.Total == 438 && fromLog.Usage.Output == 7 && fromLog.LastContext == 301 && fromLog.Usage.CostUSD > 0,
                   "session: 기록에서 사용량 다시 계산 (같은 메시지 id 는 한 번만, 비용은 단가표)");
        var big = TranscriptUsage.Total("""{"type":"assistant","message":{"id":"b","model":"x","usage":{"input_tokens":1,"output_tokens":0,"cache_read_input_tokens":4000000000,"cache_creation_input_tokens":0}}}""");
        Check.That(big.Usage.Total == 4_000_000_001 && big.LastContext == 4_000_000_001, "session: 기록의 토큰은 64비트");

        var refreshed = new WorkSessionBook();
        refreshed.Record("p", "s", "K", new RunUsage(input: 999, output: 999, cacheRead: 0, cacheWrite: 0, costUSD: 9, contextWindow: 1000), t0);
        refreshed.ApplyTranscript("p", fromLog.Usage, fromLog.LastContext);
        Check.That(refreshed.Sessions["p"].Usage.Total == 438 && refreshed.Sessions["p"].ContextPercent == 30, "session: 기록 기준으로 덮어써서 두 번 세지 않음 (문맥 창은 유지)");

        var queue = new SerialQueue();
        var log = new List<string>();
        var gate = new object();
        void Add(string s) { lock (gate) log.Add(s); }
        var a1 = queue.Run("r", async () => { Add("a-start"); await Task.Delay(150); Add("a-end"); });
        var b1 = queue.Run("r", () => { Add("b"); return Task.CompletedTask; });
        var c1 = queue.Run("other", () => { Add("c"); return Task.CompletedTask; });
        await Task.WhenAll(a1, b1, c1);
        Check.That(log.IndexOf("b") > log.IndexOf("a-end"), "session: 같은 세션 두 건은 차례로 (큐)");
        Check.That(log.IndexOf("c") < log.IndexOf("a-end"), "session: 다른 세션은 기다리지 않음");
        var boom = queue.Run("r", () => throw new InvalidOperationException("실패"));
        var after = queue.Run("r", () => { Add("after-boom"); return Task.CompletedTask; });
        try { await boom; } catch { }
        await after;
        Check.That(log.Contains("after-boom"), "session: 앞 작업이 실패해도 다음 건은 실행");
    }
}
