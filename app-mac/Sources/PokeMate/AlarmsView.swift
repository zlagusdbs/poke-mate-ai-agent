import PokeMateCore
import SwiftUI

struct AlarmsView: View {
    @ObservedObject var model: AppModel
    @State private var editing: Alarm?
    /// 펼친 그룹. 앱을 다시 켜도 기억한다 (AppStorage 는 Set 을 못 담아서 줄바꿈으로 이은 문자열).
    @AppStorage("alarmExpandedGroups") private var expandedGroups = ""
    @AppStorage("alarmUngroupedCollapsed") private var ungroupedCollapsed = false
    /// 삭제 확인
    @State private var deletingAlarm: Alarm?
    @State private var deletingGroup: String?
    /// 그룹 만들기·이름 바꾸기 (nil 이면 새 그룹)
    @State private var naming: GroupNaming?
    @State private var groupName = ""
    /// 끌고 있는 알림이 올라가 있는 그룹 ("" 은 그룹 없음)
    @State private var dropTarget: String?
    /// 상세를 펼친 알림
    @State private var openRows: Set<UUID> = []
    @State private var hovered: UUID?
    @State private var insertBefore: UUID?
    @ObservedObject private var usage = UsageStore.shared

    private struct GroupNaming: Identifiable {
        let old: String?
        var id: String { old ?? "" }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("알람").font(.headline)
                Spacer()
                Button {
                    groupName = ""
                    naming = GroupNaming(old: nil)
                } label: { Label("그룹", systemImage: "folder.badge.plus") }
                    .buttonStyle(.pill)
                    .help("새 그룹 만들기. 알림을 그룹으로 끌어다 놓으면 옮겨져요")
                Button {
                    editing = Alarm(title: "", prompt: "", hour: 9, minute: 0, repeatRule: .weekdays)
                } label: { Label("추가", systemImage: "plus") }
                    .buttonStyle(.pill)
            }
            .padding(.horizontal, Theme.gutter)
            .padding(.bottom, 4)

            ScrollView {
                VStack(spacing: 10) {
                    summaryCard
                    if !(model.alarms.isEmpty && model.alarmGroups.isEmpty) {
                        let split = AlarmGroup.make(model.alarms, groups: model.alarmGroups)
                        ForEach(split.groups) { group in
                            groupView(group).pmCard(12)
                        }
                        ungroupedView(split.ungrouped)
                    }
                }
                .padding(.horizontal, Theme.gutter)
                .padding(.bottom, 12)
            }
        }
        .task { if model.systems.isEmpty { await model.refreshSystems() } }
        .sheet(item: $editing) { alarm in
            AlarmEditor(alarm: alarm, systems: model.systems, groups: model.alarmGroups) { model.save($0) }
        }
        .alert("알림을 삭제할까요?", isPresented: Binding(get: { deletingAlarm != nil }, set: { if !$0 { deletingAlarm = nil } }),
               presenting: deletingAlarm) { alarm in
            Button("삭제", role: .destructive) { model.alarms.removeAll { $0.id == alarm.id } }
            Button("취소", role: .cancel) {}
        } message: { alarm in
            Text("\"\(alarm.title.isEmpty ? "(제목 없음)" : alarm.title)\" \(alarm.trigger == nil ? alarm.timeText + " " : "")알림을 지워요. 되돌릴 수 없어요.")
        }
        .alert("그룹을 삭제할까요?", isPresented: Binding(get: { deletingGroup != nil }, set: { if !$0 { deletingGroup = nil } }),
               presenting: deletingGroup) { name in
            Button("그룹 삭제", role: .destructive) { model.deleteGroup(name) }
            Button("취소", role: .cancel) {}
        } message: { name in
            Text("\"\(name)\" 그룹만 지워요. 안에 있던 알림은 지우지 않고 \"그룹 없음\"으로 옮겨요.")
        }
        .alert(naming?.old == nil ? "새 그룹" : "그룹 이름 바꾸기",
               isPresented: Binding(get: { naming != nil }, set: { if !$0 { naming = nil } }), presenting: naming) { target in
            TextField("그룹 이름", text: $groupName)
            Button("저장") {
                if let old = target.old { model.renameGroup(old, to: groupName) } else { model.addGroup(groupName) }
            }
            .disabled(groupName.trimmingCharacters(in: .whitespaces).isEmpty)
            Button("취소", role: .cancel) {}
        }
    }
}

