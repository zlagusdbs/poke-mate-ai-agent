using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PokeMate;

/// 포켓몬 그림 캐시. 캐시 폴더에 있으면 읽고, 없으면 PokéAPI 에서 받아 저장한다 (맥 PokemonSprites).
/// 그림이 새로 들어오면 Changed 가 불린다 (UI 스레드).
public sealed class PokemonSprites
{
    public static PokemonSprites Shared { get; } = new();

    readonly Dictionary<int, BitmapSource> images = new();
    readonly HashSet<int> loading = new();

    public event Action? Changed;

    public BitmapSource? Image(int id)
    {
        if (images.TryGetValue(id, out var image)) return image;
        Load(id);
        return images.GetValueOrDefault(id);
    }

    public BitmapSource? Cached(int id) => images.GetValueOrDefault(id);

    public async void Load(int id)
    {
        if (images.ContainsKey(id) || loading.Contains(id)) return;
        try
        {
            var file = PokedexLoader.SpriteFile(id);
            if (File.Exists(file) && Decode(File.ReadAllBytes(file)) is { } cached)
            {
                images[id] = cached;
                Changed?.Invoke();
                return;
            }
        }
        catch { }
        loading.Add(id);
        try
        {
            var data = await PokedexLoader.Sprite(id);
            if (data != null && Decode(data) is { } image)
            {
                images[id] = image;
                Changed?.Invoke();
            }
        }
        catch { }
        finally { loading.Remove(id); }
    }

    /// PNG 를 읽고 투명한 가장자리를 잘라 낸다 (맥 trimmed)
    static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(pixels, stride, 0);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (pixels[y * stride + x * 4 + 3] > 0)
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
            BitmapSource result = maxX >= minX && maxY >= minY
                ? new CroppedBitmap(source, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1))
                : source;
            result.Freeze();
            return result;
        }
        catch { return null; }
    }

    /// 트레이 아이콘용 그림. 정사각형 안에 아래 맞춤. 키우는 모습이 아직 안 내려왔으면 null.
    public BitmapSource? TrayImage(MateSpecies species, long tokens, int? final, int? shown, int side)
    {
        var form = species.ShownForm(tokens, final, shown);
        if (form == null) return PixelBitmap(MateSpecies.EggGrid, species.EggPalette(), side);
        if (Image(form.Id) is not { } sprite) return null;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            var scale = Math.Min(side / (double)sprite.PixelWidth, side / (double)sprite.PixelHeight);
            double w = sprite.PixelWidth * scale, h = sprite.PixelHeight * scale;
            dc.DrawImage(sprite, new Rect((side - w) / 2, side - h, w, h));
        }
        return Render(visual, side);
    }

    public static BitmapSource EmptyEggImage(int side) =>
        PixelBitmap(MateSpecies.EggGrid, new Dictionary<char, uint> { ['k'] = 0x3A3A4A, ['f'] = 0xF3EADB, ['s'] = 0x9A9AA5 }, side);

    public static BitmapSource PixelBitmap(IReadOnlyList<string> grid, IReadOnlyDictionary<char, uint> palette, int side)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen())
            PixelSprite.Draw(dc, grid, palette, side, side, silhouette: false);
        return Render(visual, side);
    }

    static BitmapSource Render(Visual visual, int side)
    {
        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

/// 픽셀 그림 (알 모양 등). 맥 PixelSprite
public sealed class PixelSprite : FrameworkElement
{
    readonly IReadOnlyList<string> grid;
    readonly IReadOnlyDictionary<char, uint> palette;
    readonly bool silhouette;

    public PixelSprite(IReadOnlyList<string> grid, IReadOnlyDictionary<char, uint> palette, double size = 64, bool silhouette = false)
    {
        this.grid = grid;
        this.palette = palette;
        this.silhouette = silhouette;
        Width = size;
        Height = size;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc) => Draw(dc, grid, palette, ActualWidth, ActualHeight, silhouette);

    internal static void Draw(DrawingContext dc, IReadOnlyList<string> grid, IReadOnlyDictionary<char, uint> palette,
                              double width, double height, bool silhouette)
    {
        var rows = grid.Count;
        var cols = grid.Count == 0 ? 1 : grid[0].Length;
        if (rows == 0 || cols == 0) return;
        var cell = Math.Min(width / cols, height / rows);
        var gray = silhouette ? Ui.Alpha(((SolidColorBrush)Theme.Brush("TextSecondary")).Color, 0.35) : null;
        var brushes = new Dictionary<char, Brush>();
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < grid[y].Length; x++)
            {
                var ch = grid[y][x];
                if (ch == '.') continue;
                if (!brushes.TryGetValue(ch, out var brush))
                {
                    var hex = palette.TryGetValue(ch, out var c) ? c : 0xFF00FF;
                    brush = gray ?? Ui.Alpha(Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex), 1);
                    brushes[ch] = brush;
                }
                dc.DrawRectangle(brush, null, new Rect(x * cell, y * cell, cell + 0.5, cell + 0.5));
            }
    }
}

