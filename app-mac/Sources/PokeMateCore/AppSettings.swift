import Foundation

/// 앱 설정과 경로. `~/Library/Application Support/poke-mate/settings.json` 에 저장한다.
public struct AppSettings: Codable, Equatable {
    /// 비서 이름. 화면 제목과 비서의 자기 소개에 쓴다.
    public var assistantName: String
    public var mateName: String?
    public var repoDir: String
    public var claudePath: String
    public var uvPath: String
    public var reportHour: Int
    public var reportMinute: Int
    public var lastReportDay: String?
    public var hotkey: Hotkey = .default
    /// 주인(사용자) 이름. 비어 있으면 처음 사용으로 보고 채팅창에서 이름을 묻는다.
    public var ownerName = ""
    /// Claude 모델 별칭 (opus, sonnet, fable). 비우면 계정 기본
    public var model = ""
    /// 추론 수준 (low, medium, high, xhigh, max). 비우면 기본
    public var effort = ""
    /// 내 홈의 Claude Code 개인 지침(~/.claude/CLAUDE.md, rules/)도 읽을지. 기본은 끔 (개발용 지침이 비서 답에 섞이지 않게)
    public var useHomeInstructions = false
    public var homeInChat = true
    public var homeInAutomation = true
    public var automationRules = AppSettings.defaultAutomationRules
    public var shareHomeSkills = true
    public var automationModel = ""
    public var automationEffort = ""
    public var pokedexRange: PokedexRange = .gen1
    public var claudeTools = false
    public var workSessionAlertPercent = 70

    public static let defaultAssistantName = "메이트"
    public static let defaultAutomationRules = ["commit-pr.md", "domain-knowledge.md", "production-env.md"]

    public var ownerCallName: String {
        let name = ownerName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return "" }
        return name.hasSuffix("님") ? name : name + "님"
    }

    enum CodingKeys: String, CodingKey {
        case assistantName, repoDir, claudePath, uvPath, reportHour, reportMinute, lastReportDay, hotkey, ownerName, model, effort, useHomeInstructions, pokedexRange, claudeTools, workSessionAlertPercent,
             homeInChat, homeInAutomation, automationRules, shareHomeSkills, automationModel, automationEffort
    }

    public static let `default` = AppSettings(
        assistantName: defaultAssistantName,
        repoDir: NSString(string: "~/Workspace/poke-mate-ai-agent").expandingTildeInPath,
        claudePath: NSString(string: "~/.local/bin/claude").expandingTildeInPath,
        uvPath: "/opt/homebrew/bin/uv",
        reportHour: 10,
        reportMinute: 0,
        lastReportDay: nil
    )

    public init(assistantName: String, repoDir: String, claudePath: String, uvPath: String,
                reportHour: Int, reportMinute: Int, lastReportDay: String?) {
        self.assistantName = assistantName
        self.repoDir = repoDir
        self.claudePath = claudePath
        self.uvPath = uvPath
        self.reportHour = reportHour
        self.reportMinute = reportMinute
        self.lastReportDay = lastReportDay
    }

    /// 예전 설정 파일에 없는 항목은 기본값으로 채운다.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AppSettings.default
        assistantName = try c.decodeIfPresent(String.self, forKey: .assistantName) ?? d.assistantName
        repoDir = try c.decodeIfPresent(String.self, forKey: .repoDir) ?? d.repoDir
        claudePath = try c.decodeIfPresent(String.self, forKey: .claudePath) ?? d.claudePath
        uvPath = try c.decodeIfPresent(String.self, forKey: .uvPath) ?? d.uvPath
        reportHour = try c.decodeIfPresent(Int.self, forKey: .reportHour) ?? d.reportHour
        reportMinute = try c.decodeIfPresent(Int.self, forKey: .reportMinute) ?? d.reportMinute
        lastReportDay = try c.decodeIfPresent(String.self, forKey: .lastReportDay)
        hotkey = try c.decodeIfPresent(Hotkey.self, forKey: .hotkey) ?? .default
        ownerName = try c.decodeIfPresent(String.self, forKey: .ownerName) ?? ""
        model = try c.decodeIfPresent(String.self, forKey: .model) ?? ""
        effort = try c.decodeIfPresent(String.self, forKey: .effort) ?? ""
        useHomeInstructions = try c.decodeIfPresent(Bool.self, forKey: .useHomeInstructions) ?? false
        homeInChat = try c.decodeIfPresent(Bool.self, forKey: .homeInChat) ?? useHomeInstructions
        homeInAutomation = try c.decodeIfPresent(Bool.self, forKey: .homeInAutomation) ?? useHomeInstructions
        automationRules = try c.decodeIfPresent([String].self, forKey: .automationRules) ?? AppSettings.defaultAutomationRules
        shareHomeSkills = try c.decodeIfPresent(Bool.self, forKey: .shareHomeSkills) ?? true
        automationModel = try c.decodeIfPresent(String.self, forKey: .automationModel) ?? model
        automationEffort = try c.decodeIfPresent(String.self, forKey: .automationEffort) ?? effort
        pokedexRange = (try? c.decodeIfPresent(PokedexRange.self, forKey: .pokedexRange)) ?? .gen1
        claudeTools = try c.decodeIfPresent(Bool.self, forKey: .claudeTools) ?? false
        workSessionAlertPercent = try c.decodeIfPresent(Int.self, forKey: .workSessionAlertPercent) ?? 70
    }
}

