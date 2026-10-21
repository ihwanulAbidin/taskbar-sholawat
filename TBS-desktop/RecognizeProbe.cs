using System.Diagnostics;
using System.Globalization;
using System.IO;
using Raffinert.FuzzySharp;

namespace TaskbarSholawat;

/// <summary>
/// Uji alur pengenalan 1→5 nyata pada media yang sedang diputar:
/// 1–3 judul/sumber/description (MediaContext) → 4 teks diketahui (pustaka, lalu LyricFetcher: description,
/// YouTube, wikisource) → 5 loopback per-proses → Whisper (prompt = teks diketahui
/// tanpa harakat) → AsrMatcher.Recognize + AsrTracker per jendela.
/// Pemakaian: TaskbarSholawat.exe --selftest --recognize [--seconds 180] [--known id1,id2] [--no-prompt]
///   [--no-web] [--process auto] + opsi AsrProbe (--hop 5 --window 10 --threads 8 --audio-ctx 512 --temp-inc 0 …)
/// → recognize.log di samping exe.
/// </summary>
public static class RecognizeProbe
{
    // Whisper memakai ±224 token prompt terakhir; teks Arab tanpa harakat ±2–3 huruf per token
    private const int MaxPromptChars = 600;

    // Kemiripan bagian judul ↔ judul pustaka (huruf saja, tanpa spasi): "alayallah" ≈ "alayaallahyaalimbihali"
    private const int TitlePartMatch = 85;

    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "recognize.log"), append: false) { AutoFlush = true };
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            // ── Langkah 1–3 ──
            using var media = await MediaInfo.CreateAsync();
            var snap = await media.ReadCurrentAsync() ?? throw new InvalidOperationException("Tidak ada sesi media aktif");
            var ctx = await MediaContext.BuildAsync(snap, m => Log($"  {m}"));
            Log($"[1-3] \"{ctx.Title}\" — {ctx.Artist} | {ctx.SourceKind} {ctx.Site} | video {ctx.VideoId ?? "-"} ({ctx.How ?? "-"}) | description {ctx.Description?.Split('\n').Length ?? 0} baris");

            // ── Langkah 4: teks diketahui ──
            var library = LyricLibrary.Load(PortablePaths.Library);
            Log($"[4] pustaka {PortablePaths.Library}: {library.Sholawat.Count} sholawat, {library.Errors.Count} galat");
            var known = new List<Sholawat>();
            foreach (var id in (SelfTest.ArgValue(args, "--known") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (library.GetSholawat(id) is { } s)
                {
                    known.Add(s);
                    Log($"[4] --known {id}: {s.Lines.Count} baris");
                }
                else
                {
                    Log($"[4] --known {id}: TIDAK ADA di pustaka");
                }
            }
            if (library.FindSholawat(ctx.Title) is { } byTitle && !known.Contains(byTitle))
            {
                known.Add(byTitle);
                Log($"[4] judul cocok pustaka (L1): {byTitle.Id}");
            }
            // Bagian judul yang sudah ada di pustaka (FindSholawat cocok per kata utuh: "ALA YALLAH" ≠ alias
            // "Ala Ya Allah" → cocokkan per huruf); sisanya dicari LyricFetcher (description → YouTube → wikisource)
            var missing = new List<string>();
            foreach (var part in LyricFetcher.TitleParts(ctx.Title))
            {
                var covered = known.FirstOrDefault(s => s.Titles.Any(t => PartMatches(part, t)))
                              ?? library.Sholawat.FirstOrDefault(s => s.Kind == SholawatKind.Song && s.Titles.Any(t => PartMatches(part, t)));
                if (covered is null)
                {
                    missing.Add(part);
                    continue;
                }
                if (!known.Contains(covered))
                    known.Add(covered);
                Log($"[4] bagian judul \"{part}\" → pustaka: {covered.Id}");
            }

            if (missing.Count > 0 && !args.Contains("--no-web"))
            {
                var fetched = await LyricFetcher.FetchAsync(ctx.Title, ctx.Description, ctx.VideoId, m => Log($"[4]   {m}"));
                foreach (var f in fetched)
                {
                    // Bagian yang sudah tertutup pustaka tidak ditambah dua kali
                    if (known.Any(s => s.Titles.Any(t => PartMatches(f.Title, t))))
                        continue;
                    known.Add(f.Lyrics.ToSholawat($"web-{known.Count}", [f.Title, .. f.ArabicTitle is null ? [] : new[] { f.ArabicTitle }], f.Source, null));
                    Log($"[4] LyricFetcher \"{f.Title}\" → {f.Lyrics.Lines.Count} baris, {f.Source} (dukungan {f.Support}/{f.Candidates})");
                }
            }
            if (known.Count == 0)
                throw new InvalidOperationException("Tidak ada teks lirik diketahui");
            foreach (var s in known)
                Log($"[4] teks: {s.Id} ({s.Lines.Count} baris) — {Short(s.Lines[0].Ar)}");

            // ── Langkah 5 ──
            var usePrompt = !args.Contains("--no-prompt");
            var prompt = usePrompt ? BuildPrompt(known) : null;
            Log($"[5] prompt: {(prompt is null ? "TIDAK ADA" : $"{prompt.Length} karakter: {Short(prompt, 120)}")}");
            var model = AsrProbe.ModelFrom(args);
            var options = AsrProbe.OptionsFrom(args, model, hop: 5, window: 10) with { Prompt = prompt };
            Log($"[5] model {Path.GetFileName(model)}; hop {options.Hop.TotalSeconds} s, jendela {options.Window.TotalSeconds} s, threads {options.Threads}, audio-ctx {options.AudioContext}, temp-inc {options.TemperatureInc?.ToString(CultureInfo.InvariantCulture) ?? "default"}");

            var target = SelfTest.ArgValue(args, "--process") ?? "auto";
            if (target == "auto")
                target = snap.AppId;
            var pid = AudioCapture.FindProcessId(target) ?? throw new InvalidOperationException($"Proses '{target}' tidak ditemukan");
            await using var capture = await AudioCapture.StartAsync(CaptureSource.Process, pid);
            Log($"[5] audio: {capture.Description}");

            var matcher = new AsrMatcher(library.Sholawat.Concat(known.Where(k => library.GetSholawat(k.Id) is null)));
            var tracker = new AsrTracker(knownTexts: known);
            var latencies = new List<long>();
            var sources = new Dictionary<AsrSource, int>();
            var tracking = 0;
            var windows = 0;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var seconds = AsrProbe.IntArg(args, "--seconds", 180);

            Log($"kolom: posisi media | ms | sumber | teks:baris skor/selisih | tracker | CPU% 1 core | transkripsi → baris pustaka");
            await using (var worker = AsrWorker.Start(capture, options, media))
            {
                self.Refresh();
                var cpuStart = self.TotalProcessorTime;
                var cpuLast = cpuStart;
                var wall = Stopwatch.StartNew();
                var wallLast = Stopwatch.StartNew();
                worker.Transcribed += r =>
                {
                    var rec = matcher.Recognize(r.Text, known, tracker.Hint);
                    // Tidak ada tahap yang lolos → potongan tidak yakin (bukan kandidat korpus mentah)
                    var state = tracker.Push(rec.Source == AsrSource.None ? new AsrMatch(rec.Match.Query, []) : rec.Match);
                    self.Refresh();
                    var now = self.TotalProcessorTime;
                    var cpu = (now - cpuLast).TotalMilliseconds / wallLast.Elapsed.TotalMilliseconds * 100;
                    cpuLast = now;
                    wallLast.Restart();
                    lock (gate)
                    {
                        windows++;
                        latencies.Add(r.ProcessingMs);
                        sources[rec.Source] = sources.GetValueOrDefault(rec.Source) + 1;
                        if (state.Status == AsrTrackerStatus.Tracking)
                            tracking++;
                    }
                    var best = rec.Match.Best;
                    var pos = r.MediaStart is { } a && r.MediaEnd is { } b ? $"{Fmt(a)}–{Fmt(b)}" : "-";
                    var pick = best is null ? "-" : $"{best.Sholawat.Id}:{best.CurrentLine} {best.Score:0}/{rec.Match.Margin:0}";
                    var shown = state.Sholawat is { } ss && state.Line >= 0 ? $"{state.Status} {ss.Id}:{state.Line}" : state.Status.ToString();
                    var lineText = best is null ? "" : Short(best.Sholawat.Lines[best.CurrentLine].Ar, 70);
                    Log($"{pos} | {r.ProcessingMs} | {rec.Source} | {pick} | {shown} | {cpu:0} | «{r.Text}» → «{lineText}»");
                };
                worker.SkippedSilent += (end, rms) => Log($"{end:HH:mm:ss} senyap (RMS {rms:0.00000})");
                worker.Failed += ex => Log($"GAGAL inferensi: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(seconds));

                self.Refresh();
                var cpuAvg = (self.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
                lock (gate)
                {
                    var sorted = latencies.Order().ToList();
                    Log($"Ringkasan: {windows} jendela; sumber {string.Join(", ", sources.Select(kv => $"{kv.Key} {kv.Value}"))}; tracker Tracking {tracking}/{windows}");
                    if (sorted.Count > 0)
                        Log($"Latensi ms: median {sorted[sorted.Count / 2]}, maks {sorted[^1]}; CPU rata-rata {cpuAvg:0}% satu core = {cpuAvg / Environment.ProcessorCount:0.0}% total");
                }
            }
            Log($"HASIL: {(windows > 0 ? "OK" : "GAGAL")}");
            return windows > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static bool PartMatches(string part, string title)
    {
        var a = Letters(part);
        var b = Letters(title);
        return a.Length >= 4 && b.Length >= 4 && Fuzz.PartialRatio(a, b) >= TitlePartMatch;
    }

    private static string Letters(string s) => LyricLibrary.TitleKey(s).Replace(" ", "");

    /// <summary>Teks diketahui tanpa harakat, bergantian per teks agar tiap sholawat medley terwakili.</summary>
    internal static string BuildPrompt(IReadOnlyList<Sholawat> known)
    {
        var queues = known.Select(s => new Queue<string>(s.Lines.Select(l => ArabicNormalizer.Normalize(l.Ar)))).ToList();
        var parts = new List<string>();
        var length = 0;
        while (queues.Any(q => q.Count > 0))
        {
            foreach (var q in queues)
            {
                if (q.Count == 0)
                    continue;
                var line = q.Dequeue();
                if (length + line.Length + 1 > MaxPromptChars)
                    return string.Join(" ", parts);
                parts.Add(line);
                length += line.Length + 1;
            }
        }
        return string.Join(" ", parts);
    }

    private static string Short(string s, int max = 45) => s.Length <= max ? s : s[..max] + "…";

    private static string Fmt(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t:ss\\.f}");
}
