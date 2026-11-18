import Foundation

public struct MarkdownTable: Equatable, Sendable {
    public enum Align: Equatable, Sendable { case leading, center, trailing }
    public let header: [String]
    public let aligns: [Align]
    public let rows: [[String]]

    public init(header: [String], aligns: [Align], rows: [[String]]) {
        self.header = header
        self.aligns = aligns
        self.rows = rows
    }
}

public enum MarkdownBlock: Equatable, Sendable {
    case text(String)
    case table(MarkdownTable)
}

public enum MarkdownBlocks {
    static let separator = try! NSRegularExpression(pattern: #"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$"#)

    public static func cells(_ line: String) -> [String] {
        var trimmed = line.trimmingCharacters(in: .whitespaces)
        if trimmed.hasPrefix("|") { trimmed.removeFirst() }
        if trimmed.hasSuffix("|") && !trimmed.hasSuffix("\\|") { trimmed.removeLast() }
        var cells: [String] = []
        var current = ""
        var escaped = false
        for ch in trimmed {
            if escaped {
                current.append(ch == "|" ? "|" : "\\\(ch)")
                escaped = false
            } else if ch == "\\" {
                escaped = true
            } else if ch == "|" {
                cells.append(current.trimmingCharacters(in: .whitespaces))
                current = ""
            } else {
                current.append(ch)
            }
        }
        if escaped { current.append("\\") }
        cells.append(current.trimmingCharacters(in: .whitespaces))
        return cells
    }

    static func isSeparator(_ line: String) -> Bool {
        separator.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)) != nil && line.contains("-")
    }

    static func align(_ spec: String) -> MarkdownTable.Align {
        let s = spec.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix(":") && s.hasSuffix(":") { return .center }
        if s.hasSuffix(":") { return .trailing }
        return .leading
    }

    public static func split(_ text: String) -> [MarkdownBlock] {
        let lines = text.components(separatedBy: "\n")
        var blocks: [MarkdownBlock] = []
        var buffer: [String] = []
        var inFence = false
        var i = 0
        func flush() {
            let joined = buffer.joined(separator: "\n").trimmingCharacters(in: .newlines)
            if !joined.isEmpty { blocks.append(.text(joined)) }
            buffer = []
        }
        while i < lines.count {
            let line = lines[i]
            if line.trimmingCharacters(in: .whitespaces).hasPrefix("```") { inFence.toggle() }
            if !inFence, line.contains("|"), i + 1 < lines.count, isSeparator(lines[i + 1]) {
                let header = cells(line)
                let aligns = cells(lines[i + 1]).map(align)
                if header.count == aligns.count {
                    var rows: [[String]] = []
                    var j = i + 2
                    while j < lines.count, lines[j].contains("|"), !lines[j].trimmingCharacters(in: .whitespaces).isEmpty {
                        var row = cells(lines[j])
                        if row.count < header.count { row += Array(repeating: "", count: header.count - row.count) }
                        rows.append(Array(row.prefix(header.count)))
                        j += 1
                    }
                    flush()
                    blocks.append(.table(MarkdownTable(header: header, aligns: aligns, rows: rows)))
                    i = j
                    continue
                }
            }
            buffer.append(line)
            i += 1
        }
        flush()
        return blocks
    }

    public static func hasTable(_ text: String) -> Bool {
        split(text).contains { if case .table = $0 { return true } else { return false } }
    }

    public static func plain(_ text: String) -> String {
        split(text).map { block -> String in
            switch block {
            case .text(let t): return t
            case .table(let table):
                return table.rows.map { row in
                    zip(table.header, row).map { $0.isEmpty ? $1 : "\($0): \($1)" }.joined(separator: " · ")
                }.joined(separator: "\n")
            }
        }.joined(separator: "\n\n")
    }
}

public enum TableLayout {
    public static let minColumn: Double = 60

