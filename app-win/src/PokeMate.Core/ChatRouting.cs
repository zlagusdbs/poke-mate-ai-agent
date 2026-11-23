using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 사이드바의 칸. 맥의 `SidebarItem` (`rawValue` 는 `Raw()`).
public enum SidebarItem { Chat, Alarms, Work, Notes, Collection, Connections, Settings }

/// 맥의 `SidebarItem` 의 정적 멤버. C# 의 enum 에는 정적 멤버를 못 붙여서 여기 둔다.
public static class SidebarItemExtensions
{
    public static IReadOnlyList<SidebarItem> AllCases { get; } = Enum.GetValues<SidebarItem>();

    public static IReadOnlyList<SidebarItem> Tabs { get; } = new[] { SidebarItem.Chat, SidebarItem.Alarms, SidebarItem.Work, SidebarItem.Notes, SidebarItem.Collection };
    public static IReadOnlyList<SidebarItem> Tools { get; } = new[] { SidebarItem.Connections, SidebarItem.Settings };
    public static SidebarItem WorkSessionsTab => SidebarItem.Work;

    /// 알림·메뉴가 "열어 달라"고 한 곳 (`open` 값)에 맞는 칸
    public static SidebarItem Target(string? open) => open == "workSessions" ? WorkSessionsTab : SidebarItem.Chat;

    public static string Raw(this SidebarItem item) => item switch
    {
        SidebarItem.Chat => "chat",
        SidebarItem.Alarms => "alarms",
        SidebarItem.Work => "work",
        SidebarItem.Notes => "notes",
        SidebarItem.Collection => "collection",
        SidebarItem.Connections => "connections",
        _ => "settings",
    };

    /// 맥의 SF Symbol 이름. Windows 화면은 이 이름을 자기 아이콘에 맞춘다.
    public static string Symbol(this SidebarItem item) => item switch
    {
        SidebarItem.Chat => "house",
        SidebarItem.Notes => "note.text",
        SidebarItem.Connections => "powerplug",
        SidebarItem.Alarms => "bell",
        SidebarItem.Work => "rectangle.stack",
        SidebarItem.Collection => "square.grid.3x3",
        _ => "gearshape",
    };

    public static string Title(this SidebarItem item) => item switch
    {
        SidebarItem.Chat => "홈",
        SidebarItem.Notes => "메모",
        SidebarItem.Connections => "연결",
        SidebarItem.Alarms => "알람",
        SidebarItem.Work => "작업",
        SidebarItem.Collection => "컬렉션",
        _ => "설정",
    };
}

/// 홈 대화의 보낼 곳. 레포를 고르면 그 레포의 작업 세션으로, 없으면 메이트에게. 저장 키는 repo·mateNotes (맥과 같음).
public sealed record ChatRouting
{
    [JsonInclude] public string? Repo { get; private set; }
    [JsonInclude] public List<string> MateNotes { get; private set; } = new();

    public ChatRouting() { }

    public ChatRouting(string? repo) => Repo = repo;

    [JsonIgnore] public bool IsMate => Repo == null;