extension AlarmsView {
    private static let clock = Date.VerbatimFormatStyle(format: "\(hour: .twoDigits(clock: .twentyFourHour, hourCycle: .zeroBased)):\(minute: .twoDigits)",
                                                         timeZone: .current, calendar: .current)
    private static let monthDay = Date.VerbatimFormatStyle(format: "\(month: .defaultDigits)/\(day: .defaultDigits)",
                                                            timeZone: .current, calendar: .current)

    private var summaryCard: some View {
        let enabled = model.alarms.filter(\.enabled)
        let today = AlarmRunLog.todayTotal(model.alarmLog, ids: Set(model.alarms.map(\.id)), now: Date())
        let last = model.alarmLog.filter { id, _ in model.alarms.contains { $0.id == id } }.max { $0.value.lastAt < $1.value.lastAt }
        return HStack(spacing: 12) {
            Group {
                if let current = usage.mate.current, let species = usage.species(current.species) {
                    MateSprite(species: species, tokens: current.tokens, final: current.form, size: 40)
                } else {
                    PixelSprite(grid: MateSpecies.eggGrid, palette: ["k": 0x3A3A4A, "f": 0xF3EADB, "s": 0x9A9AA5], size: 40)
                }
            }
            .frame(width: 52, height: 52)
            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            VStack(alignment: .leading, spacing: 3) {
                if model.alarms.isEmpty {
                    Text("알람이 없어요. [+ 추가]로 만들어 보세요").font(.callout)
                } else {
                    Text("켜진 알람 \(enabled.count)개 · 오늘 실행 \(today)번").font(.callout.weight(.semibold))
                    if let last, let alarm = model.alarms.first(where: { $0.id == last.key }) {
                        Text("마지막: \(title(alarm)) \(last.value.lastAt.formatted(Self.clock)) \(last.value.result)")
                            .font(.caption).foregroundStyle(last.value.failed ? Color.red : Color.secondary).lineLimit(1)
                    } else {
                        Text("아직 실행한 알람이 없어요").font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .pmCard(12)
    }

    @ViewBuilder
    private func groupView(_ group: AlarmGroup) -> some View {
        let expanded = contains(expandedGroups, group.name)
        VStack(alignment: .leading, spacing: 0) {
            Button { expandedGroups = toggled(expandedGroups, group.name) } label: {
                header(icon: "folder", name: group.name, count: group.alarms.count, expanded: expanded,
                       running: group.alarms.contains { model.alarmRuns[$0.id] == .running })
            }
            .buttonStyle(.plain)
            .help(expanded ? "접기 · 오른쪽 클릭: 이름 바꾸기, 삭제" : "펼치기 · 오른쪽 클릭: 이름 바꾸기, 삭제")
            .contextMenu {
                Button("모두 켜기") { for alarm in group.alarms { setEnabled(alarm.id, true) } }
                Button("모두 끄기") { for alarm in group.alarms { setEnabled(alarm.id, false) } }
                Divider()
                Button("이름 바꾸기") { groupName = group.name; naming = GroupNaming(old: group.name) }
                Button("그룹 삭제", role: .destructive) { deletingGroup = group.name }
            }
            if expanded {
                if group.alarms.isEmpty {
                    Divider().padding(.vertical, 6)
                    Text("비어 있어요. 알림을 끌어다 놓으세요.").font(.caption).foregroundStyle(.secondary)
                }
                ForEach(group.alarms) { alarm in
                    Divider().padding(.vertical, 2)
                    row(alarm, group: group.name)
                }
            }
        }
        .dropTarget(isOn: dropTarget == group.name)
        .dropDestination(for: String.self) { ids, _ in drop(ids, into: group.name) } isTargeted: { on in
            dropTarget = on ? group.name : (dropTarget == group.name ? nil : dropTarget)
        }
    }

    @ViewBuilder
    private func ungroupedView(_ alarms: [Alarm]) -> some View {
        if !alarms.isEmpty || !model.alarmGroups.isEmpty {
            VStack(alignment: .leading, spacing: 0) {
                Button { ungroupedCollapsed.toggle() } label: {
                    header(icon: "tray", name: "그룹 없음", count: alarms.count, expanded: !ungroupedCollapsed,
                           running: alarms.contains { model.alarmRuns[$0.id] == .running })
                }
                .buttonStyle(.plain)
                if !ungroupedCollapsed {
                    ForEach(alarms) { alarm in
                        Divider().padding(.vertical, 2)
                        row(alarm, group: nil)
                    }
                }
            }
            .dropTarget(isOn: dropTarget == "")
            .dropDestination(for: String.self) { ids, _ in drop(ids, into: nil) } isTargeted: { on in
                dropTarget = on ? "" : (dropTarget == "" ? nil : dropTarget)
            }
            .pmCard(12)
        }
    }

    private func header(icon: String, name: String, count: Int, expanded: Bool, running: Bool) -> some View {
        HStack(spacing: 6) {
            Image(systemName: expanded ? "chevron.down" : "chevron.right").font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary).frame(width: 10)
            Image(systemName: icon).font(.caption).foregroundStyle(Color.orange)
            Text(name).font(.callout.weight(.semibold)).lineLimit(1)
            Text("\(count)").font(.caption).foregroundStyle(.secondary)
            Spacer()
            if running { ProgressView().controlSize(.mini).help("실행 중인 알림이 있어요") }
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
    }

    private func title(_ alarm: Alarm) -> String { alarm.title.isEmpty ? "(제목 없음)" : alarm.title }

    private func caption(_ alarm: Alarm) -> (text: String, failed: Bool) {
        if model.alarmRuns[alarm.id] == .running { return ("실행 중…", false) }
        let record = model.alarmLog[alarm.id]
        let when: String
        if let trigger = alarm.trigger {
            let event = TriggerEvent.find(trigger.event)?.name ?? trigger.event
            let last = record.map { r in
                let today = Calendar.current.isDateInToday(r.lastAt)
                let time = r.lastAt.formatted(Self.clock)
                return " · 마지막 \(today ? time : r.lastAt.formatted(Self.monthDay) + " " + time) \(r.result)"
            } ?? ""
            when = "\(event) · \(trigger.interval)분마다" + last
        } else {
            let last: String
            if let r = record {
                last = " · 마지막 \(r.lastAt.formatted(Self.monthDay)) \(r.result)"
            } else if let day = alarm.lastRunDay {
                last = " · 마지막 \(day)"
            } else {
                last = ""
            }
            let shell = alarm.shellCommand.map { " · $ " + Alarm.displayCommand($0) } ?? ""
            when = "\(alarm.repeatRule.description) \(alarm.timeText)" + shell + last
        }
        let tokens = record?.usage.map { " · " + $0.shortText } ?? ""
        return (when + tokens, record?.failed ?? false)
    }

    private func row(_ alarm: Alarm, group: String?) -> some View {
        let open = openRows.contains(alarm.id)
        let showTools = open || hovered == alarm.id
        let line = caption(alarm)
        return VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .top, spacing: 8) {
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 5) {
                        Image(systemName: alarm.trigger != nil ? "bolt.fill" : alarm.shellCommand != nil ? "terminal.fill" : "clock.fill")
                            .font(.caption2).foregroundStyle(Color.orange)
                            .help(alarm.trigger != nil ? "트리거" : alarm.shellCommand != nil ? "명령 실행" : "정해진 시각")
                        Text(title(alarm)).font(.callout).lineLimit(1)
                    }
                    Text((alarm.enabled ? "" : "꺼짐 · ") + line.text).font(.caption).foregroundStyle(line.failed ? Color.red : Color.secondary)
                        .lineLimit(1).truncationMode(.middle).monospacedDigit()
                }
                Spacer(minLength: 4)
                HStack(spacing: 10) {
                    if showTools {
                        Button { editing = alarm } label: { Image(systemName: "pencil") }.help("수정")
                        Button(role: .destructive) { deletingAlarm = alarm } label: { Image(systemName: "trash") }
                            .foregroundStyle(.red).help("삭제")
                    }
                    if model.alarmRuns[alarm.id] == .running {
                        ProgressView().controlSize(.mini)
                            .help(alarm.trigger == nil ? "실행 중이에요. 끝나면 알림 창에 결과가 떠요" : "새로 생긴 게 있는지 보고 있어요")
                    } else {
                        Button { model.runAlarmNow(alarm) } label: { Image(systemName: "play.fill") }
                            .help(alarm.trigger == nil ? "지금 실행" : "지금 확인")
                    }
                }
                .buttonStyle(.borderless)
                .font(.caption)
                .padding(.top, 2)
                Button {
                    if open { openRows.remove(alarm.id) } else { openRows.insert(alarm.id) }
                } label: {
                    Image(systemName: open ? "chevron.up" : "chevron.down").font(.caption2.weight(.semibold))
                        .foregroundStyle(.secondary).frame(width: 18, height: 16).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(open ? "접기" : "자세히")
                .padding(.top, 2)
            }
            if case .done(let message, let failed) = model.alarmRuns[alarm.id] {
                Label(message, systemImage: failed ? "xmark.circle.fill" : "checkmark.circle.fill")
                    .font(.caption2).foregroundStyle(failed ? Color.red : Color.green)
                    .padding(.leading, 4)
            }
            if open {
                details(alarm).padding(.leading, 4)
            }
        }
        .opacity(alarm.enabled ? 1 : 0.5)
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .onHover { inside in
            if inside { hovered = alarm.id } else if hovered == alarm.id { hovered = nil }
        }
        .overlay(alignment: .top) {
            if insertBefore == alarm.id {
                Capsule().fill(Color.orange).frame(height: 2).offset(y: -4)
            }
        }
        .dropDestination(for: String.self) { ids, _ in drop(ids, into: group, before: alarm.id) } isTargeted: { on in
            insertBefore = on ? alarm.id : (insertBefore == alarm.id ? nil : insertBefore)
        }
        .contextMenu {
            Button("위로 옮기기") { model.shift(alarm.id, up: true) }.disabled(!model.canShift(alarm.id, up: true))
            Button("아래로 옮기기") { model.shift(alarm.id, up: false) }.disabled(!model.canShift(alarm.id, up: false))
        }
        .draggable(alarm.id.uuidString) {
            Label(title(alarm), systemImage: "bell")
                .padding(6).background(.background, in: RoundedRectangle(cornerRadius: 6))
        }
    }

    /// 펼쳤을 때 보이는 상세: 언제, 무엇을, 받을 곳, 마지막 실행. (실행·수정·삭제 버튼은 제목 줄에 있다)
    private func details(_ alarm: Alarm) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            if let trigger = alarm.trigger {
                HStack(spacing: 4) {
                    Text("트리거 · \(SystemNames.name(trigger.system)) · \(TriggerEvent.find(trigger.event)?.name ?? trigger.event) · \(trigger.interval)분마다")
                    TriggerHelpIcon()
                }
            } else {
                Text("\(alarm.timeText) · \(alarm.repeatRule.description)").monospacedDigit()
            }
            Text(alarm.shellCommand.map { "명령 실행: " + $0 } ?? (alarm.prompt == Alarm.dailyReportPrompt ? "내 미완료 Jira 를 정리해서 보고"
                 : alarm.prompt.isEmpty ? (alarm.trigger == nil ? "시각에 알림만" : "생기면 바로 알림") : alarm.prompt))
                .foregroundStyle(.secondary).lineLimit(3)
            if let output = model.alarmLog[alarm.id]?.output, !output.isEmpty {
                Text(output).font(.caption2.monospaced()).foregroundStyle(.tertiary).lineLimit(5).textSelection(.enabled)
            }
            HStack(spacing: 8) {
                DeliveryBadge(on: alarm.toMac, symbol: "macwindow", text: "Mac 알림")
                DeliveryBadge(on: alarm.toPanel, symbol: "rectangle.bottomhalf.inset.filled", text: "미리 알림 창")
                if alarm.trigger != nil && alarm.canWrite {
                    DeliveryBadge(on: true, symbol: "pencil", text: "쓰기 허용")
                }
                if let last = alarm.lastRunDay {
                    Text("마지막 실행 \(last)").foregroundStyle(.tertiary)
                }
            }
            .font(.caption2)
        }
        .font(.caption)
    }

    private func drop(_ ids: [String], into group: String?, before target: UUID? = nil) -> Bool {
        let uuids = ids.compactMap(UUID.init(uuidString:))
        for id in uuids { model.reorder(id, to: group, before: target) }
        dropTarget = nil
        insertBefore = nil
        return !uuids.isEmpty
    }

    private func setEnabled(_ id: UUID, _ on: Bool) {
        if let i = model.alarms.firstIndex(where: { $0.id == id }) { model.alarms[i].enabled = on }
    }

    private func contains(_ stored: String, _ key: String) -> Bool {
        stored.split(separator: "\n").contains { $0 == key }
    }

    private func toggled(_ stored: String, _ key: String) -> String {
        var keys = stored.split(separator: "\n").map(String.init)
        if let i = keys.firstIndex(of: key) { keys.remove(at: i) } else { keys.append(key) }
        return keys.joined(separator: "\n")
    }
}

