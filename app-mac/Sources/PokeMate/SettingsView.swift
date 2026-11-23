import AppKit
import PokeMateCore
import SwiftUI

struct SettingsView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        // 다른 탭처럼 위에 제목 줄을 둔다. 없으면 스크롤한 내용이 창 왼쪽 위 닫기·최소화 버튼 밑까지 올라간다.
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("설정").font(.headline)
                Spacer()
            }
            .padding(12)
            Divider()
            form
        }
    }

    private var form: some View {
        Form {
            Section("이름") {
                TextField("나를 부를 호칭", text: $model.settings.ownerName, prompt: Text("예: 길동, 트레이너 (끝에 '님'이 붙어요)"))
            }
            PokedexSection(settings: $model.settings)
            Section {
                Picker("문맥이 넘으면 알림", selection: $model.settings.workSessionAlertPercent) {
                    Text("끄기").tag(0)
                    ForEach([50, 60, 70, 80, 90], id: \.self) { Text("\($0)%").tag($0) }
                }
                Picker("자동 처리: 이 시간 넘게 쉬면 새 세션", selection: $model.settings.sessionReuseMinutes) {
                    Text("늘 이어 쓰기").tag(0)
                    ForEach([30, 55, 120, 240], id: \.self) { Text("\($0)분").tag($0) }
                }
                Picker("자동 처리: 문맥이 이만큼이면 새 세션", selection: $model.settings.sessionReuseMaxTokens) {
                    Text("늘 이어 쓰기").tag(0)
                    ForEach([80_000, 120_000, 160_000, 200_000], id: \.self) { Text(TokenFormat.compact($0)).tag($0) }
                }
            } header: {
                Text("작업 세션")
            } footer: {
                Text("알림을 받으면 위쪽 '작업' 탭에서 새 세션으로 바꿀지 정하세요. 쉰 시간·문맥 크기 기준은 모든 자동 처리 역할 공통이에요(Jira 역할 처리·GitHub 리뷰). 세션은 작업 한 건(티켓 하나, PR 하나) 안에서만 이어 쓰고, 마지막 사용 뒤 정한 시간이 지났거나 문맥이 정한 크기를 넘으면 새 세션으로 시작해요(캐시가 1시간이라 기본 55분·120k). 새 세션은 그 건의 인계 메모와 도메인 노트·지식부터 읽어요.").font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Picker(selection: $model.settings.automationModel) {
                    ForEach([""] + ClaudeCommand.models, id: \.self) { Text(ModelLabels.models[$0] ?? $0).tag($0) }
                } label: {
                    HStack(spacing: 6) {
                        Text("모델")
                        HelpIcon(text: "알람, Jira·리뷰 자동 처리, 작업 세션, 역할별 세션, 레포 정하기에 쓰는 모델이에요.\n홈 채팅창 아래에서 고르는 모델은 메이트와 직접 대화할 때만 쓰고, 여기와 따로예요.\n바꾸면 다음 실행부터 적용돼요.")
                    }
                }
                Picker(selection: $model.settings.automationEffort) {
                    ForEach([""] + ClaudeCommand.efforts, id: \.self) { Text(ModelLabels.efforts[$0] ?? $0).tag($0) }
                } label: {
                    HStack(spacing: 6) {
                        Text("추론 수준")
                        HelpIcon(text: "자동 처리가 얼마나 깊이 생각할지예요. 높을수록 꼼꼼하지만 느리고 토큰을 더 써요.\n홈 채팅창의 추론 수준과 따로예요.")
                    }
                }
            } header: {
                Text("자동 처리 모델")
            }
            RulesSection(settings: $model.settings)
            Section("단축키") {
                HotkeyRecorder(hotkey: $model.settings.hotkey)
            }
            ClaudeAccountSection(model: model)
            Section("경로") {
                TextField("메이트 저장소", text: $model.settings.repoDir)
                TextField("claude", text: $model.settings.claudePath)
                TextField("uv", text: $model.settings.uvPath)
            }
            Section {
                Button("메이트 종료") { NSApp.terminate(nil) }
            }
        }
        .formStyle(.grouped)
    }
}

