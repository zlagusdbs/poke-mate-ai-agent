using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace PokeMate;

/// 창 크기. 맥 PopoverSize (크기는 ui-prefs.json 에 기억한다)
public sealed class PopoverSize : NotifyObject
{
    public static readonly Size MinSize = new(380, 520);
    public static readonly Size MaxSize = new(960, 1200);
    public static readonly Size DefaultSize = new(496, 1008);
    // 위의 크기 값들이 먼저 만들어진 뒤에 생성해야 한다 (정적 필드는 적은 순서대로 초기화된다)
    public static PopoverSize Shared { get; } = new();

    Size size;
    public Size Size { get => size; private set => Set(ref size, value); }

    PopoverSize()
    {
        double w = Prefs.Double("popoverWidth"), h = Prefs.Double("popoverHeight");
        size = Clamped(new Size(w > 0 ? w : DefaultSize.Width, h > 0 ? h : DefaultSize.Height));
    }

    public static Size Clamped(Size value) => new(
        Math.Min(MaxSize.Width, Math.Max(MinSize.Width, Math.Round(value.Width))),
        Math.Min(MaxSize.Height, Math.Max(MinSize.Height, Math.Round(value.Height))));

    public void Set(Size value) => Size = Clamped(value);

    public void Save()
    {
        Prefs.Set("popoverWidth", Size.Width);
        Prefs.Set("popoverHeight", Size.Height);
    }
}

/// 창 고정. 켜면 다른 곳을 눌러도 닫히지 않고 다른 창 위에 떠 있다. 맥 PopoverPin
public sealed class PopoverPin : NotifyObject
{
    public static PopoverPin Shared { get; } = new();
    bool pinned = Prefs.Bool("popoverPinned");

    public bool Pinned
    {
        get => pinned;
        set { if (Set(ref pinned, value)) Prefs.Set("popoverPinned", value); }
    }
}

/// 트레이 근처(작업 표시줄 위, 화면 모서리)에 뜨는 테두리 없는 창 (맥 NSPopover + RootView 의 틀).
/// 위쪽 탭, 내용 자리, 아래 막대로 되어 있다. 다른 곳을 누르면 닫히고, 핀이면 남는다.
///
/// 확장: 탭·도구 화면은 [PopoverTab(SidebarItem.X)] 가 붙은 클래스를 자동으로 찾아 끼운다 (Extensions.cs).
/// 화면 바꾸기는 model.Selected 를 바꾸거나 ShowTab / ShowConnections / ShowSettings 를 부른다.
public partial class PopoverWindow : Window
{
    /// 지금 떠 있는 (또는 숨어 있는) 창. App 이 만든다.
    public static PopoverWindow? Current { get; private set; }

    const double EdgeMargin = 12;

