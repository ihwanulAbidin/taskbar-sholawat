using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarSholawat;

public enum DisplayMode
{
    /// <summary>Mode A: di dalam taskbar (default, keputusan user 2026-10-20).</summary>
    Taskbar,
    /// <summary>Mode B: strip AppBar di sisi dalam taskbar.</summary>
    Strip,
    /// <summary>Mode C: kotak melayang seperti subtitle, bisa digeser (permintaan user 2026-10-21).</summary>
    Floating,
}

/// <summary>settings.json di samping exe (PLAN bagian 7, portable).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public DisplayMode DisplayMode { get; set; } = DisplayMode.Taskbar;

    /// <summary>Posisi kiri-atas Mode C dalam piksel fisik layar virtual; null = default tengah bawah.
    /// Bukan DIP: Left/Top WPF dikonversi dengan DPI monitor yang sedang ditempati (salah di multi-monitor beda skala).</summary>
    public int? FloatingX { get; set; }
    public int? FloatingY { get; set; }

    /// <summary>Skala teks per mode (1 = normal), diatur lewat menu / Ctrl+gulir (permintaan user 2026-10-21).</summary>
    public double TaskbarTextScale { get; set; } = 1;
    public double StripTextScale { get; set; } = 1;
    public double FloatingTextScale { get; set; } = 1;

    public const double MinTextScale = 0.5;
    public const double MaxTextScale = 3;

    public double GetTextScale(DisplayMode mode) => Math.Clamp(mode switch
    {
        DisplayMode.Strip => StripTextScale,
        DisplayMode.Floating => FloatingTextScale,
        _ => TaskbarTextScale,
    }, MinTextScale, MaxTextScale);

    public void SetTextScale(DisplayMode mode, double scale)
    {
        scale = Math.Round(Math.Clamp(scale, MinTextScale, MaxTextScale), 2);
        switch (mode)
        {
            case DisplayMode.Strip: StripTextScale = scale; break;
            case DisplayMode.Floating: FloatingTextScale = scale; break;
            default: TaskbarTextScale = scale; break;
        }
    }

    /// <summary>Berkas tidak ada / rusak → nilai default.</summary>
    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PortablePaths.Settings), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <returns>false bila folder exe tidak bisa ditulis (mis. dijalankan dari media read-only).</returns>
    public bool TrySave()
    {
        try
        {
            File.WriteAllText(PortablePaths.Settings, JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