private extension View {
    /// 끌어다 놓을 곳 위에 올라가 있으면 파란 테두리로 표시한다.
    func dropTarget(isOn: Bool) -> some View {
        background(RoundedRectangle(cornerRadius: 6).fill(isOn ? Color.accentColor.opacity(0.12) : .clear))
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(isOn ? Color.accentColor : .clear, lineWidth: 1.5))
    }
}

/// 알림 추가·수정: 언제(시각·반복), 무엇을(제목·메이트에게 시킬 내용).
struct AlarmEditor: View {
    enum RepeatKind: String, CaseIterable, Identifiable {
        case daily = "매일", weekdays = "평일", days = "요일 선택", once = "한 번"
        var id: String { rawValue }
    }

    enum Mode: String, CaseIterable, Identifiable {
        case time = "정해진 시각", trigger = "트리거"
        var id: String { rawValue }
    }

    enum Action: String, CaseIterable, Identifiable {
        case mate = "메이트에게 시키기", command = "명령 실행"
        var id: String { rawValue }
    }

    @Environment(\.dismiss) private var dismiss
    @State private var alarm: Alarm
    @State private var mode: Mode
    @State private var action: Action
    @State private var command: String
    @State private var triggerSystem: String
    @State private var triggerEvent: String
    @State private var triggerInterval: Int
    let systems: [SystemStatus]
    @State private var time: Date
    @State private var kind: RepeatKind
    @State private var days: Set<Int>
    @State private var onceDate: Date
    let groups: [String]
    let onSave: (Alarm) -> Void

