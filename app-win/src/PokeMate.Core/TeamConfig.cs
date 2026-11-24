using System.Text.Json.Nodes;

namespace PokeMate.Core;

/// 팀 하나의 지식 저장소 정보. `Kb` 는 팀 KB 경로, `RepoMap` 은 `{domain}`·`{repo}` 가 든 레포 설명 경로 패턴.
public sealed record TeamEntry(string Kb, string? RepoMap, List<string> Domains);

/// `local/teams.json`. 내 팀(MyTeams) 순서대로 도메인 후보를 만든다.
public sealed record TeamConfig(List<string> MyTeams, string? RepoGuide, Dictionary<string, TeamEntry> Teams)
{
    public static string File => AppPaths.LocalFile("teams.json");

    /// 객체가 아니면 null. `kb` 가 빈 팀은 뺀다. `repoMap` 이 쓸 수 없는 패턴(`UsableRepoMap`)이면 null.
    public static TeamConfig? Parse(string? json)
    {
        if (Json.ParseObject(json) is not JsonObject obj) return null;
        static List<string> Items(JsonNode? node) =>
            (Json.Strings(node) ?? new List<string>()).Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();
        static string? Text(JsonNode? node) => Json.Str(node) is string s && SwiftText.TrimSpaces(s) is { Length: > 0 } t ? t : null;

        var teams = new Dictionary<string, TeamEntry>();
        if (obj["teams"] is JsonObject all)
        {
            foreach (var (name, value) in all)
            {
                var key = SwiftText.TrimSpaces(name);
                if (key.Length == 0 || value is not JsonObject body) continue;
                if (Text(body["kb"]) is not string kb) continue;
                var repoMap = Text(body["repoMap"]);
                if (repoMap != null && !UsableRepoMap(repoMap)) repoMap = null;
                teams[key] = new TeamEntry(kb, repoMap, Items(body["domains"]));
            }
        }
        return new TeamConfig(Items(obj["myTeams"]), Text(obj["repoGuide"]), teams);
    }

    /// `{domain}`·`{repo}` 가 다 있고, `{domain}` 이 마지막 경로 조각(문서 이름)에 없어야 한다 (`{domain}` 은 폴더 자리).
    public static bool UsableRepoMap(string pattern)
    {
        if (!pattern.Contains("{domain}", StringComparison.Ordinal) || !pattern.Contains("{repo}", StringComparison.Ordinal)) return false;
        var parts = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        return parts.Count > 0 && !parts[^1].Contains("{domain}", StringComparison.Ordinal);
    }

    /// 파일이 없거나 읽을 수 없으면 null
    public static TeamConfig? Load(string? from = null)
    {
        try { return Parse(Paths.ReadShared(from ?? File)); }
        catch { return null; }
    }

    /// `MyTeams` 순서대로, `Teams` 에 있는 팀만
    public List<(string Team, TeamEntry Entry)> Mine() =>
        MyTeams.Where(Teams.ContainsKey).Select(t => (t, Teams[t])).ToList();

    /// 내 팀 도메인의 합집합(순서 유지, 중복 제거). `map.Domains` 에 없는 도메인은 뺀다. 비면 `map.Domains` 전체(이름순).
    public List<string> Candidates(DomainMap map)
    {
        var list = Mine().SelectMany(m => m.Entry.Domains).Where(map.Domains.ContainsKey).Distinct().ToList();
        return list.Count > 0 ? list : map.Domains.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// 그 도메인을 가진 첫 팀 (`Mine()` 순서)
    public string? TeamOf(string domain) =>
        Mine().FirstOrDefault(m => m.Entry.Domains.Contains(domain)).Team;
}
