using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: ToolErrorCode
public static class ToolErrorCodeChecks
{
    public static void Run()
    {
        Check.That(ToolErrorCode.Detect("AUTH_EXPIRED:jira\nJira 연결이 만료됐어요").SequenceEqual(new ToolErrorCode[] { new ToolErrorCode.AuthExpired("jira") }), "detect: 만료");
        Check.That(ToolErrorCode.Detect("NETWORK_UNREACHABLE:jira, NOT_CONNECTED:hr")
                   .SequenceEqual(new ToolErrorCode[] { new ToolErrorCode.NetworkUnreachable("jira"), new ToolErrorCode.NotConnected("hr") }), "detect: 여러 개");
        Check.That(ToolErrorCode.Detect("정상 답변").Count == 0, "detect: 없음");
        Check.That(new ToolErrorCode.NotConnected("hr").System == "hr", "detect: 시스템 이름");
    }
}
