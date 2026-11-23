import PokeMateCore
import SwiftUI

struct WorkTabView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var center = WorkSessionCenter.shared
    @State private var selected: String?
    @State private var resetting: WorkSession?

    private static let clock = Date.VerbatimFormatStyle(format: "\(month: .defaultDigits)/\(day: .defaultDigits) \(hour: .twoDigits(clock: .twentyFourHour, hourCycle: .zeroBased)):\(minute: .twoDigits)",
                                                         timeZone: .current, calendar: .current)

    var body: some View {
        let sessions = center.book.sessions.values.sorted { $0.lastUsed > $1.lastUsed }
        VStack(spacing: 10) {
            header(sessions)
            if sessions.isEmpty {
                Text("레포별로 지식이 쌓이는 Claude 세션이에요. 채팅에서 보낼 곳을 고르거나 자동 처리 알람이 돌면 생겨요.")
                    .font(.callout).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .pmCard(12)
                Spacer()
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        let legacy = sessions.filter { PipelineRole.split($0.repo) == nil }
                        ForEach(Array(legacy.enumerated()), id: \.element.repo) { index, session in
                            if index > 0 { Divider().padding(.vertical, 2) }
                            row(session)
                        }
                        ForEach(Self.domains(sessions, pipelines: center.pipelines), id: \.self) { domain in
                            if !legacy.isEmpty || domain != Self.domains(sessions, pipelines: center.pipelines).first { Divider().padding(.vertical, 2) }
                            domainBlock(domain)
                        }
                    }
                }
                .frame(maxHeight: selected == nil ? .infinity : 220)
                .fixedSize(horizontal: false, vertical: selected != nil)
                .pmCard(12)
                if let repo = selected, center.book.sessions[repo] != nil {
                    WorkSessionDetail(repo: repo, model: model) { selected = nil }
                        .id(repo)
                        .frame(maxHeight: .infinity)
                        .pmCard(12)
                } else {
                    Spacer(minLength: 0)
                }
            }
        }
        .padding(.horizontal, Theme.gutter)
        .onChange(of: center.requestReset) { takeReset() }
        .onAppear { takeReset() }
        .confirmationDialog("새 세션으로 바꿀까요?", isPresented: Binding(get: { resetting != nil }, set: { if !$0 { resetting = nil } }),
                            presenting: resetting) { session in
            Button("요약을 넘겨서 새로") { center.reset(repo: session.repo, carrySummary: true, chat: model.chat) }
            Button("요약 없이 새로") { center.reset(repo: session.repo, carrySummary: false, chat: model.chat) }
            Button("취소", role: .cancel) {}
        } message: { session in
            Text("\(session.repo) 세션은 보관되고, 다음 메시지·티켓부터 새 세션으로 처리해요.")
        }
    }

    private func takeReset() {
        if let repo = center.requestReset, let session = center.book.sessions[repo] {
            selected = repo
            resetting = session
        }
        center.requestReset = nil
    }

    private func header(_ sessions: [WorkSession]) -> some View {
        let runningCount = center.running.keys.filter { $0 != WorkSessionCenter.routingSlot }.count
        return VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Image(systemName: "rectangle.stack").foregroundStyle(Color.orange)
                Text("작업 세션").font(.headline)
                Spacer()
                NewWorkSessionMenu(existing: Set(center.book.sessions.keys)) { repo in
                    center.create(repo: repo)
                    selected = repo
                }
                Button { center.reload() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.borderless).help("새로고침")
            }
            Text("작업 세션 \(sessions.count)개 · 실행 중 \(runningCount) · 이번 달 \(TokenFormat.compact(center.monthUsage.total)) · \(TokenFormat.usd(center.monthUsage.costUSD))")
                .font(.caption).foregroundStyle(.secondary).monospacedDigit()
            if let routing = center.running[WorkSessionCenter.routingSlot] {
                Text("레포 정하는 중 · \(routing)").font(.caption2).foregroundStyle(.secondary)
            }
        }
        .pmCard(12)
    }

    static func domains(_ sessions: [WorkSession], pipelines: [String: PipelineState]) -> [String] {
        Array(Set(sessions.compactMap { PipelineRole.split($0.repo)?.domain } + pipelines.keys + WorkSessionCenter.shared.reviewing.keys)).sorted()
    }

    private func domainBlock(_ domain: String) -> some View {
        let state = center.pipelines[domain]
        return VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Image(systemName: "person.3").font(.caption).foregroundStyle(Color.orange)
                Text(domain).font(.callout.weight(.semibold)).lineLimit(1)
                if let state {
                    Text(state.statusText).font(.caption2.weight(.bold))
                        .foregroundStyle(state.isActive ? Color.orange : Color.secondary)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background((state.isActive ? Color.orange : Color.secondary).opacity(0.15), in: Capsule())
                    if state.totalCost > 0 { Text(TokenFormat.usd(state.totalCost)).font(.caption2).foregroundStyle(.secondary).monospacedDigit() }
                }
                if let review = center.reviewing[domain] {
                    Text(review).font(.caption2.weight(.bold)).foregroundStyle(Color.orange).lineLimit(1)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background(Color.orange.opacity(0.15), in: Capsule())
                        .help(review)
                }
                if let waiting = center.queued[domain], !waiting.isEmpty {
                    Text("대기 \(waiting.count)").font(.caption2).foregroundStyle(.secondary)
                        .help(waiting.joined(separator: "\n"))
                }
                Spacer()
            }
            .padding(.horizontal, 6)
            ForEach(PipelineRole.allCases, id: \.self) { role in
                let key = PipelineRole.sessionKey(domain: domain, role: role)
                if let session = center.book.sessions[key] {
                    row(session, name: role.label, active: (state?.isActive == true && state?.role == role) || (role == .reviewer && center.reviewing[domain] != nil))
                        .padding(.leading, 12)
                } else if let last = center.book.lastArchived(key) {
                    // 끝나 보관한 건(티켓 끝·PR 승인)의 세션: 마지막 건과 새로 시작했던 이유만 보인다
                    Text("\(role.label) · 끝난 건 \(last.keys.last ?? "-") · 마지막 \(last.lastUsed.formatted(Self.clock))\(last.freshReason.map { " · 새로 시작: \($0)" } ?? "")")
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        .padding(.leading, 18).padding(.vertical, 2)
                } else {
                    Text("\(role.label) · 아직 없음").font(.caption).foregroundStyle(.secondary)
                        .padding(.leading, 18).padding(.vertical, 2)
                }
            }
        }
        .padding(.vertical, 4)
    }

    private func row(_ session: WorkSession, name: String? = nil, active: Bool = false) -> some View {
        let isSelected = selected == session.repo
        return VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Text(name ?? session.repo).font(.callout.weight(isSelected ? .semibold : .regular)).lineLimit(1)
                if active {
                    Text("실행 중").font(.caption2.weight(.bold)).foregroundStyle(Color.orange)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background(Color.orange.opacity(0.15), in: Capsule())
                }
                if let key = center.running[session.repo] {
                    Text("실행 중 · \(key)").font(.caption2.weight(.bold)).foregroundStyle(Color.orange)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background(Color.orange.opacity(0.15), in: Capsule())
                }
                if let waiting = center.queued[session.repo], !waiting.isEmpty {
                    Text("대기 \(waiting.count)").font(.caption2).foregroundStyle(.secondary)
                }
                Spacer()
                Menu {
                    Button("새 세션으로 바꾸기…") { resetting = session }
                    Button("보관") {
                        if selected == session.repo { selected = nil }
                        center.archive(repo: session.repo)
                    }
                } label: { Image(systemName: "ellipsis.circle") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .font(.caption)
            }
            captionView(session).font(.caption).lineLimit(1).monospacedDigit()
        }
        .padding(.vertical, 4)
        .padding(.horizontal, 6)
        .background(isSelected ? Color.orange.opacity(0.10) : Color.clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .contentShape(Rectangle())
        .onTapGesture { selected = isSelected ? nil : session.repo }
        .help("눌러서 기록 보기·말 걸기")
    }

    private func captionView(_ s: WorkSession) -> Text {
        let threshold = model.settings.workSessionAlertPercent
        if s.id.isEmpty {
            if let reason = s.freshReason {
                return Text("새로 시작: \(reason) · 다음 실행에서 노트·지식부터 읽어요").foregroundColor(.secondary)
            }
            return Text("새로 만듦 · 첫 메시지에서 레포 지식을 모아요").foregroundColor(.secondary)
        }
        var text = Text("마지막 \(s.lastUsed.formatted(Self.clock)) · 티켓 \(s.keys.count)").foregroundColor(.secondary)
        if let reason = s.freshReason { text = text + Text(" · 새로 시작: \(reason)").foregroundColor(.secondary) }
        if let p = s.contextPercent {
            text = text + Text(" · ").foregroundColor(.secondary)
                + Text("문맥 \(p)%").foregroundColor(threshold > 0 && p >= threshold ? .orange : .secondary)
        }
        return text + Text(" · \(s.usage.shortText)").foregroundColor(.secondary)
    }
}

