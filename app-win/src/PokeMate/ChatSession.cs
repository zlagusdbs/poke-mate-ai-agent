using System.Windows;
using System.Windows.Threading;

namespace PokeMate;

/// 로그인 진행 상태 (맥 ChatSession.LoginState)
public enum LoginState { Idle, WaitingBrowser, NeedsCode, Verifying }

/// 대화에서 "보낼 곳"(작업 세션)을 쓰는 데 필요한 작업 세션 쪽 기능 (맥 WorkSessionCenter 의 일부).
/// 작업 세션(Task 8)이 이 인터페이스를 구현해 `ChatSession.WorkHost` 에 꽂는다. 꽂기 전에는 `LocalWorkSessionHost` 가 쓰인다.
public interface IWorkSessionHost
{
    /// 그 레포의 작업 세션이 실행 중이거나 대기 중인가 ("작업 세션 차례를 기다리는 중…" 표시용)
    bool IsBusy(string repo);

    /// 같은 레포의 작업은 차례로 실행한다. body 는 항상 UI 스레드에서 불린다 (맥 exclusive).
    Task Exclusive(string repo, string label, Func<Task> body);

    /// 레포의 첫 메시지 앞에 붙일 글 (이전 세션 요약 + 레포 소개). 맥 prefix(for:)
    string Prefix(string repo);

    /// 이어 쓸 Claude 세션 id (없으면 null). 맥 book.resumeId
    string? ResumeId(string repo);

    /// 대화로 보낸 질문의 결과를 세션 기록에 반영 (맥 recordTalk)
    void RecordTalk(string repo, string sessionId, RunUsage? usage);

    /// 빈 작업 세션을 만든다. 이미 있으면 false (맥 create(repo:))
    bool Create(string repo);

    /// "보낼 곳" 메뉴에 올릴 세션 이름들 (역할 세션 제외, 최근 쓴 순). 맥 RoutePicker 의 sessions
    IReadOnlyList<string> RouteTargets();

    /// 이미 세션이 있는 레포 이름 전부 ("새 작업 세션" 메뉴에서 뺄 것)
    ISet<string> ExistingRepos();

    /// 세션 목록이 바뀌었을 때 (UI 스레드)
    event Action? Changed;
}

/// 채팅 상태 (맥 ChatSession). 질문은 한 번에 하나씩 처리하고, 처리 중 들어온 요청은 순서대로 기다린다.
/// 모든 멤버는 UI 스레드에서 부르고, 바뀌면 PropertyChanged(속성 이름) 가 불린다.
public sealed class ChatSession : NotifyObject
{
    public static readonly TimeSpan PermissionWait = TimeSpan.FromSeconds(600);

    /// 작업 세션 쪽 기능. Task 8 이 자기 구현을 꽂는다.
    public static IWorkSessionHost WorkHost { get; set; } = new LocalWorkSessionHost();

    public sealed class StoredChat
    {
        public List<ChatMessage> Messages { get; set; } = new();
        public string? SessionId { get; set; }
        public ChatRouting? Routing { get; set; }
    }

    abstract record Job
    {
        public sealed record Chat(string Prompt, IReadOnlyList<ImageAttachment> Images) : Job;
        public sealed record Work(string Repo, string Prompt, IReadOnlyList<ImageAttachment> Images) : Job;
        public sealed record Report(string Title, string Prompt, Action<ClaudeResponse, RunUsage?> Completion) : Job;
    }

    readonly List<ChatMessage> messages = new();
    bool isBusy;
    string? lastFailedPrompt;
    IReadOnlyList<ImageAttachment> lastFailedImages = Array.Empty<ImageAttachment>();
    string? lastFailedRepo;
    LoginState loginState = LoginState.Idle;
    string? activity;
    PermissionRequest? pendingPermission;
    TaskCompletionSource<PermissionDecision>? permissionAnswer;
    List<string> sessionAllow = new();
    ChatRouting routing = new();
    string? sessionId;
    readonly List<Job> queue = new();
    readonly ClaudeRunner runner = new();
    readonly ClaudeLogin login = new();
    readonly Func<AppSettings> settings;
    readonly string? storage;
    readonly Dispatcher? dispatcher = Application.Current?.Dispatcher;
    readonly HashSet<ClaudeRunner> automationRunners = new();

    /// 일반 대화 하나가 끝났을 때 (Claude 세션 id, 사용량). 맥 onRun
    public Action<string, RunUsage?>? OnRun { get; set; }

