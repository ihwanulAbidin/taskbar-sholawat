using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TaskbarSholawat;

public sealed record TranscriptLine(TimeSpan Start, TimeSpan End, string Text);

/// <param name="Kind">"asr" = dibuat otomatis YouTube, "manual" = diunggah pemilik.</param>
/// <param name="Client">Klien innertube yang berhasil (IOS / ANDROID).</param>
public sealed record Transcript(string VideoId, string Language, string Kind, string Client, IReadOnlyList<TranscriptLine> Lines)
{
    /// <summary>Baris yang sedang tampil pada posisi media (GSMTC terekstrapolasi); null = di sela baris.</summary>
    public TranscriptLine? LineAt(TimeSpan position)
    {
        // Baris terakhir yang sudah mulai; takarir otomatis bisa tumpang tindih, jadi ambil yang terbaru
        TranscriptLine? hit = null;
        foreach (var line in Lines)
        {
            if (line.Start > position)
                break;
            if (position < line.End)
                hit = line;
        }
        return hit;
    }

    public string FullText => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>
/// Takarir YouTube (seperti panel "Transkrip") tanpa API key, login, atau PO token. Dicek 2026-10-21:
/// baseUrl takarir dari halaman watch (klien WEB) memuat exp=xpe → wajib PO token, body kosong tanpa itu
/// (sama dengan deteksi yt-dlp). Klien innertube IOS/ANDROID memberi baseUrl exp=xpo yang terbaca
/// langsung. YouTube sering mengubah ini; versi klien diambil dari tabel INNERTUBE_CLIENTS yt-dlp
/// master (rilis 2026.08.19) dan bisa kedaluwarsa — semua kegagalan → null, tidak melempar.
/// </summary>
public static class TranscriptSource
{
    private sealed record Client(string Name, int Id, string Version, string UserAgent, Dictionary<string, object> Extra);

    private static readonly Client[] Clients =
    [
        new("IOS", 5, "21.26.4", "com.google.ios.youtube/21.26.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
            new() { ["deviceMake"] = "Apple", ["deviceModel"] = "iPhone16,2", ["osName"] = "iPhone", ["osVersion"] = "18.3.2.22D82" }),
        new("ANDROID", 3, "21.26.364", "com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip",
            new() { ["androidSdkVersion"] = 30, ["osName"] = "Android", ["osVersion"] = "11" }),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Penanda takarir otomatis: [موسيقى] [تصفيق] [Musik] …
    private static readonly Regex Tags = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <param name="language">Bahasa yang diutamakan; tanpa track bahasa ini dipakai track pertama yang ada.</param>
    public static async Task<Transcript?> FetchAsync(string videoId, string language = "ar", Action<string>? trace = null)
    {
        foreach (var client in Clients)
        {
            try
            {
                var (transcript, final) = await FetchWithClientAsync(client, videoId, language, trace);
                if (transcript is not null || final)
                    return transcript;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException)
            {
                trace?.Invoke($"{client.Name}: gagal {ex.GetType().Name}: {ex.Message}");
            }
        }
        return null;
    }

    /// <returns>final = jawaban pasti untuk video ini (mis. video tanpa takarir) — klien lain tidak dicoba.</returns>
    private static async Task<(Transcript? Transcript, bool Final)> FetchWithClientAsync(Client client, string videoId, string language, Action<string>? trace)
    {
        var context = new Dictionary<string, object>(client.Extra)
        {
            ["clientName"] = client.Name,
            ["clientVersion"] = client.Version,
            ["userAgent"] = client.UserAgent,
            ["hl"] = "id",
            ["gl"] = "ID",
        };
        var body = JsonSerializer.Serialize(new { context = new { client = context }, videoId, contentCheckOk = true, racyCheckOk = true });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
        request.Headers.Add("X-YouTube-Client-Name", client.Id.ToString());
        request.Headers.Add("X-YouTube-Client-Version", client.Version);
        request.Headers.Add("Origin", "https://www.youtube.com");

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var player = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = player.RootElement;
        var status = root.TryGetProperty("playabilityStatus", out var ps) && ps.TryGetProperty("status", out var st) ? st.GetString() : null;
        if (!root.TryGetProperty("captions", out var captions)
            || !captions.TryGetProperty("playerCaptionsTracklistRenderer", out var renderer)
            || !renderer.TryGetProperty("captionTracks", out var tracks)
            || tracks.GetArrayLength() == 0)
        {
            trace?.Invoke($"{client.Name}: status {status}, tanpa track takarir");
            // Status OK tanpa track = video memang tanpa takarir; status lain = klien ini ditolak, coba berikutnya
            return (null, status == "OK");
        }

        var track = PickTrack(tracks, language);
        var baseUrl = track.GetProperty("baseUrl").GetString()!;
        var lang = track.TryGetProperty("languageCode", out var lc) ? lc.GetString() ?? "" : "";
        var kind = track.TryGetProperty("kind", out var k) && k.GetString() == "asr" ? "asr" : "manual";
        if (QueryValues(baseUrl, "exp").Any(e => e is "xpe" or "xpv"))
        {
            // Pola yt-dlp: eksperimen ini mewajibkan PO token → body kosong
            trace?.Invoke($"{client.Name}: track {lang}/{kind} butuh PO token (exp=xpe/xpv), dilewati");
            return (null, false);
        }

        using var capRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "&fmt=json3");
        capRequest.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
        using var capResponse = await Http.SendAsync(capRequest);
        capResponse.EnsureSuccessStatusCode();
        var text = await capResponse.Content.ReadAsStringAsync();
        // ANDROID mengabaikan fmt=json3 dan mengirim XML timedtext format 3 (diamati 2026-10-21)
        var lines = text.TrimStart().StartsWith('<') ? ParseSrv3(text) : ParseJson3(text);
        trace?.Invoke($"{client.Name}: track {lang}/{kind}, {text.Length} karakter → {lines.Count} baris");
        return (lines.Count == 0 ? null : new Transcript(videoId, lang, kind, client.Name, lines), lines.Count > 0);
    }

