using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PokeMate;

/// 편집 화면(알림 수정, 설정, 연결 로그인) 색과 입력 부품. 고칠 수 있는 칸은 입력 바탕색, 칸을 담는 카드는 한 톤 짙은 회색.
/// (맥 EditorStyle + whiteField + grayCard + PMCheckboxStyle + Picker)
public static class EditorStyle
{
    static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// 칸을 담는 카드 바탕
    public static Brush Card => Theme.IsDark ? Make("#FF343434") : Make("#FFECECEC");
    /// 고칠 수 있는 글자 칸 바탕
    public static Brush Field => Theme.Brush("FieldBg");
    public static Brush Line => Theme.Brush("Line");

    // MARK: 글자 칸

    /// 글자 칸. 입력 바탕 + 얇은 테두리 (맥 whiteField). hint 는 비었을 때 보이는 안내 글자.
    public static (Border Frame, TextBox Box) TextField(string text, Action<string> changed, string? hint = null, bool multiline = false,
                                                        bool mono = false, string? id = null, int minLines = 1, int maxLines = 1)
    {
        var box = new TextBox
        {
            Text = text, Padding = new Thickness(0), AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            MinLines = minLines, MaxLines = multiline ? Math.Max(minLines, maxLines) : 1,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        box.SetResourceReference(FrameworkElement.StyleProperty, "PmTextBox");
        if (mono) box.FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic");
        if (id != null) ChatUi.Id(box, id);

        var grid = new Grid();
        grid.Children.Add(box);
        if (hint != null)
        {
            var placeholder = Ui.Text(hint, 13, "TextTertiary");
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
            placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            grid.Children.Add(placeholder);
            box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        box.TextChanged += (_, _) => changed(box.Text);

        var frame = new Border
        {
            Child = grid, Padding = new Thickness(7, 5, 7, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Background = Field, BorderBrush = Line,
        };
        return (frame, box);
    }

    public static Border WhiteField(string text, Action<string> changed, string? hint = null, bool mono = false, string? id = null)
        => TextField(text, changed, hint, false, mono, id).Frame;

    /// 비밀번호 칸 (같은 모양)
    public static (Border Frame, PasswordBox Box) PasswordField(Action<string> changed, string? id = null, string? hint = null)
    {
        var box = new PasswordBox { Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
        box.SetResourceReference(Control.ForegroundProperty, "TextFg");
        box.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextFg");
        if (id != null) ChatUi.Id(box, id);
        var grid = new Grid();
        grid.Children.Add(box);
        TextBlock? placeholder = null;
        if (hint != null)
        {
            placeholder = Ui.Text(hint, 13, "TextTertiary");
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(placeholder);
        }
        box.PasswordChanged += (_, _) =>
        {
            if (placeholder != null) placeholder.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            changed(box.Password);
        };
        var frame = new Border
        {
            Child = grid, Padding = new Thickness(7, 5, 7, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Background = Field, BorderBrush = Line,
        };
        return (frame, box);
    }

    // MARK: 켜기·끄기, 고르기

    public static CheckBox Check(object text, bool on, Action<bool> changed, string? tip = null, string? id = null, bool switchStyle = false)
    {
        var box = new CheckBox { Content = text, IsChecked = on, ToolTip = tip, Margin = new Thickness(0, 2, 0, 2) };
        box.SetResourceReference(FrameworkElement.StyleProperty, switchStyle ? "PmSwitch" : "PmCheck");
        if (tip != null) ToolTipService.SetShowDuration(box, 30000);
        if (id != null) ChatUi.Id(box, id);
        // Click 이 아니라 Checked·Unchecked: 화면 자동화(UIA)의 Toggle 도 값만 바꾸고 Click 을 보내지 않는다
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    public static CheckBox Switch(object text, bool on, Action<bool> changed, string? tip = null, string? id = null)
        => Check(text, on, changed, tip, id, switchStyle: true);

    /// 메뉴로 고르는 버튼 (맥 Picker). 글자는 지금 고른 항목.
    public static Button Picker<T>(IReadOnlyList<(string Label, T Value)> options, T current, Action<T> changed, string? id = null, double? width = null)
    {
        var label = options.Where(o => EqualityComparer<T>.Default.Equals(o.Value, current)).Select(o => o.Label).FirstOrDefault() ?? "";
        var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left };
        if (width is double w) button.MinWidth = w;
        button.SetResourceReference(FrameworkElement.StyleProperty, "PmPicker");
        if (id != null) ChatUi.Id(button, id);
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MinWidth = button.ActualWidth };
            foreach (var option in options)
            {
                var captured = option;
                var item = ChatUi.Item(option.Label, () => changed(captured.Value), check: EqualityComparer<T>.Default.Equals(option.Value, current));
                System.Windows.Automation.AutomationProperties.SetAutomationId(item, (id ?? "Picker") + ":" + option.Label);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return button;
    }

    /// 분할 선택 (맥 segmented Picker)
    public static FrameworkElement Segmented<T>(IReadOnlyList<(string Label, T Value)> options, T current, Action<T> changed, string? id = null)
    {
        var panel = new UniformGrid { Rows = 1 };
        var group = "seg" + Guid.NewGuid().ToString("N");
        foreach (var option in options)
        {
            var button = new RadioButton
            {
                Content = option.Label, GroupName = group, HorizontalContentAlignment = HorizontalAlignment.Center,
                IsChecked = EqualityComparer<T>.Default.Equals(option.Value, current),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
            if (id != null) ChatUi.Id(button, id + ":" + option.Label);
            var value = option.Value;
            button.Checked += (_, _) => { if (!EqualityComparer<T>.Default.Equals(value, current)) changed(value); };
            panel.Children.Add(button);
        }
        var card = Ui.Card(panel, 2);
        card.CornerRadius = new CornerRadius(9);
        return card;
    }

    // MARK: 설명 아이콘, 카드, 줄

    /// ? 아이콘. 마우스를 올리면 설명이 뜬다 (맥 HelpIcon)
    public static FrameworkElement Help(string text)
    {
        var icon = ChatUi.Glyph("", "TextSecondary", 12);
        icon.Margin = new Thickness(4, 0, 0, 0);
        icon.Cursor = System.Windows.Input.Cursors.Help;
        icon.ToolTip = new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Padding = new Thickness(4) } };
        ToolTipService.SetInitialShowDelay(icon, 0);
        ToolTipService.SetShowDuration(icon, 60000);
        return icon;
    }

    /// 회색 카드 한 장 (맥 Form Section + grayCard). 위에 작은 제목, 아래에 작은 설명을 둘 수 있다.
    public static FrameworkElement Section(string? header, IEnumerable<UIElement> rows, string? footer = null, UIElement? headerRight = null, double spacing = 8)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (header != null || headerRight != null)
        {
            var head = new DockPanel { Margin = new Thickness(4, 0, 4, 4), LastChildFill = true };
            if (headerRight != null) { DockPanel.SetDock(headerRight, Dock.Right); head.Children.Add(headerRight); }
            head.Children.Add(Ui.Text(header ?? "", 12, "TextSecondary", FontWeights.SemiBold));
            outer.Children.Add(head);
        }
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (row is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, first ? 0 : spacing, fe.Margin.Right, fe.Margin.Bottom);
            stack.Children.Add(row);
            first = false;
        }
        outer.Children.Add(new Border { Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = Card });
        if (footer != null)
        {
            var note = Ui.Text(footer, 11, "TextSecondary", wrap: true);
            note.Margin = new Thickness(4, 4, 4, 0);
            outer.Children.Add(note);
        }
        return outer;
    }

    /// 왼쪽 이름, 오른쪽 입력 한 줄
    public static FrameworkElement Row(UIElement label, UIElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (label is FrameworkElement l) l.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        var right = (FrameworkElement)control;
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    public static FrameworkElement Row(string label, UIElement control, string? help = null)
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal };
        text.Children.Add(Ui.Text(label, 13));
        if (help != null) text.Children.Add(Help(help));
        return Row(text, control);
    }

    /// 작은 이름 + 아래 입력 (맥 VStack caption + whiteField)
    public static FrameworkElement Labeled(string label, UIElement control)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(label, 12, "TextSecondary"));
        ((FrameworkElement)control).Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(control);
        return stack;
    }

    public static Button GlyphButton(string glyph, Action click, string? tip = null, string? foreground = null, string? id = null)
    {
        var button = new Button { Content = glyph, ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PmGlyphButton");
        if (foreground != null) button.SetResourceReference(Control.ForegroundProperty, foreground);
        if (id != null) ChatUi.Id(button, id);
        button.Click += (_, _) => click();
        return button;
    }

    /// 선 한 줄
    public static FrameworkElement Divider(double margin = 4)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        return line;
    }

    // MARK: 대화 상자

    /// 편집 창의 틀 (모달로 띄운다)
    public static Window Dialog(string title, double width, UIElement content)
    {
        var window = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Content = content,
            FontFamily = new FontFamily("Segoe UI, Malgun Gothic"), FontSize = 13, UseLayoutRounding = true,
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        window.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFg");
        window.MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && !e.Handled) { e.Handled = true; window.DialogResult = false; } };
        return window;
    }

    /// 모달로 보이고 끝나면 결과를 돌려준다. 보이는 동안 메이트 창이 닫히지 않게 붙잡는다.
    public static bool? ShowModal(Window window)
    {
        var popover = PopoverWindow.Current;
        if (popover != null && popover.IsVisible) window.Owner = popover;
        popover?.Hold();
        try { return window.ShowDialog(); }
        finally { popover?.Release(); }
    }

    /// 확인 상자 (삭제 확인 등)
    public static bool Confirm(string title, string message, string ok = "삭제", string cancel = "취소")
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text(title, 15, "TextFg", FontWeights.SemiBold));
        var text = Ui.Text(message, 13, "TextSecondary", wrap: true);
        text.Margin = new Thickness(0, 8, 0, 16);
        body.Children.Add(text);
        var window = Dialog(title, 380, body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = Ui.Pill(cancel, () => window.DialogResult = false);
        cancelButton.Margin = new Thickness(0, 0, 8, 0);
        ChatUi.Id(cancelButton, "ConfirmCancel");
        var okButton = ChatUi.Primary(ok, () => window.DialogResult = true);
        ChatUi.Id(okButton, "ConfirmOk");
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        body.Children.Add(buttons);
        return ShowModal(window) == true;
    }

    /// 알림 상자 (확인 하나)
    public static void Notice(string title, string message)
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text(title, 15, "TextFg", FontWeights.SemiBold));
        var text = Ui.Text(message, 13, "TextSecondary", wrap: true);
        text.Margin = new Thickness(0, 8, 0, 16);
        body.Children.Add(text);
        var window = Dialog(title, 380, body);
        var ok = ChatUi.Primary("확인", () => window.DialogResult = true);
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(ok);
        ShowModal(window);
    }
}
