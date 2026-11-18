import PokeMateCore
import SwiftUI

struct ChatView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var chat: ChatSession
    @State private var input = ""
    @FocusState private var inputFocused: Bool
    @State private var pending: [ImageAttachment] = []
    @State private var dropTargeted = false

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        if model.settings.ownerName.isEmpty {
                            OnboardingCard(model: model, chat: chat)
                        } else if chat.messages.isEmpty {
                            Text("무엇이든 물어보세요. 예: 내 미완료 Jira 알려줘")
                                .foregroundStyle(.secondary)
                                .padding(.top, 40)
                                .frame(maxWidth: .infinity)
                        }
                        ForEach(chat.messages) { message in
                            MessageRow(message: message) { system in
                                model.selected = .connections
                                _ = system
                            }
                            .id(message.id)
                            if let repo = message.routeSuggest {
                                RouteSuggestCard(repo: repo, sent: message.routeSent) {
                                    WorkSessionCenter.shared.create(repo: repo)
                                    chat.sendSuggested(message.id, repo: repo)
                                }
                            }
                            if !message.alarms.isEmpty {
                                AlarmProposalCard(alarms: message.alarms,
                                                  registered: message.alarms.allSatisfy { a in model.alarms.contains { $0.id == a.id } },
                                                  register: { model.register(message.alarms) },
                                                  open: { model.selected = .alarms })
                            }
                            if !message.rules.isEmpty {
                                RuleProposalCard(notes: message.rules, saved: message.rulesSaved) {
                                    for note in message.rules {
                                        if note.isForget { try? Memory.forget(note.text, in: note.file) }
                                        else if note.isMemory { try? Memory.add(note.text) }
                                        else { try? Rules.append(note.text, to: note.file) }
                                    }
                                    chat.update(message.id) { $0.rulesSaved = true }
                                }
                            }
                            if !message.notes.isEmpty {
                                NoteProposalCard(drafts: message.notes, saved: message.notesSaved) {
                                    let urls = message.notes.compactMap { try? Notes.create(title: $0.title, body: $0.body) }
                                    chat.update(message.id) { $0.notesSaved = urls.map(\.path) }
                                } open: { path in
                                    model.openNote = URL(fileURLWithPath: path)
                                    model.selected = .notes
                                }
                            }
                            if !message.worklogs.isEmpty {
                                WorklogCard(model: model, chat: chat, messageID: message.id,
                                            drafts: message.worklogs, done: Set(message.worklogsDone))
                            }
                            if let draft = message.jira {
                                JiraDraftCard(model: model, chat: chat, messageID: message.id, draft: draft,
                                              created: message.jiraCreated)
                            }
                        }
                        if chat.isBusy {
                            HStack(spacing: 8) {
                                ProgressView().controlSize(.small)
                                Text(chat.activity ?? "생각 중…").foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                                Button("취소") { chat.cancel() }.buttonStyle(.link)
                            }
                            .id("busy")
                        }
                        if let request = chat.pendingPermission {
                            PermissionCard(chat: chat, request: request).id("permission")
                        }
                        if chat.loginState != .idle {
                            LoginBox(chat: chat).id("login")
                        }
                        if chat.lastFailedPrompt != nil && !chat.isBusy {
                            Button("다시 시도") { chat.retry() }
                        }
                        Color.clear.frame(height: 1).id(Self.bottomID)
                    }
                    .padding(12)
                }
                .defaultScrollAnchor(.bottom)
                .background(Color.clear)
                // 처음 열 때, 메시지가 늘 때, "생각 중"·로그인 상자가 나타날 때 항상 맨 아래로
                .onAppear { scrollToBottom(proxy, animated: false) }
                .onChange(of: chat.messages.count) { scrollToBottom(proxy) }
                .onChange(of: chat.isBusy) { scrollToBottom(proxy) }
                .onChange(of: chat.loginState) { scrollToBottom(proxy) }
                .onChange(of: chat.pendingPermission?.id) { scrollToBottom(proxy) }
            }

            VStack(alignment: .leading, spacing: 6) {
                VStack(alignment: .leading, spacing: 8) {
                    if !pending.isEmpty {
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 8) {
                                ForEach(pending, id: \.path) { image in
                                    Thumbnail(path: image.path, size: 56)
                                        .overlay(alignment: .topTrailing) {
                                            Button { pending.removeAll { $0.path == image.path } } label: {
                                                Image(systemName: "xmark.circle.fill").foregroundStyle(.white, .black.opacity(0.6))
                                            }
                                            .buttonStyle(.plain).padding(2)
                                        }
                                }
                            }
                        }
                    }
                    HStack(alignment: .center, spacing: 10) {
                        Menu {
                            Button { add(ImageLoader.chooseFiles()) } label: { Label("이미지 첨부", systemImage: "photo") }
                            Button { chat.newConversation() } label: { Label("새 대화", systemImage: "square.and.pencil") }
                                .disabled(chat.messages.isEmpty && !chat.isBusy)
                        } label: {
                            Image(systemName: "plus").font(.system(size: 15, weight: .medium))
                        }
                        .menuStyle(.borderlessButton)
                        .menuIndicator(.hidden)
                        .fixedSize()
                        .help("이미지 첨부 (⌘V·끌어다 놓기도 돼요) · 새 대화")
                        TextField("", text: $input, prompt: Text("↩ 키로 보내기, ⇧↩ 줄바꿈"), axis: .vertical)
                            .textFieldStyle(.plain)
                            .lineLimit(1...6)
                            .focused($inputFocused)
                            .onSubmit(send)
                            .onKeyPress(.return, phases: .down) { press in
                                guard press.modifiers.contains(.shift) else { return .ignored }
                                input.append("\n")
                                return .handled
                            }
                        if chat.isBusy {
                            Button { chat.cancel() } label: {
                                Image(systemName: "stop.fill").font(.system(size: 10, weight: .bold))
                                    .foregroundStyle(Color(nsColor: .textBackgroundColor))
                                    .frame(width: 24, height: 24)
                                    .background(Color.primary, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                            }
                            .buttonStyle(.plain)
                            .help("멈추기")
                        } else {
                            let empty = input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && pending.isEmpty
                            Button(action: send) {
                                Image(systemName: "arrow.up").font(.system(size: 12, weight: .bold))
                                    .foregroundStyle(Color(nsColor: .textBackgroundColor))
                                    .frame(width: 24, height: 24)
                                    .background(Color.primary.opacity(empty ? 0.25 : 1), in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                            }
                            .buttonStyle(.plain)
                            .disabled(empty)
                            .help("보내기")
                        }
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(Color.primary.opacity(0.12), lineWidth: 1))
                HStack(spacing: 10) {
                    RoutePicker(chat: chat)
                    Spacer()
                    Menu {
                        Picker("모델", selection: $model.settings.model) {
                            ForEach([""] + ClaudeCommand.models, id: \.self) { Text(ModelLabels.models[$0] ?? $0).tag($0) }
                        }
                        .pickerStyle(.inline)
                    } label: { Text(ModelLabels.shortModel(model.settings.model)) }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    Menu {
                        Picker("추론 수준", selection: $model.settings.effort) {
                            ForEach([""] + ClaudeCommand.efforts, id: \.self) { Text(ModelLabels.efforts[$0] ?? $0).tag($0) }
                        }
                        .pickerStyle(.inline)
                    } label: { Text(ModelLabels.shortEffort(model.settings.effort)) }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                }
                .font(.caption)
                .controlSize(.small)
                .foregroundStyle(.secondary)
                .opacity(0.8)
                .padding(.trailing, 4)
            }
            .padding(10)
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 12).strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 3, dash: [8]))
                    .background(Color.accentColor.opacity(0.06))
                    .overlay(Text("여기에 놓으면 이미지를 첨부해요").font(.headline))
                    .padding(6)
            }
        }
        .onDrop(of: [.fileURL, .image], isTargeted: $dropTargeted) { providers in
            handleDrop(providers)
            return true
        }
        .onReceive(NotificationCenter.default.publisher(for: .pokeMatePasteImages)) { _ in
            add(ImageLoader.fromPasteboard())
        }
        .onAppear { inputFocused = true }
    }

    private static let bottomID = "chat-bottom"

    private func scrollToBottom(_ proxy: ScrollViewProxy, animated: Bool = true) {
        // 레이아웃이 끝난 뒤에 이동해야 마지막 줄까지 내려간다.
        DispatchQueue.main.async {
            if animated {
                withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(Self.bottomID, anchor: .bottom) }
            } else {
                proxy.scrollTo(Self.bottomID, anchor: .bottom)
            }
        }
    }

    private func send() {
        chat.send(input, images: pending)
        input = ""
        pending = []
    }

    private func add(_ images: [ImageAttachment]) {
        pending = Array((pending + images).prefix(ImageLoader.maxCount))
    }

    private func handleDrop(_ providers: [NSItemProvider]) {
        for provider in providers {
            if provider.canLoadObject(ofClass: URL.self) {
                _ = provider.loadObject(ofClass: URL.self) { url, _ in
                    guard let url, let image = ImageLoader.load(url: url) else { return }
                    DispatchQueue.main.async { add([image]) }
                }
            } else if provider.canLoadObject(ofClass: NSImage.self) {
                _ = provider.loadObject(ofClass: NSImage.self) { object, _ in
                    guard let nsImage = object as? NSImage, let image = ImageLoader.attachment(from: nsImage) else { return }
                    DispatchQueue.main.async { add([image]) }
                }
            }
        }
    }
}

