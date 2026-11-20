using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PokeMate;

/// 홈 탭의 대화 화면 (맥 ChatView). 메시지 목록 + 카드들, 입력칸, 보낼 곳·모델·추론 수준 고르기.
/// 코드로 짠 화면이다 (카드가 메시지에 따라 달라서). 카드 모양은 ChatView.Cards.cs.
[HomeChat]
public sealed partial class ChatView : Grid
{
    readonly AppModel model;
    readonly ChatSession chat;

    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel head = new();           // 처음 사용 카드 / 빈 대화 안내
    readonly StackPanel messagesPanel = new();
    readonly Dictionary<Guid, StackPanel> blocks = new();
    readonly List<Guid> shown = new();
    readonly StackPanel busyHost = new();
    readonly StackPanel permissionHost = new();
    readonly StackPanel loginHost = new();
    readonly StackPanel retryHost = new();
    TextBlock? activityText;

    readonly TextBox input = new();
    readonly TextBlock placeholder;
    readonly StackPanel thumbnails = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer thumbnailScroll;
    readonly Button sendButton = new();
    readonly TextBlock sendGlyph = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly Button routeButton = new();
    readonly Button modelButton = new();
    readonly Button effortButton = new();
    readonly Border dropOverlay = new();
    readonly List<ImageAttachment> pending = new();

    // 카드가 다시 그려져도 남아야 하는 화면 상태 (메시지 id 별)
    readonly HashSet<Guid> working = new();
    readonly Dictionary<Guid, string> errors = new();
    readonly HashSet<Guid> descriptionOpen = new();

