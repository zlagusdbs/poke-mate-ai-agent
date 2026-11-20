using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Alarm, ReportSchedule, Trigger, 알람 순서, 알람별 제한 시간, AlarmGroup, 옛 이름 호환 중 Alarm 형식에 걸린 것
public static class AlarmChecks
{
    static readonly TimeZoneInfo Tz = Check.Seoul;
    // 2026-10-24 토, 2026-10-26 월
    static DateTimeOffset At(int day, int h, int m) => Check.SeoulTime(2026, 10, day, h, m);

    static Alarm Al(string title, string? group) => new(title, "", 9, 0, new Alarm.Repeat.Daily(), group: group);
    static List<Alarm> RoundTrip(IReadOnlyList<Alarm> alarms) => Json.Deserialize<List<Alarm>>(Json.Serialize(alarms))!;

    public static void Run()
    {
        // ReportSchedule
        var yesterday = "2026-10-20";
        var today = "2026-10-21";
        Check.That(!ReportSchedule.ShouldRun(At(21, 9, 59), null, 10, 0, Tz), "schedule: 10시 전");
        Check.That(ReportSchedule.ShouldRun(At(21, 10, 0), null, 10, 0, Tz), "schedule: 10시 정각");
        Check.That(!ReportSchedule.ShouldRun(At(21, 15, 0), today, 10, 0, Tz), "schedule: 오늘 이미 실행");
        Check.That(ReportSchedule.ShouldRun(At(21, 15, 0), yesterday, 10, 0, Tz), "schedule: 잠자기 후 깨어남");
        Check.That(!ReportSchedule.ShouldRun(At(21, 0, 5), yesterday, 10, 0, Tz), "schedule: 자정 직후");
        Check.That(ReportSchedule.DayString(At(21, 23, 59), Tz) == today, "schedule: 날짜 문자열");
        Check.That(ReportSchedule.DayString(new DateTimeOffset(2026, 10, 21, 15, 30, 0, TimeSpan.Zero), Tz) == "2026-10-22", "schedule: 날짜는 지정한 시간대 기준");

        // Alarm
        var weekday = new Alarm("t", "p", 9, 30, new Alarm.Repeat.Weekdays());
        Check.That(!weekday.ShouldRun(At(24, 10, 0), Tz), "alarm: 평일 알림은 토요일에 안 울림");
        Check.That(weekday.ShouldRun(At(26, 9, 30), Tz), "alarm: 평일 알림 월요일 9:30");
        Check.That(!weekday.ShouldRun(At(26, 9, 29), Tz), "alarm: 시각 전");
        var off = weekday with { Enabled = false };
        Check.That(!off.ShouldRun(At(26, 10, 0), Tz), "alarm: 꺼진 알림");
        var saturday = new Alarm("t", "p", 8, 0, new Alarm.Repeat.Days(new[] { 7 }));
        Check.That(saturday.ShouldRun(At(24, 8, 1), Tz), "alarm: 요일 선택(토)");
        var once = new Alarm("t", "p", 14, 0, new Alarm.Repeat.Once("2026-10-26"));
        Check.That(once.ShouldRun(At(26, 15, 0), Tz) && !once.ShouldRun(At(27, 15, 0), Tz), "alarm: 한 번은 그날만");
        var ran = weekday with { LastRunDay = "2026-10-26" };
        Check.That(!ran.ShouldRun(At(26, 11, 0), Tz), "alarm: 오늘 이미 실행");
        Check.That(new Alarm.Repeat.Days(new[] { 2, 4, 6 }).Description == "월·수·금", "alarm: 요일 표시");
        Check.That(RoundTrip(new[] { saturday, once }).SequenceEqual(new[] { saturday, once }), "alarm: 저장 형식 왕복");
        var text = Json.Serialize(new[] { saturday, once }, indented: false);
        Check.That(text.Contains("\"repeatRule\":{\"days\":{\"_0\":[7]}}") && text.Contains("\"repeatRule\":{\"once\":{\"_0\":\"2026-10-26\"}}")
                   && Json.Serialize(new[] { weekday }, indented: false).Contains("\"repeatRule\":{\"weekdays\":{}}")
                   && text.Contains($"\"id\":\"{saturday.Id.ToString().ToUpperInvariant()}\""),
                   "alarm: 저장 형식은 맥과 같음 (반복 규칙 {\"days\":{\"_0\":…}}, 대문자 UUID)");
        const string oldAlarm = """[{"id":"6F1D6F5E-0000-0000-0000-000000000004","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]""";
        var decodedAlarm = Json.Deserialize<List<Alarm>>(oldAlarm)![0];
        Check.That(decodedAlarm.ToMac && decodedAlarm.ToPanel, "alarm: 예전 알림은 받을 곳 둘 다 켜짐");
        var quiet = decodedAlarm with { NotifyMac = false };
        var round = RoundTrip(new[] { quiet })[0];
        Check.That(!round.ToMac && round.ToPanel, "alarm: 받을 곳 저장 왕복");
        var reset = new Alarm("x", "", 9, 0, new Alarm.Repeat.Daily());
        var afternoon = Check.SeoulTime(2026, 10, 22, 14);
        reset.ResetSchedule(afternoon, Tz);
        Check.That(!reset.ShouldRun(afternoon, Tz), "alarm: 오후에 등록한 9시 알림은 바로 울리지 않음");
        var six = reset with { Hour = 18 };
        six.ResetSchedule(afternoon, Tz);
        Check.That(six.LastRunDay == null && six.ShouldRun(afternoon.AddHours(4), Tz), "alarm: 오늘 남은 18시는 울림");
        Check.That(Alarm.DefaultReviewRequestTrigger().Trigger?.Event == "github.review_requested"
                   && Alarm.DefaultDailyReport(9, 30, "2026-10-21") is { Prompt: "{{daily-report}}", Hour: 9, Minute: 30, LastRunDay: "2026-10-21" }, "alarm: 기본 알림");

        // Trigger
        var t = Alarm.DefaultReviewRequestTrigger();
        Check.That(!t.ShouldRun(DateTimeOffset.Now, Tz), "trigger: 시각 판단으로는 울리지 않음");
        const string oldTriggerAlarm = """[{"id":"6F1D6F5E-0000-0000-0000-000000000002","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]""";
        Check.That(Json.Deserialize<List<Alarm>>(oldTriggerAlarm)![0].Trigger == null, "trigger: 예전 알림 파일도 읽음");
        Check.That(RoundTrip(new[] { t })[0].Trigger == t.Trigger, "trigger: 저장 형식 왕복");
        var a = new EventItem("1", "a", "", ""); var b = new EventItem("2", "b", "", "");
        Check.That(TriggerDiff.NewItems(new[] { a, b }, null).Count == 0, "trigger: 처음엔 기준만 잡고 알리지 않음");
        Check.That(TriggerDiff.NewItems(new[] { a, b }, new HashSet<string> { "1" }).SequenceEqual(new[] { b }), "trigger: 새 항목만");
        var t5 = new Trigger("github", "e");
        var t1 = new Trigger("github", "e", 1);
        var baseTime = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Check.That(t5.Interval == 5 && t5.IsDue(null, baseTime) && !t5.IsDue(baseTime, baseTime.AddSeconds(240))
                   && t5.IsDue(baseTime, baseTime.AddSeconds(298)) && t1.IsDue(baseTime, baseTime.AddSeconds(60)), "trigger: 주기 판단 (기본 5분, 1분)");
        Check.That(Json.Deserialize<Trigger>("""{"system":"github","event":"e"}""")?.Interval == 5, "trigger: 예전 트리거는 5분");
        Check.That(TriggerEvent.Events("github").Count == 3 && TriggerEvent.Systems.SequenceEqual(new[] { "github", "deploy", "jira" }), "trigger: 이벤트 목록");

        // 알람 순서
        var al = new[] { Al("a", "G"), Al("b", "G"), Al("c", null), Al("d", "G"), Al("e", "없는그룹") };
        var (aa, bb, cc, dd, ee) = (al[0], al[1], al[2], al[3], al[4]);
        var groups = new[] { "G" };
        var list = al.ToList();
        List<string> Titles(IEnumerable<Alarm> x) => x.Select(i => i.Title).ToList();
        Check.That(Titles(AlarmOrder.Move(list, dd.Id, "G", aa.Id, groups)).SequenceEqual(new[] { "d", "a", "b", "c", "e" }), "order: 같은 그룹 안에서 끌어 놓은 자리로");
        var moved = AlarmOrder.Move(list, cc.Id, "G", bb.Id, groups);
        Check.That(Titles(moved).SequenceEqual(new[] { "a", "c", "b", "d", "e" }) && moved[1].Group == "G", "order: 다른 그룹 칸 사이에 놓으면 그 그룹 그 자리로");
        Check.That(Titles(AlarmOrder.Move(list, aa.Id, null, null, groups)).SequenceEqual(new[] { "b", "c", "d", "e", "a" }), "order: 그룹 머리줄에 놓으면 그 그룹 맨 끝으로");
        Check.That(Titles(AlarmOrder.Move(list, cc.Id, "G", null, groups)).SequenceEqual(new[] { "a", "b", "d", "c", "e" }), "order: 그룹 끝은 그 그룹 마지막 알람 다음");
        Check.That(AlarmOrder.Move(list, aa.Id, "G", aa.Id, groups).SequenceEqual(list), "order: 제자리에 놓으면 그대로");
        Check.That(AlarmOrder.Neighbor(list, aa.Id, true, groups) == null && AlarmOrder.Neighbor(list, dd.Id, false, groups) == null
                   && AlarmOrder.Neighbor(list, cc.Id, false, groups) == ee.Id, "order: 그룹 맨 위·맨 아래는 더 못 옮김 (없는 그룹은 그룹 없음)");
        var up = AlarmOrder.Shift(list, dd.Id, true, groups);
        Check.That(Titles(up).SequenceEqual(new[] { "a", "d", "c", "b", "e" }) && up.Select(i => i.Enabled).SequenceEqual(list.Select(i => i.Enabled)),
                   "order: 위로 옮기기는 같은 그룹 앞 알람과 자리 바꿈, 켜짐 그대로");
        Check.That(Titles(RoundTrip(up)).SequenceEqual(new[] { "a", "d", "c", "b", "e" }), "order: 순서는 파일 배열 순서로 저장");
        const string oldOrder = """[{"id":"6F1D6F5E-0000-0000-0000-00000000000A","title":"2","prompt":"","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true},{"id":"6F1D6F5E-0000-0000-0000-00000000000B","title":"1","prompt":"","hour":8,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]""";
        Check.That(Titles(Json.Deserialize<List<Alarm>>(oldOrder)!).SequenceEqual(new[] { "2", "1" }), "order: 예전 파일은 지금 순서 그대로");
        Check.That(Alarm.DisplayCommand("uv --directory /Users/u/Workspace/poke-mate-ai-agent/mcp-server run --quiet pokemate auto worklog-check", "/Users/u") == "pokemate auto worklog-check",
                   "command: uv 앞부분은 빼고 실제 명령만");
        Check.That(Alarm.DisplayCommand("/opt/homebrew/bin/uv --directory \"/a b/c\" run tool x", "/Users/u") == "tool x", "command: uv 경로·따옴표 경로도");
        Check.That(Alarm.DisplayCommand(@"& ""C:\Users\u\.local\bin\uv.exe"" --directory ""C:\Users\u\Workspace\x y\mcp-server"" run --quiet pokemate auto worklog-check", @"C:\Users\u") == "pokemate auto worklog-check",
                   "command: Windows 의 uv.exe 경로·따옴표 경로도");
        Check.That(Alarm.DisplayCommand(@"python C:\Users\u\scripts\check.py --all", @"C:\Users\u") == @"python ~\scripts\check.py --all", "command: 다른 명령은 홈을 ~ 로");
        Check.That(Alarm.DisplayCommand("python C:/Users/u/scripts/check.py", @"C:\Users\u") == "python ~/scripts/check.py", "command: 슬래시로 적은 홈도 ~ 로");
        Check.That(Alarm.DisplayCommand(new string('x', 50), @"C:\Users\u") == new string('x', 40) + "…", "command: 그래도 길면 자름");

        // 알람별 제한 시간
        var timed = new Alarm("j", "", 0, 0, new Alarm.Repeat.Daily(), trigger: new Trigger("jira", "jira.assigned_to_me"));
        Check.That(timed.AutomationTimeout == ClaudeRunner.AutomationTimeout, "timeout: 없으면 기본 30분");
        timed = timed with { TimeoutMinutes = 90 };
        Check.That(RoundTrip(new[] { timed })[0].AutomationTimeout == TimeSpan.FromSeconds(5400), "timeout: 알람별 90분 저장·읽기");

        // AlarmGroup
        var g1 = new Alarm("1:1 요청", "", 9, 0, new Alarm.Repeat.Daily(), group: "1:1");
        var g2 = new Alarm("근무일정", "p", 10, 0, new Alarm.Repeat.Weekdays());
        var g3 = new Alarm("1:1 요청", "", 13, 0, new Alarm.Repeat.Daily(), enabled: false, group: "1:1");
        var g4 = new Alarm("x", "p", 11, 0, new Alarm.Repeat.Daily(), group: "지운 그룹");
        var gt = new Alarm("리뷰", "", 0, 0, new Alarm.Repeat.Daily(), trigger: new Trigger("github", "github.review_requested"), group: "1:1");
        var split = AlarmGroup.Make(new[] { g1, g2, g3, g4, gt }, new[] { "1:1", "빈 그룹" });
        Check.That(split.Groups.Select(g => g.Alarms.Count).SequenceEqual(new[] { 3, 0 }) && split.Ungrouped.Select(x => x.Id).SequenceEqual(new[] { g2.Id, g4.Id }),
                   "group: 그룹 순서대로, 빈 그룹도, 없는 그룹 이름은 그룹 없음으로");
        Check.That(split.Groups[0].Summary == "09:00 · 13:00 매일 · 트리거 1" && split.Groups[0].AnyEnabled, "group: 요약과 켜짐");
        Check.That(split.Groups[1].Summary.Contains("끌어다"), "group: 빈 그룹 안내");
        const string oldGroup = """[{"id":"6F1D6F5E-0000-0000-0000-000000000003","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]""";
        Check.That(Json.Deserialize<List<Alarm>>(oldGroup)![0].Group == null, "group: 예전 알림 파일은 그룹 없음");

        // 명령 실행 알람 (Alarm 에 걸린 것)
        const string oldCommand = """[{"id":"6F1D6F5E-0000-0000-0000-000000000009","title":"t","prompt":"p","hour":9,"minute":0,"repeatRule":{"daily":{}},"enabled":true}]""";
        Check.That(Json.Deserialize<List<Alarm>>(oldCommand)![0].ShellCommand == null, "command: 예전 알람 파일은 명령 없음");
        var cmd = new Alarm("근무", "", 10, 0, new Alarm.Repeat.Weekdays()) { Command = @"  python ~\x.py check  " };
        Check.That(RoundTrip(new[] { cmd })[0].ShellCommand == @"python ~\x.py check", "command: 저장하고 다시 읽음");
        Check.That((cmd with { Trigger = new Trigger("jira", "jira.assigned_to_me") }).ShellCommand == null, "command: 트리거 알람에서는 명령 실행 안 함");
        var proposed = AlarmProposal.Extract("```pokemate-alarm\n[{\"title\":\"x\",\"hour\":9,\"repeat\":\"daily\",\"command\":\"rm -rf ~\",\"prompt\":\"\"}]\n```").Alarms;
        Check.That(proposed.Count == 1 && proposed[0].Command == null && proposed[0].ShellCommand == null, "command: 대화 제안 블록으로는 명령 알람을 못 만듦");
    }
}
