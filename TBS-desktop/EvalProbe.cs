using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarSholawat;

/// <summary>
/// Evaluasi berlabel tanpa audio live dan tanpa Whisper: data tersimpan diputar ulang lewat komponen asli.
/// (a) log recognize.log (transkrip Whisper per jendela) → <see cref="AsrMatcher.Recognize"/> + <see cref="AsrTracker"/>,
/// dinilai per jendela (posisi awal jendela); (b) takarir YouTube → <see cref="CaptionAligner"/>, dinilai per detik.
/// Hitungan: benar / salah tampil / sembunyi, per video dan total tuning vs held-out. Untuk suluk/none tidak
/// ada "benar" — yang diharapkan sembunyi.
/// <para>
/// Data di <c>--eval-dir</c> (default <c>eval/</c> di samping exe; di repo: library-local/eval/):
/// <c>&lt;videoId&gt;.json</c> label per video (<see cref="LabelDto"/>), <c>sholawat/*.json</c> teks di luar pustaka
/// (format pustaka), <c>asr/*.log</c> log RecognizeProbe, <c>captions/&lt;videoId&gt;.tsv</c> cache takarir
/// "startMs\tendMs\tteks" (diambil sekali bila belum ada; berkas kosong = video tanpa takarir).
/// </para>
/// Pemakaian: TaskbarSholawat.exe --selftest --eval [--eval-dir dir] [--library dir] [--offline] → eval.log di samping exe.
/// </summary>
public static partial class EvalProbe
{
    private const string Suluk = "suluk";
    private const string None = "none";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Baris jendela RecognizeProbe: "jam 1:38.7–1:48.7 | ms | … «transkripsi» → «baris»"
    [GeneratedRegex(@"^\S+ (\d+):(\d+(?:\.\d+)?)–\S+ \| \d+ \| .*?«(.*?)» → «")]
    private static partial Regex WindowLine();

    private sealed class LabelDto
    {
        public string? VideoId { get; set; }
        public string? Title { get; set; }
        public string? Split { get; set; }
        public string? LabelSource { get; set; }
        public string? TextSource { get; set; }
        public List<string>? Known { get; set; }
        public List<SegmentDto>? Segments { get; set; }
        public List<string>? AsrLogs { get; set; }
    }

    private sealed class SegmentDto
    {
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Label { get; set; }
    }

    private sealed record Segment(TimeSpan From, TimeSpan To, string Label);

    /// <summary>[benar, salah tampil, sembunyi]</summary>
    private sealed class Tally
    {
        private readonly SortedDictionary<string, int[]> _byLabel = new(StringComparer.Ordinal);

        public IEnumerable<KeyValuePair<string, int[]>> ByLabel => _byLabel;

        public void Add(string label, string? shown)
        {
            if (!_byLabel.TryGetValue(label, out var c))
                _byLabel[label] = c = new int[3];
            c[shown is null ? 2 : shown == label ? 0 : 1]++;
        }

        public void Add(Tally other)
        {
            foreach (var (label, src) in other._byLabel)
            {
                if (!_byLabel.TryGetValue(label, out var c))
                    _byLabel[label] = c = new int[3];
                for (var i = 0; i < 3; i++)
                    c[i] += src[i];
            }
        }

        /// <summary>Lagu (label = id sholawat) dan suluk/none dijumlah terpisah.</summary>
        public (int[] Song, int[] Silent) Totals()
        {
            int[] song = new int[3], silent = new int[3];
            foreach (var (label, c) in _byLabel)
            {
                var t = label is Suluk or None ? silent : song;
                for (var i = 0; i < 3; i++)
                    t[i] += c[i];
            }
            return (song, silent);
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "eval.log"), append: false) { AutoFlush = true };
        void Log(string msg) => writer.WriteLine(msg);

