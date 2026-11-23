using System.Globalization;

namespace PokeMate.Core;

/// 알람 하나의 마지막 실행 기록. JSON 은 맥·파이썬 서버(alarms.py)와 같은 키 (`lastAt` 은 ISO 8601).
public sealed record AlarmRunRecord(DateTimeOffset LastAt, string Result, bool Failed, string Day, int TodayCount, string? Output = null, RunUsage? Usage = null);

/// `alarm-runs.json`: 알람 id(대문자 UUID) → 마지막 실행 기록. 파이썬 서버도 읽는다.
/// 읽다가 파일이 깨져 있으면(서버가 쓰는 도중 등) 앱이 죽지 않고 빈 기록을 돌려준다.
public static class AlarmRunLog
{
    public static string File => Path.Combine(AppPaths.DataDir, "alarm-runs.json");

    /// 알림 목록에 보일 짧은 결과
    public static string Short(string message, bool failed)
    {
        if (failed) return "실패";
        if (message.Contains("새로 생긴 것 없", StringComparison.Ordinal)) return "새 건 없음";
        if (message.Contains("기준", StringComparison.Ordinal)) return "기준 잡음";
        if (message.StartsWith("새로 ", StringComparison.Ordinal))
        {
            var rest = SwiftText.Elements(message).Skip(3);
            var n = string.Concat(rest).Split('건', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (n != null && int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return $"새 건 {n}";
        }
        if (message.Contains("알림 창에 보냈", StringComparison.Ordinal)) return "알림 보냄";
        return "성공";
    }

    /// 오늘 횟수에 세는 결과인지 (새 건 없음·기준 잡음은 실제로 한 일이 아니다)
    public static bool Counts(string result) => result != "새 건 없음" && result != "기준 잡음";

    /// 기록 한 건을 더한 새 목록 (원래 목록은 그대로).
    public static Dictionary<Guid, AlarmRunRecord> Record(IReadOnlyDictionary<Guid, AlarmRunRecord> log, Guid id, string message, bool failed,
                                                          DateTimeOffset now, string? output = null, RunUsage? usage = null, bool count = true,
                                                          TimeZoneInfo? timeZone = null)
    {
        var next = log.ToDictionary(kv => kv.Key, kv => kv.Value);
        var result = Short(message, failed);
        var day = ReportSchedule.DayString(now, timeZone);
        var before = log.TryGetValue(id, out var previous) && previous.Day == day ? previous.TodayCount : 0;
        next[id] = new AlarmRunRecord(now, result, failed, day, before + (count && Counts(result) ? 1 : 0), output, usage);
        return next;
    }

    public static int TodayTotal(IReadOnlyDictionary<Guid, AlarmRunRecord> log, IReadOnlyCollection<Guid> ids, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var day = ReportSchedule.DayString(now, timeZone);
        return log.Where(kv => ids.Contains(kv.Key) && kv.Value.Day == day).Sum(kv => kv.Value.TodayCount);
    }

    /// 읽을 수 없거나 깨졌으면 빈 기록. 다른 프로세스가 열어 둔 파일도 읽는다.
    public static Dictionary<Guid, AlarmRunRecord> Load(string? from = null)
    {
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AlarmRunRecord>>(Paths.ReadShared(from ?? File), Json.Iso8601);
            var log = new Dictionary<Guid, AlarmRunRecord>();
            foreach (var (key, value) in raw ?? new())
                if (Guid.TryParse(key, out var id) && value != null) log[id] = value;
            return log;
        }
        catch { return new Dictionary<Guid, AlarmRunRecord>(); }
    }

    /// 키 정렬·들여쓰기. 실패하면(다른 프로세스가 잡고 있는 경우 등) false 이고 이전 파일은 그대로다.
    public static bool Save(IReadOnlyDictionary<Guid, AlarmRunRecord> log, string? to = null)
    {
        try
        {
            var raw = log.ToDictionary(kv => JsonUuid(kv.Key), kv => kv.Value);
            AppPaths.WriteAtomic(to ?? File, Json.SortedPretty(raw, Json.Iso8601));
            return true;
        }
        catch { return false; }
    }

    static string JsonUuid(Guid id) => Json.UuidConverter.Format(id);
}
