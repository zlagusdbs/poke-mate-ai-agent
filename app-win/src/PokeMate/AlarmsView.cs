using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Trigger = PokeMate.Core.Trigger;

namespace PokeMate;

/// 알람 탭 (맥 AlarmsView). 요약 카드, 그룹, 알림 목록. 알림과 그룹 모두 끌어서 옮기기와 오른쪽 클릭 "위로/아래로 옮기기"가 있다.
[PopoverTab(SidebarItem.Alarms)]
public sealed class AlarmsView : UserControl
{
    const string DragFormat = "PokeMate.Alarm";
    const string GroupDragFormat = "PokeMate.AlarmGroup";
    static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    readonly AppModel model;
    readonly StackPanel list = new();
    /// 상세를 펼친 알림
    readonly HashSet<Guid> openRows = new();
    bool dragging;
    bool renderLater;

    public AlarmsView(AppModel model)
    {
        this.model = model;
        var column = new DockPanel();

        var header = new DockPanel { Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 4) };
        var add = Ui.Pill("추가", () => Edit(new Alarm("", "", 9, 0, new Alarm.Repeat.Weekdays())));
        add.Content = ChatUi.Label("", "추가", "TextFg", 13);
        ChatUi.Id(add, "AlarmAdd");
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        var group = Ui.Pill("그룹", NewGroup, "새 그룹 만들기. 알림을 그룹으로 끌어다 놓으면 옮겨져요");
        group.Content = ChatUi.Label("", "그룹", "TextFg", 13);
        ChatUi.Id(group, "AlarmGroupAdd");
        group.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(group, Dock.Right);
        header.Children.Add(group);
        header.Children.Add(Ui.Text("알람", 15, "TextFg", FontWeights.SemiBold));
        DockPanel.SetDock(header, Dock.Top);
        column.Children.Add(header);

        list.Margin = new Thickness(Theme.Gutter, 0, Theme.Gutter, 12);
        column.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = column;

