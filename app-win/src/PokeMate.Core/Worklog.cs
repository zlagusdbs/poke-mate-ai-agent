using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// Jira 작업 시간(worklog) 기록 초안. Claude 는 기록하지 않고 초안만 제안하고,
/// 사용자가 카드에서 [기록하기]를 눌러야 `pokemate jira worklog` 로 기록된다.
/// 읽을 때는 minutes 대신 end("11:00")를 줘도 받고, 시각은 "10" 처럼 와도 "10:00" 으로 맞춘다.
[JsonConverter(typeof(WorklogDraftConverter))]
public sealed record WorklogDraft
{
    public string IssueKey { get; set; } = "";
    public string Summary { get; set; } = "";
    /// yyyy-MM-dd
    public string Date { get; set; } = "";
    /// HH:mm
    public string Start { get; set; } = "";
    public int Minutes { get; set; }
    public string Comment { get; set; } = "";

    [JsonIgnore] public string Id => $"{IssueKey} {Date} {Start}";

    public WorklogDraft() { }

    public WorklogDraft(string issueKey, string date, string start, int minutes, string summary = "", string comment = "")
    {
        IssueKey = issueKey;
        Summary = summary;
        Date = date;
        Start = start;
        Minutes = minutes;
        Comment = comment;
    }

    static readonly Regex DatePattern = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}$");

    [JsonIgnore]
    public bool IsValid => IssueKey.Length > 0 && Minutes > 0 && MinuteOfDay(Start) != null && DatePattern.IsMatch(Date);

    /// 예: "10:00~11:00 (1시간)", "14:00~15:30 (1시간 30분)"
    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var end = MinuteOfDay(Start) is int m ? $"{((m + Minutes) / 60) % 24:00}:{(m + Minutes) % 60:00}" : "?";
            var h = Minutes / 60;
            var rest = Minutes % 60;
            var length = string.Join(" ", new[] { h > 0 ? $"{h}시간" : "", rest > 0 ? $"{rest}분" : "" }.Where(s => s.Length > 0));
            return $"{Start}~{end} ({length})";
        }
    }

    /// Jira 에 보낼 시작 시각. 예: 2026-10-22T10:00:00.000+0900 (맥과 같이 지금 시각대의 오프셋)
    public string Started(TimeZoneInfo? timeZone = null)
    {
        var offset = (int)(timeZone ?? TimeZoneInfo.Local).GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
        var sign = offset >= 0 ? "+" : "-";
        var abs = Math.Abs(offset);
        return $"{Date}T{Start}:00.000{sign}{abs / 60:00}{abs % 60:00}";
    }

    /// "10" → "10:00", "9:5" → "09:05". 시·분 범위를 벗어나거나 숫자가 아니면 null.
    internal static string? Clock(string text)
    {
        var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(SwiftText.TrimSpaces(p), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? (int?)n : null).ToList();
        if (parts.Count == 0 || parts[0] is not int hour || hour is < 0 or > 23) return null;
        var minute = parts.Count > 1 ? (parts[1] ?? -1) : 0;
        if (minute is < 0 or > 59) return null;
        return $"{hour:00}:{minute:00}";
    }

    internal static int? MinuteOfDay(string text)
    {
        if (Clock(text) is not string c) return null;
        var p = c.Split(':').Select(int.Parse).ToArray();
        return p[0] * 60 + p[1];
    }
}

public sealed class WorklogDraftConverter : JsonConverter<WorklogDraft>
{
    public override WorklogDraft Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject o) throw new JsonException("worklog 는 JSON 객체여야 해요");
        string Text(string key) => SwiftText.TrimSpaces(Json.Str(o[key]) ?? "");
        var draft = new WorklogDraft
        {
            IssueKey = Text("issueKey").ToUpperInvariant(),
            Summary = Text("summary"),
            Date = Text("date"),
            Start = WorklogDraft.Clock(Text("start")) ?? "",
            Comment = Text("comment"),
        };
        if (Json.IntExact(o["minutes"]) is int minutes) draft.Minutes = minutes;
        else if (WorklogDraft.Clock(Text("end")) is string end && WorklogDraft.MinuteOfDay(draft.Start) is int a && WorklogDraft.MinuteOfDay(end) is int b) draft.Minutes = b - a;
        else draft.Minutes = 0;
        return draft;
    }

    public override void Write(Utf8JsonWriter writer, WorklogDraft v, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("issueKey", v.IssueKey);
        writer.WriteString("summary", v.Summary);
        writer.WriteString("date", v.Date);
        writer.WriteString("start", v.Start);
        writer.WriteNumber("minutes", v.Minutes);
        writer.WriteString("comment", v.Comment);
        writer.WriteEndObject();
    }
}