    /// 승인 카드가 떴을 때 / 답해서 사라졌을 때 (맥 onPermissionAsked / onPermissionDone)
    public event Action<PermissionRequest>? PermissionAsked;
    public event Action? PermissionDone;

    /// 카드 하나(메시지 한 개)가 바뀌었을 때. 메시지 목록 전체가 바뀔 때는 PropertyChanged("Messages").
    public event Action<Guid>? MessageUpdated;

    /// 대화 파일 `chat.json` 에 저장하는 기본 대화
    public ChatSession(Func<AppSettings> settings) : this(settings, AppPaths.ChatFile) { }

    /// storage 가 null 이면 저장하지 않는다 (작업 세션 상세의 대화). workRepo 가 있으면 보낼 곳이 그 레포로 고정된다.
    public ChatSession(Func<AppSettings> settings, string? storage, string? sessionId = null, string? workRepo = null)
    {
        this.settings = settings;
        this.storage = storage;
        this.sessionId = sessionId;
        FixedRepo = workRepo;
        if (workRepo != null) routing = new ChatRouting(workRepo);
        if (storage != null)
        {
            try
            {
                if (Json.TryDeserialize<StoredChat>(File.ReadAllText(storage)) is { } stored)
                {
                    messages.AddRange(stored.Messages);
                    this.sessionId = stored.SessionId;
                    if (workRepo == null && stored.Routing != null) routing = stored.Routing;
                }
            }
            catch { }
        }
    }

    public IReadOnlyList<ChatMessage> Messages => messages;
    public bool IsBusy { get => isBusy; private set => Set(ref isBusy, value); }
    public string? LastFailedPrompt { get => lastFailedPrompt; private set => Set(ref lastFailedPrompt, value); }
    public LoginState LoginState { get => loginState; private set => Set(ref loginState, value); }
    /// claude 가 알려 준 로그인 주소. 브라우저가 안 떴을 때 카드에서 직접 연다.
    public string? LoginUrl { get; private set; }
    /// 실행 중 한 줄 ("Bash 실행 중: …"). 없으면 "생각 중…"
    public string? Activity { get => activity; private set => Set(ref activity, value); }
    public PermissionRequest? PendingPermission { get => pendingPermission; private set { pendingPermission = value; Raise(); Raise(nameof(PendingPermissions)); } }
    /// 승인 대기 목록. 한 번에 하나만 묻는다 (두 번째 요청은 거부해 돌려보냄)
    public IReadOnlyList<PermissionRequest> PendingPermissions => pendingPermission == null ? Array.Empty<PermissionRequest>() : new[] { pendingPermission };
    /// 이 대화에서 항상 허용 중인 규칙
    public IReadOnlyList<string> SessionAllow => sessionAllow;
    public ChatRouting Routing { get => routing; private set { routing = value; Raise(); } }
    public string? FixedRepo { get; }
    /// 지금 이어 쓰는 Claude 세션 id (메이트 대화)
    public string? SessionId => sessionId;

    void Post(Action action)
    {
        if (dispatcher == null) action();
        else dispatcher.BeginInvoke(action);
    }

    public void ClearSessionAllow()
    {
        sessionAllow = new List<string>();
        Raise(nameof(SessionAllow));
    }

    // MARK: 보낼 곳

    /// 보낼 곳을 고른다 (null = 메이트). 레포가 고정된 대화에서는 무시. 맥 route(to:)
    public void Route(string? repo)
    {
        if (FixedRepo != null) return;
        routing.Choose(repo);
        Raise(nameof(Routing));
        Save();
    }

    // MARK: 보내기

    public void Send(string text, IReadOnlyList<ImageAttachment>? images = null)
    {
        images ??= Array.Empty<ImageAttachment>();
        var trimmed = text.Trim();
        if (trimmed.Length == 0 && images.Count == 0) return;
        var shown = trimmed;
        if (trimmed.Length == 0) trimmed = "이 이미지를 보고 설명해줘.";
        var message = new ChatMessage(ChatMessage.RoleKind.User, shown, images: images.Select(i => i.Path)) { Session = routing.Repo };
        Append(message);
        trimmed += ChatHints.For(trimmed, DateTimeOffset.Now);
        Enqueue(trimmed, images);
    }

