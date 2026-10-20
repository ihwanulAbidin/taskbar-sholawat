using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Data portable (settings.json, library/, models/) hidup di samping exe — PLAN bagian 7.
/// Jangan pakai AppContext.BaseDirectory: pada single-file yang mengekstrak isi, nilainya
/// bisa menunjuk ke folder ekstraksi di %TEMP%, bukan folder exe.
/// </summary>
public static class PortablePaths
{
    public static string AppDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string Models => Path.Combine(AppDir, "models");

    public static string Settings => Path.Combine(AppDir, "settings.json");

    /// <summary>Pustaka lirik pengguna (PLAN bagian 4), termasuk cache lirik hasil fetch.</summary>
    public static string Library => Path.Combine(AppDir, "library");
}
