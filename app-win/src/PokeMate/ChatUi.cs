using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PokeMate;

/// 대화 화면과 카드가 같이 쓰는 작은 도우미
public static class ChatUi
{
    public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static TextBlock Glyph(string glyph, string brush = "TextSecondary", double size = 12)
    {
        var text = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return text;
    }

    /// 아이콘 + 글자 한 줄 (맥 Label)
    public static StackPanel Label(string glyph, string text, string brush = "TextFg", double size = 13, FontWeight? weight = null, string? glyphBrush = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Glyph(glyph, glyphBrush ?? brush, size - 1);
        icon.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(icon);
        var label = Ui.Text(text, size, brush, weight);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        return row;
    }

    public static T Id<T>(T element, string id) where T : DependencyObject
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
        return element;
    }

    public static Button Link(string text, Action click)
    {
        var button = new Button { Content = text, FontSize = 12 };
        button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Primary(string text, Action click, bool enabled = true)
    {
        var button = new Button { Content = text, IsEnabled = enabled };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    /// 입력 카드(흰 바탕 + 옅은 테두리). 맥 `textBackgroundColor` + stroke
    public static Border Box(UIElement child, double padding = 10, double radius = 10)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "FieldBg");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        return border;
    }

    /// 주황색 옅은 바탕 카드 (승인 카드, 보낼 곳 제안)
    public static Border Tint(UIElement child, double alpha, double padding, double radius, double strokeAlpha = 0)
    {
        var orange = (Color)ColorConverter.ConvertFromString("#FFFF9500");
        return new Border
        {
            Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius),
            Background = Ui.Alpha(orange, alpha),
            BorderBrush = strokeAlpha > 0 ? Ui.Alpha(orange, strokeAlpha) : null, BorderThickness = new Thickness(strokeAlpha > 0 ? 1 : 0),
        };
    }


    // MARK: 메뉴

    public static ContextMenu NewMenu(FrameworkElement anchor) => new()
    {
        PlacementTarget = anchor,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
    };

    public static MenuItem Item(string header, Action click, bool check = false, bool enabled = true, string? foreground = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = check };
        item.SetResourceReference(Control.ForegroundProperty, foreground ?? "TextFg");
        item.Click += (_, _) => click();
        return item;
    }

    public static MenuItem Heading(string text)
    {
        var item = new MenuItem { Header = text, IsEnabled = false };
        item.SetResourceReference(Control.ForegroundProperty, "TextTertiary");
        return item;
    }

    // MARK: 이미지

    /// 첨부 이미지 미리보기. 누르면 기본 이미지 뷰어로 크게 연다 (맥 Thumbnail)
    public static FrameworkElement Thumbnail(string path, double size)
    {
        var box = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "눌러서 크게 보기" };
        box.BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0));
        box.Clip = new RectangleGeometry(new Rect(0, 0, size, size), 8, 8);
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = (int)(size * 2);
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            box.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch { box.Child = Glyph("", "TextSecondary", size / 3); ((TextBlock)box.Child).HorizontalAlignment = HorizontalAlignment.Center; }
        box.MouseLeftButtonUp += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { App.Log($"이미지를 열지 못했어요: {e.Message}"); }
        };
        return box;
    }
}
