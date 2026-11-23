using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 자동 처리 세션 규칙 하나 (SessionPolicy) · 인계 메모 · PR 작업 건 파일 · 끝난 건 보관
public static class SessionPolicyChecks
{
    static int Chars(string s) => new System.Globalization.StringInfo(s).LengthInTextElements;

    public static void Run()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var policy = new SessionPolicy();
        Check.That(policy.IdleMinutes == 55 && policy.MaxTokens == 120_000, "reuse: 기본 55분·120k");

        // 같은 건 이어 쓰기 / 다른 건 새로 / 같은 건 시간·문맥 초과 새로
        var warm = new SessionPolicy.Previous("jira:ACME-1", "s1", t0, 90_000);
        Check.That(policy.Decide("jira:ACME-1", "dev", warm, t0.AddMinutes(54)) == new SessionPolicy.Decision("s1", null),
                   "session-policy: 같은 건, 55분 안·문맥 상한 아래면 이어 씀");
        Check.That(policy.Decide("jira:ACME-2", "dev", warm, t0.AddMinutes(1)) == new SessionPolicy.Decision(null, "새 티켓 (앞: ACME-1)")
                   && SessionPolicy.Always.Decide("jira:ACME-2", "dev", warm, t0.AddMinutes(1)).ResumeId == null,
                   "session-policy: 다른 건의 세션은 어떤 설정(0 포함)에서도 이어 쓰지 않음, 이유 '새 티켓 (앞: KEY)'");
        Check.That(policy.Decide("jira:ACME-1", "dev", warm, t0.AddMinutes(61)) == new SessionPolicy.Decision(null, "식음 61분 (기준 55분)")
                   && policy.Decide("jira:ACME-1", "dev", warm with { ContextTokens = 130_000 }, t0.AddMinutes(1)) == new SessionPolicy.Decision(null, "문맥 130.0K (기준 120.0K)"),
                   "session-policy: 같은 건이라도 시간·문맥 초과면 새로 (기존 문구)");
        Check.That(new[] { "pm", "dev", "tester", "reviewer" }.All(r => policy.Decide("jira:ACME-1", r, warm, t0.AddMinutes(10)).ResumeId == "s1")
                   && policy.Decide("pr:acme/sample-web#7", "reviewer", warm with { CaseKey = "pr:acme/sample-web#7" }, t0.AddMinutes(10)).ResumeId == "s1",
                   "session-policy: 리뷰어도 같은 규칙 (역할별 예외 없음)");
        Check.That(SessionPolicy.Always.Decide("jira:ACME-1", "dev", warm with { ContextTokens = 500_000 }, t0.AddHours(9)).ResumeId == "s1"
                   && new SessionPolicy(0, 120_000).Decide("jira:ACME-1", "dev", warm, t0.AddHours(9)).ResumeId == "s1"
                   && new SessionPolicy(30, 0).Decide("jira:ACME-1", "dev", warm with { ContextTokens = 900_000 }, t0.AddMinutes(10)).ResumeId == "s1",
                   "session-policy: 0 이면 그 기준은 끔 (둘 다 0 이면 같은 건 안에서는 늘 이어 쓰기)");
        Check.That(policy.Decide("jira:ACME-1", "dev", null, t0) == new SessionPolicy.Decision(null, null)
                   && policy.Decide("jira:ACME-1", "dev", warm with { Id = "" }, t0) == new SessionPolicy.Decision(null, null),
                   "session-policy: 이전 세션이 없으면 처음 (이유 없음)");

