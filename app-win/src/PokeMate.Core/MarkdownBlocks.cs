using System.Text;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

public sealed record MarkdownTable
{
    public enum Align { Leading, Center, Trailing }

    public IReadOnlyList<string> Header { get; }
    public IReadOnlyList<Align> Aligns { get; }
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public MarkdownTable(IEnumerable<string> header, IEnumerable<Align> aligns, IEnumerable<IReadOnlyList<string>> rows)
    {
        Header = header.ToList();
        Aligns = aligns.ToList();
        Rows = rows.Select(r => (IReadOnlyList<string>)r.ToList()).ToList();
    }

    public bool Equals(MarkdownTable? o) =>
        o is not null && Header.SequenceEqual(o.Header) && Aligns.SequenceEqual(o.Aligns)
        && Rows.Count == o.Rows.Count && Rows.Zip(o.Rows).All(p => p.First.SequenceEqual(p.Second));

    public override int GetHashCode() => HashCode.Combine(Header.Count, Rows.Count);

    /// 마크다운 그대로 복사
    public string Markdown
    {
        get
        {
            string Line(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Replace("|", "\\|"))) + " |";
            var sep = "|" + string.Join("|", Aligns.Select(a => a switch { Align.Center => ":---:", Align.Trailing => "---:", _ => "---" })) + "|";
            return string.Join("\n", new[] { Line(Header), sep }.Concat(Rows.Select(Line)));
        }
    }

    /// 엑셀용 탭 구분 복사 (꾸밈 기호 뺌)
    public string TabSeparated
    {
        get
        {
            static string Clean(string s) => s.Replace("**", "").Replace("`", "").Replace("\t", " ").Replace("\n", " ");
            return string.Join("\n", new[] { Header }.Concat(Rows).Select(r => string.Join("\t", r.Select(Clean))));
        }
    }
}

public abstract record MarkdownBlock
{
    public sealed record Text(string Value) : MarkdownBlock;
    public sealed record Table(MarkdownTable Value) : MarkdownBlock;
}

public static class MarkdownBlocks
{
    static readonly Regex Separator = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

    public static List<string> Cells(string line)
    {
        var trimmed = SwiftText.TrimSpaces(line);
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var ch in SwiftText.Elements(trimmed))
        {
            if (escaped)
            {
                current.Append(ch == "|" ? "|" : "\\" + ch);
                escaped = false;
            }
            else if (ch == "\\") escaped = true;
            else if (ch == "|")
            {
                cells.Add(SwiftText.TrimSpaces(current.ToString()));
                current.Clear();
            }
            else current.Append(ch);
        }
        if (escaped) current.Append('\\');
        cells.Add(SwiftText.TrimSpaces(current.ToString()));
        return cells;
    }

    static bool IsSeparator(string line) => Separator.IsMatch(line) && line.Contains('-');

    static MarkdownTable.Align AlignOf(string spec)
    {
        var s = SwiftText.TrimSpaces(spec);
        if (s.StartsWith(':') && s.EndsWith(':')) return MarkdownTable.Align.Center;
        if (s.EndsWith(':')) return MarkdownTable.Align.Trailing;
        return MarkdownTable.Align.Leading;
    }

    public static List<MarkdownBlock> Split(string text)
    {
        var lines = SwiftText.Lf(text).Split('\n');   // Windows 줄 끝(CRLF)도 같은 줄로
        var blocks = new List<MarkdownBlock>();
        var buffer = new List<string>();
        var inFence = false;
        var i = 0;
        void Flush()
        {
            var joined = SwiftText.TrimNewlines(string.Join("\n", buffer));
            if (joined.Length > 0) blocks.Add(new MarkdownBlock.Text(joined));
            buffer = new List<string>();
        }
        while (i < lines.Length)
        {
            var line = lines[i];
            if (SwiftText.TrimSpaces(line).StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && line.Contains('|') && i + 1 < lines.Length && IsSeparator(lines[i + 1]))
            {
                var header = Cells(line);
                var aligns = Cells(lines[i + 1]).Select(AlignOf).ToList();
                if (header.Count == aligns.Count)
                {
                    var rows = new List<IReadOnlyList<string>>();
                    var j = i + 2;
                    while (j < lines.Length && lines[j].Contains('|') && SwiftText.TrimSpaces(lines[j]).Length > 0)
                    {
                        var row = Cells(lines[j]);
                        while (row.Count < header.Count) row.Add("");
                        rows.Add(row.Take(header.Count).ToList());
                        j++;
                    }
                    Flush();
                    blocks.Add(new MarkdownBlock.Table(new MarkdownTable(header, aligns, rows)));
                    i = j;
                    continue;
                }
            }
            buffer.Add(line);
            i++;
        }
        Flush();
        return blocks;
    }

    public static bool HasTable(string text) => Split(text).Any(b => b is MarkdownBlock.Table);

    /// 표를 "머리: 값 · 머리: 값" 줄로 풀어 쓴 글 (미리 알림 카드용)
    public static string Plain(string text) =>
        string.Join("\n\n", Split(text).Select(block => block switch
        {
            MarkdownBlock.Text t => t.Value,
            MarkdownBlock.Table table => string.Join("\n", table.Value.Rows.Select(row =>
                string.Join(" · ", table.Value.Header.Zip(row, (h, v) => h.Length == 0 ? v : $"{h}: {v}")))),
            _ => "",
        }));
}