    public void Choose(string? repo)
    {
        var trimmed = repo == null ? null : SwiftText.Trim(repo);
        Repo = string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public void Reset()
    {
        Repo = null;
        MateNotes = new List<string>();
    }

    /// 첫 줄(비어 있지 않은) 한 줄 요약. `limit` 글자를 넘으면 자르고 …
    public static string Summary(string text, int limit = 60)
    {
        var first = text.Split(new[] { '\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029 }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var line = first == null ? "" : SwiftText.TrimSpaces(first);
        return SwiftText.Count(line) > limit ? SwiftText.Prefix(line, limit) + "…" : line;
    }

    public void NoteSent(string repo, string text) => MateNotes.Add($"작업 세션 {repo} 로 보냄: {Summary(text)}");

    /// 다음 메이트 메시지 앞에 붙일 [앱 기록]. 한 번 꺼내면 비운다.
    public string TakeMatePrefix()
    {
        if (MateNotes.Count == 0) return "";
        var prefix = "[앱 기록] 아래는 사용자가 이 대화에서 작업 세션으로 직접 보낸 것이다. 너에게 온 말이 아니고, 답은 그 작업 세션이 했다.\n"
                     + string.Join("\n", MateNotes.Select(n => $"- {n}")) + "\n\n";
        MateNotes = new List<string>();
        return prefix;
    }

    public bool Equals(ChatRouting? o) => o is not null && Repo == o.Repo && MateNotes.SequenceEqual(o.MateNotes);

    public override int GetHashCode() => HashCode.Combine(Repo, MateNotes.Count);
}

/// 메이트 답 끝의 "이건 이 레포 작업 세션에 물어보세요" 제안 블록.
public static class RouteSuggestion
{
    public const string Tag = "pokemate-route-suggest";

    static readonly Regex Block = new(@"\n?```" + Tag + @"[ \t]*\r?\n(.*?)```[ \t]*", RegexOptions.Singleline);

    /// 블록을 본문에서 지우고 레포 이름을 읽는다. 경로가 섞인 이름(`/`·`\`)·빈 이름·JSON 아님은 버린다.
    public static (string Text, string? Repo) Extract(string text)
    {
        var m = Block.Match(text);
        if (!m.Success) return (text, null);
        var repo = Json.Str(Json.ParseObject(m.Groups[1].Value)?["repo"]);
        repo = repo == null ? null : SwiftText.Trim(repo);
        var clean = SwiftText.Trim(text.Remove(m.Index, m.Length));
        if (string.IsNullOrEmpty(repo) || repo.Contains('/') || repo.Contains('\\')) return (clean, null);
        return (clean, repo);
    }
}

/// 작업 폴더(`%USERPROFILE%\Workspace`) 아래의 git 레포.
public static class WorkspaceRepos
{
    public static string Root => Path.Combine(Paths.Home, "Workspace");

    /// `.git` 이 있는 폴더(worktree 는 `.git` 이 파일)만, 이름순(숫자는 크기순). 숨김 폴더는 뺀다. 폴더가 없으면 빈 목록.
    public static List<string> List(string? root = null)
    {
        try
        {
            var repos = new DirectoryInfo(root ?? Root).EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') && (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Where(d => Directory.Exists(Path.Combine(d.FullName, ".git")) || System.IO.File.Exists(Path.Combine(d.FullName, ".git")))
                .Select(d => d.Name)
                .ToList();
            repos.Sort(NaturalCompare);
            return repos;
        }
        catch { return new List<string>(); }
    }

    /// 새 작업 세션의 첫 메시지 앞에 붙이는 지시
    public static string Bootstrap(string repo, string? root = null)
    {
        if (repo == WorkSessionBook.Operations)
            return "이 작업 세션은 레포가 없는 운영 건 전용이다. 이번이 이 세션의 첫 메시지다. 아래 요청을 처리하고, 이후 이 세션에서 이미 읽은 것은 다시 읽지 않는다.\n\n";
        var path = Path.Combine(root ?? Root, repo);
        return $"이 작업 세션은 레포 {repo} ({path}) 전용이다. 이번이 이 세션의 첫 메시지라 먼저 지식을 모은다: 레포 루트 CLAUDE.md 와 그 안에서 가리키는 문서, README, 빌드·테스트 방법, 최근 git log(20개 안팎). 읽은 것을 짧게 요약한 뒤 아래 요청을 처리한다. 이후 이 세션에서는 이미 읽은 것을 다시 읽지 않는다.\n\n";
    }

    /// Swift `localizedStandardCompare` 자리: 대소문자 무시, 숫자 덩어리는 크기로.
    static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] is >= '0' and <= '9' && b[j] is >= '0' and <= '9')
            {
                int si = i, sj = j;
                while (i < a.Length && a[i] is >= '0' and <= '9') i++;
                while (j < b.Length && b[j] is >= '0' and <= '9') j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length < nb.Length ? -1 : 1;
                var c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var x = char.ToUpperInvariant(a[i]);
                var y = char.ToUpperInvariant(b[j]);
                if (x != y) return x < y ? -1 : 1;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
