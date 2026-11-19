import Foundation
import PokeMateCore

@MainActor
final class WorkSessionCenter: ObservableObject {
    static let shared = WorkSessionCenter()
    @Published private(set) var book = WorkSessionBook.load()
    @Published private(set) var running: [String: String] = [:] {
        didSet { writeLive() }
    }
    @Published private(set) var queued: [String: [String]] = [:] {
        didSet { writeLive() }
    }
    @Published private(set) var pipelines: [String: PipelineState] = [:]
    @Published private(set) var reviewing: [String: String] = [:]

    init() {
        writeLive()
        refreshAllUsage()
    }

    func refreshUsage(_ repo: String) {
        guard let s = book.sessions[repo], let file = Self.transcriptFile(sessionId: s.id), let data = try? Data(contentsOf: file) else { return }
        let total = TranscriptUsage.total(data)
        book.applyTranscript(repo: repo, usage: total.usage, lastContext: total.lastContext)
    }

    func refreshAllUsage() {
        for repo in book.sessions.keys { refreshUsage(repo) }
        save()
        refreshMonth()
    }

    @Published private(set) var monthUsage = RunUsage()

    func refreshMonth() {
        let start = Calendar.current.dateInterval(of: .month, for: Date())?.start ?? Date()
        let ids = Set(book.sessions.values.map(\.id) + book.archived.filter { $0.lastUsed >= start }.map(\.id)).filter { !$0.isEmpty }
        var total = RunUsage()
        for id in ids {
            guard let file = Self.transcriptFile(sessionId: id), let data = try? Data(contentsOf: file) else { continue }
            total.add(TranscriptUsage.total(data, since: start).usage)
        }
        monthUsage = total
    }

    @discardableResult
    func create(repo: String) -> Bool {
        guard book.addEmpty(repo: repo, now: Date()) else { return false }
        save()
        return true
    }

    func prefix(for repo: String) -> String {
        var text = ""
        if let summary = takeSummary(repo) { text += "## 이전 작업 세션 요약 (\(repo))\n\(summary)\n\n" }
        if book.resumeId(repo) == nil && PipelineRole.split(repo) == nil { text += WorkspaceRepos.bootstrap(repo: repo) }
        return text
    }

    func exclusive(repo: String, label: String, _ body: @escaping @MainActor @Sendable () async -> Void) async {
        queued[repo, default: []].append(label)
        let task = queue.run(repo) { [self] in
            await self.begin(repo: repo, label: label)
            await body()
            await self.finish(repo: repo)
        }
        await task.value
    }

    private func begin(repo: String, label: String) {
        if let i = queued[repo]?.firstIndex(of: label) { queued[repo]?.remove(at: i) }
        if queued[repo]?.isEmpty == true { queued[repo] = nil }
        running[repo] = label
    }

    private func finish(repo: String) { running[repo] = nil }

    static var liveFile: URL { AppPaths.dataDir.appendingPathComponent("automation/sessions-live.json") }