private struct MessageRow: View {
    let message: ChatMessage
    let onReconnect: (String) -> Void

    var body: some View {
        VStack(alignment: message.role == .user ? .trailing : .leading, spacing: 6) {
            if let session = message.session {
                Label(session, systemImage: "rectangle.stack")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
            if !message.images.isEmpty {
                HStack(spacing: 6) {
                    ForEach(message.images, id: \.self) { Thumbnail(path: $0, size: 120) }
                }
            }
            if !message.text.isEmpty {
            MarkdownContent(text: message.text)
                .textSelection(.enabled)
                .padding(10)
                .background(background, in: RoundedRectangle(cornerRadius: 10))
            }
            ForEach(ToolErrorCode.detect(in: message.text), id: \.self) { code in
                switch code {
                case .authExpired(let system), .notConnected(let system):
                    Button("\(system) 다시 연결") { onReconnect(system) }
                case .networkUnreachable:
                    EmptyView()
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: message.role == .user ? .trailing : .leading)
    }

    private var background: Color {
        switch message.role {
        case .user: return Color.orange.opacity(0.18)
        case .assistant: return Color.primary.opacity(0.05)
        case .error: return Color.red.opacity(0.15)
        }
    }
}

private struct RoutePicker: View {
    @ObservedObject var chat: ChatSession
    @ObservedObject var center = WorkSessionCenter.shared

    var body: some View {
        let sessions = center.book.sessions.values.filter { PipelineRole.split($0.repo) == nil }.sorted { $0.lastUsed > $1.lastUsed }
        Menu {
            Button { chat.route(to: nil) } label: {
                if chat.routing.isMate { Label("메이트", systemImage: "checkmark") } else { Text("메이트") }
            }
            if !sessions.isEmpty {
                Divider()
                Section("작업 세션") {
                    ForEach(sessions, id: \.repo) { s in
                        Button { chat.route(to: s.repo) } label: {
                            if chat.routing.repo == s.repo { Label(s.repo, systemImage: "checkmark") } else { Text(s.repo) }
                        }
                    }
                }
            }
            Divider()
            NewWorkSessionMenu(existing: Set(center.book.sessions.keys), label: "새 작업 세션") { repo in
                center.create(repo: repo)
                chat.route(to: repo)
            }
        } label: {
            Text("보낼 곳: \(chat.routing.repo ?? "메이트")").lineLimit(1).truncationMode(.middle)
        }
        .menuStyle(.borderlessButton)
        .fixedSize(horizontal: false, vertical: true)
        .frame(maxWidth: 220, alignment: .leading)
        .foregroundStyle(chat.routing.isMate ? Color.secondary : Color.orange)
        .help("이 대화에서 메시지를 보낼 곳. 새 대화를 시작하면 메이트로 돌아가요")
    }
}

private struct RouteSuggestCard: View {
    let repo: String
    let sent: Bool
    let send: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "rectangle.stack").foregroundStyle(Color.orange)
            Text("\(repo) 작업 세션으로 보낼까요?").font(.callout).lineLimit(2)
            Spacer()
            if sent {
                Text("보냈어요").font(.caption).foregroundStyle(.secondary)
            } else {
                Button("보내기", action: send).controlSize(.small)
            }
        }
        .padding(10)
        .background(Color.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

/// 답변이 제안한 알림. [알림에 등록]을 눌러야 알림 메뉴에 들어간다.
private struct AlarmProposalCard: View {
    let alarms: [Alarm]
    let registered: Bool
    let register: () -> Void
    let open: () -> Void

    /// 들어갈 그룹. 직접 말한 이름, 없으면 같은 제목 여러 개일 때 그 제목 (AppModel.register 와 같은 규칙).
    private var groupName: String? {
        if let named = alarms.compactMap(\.group).first { return named }
        return alarms.count > 1 && Set(alarms.map(\.title)).count == 1 ? alarms[0].title : nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Label("알림 \(alarms.count)개", systemImage: "bell.badge").font(.callout.weight(.medium))
                if let group = groupName {
                    Label(group, systemImage: "folder").font(.caption).foregroundStyle(.secondary)
                }
            }
            ForEach(alarms) { alarm in
                HStack(spacing: 6) {
                    Text(alarm.timeText).monospacedDigit()
                    Text(alarm.repeatRule.description).foregroundStyle(.secondary)
                    Text(alarm.title).lineLimit(1)
                }
                .font(.callout)
            }
            HStack {
                if registered {
                    Label("알림 메뉴에 등록했어요", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    Button("알림 보기", action: open).buttonStyle(.link)
                } else {
                    Button("알림에 등록", action: register).buttonStyle(.borderedProminent)
                }
            }
            .padding(.top, 2)
        }
        .padding(10)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
    }
}

/// 답변이 제안한 Jira 이슈 초안. [Jira에 등록]을 눌러야 등록되고, [수정]으로 고칠 수 있다.
private struct JiraDraftCard: View {
    @ObservedObject var model: AppModel
    let chat: ChatSession
    let messageID: UUID
    let draft: JiraDraft
    let created: JiraCreated?
    @State private var working = false
    @State private var error: String?
    @State private var editing = false
    @State private var showDescription = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Label("Jira 이슈", systemImage: "square.and.pencil").font(.callout.weight(.medium))
                Text("\(draft.project) · \(draft.issueType)").font(.caption.weight(.medium))
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(Color.accentColor.opacity(0.12), in: Capsule())
            }
            Text(draft.summary).font(.body.weight(.semibold)).textSelection(.enabled)
            Label("담당자 나 (연결된 Jira 계정)", systemImage: "person").font(.caption).foregroundStyle(.secondary)
            if !draft.periodText.isEmpty {
                Label(draft.periodText, systemImage: "calendar").font(.caption).foregroundStyle(.secondary)
            }
            if !draft.epicLink.isEmpty {
                Label("Epic \(draft.epicLink)", systemImage: "link").font(.caption).foregroundStyle(.secondary)
            }
            if !draft.description.isEmpty {
                DisclosureGroup(isExpanded: $showDescription) {
                    Text(draft.description).font(.caption.monospaced()).textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(.top, 4)
                } label: { Text("설명").font(.caption).foregroundStyle(.secondary) }
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            HStack {
                if let created {
                    Label("\(created.key) 등록했어요", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    Button("열기") { if let url = URL(string: created.url) { NSWorkspace.shared.open(url) } }.buttonStyle(.link)
                } else {
                    Button(action: create) {
                        if working { ProgressView().controlSize(.small) } else { Text("Jira에 등록") }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(working)
                    Button("수정") { editing = true }.disabled(working)
                }
            }
            .padding(.top, 2)
        }
        .padding(10)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
        .sheet(isPresented: $editing) {
            JiraDraftEditor(draft: draft) { edited in chat.update(messageID) { $0.jira = edited } }
        }
    }

    private func create() {
        working = true
        error = nil
        Task {
            do {
                let result = try await MateCLI.jiraCreate(draft, settings: model.settings)
                chat.update(messageID) { $0.jiraCreated = result }
            } catch {
                self.error = error.localizedDescription
            }
            working = false
        }
    }
}

/// 초안 고치기.
private struct JiraDraftEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State var draft: JiraDraft
    let onSave: (JiraDraft) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Jira 이슈 수정").font(.headline)
            Form {
                field("프로젝트 키", $draft.project, "예: MYPROJ")
                field("이슈 유형", $draft.issueType, "예: Story, Task")
                field("제목", $draft.summary, "")
                VStack(alignment: .leading, spacing: 4) {
                    Text("설명 (Jira 위키 문법)").font(.caption).foregroundStyle(.secondary)
                    TextEditor(text: $draft.description).font(.body.monospaced()).frame(minHeight: 140)
                        .scrollContentBackground(.hidden)
                        .padding(4)
                        .background(EditorStyle.field, in: RoundedRectangle(cornerRadius: 6))
                        .overlay(RoundedRectangle(cornerRadius: 6).stroke(EditorStyle.border))
                }
                .grayCard()
                field("Original Estimate", $draft.originalEstimate, "예: 1d, 0.5d, 4h (비우면 안 넣음)")
                field("Target start", $draft.targetStart, "yyyy-MM-dd, 비우면 안 넣음")
                field("Target end", $draft.targetEnd, "yyyy-MM-dd, 비우면 안 넣음")
                field("Epic Link", $draft.epicLink, "예: MYPROJ-100, 비우면 안 넣음")
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button("취소") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("저장") { onSave(draft); dismiss() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!draft.isComplete)
            }
        }
        .padding(20)
        .frame(width: 520, height: 620)
    }

    private func field(_ title: String, _ text: Binding<String>, _ hint: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            TextField("", text: text, prompt: Text(hint)).whiteField().labelsHidden()
        }
        .grayCard()
    }
}

