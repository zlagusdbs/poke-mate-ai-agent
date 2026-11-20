using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 트리거 알림: 연결된 시스템에서 어떤 일이 생기면 울린다.
/// JSON 키: system, event, intervalMinutes (맥과 같음)
public sealed record Trigger(string System, string Event, int? IntervalMinutes = null)
{
    public const int DefaultInterval = 5;
    /// 고를 수 있는 주기 (분)
    public static readonly IReadOnlyList<int> Intervals = new[] { 1, 3, 5, 10, 30, 60 };

    /// 몇 분마다 확인할지. 없으면 5분 (예전 알림 파일 호환)
    [JsonIgnore]
    public int Interval => Math.Max(1, IntervalMinutes ?? DefaultInterval);

    /// 지금 확인할 때인지. 한 번도 안 봤거나 마지막 확인에서 주기만큼 지났으면 true.
    public bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset now)
    {
        if (lastChecked is not DateTimeOffset last) return true;
        return (now - last).TotalSeconds >= Interval * 60 - 5;   // 타이머 오차 5초 허용
    }
}

/// 고를 수 있는 이벤트 목록 (mcp-server 의 events.EVENTS 와 같은 id).
public sealed record TriggerEvent(string Id, string System, string Name)
{
    public static readonly IReadOnlyList<TriggerEvent> All = new[]
    {
        new TriggerEvent("github.review_requested", "github", "리뷰어로 지정됐을 때"),
        new TriggerEvent("github.my_pr_approved", "github", "내 PR 이 승인됐을 때"),
        new TriggerEvent("github.my_pr_feedback", "github", "내 PR 에 변경 요청이나 댓글이 달렸을 때"),
        new TriggerEvent("deploy.approval_requested", "deploy", "내가 승인해야 할 배포가 생겼을 때"),
        new TriggerEvent("deploy.my_release_status", "deploy", "내 배포 상태가 바뀌었을 때"),
        new TriggerEvent("jira.assigned_to_me", "jira", "나에게 새 이슈가 할당됐을 때"),
        new TriggerEvent("jira.my_issue_status", "jira", "내 이슈 상태가 바뀌었을 때"),
    };

    public static List<TriggerEvent> Events(string system) => All.Where(e => e.System == system).ToList();
    public static TriggerEvent? Find(string id) => All.FirstOrDefault(e => e.Id == id);
    public static IReadOnlyList<string> Systems => All.Select(e => e.System).Distinct().ToList();
}

/// `pokemate events <id>` 출력 한 줄.
public sealed record EventItem(string Key, string Title, string Summary, string Url);

/// 이전에 본 key 와 비교해 새로 생긴 항목을 고른다. 처음이면(기준 없음) 아무것도 알리지 않는다.
public static class TriggerDiff
{
    public static List<EventItem> NewItems(IReadOnlyList<EventItem> current, ISet<string>? seen)
    {
        if (seen == null) return new List<EventItem>();
        return current.Where(i => !seen.Contains(i.Key)).ToList();
    }
}
