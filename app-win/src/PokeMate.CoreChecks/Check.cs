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
