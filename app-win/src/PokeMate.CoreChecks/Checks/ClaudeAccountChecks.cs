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
        Check.That(AppPaths.DataDir == Environment.GetEnvironmentVariable("POKEMATE_DATA_DIR"), "account: 데이터 폴더는 POKEMATE_DATA_DIR 우선 (검사는 임시 폴더)");
    }
}