    readonly AppModel model;
    readonly UsageStore usage = UsageStore.Shared;
    readonly PopoverPin pin = PopoverPin.Shared;
    readonly Dictionary<SidebarItem, UIElement> views = new();
    readonly Dictionary<SidebarItem, RadioButton> tabButtons = new();
    readonly Dictionary<SidebarItem, Button> toolButtons = new();
    readonly DispatcherTimer sizeSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(20) };
    int holds;
    long hiddenAt = -10_000;
    bool allowClose;

    public PopoverWindow(AppModel model)
    {
        InitializeComponent();
        Current = this;
        this.model = model;
        Width = PopoverSize.Shared.Size.Width;
        Height = PopoverSize.Shared.Size.Height;

        foreach (var item in SidebarItemExtensions.Tabs)
        {
            var button = new RadioButton { Content = item.Title(), GroupName = "tabs", HorizontalContentAlignment = HorizontalAlignment.Center };
            button.SetResourceReference(StyleProperty, "SegmentButton");
            var captured = item;
            button.Checked += (_, _) => model.Selected = captured;
            tabButtons[item] = button;
            TabBar.Children.Add(button);
        }
        foreach (var item in SidebarItemExtensions.Tools)
        {
            var button = new Button { Content = Glyph(item), ToolTip = item.Title() };
            button.SetResourceReference(StyleProperty, "IconButton");
            var captured = item;
            button.Click += (_, _) => model.Selected = model.Selected == captured ? SidebarItem.Chat : captured;
            toolButtons[item] = button;
            Tools.Children.Add(button);
        }
        var power = new Button { Content = "", ToolTip = "종료" };
        power.SetResourceReference(StyleProperty, "IconButton");
        power.Click += (_, _) => App.Instance.RequestQuit();
        Tools.Children.Add(power);

        model.PropertyChanged += OnModelChanged;
        usage.PropertyChanged += OnUsageChanged;
        pin.PropertyChanged += (_, _) => ApplyPin();
        sizeSave.Tick += (_, _) => { sizeSave.Stop(); PopoverSize.Shared.Save(); };
        clock.Tick += (_, _) => UpdateRelative();
        clock.Start();
        SizeChanged += OnSizeChanged;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled && !pin.Pinned) { e.Handled = true; HidePopover(); }
        };

        ApplyPin();
        ShowSelected();
        UpdateRelative();
    }

    static string Glyph(SidebarItem item) => item switch
    {
        SidebarItem.Connections => "",   // 링크
        SidebarItem.Settings => "",      // 톱니
        _ => "",
    };

    // MARK: 보이기·숨기기

    /// 창을 띄운다. 이미 떠 있으면 앞으로 가져온다.
    public void ShowPopover()
    {
        if (!IsVisible)
        {
            Place();
            Show();
            Place();   // 화면 배율을 알게 된 뒤 한 번 더
        }
        Topmost = true;               // 다른 창 위로 올린 뒤
        Topmost = pin.Pinned;         // 핀이 아니면 되돌린다
        Activate();
        SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Focus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr handle);

    public void HidePopover()
    {
        if (!IsVisible) return;
        hiddenAt = Environment.TickCount64;
        Hide();
    }

    /// 트레이 클릭·단축키용. 방금 다른 곳을 눌러 닫힌 직후면 (그 클릭이 곧 이 호출이다) 다시 열지 않는다.
    public void TogglePopover()
    {
        if (IsVisible) HidePopover();
        else if (Environment.TickCount64 - hiddenAt > 300) ShowPopover();
    }

    /// 다이얼로그를 띄우는 동안 창이 닫히지 않게 붙잡는다 (맥 pokeMateHoldPopover). 끝나면 Release.
    public void Hold() => holds++;
    public void Release() => holds = Math.Max(0, holds - 1);

    /// "home" | "alarms" | "work" | "notes" | "collection" (+ "connections" | "settings") 를 열고 창을 띄운다
    public void ShowTab(string tab)
    {
        var key = tab == "home" ? "chat" : tab;
        foreach (var item in SidebarItemExtensions.AllCases)
            if (item.Raw() == key) { ShowTab(item); return; }
    }

    public void ShowTab(SidebarItem item)
    {
        model.Selected = item;
        ShowPopover();
    }

    public void ShowConnections() => ShowTab(SidebarItem.Connections);
    public void ShowSettings() => ShowTab(SidebarItem.Settings);

    /// 앱을 끌 때 창을 정말로 닫는다
    public void CloseForQuit()
    {
        allowClose = true;
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        HidePopover();
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (!pin.Pinned && holds == 0 && IsVisible) HidePopover();
    }

    void ApplyPin()
    {
        Topmost = pin.Pinned;
        PinButton.Content = pin.Pinned ? "" : "";   // 꽂힌 핀 / 핀
        PinButton.ToolTip = pin.Pinned ? "고정 풀기 · 다른 곳을 누르면 닫힘" : "고정하기 · 다른 창을 눌러도 계속 떠 있음";
        if (pin.Pinned) PinButton.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        else PinButton.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        if (pin.Pinned && !IsVisible && IsLoaded) ShowPopover();
    }

    void DragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        try { DragMove(); } catch { }
    }

    // MARK: 자리

    /// 트레이가 있는 화면 모서리에 놓는다 (작업 표시줄이 아래면 오른쪽 아래). 작업 영역 안으로 당겨 넣는다.
    void Place()
    {
        var cursor = WinForms.Cursor.Position;
        var screen = WinForms.Screen.FromPoint(cursor);
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var safe = new PanelRect(work.X / sx, work.Y / sy, work.Width / sx, work.Height / sy);

        var size = PopoverSize.Clamped(PopoverSize.Shared.Size);
        var fit = new PanelSize(Math.Min(size.Width, safe.Width - EdgeMargin * 2), Math.Min(size.Height, safe.Height - EdgeMargin * 2));
        var frame = PanelPlacement.DefaultFrame(fit, safe, EdgeMargin);
        double x = frame.X, y = frame.Y;
        if (work.Left > bounds.Left && work.Right == bounds.Right) x = safe.MinX + EdgeMargin;          // 작업 표시줄이 왼쪽
        if (work.Top > bounds.Top && work.Bottom == bounds.Bottom) y = safe.MinY + EdgeMargin;           // 작업 표시줄이 위
        frame = PanelPlacement.Clamp(new PanelRect(x, y, frame.Width, frame.Height), safe, EdgeMargin);

        Width = frame.Width;
        Height = frame.Height;
        Left = frame.X;
        Top = frame.Y;
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || e.PreviousSize.Width == 0) return;
        PopoverSize.Shared.Set(new Size(ActualWidth, ActualHeight));
        sizeSave.Stop();
        sizeSave.Start();
    }

    // MARK: 화면 바꾸기

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Selected)) ShowSelected();
    }

    void ShowSelected()
    {
        var item = model.Selected;
        foreach (var (tab, button) in tabButtons)
            if (button.IsChecked != (tab == item)) button.IsChecked = tab == item;
        foreach (var (tool, button) in toolButtons)
            button.SetResourceReference(Control.ForegroundProperty, tool == item ? "AccentBrush" : "TextSecondary");
        Host.Child = ViewFor(item);
    }

    UIElement ViewFor(SidebarItem item)
    {
        if (views.TryGetValue(item, out var cached)) return cached;
        UIElement view;
        try
        {
            view = item switch
            {
                SidebarItem.Chat => new HomeView(model),
                SidebarItem.Collection => new CollectionView(model),
                _ => Extensions.Tab(item)?.Invoke(model) ?? Placeholder(item),
            };
        }
        catch (Exception e)
        {
            App.Log($"{item.Title()} 화면을 만들지 못했어요: {e}");
            view = Placeholder(item);
        }
        views[item] = view;
        return view;
    }

    static UIElement Placeholder(SidebarItem item) => new TextBlock
    {
        Text = item.Title(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Foreground = Theme.Brush("TextTertiary"),
    };

    // MARK: 아래 막대

    void OnUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsageStore.Updated)) UpdateRelative();
    }

    void OnRefresh(object sender, RoutedEventArgs e) => usage.Refresh();

    void OnPin(object sender, RoutedEventArgs e) => pin.Pinned = !pin.Pinned;

    void UpdateRelative()
    {
        UpdatedText.Text = usage.Updated is { } updated ? "갱신 " + RelativeText(DateTimeOffset.Now - updated) : "";
    }

    /// 맥의 상대 시각("2분")과 같은 자리. 작은 글씨로 "방금" / "N분 전" / "N시간 전"
    public static string RelativeText(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 45) return "방금";
        if (elapsed.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(elapsed.TotalMinutes))}분 전";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}시간 전";
        return $"{(int)elapsed.TotalDays}일 전";
    }
}
