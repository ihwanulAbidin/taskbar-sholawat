using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Uji alur pengenalan langkah 4 (<see cref="LyricFetcher"/>).
/// Pemakaian: TaskbarSholawat.exe --selftest --lyrics [--title "judul"] [--no-save] → lyrics.log di samping exe.
/// Tanpa --title: judul, description, dan videoId dari sesi media aktif (langkah 1–3). Hasil disimpan ke
/// library/cache kecuali --no-save.
/// </summary>
public static class LyricProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "lyrics.log"), append: false) { AutoFlush = true };
        // Dikunci: trace datang dari thread pool (description diambil paralel)
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            var title = SelfTest.ArgValue(args, "--title");
            string? description = null, videoId = null;
            if (title is null)
            {
                using var media = await MediaInfo.CreateAsync();
                if (await media.ReadCurrentAsync() is not { } snap)
                {
                    Log($"Tidak ada sesi media aktif dan --title tidak diberikan");
                    Log($"HASIL: GAGAL");
                    return 1;
                }
                var ctx = await MediaContext.BuildAsync(snap, msg => Log($"  {msg}"));
                (title, description, videoId) = (ctx.Title, ctx.Description, ctx.VideoId);
                Log($"Media: \"{title}\" | video {videoId ?? "(null)"} | description {description?.Length.ToString() ?? "(null)"} karakter");
            }

            var sw = Stopwatch.StartNew();
            var results = await LyricFetcher.FetchAsync(title, description, videoId, msg => Log($"  {msg}"));
            Log($"Selesai dalam {sw.ElapsedMilliseconds} ms: {results.Count} hasil");

            var save = !args.Contains("--no-save");
            foreach (var r in results)
            {
                Log($"== \"{r.Title}\" (Arab: {r.ArabicTitle ?? "-"}) ← {r.Source} \"{r.SourceTitle}\"");
                Log($"   {r.Lyrics.Lines.Count} baris unik, urutan {r.Lyrics.Order.Count}, refrain [{string.Join(",", r.Lyrics.Refrain)}], dukungan {r.Support}, {r.Candidates} kandidat");
                for (var i = 0; i < r.Lyrics.Lines.Count; i++)
                {
                    var l = r.Lyrics.Lines[i];
                    Log($"   {i,2} {l.Ar}");
                    if (l.Translation is not null)
                        Log($"      id: {l.Translation}");
                }
                if (save && LyricFetcher.Save(r, LyricLibrary.CacheDir(LyricLibrary.DefaultDir), msg => Log($"  {msg}")) is { } path)
                    Log($"   disimpan: {path}");
            }
            Log($"HASIL: {(results.Count > 0 ? "OK" : "TIDAK DITEMUKAN")}");
            return results.Count > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }
}
