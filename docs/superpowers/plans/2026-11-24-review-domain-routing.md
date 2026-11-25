# 리뷰 도메인 정하기 (팀 KB 기준) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PR 리뷰 도메인을 "0 파일 조회 → 1 팀 KB 지도 → 2 경로 규칙 → 3 PR 판단(저장 안 함) → default" 순서로 PR 마다 정하고, 여러 팀(`myTeams`)을 함께 다룬다.

**Architecture:** 설정 읽기(`TeamConfig`, `PathRules`)와 판정(`DomainReview.Route`)은 화면과 무관한 Core 에 순수 함수로 둔다. 앱(`WorkSessionCenter.HandleReview`)은 파일 목록을 받아 `Route` 를 부르고, 못 정하면 Claude 로 3단계를 한다. 레포 단위 저장(`ReviewDomainBook`)은 없앤다. Windows(C#)와 macOS(Swift)는 같은 이름·같은 동작이다.

**Tech Stack:** C# .NET (app-win, `PokeMate.CoreChecks` 실행형 검증), Swift (app-mac, `PokeMateCoreChecks`), Python helper(`pokemate auto gh-get`, 바꾸지 않음).

**Spec:** `docs/superpowers/specs/2026-11-24-review-domain-routing-design.md`

## Global Constraints

- 공개 저장소다. 코드·주석·테스트·예시·커밋 메시지에 회사·사내 시스템·저장소 실제 이름을 쓰지 않는다. 테스트는 `acme`, `example-org`, `sample-*` 같은 이름만 쓴다.
- 커밋 메시지는 `feat:`/`fix:`/`refactor:`/`docs:` 짧은 한 줄만. 본문·Co-Authored-By 없음.
- 작업 브랜치는 `feature/v2-2-0/domain-routing` (worktree `C:\workspace\poke-mate-ai-agent-v2-2-0`). 끝나면 `release/v2-2-0` 으로 `--no-ff` merge. PR 은 만들지 않는다.
- 사용자 PC 의 라이브 checkout(`C:\workspace\poke-mate-ai-agent`)은 앱이 읽는 곳이라 브랜치를 바꾸지 않는다.
- hotfix PR(v2-1-1, 사내 호스트 NO_PROXY·UTF-8)이 main 에 들어가면 `release/v2-2-0` 에 main 을 merge 한다. 그 전에는 0단계 조회가 프록시 환경에서 실패할 수 있다(실패하면 default 로 가는 것이 설계대로의 동작).
- C# 검증: `dotnet run --project app-win/src/PokeMate.CoreChecks` 의 마지막 줄. 지금 기준 `failed: 3` (settings 3건, 이번 작업과 무관한 기존 실패). 작업 뒤에도 실패는 그 3건뿐이어야 한다.
- Swift 검증: `cd app-mac && swift run PokeMateCoreChecks`. 이 PC(Windows)에서는 못 돌린다. Mac 에서 돌려야 하며, 결과 보고에 "미실행" 으로 적는다.
- 두 앱의 판정 결과·문구가 같아야 한다(Source 이름, 근거 문구, glob 규칙).
- 근거 문구 형식(스펙 4-5): `도메인: {domain} — {단계 이름}: {근거}`. 단계 이름은 `팀 KB 지도` · `domains.json 의 repos` · `경로 규칙` · `PR 판단` · `기본값`.

## Review Focus

- PR 파일이 100개를 넘을 때: `--paginate` 출력이 여러 덩어리로 와도 파일 목록이 모두 모여야 한다 → Task 4 `ParseFileList` 테스트.
- 레포 이름 대소문자가 팀 KB 파일 이름과 다를 때(`Sample.Web` ↔ `sample.web.md`): 같은 레포로 찾아야 한다 → Task 3 TeamMap 테스트.
- 팀 KB 폴더가 이 PC 에 없을 때: 예외 없이 1단계를 건너뛰어야 한다 → Task 3 TeamMap 테스트.
- 경로가 `\` 구분이거나 `./` 로 시작할 때: glob 이 같은 결과여야 한다 → Task 2 `Matches` 테스트.
- 3단계 답이 후보가 아닌 도메인(예: 조직개편으로 빠진 팀 도메인)일 때: 받지 않고 default 로 가야 한다 → Task 3 `Extract` 테스트.

---

### Task 1: TeamConfig (`local/teams.json` 읽기)

**Files:**
- Create: `app-win/src/PokeMate.Core/TeamConfig.cs`
- Modify: `app-win/src/PokeMate.Core/JiraPipeline.cs` (`DomainMap.KnowledgePaths` 의 경로 해석을 `ResolvePath` 로 뽑기)
- Test: `app-win/src/PokeMate.CoreChecks/Checks/TeamConfigChecks.cs`, `Program.cs` 에 등록

**Interfaces:**
- Produces:
  - `public static string DomainMap.ResolvePath(string item, string home)` — 기존 `KnowledgePaths` 규칙 그대로(`~/`·`~\` 홈, 절대 경로 그대로, 나머지 `home\Workspace\` 아래). `KnowledgePaths` 는 이것을 쓴다.
  - `public sealed record TeamEntry(string Kb, string? RepoMap, List<string> Domains)`
  - `public sealed record TeamConfig(List<string> MyTeams, string? RepoGuide, Dictionary<string, TeamEntry> Teams)`
    - `public static string File => AppPaths.LocalFile("teams.json")`
    - `public static TeamConfig? Parse(string? json)` — 객체가 아니면 null. `teams` 의 항목 중 `kb` 가 비었으면 그 팀은 뺀다. `repoMap` 은 `{repo}` 가 없으면 null 로 본다. 문자열은 앞뒤 공백 제거.
    - `public static TeamConfig? Load(string? from = null)` — 없거나 읽기 실패면 null.
    - `public List<(string Team, TeamEntry Entry)> Mine()` — `MyTeams` 순서대로, `Teams` 에 있는 것만.
    - `public List<string> Candidates(DomainMap map)` — `Mine()` 의 도메인을 순서대로 합치고 중복 제거, `map.Domains` 에 없는 것은 뺀다. 결과가 비면 `map.Domains.Keys` 이름순.
    - `public string? TeamOf(string domain)` — `Mine()` 에서 그 도메인을 가진 첫 팀.

- [ ] **Step 1: 실패하는 검증 작성** (`TeamConfigChecks.Run`)

```csharp
var map = DomainMap.Parse("""{"coupon":{"repos":[]},"discount":{"repos":[]},"product":{"repos":["sample-item-api"]}}""");
var cfg = TeamConfig.Parse("""{"myTeams":["pricing","product","gone"],"repoGuide":" example-org/repo-guide ","teams":{"pricing":{"kb":"kb/pricing-kb","repoMap":"raw/{domain}/repos/{repo}.md","domains":["coupon","discount","missing"]},"product":{"kb":"kb/product-kb","repoMap":null,"domains":["product"]},"empty":{"kb":"","domains":["x"]}}}""");
Check.That(cfg!.RepoGuide == "example-org/repo-guide" && cfg.Mine().Select(m => m.Team).SequenceEqual(new[] { "pricing", "product" }) && !cfg.Teams.ContainsKey("empty"), "team: 내 팀들 (없는 팀·kb 빈 팀은 뺌)");
Check.That(cfg.Candidates(map).SequenceEqual(new[] { "coupon", "discount", "product" }), "team: 후보는 내 팀 도메인 합집합, domains.json 에 없는 도메인은 뺌");
Check.That(cfg.TeamOf("product") == "product" && cfg.TeamOf("missing") == "pricing" && cfg.TeamOf("zzz") == null, "team: 도메인의 팀");
Check.That(TeamConfig.Parse("[1]") == null && TeamConfig.Parse("{") == null, "team: 깨진 파일은 없는 것으로");
var none = TeamConfig.Parse("""{"myTeams":["gone"],"teams":{}}""");
Check.That(none!.Candidates(map).SequenceEqual(new[] { "coupon", "discount", "product" }), "team: 쓸 팀이 없으면 domains.json 전체");
Check.That(DomainMap.ResolvePath("kb/x", @"C:\h") == @"C:\h\Workspace\kb\x" && DomainMap.ResolvePath("~/a", @"C:\h") == Paths.ExpandTilde("~/a", @"C:\h") && DomainMap.ResolvePath(@"D:\k", @"C:\h") == @"D:\k", "team: kb 경로 해석은 knowledge 와 같음");
```

- [ ] **Step 2: 실행해 실패 확인** — `dotnet run --project app-win/src/PokeMate.CoreChecks` → 컴파일 오류(`TeamConfig` 없음).
- [ ] **Step 3: `TeamConfig.cs` 구현, `ResolvePath` 뽑기.** JSON 은 기존 `Json.ParseObject`·`Json.Str`·`Json.Strings` 를 쓴다(`DomainMap.Parse` 와 같은 방식).
- [ ] **Step 4: 실행해 통과 확인** — 마지막 줄 `failed: 3` (기존 settings 3건만).
- [ ] **Step 5: 커밋** — `git commit -m "feat: read team config for review domains"`

### Task 2: PathRules (`local/path-rules.json` 과 glob)

**Files:**
- Create: `app-win/src/PokeMate.Core/PathRules.cs`
- Test: `app-win/src/PokeMate.CoreChecks/Checks/PathRulesChecks.cs`, `Program.cs` 에 등록

**Interfaces:**
- Produces:
  - `public sealed record PathRule(string Path, string Domain)`
  - `public sealed record PathVerdict(string? Domain, IReadOnlyDictionary<string, int> Counts)` — `Counts`: 도메인 → 그 도메인 규칙에 처음 맞은 파일 수.
  - `public sealed record PathRules(Dictionary<string, List<PathRule>> Repos)`
    - `public static string File => AppPaths.LocalFile("path-rules.json")`
    - `public static PathRules Parse(string? json)` / `Load(string? from = null)` — 깨지면 빈 규칙. 키는 대소문자 무시 비교(`StringComparer.OrdinalIgnoreCase` 사전).
    - `public static bool Matches(string glob, string path)` — 대소문자 무시. 양쪽 모두 `\`→`/`, 앞의 `./`·`/` 제거. `**` 는 `/` 포함 아무 글자(0개 이상, `a/**/b` 는 `a/b` 도 맞음), `*` 는 `/` 를 뺀 아무 글자, `?` 는 `/` 아닌 한 글자. 나머지는 글자 그대로.
    - `public PathVerdict Judge(string fullName, IReadOnlyList<string> files, IReadOnlyCollection<string> candidates)` — 후보가 아닌 도메인 규칙은 없는 것으로 본다. 파일마다 처음 맞는 규칙의 도메인을 센다. 센 도메인이 정확히 하나면 `Domain` 에 넣는다.

- [ ] **Step 1: 실패하는 검증 작성**

```csharp
Check.That(PathRules.Matches("src/Areas/Coupon/**", "src/areas/coupon/a/b.cs") && PathRules.Matches("src/**/x.cs", "src/x.cs")
           && PathRules.Matches("src/*.cs", @".\src\a.cs") && !PathRules.Matches("src/*.cs", "src/a/b.cs") && !PathRules.Matches("src/Coupon/**", "src/CouponX/a.cs"),
           "path-rule: glob (**, *, 대소문자, \\·./ 정리)");
var rules = PathRules.Parse("""{"Example-Org/Shared-Admin":[{"path":"src/Areas/Coupon/**","domain":"coupon"},{"path":"src/Areas/Item/**","domain":"product"},{"path":"src/Areas/**","domain":"old"}]}""");
var cands = new[] { "coupon", "discount", "product" };
var one = rules.Judge("example-org/shared-admin", new[] { "src/Areas/Coupon/A.cs", "src/Areas/Coupon/B.cs", "README.md" }, cands);
Check.That(one.Domain == "coupon" && one.Counts["coupon"] == 2, "path-rule: 한 도메인이면 확정 (규칙 밖 파일 섞여도)");
var two = rules.Judge("example-org/shared-admin", new[] { "src/Areas/Coupon/A.cs", "src/Areas/Item/B.cs", "src/Areas/Item/C.cs" }, cands);
Check.That(two.Domain == null && two.Counts["coupon"] == 1 && two.Counts["product"] == 2, "path-rule: 둘 이상이면 미정 + 개수");
var gone = rules.Judge("example-org/shared-admin", new[] { "src/Areas/Other/A.cs" }, cands);
Check.That(gone.Domain == null && gone.Counts.Count == 0, "path-rule: 후보가 아닌 도메인 규칙은 무시");
Check.That(rules.Judge("acme/other", new[] { "src/Areas/Coupon/A.cs" }, cands).Domain == null && PathRules.Parse("x").Repos.Count == 0, "path-rule: 규칙 없는 레포·깨진 파일");
```

- [ ] **Step 2: 실행해 실패 확인** — 컴파일 오류(`PathRules` 없음).
- [ ] **Step 3: `PathRules.cs` 구현.** glob 은 정규식으로 바꿔 쓴다(`**/` → `(.*/)?`, `**` → `.*`, `*` → `[^/]*`, `?` → `[^/]`, 나머지 `Regex.Escape`, 전체 `^…$`, `IgnoreCase`). Swift 도 같은 변환을 쓴다.
- [ ] **Step 4: 실행해 통과 확인** — `failed: 3`.
- [ ] **Step 5: 커밋** — `git commit -m "feat: add path rules for shared repos"`

### Task 3: DomainReview 판정 (1·2단계, 3단계 프롬프트, 근거 문구)

**Files:**
- Modify: `app-win/src/PokeMate.Core/DomainReview.cs`
- Modify: `app-win/src/PokeMate.Core/ReviewEval.cs` (`Domain` 이 `Known`·`ReviewDomainBook` 대신 `Listed` 사용), `app-win/src/PokeMate.Eval/Program.cs`·`app-win/src/PokeMate.Trial/Program.cs` 의 `ReviewDomainBook` 사용 제거
- Test: `app-win/src/PokeMate.CoreChecks/Checks/DomainReviewChecks.cs` (기존 Known·Learned 검증을 아래로 바꿈), `ReviewEvalChecks.cs` 맞춤

**Interfaces:**
- Consumes: Task 1 `TeamConfig`, `DomainMap.ResolvePath`; Task 2 `PathRules`, `PathVerdict`.
- Produces:
  - `public enum DomainReview.Source { TeamMap, Listed, PathRule, Chosen, Fallback }`, `Raw()` → `팀 KB 지도` · `domains.json 의 repos` · `경로 규칙` · `PR 판단` · `기본값`.
  - `public sealed record RouteHints(IReadOnlyList<string> Files, IReadOnlyList<string> SharedDocs, IReadOnlyList<string> MapDomains, IReadOnlyDictionary<string, int> RuleCounts)`
  - `public static (List<(string Domain, string Why, Source Source)> Hits, List<string> SharedDocs) DomainReview.MapLookup(ReviewTarget target, DomainMap map, TeamConfig? team, IReadOnlyList<string> candidates, string? home = null)`
    - 내 팀마다 `repoMap` 이 있으면: `{domain}` 앞부분 폴더(예: `{kb}/raw/`)의 하위 폴더를 훑어, 각 폴더에서 `{repo}` 자리 파일을 대소문자 무시로 찾는다. 폴더 이름이 후보 도메인이면 Hit(`TeamMap`, 근거 `{팀} 팀 KB {상대 경로}`), 아니면 그 파일의 전체 경로를 SharedDocs 에. 폴더가 없으면 조용히 건너뛴다.
    - `map.Domains` 의 repos 에 레포가 있고 그 도메인이 후보면 Hit(`Listed`, 근거 `domains.json 의 {domain} repos 에 {repo} 가 있음`).
  - `public static ((string Domain, string Why, Source Source)? Decided, RouteHints Hints) DomainReview.Route(ReviewTarget target, DomainMap map, TeamConfig? team, PathRules rules, IReadOnlyList<string> files, string? home = null)`
    - 후보 = `team?.Candidates(map) ?? map.Domains.Keys(이름순)`.
    - Hit 도메인이 정확히 하나 → 첫 Hit 로 확정.
    - 아니면 `rules.Judge` → 도메인 하나면 `PathRule`, 근거 `{가장 많이 맞은 규칙 path 의 앞부분} 아래 {n}개 파일` (형식 `{glob} 에 맞는 파일 {n}개` 로 해도 됨, 두 앱 같게).
    - 아니면 Decided=null, Hints 에 files·SharedDocs·Hit 도메인들(중복 제거)·Counts.
  - `public static (string Domain, string Why, Source Source)? DomainReview.Listed(ReviewTarget target, DomainMap map)` — 평가용. domains.json repos 만.
  - `public static IReadOnlyList<string> DomainReview.Candidates(DomainMap map, TeamConfig? team)` — 위 후보 규칙(앱·프롬프트·Extract 가 같이 씀).
  - `public static string DomainReview.ChoosePrompt(ReviewTarget target, DomainMap map, TeamConfig? team, RouteHints hints, string helper)` — 후보 줄 `- {domain} ({팀} 팀) · {설명} · 키워드: …`(팀 없으면 괄호 생략), 마지막 `- default · 위 어느 도메인에도 맞지 않을 때`. 읽을거리: 바뀐 파일 목록(최대 200줄, 넘으면 "외 N개"), `team.RepoGuide` 가 있으면 `{helper} gh-get repos/{RepoGuide}/contents/maps/{owner}/{repo}.md --jq '.content | @base64d'`, SharedDocs 경로(`/` 구분), RuleCounts(`경로 규칙 후보: coupon 3개, product 2개`), MapDomains(`팀 KB 지도 후보: …`). 문장: "조회가 실패하면 레포 이름만으로 고르지 말고 default". 블록 형식은 지금과 같다(`pokemate-domain`).
  - `public static (string Domain, string Why)? DomainReview.Extract(string text, IReadOnlyCollection<string> candidates)` — 후보 또는 default 만 받는다.
  - `ReviewPrompt` 의 안내 줄을 `- 리뷰 본문 "확인한 범위" 첫 줄에 "도메인: {domain} — {source.Raw()}: {why}" 를 적고…` 로 바꾼다.
  - 없앰: `Known`, `ReviewDomainBook`, `ReviewDomainChoice`, `Source.Explicit/Learned`. `Valid` 는 남기면 `Candidates` 기준으로 바꾼다.

- [ ] **Step 1: 실패하는 검증 작성** (임시 폴더에 팀 KB 를 만든다: `{tmp}/Workspace/kb/pricing-kb/raw/coupon/repos/Sample.Web.md`, `raw/common/repos/shared-admin.md`, `raw/discount/repos/sample-web.md` 는 두 번째 경우에서만)

```csharp
// home = tmp. team: pricing(repoMap, coupon·discount) + product(repoMap null, product). map: product repos ["sample-item-api"]
var hit = DomainReview.Route(new ReviewTarget("acme", "sample.web", 1, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp);
Check.That(hit.Decided is { Domain: "coupon", Source: DomainReview.Source.TeamMap } && hit.Decided.Value.Why.Contains("raw/coupon/repos"), "route: 팀 KB 지도 (대소문자 무시)");
Check.That(DomainReview.Route(new ReviewTarget("acme", "sample-item-api", 2, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp).Decided?.Source == DomainReview.Source.Listed, "route: 지도 없는 팀은 domains.json repos");
var shared = DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 3, "u"), map, team, rules, new[] { "src/Areas/Coupon/A.cs", "src/Areas/Item/B.cs" }, tmp);
Check.That(shared.Decided == null && shared.Hints.SharedDocs.Single().EndsWith("raw/common/repos/shared-admin.md", StringComparison.OrdinalIgnoreCase)
           && shared.Hints.RuleCounts["coupon"] == 1 && shared.Hints.RuleCounts["product"] == 1, "route: 공용 문서는 읽을거리, 경로 규칙 둘이면 3단계로");
var byRule = DomainReview.Route(new ReviewTarget("example-org", "shared-admin", 4, "u"), map, team, rules, new[] { "src/Areas/Item/B.cs" }, tmp);
Check.That(byRule.Decided is { Domain: "product", Source: DomainReview.Source.PathRule }, "route: 경로 규칙 한 도메인이면 확정");
// sample-web 이 coupon·discount 두 폴더에 다 있는 경우 (파일 추가 후)
Check.That(DomainReview.Route(new ReviewTarget("acme", "sample-web", 5, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp) is { Decided: null } r && r.Hints.MapDomains.SequenceEqual(new[] { "coupon", "discount" }), "route: 지도 두 도메인이면 미정");
Check.That(DomainReview.Route(new ReviewTarget("acme", "sample.web", 6, "u"), map, team, PathRules.Parse("{}"), new[] { "a.cs" }, tmp + @"\nope").Decided == null, "route: 팀 KB 폴더가 없으면 1단계 건너뜀 (예외 없음)");
var cands = DomainReview.Candidates(map, team);
Check.That(DomainReview.Extract("```pokemate-domain\n{\"domain\": \"product\", \"why\": \"상품 화면\"}\n```", cands) is { Domain: "product" }
           && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"legacy\"}\n```", cands) == null
           && DomainReview.Extract("```pokemate-domain\n{\"domain\": \"default\"}\n```", cands)?.Domain == "default", "route: 3단계 답은 후보·default 만");
var chooser = DomainReview.ChoosePrompt(new ReviewTarget("example-org", "shared-admin", 3, "u"), map, team with { RepoGuide = "example-org/repo-guide" }, shared.Hints, "H");
Check.That(chooser.Contains("- product (product 팀)") && chooser.Contains("H gh-get repos/example-org/repo-guide/contents/maps/example-org/shared-admin.md")
           && chooser.Contains("경로 규칙 후보: coupon 1개, product 1개") && chooser.Contains("shared-admin.md") && chooser.Contains("default"), "route: 3단계 안내에 후보·팀·레포 지도·공용 문서·규칙 후보");
var body = DomainReview.ReviewPrompt(new ReviewTarget("acme", "sample.web", 1, "u"), "coupon", "pricing 팀 KB raw/coupon/repos/Sample.Web.md", DomainReview.Source.TeamMap, true, notes, "H", "본문");
Check.That(body.Contains("도메인: coupon — 팀 KB 지도: pricing 팀 KB raw/coupon/repos/Sample.Web.md"), "route: 확인한 범위 첫 줄 형식");
```

- [ ] **Step 2: 실행해 실패 확인** — 컴파일 오류(`Route` 없음 등).
- [ ] **Step 3: `DomainReview.cs` 를 위 Interfaces 대로 고친다.** `ReviewEval.Domain` 은 `Listed` → 문제의 고정 도메인 → `Fallback` 순서(고정 도메인이 먼저인 지금 순서 유지). 기존 검증 중 `Known`·`Learned`·`ReviewDomainBook` 을 쓰는 줄은 지우거나 위 검증으로 바꾼다.
- [ ] **Step 4: 실행해 통과 확인** — `failed: 3`.
- [ ] **Step 5: 커밋** — `git commit -m "feat: route review domains by team KB, path rules and PR judgment"`

### Task 4: 앱 연결 (파일 목록 조회, HandleReview)

**Files:**
- Modify: `app-win/src/PokeMate.Core/MateCLI.cs`
- Modify: `app-win/src/PokeMate/WorkSessionCenter.cs` (`HandleReview`, 지금 :728~)
- Test: `app-win/src/PokeMate.CoreChecks/Checks/DomainReviewChecks.cs` (`ParseFileList`)

**Interfaces:**
- Consumes: Task 3 `Route`, `ChoosePrompt`, `Extract`, `Candidates`, `Source`; Task 1 `TeamConfig.Load`, `Mine`; Task 2 `PathRules.Load`.
- Produces:
  - `public static List<string> MateCLI.ParseFileList(string stdout)` — 줄마다 앞뒤 공백 제거, 빈 줄 제거, 순서 유지, 중복 제거.
  - `public static async Task<List<string>?> MateCLI.PullFiles(ReviewTarget target, AppSettings settings)` — `Run(new[] { "auto", "gh-get", $"repos/{target.FullName}/pulls/{target.Number}/files", "--paginate", "--jq", ".[].filename" }, …)`. 종료 코드가 0 이 아니거나 시간 초과(`MateCLIError`)면 null. 0개는 빈 목록(성공).

- [ ] **Step 1: 실패하는 검증 작성**

```csharp
Check.That(MateCLI.ParseFileList("a.cs\r\n\r\n b.cs \nc.cs\na.cs\n").SequenceEqual(new[] { "a.cs", "b.cs", "c.cs" }) && MateCLI.ParseFileList("").Count == 0, "review: 바뀐 파일 목록 읽기 (여러 쪽·빈 줄·CRLF)");
```

- [ ] **Step 2: 실행해 실패 확인** — 컴파일 오류.
- [ ] **Step 3: `ParseFileList`·`PullFiles` 구현.**
- [ ] **Step 4: `HandleReview` 를 고친다.** 순서:
  1. `ReviewTarget.Parse` 실패면 지금처럼 `Handle`.
  2. `SetRunning(RoutingSlot, target.Label)` 후 `PullFiles` 를 UI 밖에서 기다린다(기존 `OnUi` 로 돌아온다). null 이면 `EnqueueReview(target, DomainReview.Fallback, "파일 조회 실패", Source.Fallback, …)`.
  3. `Route(target, DomainMap.Load(), TeamConfig.Load(), PathRules.Load(), files)` → Decided 가 있으면 `EnqueueReview`.
  4. 없으면 지금의 `chat.RunAutomation(… ChoosePrompt …)`. 읽기 권한은 지금과 같고, dirs 에 `team.Mine()` 의 kb 경로(`DomainMap.ResolvePath`)를 더한다. 답을 `Extract(text, Candidates)` 로 읽어 `Source.Chosen`, 못 읽으면 `Fallback`, 근거 `도메인을 정하지 못해 default 로 리뷰`.
  5. **3단계 결과를 저장하지 않는다.** `ReviewDomainBook` 관련 코드는 남기지 않는다.
  - `SetReviewing` 의 표시는 `$"{label} · {source.Raw()}: {why}"` 로 바꾼다(`ProcessReview` 에 source 가 이미 들어온다).
- [ ] **Step 5: 실행해 통과 확인** — `failed: 3`, 그리고 `dotnet build app-win/PokeMate.sln` 경고 외 오류 0.
- [ ] **Step 6: 커밋** — `git commit -m "feat: decide review domain per PR in the app"`

### Task 5: macOS 같은 동작

**Files:**
- Create: `app-mac/Sources/PokeMateCore/TeamConfig.swift`, `app-mac/Sources/PokeMateCore/PathRules.swift`
- Modify: `app-mac/Sources/PokeMateCore/DomainReview.swift`, `JiraPipeline.swift`(`resolvePath`), `MateCLI.swift`, `ReviewEval.swift`, `app-mac/Sources/PokeMate/WorkSessionCenter.swift`, `app-mac/Sources/PokeMateEval/main.swift`
- Test: `app-mac/Sources/PokeMateCoreChecks/main.swift`

**Interfaces:**
- Consumes: Task 1~4 의 C# 이름·동작.
- Produces: Swift 이름은 C# 를 lowerCamelCase 로(`TeamConfig.parse`, `candidates(map:)`, `PathRules.matches(_:_:)`, `judge(fullName:files:candidates:)`, `DomainReview.route(target:map:team:rules:files:home:)`, `mapLookup`, `listed`, `choosePrompt(target:map:team:hints:helper:)`, `extract(_:candidates:)`, `MateCLI.parseFileList`, `pullFiles`). `Source` 의 rawValue 는 Task 3 의 단계 이름 그대로.

- [ ] **Step 1: Task 1~4 의 검증을 `PokeMateCoreChecks/main.swift` 에 같은 이름·같은 값으로 옮긴다.**
- [ ] **Step 2: Swift 구현을 옮긴다.** glob 정규식 변환은 Task 2 와 같은 규칙(`NSRegularExpression`, `.caseInsensitive`).
- [ ] **Step 3: 검증** — Mac 에서 `cd app-mac && swift run PokeMateCoreChecks`. 이 PC 에서는 실행 불가 → 결과 보고에 "Swift 검증 미실행(Windows)" 으로 적고, 최소한 C# 과 이름·문구가 같은지 diff 로 대조한다.
- [ ] **Step 4: 커밋** — `git commit -m "feat: route review domains on macOS"`

### Task 6: 예시·문서·이 PC 설정

**Files:**
- Create: `local.example/teams.json`, `local.example/path-rules.json` (스펙 4-2·4-3 의 일반 이름 예시 그대로)
- Modify: `README.md` (리뷰 도메인 단락: 0~3단계 한 줄씩, "팀이 바뀌면 `local/teams.json` 의 `myTeams` 만 고친다", 경로 규칙은 지금 후보가 아닌 도메인이면 무시됨)
- 이 PC 로컬(커밋 안 함, 라이브 checkout `C:\workspace\poke-mate-ai-agent\local\` 에서 고침): `teams.json` 새로, `domains.json` 에 discount 추가·coupon 키워드 나누기, `kb-sync.json` 의 `domains` 에 `"discount": "pricing"`

- [ ] **Step 1: `local.example/` 두 파일과 README 단락을 쓴다.** 회사 이름이 없는지 확인: `git diff release/v2-2-0 -- . | grep -iE "<사내 이름 목록>"` 이 아무것도 안 찍혀야 한다(사내 이름 목록은 local 설정 파일들에서 owner·repo 이름을 모아 만든다).
- [ ] **Step 2: 이 PC 로컬 설정을 고친다.** `teams.json` 은 `myTeams: ["pricing","product"]`, 실제 팀 KB 경로·전사 레포 지도 저장소 이름을 넣는다. `domains.json` discount: knowledge 는 coupon 과 같은 팀 KB 경로 + `notes/discount`, description `할인: 즉시할인·할인 정책·타겟팅`, keywords `할인, discount, dc, 즉시할인, 타겟팅`. coupon keywords 에서 `할인`·`discount` 를 뺀다. 파일이 JSON 으로 읽히는지 `python -c "import json;json.load(open(...,encoding='utf-8'))"` 로 확인.
- [ ] **Step 3: 공용 관리자 레포 경로 규칙 초안을 쓴다.** 팀 KB 의 그 레포 문서(Area 표)를 읽고 pricing·product 도메인 규칙 초안을 만든다. **`local/path-rules.json` 에 넣지 않고** 사용자에게 보여 확인받는다(스펙 7절). 확인 전 파일은 `{}`.
- [ ] **Step 4: 커밋** — `git add local.example README.md && git commit -m "docs: add team config and path rule examples"`
- [ ] **Step 5: base 로 합치기** — `release/v2-2-0` 에서 `git merge --no-ff feature/v2-2-0/domain-routing -m "feat: merge review domain routing"`, `git push origin release/v2-2-0`.
