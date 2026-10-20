using System.Windows.Media;
using Microsoft.Win32;

namespace TaskbarSholawat;

/// <summary>Warna mengikuti tema taskbar (SystemUsesLightTheme), bukan tema aplikasi.</summary>
public static class TaskbarTheme
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Brush LightForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    // Mode B: kira-kira warna taskbar Win11 (tanpa Mica) supaya strip menyatu dengan taskbar
    private static readonly Brush LightBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)));
    private static readonly Brush DarkBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));

    public static bool IsLight => Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 0) is int v && v != 0;

    public static Brush Foreground => IsLight ? LightForeground : Brushes.White;

    public static Brush StripBackground => IsLight ? LightBackground : DarkBackground;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
