using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskbarSholawat;

/// <summary>Satu baris Arab hasil ekstraksi + baris terjemah Indonesia / latin yang mengikutinya bila ada.</summary>
public sealed record ExtractedLine(string Ar, string? Translation, string? Latin);

/// <summary>
/// <see cref="Lines"/> unik (urut kemunculan pertama) — baris kembar membuat <see cref="AsrMatcher.Align"/>
/// tidak bisa membedakan posisinya. <see cref="Order"/> = urutan nyanyian asli (indeks ke Lines, termasuk
/// pengulangan); <see cref="Refrain"/> = indeks baris yang muncul lebih dari sekali.
/// </summary>
public sealed record ExtractedLyrics(
    IReadOnlyList<ExtractedLine> Lines, IReadOnlyList<int> Order, IReadOnlyList<int> Refrain)
{
    public static ExtractedLyrics Empty { get; } = new([], [], []);

    public Sholawat ToSholawat(string id, IReadOnlyList<string> titles, string? source, string? license) =>
        new(id, titles, source, license, Lines.Select(l => new SholawatLine(l.Ar, l.Latin, l.Translation)).ToList());
}

/// <summary>
/// Alur pengenalan langkah 3–4: ambil blok lirik Arab dari teks bebas (description YouTube / isi halaman web).
/// Baris Arab = proporsi huruf Arab tinggi; hashtag, link, emoji, mention, timestamp bab dibuang; baris
/// non-Arab sesudah baris Arab dipasangkan sebagai terjemah atau latin.
/// </summary>
public static partial class LyricExtractor
{
    // Baris non-Arab berturut-turut sebanyak ini memutus blok lirik (paragraf penjelasan, daftar kredit)
    private const int MaxGap = 3;

    private const int MinArabicLetters = 6;

    private const int MaxLyricWords = 16;

    // Bagian baris tersambung minimal sepanjang ini (tanpa spasi) supaya kata pendek tidak memecah baris sah
    private const int MinGluedPart = 6;

    private const int MinRepeatRows = 3;

    // Penanda batas keras (judul bagian di HTML) — blok lirik tidak boleh menyeberanginya
    internal const string HardBreak = "\f";

    private static readonly HashSet<string> s_indonesianWords =
    [
        "yang", "dan", "kami", "kepada", "engkau", "aku", "dengan", "untuk", "dari", "ini", "itu", "tidak",
        "telah", "semoga", "wahai", "kita", "mereka", "dalam", "akan", "adalah", "bagi", "oleh", "atas", "hamba",
        "tuhan", "kekasih", "nabi", "rasul", "kau", "mu", "ku", "ia", "di", "ke", "pada", "segala", "sungguh",
        "limpahkanlah", "rahmat", "salam", "selalu", "selamanya",
    ];

    /// <summary>Blok dengan baris Arab terbanyak; kosong bila tidak ada blok ≥ <paramref name="minLines"/> baris.</summary>
    public static ExtractedLyrics Extract(string text, int minLines = 2) =>
        Blocks(text).Where(b => b.Lines.Count >= minLines).MaxBy(b => b.Order.Count) ?? ExtractedLyrics.Empty;

    /// <summary>Semua blok lirik Arab, urut kemunculan (halaman web bisa memuat beberapa sholawat).</summary>
    public static IReadOnlyList<ExtractedLyrics> Blocks(string text)
    {
        var blocks = new List<ExtractedLyrics>();
        var current = new List<(string Ar, List<string> After)>();
        var gap = 0;

        void Flush()
        {
            if (current.Count > 0)
                blocks.Add(Build(current));
            current = [];
            gap = 0;
        }

        foreach (var raw in text.Split('\n'))
        {
            // Bukan Trim(): "\f" sendiri termasuk spasi
            if (raw.TrimEnd('\r') == HardBreak)
            {
                Flush();
                continue;
            }
            var line = Clean(raw);
            if (line.Length == 0)
                continue;

            if (IsArabic(line))
            {
                // Bentuk presentasi Arab (U+FB50–U+FEFF, dari PDF/situs lama) → huruf dasar
                current.Add((line.Normalize(NormalizationForm.FormKC), []));
                gap = 0;
            }
            else if (current.Count > 0)
            {
                if (++gap > MaxGap)
                    Flush();
                else
                    current[^1].After.Add(line);
            }
        }
        Flush();
        return blocks;
    }

