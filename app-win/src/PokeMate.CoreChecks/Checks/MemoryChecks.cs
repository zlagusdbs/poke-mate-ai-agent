using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: Memory (실제 저장소 대신 임시 저장소의 local\rules\memory.md 로 검사), Notes
public static class MemoryChecks
{
    public static void Run()
    {
        using (new Check.TempRepo())
        {
            Memory.Add("팀은 가격팀");
            Memory.Add("팀은 가격팀");
            Memory.Add("화요일 10시 팀 회의");
            Check.That(Memory.Items().SequenceEqual(new[] { "팀은 가격팀", "화요일 10시 팀 회의" }), "memory: 추가 (같은 건 한 번만)");
            Check.That(Memory.Forget("가격팀") == 1 && Memory.Items().SequenceEqual(new[] { "화요일 10시 팀 회의" }), "memory: 들어간 말로 잊기");
            Check.That(Memory.Forget("화요일 10시 팀 회의") == 1 && Memory.Items().Count == 0, "memory: 똑같은 줄 잊기");
            File.Delete(Memory.File);
            Memory.Add("A팀");
            Memory.Add("A팀 회의");
            Check.That(Memory.Forget("A팀") == 1 && Memory.Items().SequenceEqual(new[] { "A팀 회의" }), "memory: 똑같은 줄이 있으면 그 줄만 (들어간 말은 지우지 않음)");
            Check.That(Memory.Forget("a팀 회") == 1 && Memory.Items().Count == 0, "memory: 대소문자 구분 없이 들어간 말로");
            Check.That(Memory.Forget("") == 0 && Memory.Forget("없는 말") == 0, "memory: 빈 말·없는 말은 지운 게 없음");
            Check.That(Path.GetFileName(Memory.File) == "memory.md" && Path.GetFileName(Path.GetDirectoryName(Memory.File)) == "rules", "memory: rules\\memory.md");
        }
    }
}