    void Enqueue(string prompt, IReadOnlyList<ImageAttachment> images)
    {
        if (routing.Repo is { } repo) queue.Add(new Job.Work(repo, prompt, images));
        else queue.Add(new Job.Chat(prompt, images));
        ProcessNext();
    }

    /// 답변이 제안한 작업 세션으로 직전 질문을 보낸다 (제안 카드의 [보내기]). 맥 sendSuggested
    public void SendSuggested(Guid messageId, string repo)
    {
        var i = messages.FindIndex(m => m.Id == messageId);
        if (i < 0) return;
        var question = messages.Take(i).LastOrDefault(m => m.Role == ChatMessage.RoleKind.User && m.Session == null);
        if (question == null) return;
        Update(messageId, m => m.RouteSent = true);
        Route(repo);
        Append(new ChatMessage(ChatMessage.RoleKind.User, question.Text) { Session = repo });
        queue.Add(new Job.Work(repo, question.Text + ChatHints.For(question.Text, DateTimeOffset.Now), Array.Empty<ImageAttachment>()));
        ProcessNext();
    }

    /// 알림 실행. 새 세션으로 실행해 결과를 채팅창에 이어 붙이고, 이후 대화는 그 결과에서 이어간다.
    public void RunReport(string title, string prompt, Action<ClaudeResponse, RunUsage?> completion)
    {
        queue.Add(new Job.Report(title, prompt, completion));
        ProcessNext();
    }

    /// 자동 처리 한 번. 대화 큐와 따로, 새 프로세스로 돈다 (맥 runAutomation).
    /// quiet 이 아니면 "🔔 제목" 과 결과를 채팅창에 붙인다. completion 은 UI 스레드에서 불린다.
    public void RunAutomation(string title, string prompt, ClaudeCommand.WriteAccess access, Action<ClaudeResponse, RunUsage?> completion,
                              TimeSpan? timeout = null, string? sessionId = null, string? newSessionId = null, bool quiet = false,
                              IDictionary<string, string>? extraEnvironment = null)
    {
        if (!quiet) Append(new ChatMessage(ChatMessage.RoleKind.User, $"🔔 {title}"));
        var automation = new ClaudeRunner();
        automationRunners.Add(automation);
        var snapshot = settings();
        _ = Task.Run(async () =>
        {
            var response = await automation.Run(prompt, sessionId, snapshot, writeTools: access.Tools, writeDirs: access.Dirs,
                                                timeout: timeout ?? ClaudeRunner.AutomationTimeout, newSessionId: newSessionId,
                                                extraEnvironment: extraEnvironment);
            var usage = automation.LastUsage;
            Post(() =>
            {
                if (!quiet)
                {
                    switch (response)
                    {
                        case ClaudeResponse.Success success: Append(new ChatMessage(ChatMessage.RoleKind.Assistant, success.Text)); break;
                        case ClaudeResponse.Failure failure: Append(new ChatMessage(ChatMessage.RoleKind.Error, failure.Reason.BackgroundMessage)); break;
                    }
                }
                automationRunners.Remove(automation);
                completion(response, usage);
            });
        });
    }

    /// 앱을 끌 때: 돌고 있는 claude 와 그 자식 프로세스를 모두 멈춘다 (맥 stopAllForQuit)
    public void StopAllForQuit()
    {
        foreach (var r in automationRunners.ToList()) r.Cancel();
        runner.Cancel();
        login.Cancel();
    }

    public void Retry()
    {
        if (lastFailedPrompt is not { } prompt) return;
        LastFailedPrompt = null;
        if (lastFailedRepo is { } repo) queue.Add(new Job.Work(repo, prompt, lastFailedImages));
        else queue.Add(new Job.Chat(prompt, lastFailedImages));
        ProcessNext();
    }

    /// 멈추기: 기다리는 질문을 비우고, 승인 대기는 거부하고, 실행 중인 claude 를 멈춘다 (맥 cancel)
    public void Cancel()
    {
        queue.Clear();
        AnswerPermission(new PermissionDecision.Deny("사용자가 취소했어요"));
        runner.Cancel();
    }

    /// 맥 cancel 과 같다. (`Stop()` 은 Task 6 계획서의 이름)
    public void Stop() => Cancel();

    // MARK: 승인

