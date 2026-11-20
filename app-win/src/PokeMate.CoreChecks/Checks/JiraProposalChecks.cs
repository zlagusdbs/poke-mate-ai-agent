using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: JiraProposal, Worklog
public static class JiraProposalChecks
{
    public static void Run()
    {
        var tz = Check.Seoul;
        var now = Check.SeoulTime(2026, 10, 22, 14);
        Check.That(JiraProposal.LooksLikeRequest("지라 등록해줘: 쿠폰 API 정리") && JiraProposal.LooksLikeRequest("Jira 이슈 하나 만들어줘")
                   && JiraProposal.LooksLikeRequest("다시 담당자 내이름으로") && JiraProposal.LooksLikeRequest("어싸인은 나지 ?")
                   && !JiraProposal.LooksLikeRequest("내 미완료 Jira 알려줘") && !JiraProposal.LooksLikeRequest("매일 9시 알림 등록"), "jira: 등록 요청 판단");
        var reply = "초안이에요.\n```pokemate-jira\n{\"issue\":{\"project\":\"MYPROJ\",\"issueType\":\"Story\",\"summary\":\" 쿠폰 정리 \",\"description\":\"h3. 배경\",\"targetEnd\":\"2026-12-31\"}}\n```";
        var (text, draft) = JiraProposal.Extract(reply);
        Check.That(text == "초안이에요." && draft?.Summary == "쿠폰 정리" && draft.OriginalEstimate == "" && draft.TargetEnd == "2026-12-31", "jira: 초안 읽기 (빠진 항목은 빈칸)");
        Check.That(JiraProposal.Extract("```pokemate-jira\n{\"issue\":{\"project\":\"P\"}}\n```").Draft == null, "jira: 제목·유형 없는 초안은 버림");
        Check.That(JiraProposal.Extract("```pokemate-jira\n{\"project\":\"P\",\"issueType\":\"Story\",\"summary\":\"s\"}\n```").Draft?.Project == "P", "jira: 감싸지 않은 초안도 읽음");
        var hint = JiraProposal.Hint(now, tz);
        Check.That(hint.Contains("오늘은 2026-10-22.") && hint.Contains("Jira 스킬(skills/jira.md)") && hint.Contains("pokemate-rule") && !hint.Contains("{today}"),
                   "jira: 규칙을 따르라는 안내 (날짜 계산됨)");
        var example = JiraProposal.Extract(hint).Draft;
        Check.That(!hint.Contains("365d") && !hint.Contains("12-31") && example?.OriginalEstimate == "" && example.TargetEnd == ""
                   && hint.Contains("견적이 며칠인가요"), "jira: 견적·끝 날짜를 지어내지 않고, 없으면 비워 두고 묻기");
        Check.That(hint.Contains("담당자(Assignee)") && hint.Contains("본인으로 자동"), "jira: 담당자는 본인 자동이라고 안내");
        Check.That(JiraProposal.Extract(hint).Draft?.TargetStart == "2026-10-22", "jira: 안내 예시 형식이 읽힘");
        var m = new ChatMessage(ChatMessage.RoleKind.Assistant, "t", jira: draft);
        m.JiraCreated = new JiraCreated("MYPROJ-1", "u");
        var back = Json.Deserialize<ChatMessage>(Json.Serialize(m));
        Check.That(back?.Jira == draft && back!.JiraCreated?.Key == "MYPROJ-1", "jira: 대화 기록에 초안·결과 저장");
        const string oldChat = """{"id":"6F1D6F5E-0000-0000-0000-000000000009","role":"assistant","text":"t","date":0,"jiraDefaults":{"project":"P"}}""";
        Check.That(Json.TryDeserialize<ChatMessage>(oldChat) != null, "jira: 예전 대화 기록(기본값 포함)도 읽음");
        var d = new JiraDraft("P", "Story", "s", "d") { TargetStart = "2026-10-22", TargetEnd = "2026-12-31", OriginalEstimate = "365d" };
        Check.That(d.IsComplete && d.PeriodText == "2026-10-22 ~ 2026-12-31 · 365d" && new JiraDraft("P", "", "s", "d").IsComplete == false
                   && new JiraDraft("P", "T", "s", "d") { TargetStart = "2026-10-22" }.PeriodText == "2026-10-22 ~ ?", "jira: 완성 여부·기간 한 줄");
        Check.That(Json.Deserialize<JiraDraft>("""{"project":" P ","summary":7}""") is { Project: "P", Summary: "", Description: "" }, "jira: 빠지거나 형식이 틀린 항목은 빈칸으로 읽음");

        // Worklog
        var wl = "준비했어요.\n```pokemate-worklog\n[{\"issueKey\":\"myproj-100\",\"summary\":\"회의\",\"date\":\"2026-10-22\",\"start\":\"10\",\"end\":\"11:30\"},{\"issueKey\":\"A-1\",\"date\":\"2026-10-22\",\"start\":\"14:00\",\"minutes\":0}]\n```";
        var (wtext, drafts) = WorklogProposal.Extract(wl);
        Check.That(wtext == "준비했어요." && drafts.Count == 1, "worklog: 블록 읽기 (0분은 버림)");
        var w = drafts[0];
        Check.That(w.IssueKey == "MYPROJ-100" && w.Start == "10:00" && w.Minutes == 90 && w.TimeText == "10:00~11:30 (1시간 30분)", "worklog: end 로 시간 계산");
        Check.That(w.Started(tz) == "2026-10-22T10:00:00.000+0900", "worklog: Jira 시작 시각 형식");
        Check.That(Json.Deserialize<List<WorklogDraft>>(Json.Serialize(drafts))!.SequenceEqual(drafts), "worklog: 저장 왕복");
        Check.That(Json.Serialize(drafts, indented: false).Contains("\"minutes\":90") && !Json.Serialize(drafts, indented: false).Contains("\"end\""), "worklog: 저장은 minutes 로 (end 는 쓰지 않음)");
        Check.That(WorklogProposal.LooksLikeRequest("회의 jira에 오늘 work log 10-11 등록해줘", Array.Empty<string>()) && WorklogProposal.LooksLikeRequest("오늘 뭐 했지?", Array.Empty<string>())
                   && WorklogProposal.LooksLikeRequest("HR랑 맞춰봐", Array.Empty<string>()) && !WorklogProposal.LooksLikeRequest("지라 등록해줘", Array.Empty<string>()), "worklog: 요청 판단");
        var hintDay = Check.SeoulTime(2026, 10, 22);
        Check.That(WorklogProposal.Extract(WorklogProposal.Hint(hintDay, tz)).Drafts.FirstOrDefault()?.Date == "2026-10-22", "worklog: 안내 예시가 읽힘");
        Check.That(!new WorklogDraft { IssueKey = "A-1", Date = "2026-10-22", Start = "25:00", Minutes = 30 }.IsValid
                   && !new WorklogDraft { IssueKey = "A-1", Date = "10-22", Start = "10:00", Minutes = 30 }.IsValid
                   && new WorklogDraft { IssueKey = "A-1", Date = "2026-10-22", Start = "10:00", Minutes = 30 }.IsValid, "worklog: 시각·날짜 형식 검사");
    }
}
