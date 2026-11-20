using System.Windows;

namespace PokeMate;

/// 알람 실행 상태. 실행 중이면 목록에 돌아가는 표시를, 끝나면 결과 한 줄을 잠깐 보여 준다. (맥 AppModel.AlarmRunState)
public abstract record AlarmRunState
{
    public sealed record Running : AlarmRunState;
    public sealed record Done(string Message, bool Failed) : AlarmRunState;
}

/// 앱 전체 상태 (싱글 인스턴스: AppModel.Shared). 맥 AppModel 과 같은 이름의 공개 속성·메서드.
/// 모든 속성은 UI 스레드에서 바꾸고, 바뀌면 PropertyChanged(속성 이름) 가 불린다.
public sealed class AppModel : NotifyObject
{
    public static AppModel Shared => shared ?? throw new InvalidOperationException("AppModel 은 App 이 시작할 때 만든다");
    static AppModel? shared;

    /// 홈 탭의 대화 (맥 AppModel.chat). 대화 설정에는 ChatSettings() 를 쓴다.
    public ChatSession Chat { get; }

    SidebarItem selected;
    Uri? openNote;
    AppSettings settings;
    List<Alarm> alarms;
    List<string> alarmGroups;
    Dictionary<Guid, AlarmRunState> alarmRuns = new();
    Dictionary<Guid, AlarmRunRecord> alarmLog;
    List<SystemStatus> systems = new();
    ConnectionState? microsoft365;
    string? connectionError;
    bool isLoadingSystems;

    public AppModel()
    {
        shared = this;
        var loaded = AppPaths.LoadSettings();
        settings = loaded;
        Chat = new ChatSession(ChatSettings);
        selected = StartTab();
        alarms = AlarmStore.Load(loaded);
        alarmGroups = AlarmStore.LoadGroups();
        alarmLog = AlarmRunLog.Load();
        // 예전에 따로 돌던 GitHub 리뷰 요청 감시를 트리거 알림 하나로 옮긴다 (한 번만).
        if (!Prefs.Bool("migratedReviewTrigger"))
        {
            Prefs.Set("migratedReviewTrigger", true);
            if (!alarms.Any(a => a.Trigger?.Event == "github.review_requested"))
            {
                var trigger = Alarm.DefaultReviewRequestTrigger();
                MigrateGitHubSeen(trigger);
                alarms.Add(trigger);
            }
        }
        AlarmStore.Save(alarms);
    }

    static SidebarItem StartTab()
    {
        var raw = Prefs.String("startTab");
        foreach (var item in SidebarItemExtensions.AllCases)
            if (item.Raw() == raw) return item;
        return SidebarItem.Chat;
    }

    /// 맥 TriggerWatcher.migrateGitHubSeen. 트리거 감시(Task 7)가 연결한다. 기본은 아무것도 안 한다.
    public static Action<Alarm> MigrateGitHubSeen { get; set; } = _ => { };

    // MARK: 화면

    /// 지금 보이는 탭·도구 화면. PopoverWindow 가 이 값을 보고 화면을 바꾼다.
    public SidebarItem Selected { get => selected; set => Set(ref selected, value); }

    /// 메모 메뉴에서 열 메모 (채팅 카드의 [메모 보기])
    public Uri? OpenNote { get => openNote; set => Set(ref openNote, value); }

    // MARK: 설정

    /// 설정. 바꾸면 바로 파일에 저장한다. 값을 하나만 바꿀 때는 UpdateSettings 를 쓴다
    /// (AppSettings 는 참조 형식이라 속성을 직접 바꾸면 저장·알림이 안 된다).
    public AppSettings Settings
    {
        get => settings;
        set
        {
            settings = value;
            AppPaths.SaveSettings(value);
            Raise();
        }
    }

    public void UpdateSettings(Action<AppSettings> change)
    {
        var copy = settings with { AutomationRules = settings.AutomationRules.ToList() };
        change(copy);
        Settings = copy;
    }

    /// 대화·자동 처리에 넘길 설정. 키우는 포켓몬 이름을 메이트 이름으로 넣는다 (맥 chat 의 settings 클로저)
    public AppSettings ChatSettings() => (settings with { AutomationRules = settings.AutomationRules.ToList() }) with
    {
        MateName = UsageStore.Shared.PokemonName,
    };

