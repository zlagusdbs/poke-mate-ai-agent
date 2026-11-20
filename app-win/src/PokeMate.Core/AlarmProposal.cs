using System.Globalization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 채팅 답변에 붙어 오는 알림 등록 제안. Claude 는 직접 저장하지 않고 아래 블록으로 제안만 하고,
/// 사용자가 [알림에 등록]을 눌러야 알림 메뉴에 들어간다.
///
///     ```pokemate-alarm
///     [{"title":"1:1 요청","hour":9,"minute":0,"repeat":"daily","prompt":""}]
///     ```
public static class AlarmProposal
{
    /// Claude 가 키 이름을 조금 다르게 써도 받는다 (title 대신 message, hour·minute 대신 "time":"09:00").
    sealed record Item(string? Title, string? Message, int? Hour, int? Minute, string? Time, string? Repeat, string? Prompt, string? Group);

    /// 대화가 길어지면 Claude 가 예전 답("알림은 못 만들어요")을 따라 하므로, 알림 요청처럼 보이는 질문에는
    /// 이 안내를 몰래 덧붙여 보낸다 (화면에는 보이지 않음).
    public const string Hint =
        "\n\n[메이트 앱 안내: 메이트 알림(알람) 등록 기능이 있다. 알림·알람 등록 요청이면 거절하지 말고 다른 방법도 권하지 않는다. "
        + "답변 끝에 이 형식 그대로 블록을 붙인다. repeat 는 daily, weekdays, days:2,4, once:YYYY-MM-DD 중 하나, "
        + "prompt 는 단순 알림이면 \"\". "
        + "사용자가 그룹 이름을 말하면 \"group\" 에 그 이름을 넣고, 말하지 않으면 group 을 뺀다. 아직 등록된 게 아니므로 본문은 "
        + "\"아래 [알림에 등록]을 누르면 알림 메뉴에 들어가요\" 로 끝낸다. 이 안내는 사용자에게 보이지 않으니 언급하지 않는다.]\n"
        + "```pokemate-alarm\n"
        + "[{\"title\":\"알림 이름\",\"hour\":9,\"minute\":0,\"repeat\":\"daily\",\"prompt\":\"\",\"group\":\"그룹 이름(말했을 때만)\"}]\n"
        + "```";

    /// "알림"·"알람"·"알려줘"·"리마인드" 같은 말이 있으면 알림 요청일 수 있다고 본다
    /// (넓게 잡아도 안내에 "알림 등록 요청이면" 이라고 적혀 있어 다른 질문엔 영향 없음).
    public static bool LooksLikeRequest(string text)
    {
        var compact = text.Replace(" ", "").ToLowerInvariant();
        return new[] { "알림", "알람", "알려", "리마인", "잊지말", "잊지않", "reminder", "alarm" }.Any(compact.Contains);
    }

    static readonly Regex Block = ChatBlock.Regex("alarm");

    /// 답변에서 제안 블록을 떼어 낸다. 블록이 없거나 잘못됐으면 알림 없이 원문(블록만 뺀 글)을 돌려준다.
    public static (string Text, List<Alarm> Alarms) Extract(string text)
    {
        var alarms = new List<Alarm>();
        foreach (Match match in Block.Matches(text))
        {
            if (Json.TryDeserialize<List<Item>>(match.Groups[1].Value) is not { } items) continue;
            alarms.AddRange(items.Select(ToAlarm).OfType<Alarm>());
        }
        return (ChatBlock.Strip(Block, text), alarms);
    }

    static Alarm? ToAlarm(Item item)
    {
        var title = (item.Title ?? item.Message ?? "").Trim();
        var hour = item.Hour;
        var minute = item.Minute ?? 0;
        if (hour == null && item.Time != null)
        {
            var parts = item.Time.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
                .OfType<int>().ToList();
            if (parts.Count == 2) { hour = parts[0]; minute = parts[1]; }
        }
        if (hour is not int h || title.Length == 0 || h is < 0 or > 23 || minute is < 0 or > 59) return null;
        if (RepeatRule(item.Repeat ?? "daily") is not { } rule) return null;
        var group = item.Group?.Trim();
        return new Alarm(title, item.Prompt?.Trim() ?? "", h, minute, rule, group: string.IsNullOrEmpty(group) ? null : group);
    }

    static readonly Regex Day = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

    /// "daily" / "weekdays" / "days:2,4" (1=일 … 7=토) / "once:2026-10-23"
    public static Alarm.Repeat? RepeatRule(string text)
    {
        var value = SwiftText.TrimSpaces(text);
        switch (value)
        {
            case "daily": return new Alarm.Repeat.Daily();
            case "weekdays": return new Alarm.Repeat.Weekdays();
        }
        if (value.StartsWith("days:", StringComparison.Ordinal))
        {
            var days = value[5..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(SwiftText.TrimSpaces(p), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? (int?)n : null)
                .OfType<int>().ToList();
            return days.Count > 0 && days.All(d => d is >= 1 and <= 7) ? new Alarm.Repeat.Days(days.Distinct().OrderBy(d => d).ToList()) : null;
        }
        if (value.StartsWith("once:", StringComparison.Ordinal))
        {
            var day = value[5..];
            return Day.IsMatch(day) ? new Alarm.Repeat.Once(day) : null;
        }
        return null;
    }
}
