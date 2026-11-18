import Foundation

public struct PermissionRequest: Identifiable, Equatable, Sendable {
    public let id: String
    public let tool: String
    public let summary: String
    public let detail: String
    public let rules: [String]
    let input: Data
    let suggestions: Data

    public var inputObject: [String: Any] { (try? JSONSerialization.jsonObject(with: input) as? [String: Any]) ?? [:] }

    public init(id: String, tool: String, summary: String, detail: String, rules: [String], input: Data, suggestions: Data) {
        self.id = id
        self.tool = tool
        self.summary = summary
        self.detail = detail
        self.rules = rules
        self.input = input
        self.suggestions = suggestions
    }
}

public struct PermissionAlertGate: Sendable {
    public static let quiet: TimeInterval = 5
    public static let notificationId = "pokemate-permission"
    private var last: Date?

    public init() {}

    public mutating func shouldAlert(now: Date) -> Bool {
        if let last, now.timeIntervalSince(last) < Self.quiet { return false }
        last = now
        return true
    }

    public mutating func reset() { last = nil }

    public static func body(for request: PermissionRequest, limit: Int = 100) -> String {
        let line = "\(request.tool): \(request.summary)".replacingOccurrences(of: "\n", with: " ")
        return line.count > limit ? String(line.prefix(limit)) + "…" : line
    }
}

public enum PermissionDecision: Equatable, Sendable {
    case allow, allowAlways, allowRules([String]), deny(String)
}

