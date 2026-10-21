using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raffinert.FuzzySharp;

namespace TaskbarSholawat;

/// <param name="Title">Bagian judul media yang dicari (judul medley dipecah per bagian).</param>
/// <param name="Source">URL teks terpilih.</param>
/// <param name="Support">Baris teks terpilih yang juga ditemukan di sumber lain (hasil pencarian ≥ 2); 0 = description
/// video yang diputar, tanpa pembanding — langkah 5 yang memastikan.</param>
/// <param name="Candidates">Jumlah sumber yang memberi blok lirik.</param>
public sealed record FetchedLyrics(
    string Title, string? ArabicTitle, ExtractedLyrics Lyrics, string Source, string SourceTitle, int Support, int Candidates);

/// <summary>
/// Alur pengenalan langkah 4: teks sholawat dari web. Hanya dipanggil bila <see cref="AppSettings.SearchLyricsOnline"/>
/// aktif (judul media dikirim ke YouTube/Wikisource); hasilnya hanya disimpan di cache lokal (<see cref="Save"/>).
/// Urutan: description video yang diputar → YouTube "&lt;judul&gt; lirik" → YouTube berjudul Arab → ar.wikisource,
/// lalu kandidat dengan dukungan antar-sumber terbanyak. Video lirik berbahasa Indonesia hampir tidak pernah menulis
/// teks Arab di description (liriknya di gambar), jadi pencarian Latin terutama dipakai untuk memanen judul Arab;
/// description video berjudul Arab yang memuat teksnya. Bing/DDG tidak dipakai (DECISIONS 2026-10-21).
/// Semua kegagalan jaringan → kandidat lebih sedikit, tidak melempar.
/// </summary>
public static partial class LyricFetcher
{
    private const int MinLines = 4;
    // Pencarian Latin terutama untuk memanen judul Arab; description berjudul Arab yang memuat teksnya
    private const int LatinVideos = 5;
    private const int ArabicVideos = 8;
    private const int MaxParts = 4;
    private const int WikiPages = 3;

    // Fuzz.Ratio dua baris (setelah AsrMatcher.Fold) — beda harakat/ejaan antar-sumber masih lolos
    private const int LineAgreement = 85;

    // Judul hasil pencarian dianggap membahas judul yang dicari
    private const int TitleRelevance = 75;

    // Satu sumber menulis bait penuh, sumber lain per shadr/'ajuz: baris pendek termuat di baris panjang
    private const int PartialAgreement = 90;
    private const int MinPartialChars = 12;

    // Kerangka konsonan judul Latin vs judul Arab hasil panen. Ketat: "صل وسلم ياسلام" (sholawat lain) memberi 86
    // terhadap "maula ya sholli wa sallim"
    private const int SkeletonAgreement = 88;
    private const int MinSkeleton = 4;

    // Judul Latin vs baris teks: transliterasi judul sering tidak persis ("Dzaka", "Taghsya")
    private const int TitleSkeletonAgreement = 80;

    private const int MinSupport = 2;

    // Judul Arab generik ("يا حبيب القلب") juga memunculkan lagu pop Arab. Sholawat/qasidah menyebut ≥ 2 kata religius
    // berbeda; lagu pop yang diuji hanya "والله/ولله". Proporsi baris tidak dipakai: qasidah pujian (Sholawatullahi
    // Taghsya) hanya 8% barisnya, lagu pop 14%. "الهادي" tidak dipakai: lagu pop "ع الهادي" (pelan)
    private const int MinReligiousWords = 2;

