using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PokeMate;

/// 전역 단축키 (`RegisterHotKey`). 숨은 메시지 창으로 WM_HOTKEY 를 받는다. 설정에서 바꾸면 다시 Register 한다.
public sealed class HotkeyManager : IDisposable
{
    const int WmHotkey = 0x0312;
    const int HotkeyId = 1;
    static readonly IntPtr MessageOnly = new(-3);   // HWND_MESSAGE

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    HwndSource? source;
    Action action = () => { };
    bool registered;

    /// 마지막 Register 가 실패한 이유 (다른 프로그램이 같은 조합을 쓰는 중 등). 성공이면 null
    public string? LastError { get; private set; }

    /// 단축키를 (다시) 등록한다. 이미 등록한 것은 풀고 바꾼다. 등록하지 못하면 false.
    public bool Register(Hotkey hotkey, Action action)
    {
        this.action = action;
        if (source == null)
        {
            var parameters = new HwndSourceParameters("PokeMateHotkey") { ParentWindow = MessageOnly, Width = 0, Height = 0, WindowStyle = 0 };
            source = new HwndSource(parameters);
            source.AddHook(Hook);
        }
        Unregister();
        if (!hotkey.IsValid)
        {
            LastError = "Ctrl·Alt·Win 중 하나가 필요해요";
            return false;
        }
        registered = RegisterHotKey(source.Handle, HotkeyId, hotkey.Modifiers | Hotkey.NoRepeat, hotkey.KeyCode);
        LastError = registered ? null : $"{hotkey.Display} 를 등록하지 못했어요 (오류 {Marshal.GetLastWin32Error()})";
        return registered;
    }

    public void Unregister()
    {
        if (source != null && registered) UnregisterHotKey(source.Handle, HotkeyId);
        registered = false;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        source?.Dispose();
        source = null;
    }
}
