using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PokeMate;

/// 코드로 화면을 짤 때 쓰는 작은 도우미. 색을 테마 키로 걸어 두면 어두운 모드로 바뀔 때 같이 바뀐다.
public static class Ui
{
    public static TextBlock Text(string text, double size = 13, string fg = "TextFg", FontWeight? weight = null,
                                 bool wrap = false, int maxLines = 0)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, fg);
        if (weight is FontWeight w) block.FontWeight = w;
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        if (maxLines > 0 && wrap) block.MaxHeight = size * 1.45 * maxLines;
        return block;
    }

    /// 카드(둥근 모서리, 옅은 바탕). 맥 pmCard
    public static Border Card(UIElement child, double padding = 12)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), HorizontalAlignment = HorizontalAlignment.Stretch };
        border.SetResourceReference(FrameworkElement.StyleProperty, "PmCard");
        return border;
    }

    public static Border Capsule(UIElement child, Brush background, Thickness? padding = null, Brush? stroke = null) => new()
    {
        Child = child, Background = background, CornerRadius = new CornerRadius(20),
        Padding = padding ?? new Thickness(6, 1, 6, 1),
        BorderBrush = stroke, BorderThickness = new Thickness(stroke == null ? 0 : 1),
    };

    public static Brush Alpha(Color color, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static Border Progress(double fraction, string fill = "OrangeBrush", double height = 6)
    {
        fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        var bar = new Border { CornerRadius = new CornerRadius(height / 2) };
        bar.SetResourceReference(Border.BackgroundProperty, fill);
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);
        var track = new Border { Child = grid, CornerRadius = new CornerRadius(height / 2), ClipToBounds = true };
        track.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        return track;
    }

    public static Button Pill(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(0), ToolTip = tip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        button.Click += (_, _) => click();
        return button;
    }

    /// 한 번 더 그리는 것을 한 번으로 합친다 (스프라이트가 여러 장 내려올 때 등)
    public sealed class Debounce
    {
        readonly System.Windows.Threading.DispatcherTimer timer;

        public Debounce(Action action, int milliseconds = 40)
        {
            timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
        }

        public void Poke() { if (!timer.IsEnabled) timer.Start(); }
        public void Cancel() => timer.Stop();
    }
}