    private func writeLive() {
        let formatter = ISO8601DateFormatter()
        let object: [String: Any] = ["updatedAt": formatter.string(from: Date()), "running": running, "queued": queued]
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys]) else { return }
        try? FileManager.default.createDirectory(at: Self.liveFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: Self.liveFile, options: .atomic)
    }
    private let queue = SerialQueue()

    static let resumeNote = "이 작업 세션은 같은 레포의 이전 티켓을 처리하던 세션이다. 이미 이 세션에서 읽은 지식(CLAUDE.md, 문서, 위키, 예전 티켓, git 기록)은 다시 읽지 말고, 이번 티켓에 새로 필요한 것만 찾아라."
    static let summaryPrompt = "이 작업 세션을 새 세션으로 바꾼다. 지금까지 이 세션에서 익힌 레포 지식과 주의점(구조, 빌드·테스트 방법, 규칙, 자주 쓰는 근거 문서·위키 pageId, 처리한 티켓과 결과, 하지 말아야 할 것)을 다음 세션이 그대로 이어받을 수 있게 요약한다. 도구를 쓰지 말고 요약만 답한다. 40줄 이내."

    var onContextAlert: ((WorkSession, Int) -> Void)?
    var onPipelineNotice: ((String, String) -> Void)?
    var quitting = false
    @Published var requestReset: String?

    private func checkAlert(_ repo: String) {
        let threshold = AppPaths.loadSettings().workSessionAlertPercent
        if let hit = book.contextAlert(repo: repo, threshold: threshold), let percent = hit.contextPercent {
            onContextAlert?(hit, percent)
        }
    }

    func reload() {
        book = WorkSessionBook.load()
        refreshAllUsage()
    }

    private func save() { book.save() }

    static func issueKey(_ item: EventItem) -> String {
        item.key.components(separatedBy: ":").first ?? item.key
    }

    func knownRepo(for item: EventItem) -> String? {
        let key = Self.issueKey(item)
        if let hit = book.sessions.first(where: { $0.value.keys.contains(key) }) { return hit.key }
        let title = item.title
        if let regex = try? NSRegularExpression(pattern: #"·\s+([A-Za-z0-9._-]+)\s+#\d+"#),
           let m = regex.firstMatch(in: title, range: NSRange(title.startIndex..., in: title)),
           let r = Range(m.range(at: 1), in: title) {
            return String(title[r])
        }
        return nil
    }

    static func routePrompt(context: String, prompt: String) -> String {
        """
        \(context)

        지금은 처리하지 말고 이 건을 어느 레포에서 처리할지만 정한다. 위 처리 지시의 확인 단계와 레포 정하는 규칙을 따르되, 쓰기·댓글·상태 변경·브랜치 생성·알림은 하지 않는다(읽기만).
        - 담당자가 아니거나 이미 해결됐거나 공수 기록용 반복 티켓처럼 처리할 게 없으면 skip.
        - Task 처럼 레포가 없는 운영 건이면 repo 를 빈 문자열로.
        - 레포를 하나로 정하지 못하면 repo 를 빈 문자열로 두지 말고 가장 가능성 높은 레포 폴더 이름을 쓴다(처리 단계에서 다시 확인한다).
        답 끝에 아래 블록 하나만 붙인다.
        ```\(WorkRoute.tag)
        {"repo": "레포 폴더 이름 또는 빈 문자열", "skip": false}
        ```
        """
    }

    func handle(alarm: Alarm, item: EventItem, prompt: String, title: String, access: ClaudeCommand.WriteAccess,
                chat: ChatSession, completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        let context = "다음 이벤트가 생겼다.\n- \(item.title)\n- \(item.summary)\n- 링크: \(item.url)\n\n요청: \(prompt)"
        let key = Self.issueKey(item)
        if let repo = knownRepo(for: item) {
            enqueue(repo: repo, key: key, alarm: alarm, context: context, title: title, access: access, chat: chat, completion: completion)
            return
        }
        running[Self.routingSlot] = key
        let defaultKnowledge = DomainMap.load().knowledgeText(for: "")
        chat.runAutomation(title: "\(title) · 레포 정하기",
                           prompt: Self.routePrompt(context: context, prompt: prompt).replacingOccurrences(of: DomainMap.placeholder, with: defaultKnowledge), access: access,
                           timeout: 600, quiet: true) { [weak self] response, usage in
            guard let self else { return }
            self.running[Self.routingSlot] = nil
            guard case .success(let text, _) = response else {
                completion(response, usage)
                return
            }
            switch WorkRoute.extract(from: text) {
            case .some(nil):
                completion(.success(text: "처리할 것 없음: \(key)", sessionId: ""), usage)
            case .some(let repo?):
                self.enqueue(repo: repo, key: key, alarm: alarm, context: context, title: title, access: access, chat: chat, completion: completion)
            case nil:
                self.enqueue(repo: WorkSessionBook.operations, key: key, alarm: alarm, context: context, title: title, access: access, chat: chat, completion: completion)
            }
        }
    }

    static let routingSlot = "(레포 정하는 중)"

    private func enqueue(repo: String, key: String, alarm: Alarm, context: String, title: String, access: ClaudeCommand.WriteAccess,
                         chat: ChatSession, completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        queued[repo, default: []].append(key)
        _ = queue.run(repo) { [weak self] in
            await self?.process(repo: repo, key: key, alarm: alarm, context: context, title: title, access: access, chat: chat, completion: completion)
        }
    }

    private func runOnce(chat: ChatSession, title: String, prompt: String, access: ClaudeCommand.WriteAccess, timeout: TimeInterval,
                         sessionId: String?, newSessionId: String? = nil, quiet: Bool,
                         extraEnvironment: [String: String] = [:]) async -> (ClaudeResponse, RunUsage?) {
        await withCheckedContinuation { continuation in
            chat.runAutomation(title: title, prompt: prompt, access: access, timeout: timeout, sessionId: sessionId,
                               newSessionId: newSessionId, quiet: quiet, extraEnvironment: extraEnvironment) { response, usage in
                continuation.resume(returning: (response, usage))
            }
        }
    }

    private func process(repo: String, key: String, alarm: Alarm, context: String, title: String, access: ClaudeCommand.WriteAccess,
                         chat: ChatSession, completion: @escaping (ClaudeResponse, RunUsage?) -> Void) async {
        if let i = queued[repo]?.firstIndex(of: key) { queued[repo]?.remove(at: i) }
        if queued[repo]?.isEmpty == true { queued[repo] = nil }
        running[repo] = key
        let sessionId = book.resumeId(repo)
        var prompt = book.sessions[repo] == nil ? (takeSummary(repo).map { "## 이전 작업 세션 요약 (\(repo))\n\($0)\n\n" } ?? "") : prefix(for: repo)
        if sessionId != nil { prompt += Self.resumeNote + "\n\n" }
        let domains = DomainMap.load()
        prompt += "이번 건: \(key)\n\n" + context.replacingOccurrences(of: DomainMap.placeholder, with: domains.knowledgeText(for: domains.domain(for: repo)))
        let (response, usage) = await runOnce(chat: chat, title: "\(title) · \(repo)", prompt: prompt, access: access,
                                              timeout: alarm.automationTimeout, sessionId: sessionId, quiet: false)
        if case .success(_, let newId) = response, !newId.isEmpty {
            book.record(repo: repo, sessionId: newId, key: key, usage: usage, now: Date())
        } else if let usage, let id = sessionId {
            book.record(repo: repo, sessionId: id, key: key, usage: usage, now: Date())
        }
        refreshUsage(repo)
        checkAlert(repo)
        save()
        refreshMonth()
        running[repo] = nil
        completion(response, usage)
    }

    private func rotate(repo: String, old: WorkSession, chat: ChatSession, access: ClaudeCommand.WriteAccess) async -> String? {
        let (response, usage) = await runOnce(chat: chat, title: "작업 세션 요약 · \(repo)", prompt: Self.summaryPrompt,
                                              access: ClaudeCommand.WriteAccess(), timeout: 600, sessionId: old.id, quiet: true)
        if let usage { book.record(repo: repo, sessionId: old.id, key: nil, usage: usage, now: Date()) }
        book.archive(repo: repo)
        save()
        if case .success(let text, _) = response { return text }
        return nil
    }

    func reset(repo: String, carrySummary: Bool, chat: ChatSession) {
        guard let old = book.sessions[repo] else { return }
        if !carrySummary || old.id.isEmpty {
            book.archive(repo: repo)
            save()
            return
        }
        _ = queue.run(repo) { [weak self] in
            guard let self else { return }
            await MainActor.run { self.running[repo] = "요약 중" }
            let summary = await self.rotate(repo: repo, old: old, chat: chat, access: ClaudeCommand.WriteAccess())
            await MainActor.run {
                if let summary { self.pendingSummary[repo] = summary }
                self.running[repo] = nil
            }
        }
    }

    @Published var pendingSummary: [String: String] = [:]

    func takeSummary(_ repo: String) -> String? {
        pendingSummary.removeValue(forKey: repo)
    }

    func archive(repo: String) {
        book.archive(repo: repo)
        save()
    }

    func recordTalk(repo: String, sessionId: String, usage: RunUsage?) {
        book.record(repo: repo, sessionId: sessionId, key: nil, usage: usage, now: Date())
        refreshUsage(repo)
        checkAlert(repo)
        save()
        refreshMonth()
    }

    static func transcriptFile(sessionId: String) -> URL? {
        let projects = AppPaths.claudeConfigDir.appendingPathComponent("projects", isDirectory: true)
        let dirs = (try? FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil)) ?? []
        return dirs.map { $0.appendingPathComponent("\(sessionId).jsonl") }.first { FileManager.default.fileExists(atPath: $0.path) }
    }
}