/// 처음 사용: 주인 이름을 묻고 인사한다. Claude 를 부르지 않는다.
private struct OnboardingCard: View {
    @ObservedObject var model: AppModel
    @ObservedObject var usage = UsageStore.shared
    let chat: ChatSession
    @State private var name = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("👋 안녕하세요! 저는 \(usage.mateName(model.settings))예요.").font(.headline)
            Text("앞으로 Jira, Wiki, 근무일정, 배포, 메일 같은 업무를 같이 챙겨 드릴게요.\n먼저, 어떻게 불러 드릴까요?")
            HStack {
                TextField("", text: $name, prompt: Text("호칭. 예: 길동, 트레이너"))
                    .textFieldStyle(.roundedBorder)
                    .focused($focused)
                    .onSubmit(save)
                Button("반가워요", action: save)
                    .buttonStyle(.borderedProminent)
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(12)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
        .onAppear { focused = true }
    }

    private func save() {
        let owner = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !owner.isEmpty else { return }
        model.settings.ownerName = String(owner.prefix(20))
        chat.say("""
            반가워요, \(model.settings.ownerCallName)! 🙌 앞으로 잘 부탁드려요.

            **먼저 해 주세요**
            - 오른쪽 🔌 연결 → [일괄 연동] 으로 사내 계정을 연결해 주세요. 그래야 Jira·Wiki·근무일정을 볼 수 있어요.

            **이렇게 말해 보세요**
            - 내 미완료 Jira 알려줘
            - 평일 9시에 이번 주 근무일정 알려줘 (알림 등록)
            - 지라 등록해줘: 쿠폰 API 응답 필드 추가 (처음엔 기본값을 여쭤봐요)
            - 앞으로 지라 제목 앞에 [회의] 붙여줘 (rules/ 에 규칙으로 저장)
            """)
    }
}

