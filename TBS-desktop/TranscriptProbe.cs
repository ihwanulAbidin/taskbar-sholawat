using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Uji takarir YouTube + sinkron baris lewat posisi GSMTC (tanpa Whisper).
/// Pemakaian: TaskbarSholawat.exe --selftest --transcript [--video ID] [--lang ar] [--sync 10]
/// Tanpa --video = video dari sesi media aktif (MediaContext). → transcript.log di samping exe.
/// </summary>
public static class TranscriptProbe
{
    private const int ShownLines = 40;

    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "transcript.log"), append: false) { AutoFlush = true };
        // Dikunci: trace UIA dari thread pool
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            using var media = await MediaInfo.CreateAsync();
            var argVideo = SelfTest.ArgValue(args, "--video");
            var fromMedia = argVideo is null;
            string videoId;
            if (argVideo is not null)
            {
                videoId = argVideo;
            }
            else
            {
                var snap = await media.ReadCurrentAsync() ?? throw new InvalidOperationException("Tidak ada sesi media aktif (beri --video)");
                var ctx = await MediaContext.BuildAsync(snap, msg => Log($"  {msg}"));
                Log($"Media: \"{ctx.Title}\" — {ctx.Artist} | {ctx.SourceKind} | How {ctx.How ?? "(null)"}");
                videoId = ctx.VideoId ?? throw new InvalidOperationException("Video YouTube untuk sesi media tidak ditemukan");
            }
            Log($"VideoId: {videoId}");

            var sw = Stopwatch.StartNew();
            var transcript = await TranscriptSource.FetchAsync(videoId, SelfTest.ArgValue(args, "--lang") ?? "ar", msg => Log($"  {msg}"));
            if (transcript is null)
            {
                Log($"Transkrip: tidak tersedia ({sw.ElapsedMilliseconds} ms)");
                Log($"HASIL: GAGAL");
                return 1;
            }
            var lines = transcript.Lines;
            Log($"Transkrip: {transcript.Language}/{transcript.Kind} via {transcript.Client}, {lines.Count} baris, {Fmt(lines[0].Start)}–{Fmt(lines[^1].End)}, {sw.ElapsedMilliseconds} ms");
            foreach (var line in lines.Take(ShownLines))
                Log($"  {Fmt(line.Start)}–{Fmt(line.End)} | {line.Text}");
            if (lines.Count > ShownLines)
                Log($"  … {lines.Count - ShownLines} baris lagi");

            // Sinkron: posisi media sekarang → baris aktif. Hanya bermakna bila video yang diputar = videoId
            var syncSeconds = int.TryParse(SelfTest.ArgValue(args, "--sync"), out var n) ? n : 10;
            if (fromMedia && syncSeconds > 0)
            {
                Log($"Sinkron {syncSeconds} s (posisi GSMTC terekstrapolasi → baris):");
                for (var i = 0; i < syncSeconds; i++)
                {
                    if (await media.ReadCurrentAsync() is { } now)
                    {
                        var pos = now.PositionAt(DateTimeOffset.Now);
                        var line = transcript.LineAt(pos);
                        Log($"  {Fmt(pos)} {now.Status} → {(line is null ? "(sela)" : $"[{Fmt(line.Start)}] {line.Text}")}");
                    }
                    await Task.Delay(1000);
                }
            }
            Log($"HASIL: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static string Fmt(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t:ss\\.f}");
}
