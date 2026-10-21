using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Uji CaptionAligner: takarir YouTube + teks diketahui → timing baris → rekaman pustaka (.json + .lrc) →
/// dimuat ulang lewat LyricLibrary → baris per posisi, tanpa Whisper.
/// Pemakaian: TaskbarSholawat.exe --selftest --align [--video ID] [--known id1,id2] [--no-save] [--sync 20]
/// Tanpa --video = video dari sesi media aktif (MediaContext); tanpa --known = sholawat pustaka yang judulnya
/// cocok, lalu blok lirik di description. --sync = baris per detik dari posisi GSMTC (hanya bila video yang
/// diputar = video yang diselaraskan). → align.log di samping exe.
/// </summary>
public static class AlignProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "align.log"), append: false) { AutoFlush = true };
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            using var media = await MediaInfo.CreateAsync();
            var videoId = SelfTest.ArgValue(args, "--video");
            var fromMedia = videoId is null;
            if (videoId is null)
            {
                var snap = await media.ReadCurrentAsync() ?? throw new InvalidOperationException("Tidak ada sesi media aktif (beri --video)");
                var ctx = await MediaContext.BuildAsync(snap, m => Log($"  {m}"));
                Log($"Media: \"{ctx.Title}\" — {ctx.Artist} | {ctx.SourceKind} | How {ctx.How ?? "(null)"}");
                videoId = ctx.VideoId ?? throw new InvalidOperationException("Video YouTube untuk sesi media tidak ditemukan");
            }
            var video = await MediaDescription.FetchAsync(videoId, m => Log($"  {m}"))
                        ?? throw new InvalidOperationException($"Halaman video {videoId} tidak terbaca");
            Log($"Video: {videoId} \"{video.Title}\" — {video.Author}, {video.LengthSeconds} s");

            var sw = Stopwatch.StartNew();
            var transcript = await TranscriptSource.FetchAsync(videoId, "ar", m => Log($"  {m}"));
            if (transcript is null)
            {
                Log($"Takarir: tidak tersedia ({sw.ElapsedMilliseconds} ms)");
                Log($"HASIL: GAGAL");
                return 1;
            }
            Log($"Takarir: {transcript.Language}/{transcript.Kind} via {transcript.Client}, {transcript.Lines.Count} event, {sw.ElapsedMilliseconds} ms");

            // Teks diketahui: --known → judul pustaka → description
            var library = LyricLibrary.Load(PortablePaths.Library);
            Log($"Pustaka {PortablePaths.Library}: {library.Sholawat.Count} sholawat, {library.Recordings.Count} rekaman, {library.Errors.Count} galat");
            var known = new List<Sholawat>();
            foreach (var id in (SelfTest.ArgValue(args, "--known") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var s = library.GetSholawat(id) ?? throw new InvalidOperationException($"--known {id}: tidak ada di pustaka");
                known.Add(s);
            }
            if (known.Count == 0 && library.FindSholawat(video.Title) is { } byTitle)
                known.Add(byTitle);
            if (known.Count == 0 && LyricExtractor.Extract(video.Description) is { Lines.Count: >= 2 } fromDesc)
                known.Add(fromDesc.ToSholawat($"desc-{videoId}", [video.Title], $"description https://www.youtube.com/watch?v={videoId}", null));
            if (known.Count == 0)
                throw new InvalidOperationException("Tidak ada teks diketahui (beri --known)");
            foreach (var s in known)
                Log($"Teks: {s.Id} ({s.Lines.Count} baris), refrain = baris 0: {s.Lines[0].Ar}");

            sw.Restart();
            var timings = new CaptionAligner().Align(transcript, known);
            Log($"Selaras: {timings.Count} baris dalam {sw.ElapsedMilliseconds} ms");
            foreach (var t in timings)
                Log($"  {Fmt(t.LineStart)}–{Fmt(t.LineEnd)} {t.Sholawat.Id}:{t.Line}{(t.Refrain ? " (refrain)" : "")} {t.Confidence:0.00} | {Short(t.Sholawat.Lines[t.Line].Ar)} ← {Short(t.Caption, 60)}");
            if (timings.Count == 0)
            {
                Log($"HASIL: GAGAL tidak ada baris yang yakin");
                return 1;
            }

            if (args.Contains("--no-save"))
            {
                Log($"HASIL: OK (tidak disimpan)");
                return 0;
            }

            // Simpan → muat ulang → baris per posisi: membuktikan LRC terbaca LyricLibrary
            var json = CaptionAligner.SaveRecording(PortablePaths.Library, $"yt-{videoId}", video.Title, video.LengthSeconds, timings);
            Log($"Disimpan: {json}");
            library = LyricLibrary.Load(PortablePaths.Library);
            foreach (var e in library.Errors)
                Log($"  galat pustaka: {e}");
            var recording = library.FindRecording(video.Title, TimeSpan.FromSeconds(video.LengthSeconds))
                            ?? throw new InvalidOperationException("Rekaman tersimpan tidak ditemukan FindRecording");
            Log($"FindRecording: {recording.Id}, {recording.Segments.Count} bagian");
            var mismatch = 0;
            foreach (var t in timings)
            {
                var mid = t.LineStart + (t.LineEnd - t.LineStart) / 2;
                var hit = LyricLibrary.Locate(recording, mid);
                var ok = hit is { } h && h.Segment.Sholawat.Id == t.Sholawat.Id && h.SholawatLine == t.Line;
                if (!ok)
                {
                    mismatch++;
                    Log($"  Locate {Fmt(mid)}: diharapkan {t.Sholawat.Id}:{t.Line}, dapat {(hit is { } x ? $"{x.Segment.Sholawat.Id}:{x.SholawatLine}" : "null")}");
                }
            }
            Log($"Locate titik tengah tiap baris: {timings.Count - mismatch}/{timings.Count} cocok");

            var syncSeconds = int.TryParse(SelfTest.ArgValue(args, "--sync"), CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (fromMedia && syncSeconds > 0)
            {
                Log($"Sinkron {syncSeconds} s (posisi GSMTC → baris rekaman):");
                for (var i = 0; i < syncSeconds; i++)
                {
                    if (await media.ReadCurrentAsync() is { } now)
                    {
                        var pos = now.PositionAt(DateTimeOffset.Now);
                        var hit = LyricLibrary.Locate(recording, pos);
                        var text = hit is { SholawatLine: >= 0 } h ? Short(h.Segment.Sholawat.Lines[h.SholawatLine].Ar) : "(sela)";
                        Log($"  {Fmt(pos)} {now.Status} → {text}");
                    }
                    await Task.Delay(1000);
                }
            }
            Log($"HASIL: {(mismatch == 0 ? "OK" : "GAGAL Locate tidak cocok")}");
            return mismatch == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static string Short(string s, int max = 40) => s.Length <= max ? s : s[..max] + "…";

    private static string Fmt(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t:ss\\.f}");
}
