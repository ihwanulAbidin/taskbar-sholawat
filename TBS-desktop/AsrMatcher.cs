using System.Text;
using System.Text.RegularExpressions;
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
/// <see cref="AsrMatcher.Match"/>: kandidat terbaik per sholawat; <see cref="AsrMatcher.Align"/>: kandidat per
/// baris di satu sholawat (lihat di sana). Urut skor menurun, kecuali Align menaruh pesaing tak-bersebelahan
/// di urutan ke-2. <see cref="Margin"/> = selisih kandidat teratas ke kandidat ke-2: pada Match, sholawat LAIN —
/// kecil bila potongan cocok dengan bait yang dipakai beberapa sholawat (mis. Khoirol Bariyyah di dalam
/// Allahu Allah Lamma Nadani).
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
}

/// <summary>
/// L3 (PLAN 3.2 AsrMatcher): transkripsi Whisper satu potongan → kandidat (sholawat, baris, skor) dengan
/// fuzzy partial-ratio ke jendela 1–<see cref="AsrMatcherOptions.MaxWindowLines"/> baris berurutan.
/// </summary>
public sealed partial class AsrMatcher
{
    private readonly AsrMatcherOptions _options;
    private readonly List<(Sholawat Sholawat, string[] Lines)> _corpus;

    public AsrMatcher(IEnumerable<Sholawat> sholawat, AsrMatcherOptions? options = null)
    {
        _options = options ?? new AsrMatcherOptions();
        _corpus = sholawat.Select(s => (s, s.Lines.Select(l => Fold(l.Ar)).ToArray())).ToList();
    }

    public AsrMatch Match(string transcript, AsrHint? hint = null)
    {
        var query = Fold(transcript);
        if (query.Replace(" ", "").Length < _options.MinQueryChars)
            return new AsrMatch(query, []);

        var candidates = new List<AsrCandidate>(_corpus.Count);
        foreach (var (sholawat, lines) in _corpus)
        {
            var best = BestWindow(query, lines);
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
    /// Mode teks-diketahui (alur user langkah 5): sholawat sudah diketahui dari judul/description/web, jadi
    /// potongan hanya diselaraskan ke baris di dalam <paramref name="known"/>. Kandidat = baris berbeda
    /// (terbaik per <see cref="AsrCandidate.CurrentLine"/>); <see cref="AsrMatch.Margin"/> diukur ke kandidat
    /// yang barisnya berjarak &gt; 1, karena potongan di perbatasan dua baris wajar mendapat skor mirip.
    /// </summary>
    public AsrMatch Align(string transcript, Sholawat known, AsrHint? hint = null)
    {
        var query = Fold(transcript);
        if (query.Replace(" ", "").Length < _options.MinQueryChars)
            return new AsrMatch(query, []);

        var lines = known.Lines.Select(l => Fold(l.Ar)).ToArray();
        var byLine = new Dictionary<int, AsrCandidate>();
        foreach (var w in Windows(query, lines))
        {
            if (w.Score <= 0)
                continue;
            var current = CurrentLine(query, lines, w.Start, w.Count);
            var bonus = 0.0;
            if (hint is { } h && ReferenceEquals(h.Sholawat, known) && h.Line >= 0)
                bonus = current - h.Line is >= 0 and <= 2 ? _options.ContinuityBonus : 0;
            var c = new AsrCandidate(known, w.Start, w.Count, current, w.Score, w.Score + bonus);
            if (!byLine.TryGetValue(current, out var old) || c.Score > old.Score)
                byLine[current] = c;
        }

        var sorted = byLine.Values.OrderByDescending(c => c.Score).ToList();
        if (sorted.Count == 0)
            return new AsrMatch(query, []);
        // Kandidat ke-2 = baris terbaik yang tidak bersebelahan dengan baris teratas
        var top = sorted[0];
        var rival = sorted.Skip(1).FirstOrDefault(c => Math.Abs(c.CurrentLine - top.CurrentLine) > 1);
        var rest = sorted.Skip(1).Where(c => c != rival);
        return new AsrMatch(query, rival is null ? [top, .. rest] : [top, rival, .. rest]);
    }

    /// <summary>
    /// ArabicNormalizer + lipat huruf yang sering tertukar pada transkripsi nyanyian (ث/ص→س, ذ→ز, ظ→ض, ق→ك,
    /// hamzah di atas wawu/ya → huruf dasarnya, hamzah lepas dibuang); selain huruf Arab → spasi; tahan cengkok
    /// (huruf diulang ≥ 3 kali, kata diulang berturut). Hanya untuk pencocokan.
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

        // Cengkok/vokal panjang: huruf sama ≥ 3 kali berturut jadi satu. Dua kali dibiarkan ("لل" di "الله"
        // sah). Kata yang diulang berturut (lantunan / Whisper berulang) dihitung sekali
        var words = LetterRun().Replace(sb.ToString(), "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Where((w, i) => i == 0 || w != words[i - 1]));
    }

    [GeneratedRegex(@"(\p{L})\1{2,}")]
    private static partial Regex LetterRun();

    private (int Start, int Count, double Score) BestWindow(string query, string[] lines)
    {
        var best = (Start: 0, Count: 0, Score: 0.0);
        foreach (var w in Windows(query, lines))
        {
            // Seri: jendela lebih pendek (sudah tercatat lebih dulu) dipertahankan
            if (w.Score > best.Score)
                best = w;
        }
        return best;
    }

    // Skor kerangka konsonan (tanpa spasi & vokal panjang) sempat diuji 2026-10-21: skor naik, tapi positif
    // palsu ikut naik (sintetis sampai 5–6% vs ≤ 1% pada ambang 70; uji nyata Burdah memberi tebakan salah
    // berstatus yakin) — tidak dipakai
    private IEnumerable<(int Start, int Count, double Score)> Windows(string query, string[] lines)
    {
        for (var start = 0; start < lines.Length; start++)
        {
            var window = new StringBuilder();
            for (var count = 1; count <= _options.MaxWindowLines && start + count <= lines.Length; count++)
            {
                if (window.Length > 0)
                    window.Append(' ');
                window.Append(lines[start + count - 1]);
                yield return (start, count, WindowScore(query, window.ToString()));
            }
        }
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