struct NewWorkSessionMenu: View {
    let existing: Set<String>
    var label = "새 작업 세션"
    let create: (String) -> Void

    var body: some View {
        Menu {
            let repos = WorkspaceRepos.list()
            let candidates = repos.filter { !existing.contains($0) }
            if candidates.isEmpty {
                Text(repos.isEmpty ? "~/Workspace 아래에서 git 레포를 찾지 못했어요" : "모든 레포에 이미 작업 세션이 있어요")
            }
            ForEach(candidates, id: \.self) { repo in
                Button(repo) { create(repo) }
            }
        } label: {
            Label(label, systemImage: "plus")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .font(.caption)
        .help("~/Workspace 아래 레포를 골라 빈 작업 세션을 만들어요")
    }
}

private struct WorkSessionDetail: View {
    let repo: String
    @ObservedObject var model: AppModel
    let close: () -> Void
    @ObservedObject var center = WorkSessionCenter.shared
    @StateObject private var chat: ChatSession
    @State private var entries: [TranscriptEntry] = []
    @State private var input = ""

    init(repo: String, model: AppModel, close: @escaping () -> Void) {
        self.repo = repo
        self.model = model
        self.close = close
        _chat = StateObject(wrappedValue: ChatSession(settings: { [weak model] in model?.settings ?? .default }, storage: nil, workRepo: repo))
    }

