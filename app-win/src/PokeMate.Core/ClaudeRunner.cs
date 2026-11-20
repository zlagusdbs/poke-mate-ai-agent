namespace PokeMate.Core;

/// 대화에서 도구 승인 카드를 띄우기 위한 연결. 있으면 claude 를 대화(stream-json + 권한 응답) 방식으로 실행한다.
public sealed record ToolHooks(IReadOnlyList<string> SessionAllow, Action<string> OnActivity, Func<PermissionRequest, Task<PermissionDecision>> OnPermission);

/// `claude -p` 를 한 번 실행하고 결과를 해석한다.
public sealed class ClaudeRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(1800);
    public static readonly TimeSpan AutomationTimeout = TimeSpan.FromSeconds(1800);

    readonly ProcessRunner process = new();
    readonly InteractiveProcess interactive = new();
    volatile bool cancelled;

    public RunUsage? LastUsage { get; private set; }

    public async Task<ClaudeResponse> Run(string prompt, string? sessionId, AppSettings settings,
                                          IReadOnlyList<ImageAttachment>? images = null,
                                          IReadOnlyList<string>? writeTools = null, IReadOnlyList<string>? writeDirs = null, ToolHooks? hooks = null,
                                          TimeSpan? timeout = null, string? newSessionId = null, bool automation = true,
                                          IDictionary<string, string>? extraEnvironment = null)
    {
        cancelled = false;
        LastUsage = null;
        images ??= Array.Empty<ImageAttachment>();
        var limit = timeout ?? Timeout;
        if (!File.Exists(settings.ClaudePath)) return new ClaudeResponse.Failure(new ClaudeFailure.NotInstalled());
        try
        {
            AppPaths.Prepare(settings);
            var systemPrompt = File.ReadAllText(AppPaths.SharedFile("app/system.md", settings));
            systemPrompt += NameLine(settings);
            systemPrompt += OwnerLine(settings);
            systemPrompt += LocalNames.Load(settings.RepoDir).PromptLine();
            systemPrompt += Rules.Instructions();
            systemPrompt += Rules.Prompt(prompt);
            var args = ClaudeCommand.Arguments(sessionId, AppPaths.McpConfig, systemPrompt,
                                               model: automation ? settings.AutomationModel : settings.Model,
                                               effort: automation ? settings.AutomationEffort : settings.Effort,
                                               homeExcludes: ClaudeCommand.HomeExcludes(settings, automation),
                                               writeTools: writeTools, writeDirs: writeDirs,
                                               interactive: hooks != null, sessionAllow: hooks?.SessionAllow,
                                               newSessionId: newSessionId);
            var environment = AppPaths.ProcessEnvironment(settings);
            if (extraEnvironment != null) foreach (var kv in extraEnvironment) environment[kv.Key] = kv.Value;
            ProcessOutput output;
            if (hooks != null)
            {
                output = await interactive.Run(settings.ClaudePath, args, ClaudeCommand.InputLine(prompt, images), AppPaths.WorkDir, environment,
                                               limit > InteractiveTimeout ? limit : InteractiveTimeout,
                                               async (line, io) =>
                                               {
                                                   if (ClaudeStream.Object(line) is not { } obj) return;
                                                   if (ClaudeStream.PermissionRequest(obj) is { } request)
                                                   {
                                                       var decision = await hooks.OnPermission(request);
                                                       io.Send(ClaudeStream.Response(request, decision));
                                                       return;
                                                   }
                                                   foreach (var use in ClaudeStream.ToolUses(obj))
                                                       hooks.OnActivity(ClaudeStream.Activity(use.Name, use.Input));
                                                   if (Json.Str(obj["type"]) == "result") io.CloseInput();
                                               });
            }
            else
            {
                output = await process.Run(settings.ClaudePath, args, stdin: ClaudeCommand.InputLine(prompt, images),
                                           workDir: AppPaths.WorkDir, environment: environment, timeout: limit);
            }
            LastUsage = RunUsage.Parse(output.Stdout);
            if (cancelled) return new ClaudeResponse.Failure(new ClaudeFailure.Cancelled());
            if (output.TimedOut) return new ClaudeResponse.Failure(new ClaudeFailure.Timeout());
            var response = ClaudeResponse.Parse(output.Stdout, output.ExitCode);
            if (response is ClaudeResponse.Failure { Reason: ClaudeFailure.Malformed { Text.Length: 0 } })
                return new ClaudeResponse.Failure(new ClaudeFailure.Other(output.Stderr));
            return response;
        }
        catch (Exception e)
        {
            return new ClaudeResponse.Failure(new ClaudeFailure.Other(e.Message));
        }
    }

    public static string NameLine(AppSettings settings)
    {
        var pokemon = settings.MateName?.Trim();
        if (!string.IsNullOrEmpty(pokemon))
            return $"\n- 너는 지금 주인의 대표 포켓몬 \"{pokemon}\"이다. \"메이트\" 대신 자신을 \"{pokemon}\"(이)라고 부른다.\n";
        var name = settings.AssistantName.Trim();
        if (name.Length == 0 || name == AppSettings.DefaultAssistantName) return "";
        return $"\n- 너의 이름은 \"{name}\"이다. \"메이트\" 대신 자신을 \"{name}\"(이)라고 부른다.\n";
    }

    public static string OwnerLine(AppSettings settings)
    {
        var call = settings.OwnerCallName;
        if (call.Length == 0) return "";
        return $"\n- 주인(사용자)을 부를 때는 \"{call}\"이라고 부른다. 다른 말을 덧붙이지 않는다.\n";
    }

    public void Cancel()
    {
        cancelled = true;
        process.Cancel();
        interactive.Cancel();
    }
}
