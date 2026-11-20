using System.Globalization;

namespace PokeMate.Core;

/// 매일 보고를 지금 실행할지 판단한다. 잠자기로 보고 시각을 놓쳤어도 깨어난 뒤 그날 1회 실행된다.
public static class ReportSchedule
{
    public static bool ShouldRun(DateTimeOffset now, string? lastRunDay, int hour, int minute, TimeZoneInfo? timeZone = null)
    {
        var today = DayString(now, timeZone);
        if (lastRunDay == today) return false;
        var local = TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local);
        return local.Hour * 60 + local.Minute >= hour * 60 + minute;
    }

    /// `yyyy-MM-dd` (지정한 시간대의 날짜)
    public static string DayString(DateTimeOffset date, TimeZoneInfo? timeZone = null) =>
        TimeZoneInfo.ConvertTime(date, timeZone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