    var body: some View {
        let session = center.book.sessions[repo]
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("\(repo) 기록").font(.callout.weight(.semibold)).lineLimit(1)
                Spacer()
                Button { load() } label: { Image(systemName: "arrow.clockwise") }.help("기록 다시 읽기")
                Button(action: close) { Image(systemName: "xmark") }.help("닫기")
            }
            .buttonStyle(.borderless).font(.caption)
            if let session, !session.keys.isEmpty {
                Text("처리한 티켓: \(session.keys.joined(separator: ", "))").font(.caption).foregroundStyle(.secondary).lineLimit(2)
            }
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        if entries.isEmpty {
                            Text(session?.id.isEmpty == true ? "아직 대화가 없어요. 첫 메시지를 보내면 레포 지식부터 모아요." : "기록 파일을 찾지 못했어요.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        ForEach(entries) { e in
                            VStack(alignment: .leading, spacing: 2) {
                                Text("\(label(e.kind)) · \(e.time)").font(.caption2.weight(.semibold)).foregroundStyle(color(e.kind))
                                if e.kind == .assistant {
                                    MarkdownContent(text: e.text).font(.callout).textSelection(.enabled)
                                } else {
                                    Text(e.text).font(e.kind == .tool || e.kind == .result ? .caption.monospaced() : .callout)
                                        .foregroundStyle(e.kind == .result ? Color.secondary : Color.primary).textSelection(.enabled)
                                }
                            }
                        }
                        if chat.isBusy, let last = chat.messages.last(where: { $0.role == .user }) {
                            Text("보낸 말 · 지금").font(.caption2.weight(.semibold)).foregroundStyle(Color.orange)
                            Text(last.text).font(.callout).textSelection(.enabled)
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text(chat.activity ?? "생각 중…").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                Button("취소") { chat.cancel() }.buttonStyle(.link)
                            }
                        }
                        if let request = chat.pendingPermission {
                            PermissionCard(chat: chat, request: request)
                        }
                        if !chat.isBusy, let error = chat.messages.last, error.role == .error {
                            Text(error.text).font(.caption).foregroundStyle(.red)
                        }
                        Color.clear.frame(height: 1).id("bottom")
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .onAppear { proxy.scrollTo("bottom", anchor: .bottom) }
                .onChange(of: entries.count) { proxy.scrollTo("bottom", anchor: .bottom) }
                .onChange(of: chat.isBusy) { proxy.scrollTo("bottom", anchor: .bottom) }
            }
            HStack {
                TextField("", text: $input, prompt: Text("이 세션에 이어서 보내기"), axis: .vertical)
                    .textFieldStyle(.roundedBorder).lineLimit(1...4)
                    .onSubmit(send)
                Button("보내기", action: send).disabled(input.trimmingCharacters(in: .whitespaces).isEmpty || chat.isBusy)
            }
        }
        .task(id: session?.id) { load() }
        .onChange(of: chat.isBusy) { if !chat.isBusy { load() } }
    }

    private func load() {
        guard let id = center.book.sessions[repo]?.id, !id.isEmpty,
              let file = WorkSessionCenter.transcriptFile(sessionId: id), let data = try? Data(contentsOf: file) else {
            entries = []
            return
        }
        entries = SessionTranscript.parse(data)
    }

    private func send() {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !chat.isBusy else { return }
        input = ""
        chat.send(text)
    }

    private func label(_ k: TranscriptEntry.Kind) -> String {
        switch k {
        case .user: return "보낸 말"
        case .assistant: return "Claude"
        case .tool: return "도구"
        case .result: return "도구 결과"
        }
    }

    private func color(_ k: TranscriptEntry.Kind) -> Color {
        switch k {
        case .user: return .orange
        case .assistant: return .primary
        case .tool, .result: return .secondary
        }
    }
}
