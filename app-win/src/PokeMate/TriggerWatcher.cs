using System.Windows.Threading;

namespace PokeMate;

/// 트리거 알림 확인기 (맥 TriggerWatcher). 1분마다 깨어나서, 알림마다 정한 주기(기본 5분)가 지난 트리거의 이벤트를 조회해 새로 생긴 항목만 알린다.
/// 내용(prompt)이 있으면 메이트가 그 일을 해서 결과를 카드로, 없으면 이벤트 자체를 카드로 보여 준다.
public sealed class TriggerWatcher : IAppService
{
    /// 시작된 감시기. Start 전에는 null.
    public static TriggerWatcher? Shared { get; private set; }

    /// 새 이벤트를 쓰기 허용 알림으로 처리하는 곳 (작업 세션, Task 8 이 연결한다).
    /// 인자: 알림, 이벤트 항목, 알림 내용(prompt), 제목, 쓰기 권한(역할별 처리는 빈 권한), 끝났을 때 부를 함수(UI 스레드).
    /// 연결하지 않았으면 일반 자동 처리(쓰기 허용 알림은 RunAutomation, 아니면 RunReport)로 돌아간다.
    public delegate void ItemHandler(Alarm alarm, EventItem item, string prompt, string title, ClaudeCommand.WriteAccess access,
                                     Action<ClaudeResponse, RunUsage?> completion);

    /// 역할별 세션으로 나눠 처리 (맥 WorkSessionCenter.handlePipeline). alarm.RolePipeline 일 때
    public static ItemHandler? PipelineHandler { get; set; }
    /// 도메인 리뷰어 세션으로 리뷰 (맥 handleReview). alarm.DomainReviewer && 쓰기 권한이 있을 때
    public static ItemHandler? ReviewHandler { get; set; }
    /// 레포별 작업 세션 이어 쓰기 (맥 handle). alarm.WorkSessions && 쓰기 권한이 있을 때
    public static ItemHandler? WorkSessionHandler { get; set; }

    AppModel model = null!;
    DispatcherTimer? timer;
    readonly HashSet<Guid> checking = new();
    /// 알림별 마지막 확인 시각 (앱을 다시 켜면 처음부터)
    readonly Dictionary<Guid, DateTimeOffset> lastChecked = new();

    static string SeenFile => Path.Combine(AppPaths.DataDir, "trigger-seen.json");

    /// AlarmScheduler(0) 보다 먼저
    public int Order => -10;

    public void Start(AppModel model)
    {
        this.model = model;
        Shared = this;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += async (_, _) => await CheckDue();
        timer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        first.Tick += async (_, _) => { first.Stop(); await CheckDue(); };
        first.Start();
    }

    public void Stop() => timer?.Stop();

    /// 주기가 된 트리거만 확인한다.
    public async Task CheckDue()
    {
        var now = DateTimeOffset.Now;
        foreach (var alarm in model.Alarms.Where(a => a.Enabled).ToList())
        {
            if (alarm.Trigger is not { } trigger || !trigger.IsDue(lastChecked.TryGetValue(alarm.Id, out var last) ? last : null, now)) continue;
            var result = await Check(alarm);
            model.RecordRun(alarm.Id, result.Message, result.Failed);
        }
    }

    /// 한 알림 확인. [지금 실행] 에서도 부르고, 결과를 한 줄로 돌려준다 (알림 목록에 잠깐 보임).
    public async Task<(string Message, bool Failed)> Check(Alarm alarm)
    {
        if (alarm.Trigger is not { } trigger || checking.Contains(alarm.Id)) return ("이미 확인 중이에요", false);
        checking.Add(alarm.Id);
        lastChecked[alarm.Id] = DateTimeOffset.Now;
        try
        {
            List<EventItem> items;
            try { items = await MateCLI.Events(trigger.Event, model.Settings); }
            catch { return ("확인하지 못했어요. 연결 메뉴를 확인해 주세요", true); }

            var seenAll = LoadSeen();
            var key = Key(alarm.Id);
            var firstTime = !seenAll.ContainsKey(key);
            var fresh = TriggerDiff.NewItems(items, seenAll.TryGetValue(key, out var seen) ? new HashSet<string>(seen) : null);
            var merged = new HashSet<string>(seenAll.GetValueOrDefault(key) ?? new List<string>());
            merged.UnionWith(items.Select(i => i.Key));
            seenAll[key] = merged.ToList();
            SaveSeen(seenAll);

            foreach (var item in fresh) Notify(alarm, item);
            if (firstTime) return ($"처음이라 지금 있는 {items.Count}건을 기준으로 잡았어요", false);
            return fresh.Count == 0 ? ("새로 생긴 것 없어요", false) : ($"새로 {fresh.Count}건, 알림 창에 보냈어요", false);
        }
        finally { checking.Remove(alarm.Id); }
    }

