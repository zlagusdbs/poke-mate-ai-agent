using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 팀 설정 (local/teams.json 읽기·내 팀 도메인 후보)
public static class TeamConfigChecks
{
    public static void Run()
    {
        var map = DomainMap.Parse("""{"coupon":{"repos":[]},"discount":{"repos":[]},"product":{"repos":["sample-item-api"]}}""");
        var cfg = TeamConfig.Parse("""{"myTeams":["pricing","product","gone"],"repoGuide":" example-org/repo-guide ","teams":{"pricing":{"kb":"kb/pricing-kb","repoMap":"raw/{domain}/repos/{repo}.md","domains":["coupon","discount","missing"]},"product":{"kb":"kb/product-kb","repoMap":null,"domains":["product"]},"empty":{"kb":"","domains":["x"]}}}""");
        Check.That(cfg!.RepoGuide == "example-org/repo-guide" && cfg.Mine().Select(m => m.Team).SequenceEqual(new[] { "pricing", "product" }) && !cfg.Teams.ContainsKey("empty"), "team: 내 팀들 (없는 팀·kb 빈 팀은 뺌)");
        Check.That(cfg.Candidates(map).SequenceEqual(new[] { "coupon", "discount", "product" }), "team: 후보는 내 팀 도메인 합집합, domains.json 에 없는 도메인은 뺌");
        Check.That(cfg.TeamOf("product") == "product" && cfg.TeamOf("missing") == "pricing" && cfg.TeamOf("zzz") == null, "team: 도메인의 팀");
        Check.That(TeamConfig.Parse("[1]") == null && TeamConfig.Parse("{") == null, "team: 깨진 파일은 없는 것으로");
        var none = TeamConfig.Parse("""{"myTeams":["gone"],"teams":{}}""");
        Check.That(none!.Candidates(map).SequenceEqual(new[] { "coupon", "discount", "product" }), "team: 쓸 팀이 없으면 domains.json 전체");
        var maps = TeamConfig.Parse("""{"teams":{"a":{"kb":"k","repoMap":"raw/{repo}.md"},"b":{"kb":"k","repoMap":"raw/{domain}/x.md"},"c":{"kb":"k","repoMap":"raw/repos/{repo}-{domain}.md"},"d":{"kb":"k","repoMap":"raw\\{domain}\\{repo}.md"},"e":{"kb":"k","repoMap":"./raw/{domain}-team/{repo}.md"}}}""");
        Check.That(maps!.Teams["a"].RepoMap == null && maps.Teams["b"].RepoMap == null && maps.Teams["c"].RepoMap == null
                   && maps.Teams["d"].RepoMap == @"raw\{domain}\{repo}.md" && maps.Teams["e"].RepoMap == "./raw/{domain}-team/{repo}.md",
                   "team: repoMap 은 {domain}·{repo} 가 다 있고 {domain} 이 마지막 경로 조각에 없어야 함 (아니면 null)");
        Check.That(DomainMap.ResolvePath("kb/x", @"C:\h") == @"C:\h\Workspace\kb\x" && DomainMap.ResolvePath("~/a", @"C:\h") == Paths.ExpandTilde("~/a", @"C:\h") && DomainMap.ResolvePath(@"D:\k", @"C:\h") == @"D:\k", "team: kb 경로 해석은 knowledge 와 같음");
    }
}
