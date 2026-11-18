import Foundation

public struct ShellResult: Equatable, Sendable {
    public let exitCode: Int32
    public let timedOut: Bool
    public let tail: String

    public var ok: Bool { exitCode == 0 && !timedOut }

    public init(exitCode: Int32, timedOut: Bool, tail: String) {
        self.exitCode = exitCode
        self.timedOut = timedOut
        self.tail = tail
    }
}

public enum ShellCommand {
    public static let timeout: TimeInterval = 300

    public static func tail(_ text: String, lines: Int = 5, limit: Int = 600) -> String {
        let kept = text.split(separator: "\n", omittingEmptySubsequences: true).suffix(lines).joined(separator: "\n")
        return kept.count > limit ? "…" + String(kept.suffix(limit)) : kept
    }

    public static func run(_ command: String, timeout: TimeInterval = timeout, home: String = NSHomeDirectory()) async -> ShellResult {
        do {
            let out = try await ProcessRunner().run(executable: "/bin/zsh", arguments: ["-lc", command],
                                                     workDir: URL(fileURLWithPath: home), timeout: timeout)
            let text = String(decoding: out.stdout, as: UTF8.self) + "\n" + String(decoding: out.stderr, as: UTF8.self)
            return ShellResult(exitCode: out.exitCode, timedOut: out.timedOut,
                               tail: out.timedOut ? "제한 시간 \(Int(timeout / 60))분을 넘겨 멈췄어요\n" + tail(text) : tail(text))
        } catch {
            return ShellResult(exitCode: -1, timedOut: false, tail: error.localizedDescription)
        }
    }
}
