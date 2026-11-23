using System.Windows;
using System.Windows.Threading;

namespace PokeMate;

/// 미리 알림 상태 (맥 ReminderCenter). 30초마다 다시 알림 시각을 확인하고, 바뀔 때마다 데이터 폴더의 reminders.json 에 저장한다.
/// 앱이 시작될 때 IAppService 로 켜지고, 화면(오른쪽 아래 창)은 ReminderPanelController 가 맡는다.
/// 어디서든 ReminderCenter.Shared.Add(...) 로 카드를 더한다 (UI 스레드가 아니어도 된다).
public sealed class ReminderCenter : NotifyObject, IAppService
{
    /// 시작된 센터. Start 전에는 null.
    public static ReminderCenter? Shared { get; private set; }

    /// 카드의 [새 세션으로 바꾸기]. 기본은 작업 탭을 열고 창을 띄운다 (맥 pokeMateResetSession 처리).
    /// 작업 세션(Task 8)이 값을 바꿔 requestReset 까지 이어 준다. 인자는 레포 키.
    public static Action<string> ResetSession { get; set; } = repo =>
    {
        AppModel.Shared.Selected = SidebarItemExtensions.WorkSessionsTab;
        PopoverWindow.Current?.ShowPopover();
    };

    ReminderQueue queue;
    DateTimeOffset now = DateTimeOffset.Now;
    DispatcherTimer? timer;
    ReminderPanelController? panel;

    /// 보일 카드가 바뀌면 불린다 (창 크기·표시 갱신).
    public Action OnChange { get; set; } = () => { };

    static string File => Path.Combine(AppPaths.DataDir, "reminders.json");

    public int Order => -20;

    public ReminderCenter()
    {
        queue = Load();
    }

    static ReminderQueue Load()
    {
        try
        {
            if (System.IO.File.Exists(File) && Json.TryDeserialize<ReminderQueue>(System.IO.File.ReadAllText(File)) is { } saved) return saved;
        }
        catch { }
        return new ReminderQueue();
    }

    public ReminderQueue Queue => queue;
    public DateTimeOffset Now => now;
    public List<Reminder> Visible => queue.Visible(now);

    public void Start(AppModel model)
    {
        Shared = this;
        panel = new ReminderPanelController(this, openChat: () =>
        {
            model.Selected = SidebarItem.Chat;
            PopoverWindow.Current?.ShowPopover();
        });
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
        panel.Refresh();
    }

    public void Stop()
    {
        timer?.Stop();
        panel?.Close();
    }

    /// 카드 하나를 더한다. text 는 요약(첫 줄)으로 줄여 저장한다.
    public void Add(string title, string text, bool failed, string? url = null, string? sessionRepo = null)
    {
        OnUi(() =>
        {
            queue.Add(new Reminder(title: title, summary: Reminder.SummaryOf(text), firedAt: DateTimeOffset.Now, failed: failed, url: url, sessionRepo: sessionRepo));
            Changed();
        });
    }

    public void Dismiss(Guid id) => OnUi(() => { queue.Dismiss(id); Changed(); });
    public void DismissAll() => OnUi(() => { queue.DismissVisible(DateTimeOffset.Now); Changed(); });
    public void Snooze(Guid id, int minutes) => OnUi(() => { queue.Snooze(id, minutes, DateTimeOffset.Now); Changed(); });

    static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    void Tick()
    {
        var before = Visible.Select(r => r.Id).ToList();
        now = DateTimeOffset.Now;
        Raise(nameof(Now));
        if (!Visible.Select(r => r.Id).SequenceEqual(before)) OnChange();
    }

    void Changed()
    {
        now = DateTimeOffset.Now;
        try
        {
            AppPaths.WriteAtomic(File, Json.Serialize(queue, indented: false));
        }
        catch (Exception e) { App.Log($"미리 알림을 저장하지 못했어요: {e.Message}"); }
        Raise(nameof(Queue));
        OnChange();
    }
}
