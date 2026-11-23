using System.Text.Json.Nodes;

namespace PokeMate.Core;

public abstract record ClaudeFailure
{
    public sealed record NotInstalled : ClaudeFailure;
    public sealed record NotLoggedIn : ClaudeFailure;
    public sealed record UsageLimit : ClaudeFailure;
    public sealed record Timeout : ClaudeFailure;
    public sealed record Cancelled : ClaudeFailure;
    public sealed record Malformed(string Text) : ClaudeFailure;
    public sealed record Other(string Text) : ClaudeFailure;

    public string Message => this switch
    {
        NotInstalled => "Claude Code 설치가 필요해요.",
        NotLoggedIn => "로그인이 필요합니다. 브라우저를 띄웁니다.",
        UsageLimit => "Claude 사용 한도를 다 썼어요. 관리자에게 한도를 올려 달라고 요청하거나(터미널 claude 에서 /usage-credits), ⚙️ 설정의 「메이트 Claude 계정」에서 다른 계정으로 로그인해 주세요.",
        Timeout => "응답이 늦어요. 다시 시도해 주세요.",
        Cancelled => "취소했어요.",
        Malformed m => $"응답을 읽지 못했어요: {SwiftText.Prefix(m.Text, 200)}",
        Other o => $"오류가 났어요: {SwiftText.Prefix(o.Text, 200)}",
        _ => "",
    };

    /// 사용자가 시키지 않은 실행(알림·자동화·파이프라인)에서 쓰는 문구. 이 실행들은 로그인을 시작하지 않는다.
    public const string BackgroundLoginMessage = "로그인이 필요해요. 채팅에서 다시 물어보거나 ⚙️ 설정의 「메이트 Claude 계정」에서 로그인해 주세요.";

    public string BackgroundMessage => this is NotLoggedIn ? BackgroundLoginMessage : Message;

    /// 로그인 필요로 실패했을 때 `claude auth login` 을 시작해도 되는지: 사용자가 보낸 채팅·작업 세션 질문만.
    public static bool StartsLogin(ClaudeFailure reason, bool interactive) => interactive && reason is NotLoggedIn;
}

public abstract record ClaudeResponse
{
    public sealed record Success(string Text, string SessionId) : ClaudeResponse;
    public sealed record Failure(ClaudeFailure Reason) : ClaudeResponse;

    /// `claude -p` 출력 해석. JSON 이 아니어도 앱이 죽지 않게 Failure 로 돌려준다.
    public static ClaudeResponse Parse(string stdout, int exitCode)
    {
        var text = stdout;
        // stream-json: 여러 줄 중 마지막 "type":"result" 줄을 쓴다. (Windows 줄 끝 CR 은 JSON 공백이라 그대로 읽힌다)
        var resultLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse()
            .Select(line => Json.ParseObject(line))
            .FirstOrDefault(o => o != null && Json.Str(o["type"]) == "result");
        if (resultLine != null) return Interpret(resultLine, exitCode);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        var obj = start >= 0 && end >= start ? Json.ParseObject(text[start..(end + 1)]) : null;
        if (obj == null) return new Failure(Classify(text) ?? new ClaudeFailure.Malformed(text.Trim()));
        return Interpret(obj, exitCode);
    }

    internal static ClaudeResponse Interpret(JsonObject obj, int exitCode)
    {
        var result = Json.Str(obj["result"]) ?? "";
        var isError = Json.Bool(obj["is_error"]) ?? (exitCode != 0);
        if (isError) return new Failure(Classify(result) ?? new ClaudeFailure.Other(result));
        return new Success(result, Json.Str(obj["session_id"]) ?? "");
    }

    internal static ClaudeFailure? Classify(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("authenticate") || lower.Contains("/login") || lower.Contains("not logged in"))
            return new ClaudeFailure.NotLoggedIn();
        if (lower.Contains("usage limit") || lower.Contains("rate limit") || lower.Contains("limit reached")
            || lower.Contains("spend limit") || lower.Contains("usage-credits"))
            return new ClaudeFailure.UsageLimit();
        return null;
    }
}
