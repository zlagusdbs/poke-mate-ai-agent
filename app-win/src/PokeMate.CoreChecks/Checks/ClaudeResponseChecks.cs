using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: ClaudeResponse
public static class ClaudeResponseChecks
{
    public static void Run()
    {
        const string ok = """{"type":"result","is_error":false,"result":"답","session_id":"s1"}""";
        Check.That(ClaudeResponse.Parse(ok, 0) == new ClaudeResponse.Success("답", "s1"), "parse: 성공");
        const string stream = "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"assistant\"}\n{\"type\":\"result\",\"is_error\":false,\"result\":\"빨강\",\"session_id\":\"s9\"}\n";
        Check.That(ClaudeResponse.Parse(stream, 0) == new ClaudeResponse.Success("빨강", "s9"), "parse: stream-json 의 result 줄");
        const string auth = """{"is_error":true,"result":"Failed to authenticate: OAuth session expired","session_id":"x"}""";
        Check.That(ClaudeResponse.Parse(auth, 1) == new ClaudeResponse.Failure(new ClaudeFailure.NotLoggedIn()), "parse: 로그인 만료 JSON");
        Check.That(ClaudeResponse.Parse("Please run /login", 1) == new ClaudeResponse.Failure(new ClaudeFailure.NotLoggedIn()), "parse: JSON 아닌 로그인 안내");
        Check.That(ClaudeFailure.StartsLogin(new ClaudeFailure.NotLoggedIn(), interactive: true) && !ClaudeFailure.StartsLogin(new ClaudeFailure.NotLoggedIn(), interactive: false)
                   && !ClaudeFailure.StartsLogin(new ClaudeFailure.Timeout(), interactive: true),
                   "login: 로그인은 사용자가 보낸 채팅·작업 질문에서만 시작 (알림·자동화 실행은 시작 안 함)");
        Check.That(new ClaudeFailure.NotLoggedIn().BackgroundMessage == ClaudeFailure.BackgroundLoginMessage && !ClaudeFailure.BackgroundLoginMessage.Contains("브라우저")
                   && new ClaudeFailure.Timeout().BackgroundMessage == new ClaudeFailure.Timeout().Message,
                   "login: 알림 실행의 로그인 필요 문구는 브라우저를 띄운다고 하지 않음");
        Check.That(ClaudeResponse.Parse("segfault", 139) is ClaudeResponse.Failure { Reason: ClaudeFailure.Malformed }, "parse: 알 수 없는 출력은 malformed");
        Check.That(new ClaudeFailure.Malformed(new string('가', 300)).Message.Length == "응답을 읽지 못했어요: ".Length + 200, "parse: 오류 문구는 200자까지");
    }
}
