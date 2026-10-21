using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TaskbarSholawat;

public sealed record YouTubeVideo(string VideoId, string Title, string Author, int LengthSeconds, string Description);

public sealed record YouTubeHit(string VideoId, string Title, string Owner, int? LengthSeconds);

/// <summary>
/// Alur pengenalan langkah 3: description video YouTube dari HTML publik (tanpa API key) —
/// ytInitialPlayerResponse di halaman watch, ytInitialData di halaman pencarian. Struktur halaman
/// bisa berubah sewaktu-waktu; semua kegagalan → null, tidak melempar.
/// </summary>
public static class MediaDescription
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // Tanpa UA browser YouTube memberi halaman berbeda; Accept-Language id: teks & durasi versi Indonesia
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("id");
        return http;
    }

    public static async Task<YouTubeVideo?> FetchAsync(string videoId, Action<string>? trace = null)
    {
        using var doc = await GetEmbeddedJsonAsync(
            $"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}", "ytInitialPlayerResponse = ", trace);
        if (doc is null || !doc.RootElement.TryGetProperty("videoDetails", out var d))
        {
            trace?.Invoke(doc is null ? "watch: JSON tidak ditemukan" : "watch: videoDetails tidak ada");
            return null;
        }
        return new YouTubeVideo(
            Str(d, "videoId") ?? videoId,
            Str(d, "title") ?? "",
            Str(d, "author") ?? "",
            int.TryParse(Str(d, "lengthSeconds"), out var len) ? len : 0,
            Str(d, "shortDescription") ?? "");
    }

    /// <summary>
    /// Cadangan bila URL tidak terbaca. Banyak reupload berjudul sama, jadi syaratnya judul cocok +
    /// durasi ±5 s (bila diketahui); di antara itu utamakan channel = artis GSMTC (YouTube mengisi
    /// Artist dengan nama channel).
    /// </summary>
    public static async Task<string?> SearchAsync(string title, string artist, TimeSpan duration, Action<string>? trace = null)
    {
        var hits = await SearchVideosAsync(title, trace);
        var wanted = MediaSource.Normalize(title);
        var owner = MediaSource.Normalize(artist);
        string? best = null;
        var bestScore = 0;
        foreach (var v in hits)
        {
            var t = MediaSource.Normalize(v.Title);
            var titleOk = t.Length > 0 && (t == wanted || t.Contains(wanted) || wanted.Contains(t));
            var lenOk = duration <= TimeSpan.Zero || (v.LengthSeconds is { } l && Math.Abs(l - duration.TotalSeconds) <= 5);
            var score = !titleOk || !lenOk ? 0 : (MediaSource.Normalize(v.Owner) == owner ? 2 : 1);
            trace?.Invoke($"  kandidat {v.VideoId} \"{v.Title}\" {v.LengthSeconds?.ToString() ?? "?"} s, {v.Owner} → skor {score}");
            if (score > bestScore)
            {
                (best, bestScore) = (v.VideoId, score);
                if (score == 2)
                    break;
            }
        }
        return best;
    }

    /// <summary>Hasil pencarian YouTube apa adanya (urut tampilan); kosong bila gagal. Untuk langkah 4:
    /// mencari video lirik lain ("&lt;judul&gt; lirik") yang description-nya memuat teks.</summary>
    public static async Task<IReadOnlyList<YouTubeHit>> SearchVideosAsync(string query, Action<string>? trace = null)
    {
        using var doc = await GetEmbeddedJsonAsync(
            $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}", "var ytInitialData = ", trace);
        if (doc is null)
            return [];
        var hits = new List<YouTubeHit>();
        foreach (var v in VideoRenderers(doc.RootElement))
        {
            if (Str(v, "videoId") is not { } id)
                continue;
            var len = v.TryGetProperty("lengthText", out var lt) ? ParseLength(Str(lt, "simpleText")) : null;
            hits.Add(new YouTubeHit(id, RunsText(v, "title"), RunsText(v, "ownerText"), len));
        }
        return hits;
    }

    private static async Task<JsonDocument?> GetEmbeddedJsonAsync(string url, string marker, Action<string>? trace)
    {
        try
        {
            var html = await Http.GetStringAsync(url);
            var i = html.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0)
            {
                trace?.Invoke($"penanda \"{marker.Trim()}\" tidak ada di {url}");
                return null;
            }
            // Baca tepat satu nilai JSON setelah penanda; sisa skrip (";var meta = …") diabaikan
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[(i + marker.Length)..]));
            return JsonDocument.ParseValue(ref reader);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            trace?.Invoke($"GET {url} gagal: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Urutan dokumen dipertahankan: hasil teratas YouTube lebih dulu
    private static List<JsonElement> VideoRenderers(JsonElement e)
    {
        var found = new List<JsonElement>();
        void Walk(JsonElement x)
        {
            switch (x.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in x.EnumerateObject())
                    {
                        if (p.Name == "videoRenderer")
                            found.Add(p.Value);
                        else
                            Walk(p.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in x.EnumerateArray())
                        Walk(item);
                    break;
            }
        }
        Walk(e);
        return found;
    }

    /// <summary>"15:06", "1:02:03", atau versi Indonesia "15.06" / "1.02.03" → detik.</summary>
    internal static int? ParseLength(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var total = 0;
        foreach (var part in text.Trim().Split(':', '.'))
        {
            if (!int.TryParse(part, out var n) || n < 0)
                return null;
            total = total * 60 + n;
        }
        return total;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string RunsText(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var t))
            return "";
        if (Str(t, "simpleText") is { } simple)
            return simple;
        return t.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? string.Concat(runs.EnumerateArray().Select(r => Str(r, "text")))
            : "";
    }
}
