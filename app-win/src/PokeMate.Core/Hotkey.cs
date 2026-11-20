using System.Text.Json.Serialization;

namespace PokeMate.Core;

/// 전역 단축키. Windows `RegisterHotKey` 값: keyCode 는 가상 키 코드(VK_*), modifiers 는 MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8.
/// 맥의 ⌘·⌃ 가 겹치므로 기본 조합은 ⌘→Ctrl, ⌥→Alt 로 바꾼 Ctrl+Alt+Shift+Space.
public sealed record Hotkey(uint KeyCode, uint Modifiers)
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8;
    /// 맥 이름으로 찾는 코드를 위해: ⌥ = Alt, ⌘ = Win
    public const uint Option = Alt, Command = Win;
    /// RegisterHotKey 의 MOD_NOREPEAT (등록할 때만 더한다. 저장값에는 넣지 않는다)
    public const uint NoRepeat = 0x4000;

    public static Hotkey Default => new(0x20, Control | Alt | Shift);   // Ctrl+Alt+Shift+Space

    /// Shift 만으로는 글자 입력과 겹치므로 Ctrl·Alt·Win 중 하나는 있어야 한다.
    [JsonIgnore] public bool IsValid => (Modifiers & (Control | Alt | Win)) != 0;

    /// 맥 저장값(Carbon 값: cmdKey 256, shiftKey 512, optionKey 2048, controlKey 4096)인지.
    [JsonIgnore] public bool LooksLikeMac => (Modifiers & ~(Alt | Control | Shift | Win)) != 0;

    /// Windows 표기 순서: Ctrl+Alt+Shift+Win+키
    [JsonIgnore] public string Display
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            if ((Modifiers & Win) != 0) parts.Add("Win");
            parts.Add(KeyNames.TryGetValue(KeyCode, out var name) ? name : $"키 {KeyCode}");
            return string.Join("+", parts);
        }
    }

    public static readonly IReadOnlyDictionary<uint, string> KeyNames = MakeKeyNames();

    static Dictionary<uint, string> MakeKeyNames()
    {
        var names = new Dictionary<uint, string>
        {
            [0x20] = "Space", [0x0D] = "Enter", [0x09] = "Tab", [0x08] = "Backspace", [0x2E] = "Delete", [0x1B] = "Esc",
            [0x25] = "←", [0x27] = "→", [0x28] = "↓", [0x26] = "↑",
            [0xBD] = "-", [0xBB] = "=", [0xDB] = "[", [0xDD] = "]", [0xDC] = "\\", [0xBA] = ";", [0xDE] = "'",
            [0xBC] = ",", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
        };
        for (uint c = 'A'; c <= 'Z'; c++) names[c] = ((char)c).ToString();
        for (uint c = '0'; c <= '9'; c++) names[c] = ((char)c).ToString();
        for (uint i = 0; i < 12; i++) names[0x70 + i] = $"F{i + 1}";
        return names;
    }
}
