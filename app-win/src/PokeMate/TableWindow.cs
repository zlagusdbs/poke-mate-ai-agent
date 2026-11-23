using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PokeMate;

/// 표 크게 보기 창 (맥 TableWindowController + TableFullView). 헤더 줄은 위에 고정하고, 가로·세로로 스크롤한다.
public sealed class TableWindow : Window
{
    static readonly List<TableWindow> Open = new();

    readonly MarkdownTable table;
    readonly TextBlock copied;

    public static void Show(MarkdownTable table)
    {
        var window = new TableWindow(table);
        Open.Add(window);
        PopoverWindow.Current?.Hold();
        window.Closed += (_, _) =>
        {
            Open.Remove(window);
            PopoverWindow.Current?.Release();
        };
        window.Show();
        window.Activate();
    }

    TableWindow(MarkdownTable table)
    {
        this.table = table;
        var natural = TableMeasure.Natural(table).Select(w => Math.Max(w, TableLayout.MinColumn)).ToList();
        var contentWidth = natural.Sum() + TableLine.Spacing * Math.Max(0, natural.Count - 1) + 48;
        var contentHeight = (table.Rows.Count + 1) * 30 + 110;
        var area = SystemParameters.WorkArea;
        Title = "표 크게 보기";
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = Math.Min(Math.Max(contentWidth, 420), area.Width * 0.8);
        Height = Math.Min(Math.Max(contentHeight, 220), area.Height * 0.8);
        MinWidth = 320;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        MinHeight = 180;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(TextElement.ForegroundProperty, "TextFg");

        var root = new Grid { Margin = new Thickness(Theme.Gutter) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = Ui.Pill("닫기", Close);
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        var copy = Ui.Pill("복사", () => { });
        copy.Margin = new Thickness(0, 0, 8, 0);
        copy.Click += (_, _) => ShowCopyMenu(copy);
        DockPanel.SetDock(copy, Dock.Right);
        bar.Children.Add(copy);
        copied = Ui.Text("", 11, "TextSecondary");
        copied.Margin = new Thickness(0, 0, 8, 0);
        copied.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(copied, Dock.Right);
        bar.Children.Add(copied);
        var count = Ui.Text($"{table.Header.Count}열 · {table.Rows.Count}행", 11, "TextSecondary");
        count.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(count);
        root.Children.Add(bar);

        // 헤더 줄은 세로로 안 움직이게 따로 두고, 가로 스크롤만 본문과 맞춘다
        var card = new Border { Padding = new Thickness(10) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "PmCard");
        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        header.Children.Add(TableLine.Make(table, table.Header, natural, true, 13));
        header.Children.Add(TableLine.Divider());
        var headerScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = header };
        inner.Children.Add(headerScroll);
        var body = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        for (var r = 0; r < table.Rows.Count; r++)
        {
            body.Children.Add(TableLine.Make(table, table.Rows[r], natural, false, 13));
            if (r < table.Rows.Count - 1) body.Children.Add(TableLine.Divider(0.5));
        }
        var bodyScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        bodyScroll.ScrollChanged += (_, e) => headerScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        Grid.SetRow(bodyScroll, 1);
        inner.Children.Add(bodyScroll);
        card.Child = inner;
        Grid.SetRow(card, 1);
        root.Children.Add(card);
        Content = root;
    }

    void ShowCopyMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item("마크다운 표", () => Copy(table.Markdown, "마크다운 표를 복사했어요")));
        menu.Items.Add(Item("탭 구분 (엑셀에 붙이기)", () => Copy(table.TabSeparated, "탭 구분 글을 복사했어요")));
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.SetResourceReference(Control.ForegroundProperty, "TextFg");
        item.Click += (_, _) => click();
        return item;
    }

    void Copy(string text, string message)
    {
        try { Clipboard.SetText(text); copied.Text = message; }
        catch (Exception e) { copied.Text = $"복사하지 못했어요: {e.Message}"; }
    }
}
