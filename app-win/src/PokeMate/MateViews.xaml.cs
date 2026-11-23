using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PokeMate;

public static class MateRarityColors
{
    /// 희귀도 색 (맥 MateRarity.color: 주황·파랑·초록·회색)
    public static Color Of(this MateRarity rarity) => rarity switch
    {
        MateRarity.Legendary => Color.FromRgb(0xFF, 0x95, 0x00),
        MateRarity.Rare => Color.FromRgb(0x0A, 0x84, 0xFF),
        MateRarity.Uncommon => Color.FromRgb(0x34, 0xC7, 0x59),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    public static Brush Brush(this MateRarity rarity) => Ui.Alpha(rarity.Of(), 1);
}

/// 홈 맨 위의 키우는 포켓몬 카드 (맥 MateCard)
public sealed class MateCard : Border
{
    readonly UsageStore usage = UsageStore.Shared;
    readonly Ui.Debounce redraw;

    public MateCard()
    {
        redraw = new Ui.Debounce(Update, 20);
        Loaded += (_, _) =>
        {
            usage.PropertyChanged -= OnChanged;
            usage.PropertyChanged += OnChanged;
            Update();
        };
        Unloaded += (_, _) => usage.PropertyChanged -= OnChanged;
        Update();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageStore.Mate) or nameof(UsageStore.MateMessage) or nameof(UsageStore.AllSpecies)
            or nameof(UsageStore.PokedexError) or nameof(UsageStore.PokedexProgress) or nameof(UsageStore.Range))
            redraw.Poke();
    }

    static Border SpriteBox(UIElement sprite) => new()
    {
        Width = 84, Height = 84, CornerRadius = new CornerRadius(12), Child = sprite,
        VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.FindResource("CardBg"),
    };

    void Update()
    {
        var state = usage.Mate;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);

        if (state.Current is { } current && usage.Species(current.Species) is { } species)
        {
            var tokens = state.Tokens;
            var count = species.Path(current.Form).Count;
            var step = MateRules.Step(tokens, count);
            var form = species.ShownForm(tokens, current.Form, current.Shown);
            var live = species.Form(tokens, current.Form);

            var sprite = new MateSprite(species, tokens, current.Form, current.Shown, 72) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(SpriteBox(sprite));

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(Ui.Text(form?.Name ?? "???의 알", 15, "TextFg", FontWeights.Bold));
            if (form != null)
            {
                var number = Ui.Text($"#{form.Id}", 12, "TextSecondary");
                number.Margin = new Thickness(6, 0, 0, 0);
                number.VerticalAlignment = VerticalAlignment.Bottom;
                title.Children.Add(number);
            }
            var label = Ui.Text(MateRules.Label(step), 11, "TextFg", FontWeights.SemiBold);
            var capsule = Ui.Capsule(label, Theme.Brush("AccentSoft"));
            capsule.Margin = new Thickness(6, 0, 0, 0);
            capsule.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(capsule);
            text.Children.Add(title);

            if (live != null && form != null && live.Id != form.Id)
                text.Children.Add(Spaced(Ui.Text($"키우는 모습: {live.Name} · 컬렉션에서 고른 모습으로 보여요", 11, "TextSecondary", wrap: true), 4));
            text.Children.Add(Spaced(Ui.Progress(MateRules.StageProgress(tokens, count)), 4));
            var remain = TokenFormat.Compact(Math.Max(0, MateRules.NextGoal(tokens, count) - tokens));
            text.Children.Add(Spaced(Ui.Text($"{MateRules.GoalLabel(tokens, count)}까지 {remain}", 12, "TextSecondary"), 4));
            text.Children.Add(Spaced(Ui.Text(usage.MateMessage ?? form?.Flavor ?? "토큰을 쓰면 알이 따뜻해져요.", 12, "TextSecondary", wrap: true, maxLines: 2), 4));
            if (usage.PokedexProgress is { } progress)
                text.Children.Add(Spaced(Ui.Text($"{usage.Range.Label()} 도감 받는 중 · {progress.Text}", 11, "TextTertiary"), 4));
            else if (usage.PokedexError is { } error)
            {
                var errorRow = new StackPanel { Orientation = Orientation.Horizontal };
                var message = Ui.Text(error, 11, "OrangeBrush");
                message.MaxWidth = 220;
                errorRow.Children.Add(message);
                var retry = Ui.Pill("다시 받기", usage.LoadPokedex);
                retry.FontSize = 11;
                retry.Margin = new Thickness(6, 0, 0, 0);
                errorRow.Children.Add(retry);
                text.Children.Add(Spaced(errorRow, 4));
            }
        }
        else
        {
            var egg = new PixelSprite(MateSpecies.EggGrid, new Dictionary<char, uint> { ['k'] = 0x3A3A4A, ['f'] = 0xF3EADB, ['s'] = 0x9A9AA5 }, 72)
            { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(SpriteBox(egg));
            text.Children.Add(Ui.Text("포켓몬 도감 준비 중", 15, "TextFg", FontWeights.Bold));
            var progress = usage.PokedexProgress is { } p ? $" ({p.Text})" : "";
            text.Children.Add(Spaced(Ui.Text(usage.PokedexError ?? $"PokeAPI 에서 {usage.Range.Label()} 도감을 받고 있어요. 처음 한 번만 받아요.{progress}", 12, "TextSecondary", wrap: true, maxLines: 2), 4));
            if (usage.PokedexError != null)
            {
                var retry = Ui.Pill("다시 받기", usage.LoadPokedex);
                retry.HorizontalAlignment = HorizontalAlignment.Left;
                text.Children.Add(Spaced(retry, 4));
            }
        }
        row.Children.Add(text);
        Child = row;
    }

    static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        element.Margin = new Thickness(element.Margin.Left, top, element.Margin.Right, element.Margin.Bottom);
        return element;
    }
}