/// 답변이 제안한 규칙. [규칙에 저장]을 눌러야 이 Mac 의 규칙 파일에 들어간다.
private struct RuleProposalCard: View {
    let notes: [RuleNote]
    let saved: Bool
    let save: () -> Void

    /// 기억만 / 잊기만 / 그 밖(규칙)에 따라 제목·버튼 말을 바꾼다.
    private var kind: (title: String, symbol: String, button: String, done: String) {
        if notes.allSatisfy(\.isForget) { return ("잊기", "eraser", "잊기", "지웠어요") }
        if notes.allSatisfy(\.isMemory) { return ("기억", "brain", "기억하기", "기억했어요. 새 대화에서도 알아요") }
        return ("규칙", "pin", "규칙에 저장", "저장했어요. 다음 대화부터 늘 적용돼요")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("\(kind.title) \(notes.count)개", systemImage: kind.symbol).font(.callout.weight(.medium))
            ForEach(notes, id: \.self) { note in
                HStack(alignment: .top, spacing: 6) {
                    Text(note.isForget ? "지울 것" : note.isMemory ? "기억" : "\(note.file).md")
                        .font(.caption.monospaced()).foregroundStyle(.secondary)
                    Text(note.text).font(.callout).strikethrough(note.isForget)
                }
            }
            HStack {
                if saved {
                    Label(kind.done, systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    Button("폴더 열기") { NSWorkspace.shared.open(Rules.myDir) }.buttonStyle(.link)
                } else {
                    Button(kind.button, action: save).buttonStyle(.borderedProminent)
                }
            }
            .padding(.top, 2)
        }
        .padding(10)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
    }
}