public enum ClaudeStream {
    public static func object(_ line: String) -> [String: Any]? {
        try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any]
    }

    static func text(_ input: [String: Any], _ key: String) -> String {
        (input[key] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    static func short(_ s: String, _ limit: Int = 120) -> String {
        let one = s.replacingOccurrences(of: "\n", with: " ⏎ ")
        return one.count > limit ? String(one.prefix(limit)) + "…" : one
    }

    public static func target(tool: String, input: [String: Any]) -> String {
        for key in ["command", "file_path", "notebook_path", "url", "query", "pattern", "path", "description"] {
            let value = text(input, key)
            if !value.isEmpty { return short(value) }
        }
        return ""
    }

    public static func activity(tool: String, input: [String: Any]) -> String {
        let verb: String
        switch tool {
        case "Bash": verb = "실행 중"
        case "Read", "NotebookRead": verb = "읽는 중"
        case "Edit", "MultiEdit", "NotebookEdit": verb = "수정 중"
        case "Write": verb = "쓰는 중"
        case "Grep", "Glob": verb = "찾는 중"
        case "WebFetch": verb = "가져오는 중"
        case "WebSearch": verb = "검색 중"
        default: verb = tool.hasPrefix("mcp__") ? "조회 중" : "실행 중"
        }
        let name = tool.hasPrefix("mcp__") ? String(tool.split(separator: "__").last ?? Substring(tool)) : tool
        let target = target(tool: tool, input: input)
        return target.isEmpty ? "\(name) \(verb)" : "\(name) \(verb): \(target)"
    }

    public static func detail(tool: String, input: [String: Any]) -> String {
        switch tool {
        case "Edit":
            let old = text(input, "old_string"), new = text(input, "new_string")
            return "바꿀 부분 \(old.split(separator: "\n").count)줄 → \(new.split(separator: "\n").count)줄\n- \(short(old, 160))\n+ \(short(new, 160))"
        case "MultiEdit":
            return "수정 \((input["edits"] as? [Any])?.count ?? 0)곳"
        case "Write":
            let content = text(input, "content")
            return "\(content.split(separator: "\n").count)줄을 씀\n\(short(content, 200))"
        case "Bash":
            let description = text(input, "description")
            return description
        default:
            guard let data = try? JSONSerialization.data(withJSONObject: input, options: [.sortedKeys]) else { return "" }
            return short(String(decoding: data, as: UTF8.self), 240)
        }
    }

    public static func permissionRequest(_ object: [String: Any]) -> PermissionRequest? {
        guard object["type"] as? String == "control_request", let id = object["request_id"] as? String,
              let request = object["request"] as? [String: Any], request["subtype"] as? String == "can_use_tool",
              let tool = request["tool_name"] as? String else { return nil }
        let input = request["input"] as? [String: Any] ?? [:]
        let suggestions = (request["permission_suggestions"] as? [[String: Any]] ?? []).filter {
            $0["type"] as? String == "addRules" && $0["behavior"] as? String == "allow"
        }
        let rules = suggestions.flatMap { ($0["rules"] as? [[String: Any]]) ?? [] }.compactMap { rule -> String? in
            guard let name = rule["toolName"] as? String else { return nil }
            let content = (rule["ruleContent"] as? String) ?? ""
            return content.isEmpty ? name : "\(name)(\(content))"
        }
        let summary = target(tool: tool, input: input)
        return PermissionRequest(id: id, tool: tool, summary: summary.isEmpty ? tool : summary,
                                 detail: detail(tool: tool, input: input), rules: rules,
                                 input: (try? JSONSerialization.data(withJSONObject: input)) ?? Data("{}".utf8),
                                 suggestions: (try? JSONSerialization.data(withJSONObject: suggestions)) ?? Data("[]".utf8))
    }

    public static func response(to request: PermissionRequest, decision: PermissionDecision) -> String {
        var answer: [String: Any]
        switch decision {
        case .deny(let message):
            answer = ["behavior": "deny", "message": message]
        case .allowRules(let rules):
            answer = ["behavior": "allow", "updatedInput": (try? JSONSerialization.jsonObject(with: request.input)) ?? [:]]
            let parsed = rules.map { rule -> [String: Any] in
                let parts = AllowRules.split(rule: rule)
                var item: [String: Any] = ["toolName": parts.tool]
                if let content = parts.content { item["ruleContent"] = content }
                return item
            }
            if !parsed.isEmpty {
                answer["updatedPermissions"] = [["type": "addRules", "rules": parsed, "behavior": "allow", "destination": "session"]]
            }
        case .allow, .allowAlways:
            answer = ["behavior": "allow", "updatedInput": (try? JSONSerialization.jsonObject(with: request.input)) ?? [:]]
            if decision == .allowAlways, let suggestions = try? JSONSerialization.jsonObject(with: request.suggestions) as? [[String: Any]],
               !suggestions.isEmpty {
                answer["updatedPermissions"] = suggestions.map { s -> [String: Any] in
                    var copy = s
                    copy["destination"] = "session"
                    return copy
                }
            }
        }
        let message: [String: Any] = ["type": "control_response",
                                      "response": ["subtype": "success", "request_id": request.id, "response": answer]]
        let data = (try? JSONSerialization.data(withJSONObject: message)) ?? Data()
        return String(decoding: data, as: UTF8.self) + "\n"
    }

    public static func toolUses(_ object: [String: Any]) -> [(name: String, input: [String: Any])] {
        guard object["type"] as? String == "assistant", let message = object["message"] as? [String: Any],
              let content = message["content"] as? [[String: Any]] else { return [] }
        return content.compactMap { c in
            guard c["type"] as? String == "tool_use", let name = c["name"] as? String else { return nil }
            return (name, c["input"] as? [String: Any] ?? [:])
        }
    }
}

public final class InteractiveProcess: @unchecked Sendable {
    private let lock = NSLock()
    private var current: Process?
    private var input: FileHandle?

    public init() {}

    public func run(executable: String, arguments: [String], firstLine: String, workDir: URL?,
                    environment: [String: String]?, timeout: TimeInterval,
                    onLine: @escaping @Sendable (String, InteractiveProcess) async -> Void) async throws -> ProcessOutput {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        if let workDir { process.currentDirectoryURL = workDir }
        if let environment { process.environment = environment }
        let outPipe = Pipe(), errPipe = Pipe(), inPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        process.standardInput = inPipe

        var errData = Data()
        let errGroup = DispatchGroup()
        errGroup.enter()
        DispatchQueue.global().async { errData = errPipe.fileHandleForReading.readDataToEndOfFile(); errGroup.leave() }

        let lines = AsyncStream<String> { continuation in
            DispatchQueue.global().async {
                let handle = outPipe.fileHandleForReading
                var buffer = Data()
                while true {
                    let chunk = handle.availableData
                    if chunk.isEmpty { break }
                    buffer.append(chunk)
                    while let newline = buffer.firstIndex(of: 0x0A) {
                        continuation.yield(String(decoding: buffer[buffer.startIndex..<newline], as: UTF8.self))
                        buffer.removeSubrange(buffer.startIndex...newline)
                    }
                }
                if !buffer.isEmpty { continuation.yield(String(decoding: buffer, as: UTF8.self)) }
                continuation.finish()
            }
        }

        try process.run()
        lock.withLock {
            current = process
            input = inPipe.fileHandleForWriting
        }
        send(firstLine)

        let timedOut = Flag()
        let watchdog = Task {
            try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
            if !Task.isCancelled && process.isRunning {
                timedOut.set()
                process.terminate()
            }
        }
        var outData = Data()
        for await line in lines {
            outData.append(Data((line + "\n").utf8))
            await onLine(line, self)
        }
        watchdog.cancel()
        closeInput()
        process.waitUntilExit()
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            errGroup.notify(queue: .global()) { done.resume() }
        }
        lock.withLock { current = nil }
        return ProcessOutput(stdout: outData, stderr: errData, exitCode: process.terminationStatus, timedOut: timedOut.value)
    }

    public func send(_ line: String) {
        lock.withLock { input }?.write(Data(line.utf8))
    }

    public func closeInput() {
        let handle: FileHandle? = lock.withLock {
            let h = input
            input = nil
            return h
        }
        try? handle?.close()
    }

    public func cancel() {
        lock.withLock { current }?.terminate()
    }
}

final class Flag: @unchecked Sendable {
    private let lock = NSLock()
    private var on = false
    func set() { lock.withLock { on = true } }
    var value: Bool { lock.withLock { on } }
}