/// 컬렉션 탭: 만난 포켓몬 도감 (맥 CollectionView). 검색, 희귀도 필터, 정렬, 우클릭으로 대표 지정.
public partial class CollectionView : UserControl
{
    enum Sort { Number, Name, Rarity, Count }

    static readonly (Sort Sort, string Label)[] Sorts =
    {
        (Sort.Number, "번호순"), (Sort.Name, "이름순"), (Sort.Rarity, "희귀도순"), (Sort.Count, "모은 횟수순"),
    };

    readonly UsageStore usage = UsageStore.Shared;
    readonly Ui.Debounce redraw;
    readonly HashSet<MateRarity> rarityFilter = new();
    Sort sort = Sort.Number;
    string query = "";

    public CollectionView(AppModel model)
    {
        InitializeComponent();
        redraw = new Ui.Debounce(Rebuild, 20);
        Loaded += (_, _) => { usage.PropertyChanged -= OnChanged; usage.PropertyChanged += OnChanged; Rebuild(); };
        Unloaded += (_, _) => usage.PropertyChanged -= OnChanged;
        Rebuild();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageStore.Mate) or nameof(UsageStore.AllSpecies) or nameof(UsageStore.KnownSpecies) or nameof(UsageStore.Range))
            redraw.Poke();
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        query = SearchBox.Text;
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    void OnSortClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (value, label) in Sorts)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = sort == value };
            var captured = value;
            item.Click += (_, _) => { sort = captured; Rebuild(); };
            menu.Items.Add(item);
        }
        SortButton.ToolTip = $"정렬: {Sorts.First(s => s.Sort == sort).Label}";
        menu.IsOpen = true;
    }

    // MARK: 목록 만들기 (맥 CollectionView 의 entries / filtered)

    static List<DexEntry> EggEntries(MateState state) =>
        new[] { state.Current }.OfType<CurrentMate>().Concat(state.Party)
            .Select(mate => MateSpecies.Find(mate.Species) is { } line && line.Form(mate.Tokens, mate.Form) == null ? new DexEntry(line.Base, line) : null)
            .OfType<DexEntry>().ToList();

    List<DexEntry> Entries(MateState state, HashSet<int> seen)
    {
        var list = MateDex.Entries(usage.KnownSpecies, null).Where(e => seen.Contains(e.Id)).ToList();
        foreach (var egg in EggEntries(state))
            if (!list.Any(e => e.Id == egg.Id)) list.Add(egg);
        return list;
    }

    List<DexEntry> Filtered(List<DexEntry> list, MateState state, HashSet<int> seen)
    {
        var q = query.Trim().ToLowerInvariant().Replace("#", "");
        var output = list.Where(e => rarityFilter.Count == 0 || rarityFilter.Contains(e.Species.Rarity)).ToList();
        if (q.Length > 0)
            output = output.Where(e => e.Form.Id.ToString() == q || (seen.Contains(e.Id) && e.Form.Name.ToLowerInvariant().Contains(q))).ToList();
        return sort switch
        {
            Sort.Number => output.OrderBy(e => e.Form.Id).ToList(),
            Sort.Name => output.OrderBy(e => e.Form.Name, StringComparer.Ordinal).ToList(),
            Sort.Rarity => output.OrderBy(e => (int)e.Species.Rarity).ThenBy(e => e.Form.Id).ToList(),
            _ => output.OrderByDescending(e => state.CompletedCount(e.Id)).ThenBy(e => e.Form.Id).ToList(),
        };
    }

    void Rebuild()
    {
        var state = usage.Mate;
        var seen = state.SeenDex();
        var all = Entries(state, seen);
        var shown = Filtered(all, state, seen);
        var inRange = MateDex.Entries(usage.AllSpecies, usage.Range.FixedMaxDex()).Select(e => e.Id).ToHashSet();

        Summary.Text = $"{seen.Intersect(inRange).Count()} / {inRange.Count}마리 · {usage.Range.Label()}";

        Chips.Children.Clear();
        foreach (var rarity in MateRarityExtensions.AllCases)
            Chips.Children.Add(Chip(rarity, all.Count(e => seen.Contains(e.Id) && e.Species.Rarity == rarity)));

        Cells.Children.Clear();
        foreach (var entry in shown) Cells.Children.Add(Cell(entry, state, seen));
        Empty.Text = query.Length == 0 && rarityFilter.Count == 0 ? "아직 만난 친구가 없어요." : "찾는 친구가 없어요.";
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    UIElement Chip(MateRarity rarity, int count)
    {
        var on = rarityFilter.Contains(rarity);
        var color = rarity.Of();
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = rarity.Brush(), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        var name = Ui.Text(rarity.Label(), 12, "TextFg", FontWeights.SemiBold);
        name.Margin = new Thickness(4, 0, 0, 0);
        row.Children.Add(name);
        var number = Ui.Text(count.ToString(), 12, "TextFg", FontWeights.Bold);
        number.Margin = new Thickness(4, 0, 0, 0);
        row.Children.Add(number);
        var capsule = Ui.Capsule(row, Ui.Alpha(color, on ? 0.3 : 0.12), new Thickness(8, 3, 8, 3), Ui.Alpha(color, on ? 0.8 : 0.35));
        capsule.Margin = new Thickness(0, 0, 6, 0);
        capsule.Cursor = Cursors.Hand;
        capsule.Opacity = count == 0 && !on ? 0.45 : 1;
        capsule.ToolTip = on ? $"{rarity.Label()} 필터 끄기" : $"{rarity.Label()}만 보기";
        capsule.MouseLeftButtonUp += (_, _) =>
        {
            if (!rarityFilter.Remove(rarity)) rarityFilter.Add(rarity);
            Rebuild();
        };
        return capsule;
    }

    UIElement Cell(DexEntry entry, MateState state, HashSet<int> seen)
    {
        var species = entry.Species;
        var raising = state.Raising(species.Id);
        var tokens = raising?.Tokens ?? MateRules.CompleteAt;
        var live = raising == null ? null : species.Form(tokens, raising.Form);
        var shown = raising == null ? null : species.ShownForm(tokens, raising.Form, raising.Shown);
        var isEgg = raising != null && live == null && entry.Id == species.Base.Id;
        var isNow = raising != null && (shown?.Id == entry.Id || isEgg);
        var isLive = raising != null && live?.Id == entry.Id;
        var isCurrent = isNow && state.Current?.Species == species.Id;
        var pickable = raising != null && !isCurrent && !isEgg
            && species.Step(tokens, raising.Form) is int step
            && species.Path(raising.Form).Take(step + 1).Any(f => f.Id == entry.Id);
        var known = seen.Contains(entry.Id);
        var count = state.CompletedCount(entry.Id);
        var rarity = species.Rarity;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var number = new TextBlock { Text = $"#{entry.Id}", FontSize = 12, FontWeight = FontWeights.Bold };
        if (known || isEgg) number.Foreground = rarity.Brush(); else number.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(number);
        if (isCurrent)
        {
            var star = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            star.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            head.Children.Add(star);
        }
        top.Children.Add(head);
        if (count > 0)
        {
            var times = Ui.Text($"×{count}", 11, "TextSecondary", FontWeights.SemiBold);
            times.ToolTip = $"도감 등록 {count}번";
            Grid.SetColumn(times, 2);
            top.Children.Add(times);
        }

        var art = new Grid { Height = 56 };
        UIElement sprite = isNow && raising != null
            ? new MateSprite(species, tokens, raising.Form, raising.Shown, 48, animated: isCurrent)
            : new PokemonImage(entry.Id, 48, silhouette: !known);
        ((FrameworkElement)sprite).HorizontalAlignment = HorizontalAlignment.Center;
        art.Children.Add(sprite);
        if (isNow || isLive)
        {
            var text = isCurrent ? "대표" : MateRules.Label(species.Step(tokens, raising?.Form));
            var label = Ui.Text(text, 11, isCurrent ? "AccentBrush" : "TextSecondary", FontWeights.Bold);
            var capsule = Ui.Capsule(label, isCurrent ? Theme.Brush("AccentSoft") : Theme.Brush("HoverBg"), new Thickness(5, 1, 5, 1));
            capsule.HorizontalAlignment = HorizontalAlignment.Center;
            capsule.VerticalAlignment = VerticalAlignment.Bottom;
            capsule.RenderTransform = new TranslateTransform(0, 8);
            art.Children.Add(capsule);
        }

        var name = Ui.Text(known ? entry.Form.Name : "???", 13, known ? "TextFg" : "TextSecondary");
        name.Margin = new Thickness(0, 4, 0, 0);
        name.HorizontalAlignment = HorizontalAlignment.Center;

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(new Border { Height = 4 });
        content.Children.Add(art);
        content.Children.Add(name);

        var cell = new Border
        {
            Child = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(12), Margin = new Thickness(4),
            Background = Brushes.Transparent,
        };
        if (isCurrent) cell.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        else cell.SetResourceReference(Border.BackgroundProperty, "CardBg");

        var menu = new ContextMenu();
        if (isCurrent) menu.Items.Add(new MenuItem { Header = "지금 대표예요", IsEnabled = false });
        else if (pickable)
        {
            var pick = new MenuItem { Header = "대표로 지정" };
            pick.Click += (_, _) => usage.MakeRepresentative(species.Id, entry.Id);
            menu.Items.Add(pick);
        }
        else if (raising != null) menu.Items.Add(new MenuItem { Header = "아직 진화하지 않은 단계예요", IsEnabled = false });
        else menu.Items.Add(new MenuItem { Header = "도감 등록이 끝난 포켓몬은 대표로 지정할 수 없어요", IsEnabled = false });
        cell.ContextMenu = menu;

        cell.ToolTip = known
            ? $"{rarity.Label()} · {entry.Form.Flavor}{(pickable ? " · 우클릭해서 대표로 지정" : "")}"
            : isEgg ? $"{rarity.Label()} · 아직 알이에요" : $"{rarity.Label()} · 아직 못 만난 친구";
        return cell;
    }
}
