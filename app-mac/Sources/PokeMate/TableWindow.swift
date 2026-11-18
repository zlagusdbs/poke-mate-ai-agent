import AppKit
import PokeMateCore
import SwiftUI

extension Notification.Name {
    static let pokeMateHoldPopover = Notification.Name("pokeMateHoldPopover")
    static let pokeMateResetSession = Notification.Name("pokeMateResetSession")
}

@MainActor
enum TableWindowController {
    private static var windows: [NSPanel] = []

    static func show(_ table: MarkdownTable) {
        let natural = TableMeasure.natural(table).map { CGFloat(max($0, TableLayout.minColumn)) }
        let contentWidth = natural.reduce(0, +) + MarkdownTableView.spacing * CGFloat(max(0, natural.count - 1)) + 48
        let contentHeight = CGFloat(table.rows.count + 1) * 30 + 110
        let screen = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        let size = NSSize(width: min(max(contentWidth, 420), screen.width * 0.8), height: min(max(contentHeight, 220), screen.height * 0.8))
        let panel = NSPanel(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
                            backing: .buffered, defer: false)
        panel.title = "표 크게 보기"
        panel.level = .floating
        panel.isReleasedWhenClosed = false
        panel.contentView = NSHostingView(rootView: TableFullView(table: table, widths: natural) { [weak panel] in panel?.close() })
        panel.center()
        windows.append(panel)
        NotificationCenter.default.post(name: .pokeMateHoldPopover, object: true)
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: panel, queue: .main) { _ in
            Task { @MainActor in
                windows.removeAll { $0 === panel }
                NotificationCenter.default.post(name: .pokeMateHoldPopover, object: false)
            }
        }
        panel.makeKeyAndOrderFront(nil)
    }
}

struct TableFullView: View {
    let table: MarkdownTable
    let widths: [CGFloat]
    let close: () -> Void
    @State private var copied: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text("\(table.header.count)열 · \(table.rows.count)행").font(.caption).foregroundStyle(.secondary)
                Spacer()
                if let copied { Text(copied).font(.caption).foregroundStyle(.secondary) }
                Menu("복사") {
                    Button("마크다운 표") { copy(table.markdown, "마크다운 표를 복사했어요") }
                    Button("탭 구분 (엑셀에 붙이기)") { copy(table.tabSeparated, "탭 구분 글을 복사했어요") }
                }
                .fixedSize()
                Button("닫기", action: close).keyboardShortcut(.cancelAction)
            }
            ScrollView([.horizontal, .vertical]) {
                LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                    Section {
                        ForEach(Array(table.rows.enumerated()), id: \.offset) { r, row in
                            TableLine(table: table, cells: row, widths: widths, bold: false)
                                .padding(.vertical, 6)
                            if r < table.rows.count - 1 { Divider().opacity(0.5) }
                        }
                    } header: {
                        VStack(spacing: 0) {
                            TableLine(table: table, cells: table.header, widths: widths, bold: true).padding(.vertical, 6)
                            Divider()
                        }
                        .background(Theme.card)
                    }
                }
                .padding(.horizontal, 4)
            }
            .padding(10)
            .background(Theme.card, in: RoundedRectangle(cornerRadius: Theme.corner, style: .continuous))
        }
        .padding(.horizontal, Theme.gutter)
        .padding(.bottom, Theme.gutter)
        .padding(.top, 34)
    }

    private func copy(_ text: String, _ message: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        copied = message
    }
}