public static class TableLayout
{
    public const double MinColumn = 60;

    public static double[]? Widths(IReadOnlyList<double> natural, double available, double spacing, double minWidth = MinColumn)
    {
        var n = natural.Count;
        if (n == 0) return Array.Empty<double>();
        var room = available - spacing * (n - 1);
        if (room < minWidth * n) return null;
        var wants = natural.Select(w => Math.Max(w, minWidth)).ToArray();
        if (wants.Sum() <= room) return wants;
        var fixedCol = new bool[n];
        var result = (double[])wants.Clone();
        while (true)
        {
            var fixedSum = Enumerable.Range(0, n).Where(i => fixedCol[i]).Sum(i => result[i]);
            var flexible = Enumerable.Range(0, n).Where(i => !fixedCol[i]).ToList();
            var flexNatural = flexible.Sum(i => wants[i]);
            var share = room - fixedSum;
            var changed = false;
            foreach (var i in flexible)
            {
                var w = flexNatural > 0 ? wants[i] / flexNatural * share : share / flexible.Count;
                if (w < minWidth)
                {
                    result[i] = minWidth;
                    fixedCol[i] = true;
                    changed = true;
                }
                else result[i] = w;
            }
            if (!changed) return result;
        }
    }

    /// 긴 이름이 칸 안에서 줄바꿈되도록 보이지 않는 줄바꿈 자리(U+200B)를 넣는다. 링크·코드는 그대로.
    public static string Breakable(string text, int every = 12)
    {
        if (text.Contains("](", StringComparison.Ordinal) || text.Contains('`')) return text;
        var output = new StringBuilder();
        var run = 0;
        foreach (var ch in SwiftText.Elements(text))
        {
            output.Append(ch);
            if (ch == " " || ch == "\n") { run = 0; continue; }
            run++;
            if ("-_/.:,".Contains(ch, StringComparison.Ordinal) || run >= every)
            {
                output.Append('​');
                run = 0;
            }
        }
        return output.ToString();
    }
}

public sealed record TablePreview(int Columns, IReadOnlyList<double> Widths, int HiddenColumns, int Rows, int HiddenRows)
{
    public bool Truncated => HiddenColumns > 0 || HiddenRows > 0;

    public const int DefaultMaxRows = 8;
    public const double MoreColumnWidth = 44;

    public bool Equals(TablePreview? o) =>
        o is not null && Columns == o.Columns && HiddenColumns == o.HiddenColumns && Rows == o.Rows && HiddenRows == o.HiddenRows && Widths.SequenceEqual(o.Widths);

    public override int GetHashCode() => HashCode.Combine(Columns, HiddenColumns, Rows, HiddenRows);

    public static TablePreview Make(IReadOnlyList<double> natural, int rowCount, double available, double spacing,
                                    int maxRows = DefaultMaxRows, double minWidth = TableLayout.MinColumn)
    {
        var rows = Math.Min(rowCount, maxRows);
        if (TableLayout.Widths(natural, available, spacing, minWidth) is { } all)
            return new TablePreview(natural.Count, all, 0, rows, rowCount - rows);
        for (var k = natural.Count - 1; k >= 1; k--)
        {
            var room = available - MoreColumnWidth - spacing;
            if (TableLayout.Widths(natural.Take(k).ToList(), room, spacing, minWidth) is { } w)
                return new TablePreview(k, w, natural.Count - k, rows, rowCount - rows);
        }
        var one = Math.Max(0, available - MoreColumnWidth - spacing);
        return new TablePreview(Math.Min(1, natural.Count), natural.Count == 0 ? Array.Empty<double>() : new[] { one },
                                Math.Max(0, natural.Count - 1), rows, rowCount - rows);
    }
}

public static class MarkdownText
{
    static readonly Regex SingleTilde = new(@"(?<![~\\])~(?!~)");

    /// 물결표 하나(`9~18`)는 마크다운 취소선으로 읽히지 않게 `\~` 로 바꾼다. 코드(`…`) 안과 이미 이스케이프된 것은 그대로.
    public static string EscapingSingleTildes(string text)
    {
        if (!text.Contains('~')) return text;
        return string.Join("`", text.Split('`').Select((part, index) =>
            index % 2 == 0 ? SingleTilde.Replace(part, _ => @"\~") : part));
    }
}