    public ChatView(AppModel model)
    {
        this.model = model;
        chat = model.Chat;
        Background = Brushes.Transparent;
        AllowDrop = true;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var list = new StackPanel { Margin = new Thickness(12) };
        list.Children.Add(head);
        list.Children.Add(messagesPanel);
        list.Children.Add(busyHost);
        list.Children.Add(permissionHost);
        list.Children.Add(loginHost);
        list.Children.Add(retryHost);
        scroll.Content = list;
        Children.Add(scroll);

        thumbnailScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = thumbnails, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8),
        };
        placeholder = Ui.Text("↩ 키로 보내기, ⇧↩ 줄바꿈", 13, "TextTertiary");
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(2, 0, 0, 0);

        var bottom = BuildInputArea();
        Grid.SetRow(bottom, 1);
        Children.Add(bottom);

        BuildDropOverlay();
        Grid.SetRowSpan(dropOverlay, 2);
        Children.Add(dropOverlay);

        Drop += OnDrop;
        DragEnter += (_, e) => DragState(e);
        DragOver += (_, e) => DragState(e);
        DragLeave += (_, _) => dropOverlay.Visibility = Visibility.Collapsed;

        // 탭을 바꿔 화면에서 빠졌다가 돌아와도 이어서 갱신되도록 Loaded 에서 걸고 Unloaded 에서 푼다
        Loaded += (_, _) =>
        {
            Unsubscribe();
            Subscribe();
            SyncMessages();
            RefreshAll();
            ScrollToBottom();
            input.Focus();
        };
        Unloaded += (_, _) => Unsubscribe();
        SyncMessages();
        RefreshAll();
    }

    // MARK: 구독

    void Subscribe()
    {
        chat.PropertyChanged += OnChatChanged;
        chat.MessageUpdated += OnMessageUpdated;
        model.PropertyChanged += OnModelChanged;
        ChatSession.WorkHost.Changed += RefreshRoute;
    }

    void Unsubscribe()
    {
        chat.PropertyChanged -= OnChatChanged;
        chat.MessageUpdated -= OnMessageUpdated;
        model.PropertyChanged -= OnModelChanged;
        ChatSession.WorkHost.Changed -= RefreshRoute;
    }

    void OnChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChatSession.Messages):
                SyncMessages();
                RefreshHead();
                RefreshSend();
                ScrollToBottom();
                break;
            case nameof(ChatSession.IsBusy):
                RefreshBusy();
                RefreshRetry();
                RefreshSend();
                ScrollToBottom();
                break;
            case nameof(ChatSession.Activity):
                if (activityText != null) activityText.Text = chat.Activity ?? "생각 중…";
                break;
            case nameof(ChatSession.LastFailedPrompt):
                RefreshRetry();
                break;
            case nameof(ChatSession.LoginState):
                RefreshLogin();
                ScrollToBottom();
                break;
            case nameof(ChatSession.PendingPermission):
            case nameof(ChatSession.SessionAllow):
                RefreshPermission();
                ScrollToBottom();
                break;
            case nameof(ChatSession.Routing):
                RefreshRoute();
                break;
        }
    }

    void OnMessageUpdated(Guid id)
    {
        if (!blocks.TryGetValue(id, out var block)) return;
        var message = chat.Messages.FirstOrDefault(m => m.Id == id);
        if (message == null) return;
        block.Children.Clear();
        FillBlock(block, message);
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Settings))
        {
            RefreshHead();
            RefreshPickers();
        }
        else if (e.PropertyName == nameof(AppModel.Alarms))
        {
            foreach (var message in chat.Messages.Where(m => m.Alarms.Count > 0)) OnMessageUpdated(message.Id);
        }
    }

    void RefreshAll()
    {
        RefreshHead();
        RefreshBusy();
        RefreshPermission();
        RefreshLogin();
        RefreshRetry();
        RefreshSend();
        RefreshPickers();
        RefreshRoute();
    }

    // MARK: 메시지 목록

    /// 화면의 메시지 줄을 대화 내용에 맞춘다. 늘어난 것만 붙이고, 새 대화로 비워졌으면 처음부터 다시 만든다.
    void SyncMessages()
    {
        var current = chat.Messages;
        var prefixMatches = shown.Count <= current.Count && shown.Select((id, i) => current[i].Id == id).All(x => x);
        if (!prefixMatches)
        {
            messagesPanel.Children.Clear();
            blocks.Clear();
            shown.Clear();
        }
        for (var i = shown.Count; i < current.Count; i++)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            FillBlock(block, current[i]);
            blocks[current[i].Id] = block;
            shown.Add(current[i].Id);
            messagesPanel.Children.Add(block);
        }
    }

    void FillBlock(StackPanel block, ChatMessage message)
    {
        void Add(UIElement element)
        {
            if (element is FrameworkElement f) f.Margin = new Thickness(0, block.Children.Count == 0 ? 0 : 10, 0, 0);
            block.Children.Add(element);
        }
        Add(MessageRow(message));
        if (message.RouteSuggest is { } repo) Add(RouteSuggestCard(message, repo));
        if (message.Alarms.Count > 0) Add(AlarmProposalCard(message));
        if (message.Rules.Count > 0) Add(RuleProposalCard(message));
        if (message.Notes.Count > 0) Add(NoteProposalCard(message));
        if (message.Worklogs.Count > 0) Add(WorklogCard(message));
        if (message.Jira != null) Add(JiraDraftCard(message));
    }

    // MARK: 머리 (처음 사용 · 빈 대화)

    void RefreshHead()
    {
        head.Children.Clear();
        if (model.Settings.OwnerName.Length == 0) head.Children.Add(OnboardingCard());
        else if (chat.Messages.Count == 0)
        {
            var text = Ui.Text("무엇이든 물어보세요. 예: 내 미완료 Jira 알려줘", 13, "TextSecondary", wrap: true);
            text.TextAlignment = TextAlignment.Center;
            text.Margin = new Thickness(0, 40, 0, 0);
            head.Children.Add(text);
        }
        if (head.Children.Count > 0) head.Margin = new Thickness(0, 0, 0, 10);
        else head.Margin = new Thickness(0);
    }

    // MARK: 아래 꼬리 (실행 중 · 승인 · 로그인 · 다시 시도)

    void RefreshBusy()
    {
        busyHost.Children.Clear();
        activityText = null;
        if (!chat.IsBusy) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 28, Height = 3, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        activityText = Ui.Text(chat.Activity ?? "생각 중…", 13, "TextSecondary");
        activityText.MaxWidth = 300;
        activityText.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(activityText);
        var cancel = ChatUi.Link("취소", () => chat.Cancel());
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(cancel);
        busyHost.Children.Add(row);
    }

    void RefreshPermission()
    {
        permissionHost.Children.Clear();
        if (chat.PendingPermission is { } request)
        {
            var card = PermissionCard(request);
            card.Margin = new Thickness(0, 0, 0, 10);
            permissionHost.Children.Add(card);
        }
    }

    void RefreshLogin()
    {
        loginHost.Children.Clear();
        if (chat.LoginState != LoginState.Idle)
        {
            var box = LoginBox();
            box.Margin = new Thickness(0, 0, 0, 10);
            loginHost.Children.Add(box);
        }
    }

    void RefreshRetry()
    {
        retryHost.Children.Clear();
        if (chat.LastFailedPrompt != null && !chat.IsBusy)
        {
            var button = Ui.Pill("다시 시도", () => chat.Retry());
            button.HorizontalAlignment = HorizontalAlignment.Left;
            retryHost.Children.Add(button);
        }
    }

    void ScrollToBottom()
    {
        // 레이아웃이 끝난 뒤에 이동해야 마지막 줄까지 내려간다.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => scroll.ScrollToBottom()));
    }

    // MARK: 입력 영역

    UIElement BuildInputArea()
    {
        var area = new StackPanel { Margin = new Thickness(10) };

        var box = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10) };
        box.SetResourceReference(Border.BackgroundProperty, "FieldBg");
        box.SetResourceReference(Border.BorderBrushProperty, "Line");
        var inner = new StackPanel();
        inner.Children.Add(thumbnailScroll);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var plus = new Button { Content = "", ToolTip = "이미지 첨부 (Ctrl+V·끌어다 놓기도 돼요) · 새 대화", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-6, 0, 4, 0), FontSize = 14 };
        plus.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(plus, "첨부와 새 대화");
        System.Windows.Automation.AutomationProperties.SetAutomationId(plus, "PlusButton");
        plus.Click += (_, _) => ShowPlusMenu(plus);
        row.Children.Add(plus);

        System.Windows.Automation.AutomationProperties.SetName(input, "메시지 입력");
        System.Windows.Automation.AutomationProperties.SetAutomationId(input, "ChatInput");
        input.AcceptsReturn = true;
        input.TextWrapping = TextWrapping.Wrap;
        input.MinLines = 1;
        input.MaxLines = 6;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        input.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
        input.Padding = new Thickness(0, 3, 0, 3);
        input.TextChanged += (_, _) => { placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshSend(); };
        input.PreviewKeyDown += OnInputKey;
        DataObject.AddPastingHandler(input, OnPaste);
        var inputHost = new Grid();
        inputHost.Children.Add(input);
        inputHost.Children.Add(placeholder);
        Grid.SetColumn(inputHost, 1);
        row.Children.Add(inputHost);

        sendButton.SetResourceReference(FrameworkElement.StyleProperty, "SendButton");
        sendButton.VerticalAlignment = VerticalAlignment.Bottom;
        sendButton.Margin = new Thickness(10, 0, 0, 2);
        sendButton.Content = sendGlyph;
        sendGlyph.FontFamily = new FontFamily(ChatUi.IconFont);
        sendButton.Click += (_, _) => { if (chat.IsBusy) chat.Cancel(); else Send(); };
        Grid.SetColumn(sendButton, 2);
        row.Children.Add(sendButton);
        inner.Children.Add(row);
        box.Child = inner;
        area.Children.Add(box);

        // 보낼 곳 · 모델 · 추론 수준
        var options = new DockPanel { Margin = new Thickness(0, 6, 4, 0), Opacity = 0.85 };
        StyleTextButton(effortButton);
        StyleTextButton(modelButton);
        StyleTextButton(routeButton);
        effortButton.Click += (_, _) => ShowChoiceMenu(effortButton, ClaudeCommand.Efforts, ModelLabels.Efforts, () => model.Settings.Effort, v => model.UpdateSettings(s => s.Effort = v));
        modelButton.Click += (_, _) => ShowChoiceMenu(modelButton, ClaudeCommand.Models, ModelLabels.Models, () => model.Settings.Model, v => model.UpdateSettings(s => s.Model = v));
        routeButton.Click += (_, _) => ShowRouteMenu();
        DockPanel.SetDock(effortButton, Dock.Right);
        DockPanel.SetDock(modelButton, Dock.Right);
        options.Children.Add(effortButton);
        options.Children.Add(modelButton);
        options.Children.Add(routeButton);
        area.Children.Add(options);
        return area;
    }

    static void StyleTextButton(Button button)
    {
        button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        button.FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        button.FontSize = 11;
        button.HorizontalAlignment = HorizontalAlignment.Left;
    }

    void RefreshSend()
    {
        var busy = chat.IsBusy;
        var empty = input.Text.Trim().Length == 0 && pending.Count == 0;
        sendGlyph.Text = busy ? "" : "";
        sendGlyph.FontSize = busy ? 9 : 12;
        sendButton.Opacity = !busy && empty ? 0.25 : 1;
        sendButton.ToolTip = busy ? "멈추기" : "보내기";
        sendButton.IsHitTestVisible = busy || !empty;
        System.Windows.Automation.AutomationProperties.SetName(sendButton, busy ? "멈추기" : "보내기");
        System.Windows.Automation.AutomationProperties.SetAutomationId(sendButton, "SendButton");
    }

    void RefreshPickers()
    {
        modelButton.Content = ModelLabels.ShortModel(model.Settings.Model);
        effortButton.Content = ModelLabels.ShortEffort(model.Settings.Effort);
    }

    void RefreshRoute()
    {
        var label = new TextBlock { Text = $"보낼 곳: {chat.Routing.Repo ?? "메이트"}", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        routeButton.Content = label;
        routeButton.ToolTip = "이 대화에서 메시지를 보낼 곳. 새 대화를 시작하면 메이트로 돌아가요";
        if (chat.Routing.IsMate) routeButton.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        else routeButton.SetResourceReference(Control.ForegroundProperty, "OrangeBrush");
    }

    void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            e.Handled = true;
            if (!chat.IsBusy || input.Text.Trim().Length > 0) Send();
        }
    }

    void Send()
    {
        if (input.Text.Trim().Length == 0 && pending.Count == 0) return;
        chat.Send(input.Text, pending.ToList());
        input.Clear();
        pending.Clear();
        RefreshThumbnails();
        RefreshSend();
    }

    // MARK: 이미지 첨부

    void Add(IEnumerable<ImageAttachment> images)
    {
        foreach (var image in images)
            if (pending.Count < ImageLoader.MaxCount) pending.Add(image);
        RefreshThumbnails();
        RefreshSend();
    }

    void RefreshThumbnails()
    {
        thumbnails.Children.Clear();
        thumbnailScroll.Visibility = pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var image in pending.ToList())
        {
            var holder = new Grid { Margin = new Thickness(0, 0, 8, 0) };
            holder.Children.Add(Thumbnail(image.Path, 56));
            var remove = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0),
                Cursor = Cursors.Hand, ToolTip = "빼기",
                Child = new TextBlock { Text = "", FontFamily = new FontFamily(ChatUi.IconFont), FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            remove.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                pending.RemoveAll(p => p.Path == image.Path);
                RefreshThumbnails();
                RefreshSend();
            };
            holder.Children.Add(remove);
            thumbnails.Children.Add(holder);
        }
    }

    void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!ImageLoader.ClipboardHasImage()) return;
        e.CancelCommand();
        Add(ImageLoader.FromClipboard());
    }

    void BuildDropOverlay()
    {
        dropOverlay.Visibility = Visibility.Collapsed;
        dropOverlay.IsHitTestVisible = false;
        dropOverlay.Margin = new Thickness(6);
        dropOverlay.CornerRadius = new CornerRadius(12);
        dropOverlay.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        var frame = new Rectangle { RadiusX = 12, RadiusY = 12, StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 3, 2 } };
        frame.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        var grid = new Grid();
        grid.Children.Add(frame);
        grid.Children.Add(Ui.Text("여기에 놓으면 이미지를 첨부해요", 15, "TextFg", FontWeights.SemiBold));
        ((TextBlock)grid.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)grid.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        dropOverlay.Child = grid;
    }

    void DragState(DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        dropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        dropOverlay.Visibility = Visibility.Collapsed;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                Add(files.Select(ImageLoader.Load).OfType<ImageAttachment>());
            else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap && ImageLoader.Attachment(bitmap) is { } attachment)
                Add(new[] { attachment });
        }
        catch (Exception ex) { App.Log($"끌어 놓은 이미지를 읽지 못했어요: {ex.Message}"); }
        e.Handled = true;
    }

    // MARK: 메뉴

    void ShowPlusMenu(FrameworkElement anchor)
    {
        var menu = ChatUi.NewMenu(anchor);
        menu.Items.Add(ChatUi.Item("이미지 첨부", () => Add(ImageLoader.ChooseFiles())));
        menu.Items.Add(ChatUi.Item("새 대화", () => chat.NewConversation(), enabled: chat.Messages.Count > 0 || chat.IsBusy));
        menu.IsOpen = true;
    }

    void ShowChoiceMenu(FrameworkElement anchor, IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> labels, Func<string> current, Action<string> choose)
    {
        var menu = ChatUi.NewMenu(anchor);
        foreach (var key in new[] { "" }.Concat(keys))
            menu.Items.Add(ChatUi.Item(labels.GetValueOrDefault(key) ?? key, () => choose(key), check: current() == key));
        menu.IsOpen = true;
    }

    void ShowRouteMenu()
    {
        var host = ChatSession.WorkHost;
        var sessions = host.RouteTargets();
        var menu = ChatUi.NewMenu(routeButton);
        menu.Items.Add(ChatUi.Item("메이트", () => chat.Route(null), check: chat.Routing.IsMate));
        if (sessions.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ChatUi.Heading("작업 세션"));
            foreach (var repo in sessions)
                menu.Items.Add(ChatUi.Item(repo, () => chat.Route(repo), check: chat.Routing.Repo == repo));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(NewWorkSessionMenu.Make("새 작업 세션", repo =>
        {
            host.Create(repo);
            chat.Route(repo);
        }));
        menu.IsOpen = true;
    }
}

/// "새 작업 세션" 하위 메뉴 (맥 NewWorkSessionMenu). 작업 탭(Task 8)도 같은 메뉴를 쓴다.
public static class NewWorkSessionMenu
{
    /// WorkspaceRepos.List() 중 아직 세션이 없는 레포를 고르는 메뉴 항목. 고르면 create(repo).
    public static MenuItem Make(string label, Action<string> create, ISet<string>? existing = null)
    {
        existing ??= ChatSession.WorkHost.ExistingRepos();
        var parent = ChatUi.Item(label, () => { });
        var repos = WorkspaceRepos.List();
        var candidates = repos.Where(r => !existing.Contains(r)).ToList();
        if (candidates.Count == 0)
            parent.Items.Add(new MenuItem { Header = repos.Count == 0 ? "~/Workspace 아래에서 git 레포를 찾지 못했어요" : "모든 레포에 이미 작업 세션이 있어요", IsEnabled = false });
        foreach (var repo in candidates) parent.Items.Add(ChatUi.Item(repo, () => create(repo)));
        parent.ToolTip = "~/Workspace 아래 레포를 골라 빈 작업 세션을 만들어요";
        return parent;
    }
}
