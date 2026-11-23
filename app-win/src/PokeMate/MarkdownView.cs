using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PokeMate;

/// 대화 글을 화면 요소로 바꾼다 (맥 MarkdownContent). 표가 있으면 글과 표를 나눠 놓고, 표는 칸 너비에 맞춰 줄여 보여 준다.
/// 맥의 `Text(LocalizedStringKey)` 처럼 줄 안 서식(굵게·기울임·코드·취소선·링크)만 읽고, 제목·목록 기호는 글자 그대로 둔다.
public sealed class MarkdownContent : StackPanel
{
    public MarkdownContent(string text, double fontSize = 13)
    {
        if (MarkdownBlocks.HasTable(text))
        {
            foreach (var block in MarkdownBlocks.Split(text))
            {
                switch (block)
                {
                    case MarkdownBlock.Text t: Children.Add(Spacing(MarkdownInline.Block(t.Value, fontSize))); break;
                    case MarkdownBlock.Table table: Children.Add(Spacing(new MarkdownTableView(table.Value, fontSize))); break;
                }
            }
        }
        else Children.Add(MarkdownInline.Block(text, fontSize));
    }

    static T Spacing<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 0, 0, 8);
        return element;
    }
}

/// 줄 안 마크다운 → TextBlock
public static class MarkdownInline
{
    static readonly Regex Token = new(
        @"\*\*(?<bold>.+?)\*\*" +
        @"|`(?<code>[^`\n]+)`" +
        @"|\[(?<label>[^\]\n]+)\]\((?<url>https?://[^)\s]+)\)" +
        @"|~~(?<strike>.+?)~~" +
        @"|(?<![\w*])\*(?<italic>[^*\n]+?)\*(?![\w*])" +
        @"|(?<![\w_])_(?<italic2>[^_\n]+?)_(?![\w_])",
        RegexOptions.Singleline);

    public static TextBlock Block(string text, double fontSize = 13, bool bold = false)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = fontSize };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFg");
        if (bold) block.FontWeight = FontWeights.SemiBold;
        Fill(block.Inlines, text, fontSize);
        return block;
    }

    public static void Fill(InlineCollection inlines, string text, double fontSize, FontWeight? weight = null, FontStyle? style = null, TextDecorationCollection? decorations = null)
    {
        var position = 0;
        foreach (Match m in Token.Matches(text))
        {
            if (m.Index > position) inlines.Add(Plain(text[position..m.Index], weight, style, decorations));
            position = m.Index + m.Length;
            if (m.Groups["bold"].Success)
            {
                var span = new Span();
                Fill(span.Inlines, m.Groups["bold"].Value, fontSize, FontWeights.Bold, style, decorations);
                inlines.Add(span);
            }
            else if (m.Groups["code"].Success)
            {
                var run = new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = fontSize - 1 };
                run.SetResourceReference(TextElement.BackgroundProperty, "HoverBg");
                inlines.Add(run);
            }
            else if (m.Groups["label"].Success)
            {
                var link = new Hyperlink { NavigateUri = new Uri(m.Groups["url"].Value, UriKind.Absolute) };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                link.RequestNavigate += (_, e) => Open(e.Uri.AbsoluteUri);
                Fill(link.Inlines, m.Groups["label"].Value, fontSize, weight, style, decorations);
                inlines.Add(link);
            }
            else if (m.Groups["strike"].Success)
            {
                var span = new Span();
                Fill(span.Inlines, m.Groups["strike"].Value, fontSize, weight, style, TextDecorations.Strikethrough);
                inlines.Add(span);
            }
            else
            {
                var inner = m.Groups["italic"].Success ? m.Groups["italic"].Value : m.Groups["italic2"].Value;
                var span = new Span();
                Fill(span.Inlines, inner, fontSize, weight, FontStyles.Italic, decorations);
                inlines.Add(span);
            }
        }
        if (position < text.Length) inlines.Add(Plain(text[position..], weight, style, decorations));
    }

    static Run Plain(string text, FontWeight? weight, FontStyle? style, TextDecorationCollection? decorations)
    {
        var run = new Run(Unescape(text));
        if (weight is FontWeight w) run.FontWeight = w;
        if (style is FontStyle s) run.FontStyle = s;
        if (decorations != null) run.TextDecorations = decorations;
        return run;
    }

    /// `\~` `\*` 같은 마크다운 이스케이프를 글자로 되돌린다
    static string Unescape(string text) => text.Contains('\\') ? Regex.Replace(text, @"\\([*_~`\[\]])", "$1") : text;

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { App.Log($"링크를 열지 못했어요: {e.Message}"); }
    }
}

