using System.Diagnostics;

namespace PokeMate;

/// Windows 알림. 트레이 풍선 알림이다 (Windows 10·11 에서는 알림 센터에 쌓인다).
/// 알림을 누르면 onClick, 없으면 메이트 창이 열린다. 풍선은 한 번에 하나만 떠서, 새 알림이 오면 앞의 것을 밀어낸다.
/// 알림 센터에 이미 들어간 알림은 앱이 지울 수 없다 (Clear 는 아직 떠 있는 풍선만 거둔다).
public static class Notifier
{
    static TrayIcon? tray;
    static string? shownTag;
    static Action? shownClick;

    /// App 이 시작할 때 한 번 연결한다. 연결 전의 알림은 디버그 출력으로만 남는다.
    public static void Attach(TrayIcon trayIcon)
    {
        tray = trayIcon;
        trayIcon.BalloonClicked += () =>
        {
            var click = shownClick;
            shownClick = null;
            shownTag = null;
            if (click != null) click(); else PopoverWindow.Current?.ShowPopover();
        };
        trayIcon.BalloonClosed += () => { shownClick = null; shownTag = null; };
    }

    /// 알림을 띄운다. tag 는 같은 종류의 알림을 나중에 Clear 로 거두기 위한 이름이다.
    public static void Show(string title, string body, string? tag = null, Action? onClick = null)
    {
        if (tray == null)
        {
            Debug.WriteLine($"[알림] {title}\n{body}");
            return;
        }
        shownTag = tag;
        shownClick = onClick;
        tray.ShowBalloon(title, body);
    }

    /// 알림이 "열어 달라"고 한 곳(맥 info["open"])을 열어 주는 클릭 동작. 예: Notifier.Show(t, b, onClick: Notifier.Opening("workSessions"))
    public static Action Opening(string? open) => () =>
    {
        AppModel.Shared.Selected = SidebarItemExtensions.Target(open);
        PopoverWindow.Current?.ShowPopover();
    };

    /// tag 로 띄운 알림이 아직 떠 있으면 거둔다
    public static void Clear(string tag)
    {
        if (tray == null || shownTag != tag) return;
        shownTag = null;
        shownClick = null;
        tray.HideBalloon();
    }
}
