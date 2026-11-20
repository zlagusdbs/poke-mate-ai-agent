using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 대화 기록 한 칸. JSON 은 맥과 같은 키 (`date` 는 Swift 기본 형식: 2001 기준 초). 예전 기록(이미지·알림 없음)도 읽는다.
/// 이름 규칙: Swift 의 중첩 enum `Role` 은 속성 `Role` 과 겹쳐 `RoleKind` 로 둔다 (JSON 값은 user·assistant·error).
public sealed record ChatMessage
{
    public enum RoleKind { User, Assistant, Error }

    public Guid Id { get; init; } = Guid.NewGuid();
    public RoleKind Role { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;

    List<string> images = new();
    List<Alarm> alarms = new();
    List<RuleNote> rules = new();
    List<WorklogDraft> worklogs = new();
    List<string> worklogsDone = new();
    List<NoteDraft> notes = new();
    List<string> notesSaved = new();

    /// 첨부 이미지 파일 경로 (앱 데이터 폴더의 attachments\ 아래)
    public List<string> Images { get => images; init => images = value ?? new(); }
    /// 답변이 등록을 제안한 알림 (사용자가 버튼을 눌러야 등록된다)
    public List<Alarm> Alarms { get => alarms; init => alarms = value ?? new(); }
    /// 답변이 제안한 Jira 이슈 초안 (카드에서 고칠 수 있음)과 등록 결과
    public JiraDraft? Jira { get; set; }
    public JiraCreated? JiraCreated { get; set; }
    /// 답변이 제안한 규칙과 저장 여부
    public List<RuleNote> Rules { get => rules; set => rules = value ?? new(); }
    public bool RulesSaved { get; set; }
    /// 답변이 제안한 worklog 와 기록 끝난 것(id)
    public List<WorklogDraft> Worklogs { get => worklogs; set => worklogs = value ?? new(); }
    public List<string> WorklogsDone { get => worklogsDone; set => worklogsDone = value ?? new(); }
    /// 답변이 제안한 메모와 저장한 파일 경로
    public List<NoteDraft> Notes { get => notes; set => notes = value ?? new(); }
    public List<string> NotesSaved { get => notesSaved; set => notesSaved = value ?? new(); }
    public string? Session { get; set; }
    public string? RouteSuggest { get; set; }
    public bool RouteSent { get; set; }

    [JsonConstructor]
    public ChatMessage() { }

    public ChatMessage(RoleKind role, string text, DateTimeOffset? date = null, IEnumerable<string>? images = null,
                       IEnumerable<Alarm>? alarms = null, JiraDraft? jira = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Role = role;
        Text = text;
        Date = date ?? DateTimeOffset.Now;
        this.images = images?.ToList() ?? new();
        this.alarms = alarms?.ToList() ?? new();
        Jira = jira;
    }

    public bool Equals(ChatMessage? o) =>
        o is not null && Id == o.Id && Role == o.Role && Text == o.Text && Date == o.Date && Images.SequenceEqual(o.Images)
        && Alarms.SequenceEqual(o.Alarms) && Equals(Jira, o.Jira) && Equals(JiraCreated, o.JiraCreated) && Rules.SequenceEqual(o.Rules)
        && RulesSaved == o.RulesSaved && Worklogs.SequenceEqual(o.Worklogs) && WorklogsDone.SequenceEqual(o.WorklogsDone)
        && Notes.SequenceEqual(o.Notes) && NotesSaved.SequenceEqual(o.NotesSaved) && Session == o.Session
        && RouteSuggest == o.RouteSuggest && RouteSent == o.RouteSent;

    public override int GetHashCode() => HashCode.Combine(Id, Role, Text);
}

/// Claude 에게 보낼 이미지 한 장.
public sealed record ImageAttachment(string MediaType, byte[] Data, string Path)
{
    // MediaType: image/png, image/jpeg ... / Path: 화면 표시용 저장 위치

    public bool Equals(ImageAttachment? other) =>
        other is not null && MediaType == other.MediaType && Path == other.Path && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(MediaType, Path, Data.Length);
}
