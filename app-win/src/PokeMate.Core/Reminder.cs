using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 오른쪽 아래 "메이트 알림" 창의 카드 하나. 날짜는 Swift 기본 형식(2001 기준 초).
public sealed record Reminder
{
    public Guid Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public DateTimeOffset FiredAt { get; }
    public bool Failed { get; }
    /// 다시 알림으로 미뤄 둔 시각. 없으면 지금 보인다.
    public DateTimeOffset? SnoozedUntil { get; set; }
    /// 카드에서 바로 열 링크 (예: PR). 있으면 [자세히] 대신 [열기] 버튼.
    public string? Url { get; set; }
    public string? SessionRepo { get; set; }

    [JsonConstructor]
    public Reminder(Guid id = default, string title = "", string summary = "", DateTimeOffset firedAt = default, bool failed = false,
                    DateTimeOffset? snoozedUntil = null, string? url = null, string? sessionRepo = null)
    {
        Id = id == default ? Guid.NewGuid() : id;
        Title = title;
        Summary = summary;
        FiredAt = firedAt;
        Failed = failed;
        SnoozedUntil = snoozedUntil;
        Url = url;
        SessionRepo = sessionRepo;
    }

    /// 결과 첫 줄(비어 있지 않은 줄)을 요약으로 쓴다.
    public static string SummaryOf(string text) =>
        MarkdownBlocks.Plain(text).Split('\n').Select(l => SwiftText.TrimSpaces(l)).FirstOrDefault(l => l.Length > 0) ?? "";

    internal bool IsVisible(DateTimeOffset now) => SnoozedUntil is not DateTimeOffset until || until <= now;
}

/// 미리 알림 목록. 추가·확인·다시 알림과 지금 보일 카드를 계산한다.
public sealed record ReminderQueue
{
    public static readonly IReadOnlyList<int> SnoozeChoices = new[] { 5, 10, 30, 60 };

    [JsonInclude] public List<Reminder> Items { get; private set; } = new();

    public ReminderQueue() { }

    public ReminderQueue(IEnumerable<Reminder> items) { Items = items.ToList(); }

    public void Add(Reminder reminder) => Items.Add(reminder);

    public void Dismiss(Guid id) => Items.RemoveAll(r => r.Id == id);

    public void DismissVisible(DateTimeOffset now) => Items.RemoveAll(r => r.IsVisible(now));

    public void Snooze(Guid id, int minutes, DateTimeOffset now)
    {
        var item = Items.FirstOrDefault(r => r.Id == id);
        if (item != null) item.SnoozedUntil = now.AddMinutes(minutes);
    }

    /// 지금 보일 카드 (최근 것이 위).
    public List<Reminder> Visible(DateTimeOffset now) => Items.Where(r => r.IsVisible(now)).OrderByDescending(r => r.FiredAt).ToList();

    public bool Equals(ReminderQueue? o) => o is not null && Items.SequenceEqual(o.Items);
    public override int GetHashCode() => Items.Count;
}
