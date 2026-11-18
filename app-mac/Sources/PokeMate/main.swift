import AppKit
import Combine
import PokeMateCore
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let model = AppModel()
    private var statusItem: NSStatusItem!
    private let popover = NSPopover()
    private let hotkey = HotkeyManager()
    private var notifier: Notifier!
    private var scheduler: AlarmScheduler!
    private let reminders = ReminderCenter()
    private var reminderPanel: ReminderPanelController!
    private var triggers: TriggerWatcher!
    private var hotkeyWatch: AnyCancellable?

    private var keyMonitor: Any?
    private static let statusItemName = "PokeMateStatusItem"
    private var usageWatch: AnyCancellable?
    private var sizeWatch: AnyCancellable?
    private var iconWatch: AnyCancellable?
    private var pinWatch: AnyCancellable?
    private var rangeWatch: AnyCancellable?
    private var popoverHolds = 0

    func applicationWillTerminate(_ notification: Notification) {
        WorkSessionCenter.shared.quitting = true
        model.chat.stopAllForQuit()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let active = WorkSessionCenter.shared.pipelines.values.filter(\.isActive)
        guard !active.isEmpty else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = "처리 중인 티켓이 있어요"
        alert.informativeText = active.map(\.statusText).joined(separator: "\n") + "\n\n지금 끄면 다음에 켤 때 끊긴 단계부터 이어서 해요."
        alert.addButton(withTitle: "끄기")
        alert.addButton(withTitle: "취소")
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn ? .terminateNow : .terminateCancel
    }

    /// 메뉴바 앱이라 기본 메뉴가 없어서, ⌘C·⌘V 등이 동작하도록 편집 메뉴를 직접 만든다.
    private func installEditMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "창 닫기", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        appMenu.addItem(withTitle: "PokeMate 숨기기", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "PokeMate 종료", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "편집")
        edit.addItem(withTitle: "실행 취소", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "다시 실행", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "오려두기", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "복사하기", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "붙여넣기", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "모두 선택", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)
        NSApp.mainMenu = main
    }

    /// 클립보드에 이미지가 있을 때 ⌘V 는 이미지 첨부로 처리하고, 글자는 원래대로 붙여넣는다.
    private func installImagePasteMonitor() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            // 한글 입력 중에는 글자가 "ㅍ" 로 오므로 글자 대신 물리 키 위치(V = 9)로 판단한다.
            let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
            guard flags == .command, event.keyCode == 9,
                  ImageLoader.pasteboardHasImage() else { return event }
            NotificationCenter.default.post(name: .pokeMatePasteImages, object: nil)
            return nil
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        installEditMenu()
        ClaudeCommand.installPermissions()
        LegacyMigration.copyKeychain(settings: model.settings)
        _ = WorkSessionCenter.shared
        installImagePasteMonitor()
        // 처음 한 번은 메뉴바 오른쪽 끝(시계 쪽)에 둔다. 아이콘이 많으면 왼쪽(노치 쪽) 아이콘이 숨거나
        // Ice 같은 메뉴바 정리 앱이 숨기는데, 오른쪽 끝은 늘 보인다. 사용자가 ⌘+끌기로 옮기면 그 자리를 기억한다.
        let positionKey = "NSStatusItem Preferred Position \(Self.statusItemName)"
        if !UserDefaults.standard.bool(forKey: "placedStatusItem") {
            UserDefaults.standard.set(true, forKey: "placedStatusItem")
            UserDefaults.standard.set(1, forKey: positionKey)
            UserDefaults.standard.set(true, forKey: "NSStatusItem Visible \(Self.statusItemName)")
        }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.autosaveName = Self.statusItemName
        if let button = statusItem.button {
            button.image = PokemonSprites.emptyEggIcon()
            button.imagePosition = .imageLeading
            button.action = #selector(statusClicked)
            button.target = self
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        popover.behavior = .transient
        popover.animates = false
        popover.contentSize = PopoverSize.shared.size
        sizeWatch = PopoverSize.shared.$size.removeDuplicates().sink { [weak self] size in
            self?.popover.contentSize = size
        }
        popover.contentViewController = NSHostingController(rootView: RootView(model: model))
        rangeWatch = model.$settings.map(\.pokedexRange).removeDuplicates().sink { range in
            UsageStore.shared.applyRange(range)
        }
        NotificationCenter.default.addObserver(forName: .pokeMateHoldPopover, object: nil, queue: .main) { [weak self] note in
            Task { @MainActor in
                guard let self else { return }
                self.popoverHolds = max(0, self.popoverHolds + ((note.object as? Bool) == true ? 1 : -1))
                self.applyPin(PopoverPin.shared.pinned)
            }
        }
        pinWatch = PopoverPin.shared.$pinned.removeDuplicates().sink { [weak self] pinned in
            self?.applyPin(pinned)
        }

        hotkeyWatch = model.$settings.map(\.hotkey).removeDuplicates().sink { [weak self] key in
            self?.hotkey.register(key) { self?.togglePopover() }
        }
        notifier = Notifier { [weak self] info in
            self?.model.selected = SidebarItem.target(open: info["open"] as? String)
            self?.showPopover()
        }
        WorkSessionCenter.shared.onContextAlert = { [weak self] session, percent in
            guard let self else { return }
            let title = "작업 세션 문맥 \(self.model.settings.workSessionAlertPercent)% 넘음"
            let body = "\(session.repo) · 지금 \(percent)% · 처리한 티켓 \(session.keys.count)건"
            self.notifier.send(title: title, body: body, info: ["open": "workSessions"])
            self.reminders.add(title: title, text: body, failed: false, sessionRepo: session.repo)
        }
        WorkSessionCenter.shared.onPipelineNotice = { [weak self] title, body in
            guard let self else { return }
            self.notifier.send(title: title, body: body, info: ["open": "workSessions"])
            self.reminders.add(title: title, text: body, failed: false, url: nil)
        }
        NotificationCenter.default.addObserver(forName: .pokeMateResetSession, object: nil, queue: .main) { [weak self] note in
            Task { @MainActor in
                guard let self, let repo = note.object as? String else { return }
                self.model.selected = SidebarItem.workSessionsTab
                self.showPopover()
                WorkSessionCenter.shared.requestReset = repo
            }
        }
        reminderPanel = ReminderPanelController(center: reminders) { [weak self] in
            self?.model.selected = .chat
            self?.showPopover()
        }
        reminders.start()
        reminderPanel.refresh()
        var gate = PermissionAlertGate()
        model.chat.onPermissionAsked = { [weak self] request in
            guard let self, gate.shouldAlert(now: Date()) else { return }
            self.notifier.send(title: "\(UsageStore.shared.mateName(self.model.settings)) 승인 요청",
                               body: PermissionAlertGate.body(for: request), id: PermissionAlertGate.notificationId)
        }
        model.chat.onPermissionDone = { [weak self] in
            self?.notifier.remove(id: PermissionAlertGate.notificationId)
        }
        triggers = TriggerWatcher(model: model, notifier: notifier, reminders: reminders)
        scheduler = AlarmScheduler(model: model, notifier: notifier, reminders: reminders, triggers: triggers)
        scheduler.start()
        triggers.start()
        WorkSessionCenter.shared.resumePipelines(chat: model.chat) { [weak self] title, text, failed in
            self?.reminders.add(title: title, text: text, failed: failed, url: nil)
        }
        UsageStore.shared.start()
        DispatchQueue.main.async { [weak self] in self?.showPopover() }
        usageWatch = UsageStore.shared.$summary.receive(on: RunLoop.main).sink { [weak self] s in
            self?.statusItem.button?.title = s.today.total > 0 ? " " + TokenFormat.compact(s.today.total) : ""
        }
        let usage = UsageStore.shared
        iconWatch = Publishers.CombineLatest3(usage.$mate, usage.$allSpecies, PokemonSprites.shared.$images)
            .receive(on: RunLoop.main)
            .sink { [weak self] state, _, _ in self?.updateStatusIcon(state) }
    }

    private func updateStatusIcon(_ state: MateState) {
        guard let button = statusItem.button else { return }
        if let current = state.current, let species = UsageStore.shared.species(current.species),
           let icon = PokemonSprites.shared.menuBarIcon(species: species, tokens: current.tokens, final: current.form, shown: current.shown) {
            button.image = icon
        } else {
            button.image = PokemonSprites.emptyEggIcon()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showPopover()
        return false
    }

    private func applyPin(_ pinned: Bool) {
        popover.behavior = pinned || popoverHolds > 0 ? .applicationDefined : .transient
        if pinned { popover.contentViewController?.view.window?.level = .floating }
        if pinned && !popover.isShown { showPopover() }
    }

    @objc private func statusClicked() {
        let event = NSApp.currentEvent
        if event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true {
            showStatusMenu()
        } else {
            togglePopover()
        }
    }

    private func showStatusMenu() {
        let pinned = PopoverPin.shared.pinned
        let menu = NSMenu()
        let pin = menu.addItem(withTitle: pinned ? "고정 풀기" : "고정하기", action: #selector(togglePin), keyEquivalent: "")
        pin.target = self
        pin.state = pinned ? .on : .off
        menu.addItem(.separator())
        menu.addItem(withTitle: "PokeMate 종료", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        statusItem.menu = menu
        statusItem.button?.performClick(nil)
        statusItem.menu = nil
    }

    @objc private func togglePin() {
        PopoverPin.shared.pinned.toggle()
    }

    @objc private func togglePopover() {
        popover.isShown ? popover.performClose(nil) : showPopover()
    }

    private func showPopover() {
        guard let button = statusItem.button else { return }
        NSApp.activate(ignoringOtherApps: true)
        if !popover.isShown {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
        if PopoverPin.shared.pinned { popover.contentViewController?.view.window?.level = .floating }
        popover.contentViewController?.view.window?.makeKey()
    }
}

extension Notification.Name {
    static let pokeMatePasteImages = Notification.Name("pokeMatePasteImages")
}

MainActor.assumeIsolated {
    LegacyMigration.copyDefaults()
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    withExtendedLifetime(delegate) { app.run() }
}
