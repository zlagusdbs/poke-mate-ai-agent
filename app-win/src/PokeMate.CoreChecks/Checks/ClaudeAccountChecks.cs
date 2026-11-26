using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: ClaudeAccount
public static class ClaudeAccountChecks
{
    public static void Run()
    {
        const string status = """{"loggedIn":true,"email":"kim@example.com","orgName":"Example Inc.","subscriptionType":"team"}""";
        Check.That(ClaudeAccount.Parse(status)?.Display == "kim@example.com · Example Inc. (team)", "account: 표시");
        Check.That(ClaudeAccount.Parse("""{"loggedIn":false}""")?.Display == "로그인 안 됨", "account: 로그인 안 됨");
        const string limit = """{"type":"result","is_error":true,"result":"You've hit your individual spend limit · run /usage-credits to ask your admin for a higher limit"}""";
        Check.That(ClaudeResponse.Parse(limit, 1) == new ClaudeResponse.Failure(new ClaudeFailure.UsageLimit()), "account: 개인 사용 한도 오류");
        var env = AppPaths.ProcessEnvironment(AppSettings.Default);
        Check.That(env["CLAUDE_CONFIG_DIR"] == AppPaths.ClaudeConfigDir, "account: 메이트는 항상 전용 로그인 폴더");
        const string url = "https://claude.com/cai/oauth/authorize?code=true&state=abc";
        var linked = "Opening browser to sign in…\nIf the browser didn't open, visit:\n\u001B]8;;" + url + "\u0007" + url + "\u001B]8;;\u0007\nPaste code here if prompted > ";
        Check.That(ClaudeLogin.SignInUrl(linked) == url, "login: 터미널 링크로 감싼 로그인 주소를 읽음");
        Check.That(ClaudeLogin.SignInUrl("Sign in at:\n\u001B[1m" + url + "\u001B[0m\nPaste code here > ") == url, "login: Sign in at 아래 주소, 색 코드 제거");
        Check.That(ClaudeLogin.SignInUrl("Opening browser to sign in…\nPaste code here if prompted > ") == null, "login: 주소가 없으면 null");
        Check.That(AppPaths.DataDir == Environment.GetEnvironmentVariable("POKEMATE_DATA_DIR"), "account: 데이터 폴더는 POKEMATE_DATA_DIR 우선 (검사는 임시 폴더)");
    }
}
