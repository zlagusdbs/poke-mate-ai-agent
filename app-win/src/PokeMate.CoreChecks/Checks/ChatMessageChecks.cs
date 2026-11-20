using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: ChatMessage, Reminder, ChatHints
public static class ChatMessageChecks
{
    public static void Run()
    {
        // ChatMessage
        const string old = """[{"id":"6F1D6F5E-0000-0000-0000-000000000001","role":"user","text":"hi","date":0}]""";
        var m = Json.Deserialize<List<ChatMessage>>(old);
        Check.That(m?[0].Images.Count == 0 && m[0].Alarms.Count == 0 && m[0].Role == ChatMessage.RoleKind.User && m[0].Date == Json.SwiftDateConverter.ReferenceDate,
                   "chat: 예전 대화 기록도 읽음 (이미지 없음)");
        var full = new ChatMessage(ChatMessage.RoleKind.Assistant, "t", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), new[] { "a.png" })
        {
            Rules = new List<RuleNote> { new("memory", "가격팀") }, RulesSaved = true,
            Worklogs = new List<WorklogDraft> { new() { IssueKey = "A-1", Date = "2026-10-22", Start = "10:00", Minutes = 60 } }, WorklogsDone = new List<string> { "A-1 2026-10-22 10:00" },
            Notes = new List<NoteDraft> { new("제목", "본문") }, NotesSaved = new List<string> { @"C:\n\a.md" },
            Session = "repo", RouteSuggest = "sample", RouteSent = true,
        };
        var back = Json.Deserialize<ChatMessage>(Json.Serialize(full));
        Check.That(back == full && back!.Role == ChatMessage.RoleKind.Assistant && Json.Serialize(full, indented: false).Contains("\"role\":\"assistant\""), "chat: 모든 항목 저장 왕복 (role 은 문자열)");

        // Reminder
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var q = new ReminderQueue();
        var a = new Reminder(title: "미완료 Jira 보고", summary: "미완료 Jira 3건", firedAt: t0);
        var b = new Reminder(title: "메일", summary: "답장 필요 2통", firedAt: t0.AddSeconds(60));
        q.Add(a); q.Add(b);
        Check.That(q.Visible(t0.AddSeconds(61)).Select(r => r.Id).SequenceEqual(new[] { b.Id, a.Id }), "reminder: 최근 것이 위");
        q.Snooze(a.Id, 10, t0.AddSeconds(120));
        Check.That(q.Visible(t0.AddSeconds(120 + 599)).Select(r => r.Id).SequenceEqual(new[] { b.Id }), "reminder: 다시 알림 전에는 안 보임");
        Check.That(q.Visible(t0.AddSeconds(120 + 600)).Select(r => r.Id).Contains(a.Id), "reminder: 다시 알림 시각이 되면 보임");
        q.Dismiss(b.Id);
        Check.That(!q.Items.Any(r => r.Id == b.Id), "reminder: 확인하면 사라짐");
        q.Add(b);
        q.DismissVisible(t0.AddSeconds(130));
        Check.That(q.Items.Select(r => r.Id).SequenceEqual(new[] { a.Id }), "reminder: 모두 확인은 보이는 카드만 (미룬 카드는 남음)");
        var data = Json.Serialize(q);
        Check.That(Json.Deserialize<ReminderQueue>(data) == q, "reminder: 저장 형식 왕복");
        Check.That(data.Contains("\"items\"") && data.Contains("\"firedAt\": 821692800"), "reminder: 저장 형식은 맥과 같음 (items, 2001 기준 초 821692800 = 2027-01-15)");
        Check.That(Reminder.SummaryOf("\n  미완료 Jira 3건\nNOVA-1") == "미완료 Jira 3건", "reminder: 첫 줄 요약");

        // ChatHints (worklog 캘린더 로컬 이름을 읽는 저장소는 임시 저장소)
        using var repo = new Check.TempRepo();
        var now = DateTimeOffset.Now;
        var both = ChatHints.For("평일 17시 30분에 오늘 HR랑 worklog 맞춰봐 알림 등록해줘", now, TimeZoneInfo.Local);
        Check.That(both.Contains("pokemate-alarm") && both.Contains("pokemate-worklog") && both.Contains("여러 개 붙었다"), "hints: 알림·worklog 둘 다 붙음");
        Check.That(!ChatHints.For("회의 worklog 10-11 기록해줘", now, TimeZoneInfo.Local).Contains("여러 개 붙었다"), "hints: 하나면 묶음 안내 없음");
        Check.That(ChatHints.For("안녕", now, TimeZoneInfo.Local).Length == 0, "hints: 해당 없으면 안 붙임");
    }
}
