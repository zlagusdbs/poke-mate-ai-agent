using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 경로 규칙 (local/path-rules.json 읽기·glob·도메인 판정)
public static class PathRulesChecks
{
    public static void Run()
    {
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
    }
}