    // MARK: 알람

    public List<Alarm> Alarms
    {
        get => alarms;
        set
        {
            alarms = value;
            AlarmStore.Save(alarms);
            Raise();
        }
    }

    /// 알림 그룹 이름 (보이는 순서). 비어 있는 그룹도 남는다.
    public List<string> AlarmGroups
    {
        get => alarmGroups;
        set
        {
            alarmGroups = value;
            AlarmStore.SaveGroups(alarmGroups);
            Raise();
        }
    }

    public IReadOnlyDictionary<Guid, AlarmRunState> AlarmRuns => alarmRuns;
    public IReadOnlyDictionary<Guid, AlarmRunRecord> AlarmLog => alarmLog;

    public void RecordRun(Guid id, string message, bool failed, string? output = null, RunUsage? usage = null, bool count = true)
    {
        alarmLog = AlarmRunLog.Record(alarmLog, id, message, failed, DateTimeOffset.Now, output, usage, count);
        AlarmRunLog.Save(alarmLog);
        Raise(nameof(AlarmLog));
    }

    public void MarkRunning(Guid id)
    {
        alarmRuns = new Dictionary<Guid, AlarmRunState>(alarmRuns) { [id] = new AlarmRunState.Running() };
        Raise(nameof(AlarmRuns));
    }

    /// 결과는 6초 보여 주고 지운다.
    public async void MarkDone(Guid id, string message, bool failed = false, string? output = null, RunUsage? usage = null)
    {
        var state = new AlarmRunState.Done(message, failed);
        alarmRuns = new Dictionary<Guid, AlarmRunState>(alarmRuns) { [id] = state };
        Raise(nameof(AlarmRuns));
        RecordRun(id, message, failed, output, usage);
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (alarmRuns.TryGetValue(id, out var now) && now == state)
        {
            var next = new Dictionary<Guid, AlarmRunState>(alarmRuns);
            next.Remove(id);
            alarmRuns = next;
            Raise(nameof(AlarmRuns));
        }
    }

    /// 알림을 지금 바로 실행 (AlarmScheduler 가 연결한다).
    public Action<Alarm> RunAlarmNow { get; set; } = _ => { };

    /// 알림 추가·수정. 시각 알림 먼저, 트리거는 아래로 정렬한다.
    public void Save(Alarm alarm)
    {
        var list = alarms.ToList();
        var i = list.FindIndex(a => a.Id == alarm.Id);
        if (i >= 0) list[i] = alarm; else list.Add(alarm);
        // 안정 정렬 (Swift sort 와 같은 결과): 시각 알림 먼저, 그다음 시·분
        Alarms = list
            .Select((a, index) => (a, index))
            .OrderBy(x => x.a.Trigger != null)
            .ThenBy(x => x.a.Hour).ThenBy(x => x.a.Minute)
            .ThenBy(x => x.index)
            .Select(x => x.a).ToList();
    }

    /// 채팅에서 제안된 알림을 등록한다. 이미 등록한 것은 건너뛴다.
    /// 그룹 이름을 말했으면 그 그룹(없으면 새로 만듦)에, 아니면 같은 제목이 여러 개일 때(09·13·18시 같은) 그 제목으로 묶는다.
    public void Register(IReadOnlyList<Alarm> proposed)
    {
        var counts = proposed.GroupBy(a => a.Title).ToDictionary(g => g.Key, g => g.Count());
        foreach (var original in proposed)
        {
            if (alarms.Any(a => a.Id == original.Id)) continue;
            var alarm = original with { };
            alarm.ResetSchedule(DateTimeOffset.Now);
            if (alarm.Group != null)
                AddGroup(alarm.Group);
            else if (counts.GetValueOrDefault(alarm.Title) > 1)
            {
                alarm.Group = alarm.Title;
                AddGroup(alarm.Title);
            }
            Save(alarm);
        }
    }

    // MARK: 알림 그룹

    /// 그룹 추가. 이미 있으면 그대로 둔다. 실제로 쓴 이름을 돌려준다.
    public string? AddGroup(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        if (!alarmGroups.Contains(name)) AlarmGroups = alarmGroups.Append(name).ToList();
        return name;
    }

