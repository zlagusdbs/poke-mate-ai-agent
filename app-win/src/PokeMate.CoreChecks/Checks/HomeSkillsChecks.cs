using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 홈 스킬 공유
public static class HomeSkillsChecks
{
    static string? LinkTarget(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget; } catch { return null; }
    }

    public static void Run()
    {
        // 공백·한글이 든 홈 폴더에서도 정션이 걸리는지 함께 본다
        var root = Check.TempDir("pm-skills");
        var home = Path.Combine(root, "홍 길동");
        var skills = HomeSkills.Source(home);
        foreach (var name in new[] { "sample-cli", "acme-jira" })
        {
            Directory.CreateDirectory(Path.Combine(skills, name));
            File.WriteAllText(Path.Combine(skills, name, "SKILL.md"), "x");
        }
        Directory.CreateDirectory(Path.Combine(skills, "synced", "abc", "docs"));
        File.WriteAllText(Path.Combine(skills, "synced", "abc", "docs", "SKILL.md"), "x");
        var target = Path.Combine(root, "work", ".claude", "skills");
        Directory.CreateDirectory(Path.Combine(target, "acme-jira"));
        var first = HomeSkills.Sync(enabled: true, home: home, target: target);
        Check.That(HomeSkills.Names(home).SequenceEqual(new[] { "acme-jira", "sample-cli" }) && first.Linked.SequenceEqual(new[] { "sample-cli" })
                   && string.Equals(LinkTarget(Path.Combine(target, "sample-cli"))?.TrimEnd('\\'), Path.Combine(skills, "sample-cli"), StringComparison.OrdinalIgnoreCase)
                   && LinkTarget(Path.Combine(target, "acme-jira")) == null,
                   "skills: 홈 스킬을 링크, 하위 폴더 동기화 사본은 빼고 메이트 쪽 같은 이름 폴더는 그대로 둠");
        Check.That(File.Exists(Path.Combine(target, "sample-cli", "SKILL.md")), "skills: 링크(정션)로 스킬 파일이 보임");
        Directory.CreateDirectory(Path.Combine(skills, "sample-new"));
        File.WriteAllText(Path.Combine(skills, "sample-new", "SKILL.md"), "x");
        Directory.Delete(Path.Combine(skills, "sample-cli"), true);
        var second = HomeSkills.Sync(enabled: true, home: home, target: target);
        Check.That(second.Linked.SequenceEqual(new[] { "sample-new" }) && second.Removed.SequenceEqual(new[] { "sample-cli" }), "skills: 새 스킬은 링크, 사라진 스킬 링크는 지움");
        var off = HomeSkills.Sync(enabled: false, home: home, target: target);
        Check.That(off.Removed.SequenceEqual(new[] { "sample-new" }) && Directory.Exists(Path.Combine(target, "acme-jira"))
                   && File.Exists(Path.Combine(skills, "sample-new", "SKILL.md")), "skills: 끄면 홈 링크만 지움 (원본은 그대로)");
        var old = Json.TryDeserialize<AppSettings>("{}");
        Check.That(old?.ShareHomeSkills == true, "skills: 기본은 켬");
        Check.Remove(root);
    }
}
