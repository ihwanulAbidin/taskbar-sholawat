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

    /// <summary>
    /// <see cref="AsrMatcher.Recognize"/>: huruf minimal setelah kata seruan umum dibuang (<see
    /// cref="AsrMatcher.Distinctive"/>). Uji nyata 2026-10-21: suluk «يا رب يا رب يا رب» terbaca baris 0 Ala Yallah
    /// («يا رب يا عالم بحالي») 14/17 jendela, «شيء لله» berulang terbaca Ya Habibana — lebih baik sembunyi.
    /// </summary>
    public int MinDistinctChars { get; init; } = 6;
}

/// <summary>Lolos bila skor kandidat teratas ≥ <see cref="MinScore"/> dan selisih ≥ <see cref="MinMargin"/>.</summary>
public sealed record AsrGate(double MinScore, double MinMargin)
{
    public bool Passes(AsrMatch match) =>
        match.Best is { } best && best.Score >= MinScore && match.Margin >= MinMargin;
}

/// <summary>Gerbang per tahap <see cref="AsrMatcher.Recognize"/>. Suluk ketat: dilantunkan bebas, dan potongan
/// lagu utama tidak boleh terbaca sebagai suluk.</summary>
public sealed record AsrCascadeOptions
{
    public AsrGate Known { get; init; } = new(AsrTrackerOptions.KnownText.MinScore, AsrTrackerOptions.KnownText.MinMargin);
    public AsrGate Suluk { get; init; } = new(75, 8);
    public AsrGate Corpus { get; init; } = new(new AsrTrackerOptions().MinScore, new AsrTrackerOptions().MinMargin);

    /// <summary>Teks-diketahui kalah bila sholawat lain di pustaka mendapat skor mentah setinggi ini di atasnya.</summary>
    public double KnownOverride { get; init; } = 5;
}

public enum AsrSource
{
    /// <summary>Tidak ada tahap yang yakin.</summary>
    None,

    /// <summary>Lirik lagu yang diketahui dari judul/description/web.</summary>
    KnownText,

    Suluk,

    /// <summary>Korpus lagu (judul tidak membantu).</summary>
    Corpus,
}

/// <summary>Jenis tampilan: <see cref="AsrSource.Suluk"/> = suluk, selainnya lagu.</summary>
public sealed record AsrRecognition(AsrSource Source, AsrMatch Match)
{
    public AsrCandidate? Best => Source == AsrSource.None ? null : Match.Best;
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

