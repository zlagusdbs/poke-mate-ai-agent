import Foundation
import PokeMateCore

/// 채팅 상태. 질문은 한 번에 하나씩 처리하고, 처리 중 들어온 요청은 순서대로 기다린다.
@MainActor
final class ChatSession: ObservableObject {
    @Published private(set) var messages: [ChatMessage] = []
    @Published private(set) var isBusy = false
    @Published private(set) var lastFailedPrompt: String?
    private var lastFailedImages: [ImageAttachment] = []
    @Published private(set) var loginState: LoginState = .idle
    @Published private(set) var activity: String?
    @Published private(set) var pendingPermission: PermissionRequest?
    private var permissionAnswer: CheckedContinuation<PermissionDecision, Never>?
    @Published private(set) var sessionAllow: [String] = []

    func clearSessionAllow() { sessionAllow = [] }
    var onPermissionAsked: ((PermissionRequest) -> Void)?
    var onPermissionDone: (() -> Void)?
    static let permissionWait: UInt64 = 600

    enum LoginState { case idle, waitingBrowser, needsCode, verifying }

    @Published private(set) var routing = ChatRouting()
    let fixedRepo: String?

    func route(to repo: String?) {
        guard fixedRepo == nil else { return }
        routing.choose(repo)
        save()
    }

    private enum Job {
        case chat(String, [ImageAttachment])
        case work(repo: String, prompt: String, [ImageAttachment])
        case report(title: String, prompt: String, (ClaudeResponse, RunUsage?) -> Void)
    }

    private struct Stored: Codable {
        var messages: [ChatMessage]
        var sessionId: String?
        var routing: ChatRouting?
    }

    private var sessionId: String?
    private var queue: [Job] = []
    private let runner = ClaudeRunner()
    private let login = ClaudeLogin()
    private let settings: () -> AppSettings
    private let storage: URL?
    var onRun: ((String, RunUsage?) -> Void)?

    init(settings: @escaping () -> AppSettings, storage: URL? = AppPaths.chatFile, sessionId: String? = nil, workRepo: String? = nil) {
        self.settings = settings
        self.storage = storage
        self.sessionId = sessionId
        self.fixedRepo = workRepo
        if let workRepo { routing = ChatRouting(repo: workRepo) }
        if let storage, let data = try? Data(contentsOf: storage),
           let stored = try? JSONDecoder().decode(Stored.self, from: data) {
            messages = stored.messages
            self.sessionId = stored.sessionId
            if workRepo == nil, let saved = stored.routing { routing = saved }
        }
    }

    func send(_ text: String, images: [ImageAttachment] = []) {
        var trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty || !images.isEmpty else { return }
        if trimmed.isEmpty { trimmed = "이 이미지를 보고 설명해줘." }
        var message = ChatMessage(role: .user, text: text.trimmingCharacters(in: .whitespacesAndNewlines), images: images.map(\.path))
        message.session = routing.repo
        append(message)
        trimmed += ChatHints.for(trimmed, now: Date(), calendar: .current)
        enqueue(trimmed, images)
    }

    private func enqueue(_ prompt: String, _ images: [ImageAttachment]) {
        if let repo = routing.repo {
            queue.append(.work(repo: repo, prompt: prompt, images))
        } else {
            queue.append(.chat(prompt, images))
        }
        processNext()
    }

    func sendSuggested(_ messageID: UUID, repo: String) {
        guard let i = messages.firstIndex(where: { $0.id == messageID }),
              let question = messages[..<i].last(where: { $0.role == .user && $0.session == nil }) else { return }
        update(messageID) { $0.routeSent = true }
        route(to: repo)
        var message = ChatMessage(role: .user, text: question.text)
        message.session = repo
        append(message)
        queue.append(.work(repo: repo, prompt: question.text + ChatHints.for(question.text, now: Date(), calendar: .current), []))
        processNext()
    }

    /// 알림 실행. 새 세션으로 실행해 결과를 채팅창에 이어 붙이고, 이후 대화는 그 결과에서 이어간다.
    func runReport(title: String, prompt: String, completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        queue.append(.report(title: title, prompt: prompt, completion))
        processNext()
    }

