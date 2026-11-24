using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 도메인 리뷰어 (도메인 정하기·리뷰어 세션 안내·공유 노트 폴더). 팀 KB 는 임시 폴더에 만든다.
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
        var inRepo = new ReviewTarget("acme", "ACME-Sample-Service", 1, "u");
        Check.That(DomainReview.Listed(inRepo, map) is { Domain: "acme", Source: DomainReview.Source.Listed } listed && listed.Why == "domains.json 의 acme repos 에 ACME-Sample-Service 가 있음",
                   "review: domains.json repos (대소문자 무시)");
        var other = new ReviewTarget("acme", "sample-web", 2, "u");
        Check.That(DomainReview.Listed(other, map) == null, "review: repos 에 없는 레포는 null");
        Check.That(DomainReview.Candidates(map, null).SequenceEqual(new[] { "acme", "beta" }), "review: 팀 설정이 없으면 domains.json 전체가 후보 (이름순, default 제외)");
        var chooser = DomainReview.ChoosePrompt(other, map, null, new RouteHints(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, int>()), "H");
        Check.That(chooser.Contains("- acme · 쿠폰·할인 · 키워드: 쿠폰, coupon") && chooser.Contains("- default ·") && !chooser.Contains("(acme 팀)")
                   && chooser.Contains("H gh-get repos/acme/sample-web/pulls/2/files") && !chooser.Contains("gh-get repos/example-org/repo-guide")
                   && chooser.Contains("레포 이름만으로 고르지 말고") && chooser.Contains("```pokemate-domain"), "review: 도메인 정하기 안내에 목록·설명·키워드 (팀 없음, 파일 목록 없으면 조회 명령)");
        var allCands = DomainReview.Candidates(map, null);
        var chosen = DomainReview.Extract("답\n```pokemate-domain\n{\"domain\": \"acme\", \"why\": \"쿠폰함\"}\n```", allCands);
        Check.That(chosen is { Domain: "acme", Why: "쿠폰함" }
                   && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"nope\"}\n```", allCands) == null
                   && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"default\"}\n```", allCands)?.Domain == "default"
                   && DomainReview.Extract("```pokemate-domain\r\n{\"domain\": \"acme\"}\r\n```", allCands) is { Why: "이유 없음" },
                   "review: 도메인 블록 읽기 (없는 도메인은 거절, default 는 됨)");

        // 노트 폴더 안의 점검표·노트는 항상 / 구분 경로로 안내한다 (Git Bash·Claude 가 그대로 읽는다)
        var present = new Dictionary<string, int> { ["C:/n/acme/checklist.md"] = 20, ["C:/n/acme/notes.md"] = 12 };
        var notes = new DomainNotes(@"C:\n", lines: p => present.TryGetValue(p, out var n) ? n : null);
        var empty = new DomainNotes(@"C:\n", lines: _ => null);
        var first = DomainReview.ReviewPrompt(other, "acme", "쿠폰함", DomainReview.Source.Chosen, true, empty, "H", "본문");
        var again = DomainReview.ReviewPrompt(other, "acme", "쿠폰함", DomainReview.Source.Listed, false, notes, "H", "본문");
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
        Check.That(first.Contains("도메인 acme (PR 판단: 쿠폰함)") && again.Contains("도메인 acme (domains.json 의 repos: 쿠폰함)")
                   && first.Contains("로컬 레포 폴더(~/Workspace/sample-web)"), "review: 도메인을 정한 근거를 안내에 적음");
        Check.That(DomainReview.Source.TeamMap.Raw() == "팀 KB 지도" && DomainReview.Source.Listed.Raw() == "domains.json 의 repos" && DomainReview.Source.PathRule.Raw() == "경로 규칙"
                   && DomainReview.Source.Chosen.Raw() == "PR 판단" && DomainReview.Source.Fallback.Raw() == "기본값", "route: 단계 이름");

        // 도메인 정하기 1·2단계: 임시 홈 아래 팀 KB (`{홈}\Workspace\kb\pricing-kb`)
        var tmp = Check.TempDir("pm-route");
        var kbRaw = Path.Combine(tmp, "Workspace", "kb", "pricing-kb", "raw");
        void Doc(string relative)
        {
            var path = Path.Combine(kbRaw, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "# 레포");
        }
        Doc("coupon/repos/Sample.Web.md");
        Doc("common/repos/shared-admin.md");
        var team = new TeamConfig(new List<string> { "pricing", "product" }, null, new Dictionary<string, TeamEntry>
        {
            ["pricing"] = new("kb/pricing-kb", "raw/{domain}/repos/{repo}.md", new List<string> { "coupon", "discount" }),
            ["product"] = new("kb/product-kb", null, new List<string> { "product" }),
        });
        map = DomainMap.Parse("""{"coupon": {"description": "쿠폰", "keywords": ["coupon"]}, "discount": {}, "product": {"repos": ["sample-item-api"], "description": "상품"}}""");
        var rules = PathRules.Parse("""{"example-org/shared-admin": [{"path": "src/Areas/Coupon/**", "domain": "coupon"}, {"path": "src/Areas/Discount/**", "domain": "discount"}, {"path": "src/Areas/Item/**", "domain": "product"}]}""");
        var hit = DomainReview.Route(new ReviewTarget("acme", "sample.web", 1, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp);
        Check.That(hit.Decided is { Domain: "coupon", Source: DomainReview.Source.TeamMap } && hit.Decided.Value.Why.Contains("raw/coupon/repos"), "route: 팀 KB 지도 (대소문자 무시)");
        Check.That(hit.Decided?.Why == "pricing 팀 KB raw/coupon/repos/Sample.Web.md", "route: 팀 KB 지도 근거는 팀 KB 안의 상대 경로");
        Check.That(DomainReview.Route(new ReviewTarget("acme", "sample-item-api", 2, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp).Decided?.Source == DomainReview.Source.Listed, "route: 지도 없는 팀은 domains.json repos");
        var shared = DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 3, "u"), map, team, rules, new[] { "src/Areas/Coupon/A.cs", "src/Areas/Item/B.cs" }, tmp);
        Check.That(shared.Decided == null && shared.Hints.SharedDocs.Single().EndsWith("raw/common/repos/shared-admin.md", StringComparison.OrdinalIgnoreCase)
                   && shared.Hints.RuleCounts["coupon"] == 1 && shared.Hints.RuleCounts["product"] == 1, "route: 공용 문서는 읽을거리, 경로 규칙 둘이면 3단계로");
        var byRule = DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 4, "u"), map, team, rules, new[] { "src/Areas/Item/B.cs" }, tmp);
        Check.That(byRule.Decided is { Domain: "product", Source: DomainReview.Source.PathRule }, "route: 경로 규칙 한 도메인이면 확정");
        Check.That(byRule.Decided?.Why == "src/Areas/Item 아래 1개 파일"
                   && DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 4, "u"), map, team, rules, new[] { "src/Areas/Coupon/A.cs", "src/Areas/Coupon/B/C.cs", "README.md" }, tmp).Decided?.Why == "src/Areas/Coupon 아래 2개 파일",
                   "route: 경로 규칙 근거는 규칙 경로의 와일드카드 앞 폴더와 파일 수");
        var loose = PathRules.Parse("""{"example-org/shared-admin": [{"path": "Coupon*/**", "domain": "coupon"}, {"path": "**/Item.cs", "domain": "product"}]}""");
        Check.That(DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 4, "u"), map, team, loose, new[] { "CouponA/x.cs", "CouponB/y/z.cs" }, tmp).Decided?.Why == "Coupon*/** 에 맞는 파일 2개"
                   && DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 4, "u"), map, team, loose, new[] { "src/Item.cs" }, tmp).Decided?.Why == "**/Item.cs 에 맞는 파일 1개",
                   "route: 와일드카드 앞 폴더가 없는 규칙의 근거는 glob 그대로");
        var unusable = team with { Teams = new Dictionary<string, TeamEntry> { ["pricing"] = new("kb/pricing-kb", "raw/{domain}/repos/{repo}.md", new List<string> { "legacy" }) } };
        Check.That(DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 9, "u"), map, unusable, PathRules.Parse("{}"), new[] { "a.cs" }, tmp) is { Decided: null } skipped
                   && skipped.Hints.SharedDocs.Count == 0 && skipped.Hints.MapDomains.Count == 0,
                   "route: 쓸 수 있는 팀 도메인이 없으면 팀 KB 폴더를 훑지 않음");
        Check.That(MateCLI.ParseFileList("a.cs\r\n\r\n b.cs \nc.cs\na.cs\n").SequenceEqual(new[] { "a.cs", "b.cs", "c.cs" }) && MateCLI.ParseFileList("").Count == 0, "review: 바뀐 파일 목록 읽기 (여러 쪽·빈 줄·CRLF)");
        // sample-web 이 coupon·discount 두 폴더에 다 있는 경우 (파일 추가 후)
        Doc("coupon/repos/sample-web.md");
        Doc("discount/repos/sample-web.md");
        Check.That(DomainReview.Route(new ReviewTarget("acme", "sample-web", 5, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp) is { Decided: null } r && r.Hints.MapDomains.SequenceEqual(new[] { "coupon", "discount" }), "route: 지도 두 도메인이면 미정");
        Check.That(DomainReview.Route(new ReviewTarget("acme", "sample.web", 6, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp + @"\nope").Decided == null, "route: 팀 KB 폴더가 없으면 1단계 건너뜀 (예외 없음)");
        Check.That(DomainReview.Route(new ReviewTarget("acme", "sample-item-api", 7, "u"), map, null, PathRules.Parse("{}"), new[] { "a.cs" }, tmp).Decided?.Source == DomainReview.Source.Listed
                   && DomainReview.Route(new ReviewTarget("acme", "sample.web", 7, "u"), map, null, PathRules.Parse("{}"), new[] { "a.cs" }, tmp).Decided == null,
                   "route: 팀 설정이 없으면 팀 KB 지도는 건너뛰고 domains.json repos 만");
        Check.That(DomainReview.Route(new ReviewTarget("acme", "sample-item-api", 8, "u"), map, team with { MyTeams = new List<string> { "pricing" } }, PathRules.Parse("{}"), new[] { "a.cs" }, tmp).Decided == null,
                   "route: 후보가 아닌 도메인의 repos 는 1단계에 쓰지 않음");
        var cands = DomainReview.Candidates(map, team);
        Check.That(DomainReview.Extract("```pokemate-domain\n{\"domain\": \"product\", \"why\": \"상품 화면\"}\n```", cands) is { Domain: "product" }
                   && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"legacy\"}\n```", cands) == null
                   && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"default\"}\n```", cands)?.Domain == "default", "route: 3단계 답은 후보·default 만");
        Check.That(DomainReview.Decide("```pokemate-domain\n{\"domain\": \"product\", \"why\": \"상품 화면\"}\n```", cands) == ("product", "상품 화면", DomainReview.Source.Chosen)
                   && DomainReview.Decide("```pokemate-domain\n{\"domain\": \"default\", \"why\": \"맞는 도메인 없음\"}\n```", cands) == ("default", "맞는 도메인 없음", DomainReview.Source.Fallback)
                   && DomainReview.Decide("```pokemate-domain\n{\"domain\": \"default\"}\n```", cands) == ("default", "도메인을 정하지 못해 default 로 리뷰", DomainReview.Source.Fallback)
                   && DomainReview.Decide("```pokemate-domain\n{\"domain\": \"legacy\"}\n```", cands) == ("default", "도메인을 정하지 못해 default 로 리뷰", DomainReview.Source.Fallback)
                   && DomainReview.Decide(null, cands) == ("default", "도메인을 정하지 못해 default 로 리뷰", DomainReview.Source.Fallback),
                   "route: 3단계 답 판정 (후보 Chosen, default·못 읽음·실패는 Fallback, 이유 없는 default 는 정하지 못함)");
        var routeChooser = DomainReview.ChoosePrompt(new ReviewTarget("example-org", "shared-admin", 3, "u"), map, team with { RepoGuide = "example-org/repo-guide" }, shared.Hints, "H");
        Check.That(routeChooser.Contains("- product (product 팀)") && routeChooser.Contains("H gh-get repos/example-org/repo-guide/contents/maps/example-org/shared-admin.md")
                   && routeChooser.Contains("경로 규칙 후보: coupon 1개, product 1개") && routeChooser.Contains("shared-admin.md") && routeChooser.Contains("default"), "route: 3단계 안내에 후보·팀·레포 지도·공용 문서·규칙 후보");
        Check.That(routeChooser.Contains("- coupon (pricing 팀) · 쿠폰 · 키워드: coupon") && routeChooser.Contains("src/Areas/Item/B.cs") && !routeChooser.Contains("pulls/3/files")
                   && !routeChooser.Contains("\\"), "route: 3단계 안내에 바뀐 파일 목록, 경로는 / 구분");
        var many = DomainReview.ChoosePrompt(new ReviewTarget("acme", "sample-web", 5, "u"), map, team,
                                             new RouteHints(Enumerable.Range(1, 203).Select(i => $"f{i}.cs").ToList(), Array.Empty<string>(), new[] { "coupon", "discount" }, new Dictionary<string, int>()), "H");
        Check.That(many.Contains("f200.cs") && !many.Contains("f201.cs") && many.Contains("외 3개") && many.Contains("팀 KB 지도 후보: coupon, discount"), "route: 바뀐 파일은 200개까지, 지도 후보");
        var body = DomainReview.ReviewPrompt(new ReviewTarget("acme", "sample.web", 1, "u"), "coupon", "pricing 팀 KB raw/coupon/repos/Sample.Web.md", DomainReview.Source.TeamMap, true, notes, "H", "본문");
        Check.That(body.Contains("도메인: coupon — 팀 KB 지도: pricing 팀 KB raw/coupon/repos/Sample.Web.md"), "route: 확인한 범위 첫 줄 형식");
        Check.Remove(tmp);

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

        Check.That(DomainReviewPaths(), "review: 도메인 지도 파일은 local\\domains.json, 세션·파이프라인은 데이터 폴더의 automation 아래");
    }

    static bool DomainReviewPaths() =>
        DomainMap.File == AppPaths.LocalFile("domains.json")
        && WorkSessionBook.File == Path.Combine(AppPaths.DataDir, "automation", "sessions.json")
        && PipelineState.Dir() == Path.Combine(AppPaths.DataDir, "automation", "pipelines")
        && PipelineAccess.File == AppPaths.LocalFile("pipeline-tools.json");
}
