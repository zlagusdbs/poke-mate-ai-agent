using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 미리 알림 창 위치 (WPF 좌표: 위가 0, 아래로 갈수록 커짐. 맥 검사의 좌표를 위아래 뒤집어 옮김)
public static class PanelPlacementChecks
{
    public static void Run()
    {
        // 화면 높이 875 에서 아래에 작업 표시줄(80)이 보이면 작업 영역은 위쪽 795
        var visible = new PanelRect(0, 0, 1440, 795);
        Check.That(PanelPlacement.SafeFrame(visible, PanelPlacement.Edge.Bottom, false, 64) == visible, "panel: 작업 표시줄이 보이면 작업 영역 그대로");
        Check.That(PanelPlacement.SafeFrame(new PanelRect(0, 0, 1440, 871), PanelPlacement.Edge.Bottom, true, 80) == new PanelRect(0, 0, 1440, 791),
                   "panel: 자동 숨김 작업 표시줄(아래)은 그 크기만큼 비킴");
        Check.That(PanelPlacement.SafeFrame(new PanelRect(4, 0, 1436, 875), PanelPlacement.Edge.Left, true, 70).MinX == 74, "panel: 자동 숨김 작업 표시줄(왼쪽)");
        Check.That(PanelPlacement.SafeFrame(new PanelRect(0, 4, 1440, 871), PanelPlacement.Edge.Top, true, 80) == new PanelRect(0, 84, 1440, 791),
                   "panel: 자동 숨김 작업 표시줄(위쪽, Windows 에만 있음)");
        Check.That(PanelPlacement.SafeFrame(new PanelRect(0, 0, 1436, 875), PanelPlacement.Edge.Right, true, 70) == new PanelRect(0, 0, 1366, 875), "panel: 자동 숨김 작업 표시줄(오른쪽)");
        var d = PanelPlacement.DefaultFrame(new PanelSize(340, 300), visible);
        Check.That(d == new PanelRect(1084, 479, 340, 300), "panel: 기본은 오른쪽 아래 여백 16");
        Check.That(PanelPlacement.DefaultFrame(new PanelSize(340, 2000), visible).Height == 763, "panel: 화면보다 크면 높이를 줄임(안에서 스크롤)");
        var pulled = PanelPlacement.Clamp(new PanelRect(1400, 700, 340, 300), visible);
        Check.That(pulled == new PanelRect(1084, 479, 340, 300), "panel: 화면 밖·작업 표시줄 위로 옮긴 자리는 안으로 당김");
        Check.That(PanelPlacement.Clamp(new PanelRect(500, 400, 340, 300), visible) == new PanelRect(500, 400, 340, 300), "panel: 안에 있는 자리는 그대로");
        Check.That(PanelPlacement.Clamp(new PanelRect(-50, -20, 340, 300), visible) == new PanelRect(16, 16, 340, 300), "panel: 왼쪽 위 밖은 여백 16 안으로");
    }
}