    /// <summary>HTML → teks per baris (blok &amp; &lt;br&gt; jadi baris baru; script/style dibuang; judul h1–h6
    /// memutus blok lirik, karena satu halaman sering memuat beberapa sholawat).</summary>
    public static string HtmlToText(string html)
    {
        var s = ScriptOrStyle().Replace(html, " ");
        s = Heading().Replace(s, $"\n{HardBreak}\n");
        // Judul tebal satu paragraf ("<p><strong>2. Sholawat Khoirul Bariyah</strong></p>") juga judul, kecuali
        // isinya baris Arab (banyak situs menebalkan baris lirik)
        s = BoldParagraph().Replace(s, m => IsArabic(WebUtility.HtmlDecode(AnyTag().Replace(m.Groups[3].Value, " ")))
            ? m.Value
            : $"\n{HardBreak}\n{m.Value}");
        s = BlockTag().Replace(s, "\n");
        s = AnyTag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        return string.Join('\n', s.Split('\n').Select(l => l == HardBreak ? l : Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0));
    }

    internal static bool IsArabic(string line)
    {
        int arabic = 0, letters = 0;
        foreach (var c in line)
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            if (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿')
                arabic++;
        }
        return arabic >= MinArabicLetters && arabic >= letters * 0.8;
    }

    private static ExtractedLyrics Build(List<(string Ar, List<string> After)> rows)
    {
        rows = SplitGlued(rows);
        var lines = new List<ExtractedLine>();
        var index = new Dictionary<string, int>();
        var order = new List<int>();
        foreach (var (ar, after) in rows)
        {
            var key = ArabicNormalizer.Normalize(ar);
            if (!index.TryGetValue(key, out var i))
            {
                i = lines.Count;
                index[key] = i;
                var (translation, latin) = Classify(after);
                lines.Add(new ExtractedLine(ar, translation, latin));
            }
            else if (lines[i].Translation is null && lines[i].Latin is null && after.Count > 0)
            {
                var (translation, latin) = Classify(after);
                lines[i] = lines[i] with { Translation = translation, Latin = latin };
            }
            order.Add(i);
        }
        return new ExtractedLyrics(lines, order, RefrainOf(order));
    }

    /// <summary>Indeks baris yang muncul lebih dari sekali dalam satu putaran: description sering menulis seluruh
    /// lirik dua kali (dinyanyikan dua kali) — tanpa ini semua baris tampak berulang (uji Ya Tarim 6o9NVDHfa2s:
    /// 21 dari 23 baris "refrain").</summary>
    internal static IReadOnlyList<int> RefrainOf(IReadOnlyList<int> order) =>
        order.Take(Period(order)).GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();

    // Panjang putaran terpendek p: order = order[..p] diulang (putaran terakhir boleh terpotong, minimal separuh dan
    // minimal MinRepeatRows baris — "A B A" tetap refrain A, bukan blok [A, B] diulang)
    private static int Period(IReadOnlyList<int> order)
    {
        for (var p = 1; p < order.Count; p++)
        {
            if (order[p] != order[0] || order.Count - p < Math.Max(MinRepeatRows, (p + 1) / 2))
                continue;
            var repeats = true;
            for (var i = p; i < order.Count && repeats; i++)
                repeats = order[i] == order[i - p];
            if (repeats)
                return p;
        }
        return order.Count;
    }

    // Baris yang sumbernya lupa diberi baris baru: "شي لله يا ولي اللهثبت خطا قدميك" = dua baris lain di blok yang
    // sama tersambung tanpa spasi → dipecah; baris non-Arab sesudahnya ikut bagian kedua
    private static List<(string Ar, List<string> After)> SplitGlued(List<(string Ar, List<string> After)> rows)
    {
        static string Key(string s) => ArabicNormalizer.Normalize(s).Replace(" ", "");
        var keys = rows.Select(r => Key(r.Ar)).ToHashSet();
        var result = new List<(string Ar, List<string> After)>(rows.Count);
        foreach (var row in rows)
        {
            var whole = Key(row.Ar);
            var split = Enumerable.Range(1, Math.Max(0, row.Ar.Length - 1))
                .Select(i => (Head: row.Ar[..i].Trim(), Tail: row.Ar[i..].Trim()))
                .FirstOrDefault(x => Key(x.Head) is { Length: >= MinGluedPart } h && h != whole && keys.Contains(h)
                                     && Key(x.Tail) is { Length: >= MinGluedPart } t && t != whole && keys.Contains(t));
            if (split.Head is null)
            {
                result.Add(row);
                continue;
            }
            result.Add((split.Head, []));
            result.Add((split.Tail, row.After));
        }
        return result;
    }

    // Baris sesudah baris Arab: yang paling "Indonesia" = terjemah (bila cukup), satu lainnya = latin
    private static (string? Translation, string? Latin) Classify(List<string> after)
    {
        var scored = after.Take(2).Select(l => (Line: l, Score: IndonesianScore(l))).ToList();
        var best = scored.OrderByDescending(x => x.Score).FirstOrDefault();
        var translation = best.Line is not null && best.Score >= 0.12 ? best.Line : null;
        var latin = scored.Select(x => x.Line).FirstOrDefault(l => l != translation);
        return (translation, latin);
    }