        Loaded += (_, _) =>
        {
            model.PropertyChanged += OnModelChanged;
            UsageStore.Shared.PropertyChanged += OnUsageChanged;
            Render();
            if (model.Systems.Count == 0) _ = model.RefreshSystems();
        };
        Unloaded += (_, _) =>
        {
            model.PropertyChanged -= OnModelChanged;
            UsageStore.Shared.PropertyChanged -= OnUsageChanged;
        };
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Alarms) or nameof(AppModel.AlarmGroups) or nameof(AppModel.AlarmRuns) or nameof(AppModel.AlarmLog) or nameof(AppModel.Systems))
            Render();
    }

    void OnUsageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageStore.Mate) or nameof(UsageStore.AllSpecies)) Render();
    }

    // MARK: 펼침 상태 (앱을 다시 켜도 기억한다)

    static HashSet<string> ExpandedGroups() => (Prefs.String("alarmExpandedGroups") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    static void ToggleGroup(string name)
    {
        var keys = (Prefs.String("alarmExpandedGroups") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!keys.Remove(name)) keys.Add(name);
        Prefs.Set("alarmExpandedGroups", string.Join("\n", keys));
    }

    // MARK: 그리기

    void Render()
    {
        if (dragging) { renderLater = true; return; }
        list.Children.Clear();
        list.Children.Add(SummaryCard());
        if (model.Alarms.Count == 0 && model.AlarmGroups.Count == 0) return;
        var split = AlarmGroup.Make(model.Alarms, model.AlarmGroups);
        var expanded = ExpandedGroups();
        foreach (var group in split.Groups)
            list.Children.Add(Spaced(GroupView(group, expanded.Contains(group.Name))));
        if (split.Ungrouped.Count > 0 || model.AlarmGroups.Count > 0)
            list.Children.Add(Spaced(UngroupedView(split.Ungrouped)));
    }

    static UIElement Spaced(UIElement element)
    {
        if (element is FrameworkElement fe) fe.Margin = new Thickness(0, 10, 0, 0);
        return element;
    }

    static string Title(Alarm alarm) => alarm.Title.Length == 0 ? "(제목 없음)" : alarm.Title;
    static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    static string MonthDay(DateTimeOffset at) => at.ToLocalTime().ToString("M/d", CultureInfo.InvariantCulture);

    UIElement SummaryCard()
    {
        var enabled = model.Alarms.Count(a => a.Enabled);
        var ids = model.Alarms.Select(a => a.Id).ToHashSet();
        var today = AlarmRunLog.TodayTotal(model.AlarmLog, ids, DateTimeOffset.Now);
        var last = model.AlarmLog.Where(kv => ids.Contains(kv.Key)).OrderByDescending(kv => kv.Value.LastAt).Cast<KeyValuePair<Guid, AlarmRunRecord>?>().FirstOrDefault();

        var row = new DockPanel();
        var usage = UsageStore.Shared;
        UIElement sprite;
        if (usage.Mate.Current is { } current && usage.Species(current.Species) is { } species)
            sprite = new MateSprite(species, current.Tokens, current.Form, current.Shown, 40);
        else
            sprite = new PixelSprite(MateSpecies.EggGrid, new Dictionary<char, uint> { ['k'] = 0x3A3A4A, ['f'] = 0xF3EADB, ['s'] = 0x9A9AA5 }, 40);
        var frame = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(10), Child = sprite, Margin = new Thickness(0, 0, 12, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        DockPanel.SetDock(frame, Dock.Left);
        row.Children.Add(frame);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (model.Alarms.Count == 0)
            text.Children.Add(Ui.Text("알람이 없어요. [+ 추가]로 만들어 보세요", 13, wrap: true));
        else
        {
            text.Children.Add(Ui.Text($"켜진 알람 {enabled}개 · 오늘 실행 {today}번", 13, weight: FontWeights.SemiBold));
            if (last is { } pair && model.Alarms.FirstOrDefault(a => a.Id == pair.Key) is { } alarm)
            {
                var line = Ui.Text($"마지막: {Title(alarm)} {Clock(pair.Value.LastAt)} {pair.Value.Result}", 11, pair.Value.Failed ? "TextSecondary" : "TextSecondary");
                if (pair.Value.Failed) line.Foreground = Brushes.IndianRed;
                line.Margin = new Thickness(0, 3, 0, 0);
                text.Children.Add(line);
            }
            else
            {
                var line = Ui.Text("아직 실행한 알람이 없어요", 11, "TextSecondary");
                line.Margin = new Thickness(0, 3, 0, 0);
                text.Children.Add(line);
            }
        }
        row.Children.Add(text);
        var card = Ui.Card(row, 12);
        ChatUi.Id(card, "AlarmSummary");
        return card;
    }

    // MARK: 그룹

    UIElement GroupView(AlarmGroup group, bool expanded)
    {
        var body = new StackPanel();
        var header = Header("", group.Name, group.Alarms.Count, expanded, group.Alarms.Any(a => model.AlarmRuns.GetValueOrDefault(a.Id) is AlarmRunState.Running),
            () => { ToggleGroup(group.Name); Render(); }, expanded ? "접기 · 끌어서 순서 바꾸기 · 오른쪽 클릭: 옮기기, 이름 바꾸기, 삭제" : "펼치기 · 끌어서 순서 바꾸기 · 오른쪽 클릭: 옮기기, 이름 바꾸기, 삭제", "AlarmGroup:" + group.Name);
        var menu = new ContextMenu();
        menu.Items.Add(ChatUi.Item("모두 켜기", () => { foreach (var a in group.Alarms) SetEnabled(a.Id, true); }));
        menu.Items.Add(ChatUi.Item("모두 끄기", () => { foreach (var a in group.Alarms) SetEnabled(a.Id, false); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(ChatUi.Item("위로 옮기기", () => model.ShiftGroup(group.Name, true), enabled: model.CanShiftGroup(group.Name, true)));
        menu.Items.Add(ChatUi.Item("아래로 옮기기", () => model.ShiftGroup(group.Name, false), enabled: model.CanShiftGroup(group.Name, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(ChatUi.Item("이름 바꾸기", () => RenameGroup(group.Name)));
        menu.Items.Add(ChatUi.Item("그룹 삭제", () => DeleteGroup(group.Name), foreground: "OrangeBrush"));
        header.ContextMenu = menu;
        DragSource(header, () => new DataObject(GroupDragFormat, group.Name));
        body.Children.Add(header);
        if (expanded)
        {
            if (group.Alarms.Count == 0)
            {
                body.Children.Add(EditorStyle.Divider(6));
                body.Children.Add(Ui.Text("비어 있어요. 알림을 끌어다 놓으세요.", 11, "TextSecondary"));
            }
            foreach (var alarm in group.Alarms)
            {
                body.Children.Add(EditorStyle.Divider(2));
                body.Children.Add(Row(alarm, group.Name));
            }
        }
        var card = Ui.Card(body, 12);
        MakeDropTarget(card, group.Name);
        return card;
    }

    UIElement UngroupedView(List<Alarm> alarms)
    {
        var collapsed = Prefs.Bool("alarmUngroupedCollapsed");
        var body = new StackPanel();
        body.Children.Add(Header("", "그룹 없음", alarms.Count, !collapsed, alarms.Any(a => model.AlarmRuns.GetValueOrDefault(a.Id) is AlarmRunState.Running),
            () => { Prefs.Set("alarmUngroupedCollapsed", !collapsed); Render(); }, null, "AlarmGroup:ungrouped"));
        if (!collapsed)
            foreach (var alarm in alarms)
            {
                body.Children.Add(EditorStyle.Divider(2));
                body.Children.Add(Row(alarm, null));
            }
        var card = Ui.Card(body, 12);
        MakeDropTarget(card, null);
        return card;
    }

    FrameworkElement Header(string icon, string name, int count, bool expanded, bool running, Action toggle, string? tip, string id)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 2) };
        var chevron = ChatUi.Glyph(expanded ? "" : "", "TextSecondary", 9);
        chevron.Width = 12;
        row.Children.Add(chevron);
        var folder = ChatUi.Glyph(icon, "OrangeBrush", 12);
        folder.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(folder);
        row.Children.Add(Ui.Text(name, 13, weight: FontWeights.SemiBold));
        var number = Ui.Text(count.ToString(), 11, "TextSecondary");
        number.Margin = new Thickness(6, 0, 0, 0);
        number.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(number);
        if (running)
        {
            var spin = Ui.Text("실행 중", 11, "OrangeBrush");
            spin.Margin = new Thickness(8, 0, 0, 0);
            spin.VerticalAlignment = VerticalAlignment.Center;
            spin.ToolTip = "실행 중인 알림이 있어요";
            row.Children.Add(spin);
        }
        var button = new Button { Content = row, Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        template.VisualTree = presenter;
        button.Template = template;
        ChatUi.Id(button, id);
        button.Click += (_, _) => toggle();
        return button;
    }

    // MARK: 끌어다 놓기

    Guid? DraggedId(DragEventArgs e) =>
        e.Data.GetDataPresent(DragFormat) && Guid.TryParse(e.Data.GetData(DragFormat) as string, out var id) ? id : null;

    static string? DraggedGroup(DragEventArgs e) => e.Data.GetDataPresent(GroupDragFormat) ? e.Data.GetData(GroupDragFormat) as string : null;

    /// 누른 채로 조금 움직이면 끌기를 시작한다. 안쪽 버튼(실행·수정 등)에서 누르면 시작하지 않는다.
    void DragSource(FrameworkElement source, Func<DataObject> data, bool skipButtons = false)
    {
        Point? start = null;
        source.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = null;
            if (skipButtons)
                for (var d = e.OriginalSource as DependencyObject; d != null && d != source; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                    if (d is System.Windows.Controls.Primitives.ButtonBase) return;
            start = e.GetPosition(source);
        };
        source.PreviewMouseLeftButtonUp += (_, _) => start = null;
        source.PreviewMouseMove += (_, e) =>
        {
            if (start is not Point origin || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(source);
            if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            dragging = true;
            try { DragDrop.DoDragDrop(source, data(), DragDropEffects.Move); }
            finally
            {
                dragging = false;
                if (renderLater) { renderLater = false; Render(); }
            }
        };
    }

    /// 알림을 놓으면 그 그룹 끝으로, 그룹 머리줄을 놓으면 그 그룹 자리로(그룹 없음이면 맨 끝으로) 옮긴다.
    void MakeDropTarget(Border target, string? group)
    {
        target.AllowDrop = true;
        void Mark(bool on)
        {
            target.BorderThickness = new Thickness(on ? 1.5 : 0);
            target.BorderBrush = on ? Theme.Brush("AccentBrush") : null;
            target.Background = on ? Theme.Brush("AccentSoft") : Theme.Brush("CardBg");
        }
        bool Accepts(DragEventArgs e) => DraggedId(e) != null || (DraggedGroup(e) is { } dragged && dragged != group);
        target.DragEnter += (_, e) => { if (Accepts(e)) Mark(true); };
        target.DragOver += (_, e) => { e.Effects = Accepts(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        target.DragLeave += (_, _) => Mark(false);
        target.Drop += (_, e) =>
        {
            Mark(false);
            if (DraggedId(e) is { } id) model.Reorder(id, group, null);
            else if (DraggedGroup(e) is { } dragged) model.MoveGroup(dragged, group);
            e.Handled = true;
        };
    }

    // MARK: 알림 한 줄

    (string Text, bool Failed) Caption(Alarm alarm)
    {
        if (model.AlarmRuns.GetValueOrDefault(alarm.Id) is AlarmRunState.Running) return ("실행 중…", false);
        model.AlarmLog.TryGetValue(alarm.Id, out var record);
        string when;
        if (alarm.Trigger is { } trigger)
        {
            var name = TriggerEvent.Find(trigger.Event)?.Name ?? trigger.Event;
            var last = "";
            if (record != null)
            {
                var isToday = record.LastAt.ToLocalTime().Date == DateTime.Today;
                var time = Clock(record.LastAt);
                last = $" · 마지막 {(isToday ? time : MonthDay(record.LastAt) + " " + time)} {record.Result}";
            }
            when = $"{name} · {trigger.Interval}분마다" + last;
        }
        else
        {
            string last;
            if (record != null) last = $" · 마지막 {MonthDay(record.LastAt)} {record.Result}";
            else if (alarm.LastRunDay is { } day) last = $" · 마지막 {day}";
            else last = "";
            var shell = alarm.ShellCommand is { } command ? " · $ " + Alarm.DisplayCommand(command) : "";
            when = $"{alarm.RepeatRule.Description} {alarm.TimeText}" + shell + last;
        }
        var tokens = record?.Usage is { } usage ? " · " + usage.ShortText : "";
        return (when + tokens, record?.Failed ?? false);
    }

    UIElement Row(Alarm alarm, string? group)
    {
        var open = openRows.Contains(alarm.Id);
        var running = model.AlarmRuns.GetValueOrDefault(alarm.Id) is AlarmRunState.Running;
        var line = Caption(alarm);

        var column = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 4), Opacity = alarm.Enabled ? 1 : 0.5 };
        ChatUi.Id(column, "AlarmRow:" + Title(alarm));

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var kind = alarm.Trigger != null ? "" : alarm.ShellCommand != null ? "" : "";
        var icon = ChatUi.Glyph(kind, "OrangeBrush", 10);
        icon.Margin = new Thickness(0, 0, 5, 0);
        icon.ToolTip = alarm.Trigger != null ? "트리거" : alarm.ShellCommand != null ? "명령 실행" : "정해진 시각";
        titleRow.Children.Add(icon);
        titleRow.Children.Add(Ui.Text(Title(alarm), 13));
        info.Children.Add(titleRow);
        var caption = Ui.Text((alarm.Enabled ? "" : "꺼짐 · ") + line.Text, 11, "TextSecondary");
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        if (line.Failed) caption.Foreground = Brushes.IndianRed;
        caption.Margin = new Thickness(0, 2, 0, 0);
        ChatUi.Id(caption, "AlarmCaption:" + Title(alarm));
        info.Children.Add(caption);
        top.Children.Add(info);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var edit = EditorStyle.GlyphButton("", () => Edit(alarm with { }), "수정", id: "AlarmEdit:" + Title(alarm));
        var delete = EditorStyle.GlyphButton("", () => DeleteAlarm(alarm), "삭제", id: "AlarmDelete:" + Title(alarm));
        delete.Foreground = Brushes.IndianRed;
        var hoverTools = new StackPanel { Orientation = Orientation.Horizontal, Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        hoverTools.Children.Add(edit);
        hoverTools.Children.Add(delete);
        tools.Children.Add(hoverTools);
        if (running)
        {
            var spin = Ui.Text("실행 중", 11, "OrangeBrush");
            spin.VerticalAlignment = VerticalAlignment.Center;
            spin.Margin = new Thickness(4, 0, 4, 0);
            spin.ToolTip = alarm.Trigger == null ? "실행 중이에요. 끝나면 알림 창에 결과가 떠요" : "새로 생긴 게 있는지 보고 있어요";
            tools.Children.Add(spin);
        }
        else
            tools.Children.Add(EditorStyle.GlyphButton("", () => model.RunAlarmNow(alarm), alarm.Trigger == null ? "지금 실행" : "지금 확인", id: "AlarmRun:" + Title(alarm)));
        Grid.SetColumn(tools, 1);
        top.Children.Add(tools);

        var chevron = EditorStyle.GlyphButton(open ? "" : "", () =>
        {
            if (!openRows.Remove(alarm.Id)) openRows.Add(alarm.Id);
            Render();
        }, open ? "접기" : "자세히", id: "AlarmOpen:" + Title(alarm));
        chevron.FontSize = 9;
        chevron.VerticalAlignment = VerticalAlignment.Top;
        chevron.Margin = new Thickness(2, 2, 0, 0);
        Grid.SetColumn(chevron, 2);
        top.Children.Add(chevron);
        column.Children.Add(top);

        if (model.AlarmRuns.GetValueOrDefault(alarm.Id) is AlarmRunState.Done done)
        {
            var result = ChatUi.Label(done.Failed ? "" : "", done.Message, "TextFg", 11);
            foreach (var child in result.Children.OfType<TextBlock>()) child.Foreground = done.Failed ? Brushes.IndianRed : Brushes.SeaGreen;
            result.Margin = new Thickness(4, 4, 0, 0);
            ChatUi.Id(result, "AlarmDone:" + Title(alarm));
            column.Children.Add(result);
        }
        if (open)
        {
            var detail = Details(alarm);
            detail.Margin = new Thickness(4, 4, 0, 0);
            column.Children.Add(detail);
        }

        // 마우스를 올리면 수정·삭제가 보인다
        column.MouseEnter += (_, _) => hoverTools.Visibility = Visibility.Visible;
        column.MouseLeave += (_, _) => { if (!openRows.Contains(alarm.Id)) hoverTools.Visibility = Visibility.Collapsed; };

        // 오른쪽 클릭 메뉴
        var menu = new ContextMenu();
        menu.Items.Add(ChatUi.Item("위로 옮기기", () => model.Shift(alarm.Id, true), enabled: model.CanShift(alarm.Id, true)));
        menu.Items.Add(ChatUi.Item("아래로 옮기기", () => model.Shift(alarm.Id, false), enabled: model.CanShift(alarm.Id, false)));
        column.ContextMenu = menu;

        // 끌어서 옮기기: 다른 알림 위에 놓으면 그 앞으로, 그룹 위에 놓으면 그 그룹 끝으로
        var insert = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = Theme.Brush("OrangeBrush"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, -4, 0, 2) };
        column.Children.Insert(0, insert);
        column.AllowDrop = true;
        column.DragEnter += (_, e) => { if (DraggedId(e) is { } id && id != alarm.Id) insert.Visibility = Visibility.Visible; };
        column.DragOver += (_, e) => { if (DraggedId(e) == null) return; e.Effects = DragDropEffects.Move; e.Handled = true; };
        column.DragLeave += (_, _) => insert.Visibility = Visibility.Collapsed;
        column.Drop += (_, e) =>
        {
            insert.Visibility = Visibility.Collapsed;
            if (DraggedId(e) is not { } id) return;
            if (id != alarm.Id) model.Reorder(id, group, alarm.Id);
            e.Handled = true;
        };
        DragSource(column, () => new DataObject(DragFormat, alarm.Id.ToString()), skipButtons: true);
        return column;
    }

    /// 펼쳤을 때 보이는 상세: 언제, 무엇을, 받을 곳, 마지막 실행. (실행·수정·삭제 버튼은 제목 줄에 있다)
    FrameworkElement Details(Alarm alarm)
    {
        var stack = new StackPanel();
        if (alarm.Trigger is { } trigger)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Text($"트리거 · {SystemNames.Name(trigger.System)} · {TriggerEvent.Find(trigger.Event)?.Name ?? trigger.Event} · {trigger.Interval}분마다", 11));
            row.Children.Add(EditorStyle.Help(AlarmEditor.TriggerHelp));
            stack.Children.Add(row);
        }
        else stack.Children.Add(Ui.Text($"{alarm.TimeText} · {alarm.RepeatRule.Description}", 11));

        var what = alarm.ShellCommand is { } command ? "명령 실행: " + command
            : alarm.Prompt == Alarm.DailyReportPrompt ? "내 미완료 Jira 를 정리해서 보고"
            : alarm.Prompt.Length == 0 ? (alarm.Trigger == null ? "시각에 알림만" : "생기면 바로 알림")
            : alarm.Prompt;
        var content = Ui.Text(what, 11, "TextSecondary", wrap: true, maxLines: 3);
        content.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(content);
        if (model.AlarmLog.GetValueOrDefault(alarm.Id)?.Output is { Length: > 0 } output)
        {
            var text = new TextBox { Text = output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = new FontFamily("Cascadia Mono, Consolas, Malgun Gothic"), FontSize = 10.5, MaxHeight = 80, Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 0) };
            text.SetResourceReference(Control.ForegroundProperty, "TextTertiary");
            ChatUi.Id(text, "AlarmOutput:" + Title(alarm));
            stack.Children.Add(text);
        }
        var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        badges.Children.Add(Badge(alarm.ToMac, "", "Windows 알림"));
        badges.Children.Add(Badge(alarm.ToPanel, "", "미리 알림 창"));
        if (alarm.Trigger != null && alarm.CanWrite) badges.Children.Add(Badge(true, "", "쓰기 허용"));
        if (alarm.LastRunDay is { } last)
        {
            var text = Ui.Text($"마지막 실행 {last}", 10.5, "TextTertiary");
            text.Margin = new Thickness(0, 0, 0, 0);
            badges.Children.Add(text);
        }
        stack.Children.Add(badges);
        return stack;
    }

    /// 받을 곳 표시. 꺼진 곳은 흐리게 줄을 긋는다.
    static UIElement Badge(bool on, string glyph, string text)
    {
        var label = ChatUi.Label(glyph, text, "TextSecondary", 10.5);
        label.Margin = new Thickness(0, 0, 10, 0);
        label.Opacity = on ? 1 : 0.4;
        label.ToolTip = on ? $"{text}으로 받아요" : $"{text}으로는 받지 않아요";
        if (!on) foreach (var child in label.Children.OfType<TextBlock>()) child.TextDecorations = TextDecorations.Strikethrough;
        return label;
    }

    // MARK: 동작

    void SetEnabled(Guid id, bool on)
    {
        if (model.Alarms.FirstOrDefault(a => a.Id == id) is not { } alarm) return;
        alarm.Enabled = on;
        model.Alarms = model.Alarms.ToList();
    }

    void Edit(Alarm alarm)
    {
        try
        {
            var saved = AlarmEditor.Show(alarm, model.Systems, model.AlarmGroups);
            if (saved != null) model.Save(saved);
        }
        catch (Exception e)
        {
            App.Log($"알림 수정 창 오류: {e}");
            throw;
        }
    }

    void DeleteAlarm(Alarm alarm)
    {
        var when = alarm.Trigger == null ? alarm.TimeText + " " : "";
        if (!EditorStyle.Confirm("알림을 삭제할까요?", $"\"{Title(alarm)}\" {when}알림을 지워요. 되돌릴 수 없어요.")) return;
        model.Alarms = model.Alarms.Where(a => a.Id != alarm.Id).ToList();
    }

    void DeleteGroup(string name)
    {
        if (!EditorStyle.Confirm("그룹을 삭제할까요?", $"\"{name}\" 그룹만 지워요. 안에 있던 알림은 지우지 않고 \"그룹 없음\"으로 옮겨요.", "그룹 삭제")) return;
        model.DeleteGroup(name);
    }

    void NewGroup()
    {
        if (AskName("새 그룹", "") is { } name) model.AddGroup(name);
    }

    void RenameGroup(string old)
    {
        if (AskName("그룹 이름 바꾸기", old) is { } name) model.RenameGroup(old, name);
    }

    /// 이름 입력 창 (맥 그룹 이름 alert). 취소하면 null
    static string? AskName(string title, string initial)
    {
        var name = initial;
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(Ui.Text(title, 15, "TextFg", FontWeights.SemiBold));
        Button? save = null;
        var (frame, box) = EditorStyle.TextField(initial, t => { name = t; if (save != null) save.IsEnabled = name.Trim().Length > 0; }, "그룹 이름", id: "GroupName");
        frame.Margin = new Thickness(0, 10, 0, 14);
        body.Children.Add(frame);
        var window = EditorStyle.Dialog(title, 340, body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Pill("취소", () => window.DialogResult = false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        save = ChatUi.Primary("저장", () => window.DialogResult = true, enabled: initial.Trim().Length > 0);
        ChatUi.Id(save, "GroupNameSave");
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        body.Children.Add(buttons);
        ChatUi.Id(cancel, "GroupNameCancel");
        EditorStyle.ButtonKeys(window, new[] { cancel, save }, 1);
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return EditorStyle.ShowModal(window) == true && name.Trim().Length > 0 ? name : null;
    }
}

/// 시스템 이름 표시 (맥 SystemNames)
public static class SystemNames
{
    static readonly Dictionary<string, string> Known = new() { ["github"] = "GitHub", ["deploy"] = "Deploy", ["jira"] = "Jira", ["wiki"] = "Wiki", ["hr"] = "HR" };

    public static string Name(string id) => LocalNames.Current.Name(id) ?? Known.GetValueOrDefault(id) ?? id;
}

/// 알림 추가·수정 창 (맥 AlarmEditor): 언제(시각·반복·트리거), 무엇을(제목·메이트에게 시킬 내용·명령), 받을 곳, 쓰기.
public sealed class AlarmEditor
{
    enum Mode { Time, Trigger }
    enum RepeatKind { Daily, Weekdays, Days, Once }

    public const string TriggerHelp = "켜져 있는 동안 정한 주기(기본 5분)마다 확인해서, 새로 생긴 것만 알려 드려요.\n처음 켤 때 이미 있던 건 알리지 않아요. 바로 보고 싶으면 ▶(지금 확인)을 누르세요.\n내용을 비워 두면 바로 알리고, 내용을 적으면 메이트가 그 일을 한 뒤 결과를 알려요(조금 느림).";

    /// 창을 열고, 저장하면 고친 알림(사본)을, 취소하면 null 을 돌려준다.
    public static Alarm? Show(Alarm alarm, IReadOnlyList<SystemStatus> systems, IReadOnlyList<string> groups)
    {
        var editor = new AlarmEditor(alarm with { }, systems, groups);
        return EditorStyle.ShowModal(editor.window) == true ? editor.result : null;
    }

    readonly Alarm alarm;
    readonly IReadOnlyList<SystemStatus> systems;
    readonly IReadOnlyList<string> groups;
    readonly Window window;
    readonly StackPanel body = new();
    readonly TextBlock heading;
    Button? saveButton;
    Alarm? result;

    Mode mode;
    bool commandAction;
    string command;
    string triggerSystem, triggerEvent;
    int triggerInterval;
    string hour, minute;
    RepeatKind kind;
    HashSet<int> days = new();
    string onceDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    AlarmEditor(Alarm alarm, IReadOnlyList<SystemStatus> systems, IReadOnlyList<string> groups)
    {
        this.alarm = alarm;
        this.systems = systems;
        this.groups = groups;
        mode = alarm.Trigger == null ? Mode.Time : Mode.Trigger;
        commandAction = alarm.ShellCommand != null;
        command = alarm.Command ?? "";
        var system = alarm.Trigger?.System ?? TriggerEvent.Systems[0];
        triggerSystem = system;
        triggerEvent = alarm.Trigger?.Event ?? TriggerEvent.Events(system)[0].Id;
        triggerInterval = alarm.Trigger?.Interval ?? Trigger.DefaultInterval;
        hour = alarm.Hour.ToString("00");
        minute = alarm.Minute.ToString("00");
        switch (alarm.RepeatRule)
        {
            case Alarm.Repeat.Daily: kind = RepeatKind.Daily; break;
            case Alarm.Repeat.Weekdays: kind = RepeatKind.Weekdays; break;
            case Alarm.Repeat.Days d: kind = RepeatKind.Days; days = d.Values.ToHashSet(); break;
            case Alarm.Repeat.Once o: kind = RepeatKind.Once; if (DateTime.TryParseExact(o.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) onceDate = o.Day; break;
        }

        heading = Ui.Text(alarm.Title.Length == 0 && alarm.Prompt.Length == 0 ? "알림 추가" : "알림 수정", 15, "TextFg", FontWeights.SemiBold);
        heading.Margin = new Thickness(20, 16, 20, 10);

        var root = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 16) };
        window = EditorStyle.Dialog("알림", 460, root);
        var cancel = Ui.Pill("취소", () => window.DialogResult = false);
        ChatUi.Id(cancel, "AlarmCancel");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        saveButton = ChatUi.Primary("저장", Save);
        ChatUi.Id(saveButton, "AlarmSave");
        footer.Children.Add(cancel);
        footer.Children.Add(saveButton);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        EditorStyle.ButtonKeys(window, new[] { cancel, saveButton }, 1);

        body.Margin = new Thickness(20, 0, 20, 0);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.Loaded += (_, _) => { if (alarm.Title.Length == 0 && mode == Mode.Trigger) AutoTitle(); };
        Build();
    }

    bool IsDailyReport => alarm.Prompt == Alarm.DailyReportPrompt;

    static string SystemLabel(IReadOnlyList<SystemStatus> systems, string system)
    {
        var connected = systems.FirstOrDefault(s => s.System == system)?.Status == ConnectionState.Connected;
        return SystemNames.Name(system) + (connected ? "" : " (연결 안 됨)");
    }

    // MARK: 만들기

    /// 방식·반복·할 일처럼 모양이 바뀌는 값이 바뀌면 화면을 다시 만든다 (글자 칸은 값을 잃지 않는다)
    void Build()
    {
        body.Children.Clear();

        var when = new List<UIElement>
        {
            EditorStyle.Switch("사용", alarm.Enabled, on => { alarm.Enabled = on; }, id: "AlarmEnabled"),
        };
        var modeRow = new DockPanel();
        var help = EditorStyle.Help(TriggerHelp);
        DockPanel.SetDock(help, Dock.Right);
        modeRow.Children.Add(help);
        modeRow.Children.Add(EditorStyle.Segmented(new[] { ("정해진 시각", Mode.Time), ("트리거", Mode.Trigger) }, mode,
            m => { mode = m; if (mode == Mode.Trigger && alarm.Title.Length == 0) AutoTitle(); Build(); }, "AlarmMode"));
        when.Add(modeRow);
        if (mode == Mode.Trigger)
        {
            when.Add(EditorStyle.Row("시스템", EditorStyle.Picker(TriggerEvent.Systems.Select(s => (SystemLabel(systems, s), s)).ToList(), triggerSystem,
                s => { triggerSystem = s; triggerEvent = TriggerEvent.Events(s)[0].Id; AutoTitle(); Build(); }, "AlarmTriggerSystem")));
            when.Add(EditorStyle.Row("이벤트", EditorStyle.Picker(TriggerEvent.Events(triggerSystem).Select(e => (e.Name, e.Id)).ToList(), triggerEvent,
                e => { triggerEvent = e; AutoTitle(); Build(); }, "AlarmTriggerEvent")));
            when.Add(EditorStyle.Row("확인 주기", EditorStyle.Picker(Trigger.Intervals.Select(i => ($"{i}분마다", i)).ToList(), triggerInterval,
                i => { triggerInterval = i; Build(); }, "AlarmTriggerInterval")));
            if (systems.FirstOrDefault(s => s.System == triggerSystem)?.Status != ConnectionState.Connected)
            {
                var warning = Ui.Text($"{SystemNames.Name(triggerSystem)} 이(가) 연결돼 있지 않아요. 연결 메뉴에서 먼저 연결해 주세요.", 11, "OrangeBrush", wrap: true);
                when.Add(warning);
            }
        }
        body.Children.Add(EditorStyle.Section("언제", when));

        if (mode == Mode.Time) body.Children.Add(EditorStyle.Section("시각과 반복", TimeRows()));

        var deliver = new List<UIElement>
        {
            Wrap(
                EditorStyle.Check("Windows 알림", alarm.ToMac, on => { alarm.NotifyMac = on; Build(); }, "화면 오른쪽 아래에 잠깐 떴다가 알림 센터에 쌓이는 Windows 알림", "AlarmToWindows"),
                EditorStyle.Check("미리 알림 창", alarm.ToPanel, on => { alarm.NotifyPanel = on; Build(); }, "화면 오른쪽 아래에 [확인]을 누를 때까지 남아 있는 메이트 카드", "AlarmToPanel")),
        };
        if (!alarm.ToMac && !alarm.ToPanel)
        {
            var warning = ChatUi.Label("", $"둘 다 끄면 알림이 오지 않아요{(mode == Mode.Time ? " (결과는 채팅창에만 남아요)" : "")}", "OrangeBrush", 11);
            deliver.Add(warning);
        }
        body.Children.Add(EditorStyle.Section("받을 곳", deliver));

        if (mode == Mode.Trigger) body.Children.Add(EditorStyle.Section("쓰기", WriteRows()));
        body.Children.Add(EditorStyle.Section("무엇을", WhatRows()));
        UpdateSave();
    }

    static UIElement Wrap(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            if (child is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 16, 0);
            row.Children.Add(child);
        }
        return row;
    }

    IEnumerable<UIElement> TimeRows()
    {
        var time = new StackPanel { Orientation = Orientation.Horizontal };
        time.Children.Add(TimeBox(hour, 23, t => { hour = t; UpdateSave(); }, "AlarmHour"));
        var colon = Ui.Text(":", 13);
        colon.Margin = new Thickness(4, 0, 4, 0);
        colon.VerticalAlignment = VerticalAlignment.Center;
        time.Children.Add(colon);
        time.Children.Add(TimeBox(minute, 59, t => { minute = t; UpdateSave(); }, "AlarmMinute"));
        yield return EditorStyle.Row("시각", time);
        yield return EditorStyle.Segmented(new[] { ("매일", RepeatKind.Daily), ("평일", RepeatKind.Weekdays), ("요일 선택", RepeatKind.Days), ("한 번", RepeatKind.Once) }, kind,
            k => { kind = k; Build(); }, "AlarmRepeat");
        if (kind == RepeatKind.Days)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var names = new[] { "일", "월", "화", "수", "목", "금", "토" };
            for (var i = 0; i < 7; i++)
            {
                var day = i + 1;
                var toggle = new System.Windows.Controls.Primitives.ToggleButton { Content = names[i], IsChecked = days.Contains(day) };
                toggle.SetResourceReference(FrameworkElement.StyleProperty, "PmToggleButton");
                ChatUi.Id(toggle, "AlarmDay:" + names[i]);
                toggle.Checked += (_, _) => { days.Add(day); UpdateSave(); };
                toggle.Unchecked += (_, _) => { days.Remove(day); UpdateSave(); };
                row.Children.Add(toggle);
            }
            yield return row;
        }
        if (kind == RepeatKind.Once)
            yield return EditorStyle.Row("날짜", EditorStyle.WhiteField(onceDate, t => { onceDate = t; UpdateSave(); }, "yyyy-MM-dd", id: "AlarmOnceDate").With(f => f.Width = 110));
    }

    /// 시·분 칸. 숫자만, 위·아래 화살표로 1씩, 칸을 벗어나면 두 자리로 맞춘다.
    static UIElement TimeBox(string value, int max, Action<string> changed, string id)
    {
        var (frame, box) = EditorStyle.TextField(value, changed, id: id);
        frame.Width = 44;
        box.HorizontalContentAlignment = HorizontalAlignment.Center;
        box.MaxLength = 2;
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            var step = e.Key == Key.Up ? 1 : -1;
            var current = int.TryParse(box.Text, out var v) ? v : 0;
            box.Text = ((current + step + max + 1) % (max + 1)).ToString("00");
            e.Handled = true;
        };
        box.LostFocus += (_, _) => { if (int.TryParse(box.Text, out var v)) box.Text = Math.Clamp(v, 0, max).ToString("00"); };
        return frame;
    }

    IEnumerable<UIElement> WriteRows()
    {
        yield return EditorStyle.Check("쓰기 허용", alarm.CanWrite, on => { alarm.AllowWrite = on; Build(); },
            "켜면 이 알림은 write-tools.json 에 적은 명령만 추가로 실행할 수 있어요", "AlarmAllowWrite");
        if (!alarm.CanWrite) yield break;
        yield return EditorStyle.Row("제한 시간", EditorStyle.Picker(Alarm.TimeoutChoices.Select(m => ($"{m}분", m)).ToList(), alarm.TimeoutMinutes ?? 30,
            m => { alarm.TimeoutMinutes = m == 30 ? null : m; Build(); }, "AlarmTimeout"), "쓰기 알람 한 번이 이 시간을 넘기면 멈춰요");
        yield return EditorStyle.Check("레포별 작업 세션 이어 쓰기", alarm.UseWorkSessions ?? false, on => { alarm.UseWorkSessions = on ? true : null; Build(); },
            "같은 레포의 티켓은 같은 Claude 세션을 이어 써서, 이미 읽은 문서를 다시 읽지 않아요. 위쪽 \"작업\" 탭에서 기록을 보고 말을 걸 수 있어요", "AlarmWorkSessions");
        if (alarm.UseWorkSessions == true && alarm.ReviewsPullRequests)
            yield return EditorStyle.Check("도메인 리뷰어 세션으로 리뷰", alarm.UseDomainReviewer ?? true, on => { alarm.UseDomainReviewer = on ? null : false; Build(); },
                "PR 의 도메인을 정하고(domains.json 의 repos, 없으면 PR 내용으로) 그 도메인의 리뷰어 세션({도메인}:reviewer)에서 지식 저장소·위키·도메인 메모를 읽고 리뷰해요. 끄면 레포 이름 세션에서 리뷰해요", "AlarmDomainReviewer");
        if (alarm.UseWorkSessions == true)
            yield return EditorStyle.Check("역할별 세션으로 나눠 처리 (PM·개발·테스터·리뷰어)", alarm.UseRolePipeline ?? false, on => { alarm.UseRolePipeline = on ? true : null; Build(); },
                "Jira 자동 처리용. 도메인마다 역할 세션 4개가 핑퐁으로 처리하고, push 는 PokeMate 가 직접 해요. 프롬프트는 jira-common·jira-pm·jira-dev·jira-tester·jira-reviewer, 권한은 pipeline-tools.json", "AlarmRolePipeline");
        var tools = ClaudeCommand.LoadWriteTools();
        yield return Ui.Text(tools.Count == 0 ? "write-tools.json 이 비어 있어서 조회만 해요" : "허용: " + string.Join(", ", tools), 11, "TextSecondary", wrap: true, maxLines: 4);
    }

    IEnumerable<UIElement> WhatRows()
    {
        if (groups.Count > 0)
        {
            var options = new List<(string, string?)> { ("그룹 없음", null) };
            options.AddRange(groups.Select(g => (g, (string?)g)));
            yield return EditorStyle.Row("그룹", EditorStyle.Picker(options, alarm.Group, g => { alarm.Group = g; Build(); }, "AlarmGroup"));
        }
        yield return EditorStyle.Labeled("제목", EditorStyle.WhiteField(alarm.Title, t => { alarm.Title = t; UpdateSave(); }, "알림에 보일 이름. 예: 근무일정 확인", id: "AlarmTitle"));
        if (mode == Mode.Time)
            yield return EditorStyle.Row("할 일", EditorStyle.Segmented(new[] { ("메이트에게 시키기", false), ("명령 실행", true) }, commandAction,
                c => { commandAction = c; Build(); }, "AlarmAction"));
        if (mode == Mode.Time && commandAction)
        {
            var stack = new StackPanel();
            stack.Children.Add(EditorStyle.Labeled("명령", EditorStyle.WhiteField(command, t => { command = t; UpdateSave(); }, "예: python ~\\scripts\\check.py", mono: true, id: "AlarmCommand")));
            var note = Ui.Text("Claude 를 부르지 않고 PowerShell(powershell.exe)로 홈 폴더에서 실행해요. 5분이 지나면 멈춰요. 종료 코드 0 이면 성공, 출력 마지막 줄을 알림에 보내요.", 11, "TextSecondary", wrap: true);
            note.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(note);
            yield return stack;
        }
        else if (IsDailyReport)
        {
            var stack = new StackPanel();
            stack.Children.Add(Ui.Text("내 미완료 Jira 를 정리해서 보고 (기본 보고 문구)", 13, "TextSecondary", wrap: true));
            var link = ChatUi.Link("직접 쓰기로 바꾸기", () => { alarm.Prompt = ""; Build(); });
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(link);
            yield return stack;
        }
        else
        {
            var stack = new StackPanel();
            var head = new DockPanel();
            var load = ChatUi.Link("프롬프트 불러오기", () => { });
            ChatUi.Id(load, "AlarmPromptLoad");
            load.FontSize = 11;
            load.Click += (_, _) =>
            {
                var menu = new ContextMenu { PlacementTarget = load, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                var names = Rules.PromptNames();
                if (names.Count == 0) menu.Items.Add(ChatUi.Heading("설정 → 규칙 → prompts 에 파일이 없어요"));
                foreach (var name in names)
                {
                    var captured = name;
                    menu.Items.Add(ChatUi.Item(name, () => { alarm.Prompt = "{{prompt:" + captured + "}}"; Build(); }));
                }
                menu.IsOpen = true;
            };
            DockPanel.SetDock(load, Dock.Right);
            head.Children.Add(load);
            head.Children.Add(Ui.Text("내용", 12, "TextSecondary"));
            stack.Children.Add(head);
            var (frame, _) = EditorStyle.TextField(alarm.Prompt, t => alarm.Prompt = t,
                mode == Mode.Trigger ? "비워두면 알림만. 예: 이 PR 변경 내용 요약해줘" : "비워두면 알림만. 예: 이번 주 내 근무일정 알려줘",
                multiline: true, id: "AlarmPrompt", minLines: 3, maxLines: 6);
            frame.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(frame);
            yield return stack;
        }
    }

    // MARK: 제목 자동 채우기, 저장

    /// 제목이 비었거나 예전에 자동으로 채운 이름이면 고른 이벤트로 다시 채운다.
    void AutoTitle()
    {
        var autoNames = TriggerEvent.All.Select(e => $"{SystemNames.Name(e.System)} · {e.Name}").ToList();
        if (!(alarm.Title.Length == 0 || autoNames.Contains(alarm.Title) || alarm.Title == "GitHub · 리뷰 요청")) return;
        if (TriggerEvent.Find(triggerEvent) is { } found)
            alarm.Title = $"{SystemNames.Name(found.System)} · {found.Name}";
    }

    bool ValidTime() => int.TryParse(hour, out var h) && h is >= 0 and <= 23 && int.TryParse(minute, out var m) && m is >= 0 and <= 59;
    bool ValidDate() => DateTime.TryParseExact(onceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    void UpdateSave()
    {
        if (saveButton == null) return;
        saveButton.IsEnabled = alarm.Title.Trim().Length > 0
            && !(mode == Mode.Time && kind == RepeatKind.Days && days.Count == 0)
            && !(mode == Mode.Time && kind == RepeatKind.Once && !ValidDate())
            && !(mode == Mode.Time && !ValidTime())
            && !(mode == Mode.Time && commandAction && command.Trim().Length == 0);
    }

    void Save()
    {
        if (mode == Mode.Trigger)
        {
            alarm.Trigger = new Trigger(triggerSystem, triggerEvent, triggerInterval);
            alarm.Command = null;
            if (alarm.Prompt == Alarm.DailyReportPrompt) alarm.Prompt = "";
            Finish();
            return;
        }
        alarm.Trigger = null;
        alarm.AllowWrite = null;
        var trimmed = command.Trim();
        alarm.Command = commandAction && trimmed.Length > 0 ? trimmed : null;
        alarm.Hour = int.Parse(hour);
        alarm.Minute = int.Parse(minute);
        switch (kind)
        {
            case RepeatKind.Daily: alarm.RepeatRule = new Alarm.Repeat.Daily(); break;
            case RepeatKind.Weekdays: alarm.RepeatRule = new Alarm.Repeat.Weekdays(); break;
            case RepeatKind.Days: alarm.RepeatRule = new Alarm.Repeat.Days(days.OrderBy(d => d).ToList()); break;
            case RepeatKind.Once:
                var date = DateTime.ParseExact(onceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                alarm.RepeatRule = new Alarm.Repeat.Once(ReportSchedule.DayString(new DateTimeOffset(date)));
                alarm.Enabled = true;
                break;
        }
        // 시각이나 반복을 바꾸면 기록을 새로 잡는다 (오늘 시각이 이미 지났으면 다음부터).
        alarm.ResetSchedule(DateTimeOffset.Now);
        Finish();
    }

    void Finish()
    {
        result = alarm;
        window.DialogResult = true;
    }
}