    /// <summary>Korpus terbuka: semua sholawat, atau hanya satu jenis (<paramref name="kind"/>).</summary>
    public AsrMatch Match(string transcript, AsrHint? hint = null, SholawatKind? kind = null)
    {
        var query = Fold(transcript);
        if (query.Replace(" ", "").Length < _options.MinQueryChars)
            return new AsrMatch(query, []);

        var candidates = new List<AsrCandidate>(_corpus.Count);
        foreach (var (sholawat, lines) in _corpus)
        {
            if (kind is { } k && sholawat.Kind != k)
                continue;
            var best = BestWindow(query, lines);
            if (best.Score <= 0)
                continue;

            var current = CurrentLine(query, lines, best.Start, best.Count);
            var c = new AsrCandidate(sholawat, best.Start, best.Count, current, best.Score, best.Score);
            candidates.Add(c with { Score = best.Score + Bonus(c, hint) });
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new AsrMatch(query, candidates);
    }

    /// <summary>
    /// Mode teks-diketahui (alur user langkah 5): sholawat sudah diketahui dari judul/description/web — satu
    /// teks, atau 2–3 teks untuk judul medley — jadi potongan hanya diselaraskan ke baris di dalam
    /// <paramref name="known"/>. Kandidat = (teks, baris) berbeda; <see cref="AsrMatch.Margin"/> diukur ke
    /// kandidat yang bukan baris bersebelahan di teks yang sama, karena potongan di perbatasan dua baris wajar
    /// mendapat skor mirip.
    /// </summary>
    public AsrMatch Align(string transcript, IReadOnlyList<Sholawat> known, AsrHint? hint = null)
    {
        var query = Fold(transcript);
        if (query.Replace(" ", "").Length < _options.MinQueryChars || known.Count == 0)
            return new AsrMatch(query, []);

        var byLine = new Dictionary<(Sholawat, int), AsrCandidate>();
        foreach (var text in known)
        {
            var lines = text.Lines.Select(l => Fold(l.Ar)).ToArray();
            foreach (var w in Windows(query, lines))
            {
                if (w.Score <= 0)
                    continue;
                var current = CurrentLine(query, lines, w.Start, w.Count);
                var c = new AsrCandidate(text, w.Start, w.Count, current, w.Score, w.Score);
                c = c with { Score = w.Score + Bonus(c, hint) };
                if (!byLine.TryGetValue((text, current), out var old) || c.Score > old.Score)
                    byLine[(text, current)] = c;
            }
        }

        var sorted = byLine.Values.OrderByDescending(c => c.Score).ToList();
        if (sorted.Count == 0)
            return new AsrMatch(query, []);
        var top = sorted[0];
        var rival = sorted.Skip(1).FirstOrDefault(c =>
            !ReferenceEquals(c.Sholawat, top.Sholawat) || Math.Abs(c.CurrentLine - top.CurrentLine) > 1);
        var rest = sorted.Skip(1).Where(c => c != rival);
        return new AsrMatch(query, rival is null ? [top, .. rest] : [top, rival, .. rest]);
    }

    public AsrMatch Align(string transcript, Sholawat known, AsrHint? hint = null) =>
        Align(transcript, [known], hint);

    /// <summary>
    /// Urutan pencocokan (alur user): (a) teks yang diketahui dari judul/description/web → (b) kumpulan suluk,
    /// gerbang ketat → (c) korpus lagu. Hasil pertama yang lolos gerbangnya dipakai; tidak ada → <see
    /// cref="AsrSource.None"/> dengan hasil tahap terakhir (untuk log). Gerbang (a) longgar, jadi (a) ditolak bila
    /// sholawat lain di pustaka jelas lebih cocok — judul bisa salah dikenali (uji nyata 2026-10-21: teks salah
    /// "Ala Ya Allah" menerima refrain Burdah dengan gerbang longgar).
    /// </summary>
    public AsrRecognition Recognize(
        string transcript, IReadOnlyList<Sholawat>? known = null, AsrHint? hint = null, AsrCascadeOptions? gates = null)
    {
        gates ??= new AsrCascadeOptions();
        // Hanya seruan umum (يا رب، الله، شيء لله، صلوا على محمد …) → tidak cukup untuk menunjuk baris mana pun
        if (Distinctive(Fold(transcript)).Replace(" ", "").Length < _options.MinDistinctChars)
            return new AsrRecognition(AsrSource.None, new AsrMatch(Fold(transcript), []));

        var suluk = Match(transcript, hint, SholawatKind.Suluk);
        var corpus = Match(transcript, hint, SholawatKind.Song);

        if (known is { Count: > 0 })
        {
            var aligned = Align(transcript, known, hint);
            var rival = suluk.Candidates.Concat(corpus.Candidates)
                .Where(c => !known.Contains(c.Sholawat))
                .Select(c => c.RawScore)
                .DefaultIfEmpty(0)
                .Max();
            if (gates.Known.Passes(aligned) && rival < aligned.Best!.RawScore + gates.KnownOverride)
                return new AsrRecognition(AsrSource.KnownText, aligned);
        }

        if (gates.Suluk.Passes(suluk))
            return new AsrRecognition(AsrSource.Suluk, suluk);
        return new AsrRecognition(gates.Corpus.Passes(corpus) ? AsrSource.Corpus : AsrSource.None, corpus);
    }

    // Kata seruan yang muncul di hampir semua sholawat/suluk (bentuk Fold; awalan و/ف/ب/ل ikut dibuang)
    private static readonly HashSet<string> CommonWords =
    [
        "يا", "اي", "ايا", "رب", "ربي", "ربنا", "الله", "لله", "اللهم", "يالله", "الا", "الى", "على", "علي", "عليه",
        "عليك", "عليا", "علينا", "محمد", "احمد", "صلي", "صل", "صلوا", "سلم", "سلموا", "صلاه", "صلاته", "شي", "شيء",
        "حبيبي", "حبيبنا", "رسول", "النبي", "نبي", "نبينا", "طه", "مولاي", "مولانا", "اهلا", "سهلا", "مرحبا", "اه", "ها",
        "لا", "اله", "هو",
    ];

    /// <summary>Kata yang tersisa setelah kata seruan umum dibuang (input sudah <see cref="Fold"/>).</summary>
    public static string Distinctive(string folded) =>
        string.Join(' ', folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !IsCommon(w)));

    private static bool IsCommon(string word) =>
        CommonWords.Contains(word)
        || word.Length > 2 && word[0] is 'و' or 'ف' or 'ب' or 'ل' && CommonWords.Contains(word[1..]);

    private double Bonus(AsrCandidate c, AsrHint? hint)
    {
        if (hint is not { } h || !ReferenceEquals(h.Sholawat, c.Sholawat))
            return 0;
        return h.Line >= 0 && c.CurrentLine - h.Line is >= 0 and <= 2 ? _options.ContinuityBonus : _options.SameSholawatBonus;
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