extension WorkSessionCenter {
    static func pipelineEvent(_ item: EventItem) -> String {
        "다음 이벤트가 생겼다.\n- \(item.title)\n- \(item.summary)\n- 링크: \(item.url)"
    }

    func pipelineTable(settings: AppSettings) -> [PipelineRole: ClaudeCommand.WriteAccess] {
        let home = NSHomeDirectory()
        var table = PipelineAccess.defaults(helper: PipelineAccess.helper(settings: settings), workDir: home + "/Workspace",
                                            references: home + "/.claude/references", pipelines: PipelineState.dir().path)
        if let data = try? Data(contentsOf: PipelineAccess.file) {
            for (role, access) in PipelineAccess.parse(data) { table[role] = access }
        } else if AppPaths.localReady {
            try? PipelineAccess.encode(table).write(to: PipelineAccess.file, options: .atomic)
        }
        return table
    }

    func handlePipeline(alarm: Alarm, item: EventItem, prompt: String, title: String, chat: ChatSession,
                        completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        let key = Self.issueKey(item)
        let event = Self.pipelineEvent(item)
        let existing = PipelineState.load(key: key)
        switch PipelineState.arrival(of: existing) {
        case .resume:
            guard var state = existing else { break }
            state.resume(now: Date())
            state.event = event
            state.save()
            enqueuePipeline(state, chat: chat, completion: completion)
            return
        case .skip(let why):
            completion(.success(text: why, sessionId: ""), nil)
            return
        case .start:
            break
        }
        let table = pipelineTable(settings: AppPaths.loadSettings())
        var readOnly = table[.pm] ?? ClaudeCommand.WriteAccess()
        readOnly.tools = readOnly.tools.filter { rule in ["jira-get", "jira-search", "jira-comments", "jira-links", "git-read"].contains { rule.contains(" \($0):") } }
        running[Self.routingSlot] = key
        let context = "\(event)\n\n요청: \(prompt)"
        let defaultKnowledge = DomainMap.load().knowledgeText(for: "")
        chat.runAutomation(title: "\(title) · 레포 정하기",
                           prompt: Self.routePrompt(context: context, prompt: prompt).replacingOccurrences(of: DomainMap.placeholder, with: defaultKnowledge), access: readOnly,
                           timeout: 600, quiet: true) { [weak self] response, usage in
            guard let self else { return }
            self.running[Self.routingSlot] = nil
            guard case .success(let text, _) = response else {
                completion(response, usage)
                return
            }
            guard let route = WorkRoute.extract(from: text) else {
                completion(.failure(.other("레포를 정하지 못했어요: \(key)")), usage)
                return
            }
            guard let repo = route else {
                completion(.success(text: "처리할 것 없음: \(key)", sessionId: ""), usage)
                return
            }
            let isOps = repo == WorkSessionBook.operations
            let domain = isOps ? repo : DomainMap.load().domain(for: repo)
            let state = PipelineState(key: key, title: item.title, event: event, domain: domain, repo: isOps ? nil : repo,
                                      timeoutMinutes: alarm.timeoutMinutes ?? Int(ClaudeRunner.automationTimeout / 60), now: Date())
            state.save()
            self.enqueuePipeline(state, chat: chat, completion: completion)
        }
    }

