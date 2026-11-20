namespace PokeMate.Core;

/// 화면 위치와 크기 (WPF 좌표: 왼쪽 위가 0, 아래·오른쪽으로 커진다). 맥의 CGRect 자리.
public sealed record PanelRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MaxX => X + Width;
    public double MinY => Y;
    public double MaxY => Y + Height;
}

public sealed record PanelSize(double Width, double Height);

/// 오른쪽 아래 "메이트 알림" 창의 자리 계산. 맥(위로 커지는 좌표)과 달리 WPF 좌표로 옮겼다: 아래쪽은 MaxY.
public static class PanelPlacement
{
    public enum Edge { Bottom, Left, Right, Top }

    /// 자동 숨김 작업 표시줄이 가리는 만큼 비킨 영역. 숨김이 아니면 작업 영역(WorkArea) 그대로.
    public static PanelRect SafeFrame(PanelRect visible, Edge dockEdge, bool dockHidden, double dockSize)
    {
        if (!dockHidden) return visible;
        var inset = Math.Max(0, dockSize);
        return dockEdge switch
        {
            Edge.Bottom => new PanelRect(visible.MinX, visible.MinY, visible.Width, Math.Max(0, visible.Height - inset)),
            Edge.Top => new PanelRect(visible.MinX, visible.MinY + inset, visible.Width, Math.Max(0, visible.Height - inset)),
            Edge.Left => new PanelRect(visible.MinX + inset, visible.MinY, Math.Max(0, visible.Width - inset), visible.Height),
            _ => new PanelRect(visible.MinX, visible.MinY, Math.Max(0, visible.Width - inset), visible.Height),
        };
    }

    /// 기본 자리: 오른쪽 아래, 여백 16
    public static PanelRect DefaultFrame(PanelSize size, PanelRect safe, double margin = 16)
    {
        var height = Math.Min(size.Height, Math.Max(0, safe.Height - margin * 2));
        return new PanelRect(safe.MaxX - size.Width - margin, safe.MaxY - height - margin, size.Width, height);
    }

    /// 화면 밖이나 작업 표시줄 위로 옮긴 자리를 안으로 당긴다.
    public static PanelRect Clamp(PanelRect frame, PanelRect safe, double margin = 16)
    {
        var height = Math.Min(frame.Height, Math.Max(0, safe.Height - margin * 2));
        var width = Math.Min(frame.Width, Math.Max(0, safe.Width - margin * 2));
        var x = Math.Min(Math.Max(frame.MinX, safe.MinX + margin), safe.MaxX - margin - width);
        var y = Math.Min(Math.Max(frame.MinY, safe.MinY + margin), safe.MaxY - margin - height);
        return new PanelRect(x, y, width, height);
    }
}