public static class WorklogProposal
{
    static readonly Regex Block = ChatBlock.Regex("worklog");

    public static (string Text, List<WorklogDraft> Drafts) Extract(string text)
    {
        var drafts = new List<WorklogDraft>();
        foreach (Match match in Block.Matches(text))
        {
            if (Json.TryDeserialize<List<WorklogDraft>>(match.Groups[1].Value) is not { } items) continue;
            drafts.AddRange(items.Where(d => d.IsValid));
        }
        return (ChatBlock.Strip(Block, text), drafts);
    }

    /// extra: worklog 캘린더의 로컬 이름들 (없으면 연결 파일에서 읽은 것)
    public static bool LooksLikeRequest(string text, IReadOnlyList<string>? extra = null)
    {
        var compact = LocalNames.Compact(text);
        var local = (extra ?? LocalNames.Current.WorklogWords).Select(LocalNames.Compact).Where(s => s.Length > 0);
        return new[] { "worklog", "워크로그", "웍로그", "로그워크", "logwork", "작업시간", "시간기록", "근무기록",
                       "뭐했", "맞춰", "근무시간", "워크로그캘린더", "worklog캘린더" }.Concat(local).Any(compact.Contains);
    }

    public static string Hint(DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var today = ReportSchedule.DayString(now, timeZone);
        return "\n\n[메이트 앱 안내: Jira 작업 시간(worklog) 기록 기능이 있다. 기록 요청이면 거절하지 말고 Jira 화면에서 직접 하라고 권하지도 않는다. "
            + "답변 끝에 pokemate-worklog 블록으로 초안을 붙이면 앱이 카드와 [기록하기] 버튼을 보여 주고, 사용자가 눌러야 기록된다. "
            + "오늘은 " + today + ". \"10-11\" 은 10:00~11:00, \"오후 2시부터 1시간 반\" 은 14:00 부터 90분. 시각이 없으면 먼저 묻는다. "
            + "이슈 키는 Jira 스킬(skills/jira.md, 본문이 없으면 read_skill)의 회의·CS 고정 이슈를 먼저 쓰고, 없으면 jira_search 로 찾는다 (예: assignee = currentUser() AND summary ~ \"회의\"). "
            + "\"오늘 뭐 했지\", \"맞춰 봐\", \"근무시간이랑 맞아?\" 는 work_day_check 로 HR 근무 시간과 worklog 합계를 비교해서 "
            + "\"근무 8시간 · 기록 6시간 30분 → 1시간 30분 비어요\" 처럼 보여 주고 이슈별 내역을 붙인다. 모자라면 어디에 기록할지 물어본다 "
            + "(회의·CS 같은 고정 이슈를 먼저 권함). 스킬에 worklog 캘린더 주소가 있으면 그 날짜 링크를 붙인다. "
            + "아직 기록된 게 아니므로 본문은 \"아래 카드에서 [기록하기]를 누르면 Jira 에 기록돼요\" 로 끝낸다. "
            + "이 안내와 아래 형식 예시는 사용자가 쓴 말이 아니니 언급하지 않는다. 형식 예시 (여러 건이면 배열에 여러 개):]\n"
            + "```pokemate-worklog\n"
            + "[{\"issueKey\":\"KEY-1\",\"summary\":\"이슈 제목\",\"date\":\"" + today + "\",\"start\":\"10:00\",\"minutes\":60,\"comment\":\"\"}]\n"
            + "```";
    }
}