        // 건 키
        Check.That(SessionPolicy.JiraKey("ACME-101") == "jira:ACME-101" && SessionPolicy.PrKey("acme", "sample-web", 12) == "pr:acme/sample-web#12"
                   && SessionPolicy.Label("jira:ACME-101") == "ACME-101" && SessionPolicy.Label("pr:acme/sample-web#12") == "sample-web#12" && SessionPolicy.Label("ACME-9") == "ACME-9",
                   "session-policy: 건 키 jira:{KEY}·pr:{owner}/{repo}#{n} 와 짧은 이름");
        Check.That(SessionPolicy.SameCase("ACME-1", "jira:ACME-1") && SessionPolicy.SameCase("sample-web#12", "pr:acme/sample-web#12")
                   && !SessionPolicy.SameCase("jira:ACME-1", "pr:acme/ACME-1#1") && !SessionPolicy.SameCase("pr:a/x#1", "pr:b/x#1"),
                   "session-policy: 작업 탭 기록의 짧은 이름과도 같은 건인지 비교");
        var target = ReviewTarget.Parse("https://github.example.com/acme/sample-web/pull/12")!;
        Check.That(new ReviewCase(target, "acme", t0).Key == "pr:acme/sample-web#12" && ReviewCase.FileName(target) == "acme__sample-web__12.json"
                   && policy.Decide("pr:acme/sample-web#12", "reviewer", new SessionPolicy.Previous("sample-web#9", "r9", t0, 0), t0).Reason == "새 PR (앞: sample-web#9)",
                   "session-policy: PR 키·파일 이름, 다른 PR 이면 '새 PR (앞: repo#n)'");

        // 레포 작업 세션 (알림 자동 처리의 레포 세션): 레포가 한 건, 시간·문맥만
        var rbook = new WorkSessionBook();
        rbook.Record("sample-api", "r1", "ACME-2", null, t0);
        var idle = rbook.Copy();
        Check.That(rbook.Copy().ReuseId("sample-api", policy, t0.AddMinutes(10)) == "r1"
                   && idle.ReuseId("sample-api", policy, t0.AddMinutes(61)) == null && idle.Sessions["sample-api"].Id == "" && idle.Sessions["sample-api"].FreshReason == "식음 61분 (기준 55분)"
                   && idle.Archived.Select(s => s.Id).SequenceEqual(new[] { "r1" }), "reuse: 레포 세션은 시간·문맥 기준, 새로면 옛 세션 보관·이유 남김");
        idle.Record("sample-api", "r2", "ACME-3", null, t0.AddMinutes(62));
        Check.That(idle.Sessions["sample-api"].Id == "r2" && idle.Sessions["sample-api"].FreshReason == "식음 61분 (기준 55분)", "reuse: 새 세션 기록에 이유가 이어짐");
        var empty = new WorkSessionBook();
        Check.That(empty.ReuseId("nope", policy, t0) == null && empty.Sessions.Count == 0, "reuse: 세션이 없으면 그대로 새로");
        var huge = new WorkSessionBook();
        huge.Record("acme:pm", "p1", "K", new RunUsage(0, 0, 4_000_000_000, 0, 0, 1_000_000), t0);
        Check.That(huge.Sessions["acme:pm"].ContextTokens == 4_000_000_000 && huge.Sessions["acme:pm"].ContextPercent == 100
                   && huge.ReuseId("acme:pm", policy, t0.AddSeconds(60)) == null && huge.Sessions["acme:pm"].FreshReason!.StartsWith("문맥 4.00B"), "reuse: 문맥 토큰은 64비트로 비교");

        // 예전 settings (reviewerAlwaysFresh 있음): 읽을 때 무시하고 다시 쓰지 않는다
        var oldSettings = Json.Deserialize<AppSettings>("""{"repoDir": "C:\\r", "reviewerAlwaysFresh": false, "sessionReuseMinutes": 30}""");
        var setSettings = Json.Deserialize<AppSettings>("""{"sessionReuseMinutes": 0, "sessionReuseMaxTokens": -5}""");
        var written = Json.Serialize(oldSettings!);
        Check.That(oldSettings!.SessionReuseMinutes == 30 && oldSettings.SessionReuseMaxTokens == 120_000 && !written.Contains("reviewerAlwaysFresh")
                   && written.Contains("\"sessionReuseMinutes\": 30") && new SessionPolicy(setSettings!) == SessionPolicy.Always,
                   "settings: 예전 reviewerAlwaysFresh 는 무시·다시 쓰지 않음, 0(끔)·음수는 0");

