using System.Diagnostics;

namespace PokeMate;

/// 파일·폴더·주소를 Windows 기본 연결 프로그램(탐색기·브라우저·편집기)으로 연다.
/// 환경 변수 POKEMATE_TEST_OPEN_LOG 가 있으면 열지 않고 그 파일에 한 줄씩 적는다 (화면 확인용).
public static class Opener
{
    public static void Open(string pathOrUrl)
    {
        var log = Environment.GetEnvironmentVariable("POKEMATE_TEST_OPEN_LOG");
        if (!string.IsNullOrEmpty(log))
        {
            try { File.AppendAllText(log, pathOrUrl + Environment.NewLine); } catch { }
            return;
        }
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception e) { App.Log($"열지 못했어요: {pathOrUrl} ({e.Message})"); }
    }
}