        try
        {
            var evalDir = SelfTest.ArgValue(args, "--eval-dir") ?? Path.Combine(PortablePaths.AppDir, "eval");
            var libraryDir = SelfTest.ArgValue(args, "--library") ?? PortablePaths.Library;
            var offline = args.Contains("--offline");
            var library = LyricLibrary.Load(libraryDir);
            var extra = LyricLibrary.Load(evalDir);
            var all = library.Sholawat.Concat(extra.Sholawat.Where(s => library.GetSholawat(s.Id) is null)).ToList();
            Log($"eval {DateTime.Now:yyyy-MM-dd HH:mm} | pustaka {libraryDir}: {library.Sholawat.Count} | teks eval {evalDir}: {extra.Sholawat.Count}");
            foreach (var e in library.Errors.Concat(extra.Errors))
                Log($"  galat pustaka: {e}");

            var splits = new SortedDictionary<string, (Tally Caption, Tally Asr)>(StringComparer.Ordinal);
            var files = Directory.Exists(evalDir) ? Directory.EnumerateFiles(evalDir, "*.json").Order(StringComparer.Ordinal).ToList() : [];
            if (files.Count == 0)
                throw new InvalidOperationException($"Tidak ada berkas label di {evalDir}");

            foreach (var file in files)
            {
                LabelDto dto;
                List<Segment> segments;
                List<Sholawat> known;
                try
                {
                    dto = JsonSerializer.Deserialize<LabelDto>(File.ReadAllText(file), s_json) ?? throw new FormatException("kosong");
                    if (string.IsNullOrWhiteSpace(dto.VideoId))
                        throw new FormatException("videoId kosong");
                    known = (dto.Known ?? []).Select(id => all.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                                                           ?? throw new FormatException($"known \"{id}\" tidak ada di pustaka/eval")).ToList();
                    segments = (dto.Segments ?? []).Select(s => ParseSegment(s, known)).OrderBy(s => s.From).ToList();
                    for (var i = 1; i < segments.Count; i++)
                    {
                        if (segments[i].From < segments[i - 1].To)
                            throw new FormatException($"segmen tumpang tindih di {Fmt(segments[i].From)}");
                    }
                }
                catch (Exception ex) when (ex is JsonException or FormatException or IOException)
                {
                    Log($"\n{Path.GetFileName(file)}: DILEWATI — {ex.Message}");
                    continue;
                }

                var split = dto.Split?.ToLowerInvariant() is "heldout" or "held-out" ? "heldout" : "tuning";
                if (!splits.TryGetValue(split, out var acc))
                    splits[split] = acc = (new Tally(), new Tally());
                Log($"\n{dto.VideoId} [{split}, label {dto.LabelSource ?? "?"}] {dto.Title}");
                Log($"  teks: {string.Join(", ", known.Select(s => s.Id))}; segmen {segments.Count}");
                string? LabelAt(TimeSpan t) => segments.FirstOrDefault(s => s.From <= t && t < s.To)?.Label;

                // (b) takarir → CaptionAligner, per detik
                var transcript = await LoadCaptionsAsync(evalDir, dto.VideoId, offline, Log);
                if (transcript is null || known.Count == 0)
                {
                    Log($"  takarir: {(transcript is null ? "tidak ada" : "tanpa teks diketahui")}");
                }
                else
                {
                    var timings = new CaptionAligner().Align(transcript, known);
                    var tally = new Tally();
                    foreach (var seg in segments)
                    {
                        for (var t = seg.From; t < seg.To; t += TimeSpan.FromSeconds(1))
                            tally.Add(seg.Label, timings.FirstOrDefault(x => x.LineStart <= t && t < x.LineEnd)?.Sholawat.Id);
                    }
                    Log($"  takarir ({transcript.Lines.Count} event, {timings.Count} baris selaras), per detik:");
                    Report(tally, Log);
                    acc.Caption.Add(tally);
                }

                // (a) transkrip Whisper tersimpan → Recognize + tracker, per jendela
                foreach (var rel in dto.AsrLogs ?? [])
                {
                    var path = Path.Combine(evalDir, rel);
                    if (!File.Exists(path))
                    {
                        Log($"  asr {rel}: berkas tidak ada");
                        continue;
                    }
                    var matcher = new AsrMatcher(all);
                    var tracker = new AsrTracker(knownTexts: known);
                    var tally = new Tally();
                    var windows = 0;
                    foreach (var line in File.ReadLines(path))
                    {
                        var m = WindowLine().Match(line);
                        if (!m.Success)
                            continue;
                        windows++;
                        var pos = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                                                       + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                        // Sama dengan RecognizeProbe: potongan tanpa tahap yang lolos = tidak yakin
                        var rec = matcher.Recognize(m.Groups[3].Value, known, tracker.Hint);
                        var state = tracker.Push(rec.Source == AsrSource.None ? new AsrMatch(rec.Match.Query, []) : rec.Match);
                        if (LabelAt(pos) is { } label)
                            tally.Add(label, state.Status != AsrTrackerStatus.Hidden && state.Line >= 0 ? state.Sholawat?.Id : null);
                    }
                    Log($"  asr {rel} ({windows} jendela), per jendela:");
                    Report(tally, Log);
                    acc.Asr.Add(tally);
                }
            }

            Log("\nTOTAL (benar / salah tampil / sembunyi; lagu = label sholawat, diam = suluk+none)");
            foreach (var (split, (caption, asr)) in splits)
            {
                Log($"  {split,-8} takarir {Totals(caption)} | asr {Totals(asr)}");
            }

            // Baris yang seluruhnya kata seruan umum → AsrMatcher.Recognize selalu None untuk potongan berisi baris itu saja
            Log($"\nGerbang kata umum (AsrMatcher.Distinctive < {new AsrMatcherOptions().MinDistinctChars} huruf), per sholawat:");
            var minChars = new AsrMatcherOptions().MinDistinctChars;
            foreach (var s in all.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var gated = s.Lines.Select((l, i) => (l, i))
                    .Where(x => AsrMatcher.Distinctive(AsrMatcher.Fold(x.l.Ar)).Replace(" ", "").Length < minChars).ToList();
                var sample = gated.Count == 0 ? "" : $" — mis. {gated[0].i}: {gated[0].l.Ar}";
                Log($"  {s.Id}: {gated.Count}/{s.Lines.Count}{sample}");
            }
            Log("HASIL: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static Segment ParseSegment(SegmentDto s, List<Sholawat> known)
    {
        var from = ParseTime(s.From);
        var to = ParseTime(s.To);
        if (to <= from)
            throw new FormatException($"segmen {s.From}–{s.To}: akhir ≤ awal");
        var label = s.Label?.Trim() ?? throw new FormatException($"segmen {s.From}: label kosong");
        if (label.ToLowerInvariant() is Suluk or None)
            return new Segment(from, to, label.ToLowerInvariant());
        var text = known.FirstOrDefault(k => k.Id.Equals(label, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FormatException($"segmen {s.From}: label \"{label}\" bukan suluk/none/id di known");
        return new Segment(from, to, text.Id);
    }

    private static TimeSpan ParseTime(string? value)
    {
        var parts = (value ?? "").Split(':');
        if (parts.Length is < 2 or > 3)
            throw new FormatException($"waktu \"{value}\" bukan m:ss / h:mm:ss");
        var seconds = 0.0;
        foreach (var p in parts)
            seconds = seconds * 60 + double.Parse(p, CultureInfo.InvariantCulture);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<Transcript?> LoadCaptionsAsync(string evalDir, string videoId, bool offline, Action<string> log)
    {
        var path = Path.Combine(evalDir, "captions", $"{videoId}.tsv");
        if (!File.Exists(path))
        {
            if (offline)
                return null;
            var fetched = await TranscriptSource.FetchAsync(videoId, "ar");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllLinesAsync(path, fetched?.Lines.Select(l =>
                string.Create(CultureInfo.InvariantCulture, $"{(long)l.Start.TotalMilliseconds}\t{(long)l.End.TotalMilliseconds}\t{l.Text.ReplaceLineEndings(" ")}")) ?? []);
            log($"  takarir diambil: {(fetched is null ? "tidak tersedia" : $"{fetched.Language}/{fetched.Kind}")} → {path}");
        }
        var lines = File.ReadLines(path).Select(l => l.Split('\t', 3)).Where(p => p.Length == 3)
            .Select(p => new TranscriptLine(TimeSpan.FromMilliseconds(long.Parse(p[0], CultureInfo.InvariantCulture)),
                TimeSpan.FromMilliseconds(long.Parse(p[1], CultureInfo.InvariantCulture)), p[2]))
            .ToList();
        return lines.Count == 0 ? null : new Transcript(videoId, "ar", "cache", "eval", lines);
    }

    private static void Report(Tally tally, Action<string> log)
    {
        foreach (var (label, c) in tally.ByLabel)
            log($"    {label,-32} {c[0]}/{c[1]}/{c[2]}");
    }

    private static string Totals(Tally tally)
    {
        var (song, silent) = tally.Totals();
        var n = song.Sum();
        var pct = n == 0 ? "-" : (100.0 * song[0] / n).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        return $"lagu {song[0]}/{song[1]}/{song[2]} ({pct} benar), diam {silent[0]}/{silent[1]}/{silent[2]}";
    }

    private static string Fmt(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t:ss}");
}
