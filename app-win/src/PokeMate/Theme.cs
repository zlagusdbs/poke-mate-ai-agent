using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PokeMate;

/// 맥 Theme.swift. 색은 시스템 앱 테마(밝음·어두움)를 따라가며, 화면은 DynamicResource 키로 참조한다.
public static class Theme
{
    public const double Corner = 14;
    public const double Gutter = 14;

    public static bool IsDark { get; private set; }

    /// 시스템 설정(앱 모드)을 읽어 브러시를 다시 채운다. 설정이 바뀔 때마다 부른다.
    public static void Apply()
    {
        IsDark = SystemUsesDark();
        var r = Application.Current.Resources;
        void Put(string key, string hex) => r[key] = Make(hex);
        if (IsDark)
        {
            Put("WindowBg", "#FF202020"); Put("CardBg", "#14FFFFFF"); Put("FieldBg", "#FF2D2D2D");
            Put("TextFg", "#FFF2F2F2"); Put("TextSecondary", "#FFA8A8A8"); Put("TextTertiary", "#FF7C7C7C");
            Put("AccentBrush", "#FF4CA3FF"); Put("AccentSoft", "#334CA3FF"); Put("Line", "#33FFFFFF");
            Put("HoverBg", "#1AFFFFFF"); Put("PressBg", "#33FFFFFF"); Put("ScrollThumb", "#66AAAAAA");
        }
        else
        {
            Put("WindowBg", "#FFF7F7F7"); Put("CardBg", "#0F000000"); Put("FieldBg", "#FFFFFFFF");
            Put("TextFg", "#FF1B1B1B"); Put("TextSecondary", "#FF666666"); Put("TextTertiary", "#FF8E8E8E");
            Put("AccentBrush", "#FF0A66C2"); Put("AccentSoft", "#260A66C2"); Put("Line", "#26000000");
            Put("HoverBg", "#12000000"); Put("PressBg", "#26000000"); Put("ScrollThumb", "#66707070");
        }
        Put("OrangeBrush", "#FFFF9500");
    }

    static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return false; }
    }

    static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// 지금 테마의 브러시 (키는 위 Apply 의 이름)
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