/// [변경] 을 누르고 원하는 키 조합을 누르면 바로 바뀐다. Esc 는 취소.
private struct HotkeyRecorder: View {
    @Binding var hotkey: Hotkey
    @State private var recording = false
    @State private var monitor: Any?
    @State private var message: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("채팅창 열기·닫기")
                Spacer()
                Text(recording ? "키 조합을 누르세요…" : hotkey.display)
                    .font(.body.monospaced())
                    .padding(.horizontal, 10).padding(.vertical, 4)
                    .background(recording ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.1),
                                in: RoundedRectangle(cornerRadius: 6))
                Button(recording ? "취소" : "변경") { recording ? stop() : start() }
                if hotkey != .default && !recording {
                    Button("기본값") { hotkey = .default; message = nil }
                }
            }
            if let message {
                Text(message).font(.caption).foregroundStyle(.secondary)
            }
        }
        .onDisappear { stop() }
    }

    private func start() {
        message = nil
        recording = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.keyCode == 53 { stop(); return nil }   // Esc
            let flags = event.modifierFlags
            var mods: UInt32 = 0
            if flags.contains(.command) { mods |= Hotkey.command }
            if flags.contains(.shift) { mods |= Hotkey.shift }
            if flags.contains(.option) { mods |= Hotkey.option }
            if flags.contains(.control) { mods |= Hotkey.control }
            let candidate = Hotkey(keyCode: UInt32(event.keyCode), modifiers: mods)
            if candidate.isValid {
                hotkey = candidate
                message = "\(candidate.display) 로 바꿨어요. 다른 앱과 겹치면 그 앱의 단축키가 먼저 동작할 수 있어요."
                stop()
            } else {
                message = "⌘, ⌃, ⌥ 중 하나 이상을 같이 눌러 주세요."
            }
            return nil
        }
    }

    private func stop() {
        recording = false
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }
}

/// 메이트가 쓰는 Claude 계정. 전용 로그인을 켜면 터미널 Claude Code 와 다른 계정(예: 팀 플랜)으로 쓸 수 있다.
private struct ClaudeAccountSection: View {
    @ObservedObject var model: AppModel
    @State private var account: ClaudeAccount?
    @State private var loading = false