    func resumePipelines(chat: ChatSession, notify: @escaping (String, String, Bool) -> Void) {
        for state in PipelineState.loadAll() where state.isActive {
            enqueuePipeline(state, chat: chat) { response, _ in
                switch response {
                case .success(let text, _): notify("Jira 역할 처리 이어서 끝남 · \(state.key)", text, false)
                case .failure(let failure): notify("Jira 역할 처리 멈춤 · \(state.key)", failure.message, true)
                }
            }
        }
    }

    private func enqueuePipeline(_ state: PipelineState, chat: ChatSession, completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        let domain = state.domain
        queued[domain, default: []].append(state.key)
        _ = queue.run(domain) { [weak self] in
            guard let self else { return }
            let final = await self.processPipeline(state, chat: chat)
            let cost = RunUsage(costUSD: final.totalCost)
            switch final.stage {
            case .stopped:
                completion(.failure(.other("\(final.key) 멈춤: \(final.reason ?? "")")), cost)
            default:
                let roles = PipelineRole.allCases.compactMap { r in final.costs[r.rawValue].map { "\(r.label) \(TokenFormat.usd($0))" } }.joined(separator: " · ")
                let summary = PipelineNext.strip(final.summary)
                completion(.success(text: "\(final.statusText)\n\(summary)\n\n비용: \(roles)", sessionId: ""), cost)
            }
        }
    }