    public void RenameGroup(string old, string @new)
    {
        @new = @new.Trim();
        var i = alarmGroups.IndexOf(old);
        if (@new.Length == 0 || @new == old || alarmGroups.Contains(@new) || i < 0) return;
        var groups = alarmGroups.ToList();
        groups[i] = @new;
        AlarmGroups = groups;
        Alarms = alarms.Select(a => a.Group == old ? a with { Group = @new } : a).ToList();
    }

    /// 그룹만 지운다. 안의 알림은 "그룹 없음" 으로 옮긴다.
    public void DeleteGroup(string name)
    {
        AlarmGroups = alarmGroups.Where(g => g != name).ToList();
        Alarms = alarms.Select(a => a.Group == name ? a with { Group = null } : a).ToList();
    }

    /// 끌어다 놓기: 알림을 그룹으로 옮긴다 (null 이면 그룹 없음).
    public void Reorder(Guid id, string? group, Guid? before)
    {
        var next = AlarmOrder.Move(alarms, id, group, before, alarmGroups);
        if (!next.SequenceEqual(alarms)) Alarms = next;
    }

    public bool CanShift(Guid id, bool up) => AlarmOrder.Neighbor(alarms, id, up, alarmGroups) != null;

    public void Shift(Guid id, bool up)
    {
        var next = AlarmOrder.Shift(alarms, id, up, alarmGroups);
        if (!next.SequenceEqual(alarms)) Alarms = next;
    }

    public void Move(Guid alarmId, string? group)
    {
        var i = alarms.FindIndex(a => a.Id == alarmId);
        if (i < 0 || alarms[i].Group == group) return;
        var list = alarms.ToList();
        list[i] = list[i] with { Group = group };
        Alarms = list;
    }

    // MARK: 연결

    public IReadOnlyList<SystemStatus> Systems => systems;
    /// Microsoft 365 커넥터 상태. null 이면 확인 중.
    public ConnectionState? Microsoft365 { get => microsoft365; private set => Set(ref microsoft365, value); }
    public string? ConnectionError { get => connectionError; private set => Set(ref connectionError, value); }
    public bool IsLoadingSystems { get => isLoadingSystems; private set => Set(ref isLoadingSystems, value); }

    public async Task RefreshSystems()
    {
        Microsoft365 = null;
        var snapshot = settings;
        _ = CheckMicrosoft365(snapshot);
        IsLoadingSystems = true;
        try
        {
            systems = await MateCLI.Status(settings);
            Raise(nameof(Systems));
            ConnectionError = null;
        }
        catch (Exception e)
        {
            ConnectionError = e.Message;
        }
        finally { IsLoadingSystems = false; }
    }

    async Task CheckMicrosoft365(AppSettings snapshot)
    {
        try { Microsoft365 = await ConnectorStatus.Check(ConnectorStatus.Microsoft365, snapshot); }
        catch { Microsoft365 = ConnectionState.Unavailable; }
    }

    public async Task<List<ConnectResult>> Connect(ConnectTarget target, string username, string secret)
    {
        try
        {
            return await MateCLI.Connect(target, username, secret, settings);
        }
        catch (Exception e)
        {
            return new List<ConnectResult> { new("", "error", e.Message) };
        }
        finally { _ = RefreshSystems(); }
    }

    public ConnectionState? Status(string system) => systems.FirstOrDefault(s => s.System == system)?.Status;

    // MARK: 끝낼 때 (맥 applicationShouldTerminate / applicationWillTerminate)

    /// 끄기 전에 물어볼 일이 있는지 알려 주는 곳. 각각 "처리 중인 일" 한 줄씩을 돌려준다 (없으면 빈 목록).
    /// 작업 세션(Task 8)은 처리 중인 티켓의 상태 줄을 돌려주는 함수를 여기에 더한다.
    public List<Func<IEnumerable<string>>> QuitGuards { get; } = new();

    /// 앱이 정말 꺼질 때 한 번 불린다. 하위 프로세스를 멈추는 곳 (맥 stopAllForQuit)
    public event Action? Quitting;

    internal void RaiseQuitting() => Quitting?.Invoke();

    /// 지금 처리 중인 일의 상태 줄들
    public List<string> ActiveWork() => QuitGuards.SelectMany(g => g()).ToList();
}
