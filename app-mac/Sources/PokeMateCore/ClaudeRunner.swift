import Foundation

/// `claude -p` 를 한 번 실행하고 결과를 해석한다.
public struct ToolHooks: Sendable {
    public let sessionAllow: [String]
    public let onActivity: @Sendable (String) -> Void
    public let onPermission: @Sendable (PermissionRequest) async -> PermissionDecision

    public init(sessionAllow: [String], onActivity: @escaping @Sendable (String) -> Void,
                onPermission: @escaping @Sendable (PermissionRequest) async -> PermissionDecision) {
        self.sessionAllow = sessionAllow
        self.onActivity = onActivity
        self.onPermission = onPermission
    }
}

public final class ClaudeRunner: @unchecked Sendable {
    public static let timeout: TimeInterval = 120

    private let process = ProcessRunner()
    private let interactive = InteractiveProcess()
    public private(set) var lastUsage: RunUsage?
    public static let interactiveTimeout: TimeInterval = 1800
    private var cancelled = false

    public init() {}

    public static let automationTimeout: TimeInterval = 1800

    public func run(prompt: String, images: [ImageAttachment] = [], sessionId: String?, settings: AppSettings,
                    writeTools: [String] = [], writeDirs: [String] = [], hooks: ToolHooks? = nil,
                    timeout: TimeInterval = ClaudeRunner.timeout, newSessionId: String? = nil, automation: Bool = true,
                    extraEnvironment: [String: String] = [:]) async -> ClaudeResponse {
        cancelled = false
        lastUsage = nil
        guard FileManager.default.isExecutableFile(atPath: settings.claudePath) else {
            return .failure(.notInstalled)
        }
        do {
            try AppPaths.prepare(settings: settings)
            var systemPrompt = try String(contentsOf: AppPaths.sharedFile("app/system.md", settings: settings), encoding: .utf8)
            systemPrompt += Self.nameLine(settings)
            systemPrompt += Self.ownerLine(settings)
            systemPrompt += LocalNames.load(repoDir: settings.repoDir).promptLine()
            systemPrompt += Rules.instructions()
            systemPrompt += Rules.prompt(for: prompt)
            let args = ClaudeCommand.arguments(sessionId: sessionId,
                                               mcpConfigPath: AppPaths.mcpConfig.path, systemPrompt: systemPrompt,
                                               model: automation ? settings.automationModel : settings.model,
                                               effort: automation ? settings.automationEffort : settings.effort,
                                               homeExcludes: ClaudeCommand.homeExcludes(settings: settings, automation: automation),
                                               writeTools: writeTools, writeDirs: writeDirs,
                                               interactive: hooks != nil, sessionAllow: hooks?.sessionAllow ?? [],
                                               newSessionId: newSessionId)
            let out: ProcessOutput
            if let hooks {
                out = try await interactive.run(executable: settings.claudePath, arguments: args,
                                                firstLine: ClaudeCommand.inputLine(prompt: prompt, images: images),
                                                workDir: AppPaths.workDir,
                                                environment: AppPaths.processEnvironment(settings: settings).merging(extraEnvironment) { _, new in new },
                                                timeout: max(timeout, Self.interactiveTimeout)) { line, io in
                    guard let object = ClaudeStream.object(line) else { return }
                    if let request = ClaudeStream.permissionRequest(object) {
                        let decision = await hooks.onPermission(request)
                        io.send(ClaudeStream.response(to: request, decision: decision))
                        return
                    }
                    for use in ClaudeStream.toolUses(object) {
                        hooks.onActivity(ClaudeStream.activity(tool: use.name, input: use.input))
                    }
                    if object["type"] as? String == "result" { io.closeInput() }
                }
            } else {
                out = try await process.run(executable: settings.claudePath, arguments: args,
                                            stdin: ClaudeCommand.inputLine(prompt: prompt, images: images),
                                            workDir: AppPaths.workDir,
                                            environment: AppPaths.processEnvironment(settings: settings).merging(extraEnvironment) { _, new in new },
                                            timeout: timeout)
            }
            lastUsage = RunUsage.parse(stdout: out.stdout)
            if cancelled { return .failure(.cancelled) }
            if out.timedOut { return .failure(.timeout) }
            let response = ClaudeResponse.parse(stdout: out.stdout, exitCode: out.exitCode)
            if case .failure(.malformed(let text)) = response, text.isEmpty {
                return .failure(.other(String(decoding: out.stderr, as: UTF8.self)))
            }
            return response
        } catch {
            return .failure(.other(error.localizedDescription))
        }
    }

    public static func nameLine(_ settings: AppSettings) -> String {
        if let pokemon = settings.mateName?.trimmingCharacters(in: .whitespacesAndNewlines), !pokemon.isEmpty {
            return "\n- 너는 지금 주인의 대표 포켓몬 \"\(pokemon)\"이다. \"메이트\" 대신 자신을 \"\(pokemon)\"(이)라고 부른다.\n"
        }
        let name = settings.assistantName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty && name != AppSettings.defaultAssistantName else { return "" }
        return "\n- 너의 이름은 \"\(name)\"이다. \"메이트\" 대신 자신을 \"\(name)\"(이)라고 부른다.\n"
    }

    public static func ownerLine(_ settings: AppSettings) -> String {
        let call = settings.ownerCallName
        guard !call.isEmpty else { return "" }
        return "\n- 주인(사용자)을 부를 때는 \"\(call)\"이라고 부른다. 다른 말을 덧붙이지 않는다.\n"
    }

    public func cancel() {
        cancelled = true
        process.cancel()
        interactive.cancel()
    }
}
