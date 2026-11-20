using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace PokeMate;

/// 작업 세션 센터 (맥 WorkSessionCenter). 레포(또는 `{도메인}:{역할}`)마다 Claude 세션을 하나씩 두고,
/// 트리거 알림이 새 이벤트를 알리면 레포 정하기 → 그 레포의 세션에 이어 쓰기 / Jira 역할별 처리 / 도메인 리뷰어 리뷰로 나눠 처리한다.
/// 같은 레포(도메인)의 일은 차례로, 다른 레포는 동시에. 지금 하는 일은 `automation\sessions-live.json` 에 남겨 파이썬 서버(`work_sessions` 도구)가 읽는다.
///
/// 모든 멤버는 UI 스레드에서 부른다. 바뀌면 PropertyChanged(Book·Running·Queued·Pipelines·Reviewing·MonthUsage·PendingSummary·RequestReset) 가 불리고,
/// 세션 목록이 바뀌면 `Changed` 도 불린다.
///
/// 세션을 이어 쓸지 새로 시작할지 고르는 곳은 한 군데 `ReuseSession(key)` 다. 레포 세션(`Process`), 역할 세션(`PipelineIO.SessionId`),
/// 리뷰어 세션(`ProcessReview`)이 모두 여기를 지난다.
public sealed class WorkSessionCenter : NotifyObject, IWorkSessionHost
{
    /// 하나뿐인 센터 (맥 shared). 처음 부를 때 만든다. 앱 시작 때는 WorkSessionService 가 Start 를 부른다.
    public static WorkSessionCenter Shared => shared ??= new WorkSessionCenter();
    static WorkSessionCenter? shared;

    public const string RoutingSlot = "(레포 정하는 중)";

    public const string ResumeNote = "이 작업 세션은 같은 레포의 이전 티켓을 처리하던 세션이다. 이미 이 세션에서 읽은 지식(CLAUDE.md, 문서, 위키, 예전 티켓, git 기록)은 다시 읽지 말고, 이번 티켓에 새로 필요한 것만 찾아라.";
    public const string SummaryPrompt = "이 작업 세션을 새 세션으로 바꾼다. 지금까지 이 세션에서 익힌 레포 지식과 주의점(구조, 빌드·테스트 방법, 규칙, 자주 쓰는 근거 문서·위키 pageId, 처리한 티켓과 결과, 하지 말아야 할 것)을 다음 세션이 그대로 이어받을 수 있게 요약한다. 도구를 쓰지 말고 요약만 답한다. 40줄 이내.";

    static readonly Regex RepoInTitle = new(@"·\s+([A-Za-z0-9._-]+)\s+#\d+", RegexOptions.Compiled);

    readonly SerialQueue queue = new();
    readonly Dispatcher dispatcher;

    WorkSessionBook book;
    Dictionary<string, string> running = new();
    Dictionary<string, List<string>> queued = new();
    Dictionary<string, PipelineState> pipelines = new();
    Dictionary<string, string> reviewing = new();
    Dictionary<string, string> pendingSummary = new();
    RunUsage monthUsage = new();
    string? requestReset;

    /// 앱을 끄는 중이면 역할 실행의 끊김을 "중단" 으로 기록한다 (다음에 켤 때 이어 간다)
    public bool Quitting { get; set; }

    /// 문맥이 문턱을 처음 넘은 세션 (세션, 문맥 %)
    public Action<WorkSession, int>? OnContextAlert { get; set; }
    /// 역할별 처리 안내 (제목, 내용)
    public Action<string, string>? OnPipelineNotice { get; set; }

    public event Action? Changed;

    public WorkSessionCenter()
    {
        dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        book = WorkSessionBook.Load();
        WriteLive();
        RefreshAllUsage();
    }

    // MARK: 앱에 연결 (WorkSessionService.Start)

    /// 대화·알림·미리 알림·끝내기에 연결하고 끊긴 티켓을 이어 간다 (맥 main.swift 의 연결 부분). 한 번만 부른다.
    public void Start(AppModel model)
    {
        ChatSession.WorkHost = this;

        TriggerWatcher.PipelineHandler = (alarm, item, prompt, title, _, completion) => HandlePipeline(alarm, item, prompt, title, model.Chat, completion);
        TriggerWatcher.ReviewHandler = (alarm, item, prompt, title, access, completion) => HandleReview(alarm, item, prompt, title, access, model.Chat, completion);
        TriggerWatcher.WorkSessionHandler = (alarm, item, prompt, title, access, completion) => Handle(alarm, item, prompt, title, access, model.Chat, completion);

        OnContextAlert = (session, percent) =>
        {
            var title = $"작업 세션 문맥 {model.Settings.WorkSessionAlertPercent}% 넘음";
            var body = $"{session.Repo} · 지금 {percent}% · 처리한 티켓 {session.Keys.Count}건";
            Notifier.Show(title, body, onClick: Notifier.Opening("workSessions"));
            ReminderCenter.Shared?.Add(title, body, false, sessionRepo: session.Repo);
        };
        OnPipelineNotice = (title, body) =>
        {
            Notifier.Show(title, body, onClick: Notifier.Opening("workSessions"));
            ReminderCenter.Shared?.Add(title, body, false, null);
        };

        // 카드의 "새 세션으로 바꾸기": 작업 탭을 열고(기본 동작) 그 레포의 확인 창을 띄우게 한다
        var previous = ReminderCenter.ResetSession;
        ReminderCenter.ResetSession = repo =>
        {
            previous(repo);
            RequestReset = repo;
        };

        model.QuitGuards.Add(() => pipelines.Values.Where(p => p.IsActive).Select(p => p.StatusText).ToList());
        model.Quitting += () => Quitting = true;

        // 앱이 꺼져서 끊긴 티켓을 이어서 한다
        ResumePipelines(model.Chat, (title, text, failed) => ReminderCenter.Shared?.Add(title, text, failed, null));
    }

