import Foundation

/// `claude -p` 실행 인자. 허용 도구 목록(메이트 MCP + Microsoft 365 조회)만 쓰도록 제한한다.
public enum ClaudeCommand {
    public static let allowedTools = [
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
    ] + microsoft365Tools

    /// claude.ai 의 Microsoft 365 커넥터 (메일·일정·Teams·SharePoint). 모두 조회용 도구다.
    public static let microsoft365Tools = [
        "get_me", "outlook_email_search", "outlook_calendar_search", "outlook_find_available_time",
        "find_meeting_availability", "search_people", "read_resource", "get_granted_scopes",
        "teams_list_teams", "teams_list_channels", "teams_list_channel_messages", "teams_list_chats",
        "chat_message_search", "sharepoint_search", "sharepoint_folder_search",
    ].map { "mcp__claude_ai_Microsoft_365__\($0)" }

    /// 질문과 이미지는 인자가 아니라 표준 입력(`inputLine`)으로 보낸다.
    /// 고를 수 있는 모델 별칭과 추론 수준 (`claude --help` 의 --model, --effort)
    public static let models = ["opus", "sonnet", "fable"]
    public static let efforts = ["low", "medium", "high", "xhigh", "max"]

    /// 메이트가 읽지 않을 CLAUDE.md: 내 홈의 Claude Code 개인 지침 (claudeMdExcludes 설정).
    public static func homeRules(home: String = NSHomeDirectory()) -> [String] {
        let dir = URL(fileURLWithPath: home).appendingPathComponent(".claude/rules", isDirectory: true)
        return ((try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []).filter { $0.hasSuffix(".md") }.sorted()
    }

    public static func homeExcludes(settings: AppSettings, automation: Bool, home: String = NSHomeDirectory(), rules: [String]? = nil) -> [String] {
        let all = ["\(home)/.claude/**", "\(home)/CLAUDE.md"]
        guard settings.useHomeInstructions else { return all }
        if !automation { return settings.homeInChat ? [] : all }
        guard settings.homeInAutomation else { return all }
        let unchecked = (rules ?? homeRules(home: home)).filter { !settings.automationRules.contains($0) }
        return ["\(home)/.claude/CLAUDE.md", "\(home)/CLAUDE.md", "\(home)/.claude/rules/*/**"] + unchecked.map { "\(home)/.claude/rules/\($0)" }
    }

    public static func homeInstructionExcludes(home: String = NSHomeDirectory(), patterns: [String]? = nil) -> String {
        let patterns = patterns ?? ["\(home)/.claude/**", "\(home)/CLAUDE.md"]
        let data = try! JSONSerialization.data(withJSONObject: ["claudeMdExcludes": patterns], options: [.sortedKeys, .withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self)
    }

    public static let writeBaseTools = ["Read", "Grep", "Glob"]

    public static var permissionsFile: URL { AppPaths.workDir.appendingPathComponent(".claude/settings.json") }

    public static func loadDeny(from file: URL = permissionsFile) -> [String] {
        guard let data = try? Data(contentsOf: file),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let permissions = object["permissions"] as? [String: Any] else { return [] }
        return permissions["deny"] as? [String] ?? []
    }

    public static func defaultDeny(dataDir: URL = AppPaths.dataDir, localDir: URL = AppPaths.localDir) -> [String] {
        let commands = ["sudo", "su", "doas", "defaults write", "defaults delete", "scutil", "networksetup", "systemsetup",
                        "pmset", "spctl", "csrutil", "nvram", "launchctl", "dscl", "security", "tccutil", "diskutil",
                        "fdesetup", "profiles", "softwareupdate", "kextload", "shutdown", "reboot", "halt", "osascript"]
        let guarded = ["./.claude/**", "/" + dataDir.appendingPathComponent("write-tools.json").path,
                       "/" + dataDir.appendingPathComponent("alarms.json").path,
                       "/" + dataDir.appendingPathComponent("settings.json").path]
            + ["write-tools.json", "pipeline-tools.json", "domains.json"].map { "/" + localDir.appendingPathComponent($0).path }
        return commands.map { "Bash(\($0):*)" } + guarded.flatMap { ["Edit(\($0))", "Write(\($0))"] }
    }

    @discardableResult
    public static func installPermissions(at file: URL = permissionsFile, dataDir: URL = AppPaths.dataDir, localDir: URL = AppPaths.localDir) -> Bool {
        let defaults = defaultDeny(dataDir: dataDir, localDir: localDir)
        if let data = try? Data(contentsOf: file), var object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            var permissions = object["permissions"] as? [String: Any] ?? [:]
            let deny = permissions["deny"] as? [String] ?? []
            let missing = defaults.filter { !deny.contains($0) }
            guard !missing.isEmpty else { return false }
            permissions["deny"] = deny + missing
            object["permissions"] = permissions
            guard let merged = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return false }
            return (try? merged.write(to: file)) != nil
        }
        let content: [String: Any] = ["permissions": ["deny": defaults, "ask": [String](), "allow": [String]()]]
        guard let data = try? JSONSerialization.data(withJSONObject: content, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else { return false }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        return (try? data.write(to: file)) != nil
    }

    public static var writeToolsFile: URL { AppPaths.localFile("write-tools.json") }

    public struct WriteAccess: Codable, Equatable, Sendable {
        public var tools: [String]
        public var dirs: [String]
        public init(tools: [String] = [], dirs: [String] = []) { self.tools = tools; self.dirs = dirs }
    }

    public static func loadWriteAccess(from file: URL = writeToolsFile, home: String = NSHomeDirectory()) -> WriteAccess {
        guard let data = try? Data(contentsOf: file) else { return WriteAccess() }
        let clean: ([String]) -> [String] = { $0.map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } }
        if let tools = try? JSONDecoder().decode([String].self, from: data) { return WriteAccess(tools: clean(tools)) }
        guard let access = try? JSONDecoder().decode(WriteAccess.self, from: data) else { return WriteAccess() }
        let dirs = clean(access.dirs).map { $0.hasPrefix("~/") ? home + "/" + $0.dropFirst(2) : $0 }
        return WriteAccess(tools: clean(access.tools), dirs: dirs)
    }