    static string Key(Guid id) => id.ToString("D").ToUpperInvariant();

    void Notify(Alarm alarm, EventItem item)
    {
        var reminders = ReminderCenter.Shared;
        var url = item.Url.Length == 0 ? null : item.Url;
        var prompt = (AlarmStore.ResolvePrompt(alarm, model.Settings) ?? "").Trim();
        if (prompt.Length == 0)
        {
            if (alarm.ToMac) Notifier.Show($"🔔 {item.Title}", Nonempty(item.Summary));
            if (alarm.ToPanel) reminders?.Add(item.Title, item.Summary, false, url);
            return;
        }
        var context = $"다음 이벤트가 생겼다.\n- {item.Title}\n- {item.Summary}\n- 링크: {item.Url}\n\n요청: {prompt}";
        void Finish(ClaudeResponse response, RunUsage? usage)
        {
            switch (response)
            {
                case ClaudeResponse.Success:
                    model.RecordRun(alarm.Id, "끝났어요", false, usage: usage, count: false);
                    break;
                case ClaudeResponse.Failure failure:
                    model.RecordRun(alarm.Id, failure.Reason.Message, true, usage: usage, count: false);
                    break;
            }
            switch (response)
            {
                case ClaudeResponse.Success success:
                    if (alarm.ToMac) Notifier.Show($"🔔 {item.Title}", Nonempty(Reminder.SummaryOf(success.Text)));
                    if (alarm.ToPanel) reminders?.Add(item.Title, success.Text, false, url);
                    break;
                case ClaudeResponse.Failure failure:
                    if (alarm.ToMac) Notifier.Show($"🔔 {item.Title}", Nonempty(item.Summary));
                    if (alarm.ToPanel) reminders?.Add(item.Title, failure.Reason.Message, true, url);
                    break;
            }
        }
        var title = $"{alarm.Title} · {item.Title}";
        var access = alarm.CanWrite ? ClaudeCommand.LoadWriteAccess() : new ClaudeCommand.WriteAccess();
        if (alarm.RolePipeline && PipelineHandler != null)
            PipelineHandler(alarm, item, prompt, title, access, Finish);
        else if (alarm.DomainReviewer && access.Tools.Count > 0 && ReviewHandler != null)
            ReviewHandler(alarm, item, prompt, title, access, Finish);
        else if (alarm.WorkSessions && access.Tools.Count > 0 && WorkSessionHandler != null)
            WorkSessionHandler(alarm, item, prompt, title, access, Finish);
        else if (alarm.CanWrite && access.Tools.Count > 0)
            model.Chat.RunAutomation(title, WithDefaultKnowledge(context), access, Finish, alarm.AutomationTimeout);
        else
            model.Chat.RunReport(title, WithDefaultKnowledge(context), Finish);
    }

    static string Nonempty(string text) => text.Trim().Length == 0 ? "새 항목이 있어요" : text;

    public static string WithDefaultKnowledge(string text)
    {
        if (!text.Contains(DomainMap.Placeholder, StringComparison.Ordinal)) return text;
        return text.Replace(DomainMap.Placeholder, DomainMap.Load().KnowledgeText(""), StringComparison.Ordinal);
    }

    /// 예전 GitHub 리뷰 요청 감시가 기억하던 PR 을 새 트리거 알림의 기준으로 옮긴다 (다시 알리지 않게).
    /// AppModel 이 만들어지기 전에 AppModel.MigrateGitHubSeen 에 연결해 둔다 (App.OnStartup).
    public static void MigrateGitHubSeen(Alarm alarm)
    {
        var old = Path.Combine(AppPaths.DataDir, "github-seen.json");
        try
        {
            if (!File.Exists(old) || Json.TryDeserialize<List<int>>(File.ReadAllText(old)) is not { } ids) return;
            var seen = LoadSeen();
            seen[Key(alarm.Id)] = ids.Select(i => i.ToString()).ToList();
            SaveSeen(seen);
            File.Delete(old);
        }
        catch { }
    }

    static Dictionary<string, List<string>> LoadSeen()
    {
        try
        {
            if (File.Exists(SeenFile) && Json.TryDeserialize<Dictionary<string, List<string>>>(File.ReadAllText(SeenFile)) is { } seen) return seen;
        }
        catch { }
        return new Dictionary<string, List<string>>();
    }

    static void SaveSeen(Dictionary<string, List<string>> seen)
    {
        try { AppPaths.WriteAtomic(SeenFile, Json.Serialize(seen, indented: false)); }
        catch (Exception e) { App.Log($"트리거 기록을 저장하지 못했어요: {e.Message}"); }
    }
}
