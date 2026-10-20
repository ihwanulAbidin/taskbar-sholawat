using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarSholawat;

public sealed record SholawatLine(string Ar, string? Latin, string? Translation);

public sealed record Sholawat(
    string Id, IReadOnlyList<string> Titles, string? Source, string? License, IReadOnlyList<SholawatLine> Lines);

/// <summary>
/// Satu bagian medley. Waktu <see cref="Timing"/> relatif terhadap <see cref="Start"/> — LRC dibuat
/// per potongan bagian (note.md bagian 2 langkah 1). <see cref="LineMap"/>: indeks baris LRC →
/// indeks baris sholawat (-1 bila teks LRC tidak ditemukan di sholawat), untuk mengambil terjemah.
/// </summary>
public sealed record RecordingSegment(
    TimeSpan Start, TimeSpan End, Sholawat Sholawat, Lrc? Timing, IReadOnlyList<int> LineMap);

public sealed record Recording(
    string Id, IReadOnlyList<string> TitleContains, double? DurationSec, IReadOnlyList<RecordingSegment> Segments);

/// <summary>
/// <paramref name="TimingLine"/>/<paramref name="SholawatLine"/>/<paramref name="Word"/> = -1 bila tidak
/// diketahui (bagian tanpa LRC, jeda sebelum baris pertama, atau kata tanpa timing).
/// </summary>
public readonly record struct LyricPosition(
    Recording Recording, RecordingSegment Segment, int TimingLine, int SholawatLine, int Word);

/// <summary>
/// Pustaka lirik <c>library/</c> di samping exe (PLAN bagian 4). Berkas rusak dilewati dan dicatat di
/// <see cref="Errors"/>; folder tidak ada = pustaka kosong.
/// </summary>
public sealed class LyricLibrary
{
    // Durasi GSMTC vs durationSec di pustaka (pembulatan / iklan pendek di awal)
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Sholawat> _sholawat;
    private readonly List<(string Key, Sholawat Sholawat)> _titleIndex;
    private readonly List<(string Key, Recording Recording)> _recordingIndex;

    private LyricLibrary(Dictionary<string, Sholawat> sholawat, List<Recording> recordings, List<string> errors)
    {
        _sholawat = sholawat;
        Recordings = recordings;
        Errors = errors;
        _titleIndex = sholawat.Values
            .SelectMany(s => s.Titles.Select(t => (Key: TitleKey(t), Sholawat: s)))
            .Where(e => e.Key.Length > 0)
            .ToList();
        _recordingIndex = recordings
            .SelectMany(r => r.TitleContains.Select(t => (Key: TitleKey(t), Recording: r)))
            .Where(e => e.Key.Length > 0)
            .ToList();
    }

    public static string DefaultDir => Path.Combine(PortablePaths.AppDir, "library");

    public IReadOnlyCollection<Sholawat> Sholawat => _sholawat.Values;

    public IReadOnlyList<Recording> Recordings { get; }

    public IReadOnlyList<string> Errors { get; }

    public Sholawat? GetSholawat(string id) => _sholawat.GetValueOrDefault(id);