    private static readonly HashSet<string> s_religiousWords =
    [
        "الله", "لله", "اللهم", "رسول", "الرسول", "النبي", "نبي", "محمد", "احمد", "طه", "يس", "المصطفي", "مصطفي",
        "المختار", "صلي", "صل", "صلاه", "الصلاه", "صلوات", "سلام", "السلام", "رب", "ربنا", "ربي", "الهي", "اله",
        "مولاي", "مولانا", "عظيم", "العظيم", "رحيم", "الرحيم", "كريم", "الكريم", "مجيد", "رحمن", "الرحمن", "شفيع",
        "الشفيع", "الجنه", "ذنوب", "الذنوب", "البشير", "النذير", "الرسل", "رسل", "المرسلين", "الانبياء", "الحبيب",
        "الخلق", "البريه", "البرايا", "الدين", "الاسلام", "مكه", "طيبه", "المدينه", "الحرم", "القران", "العرش",
        "الكونين", "الثقلين", "الاله",
    ];

    private static readonly HttpClient Wiki = CreateWikiClient();

    private static readonly JsonSerializerOptions s_cacheJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Kata Latin di judul video yang bukan bagian nama sholawat
    private static readonly HashSet<string> s_noiseWords =
    [
        "lirik", "lyrics", "lyric", "teks", "text", "official", "video", "music", "audio", "mv", "hd", "full",
        "arab", "arabic", "latin", "terjemah", "terjemahan", "artinya", "arti", "dan", "new", "terbaru", "cover",
        "sholawat", "shalawat", "solawat", "salawat", "qasidah", "qosidah", "kasidah", "nasyid", "hadroh", "hadrah",
        "banjari", "merdu", "live",
    ];

    // Owner: channel YouTube / "wikisource" — dukungan hanya dihitung dari owner lain (satu channel sering memakai
    // description yang sama untuk beberapa versi video). Anchor: dari pencarian Latin (konteks video Indonesia yang
    // diputar), lihat Choose
    private sealed record Candidate(string Url, string Owner, string SourceTitle, ExtractedLyrics Lyrics, bool Anchor)
    {
        public string[] Folded { get; } = Lyrics.Lines.Select(l => AsrMatcher.Fold(l.Ar)).ToArray();
    }

    private static HttpClient CreateWikiClient()
    {
        // Kebijakan Wikimedia: UA deskriptif
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskbarSholawat/1.0 (lirik sholawat; cache lokal)");
        return http;
    }