    // Kata fungsi + imbuhan khas (-kan, -nya, -lah selain "allah") per jumlah kata; transliterasi latin ±0
    private static double IndonesianScore(string line)
    {
        var words = Words().Matches(line.ToLowerInvariant()).Select(m => m.Value).ToList();
        if (words.Count == 0)
            return 0;
        var hits = words.Count(w => s_indonesianWords.Contains(w)
            || w.Length > 5 && (w.EndsWith("kan") || w.EndsWith("nya") || w.EndsWith("lah") && !w.EndsWith("allah")));
        return (double)hits / words.Count;
    }

    private static string Clean(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || Url().IsMatch(s) || s.StartsWith('@'))
            return "";
        s = Hashtag().Replace(s, " ");
        s = ChapterTime().Replace(s, " ");
        s = NonText().Replace(s, " ");
        // Sesudah emoji dibuang: "🔸كلمات وألحان | تراث"
        if (Credit().IsMatch(s))
            return "";
        // Tatweel hiasan ("الــــرَّحِـــيمَةْ") & pemisah shadr/'ajuz ("***", "،،،،") → bentuk pustaka " ، "
        s = s.Replace("ـ", "");
        s = HemistichSeparator().Replace(s, " ، ");
        // Penomoran bait / poin: "1.", "(2)", "١-", "•"; label "Artinya:", "Latin:" dsb.
        s = Numbering().Replace(s, "");
        s = Label().Replace(s, "");
        s = s.Trim('*', '_', '"', '“', '”', ' ', '-', '–', '•', '|', '(', ')');
        s = Spaces().Replace(s, " ").Trim();
        // Paragraf penjelasan berbahasa Arab (description video Arab) / pengantar "…:" — bukan baris lirik;
        // bait penuh "shadr ، 'ajuz" paling panjang ±14 kata
        if (IsArabic(s) && (s.Split(' ').Length > MaxLyricWords || raw.TrimEnd().EndsWith(':')))
            return "";
        return s;
    }

    [GeneratedRegex(@"https?://|www\.|\.com\b|\.id\b", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"#\S+")]
    private static partial Regex Hashtag();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?\b")]
    private static partial Regex ChapterTime();

    // Emoji & simbol (Surrogate pair, Dingbats, Misc Symbols, variation selector) — bukan huruf/tanda baca biasa
    [GeneratedRegex(@"[\p{Cs}\p{So}☀-➿️‍]")]
    private static partial Regex NonText();

    [GeneratedRegex(@"^\s*(?:\(?[0-9٠-٩]{1,3}[\).\-:]|[•·▪►✓]\s*)\s*")]
    private static partial Regex Numbering();

    [GeneratedRegex(@"^\s*(?:arab\s+latin|tulisan\s+(?:arab|latin)|bacaan\s+latin|latin(?:nya)?|arti(?:nya)?|terjemah(?:an|annya)?|makna(?:nya)?)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();

    // Kredit description video Arab: "كلمات وألحان | تراث", "أداء : …", "الهندسة الصوتية | …", "ساوند كلاود :" —
    // label kredit lalu pemisah dalam ≤ 3 kata; juga judul "كلمات أغنية "البردة""
    [GeneratedRegex(@"^[\s:.\-–|*=~_]*(?:كلمات\s+(?:أغنية|اغنية|قصيدة|نشيد|أنشودة|انشودة)|(?:ال)?(?:كلمات|ألحان|الحان|لحن|تلحين|أداء|اداء|غناء|إنشاد|انشاد|توزيع|هندسة|مونتاج|تصوير|إخراج|اخراج|إنتاج|انتاج|إشراف|اشراف|منشد|ميكس|مكس|جرافيك|ساوند|تابعونا|اشترك|اشتركوا|قناة|للتواصل|حسابات)(?:\s+\S+){0,3}?\s*[:|：\-–])")]
    private static partial Regex Credit();

    [GeneratedRegex(@"\s*(?:\*{2,}|،{2,})\s*")]
    private static partial Regex HemistichSeparator();

    // Isi tidak boleh menyeberangi paragraf/tebal lain — tanpa itu pola lazy merentang dari baris Arab sebelumnya
    [GeneratedRegex(@"<(p|div)\b[^>]*>\s*<(strong|b)\b[^>]*>((?:(?!</?(?:p|div|strong|b)\b).)*?)</\2>\s*</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BoldParagraph();

    [GeneratedRegex(@"<\s*h[1-6]\b", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<(script|style|noscript)\b.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|p|div|li|h[1-6]|tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
}
