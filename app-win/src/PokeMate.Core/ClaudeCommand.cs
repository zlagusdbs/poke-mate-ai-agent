using System.Text.Json;
using System.Text.Json.Nodes;

namespace PokeMate.Core;

/// `claude -p` 실행 인자. 허용 도구 목록(메이트 MCP + Microsoft 365 조회)만 쓰도록 제한한다.
public static class ClaudeCommand
{
    /// claude.ai 의 Microsoft 365 커넥터 (메일·일정·Teams·SharePoint). 모두 조회용 도구다.
    public static readonly IReadOnlyList<string> Microsoft365Tools = new[]
    {
        "get_me", "outlook_email_search", "outlook_calendar_search", "outlook_find_available_time",
        "find_meeting_availability", "search_people", "read_resource", "get_granted_scopes",
        "teams_list_teams", "teams_list_channels", "teams_list_channel_messages", "teams_list_chats",
        "chat_message_search", "sharepoint_search", "sharepoint_folder_search",
    }.Select(t => $"mcp__claude_ai_Microsoft_365__{t}").ToArray();

    public static readonly IReadOnlyList<string> AllowedTools = new[]
    {
        "mcp__pokemate__read_skill",
        "mcp__pokemate__notes_search",
        "mcp__pokemate__notes_read",
        "mcp__pokemate__alarms_list",
        "mcp__pokemate__automation_history",
        "mcp__pokemate__work_sessions",
        "mcp__pokemate__usage_summary",
        "mcp__pokemate__jira_search",
        "mcp__pokemate__jira_projects",
        "mcp__pokemate__jira_my_worklogs",
        "mcp__pokemate__work_day_check",
        "mcp__pokemate__wiki_search",
        "mcp__pokemate__wiki_get_page",
        "mcp__pokemate__hr_holidays",
        "mcp__pokemate__hr_vacations",
        "mcp__pokemate__hr_flexwork_week",
        "mcp__pokemate__deploy_my_releases",
        "mcp__pokemate__deploy_approvals",
        "mcp__pokemate__deploy_domain_releases",
        "mcp__pokemate__deploy_release_search",
        "mcp__pokemate__github_review_requests",
    }.Concat(Microsoft365Tools).ToArray();

    /// 질문과 이미지는 인자가 아니라 표준 입력(`InputLine`)으로 보낸다.
    /// 고를 수 있는 모델 별칭과 추론 수준 (`claude --help` 의 --model, --effort)
    public static readonly IReadOnlyList<string> Models = new[] { "opus", "sonnet", "fable" };
    public static readonly IReadOnlyList<string> Efforts = new[] { "low", "medium", "high", "xhigh", "max" };