/// 답변이 제안한 worklog. [기록하기]를 눌러야 Jira 에 기록된다. 이미 기록한 건 다시 보내지 않는다.
private struct WorklogCard: View {
    @ObservedObject var model: AppModel
    let chat: ChatSession
    let messageID: UUID
    let drafts: [WorklogDraft]
    let done: Set<String>
    @State private var working = false
    @State private var error: String?

    private var total: Int { drafts.map(\.minutes).reduce(0, +) }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Label("Work log \(drafts.count)건", systemImage: "clock.badge.checkmark").font(.callout.weight(.medium))
                Text("합계 \(total / 60)시간\(total % 60 > 0 ? " \(total % 60)분" : "")").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(drafts) { draft in
                HStack(alignment: .top, spacing: 6) {
                    Image(systemName: done.contains(draft.id) ? "checkmark.circle.fill" : "circle")
                        .foregroundStyle(done.contains(draft.id) ? Color.green : Color.secondary)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("\(draft.issueKey)\(draft.summary.isEmpty ? "" : " · \(draft.summary)")").font(.callout.weight(.medium))
                        Text("\(draft.date) \(draft.timeText)\(draft.comment.isEmpty ? "" : " · \(draft.comment)")")
                            .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    }
                }
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            HStack {
                if drafts.allSatisfy({ done.contains($0.id) }) {
                    Label("Jira 에 기록했어요", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                } else {
                    Button(action: record) {
                        if working { ProgressView().controlSize(.small) } else { Text("기록하기") }
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(working)
                }
            }
            .padding(.top, 2)
        }
        .padding(10)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
    }