        // 평가 (--reuse): 앱과 같은 규칙 — 같은 건만 이어 쓰고 다른 건은 새로
        Check.That(ReviewEval.Policy("always") == SessionPolicy.Always && ReviewEval.Policy("default") == new SessionPolicy() && ReviewEval.Policy("30,80000") == new SessionPolicy(30, 80_000)
                   && ReviewEval.Policy("x") == null && ReviewEval.Policy("-1,5") == null && ReviewEval.NewLines("a\nb\nc", 2) == "c",
                   "eval: 세션 이어 쓰기 규칙 인자, 이번 실행에 생긴 기록만");
        var ebook = new WorkSessionBook();
        var et = t0;
        ReviewEval.RecordSession(ebook, "acme:reviewer", "bad", false, "sample-web#1", null, null, et);
        Check.That(ebook.Sessions.Count == 0 && ReviewEval.ChooseSession(ebook, "acme:reviewer", "pr:acme/sample-web#1", SessionPolicy.Always, et).ResumeId == null,
                   "eval: 실패한 실행은 세션을 기록하지 않음 (다음 문제는 새로)");
        ReviewEval.RecordSession(ebook, "acme:reviewer", "s1", true, "sample-web#1", null, (new RunUsage(input: 1, cacheRead: 95_000), 96_000), et);
        var sameCase = ReviewEval.ChooseSession(ebook, "acme:reviewer", "pr:acme/sample-web#1", new SessionPolicy(), et.AddSeconds(60));
        var otherCase = ReviewEval.ChooseSession(ebook, "acme:reviewer", "pr:acme/sample-web#2", SessionPolicy.Always, et.AddSeconds(90));
        Check.That(sameCase.ResumeId == "s1" && otherCase == new SessionPolicy.Decision(null, "새 PR (앞: sample-web#1)")
                   && ebook.Sessions["acme:reviewer"].FreshReason == "새 PR (앞: sample-web#1)" && ebook.Archived.Select(s => s.Id).SequenceEqual(new[] { "s1" }),
                   "eval: 같은 PR 은 기준대로 이어 쓰고, 다른 PR 은 '늘 이어 쓰기' 에서도 새로");
        ReviewEval.RecordSession(ebook, "acme:reviewer", "s2", false, "sample-web#2", null, null, et.AddSeconds(120));
        Check.That(ebook.Sessions["acme:reviewer"].Id == "", "eval: 실패하면 기록을 바꾸지 않음");

