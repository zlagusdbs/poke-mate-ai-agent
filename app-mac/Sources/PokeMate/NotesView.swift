import PokeMateCore
import SwiftUI

/// 메모 메뉴: 목록 → 누르면 편집. 고치면 바로 md 파일에 저장된다.
struct NotesView: View {
    @ObservedObject var model: AppModel
    @State private var notes: [Note] = []
    @State private var query = ""
    @State private var deleting: Note?

    var body: some View {
        if let url = model.openNote {
            NoteEditor(url: url) { model.openNote = nil; reload() }
        } else {
            list
        }
    }

    private var list: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("메모").font(.headline)
                Spacer()
                Button {
                    if let url = try? Notes.create(title: "", body: "") { model.openNote = url }
                } label: { Label("새 메모", systemImage: "square.and.pencil") }
                    .buttonStyle(.pill)
            }
            .padding(.horizontal, Theme.gutter)
            .padding(.bottom, 8)
            TextField("", text: $query, prompt: Text("메모 찾기"))
                .whiteField()
                .padding(.horizontal, Theme.gutter)
                .padding(.bottom, 6)
            let shown = filtered
            if shown.isEmpty {
                Text(notes.isEmpty ? "메모가 없어요. [새 메모]를 누르거나, 채팅에서 \"메모해줘: …\" 라고 말해 보세요."
                                   : "\"\(query)\" 이(가) 든 메모가 없어요.")
                    .font(.callout).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity).padding(.top, 40)
                Spacer()
            } else {
                List(shown) { note in
                    HStack(alignment: .top, spacing: 8) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(note.title).font(.callout.weight(.medium)).lineLimit(1)
                            HStack(spacing: 6) {
                                Text(note.modified, format: .dateTime.month().day().hour().minute())
                                if !note.preview.isEmpty { Text(note.preview).lineLimit(1) }
                            }
                            .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button(role: .destructive) { deleting = note } label: { Image(systemName: "trash") }
                            .buttonStyle(.borderless).foregroundStyle(.red).font(.caption).help("삭제")
                    }
                    .padding(.vertical, 6)
                    .contentShape(Rectangle())
                    .onTapGesture { model.openNote = note.url }
                    .pmListRow()
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
            }
        }
        .onAppear(perform: reload)
        .alert("메모를 삭제할까요?", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
               presenting: deleting) { note in
            Button("삭제", role: .destructive) { try? Notes.delete(note.url); reload() }
            Button("취소", role: .cancel) {}
        } message: { note in
            Text("\"\(note.title)\" 메모를 지워요. 되돌릴 수 없어요.")
        }
    }

    private var filtered: [Note] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return notes }
        return notes.filter { $0.title.lowercased().contains(q) || Notes.read($0.url).lowercased().contains(q) }
    }

    private func reload() { notes = Notes.list() }
}

/// 메모 한 개 편집. 글자를 고칠 때마다 저장한다 (마크다운 그대로).
private struct NoteEditor: View {
    let url: URL
    let close: () -> Void
    @State private var text = ""
    @State private var loaded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Button(action: close) { Label("목록", systemImage: "chevron.left") }.buttonStyle(.borderless)
                Spacer()
                Button { NSWorkspace.shared.open(url) } label: { Image(systemName: "arrow.up.forward.square") }
                    .buttonStyle(.borderless).help("다른 앱으로 열기")
            }
            .padding(.horizontal, Theme.gutter)
            .padding(.bottom, 6)
            TextEditor(text: $text)
                .font(.body.monospaced())
                .scrollContentBackground(.hidden)
                .padding(10)
                .background(Theme.card, in: RoundedRectangle(cornerRadius: Theme.corner, style: .continuous))
                .padding(.horizontal, Theme.gutter)
                .onChange(of: text) { if loaded { try? Notes.save(url, text: text) } }
            Text("첫 줄이 제목이에요. 마크다운으로 적으면 돼요 (# 제목, - 목록, | 표 |).")
                .font(.caption).foregroundStyle(.secondary).padding(8)
        }
        .onAppear {
            text = Notes.read(url)
            loaded = true
        }
    }
}
