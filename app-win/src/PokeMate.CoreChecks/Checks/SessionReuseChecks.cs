using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 도메인 리뷰어 — 세션 이어 쓰기 규칙 (reuse)
public static class SessionReuseChecks
{
    public static void Run()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var rbook = new WorkSessionBook();
        rbook.Record("acme:reviewer", "s1", "ACME-1", null, t0);
        rbook.Sessions["acme:reviewer"].ContextTokens = 90_000;
        var policy = new SessionReuse();
        var keepReviewer = new SessionReuse(freshReviewer: false);
        Check.That(policy.IdleMinutes == 55 && policy.MaxTokens == 120_000, "reuse: 기본 55분·120k");
        var warm = rbook.Copy();
        Check.That(warm.ReuseId("acme:reviewer", keepReviewer, t0.AddMinutes(54)) == "s1" && warm.Archived.Count == 0, "reuse: 55분 안·문맥 상한 아래면 이어 씀");
        var idle = rbook.Copy();
        Check.That(idle.ReuseId("acme:reviewer", keepReviewer, t0.AddMinutes(61)) == null
                   && idle.Sessions["acme:reviewer"].Id == "" && idle.Sessions["acme:reviewer"].FreshReason == "식음 61분 (기준 55분)"
                   && idle.Archived.Select(s => s.Id).SequenceEqual(new[] { "s1" }), "reuse: 55분 넘게 쉬면 새 세션, 옛 세션은 보관, 이유 남김");
        idle.Record("acme:reviewer", "s2", "ACME-2", null, t0.AddMinutes(62));
        Check.That(idle.Sessions["acme:reviewer"].Id == "s2" && idle.Sessions["acme:reviewer"].FreshReason == "식음 61분 (기준 55분)"
                   && idle.Sessions["acme:reviewer"].Keys.SequenceEqual(new[] { "ACME-2" }) && idle.Archived.Count == 1, "reuse: 새 세션 기록에 이유가 이어짐");
        var big = rbook.Copy();
        big.Sessions["acme:reviewer"].ContextTokens = 130_000;
        Check.That(big.ReuseId("acme:reviewer", keepReviewer, t0.AddSeconds(60)) == null
                   && big.Sessions["acme:reviewer"].FreshReason?.StartsWith("문맥 ") == true, "reuse: 문맥이 상한 이상이면 새 세션");
        Check.That(big.Sessions["acme:reviewer"].FreshReason == "문맥 130.0K (기준 120.0K)", "reuse: 문맥 이유는 K 단위로");
        var rev = rbook.Copy();
        Check.That(rev.ReuseId("acme:reviewer", policy, t0.AddSeconds(60)) == null
                   && rev.Sessions["acme:reviewer"].FreshReason == "리뷰어는 늘 새로" && rev.Archived.Select(s => s.Id).SequenceEqual(new[] { "s1" }),
                   "reuse: 리뷰어 세션은 기본으로 늘 새로 (옛 세션 보관, 이유 남김)");
        var pmBook = new WorkSessionBook();
        pmBook.Record("acme:pm", "p1", "ACME-1", null, t0);
        pmBook.Record("sample-api", "r1", "ACME-2", null, t0);
        Check.That(pmBook.ReuseId("acme:pm", policy, t0.AddSeconds(60)) == "p1" && pmBook.ReuseId("sample-api", policy, t0.AddSeconds(60)) == "r1",
                   "reuse: PM·개발·테스터·레포 세션은 시간·문맥 기준 그대로");
        var revOff = rbook.Copy();
        Check.That(revOff.ReuseId("acme:reviewer", keepReviewer, t0.AddSeconds(60)) == "s1", "reuse: 토글을 끄면 리뷰어도 같은 기준");
        var keep = rbook.Copy();
        keep.Sessions["acme:reviewer"].ContextTokens = 130_000;
        Check.That(keep.ReuseId("acme:reviewer", SessionReuse.Always, t0.AddHours(9)) == "s1"
                   && new SessionReuse(0, 120_000, false).FreshReason(rbook.Sessions["acme:reviewer"], t0.AddHours(9)) == null,
                   "reuse: 0 이면 그 기준은 끔 (둘 다 0 이면 늘 이어 쓰기)");
        var empty = new WorkSessionBook();
        Check.That(empty.ReuseId("nope", policy, t0) == null && empty.Sessions.Count == 0, "reuse: 세션이 없으면 그대로 새로");
        var oldSettings = Json.Deserialize<AppSettings>("""{"repoDir": "C:\\r"}""");
        var setSettings = Json.Deserialize<AppSettings>("""{"sessionReuseMinutes": 0, "sessionReuseMaxTokens": -5}""");
        Check.That(oldSettings?.SessionReuseMinutes == 55 && oldSettings.SessionReuseMaxTokens == 120_000 && oldSettings.ReviewerAlwaysFresh
                   && setSettings != null && new SessionReuse(setSettings) == new SessionReuse(0, 0, true), "reuse: 설정 기본값과 0(끔)");
        Check.That(ReviewEval.Policy("always") == SessionReuse.Always && ReviewEval.Policy("default") == new SessionReuse() && ReviewEval.Policy("30,80000") == new SessionReuse(30, 80_000)
                   && ReviewEval.Policy("x") == null && ReviewEval.Policy("-1,5") == null && ReviewEval.NewLines("a\nb\nc", 2) == "c",
                   "eval: 세션 이어 쓰기 규칙 인자, 이번 실행에 생긴 기록만");
        var ebook = new WorkSessionBook();
        var et = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        ReviewEval.RecordSession(ebook, "acme:reviewer", "bad", false, "c1", null, null, et);
        Check.That(ebook.Sessions.Count == 0 && ebook.ReuseId("acme:reviewer", SessionReuse.Always, et) == null, "eval: 실패한 실행은 세션을 기록하지 않음 (다음 문제는 새로)");
        ReviewEval.RecordSession(ebook, "acme:reviewer", "s1", true, "c2", null, (new RunUsage(input: 1, cacheRead: 95_000), 96_000), et);
        Check.That(ebook.Sessions["acme:reviewer"].ContextTokens == 96_000 && ebook.ReuseId("acme:reviewer", keepReviewer, et.AddSeconds(60)) == "s1",
                   "eval: 성공한 실행은 기록하고 끝 문맥으로 다음 판단");
        ReviewEval.RecordSession(ebook, "acme:reviewer", "s1", false, "c3", null, null, et.AddSeconds(120));
        Check.That(ebook.Sessions["acme:reviewer"].Keys.SequenceEqual(new[] { "c2" }), "eval: 이어 쓰다 실패해도 기록을 바꾸지 않음");

        // 문맥 기준은 64비트 (캐시 읽기가 많은 긴 세션)
        var huge = new WorkSessionBook();
        huge.Record("acme:pm", "p1", "K", new RunUsage(0, 0, 4_000_000_000, 0, 0, 1_000_000), t0);
        Check.That(huge.Sessions["acme:pm"].ContextTokens == 4_000_000_000 && huge.Sessions["acme:pm"].ContextPercent == 100
                   && huge.ReuseId("acme:pm", policy, t0.AddSeconds(60)) == null && huge.Sessions["acme:pm"].FreshReason!.StartsWith("문맥 4.00B"), "reuse: 문맥 토큰은 64비트로 비교");
    }
}