public enum AppPaths {
    public static var dataDir: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("poke-mate", isDirectory: true)
    }
    public static var workDir: URL { dataDir.appendingPathComponent("work", isDirectory: true) }
    /// 메이트 전용 Claude 로그인 정보가 들어가는 폴더. 터미널 Claude Code 로그인과 따로 쓴다.
    public static var claudeConfigDir: URL { dataDir.appendingPathComponent("claude-config", isDirectory: true) }
    public static var mcpConfig: URL { dataDir.appendingPathComponent("mcp-config.json") }
    public static var settingsFile: URL { dataDir.appendingPathComponent("settings.json") }
    public static var chatFile: URL { dataDir.appendingPathComponent("chat.json") }

    public static var repoDir: URL { URL(fileURLWithPath: loadSettings().repoDir, isDirectory: true) }
    public static var sharedDir: URL { repoDir.appendingPathComponent("shared", isDirectory: true) }
    public static var localDir: URL { repoDir.appendingPathComponent("local", isDirectory: true) }

    public static func isDirectory(_ url: URL) -> Bool {
        var dir: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &dir) && dir.boolValue
    }

    public static var localReady: Bool { isDirectory(localDir) }

    public static func localFile(_ name: String) -> URL { localDir.appendingPathComponent(name) }

    @discardableResult
    public static func makeLocalFromExample(repo: URL = repoDir) -> Bool {
        let local = repo.appendingPathComponent("local", isDirectory: true)
        let example = repo.appendingPathComponent("local.example", isDirectory: true)
        guard !isDirectory(local), isDirectory(example) else { return false }
        return (try? FileManager.default.copyItem(at: example, to: local)) != nil
    }

    public static func sharedFile(_ name: String, settings: AppSettings) -> URL {
        URL(fileURLWithPath: settings.repoDir).appendingPathComponent("shared").appendingPathComponent(name)
    }

    /// 데이터 폴더를 만들고, 템플릿으로 실제 MCP 설정 파일을 다시 쓴다.
    public static func prepare(settings: AppSettings) throws {
        try FileManager.default.createDirectory(at: workDir, withIntermediateDirectories: true)
        ClaudeCommand.installPermissions()
        HomeSkills.sync(enabled: settings.shareHomeSkills)
        let template = try String(contentsOf: sharedFile("mcp-config.template.json", settings: settings), encoding: .utf8)
        let config = template
            .replacingOccurrences(of: "{{UV_PATH}}", with: settings.uvPath)
            .replacingOccurrences(of: "{{MCP_SERVER_DIR}}", with: settings.repoDir + "/mcp-server")
        try config.write(to: mcpConfig, atomically: true, encoding: .utf8)
    }

    public static func loadSettings() -> AppSettings {
        guard let data = try? Data(contentsOf: settingsFile),
              let settings = try? JSONDecoder().decode(AppSettings.self, from: data) else { return .default }
        return settings
    }

    public static func saveSettings(_ settings: AppSettings) {
        try? FileManager.default.createDirectory(at: dataDir, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? encoder.encode(settings).write(to: settingsFile, options: .atomic)
    }

    /// GUI 앱은 터미널과 PATH 가 달라서, 하위 프로세스에 쓸 PATH 를 직접 만든다.
    public static func processEnvironment(settings: AppSettings) -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        let extra = [
            URL(fileURLWithPath: settings.uvPath).deletingLastPathComponent().path,
            URL(fileURLWithPath: settings.claudePath).deletingLastPathComponent().path,
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",
        ]
        env["PATH"] = (extra + [env["PATH"] ?? ""]).filter { !$0.isEmpty }.joined(separator: ":")
        // 메이트는 항상 전용 로그인을 쓴다 (터미널 Claude Code 와 다른 계정·조직 가능)
        try? FileManager.default.createDirectory(at: claudeConfigDir, withIntermediateDirectories: true)
        env["CLAUDE_CONFIG_DIR"] = claudeConfigDir.path
        return env
    }
}

public enum HomeSkills {
    public static func source(home: String = NSHomeDirectory()) -> URL {
        URL(fileURLWithPath: home).appendingPathComponent(".claude/skills", isDirectory: true)
    }

    public static var target: URL { AppPaths.workDir.appendingPathComponent(".claude/skills", isDirectory: true) }

    public static func names(home: String = NSHomeDirectory()) -> [String] {
        let fm = FileManager.default
        let dir = source(home: home)
        return ((try? fm.contentsOfDirectory(atPath: dir.path)) ?? []).filter { name in
            !name.hasPrefix(".") && fm.fileExists(atPath: dir.appendingPathComponent(name).appendingPathComponent("SKILL.md").path)
        }.sorted()
    }

    @discardableResult
    public static func sync(enabled: Bool, home: String = NSHomeDirectory(), target: URL = target) -> (linked: [String], removed: [String]) {
        let fm = FileManager.default
        let base = source(home: home).path + "/"
        let wanted = enabled ? names(home: home) : []
        var linked: [String] = [], removed: [String] = []
        try? fm.createDirectory(at: target, withIntermediateDirectories: true)
        for name in (try? fm.contentsOfDirectory(atPath: target.path)) ?? [] {
            let path = target.appendingPathComponent(name).path
            guard let dest = try? fm.destinationOfSymbolicLink(atPath: path), dest.hasPrefix(base) else { continue }
            if !wanted.contains(name) || dest != base + name {
                try? fm.removeItem(atPath: path)
                removed.append(name)
            }
        }
        for name in wanted {
            let path = target.appendingPathComponent(name).path
            if (try? fm.attributesOfItem(atPath: path)) != nil { continue }
            if (try? fm.createSymbolicLink(atPath: path, withDestinationPath: base + name)) != nil { linked.append(name) }
        }
        return (linked, removed)
    }
}
