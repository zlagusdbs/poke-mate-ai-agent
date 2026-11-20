using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: AlarmProposal
public static class AlarmProposalChecks
{
    public static void Run()
    {
        var reply = "알림 3개를 준비했어요.\n\n```pokemate-alarm\n[{\"title\":\"1:1 요청\",\"hour\":9,\"minute\":0,\"repeat\":\"daily\",\"prompt\":\"\"},{\"title\":\"1:1 요청\",\"hour\":13,\"repeat\":\"weekdays\"},{\"title\":\"x\",\"hour\":25}]\n```";
        var (text, alarms) = AlarmProposal.Extract(reply);
        Check.That(text == "알림 3개를 준비했어요.", "proposal: 블록은 본문에서 뺌");
        Check.That(alarms.Count == 2 && alarms[0].Hour == 9 && alarms[0].RepeatRule is Alarm.Repeat.Daily && alarms[1].Minute == 0
                   && alarms[1].RepeatRule is Alarm.Repeat.Weekdays && alarms[0].Prompt.Length == 0, "proposal: 알림 읽기, 잘못된 시각은 버림");
        Check.That(AlarmProposal.Extract("그냥 답").Alarms.Count == 0, "proposal: 블록 없으면 없음");
        Check.That(AlarmProposal.Extract("```pokemate-alarm\n깨진 json\n```").Text.Length == 0, "proposal: 깨진 블록도 본문에서 뺌");
        Check.That(AlarmProposal.RepeatRule("days:4,2,2") == new Alarm.Repeat.Days(new[] { 2, 4 }) && AlarmProposal.RepeatRule("once:2026-10-23") == new Alarm.Repeat.Once("2026-10-23")
                   && AlarmProposal.RepeatRule("days:9") == null && AlarmProposal.RepeatRule("hourly") == null, "proposal: 반복 규칙");
        var grouped = AlarmProposal.Extract("```pokemate-alarm\n[{\"title\":\"a\",\"hour\":9,\"group\":\" 팀 미팅 \"},{\"title\":\"b\",\"hour\":10,\"group\":\"\"}]\n```").Alarms;
        Check.That(grouped.Select(a => a.Group).SequenceEqual(new string?[] { "팀 미팅", null }), "proposal: 그룹 이름 (빈 이름은 그룹 없음)");
        var loose = AlarmProposal.Extract("```pokemate-alarm\n[{\"time\":\"18:30\",\"repeat\":\"daily\",\"message\":\"m\"}]\n```").Alarms;
        Check.That(loose.Count == 1 && loose[0].Hour == 18 && loose[0].Minute == 30 && loose[0].Title == "m", "proposal: time·message 키도 받음");
        Check.That(AlarmProposal.LooksLikeRequest("매일 9시에 잊지말라고 알림 기록해줘") && AlarmProposal.LooksLikeRequest("매일 오전 9시에 1:1 알람을 해야 한다고 알람설정해줘")
                   && AlarmProposal.LooksLikeRequest("잊지 않게 리마인드 해줘")
                   && !AlarmProposal.LooksLikeRequest("내 미완료 Jira"), "proposal: 알림 요청 판단 (알람도)");
        Check.That(AlarmProposal.Extract(AlarmProposal.Hint).Alarms.Count == 1, "proposal: 안내 속 예시 형식이 실제로 읽힘");
        var msg = new ChatMessage(ChatMessage.RoleKind.Assistant, "t", alarms: alarms);
        var back = Json.Deserialize<ChatMessage>(Json.Serialize(msg));
        Check.That(back?.Alarms.Select(a => a.Id).SequenceEqual(alarms.Select(a => a.Id)) == true, "proposal: 대화 기록에 저장 (같은 id 라 다시 등록 안 됨)");

        var afternoon = Check.SeoulTime(2026, 10, 22, 14);
        var nine = alarms[0];
        nine.ResetSchedule(afternoon, Check.Seoul);
        Check.That(!nine.ShouldRun(afternoon, Check.Seoul), "proposal: 오후에 등록한 9시 알림은 바로 울리지 않음");
        var six = nine with { Hour = 18 };
        six.ResetSchedule(afternoon, Check.Seoul);
        Check.That(six.LastRunDay == null && six.ShouldRun(afternoon.AddHours(4), Check.Seoul), "proposal: 오늘 남은 18시는 울림");
    }
}
