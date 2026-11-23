using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 로컬 시스템 이름
public static class LocalNamesChecks
{
    /// 이 저장소의 루트 (검사 실행 파일 위로 올라가며 shared\connections.example.json 을 찾는다)
    static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "shared", "connections.example.json"))) return dir.FullName;
        return null;
    }

    public static void Run()
    {
        const string json = """[{"id":"hr","name":"ACME-HR","aliases":["근태판"," "],"group":"ad","enabled":true},{"id":"permit","name":"Acme Permit","group":"ad","enabled":true},{"id":"jira","name":"Jira","group":"ad","enabled":true},{"kind":"settings","id":"automation","worklogCalendarTitle":"Acme 캘린더","worklogAliases":["아크미달력"]}]""";
        var names = LocalNames.Parse(json);
        Check.That(names.Entries.Select(e => e.Id).SequenceEqual(new[] { "hr", "permit", "jira" }) && names.Name("hr") == "ACME-HR" && names.Name("x") == null, "local: 연결 이름 읽기 (설정 줄은 빼고)");
        Check.That(names.Entries[0].Aliases.SequenceEqual(new[] { "근태판" }) && names.WorklogWords.SequenceEqual(new[] { "Acme 캘린더", "아크미달력" }), "local: 별칭·worklog 캘린더 이름 (빈 값 버림)");
        var line = names.PromptLine(new[] { "mcp__pokemate__hr_holidays", "mcp__pokemate__jira_search" });
        Check.That(line.Contains("\"ACME-HR\", \"근태판\" → hr (hr_ 로 시작하는 도구)") && line.Contains("\"Acme Permit\" → permit (조회 도구 없음")
                   && !line.Contains("\"Jira\"") && line.Contains("\"Acme 캘린더\", \"아크미달력\" → worklog 캘린더"), "local: 프롬프트에 부르는 이름 → 도구 안내 (같은 이름은 생략)");
        Check.That(new LocalNames().PromptLine().Length == 0, "local: 로컬 이름이 없으면 안내도 없음");
        Check.That(LocalNames.Parse("not json") == new LocalNames(), "local: 깨진 파일은 빈 값");
        Check.That(WorklogProposal.LooksLikeRequest("아크미 달력이랑 비교", new[] { "아크미달력" }) && !WorklogProposal.LooksLikeRequest("내일 회의실 잡아줘", Array.Empty<string>()),
                   "local: worklog 캘린더 로컬 이름으로도 worklog 안내");
        var repo = RepoRoot();
        var example = repo == null ? new LocalNames() : LocalNames.Parse(File.ReadAllText(Path.Combine(repo, "shared", "connections.example.json")));
        Check.That(repo != null && example.Name("hr") == "HR" && example.Entries.First(e => e.Id == "hr").Aliases.SequenceEqual(new[] { "근태 시스템" }) && example.WorklogWords.SequenceEqual(new[] { "근무 캘린더" }),
                   "local: 예시 연결 파일 읽기");

        // 파일 고르기: local\connections.json → shared\connections.local.json → shared\connections.example.json
        var dir = Check.TempDir("pm-local");
        Directory.CreateDirectory(Path.Combine(dir, "shared"));
        Directory.CreateDirectory(Path.Combine(dir, "local"));
        Check.That(LocalNames.File(dir) == Path.Combine(dir, "shared", "connections.example.json") && LocalNames.Load(dir).Entries.Count == 0, "local: 파일이 없으면 예시 파일 이름, 읽을 수 없으면 빈 값");
        File.WriteAllText(Path.Combine(dir, "shared", "connections.local.json"), "[{\"id\":\"a\",\"name\":\"A\"}]");
        File.WriteAllText(Path.Combine(dir, "local", "connections.json"), "[{\"id\":\"b\",\"name\":\"B\"}]");
        Check.That(LocalNames.Load(dir).Name("b") == "B", "local: local\\connections.json 이 먼저");
        File.Delete(Path.Combine(dir, "local", "connections.json"));
        Check.That(LocalNames.Load(dir).Name("a") == "A", "local: 없으면 shared\\connections.local.json");
        Check.Remove(dir);
    }
}
