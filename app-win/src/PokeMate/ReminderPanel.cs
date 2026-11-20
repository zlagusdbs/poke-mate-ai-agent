using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PokeMate;

/// 화면 오른쪽 아래 "메이트 알림" 창 (맥 ReminderPanelController + ReminderListView).
/// 항상 위에 떠 있지만 입력 포커스는 가져가지 않는다 (WS_EX_NOACTIVATE). 머리 줄을 끌어서 옮길 수 있고 위치는 기억한다.
public sealed class ReminderPanelController
{
    public const double Width = 340;
    const double MaxHeight = 520;

    // 맥 Palette: 연한 파랑 카드 (밝은 모드·어두운 모드 모두 같은 색)
    static readonly Color Background = Color.FromRgb(0xE3, 0xF0, 0xFF);
    static readonly Color TitleColor = Color.FromRgb(0x05, 0x2B, 0x54);
    static readonly Color SubColor = Color.FromRgb(0x17, 0x5E, 0xA6);
    static readonly Color LineColor = Color.FromRgb(0x85, 0xB8, 0xEB);
    static readonly Color AccentColor = Color.FromRgb(0x38, 0x8A, 0xDE);

    static Brush Solid(Color color) { var b = new SolidColorBrush(color); b.Freeze(); return b; }

    readonly ReminderCenter center;
    readonly Action openChat;
    readonly Window window;
    readonly Border host = new();
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool placing;