    /// <param name="description">Description video yang sedang diputar (langkah 3), bila ada.</param>
    /// <param name="videoId">Video yang diputar: tidak dihitung ulang sebagai kandidat pencarian.</param>
    /// <returns>Satu hasil per bagian judul yang ditemukan teksnya (kosong = tidak ditemukan).</returns>
    public static async Task<IReadOnlyList<FetchedLyrics>> FetchAsync(
        string mediaTitle, string? description, string? videoId, Action<string>? trace = null)
    {
        var parts = TitleParts(mediaTitle);
        trace?.Invoke($"bagian judul: {string.Join(" | ", parts.Select(p => $"\"{p}\""))}");

        if (!string.IsNullOrWhiteSpace(description))
        {
            var own = LyricExtractor.Blocks(description).Where(b => b.Lines.Count >= MinLines).Take(MaxParts).ToList();
            trace?.Invoke($"description video diputar: {own.Count} blok lirik");
            if (own.Count > 0)
            {
                // Judul media utuh: bagian judul bisa nama pelantun ("Mesut Kurtis - Burdah …"), dan judul utuh
                // yang sama akan cocok lagi di LyricLibrary.FindSholawat. Blok berikutnya diberi nomor (berkas cache)
                var url = videoId is null ? "description" : $"https://www.youtube.com/watch?v={videoId}";
                var title = mediaTitle.Trim();
                return own.Select((b, i) => new FetchedLyrics(
                    i == 0 ? title : $"{title} ({i + 1})", ArabicRuns(mediaTitle).FirstOrDefault(), b, url,
                    "description video diputar", 0, own.Count)).ToList();
            }
        }

        var results = new List<FetchedLyrics>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (videoId is not null)
            seen.Add(videoId);
        foreach (var part in parts)
        {
            if (await FetchPartAsync(part, seen, trace) is { } r)
                results.Add(r);
        }
        return results;
    }

    private static async Task<FetchedLyrics?> FetchPartAsync(string part, HashSet<string> seen, Action<string>? trace)
    {
        var pool = new List<Candidate>();
        var arabicTitle = ArabicRuns(part).FirstOrDefault();

        if (arabicTitle is null)
        {
            var hits = await MediaDescription.SearchVideosAsync($"{part} lirik", trace);
            trace?.Invoke($"[{part}] YouTube \"{part} lirik\": {hits.Count} video");
            await AddVideosAsync(pool, hits, part, null, LatinVideos, anchor: true, seen, trace);
            arabicTitle = HarvestArabicTitle(hits, part, trace);
            // Video berjudul Arab sering menulis transliterasi Latin juga ("Ya Tarim يا تريم"); kata Arab di kueri
            // memunculkannya (uji: Ya Robbi Bil Mustofa, Ya Tarim, Sholawat Nariyah). Hanya untuk panen judul
            foreach (var suffix in new[] { "قصيدة", "كلمات" })
            {
                if (arabicTitle is not null)
                    break;
                var more = await MediaDescription.SearchVideosAsync($"{part} {suffix}", trace);
                arabicTitle = HarvestArabicTitle(more, part, trace);
                trace?.Invoke($"[{part}] YouTube \"{part} {suffix}\": {more.Count} video");
            }
            trace?.Invoke($"[{part}] judul Arab dari judul hasil: {arabicTitle ?? "(tidak ada)"}");
            arabicTitle ??= SeedTitle(pool, part, trace);
        }

        if (arabicTitle is not null)
        {
            foreach (var query in new[] { $"{arabicTitle} كلمات", arabicTitle })
            {
                var hits = await MediaDescription.SearchVideosAsync(query, trace);
                trace?.Invoke($"[{part}] YouTube \"{query}\": {hits.Count} video");
                await AddVideosAsync(pool, hits, part, arabicTitle, ArabicVideos, anchor: false, seen, trace);
            }
        }

        var best = Choose(pool);
        if (arabicTitle is not null && (best is null || best.Value.Support < MinSupport))
        {
            await AddWikisourceAsync(pool, part, arabicTitle, trace);
            best = Choose(pool);
        }
        Choose(pool, trace);

        if (best is not { } b)
        {
            trace?.Invoke($"[{part}] tidak ada kandidat lirik");
            return null;
        }
        // Tanpa pembanding, kandidat bisa sholawat lain yang kebetulan relevan judulnya — tidak disimpan: cache
        // dipakai sebagai teks-diketahui berikutnya
        if (b.Support < MinSupport)
        {
            trace?.Invoke($"[{part}] tidak yakin: dukungan terbaik {b.Support} ({b.Candidate.Url}, {pool.Count} kandidat)");
            return null;
        }
        // Baris tanpa dukungan di awal/akhir = kredit, promosi channel, judul berulang; di tengah dibiarkan (bisa
        // bait yang tidak ditulis sumber lain). Sisa terlalu sedikit = sumber memecah baris berbeda → utuh
        var sliced = Slice(b.Candidate.Lyrics, Array.IndexOf(b.Supported, true), Array.LastIndexOf(b.Supported, true));
        var lyrics = sliced.Lines.Count >= MinLines ? sliced : b.Candidate.Lyrics;
        trace?.Invoke($"[{part}] terpilih {b.Candidate.Url} ({lyrics.Lines.Count}/{b.Candidate.Lyrics.Lines.Count} baris, dukungan {b.Support}, {pool.Count} kandidat)");
        return new FetchedLyrics(part, arabicTitle, lyrics, b.Candidate.Url, b.Candidate.SourceTitle, b.Support, pool.Count);
    }

    private static async Task AddVideosAsync(
        List<Candidate> pool, IReadOnlyList<YouTubeSearchHit> hits, string part, string? arabicTitle, int max,
        bool anchor, HashSet<string> seen, Action<string>? trace)
    {
        var picked = hits.Where(h => Relevant(h.Title, part, arabicTitle) && seen.Add(h.VideoId)).Take(max).ToList();
        var videos = await Task.WhenAll(picked.Select(h => MediaDescription.FetchAsync(h.VideoId, trace)));
        foreach (var (hit, video) in picked.Zip(videos))
        {
            var block = video is null ? ExtractedLyrics.Empty : LyricExtractor.Extract(video.Description, MinLines);
            var religious = ReligiousWords(block);
            trace?.Invoke($"  {hit.VideoId} \"{hit.Title}\": {(video is null ? "gagal" : $"{block.Lines.Count} baris, {religious} kata religius")}");
            if (block.Lines.Count >= MinLines && religious >= MinReligiousWords && SoundsLikeTitle(block, part))
                pool.Add(new Candidate($"https://www.youtube.com/watch?v={hit.VideoId}", hit.Owner, hit.Title, block, anchor));
        }
    }

    private static async Task AddWikisourceAsync(List<Candidate> pool, string part, string arabicTitle, Action<string>? trace)
    {
        var titles = new List<string>();
        try
        {
            var api = "https://ar.wikisource.org/w/api.php?action=query&list=search&format=json"
                      + $"&srlimit={WikiPages}&srsearch={Uri.EscapeDataString(arabicTitle)}";
            using var doc = JsonDocument.Parse(await Wiki.GetStringAsync(api));
            foreach (var s in doc.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
            {
                if (s.TryGetProperty("title", out var t) && t.GetString() is { } title)
                    titles.Add(title);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or KeyNotFoundException or InvalidOperationException)
        {
            trace?.Invoke($"wikisource search gagal: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        trace?.Invoke($"wikisource \"{arabicTitle}\": {string.Join(" | ", titles)}");

        var key = SquashedFold(arabicTitle);
        foreach (var title in titles)
        {
            var url = $"https://ar.wikisource.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
            string raw;
            try
            {
                raw = await Wiki.GetStringAsync(
                    $"https://ar.wikisource.org/w/index.php?action=raw&title={Uri.EscapeDataString(title)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                trace?.Invoke($"  {title}: gagal {ex.Message}");
                continue;
            }

            // Pencarian Wikisource lepas (kata "عظيم" saja sudah cocok): blok dipakai hanya bila judul halaman
            // atau bait pembukanya memuat judul yang dicari
            var titleOk = Fuzz.PartialRatio(key, SquashedFold(title)) >= 85;
            var blocks = LyricExtractor.Blocks(WikiToText(raw))
                .Where(b => b.Lines.Count >= MinLines && ReligiousWords(b) >= MinReligiousWords && SoundsLikeTitle(b, part))
                .Where(b => titleOk || Fuzz.PartialRatio(key, SquashedFold(string.Join(' ', b.Lines.Take(2).Select(l => l.Ar)))) >= 85)
                .ToList();
            trace?.Invoke($"  {title}: {blocks.Count} blok relevan");
            foreach (var b in blocks)
                pool.Add(new Candidate(url, "wikisource", title, b, Anchor: false));
        }
    }

    // Dukungan = baris kandidat yang juga muncul di kandidat milik owner lain. Seri → baris lebih banyak (teks lebih
    // lengkap), lalu peringkat pencarian lebih tinggi. Bila ada kandidat jangkar (pencarian Latin), kandidat
    // pencarian Arab hanya boleh terpilih bila ≥ MinSupport barisnya cocok dengan jangkar: judul Arab generik bisa
    // memunculkan lagu lain yang tersebar di banyak channel dan saling mendukung (uji "Ya Habibal Qolbi" → lagu pop)
    private static (Candidate Candidate, bool[] Supported, int Support)? Choose(List<Candidate> pool, Action<string>? trace = null)
    {
        var anchors = pool.Where(c => c.Anchor).ToList();
        (Candidate Candidate, bool[] Supported, int Support)? best = null;
        foreach (var c in pool)
        {
            var others = pool.Where(o => o.Owner != c.Owner).ToList();
            var supported = c.Folded
                .Select(line => others.Any(o => o.Folded.Any(x => SameLine(line, x))))
                .ToArray();
            var support = supported.Count(x => x);
            var anchored = c.Anchor || anchors.Count == 0
                || c.Folded.Count(line => anchors.Any(a => a.Owner != c.Owner && a.Folded.Any(x => SameLine(line, x)))) >= MinSupport;
            trace?.Invoke($"  kandidat {c.Url} [{c.Owner}]{(c.Anchor ? " jangkar" : "")} {c.Lyrics.Lines.Count} baris, dukungan {support}{(anchored ? "" : ", tidak cocok jangkar")}");
            if (!anchored)
                continue;
            if (best is null || support > best.Value.Support
                || support == best.Value.Support && c.Lyrics.Lines.Count > best.Value.Candidate.Lyrics.Lines.Count)
                best = (c, supported, support);
        }
        return best;
    }

    // Nama sholawat diambil dari bait pembuka / refrain, jadi teksnya harus memuat baris (atau dua baris berurutan)
    // yang sebunyi dengan judul Latin. Judul Latin terlalu pendek ("Ala Yallah", "Badar") → tidak diperiksa
    private static bool SoundsLikeTitle(ExtractedLyrics lyrics, string part)
    {
        var latin = Skeleton(LatinKey(part));
        if (latin.Length < MinSkeleton)
            return true;
        var lines = lyrics.Lines.Select(l => l.Ar).ToList();
        return lines.Concat(lines.Zip(lines.Skip(1), (a, b) => $"{a} {b}"))
            .Any(l => Fuzz.PartialRatio(latin, Skeleton(l)) >= TitleSkeletonAgreement);
    }

    // Kata religius berbeda di seluruh teks; "يا" dan ≤ 2 prefiks و/ف/ب/ل/ك dilepas ("والله", "فلرب", "ياالله");
    // "لله"/"اللهم" dihitung sebagai "الله"
    private static int ReligiousWords(ExtractedLyrics lyrics)
    {
        var found = new HashSet<string>();
        foreach (var raw in lyrics.Lines.SelectMany(l => ArabicNormalizer.Normalize(l.Ar).Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            var w = raw.StartsWith("يا") && raw.Length > 4 ? raw[2..] : raw;
            for (var strip = 0; strip <= 2 && w.Length > 1; strip++)
            {
                if (s_religiousWords.Contains(w))
                {
                    found.Add(w is "لله" or "اللهم" ? "الله" : w);
                    break;
                }
                if (!"وفبلك".Contains(w[0]))
                    break;
                w = w[1..];
            }
        }
        return found.Count;
    }

    private static bool SameLine(string a, string b) =>
        Fuzz.Ratio(a, b) >= LineAgreement
        || Math.Min(a.Length, b.Length) >= MinPartialChars && Fuzz.PartialRatio(a, b) >= PartialAgreement;

    // Baris unik [first, last]; Order & Refrain diindeks ulang, kemunculan di luar rentang dibuang
    private static ExtractedLyrics Slice(ExtractedLyrics lyrics, int first, int last)
    {
        var order = lyrics.Order.Where(i => i >= first && i <= last).Select(i => i - first).ToList();
        var refrain = order.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();
        return new ExtractedLyrics(lyrics.Lines.Skip(first).Take(last - first + 1).ToList(), order, refrain);
    }

    private static bool Relevant(string videoTitle, string part, string? arabicTitle)
    {
        if (arabicTitle is not null && SquashedFold(videoTitle) is { Length: > 0 } ar
            && Fuzz.PartialRatio(SquashedFold(arabicTitle), ar) >= TitleRelevance)
            return true;
        var latin = LatinKey(part);
        return latin.Length > 0 && Fuzz.PartialRatio(latin, LatinKey(videoTitle)) >= TitleRelevance;
    }

    /// <summary>
    /// Judul media → bagian yang dicari: isi kurung dibuang, dipecah pada "|", " - ", "&amp;", "+", " x ", "/"
    /// (medley "A - B - C"; nama pelantun/channel sering juga dipisah tanda ini — bagian seperti itu tidak menemukan
    /// teks yang sebunyi, lihat <see cref="SoundsLikeTitle"/>), nama pelantun ("pelantun …", "feat …") dipotong.
    /// </summary>
    public static IReadOnlyList<string> TitleParts(string mediaTitle)
    {
        var s = Brackets().Replace(mediaTitle, " ");
        var parts = MedleySeparator().Split(s)
            .Select(p => Performer().Replace(p, ""))
            .Select(p => Spaces().Replace(Medley().Replace(p, " "), " ").Trim(' ', '-', '–', ':', '.', ','))
            .Where(p => p.Length > 0 && (ArabicRuns(p).Count > 0 || LatinKey(p).Length >= 3))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxParts)
            .ToList();
        // Bagian Arab di dalam kurung ("Qul Ya 'Azhim ( قل يا عظيم )") tetap berguna sebagai judul Arab
        if (parts.Count == 1 && ArabicRuns(parts[0]).Count == 0 && ArabicRuns(mediaTitle).FirstOrDefault() is { } ar)
            parts[0] = $"{parts[0]} {ar}";
        return parts.Count > 0 ? parts : [mediaTitle.Trim()];
    }

    /// <summary>Runtun kata Arab (≥ 2 kata, ≥ 6 huruf) dalam teks, urut kemunculan.</summary>
    internal static IReadOnlyList<string> ArabicRuns(string text) =>
        ArabicRun().Matches(text)
            .Select(m => Spaces().Replace(m.Value, " ").Trim())
            .Where(r => r.Contains(' ') && r.Count(c => c is >= 'ء' and <= 'ي') >= 6)
            .ToList();

    // Judul Arab di judul hasil yang relevan secara Latin (hanya 1–2 dari 20 hasil yang menulisnya — judul pelantun
    // seperti "ماهر زين - مولاي" tersaring karena judul Latinnya tidak relevan). Ejaan berbeda ("ألا يالله" /
    // "ألا يا الله") dikelompokkan; terbanyak menang, seri → peringkat lebih tinggi
    private static string? HarvestArabicTitle(IReadOnlyList<YouTubeSearchHit> hits, string part, Action<string>? trace)
    {
        var latin = Skeleton(LatinKey(part));
        var groups = new List<(string Key, string Text, int Count)>();
        foreach (var hit in hits.Where(h => Relevant(h.Title, part, null)))
        {
            foreach (var run in ArabicRuns(hit.Title))
            {
                var key = SquashedFold(run);
                var i = groups.FindIndex(g => Fuzz.Ratio(g.Key, key) >= LineAgreement);
                if (i >= 0)
                    groups[i] = groups[i] with { Count = groups[i].Count + 1 };
                else
                    groups.Add((key, run, 1));
            }
        }
        // Judul Latin cukup panjang: judul Arab harus sebunyi ("maula ya sholli wa sallim" ≈ "مولاي صل وسلم"), supaya
        // judul Arab sholawat lain di hasil yang sama ("صل وسلم ياسلام") tidak terpanen
        var passed = groups.Where(g =>
        {
            var score = latin.Length < MinSkeleton ? 100 : Fuzz.PartialRatio(latin, Skeleton(g.Text));
            trace?.Invoke($"  judul Arab \"{g.Text}\" ×{g.Count}, kerangka {latin}~{Skeleton(g.Text)} = {score}");
            return score >= SkeletonAgreement;
        }).ToList();
        return passed.Count == 0 ? null : ArabicNormalizer.Normalize(passed.MaxBy(g => g.Count).Text);
    }

    // Cadangan bila judul hasil tidak memuat judul Arab: baris kandidat yang sebunyi dengan judul Latin (nama sholawat
    // biasanya diambil dari bait pembuka / refrain). Bukan sekadar baris pertama: description video Indonesia sering
    // memuat sholawat lain (uji "Ya Robbi Bil Mustofa" → baris pertama Burdah)
    private static string? SeedTitle(List<Candidate> pool, string part, Action<string>? trace)
    {
        var latin = Skeleton(LatinKey(part));
        if (latin.Length < MinSkeleton)
            return null;
        var lines = pool
            .SelectMany(c => c.Lyrics.Lines.Select(l => (c.Url, Line: ArabicNormalizer.Normalize(l.Ar))))
            .Select(x => (x.Url, x.Line, Score: Fuzz.PartialRatio(latin, Skeleton(x.Line))))
            .ToList();
        if (lines.Count == 0)
            return null;
        var best = lines.MaxBy(x => x.Score);
        if (best.Score < SkeletonAgreement)
        {
            trace?.Invoke($"[{part}] tidak ada baris kandidat sebunyi judul (terbaik {best.Score:0})");
            return null;
        }
        var title = string.Join(' ', best.Line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
        trace?.Invoke($"[{part}] judul Arab dari baris {best.Url}: {title} (kerangka {best.Score:0})");
        return title;
    }

    /// <summary>
    /// Kerangka konsonan lintas aksara untuk membandingkan judul transliterasi Indonesia dengan judul Arab: huruf
    /// sebunyi satu kelas (ص/س/ش/ث = s, sh, sy, ts; ط/ت = t, th; ض/ظ/ذ/ز/د = d, dz, dh, z, zh; ق/ك = q, k; ح/خ/ه = h,
    /// kh), vokal, alif, wawu, ya, 'ain, hamzah dibuang, h di akhir kata dibuang (ة / "-ah"), huruf kembar jadi satu.
    /// </summary>
    internal static string Skeleton(string text)
    {
        var sb = new StringBuilder();
        var arabic = text.Any(c => c is >= 'ء' and <= 'ي');
        foreach (var word in (arabic ? ArabicNormalizer.Normalize(text) : text.ToLowerInvariant())
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new StringBuilder();
            for (var i = 0; i < word.Length; i++)
            {
                var c = word[i];
                var next = i + 1 < word.Length ? word[i + 1] : '\0';
                // Digraf transliterasi: huruf kedua dilewati
                if (next == 'h' && c is 's' or 't' or 'd' or 'z' or 'k' or 'g' || c == 's' && next == 'y'
                    || c == 't' && next == 's' || c == 'd' && next == 'z')
                    i++;
                var k = c switch
                {
                    'b' or 'p' or 'ب' => 'b',
                    't' or 'ت' or 'ط' => 't',
                    's' or 'c' or 'ث' or 'س' or 'ش' or 'ص' => 's',
                    'j' or 'ج' => 'j',
                    'h' or 'ح' or 'خ' or 'ه' or 'ة' => 'h',
                    'd' or 'z' or 'د' or 'ذ' or 'ز' or 'ض' or 'ظ' => 'd',
                    'r' or 'ر' => 'r',
                    'k' or 'q' or 'x' or 'ك' or 'ق' => 'k',
                    'l' or 'ل' => 'l',
                    'm' or 'م' => 'm',
                    'n' or 'ن' => 'n',
                    'f' or 'v' or 'ف' => 'f',
                    'g' or 'غ' => 'g',
                    _ => '\0',
                };
                if (k != '\0')
                    w.Append(k);
            }
            if (w.Length > 1 && w[^1] == 'h')
                w.Length--;
            sb.Append(w);
        }
        var collapsed = new StringBuilder(sb.Length);
        foreach (var c in sb.ToString())
        {
            if (collapsed.Length == 0 || collapsed[^1] != c)
                collapsed.Append(c);
        }
        return collapsed.ToString();
    }

    /// <summary>Wikiteks Wikisource → baris teks: bait <c>{{أبيات|…\\…}}</c> / <c>{{بيت|…|…}}</c> jadi baris
    /// "shadr ، 'ajuz", templat lain & tag dibuang, judul bagian memutus blok lirik.</summary>
    internal static string WikiToText(string raw)
    {
        var s = WikiComment().Replace(raw, "");
        s = WikiRef().Replace(s, "");
        s = VerseTemplate().Replace(s, m => $"\n{m.Groups[1].Value} ، {m.Groups[2].Value}\n");
        s = VersesOpen().Replace(s, "\n");
        string prev;
        do
        {
            prev = s;
            s = Template().Replace(s, "");
        } while (s != prev);
        s = s.Replace("{{", "").Replace("}}", "");
        s = WikiLink().Replace(s, "$1");
        s = s.Replace("'''", "").Replace("''", "");
        s = WikiHeading().Replace(s, $"\n{LyricExtractor.HardBreak}\n");
        s = Tag().Replace(s, "\n");
        s = s.Replace("\\\\", " ، ");
        return s;
    }

    /// <summary>Simpan ke <paramref name="dir"/> (biasanya <c>library/cache</c>) dalam format pustaka sholawat;
    /// berkas lama untuk judul yang sama ditimpa. Gagal tulis → null.</summary>
    public static string? Save(FetchedLyrics f, string dir, Action<string>? trace = null)
    {
        var slug = LyricLibrary.TitleKey(f.Title).Replace(' ', '-');
        foreach (var c in Path.GetInvalidFileNameChars())
            slug = slug.Replace(c, '-');
        if (slug.Length == 0)
            slug = $"{(uint)f.Title.GetHashCode():x8}";
        var id = $"web-{slug}";
        var dto = new
        {
            id,
            titles = new[] { f.Title, f.ArabicTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToArray(),
            source = f.Source,
            license = "cache lokal hasil pencarian online — milik sumber aslinya, jangan dibundel/disebarkan",
            fetched = new
            {
                at = DateTimeOffset.Now.ToString("O"),
                sourceTitle = f.SourceTitle,
                support = f.Support,
                candidates = f.Candidates,
                order = f.Lyrics.Order,
                refrain = f.Lyrics.Refrain,
            },
            lines = f.Lyrics.Lines.Select(l => new { ar = l.Ar, latin = l.Latin, id = l.Translation }).ToArray(),
        };
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{id}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dto, s_cacheJson));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            trace?.Invoke($"cache tidak bisa ditulis: {ex.Message}");
            return null;
        }
    }

    private static string SquashedFold(string text) => AsrMatcher.Fold(text).Replace(" ", "");

    private static string LatinKey(string text) =>
        string.Join(' ', LyricLibrary.TitleKey(text).Split(' ')
            .Where(w => w.Length > 0 && w.All(c => c < 'ء') && !s_noiseWords.Contains(w)));

    [GeneratedRegex(@"[\(\[【「〔][^\)\]】」〕]*[\)\]】」〕]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s(?:pelantun|vokal|vocal|voc\.?|by|feat\.?|ft\.?|cover|bersama|oleh)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Performer();

    [GeneratedRegex(@"\s*(?:&|\+|\s[xX]\s|/|،|\||｜|\s[-–—]\s)\s*")]
    private static partial Regex MedleySeparator();

    [GeneratedRegex(@"\bmedley\b", RegexOptions.IgnoreCase)]
    private static partial Regex Medley();

    // Huruf Arab + harakat + spasi di antaranya (tanpa angka/tanda baca Latin)
    [GeneratedRegex(@"[ء-يٱ-ۓً-ْٰـ]+(?:\s+[ء-يٱ-ۓً-ْٰـ]+)*")]
    private static partial Regex ArabicRun();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex WikiComment();

    [GeneratedRegex(@"<ref\b[^>]*?(?:/>|>.*?</ref>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex WikiRef();

    [GeneratedRegex(@"\{\{\s*بيت\s*\|([^|{}]*)\|([^|{}]*)(?:\|[^{}]*)?\}\}")]
    private static partial Regex VerseTemplate();

    [GeneratedRegex(@"\{\{\s*(?:أبيات|ابيات|قصيدة)\s*\|")]
    private static partial Regex VersesOpen();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"^\s*=+[^=\n]+=+\s*$", RegexOptions.Multiline)]
    private static partial Regex WikiHeading();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
}