    private func record() {
        working = true
        error = nil
        Task {
            for draft in drafts where !done.contains(draft.id) {
                do {
                    try await MateCLI.jiraWorklog(draft, settings: model.settings)
                    chat.update(messageID) { $0.worklogsDone.append(draft.id) }
                } catch {
                    self.error = "\(draft.issueKey) \(draft.start): \(error.localizedDescription)"
                    break
                }
            }
            working = false
        }
    }
}

/// 답변이 제안한 메모. [메모에 저장]을 눌러야 notes/ 에 md 파일로 저장된다.
private struct NoteProposalCard: View {
    let drafts: [NoteDraft]
    let saved: [String]
    let save: () -> Void
    let open: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("메모 \(drafts.count)개", systemImage: "note.text").font(.callout.weight(.medium))
            ForEach(drafts, id: \.self) { draft in
                VStack(alignment: .leading, spacing: 2) {
                    Text(draft.title.isEmpty ? "(제목 없음)" : draft.title).font(.callout.weight(.semibold))
                    Text(draft.body).font(.caption).foregroundStyle(.secondary).lineLimit(4)
                }
            }
            HStack {
                if saved.isEmpty {
                    Button("메모에 저장", action: save).buttonStyle(.borderedProminent)
                } else {
                    Label("메모에 저장했어요", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                    if let first = saved.first {
                        Button("메모 보기") { open(first) }.buttonStyle(.link)
                    }
                }
            }
            .padding(.top, 2)
        }
        .padding(10)
        .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
    }
}