    public void AnswerPermission(PermissionDecision decision)
    {
        if (pendingPermission is not { } request || permissionAnswer is not { } answer) return;
        if (decision is PermissionDecision.AllowAlways) AddAllow(request.Rules);
        if (decision is PermissionDecision.AllowRules allowRules) AddAllow(allowRules.Rules);
        PendingPermission = null;
        permissionAnswer = null;
        PermissionDone?.Invoke();
        answer.TrySetResult(decision);
    }

    void AddAllow(IEnumerable<string> rules)
    {
        var next = sessionAllow.ToList();
        foreach (var rule in rules.Where(r => !next.Contains(r))) next.Add(rule);
        sessionAllow = next;
        Raise(nameof(SessionAllow));
    }

    async Task<PermissionDecision> AskPermission(PermissionRequest request)
    {
        if (pendingPermission != null) return new PermissionDecision.Deny("다른 승인을 기다리는 중이에요");
        var answer = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        permissionAnswer = answer;
        PendingPermission = request;
        PermissionAsked?.Invoke(request);
        var id = request.Id;
        _ = Task.Delay(PermissionWait).ContinueWith(_ => Post(() =>
        {
            if (pendingPermission?.Id == id) AnswerPermission(new PermissionDecision.Deny("10분 동안 답이 없어 거부했어요"));
        }));
        return await answer.Task;
    }

    ToolHooks? MakeToolHooks(IReadOnlyList<string>? preallowed = null)
    {
        if (!settings().ClaudeTools) return null;
        preallowed ??= Array.Empty<string>();
        // ClaudeRunner 는 이 콜백들을 다른 스레드에서 부른다. 상태는 UI 스레드에서만 바꾼다.
        return new ToolHooks(preallowed.Concat(sessionAllow.Where(a => !preallowed.Contains(a))).ToList(),
                             text => Post(() => Activity = text),
                             request =>
                             {
                                 var result = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                                 Post(async () =>
                                 {
                                     try { result.TrySetResult(await AskPermission(request)); }
                                     catch (Exception e) { result.TrySetResult(new PermissionDecision.Deny(e.Message)); }
                                 });
                                 return result.Task;
                             });
    }

    // MARK: 대화 관리

    /// Claude 를 부르지 않고 메이트 말로 바로 붙인다 (처음 인사 등).
    public void Say(string text) => Append(new ChatMessage(ChatMessage.RoleKind.Assistant, text));

    /// 계정이 바뀌면 이전 세션을 이어 쓸 수 없어서 세션만 잊는다 (화면의 대화는 남김).
    public void ForgetSession()
    {
        sessionId = null;
        ClearSessionAllow();
        Save();
    }

    public void NewConversation()
    {
        Cancel();
        messages.Clear();
        sessionId = null;
        ClearSessionAllow();
        LastFailedPrompt = null;
        if (FixedRepo == null) { routing.Reset(); Raise(nameof(Routing)); }
        Raise(nameof(Messages));
        Save();
    }

    // MARK: 실행

    void ProcessNext()
    {
        if (isBusy || queue.Count == 0) return;
        IsBusy = true;
        var job = queue[0];
        queue.RemoveAt(0);
        _ = RunJob(job);
    }