    private func processPipeline(_ start: PipelineState, chat: ChatSession) async -> PipelineState {
        let domain = start.domain
        if let i = queued[domain]?.firstIndex(of: start.key) { queued[domain]?.remove(at: i) }
        if queued[domain]?.isEmpty == true { queued[domain] = nil }
        running[domain] = start.statusText
        pipelines[domain] = start
        let settings = AppPaths.loadSettings()
        var config = PipelineConfig.load(promptsDir: Rules.promptsDir, access: pipelineTable(settings: settings), dir: PipelineState.dir(),
                                         helper: PipelineAccess.helper(settings: settings), fallbackDir: Rules.folder(.prompt, layer: .shared))
        config.domains = DomainMap.load()
        let timeout = TimeInterval(max(1, start.timeoutMinutes) * 60)
        let io = PipelineIO(
            runRole: { [weak self] role, prompt, sessionId, newSessionId, access in
                guard let self else { return PipelineRoleResult(failure: "앱이 닫혔어요") }
                return await self.runRole(chat: chat, title: "\(start.key) · \(domain):\(role.rawValue)", prompt: prompt, access: access,
                                          timeout: timeout, sessionId: sessionId, newSessionId: newSessionId)
            },
            helper: { args in await Self.runHelper(args, settings: settings) },
            diff: { worktree in await Self.worktreeDiff(worktree) },
            sessionId: { [weak self] key in await self?.book.resumeId(key) },
            recordSession: { [weak self] sessionKey, id, ticket, usage in
                await self?.recordRoleSession(sessionKey, id: id, ticket: ticket, usage: usage)
            },
            save: { [weak self] state in
                state.save()
                await self?.showPipeline(state)
            },
            notice: { [weak self] title, body in
                await self?.pipelineNotice(title, body)
            })
        let final = await JiraPipeline.run(start, io: io, config: config)
        running[domain] = nil
        pipelines[domain] = final
        refreshMonth()
        return final
    }

    private func recordRoleSession(_ sessionKey: String, id: String, ticket: String, usage: RunUsage?) {
        book.record(repo: sessionKey, sessionId: id, key: ticket, usage: usage, now: Date())
        refreshUsage(sessionKey)
        checkAlert(sessionKey)
        save()
    }

    private func pipelineNotice(_ title: String, _ body: String) {
        onPipelineNotice?(title, body)
    }

    private func showPipeline(_ state: PipelineState) {
        pipelines[state.domain] = state
        running[state.domain] = state.statusText
    }

    private func runRole(chat: ChatSession, title: String, prompt: String, access: ClaudeCommand.WriteAccess, timeout: TimeInterval,
                         sessionId: String?, newSessionId: String?) async -> PipelineRoleResult {
        let (response, usage) = await runOnce(chat: chat, title: title, prompt: prompt, access: access, timeout: timeout, sessionId: sessionId,
                                              newSessionId: newSessionId, quiet: true)
        if quitting { return PipelineRoleResult(usage: usage, interrupted: true) }
        switch response {
        case .success(let text, let id): return PipelineRoleResult(text: text, sessionId: id.isEmpty ? nil : id, usage: usage)
        case .failure(let failure): return PipelineRoleResult(sessionId: nil, usage: usage, failure: failure.message)
        }
    }

    nonisolated static func runHelper(_ args: [String], settings: AppSettings) async -> (ok: Bool, output: String) {
        let base = ["--directory", settings.repoDir + "/mcp-server", "run", "--quiet", "pokemate", "auto"]
        guard let out = try? await ProcessRunner().run(executable: settings.uvPath, arguments: base + args,
                                                       environment: AppPaths.processEnvironment(settings: settings), timeout: 1500) else {
            return (false, "helper 실행 실패")
        }
        let text = (String(decoding: out.stdout, as: UTF8.self) + "\n" + String(decoding: out.stderr, as: UTF8.self))
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return (out.exitCode == 0 && !out.timedOut, String(text.suffix(3000)))
    }

    nonisolated static func worktreeDiff(_ worktree: String) async -> String? {
        let runner = ProcessRunner()
        guard let add = try? await runner.run(executable: "/usr/bin/git", arguments: ["-C", worktree, "add", "-A", "-N"], timeout: 120),
              add.exitCode == 0,
              let diff = try? await runner.run(executable: "/usr/bin/git", arguments: ["-C", worktree, "diff", "HEAD"], timeout: 120),
              diff.exitCode == 0 else { return nil }
        return String(decoding: diff.stdout, as: UTF8.self)
    }
}