    /// 메이트가 읽지 않을 CLAUDE.md: 내 홈의 Claude Code 개인 지침 (claudeMdExcludes 설정).
    public static List<string> HomeRules(string? home = null)
    {
        var dir = Path.Combine(home ?? Paths.Home, ".claude", "rules");
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!)
            .Where(n => n.EndsWith(".md", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// claudeMdExcludes 패턴. Windows 홈은 `/` 로 적는다 (`C:/Users/u/.claude/**`).
    public static List<string> HomeExcludes(AppSettings settings, bool automation, string? home = null, IReadOnlyList<string>? rules = null)
    {
        var h = Paths.ForwardSlashes(home ?? Paths.Home);
        var all = new List<string> { $"{h}/.claude/**", $"{h}/CLAUDE.md" };
        if (!settings.UseHomeInstructions) return all;
        if (!automation) return settings.HomeInChat ? new List<string>() : all;
        if (!settings.HomeInAutomation) return all;
        var unchecked_ = (rules ?? HomeRules(home)).Where(r => !settings.AutomationRules.Contains(r));
        return new List<string> { $"{h}/.claude/CLAUDE.md", $"{h}/CLAUDE.md", $"{h}/.claude/rules/*/**" }
            .Concat(unchecked_.Select(r => $"{h}/.claude/rules/{r}")).ToList();
    }

    public static string HomeInstructionExcludes(string? home = null, IReadOnlyList<string>? patterns = null)
    {
        var h = Paths.ForwardSlashes(home ?? Paths.Home);
        patterns ??= new[] { $"{h}/.claude/**", $"{h}/CLAUDE.md" };
        var obj = new JsonObject { ["claudeMdExcludes"] = new JsonArray(patterns.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
        return obj.ToJsonString(Json.Compact);
    }

    public static readonly IReadOnlyList<string> WriteBaseTools = new[] { "Read", "Grep", "Glob" };

    public static string PermissionsFile => Path.Combine(AppPaths.WorkDir, ".claude", "settings.json");

    public static List<string> LoadDeny(string? file = null)
    {
        try
        {
            var obj = Json.ParseObject(File.ReadAllText(file ?? PermissionsFile));
            return Json.Strings(obj?["permissions"]?["deny"]) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// 맥 deny 목록 그대로 (Claude Code 의 Bash 는 Windows 에서 Git Bash 로 돌아 sudo 등도 뜻이 있다)
    static readonly string[] MacCommands =
    {
        "sudo", "su", "doas", "defaults write", "defaults delete", "scutil", "networksetup", "systemsetup",
        "pmset", "spctl", "csrutil", "nvram", "launchctl", "dscl", "security", "tccutil", "diskutil",
        "fdesetup", "profiles", "softwareupdate", "kextload", "shutdown", "reboot", "halt", "osascript",
    };

    /// Windows 시스템·보안 설정을 바꾸는 명령
    static readonly string[] WindowsCommands =
    {
        "runas", "reg add", "reg delete", "reg import", "regedit", "netsh", "bcdedit", "sc", "schtasks", "shutdown",
        "takeown", "icacls", "net user", "net localgroup", "wmic", "diskpart", "format", "cipher",
        "powershell -EncodedCommand", "Set-ExecutionPolicy",
    };

    /// 기본 deny. 명령 규칙은 `Bash(...)`·`PowerShell(...)` 양쪽에, 보호 파일은 Claude Code 의 Windows 경로 형식(`//c/…`)으로.
    public static List<string> DefaultDeny(string? dataDir = null, string? localDir = null)
    {
        dataDir ??= AppPaths.DataDir;
        localDir ??= AppPaths.LocalDir;
        var commands = MacCommands.Concat(WindowsCommands).Distinct().ToList();
        var guarded = new List<string>
        {
            "./.claude/**",
            Paths.ClaudePattern(Path.Combine(dataDir, "write-tools.json")),
            Paths.ClaudePattern(Path.Combine(dataDir, "alarms.json")),
            Paths.ClaudePattern(Path.Combine(dataDir, "settings.json")),
        };
        guarded.AddRange(new[] { "write-tools.json", "pipeline-tools.json", "domains.json" }.Select(n => Paths.ClaudePattern(Path.Combine(localDir, n))));
        return commands.Select(c => $"Bash({c}:*)")
            .Concat(commands.Select(c => $"PowerShell({c}:*)"))
            .Concat(guarded.SelectMany(g => new[] { $"Edit({g})", $"Write({g})" }))
            .ToList();
    }

    /// 권한 파일이 없으면 만들고, 있으면 빠진 기본 deny 만 뒤에 더한다. 바꿨으면 true.
    public static bool InstallPermissions(string? file = null, string? dataDir = null, string? localDir = null)
    {
        file ??= PermissionsFile;
        var defaults = DefaultDeny(dataDir, localDir);
        JsonObject? existing = null;
        try { if (File.Exists(file)) existing = Json.ParseObject(File.ReadAllText(file)); } catch { }
        if (existing != null)
        {
            var permissions = existing["permissions"] as JsonObject ?? new JsonObject();
            var deny = Json.Strings(permissions["deny"]) ?? new List<string>();
            var missing = defaults.Where(d => !deny.Contains(d)).ToList();
            if (missing.Count == 0) return false;
            permissions["deny"] = new JsonArray(deny.Concat(missing).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            existing["permissions"] = Json.Clone(permissions);
            return TryWrite(file, existing);
        }
        var content = new JsonObject
        {
            ["permissions"] = new JsonObject
            {
                ["deny"] = new JsonArray(defaults.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["ask"] = new JsonArray(),
                ["allow"] = new JsonArray(),
            },
        };
        return TryWrite(file, content);
    }

    static bool TryWrite(string file, JsonNode node)
    {
        try
        {
            AppPaths.WriteAtomic(file, Json.Sorted(node)!.ToJsonString(Json.Options));
            return true;
        }
        catch { return false; }
    }

    public static string WriteToolsFile => AppPaths.LocalFile("write-tools.json");

    public sealed record WriteAccess(IReadOnlyList<string> Tools, IReadOnlyList<string> Dirs)
    {
        public WriteAccess() : this(Array.Empty<string>(), Array.Empty<string>()) { }

        public bool Equals(WriteAccess? o) => o is not null && Tools.SequenceEqual(o.Tools) && Dirs.SequenceEqual(o.Dirs);
        public override int GetHashCode() => HashCode.Combine(Tools.Count, Dirs.Count);
    }

    /// `["Bash(x:*)"]` 또는 `{"tools": [...], "dirs": [...]}`. 폴더의 `~/` 는 홈 폴더로.
    public static WriteAccess LoadWriteAccess(string? file = null, string? home = null)
    {
        string text;
        try { text = File.ReadAllText(file ?? WriteToolsFile); } catch { return new WriteAccess(); }
        static List<string> Clean(IEnumerable<string> items) => items.Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();
        JsonNode? node;
        try { node = JsonNode.Parse(text); } catch { return new WriteAccess(); }
        if (Json.Strings(node) is { } tools) return new WriteAccess(Clean(tools), Array.Empty<string>());
        if (node is not JsonObject obj) return new WriteAccess();
        var t = Json.Strings(obj["tools"]);
        var d = Json.Strings(obj["dirs"]);
        if (t == null || d == null) return new WriteAccess();
        var dirs = Clean(d).Select(x => x.StartsWith("~/", StringComparison.Ordinal) || x.StartsWith("~\\", StringComparison.Ordinal) ? Paths.ExpandTilde(x, home) : x).ToList();
        return new WriteAccess(Clean(t), dirs);
    }

    public static List<string> LoadWriteTools(string? file = null) => LoadWriteAccess(file).Tools.ToList();

    public static List<string> Arguments(string? sessionId, string mcpConfigPath, string systemPrompt,
                                         string model = "", string effort = "", IReadOnlyList<string>? homeExcludes = null,
                                         IReadOnlyList<string>? writeTools = null, IReadOnlyList<string>? writeDirs = null,
                                         bool interactive = false, IReadOnlyList<string>? sessionAllow = null, string? home = null,
                                         string? newSessionId = null)
    {
        writeTools ??= Array.Empty<string>();
        writeDirs ??= Array.Empty<string>();
        homeExcludes ??= Array.Empty<string>();
        sessionAllow ??= Array.Empty<string>();
        var editTools = new[] { "Edit", "MultiEdit", "Write", "Skill" }.Where(name => writeTools.Any(t => t.StartsWith(name + "(", StringComparison.Ordinal)));
        var builtIn = interactive ? "default" : writeTools.Count == 0 ? "" : string.Join(",", WriteBaseTools.Append("Bash").Concat(editTools));
        var allowed = interactive ? AllowedTools.Concat(sessionAllow) : writeTools.Count == 0 ? AllowedTools : AllowedTools.Concat(writeTools);
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--mcp-config", mcpConfigPath,          // 메이트 MCP 서버. claude.ai 커넥터(Microsoft 365)도 함께 쓰려고 strict 는 쓰지 않는다
            "--tools", builtIn,                     // 파일 읽기·쓰기, 터미널 등 기본 도구를 모두 끈다
            "--allowedTools", string.Join(",", allowed),
            "--setting-sources", "project",         // 평소 쓰는 플러그인·훅(사용자 설정)을 불러오지 않는다
            "--append-system-prompt", systemPrompt,
        };
        if (interactive) args.AddRange(new[] { "--permission-prompts", "host", "--permission-prompt-tool", "stdio", "--add-dir", home ?? Paths.Home });
        else args.AddRange(new[] { "--permission-prompts", "none" });
        if (Models.Contains(model)) args.AddRange(new[] { "--model", model });
        if (Efforts.Contains(effort)) args.AddRange(new[] { "--effort", effort });
        if (homeExcludes.Count > 0) args.AddRange(new[] { "--settings", HomeInstructionExcludes(patterns: homeExcludes) });
        if (writeTools.Count > 0 && !interactive)
            foreach (var dir in writeDirs) args.AddRange(new[] { "--add-dir", dir });
        if (!string.IsNullOrEmpty(sessionId)) args.AddRange(new[] { "--resume", sessionId });
        else if (!string.IsNullOrEmpty(newSessionId)) args.AddRange(new[] { "--session-id", newSessionId });
        return args;
    }

    /// 표준 입력으로 보낼 사용자 메시지 한 줄 (이미지 먼저, 질문 뒤).
    public static string InputLine(string prompt, IReadOnlyList<ImageAttachment> images)
    {
        var content = new JsonArray();
        foreach (var image in images)
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
            });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });
        var message = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } };
        return message.ToJsonString(Json.Compact) + "\n";
    }
}