    async Task RunJob(Job job)
    {
        try
        {
            switch (job)
            {
                case Job.Chat chat:
                {
                    var notes = routing.TakeMatePrefix();
                    if (notes.Length > 0) Save();
                    var response = await runner.Run(notes + chat.Prompt, sessionId, settings(), images: chat.Images, hooks: MakeToolHooks(), automation: false);
                    Activity = null;
                    if (response is ClaudeResponse.Success { SessionId.Length: > 0 } ok) OnRun?.Invoke(ok.SessionId, runner.LastUsage);
                    Handle(response, chat.Prompt, chat.Images);
                    break;
                }
                case Job.Work work:
                    await RunWork(work.Repo, work.Prompt, work.Images);
                    break;
                case Job.Report report:
                {
                    Append(new ChatMessage(ChatMessage.RoleKind.User, $"🔔 {report.Title}"));
                    var response = await runner.Run(report.Prompt, null, settings());
                    Handle(response, null, Array.Empty<ImageAttachment>(), interactive: false);
                    report.Completion(response, runner.LastUsage);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            App.Log($"대화 처리 중 오류: {e}");
            Activity = null;
            Append(new ChatMessage(ChatMessage.RoleKind.Error, new ClaudeFailure.Other(e.Message).Message));
        }
        IsBusy = false;
        ProcessNext();
    }

    async Task RunWork(string repo, string prompt, IReadOnlyList<ImageAttachment> images)
    {
        var host = WorkHost;
        var access = ClaudeCommand.LoadWriteAccess();
        Activity = host.IsBusy(repo) ? $"{repo} 작업 세션 차례를 기다리는 중…" : null;
        await host.Exclusive(repo, "채팅", async () =>
        {
            Activity = null;
            var full = host.Prefix(repo) + prompt;
            var response = await runner.Run(full, host.ResumeId(repo), settings(), images: images, writeTools: access.Tools, writeDirs: access.Dirs,
                                            hooks: MakeToolHooks(access.Tools), timeout: ClaudeRunner.AutomationTimeout);
            Activity = null;
            if (response is ClaudeResponse.Success { SessionId.Length: > 0 } ok) host.RecordTalk(repo, ok.SessionId, runner.LastUsage);
            if (FixedRepo == null) { routing.NoteSent(repo, prompt); Save(); }
            Handle(response, prompt, images, repo);
        });
    }

    /// `interactive` 는 사용자가 보낸 채팅·작업 세션 질문일 때만 true. 알림 실행은 로그인을 시작하지 않고 로그인이 필요하다고만 남긴다.
    void Handle(ClaudeResponse response, string? prompt, IReadOnlyList<ImageAttachment> images, string? repo = null, bool interactive = true)
    {
        lastFailedRepo = repo;
        switch (response)
        {
            case ClaudeResponse.Success success:
            {
                if (repo == null && success.SessionId.Length > 0) sessionId = success.SessionId;
                var (afterSuggest, suggested) = repo == null ? RouteSuggestion.Extract(success.Text) : (success.Text, null);
                var (afterAlarms, alarms) = AlarmProposal.Extract(afterSuggest);
                var (afterJira, jira) = JiraProposal.Extract(afterAlarms);
                var (afterRules, rules) = RuleProposal.Extract(afterJira);
                var (afterWorklogs, worklogs) = WorklogProposal.Extract(afterRules);
                var (clean, notes) = NoteProposal.Extract(afterWorklogs);
                var message = new ChatMessage(ChatMessage.RoleKind.Assistant, clean, alarms: alarms, jira: jira)
                {
                    Rules = rules, Worklogs = worklogs, Notes = notes, Session = repo, RouteSuggest = suggested,
                };
                Append(message);
                break;
            }
            case ClaudeResponse.Failure failure:
            {
                Append(new ChatMessage(ChatMessage.RoleKind.Error, interactive ? failure.Reason.Message : failure.Reason.BackgroundMessage) { Session = repo });
                lastFailedImages = images;
                if (ClaudeFailure.StartsLogin(failure.Reason, interactive)) StartLogin(prompt);
                else if (failure.Reason is ClaudeFailure.NotLoggedIn) { }   // 알림 실행: 로그인은 사용자가 채팅이나 설정에서 시작한다
                else if (failure.Reason is not (ClaudeFailure.Cancelled or ClaudeFailure.UsageLimit))   // 한도는 다시 시도해도 같아서 버튼을 숨긴다
                    LastFailedPrompt = prompt;
                break;
            }
        }
    }

    // MARK: 로그인

    /// `claude auth login` 을 실행해 브라우저를 띄우고, 로그인되면 질문을 다시 보낸다.
    public void StartLogin(string? retry)
    {
        LoginUrl = null;
        LoginState = LoginState.WaitingBrowser;
        login.Start(settings(), e =>
        {
            switch (e)
            {
                case ClaudeLogin.Event.BrowserOpened:
                    break;
                case ClaudeLogin.Event.NeedsCode needs:
                    LoginUrl = needs.Url;
                    if (loginState == LoginState.WaitingBrowser) LoginState = LoginState.NeedsCode;
                    break;
                case ClaudeLogin.Event.Finished finished:
                    LoginState = LoginState.Idle;
                    if (finished.Ok)
                    {
                        Append(new ChatMessage(ChatMessage.RoleKind.Assistant, "로그인됐어요."));
                        if (retry != null)
                        {
                            queue.Add(new Job.Chat(retry, lastFailedImages));
                            ProcessNext();
                        }
                    }
                    else
                    {
                        Append(new ChatMessage(ChatMessage.RoleKind.Error, "로그인하지 못했어요. 다시 시도해 주세요."));
                        LastFailedPrompt = retry;
                    }
                    break;
            }
        });
    }

    public void SubmitLoginCode(string code)
    {
        LoginState = LoginState.Verifying;
        login.Submit(code);
    }

    public void CancelLogin()
    {
        login.Cancel();
        LoginState = LoginState.Idle;
    }

    // MARK: 메시지

    /// 카드에서 초안을 고치거나 등록했을 때 그 메시지를 바꿔 저장한다.
    public void Update(Guid id, Action<ChatMessage> change)
    {
        var message = messages.FirstOrDefault(m => m.Id == id);
        if (message == null) return;
        change(message);
        Save();
        MessageUpdated?.Invoke(id);
    }

    void Append(ChatMessage message)
    {
        messages.Add(message);
        Save();
        Raise(nameof(Messages));
    }

    void Save()
    {
        if (storage == null) return;
        try
        {
            var stored = new StoredChat { Messages = messages.ToList(), SessionId = sessionId, Routing = FixedRepo == null ? routing : null };
            AppPaths.WriteAtomic(storage, Json.Serialize(stored, indented: false));
        }
        catch (Exception e) { App.Log($"대화를 저장하지 못했어요: {e.Message}"); }
    }
}

/// 작업 세션 화면(Task 8)이 연결되기 전의 대체 구현. 세션 기록(sessions.json)만 직접 읽고 쓴다.
public sealed class LocalWorkSessionHost : IWorkSessionHost
{
    readonly SerialQueue queue = new();
    readonly Dictionary<string, int> waiting = new();
    WorkSessionBook? book;

    WorkSessionBook Book => book ??= WorkSessionBook.Load();

    public event Action? Changed;

    public bool IsBusy(string repo) => waiting.GetValueOrDefault(repo) > 0;

    public async Task Exclusive(string repo, string label, Func<Task> body)
    {
        waiting[repo] = waiting.GetValueOrDefault(repo) + 1;
        var dispatcher = Application.Current.Dispatcher;
        await queue.Run(repo, async () =>
        {
            try { await await dispatcher.InvokeAsync(body); }
            finally { await dispatcher.InvokeAsync(() => waiting[repo] = Math.Max(0, waiting.GetValueOrDefault(repo) - 1)); }
        });
    }

    public string Prefix(string repo) =>
        Book.ResumeId(repo) == null && PipelineRoleExtensions.Split(repo) == null ? WorkspaceRepos.Bootstrap(repo) : "";

    public string? ResumeId(string repo) => Book.ResumeId(repo);

    public void RecordTalk(string repo, string sessionId, RunUsage? usage)
    {
        Book.Record(repo, sessionId, null, usage, DateTimeOffset.Now);
        Book.Save();
        Changed?.Invoke();
    }

    public bool Create(string repo)
    {
        if (!Book.AddEmpty(repo, DateTimeOffset.Now)) return false;
        Book.Save();
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<string> RouteTargets() =>
        Book.Sessions.Values.Where(s => PipelineRoleExtensions.Split(s.Repo) == null).OrderByDescending(s => s.LastUsed).Select(s => s.Repo).ToList();

    public ISet<string> ExistingRepos() => Book.Sessions.Keys.ToHashSet();
}

/// 승인 카드가 뜨면 알림(`{메이트 이름} 승인 요청`)을 띄우고, 답하면 거둔다 (맥 main.swift 의 onPermissionAsked/Done).
/// 연달아 뜨는 카드는 5초에 알림 하나로 묶는다. 대화 세션을 새로 만드는 쪽(작업 세션 상세)은 `Watch(chat)` 를 부른다.
public sealed class ChatPermissionAlerts : IAppService
{
    static readonly PermissionAlertGate Gate = new();
    static AppModel? model;

    public void Start(AppModel appModel)
    {
        model = appModel;
        Watch(appModel.Chat);
    }

    /// 그 대화의 승인 요청을 알림으로 알린다 (한 번만 부르면 된다)
    public static void Watch(ChatSession chat)
    {
        chat.PermissionAsked += request =>
        {
            if (!Gate.ShouldAlert(DateTimeOffset.Now)) return;
            var settings = model?.Settings ?? AppPaths.LoadSettings();
            Notifier.Show($"{UsageStore.Shared.MateName(settings)} 승인 요청", PermissionAlertGate.Body(request), tag: PermissionAlertGate.NotificationId);
        };
        chat.PermissionDone += () => Notifier.Clear(PermissionAlertGate.NotificationId);
    }
}