    // MARK: 상태

    public WorkSessionBook Book => book;
    /// 레포(또는 도메인, 레포 정하는 중이면 RoutingSlot) → 지금 처리 중인 건
    public IReadOnlyDictionary<string, string> Running => running;
    /// 레포(또는 도메인) → 차례를 기다리는 건
    public IReadOnlyDictionary<string, List<string>> Queued => queued;
    /// 도메인 → 마지막으로 본 역할별 처리 상태
    public IReadOnlyDictionary<string, PipelineState> Pipelines => pipelines;
    /// 도메인 → 리뷰 중인 PR
    public IReadOnlyDictionary<string, string> Reviewing => reviewing;
    public IReadOnlyDictionary<string, string> PendingSummary => pendingSummary;
    public RunUsage MonthUsage { get => monthUsage; private set { monthUsage = value; Raise(); } }

    /// 카드의 [새 세션으로 바꾸기] 가 요청한 레포. 작업 탭이 받아 확인 창을 띄우고 지운다.
    public string? RequestReset
    {
        get => requestReset;
        set { if (requestReset == value) return; requestReset = value; Raise(); }
    }

    void SetRunning(string key, string? value)
    {
        if (value == null) running.Remove(key); else running[key] = value;
        Raise(nameof(Running));
        WriteLive();
    }

    void AddQueued(string key, string label)
    {
        if (!queued.TryGetValue(key, out var list)) queued[key] = list = new List<string>();
        list.Add(label);
        Raise(nameof(Queued));
        WriteLive();
    }

    void RemoveQueued(string key, string label)
    {
        if (queued.TryGetValue(key, out var list))
        {
            var i = list.IndexOf(label);
            if (i >= 0) list.RemoveAt(i);
            if (list.Count == 0) queued.Remove(key);
        }
        Raise(nameof(Queued));
        WriteLive();
    }

    void SetPipeline(string domain, PipelineState state)
    {
        pipelines[domain] = state;
        Raise(nameof(Pipelines));
    }

    void SetReviewing(string domain, string? value)
    {
        if (value == null) reviewing.Remove(domain); else reviewing[domain] = value;
        Raise(nameof(Reviewing));
    }

    /// 세션 기록이 바뀌었다고 화면·채팅에 알린다
    void Touch()
    {
        Raise(nameof(Book));
        Changed?.Invoke();
    }

    void Save()
    {
        book.Save();
        Touch();
    }

    // MARK: 사용량

    bool ApplyUsage(string repo)
    {
        if (!book.Sessions.TryGetValue(repo, out var s) || TranscriptFile(s.Id) is not { } file) return false;
        try
        {
            var total = TranscriptUsage.Total(Paths.ReadShared(file));
            book.ApplyTranscript(repo, total.Usage, total.LastContext);
            return true;
        }
        catch { return false; }
    }

    public void RefreshUsage(string repo)
    {
        if (ApplyUsage(repo)) Touch();
    }

    public void RefreshAllUsage()
    {
        foreach (var repo in book.Sessions.Keys.ToList()) ApplyUsage(repo);
        Save();
        RefreshMonth();
    }

