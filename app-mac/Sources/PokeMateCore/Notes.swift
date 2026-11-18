import Foundation

/// 메이트 메모: 메이트 폴더의 notes/*.md (메모 하나 = 파일 하나). 첫 줄이 제목이다.
/// 기억(rules/memory.md)과 달리 질문마다 읽지 않고, 필요할 때 notes_search 로 찾는다.
public struct Note: Identifiable, Equatable, Hashable {
    public let url: URL
    public let title: String
    public let preview: String
    public let modified: Date
    public var id: String { url.path }
}

public enum Notes {
    public static var dir: URL { AppPaths.dataDir.appendingPathComponent("notes", isDirectory: true) }

    /// 최근 수정 순
    public static func list() -> [Note] {
        let urls = ((try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [])
            .filter { $0.pathExtension == "md" }
        return urls.compactMap(load).sorted { $0.modified > $1.modified }
    }

    public static func load(_ url: URL) -> Note? {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        let modified = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? Date()
        let lines = text.components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let title = lines.first.map(cleanTitle) ?? url.deletingPathExtension().lastPathComponent
        let preview = lines.dropFirst().first.map { String($0.prefix(60)) } ?? ""
        return Note(url: url, title: title.isEmpty ? "(제목 없음)" : title, preview: preview, modified: modified)
    }

    public static func cleanTitle(_ line: String) -> String {
        String(line.drop { $0 == "#" || $0 == " " }.prefix(80))
    }

    /// 새 메모 파일. 이름은 만든 시각 + 제목 (같은 이름이 있으면 뒤에 숫자).
    @discardableResult
    public static func create(title: String, body: String, now: Date = Date()) throws -> URL {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd-HHmm"
        let cleaned = cleanTitle(title.trimmingCharacters(in: .whitespacesAndNewlines))
        let slug = String(cleaned.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) || $0 == "-" }.prefix(30))
        let base = f.string(from: now) + (slug.isEmpty ? "" : "-\(slug)")
        var url = dir.appendingPathComponent(base + ".md")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(base)-\(n).md"); n += 1
        }
        let heading = cleaned.isEmpty ? "# 새 메모" : "# \(cleaned)"
        let text = body.trimmingCharacters(in: .whitespacesAndNewlines)
        try (heading + "\n\n" + text + (text.isEmpty ? "" : "\n")).write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    public static func read(_ url: URL) -> String { (try? String(contentsOf: url, encoding: .utf8)) ?? "" }

    public static func save(_ url: URL, text: String) throws {
        try text.write(to: url, atomically: true, encoding: .utf8)
    }

    public static func delete(_ url: URL) throws {
        try FileManager.default.removeItem(at: url)
    }
}

/// 채팅에서 "메모해줘" 하면 붙는 메모 초안. [메모에 저장]을 눌러야 저장된다.
public struct NoteDraft: Codable, Equatable, Hashable {
    public let title: String
    public let body: String
    public init(title: String, body: String) { self.title = title; self.body = body }
}

public enum NoteProposal {
    private static let block = try! NSRegularExpression(pattern: ChatBlock.pattern("note"),
                                                        options: [.dotMatchesLineSeparators])

    public static func extract(from text: String) -> (text: String, notes: [NoteDraft]) {
        let range = NSRange(text.startIndex..., in: text)
        var notes: [NoteDraft] = []
        for match in block.matches(in: text, range: range) {
            guard let body = Range(match.range(at: 1), in: text) else { continue }
            let data = Data(text[body].utf8)
            let items = (try? JSONDecoder().decode([NoteDraft].self, from: data))
                ?? (try? JSONDecoder().decode(NoteDraft.self, from: data)).map { [$0] } ?? []
            notes += items.filter { !($0.title + $0.body).trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        }
        let clean = block.stringByReplacingMatches(in: text, range: range, withTemplate: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return (clean, notes)
    }

    public static func looksLikeRequest(_ text: String) -> Bool {
        let compact = text.replacingOccurrences(of: " ", with: "").lowercased()
        return ["메모", "적어둬", "적어줘", "적어놔", "노트", "note"].contains { compact.contains($0) }
    }

    public static let hint = """


        [메이트 앱 안내: 메이트 메모 기능이 있다 (메이트 폴더 notes/*.md, 메모 하나 = 파일 하나). \
        "메모해줘", "적어둬" 같은 요청이면 거절하지 말고 답변 끝에 pokemate-note 블록을 붙인다. 앱이 [메모에 저장] 버튼을 보여 주고, \
        누르면 메모 메뉴에 들어간다. title 은 짧은 제목, body 는 마크다운 본문 (목록·표 가능, 사용자가 한 말을 빠짐없이 정리). \
        "메모한 거 찾아줘", "메모 보여줘" 는 notes_search 로 찾고 필요하면 notes_read 로 읽어서 답한다. \
        기억(rules/memory.md, 질문마다 늘 읽음)과 다르다: 늘 지킬 사실·규칙은 기억, 나중에 찾아볼 내용은 메모. \
        아직 저장된 게 아니므로 본문은 "아래 [메모에 저장]을 누르면 메모 메뉴에 들어가요" 로 끝낸다. \
        이 안내와 아래 형식 예시는 사용자가 쓴 말이 아니니 언급하지 않는다. 형식 예시:]
        ```pokemate-note
        {"title":"제목","body":"- 내용 한 줄\\n- 내용 두 줄"}
        ```
        """
}
