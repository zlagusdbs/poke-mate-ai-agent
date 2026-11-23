using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PokeMate;

/// 홈 탭 (맥 HomeView): 키우는 포켓몬 카드, 오늘 토큰, 그 아래 대화 자리.
public partial class HomeView : UserControl
{
    public HomeView(AppModel model)
    {
        InitializeComponent();
        MateHost.Child = new MateCard();
        HeaderHost.Child = new HomeHeader();
        if (Extensions.HomeChat() is { } chat)
        {
            try { ChatHost.Child = chat(model); }
            catch (Exception e) { App.Log($"대화 화면을 만들지 못했어요: {e}"); }
        }
    }
}

/// 오늘·이번 주·이번 달 토큰과 일별 그래프 (맥 HomeHeader). 눌러서 그래프를 접고 편다.
public sealed class HomeHeader : Border
{
    readonly UsageStore usage = UsageStore.Shared;
    readonly DailyUsageChart chart = new();
    readonly StackPanel body = new();
    bool showChart = Prefs.Bool("showDailyChart", true);

    public HomeHeader()
    {
        Child = body;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ToolTip = "";
        MouseLeftButtonUp += (_, _) => Toggle();
        // 탭을 바꿔 화면에서 빠졌다가 돌아와도 이어서 갱신되도록 Loaded 에서 걸고 Unloaded 에서 푼다
        Loaded += (_, _) =>
        {
            usage.PropertyChanged -= OnChanged;
            usage.PropertyChanged += OnChanged;
            usage.Start();
            Update();
        };
        Unloaded += (_, _) => usage.PropertyChanged -= OnChanged;
        Update();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageStore.Summary) or nameof(UsageStore.Loading)) Update();
    }

    void Toggle()
    {
        showChart = !showChart;
        Prefs.Set("showDailyChart", showChart);
        Update();
    }

    static string Grouped(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    void Update()
    {
        var s = usage.Summary;
        ToolTip = showChart ? "눌러서 일별 그래프 접기" : "눌러서 일별 그래프 보기";
        body.Children.Clear();

        var title = new Grid();
        title.Children.Add(Ui.Text("오늘 사용한 토큰", 12, "TextSecondary"));
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (usage.Loading) icons.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 28, Height = 3, Margin = new Thickness(0, 0, 8, 0) });
        icons.Children.Add(Glyph("", showChart ? "OrangeBrush" : "TextSecondary", 12));
        icons.Children.Add(Glyph(showChart ? "" : "", "TextSecondary", 10, new Thickness(6, 0, 0, 0)));
        title.Children.Add(icons);
        body.Children.Add(title);

        var big = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        big.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        big.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        big.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var total = Ui.Text(TokenFormat.Compact(s.Today.Total), 26, "TextFg", FontWeights.Bold);
        var exact = Ui.Text(Grouped(s.Today.Total), 12, "TextSecondary");
        exact.Margin = new Thickness(8, 0, 0, 5);
        exact.VerticalAlignment = VerticalAlignment.Bottom;
        var cost = Ui.Text(TokenFormat.Usd(s.TodayCost), 13, "TextSecondary");
        cost.ToolTip = "모델 단가로 환산한 추정 비용. 구독료·실제 청구액이 아님";
        cost.HorizontalAlignment = HorizontalAlignment.Right;
        cost.VerticalAlignment = VerticalAlignment.Bottom;
        cost.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetColumn(exact, 1);
        Grid.SetColumn(cost, 2);
        big.Children.Add(total);
        big.Children.Add(exact);
        big.Children.Add(cost);
        body.Children.Add(big);

        var periods = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        periods.Children.Add(Ui.Text($"이번 주 {TokenFormat.Compact(s.Week.Total)} · {TokenFormat.Usd(s.WeekCost)}", 12, "TextSecondary"));
        var month = Ui.Text($"이번 달 {TokenFormat.Compact(s.Month.Total)} · {TokenFormat.Usd(s.MonthCost)}", 12, "TextSecondary");
        month.Margin = new Thickness(12, 0, 0, 0);
        periods.Children.Add(month);
        body.Children.Add(periods);

        if (s.TodayByModel.Count > 0)
        {
            var models = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var row in s.TodayByModel.Take(3))
            {
                var text = Ui.Text($"{row.Model.Replace("claude-", "")} {TokenFormat.Compact(row.Bucket.Total)}", 11, "TextTertiary");
                text.Margin = new Thickness(0, 0, 10, 0);
                models.Children.Add(text);
            }
            body.Children.Add(models);
        }
        if (s.UnpricedModels.Count > 0)
            body.Children.Add(WithTop(Ui.Text($"단가 없는 모델은 비용에서 빠짐: {string.Join(", ", s.UnpricedModels)}", 11, "TextTertiary"), 6));

        if (showChart)
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
            line.SetResourceReference(Border.BackgroundProperty, "Line");
            body.Children.Add(line);
            chart.SetDays(s.Daily);
            body.Children.Add(chart);
        }
    }

    static TextBlock Glyph(string glyph, string brush, double size, Thickness? margin = null)
    {
        var text = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (margin is { } m) text.Margin = m;
        return text;
    }

    static T WithTop<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, top, 0, 0);
        return element;
    }
}
