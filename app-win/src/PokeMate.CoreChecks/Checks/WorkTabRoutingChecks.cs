using System.Text;
using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 작업 탭·보낼 곳
public static class WorkTabRoutingChecks
{
    public static void Run()
    {
        var tabs = SidebarItemExtensions.Tabs;
        Check.That(tabs.SequenceEqual(new[] { SidebarItem.Chat, SidebarItem.Alarms, SidebarItem.Work, SidebarItem.Notes, SidebarItem.Collection })
                   && tabs.Select(t => t.Title()).SequenceEqual(new[] { "홈", "알람", "작업", "메모", "컬렉션" }), "tab: 홈 | 알람 | 작업 | 메모 | 컬렉션");
        Check.That(SidebarItemExtensions.Target("workSessions") == SidebarItem.Work && SidebarItemExtensions.WorkSessionsTab == SidebarItem.Work && SidebarItemExtensions.Target(null) == SidebarItem.Chat,
                   "tab: 문맥 알림·새 세션으로 바꾸기는 작업 탭으로");
        Check.That(SidebarItemExtensions.Tools.SequenceEqual(new[] { SidebarItem.Connections, SidebarItem.Settings }) && SidebarItem.Connections.Title() == "연결" && SidebarItem.Settings.Title() == "설정"
                   && SidebarItemExtensions.AllCases.Count == 7, "tab: 연결·설정은 도구 칸");

        var routing = new ChatRouting();
        Check.That(routing.IsMate && routing.Repo == null, "route: 기본은 메이트");
        routing.Choose(" sample-shop-price-service ");
        routing.NoteSent("sample-shop-price-service", "가격 API 타임아웃 설정 어디 있어?\n둘째 줄");
        var saved = Json.Serialize(routing);
        var restored = Json.Deserialize<ChatRouting>(saved);
        Check.That(restored?.Repo == "sample-shop-price-service" && restored.MateNotes.SequenceEqual(new[] { "작업 세션 sample-shop-price-service 로 보냄: 가격 API 타임아웃 설정 어디 있어?" }),
                   "route: 고른 보낼 곳·메이트 기록 줄은 저장 뒤에도 유지");
        Check.That(saved.Contains("\"repo\"") && saved.Contains("\"mateNotes\"") && Json.Deserialize<ChatRouting>("""{"repo":"r","mateNotes":["a"]}""") is { Repo: "r" } r && r.MateNotes.Count == 1,
                   "route: 저장 키는 repo·mateNotes (맥과 같음)");
        var taken = restored!;
        var prefix = taken.TakeMatePrefix();
        Check.That(prefix.Contains("[앱 기록]") && prefix.Contains("작업 세션 sample-shop-price-service 로 보냄") && taken.MateNotes.Count == 0 && taken.Repo == "sample-shop-price-service",
                   "route: 메이트 기록 줄은 다음 메이트 메시지 앞에 한 번만, 보낼 곳은 그대로");
        Check.That(taken.TakeMatePrefix().Length == 0, "route: 두 번째엔 붙이지 않음");
        routing.Reset();
        Check.That(routing.IsMate && routing.MateNotes.Count == 0, "route: 새 대화면 메이트로");
        routing.Choose("  ");
        Check.That(routing.IsMate, "route: 빈 이름은 메이트");
        Check.That(new System.Globalization.StringInfo(ChatRouting.Summary(new string((char)0xAC00, 70))).LengthInTextElements == 61, "route: 요약은 60자 + …");
        Check.That(ChatRouting.Summary("\r\n  첫 줄  \r\n둘째") == "첫 줄", "route: 요약은 CRLF 도 줄바꿈으로, 첫 글 줄만");

        var message = new ChatMessage(ChatMessage.RoleKind.Assistant, "답") { Session = "r", RouteSuggest = "r" };
        var decoded = Json.Deserialize<ChatMessage>(Json.Serialize(message));
        Check.That(decoded?.Session == "r" && decoded.RouteSuggest == "r" && decoded.RouteSent == false, "route: 말풍선 세션 라벨·제안 저장");
        var old = Json.Deserialize<ChatMessage>("""{"id":"6F9619FF-8B86-D011-B42D-00C04FC964FF","role":"user","text":"a","date":0}""");
        Check.That(old != null && old.Session == null && old.RouteSuggest == null, "route: 예전 대화 기록도 읽음");

        var suggested = RouteSuggestion.Extract("타임아웃은 설정 파일에 있어요.\n\n```pokemate-route-suggest\n{\"repo\": \"sample-shop-price-service\"}\n```\n");
        Check.That(suggested.Repo == "sample-shop-price-service" && suggested.Text == "타임아웃은 설정 파일에 있어요.", "suggest: 블록 읽고 본문에서 지움");
        Check.That(RouteSuggestion.Extract("그냥 답").Repo == null && RouteSuggestion.Extract("그냥 답").Text == "그냥 답", "suggest: 블록 없으면 그대로");
        var bad = RouteSuggestion.Extract("답\n```pokemate-route-suggest\n{\"repo\": \"../etc\"}\n```");
        Check.That(bad.Repo == null && bad.Text == "답", "suggest: 경로가 섞인 이름은 버림");
        Check.That(RouteSuggestion.Extract("답\n```pokemate-route-suggest\n{\"repo\": \"..\\\\etc\"}\n```").Repo == null, "suggest: 역슬래시가 섞인 이름도 버림 (Windows 경로)");
        Check.That(RouteSuggestion.Extract("답\n```pokemate-route-suggest\nnot json\n```").Repo == null, "suggest: JSON 아니면 버림");
        Check.That(WorkRoute.Extract("```pokemate-route-suggest\n{\"repo\":\"x\"}\n```") == null, "suggest: 자동 처리 레포 정하기 블록과 섞이지 않음");
        Check.That(RouteSuggestion.Extract("타임아웃은 설정 파일에 있어요.\r\n\r\n```pokemate-route-suggest\r\n{\"repo\": \"sample-shop-price-service\"}\r\n```\r\n").Repo == "sample-shop-price-service",
                   "suggest: CRLF 줄 끝의 블록도 읽음");

        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var book = new WorkSessionBook();
        Check.That(book.AddEmpty("r", t0) && !book.AddEmpty("r", t0) && book.ResumeId("r") == null, "new: 빈 세션 만들기, 같은 레포는 한 번만, resume 없음");
        book.Record("r", "s1", null, null, t0);
        Check.That(book.Sessions["r"].Id == "s1" && book.Archived.Count == 0 && book.ResumeId("r") == "s1", "new: 첫 실행 세션 id 를 그대로 이어받음 (보관 안 함)");
        Check.That(WorkspaceRepos.Bootstrap("r", @"C:\w").Contains(@"C:\w\r") && WorkspaceRepos.Bootstrap("r").Contains("CLAUDE.md"), "new: 첫 메시지에 지식 모으기 지시");
        Check.That(WorkspaceRepos.Bootstrap(WorkSessionBook.Operations).Contains("레포가 없는 운영 건 전용") && !WorkspaceRepos.Bootstrap(WorkSessionBook.Operations).Contains("CLAUDE.md"), "new: 운영 세션은 레포 지식 모으기 없음");
        Check.That(WorkspaceRepos.Root == Path.Combine(Paths.Home, "Workspace"), "new: 작업 폴더는 홈의 Workspace");

        var tmp = Check.TempDir("pm-repos");
        foreach (var name in new[] { "b-repo", "a-repo", "plain", "repo10", "repo2" }) Directory.CreateDirectory(Path.Combine(tmp, name));
        foreach (var name in new[] { "b-repo", "a-repo" }) Directory.CreateDirectory(Path.Combine(tmp, name, ".git"));
        Check.That(WorkspaceRepos.List(tmp).SequenceEqual(new[] { "a-repo", "b-repo" }), "new: ~/Workspace 아래 git 레포만 이름순");
        foreach (var name in new[] { "repo10", "repo2" }) File.WriteAllText(Path.Combine(tmp, name, ".git"), "gitdir: ../elsewhere");   // worktree 는 .git 이 파일
        Check.That(WorkspaceRepos.List(tmp).SequenceEqual(new[] { "a-repo", "b-repo", "repo2", "repo10" }), "new: .git 이 파일인 worktree 도 레포, 숫자는 크기순 (repo2 < repo10)");
        Check.That(WorkspaceRepos.List(Path.Combine(tmp, "없음")).Count == 0, "new: 폴더가 없으면 빈 목록");
        Check.Remove(tmp);

        var lines = string.Join("\n",
            """{"type":"assistant","timestamp":"2026-10-31T23:00:00.000Z","message":{"id":"m1","model":"x","usage":{"input_tokens":100,"output_tokens":0}}}""",
            """{"type":"assistant","timestamp":"2026-11-02T01:00:00.000Z","message":{"id":"m2","model":"x","usage":{"input_tokens":7,"output_tokens":3}}}""");
        var since = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        Check.That(TranscriptUsage.Total(lines, since).Usage.Total == 10 && TranscriptUsage.Total(lines).Usage.Total == 110, "month: 이번 달 토큰은 기록 시각으로 거름");
    }
}
