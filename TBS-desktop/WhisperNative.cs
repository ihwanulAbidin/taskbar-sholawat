using System.IO;
using Whisper.net.LibraryLoader;

namespace TaskbarSholawat;

/// <summary>
/// Whisper.net mencari DLL native di &lt;folder&gt;/runtimes/win-x64/, dengan folder = folder exe.
/// Pada exe single-file, DLL itu ikut di-bundle lalu diekstrak .NET ke %TEMP%\.net\&lt;app&gt;\&lt;hash&gt;\
/// (IncludeNativeLibrariesForSelfExtract), sehingga loader tidak menemukannya. Host .NET
/// mengumumkan folder ekstraksi itu lewat NATIVE_DLL_SEARCH_DIRECTORIES — arahkan loader ke sana.
/// </summary>
public static class WhisperNative
{
    /// <returns>Folder dasar yang dipakai, atau null bila tidak perlu (build biasa / bukan single-file).</returns>
    public static string? Configure()
    {
        var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

        foreach (var dir in dirs)
        {
            if (!File.Exists(Path.Combine(dir, "runtimes", "win-x64", "whisper.dll")))
                continue;

            // Loader memakai Path.GetDirectoryName(LibraryPath), jadi beri jalur berkas di dalam folder itu
            RuntimeOptions.LibraryPath = Path.Combine(dir, "whisper.dll");
            return dir;
        }
        return null;
    }
}
