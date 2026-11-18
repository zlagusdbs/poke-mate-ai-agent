import Foundation
import PokeMateCore

let args = CommandLine.arguments
guard args.count >= 5, ProcessInfo.processInfo.environment["POKEMATE_TRIAL"] != nil else {
    print("usage: POKEMATE_TRIAL=<folder> PokeMateTrial <folder> <KEY> <domain> <repo> [promptsDir]")
    exit(2)
}
let root = URL(fileURLWithPath: args[1])
let key = args[2], domain = args[3], repo = args[4]
let promptsDir = URL(fileURLWithPath: args.count > 5 ? args[5] : root.appendingPathComponent("prompts").path)
enum Trial {
    static let settings = AppPaths.loadSettings()

    static func line(_ text: String) {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        print("[\(f.string(from: Date()))] \(text)")
        fflush(stdout)
    }
}
let settings = Trial.settings
let helperText = PipelineAccess.helper(settings: settings)
let pipelinesDir = root.appendingPathComponent("pipelines")
let bookFile = root.appendingPathComponent("sessions.json")
let table = PipelineAccess.defaults(helper: helperText, workDir: root.appendingPathComponent("workspace").path,
                                    references: NSHomeDirectory() + "/.claude/references", pipelines: pipelinesDir.path)
var config = PipelineConfig.load(promptsDir: promptsDir, access: table, dir: pipelinesDir, helper: helperText)
config.domains = DomainMap.load(from: root.appendingPathComponent("domains.json"))
let issue = (try? JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("jira/\(key).json")))) as? [String: Any]
let summary = (((issue?["issue"] as? [String: Any])?["fields"] as? [String: Any])?["summary"] as? String) ?? key
let event = "다음 이벤트가 생겼다.\n- [\(key)] \(summary)\n- 나에게 할당된 이슈\n- 링크: https://jira.example.com/browse/\(key)"

final class Book: @unchecked Sendable {
    let lock = NSLock()
    var book: WorkSessionBook
    let file: URL
    init(file: URL) { self.file = file; book = WorkSessionBook.load(from: file) }
    func id(_ k: String) -> String? { lock.withLock { book.resumeId(k) } }
    func record(_ k: String, _ id: String, _ ticket: String, _ usage: RunUsage?) {
        lock.withLock {
            book.record(repo: k, sessionId: id, key: ticket, usage: usage, now: Date())
            book.save(to: file)
        }
    }
}
let book = Book(file: bookFile)

let io = PipelineIO(
    runRole: { role, prompt, sessionId, newSessionId, access in
        Trial.line("\(role.label) 시작 (세션 \(sessionId ?? newSessionId ?? "-") · \(sessionId == nil ? "새로" : "이어 쓰기")\(prompt.hasPrefix(JiraPipeline.interruptedNote) ? " · 끊긴 실행 이어 가기" : ""))")
        let runner = ClaudeRunner()
        let response = await runner.run(prompt: prompt, sessionId: sessionId, settings: Trial.settings, writeTools: access.tools, writeDirs: access.dirs,
                                         timeout: 1800, newSessionId: newSessionId)
        switch response {
        case .success(let text, let id):
            Trial.line("\(role.label) 끝 · \(TokenFormat.usd(runner.lastUsage?.costUSD ?? 0))")
            return PipelineRoleResult(text: text, sessionId: id.isEmpty ? nil : id, usage: runner.lastUsage)
        case .failure(let failure):
            Trial.line("\(role.label) 실패 · \(failure.message)")
            return PipelineRoleResult(usage: runner.lastUsage, failure: failure.message)
        }
    },
    helper: { helperArgs in
        let base = ["--directory", Trial.settings.repoDir + "/mcp-server", "run", "--quiet", "pokemate", "auto"]
        guard let out = try? await ProcessRunner().run(executable: Trial.settings.uvPath, arguments: base + helperArgs,
                                                       environment: AppPaths.processEnvironment(settings: Trial.settings), timeout: 1500) else {
            return (false, "helper 실행 실패")
        }
        let text = (String(decoding: out.stdout, as: UTF8.self) + "\n" + String(decoding: out.stderr, as: UTF8.self)).trimmingCharacters(in: .whitespacesAndNewlines)
        Trial.line("PokeMate \(helperArgs.first ?? "") → \(out.exitCode == 0 ? "ok" : "실패")")
        return (out.exitCode == 0, String(text.suffix(3000)))
    },
    diff: { worktree in
        let runner = ProcessRunner()
        guard let add = try? await runner.run(executable: "/usr/bin/git", arguments: ["-C", worktree, "add", "-A", "-N"], timeout: 120), add.exitCode == 0,
              let diff = try? await runner.run(executable: "/usr/bin/git", arguments: ["-C", worktree, "diff", "HEAD"], timeout: 120), diff.exitCode == 0 else { return nil }
        return String(decoding: diff.stdout, as: UTF8.self)
    },
    sessionId: { k in book.id(k) },
    recordSession: { k, id, ticket, usage in book.record(k, id, ticket, usage) },
    save: { state in
        state.save(in: pipelinesDir)
        let last = state.history.last.map { "\($0.role)→\($0.next)" } ?? "-"
        Trial.line("단계 \(state.statusText) · 마지막 \(last) · 비용 \(TokenFormat.usd(state.totalCost))\(state.inflight.map { " · 실행 중 \($0.role) \($0.sessionId)" } ?? "")")
    },
    notice: { title, body in Trial.line("알림 \(title): \(body)") })

let start: PipelineState
if var saved = PipelineState.load(key: key, in: pipelinesDir) {
    if saved.stage == .waiting { saved.resume(now: Date()) }
    start = saved
} else {
    start = PipelineState(key: key, title: summary, event: event, domain: domain, repo: repo, timeoutMinutes: 30, now: Date())
}
let final = await JiraPipeline.run(start, io: io, config: config)
Trial.line("끝: \(final.stage.rawValue) · \(final.reason ?? "") · 되돌림 테스터 \(final.testerBacks) 리뷰어 \(final.reviewerBacks)")
for role in PipelineRole.allCases { Trial.line("비용 \(role.label): \(TokenFormat.usd(final.costs[role.rawValue] ?? 0))") }
Trial.line("합계 \(TokenFormat.usd(final.totalCost))")
exit(final.stage == .done || final.stage == .waiting ? 0 : 1)
