namespace TaskbarSholawat;

/// <param name="Site">"youtube", "youtube-music", host lain, atau null (aplikasi non-browser / tak terbaca).</param>
/// <param name="TabTitle">Judul tab media bila tab itu tidak aktif di browser.</param>
/// <param name="How">"url" = videoId dari address bar, "search" = dari pencarian YouTube, null = tidak ada video.</param>
public sealed record MediaContext(
    string Title,
    string Artist,
    string SourceApp,
    SourceKind SourceKind,
    string? Site,
    string? TabTitle,
    string? Url,
    string? VideoId,
    string? Description,
    int? DurationSec,
    string? How)
{
    /// <summary>Alur pengenalan langkah 1–3: judul (GSMTC) → sumber (app/URL) → description (YouTube).</summary>
    public static async Task<MediaContext> BuildAsync(MediaSnapshot media, Action<string>? trace = null)
    {
        var source = await Task.Run(() => MediaSource.Detect(media, trace));
        var gsmtcDuration = media.EndTime > TimeSpan.Zero ? (int?)Math.Round(media.EndTime.TotalSeconds) : null;

        var videoId = source.Site is "youtube" or "youtube-music" ? source.VideoId : null;
        var how = videoId is null ? null : "url";
        // Cadangan bila URL tak terbaca (tab tidak aktif, Spotify, aplikasi lain). URL non-YouTube yang
        // terbaca tidak dicari ulang: sumbernya sudah jelas bukan YouTube
        if (videoId is null && source.Url is null && media.Title.Length > 0)
        {
            videoId = await MediaDescription.SearchAsync(media.Title, media.Artist, media.EndTime, trace);
            how = videoId is null ? null : "search";
        }

        var video = videoId is null ? null : await MediaDescription.FetchAsync(videoId, trace);
        return new MediaContext(
            media.Title,
            media.Artist,
            media.AppId,
            source.Kind,
            source.Site ?? (videoId is null ? null : "youtube"),
            source.TabTitle,
            source.Url ?? (videoId is null ? null : $"https://www.youtube.com/watch?v={videoId}"),
            videoId,
            video?.Description,
            video is { LengthSeconds: > 0 } ? video.LengthSeconds : gsmtcDuration,
            how);
    }
}
