using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 마크다운 표, 표 열 너비, 표 미리보기·복사, 물결표 하나는 글자로
public static class MarkdownTableChecks
{
    public static void Run()
    {
        // 마크다운 표
        var text = "세션 현황이에요.\n\n| 구분 | 개수 | 비고 |\n|:---|---:|:---:|\n| **작업 세션** | 1 | `price-service` |\n| 대화·알람 | 28 | a \\| b |\n\n끝.";
        var blocks = MarkdownBlocks.Split(text);
        var t = blocks.Count > 1 && blocks[1] is MarkdownBlock.Table found ? found.Value : new MarkdownTable(new string[0], new MarkdownTable.Align[0], new string[0][]);
        Check.That(blocks.Count == 3 && blocks[0] == new MarkdownBlock.Text("세션 현황이에요.") && blocks[2] == new MarkdownBlock.Text("끝."), "table: 글·표·글로 나눔");
        Check.That(t.Header.SequenceEqual(new[] { "구분", "개수", "비고" })
                   && t.Aligns.SequenceEqual(new[] { MarkdownTable.Align.Leading, MarkdownTable.Align.Trailing, MarkdownTable.Align.Center }), "table: 머리줄과 열 정렬");
        Check.That(t.Rows.Count == 2 && t.Rows[0].SequenceEqual(new[] { "**작업 세션**", "1", "`price-service`" }) && t.Rows[1].SequenceEqual(new[] { "대화·알람", "28", "a | b" }),
                   "table: 칸 (꾸밈 그대로, \\| 는 글자)");
        Check.That(MarkdownBlocks.Split("```\n| a | b |\n|---|---|\n```").Count == 1 && !MarkdownBlocks.HasTable("```\n| a | b |\n|---|---|\n```"), "table: 코드 블록 안의 표 모양은 그대로 글");
        Check.That(MarkdownBlocks.Split("a | b 는 파이프\n그냥 글").SequenceEqual(new MarkdownBlock[] { new MarkdownBlock.Text("a | b 는 파이프\n그냥 글") }), "table: 구분줄 없으면 표 아님");
        Check.That(MarkdownBlocks.Plain("| 이름 | 수 |\n|---|---|\n| x | 1 |\n| y |") == "이름: x · 수: 1\n이름: y · 수: ", "table: 미리 알림 카드용 정렬 글 (모자란 칸 채움)");
        Check.That(MarkdownBlocks.Split("| a | b |\r\n|---|---|\r\n| 1 | 2 |\r\n").Count == 1
                   && MarkdownBlocks.Split("| a | b |\r\n|---|---|\r\n| 1 | 2 |\r\n")[0] is MarkdownBlock.Table, "table: CRLF 줄 끝도 표로 읽음 (Windows 줄 끝)");

        // 표 열 너비
        var fits = TableLayout.Widths(new double[] { 80, 50, 100 }, 400, 10);
        Check.That(fits != null && fits.SequenceEqual(new double[] { 80, 60, 100 }), "tablelayout: 다 들어가면 자연 폭 (최소 60)");
        var squeezed = TableLayout.Widths(new double[] { 300, 100, 400, 200, 120 }, 450, 10)!;
        Check.That(Math.Abs(squeezed.Sum() - 410) < 0.001 && squeezed.All(w => w >= 60) && squeezed[2] > squeezed[0] && squeezed[0] > squeezed[3],
                   "tablelayout: 넘치면 자연 폭 비례로 줄이고 최소 60, 말풍선 폭 안에");
        var withMin = TableLayout.Widths(new double[] { 1000, 10, 10 }, 300, 0)!;
        Check.That(withMin[1] == 60 && withMin[2] == 60 && Math.Abs(withMin[0] - 180) < 0.001, "tablelayout: 좁은 열은 최소 폭 고정, 남는 폭은 넓은 열에");
        Check.That(TableLayout.Widths(Enumerable.Repeat(100.0, 8).ToArray(), 450, 10) == null, "tablelayout: 최소 폭으로도 안 들어가면 카드로");
        Check.That(TableLayout.Breakable("sample-shop-price-service") == "sample-​shop-​price-​service"
                   && TableLayout.Breakable("[a](http://x-y)") == "[a](http://x-y)" && TableLayout.Breakable("`a-b`") == "`a-b`",
                   "tablelayout: 긴 이름은 칸 안 줄바꿈 자리, 링크·코드는 그대로");

        // 표 미리보기·복사
        var fit = TablePreview.Make(new double[] { 100, 80, 90 }, 3, 450, 12);
        Check.That(fit.Columns == 3 && fit.HiddenColumns == 0 && fit.HiddenRows == 0 && !fit.Truncated, "tablepreview: 들어가면 그대로");
        var wide = TablePreview.Make(Enumerable.Repeat(100.0, 9).ToArray(), 3, 450, 12);
        var used = wide.Widths.Sum() + wide.Columns * 12 + TablePreview.MoreColumnWidth;
        Check.That(wide.Columns == 5 && wide.HiddenColumns == 4 && wide.Truncated && used <= 450 + 0.001, "tablepreview: 들어가는 앞쪽 열만 + N열");
        var tall = TablePreview.Make(new double[] { 80, 80 }, 20, 450, 12);
        Check.That(tall.Rows == 8 && tall.HiddenRows == 12 && tall.HiddenColumns == 0 && tall.Truncated, "tablepreview: 8행 넘으면 앞 8행 + M행");
        var table = new MarkdownTable(new[] { "a", "b" }, new[] { MarkdownTable.Align.Leading, MarkdownTable.Align.Trailing },
                                      new[] { new[] { "**x**", "1|2" }, new[] { "`y`", "3" } });
        Check.That(table.Markdown == "| a | b |\n|---|---:|\n| **x** | 1\\|2 |\n| `y` | 3 |", "tablepreview: 마크다운 그대로 복사");
        Check.That(table.TabSeparated == "a\tb\nx\t1|2\ny\t3", "tablepreview: 엑셀용 탭 구분 복사 (꾸밈 기호 뺌)");

        // 물결표 하나는 글자로 (맥 검사의 취소선 판정은 AttributedString 이라 옮기지 못하고, 그 입력인 이스케이프 결과를 직접 확인한다)
        Check.That(MarkdownText.EscapingSingleTildes("근무시간(9~18시)만 잡아도 … 1. 평일 9~18시") == "근무시간(9\\~18시)만 잡아도 … 1. 평일 9\\~18시", "markdown: 물결표 하나가 두 번 나와도 모두 글자로 (취소선 안 됨)");
        Check.That(MarkdownText.EscapingSingleTildes("이건 ~~지움~~ 이야") == "이건 ~~지움~~ 이야", "markdown: 물결표 두 개 쌍은 취소선 그대로");
        Check.That(MarkdownText.EscapingSingleTildes("`a~b` 와 9~18") == "`a~b` 와 9\\~18" && MarkdownText.EscapingSingleTildes("이미 \\~ 됨") == "이미 \\~ 됨",
                   "markdown: 코드 안·이미 이스케이프된 물결표는 그대로");
        Check.That(MarkdownText.EscapingSingleTildes("물결표 없음") == "물결표 없음", "markdown: 물결표가 없으면 그대로");
    }
}
