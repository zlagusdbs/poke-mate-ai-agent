import Foundation
import PokeMateCore

// 사용: POKEMATE_TRIAL=<폴더> PokeMateTrial <폴더> <KEY> <도메인> <레포> [프롬프트 폴더]
//       POKEMATE_TRIAL=<폴더> PokeMateTrial <폴더> --review <PR 주소> <도메인>   (GitHub 리뷰 한 차례: PR 파일 prs/·세션 규칙·인계 블록)
let args = CommandLine.arguments
guard args.count >= 5, ProcessInfo.processInfo.environment["POKEMATE_TRIAL"] != nil else {
    print("usage: POKEMATE_TRIAL=<folder> PokeMateTrial <folder> <KEY> <domain> <repo> [promptsDir]")
    print("       POKEMATE_TRIAL=<folder> PokeMateTrial <folder> --review <PR url> <domain>")
    exit(2)
}
let root = URL(fileURLWithPath: args[1])
let reviewMode = args[2] == "--review"
let key = args[2], domain = args[3], repo = args[4]
let promptsDir = URL(fileURLWithPath: args.count > 5 && !reviewMode ? args[5] : root.appendingPathComponent("prompts").path)
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
config.notes = DomainNotes(root: root.appendingPathComponent("notes").path)
let issue = (try? JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("jira/\(key).json")))) as? [String: Any]
let summary = (((issue?["issue"] as? [String: Any])?["fields"] as? [String: Any])?["summary"] as? String) ?? key
let event = "다음 이벤트가 생겼다.\n- [\(key)] \(summary)\n- 나에게 할당된 이슈\n- 링크: https://jira.example.com/browse/\(key)"

final class Book: @unchecked Sendable {
    let lock = NSLock()
    var book: WorkSessionBook
    let file: URL
    init(file: URL) { self.file = file; book = WorkSessionBook.load(from: file) }
    /// 앱과 같은 방식: 새로 시작하면 앞 세션을 보관하고 이유(새 티켓·새 PR·식음·문맥)를 남긴다. 작업 탭 칸에 남은 이유를 돌려준다.
    @discardableResult
    func chose(_ k: String, _ caseKey: String, _ resumeId: String?, _ reason: String?) -> String? {
        guard resumeId == nil else { return nil }
        return lock.withLock {
            if book.startFresh(repo: k, caseKey: caseKey, reason: reason, now: Date()) { book.save(to: file) }
            return book.sessions[k]?.freshReason
        }
    }
    /// 끝난 건의 역할 세션을 보관한다 (앱과 같은 방식)
    func archive(_ keys: [String], _ label: String) -> Bool {
        lock.withLock {
            guard book.archiveCase(keys, label: label) else { return false }
            book.save(to: file)
            return true
        }
    }
    func record(_ k: String, _ id: String, _ ticket: String, _ usage: RunUsage?) {
        lock.withLock {
            book.record(repo: k, sessionId: id, key: ticket, usage: usage, now: Date())
            book.save(to: file)
        }
    }
}
let book = Book(file: bookFile)