        HandoffChecks(t0);
        CaseFileChecks(t0, target);
        ArchiveChecks(t0);
    }

    static void HandoffChecks(DateTimeOffset t0)
    {
        var doc = "확인한 범위: pricing.py\n**결함**: 2건\n- pricing.py:3 음수 가격 검사 없음\n  (근거: 3줄)\n1. tests 누락\n미확인(질문):\n- 할인 상한 정책\n버린 후보와 이유: 없음\n- 버린 것\n판정: 댓글";
        var found = Handoffs.Findings(doc);
        Check.That(found.SequenceEqual(new[] { "결함: pricing.py:3 음수 가격 검사 없음", "결함: tests 누락", "미확인: 할인 상한 정책" }),
                   "handoff: 결과 문서의 결함·미확인 절에서 항목 제목만 (건수 줄·다른 절은 뺌)");
        Check.That(Handoffs.Findings("결함: 없음\n미확인(질문): 없음\n판정: 승인").Count == 0
                   && Handoffs.Findings("결함: pricing.py:9 0 나누기\n결함 없이 미확인만 있음").SequenceEqual(new[] { "결함: pricing.py:9 0 나누기" })
                   && Handoffs.Findings("결함마다 파일:줄\n- 이건 아님").Count == 0,
                   "handoff: '없음'·머리말이 아닌 줄은 지적으로 치지 않음");
        Check.That(Handoffs.Summary("다음에 할 일", "답 본문\n```pokemate-next\n{\"next\": \"pm\"}\n```") == "다음에 할 일\n답: 답 본문"
                   && Handoffs.Summary("", "한 줄 답") == "한 줄 답" && Chars(Handoffs.Summary(new string('가', 900), "x")) == 600,
                   "handoff: 요약은 note 와 답 앞부분 (600자 안, 새로 묻지 않음)");
        Check.That(Handoffs.Block(null, "dev") == null && Handoffs.Block(new List<CaseHandoff>(), "dev") == null, "handoff: 앞 차례가 없으면 블록 없음 (처음)");
        var many = Enumerable.Range(1, 12).Select(i => new CaseHandoff(i % 2 == 0 ? "dev" : "reviewer", t0.AddMinutes(i), $"메모 {i}", i % 2 == 0 ? null : new List<string> { $"결함: 지적 {i}" })).ToList();
        var block = Handoffs.Block(many, "reviewer")!;
        Check.That(block.StartsWith(Handoffs.Title + "\n" + Handoffs.Intro, StringComparison.Ordinal) && !block.Contains("메모 4\n") && !block.Contains("- 리뷰어: 메모 3")
                   && block.Contains("- 개발: 메모 6") && block.IndexOf("메모 5", StringComparison.Ordinal) < block.IndexOf("메모 12", StringComparison.Ordinal)
                   && block.Contains(Handoffs.FindingsTitle + "\n- 결함: 지적 11") && !block.Contains("지적 9"),
                   "handoff: 최근 8개(오래된 것부터), 리뷰어에게는 자신의 마지막 지적");
        Check.That(!Handoffs.Block(many, "dev")!.Contains(Handoffs.FindingsTitle), "handoff: 리뷰어가 아니면 지적 절 없음");
        var big = Enumerable.Range(1, 8).Select(i => new CaseHandoff("pm", t0, new string((char)('가' + i), 590))).ToList();
        var capped = Handoffs.Block(big, "pm")!;
        var huge = Handoffs.Block(new List<CaseHandoff> { new("pm", t0, new string('나', 5000)) }, "pm")!;
        Check.That(Chars(capped) <= Handoffs.MaxChars && capped.Contains(new string((char)('가' + 8), 590)) && !capped.Contains(new string((char)('가' + 1), 590))
                   && Chars(huge) == Handoffs.MaxChars && huge.EndsWith("…"),
                   "handoff: 블록 전체 4,000자 안 (넘치면 오래된 메모부터 빼고, 하나로도 넘치면 자름)");
        var list = Handoffs.Append(null, new CaseHandoff("pm", t0, "a"));
        for (var i = 0; i < 60; i++) list = Handoffs.Append(list, new CaseHandoff("pm", t0, $"m{i}"));
        Check.That(list.Count == Handoffs.Keep && list[^1].Summary == "m59", "handoff: 파일에는 최근 50개까지");
    }

    static void CaseFileChecks(DateTimeOffset t0, ReviewTarget target)
    {
        var dir = Check.TempDir("pm-prs");
        var (first, d1) = ReviewCase.Begin(target, "acme", new SessionPolicy(), dir, t0);
        Check.That(d1 == new SessionPolicy.Decision(null, null) && first.HandoffBlock(d1) == null && first.Status == ReviewCase.StatusKind.Open, "pr-case: 처음 리뷰는 새 세션, 블록 없음");
        var comment = "sample-web#12 · 댓글 · 결함 1건\n결함:\n- pricing.py:3 음수 가격 검사 없음\n미확인(질문):\n- 할인 상한 정책";
        Check.That(first.Finish("r1", new RunUsage(input: 10, cacheRead: 5000), comment, t0.AddMinutes(1)) == null && first.Save(dir), "pr-case: 댓글이면 리뷰 중 그대로");
        var text = File.ReadAllText(ReviewCase.File(target, dir));
        Check.That(text.Contains("\"key\": \"pr:acme/sample-web#12\"") && text.Contains("\"status\": \"open\"") && text.Contains("\"sessions\": {")
                   && text.Contains("\"reviewer\": {") && text.Contains("\"contextTokens\": 5010") && text.Contains("\"handoffs\": [")
                   && text.Contains("\"findings\": [") && text.Contains("\"at\": \"") && !text.Contains("archivedAt") && !text.Contains("\"label\""),
                   "pr-case: PR 파일 모양 (key·status·sessions·handoffs, 날짜 ISO 8601, 보관 전에는 archivedAt 없음)");
        var (again, d2) = ReviewCase.Begin(target, "acme", new SessionPolicy(), dir, t0.AddMinutes(10));
        Check.That(d2 == new SessionPolicy.Decision("r1", null) && again.HandoffBlock(d2) == null, "pr-case: 같은 PR 을 55분 안에 다시 리뷰하면 이어 쓰고 블록 없음");
        var (late, d3) = ReviewCase.Begin(target, "acme", new SessionPolicy(), dir, t0.AddMinutes(90));
        var block = late.HandoffBlock(d3)!;
        Check.That(d3.ResumeId == null && d3.Reason == "식음 89분 (기준 55분)" && block.Contains("- 리뷰어: sample-web#12 · 댓글 · 결함 1건")
                   && block.Contains(Handoffs.FindingsTitle + "\n- 결함: pricing.py:3 음수 가격 검사 없음\n- 미확인: 할인 상한 정책"),
                   "pr-case: 같은 PR 이 시간 초과로 새 세션이면 이전 지적이 들어간 인계 블록");
        Check.That(late.Finish("r2", null, "sample-web#12 · 승인 · 결함 없음", t0.AddMinutes(95)) == ReviewCase.StatusKind.Approved && late.IsArchived && late.Save(dir)
                   && ReviewCase.Load(target, dir) is { Status: ReviewCase.StatusKind.Approved, ArchivedAt: not null, Handoffs.Count: 2 } back && back.Sessions!["reviewer"].Id == "r2",
                   "pr-case: 승인하면 보관(archivedAt)하고 기록은 남음");
        var (reopened, _) = ReviewCase.Begin(target, "acme", new SessionPolicy(), dir, t0.AddMinutes(200));
        Check.That(!reopened.IsArchived && reopened.Status == ReviewCase.StatusKind.Open && reopened.Handoffs!.Count == 2, "pr-case: 같은 PR 에 다시 요청이 오면 다시 엶 (메모 유지)");
        Check.That(ReviewCase.Outcome("sample-web#12 · 닫힘") == ReviewCase.StatusKind.Closed && ReviewCase.Outcome("**sample-web#12 · 승인 · 결함 없음**") == ReviewCase.StatusKind.Approved
                   && ReviewCase.Outcome("sample-web#12 · 확신할 수 없음 · diff 가 너무 큼") == null && ReviewCase.Outcome("승인 이유를 적는다") == null
                   && ReviewCase.Outcome("sample-web#12 · 건너뜀 · 팀 요청") == null,
                   "pr-case: 마지막 답 첫 줄의 결과로 승인·닫힘만 보관");
        Check.Remove(dir);

        // 새 필드가 없는 옛 진행 파일도 읽힌다 (handoffs·sessions 없음), 있으면 왕복
        var pdir = Check.TempDir("pm-old-pipeline");
        File.WriteAllText(Path.Combine(pdir, "ACME-9.json"), """{"costs":{},"create":[],"createdAt":"2026-11-17T08:00:00Z","domain":"acme","event":"e","history":[],"key":"ACME-9","mustStop":false,"note":"","reviewerBacks":0,"stage":"dev","summary":"","testerBacks":0,"testsPassed":false,"timeoutMinutes":30,"title":"t","updatedAt":"2026-11-17T08:00:00Z"}""");
        var old = PipelineState.Load("ACME-9", pdir);
        Check.That(old is { Stage: PipelineState.StageKind.Dev, Handoffs: null, Sessions: null } && Handoffs.Block(old.Handoffs, "dev") == null, "handoff: 예전 진행 파일(handoffs 없음)도 읽힘");
        old!.AddHandoff(PipelineRole.Dev, "고쳤다", "답", t0);
        old.Save(pdir);
        var saved = File.ReadAllText(PipelineState.File("ACME-9", pdir));
        var reread = PipelineState.Load("ACME-9", pdir);
        Check.That(saved.Contains("\"handoffs\": [") && saved.Contains("\"role\": \"dev\"") && saved.Contains("\"summary\": \"고쳤다\\n답: 답\"") && !saved.Contains("\"findings\"")
                   && reread?.Handoffs is { Count: 1 } h && h[0] == new CaseHandoff("dev", t0, "고쳤다\n답: 답"),
                   "handoff: 진행 파일 handoffs [{role, at, summary}] 저장·읽기 (리뷰어가 아니면 findings 없음)");
        Check.Remove(pdir);
    }

    static void ArchiveChecks(DateTimeOffset t0)
    {
        // 작업 탭 기록: 끝난 건의 역할 세션은 보관(목록에서 빠지고 기록은 남음), 빈 칸은 지운다. 다른 건 세션은 그대로.
        var book = new WorkSessionBook();
        book.Record("acme:pm", "p1", "ACME-1", null, t0);
        book.Record("acme:dev", "d1", "ACME-1", null, t0);
        book.Record("acme:tester", "t0", "ACME-0", null, t0);
        book.StartFresh("acme:reviewer", "jira:ACME-1", "식음 61분 (기준 55분)", t0);
        var keys = PipelineRoleExtensions.AllCases.Select(r => PipelineRoleExtensions.SessionKey("acme", r)).ToList();
        Check.That(book.ArchiveCase(keys, "ACME-1") && !book.Sessions.ContainsKey("acme:pm") && !book.Sessions.ContainsKey("acme:dev") && !book.Sessions.ContainsKey("acme:reviewer")
                   && book.Sessions["acme:tester"].Id == "t0" && book.Archived.Select(s => s.Id).OrderBy(x => x).SequenceEqual(new[] { "d1", "p1" })
                   && !book.ArchiveCase(keys, "ACME-1"),
                   "archive: 끝난 티켓의 역할 세션은 보관, 빈 칸은 지우고, 다른 티켓 세션은 그대로");
        Check.That(book.LastArchived("acme:dev")?.Id == "d1" && book.Previous("acme:dev") is { CaseKey: "ACME-1", Id: "d1" },
                   "archive: 보관한 칸의 마지막 세션은 작업 탭·다음 건 이유에 쓴다");
        Check.That(book.StartFresh("acme:dev", "jira:ACME-2", null, t0.AddMinutes(5)) && book.Sessions["acme:dev"].FreshReason == "새 티켓 (앞: ACME-1)"
                   && !book.StartFresh("acme:dev", "jira:ACME-2", null, t0.AddMinutes(6)) && book.Archived.Count == 2,
                   "archive: 보관 뒤 다음 티켓도 '새 티켓 (앞: KEY)' (같은 이유로 두 번 바꾸지 않음)");
        book.Record("acme:reviewer", "r1", "sample-web#7", null, t0);
        Check.That(book.StartFresh("acme:reviewer", "jira:ACME-2", null, t0) && book.Sessions["acme:reviewer"].FreshReason == "새 티켓 (앞: sample-web#7)"
                   && book.Archived.Any(a => a.Id == "r1"),
                   "archive: 리뷰어 칸은 PR 리뷰와 Jira 리뷰어가 함께 쓰고, 건이 바뀌면 새로");
    }
}
