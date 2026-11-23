using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 도메인 리뷰어 (도메인 정하기·리뷰어 세션 안내·공유 노트 폴더). review-domains.json 은 임시 폴더에 쓴다.
public static class DomainReviewChecks
{
    public static async Task Run()
    {
        var target = ReviewTarget.Parse("https://git.example.com/acme/sample-web/pull/280");
        Check.That(target?.FullName == "acme/sample-web" && target.Number == 280 && target.Label == "sample-web#280"
                   && ReviewTarget.Parse("https://git.example.com/acme/sample-web/issues/3") == null, "review: PR 주소 읽기");
        Check.That(ReviewTarget.Parse("https://git.example.com/acme/sample-web/pull/280/files?x=1#a") is { Number: 280, Repo: "sample-web" }
                   && ReviewTarget.Parse("nope") == null && ReviewTarget.Parse("https://git.example.com/acme/pull/1") == null
                   && ReviewTarget.Parse("https://git.example.com/acme/sample-web/pull/abc") == null, "review: PR 주소 읽기 — 뒤에 더 붙은 경로·주소 아님·숫자 아님");
        var map = DomainMap.Parse("""{"acme": {"repos": ["acme-sample-service"], "description": "쿠폰·할인", "keywords": ["쿠폰", "coupon"], "knowledge": ["acme-kb"]}, "beta": {"repos": ["beta-api"]}, "default": {"knowledge": ["sample-notes"]}}""");
        Check.That(map.Descriptions["acme"] == "쿠폰·할인" && map.Keywords["acme"].SequenceEqual(new[] { "쿠폰", "coupon" }) && !map.Keywords.ContainsKey("beta"), "review: 도메인 설명·키워드 읽기");
        var inRepo = new ReviewTarget("acme", "acme-sample-service", 1, "u");
        var explicitHit = DomainReview.Known(inRepo, map, new ReviewDomainBook { Repos = { ["acme/acme-sample-service"] = new ReviewDomainChoice("beta", "x", DateTimeOffset.Now) } });
        Check.That(explicitHit?.Domain == "acme" && explicitHit.Value.Source == DomainReview.Source.Explicit, "review: domains.json repos 가 예전 기록보다 이김");
        var other = new ReviewTarget("acme", "sample-web", 2, "u");
        var book = new ReviewDomainBook();
        Check.That(DomainReview.Known(other, map, book) == null, "review: 처음 보는 레포는 도메인 정하기로");
        book.Repos[other.FullName] = new ReviewDomainChoice("acme", "쿠폰함 화면", DateTimeOffset.Now);
        Check.That(DomainReview.Known(other, map, book)?.Source == DomainReview.Source.Learned
                   && DomainReview.Known(other, map, new ReviewDomainBook { Repos = { [other.FullName] = new ReviewDomainChoice("gone", "", DateTimeOffset.Now) } }) == null,
                   "review: 정해 둔 기록을 쓰고, 없어진 도메인 기록은 무시");
        var chooser = DomainReview.ChoosePrompt(other, map, "H");
        Check.That(chooser.Contains("- acme · 쿠폰·할인 · 키워드: 쿠폰, coupon · 레포: acme-sample-service") && chooser.Contains("- default ·")
                   && chooser.Contains("H gh-get repos/acme/sample-web/pulls/2/files"), "review: 도메인 정하기 안내에 목록·설명·키워드");
        var chosen = DomainReview.Extract("답\n```pokemate-domain\n{\"domain\": \"acme\", \"why\": \"쿠폰함\"}\n```", map);
        Check.That(chosen is { Domain: "acme", Why: "쿠폰함" }
                   && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"nope\"}\n```", map) == null
                   && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"default\"}\n```", map)?.Domain == "default"
                   && DomainReview.Extract("```pokemate-domain\r\n{\"domain\": \"acme\"}\r\n```", map) is { Why: "이유 없음" },
                   "review: 도메인 블록 읽기 (없는 도메인은 거절, default 는 됨)");

        // 노트 폴더 안의 점검표·노트는 항상 / 구분 경로로 안내한다 (Git Bash·Claude 가 그대로 읽는다)
        var present = new Dictionary<string, int> { ["C:/n/acme/checklist.md"] = 20, ["C:/n/acme/notes.md"] = 12 };
        var notes = new DomainNotes(@"C:\n", lines: p => present.TryGetValue(p, out var n) ? n : null);
        var empty = new DomainNotes(@"C:\n", lines: _ => null);
        var first = DomainReview.ReviewPrompt(other, "acme", "쿠폰함", DomainReview.Source.Chosen, true, empty, "H", "본문");
        var again = DomainReview.ReviewPrompt(other, "acme", "쿠폰함", DomainReview.Source.Learned, false, notes, "H", "본문");
        var unsure = DomainReview.ReviewPrompt(other, "default", "못 정함", DomainReview.Source.Chosen, true, notes, "H", "본문");
        var handed = DomainReview.ReviewPrompt(other, "acme", "쿠폰함", DomainReview.Source.Chosen, true, notes, "H", "본문", "## 이번 건 인계\n- 리뷰어: 앞 리뷰");
        var resumedWithBlock = DomainReview.ReviewPrompt(other, "acme", "쿠폰함", DomainReview.Source.Chosen, false, notes, "H", "본문", "## 이번 건 인계\n- 리뷰어: 앞 리뷰");
        Check.That(handed.Contains("## 이번 건 인계\n- 리뷰어: 앞 리뷰") && handed.IndexOf("## 이번 건 인계", StringComparison.Ordinal) < handed.IndexOf("도메인 노트", StringComparison.Ordinal)
                   && !resumedWithBlock.Contains("## 이번 건 인계") && !first.Contains("## 이번 건 인계"),
                   "review: 새 세션이면 인계 블록을 안내 앞쪽에, 이어 쓰는 세션·처음에는 없음");
        Check.That(DomainReview.SessionKey("acme") == "acme:reviewer"
                   && first.Contains("세션 acme:reviewer") && first.Contains("PR 마다 따로") && first.Contains("새 세션이니") && first.Contains("H domain-note --domain acme --init") && first.EndsWith("본문")
                   && first.Contains("`C:/n/acme/checklist.md` (없음)")
                   && again.Contains("새로 필요한 것만") && again.Contains("`C:/n/acme/checklist.md` (20줄)") && again.Contains("`C:/n/acme/notes.md` (12줄)")
                   && !again.Contains("--init") && again.Contains("H gh-file --repo acme/sample-web")
                   && again.Contains("H domain-note --domain acme --kind {용어집|업무 규칙|장애|함정|결정|동작} --source \"sample-web#2\"") && again.Contains("직접 고치지 않는다")
                   && unsure.Contains("도메인 미정, 점검표 없음") && !unsure.Contains("domain-note")
                   && !notes.PromptLines("acme", "H", "ACME-1", canWrite: false).Contains("domain-note")
                   && notes.PromptLines("acme", "H", "ACME-1", canWrite: false).Contains("읽기만"),
                   "review: 리뷰어 세션 안내 (새 세션·이어 쓰기·공유 노트·init·gh-file·domain-note·default)");
        Check.That(first.Contains("도메인 acme (이번에 PR 내용으로 정함: 쿠폰함)") && again.Contains("도메인 acme (예전에 정한 기록: 쿠폰함)")
                   && first.Contains("로컬 레포 폴더(~/Workspace/sample-web)"), "review: 도메인을 정한 근거를 안내에 적음");

        var rows = """[{"kind":"settings","id":"automation","domainNotesDir":"~/kb/notes"},{"id":"jira"}]""";
        Check.That(DomainNotes.RootFromConnections(rows, @"C:\u") == @"C:\u\kb\notes" && DomainNotes.RootFromConnections(null, @"C:\u") == @"C:\u\.claude\references\notes"
                   && DomainNotes.RootFromConnections("""[{"kind":"settings","id":"automation","domainNotesDir":" "}]""", @"C:\u") == @"C:\u\.claude\references\notes"
                   && DomainNotes.RootFromConnections("""[{"kind":"settings","id":"automation","domainNotesDir":"D:\\kb"}]""", @"C:\u") == @"D:\kb"
                   && DomainNotes.RootFromConnections("not json", @"C:\u") == @"C:\u\.claude\references\notes",
                   "notes: 노트 폴더는 automation 설정 domainNotesDir, 없으면 ~/.claude/references/notes");
        var mapped = new DomainNotes(@"C:\n", lines: _ => 5, maps: f => f == "C:/n/acme/maps" ? Enumerable.Range(1, 22).Select(i => $"주제{i}").ToList() : new List<string>());
        var mapLine = mapped.PromptLines("acme", "H", "ACME-1");
        Check.That(mapLine.Contains("- 지도: `C:/n/acme/maps/` 아래 주제1, ") && mapLine.Contains(" 외 2개") && !mapLine.Contains("주제22,")
                   && !new DomainNotes(@"C:\n", lines: _ => 5, maps: _ => new List<string>()).PromptLines("acme", "H", "ACME-1").Contains("지도"),
                   "notes: 지도가 있으면 주제 목록(최대 20개)을 안내, 없으면 줄 없음");
        var mapBase = Check.TempDir("pokemate-maps");
        foreach (var name in new[] { "b.md", "a.md", ".hidden.md", "x.txt" }) File.WriteAllBytes(Path.Combine(mapBase, name), Array.Empty<byte>());
        Check.That(DomainNotes.ListMaps(mapBase).SequenceEqual(new[] { "a", "b" }) && DomainNotes.ListMaps(Path.Combine(mapBase, "없음")).Count == 0, "notes: 지도 목록은 .md 만, 숨김 파일 제외");
        Check.That(DomainNotes.CountLines(Path.Combine(mapBase, "a.md")) == 1 && DomainNotes.CountLines(Path.Combine(mapBase, "없음.md")) == null, "notes: 줄 수 (빈 파일 1줄, 없으면 nil)");
        File.WriteAllText(Path.Combine(mapBase, "c.md"), "한\r\n둘\r\n셋\r\n");
        Check.That(DomainNotes.CountLines(Path.Combine(mapBase, "c.md")) == 4, "notes: CRLF 파일도 줄 수는 같은 방식 (줄바꿈 수 + 1)");
        Check.Remove(mapBase);
        var mapDefaults = PipelineAccess.Defaults("h", @"C:\ws", @"C:\refs", @"C:\p");
        Check.That(mapDefaults[PipelineRole.Pm].Tools.Contains("Bash(h domain-map:*)") && !mapDefaults[PipelineRole.Reviewer].Tools.Contains("Bash(h domain-map:*)"), "notes: 지도는 PM 만 남긴다");

        // 노트 폴더가 링크면 실제 경로로 (Windows 는 디렉터리 정션: 관리자 권한이 필요 없다)
        var linkBase = Check.TempDir("pokemate-notes");
        var real = Path.Combine(linkBase, "real");
        var link = Path.Combine(linkBase, "link");
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        var made = await new ProcessRunner().Run("cmd.exe", new[] { "/c", "mklink", "/J", link, real }, timeout: TimeSpan.FromSeconds(30));
        Check.That(made.ExitCode == 0 && DomainNotes.Resolved(link).EndsWith(@"\real", StringComparison.OrdinalIgnoreCase)
                   && DomainNotes.Resolved(Path.Combine(link, "sub")).EndsWith(@"\real\sub", StringComparison.OrdinalIgnoreCase)
                   && DomainNotes.Resolved(Path.Combine(link, "none")).EndsWith(@"\real\none", StringComparison.OrdinalIgnoreCase)
                   && DomainNotes.Resolved(@"C:\n\none") == @"C:\n\none" && DomainNotes.Resolved(real) == real,
                   "notes: 노트 폴더가 링크(정션)면 실제 경로로 (Claude 권한은 실제 경로로 판단)");
        Check.Remove(linkBase);

        // review-domains.json: 맥과 같은 모양 (repos → {domain, why, at(ISO 8601)}), 키 정렬
        var dir = Check.TempDir("pm-review");
        var file = Path.Combine(dir, "automation", "state", "review-domains.json");
        var saved = new ReviewDomainBook { Repos = { ["acme/b"] = new ReviewDomainChoice("acme", "쿠폰함", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)), ["acme/a"] = new ReviewDomainChoice("beta", "x", DateTimeOffset.FromUnixTimeSeconds(1_800_000_060)) } };
        var ok = saved.Save(file);
        var text = File.ReadAllText(file);
        Check.That(ok && ReviewDomainBook.Load(file).Repos.Count == 2 && ReviewDomainBook.Load(file).Repos["acme/b"] == saved.Repos["acme/b"]
                   && text.Contains("\"at\": \"2027-01-15T08:00:00Z\"") && text.IndexOf("acme/a", StringComparison.Ordinal) < text.IndexOf("acme/b", StringComparison.Ordinal),
                   "review: review-domains.json 저장·읽기 (ISO 8601, 키 정렬)");
        File.WriteAllText(file, "{\"repos\":{\"acme/x\":{\"domain\":\"a");
        Check.That(ReviewDomainBook.Load(file).Repos.Count == 0 && ReviewDomainBook.Load(Path.Combine(dir, "없음.json")).Repos.Count == 0, "review: 깨진·없는 review-domains.json 은 빈 값");
        Check.Remove(dir);
        Check.That(DomainReviewPaths(), "review: 기록 파일은 데이터 폴더의 automation\\state 아래, 도메인 지도 파일은 local\\domains.json");
    }

    static bool DomainReviewPaths() =>
        ReviewDomainBook.File == Path.Combine(AppPaths.DataDir, "automation", "state", "review-domains.json")
        && DomainMap.File == AppPaths.LocalFile("domains.json")
        && WorkSessionBook.File == Path.Combine(AppPaths.DataDir, "automation", "sessions.json")
        && PipelineState.Dir() == Path.Combine(AppPaths.DataDir, "automation", "pipelines")
        && PipelineAccess.File == AppPaths.LocalFile("pipeline-tools.json");
}
