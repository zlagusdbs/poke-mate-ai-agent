using PokeMate.Core;

namespace PokeMate.CoreChecks;

// MARK: 저장소 위치 (설치 때 적어 둔 repo-path.txt, 저장소를 옮긴 뒤)
public static class RepoPathChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "pm-repo-path-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var repo = Path.Combine(root, "새 위치", "poke-mate-ai-agent");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(repo);
        try
        {
            Check.That(AppSettings.InstalledRepoDir(install) == null, "repo-path: 파일이 없으면 null");
            File.WriteAllText(Path.Combine(install, "repo-path.txt"), repo + "\r\n");
            Check.That(AppSettings.InstalledRepoDir(install) == repo, "repo-path: 적힌 폴더 (줄 끝 공백은 뺀다)");
            File.WriteAllText(Path.Combine(install, "repo-path.txt"), Path.Combine(root, "없는 폴더"));
            Check.That(AppSettings.InstalledRepoDir(install) == null, "repo-path: 적힌 폴더가 없으면 null");

            var gone = Path.Combine(root, "옮기기 전", "poke-mate-ai-agent");
            Check.That(AppSettings.MovedRepo(gone, repo) == repo, "repo-path: 설정의 폴더가 사라졌으면 설치 때 적어 둔 위치");
            Check.That(AppSettings.MovedRepo(repo, Path.Combine(root, "다른 곳")) == repo, "repo-path: 설정의 폴더가 있으면 그대로");
            Check.That(AppSettings.MovedRepo(gone, Path.Combine(root, "다른 곳")) == gone, "repo-path: 둘 다 없으면 설정 그대로");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