    init(alarm: Alarm, systems: [SystemStatus], groups: [String] = [], onSave: @escaping (Alarm) -> Void) {
        self.onSave = onSave
        self.groups = groups
        self.systems = systems
        _alarm = State(initialValue: alarm)
        _mode = State(initialValue: alarm.trigger == nil ? .time : .trigger)
        _action = State(initialValue: alarm.shellCommand == nil ? .mate : .command)
        _command = State(initialValue: alarm.command ?? "")
        let system = alarm.trigger?.system ?? TriggerEvent.systems[0]
        _triggerSystem = State(initialValue: system)
        _triggerEvent = State(initialValue: alarm.trigger?.event ?? TriggerEvent.events(for: system)[0].id)
        _triggerInterval = State(initialValue: alarm.trigger?.interval ?? Trigger.defaultInterval)
        let cal = Calendar.current
        _time = State(initialValue: cal.date(bySettingHour: alarm.hour, minute: alarm.minute, second: 0, of: Date()) ?? Date())
        switch alarm.repeatRule {
        case .daily: _kind = State(initialValue: .daily); _days = State(initialValue: []); _onceDate = State(initialValue: Date())
        case .weekdays: _kind = State(initialValue: .weekdays); _days = State(initialValue: []); _onceDate = State(initialValue: Date())
        case .days(let d): _kind = State(initialValue: .days); _days = State(initialValue: Set(d)); _onceDate = State(initialValue: Date())
        case .once(let day):
            _kind = State(initialValue: .once); _days = State(initialValue: [])
            let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"
            _onceDate = State(initialValue: f.date(from: day) ?? Date())
        }
    }

