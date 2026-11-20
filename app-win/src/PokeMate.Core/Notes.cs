using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 메이트 메모: 메이트 폴더(앱 데이터 폴더)의 notes\*.md (메모 하나 = 파일 하나). 첫 줄이 제목이다.
/// 기억(rules/memory.md)과 달리 질문마다 읽지 않고, 필요할 때 notes_search 로 찾는다. 파이썬 서버(notes.py)도 같은 폴더를 본다.
public sealed record Note(string Url, string Title, string Preview, DateTimeOffset Modified)
{
    public string Id => Url;
}

public static class Notes
{
    public static string Dir => Path.Combine(AppPaths.DataDir, "notes");

    static readonly UTF8Encoding Utf8 = new(false);

    /// 최근 수정 순
    public static List<Note> List()
    {
        try
        {
            return Directory.EnumerateFiles(Dir, "*.md").Select(Load).OfType<Note>().OrderByDescending(n => n.Modified).ToList();
        }
        catch { return new List<Note>(); }
    }

    public static Note? Load(string url)
    {
        string text;
        try { text = System.IO.File.ReadAllText(url); } catch { return null; }
        DateTimeOffset modified;
        try { modified = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(url), TimeSpan.Zero); } catch { modified = DateTimeOffset.Now; }
        var lines = SwiftText.Lf(text).Split('\n').Select(SwiftText.TrimSpaces).Where(l => l.Length > 0).ToList();
        var title = lines.Count > 0 ? CleanTitle(lines[0]) : Path.GetFileNameWithoutExtension(url);
        var preview = lines.Count > 1 ? SwiftText.Prefix(lines[1], 60) : "";
        return new Note(url, title.Length == 0 ? "(제목 없음)" : title, preview, modified);
    }

    /// 앞의 `#`·공백을 떼고 80자까지
    public static string CleanTitle(string line)
    {
        var elements = SwiftText.Elements(line).SkipWhile(c => c == "#" || c == " ");
        return SwiftText.Prefix(string.Concat(elements), 80);
    }

    /// 새 메모 파일. 이름은 만든 시각 + 제목 (같은 이름이 있으면 뒤에 숫자). 시각은 지금 시각대 기준.
    public static string Create(string title, string body, DateTimeOffset? now = null)
    {
        Directory.CreateDirectory(Dir);
        var stamp = (now ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var cleaned = CleanTitle(title.Trim());
        var slug = Slug(cleaned);
        var name = stamp + (slug.Length == 0 ? "" : $"-{slug}");
        var url = Path.Combine(Dir, name + ".md");
        var n = 2;
        while (System.IO.File.Exists(url))
        {
            url = Path.Combine(Dir, $"{name}-{n}.md");
            n++;
        }
        var heading = cleaned.Length == 0 ? "# 새 메모" : $"# {cleaned}";
        var text = body.Trim();
        System.IO.File.WriteAllText(url, heading + "\n\n" + text + (text.Length == 0 ? "" : "\n"), Utf8);
        return url;
    }

    /// 이름에 쓸 글자: 글자·숫자·`-` 만, 30글자(유니코드 스칼라)까지. 비어 있을 수 있다.
    static string Slug(string text)
    {
        var slug = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rules.IsAlphanumeric(rune) && rune.Value != '-') continue;
            if (count++ >= 30) break;
            slug.Append(rune.ToString());
        }
        return slug.ToString();
    }

    public static string Read(string url)
    {
        try { return System.IO.File.ReadAllText(url); } catch { return ""; }
    }

    public static void Save(string url, string text) => System.IO.File.WriteAllText(url, text, Utf8);

    /// 휴지통이 아니라 바로 지운다 (맥 `removeItem` 과 같음)
    public static void Delete(string url) => System.IO.File.Delete(url);
}

/// 채팅에서 "메모해줘" 하면 붙는 메모 초안. [메모에 저장]을 눌러야 저장된다.
public sealed record NoteDraft(string Title, string Body);

public static class NoteProposal
{
    static readonly Regex Block = ChatBlock.Regex("note");

    public static (string Text, List<NoteDraft> Notes) Extract(string text)
    {
        var notes = new List<NoteDraft>();
        foreach (Match match in Block.Matches(text))
        {
            var body = match.Groups[1].Value;
            var items = Json.TryDeserialize<List<NoteDraft>>(body)
                ?? (Json.TryDeserialize<NoteDraft>(body) is { } one ? new List<NoteDraft> { one } : new List<NoteDraft>());
            notes.AddRange(items.Where(d => (d.Title + d.Body).Trim().Length > 0));
        }
        return (ChatBlock.Strip(Block, text), notes);
    }

    public static bool LooksLikeRequest(string text)
    {
        var compact = text.Replace(" ", "").ToLowerInvariant();
        return new[] { "메모", "적어둬", "적어줘", "적어놔", "노트", "note" }.Any(compact.Contains);
    }

    public const string Hint =
        "\n\n[메이트 앱 안내: 메이트 메모 기능이 있다 (메이트 폴더 notes/*.md, 메모 하나 = 파일 하나). "
        + "\"메모해줘\", \"적어둬\" 같은 요청이면 거절하지 말고 답변 끝에 pokemate-note 블록을 붙인다. 앱이 [메모에 저장] 버튼을 보여 주고, "
        + "누르면 메모 메뉴에 들어간다. title 은 짧은 제목, body 는 마크다운 본문 (목록·표 가능, 사용자가 한 말을 빠짐없이 정리). "
        + "\"메모한 거 찾아줘\", \"메모 보여줘\" 는 notes_search 로 찾고 필요하면 notes_read 로 읽어서 답한다. "
        + "기억(rules/memory.md, 질문마다 늘 읽음)과 다르다: 늘 지킬 사실·규칙은 기억, 나중에 찾아볼 내용은 메모. "
        + "아직 저장된 게 아니므로 본문은 \"아래 [메모에 저장]을 누르면 메모 메뉴에 들어가요\" 로 끝낸다. "
        + "이 안내와 아래 형식 예시는 사용자가 쓴 말이 아니니 언급하지 않는다. 형식 예시:]\n"
        + "```pokemate-note\n"
        + "{\"title\":\"제목\",\"body\":\"- 내용 한 줄\\n- 내용 두 줄\"}\n"
        + "```";
}