    /// <summary>Utamakan takarir manual berbahasa <paramref name="language"/>, lalu otomatis, lalu apa saja.</summary>
    private static JsonElement PickTrack(JsonElement tracks, string language)
    {
        var all = tracks.EnumerateArray().Where(t => t.TryGetProperty("baseUrl", out _)).ToList();
        int Rank(JsonElement t)
        {
            var lang = t.TryGetProperty("languageCode", out var l) ? l.GetString() : null;
            var asr = t.TryGetProperty("kind", out var k) && k.GetString() == "asr";
            return (lang == language ? 0 : 2) + (asr ? 1 : 0);
        }
        return all.OrderBy(Rank).First();
    }

    internal static List<TranscriptLine> ParseJson3(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var lines = new List<TranscriptLine>();
        if (!doc.RootElement.TryGetProperty("events", out var events))
            return lines;
        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("segs", out var segs) || !e.TryGetProperty("tStartMs", out var t))
                continue;
            var start = t.GetInt64();
            var dur = e.TryGetProperty("dDurationMs", out var d) ? d.GetInt64() : 0;
            var raw = string.Concat(segs.EnumerateArray().Select(s => s.TryGetProperty("utf8", out var u) ? u.GetString() : ""));
            Add(lines, start, dur, raw);
        }
        return lines;
    }

    internal static List<TranscriptLine> ParseSrv3(string xml)
    {
        var lines = new List<TranscriptLine>();
        var body = XDocument.Parse(xml).Root?.Element("body");
        if (body is null)
            return lines;
        foreach (var p in body.Elements("p"))
        {
            if (!long.TryParse((string?)p.Attribute("t"), out var start))
                continue;
            long.TryParse((string?)p.Attribute("d"), out var dur);
            // Takarir otomatis: kata per kata di <s>; manual: teks langsung di <p>
            var raw = p.Elements("s").Any() ? string.Concat(p.Elements("s").Select(s => s.Value)) : p.Value;
            Add(lines, start, dur, raw);
        }
        return lines;
    }

    private static void Add(List<TranscriptLine> lines, long startMs, long durMs, string? raw)
    {
        var text = Spaces.Replace(Tags.Replace(raw ?? "", " "), " ").Trim();
        if (text.Length == 0)
            return;
        lines.Add(new TranscriptLine(TimeSpan.FromMilliseconds(startMs), TimeSpan.FromMilliseconds(startMs + durMs), text));
    }

    private static IEnumerable<string> QueryValues(string url, string key)
    {
        var q = url.IndexOf('?');
        if (q < 0)
            yield break;
        foreach (var pair in url[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
                foreach (var v in Uri.UnescapeDataString(pair[(eq + 1)..]).Split(','))
                    yield return v;
        }
    }
}
