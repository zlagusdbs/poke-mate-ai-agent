using System.Reflection;
using System.Text;
using PokeMate.CoreChecks;

// Xcode 없이 XCTest 를 쓸 수 없어서 만든 맥 검증 프로그램을 옮긴 것. `dotnet run --project app-win/src/PokeMate.CoreChecks`
if (FakeTools.TryRun(args) is int toolExit) return toolExit;
Console.OutputEncoding = new UTF8Encoding(false);

// 검사는 실제 데이터 폴더(%LOCALAPPDATA%\poke-mate)를 건드리지 않는다.
var dataDir = Path.Combine(Path.GetTempPath(), $"pm-checks-data-{Guid.NewGuid():N}");
Directory.CreateDirectory(dataDir);
Environment.SetEnvironmentVariable("POKEMATE_DATA_DIR", dataDir);

var suites = typeof(Check).Assembly.GetTypes()
    .Where(t => t.IsClass && t.IsAbstract && t.IsSealed && t.Name.EndsWith("Checks", StringComparison.Ordinal))
    .Select(t => (type: t, run: t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)))
    .Where(s => s.run != null)
    .OrderBy(s => s.type.Name, StringComparer.Ordinal);

foreach (var (type, run) in suites)
{
    try
    {
        var result = run!.Invoke(null, null);
        if (result is Task task) await task;
    }
    catch (Exception e)
    {
        var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
        Check.That(false, $"{type.Name}: 예외 {inner.GetType().Name}: {inner.Message}");
    }
}

try { Directory.Delete(dataDir, true); } catch { }

Console.WriteLine($"passed: {Check.Passed}, failed: {Check.Failures}");
return Check.Failures == 0 ? 0 : 1;
