using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PokeMate;

/// 작업 탭 (맥 WorkTabView): 레포별 작업 세션 목록 + Jira 역할별 처리(도메인) 묶음, 누르면 아래에 그 세션의 기록과 이어 말하기.
/// 코드로 짠 화면이다. 센터가 바뀔 때마다 목록을 다시 그리되, 열려 있는 상세(대화)는 그 세션이 바뀔 때만 새로 만든다.
[PopoverTab(SidebarItem.Work)]
public sealed class WorkSessionsView : UserControl
{
    readonly AppModel model;
    readonly WorkSessionCenter center = WorkSessionCenter.Shared;
    readonly Grid root = new();
    readonly Border headerHost = new();
    readonly Border listHost = new();
    readonly Border detailHost = new();
    readonly Ui.Debounce redraw;
    string? selected;
    WorkSessionDetail? detail;

    public WorkSessionsView(AppModel model)
    {
        this.model = model;
        redraw = new Ui.Debounce(Rebuild, 60);
        Content = root;
        root.Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 0);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        headerHost.Margin = new Thickness(0, 0, 0, 10);
        listHost.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(headerHost, 0);
        Grid.SetRow(listHost, 1);
        Grid.SetRow(detailHost, 2);
        root.Children.Add(headerHost);
        root.Children.Add(listHost);
        root.Children.Add(detailHost);
        Loaded += (_, _) =>
        {
            center.PropertyChanged += OnCenterChanged;
            Rebuild();
            TakeReset();
        };
        Unloaded += (_, _) => { center.PropertyChanged -= OnCenterChanged; redraw.Cancel(); };
        Rebuild();
    }

    void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkSessionCenter.RequestReset)) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TakeReset));
        else redraw.Poke();
    }

    // MARK: 새 세션으로 바꾸기

    void TakeReset()
    {
        if (center.RequestReset is not { } repo) return;
        center.RequestReset = null;
        if (center.Book.Sessions.TryGetValue(repo, out var session))
        {
            Select(repo);
            AskReset(session);
        }
    }

    void AskReset(WorkSession session)
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text("새 세션으로 바꿀까요?", 15, "TextFg", FontWeights.SemiBold));
        var text = Ui.Text($"{session.Repo} 세션은 보관되고, 다음 메시지·티켓부터 새 세션으로 처리해요.", 13, "TextSecondary", wrap: true);
        text.Margin = new Thickness(0, 8, 0, 16);
        body.Children.Add(text);
        var window = EditorStyle.Dialog("새 세션으로 바꿀까요?", 400, body);
        bool? carry = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = ChatUi.Id(Ui.Pill("취소", () => window.DialogResult = false), "ResetCancel");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var plain = ChatUi.Id(Ui.Pill("요약 없이 새로", () => { carry = false; window.DialogResult = true; }), "ResetPlain");
        plain.Margin = new Thickness(0, 0, 8, 0);
        var summary = ChatUi.Id(ChatUi.Primary("요약을 넘겨서 새로", () => { carry = true; window.DialogResult = true; }), "ResetSummary");
        buttons.Children.Add(cancel);
        buttons.Children.Add(plain);
        buttons.Children.Add(summary);
        body.Children.Add(buttons);
        EditorStyle.ShowModal(window);
        if (carry is bool carrySummary)
        {
            if (selected == session.Repo && !carrySummary) Select(null);
            center.Reset(session.Repo, carrySummary, model.Chat);
        }
    }

    // MARK: 다시 그리기

    void Select(string? repo)
    {
        selected = repo;
        Rebuild();
    }

    void Rebuild()
    {
        var sessions = center.Book.Sessions.Values.OrderByDescending(s => s.LastUsed).ToList();
        if (selected != null && !center.Book.Sessions.ContainsKey(selected)) selected = null;

        headerHost.Child = Header(sessions);
        if (sessions.Count == 0)
        {
            var empty = Ui.Text("레포별로 지식이 쌓이는 Claude 세션이에요. 채팅에서 보낼 곳을 고르거나 자동 처리 알람이 돌면 생겨요.", 13, "TextSecondary", wrap: true);
            listHost.Child = Ui.Card(empty);
            SetRows(listFills: false);
            SetDetail(null);
            return;
        }

        var list = new StackPanel();
        var legacy = sessions.Where(s => PipelineRoleExtensions.Split(s.Repo) == null).ToList();
        for (var i = 0; i < legacy.Count; i++)
        {
            if (i > 0) list.Children.Add(EditorStyle.Divider(2));
            list.Children.Add(Row(legacy[i]));
        }
        var domains = Domains(sessions);
        for (var i = 0; i < domains.Count; i++)
        {
            if (legacy.Count > 0 || i > 0) list.Children.Add(EditorStyle.Divider(2));
            list.Children.Add(DomainBlock(domains[i]));
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = selected == null ? double.PositiveInfinity : 220 };
        listHost.Child = Ui.Card(scroll);
        // 선택이 없으면 목록이 남는 높이를 다 쓰고, 있으면 목록은 최대 220 으로 줄이고 아래를 상세가 쓴다
        SetRows(listFills: selected == null);
        SetDetail(selected);
    }

    void SetRows(bool listFills)
    {
        root.RowDefinitions[1].Height = listFills ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        root.RowDefinitions[2].Height = listFills ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    void SetDetail(string? repo)
    {
        if (repo == null)
        {
            detail?.Detach();
            detail = null;
            detailHost.Child = null;
            return;
        }
        if (detail == null || detail.Repo != repo)
        {
            detail?.Detach();
            detail = new WorkSessionDetail(repo, model, center, () => Select(null));
            detailHost.Child = Ui.Card(detail);
        }
    }

    UIElement Header(List<WorkSession> sessions)
    {
        var runningCount = center.Running.Keys.Count(k => k != WorkSessionCenter.RoutingSlot);
        var column = new StackPanel();

        var top = new DockPanel();
        var refresh = EditorStyle.GlyphButton("", () => center.Reload(), "새로고침", id: "WorkRefresh");
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        var add = Ui.Pill("새 작업 세션", () => { });
        add.Content = ChatUi.Label("", "새 작업 세션", "TextFg", 12);
        add.ToolTip = "~/Workspace 아래 레포를 골라 빈 작업 세션을 만들어요";
        ChatUi.Id(add, "WorkNew");
        add.Click += (_, _) => ShowNewMenu(add);
        add.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        var title = ChatUi.Label("", "작업 세션", "TextFg", 15, FontWeights.SemiBold, glyphBrush: "OrangeBrush");
        top.Children.Add(title);
        column.Children.Add(top);

        var summary = Ui.Text($"작업 세션 {sessions.Count}개 · 실행 중 {runningCount} · 이번 달 {TokenFormat.Compact(center.MonthUsage.Total)} · {TokenFormat.Usd(center.MonthUsage.CostUSD)}",
                              11, "TextSecondary");
        summary.Margin = new Thickness(0, 6, 0, 0);
        ChatUi.Id(summary, "WorkSummary");
        column.Children.Add(summary);
        if (center.Running.TryGetValue(WorkSessionCenter.RoutingSlot, out var routing))
        {
            var line = Ui.Text($"레포 정하는 중 · {routing}", 10, "TextSecondary");
            line.Margin = new Thickness(0, 2, 0, 0);
            column.Children.Add(line);
        }
        return Ui.Card(column);
    }

    void ShowNewMenu(FrameworkElement anchor)
    {
        var menu = ChatUi.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        foreach (var item in NewWorkSessionMenu.Candidates(repo =>
                 {
                     center.Create(repo);
                     Select(repo);
                 }, center.Book.Sessions.Keys.ToHashSet()))
            menu.Items.Add(item);
        menu.IsOpen = true;
    }

    // MARK: 도메인 묶음 (Jira 역할별 처리)

    /// 역할 세션·역할별 처리·리뷰가 있는 도메인 이름들 (이름순)
    public static List<string> Domains(IEnumerable<WorkSession> sessions)
    {
        var center = WorkSessionCenter.Shared;
        return sessions.Select(s => PipelineRoleExtensions.Split(s.Repo)?.Domain).Where(d => d != null).Select(d => d!)
            .Concat(center.Pipelines.Keys).Concat(center.Reviewing.Keys)
            .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    UIElement DomainBlock(string domain)
    {
        center.Pipelines.TryGetValue(domain, out var state);
        var block = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var head = new WrapPanel { Margin = new Thickness(6, 0, 6, 0) };
        head.Children.Add(ChatUi.Label("", domain, "TextFg", 13, FontWeights.SemiBold, glyphBrush: "OrangeBrush"));
        if (state != null)
        {
            head.Children.Add(Pill(state.StatusText, state.IsActive ? "OrangeBrush" : "TextSecondary"));
            if (state.TotalCost > 0)
            {
                var cost = Ui.Text(TokenFormat.Usd(state.TotalCost), 10, "TextSecondary");
                cost.Margin = new Thickness(6, 0, 0, 0);
                cost.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(cost);
            }
        }
        if (center.Reviewing.TryGetValue(domain, out var review))
        {
            var pill = Pill(review, "OrangeBrush");
            pill.ToolTip = review;
            head.Children.Add(pill);
        }
        if (center.Queued.TryGetValue(domain, out var waiting) && waiting.Count > 0)
        {
            var count = Ui.Text($"대기 {waiting.Count}", 10, "TextSecondary");
            count.Margin = new Thickness(6, 0, 0, 0);
            count.VerticalAlignment = VerticalAlignment.Center;
            count.ToolTip = string.Join("\n", waiting);
            head.Children.Add(count);
        }
        block.Children.Add(head);
        foreach (var role in PipelineRoleExtensions.AllCases)
        {
            var key = PipelineRoleExtensions.SessionKey(domain, role);
            if (center.Book.Sessions.TryGetValue(key, out var session))
            {
                var active = (state?.IsActive == true && state.Role == role) || (role == PipelineRole.Reviewer && center.Reviewing.ContainsKey(domain));
                var row = Row(session, role.Label(), active);
                row.Margin = new Thickness(12, 0, 0, 0);
                block.Children.Add(row);
            }
            else if (center.Book.LastArchived(key) is { } last)
            {
                // 끝나 보관한 건(티켓 끝·PR 승인)의 세션: 마지막 건과 새로 시작했던 이유만 보인다
                var why = last.FreshReason is { } reason ? $" · 새로 시작: {reason}" : "";
                var ended = Ui.Text($"{role.Label()} · 끝난 건 {last.Keys.LastOrDefault() ?? "-"} · 마지막 {last.LastUsed.ToLocalTime().ToString("M'/'d HH:mm", CultureInfo.InvariantCulture)}{why}",
                                    11, "TextSecondary");
                ended.Margin = new Thickness(18, 2, 0, 2);
                ended.TextTrimming = TextTrimming.CharacterEllipsis;
                block.Children.Add(ended);
            }
            else
            {
                var none = Ui.Text($"{role.Label()} · 아직 없음", 11, "TextSecondary");
                none.Margin = new Thickness(18, 2, 0, 2);
                block.Children.Add(none);
            }
        }
        return block;
    }

    static FrameworkElement Pill(string text, string brush)
    {
        var label = Ui.Text(text, 10, brush, FontWeights.Bold);
        var border = new Border { Child = label, CornerRadius = new CornerRadius(20), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        border.Background = Ui.Alpha(((SolidColorBrush)Theme.Brush(brush)).Color, 0.15);
        return border;
    }

    // MARK: 세션 한 줄

    FrameworkElement Row(WorkSession session, string? name = null, bool active = false)
    {
        var isSelected = selected == session.Repo;
        var box = new Button
        {
            Template = FlatTemplate(), Padding = new Thickness(6, 4, 6, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = null,
        };
        if (isSelected) box.Background = Ui.Alpha((Color)ColorConverter.ConvertFromString("#FFFF9500"), 0.10);
        box.ToolTip = "눌러서 기록 보기·말 걸기";
        ChatUi.Id(box, "WorkRow:" + session.Repo);
        var column = new StackPanel();

        var head = new DockPanel();
        var more = EditorStyle.GlyphButton("", () => { }, "더 보기", id: "WorkMore:" + session.Repo);
        more.Click += (_, _) => ShowRowMenu(more, session);
        DockPanel.SetDock(more, Dock.Right);
        head.Children.Add(more);
        var tags = new StackPanel { Orientation = Orientation.Horizontal };
        tags.Children.Add(Ui.Text(name ?? session.Repo, 13, "TextFg", isSelected ? FontWeights.SemiBold : FontWeights.Normal));
        if (active) tags.Children.Add(Pill("실행 중", "OrangeBrush"));
        if (center.Running.TryGetValue(session.Repo, out var runKey)) tags.Children.Add(Pill($"실행 중 · {runKey}", "OrangeBrush"));
        if (center.Queued.TryGetValue(session.Repo, out var waiting) && waiting.Count > 0)
        {
            var count = Ui.Text($"대기 {waiting.Count}", 10, "TextSecondary");
            count.Margin = new Thickness(6, 0, 0, 0);
            count.VerticalAlignment = VerticalAlignment.Center;
            tags.Children.Add(count);
        }
        head.Children.Add(tags);
        column.Children.Add(head);
        column.Children.Add(Caption(session));
        box.Content = column;
        box.Click += (_, _) => Select(isSelected ? null : session.Repo);
        return box;
    }

    /// 누르는 모양이 없는 평평한 버튼 틀 (한 줄 전체가 눌리는 칸)
    static ControlTemplate FlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    void ShowRowMenu(FrameworkElement anchor, WorkSession session)
    {
        var menu = ChatUi.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Items.Add(ChatUi.Item("새 세션으로 바꾸기…", () => AskReset(session)));
        menu.Items.Add(ChatUi.Item("보관", () =>
        {
            if (selected == session.Repo) selected = null;
            center.Archive(session.Repo);
        }));
        menu.IsOpen = true;
    }

    /// 마지막 사용·티켓 수·문맥 %·사용량 한 줄 (맥 captionView)
    TextBlock Caption(WorkSession s)
    {
        var threshold = model.Settings.WorkSessionAlertPercent;
        var block = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        void Add(string text, string brush = "TextSecondary")
        {
            var run = new System.Windows.Documents.Run(text);
            run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brush);
            block.Inlines.Add(run);
        }
        if (s.Id.Length == 0)
        {
            Add(s.FreshReason is { } why ? $"새로 시작: {why} · 다음 실행에서 노트·지식부터 읽어요" : "새로 만듦 · 첫 메시지에서 레포 지식을 모아요");
            return block;
        }
        Add($"마지막 {s.LastUsed.ToLocalTime().ToString("M'/'d HH:mm", CultureInfo.InvariantCulture)} · 티켓 {s.Keys.Count}");
        if (s.FreshReason is { } reason) Add($" · 새로 시작: {reason}");
        if (s.ContextPercent is int p)
        {
            Add(" · ");
            Add($"문맥 {p}%", threshold > 0 && p >= threshold ? "OrangeBrush" : "TextSecondary");
        }
        Add($" · {s.Usage.ShortText}");
        return block;
    }
}

/// 세션 하나의 기록 보기와 이어 말하기 (맥 WorkSessionDetail). 대화는 이 화면 전용 ChatSession(저장 안 함, 그 레포로 고정).
public sealed class WorkSessionDetail : Grid
{
    public string Repo { get; }
    readonly AppModel model;
    readonly WorkSessionCenter center;
    readonly Action close;
    readonly ChatSession chat;
    readonly StackPanel entriesPanel = new();
    readonly StackPanel busyHost = new();
    readonly StackPanel permissionHost = new();
    readonly StackPanel errorHost = new();
    readonly StackPanel keysHost = new();
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    TextBox input = new();
    readonly Button send;
    readonly Ui.Debounce refresh;
    string? loadedId;
    bool attached;

    public WorkSessionDetail(string repo, AppModel model, WorkSessionCenter center, Action close)
    {
        Repo = repo;
        this.model = model;
        this.center = center;
        this.close = close;
        chat = new ChatSession(() => model.ChatSettings(), storage: null, sessionId: null, workRepo: repo);
        ChatPermissionAlerts.Watch(chat);
        refresh = new Ui.Debounce(Redraw, 40);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel();
        var closeButton = EditorStyle.GlyphButton("", close, "닫기", id: "WorkDetailClose");
        DockPanel.SetDock(closeButton, Dock.Right);
        head.Children.Add(closeButton);
        var reload = EditorStyle.GlyphButton("", () => Load(), "기록 다시 읽기", id: "WorkDetailReload");
        DockPanel.SetDock(reload, Dock.Right);
        head.Children.Add(reload);
        head.Children.Add(Ui.Text($"{repo} 기록", 13, "TextFg", FontWeights.SemiBold));
        Children.Add(head);

        keysHost.Margin = new Thickness(0, 6, 0, 0);
        SetRow(keysHost, 1);
        Children.Add(keysHost);

        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        body.Children.Add(entriesPanel);
        body.Children.Add(busyHost);
        body.Children.Add(permissionHost);
        body.Children.Add(errorHost);
        scroll.Content = body;
        scroll.Margin = new Thickness(0, 6, 0, 0);
        SetRow(scroll, 2);
        Children.Add(scroll);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (frame, box) = EditorStyle.TextField("", _ => RefreshSend(), "이 세션에 이어서 보내기", multiline: true, id: "WorkDetailInput", minLines: 1, maxLines: 4);
        input = box;
        box.PreviewKeyDown += OnInputKey;
        send = ChatUi.Id(ChatUi.Primary("보내기", Send, enabled: false), "WorkDetailSend");
        send.Margin = new Thickness(8, 0, 0, 0);
        send.VerticalAlignment = VerticalAlignment.Bottom;
        SetColumn(frame, 0);
        SetColumn(send, 1);
        row.Children.Add(frame);
        row.Children.Add(send);
        SetRow(row, 3);
        Children.Add(row);

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        Load();
        Redraw();
    }

    // 맥은 화면이 사라질 때 구독이 풀린다. 여기는 탭 전환(Unloaded)과 상세를 닫을 때(Detach) 푼다.
    void Attach()
    {
        if (attached) return;
        attached = true;
        chat.PropertyChanged += OnChatChanged;
        center.PropertyChanged += OnCenterChanged;
        Load();
        Redraw();
    }

    public void Detach()
    {
        if (!attached) return;
        attached = false;
        chat.PropertyChanged -= OnChatChanged;
        center.PropertyChanged -= OnCenterChanged;
        refresh.Cancel();
    }

    bool wasBusy;

    void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSession.IsBusy) && wasBusy && !chat.IsBusy) Load();
        if (e.PropertyName == nameof(ChatSession.IsBusy)) wasBusy = chat.IsBusy;
        refresh.Poke();
    }

    void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkSessionCenter.Book))
        {
            var id = center.Book.Sessions.GetValueOrDefault(Repo)?.Id;
            if (id != loadedId) Load();
            else refresh.Poke();
        }
    }

    /// 기록 파일을 다시 읽는다 (세션 id 가 바뀌거나 대화가 끝났을 때)
    void Load()
    {
        var id = center.Book.Sessions.GetValueOrDefault(Repo)?.Id;
        loadedId = id;
        entries = new List<TranscriptEntry>();
        if (!string.IsNullOrEmpty(id) && WorkSessionCenter.TranscriptFile(id) is { } file)
        {
            try { entries = SessionTranscript.Parse(Paths.ReadShared(file)); } catch { }
        }
        Redraw();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => scroll.ScrollToBottom()));
    }

    List<TranscriptEntry> entries = new();

    void Redraw()
    {
        var session = center.Book.Sessions.GetValueOrDefault(Repo);
        keysHost.Children.Clear();
        if (session != null && session.Keys.Count > 0)
        {
            var keys = Ui.Text($"처리한 티켓: {string.Join(", ", session.Keys)}", 11, "TextSecondary", wrap: true, maxLines: 2);
            keysHost.Children.Add(keys);
        }

        entriesPanel.Children.Clear();
        if (entries.Count == 0)
            entriesPanel.Children.Add(Ui.Text(session?.Id.Length == 0 ? "아직 대화가 없어요. 첫 메시지를 보내면 레포 지식부터 모아요." : "기록 파일을 찾지 못했어요.", 11, "TextSecondary", wrap: true));
        foreach (var e in entries) entriesPanel.Children.Add(EntryView(e));

        busyHost.Children.Clear();
        if (chat.IsBusy && chat.Messages.LastOrDefault(m => m.Role == ChatMessage.RoleKind.User) is { } last)
        {
            var sent = Ui.Text("보낸 말 · 지금", 10, "OrangeBrush", FontWeights.SemiBold);
            sent.Margin = new Thickness(0, 8, 0, 0);
            busyHost.Children.Add(sent);
            busyHost.Children.Add(Ui.Text(last.Text, 13, "TextFg", wrap: true));
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            line.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 28, Height = 3, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var activity = Ui.Text(chat.Activity ?? "생각 중…", 11, "TextSecondary");
            activity.MaxWidth = 280;
            line.Children.Add(activity);
            var cancel = ChatUi.Id(ChatUi.Link("취소", () => chat.Cancel()), "WorkDetailCancel");
            cancel.Margin = new Thickness(8, 0, 0, 0);
            line.Children.Add(cancel);
            busyHost.Children.Add(line);
        }

        permissionHost.Children.Clear();
        if (chat.PendingPermission is { } request)
        {
            var card = ChatView.PermissionCard(chat, request);
            card.Margin = new Thickness(0, 8, 0, 0);
            permissionHost.Children.Add(card);
        }

        errorHost.Children.Clear();
        if (!chat.IsBusy && chat.Messages.LastOrDefault() is { Role: ChatMessage.RoleKind.Error } error)
        {
            var text = Ui.Text(error.Text, 11, "OrangeBrush", wrap: true);
            text.Foreground = Brushes.IndianRed;
            text.Margin = new Thickness(0, 8, 0, 0);
            errorHost.Children.Add(text);
        }
        RefreshSend();
    }

    UIElement EntryView(TranscriptEntry e)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var (label, brush) = e.Kind switch
        {
            TranscriptEntry.EntryKind.User => ("보낸 말", "OrangeBrush"),
            TranscriptEntry.EntryKind.Assistant => ("Claude", "TextFg"),
            TranscriptEntry.EntryKind.Tool => ("도구", "TextSecondary"),
            _ => ("도구 결과", "TextSecondary"),
        };
        panel.Children.Add(Ui.Text($"{label} · {e.Time}", 10, brush, FontWeights.SemiBold));
        if (e.Kind == TranscriptEntry.EntryKind.Assistant)
        {
            panel.Children.Add(new MarkdownContent(e.Text, 13));
        }
        else
        {
            var mono = e.Kind is TranscriptEntry.EntryKind.Tool or TranscriptEntry.EntryKind.Result;
            var text = Ui.Text(e.Text, mono ? 11 : 13, e.Kind == TranscriptEntry.EntryKind.Result ? "TextSecondary" : "TextFg", wrap: true);
            text.TextTrimming = TextTrimming.None;
            if (mono) text.FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic");
            panel.Children.Add(text);
        }
        return panel;
    }

    void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    void RefreshSend() => send.IsEnabled = input.Text.Trim().Length > 0 && !chat.IsBusy;

    void Send()
    {
        var text = input.Text.Trim();
        if (text.Length == 0 || chat.IsBusy) return;
        input.Clear();
        chat.Send(text);
    }
}
