using System.Text;
using Raffinert.FuzzySharp;

namespace TaskbarSholawat;

/// <summary>Posisi terakhir yang diyakini (dari <see cref="AsrTracker"/>) untuk bonus kontinuitas.</summary>
public readonly record struct AsrHint(Sholawat Sholawat, int Line);

/// <summary>
/// Jendela <see cref="LineCount"/> baris berurutan mulai <see cref="StartLine"/>. <see cref="CurrentLine"/> =
/// baris tempat ujung potongan berada (yang sedang dinyanyikan). <see cref="Score"/> = <see cref="RawScore"/>
/// + bonus kontinuitas, skala 0–100(+bonus).
/// </summary>
public sealed record AsrCandidate(
    Sholawat Sholawat, int StartLine, int LineCount, int CurrentLine, double RawScore, double Score);

/// <summary>
/// Kandidat terbaik per sholawat, urut skor menurun. <see cref="Margin"/> = selisih skor kandidat teratas
/// ke kandidat teratas dari sholawat LAIN — kecil bila potongan cocok dengan bait yang dipakai beberapa
/// sholawat (mis. Khoirol Bariyyah di dalam Allahu Allah Lamma Nadani).
/// </summary>
public sealed record AsrMatch(string Query, IReadOnlyList<AsrCandidate> Candidates)
{
    public AsrCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;

    public double Margin => Candidates.Count switch
    {
        0 => 0,
        1 => Candidates[0].Score,
        _ => Candidates[0].Score - Candidates[1].Score,
    };
}

public sealed record AsrMatcherOptions
{
    /// <summary>Potongan 5–10 s bisa memotong 2–3 baris pendek.</summary>
    public int MaxWindowLines { get; init; } = 3;

    /// <summary>Kandidat di baris yang sama / 1–2 baris sesudah petunjuk.</summary>
    public double ContinuityBonus { get; init; } = 8;

    /// <summary>Kandidat di sholawat yang sama tapi baris lain (reff kembali ke awal, bait diulang).</summary>
    public double SameSholawatBonus { get; init; } = 2;

    /// <summary>Transkripsi sependek ini (setelah dilipat) tidak dicocokkan — terlalu umum (mis. "الله").</summary>
    public int MinQueryChars { get; init; } = 8;

    internal AsrScoreMode ScoreMode { get; init; } = AsrScoreMode.Words;
}

internal enum AsrScoreMode { Words, Skeleton, Max, Mean }

/// <summary>
/// L3 (PLAN 3.2 AsrMatcher): transkripsi Whisper satu potongan → kandidat (sholawat, baris, skor) dengan
/// fuzzy partial-ratio ke jendela 1–<see cref="AsrMatcherOptions.MaxWindowLines"/> baris berurutan.
/// </summary>
public sealed class AsrMatcher
{
    private readonly AsrMatcherOptions _options;
    private readonly List<(Sholawat Sholawat, string[] Lines, string[] Skeletons)> _corpus;

    public AsrMatcher(IEnumerable<Sholawat> sholawat, AsrMatcherOptions? options = null)
    {
        _options = options ?? new AsrMatcherOptions();
        _corpus = sholawat.Select(s =>
        {
            var lines = s.Lines.Select(l => Fold(l.Ar)).ToArray();
            return (s, lines, lines.Select(Skeleton).ToArray());
        }).ToList();
    }