/// 로그인 진행 상자. 브라우저에 나온 코드를 붙여넣으면 claude 에게만 전달하고 지운다.
private struct LoginBox: View {
    @ObservedObject var chat: ChatSession
    @State private var code = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            switch chat.loginState {
            case .waitingBrowser:
                Label("브라우저에서 회사 계정으로 로그인하고 승인해 주세요.", systemImage: "safari")
            case .needsCode:
                Text("브라우저에 나온 코드를 붙여넣어 주세요.")
                HStack {
                    RomanTextField(placeholder: "코드", text: $code, secure: true, onSubmit: submit)
                    Button("확인", action: submit).disabled(code.isEmpty)
                }
            case .verifying:
                HStack { ProgressView().controlSize(.small); Text("로그인 확인 중…") }
            case .idle:
                EmptyView()
            }
            Button("로그인 취소") { chat.cancelLogin() }.buttonStyle(.link)
        }
        .padding(10)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 10))
    }

    private func submit() {
        chat.submitLoginCode(code)
        code = ""
    }
}

/// 첨부 이미지 미리보기. 누르면 미리보기 앱으로 크게 연다.
private struct Thumbnail: View {
    let path: String
    let size: CGFloat

    var body: some View {
        Group {
            if let image = NSImage(contentsOfFile: path) {
                Image(nsImage: image).resizable().scaledToFill()
            } else {
                Image(systemName: "photo").foregroundStyle(.secondary)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.black.opacity(0.08)))
        .onTapGesture { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
    }
}

struct PermissionCard: View {
    @ObservedObject var chat: ChatSession
    let request: PermissionRequest
    @State private var showAllowed = false

    private var menu: AllowMenu {
        AllowRules.menu(tool: request.tool, input: request.inputObject, suggested: request.rules, deny: ClaudeCommand.loadDeny())
    }

    var body: some View {
        let menu = menu
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "hand.raised.fill").foregroundStyle(.orange)
                Text("\(request.tool) 실행을 허용할까요?").font(.callout.weight(.semibold))
                Spacer()
                if !chat.sessionAllow.isEmpty {
                    Button("항상 허용 \(chat.sessionAllow.count)개") { showAllowed.toggle() }
                        .buttonStyle(.link).font(.caption)
                        .popover(isPresented: $showAllowed) {
                            VStack(alignment: .leading, spacing: 6) {
                                Text("이 대화에서 항상 허용 중").font(.caption.weight(.semibold))
                                ForEach(chat.sessionAllow, id: \.self) { Text($0).font(.caption.monospaced()) }
                                Button("모두 지우기") { chat.clearSessionAllow(); showAllowed = false }
                                    .controlSize(.small)
                            }
                            .padding(12)
                            .frame(minWidth: 220, alignment: .leading)
                        }
                }
            }
            Text(request.summary).font(.callout.monospaced()).textSelection(.enabled)
                .padding(8).frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            if !request.detail.isEmpty {
                Text(request.detail).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(6).textSelection(.enabled)
            }
            HStack(spacing: 8) {
                Button("허용") { chat.answerPermission(.allow) }.buttonStyle(.borderedProminent)
                Button("거부") { chat.answerPermission(.deny("사용자가 거부했어요")) }
                Menu("이 대화에서 항상 허용") {
                    if let reason = menu.unavailable {
                        Text(reason)
                    }
                    ForEach(menu.options) { option in
                        Button {
                            chat.answerPermission(.allowRules(option.rules))
                        } label: {
                            if option.caution {
                                Text("\(option.label)  ⚠︎ 주의 (바꾸거나 내보내는 명령)").foregroundColor(.red)
                            } else {
                                Text(option.label)
                            }
                        }
                        .help(option.rules.joined(separator: "\n"))
                    }
                }
                .fixedSize()
                Spacer()
                Text("10분 안에 답이 없으면 거부").font(.caption2).foregroundStyle(.tertiary)
            }
        }
        .padding(12)
        .background(Color.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(Color.orange.opacity(0.35), lineWidth: 1))
    }
}

struct MarkdownContent: View {
    let text: String

