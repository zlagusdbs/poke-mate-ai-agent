using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 알람 실행 기록 (+ 명령 알람 실행 기록, Review Focus 3: 깨진 JSON → 빈 값)
public static class AlarmRunLogChecks
{
    public static void Run()
    {
        var tz = Check.Seoul;
        Check.That(new[]
                   {
                       AlarmRunLog.Short("끝났어요. 결과는 알림 창과 채팅에 있어요", false), AlarmRunLog.Short("x", true),
                       AlarmRunLog.Short("새로 생긴 것 없어요", false), AlarmRunLog.Short("새로 3건, 알림 창에 보냈어요", false),
                       AlarmRunLog.Short("처음이라 지금 있는 5건을 기준으로 잡았어요", false), AlarmRunLog.Short("알림 창에 보냈어요", false),
                   }.SequenceEqual(new[] { "성공", "실패", "새 건 없음", "새 건 3", "기준 잡음", "알림 보냄" }), "alarmlog: 결과 문구 짧게");
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
        var log = AlarmRunLog.Record(new Dictionary<Guid, AlarmRunRecord>(), id, "끝났어요", false, t, timeZone: tz);
        log = AlarmRunLog.Record(log, id, "새로 생긴 것 없어요", false, t.AddSeconds(60), timeZone: tz);
        log = AlarmRunLog.Record(log, other, "x", true, t, timeZone: tz);
        Check.That(log[id].TodayCount == 1 && log[id].Result == "새 건 없음" && AlarmRunLog.TodayTotal(log, new HashSet<Guid> { id, other }, t, tz) == 2,
                   "alarmlog: 오늘 횟수는 실제로 한 일만, 마지막 결과는 늘 갱신");
        log = AlarmRunLog.Record(log, id, "끝났어요", false, t.AddSeconds(86_400), timeZone: tz);
        Check.That(log[id].TodayCount == 1 && log[id].Day == "2026-11-18", "alarmlog: 날이 바뀌면 오늘 횟수 새로");
        var dir = Check.TempDir("pm-runs");
        var file = Path.Combine(dir, "alarm-runs.json");
        AlarmRunLog.Save(log, file);
        var back = AlarmRunLog.Load(file);
        Check.That(back.Count == log.Count && log.All(kv => back.TryGetValue(kv.Key, out var r) && r == kv.Value), "alarmlog: 저장하고 다시 읽음");
        var text = File.ReadAllText(file);
        Check.That(text.Contains($"\"{id.ToString().ToUpperInvariant()}\"") && text.Contains("\"lastAt\": \"2026-11-18T01:00:00Z\"") && text.Contains("\"todayCount\": 1"),
                   "alarmlog: 저장 형식은 맥·파이썬 서버와 같음 (대문자 UUID 키, ISO 8601 날짜)");

        // 명령 실행 알람: 실행 기록에 출력을 남김
        var withOutput = AlarmRunLog.Record(new Dictionary<Guid, AlarmRunRecord>(), Guid.NewGuid(), "종료 코드 3", true, DateTimeOffset.Now, output: "oops");
        Check.That(withOutput.Values.First().Output == "oops" && withOutput.Values.First().Result == "실패", "command: 실행 기록에 출력 남김");
        var usage = new RunUsage(1, 2, 3, 4, 0.5, 200_000);
        var withUsage = AlarmRunLog.Record(new Dictionary<Guid, AlarmRunRecord>(), id, "끝났어요", false, t, usage: usage, timeZone: tz);
        AlarmRunLog.Save(withUsage, file);
        var usageBack = AlarmRunLog.Load(file)[id].Usage;
        Check.That(usageBack?.Input == 1 && usageBack.CacheWrite == 4 && usageBack.CostUSD == 0.5 && usageBack.ContextWindow == 200_000
                   && File.ReadAllText(file).Contains("\"costUSD\": 0.5"), "alarmlog: 토큰 사용량도 저장 (키 costUSD)");

        // 토큰 수는 64비트 (캐시 읽기만으로 20억을 넘을 수 있다)
        var huge = new RunUsage(cacheRead: 5_000_000_000L, input: 3);
        var hugeLog = AlarmRunLog.Record(new Dictionary<Guid, AlarmRunRecord>(), id, "끝났어요", false, t, usage: huge, timeZone: tz);
        AlarmRunLog.Save(hugeLog, file);
        Check.That(AlarmRunLog.Load(file)[id].Usage?.CacheRead == 5_000_000_000L && huge.Total == 5_000_000_003L && TokenFormat.Compact(huge.Total) == "5.00B"
                   && new TokenBucket(cacheRead: 3_000_000_000L).Total == 3_000_000_000L
                   && RunUsage.Parse("{\"type\":\"result\",\"usage\":{\"cache_read_input_tokens\":4000000000,\"input_tokens\":1}}")?.Total == 4_000_000_001L,
                   "usage: 20억을 넘는 토큰 수도 읽고 저장함 (64비트)");

        // Review Focus 3: 파이썬 서버가 쓰는 중이라 깨진 JSON 을 읽어도 죽지 않고 빈 값
        File.WriteAllText(file, "{\"6F1D6F5E-0000-0000-0000-000000000001\": {\"lastAt\": \"2026-11-1");
        Check.That(AlarmRunLog.Load(file).Count == 0, "alarmlog: 쓰다 만 깨진 JSON 은 빈 기록 (앱이 죽지 않음)");
        File.WriteAllText(file, "");
        Check.That(AlarmRunLog.Load(file).Count == 0, "alarmlog: 빈 파일도 빈 기록");
        File.WriteAllText(file, "[1,2]");
        Check.That(AlarmRunLog.Load(file).Count == 0, "alarmlog: 모양이 다른 JSON 도 빈 기록");
        Check.That(AlarmRunLog.Load(Path.Combine(dir, "none.json")).Count == 0, "alarmlog: 파일이 없으면 빈 기록");
        File.WriteAllText(file, """{"not-a-uuid":{"lastAt":"2026-11-17T01:00:00Z","result":"성공","failed":false,"day":"2026-11-17","todayCount":1}}""");
        Check.That(AlarmRunLog.Load(file).Count == 0, "alarmlog: UUID 가 아닌 키는 버림");

        // 파이썬 서버가 파일을 열어 둔 채(읽기만 허용)여도 읽을 수 있고, 저장은 실패를 알리고 임시 파일을 남기지 않음
        AlarmRunLog.Save(log, file);
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Check.That(AlarmRunLog.Load(file).Count == log.Count, "alarmlog: 다른 프로세스가 열어 둔 파일도 읽음");
        }
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var saved = AlarmRunLog.Save(new Dictionary<Guid, AlarmRunRecord>(), file);
            Check.That(!saved && !Directory.GetFiles(dir, "*.tmp-*").Any(), "alarmlog: 못 바꿔 넣으면 실패를 알리고 임시 파일을 남기지 않음");
        }
        Check.That(AlarmRunLog.Load(file).Count == log.Count, "alarmlog: 저장에 실패해도 이전 기록은 그대로");
        Check.Remove(dir);
    }
}