    public void RefreshMonth()
    {
        var now = DateTime.Now;
        var start = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local));
        var ids = book.Sessions.Values.Select(s => s.Id)
            .Concat(book.Archived.Where(s => s.LastUsed >= start).Select(s => s.Id))
            .Where(id => id.Length > 0).ToHashSet();
        var total = new RunUsage();
        foreach (var id in ids)
        {
            if (TranscriptFile(id) is not { } file) continue;
            try { total.Add(TranscriptUsage.Total(Paths.ReadShared(file), start).Usage); } catch { }
        }
        MonthUsage = total;
    }

    /// Claude 설정 폴더의 대화 기록 파일 (없으면 null)
    public static string? TranscriptFile(string sessionId) =>
        sessionId.Length == 0 ? null : ReviewEval.TranscriptFile(AppPaths.ClaudeConfigDir, sessionId);

    // MARK: 세션 고르기 (이어 쓸지 새로 시작할지)

    /// 이어 쓸 Claude 세션 id (새로 시작해야 하면 null). 설정의 "세션 이어 쓰기" 기준에 걸리면 옛 세션을 보관하고 이유를 남긴 빈 세션을 둔다.
    /// 이어 쓸지 정하는 규칙은 이 메서드와 Core 의 `WorkSessionBook.ReuseId`·`SessionReuse.FreshReason` 에만 있다.
    public string? ReuseSession(string key)
    {
        var before = book.Sessions.TryGetValue(key, out var s) ? s.Copy() : null;
        var id = book.ReuseId(key, new SessionReuse(AppPaths.LoadSettings()), DateTimeOffset.Now);
        var after = book.Sessions.GetValueOrDefault(key);
        if (!Equals(before, after)) Save();
        return id;
    }

    // MARK: IWorkSessionHost

    public bool IsBusy(string repo) => running.ContainsKey(repo) || queued.ContainsKey(repo);

    /// 빈 작업 세션을 만든다. 이미 있으면 false
    public bool Create(string repo)
    {
        if (!book.AddEmpty(repo, DateTimeOffset.Now)) return false;
        Save();
        return true;
    }

    /// 레포의 첫 메시지 앞에 붙일 글 (이전 세션 요약 + 레포 소개)
    public string Prefix(string repo)
    {
        var text = "";
        if (TakeSummary(repo) is { } summary) text += $"## 이전 작업 세션 요약 ({repo})\n{summary}\n\n";
        if (book.ResumeId(repo) == null && PipelineRoleExtensions.Split(repo) == null) text += WorkspaceRepos.Bootstrap(repo);
        return text;
    }

    public string? ResumeId(string repo) => book.ResumeId(repo);

    public IReadOnlyList<string> RouteTargets() =>
        book.Sessions.Values.Where(s => PipelineRoleExtensions.Split(s.Repo) == null).OrderByDescending(s => s.LastUsed).Select(s => s.Repo).ToList();

    public ISet<string> ExistingRepos() => book.Sessions.Keys.ToHashSet();

    /// 같은 레포의 일은 차례로. body 는 항상 UI 스레드에서 불린다 (맥 exclusive).
    public async Task Exclusive(string repo, string label, Func<Task> body)
    {
        AddQueued(repo, label);
        await queue.Run(repo, () => OnUi(async () =>
        {
            RemoveQueued(repo, label);
            SetRunning(repo, label);
            try { await body(); }
            finally { SetRunning(repo, null); }
        }));
    }

    /// 큐 작업(스레드 풀)에서 UI 스레드로 올라가 끝까지 기다린다
    Task OnUi(Func<Task> job) => dispatcher.InvokeAsync(job).Task.Unwrap();

    // MARK: 라이브 파일 (sessions-live.json)

    public static string LiveFile => Path.Combine(AppPaths.DataDir, "automation", "sessions-live.json");

    /// 지금 하는 일을 파일로 남긴다: `{"queued": {레포: [건…]}, "running": {레포: 건}, "updatedAt": "2026-11-20T01:23:45Z"}` (키 정렬, 들여쓰기).
    /// 쓰다가 막히면(파이썬이 읽는 중 등) 다음 변경 때 다시 쓴다.
    void WriteLive()
    {
        try
        {
            var runningNode = new JsonObject();
            foreach (var kv in running) runningNode[kv.Key] = kv.Value;
            var queuedNode = new JsonObject();
            foreach (var kv in queued) queuedNode[kv.Key] = new JsonArray(kv.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            var root = new JsonObject
            {
                ["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["running"] = runningNode,
                ["queued"] = queuedNode,
            };
            AppPaths.WriteAtomic(LiveFile, Json.Sorted(root)!.ToJsonString(Json.Options));
        }
        catch (Exception e) { App.Log($"작업 세션 상태 파일을 쓰지 못했어요: {e.Message}"); }
    }

    // MARK: 문맥 알림

    void CheckAlert(string repo)
    {
        var threshold = AppPaths.LoadSettings().WorkSessionAlertPercent;
        if (book.ContextAlert(repo, threshold) is { } hit && hit.ContextPercent is int percent) OnContextAlert?.Invoke(hit, percent);
    }

    public void Reload()
    {
        book = WorkSessionBook.Load();
        RefreshAllUsage();
    }

    // MARK: 이벤트 → 레포

    public static string IssueKey(EventItem item) => item.Key.Split(':')[0];

    /// 이 티켓을 이미 처리한 세션의 레포, 아니면 제목의 `· 레포 #123` 에서
    public string? KnownRepo(EventItem item)
    {
        var key = IssueKey(item);
        foreach (var kv in book.Sessions)
            if (kv.Value.Keys.Contains(key)) return kv.Key;
        var m = RepoInTitle.Match(item.Title);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string RoutePrompt(string context, string prompt) => string.Join("\n", new[]
    {
        context,
        "",
        "지금은 처리하지 말고 이 건을 어느 레포에서 처리할지만 정한다. 위 처리 지시의 확인 단계와 레포 정하는 규칙을 따르되, 쓰기·댓글·상태 변경·브랜치 생성·알림은 하지 않는다(읽기만).",
        "- 담당자가 아니거나 이미 해결됐거나 공수 기록용 반복 티켓처럼 처리할 게 없으면 skip.",
        "- Task 처럼 레포가 없는 운영 건이면 repo 를 빈 문자열로.",
        "- 레포를 하나로 정하지 못하면 repo 를 빈 문자열로 두지 말고 가장 가능성 높은 레포 폴더 이름을 쓴다(처리 단계에서 다시 확인한다).",
        "답 끝에 아래 블록 하나만 붙인다.",
        "```" + WorkRoute.Tag,
        "{\"repo\": \"레포 폴더 이름 또는 빈 문자열\", \"skip\": false}",
        "```",
    });

    static string WithKnowledge(string text, string knowledge) => text.Replace(DomainMap.Placeholder, knowledge, StringComparison.Ordinal);

    // MARK: 레포별 작업 세션

    /// 쓰기 허용 알림의 새 이벤트: 레포를 정해(이미 아는 레포면 바로) 그 레포의 작업 세션에 이어서 처리한다.
    public void Handle(Alarm alarm, EventItem item, string prompt, string title, ClaudeCommand.WriteAccess access,
                       ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        var context = $"다음 이벤트가 생겼다.\n- {item.Title}\n- {item.Summary}\n- 링크: {item.Url}\n\n요청: {prompt}";
        var key = IssueKey(item);
        if (KnownRepo(item) is { } repo)
        {
            Enqueue(repo, key, alarm, context, title, access, chat, completion);
            return;
        }
        SetRunning(RoutingSlot, key);
        var defaultKnowledge = DomainMap.Load().KnowledgeText("");
        chat.RunAutomation($"{title} · 레포 정하기", WithKnowledge(RoutePrompt(context, prompt), defaultKnowledge), access, (response, usage) =>
        {
            SetRunning(RoutingSlot, null);
            if (response is not ClaudeResponse.Success success)
            {
                completion(response, usage);
                return;
            }
            var route = WorkRoute.Extract(success.Text);
            if (route == null)
                Enqueue(WorkSessionBook.Operations, key, alarm, context, title, access, chat, completion);
            else if (route.Skipped)
                completion(new ClaudeResponse.Success($"처리할 것 없음: {key}", ""), usage);
            else
                Enqueue(route.Repo!, key, alarm, context, title, access, chat, completion);
        }, TimeSpan.FromSeconds(600), quiet: true);
    }

    void Enqueue(string repo, string key, Alarm alarm, string context, string title, ClaudeCommand.WriteAccess access,
                 ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        AddQueued(repo, key);
        _ = queue.Run(repo, () => OnUi(() => Process(repo, key, alarm, context, title, access, chat, completion)));
    }

    /// chat.RunAutomation 을 기다릴 수 있게 감싼다 (맥 runOnce)
    Task<(ClaudeResponse Response, RunUsage? Usage)> RunOnce(ChatSession chat, string title, string prompt, ClaudeCommand.WriteAccess access, TimeSpan timeout,
                                                             string? sessionId, string? newSessionId = null, bool quiet = false,
                                                             IDictionary<string, string>? extraEnvironment = null)
    {
        var source = new TaskCompletionSource<(ClaudeResponse, RunUsage?)>();
        chat.RunAutomation(title, prompt, access, (response, usage) => source.TrySetResult((response, usage)),
                           timeout, sessionId, newSessionId, quiet, extraEnvironment);
        return source.Task;
    }

    async Task Process(string repo, string key, Alarm alarm, string context, string title, ClaudeCommand.WriteAccess access,
                       ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        RemoveQueued(repo, key);
        SetRunning(repo, key);
        var sessionId = ReuseSession(repo);
        var prompt = !book.Sessions.ContainsKey(repo) ? (TakeSummary(repo) is { } summary ? $"## 이전 작업 세션 요약 ({repo})\n{summary}\n\n" : "") : Prefix(repo);
        if (sessionId != null) prompt += ResumeNote + "\n\n";
        var domains = DomainMap.Load();
        prompt += $"이번 건: {key}\n\n" + WithKnowledge(context, domains.KnowledgeText(domains.Domain(repo)));
        var (response, usage) = await RunOnce(chat, $"{title} · {repo}", prompt, access, alarm.AutomationTimeout, sessionId, quiet: false);
        if (response is ClaudeResponse.Success { SessionId.Length: > 0 } ok)
            book.Record(repo, ok.SessionId, key, usage, DateTimeOffset.Now);
        else if (usage != null && sessionId != null)
            book.Record(repo, sessionId, key, usage, DateTimeOffset.Now);
        RefreshUsage(repo);
        CheckAlert(repo);
        Save();
        RefreshMonth();
        SetRunning(repo, null);
        completion(response, usage);
    }

    /// 세션을 새로 바꾸기 전에 지금까지 익힌 것을 요약시킨다. 요약 글을 돌려주고 옛 세션은 보관한다.
    async Task<string?> Rotate(string repo, WorkSession old, ChatSession chat)
    {
        var (response, usage) = await RunOnce(chat, $"작업 세션 요약 · {repo}", SummaryPrompt, new ClaudeCommand.WriteAccess(), TimeSpan.FromSeconds(600), old.Id, quiet: true);
        if (usage != null) book.Record(repo, old.Id, null, usage, DateTimeOffset.Now);
        book.Archive(repo);
        Save();
        return response is ClaudeResponse.Success success ? success.Text : null;
    }

    /// 새 세션으로 바꾼다. carrySummary 면 옛 세션에 요약을 시켜 다음 첫 메시지에 붙인다.
    public void Reset(string repo, bool carrySummary, ChatSession chat)
    {
        if (!book.Sessions.TryGetValue(repo, out var old)) return;
        old = old.Copy();
        if (!carrySummary || old.Id.Length == 0)
        {
            book.Archive(repo);
            Save();
            return;
        }
        _ = queue.Run(repo, () => OnUi(async () =>
        {
            SetRunning(repo, "요약 중");
            var summary = await Rotate(repo, old, chat);
            if (summary != null)
            {
                pendingSummary[repo] = summary;
                Raise(nameof(PendingSummary));
            }
            SetRunning(repo, null);
        }));
    }

    public string? TakeSummary(string repo)
    {
        if (!pendingSummary.Remove(repo, out var summary)) return null;
        Raise(nameof(PendingSummary));
        return summary;
    }

    public void Archive(string repo)
    {
        book.Archive(repo);
        Save();
    }

    /// 대화로 보낸 질문의 결과를 세션 기록에 반영
    public void RecordTalk(string repo, string sessionId, RunUsage? usage)
    {
        book.Record(repo, sessionId, null, usage, DateTimeOffset.Now);
        RefreshUsage(repo);
        CheckAlert(repo);
        Save();
        RefreshMonth();
    }

    // MARK: Jira 역할별 처리

    public static string PipelineEvent(EventItem item) => $"다음 이벤트가 생겼다.\n- {item.Title}\n- {item.Summary}\n- 링크: {item.Url}";

    /// 역할별 권한 표. 권한 파일(local\pipeline-tools.json)이 없고 local 폴더가 있으면 기본값으로 만든다.
    public Dictionary<PipelineRole, ClaudeCommand.WriteAccess> PipelineTable(AppSettings settings)
    {
        var home = Paths.Home;
        var table = PipelineAccess.Defaults(PipelineAccess.Helper(settings), Path.Combine(home, "Workspace"),
                                            Path.Combine(home, ".claude", "references"), PipelineState.Dir());
        try
        {
            if (System.IO.File.Exists(PipelineAccess.File))
            {
                foreach (var kv in PipelineAccess.Parse(Paths.ReadShared(PipelineAccess.File))) table[kv.Key] = kv.Value;
            }
            else if (AppPaths.LocalReady)
            {
                AppPaths.WriteAtomic(PipelineAccess.File, PipelineAccess.Encode(table));
            }
        }
        catch (Exception e) { App.Log($"역할별 권한 파일을 다루지 못했어요: {e.Message}"); }
        return table;
    }

    static readonly string[] ReadOnlyHelperNames = { "jira-get", "jira-search", "jira-comments", "jira-links", "git-read" };

    /// 티켓 하나를 역할별로 처리한다. 이어 갈 기록이 있으면 이어서, 없으면 레포를 정한 뒤 처음부터.
    public void HandlePipeline(Alarm alarm, EventItem item, string prompt, string title, ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        var key = IssueKey(item);
        var evt = PipelineEvent(item);
        var existing = PipelineState.Load(key);
        switch (PipelineState.ArrivalOf(existing))
        {
            case PipelineState.Arrival.Resume when existing != null:
            {
                var state = existing;
                state.Resume(DateTimeOffset.Now);
                state.Event = evt;
                state.Save();
                EnqueuePipeline(state, chat, completion);
                return;
            }
            case PipelineState.Arrival.Skip skip:
                completion(new ClaudeResponse.Success(skip.Reason, ""), null);
                return;
        }
        var table = PipelineTable(AppPaths.LoadSettings());
        var pm = table.GetValueOrDefault(PipelineRole.Pm) ?? new ClaudeCommand.WriteAccess();
        var readOnly = new ClaudeCommand.WriteAccess(
            pm.Tools.Where(rule => ReadOnlyHelperNames.Any(n => rule.Contains($" {n}:", StringComparison.Ordinal))).ToList(), pm.Dirs);
        SetRunning(RoutingSlot, key);
        var context = $"{evt}\n\n요청: {prompt}";
        var defaultKnowledge = DomainMap.Load().KnowledgeText("");
        chat.RunAutomation($"{title} · 레포 정하기", WithKnowledge(RoutePrompt(context, prompt), defaultKnowledge), readOnly, (response, usage) =>
        {
            SetRunning(RoutingSlot, null);
            if (response is not ClaudeResponse.Success success)
            {
                completion(response, usage);
                return;
            }
            var route = WorkRoute.Extract(success.Text);
            if (route == null)
            {
                completion(new ClaudeResponse.Failure(new ClaudeFailure.Other($"레포를 정하지 못했어요: {key}")), usage);
                return;
            }
            if (route.Skipped)
            {
                completion(new ClaudeResponse.Success($"처리할 것 없음: {key}", ""), usage);
                return;
            }
            var repo = route.Repo!;
            var isOps = repo == WorkSessionBook.Operations;
            var domain = isOps ? repo : DomainMap.Load().Domain(repo);
            var state = new PipelineState(key, item.Title, evt, domain, isOps ? null : repo,
                                          alarm.TimeoutMinutes ?? (int)(ClaudeRunner.AutomationTimeout.TotalSeconds / 60), DateTimeOffset.Now);
            state.Save();
            EnqueuePipeline(state, chat, completion);
        }, TimeSpan.FromSeconds(600), quiet: true);
    }

    /// 앱을 켤 때 끊긴(진행 중이던) 티켓을 이어서 한다
    public void ResumePipelines(ChatSession chat, Action<string, string, bool> notify)
    {
        foreach (var state in PipelineState.LoadAll().Where(s => s.IsActive))
        {
            EnqueuePipeline(state, chat, (response, _) =>
            {
                switch (response)
                {
                    case ClaudeResponse.Success success: notify($"Jira 역할 처리 이어서 끝남 · {state.Key}", success.Text, false); break;
                    case ClaudeResponse.Failure failure: notify($"Jira 역할 처리 멈춤 · {state.Key}", failure.Reason.BackgroundMessage, true); break;
                }
            });
        }
    }

    void EnqueuePipeline(PipelineState state, ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        var domain = state.Domain;
        AddQueued(domain, state.Key);
        _ = queue.Run(domain, () => OnUi(async () =>
        {
            var final = await ProcessPipeline(state, chat);
            var cost = new RunUsage(costUSD: final.TotalCost);
            if (final.Stage == PipelineState.StageKind.Stopped)
            {
                completion(new ClaudeResponse.Failure(new ClaudeFailure.Other($"{final.Key} 멈춤: {final.Reason ?? ""}")), cost);
                return;
            }
            var roles = string.Join(" · ", PipelineRoleExtensions.AllCases
                .Where(r => final.Costs.ContainsKey(r.Raw()))
                .Select(r => $"{r.Label()} {TokenFormat.Usd(final.Costs[r.Raw()])}"));
            var summary = PipelineNext.Strip(final.Summary);
            completion(new ClaudeResponse.Success($"{final.StatusText}\n{summary}\n\n비용: {roles}", ""), cost);
        }));
    }

    async Task<PipelineState> ProcessPipeline(PipelineState start, ChatSession chat)
    {
        var domain = start.Domain;
        RemoveQueued(domain, start.Key);
        SetRunning(domain, start.StatusText);
        SetPipeline(domain, start);
        var settings = AppPaths.LoadSettings();
        var config = PipelineConfig.Load(Rules.PromptsDir, PipelineTable(settings), PipelineState.Dir(), PipelineAccess.Helper(settings),
                                         Rules.Folder(Rules.Kind.Prompt, Rules.Layer.Shared));
        config.Domains = DomainMap.Load();
        config.Notes = DomainNotes.Load();
        var timeout = TimeSpan.FromMinutes(Math.Max(1, start.TimeoutMinutes));
        var io = new PipelineIO(
            runRole: (role, prompt, sessionId, newSessionId, access) =>
                RunRole(chat, $"{start.Key} · {domain}:{role.Raw()}", prompt, access, timeout, sessionId, newSessionId),
            helper: args => RunHelper(args, settings),
            diff: worktree => WorktreeDiff(worktree, settings),
            recordSession: (sessionKey, id, ticket, usage) =>
            {
                RecordRoleSession(sessionKey, id, ticket, usage);
                return Task.CompletedTask;
            },
            save: state =>
            {
                state.Save();
                ShowPipeline(state);
                return Task.CompletedTask;
            },
            notice: (noticeTitle, body) =>
            {
                OnPipelineNotice?.Invoke(noticeTitle, body);
                return Task.CompletedTask;
            },
            policy: () => new SessionReuse(AppPaths.LoadSettings()),
            sessionChosen: (sessionKey, ticket, resumeId, reason) =>
            {
                ChooseRoleSession(sessionKey, ticket, resumeId, reason);
                return Task.CompletedTask;
            });
        var final = await JiraPipeline.Run(start, io, config);
        SetRunning(domain, null);
        SetPipeline(domain, final);
        RefreshMonth();
        return final;
    }

    /// 역할 세션을 고른 결과를 작업 탭 기록(`{도메인}:{역할}`)에 맞춘다. 이어 쓰면 그대로, 새로 시작하면 앞 세션을 보관하고 이유(새 티켓·식음·문맥)를 남긴다.
    void ChooseRoleSession(string sessionKey, string ticket, string? resumeId, string? reason)
    {
        if (resumeId != null) return;
        if (book.StartFresh(sessionKey, ticket, reason, DateTimeOffset.Now)) Save();
    }

    void RecordRoleSession(string sessionKey, string id, string ticket, RunUsage? usage)
    {
        book.Record(sessionKey, id, ticket, usage, DateTimeOffset.Now);
        RefreshUsage(sessionKey);
        CheckAlert(sessionKey);
        Save();
    }

    void ShowPipeline(PipelineState state)
    {
        SetPipeline(state.Domain, state);
        SetRunning(state.Domain, state.StatusText);
    }

    async Task<PipelineRoleResult> RunRole(ChatSession chat, string title, string prompt, ClaudeCommand.WriteAccess access, TimeSpan timeout,
                                           string? sessionId, string? newSessionId)
    {
        var (response, usage) = await RunOnce(chat, title, prompt, access, timeout, sessionId, newSessionId, quiet: true);
        if (Quitting) return new PipelineRoleResult(Usage: usage, Interrupted: true);
        return response switch
        {
            ClaudeResponse.Success success => new PipelineRoleResult(Text: success.Text, SessionId: success.SessionId.Length == 0 ? null : success.SessionId, Usage: usage),
            ClaudeResponse.Failure failure => new PipelineRoleResult(SessionId: null, Usage: usage, Failure: failure.Reason.BackgroundMessage),
            _ => new PipelineRoleResult(Usage: usage, Failure: "알 수 없는 응답"),
        };
    }

    /// `uv run pokemate auto …` 를 실행한다 (Jira·브랜치·push 같은 PokeMate 가 직접 하는 일)
    static async Task<(bool Ok, string Output)> RunHelper(IReadOnlyList<string> args, AppSettings settings)
    {
        var baseArgs = new List<string> { "--directory", Path.Combine(settings.RepoDir, "mcp-server"), "run", "--quiet", "pokemate", "auto" };
        baseArgs.AddRange(args);
        try
        {
            var output = await new ProcessRunner().Run(settings.UvPath, baseArgs, environment: AppPaths.ProcessEnvironment(settings), timeout: TimeSpan.FromSeconds(1500));
            var text = (output.Stdout + "\n" + output.Stderr).Trim();
            return (output.ExitCode == 0 && !output.TimedOut, Suffix(text, 3000));
        }
        catch { return (false, "helper 실행 실패"); }
    }

    static string Suffix(string text, int count)
    {
        if (text.Length <= count) return text;
        var start = text.Length - count;
        if (char.IsLowSurrogate(text[start])) start++;
        return text[start..];
    }

    /// 작업 폴더의 변경(diff). 새 파일도 보이게 `add -A -N` 한 뒤 `diff HEAD`. 실패하면 null
    static async Task<string?> WorktreeDiff(string worktree, AppSettings settings)
    {
        try
        {
            var runner = new ProcessRunner();
            var environment = AppPaths.ProcessEnvironment(settings);
            var add = await runner.Run("git", new[] { "-C", worktree, "add", "-A", "-N" }, environment: environment, timeout: TimeSpan.FromSeconds(120));
            if (add.ExitCode != 0) return null;
            var diff = await runner.Run("git", new[] { "-C", worktree, "diff", "HEAD" }, environment: environment, timeout: TimeSpan.FromSeconds(120));
            return diff.ExitCode == 0 ? diff.Stdout : null;
        }
        catch { return null; }
    }

    // MARK: 도메인 리뷰어

    /// GitHub PR 리뷰 요청: 도메인을 정해(이미 아는 PR 레포면 바로) 그 도메인의 리뷰어 세션으로 리뷰한다. PR 이 아니면 레포별 작업 세션으로.
    public void HandleReview(Alarm alarm, EventItem item, string prompt, string title, ClaudeCommand.WriteAccess access,
                             ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        if (ReviewTarget.Parse(item.Url) is not { } target)
        {
            Handle(alarm, item, prompt, title, access, chat, completion);
            return;
        }
        var context = $"{PipelineEvent(item)}\n\n요청: {prompt}";
        var map = DomainMap.Load();
        if (DomainReview.Known(target, map, ReviewDomainBook.Load()) is { } hit)
        {
            EnqueueReview(target, hit.Domain, hit.Why, hit.Source, context, alarm, title, access, chat, completion);
            return;
        }
        var helper = PipelineAccess.Helper(AppPaths.LoadSettings());
        var readOnly = new ClaudeCommand.WriteAccess(new[] { $"Bash({helper} gh-get:*)", $"Bash({helper} jira-get:*)" }, Array.Empty<string>());
        SetRunning(RoutingSlot, target.Label);
        chat.RunAutomation($"{title} · 도메인 정하기", DomainReview.ChoosePrompt(target, map, helper), readOnly, (response, _) =>
        {
            SetRunning(RoutingSlot, null);
            var domain = DomainReview.Fallback;
            var why = $"도메인을 정하지 못해 {DomainReview.Fallback} 로 리뷰";
            if (response is ClaudeResponse.Success success && DomainReview.Extract(success.Text, map) is { } choice)
            {
                domain = choice.Domain;
                why = choice.Why;
                var book = ReviewDomainBook.Load();
                book.Repos[target.FullName] = new ReviewDomainChoice(choice.Domain, choice.Why, DateTimeOffset.Now);
                book.Save();
            }
            EnqueueReview(target, domain, why, DomainReview.Source.Chosen, context, alarm, title, access, chat, completion);
        }, TimeSpan.FromSeconds(600), quiet: true);
    }

    void EnqueueReview(ReviewTarget target, string domain, string why, DomainReview.Source source, string context, Alarm alarm, string title,
                       ClaudeCommand.WriteAccess access, ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        AddQueued(domain, $"리뷰 {target.Label}");
        _ = queue.Run(domain, () => OnUi(() => ProcessReview(target, domain, why, source, context, alarm, title, access, chat, completion)));
    }

    async Task ProcessReview(ReviewTarget target, string domain, string why, DomainReview.Source source, string context, Alarm alarm, string title,
                             ClaudeCommand.WriteAccess access, ChatSession chat, Action<ClaudeResponse, RunUsage?> completion)
    {
        var label = $"리뷰 {target.Label}";
        RemoveQueued(domain, label);
        SetRunning(domain, label);
        SetReviewing(domain, $"{label} · {why}");
        var key = DomainReview.SessionKey(domain);
        var sessionId = ReuseSession(key);
        var map = DomainMap.Load();
        var notes = DomainNotes.Load();
        var dirs = access.Dirs.ToList();
        dirs.AddRange(map.KnowledgePaths(domain).Append(notes.Root).Where(d => !dirs.Contains(d)).ToList());
        var runAccess = new ClaudeCommand.WriteAccess(access.Tools, dirs);
        var helper = PipelineAccess.Helper(AppPaths.LoadSettings());
        var prompt = DomainReview.ReviewPrompt(target, domain, why, source, sessionId == null, notes, helper,
                                               WithKnowledge(context, map.KnowledgeText(domain)).Replace("{{helper}}", helper, StringComparison.Ordinal));
        var (response, usage) = await RunOnce(chat, $"{title} · {key}", prompt, runAccess, alarm.AutomationTimeout, sessionId, quiet: false,
                                              extraEnvironment: new Dictionary<string, string> { ["POKEMATE_REVIEW_REPO"] = target.FullName });
        if (response is ClaudeResponse.Success { SessionId.Length: > 0 } ok)
            book.Record(key, ok.SessionId, target.Label, usage, DateTimeOffset.Now);
        else if (usage != null && sessionId != null)
            book.Record(key, sessionId, target.Label, usage, DateTimeOffset.Now);
        RefreshUsage(key);
        CheckAlert(key);
        Save();
        RefreshMonth();
        SetRunning(domain, null);
        SetReviewing(domain, null);
        completion(response, usage);
    }
}

/// 앱이 켜질 때 작업 세션 센터를 연결하는 부품. 알림 센터·트리거 감시(Order -20·-10)가 먼저 켜진 뒤에 시작한다.
public sealed class WorkSessionService : IAppService
{
    public int Order => 10;

    public void Start(AppModel model)
    {
        WorkSessionCenter.Shared.Start(model);
        WorkSessionTestHook.Start(WorkSessionCenter.Shared, model);
    }
}

/// 시험용 입구: 환경 변수 POKEMATE_TEST_EVENTS 가 폴더를 가리킬 때만 켜진다. 그 폴더에 `이름.event.json` 을 놓으면
/// 트리거 알림이 새 이벤트를 알린 것처럼 작업 세션 처리를 시작하고, 끝나면 `이름.result.json` 을 남긴다 (실제 Jira·GitHub 없이 흐름을 확인하려고).
///   {"mode": "work|pipeline|review", "key": "ACME-201", "title": "…", "summary": "…", "url": "…", "prompt": "…", "timeoutMinutes": 5}
static class WorkSessionTestHook
{
    public static void Start(WorkSessionCenter center, AppModel model)
    {
        var folder = Environment.GetEnvironmentVariable("POKEMATE_TEST_EVENTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        try { Directory.CreateDirectory(folder); } catch { return; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            string[] files;
            try { files = Directory.GetFiles(folder, "*.event.json"); } catch { return; }
            foreach (var file in files) Take(center, model, file);
        };
        timer.Start();
    }

    static void Take(WorkSessionCenter center, AppModel model, string file)
    {
        System.Text.Json.Nodes.JsonObject? spec;
        try
        {
            spec = Json.ParseObject(File.ReadAllText(file));
            File.Move(file, file.Replace(".event.json", ".taken"), overwrite: true);
        }
        catch { return; }
        if (spec == null) return;
        var mode = Json.Str(spec["mode"]) ?? "work";
        var item = new EventItem(Json.Str(spec["key"]) ?? "TEST-1", Json.Str(spec["title"]) ?? "시험 이벤트", Json.Str(spec["summary"]) ?? "", Json.Str(spec["url"]) ?? "");
        var prompt = Json.Str(spec["prompt"]) ?? "시험 요청";
        var alarm = new Alarm
        {
            Title = "시험 알림", Trigger = new PokeMate.Core.Trigger("jira", "jira.assigned_to_me"), AllowWrite = true, UseWorkSessions = true,
            UseRolePipeline = mode == "pipeline", TimeoutMinutes = (int?)Json.Long(spec["timeoutMinutes"]),
        };
        var title = $"{alarm.Title} · {item.Title}";
        var access = ClaudeCommand.LoadWriteAccess();
        var result = file.Replace(".event.json", ".result.json");
        void Done(ClaudeResponse response, RunUsage? usage)
        {
            var node = new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = response is ClaudeResponse.Success,
                ["text"] = response is ClaudeResponse.Success s ? s.Text : ((ClaudeResponse.Failure)response).Reason.Message,
                ["cost"] = usage?.CostUSD ?? 0,
            };
            try { File.WriteAllText(result, node.ToJsonString(Json.Options)); } catch { }
            App.Log($"시험 이벤트 {item.Key} ({mode}) 끝: {node["ok"]}");
        }
        switch (mode)
        {
            case "pipeline": center.HandlePipeline(alarm, item, prompt, title, model.Chat, Done); break;
            case "review": center.HandleReview(alarm, item, prompt, title, access, model.Chat, Done); break;
            default: center.Handle(alarm, item, prompt, title, access, model.Chat, Done); break;
        }
    }
}
