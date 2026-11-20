using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PokeMate;

/// 트레이 아이콘 (`NotifyIcon`). 키우는 포켓몬 그림, 툴팁은 토큰 요약, 오른쪽 클릭 메뉴 (열기·설정·고정·끝내기).
/// 왼쪽 클릭은 Toggle, 풍선 알림은 Notifier 가 ShowBalloon 으로 쓴다.
public sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    readonly WinForms.NotifyIcon icon = new();
    readonly WinForms.ContextMenuStrip menu = new();
    readonly WinForms.ToolStripMenuItem pinItem = new();
    Drawing.Icon? current;

    /// 풍선 알림을 눌렀을 때
    public event Action? BalloonClicked;
    /// 풍선 알림이 사라졌을 때 (눌러서든 시간이 지나서든)
    public event Action? BalloonClosed;

    public TrayIcon(Action toggle, Action open, Action settings, Action togglePin, Func<bool> isPinned, Action quit)
    {
        var openItem = new WinForms.ToolStripMenuItem("열기");
        openItem.Click += (_, _) => open();
        var settingsItem = new WinForms.ToolStripMenuItem("설정");
        settingsItem.Click += (_, _) => settings();
        pinItem.Click += (_, _) => togglePin();
        var quitItem = new WinForms.ToolStripMenuItem("끝내기");
        quitItem.Click += (_, _) => quit();
        menu.Items.AddRange(new WinForms.ToolStripItem[] { openItem, settingsItem, pinItem, new WinForms.ToolStripSeparator(), quitItem });
        menu.Opening += (_, _) => pinItem.Text = isPinned() ? "고정 풀기" : "고정하기";

        icon.ContextMenuStrip = menu;
        icon.Text = "PokeMate";
        icon.MouseUp += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) toggle(); };
        icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
        icon.BalloonTipClosed += (_, _) => BalloonClosed?.Invoke();
        SetImage(PokemonSprites.EmptyEggImage(IconSide));
        icon.Visible = true;
    }

    /// 트레이 아이콘 한 변 (화면 배율에 맞춘 작은 아이콘 크기)
    public static int IconSide => Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);

    public void SetImage(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        stream.Position = 0;
        using var bitmap = new Drawing.Bitmap(stream);
        var handle = bitmap.GetHicon();
        Drawing.Icon next;
        try { next = (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
        var previous = current;
        current = next;
        icon.Icon = next;
        previous?.Dispose();
    }

    /// 툴팁 (맥 메뉴바의 토큰 숫자 자리). Windows 는 127자까지.
    public void SetTooltip(string text) => icon.Text = text.Length > 127 ? text[..127] : text;

    public void ShowBalloon(string title, string body)
    {
        icon.ShowBalloonTip(10_000, title.Length > 63 ? title[..63] : title, body.Length > 255 ? body[..255] : body, WinForms.ToolTipIcon.None);
    }

    /// 떠 있는 풍선을 거둔다 (아이콘을 껐다 켜는 방법밖에 없다)
    public void HideBalloon()
    {
        icon.Visible = false;
        icon.Visible = true;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        current?.Dispose();
    }
}