    func runAutomation(title: String, prompt: String, access: ClaudeCommand.WriteAccess, timeout: TimeInterval = ClaudeRunner.automationTimeout,
                       sessionId: String? = nil, newSessionId: String? = nil, quiet: Bool = false,
                       extraEnvironment: [String: String] = [:],
                       completion: @escaping (ClaudeResponse, RunUsage?) -> Void) {
        if !quiet { append(ChatMessage(role: .user, text: "🔔 \(title)")) }
        let runner = ClaudeRunner()
        let id = ObjectIdentifier(runner)
        automationRunners[id] = runner
        Task {
            let response = await runner.run(prompt: prompt, sessionId: sessionId, settings: settings(),
                                            writeTools: access.tools, writeDirs: access.dirs,
                                            timeout: timeout, newSessionId: newSessionId, extraEnvironment: extraEnvironment)
            if !quiet {
                switch response {
                case .success(let text, _):
                    append(ChatMessage(role: .assistant, text: text))
                case .failure(let failure):
                    append(ChatMessage(role: .error, text: failure.backgroundMessage))
                }
            }
            automationRunners[id] = nil
            completion(response, runner.lastUsage)
        }
    }

    private var automationRunners: [ObjectIdentifier: ClaudeRunner] = [:]

    func stopAllForQuit() {
        for runner in automationRunners.values { runner.cancel() }
        runner.cancel()
    }

    func retry() {
        guard let prompt = lastFailedPrompt else { return }
        lastFailedPrompt = nil
        if let repo = lastFailedRepo {
            queue.append(.work(repo: repo, prompt: prompt, lastFailedImages))
        } else {
            queue.append(.chat(prompt, lastFailedImages))
        }
        processNext()
    }

    private var lastFailedRepo: String?

    func cancel() {
        queue.removeAll()
        answerPermission(.deny("사용자가 취소했어요"))
        runner.cancel()
    }

    func answerPermission(_ decision: PermissionDecision) {
        guard let request = pendingPermission, let answer = permissionAnswer else { return }
        if decision == .allowAlways { sessionAllow += request.rules.filter { !sessionAllow.contains($0) } }
        if case .allowRules(let rules) = decision { sessionAllow += rules.filter { !sessionAllow.contains($0) } }
        pendingPermission = nil
        permissionAnswer = nil
        onPermissionDone?()
        answer.resume(returning: decision)
    }

