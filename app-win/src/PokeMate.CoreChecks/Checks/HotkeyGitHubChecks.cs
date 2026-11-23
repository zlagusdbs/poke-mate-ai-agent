using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Hotkey, GitHub
public static class HotkeyGitHubChecks
{
    public static void Run()
    {
        Check.That(Hotkey.Default.Display == "Ctrl+Alt+Shift+Space", "hotkey: 기본 표기");
        Check.That(new Hotkey(0x4B, Hotkey.Control | Hotkey.Alt).Display == "Ctrl+Alt+K", "hotkey: Ctrl+Alt+K 표기");
        Check.That(!new Hotkey(0x4B, Hotkey.Shift).IsValid, "hotkey: Shift 만은 안 됨");
        Check.That(Json.Serialize(Hotkey.Default, indented: false) == """{"keyCode":32,"modifiers":7}""", "hotkey: 저장 형식은 keyCode·modifiers 만");
        Check.That(GitHubLogin.Code("! First copy your one-time code: 1A2B-3C4D\r\nPress Enter") == "1A2B-3C4D", "github: 한 번용 코드 찾기");
        const string prs = """[{"id":7,"repo":"acme/sample-service","number":1290,"title":"t","author":"kim","url":"https://g/pull/1290","updated":"2026-10-22T10:42"}]""";
        Check.That(Json.TryDeserialize<List<PullRequest>>(prs)?.FirstOrDefault()?.Number == 1290, "github: PR 디코딩");
        Check.That(GitHubLogin.GhPath().Length > 0, "github: gh 경로 (없으면 gh)");
    }
}
