using System.Text.Json.Nodes;

namespace PokeMate.Core;

public sealed class MateCLIError : Exception
{
    public MateCLIError(string message) : base(message) { }
}

/// mcp-server 의 `pokemate` 명령 실행. 아이디·비밀번호는 표준 입력으로만 넘긴다.
public static class MateCLI
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<List<SystemStatus>> Status(AppSettings settings)
    {
        var output = await Run(new[] { "status" }, null, settings);
        return Decode<List<SystemStatus>>(output);
    }

    public static async Task<List<ConnectResult>> Connect(ConnectTarget target, string username, string secret, AppSettings settings)
    {
        var output = await Run(target.Arguments, $"{username}\n{secret}\n", settings);
        return Decode<List<ConnectResult>>(output);
    }

    public static async Task<List<PullRequest>> GithubReviewRequests(AppSettings settings)
    {
        var output = await Run(new[] { "github", "review-requests" }, null, settings);
        return Decode<List<PullRequest>>(output);
    }

    public static async Task<bool> MigrateKeychain(AppSettings settings)
    {
        var output = await Run(new[] { "migrate-keychain" }, null, settings);
        return output.ExitCode == 0;
    }

    public static async Task<List<EventItem>> Events(string eventId, AppSettings settings)
    {
        var output = await Run(new[] { "events", eventId }, null, settings);
        return Decode<List<EventItem>>(output);
    }

    sealed record Failure(string Error, string? Detail);

    /// 이슈 등록. 사용자가 확인 카드에서 [Jira에 등록]을 눌렀을 때만 부른다.
    public static async Task<JiraCreated> JiraCreate(JiraDraft draft, AppSettings settings)
    {
        var input = Json.Serialize(draft, indented: false);
        var output = await Run(new[] { "jira", "create" }, input, settings);
        if (TryCreated(output.Stdout) is JiraCreated created) return created;
        if (TryFailure(output.Stdout) is Failure failure)
            throw new MateCLIError(JiraFailure.Message(failure.Error, failure.Detail ?? ""));
        return Decode<JiraCreated>(output);
    }

    static JiraCreated? TryCreated(string text)
    {
        var obj = Json.ParseObject(text);
        return obj != null && Json.Str(obj["key"]) is string key && Json.Str(obj["url"]) is string url ? new JiraCreated(key, url) : null;
    }

    static Failure? TryFailure(string text)
    {
        var obj = Json.ParseObject(text);
        return obj != null && Json.Str(obj["error"]) is string error ? new Failure(error, Json.Str(obj["detail"])) : null;
    }

    /// worklog 기록. 사용자가 카드에서 [기록하기]를 눌렀을 때만 부른다.
    public static async Task JiraWorklog(WorklogDraft draft, AppSettings settings)
    {
        var body = new JsonObject
        {
            ["issueKey"] = draft.IssueKey, ["started"] = draft.Started(),
            ["timeSpentSeconds"] = draft.Minutes * 60, ["comment"] = draft.Comment,
        };
        var output = await Run(new[] { "jira", "worklog" }, body.ToJsonString(Json.Compact), settings);
        if (TryFailure(output.Stdout) is Failure failure)
            throw new MateCLIError(JiraFailure.Message(failure.Error, failure.Detail ?? ""));
        if (output.ExitCode != 0) throw new MateCLIError(output.Stderr);
    }

    static async Task<ProcessOutput> Run(IEnumerable<string> args, string? stdin, AppSettings settings)
    {
        var arguments = new List<string> { "--directory", Path.Combine(settings.RepoDir, "mcp-server"), "run", "--quiet", "pokemate" };
        arguments.AddRange(args);
        var output = await new ProcessRunner().Run(settings.UvPath, arguments, stdin: stdin,
                                                   environment: AppPaths.ProcessEnvironment(settings), timeout: Timeout);
        if (output.TimedOut) throw new MateCLIError("연결 확인이 너무 오래 걸려요");
        return output;
    }

    static T Decode<T>(ProcessOutput output)
    {
        try
        {
            return Json.Deserialize<T>(output.Stdout) ?? throw new MateCLIError("빈 응답");
        }
        catch (Exception e) when (e is not MateCLIError)
        {
            var text = output.Stderr.Length == 0 ? output.Stdout : output.Stderr;
            throw new MateCLIError(text.Trim());
        }
    }
}

/// 이슈 등록 실패를 사람이 읽는 말로.
public static class JiraFailure
{
    public static string Message(string code, string detail) => code switch
    {
        "NOT_CONNECTED:jira" => "Jira 연결이 필요해요. 연결 메뉴에서 연결해 주세요.",
        "AUTH_EXPIRED:jira" => "Jira 연결이 만료됐어요. 연결 메뉴에서 다시 연결해 주세요.",
        "NETWORK_UNREACHABLE:jira" => "사내망 연결을 확인해 주세요.",
        "REJECTED:jira" => $"Jira 가 등록을 거절했어요: {detail}",
        _ => detail.Length == 0 ? $"등록하지 못했어요 ({code})" : $"등록하지 못했어요: {detail}",
    };
}