    public static func widths(natural: [Double], available: Double, spacing: Double, minWidth: Double = minColumn) -> [Double]? {
        let n = natural.count
        guard n > 0 else { return [] }
        let room = available - spacing * Double(n - 1)
        guard room >= minWidth * Double(n) else { return nil }
        let wants = natural.map { max($0, minWidth) }
        if wants.reduce(0, +) <= room { return wants }
        var fixed = Array(repeating: false, count: n)
        var result = wants
        while true {
            let fixedSum = zip(result, fixed).filter { $0.1 }.map(\.0).reduce(0, +)
            let flexible = (0..<n).filter { !fixed[$0] }
            let flexNatural = flexible.map { wants[$0] }.reduce(0, +)
            let share = room - fixedSum
            var changed = false
            for i in flexible {
                let w = flexNatural > 0 ? wants[i] / flexNatural * share : share / Double(flexible.count)
                if w < minWidth {
                    result[i] = minWidth
                    fixed[i] = true
                    changed = true
                } else {
                    result[i] = w
                }
            }
            if !changed { return result }
        }
    }

    public static func breakable(_ text: String, every: Int = 12) -> String {
        if text.contains("](") || text.contains("`") { return text }
        var out = ""
        var run = 0
        for ch in text {
            out.append(ch)
            if ch == " " || ch == "\n" {
                run = 0
                continue
            }
            run += 1
            if "-_/.:,".contains(ch) || run >= every {
                out.append("\u{200B}")
                run = 0
            }
        }
        return out
    }
}

public struct TablePreview: Equatable, Sendable {
    public let columns: Int
    public let widths: [Double]
    public let hiddenColumns: Int
    public let rows: Int
    public let hiddenRows: Int

    public var truncated: Bool { hiddenColumns > 0 || hiddenRows > 0 }

    public static let maxRows = 8
    public static let moreColumnWidth: Double = 44

    public static func make(natural: [Double], rowCount: Int, available: Double, spacing: Double,
                            maxRows: Int = maxRows, minWidth: Double = TableLayout.minColumn) -> TablePreview {
        let rows = min(rowCount, maxRows)
        if let all = TableLayout.widths(natural: natural, available: available, spacing: spacing, minWidth: minWidth) {
            return TablePreview(columns: natural.count, widths: all, hiddenColumns: 0, rows: rows, hiddenRows: rowCount - rows)
        }
        var k = natural.count - 1
        while k >= 1 {
            let room = available - moreColumnWidth - spacing
            if let w = TableLayout.widths(natural: Array(natural.prefix(k)), available: room, spacing: spacing, minWidth: minWidth) {
                return TablePreview(columns: k, widths: w, hiddenColumns: natural.count - k, rows: rows, hiddenRows: rowCount - rows)
            }
            k -= 1
        }
        let one = max(0, available - moreColumnWidth - spacing)
        return TablePreview(columns: min(1, natural.count), widths: natural.isEmpty ? [] : [one],
                            hiddenColumns: max(0, natural.count - 1), rows: rows, hiddenRows: rowCount - rows)
    }
}

extension MarkdownTable {
    public var markdown: String {
        func line(_ cells: [String]) -> String { "| " + cells.map { $0.replacingOccurrences(of: "|", with: "\\|") }.joined(separator: " | ") + " |" }
        let sep = "|" + aligns.map { a -> String in
            switch a {
            case .leading: return "---"
            case .center: return ":---:"
            case .trailing: return "---:"
            }
        }.joined(separator: "|") + "|"
        return ([line(header), sep] + rows.map(line)).joined(separator: "\n")
    }

    public var tabSeparated: String {
        func clean(_ s: String) -> String {
            s.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
                .replacingOccurrences(of: "\t", with: " ").replacingOccurrences(of: "\n", with: " ")
        }
        return ([header] + rows).map { $0.map(clean).joined(separator: "\t") }.joined(separator: "\n")
    }
}

public enum MarkdownText {
    public static func escapingSingleTildes(_ text: String) -> String {
        guard text.contains("~") else { return text }
        let regex = try! NSRegularExpression(pattern: "(?<![~\\\\])~(?!~)")
        return text.components(separatedBy: "`").enumerated().map { index, part in
            guard index % 2 == 0 else { return part }
            return regex.stringByReplacingMatches(in: part, range: NSRange(part.startIndex..., in: part), withTemplate: "\\\\~")
        }.joined(separator: "`")
    }
}