extension WorkSessionCenter {
    func handleReview(alarm: Alarm, item: EventItem, prompt: String, title: String, access: ClaudeCommand.WriteAccess,
                      chat: ChatSession, completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        guard let target = ReviewTarget.parse(url: item.url) else {
            handle(alarm: alarm, item: item, prompt: prompt, title: title, access: access, chat: chat, completion: completion)
            return
        }
        let context = "\(Self.pipelineEvent(item))\n\n요청: \(prompt)"
        let map = DomainMap.load()
        if let hit = DomainReview.known(target, map: map, book: ReviewDomainBook.load()) {
            enqueueReview(target, domain: hit.domain, why: hit.why, source: hit.source, context: context, alarm: alarm, title: title,
                          access: access, chat: chat, completion: completion)
            return
        }
        let helper = PipelineAccess.helper(settings: AppPaths.loadSettings())
        let readOnly = ClaudeCommand.WriteAccess(tools: ["Bash(\(helper) gh-get:*)", "Bash(\(helper) jira-get:*)"], dirs: [])
        running[Self.routingSlot] = target.label
        chat.runAutomation(title: "\(title) · 도메인 정하기", prompt: DomainReview.choosePrompt(target: target, map: map, helper: helper),
                           access: readOnly, timeout: 600, quiet: true) { [weak self] response, _ in
            guard let self else { return }
            self.running[Self.routingSlot] = nil
            var domain = DomainReview.fallback
            var why = "도메인을 정하지 못해 \(DomainReview.fallback) 로 리뷰"
            if case .success(let text, _) = response, let choice = DomainReview.extract(from: text, map: map) {
                domain = choice.domain
                why = choice.why
                var book = ReviewDomainBook.load()
                book.repos[target.fullName] = ReviewDomainChoice(domain: choice.domain, why: choice.why, at: Date())
                book.save()
            }
            self.enqueueReview(target, domain: domain, why: why, source: .chosen, context: context, alarm: alarm, title: title,
                               access: access, chat: chat, completion: completion)
        }
    }

    private func enqueueReview(_ target: ReviewTarget, domain: String, why: String, source: DomainReview.Source, context: String,
                               alarm: Alarm, title: String, access: ClaudeCommand.WriteAccess, chat: ChatSession,
                               completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        queued[domain, default: []].append("리뷰 \(target.label)")
        _ = queue.run(domain) { [weak self] in
            await self?.processReview(target, domain: domain, why: why, source: source, context: context, alarm: alarm, title: title,
                                      access: access, chat: chat, completion: completion)
        }
    }

    private func processReview(_ target: ReviewTarget, domain: String, why: String, source: DomainReview.Source, context: String,
                               alarm: Alarm, title: String, access: ClaudeCommand.WriteAccess, chat: ChatSession,
                               completion: @escaping (ClaudeResponse, RunUsage?) -> Void) async {
        let label = "리뷰 \(target.label)"
        if let i = queued[domain]?.firstIndex(of: label) { queued[domain]?.remove(at: i) }
        if queued[domain]?.isEmpty == true { queued[domain] = nil }
        running[domain] = label
        reviewing[domain] = "\(label) · \(why)"
        let key = DomainReview.sessionKey(domain)
        let sessionId = book.resumeId(key)
        let map = DomainMap.load()
        let memo = DomainReview.memoFile(domain)
        let memoLines = ((try? String(contentsOf: memo, encoding: .utf8)) ?? "").split(separator: "\n").count
        var runAccess = access
        runAccess.dirs += map.knowledgePaths(for: domain).filter { !runAccess.dirs.contains($0) }
        let helper = PipelineAccess.helper(settings: AppPaths.loadSettings())
        let prompt = DomainReview.reviewPrompt(target: target, domain: domain, why: why, source: source, firstRun: sessionId == nil,
                                               memo: memo, memoLines: memoLines, helper: helper,
                                               context: context.replacingOccurrences(of: DomainMap.placeholder, with: map.knowledgeText(for: domain)))
        let (response, usage) = await runOnce(chat: chat, title: "\(title) · \(key)", prompt: prompt, access: runAccess,
                                              timeout: alarm.automationTimeout, sessionId: sessionId, quiet: false,
                                              extraEnvironment: ["POKEMATE_REVIEW_REPO": target.fullName])
        if case .success(_, let newId) = response, !newId.isEmpty {
            book.record(repo: key, sessionId: newId, key: target.label, usage: usage, now: Date())
        } else if let usage, let id = sessionId {
            book.record(repo: key, sessionId: id, key: target.label, usage: usage, now: Date())
        }
        refreshUsage(key)
        checkAlert(key)
        save()
        refreshMonth()
        running[domain] = nil
        reviewing[domain] = nil
        completion(response, usage)
    }
}
