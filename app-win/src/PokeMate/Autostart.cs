using Microsoft.Win32;

namespace PokeMate;

/// "Windows 시작 시 실행". HKCU\Software\Microsoft\Windows\CurrentVersion\Run 의 값 PokeMate 에 실행 파일 경로를 적는다 (기본 꺼짐).
/// 환경 변수 POKEMATE_AUTOSTART_KEY 가 있으면 Run 대신 HKCU 아래 그 하위 키를 쓴다 (실제 시작 항목을 건드리지 않는 확인용).
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PokeMate";

    static string KeyPath
    {
        get
        {
            var overrideKey = Environment.GetEnvironmentVariable("POKEMATE_AUTOSTART_KEY");
            return string.IsNullOrWhiteSpace(overrideKey) ? RunKey : overrideKey;
        }
    }

    /// 지금 이 앱을 시작할 때 실행하게 되어 있는지
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is string value && value.Trim().Length > 0;
            }
            catch { return false; }
        }
    }

    /// 켜면 값을 쓰고, 끄면 값을 지운다. 실패하면 false.
    public static bool Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return IsEnabled == enabled;
        }
        catch (Exception e)
        {
            App.Log($"시작 프로그램을 바꾸지 못했어요: {e.Message}");
            return false;
        }
    }
}