    private var isDailyReport: Bool { alarm.prompt == Alarm.dailyReportPrompt }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(alarm.title.isEmpty && alarm.prompt.isEmpty ? "알림 추가" : "알림 수정").font(.headline)

            Form {
                Section("언제") {
                    Toggle("사용", isOn: $alarm.enabled).toggleStyle(.switch).tint(.orange)
                    HStack(spacing: 6) {
                        Picker("방식", selection: $mode) {
                            ForEach(Mode.allCases) { Text($0.rawValue).tag($0) }
                        }
                        .pickerStyle(.segmented)
                        TriggerHelpIcon()
                    }
                    if mode == .trigger {
                        Picker("시스템", selection: $triggerSystem) {
                            ForEach(TriggerEvent.systems, id: \.self) { system in
                                Text(systemLabel(system)).tag(system)
                            }
                        }
                        .onChange(of: triggerSystem) {
                            triggerEvent = TriggerEvent.events(for: triggerSystem)[0].id
                            autoTitle()
                        }
                        Picker("이벤트", selection: $triggerEvent) {
                            ForEach(TriggerEvent.events(for: triggerSystem)) { Text($0.name).tag($0.id) }
                        }
                        .onChange(of: triggerEvent) { autoTitle() }
                        Picker("확인 주기", selection: $triggerInterval) {
                            ForEach(Trigger.intervals, id: \.self) { Text("\($0)분마다").tag($0) }
                        }
                        if systems.first(where: { $0.system == triggerSystem })?.status != .connected {
                            Text("\(SystemNames.name(triggerSystem)) 이(가) 연결돼 있지 않아요. 연결 메뉴에서 먼저 연결해 주세요.")
                                .font(.caption).foregroundStyle(.orange)
                        }
                    }
                }
                .grayCard()
                if mode == .time {
                Section("시각과 반복") {
                    DatePicker("시각", selection: $time, displayedComponents: .hourAndMinute)
                    Picker("반복", selection: $kind) {
                        ForEach(RepeatKind.allCases) { Text($0.rawValue).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    if kind == .days {
                        HStack(spacing: 4) {
                            ForEach(Array(zip(1...7, ["일", "월", "화", "수", "목", "금", "토"])), id: \.0) { day, name in
                                Toggle(name, isOn: Binding(
                                    get: { days.contains(day) },
                                    set: { on in if on { days.insert(day) } else { days.remove(day) } }
                                ))
                                .toggleStyle(.button)
                            }
                        }
                    }
                    if kind == .once {
                        DatePicker("날짜", selection: $onceDate, displayedComponents: .date)
                    }
                }
                .grayCard()
                }
                Section("받을 곳") {
                    HStack(spacing: 16) {
                        Toggle("Mac 알림", isOn: Binding(get: { alarm.toMac }, set: { alarm.notifyMac = $0 }))
                            .help("화면 오른쪽 위에 잠깐 떴다가 알림 센터에 쌓이는 macOS 알림")
                        Toggle("미리 알림 창", isOn: Binding(get: { alarm.toPanel }, set: { alarm.notifyPanel = $0 }))
                            .help("화면 오른쪽 아래에 [확인]을 누를 때까지 남아 있는 메이트 카드")
                    }
                    .toggleStyle(PMCheckboxStyle())
                    if !alarm.toMac && !alarm.toPanel {
                        Label("둘 다 끄면 알림이 오지 않아요\(mode == .time ? " (결과는 채팅창에만 남아요)" : "")",
                              systemImage: "exclamationmark.triangle.fill")
                            .font(.caption).foregroundStyle(.orange)
                    }
                }
                .grayCard()
                if mode == .trigger {
                    Section("쓰기") {
                        Toggle("쓰기 허용", isOn: Binding(get: { alarm.canWrite }, set: { alarm.allowWrite = $0 }))
                            .toggleStyle(PMCheckboxStyle())
                            .help("켜면 이 알림은 write-tools.json 에 적은 명령만 추가로 실행할 수 있어요")
                        if alarm.canWrite {
                            Picker("제한 시간", selection: Binding(get: { alarm.timeoutMinutes ?? 30 }, set: { alarm.timeoutMinutes = $0 == 30 ? nil : $0 })) {
                                ForEach(Alarm.timeoutChoices, id: \.self) { Text("\($0)분").tag($0) }
                            }
                            .help("쓰기 알람 한 번이 이 시간을 넘기면 멈춰요")
                            Toggle("레포별 작업 세션 이어 쓰기", isOn: Binding(get: { alarm.useWorkSessions ?? false }, set: { alarm.useWorkSessions = $0 ? true : nil }))
                                .toggleStyle(PMCheckboxStyle())
                                .help("같은 레포의 티켓은 같은 Claude 세션을 이어 써서, 이미 읽은 문서를 다시 읽지 않아요. 위쪽 \"작업\" 탭에서 기록을 보고 말을 걸 수 있어요")
                            if alarm.useWorkSessions == true {
                                Toggle("역할별 세션으로 나눠 처리 (PM·개발·테스터·리뷰어)", isOn: Binding(get: { alarm.useRolePipeline ?? false }, set: { alarm.useRolePipeline = $0 ? true : nil }))
                                    .toggleStyle(PMCheckboxStyle())
                                    .help("Jira 자동 처리용. 도메인마다 역할 세션 4개가 핑퐁으로 처리하고, push 는 PokeMate 가 직접 해요. 프롬프트는 jira-common·jira-pm·jira-dev·jira-tester·jira-reviewer, 권한은 pipeline-tools.json")
                            }
                            Text(ClaudeCommand.loadWriteTools().isEmpty
                                 ? "write-tools.json 이 비어 있어서 조회만 해요"
                                 : "허용: " + ClaudeCommand.loadWriteTools().joined(separator: ", "))
                                .font(.caption).foregroundStyle(.secondary).lineLimit(4)
                        }
                    }
                    .grayCard()
                }
                Section("무엇을") {
                    if !groups.isEmpty {
                        Picker("그룹", selection: $alarm.group) {
                            Text("그룹 없음").tag(String?.none)
                            ForEach(groups, id: \.self) { Text($0).tag(String?.some($0)) }
                        }
                    }
                    VStack(alignment: .leading, spacing: 4) {
                        Text("제목").font(.caption).foregroundStyle(.secondary)
                        TextField("", text: $alarm.title, prompt: Text("알림에 보일 이름. 예: 근무일정 확인"))
                            .whiteField()
                            .labelsHidden()
                    }
                    if mode == .time {
                        Picker("할 일", selection: $action) {
                            ForEach(Action.allCases) { Text($0.rawValue).tag($0) }
                        }
                        .pickerStyle(.segmented)
                    }
                    if mode == .time && action == .command {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("명령").font(.caption).foregroundStyle(.secondary)
                            TextField("", text: $command, prompt: Text("예: python3 ~/scripts/check.py"))
                                .font(.callout.monospaced())
                                .whiteField()
                                .labelsHidden()
                            Text("Claude 를 부르지 않고 로그인 셸(zsh -lc)로 홈 폴더에서 실행해요. 5분이 지나면 멈춰요. 종료 코드 0 이면 성공, 출력 마지막 줄을 알림에 보내요.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    } else if isDailyReport {
                        Text("내 미완료 Jira 를 정리해서 보고 (기본 보고 문구)").foregroundStyle(.secondary)
                        Button("직접 쓰기로 바꾸기") { alarm.prompt = "" }.buttonStyle(.link)
                    } else {
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text("내용").font(.caption).foregroundStyle(.secondary)
                                Spacer()
                                Menu("프롬프트 불러오기") {
                                    let names = Rules.promptNames()
                                    if names.isEmpty {
                                        Text("설정 → 규칙 → prompts 에 파일이 없어요")
                                    }
                                    ForEach(names, id: \.self) { name in
                                        Button(name) { alarm.prompt = "{{prompt:\(name)}}" }
                                    }
                                }
                                .menuStyle(.borderlessButton)
                                .fixedSize()
                                .font(.caption)
                            }
                            TextField("", text: $alarm.prompt,
                                      prompt: Text(mode == .trigger ? "비워두면 알림만. 예: 이 PR 변경 내용 요약해줘"
                                                                    : "비워두면 알림만. 예: 이번 주 내 근무일정 알려줘"),
                                      axis: .vertical)
                                .whiteField()
                                .labelsHidden()
                                .lineLimit(3...6)
                        }
                    }
                }
                .grayCard()
            }
            .formStyle(.grouped)

            HStack {
                Spacer()
                Button("취소") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("저장") { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(alarm.title.trimmingCharacters(in: .whitespaces).isEmpty
                              || (mode == .time && kind == .days && days.isEmpty)
                              || (mode == .time && action == .command && command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty))
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear { if alarm.title.isEmpty && mode == .trigger { autoTitle() } }
        .onChange(of: mode) { if mode == .trigger && alarm.title.isEmpty { autoTitle() } }
    }

    private func systemLabel(_ system: String) -> String {
        let connected = systems.first { $0.system == system }?.status == .connected
        return SystemNames.name(system) + (connected ? "" : " (연결 안 됨)")
    }

    /// 제목이 비었거나 예전에 자동으로 채운 이름이면 고른 이벤트로 다시 채운다.
    private func autoTitle() {
        let autoNames = TriggerEvent.all.map { "\(SystemNames.name($0.system)) · \($0.name)" }
        guard alarm.title.isEmpty || autoNames.contains(alarm.title) || alarm.title == "GitHub · 리뷰 요청" else { return }
        if let event = TriggerEvent.find(triggerEvent) {
            alarm.title = "\(SystemNames.name(event.system)) · \(event.name)"
        }
    }

    private func save() {
        if mode == .trigger {
            alarm.trigger = Trigger(system: triggerSystem, event: triggerEvent, intervalMinutes: triggerInterval)
            alarm.command = nil
            if alarm.prompt == Alarm.dailyReportPrompt { alarm.prompt = "" }
            onSave(alarm)
            dismiss()
            return
        }
        alarm.trigger = nil
        alarm.allowWrite = nil
        let trimmed = command.trimmingCharacters(in: .whitespacesAndNewlines)
        alarm.command = action == .command && !trimmed.isEmpty ? trimmed : nil
        let cal = Calendar.current
        alarm.hour = cal.component(.hour, from: time)
        alarm.minute = cal.component(.minute, from: time)
        switch kind {
        case .daily: alarm.repeatRule = .daily
        case .weekdays: alarm.repeatRule = .weekdays
        case .days: alarm.repeatRule = .days(days.sorted())
        case .once: alarm.repeatRule = .once(ReportSchedule.dayString(onceDate, calendar: cal)); alarm.enabled = true
        }
        // 시각이나 반복을 바꾸면 기록을 새로 잡는다 (오늘 시각이 이미 지났으면 다음부터).
        alarm.resetSchedule(now: Date(), calendar: cal)
        onSave(alarm)
        dismiss()
    }
}

enum SystemNames {
    static func name(_ id: String) -> String {
        LocalNames.current.name(id) ?? ["github": "GitHub", "deploy": "Deploy", "jira": "Jira", "wiki": "Wiki", "hr": "HR"][id] ?? id
    }
}

/// 트리거 설명 아이콘.
struct TriggerHelpIcon: View {
    var body: some View {
        HelpIcon(text: "켜져 있는 동안 정한 주기(기본 5분)마다 확인해서, 새로 생긴 것만 알려 드려요.\n처음 켤 때 이미 있던 건 알리지 않아요. 바로 보고 싶으면 ▶(지금 확인)을 누르세요.\n내용을 비워 두면 바로 알리고, 내용을 적으면 메이트가 그 일을 한 뒤 결과를 알려요(조금 느림).")
    }
}

/// ? 아이콘. 마우스를 올리거나 누르면 말풍선으로 설명한다.
/// (시트·패널 안에서는 macOS 기본 툴팁(.help)이 잘 안 떠서 말풍선을 쓴다)
struct HelpIcon: View {
    let text: String
    @State private var showing = false

    var body: some View {
        Image(systemName: "questionmark.circle")
            .font(.caption)
            .foregroundStyle(showing ? Color.accentColor : Color.secondary)
            .contentShape(Rectangle())
            .onHover { showing = $0 }
            .onTapGesture { showing.toggle() }
            .popover(isPresented: $showing, arrowEdge: .bottom) {
                Text(text)
                    .font(.callout)
                    .padding(10)
                    .frame(width: 280, alignment: .leading)
                    .fixedSize(horizontal: false, vertical: true)
            }
    }
}

/// 알림 목록에서 받을 곳 표시. 꺼진 곳은 흐리게 줄을 긋는다.
private struct DeliveryBadge: View {
    let on: Bool
    let symbol: String
    let text: String

    var body: some View {
        Label(text, systemImage: symbol)
            .foregroundStyle(on ? Color.secondary : Color.secondary.opacity(0.4))
            .strikethrough(!on)
            .help(on ? "\(text)으로 받아요" : "\(text)으로는 받지 않아요")
    }
}
