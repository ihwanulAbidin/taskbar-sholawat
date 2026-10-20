using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Uji alur pengenalan langkah 1–3 pada sesi media aktif.
/// Pemakaian: TaskbarSholawat.exe --selftest --source → source.log di samping exe.
/// </summary>
public static class SourceProbe
{
    private const int DescriptionLines = 40;

    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "source.log"), append: false) { AutoFlush = true };
        // Dikunci: trace UIA datang dari thread pool
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            using var media = await MediaInfo.CreateAsync();
            var snap = await media.ReadCurrentAsync();
            if (snap is null)
            {
                Log($"Tidak ada sesi media aktif");
                Log($"HASIL: GAGAL");
                return 1;
            }
            Log($"GSMTC: {snap.AppId} | {snap.Status} | \"{snap.Title}\" — \"{snap.Artist}\" | EndTime {snap.EndTime.TotalSeconds:0.000} s");

            var sw = Stopwatch.StartNew();
            var ctx = await MediaContext.BuildAsync(snap, msg => Log($"  {msg}"));
            Log($"Selesai dalam {sw.ElapsedMilliseconds} ms");
            Log($"Title       : {ctx.Title}");
            Log($"Artist      : {ctx.Artist}");
            Log($"SourceApp   : {ctx.SourceApp}");
            Log($"SourceKind  : {ctx.SourceKind}");
            Log($"Site        : {ctx.Site ?? "(null)"}");
            Log($"TabTitle    : {ctx.TabTitle ?? "(null)"}");
            Log($"Url         : {ctx.Url ?? "(null)"}");
            Log($"VideoId     : {ctx.VideoId ?? "(null)"}");
            Log($"DurationSec : {ctx.DurationSec?.ToString() ?? "(null)"}");
            Log($"How         : {ctx.How ?? "(null)"}");
            if (ctx.Description is null)
            {
                Log($"Description : (null)");
            }
            else
            {
                var lines = ctx.Description.Split('\n');
                Log($"Description : {lines.Length} baris, {ctx.Description.Length} karakter; {Math.Min(lines.Length, DescriptionLines)} baris pertama:");
                foreach (var line in lines.Take(DescriptionLines))
                    Log($"  | {line.TrimEnd('\r')}");
            }
            Log($"HASIL: {(ctx.Description is null ? "SEBAGIAN (tanpa description)" : "OK")}");
            return ctx.Description is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }
}