/// 포켓몬 그림 한 장. 아직 안 내려왔으면 작은 진행 표시. silhouette 이면 못 만난 친구의 그림자 (맥 PokemonImage)
public sealed class PokemonImage : Grid
{
    readonly int id;
    readonly bool silhouette;
    readonly double size;
    bool shown;

    public PokemonImage(int id, double size = 64, bool silhouette = false)
    {
        this.id = id;
        this.size = size;
        this.silhouette = silhouette;
        Width = size;
        Height = size;
        Loaded += (_, _) =>
        {
            PokemonSprites.Shared.Changed += Refresh;
            PokemonSprites.Shared.Load(id);
            Refresh();
        };
        Unloaded += (_, _) => PokemonSprites.Shared.Changed -= Refresh;
        Refresh();
    }

    void Refresh()
    {
        var image = PokemonSprites.Shared.Cached(id);
        if (image == null)
        {
            if (shown || Children.Count == 0)
            {
                Children.Clear();
                var bar = new ProgressBar { IsIndeterminate = true, Width = 18, Height = 3 };
                Children.Add(bar);
                shown = false;
            }
            return;
        }
        if (shown) return;
        shown = true;
        Children.Clear();
        UIElement content;
        if (silhouette)
        {
            var mask = new ImageBrush(image) { Stretch = Stretch.Uniform };
            var fill = new Border { OpacityMask = mask, Opacity = 0.35 };
            fill.SetResourceReference(Border.BackgroundProperty, "TextSecondary");
            content = fill;
        }
        else content = new Image { Source = image, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.NearestNeighbor);
        Children.Add(content);
    }
}

/// 키우는 포켓몬. 알이면 알 그림, 아니면 모습. animated 면 0.5초마다 살짝 뛴다 (맥 MateSprite)
public sealed class MateSprite : Grid
{
    static readonly System.Windows.Threading.DispatcherTimer Ticker = new() { Interval = TimeSpan.FromMilliseconds(500) };
    static event Action? Tick;
    static bool phase;

    static MateSprite() => Ticker.Tick += (_, _) => { phase = !phase; Tick?.Invoke(); };

    readonly TranslateTransform shift = new();
    readonly bool animated;

    public MateSprite(MateSpecies species, long tokens, int? final, int? shown, double size = 64, bool animated = true)
    {
        this.animated = animated;
        Width = size;
        Height = size;
        RenderTransform = shift;
        var form = species.ShownForm(tokens, final, shown);
        Children.Add(form != null ? new PokemonImage(form.Id, size) : new PixelSprite(MateSpecies.EggGrid, species.EggPalette(), size));
        Loaded += (_, _) =>
        {
            if (!animated) return;
            Tick += Step;
            Ticker.Start();
        };
        Unloaded += (_, _) => Tick -= Step;
    }

    void Step() => shift.Y = animated && phase ? -2 : 0;
}