    public ReminderPanelController(ReminderCenter center, Action openChat)
    {
        this.center = center;
        this.openChat = openChat;
        window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height, Width = Width, Title = "메이트 알림",
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic"), FontSize = 13, UseLayoutRounding = true,
            Content = host,
        };
        ChatUi.Id(window, "ReminderPanel");
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        };
        window.LocationChanged += (_, _) =>
        {
            if (placing || !window.IsVisible) return;
            saveTimer.Stop();
            saveTimer.Start();
        };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveMoved(); };
        center.OnChange = Refresh;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => window.Dispatcher.BeginInvoke(Refresh);
    }

    const int GwlExStyle = -20;
    const int WsExNoActivate = 0x08000000;
    const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    public void Close()
    {
        saveTimer.Stop();
        try { window.Close(); } catch { }
    }

    // MARK: 자리

    /// 항상 주 화면의 작업 영역(작업 표시줄 제외) 안. 맥은 노트북 화면, Windows 는 주 화면.
    static PanelRect SafeFrame()
    {
        var area = SystemParameters.WorkArea;
        return new PanelRect(area.Left, area.Top, area.Width, area.Height);
    }

    void SaveMoved()
    {
        if (placing || !window.IsVisible) return;
        var fixedFrame = PanelPlacement.Clamp(new PanelRect(window.Left, window.Top, window.ActualWidth, window.ActualHeight), SafeFrame());
        Prefs.Set("reminderPanelX", fixedFrame.X);
        Prefs.Set("reminderPanelY", fixedFrame.Y);
        Prefs.Set("reminderPanelSet", true);
        if (Math.Abs(fixedFrame.X - window.Left) > 0.5 || Math.Abs(fixedFrame.Y - window.Top) > 0.5) Place(fixedFrame);
    }

    void Place(PanelRect frame)
    {
        placing = true;
        window.Left = frame.X;
        window.Top = frame.Y;
        placing = false;
    }

    public void ResetPosition()
    {
        Prefs.Set("reminderPanelSet", false);
        Refresh();
    }

    // MARK: 보이기

    public void Refresh()
    {
        var items = center.Visible;
        if (items.Count == 0) { if (window.IsVisible) window.Hide(); return; }

        host.Child = Build(items);
        host.Measure(new Size(Width, double.PositiveInfinity));
        var size = new PanelSize(Width, Math.Min(host.DesiredSize.Height, MaxHeight));
        var safe = SafeFrame();
        var frame = PanelPlacement.DefaultFrame(size, safe);
        if (Prefs.Bool("reminderPanelSet"))
            frame = PanelPlacement.Clamp(new PanelRect(Prefs.Double("reminderPanelX"), Prefs.Double("reminderPanelY"), size.Width, size.Height), safe);
        placing = true;
        window.Width = Width;
        window.Left = frame.X;
        // 아래 모서리를 기준으로 맞춘다: 높이가 바뀌어도 아래 여백이 그대로
        window.Top = frame.Y;
        placing = false;
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        // 실제 높이가 달라졌으면 아래 모서리를 다시 맞춘다
        var actual = window.ActualHeight;
        if (actual > 0 && Math.Abs(actual - size.Height) > 1 && !Prefs.Bool("reminderPanelSet"))
            Place(PanelPlacement.DefaultFrame(new PanelSize(Width, actual), safe));
    }

    // MARK: 화면 (맥 ReminderListView · ReminderCard)

    UIElement Build(List<Reminder> items)
    {
        var column = new StackPanel();

        var header = new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 8), ToolTip = "끌어서 옮길 수 있어요 · 오른쪽 클릭: 기본 위치로" };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var bell = new TextBlock { Text = "", FontFamily = new FontFamily(ChatUi.IconFont), FontSize = 12, Foreground = Solid(TitleColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        label.Children.Add(bell);
        label.Children.Add(new TextBlock { Text = $"메이트 알림 {items.Count}", FontWeight = FontWeights.Medium, Foreground = Solid(TitleColor), VerticalAlignment = VerticalAlignment.Center });
        header.Child = label;
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) { try { window.DragMove(); } catch { } } };
        var menu = new ContextMenu();
        menu.Items.Add(ChatUi.Item("기본 위치로", ResetPosition));
        header.ContextMenu = menu;
        column.Children.Add(header);

        var cards = new StackPanel();
        foreach (var item in items)
        {
            var card = Card(item);
            card.Margin = new Thickness(0, cards.Children.Count == 0 ? 0 : 8, 0, 0);
            cards.Children.Add(card);
        }
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 400 };
        column.Children.Add(scroll);

        var all = new Button { Content = "모두 확인", Cursor = Cursors.Hand, Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        all.Template = TextOnlyTemplate();
        all.Foreground = Solid(SubColor);
        all.FontSize = 11;
        ChatUi.Id(all, "ReminderDismissAll");
        all.Click += (_, _) => center.DismissAll();
        column.Children.Add(all);

        return new Border
        {
            Child = column, Padding = new Thickness(12), CornerRadius = new CornerRadius(14), Width = Width,
            Background = Solid(Background), BorderBrush = Solid(Color.FromArgb(0x99, LineColor.R, LineColor.G, LineColor.B)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 },
            Margin = new Thickness(0),
        };
    }

    Border Card(Reminder item)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = item.Title, FontWeight = FontWeights.Medium, Foreground = Solid(TitleColor), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var time = item.FiredAt.ToLocalTime().ToString("tt h:mm", CultureInfo.GetCultureInfo("ko-KR"));
        stack.Children.Add(new TextBlock
        {
            Text = $"{item.Summary} · {time}", FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 32,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
            Foreground = item.Failed ? Brushes.Red : Solid(SubColor),
        });

        var buttons = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (item.SessionRepo is { } repo)
            left.Children.Add(Chip("새 세션으로 바꾸기", false, () => { ReminderCenter.ResetSession(repo); center.Dismiss(item.Id); }, "ReminderReset"));
        else if (!string.IsNullOrEmpty(item.Url) && Uri.TryCreate(item.Url, UriKind.Absolute, out var url))
            left.Children.Add(Chip("열기", false, () => { Opener.Open(url.ToString()); center.Dismiss(item.Id); }, "ReminderOpen"));
        else
            left.Children.Add(Chip("자세히", false, openChat, "ReminderDetail"));

        var snooze = Chip("10분 뒤", false, () => center.Snooze(item.Id, 10), "ReminderSnooze");
        snooze.Margin = new Thickness(6, 0, 0, 0);
        left.Children.Add(snooze);
        var more = Chip("", false, () => { }, "ReminderSnoozeMenu");
        more.FontFamily = new FontFamily(ChatUi.IconFont);
        more.FontSize = 9;
        more.Margin = new Thickness(2, 0, 0, 0);
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = more, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var minutes in ReminderQueue.SnoozeChoices)
            {
                var m = minutes;
                menu.Items.Add(ChatUi.Item(m >= 60 ? $"{m / 60}시간 뒤" : $"{m}분 뒤", () => center.Snooze(item.Id, m)));
            }
            menu.IsOpen = true;
        };
        left.Children.Add(more);
        buttons.Children.Add(left);

        var ok = Chip("확인", true, () => center.Dismiss(item.Id), "ReminderOk");
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        return new Border
        {
            Child = stack, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Background = Brushes.White,
        };
    }

    // MARK: 칩 단추 (맥 ChipStyle)

    static ControlTemplate TextOnlyTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        return template;
    }

    static Button Chip(string text, bool filled, Action click, string? id = null)
    {
        var button = new Button
        {
            Content = text, FontSize = 11, Cursor = Cursors.Hand, Focusable = false,
            Foreground = filled ? Brushes.White : Solid(TitleColor),
        };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 3, 8, 3));
        border.SetValue(Border.BackgroundProperty, filled ? Solid(AccentColor) : Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, filled ? Brushes.Transparent : Solid(LineColor));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0.5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var pressed = new System.Windows.Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));
        template.Triggers.Add(pressed);
        button.Template = template;
        if (id != null) ChatUi.Id(button, id);
        button.Click += (_, _) => click();
        return button;
    }
}
