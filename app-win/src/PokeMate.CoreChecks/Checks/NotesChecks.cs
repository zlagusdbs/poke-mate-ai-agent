using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Notes (앱 데이터 폴더는 POKEMATE_DATA_DIR 의 임시 폴더)
public static class NotesChecks
{
    public static void Run()
    {
        var reply = "정리했어요.\n```pokemate-note\n{\"title\":\"회의\",\"body\":\"- TTL 10분\"}\n```";
        var (text, notes) = NoteProposal.Extract(reply);
        Check.That(text == "정리했어요." && notes.SequenceEqual(new[] { new NoteDraft("회의", "- TTL 10분") }), "notes: 제안 읽기");
        Check.That(NoteProposal.Extract(NoteProposal.Hint).Notes.Count == 1, "notes: 안내 예시가 읽힘");
        Check.That(NoteProposal.LooksLikeRequest("이거 메모해줘") && NoteProposal.LooksLikeRequest("적어 둬") && !NoteProposal.LooksLikeRequest("내 미완료 Jira"), "notes: 요청 판단");
        Check.That(Notes.CleanTitle("## 회의 메모") == "회의 메모", "notes: 제목 정리");
        var url = Notes.Create("점검 테스트", "- 한 줄", DateTimeOffset.FromUnixTimeSeconds(0));
        var loaded = Notes.Load(url);
        Check.That(loaded?.Title == "점검 테스트" && loaded.Preview == "- 한 줄" && url.StartsWith(Notes.Dir, StringComparison.OrdinalIgnoreCase), "notes: 만들고 읽기");
        var second = Notes.Create("점검 테스트", "", DateTimeOffset.FromUnixTimeSeconds(0));
        Check.That(second != url && Path.GetFileName(second).EndsWith("-2.md") && Notes.Read(second) == "# 점검 테스트\n\n", "notes: 같은 이름이 있으면 뒤에 숫자, 본문이 없으면 제목만");
        var stamp = DateTimeOffset.FromUnixTimeSeconds(0).ToLocalTime().ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        Check.That(Path.GetFileName(Notes.Create("## 회의 !", "x", DateTimeOffset.FromUnixTimeSeconds(0))) == stamp + "-회의.md"
                   && Path.GetFileName(Notes.Create("!!", "x", DateTimeOffset.FromUnixTimeSeconds(0))) == stamp + ".md", "notes: 이름은 시각 + 제목 (글자·숫자·- 만 30자까지)");
        Check.That(Notes.List().Select(n => n.Url).Contains(url), "notes: 목록");
        Notes.Save(url, "# 바꿈\n\n- 둘째");
        Check.That(Notes.Load(url)?.Title == "바꿈" && Notes.Load(url)?.Preview == "- 둘째", "notes: 저장");
        Notes.Delete(url);
        Check.That(!File.Exists(url), "notes: 삭제");
        Check.That(Notes.Load(url) == null, "notes: 없는 메모는 읽지 못함");
        Check.Remove(Notes.Dir);
    }
}
