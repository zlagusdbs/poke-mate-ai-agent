using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PokeMate;

/// 일별 토큰 막대 그래프 (맥 UsageChart.swift). 7·14·30일 선택, 막대에 마우스를 올리면 그날 숫자를 보여 준다.
public sealed class DailyUsageChart : StackPanel
{
    static readonly CultureInfo Korean = new("ko-KR");

    sealed record Point(DailyUsage Usage, DateTime Date);

    readonly BarChart chart = new() { Height = 120 };
    readonly TextBlock summary = Ui.Text("", 12, "TextSecondary");
    readonly TextBlock focusDate = Ui.Text("", 12, "TextFg", FontWeights.SemiBold);
    readonly TextBlock focusTotal = Ui.Text("", 12);
    readonly TextBlock focusModels = Ui.Text("", 12, "TextTertiary");
    readonly StackPanel focusRow = new() { Orientation = Orientation.Horizontal };
    IReadOnlyList<DailyUsage> days = Array.Empty<DailyUsage>();
    int range = 14;
    List<Point> points = new();

    public DailyUsageChart()
    {
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var n in new[] { 7, 14, 30 })
        {
            var button = new RadioButton { Content = $"{n}일", GroupName = "usageRange", IsChecked = n == range, Padding = new Thickness(0) };
            button.SetResourceReference(StyleProperty, "SegmentButton");
            var captured = n;
            button.Checked += (_, _) => { range = captured; Rebuild(); };
            segments.Children.Add(button);
        }
        var segmentCard = new Border { Child = segments, Padding = new Thickness(1), CornerRadius = new CornerRadius(7) };
        segmentCard.SetResourceReference(Border.StyleProperty, "PmCard");
        segmentCard.CornerRadius = new CornerRadius(7);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.HorizontalAlignment = HorizontalAlignment.Right;
        summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(summary, 1);
        header.Children.Add(segmentCard);
        header.Children.Add(summary);

        focusDate.Margin = new Thickness(0, 0, 6, 0);
        focusTotal.Margin = new Thickness(0, 0, 6, 0);
        focusRow.Children.Add(focusDate);
        focusRow.Children.Add(focusTotal);
        focusRow.Children.Add(focusModels);
        focusRow.Margin = new Thickness(0, 8, 0, 8);

        Children.Add(header);
        Children.Add(focusRow);
        Children.Add(chart);
        chart.HoverChanged += UpdateFocus;
        // 그래프 위에서 누르는 것은 카드 접기로 넘기지 않는다
        MouseLeftButtonUp += (_, e) => e.Handled = true;
    }

    public void SetDays(IReadOnlyList<DailyUsage> value)
    {
        days = value;
        Rebuild();
    }

    void Rebuild()
    {
        points = days.TakeLast(range)
            .Select(u => DateTime.TryParseExact(u.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? new Point(u, date) : null)
            .OfType<Point>().ToList();
        long total = points.Sum(p => p.Usage.Total);
        var cost = points.Sum(p => p.Usage.Cost);
        summary.Text = $"합계 {TokenFormat.Compact(total)} · {TokenFormat.Usd(cost)}  하루 평균 {TokenFormat.Compact(points.Count == 0 ? 0 : total / points.Count)}";
        chart.Set(points.Select(p => (p.Date, p.Usage.Total)).ToList(), range);
        UpdateFocus();
    }

    void UpdateFocus()
    {
        var index = chart.Hovered >= 0 && chart.Hovered < points.Count ? chart.Hovered : points.Count - 1;
        chart.FocusIndex = index;
        if (index < 0)
        {
            focusRow.Visibility = Visibility.Collapsed;
            return;
        }
        focusRow.Visibility = Visibility.Visible;
        var point = points[index];
        focusDate.Text = point.Date.ToString("M월 d일 (ddd)", Korean);
        focusTotal.Text = $"{TokenFormat.Compact(point.Usage.Total)} · {TokenFormat.Usd(point.Usage.Cost)}";
        focusModels.Text = string.Join("  ", point.Usage.ByModel.OrderByDescending(m => m.Value).Take(3)
            .Select(m => $"{m.Key.Replace("claude-", "")} {TokenFormat.Compact(m.Value)}"));
    }

    /// 막대 그림 자체
    sealed class BarChart : FrameworkElement
    {
        List<(DateTime Date, long Total)> bars = new();
        int range = 14;

        public int Hovered { get; private set; } = -1;
        int focusIndex = -1;
        public int FocusIndex { get => focusIndex; set { if (focusIndex == value) return; focusIndex = value; InvalidateVisual(); } }
        public event Action? HoverChanged;

        public void Set(List<(DateTime, long)> value, int days)
        {
            bars = value;
            range = days;
            InvalidateVisual();
        }

        const double Left = 40, Bottom = 16, Top = 6;

        double Slot => bars.Count == 0 ? 1 : Math.Max(1, (ActualWidth - Left) / bars.Count);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var x = e.GetPosition(this).X - Left;
            var index = bars.Count == 0 || x < 0 ? -1 : Math.Min(bars.Count - 1, (int)(x / Slot));
            if (index == Hovered) return;
            Hovered = index;
            HoverChanged?.Invoke();
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (Hovered < 0) return;
            Hovered = -1;
            HoverChanged?.Invoke();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            // 마우스가 닿는 면 (투명한 바탕)
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            var width = ActualWidth - Left;
            var height = ActualHeight - Bottom - Top;
            if (width <= 0 || height <= 0) return;

            var max = Math.Max(1L, bars.Count == 0 ? 1 : bars.Max(b => b.Total));
            var step = NiceStep(max / 3.0);
            var top = Math.Max(step, Math.Ceiling(max / step) * step);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var line = new Pen(Theme.Brush("Line"), 1);
            var label = Theme.Brush("TextTertiary");
            for (var value = 0.0; value <= top + 0.001; value += step)
            {
                var y = Top + height - height * value / top;
                dc.DrawLine(line, new System.Windows.Point(Left, Math.Round(y) + 0.5), new System.Windows.Point(ActualWidth, Math.Round(y) + 0.5));
                var text = Format(TokenFormat.Compact((long)value), 10, label, dpi);
                dc.DrawText(text, new System.Windows.Point(Left - 6 - text.Width, y - text.Height / 2));
            }

            var slot = Slot;
            var barWidth = Math.Max(2, slot * 0.72);
            var focus = FocusIndex;
            for (var i = 0; i < bars.Count; i++)
            {
                var h = height * bars[i].Total / top;
                var brush = i == focus ? Theme.Brush("OrangeBrush") : Ui.Alpha(Color.FromRgb(0xFF, 0x95, 0x00), 0.45);
                dc.DrawRoundedRectangle(brush, null, new Rect(Left + i * slot + (slot - barWidth) / 2, Top + height - h, barWidth, Math.Max(0, h)), 2, 2);
            }

            var stride = range > 14 ? 7 : range > 7 ? 2 : 1;
            for (var i = 0; i < bars.Count; i += stride)
            {
                var text = Format($"{bars[i].Date.Month}/{bars[i].Date.Day}", 10, label, dpi);
                dc.DrawText(text, new System.Windows.Point(Left + i * slot + (slot - text.Width) / 2, Top + height + 2));
            }
        }

        static FormattedText Format(string text, double size, Brush brush, double dpi) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);

        /// 1·2·5 × 10^n 중 value 이상인 가장 작은 눈금 간격
        static double NiceStep(double value)
        {
            if (value <= 1) return 1;
            var pow = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
                if (m * pow >= value) return m * pow;
            return 10 * pow;
        }
    }
}
