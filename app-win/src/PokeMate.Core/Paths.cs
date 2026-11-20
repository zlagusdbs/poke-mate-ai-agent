using System.Globalization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// Windows 경로 도우미. 맥의 `NSHomeDirectory()`·`expandingTildeInPath` 자리.
public static class Paths
{
    /// `%USERPROFILE%`
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// `~`, `~/x`, `~\x` 를 홈 폴더로 푼다. 맥에서 복사한 `local/` 파일의 `~/` 경로도 여기서 풀린다.
    public static string ExpandTilde(string path, string? home = null)
    {
        home ??= Home;
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, path[2..].Replace('/', '\\'));
        return path;
    }

    /// 다른 프로세스(파이썬 서버)가 쓰는 중이어도 읽는다 (쓰기·삭제 공유). 읽을 수 없으면 예외.
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    static readonly Regex Drive = new(@"^([A-Za-z]):[\\/]?(.*)$", RegexOptions.Singleline);

    /// Claude Code 권한 규칙(`Edit(...)`)에 쓰는 절대 경로 형식.
    /// `C:\Users\x\f.json` → `//c/Users/x/f.json` (Claude Code 2.1.328 에서 deny 가 걸리는 것을 확인함).
    /// 드라이브 글자가 없는 경로(`/d`)는 맥과 같이 앞에 `/` 를 하나 더 붙인다.
    public static string ClaudePattern(string windowsPath)
    {
        var m = Drive.Match(windowsPath);
        if (m.Success)
        {
            var rest = m.Groups[2].Value.Replace('\\', '/');
            return "//" + char.ToLowerInvariant(m.Groups[1].Value[0]) + (rest.Length == 0 ? "" : "/" + rest);
        }
        return "/" + windowsPath.Replace('\\', '/');
    }

    /// 앞으로 쓰는 `/` 경로 (glob 패턴용): `C:\Users\u` → `C:/Users/u`
    public static string ForwardSlashes(string path) => path.Replace('\\', '/');

    /// Windows 절대 경로인지 (`C:\…`, `C:/…`, `\\server\…`).
    public static bool IsWindowsAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);
}

/// Swift `String.count`·`prefix`·`suffix` 처럼 글자(문자소) 단위로 센다.
internal static class SwiftText
{
    public static int Count(string s) => new StringInfo(s).LengthInTextElements;

    public static string Prefix(string s, int n)
    {
        var info = new StringInfo(s);
        return n >= info.LengthInTextElements ? s : info.SubstringByTextElements(0, Math.Max(0, n));
    }

    public static string Suffix(string s, int n)
    {
        var info = new StringInfo(s);
        var len = info.LengthInTextElements;
        return n >= len ? s : info.SubstringByTextElements(len - Math.Max(0, n));
    }

    /// 글자(문자소) 목록 (Swift `for ch in string`)
    public static IEnumerable<string> Elements(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) yield return (string)e.Current;
    }

    /// Swift `.newlines` 다듬기 (U+000A–000D, U+0085, U+2028, U+2029)
    public static string TrimNewlines(string s) => s.Trim('\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029');

    /// Swift `.whitespacesAndNewlines` 다듬기
    public static string Trim(string s) => s.Trim();

    /// Swift `.whitespaces` 다듬기 (줄바꿈은 남김)
    public static string TrimSpaces(string s) => s.Trim(' ', '\t', '\u00A0', '\u3000');

    /// Windows 출력의 CRLF 를 LF 로.
    public static string Lf(string s) => s.Replace("\r\n", "\n");
}