    var body: some View {
        if MarkdownBlocks.hasTable(text) {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(Array(MarkdownBlocks.split(text).enumerated()), id: \.offset) { _, block in
                    switch block {
                    case .text(let t): Text(LocalizedStringKey(MarkdownText.escapingSingleTildes(t)))
                    case .table(let table): MarkdownTableView(table: table)
                    }
                }
            }
        } else {
            Text(LocalizedStringKey(MarkdownText.escapingSingleTildes(text)))
        }
    }
}

struct MarkdownTableView: View {
    let table: MarkdownTable
    @State private var width: CGFloat = 0
    static let spacing: CGFloat = 12

    var body: some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(GeometryReader { g in
                Color.clear
                    .onAppear { width = g.size.width.rounded(.down) }
                    .onChange(of: g.size.width.rounded(.down)) { _, new in
                        if abs(new - width) >= 1 { width = new }
                    }
            })
    }

    @ViewBuilder private var content: some View {
        if width <= 0 {
            Color.clear.frame(height: 1)
        } else {
            let preview = TablePreview.make(natural: TableMeasure.natural(table), rowCount: table.rows.count,
                                            available: Double(width), spacing: Double(Self.spacing))
            VStack(alignment: .leading, spacing: 6) {
                TableLine(table: table, cells: table.header, widths: preview.widths.map { CGFloat($0) }, bold: true,
                          more: preview.hiddenColumns > 0 ? "+\(preview.hiddenColumns)열" : nil)
                Divider()
                ForEach(0..<preview.rows, id: \.self) { r in
                    TableLine(table: table, cells: table.rows[r], widths: preview.widths.map { CGFloat($0) }, bold: false, more: preview.hiddenColumns > 0 ? "" : nil)
                    if r < preview.rows - 1 { Divider().opacity(0.5) }
                }
                if preview.hiddenRows > 0 {
                    Text("+\(preview.hiddenRows)행").font(.caption).foregroundStyle(.secondary)
                }
            }
            .overlay(alignment: .topTrailing) {
                if preview.truncated {
                    Button { TableWindowController.show(table) } label: {
                        Image(systemName: "arrow.up.left.and.arrow.down.right")
                            .font(.caption.weight(.semibold))
                            .padding(4)
                            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                    }
                    .buttonStyle(.plain)
                    .help("표 크게 보기")
                    .offset(x: 2, y: -4)
                }
            }
        }
    }
}

enum TableMeasure {
    static func measure(_ text: String, bold: Bool) -> Double {
        let plain = text.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
        let font = bold ? NSFont.boldSystemFont(ofSize: NSFont.systemFontSize) : NSFont.systemFont(ofSize: NSFont.systemFontSize)
        return min(320, Double((plain as NSString).size(withAttributes: [.font: font]).width) + 4)
    }

    static func natural(_ table: MarkdownTable) -> [Double] {
        table.header.indices.map { i in
            let cells = table.rows.compactMap { i < $0.count ? $0[i] : nil }
            return ([measure(table.header[i], bold: true)] + cells.map { measure($0, bold: false) }).max() ?? 0
        }
    }
}

struct TableLine: View {
    let table: MarkdownTable
    let cells: [String]
    let widths: [CGFloat]
    let bold: Bool
    var more: String?

    var body: some View {
        HStack(alignment: .top, spacing: MarkdownTableView.spacing) {
            ForEach(Array(widths.enumerated()), id: \.offset) { i, w in
                Text(LocalizedStringKey(MarkdownText.escapingSingleTildes(TableLayout.breakable(i < cells.count ? cells[i] : ""))))
                    .fontWeight(bold ? .semibold : .regular)
                    .multilineTextAlignment(textAlign(i))
                    .frame(width: w, alignment: alignment(i))
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let more {
                Text(more).font(.caption).foregroundStyle(.secondary).frame(width: CGFloat(TablePreview.moreColumnWidth), alignment: .leading)
            }
        }
    }

    private func align(_ i: Int) -> MarkdownTable.Align { i < table.aligns.count ? table.aligns[i] : .leading }

    private func alignment(_ i: Int) -> Alignment {
        switch align(i) {
        case .leading: return .leading
        case .center: return .center
        case .trailing: return .trailing
        }
    }

    private func textAlign(_ i: Int) -> TextAlignment {
        switch align(i) {
        case .leading: return .leading
        case .center: return .center
        case .trailing: return .trailing
        }
    }
}