    var body: some View {
        Section {
            HStack {
                Text("연결된 계정")
                Spacer()
                if loading {
                    ProgressView().controlSize(.small)
                } else {
                    Text(account?.display ?? "claude 를 찾지 못했어요").foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                }
            }
            HStack {
                Button(account?.loggedIn == true ? "다른 계정으로 로그인" : "로그인") {
                    Task {
                        if account?.loggedIn == true { await ClaudeLogin.logout(settings: model.settings) }
                        model.chat.forgetSession()
                        model.selected = .chat
                        model.chat.startLogin(retry: nil)
                    }
                }
                if account?.loggedIn == true {
                    Button("로그아웃") { Task { await ClaudeLogin.logout(settings: model.settings); await refresh() } }
                }
            }
        } header: {
            Text("메이트 Claude 계정")
        } footer: {
            Text("메이트는 터미널 Claude Code 와 따로 로그인해요. 로그인할 때 브라우저에서 쓸 조직(팀 플랜 등)을 고를 수 있어요. 사내 데이터를 어느 조직으로 보내도 되는지는 회사 기준을 따라 주세요. Microsoft 365 커넥터는 로그인한 계정의 claude.ai 설정에서 따로 연결해야 해요.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task { await refresh() }
        .onChange(of: model.chat.loginState) { if model.chat.loginState == .idle { Task { await refresh() } } }
    }

    private func refresh() async {
        loading = true
        account = await ClaudeLogin.account(settings: model.settings)
        loading = false
    }
}


/// 메이트 폴더의 md: root(CLAUDE.md, 고치기만) · rules(질문마다 모두 읽음) · skills(주제가 나올 때만 읽음).
private struct RulesSection: View {
    @Binding var settings: AppSettings
    @State private var files: [Rules.File] = []
    @State private var deleting: Rules.File?
    @State private var creating: Rules.Kind?
    @State private var newName = ""
    @State private var openingShared: Rules.File?
    @State private var localReady = AppPaths.localReady
    @AppStorage("rulesCollapsed") private var collapsed = ""
    @AppStorage("homeRulesOpen") private var homeRulesOpen = false
    @AppStorage("homeInstructionsOpen") private var homeOpen = false

    private static let rootHelp = "메이트 전체에 늘 적용하는 기본 지시예요 (말투, 답하는 방식 등).\nshared/CLAUDE.md(일반, Git 에 올라감) 다음에 local/CLAUDE.md(회사·개인, Git 제외)를 붙여 읽고, 겹치면 local 을 따라요. 지우면 기본 지시가 사라져서 고치기만 하고 지울 수는 없어요."
    private static let rulesHelp = "질문할 때마다 모든 파일을 통째로 읽어요. 길수록 질문마다 비용이 늘고 답이 느려지니, 늘 지켜야 할 것만 짧게 적고 파일당 200줄을 넘기지 마세요.\n채팅에서 \"기억해\" 하고 [기억하기]를 누르면 rules/memory.md 에 쌓여요."
    private static let promptsHelp = "알람 내용에서 불러 쓰는 프롬프트 파일이에요. 대화에서는 자동으로 읽지 않아요.\n알람 내용에 {{prompt:이름}} 을 쓰거나, 알람 수정 화면의 [프롬프트 불러오기]로 골라요."
    private static let skillsHelp = "주제별 안내서예요. 평소엔 한 줄 설명만 보내고, 질문에 그 주제의 키워드가 나오면 그때 본문을 읽어요.\n예: Jira 이야기가 나오면 skills/jira.md 를 읽어요. 키워드가 없어도 필요하면 메이트가 직접 찾아 읽어요.\n파일 맨 위에 description(한 줄 설명)과 keywords(쉼표로 구분)를 적어 주세요."

    var body: some View {
        Section {
            if !localReady {
                VStack(alignment: .leading, spacing: 6) {
                    Label("local 폴더가 없어요. 알람·규칙을 읽기만 하고 저장하지 않아요.", systemImage: "exclamationmark.triangle.fill")
                        .font(.caption).foregroundStyle(.orange)
                    Text("레포 위치: \(AppPaths.repoDir.path) (아래 \"메이트 저장소\" 설정). local/ 은 Git 에 올라가지 않는 회사·개인 설정 폴더예요. 다른 PC 에서 옮겨 오거나 예시로 만들어요.")
                        .font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Button("예시(local.example)로 local 만들기") {
                        AppPaths.makeLocalFromExample()
                        localReady = AppPaths.localReady
                        reload()
                    }
                    .font(.caption)
                }
            }
            ForEach([Rules.Kind.root, .rule, .skill, .prompt], id: \.self) { kind in
                let list = files.filter { $0.kind == kind }
                group(kind, count: list.count)
                if !isCollapsed(kind) {
                    if list.isEmpty && kind != .root { empty }
                    ForEach(list) { fileRow($0, deletable: kind != .root) }
                }
            }
        } header: {
            HStack {
                Text("규칙")
                Spacer()
                Button("폴더 열기") { NSWorkspace.shared.open(localReady ? AppPaths.localDir : AppPaths.repoDir) }.buttonStyle(.link).font(.caption)
                Button { reload() } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(.borderless).help("새로고침")
            }
        }
        .task { reload() }
        Section {
            Toggle(isOn: $settings.useHomeInstructions) {
                HStack(spacing: 6) {
                    if settings.useHomeInstructions {
                        Button { homeOpen.toggle() } label: {
                            Image(systemName: homeOpen ? "chevron.down" : "chevron.right").font(.caption2.weight(.semibold))
                                .foregroundStyle(.secondary).frame(width: 10)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help(homeOpen ? "접기" : "펼치기")
                    }
                    Text("내 Claude Code 개인 지침도 읽기")
                    HelpIcon(text: "켜면 내 홈의 ~/.claude/CLAUDE.md 와 그 rules/ 도 메이트가 읽어요.\n개발용 지침(코드 분석 형식, 커밋 규칙 등)이 비서 답에 섞이고 질문마다 읽는 양이 늘어서 기본은 꺼 둬요.\n메이트 지시는 메이트 폴더의 CLAUDE.md 에 적으세요.")
                }
            }
            if settings.useHomeInstructions && homeOpen {
                Toggle(isOn: $settings.homeInChat) {
                    HStack(spacing: 6) {
                        Text("대화에서 읽기")
                        HelpIcon(text: "홈 탭에서 메이트와 직접 채팅할 때 내 Claude Code 개인 지침(~/.claude/CLAUDE.md 와 rules 전부)을 읽어요.\n켜면 답하는 방식이 Claude Code 와 같아져요(코드 분석 형식, 커밋 규칙 등). 질문마다 읽는 양이 늘어요.\n끄면 메이트 폴더의 CLAUDE.md 와 rules 만 따라요.")
                    }
                }
                .padding(.leading, 16)
                Toggle(isOn: $settings.homeInAutomation) {
                    HStack(spacing: 6) {
                        Text("자동 처리(알람)에서 읽기")
                        HelpIcon(text: "알람, Jira·리뷰 자동 처리, 작업 세션이 실행될 때 개인 지침을 읽어요.\n아래에서 체크한 rules 파일만 읽어요. 체크하지 않은 규칙은 자동 처리에 적용되지 않아요.\n내 작업에만 맞는 규칙(설명 형식 등)은 빼 두세요.")
                    }
                }
                .padding(.leading, 16)
                if settings.homeInAutomation {
                    let files = ClaudeCommand.homeRules()
                    Button { homeRulesOpen.toggle() } label: {
                        HStack(spacing: 6) {
                            Image(systemName: homeRulesOpen ? "chevron.down" : "chevron.right").font(.caption2.weight(.semibold))
                                .foregroundStyle(.secondary).frame(width: 10)
                            Text("rules").font(.caption.monospaced().weight(.semibold)).foregroundStyle(.secondary)
                            Text("\(files.filter { settings.automationRules.contains($0) }.count)/\(files.count)").font(.caption2).foregroundStyle(.tertiary)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(homeRulesOpen ? "접기" : "펼치기")
                    .overlay(alignment: .trailing) {
                        HelpIcon(text: "~/.claude/rules 의 파일이에요. 새 파일은 체크 안 된 상태로 나타나요.").offset(x: 22)
                    }
                    .padding(.leading, 32)
                    if homeRulesOpen && files.isEmpty {
                        Text("~/.claude/rules 에 md 파일이 없어요").font(.caption).foregroundStyle(.secondary).padding(.leading, 48)
                    }
                    ForEach(homeRulesOpen ? files : [], id: \.self) { file in
                        Toggle(file, isOn: Binding(
                            get: { settings.automationRules.contains(file) },
                            set: { on in
                                settings.automationRules.removeAll { $0 == file }
                                if on { settings.automationRules.append(file) }
                            }))
                            .toggleStyle(PMCheckboxStyle())
                            .font(.caption)
                            .padding(.leading, 48)
                    }
                }
            }
            Toggle(isOn: $settings.shareHomeSkills) {
                HStack(spacing: 6) {
                    Text("내 Claude Code 스킬도 쓰기")
                    HelpIcon(text: "~/.claude/skills 의 스킬을 메이트도 써요. 새 스킬도 다음 실행부터 자동으로 보여요.\n메이트 폴더에 같은 주제가 있으면 메이트 쪽을 따라요.\n스킬이 안내하는 명령도 권한은 그대로라, 자동 처리에서는 허용한 명령만 실행돼요.")
                }
            }
            .onChange(of: settings.shareHomeSkills) { _, on in HomeSkills.sync(enabled: on) }
            Toggle(isOn: $settings.claudeTools) {
                HStack(spacing: 6) {
                    Text("Claude Code 도구 쓰기")
                    HelpIcon(text: "켜면 메이트 대화에서 Claude Code 기본 도구(터미널 명령, 파일 읽기·수정, 웹 가져오기 등)를 써요.\n명령 실행·파일 수정처럼 확인이 필요한 일은 대화창에 [허용/거부] 카드가 떠요. 누르기 전에는 실행하지 않아요.\n권한 파일의 deny 에 적은 것(sudo, 시스템 설정 변경 등)은 카드 없이 거부해요.\n알람 실행에는 쓰지 않아요.")
                    Spacer()
                    Button("권한 파일 열기") {
                        ClaudeCommand.installPermissions()
                        NSWorkspace.shared.open(ClaudeCommand.permissionsFile)
                    }
                    .controlSize(.small)
                }
            }
        } footer: {
            Text("고친 내용은 다음 질문부터 바로 적용돼요.").font(.caption).foregroundStyle(.secondary)
                .multilineTextAlignment(.trailing)
                .frame(maxWidth: .infinity, alignment: .trailing).fixedSize(horizontal: false, vertical: true)
        }
        .alert("파일을 삭제할까요?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
               presenting: deleting) { file in
            Button("삭제", role: .destructive) { try? Rules.delete(file); reload() }
            Button("취소", role: .cancel) {}
        } message: { file in
            Text("\(file.layer.rawValue)/\(file.name) 을(를) 휴지통으로 옮겨요.")
        }
        .alert("Git 에 올라가는 파일이에요", isPresented: Binding(get: { openingShared != nil }, set: { if !$0 { openingShared = nil } }),
               presenting: openingShared) { file in
            Button("열기") { NSWorkspace.shared.open(file.url) }
            Button("취소", role: .cancel) {}
        } message: { file in
            Text("shared/\(file.name) 은 레포에 함께 올라가요. 회사 이름·주소·사람 이름 같은 값은 넣지 말고, 그런 내용은 local 에 같은 이름 파일로 덮어쓰세요.")
        }
        .alert(creating.map(Self.newTitle) ?? "새 파일", isPresented: Binding(get: { creating != nil }, set: { if !$0 { creating = nil } })) {
            TextField("파일 이름. 예: wiki", text: $newName)
            Button("local 에 만들기") {
                if let kind = creating, let url = try? Rules.create(newName, kind: kind, layer: .local) { NSWorkspace.shared.open(url); reload() }
            }
            .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty || !localReady)
            Button("shared 에 만들기") {
                if let kind = creating, let url = try? Rules.create(newName, kind: kind, layer: .shared) { NSWorkspace.shared.open(url); reload() }
            }
            .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty)
            Button("취소", role: .cancel) {}
        } message: {
            Text("\(creating.map(Self.folder) ?? "rules")/ 에 md 파일을 만들고 열어요. local 은 Git 에 올라가지 않는 회사·개인용, shared 는 Git 에 올라가는 일반용이에요.")
        }
    }

    private static func folder(_ kind: Rules.Kind) -> String {
        switch kind {
        case .root: return "root"
        case .rule: return "rules"
        case .skill: return "skills"
        case .prompt: return "prompts"
        }
    }

    private static func newTitle(_ kind: Rules.Kind) -> String {
        switch kind {
        case .skill: return "새 스킬"
        case .prompt: return "새 프롬프트"
        default: return "새 규칙"
        }
    }

    private static func help(_ kind: Rules.Kind) -> String {
        switch kind {
        case .root: return rootHelp
        case .rule: return rulesHelp
        case .skill: return skillsHelp
        case .prompt: return promptsHelp
        }
    }

    private func isCollapsed(_ kind: Rules.Kind) -> Bool { collapsed.split(separator: ",").contains { $0 == kind.rawValue } }

    private func toggleCollapsed(_ kind: Rules.Kind) {
        var keys = collapsed.split(separator: ",").map(String.init)
        if let i = keys.firstIndex(of: kind.rawValue) { keys.remove(at: i) } else { keys.append(kind.rawValue) }
        collapsed = keys.joined(separator: ",")
    }

    private func group(_ kind: Rules.Kind, count: Int) -> some View {
        let open = !isCollapsed(kind)
        return HStack(spacing: 6) {
            Button { toggleCollapsed(kind) } label: {
                HStack(spacing: 6) {
                    Image(systemName: open ? "chevron.down" : "chevron.right").font(.caption2.weight(.semibold))
                        .foregroundStyle(.secondary).frame(width: 10)
                    Text(Self.folder(kind)).font(.caption.monospaced().weight(.semibold)).foregroundStyle(.secondary)
                    Text("\(count)").font(.caption2).foregroundStyle(.tertiary)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(open ? "접기" : "펼치기")
            HelpIcon(text: Self.help(kind))
            Spacer()
            if kind != .root {
                Button { newName = ""; creating = kind } label: { Image(systemName: "plus") }
                    .buttonStyle(.borderless).font(.caption).help(Self.newTitle(kind))
            }
        }
    }

    private var empty: some View {
        Text("없음").font(.caption).foregroundStyle(.tertiary).padding(.leading, 18)
    }

    private func fileRow(_ file: Rules.File, deletable: Bool) -> some View {
        HStack(spacing: 6) {
            Image(systemName: "doc.text").font(.caption).foregroundStyle(.secondary)
            Text(file.url.lastPathComponent).font(.callout.monospaced())
                .strikethrough(hidden.contains(file.id), color: .secondary)
            Text(file.layer.rawValue).font(.caption2.weight(.semibold))
                .foregroundStyle(file.layer == .local ? Color.blue : Color.secondary)
                .padding(.horizontal, 5).padding(.vertical, 1)
                .background((file.layer == .local ? Color.blue : Color.secondary).opacity(0.12), in: Capsule())
            if hidden.contains(file.id) {
                Text("local 이 덮어씀").font(.caption2).foregroundStyle(.secondary)
            }
            if file.kind == .rule {
                let lines = file.lineCount
                Text(lines > Rules.ruleLineLimit ? "\(lines)줄 · 200줄 넘음" : "\(lines)줄")
                    .font(.caption2).foregroundStyle(lines > Rules.ruleLineLimit ? Color.orange : Color.secondary)
            }
            Spacer()
            HStack(spacing: 10) {
                Button { if file.layer == .shared { openingShared = file } else { NSWorkspace.shared.open(file.url) } } label: { Image(systemName: "arrow.up.forward.square") }.help("열기")
                if deletable {
                    Button(role: .destructive) { deleting = file } label: { Image(systemName: "trash") }
                        .foregroundStyle(.red).help("삭제")
                }
            }
            .buttonStyle(.borderless)
            .font(.caption)
        }
        .padding(.leading, 18)
    }

    private var hidden: Set<String> { Rules.overridden(files) }

    private func reload() {
        localReady = AppPaths.localReady
        files = Rules.files()
    }
}

private struct PokedexSection: View {
    @Binding var settings: AppSettings
    @ObservedObject var usage = UsageStore.shared

    var body: some View {
        Section {
            Picker("포켓몬 범위", selection: $settings.pokedexRange) {
                ForEach(PokedexRange.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            if let progress = usage.pokedexProgress {
                HStack(spacing: 8) {
                    ProgressView(value: Double(progress.done), total: Double(max(1, progress.total))).frame(width: 120)
                    Text("\(usage.range.label) 받는 중 · \(progress.text)").font(.caption).foregroundStyle(.secondary).monospacedDigit()
                }
            } else if let error = usage.pokedexError {
                HStack {
                    Text(error).font(.caption).foregroundStyle(.orange)
                    Spacer()
                    Button("다시 받기") { usage.loadPokedex() }
                }
            }
        } header: {
            Text("포켓몬")
        } footer: {
            Text("새 범위는 다음 알부터 적용돼요. 키우는 포켓몬과 도감 기록은 그대로예요. 한 번 받은 도감은 다시 받지 않아요.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct PMCheckboxStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button { configuration.isOn.toggle() } label: {
            HStack(spacing: 6) {
                ZStack {
                    RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                        .fill(configuration.isOn ? Color.accentColor : Color(nsColor: .controlBackgroundColor))
                    RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                        .strokeBorder(configuration.isOn ? Color.accentColor : Color.gray.opacity(0.45), lineWidth: 1)
                    if configuration.isOn {
                        Image(systemName: "checkmark").font(.system(size: 9, weight: .bold)).foregroundStyle(.white)
                    }
                }
                .frame(width: 14, height: 14)
                configuration.label
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
