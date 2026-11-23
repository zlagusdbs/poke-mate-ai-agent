using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PokeMate;

/// 첨부 이미지를 Claude 에 보내기 좋은 크기로 줄이고, 화면 표시용으로 앱 데이터 폴더에 저장한다 (맥 ImageLoader).
public static class ImageLoader
{
    public const double MaxSide = 1568;       // Claude 권장 최대 변 길이
    public const int MaxCount = 5;

    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

    public static string Folder => Path.Combine(AppPaths.DataDir, "attachments");

    public static ImageAttachment? Load(string path)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return Attachment(decoder.Frames[0]);
        }
        catch { return null; }
    }

    /// 흰 바탕 위에 올려 JPEG(품질 0.85)로 만든다. 가장 긴 변이 MaxSide 를 넘으면 줄인다.
    public static ImageAttachment? Attachment(BitmapSource image)
    {
        try
        {
            double w = image.PixelWidth, h = image.PixelHeight;
            var scale = Math.Min(1, MaxSide / Math.Max(w, h));
            var width = (int)Math.Round(w * scale);
            var height = (int)Math.Round(h * scale);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawImage(image, new Rect(0, 0, width, height));
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var data = stream.ToArray();

            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, Guid.NewGuid().ToString().ToUpperInvariant() + ".jpg");
            try { File.WriteAllBytes(file, data); } catch { }
            return new ImageAttachment("image/jpeg", data, file);
        }
        catch { return null; }
    }

    /// 클립보드에 이미지(스크린샷 데이터 또는 이미지 파일)가 있는지. 글자도 같이 있으면 글자로 본다.
    public static bool ClipboardHasImage()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                return Clipboard.GetFileDropList().Cast<string>().Any(IsImageFile);
            return Clipboard.ContainsImage() && !Clipboard.ContainsText();
        }
        catch { return false; }
    }

    /// 클립보드의 이미지 (스크린샷 붙여넣기)
    public static List<ImageAttachment> FromClipboard()
    {
        var result = new List<ImageAttachment>();
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
                if (files.Count > 0)
                {
                    result.AddRange(files.Select(Load).OfType<ImageAttachment>());
                    return result;
                }
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image && Attachment(image) is { } attachment)
                result.Add(attachment);
        }
        catch { }
        return result;
    }

    public static List<ImageAttachment> ChooseFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "이미지|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff",
            Multiselect = true,
            Title = "첨부할 이미지를 고르세요",
        };
        if (dialog.ShowDialog() != true) return new List<ImageAttachment>();
        return dialog.FileNames.Select(Load).OfType<ImageAttachment>().ToList();
    }

    static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
