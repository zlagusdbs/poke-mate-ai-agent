namespace PokeMate.CoreChecks;

/// 맥 `PokeMateCoreChecks` 의 `check(_:_:)` 와 같은 검사 한 줄.
public static class Check
{
    public static int Passed;
    public static int Failures;

    public static void That(bool condition, string name)
    {
        if (condition) { Passed++; }
        else { Failures++; Console.WriteLine($"FAIL: {name}"); }
    }

    /// 검사 안에서 예외가 나도 다음 검사를 계속한다 (맥의 `try?` 처럼).
    public static void That(Func<bool> condition, string name)
    {
        bool ok;
        try { ok = condition(); }
        catch (Exception e) { ok = false; Console.WriteLine($"  ({name}: {e.GetType().Name}: {e.Message})"); }
        That(ok, name);
    }

    /// 맥 검사의 `Asia/Seoul` 달력.
    public static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    public static DateTimeOffset SeoulTime(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(9));

    /// 앱 데이터 폴더(POKEMATE_DATA_DIR 의 임시 폴더)의 설정에 임시 저장소를 repoDir 로 적는다.
    /// 규칙·알람·메모처럼 repoDir 아래를 읽고 쓰는 검사가 실제 저장소(`%USERPROFILE%\Workspace\poke-mate-ai-agent`)를 건드리지 않게 한다.
    public sealed class TempRepo : IDisposable
    {
        public string Path { get; }
        public string Shared => System.IO.Path.Combine(Path, "shared");
        public string Local => System.IO.Path.Combine(Path, "local");

        readonly string? previous;

        public TempRepo(bool withLocal = true)
        {
            try { previous = File.ReadAllText(PokeMate.Core.AppPaths.SettingsFile); } catch { previous = null; }
            Path = TempDir("pm-repo");
            Directory.CreateDirectory(Shared);
            if (withLocal) Directory.CreateDirectory(Local);
            var settings = PokeMate.Core.AppSettings.Default;
            settings.RepoDir = Path;
            if (!PokeMate.Core.AppPaths.SaveSettings(settings)) throw new IOException("설정을 임시 저장소로 바꾸지 못했어요");
        }

        public void Dispose()
        {
            try
            {
                if (previous == null) File.Delete(PokeMate.Core.AppPaths.SettingsFile);
                else File.WriteAllText(PokeMate.Core.AppPaths.SettingsFile, previous);
            }
            catch { }
            Remove(Path);
        }
    }

    /// 검사용 임시 폴더 (실제 데이터 폴더·홈을 건드리지 않게).
    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