    public AsrMatch Match(string transcript, AsrHint? hint = null)
    {
        var query = Fold(transcript);
        if (query.Replace(" ", "").Length < _options.MinQueryChars)
            return new AsrMatch(query, []);

        var candidates = new List<AsrCandidate>(_corpus.Count);
        var skeleton = Skeleton(query);
        foreach (var (sholawat, lines, skeletons) in _corpus)
        {
            var best = BestWindow(query, skeleton, lines, skeletons);
            if (best.Score <= 0)
                continue;

            var current = CurrentLine(query, lines, best.Start, best.Count);
            var bonus = 0.0;
            if (hint is { } h && ReferenceEquals(h.Sholawat, sholawat))
            {
                var next = current - h.Line;
                bonus = next is >= 0 and <= 2 ? _options.ContinuityBonus : _options.SameSholawatBonus;
            }
            candidates.Add(new AsrCandidate(sholawat, best.Start, best.Count, current, best.Score, best.Score + bonus));
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new AsrMatch(query, candidates);
    }

    /// <summary>
    /// ArabicNormalizer + lipat huruf yang sering tertukar pada transkripsi nyanyian (ث/ص→س, ذ→ز, ظ→ض, ق→ك,
    /// hamzah di atas wawu/ya → huruf dasarnya, hamzah lepas dibuang); selain huruf Arab → spasi.
    /// Hanya untuk pencocokan.
    /// </summary>
    public static string Fold(string text)
    {
        var normalized = ArabicNormalizer.Normalize(text);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var folded = c switch
            {
                'ث' or 'ص' => 'س',
                'ذ' => 'ز',
                'ظ' => 'ض',
                'ق' => 'ك',
                'ئ' => 'ي',
                'ؤ' => 'و',
                'ء' => '\0',
                >= 'ء' and <= 'ي' => c,
                _ => ' ',
            };
            if (folded == '\0')
                continue;
            if (folded != ' ')
                sb.Append(folded);
            else if (sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    private (int Start, int Count, double Score) BestWindow(string query, string skeleton, string[] lines, string[] skeletons)
    {
        var best = (Start: 0, Count: 0, Score: 0.0);
        for (var start = 0; start < lines.Length; start++)
        {
            var window = new StringBuilder();
            var skWindow = new StringBuilder();
            for (var count = 1; count <= _options.MaxWindowLines && start + count <= lines.Length; count++)
            {
                if (window.Length > 0)
                    window.Append(' ');
                window.Append(lines[start + count - 1]);
                skWindow.Append(skeletons[start + count - 1]);
                var words = _options.ScoreMode == AsrScoreMode.Skeleton ? 0 : WindowScore(query, window.ToString());
                var sk = _options.ScoreMode == AsrScoreMode.Words ? 0 : WindowScore(skeleton, skWindow.ToString());
                var score = _options.ScoreMode switch
                {
                    AsrScoreMode.Words => words,
                    AsrScoreMode.Skeleton => sk,
                    AsrScoreMode.Max => Math.Max(words, sk),
                    _ => (words + sk) / 2,
                };
                // Seri: jendela lebih pendek (sudah tercatat lebih dulu) dipertahankan
                if (score > best.Score)
                    best = (start, count, score);
            }
        }
        return best;
    }

    /// <summary>Kerangka konsonan: tanpa spasi & huruf vokal panjang (ا و ي), huruf berulang diringkas —
    /// Whisper memotong kata di tempat lain dan memanjangkan/menghilangkan vokal pada nyanyian.</summary>
    internal static string Skeleton(string folded)
    {
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (c is ' ' or 'ا' or 'و' or 'ي')
                continue;
            if (sb.Length == 0 || sb[^1] != c)
                sb.Append(c);
        }
        return sb.ToString();
    }

    // PartialRatio mencari bagian terbaik dari string yang lebih panjang. Jendela yang lebih pendek dari
    // potongan dikali cakupannya, supaya baris pendek & umum ("الله الله") tidak menang hanya karena
    // termuat di dalam potongan panjang
    private static double WindowScore(string query, string window)
    {
        var ratio = Fuzz.PartialRatio(query, window);
        return window.Length >= query.Length ? ratio : ratio * window.Length / query.Length;
    }

    // Ujung potongan = bagian yang paling baru dinyanyikan
    private static int CurrentLine(string query, string[] lines, int start, int count)
    {
        if (count == 1)
            return start;
        var tail = query.Length <= 12 ? query : query[^Math.Max(12, query.Length / 3)..];
        var best = start;
        var bestScore = -1.0;
        for (var i = start; i < start + count; i++)
        {
            var score = Fuzz.PartialRatio(tail, lines[i]);
            // Seri: baris lebih akhir, karena ujung potongan ada di akhir jendela
            if (score >= bestScore)
            {
                best = i;
                bestScore = score;
            }
        }
        return best;
    }
}