/// 표 칸 너비 재기 (맥 TableMeasure)
public static class TableMeasure
{
    static readonly Typeface Regular = new(new FontFamily("Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static double Measure(string text, bool bold, double fontSize = 13)
    {
        var plain = text.Replace("**", "").Replace("`", "");
        var formatted = new FormattedText(plain, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? Bold : Regular, fontSize,
                                          Brushes.Black, 1.0);
        return Math.Min(320, formatted.WidthIncludingTrailingWhitespace + 4);
    }

    public static List<double> Natural(MarkdownTable table, double fontSize = 13) =>
        table.Header.Select((_, i) =>
        {
            var cells = table.Rows.Where(r => i < r.Count).Select(r => r[i]);
            return new[] { Measure(table.Header[i], true, fontSize) }.Concat(cells.Select(c => Measure(c, false, fontSize))).Max();
        }).ToList();
}

/// 표 한 줄 (맥 TableLine)
public static class TableLine
{
    public const double Spacing = 12;

    public static Grid Make(MarkdownTable table, IReadOnlyList<string> cells, IReadOnlyList<double> widths, bool bold, double fontSize, string? more = null)
    {
        var grid = new Grid();
        for (var i = 0; i < widths.Count; i++)
        {
            if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Spacing) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[i]) });
            var text = TableLayout.Breakable(i < cells.Count ? cells[i] : "");
            var block = MarkdownInline.Block(text, fontSize, bold);
            block.TextAlignment = table.Aligns.ElementAtOrDefault(i) switch
            {
                MarkdownTable.Align.Center => TextAlignment.Center,
                MarkdownTable.Align.Trailing => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            Grid.SetColumn(block, i * 2);
            grid.Children.Add(block);
        }
        if (more != null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Spacing) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TablePreview.MoreColumnWidth) });
            var note = Ui.Text(more, 11, "TextSecondary");
            Grid.SetColumn(note, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(note);
        }
        return grid;
    }

    public static Border Divider(double opacity = 1)
    {
        var line = new Border { Height = 1, Opacity = opacity, Margin = new Thickness(0, 6, 0, 6) };
        line.SetResourceReference(Border.BackgroundProperty, "Line");
        return line;
    }
}

/// 대화 안의 표 미리보기 (맥 MarkdownTableView). 칸이 모자라면 열·행을 줄이고 [크게 보기] 버튼을 단다.
public sealed class MarkdownTableView : Grid
{
    readonly MarkdownTable table;
    readonly double fontSize;
    readonly List<double> natural;
    double width;

    public MarkdownTableView(MarkdownTable table, double fontSize = 13)
    {
        this.table = table;
        this.fontSize = fontSize;
        natural = TableMeasure.Natural(table, fontSize);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ClipToBounds = true;
        SizeChanged += (_, e) =>
        {
            var w = Math.Floor(e.NewSize.Width);
            if (Math.Abs(w - width) < 1) return;
            width = w;
            Rebuild();
        };
        Children.Add(new Border { Height = 1 });
    }

    void Rebuild()
    {
        Children.Clear();
        if (width <= 0) { Children.Add(new Border { Height = 1 }); return; }
        var preview = TablePreview.Make(natural, table.Rows.Count, width, TableLine.Spacing);
        var more = preview.HiddenColumns > 0;
        var body = new StackPanel();
        body.Children.Add(TableLine.Make(table, table.Header, preview.Widths, true, fontSize, more ? $"+{preview.HiddenColumns}열" : null));
        body.Children.Add(TableLine.Divider());
        for (var r = 0; r < preview.Rows; r++)
        {
            body.Children.Add(TableLine.Make(table, table.Rows[r], preview.Widths, false, fontSize, more ? "" : null));
            if (r < preview.Rows - 1) body.Children.Add(TableLine.Divider(0.5));
        }
        if (preview.HiddenRows > 0) body.Children.Add(Ui.Text($"+{preview.HiddenRows}행", 11, "TextSecondary"));
        Children.Add(body);
        if (preview.Truncated)
        {
            var expand = new Button
            {
                Content = "", ToolTip = "표 크게 보기", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -2, 0), FontSize = 11,
            };
            expand.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            System.Windows.Automation.AutomationProperties.SetName(expand, "표 크게 보기");
            System.Windows.Automation.AutomationProperties.SetAutomationId(expand, "ExpandTable");
            expand.Click += (_, _) => TableWindow.Show(table);
            Children.Add(expand);
        }
    }
}
