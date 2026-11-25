using System.Text.Json.Nodes;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 도메인 리뷰어 — 리뷰 기준 문제 묶음 (eval)
public static class ReviewEvalChecks
{
    /// 이 저장소의 루트 (검사 실행 파일 위로 올라가며 shared\eval\fixtures 를 찾는다)
    static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "shared", "eval", "fixtures"))) return dir.FullName;
        return null;
    }

    public static void Run()
    {
        var raw = """[{"id": "sample-web-280", "url": "https://git.example.com/acme/sample-web/pull/280", "domain": "acme", "mustFind": []}, {"id": "", "url": "https://git.example.com/acme/x/pull/1"}, {"id": "bad", "url": "nope"}, {"id": "sample-docs-1", "url": "https://git.example.com/acme/sample-docs/pull/1"}]""";
        var cases = ReviewEval.LoadCases(raw);
        Check.That(cases.Select(c => c.Id).SequenceEqual(new[] { "sample-web-280", "sample-docs-1" }) && cases[0].Domain == "acme" && cases[1].Domain == null, "eval: 문제 묶음 읽기 (id·주소 없는 것은 뺌)");
        Check.That(ReviewEval.LoadCases("not json").Count == 0 && ReviewEval.LoadCases("{}").Count == 0, "eval: 깨진 문제 묶음은 빈 목록");
        var access = ReviewEval.Access("H", new[] { @"C:\w", @"C:\n", @"C:\w" });
        Check.That(access.Dirs.SequenceEqual(new[] { @"C:\w", @"C:\n" }) && access.Tools.Contains("Bash(H gh-diff:*)") && access.Tools.Contains("Skill(domain-review)") && access.Tools.Contains("Skill(code-review)")
                   && !access.Tools.Any(t => t.Contains("gh-review") || t.Contains("event") || t.Contains("domain-note") || t.Contains("write-note") || t.Contains("review-state") || t.StartsWith("Edit(") || t.StartsWith("Write("))
                   && ReviewEval.Environment(ReviewTarget.Parse(cases[0].Url)!).OrderBy(kv => kv.Key).SequenceEqual(new Dictionary<string, string> { ["POKEMATE_EVAL"] = "1", ["POKEMATE_REVIEW_REPO"] = "acme/sample-web" }.OrderBy(kv => kv.Key)),
                   "eval: 읽기 명령과 스킬만 허용, 쓰기 막는 환경");
        var emap = DomainMap.Parse("""{"acme": {"repos": ["sample-docs"]}}""");
        var target = ReviewTarget.Parse(cases[0].Url)!;
        Check.That(ReviewEval.Domain(cases[0], target, emap).Domain == "acme"
                   && ReviewEval.Domain(cases[1], ReviewTarget.Parse(cases[1].Url)!, emap).Source == DomainReview.Source.Listed
                   && ReviewEval.Domain(new ReviewEvalCase("x", cases[0].Url, "nope"), target, emap) is { Domain: "default", Source: DomainReview.Source.Fallback },
                   "eval: 도메인은 문제에 적은 것 → domains.json repos → default");
        Check.That(ReviewEval.Domain(cases[0], target, emap) is { Why: "평가 문제에 정한 도메인", Source: DomainReview.Source.Listed }, "eval: 문제에 정한 도메인은 정해진 도메인(Listed), PR 판단 아님");
        var ep = ReviewEval.Prompt(cases[0], target, "acme", "평가", DomainReview.Source.Chosen, new DomainNotes(@"C:\n", lines: _ => 3),
                                   "H", "리뷰 {{helper}} gh-diff\n{{knowledge}}", "   - `kb`");
        Check.That(ep.Contains("리뷰 H gh-diff") && ep.Contains("   - `kb`") && !ep.Contains("{{") && ep.Contains("```pokemate-eval") && ep.Contains("링크: " + cases[0].Url)
                   && ep.Contains("세션 acme:reviewer") && ep.Contains("review-state 저장"), "eval: 실제 리뷰와 같은 프롬프트 + 평가 끝맺음");
        var transcript = string.Join("\n",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Skill","input":{"skill":"domain-review"}}]}}""",
            """{"type":"user","message":{"content":"x"}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"a"},{"type":"tool_use","name":"Skill","input":{"skill":"code-review","args":"high"}},{"type":"tool_use","name":"Bash","input":{}}]}}""");
        Check.That(ReviewEval.SkillsUsed(transcript).SequenceEqual(new[] { "domain-review", "code-review" })
                   && ReviewEval.SkillsUsed(transcript.Replace("\n", "\r\n")).Count == 2, "eval: 기록에서 부른 스킬 찾기 (스킬 안에서 부른 것 포함)");
        var fixtureCases = ReviewEval.LoadCases("""[{"id": "acme-coupon-boundary", "fixture": "acme-coupon-boundary"}, {"id": "bad", "fixture": "../x"}, {"id": "dot", "fixture": ".x"}, {"id": "win", "fixture": "..\\x"}, {"id": "drive", "fixture": "C:\\x"}]""");
        Check.That(fixtureCases.Select(c => c.Id).SequenceEqual(new[] { "acme-coupon-boundary" }) && fixtureCases[0].Fixture == "acme-coupon-boundary" && fixtureCases[0].Url.Length == 0,
                   "eval: 가상 레포 문제 읽기 (경로 넘는 이름 거절)");
        Check.That(ReviewEval.LoadCases("""[{"id": "w", "fixture": "a\\b"}]""").Count == 0, "eval: 역슬래시가 든 가상 레포 문제 이름도 거절 (Windows 경로)");

        var repoRoot = RepoRoot();
        var fixtureDir = repoRoot == null ? null : ReviewEval.FixtureDir("acme-coupon-boundary", Path.Combine(repoRoot, "no-local"), Path.Combine(repoRoot, "shared"));
        var fixture = fixtureDir == null ? null : ReviewEval.LoadFixture(fixtureDir);
        Check.That(fixture?.Key == "ACME-201" && fixture.Repo == "acme-sample-coupon" && fixture.Brief.Contains("이상이면 할인")
                   && fixtureDir != null && File.Exists(Path.Combine(fixtureDir, "head", "coupon.py")), "eval: shared 의 가상 레포 문제 파일");
        Check.That(ReviewEval.FixtureDir("없는-문제", Path.Combine(repoRoot ?? @"C:\x", "no-local"), Path.Combine(repoRoot ?? @"C:\x", "shared")) == null, "eval: 없는 가상 레포 문제는 nil");
        if (fixture != null)
        {
            var fmap = DomainMap.Parse("""{"acme": {"repos": ["acme-sample-coupon"]}}""");
            var fstate = ReviewEval.FixtureState(fixture, ReviewEval.FixtureDomain(fixtureCases[0], fixture, fmap), @"C:\w\W", DateTimeOffset.Now);
            Check.That(fstate.Domain == "acme" && fstate.Stage == PipelineState.StageKind.Reviewer && fstate.TestsPassed && fstate.Worktree == @"C:\w\W" && fstate.Brief == fixture.Brief
                       && fstate.Allowed(PipelineRole.Reviewer).Contains(PipelineNext.NextKind.Pm), "eval: 가상 레포 문제는 리뷰어 단계 상태로 시작");
            var fconfig = new PipelineConfig(new Dictionary<PipelineRole, string> { [PipelineRole.Reviewer] = "리뷰어 지시" }, "공통", new Dictionary<PipelineRole, ClaudeCommand.WriteAccess>(), @"C:\w",
                                             notes: new DomainNotes(@"C:\n", lines: _ => 3, maps: _ => new List<string>()));
            var fprompt = JiraPipeline.Prompt(PipelineRole.Reviewer, fstate, fconfig, false, @"C:\w\ACME-201.diff") + "\n\n" + ReviewEval.PipelineTail;
            Check.That(fprompt.Contains("리뷰할 변경(diff 파일): C:/w/ACME-201.diff") && fprompt.Contains("## PM 개발 지시서") && fprompt.Contains("```pokemate-eval") && fprompt.Contains("domain-note 는 하지 않는다"),
                       "eval: 가상 레포 문제는 Jira 리뷰어 프롬프트 + 평가 끝맺음");
        }
        else Check.That(false, "eval: 가상 레포 문제 파일을 못 찾아 상태·프롬프트 검사를 못 함");
        var raccess = ReviewEval.ReviewerAccess("H", @"C:\w\W", new[] { @"C:\ws", @"C:\refs", @"C:\n" });
        Check.That(raccess.Tools.Contains("Skill(domain-review)") && raccess.Tools.Contains("Skill(code-review)") && raccess.Tools.Contains("Bash(H git-read:*)")
                   && !raccess.Tools.Any(t => t.Contains("domain-note") || t.Contains("domain-map") || t.StartsWith("Edit(") || t.StartsWith("Write("))
                   && new[] { @"C:\ws", @"C:\refs", @"C:\n", @"C:\w\W" }.All(raccess.Dirs.Contains), "eval: 가상 레포 리뷰어는 읽기·스킬만, 쓰기 없음");

        // 평가 결과 한 줄 (eval 폴더의 JSON): 키는 맥과 같고 토큰은 64비트
        var meta = ReviewEval.Meta(0.5, 12.5, new[] { "domain-review" }, "s1", new RunUsage(1, 2, 4_000_000_000, 4, 0.5), true, "acme", resumed: true, freshReason: "식음 61분 (기준 55분)", contextAfter: 4_000_000_001);
        Check.That(Json.Double(meta["costUSD"]) == 0.5 && Json.Double(meta["seconds"]) == 12.5 && Json.Strings(meta["skills"])!.SequenceEqual(new[] { "domain-review" }) && Json.Str(meta["sessionId"]) == "s1"
                   && Json.Bool(meta["ok"]) == true && Json.Str(meta["domain"]) == "acme" && Json.Bool(meta["resumed"]) == true && Json.Str(meta["freshReason"]) == "식음 61분 (기준 55분)"
                   && Json.Long(meta["contextAfter"]) == 4_000_000_001 && Json.Long(meta["cacheRead"]) == 4_000_000_000 && Json.Long(meta["input"]) == 1 && Json.Long(meta["output"]) == 2 && Json.Long(meta["cacheWrite"]) == 4,
                   "eval: 결과 한 줄 (비용·시간·스킬·세션·토큰)");
        var bare = ReviewEval.Meta(0, 1, Array.Empty<string>(), "s", null, false, "default");
        Check.That(!bare.ContainsKey("freshReason") && !bare.ContainsKey("contextAfter") && !bare.ContainsKey("input") && Json.Bool(bare["resumed"]) == false, "eval: 값이 없으면 결과 한 줄에 키를 넣지 않음");

        // 기록 폴더에서 세션 기록 찾기 (projects\<폴더>\<세션 id>.jsonl)
        var config = Check.TempDir("pm-claude-config");
        Directory.CreateDirectory(Path.Combine(config, "projects", "C--Users-홍 길동-work"));
        File.WriteAllText(Path.Combine(config, "projects", "C--Users-홍 길동-work", "abc.jsonl"), "{}");
        File.WriteAllText(Path.Combine(config, "projects", "note.txt"), "x");
        Check.That(ReviewEval.TranscriptFile(config, "abc") == Path.Combine(config, "projects", "C--Users-홍 길동-work", "abc.jsonl") && ReviewEval.TranscriptFile(config, "none") == null
                   && ReviewEval.TranscriptFile(Path.Combine(config, "없음"), "abc") == null, "eval: 세션 id 로 대화 기록 파일 찾기 (한글·공백 폴더 이름)");
        Check.Remove(config);
        Check.That(ReviewEval.NewLines("a\r\nb\r\nc", 2) == "c" && ReviewEval.NewLines("a\nb\nc", 5) == "" && ReviewEval.NewLines("a\nb\nc", 0) == "a\nb\nc", "eval: 이번 실행에 생긴 줄만 (CRLF 포함)");

        // 문제 묶음 JSON 왕복 (맥과 같은 키)
        var round = Json.Deserialize<ReviewEvalCase>(Json.Serialize(new ReviewEvalCase("a", "https://git.example.com/x/y/pull/1", "acme")));
        Check.That(round == new ReviewEvalCase("a", "https://git.example.com/x/y/pull/1", "acme") && Json.Serialize(new ReviewEvalCase("a", "u")).Contains("\"id\"") && !Json.Serialize(new ReviewEvalCase("a", "u")).Contains("domain"),
                   "eval: 문제 한 건 JSON 왕복 (없는 값은 키 생략)");
    }
}
