namespace PokeMate.Core;

// Task 2 는 MateCLI.JiraWorklog 가 쓰는 WorklogDraft 의 필드와 Started 만 옮겼다.
// 너그러운 읽기(end → minutes, "10" → "10:00"), IsValid, Id, WorklogProposal 은 나중 작업이 채운다.

/// Jira 작업 시간(worklog) 기록 초안.
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

    /// Jira 가 받는 시작 시각. 예: 2026-10-22T10:00:00.000+0900 (맥과 같이 지금 시각대의 오프셋)
    public string Started(TimeZoneInfo? timeZone = null)
    {
        var offset = (int)(timeZone ?? TimeZoneInfo.Local).GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
        var sign = offset >= 0 ? "+" : "-";
        var abs = Math.Abs(offset);
        return $"{Date}T{Start}:00.000{sign}{abs / 60:00}{abs % 60:00}";
    }
}