// GitHub 리뷰 한 차례 (앱의 processReview 와 같은 순서): PR 파일로 세션 고르기 → 새 세션이면 인계 블록 → 실행 → 인계 메모·승인이면 보관
if reviewMode {
    guard let target = ReviewTarget.parse(url: args[3]) else {
        print("PR 주소가 아니에요: \(args[3])")
        exit(2)
    }
    let reviewDomain = args[4]
    let prsDir = root.appendingPathComponent("prs")
    let slot = DomainReview.sessionKey(reviewDomain)
    let begun = ReviewCase.begin(target: target, domain: reviewDomain, policy: SessionPolicy(settings: AppPaths.loadSettings()), in: prsDir, now: Date())
    var review = begun.review
    let decision = begun.decision
    review.save(in: prsDir)
    let resumeId = decision.resumeId
    let freshId = resumeId == nil ? UUID().uuidString.lowercased() : nil
    let tabReason = resumeId == nil ? book.chose(slot, review.key, nil, decision.reason) : nil
    let handoff = review.handoffBlock(decision)
    let how = resumeId == nil ? "새로" + (decision.reason.map { " (\($0))" } ?? " (처음)") : "이어 쓰기"
    Trial.line("리뷰 \(target.label) 세션 \(how) · \(resumeId ?? freshId ?? "-")\(handoff != nil ? " · 인계 블록 있음" : "")\(tabReason.map { " · 작업 탭 이유: \($0)" } ?? "")")
    let context = "다음 이벤트가 생겼다.\n- [리뷰 요청] \(target.label)\n- 링크: \(target.url)\n\n요청: 시험 리뷰"
    let prompt = DomainReview.reviewPrompt(target: target, domain: reviewDomain, why: "시험 도메인", source: .listed, firstRun: resumeId == nil,
                                           notes: DomainNotes(root: root.appendingPathComponent("notes").path), helper: helperText, context: context, handoff: handoff)
    let runner = ClaudeRunner()
    let response = await runner.run(prompt: prompt, sessionId: resumeId, settings: settings, timeout: 1800, newSessionId: freshId)
    var answer: String?
    var usedId: String?
    if case .success(let text, let id) = response {
        answer = text
        usedId = id.isEmpty ? (runner.lastUsage != nil ? resumeId ?? freshId : nil) : id
    } else if runner.lastUsage != nil {
        usedId = resumeId ?? freshId
    }
    if let usedId { book.record(slot, usedId, target.label, runner.lastUsage) }
    let done = review.finish(sessionId: usedId, usage: runner.lastUsage, answer: answer, now: Date())
    review.save(in: prsDir)
    if let done, book.archive([slot], target.label) { Trial.line("끝난 건 보관: \(target.label) (\(done.rawValue))") }
    let firstLine = (answer ?? "실패").split(separator: "\n").first.map(String.init) ?? ""
    Trial.line("리뷰 끝 · \(firstLine) · 상태 \(review.status.rawValue) · 인계 메모 \(review.handoffs?.count ?? 0)개")
    exit(answer != nil ? 0 : 1)
}

let io = PipelineIO(
    runRole: { role, prompt, sessionId, newSessionId, access in
        let handoff = prompt.contains(Handoffs.title + "\n" + Handoffs.intro) ? " · 인계 블록 있음" : ""
        Trial.line("\(role.label) 시작 (세션 \(sessionId ?? newSessionId ?? "-") · \(sessionId == nil ? "새로" : "이어 쓰기")\(handoff)\(prompt.hasPrefix(JiraPipeline.interruptedNote) ? " · 끊긴 실행 이어 가기" : ""))")
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
    recordSession: { k, id, ticket, usage in book.record(k, id, ticket, usage) },
    save: { state in
        state.save(in: pipelinesDir)
        let last = state.history.last.map { "\($0.role)→\($0.next)" } ?? "-"
        Trial.line("단계 \(state.statusText) · 마지막 \(last) · 비용 \(TokenFormat.usd(state.totalCost))\(state.inflight.map { " · 실행 중 \($0.role) \($0.sessionId)" } ?? "")")
    },
    notice: { title, body in Trial.line("알림 \(title): \(body)") },
    policy: { SessionPolicy(settings: AppPaths.loadSettings()) },
    sessionChosen: { k, caseKey, resumeId, reason in
        let tab = book.chose(k, caseKey, resumeId, reason)
        if resumeId == nil { Trial.line("세션 고름 \(k): 새로\(reason.map { " (\($0))" } ?? " (처음)") · 작업 탭 이유: \(tab ?? "-")") }
    },
    archiveCase: { ticket, keys in
        if book.archive(keys, ticket) { Trial.line("끝난 건 보관: \(ticket) 의 역할 세션") }
    })

let start: PipelineState
if var saved = PipelineState.load(key: key, in: pipelinesDir) {
    if saved.stage == .waiting { saved.resume(now: Date()) }
    start = saved
} else {
    start = PipelineState(key: key, title: summary, event: event, domain: domain, repo: repo, timeoutMinutes: 30, now: Date())
}
let final = await JiraPipeline.run(start, io: io, config: config)
Trial.line("끝: \(final.stage.rawValue) · \(final.reason ?? "") · 되돌림 테스터 \(final.testerBacks) 리뷰어 \(final.reviewerBacks) · 인계 메모 \(final.handoffs?.count ?? 0)개")
for role in PipelineRole.allCases { Trial.line("비용 \(role.label): \(TokenFormat.usd(final.costs[role.rawValue] ?? 0))") }
Trial.line("합계 \(TokenFormat.usd(final.totalCost))")
exit(final.stage == .done || final.stage == .waiting ? 0 : 1)