    public static LyricLibrary Load(string dir)
    {
        var errors = new List<string>();
        var sholawat = new Dictionary<string, Sholawat>(StringComparer.OrdinalIgnoreCase);
        var sholawatFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recordings = new List<Recording>();

        // Urutan nama berkas ordinal: bila id kembar, berkas pertama menang
        foreach (var path in JsonFiles(Path.Combine(dir, "sholawat")))
        {
            try
            {
                var s = ReadSholawat(path);
                if (sholawat.TryAdd(s.Id, s))
                    sholawatFiles[s.Id] = path;
                else
                    errors.Add($"{path}: id \"{s.Id}\" sudah dipakai {Path.GetFileName(sholawatFiles[s.Id])}, dilewati");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        foreach (var path in JsonFiles(Path.Combine(dir, "recordings")))
        {
            try
            {
                var r = ReadRecording(path, sholawat, errors);
                if (r.Segments.Count == 0)
                    errors.Add($"{path}: tidak ada bagian yang valid, dilewati");
                else
                    recordings.Add(r);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        return new LyricLibrary(sholawat, recordings, errors);
    }

    /// <summary>
    /// L1: rekaman yang <c>titleContains</c>-nya muncul di judul media. Bila durasi keduanya diketahui
    /// dan selisihnya lebih dari toleransi, rekaman itu dilewati. Pola terpanjang menang.
    /// </summary>
    public Recording? FindRecording(string mediaTitle, TimeSpan? duration = null)
    {
        var key = TitleKey(mediaTitle);
        return _recordingIndex
            .Where(e => ContainsWords(key, e.Key))
            .Where(e => duration is not { } d || e.Recording.DurationSec is not { } sec
                        || (d - TimeSpan.FromSeconds(sec)).Duration() <= DurationTolerance)
            .OrderByDescending(e => e.Key.Length)
            .Select(e => e.Recording)
            .FirstOrDefault();
    }

    /// <summary>L1: sholawat yang salah satu judul/aliasnya muncul di judul media. Alias terpanjang menang.</summary>
    public Sholawat? FindSholawat(string mediaTitle)
    {
        var key = TitleKey(mediaTitle);
        return _titleIndex
            .Where(e => ContainsWords(key, e.Key))
            .OrderByDescending(e => e.Key.Length)
            .Select(e => e.Sholawat)
            .FirstOrDefault();
    }

    /// <summary>Posisi pemutaran pada rekaman → bagian, baris, kata. Null bila di luar semua bagian.</summary>
    public static LyricPosition? Locate(Recording recording, TimeSpan position)
    {
        foreach (var seg in recording.Segments)
        {
            if (position < seg.Start || position >= seg.End)
                continue;
            if (seg.Timing is not { } lrc)
                return new LyricPosition(recording, seg, -1, -1, -1);

            var local = position - seg.Start;
            var line = lrc.LineIndexAt(local);
            return new LyricPosition(recording, seg, line,
                line < 0 ? -1 : seg.LineMap[line],
                lrc.WordIndexAt(line, local));
        }
        return null;
    }

    /// <summary>
    /// Kunci judul: normalisasi Arab, huruf kecil, tanda baca → spasi. Normalisasi lebih dulu supaya
    /// harakat (bukan huruf) dibuang, bukan dijadikan pemisah kata.
    /// </summary>
    internal static string TitleKey(string title)
    {
        var normalized = ArabicNormalizer.Normalize(title).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    // Cocok per kata utuh: "ya tarim" cocok dengan "ya tarim ya habibana", "rim" tidak
    private static bool ContainsWords(string haystack, string needle) =>
        $" {haystack} ".Contains($" {needle} ", StringComparison.Ordinal);

    private static IEnumerable<string> JsonFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal) : [];

    private static Sholawat ReadSholawat(string path)
    {
        var dto = JsonSerializer.Deserialize<SholawatDto>(File.ReadAllText(path), s_json)
                  ?? throw new FormatException("berkas kosong");
        var lines = (dto.Lines ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l?.Ar))
            .Select(l => new SholawatLine(l!.Ar!.Trim(), l.Latin, l.Id))
            .ToList();
        if (lines.Count == 0)
            throw new FormatException("tidak ada baris \"ar\"");

        var id = string.IsNullOrWhiteSpace(dto.Id) ? Path.GetFileNameWithoutExtension(path) : dto.Id.Trim();
        var titles = (dto.Titles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()).ToList();
        return new Sholawat(id, titles, dto.Source, dto.License, lines);
    }

    private static Recording ReadRecording(string path, Dictionary<string, Sholawat> sholawat, List<string> errors)
    {
        var dto = JsonSerializer.Deserialize<RecordingDto>(File.ReadAllText(path), s_json)
                  ?? throw new FormatException("berkas kosong");
        var dir = Path.GetDirectoryName(path)!;
        var segments = new List<RecordingSegment>();

        for (var i = 0; i < (dto.Segments?.Count ?? 0); i++)
        {
            var seg = dto.Segments![i];
            var where = $"{path}: bagian {i + 1}";
            if (seg is null || seg.End <= seg.Start || seg.Start < 0)
            {
                errors.Add($"{where}: start/end tidak valid, dilewati");
                continue;
            }
            if (seg.Sholawat is null || !sholawat.TryGetValue(seg.Sholawat, out var s))
            {
                errors.Add($"{where}: sholawat \"{seg.Sholawat}\" tidak ada di pustaka, dilewati");
                continue;
            }

            Lrc? timing = null;
            if (!string.IsNullOrWhiteSpace(seg.Timing))
            {
                var lrcPath = Path.Combine(dir, seg.Timing);
                try
                {
                    timing = Lrc.Load(lrcPath);
                    errors.AddRange(timing.Errors.Select(e => $"{lrcPath}: {e}"));
                    if (timing.Lines.Count == 0)
                    {
                        errors.Add($"{lrcPath}: tidak ada baris bertimestamp, bagian tanpa timing");
                        timing = null;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{where}: timing tidak terbaca ({ex.Message}), bagian tanpa timing");
                }
            }

            segments.Add(new RecordingSegment(
                TimeSpan.FromSeconds(seg.Start), TimeSpan.FromSeconds(seg.End), s, timing,
                timing is null ? [] : MapLines(timing, s)));
        }

        var id = string.IsNullOrWhiteSpace(dto.Id) ? Path.GetFileNameWithoutExtension(path) : dto.Id.Trim();
        var titleContains = (dto.Match?.TitleContains ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        return new Recording(id, titleContains, dto.Match?.DurationSec, segments.OrderBy(s => s.Start).ToList());
    }

    // Baris LRC = teks persis seperti dinyanyikan (pengulangan ditulis ulang); cari baris sholawat yang sama,
    // atau yang memuatnya bila LRC memecah satu bait menjadi beberapa baris
    private static int[] MapLines(Lrc timing, Sholawat s)
    {
        var normalized = s.Lines.Select(l => ArabicNormalizer.Normalize(l.Ar)).ToList();
        return timing.Lines.Select(l =>
        {
            var text = ArabicNormalizer.Normalize(l.Text);
            if (text.Length == 0)
                return -1;
            var exact = normalized.IndexOf(text);
            return exact >= 0 ? exact : normalized.FindIndex(n => n.Contains(text, StringComparison.Ordinal));
        }).ToArray();
    }

    private sealed class SholawatDto
    {
        public string? Id { get; set; }
        public List<string?>? Titles { get; set; }
        public string? Source { get; set; }
        public string? License { get; set; }
        public List<LineDto?>? Lines { get; set; }
    }

    private sealed class LineDto
    {
        public string? Ar { get; set; }
        public string? Latin { get; set; }

        /// <summary>"id" = terjemah bahasa Indonesia (PLAN bagian 4), bukan pengenal</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class RecordingDto
    {
        public string? Id { get; set; }
        public MatchDto? Match { get; set; }
        public List<SegmentDto?>? Segments { get; set; }
    }

    private sealed class MatchDto
    {
        public List<string?>? TitleContains { get; set; }
        public double? DurationSec { get; set; }
    }

    private sealed class SegmentDto
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Sholawat { get; set; }
        public string? Timing { get; set; }
    }
}
