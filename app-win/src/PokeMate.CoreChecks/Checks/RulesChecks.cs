using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Rules, Memory, 설정 층 (shared·local)
public static class RulesChecks
{
    public static void Run()
    {
        using var repo = new Check.TempRepo();

        // Rules
        Check.That(Rules.SafeName("Wiki 규칙.md") == "wiki규칙" && Rules.SafeName("!!") == "memory", "rules: 파일 이름 정리");
        var jiraFile = new Rules.File(Path.Combine(Rules.SkillsDir, "jira.md"));
        var memoryFile = new Rules.File(Path.Combine(Rules.MyDir, "memory.md"));
        var root = new Rules.File(Rules.ClaudeMd);
        Check.That(jiraFile.Name == "skills/jira.md" && jiraFile.Kind == Rules.Kind.Skill && memoryFile.Name == "rules/memory.md" && memoryFile.Kind == Rules.Kind.Rule
                   && root.Name == "CLAUDE.md" && root.Kind == Rules.Kind.Root, "rules: root·rules·skills 구분");
        Check.That(!Rules.Prompt("아무 질문").Contains("## CLAUDE.md"), "rules: CLAUDE.md 는 Claude Code 가 읽으므로 안 붙임");
        var jira = Rules.Info.Parse("skills/jira.md", "---\ndescription: Jira 이슈 등록·worklog\nkeywords: jira, 지라, work log\n---\n# Jira 규칙\n- 프로젝트 키: X");
        var wiki = Rules.Info.Parse("skills/wiki.md", "# 위키 검색 규칙\n- 공간은 PRICE");
        var memory = Rules.Info.Parse("rules/memory.md", "# memory\n- 팀은 가격팀");
        Check.That(jira.Description == "Jira 이슈 등록·worklog" && jira.Body == "# Jira 규칙\n- 프로젝트 키: X", "rules: 머리말 읽기");
        Check.That(wiki.Description == "위키 검색 규칙" && wiki.Keywords.SequenceEqual(new[] { "wiki" }), "rules: 머리말 없으면 제목·파일 이름");
        Check.That(jira.Matches("회의 WorkLog 10-11") && jira.Matches("지라 등록해줘") && !jira.Matches("오늘 메일 요약"), "rules: 키워드로 판단");
        var p = Rules.Render(new[] { memory }, new[] { jira, wiki }, "지라 등록해줘");
        Check.That(p.Contains("- skills/wiki.md: 위키 검색 규칙") && p.Contains("## skills/jira.md\n# Jira 규칙") && !p.Contains("공간은 PRICE")
                   && p.Contains("## rules/memory.md\n# memory\n- 팀은 가격팀") && p.Contains("read_skill"), "rules: rules 는 늘, skills 는 목록 + 맞는 본문");
        var unrelated = Rules.Render(new[] { memory }, new[] { jira, wiki }, "오늘 메일 요약");
        Check.That(!unrelated.Contains("프로젝트 키") && unrelated.Contains("팀은 가격팀"), "rules: 상관없는 질문엔 스킬 본문 없음, 규칙은 있음");
        var reply = "알겠어요.\n```pokemate-rule\n[{\"file\":\"jira\",\"text\":\"제목 앞에 [회의]\"},{\"file\":\"memory\",\"text\":\" \"}]\n```";
        var (text, notes) = RuleProposal.Extract(reply);
        Check.That(text == "알겠어요." && notes.SequenceEqual(new[] { new RuleNote("jira", "제목 앞에 [회의]") }), "rules: 제안 읽기 (빈 줄은 버림)");
        Check.That(RuleProposal.Extract("```pokemate-rule\n{\"file\":\"memory\",\"text\":\"팀은 가격팀\"}\n```").Notes.Count == 1, "rules: 한 개짜리도 읽음");
        Check.That(RuleProposal.Extract(RuleProposal.Hint).Notes.Count == 2, "rules: 안내 예시가 읽힘");
        Check.That(RuleProposal.LooksLikeRequest("앞으로 지라 제목 앞에 [회의] 붙여줘") && !RuleProposal.LooksLikeRequest("내 미완료 Jira"), "rules: 요청 판단");
        const string old = """{"assistantName":"옛 이름","repoDir":"C:\\r","claudePath":"C:\\c","uvPath":"C:\\u","reportHour":10,"reportMinute":0}""";
        Check.That(Json.TryDeserialize<AppSettings>(old)?.OwnerName == "", "rules: 예전 설정은 주인 이름 없음 (처음 인사)");
        var forget = RuleProposal.Extract("```pokemate-rule\n[{\"file\":\"memory\",\"text\":\"가격팀\",\"forget\":true}]\n```").Notes;
        Check.That(forget.Count == 1 && forget[0].IsForget && forget[0].IsMemory, "memory: 잊기 제안 읽기");
        Check.That(RuleProposal.Extract(RuleProposal.Hint).Notes[0].IsMemory, "memory: 안내 예시의 기억이 읽힘");
        Check.That(RuleProposal.LooksLikeRequest("그거 잊어줘") && RuleProposal.LooksLikeRequest("내 이름 기억하고 있어?"), "memory: 잊기·기억 질문도 안내");

        // 파일을 만들고 더하기 (임시 저장소의 local\)
        var created = Rules.Create("Wiki 규칙", Rules.Kind.Skill);
        var skillText = File.ReadAllText(created);
        Check.That(created == Path.Combine(repo.Local, "skills", "wiki규칙.md") && skillText.StartsWith("---\ndescription: wiki규칙 관련 안내 (한 줄 설명)\nkeywords: wiki규칙\n---\n# wiki규칙"),
                   "rules: 새 스킬은 머리말 틀을 넣어 줌");
        Check.That(Rules.Create("Wiki 규칙", Rules.Kind.Skill) == created, "rules: 이미 있으면 그 파일");
        var appended = Rules.Append("  한 줄  ", "wiki규칙");
        Check.That(appended == created && File.ReadAllText(created).EndsWith("- 한 줄\n"), "rules: 같은 이름의 스킬이 있으면 스킬에 한 줄 추가");
        var inRules = Rules.Append("기억", "새주제");
        Check.That(inRules == Path.Combine(repo.Local, "rules", "새주제.md") && File.ReadAllText(inRules) == "# 새주제\n\n- 기억\n", "rules: 없으면 rules\\ 에 만들어 추가");
        Check.That(new[] { Path.GetDirectoryName(created)!, Path.GetDirectoryName(inRules)! }.SelectMany(Directory.GetFiles).All(f => !Path.GetFileName(f).Contains(".tmp-", StringComparison.Ordinal))
                   && File.ReadAllBytes(inRules)[0] == (byte)'#', "rules: 원자적으로 저장 (임시 파일 남지 않음, BOM 없음)");
        Check.That(Rules.Files().Select(f => f.Name).Contains("skills/wiki규칙.md") && Rules.Files().Select(f => f.Name).Contains("rules/새주제.md"), "rules: 파일 목록");
        Check.That(Rules.Prompt("wiki규칙 알려줘").Contains("## skills/wiki규칙.md"), "rules: 만든 스킬이 질문의 키워드와 맞으면 본문이 붙음");
        using (var none = new Check.TempRepo(withLocal: false))
        {
            var failed = false;
            try { Rules.Create("x", Rules.Kind.Rule); } catch (Rules.LocalMissingException) { failed = true; }
            Check.That(failed && !Directory.Exists(none.Local), "rules: local 이 없으면 만들지 않음");
        }

        // 설정 층 (shared·local)
        using (var layers = new Check.TempRepo(withLocal: false))
        {
            var alarmsFile = Path.Combine(layers.Path, "alarms.json");
            var cfg = AppSettings.Default;
            cfg.ReportHour = 9;
            Check.That(AlarmStore.Load(cfg, alarmsFile, ready: false).Count == 0, "layers: local 이 없으면 알람은 빈 목록(기본 알람도 안 만듦)");
            var one = new Alarm("t", "", 9, 0, new Alarm.Repeat.Daily());
            AlarmStore.Save(new[] { one }, alarmsFile, ready: false);
            Check.That(!File.Exists(alarmsFile), "layers: local 이 없으면 저장하지 않음");
            Check.That(AlarmStore.Save(new[] { one }, alarmsFile, ready: true) && AlarmStore.Load(cfg, alarmsFile, ready: true).Select(a => a.Title).SequenceEqual(new[] { "t" }),
                       "layers: local 이 있으면 저장·읽기");
            var saved = File.ReadAllText(alarmsFile);
            Check.That(saved.IndexOf("\"enabled\"", StringComparison.Ordinal) < saved.IndexOf("\"hour\"", StringComparison.Ordinal)
                       && saved.IndexOf("\"hour\"", StringComparison.Ordinal) < saved.IndexOf("\"title\"", StringComparison.Ordinal), "layers: 알람 파일은 키 정렬");
            File.WriteAllText(alarmsFile, "{ 깨진");
            var fallback = AlarmStore.Load(cfg, alarmsFile, ready: true);
            Check.That(fallback.Count == 1 && fallback[0].Title == "미완료 Jira 보고" && fallback[0].Hour == 9, "layers: 알람 파일이 깨졌으면 기본 보고 알람");

            var shared = Path.Combine(layers.Path, "s2");
            var local = Path.Combine(layers.Path, "l2");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(shared, "CLAUDE.md"), "일반 지시");
            Check.That(Rules.Instructions(shared, local).EndsWith("일반 지시"), "layers: local CLAUDE.md 가 없으면 shared 만");
            File.WriteAllText(Path.Combine(local, "CLAUDE.md"), "회사 지시");
            Check.That(Rules.Instructions(shared, local).Contains("일반 지시\n\n회사 지시"), "layers: CLAUDE.md 는 shared 다음 local 순서로 붙임");

            var sj = new Rules.File(Path.Combine(AppPaths.SharedDir, "skills", "jira.md"));
            var sh = new Rules.File(Path.Combine(AppPaths.SharedDir, "skills", "hr.md"));
            var lj = new Rules.File(Path.Combine(AppPaths.LocalDir, "skills", "jira.md"));
            var lc = new Rules.File(Path.Combine(AppPaths.LocalDir, "CLAUDE.md"));
            var sc = new Rules.File(Path.Combine(AppPaths.SharedDir, "CLAUDE.md"));
            var all = new[] { sc, sj, sh, lc, lj };
            Check.That(sj.Layer == Rules.Layer.Shared && lj.Layer == Rules.Layer.Local && Rules.Overridden(all).SetEquals(new[] { sj.Id })
                       && Rules.Effective(all).Select(f => f.Id).SequenceEqual(new[] { sc.Id, sh.Id, lc.Id, lj.Id }), "layers: 같은 이름이면 local 이 이긴다, CLAUDE.md 는 둘 다");
        }
    }
}