    public static func loadWriteTools(from file: URL = writeToolsFile) -> [String] {
        loadWriteAccess(from: file).tools
    }

    public static func arguments(sessionId: String?, mcpConfigPath: String, systemPrompt: String,
                                 model: String = "", effort: String = "", homeExcludes: [String] = [],
                                 writeTools: [String] = [], writeDirs: [String] = [],
                                 interactive: Bool = false, sessionAllow: [String] = [], home: String = NSHomeDirectory(),
                                 newSessionId: String? = nil) -> [String] {
        let editTools = ["Edit", "MultiEdit", "Write", "Skill"].filter { name in writeTools.contains { $0.hasPrefix(name + "(") } }
        let builtIn = interactive ? "default" : writeTools.isEmpty ? "" : (writeBaseTools + ["Bash"] + editTools).joined(separator: ",")
        let allowed = interactive ? allowedTools + sessionAllow : writeTools.isEmpty ? allowedTools : allowedTools + writeTools
        var args = [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--mcp-config", mcpConfigPath,         // 메이트 MCP 서버. claude.ai 커넥터(Microsoft 365)도 함께 쓰려고 strict 는 쓰지 않는다
            "--tools", builtIn,                  // 파일 읽기·쓰기, 터미널 등 기본 도구를 모두 끈다
            "--allowedTools", allowed.joined(separator: ","),
            "--setting-sources", "project",       // 평소 쓰는 플러그인·훅(사용자 설정)을 불러오지 않는다
            "--append-system-prompt", systemPrompt,
        ]
        if interactive {
            args += ["--permission-prompts", "host", "--permission-prompt-tool", "stdio", "--add-dir", home]
        } else {
            args += ["--permission-prompts", "none"]
        }
        if models.contains(model) { args += ["--model", model] }
        if efforts.contains(effort) { args += ["--effort", effort] }
        if !homeExcludes.isEmpty { args += ["--settings", homeInstructionExcludes(patterns: homeExcludes)] }
        if !writeTools.isEmpty && !interactive {
            for dir in writeDirs { args += ["--add-dir", dir] }
        }
        if let sessionId, !sessionId.isEmpty {
            args += ["--resume", sessionId]
        } else if let newSessionId, !newSessionId.isEmpty {
            args += ["--session-id", newSessionId]
        }
        return args
    }

    /// 표준 입력으로 보낼 사용자 메시지 한 줄 (이미지 먼저, 질문 뒤).
    public static func inputLine(prompt: String, images: [ImageAttachment]) -> String {
        var content: [[String: Any]] = images.map {
            ["type": "image", "source": ["type": "base64", "media_type": $0.mediaType, "data": $0.data.base64EncodedString()]]
        }
        content.append(["type": "text", "text": prompt])
        let message: [String: Any] = ["type": "user", "message": ["role": "user", "content": content]]
        let data = (try? JSONSerialization.data(withJSONObject: message)) ?? Data()
        return String(decoding: data, as: UTF8.self) + "\n"
    }
}
