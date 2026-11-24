using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PokeMate.Core;

/// 경로 규칙 하나. `Path` 는 glob, `Domain` 은 그 경로가 속한 도메인.
public sealed record PathRule(string Path, string Domain);

/// 경로 규칙 판정 결과. `Counts` 는 도메인 → 그 도메인 규칙에 처음 맞은 파일 수. `Domain` 은 센 도메인이 정확히 하나일 때만 채운다.
public sealed record PathVerdict(string? Domain, IReadOnlyDictionary<string, int> Counts);

/// `local/path-rules.json`. 여러 도메인이 한 저장소에 섞인 곳에서 바뀐 파일 경로로 도메인을 가른다.
public sealed record PathRules(Dictionary<string, List<PathRule>> Repos)
{
    public static string File => AppPaths.LocalFile("path-rules.json");

    /// 객체가 아니면 빈 규칙. `path`·`domain` 이 빈 항목은 뺀다. 저장소 키는 대소문자 무시.
    public static PathRules Parse(string? json)
    {
        var repos = new Dictionary<string, List<PathRule>>(StringComparer.OrdinalIgnoreCase);
        if (Json.ParseObject(json) is not JsonObject obj) return new PathRules(repos);
        foreach (var (name, value) in obj)
        {
            var key = SwiftText.TrimSpaces(name);
            if (key.Length == 0 || value is not JsonArray items) continue;
            var list = new List<PathRule>();
            foreach (var item in items)
            {
                if (item is not JsonObject body) continue;
                var path = Json.Str(body["path"]) is string p ? SwiftText.TrimSpaces(p) : "";
                var domain = Json.Str(body["domain"]) is string d ? SwiftText.TrimSpaces(d) : "";
                if (path.Length > 0 && domain.Length > 0) list.Add(new PathRule(path, domain));
            }
            if (list.Count > 0) repos[key] = list;
        }
        return new PathRules(repos);
    }

    /// 파일이 없거나 읽을 수 없으면 빈 규칙
    public static PathRules Load(string? from = null)
    {
        try { return Parse(Paths.ReadShared(from ?? File)); }
        catch { return Parse(null); }
    }

    /// `\` → `/`, 앞의 `./`·`/` 제거
    static string Clean(string s)
    {
        s = s.Replace('\\', '/');
        while (true)
        {
            if (s.StartsWith("./", StringComparison.Ordinal)) s = s.Substring(2);
            else if (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1);
            else return s;
        }
    }

    /// glob 을 정규식으로 바꿔 대소문자 무시로 비교. `**/` 는 `(.*/)?`, `**` 는 `.*`, `*` 는 `[^/]*`, `?` 는 `[^/]`.
    public static bool Matches(string glob, string path)
    {
        var g = Clean(glob);
        var sb = new StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                if (i + 2 < g.Length && g[i + 2] == '/') { sb.Append("(.*/)?"); i += 2; }
                else { sb.Append(".*"); i += 1; }
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return Regex.IsMatch(Clean(path), sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// 후보가 아닌 도메인 규칙은 없는 것으로 본다. 파일마다 처음 맞는 규칙의 도메인을 센다. 센 도메인이 정확히 하나면 `Domain` 에 넣는다.
    public PathVerdict Judge(string fullName, IReadOnlyList<string> files, IReadOnlyCollection<string> candidates)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Repos.TryGetValue(fullName, out var all))
        {
            var rules = all.Where(r => candidates.Contains(r.Domain)).ToList();
            foreach (var file in files)
            {
                var hit = rules.FirstOrDefault(r => Matches(r.Path, file));
                if (hit != null) counts[hit.Domain] = counts.GetValueOrDefault(hit.Domain) + 1;
            }
        }
        return new PathVerdict(counts.Count == 1 ? counts.Keys.First() : null, counts);
    }
}