    private func askPermission(_ request: PermissionRequest) async -> PermissionDecision {
        if pendingPermission != nil { return .deny("다른 승인을 기다리는 중이에요") }
        return await withCheckedContinuation { continuation in
            pendingPermission = request
            permissionAnswer = continuation
            onPermissionAsked?(request)
            let id = request.id
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: Self.permissionWait * 1_000_000_000)
                if self?.pendingPermission?.id == id { self?.answerPermission(.deny("10분 동안 답이 없어 거부했어요")) }
            }
        }
    }

    private func toolHooks(preallowed: [String] = []) -> ToolHooks? {
        guard settings().claudeTools else { return nil }
        return ToolHooks(sessionAllow: preallowed + sessionAllow.filter { !preallowed.contains($0) },
                         onActivity: { [weak self] text in Task { @MainActor in self?.activity = text } },
                         onPermission: { [weak self] request in
                             guard let self else { return .deny("창이 닫혔어요") }
                             return await self.askPermission(request)
                         })
    }

    /// Claude 를 부르지 않고 메이트 말로 바로 붙인다 (처음 인사 등).
    func say(_ text: String) {
        append(ChatMessage(role: .assistant, text: text))
    }

    /// 계정이 바뀌면 이전 세션을 이어 쓸 수 없어서 세션만 잊는다 (화면의 대화는 남김).
    func forgetSession() {
        sessionId = nil
        sessionAllow = []
        save()
    }

    func newConversation() {
        cancel()
        messages = []
        sessionId = nil
        sessionAllow = []
        lastFailedPrompt = nil
        if fixedRepo == nil { routing.reset() }
        save()
    }

    private func processNext() {
        guard !isBusy, !queue.isEmpty else { return }
        isBusy = true
        let job = queue.removeFirst()
        Task {
            switch job {
            case .chat(let prompt, let images):
                let notes = routing.takeMatePrefix()
                if !notes.isEmpty { save() }
                let response = await runner.run(prompt: notes + prompt, images: images, sessionId: sessionId, settings: settings(),
                                                hooks: toolHooks(), automation: false)
                activity = nil
                if case .success(_, let id) = response, !id.isEmpty { onRun?(id, runner.lastUsage) }
                handle(response, prompt: prompt, images: images)
            case .work(let repo, let prompt, let images):
                await runWork(repo: repo, prompt: prompt, images: images)
            case .report(let title, let prompt, let completion):
                append(ChatMessage(role: .user, text: "🔔 \(title)"))
                let response = await runner.run(prompt: prompt, sessionId: nil, settings: settings())
                handle(response, prompt: nil, images: [], interactive: false)
                completion(response, runner.lastUsage)
            }
            isBusy = false
            processNext()
        }
    }

    private func runWork(repo: String, prompt: String, images: [ImageAttachment]) async {
        let center = WorkSessionCenter.shared
        let access = ClaudeCommand.loadWriteAccess()
        activity = center.running[repo] != nil || center.queued[repo] != nil ? "\(repo) 작업 세션 차례를 기다리는 중…" : nil
        await center.exclusive(repo: repo, label: "채팅") { [weak self] in
            guard let self else { return }
            self.activity = nil
            let full = center.prefix(for: repo) + prompt
            let response = await self.runner.run(prompt: full, images: images, sessionId: center.book.resumeId(repo), settings: self.settings(),
                                                 writeTools: access.tools, writeDirs: access.dirs, hooks: self.toolHooks(preallowed: access.tools),
                                                 timeout: ClaudeRunner.automationTimeout)
            self.activity = nil
            if case .success(_, let id) = response, !id.isEmpty {
                center.recordTalk(repo: repo, sessionId: id, usage: self.runner.lastUsage)
            }
            if self.fixedRepo == nil { self.routing.noteSent(repo: repo, text: prompt) }
            self.handle(response, prompt: prompt, images: images, repo: repo)
        }
    }

    /// `interactive` 는 사용자가 보낸 채팅·작업 세션 질문일 때만 true. 알림 실행은 로그인을 시작하지 않고 로그인이 필요하다고만 남긴다.
    private func handle(_ response: ClaudeResponse, prompt: String?, images: [ImageAttachment], repo: String? = nil, interactive: Bool = true) {
        lastFailedRepo = repo
        switch response {
        case .success(let text, let newSession):
            if repo == nil, !newSession.isEmpty { sessionId = newSession }
            let (afterSuggest, suggested) = repo == nil ? RouteSuggestion.extract(from: text) : (text, nil)
            let (afterAlarms, alarms) = AlarmProposal.extract(from: afterSuggest)
            let (afterJira, jira) = JiraProposal.extract(from: afterAlarms)
            let (afterRules, rules) = RuleProposal.extract(from: afterJira)
            let (afterWorklogs, worklogs) = WorklogProposal.extract(from: afterRules)
            let (clean, notes) = NoteProposal.extract(from: afterWorklogs)
            var message = ChatMessage(role: .assistant, text: clean, alarms: alarms, jira: jira)
            message.rules = rules
            message.worklogs = worklogs
            message.notes = notes
            message.session = repo
            message.routeSuggest = suggested
            append(message)
        case .failure(let failure):
            var message = ChatMessage(role: .error, text: interactive ? failure.message : failure.backgroundMessage)
            message.session = repo
            append(message)
            lastFailedImages = images
            if ClaudeFailure.startsLogin(failure, interactive: interactive) {
                startLogin(retry: prompt)
            } else if failure == .notLoggedIn {
                // 알림 실행: 로그인은 사용자가 채팅이나 설정에서 시작한다
            } else if failure != .cancelled && failure != .usageLimit {   // 한도는 다시 시도해도 같아서 버튼을 숨긴다
                lastFailedPrompt = prompt
            }
        }
    }

    // MARK: 로그인

    /// `claude auth login` 을 실행해 브라우저를 띄우고, 로그인되면 질문을 다시 보낸다.
    func startLogin(retry prompt: String?) {
        loginState = .waitingBrowser
        login.start(settings: settings()) { [weak self] event in
            guard let self else { return }
            switch event {
            case .browserOpened:
                break
            case .needsCode:
                if self.loginState == .waitingBrowser { self.loginState = .needsCode }
            case .finished(let ok):
                self.loginState = .idle
                if ok {
                    self.append(ChatMessage(role: .assistant, text: "로그인됐어요."))
                    if let prompt {
                        self.queue.append(.chat(prompt, self.lastFailedImages))
                        self.processNext()
                    }
                } else {
                    self.append(ChatMessage(role: .error, text: "로그인하지 못했어요. 다시 시도해 주세요."))
                    self.lastFailedPrompt = prompt
                }
            }
        }
    }

    func submitLoginCode(_ code: String) {
        loginState = .verifying
        login.submit(code: code)
    }

    func cancelLogin() {
        login.cancel()
        loginState = .idle
    }

    /// 카드에서 초안을 고치거나 등록했을 때 그 메시지를 바꿔 저장한다.
    func update(_ id: UUID, _ change: (inout ChatMessage) -> Void) {
        guard let i = messages.firstIndex(where: { $0.id == id }) else { return }
        change(&messages[i])
        save()
    }

    private func append(_ message: ChatMessage) {
        messages.append(message)
        save()
    }

    private func save() {
        guard let storage else { return }
        try? FileManager.default.createDirectory(at: storage.deletingLastPathComponent(), withIntermediateDirectories: true)
        let stored = Stored(messages: messages, sessionId: sessionId, routing: fixedRepo == nil ? routing : nil)
        try? JSONEncoder().encode(stored).write(to: storage, options: .atomic)
    }
}
