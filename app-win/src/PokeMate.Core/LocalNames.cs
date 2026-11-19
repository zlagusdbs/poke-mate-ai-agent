using System.Text.Json.Nodes;

namespace PokeMate.Core;

/// 사내 시스템을 사용자가 부르는 이름 (connections 파일의 name·aliases). ClaudeRunner 가 시스템 안내에 붙인다.
public sealed record LocalNames
{
    public sealed record Entry(string Id, string Name, IReadOnlyList<string> Aliases)
    {
        public bool Equals(Entry? o) => o is not null && Id == o.Id && Name == o.Name && Aliases.SequenceEqual(o.Aliases);
        public override int GetHashCode() => HashCode.Combine(Id, Name);
    }

    public List<Entry> Entries { get; set; } = new();
    public List<string> WorklogWords { get; set; } = new();

    public LocalNames() { }

    public LocalNames(IEnumerable<Entry> entries, IEnumerable<string> worklogWords)
    {
        Entries = entries.ToList();
        WorklogWords = worklogWords.ToList();
    }

    public bool Equals(LocalNames? o) => o is not null && Entries.SequenceEqual(o.Entries) && WorklogWords.SequenceEqual(o.WorklogWords);
    public override int GetHashCode() => HashCode.Combine(Entries.Count, WorklogWords.Count);

    public static LocalNames Parse(string json)
    {
        if (Json.ParseArray(json) is not JsonArray rows || rows.Any(r => r is not JsonObject)) return new LocalNames();
        var names = new LocalNames();
        foreach (var row in rows.OfType<JsonObject>())
        {
            List<string> Strings(string key) => (Json.Strings(row[key]) ?? new List<string>()).Select(SwiftText.TrimSpaces).Where(s => s.Length > 0).ToList();
            if (Json.Str(row["kind"]) == "settings")
            {
                if (Json.Str(row["id"]) == "automation")
                {
                    var title = SwiftText.TrimSpaces(Json.Str(row["worklogCalendarTitle"]) ?? "");
                    names.WorklogWords = (title.Length == 0 ? new List<string>() : new List<string> { title }).Concat(Strings("worklogAliases")).ToList();
                }
                continue;
            }
            if (Json.Str(row["id"]) is not string id) continue;
            names.Entries.Add(new Entry(id, Json.Str(row["name"]) ?? id, Strings("aliases")));
        }
        return names;
    }

    public static string File(string repoDir)
    {
        var shared = Path.Combine(repoDir, "shared");
        var candidates = new[] { Path.Combine(repoDir, "local", "connections.json"), Path.Combine(shared, "connections.local.json") };
        return candidates.FirstOrDefault(System.IO.File.Exists) ?? Path.Combine(shared, "connections.example.json");
    }

    public static LocalNames Load(string repoDir)
    {
        try { return Parse(System.IO.File.ReadAllText(File(repoDir))); }
        catch { return new LocalNames(); }
    }

    static (string RepoDir, LocalNames Names)? cached;
    static readonly object Gate = new();

    public static LocalNames Current
    {
        get
        {
            var repoDir = AppPaths.LoadSettings().RepoDir;
            lock (Gate)
            {
                if (cached is { } c && c.RepoDir == repoDir) return c.Names;
                var names = Load(repoDir);
                cached = (repoDir, names);
                return names;
            }
        }
    }

    public string? Name(string id) => Entries.FirstOrDefault(e => e.Id == id)?.Name;

    public static string Compact(string text) => text.Replace(" ", "").ToLowerInvariant();

    public string PromptLine(IReadOnlyList<string>? tools = null)
    {
        tools ??= ClaudeCommand.AllowedTools;
        var lines = new List<string>();
        foreach (var entry in Entries)
        {
            var spoken = new[] { entry.Name }.Concat(entry.Aliases).Where(s => Compact(s) != Compact(entry.Id)).ToList();
            if (spoken.Count == 0) continue;
            var prefix = $"mcp__pokemate__{entry.Id}_";
            var owned = tools.Any(t => t.StartsWith(prefix, StringComparison.Ordinal));
            var target = owned ? $"{entry.Id} ({entry.Id}_ 로 시작하는 도구)" : $"{entry.Id} (조회 도구 없음, 연결 화면의 이름)";
            lines.Add("  - " + string.Join(", ", spoken.Select(s => $"\"{s}\"")) + " → " + target);
        }
        if (WorklogWords.Count > 0)
            lines.Add("  - " + string.Join(", ", WorklogWords.Select(s => $"\"{s}\"")) + " → worklog 캘린더 (work_day_check, jira_my_worklogs)");
        if (lines.Count == 0) return "";
        return "\n- 사내 시스템 이름: 사용자는 시스템을 아래 이름으로 부른다. 그렇게 부르면 오른쪽 연결의 도구를 쓰고, 답할 때도 사용자가 부른 이름으로 말한다.\n"
            + string.Join("\n", lines) + "\n";
    }
}
