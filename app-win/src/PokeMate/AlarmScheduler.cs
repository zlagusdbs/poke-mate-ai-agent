using System.Windows.Threading;
using Microsoft.Win32;

namespace PokeMate;

/// 알림 실행기 (맥 AlarmScheduler). 1분마다, 그리고 잠자기에서 깨어날 때 실행할 알림을 찾는다.
/// IAppService 로 시작되며 model.RunAlarmNow 를 연결한다 ([지금 실행]).
public sealed class AlarmScheduler : IAppService
{
    /// 시작된 스케줄러. Start 전에는 null.
    public static AlarmScheduler? Shared { get; private set; }

    AppModel model = null!;
    DispatcherTimer? timer;

    /// TriggerWatcher(-10) 와 ReminderCenter(-20) 다음에 시작한다
    public int Order => 0;

    public void Start(AppModel model)
    {
        this.model = model;
        Shared = this;
        model.RunAlarmNow = alarm =>
        {
            if (alarm.Trigger != null)
            {
                model.MarkRunning(alarm.Id);
                _ = CheckTrigger(alarm);
            }
            else Run(alarm);
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => CheckAll();
        timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        CheckAll();
    }

    public void Stop()
    {
        timer?.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    // 잠자기에서 깨어날 때
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) System.Windows.Application.Current?.Dispatcher.BeginInvoke(CheckAll);
    }

    async Task CheckTrigger(Alarm alarm)
    {
        var result = TriggerWatcher.Shared is { } watcher ? await watcher.Check(alarm) : ("트리거 감시가 꺼져 있어요", true);
        model.MarkDone(alarm.Id, result.Item1, result.Item2);
    }

    /// 시각이 된 알림을 모두 실행한다
    public void CheckAll()
    {
        var now = DateTimeOffset.Now;
        foreach (var alarm in model.Alarms.ToList())
            if (alarm.ShouldRun(now)) Run(alarm);
    }

    static string Body(string text, string fallback) => text.Trim().Length == 0 ? fallback : text;

    /// 한 알림 실행. 실행하는 순간 기록한다 (실패해도 같은 날 다시 시도하지 않는다).
    public void Run(Alarm alarm)
    {
        var today = ReportSchedule.DayString(DateTimeOffset.Now);
        var stored = model.Alarms.FirstOrDefault(a => a.Id == alarm.Id);
        if (stored != null)
        {
            stored.LastRunDay = today;
            if (stored.RepeatRule is Alarm.Repeat.Once) stored.Enabled = false;
            model.Alarms = model.Alarms.ToList();   // 저장하고 화면에 알린다
        }

        if (alarm.ShellCommand is { } command)
        {
            model.MarkRunning(alarm.Id);
            _ = RunCommand(alarm, command);
            return;
        }
        var reminders = ReminderCenter.Shared;
        if (alarm.Prompt.Trim().Length == 0)
        {
            if (alarm.ToMac) Notifier.Show($"🔔 {alarm.Title}", "잊지 마세요.");
            if (alarm.ToPanel) reminders?.Add(alarm.Title, $"잊지 마세요: {alarm.Title}", false);
            model.MarkDone(alarm.Id, "알림 창에 보냈어요");
            return;
        }
        if (AlarmStore.ResolvePrompt(alarm, model.Settings) is not { } prompt)
        {
            if (alarm.ToMac) Notifier.Show(alarm.Title, "알림 내용을 읽지 못했어요.");
            if (alarm.ToPanel) reminders?.Add(alarm.Title, "알림 내용을 읽지 못했어요.", true);
            model.MarkDone(alarm.Id, "알림 내용을 읽지 못했어요", failed: true);
            return;
        }
        model.MarkRunning(alarm.Id);
        model.Chat.RunReport(alarm.Title, prompt, (response, usage) =>
        {
            switch (response)
            {
                case ClaudeResponse.Success success:
                    var lines = success.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (alarm.ToMac) Notifier.Show($"🔔 {alarm.Title}", Body(string.Join("\n", lines.Take(4)), "끝났어요"));
                    if (alarm.ToPanel) reminders?.Add(alarm.Title, success.Text, false);
                    model.MarkDone(alarm.Id, "끝났어요. 결과는 알림 창과 채팅에 있어요", usage: usage);
                    break;
                case ClaudeResponse.Failure failure:
                    if (alarm.ToMac) Notifier.Show($"🔔 {alarm.Title} 실패", failure.Reason.BackgroundMessage);
                    if (alarm.ToPanel) reminders?.Add(alarm.Title, failure.Reason.BackgroundMessage, true);
                    model.MarkDone(alarm.Id, failure.Reason.BackgroundMessage, failed: true, usage: usage);
                    break;
            }
        });
    }

    async Task RunCommand(Alarm alarm, string command)
    {
        var result = await ShellCommand.Run(command);
        // 성공했는데 아무것도 출력하지 않으면 알릴 게 없다는 뜻이다 (예: worklog 가 HR 근무시간과 맞는 날). 알림 없이 기록만 남긴다.
        if (result.Ok && result.Tail.Trim().Length == 0)
        {
            model.MarkDone(alarm.Id, "알릴 것 없음");
            return;
        }
        var summary = result.Ok ? "끝났어요" : $"종료 코드 {result.ExitCode}";
        var body = result.Tail.Length == 0 ? summary : result.Tail;
        var title = result.Ok ? $"🔔 {alarm.Title}" : $"🔔 {alarm.Title} 실패";
        if (alarm.ToMac) Notifier.Show(title, body);
        if (alarm.ToPanel) ReminderCenter.Shared?.Add(alarm.Title, body, !result.Ok);
        model.MarkDone(alarm.Id, summary, failed: !result.Ok, output: result.Tail);
    }
}
