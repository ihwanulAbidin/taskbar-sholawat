using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace TaskbarSholawat;

public enum SourceKind { Chrome, Edge, Firefox, Brave, Opera, Spotify, Other }

/// <param name="TabTitle">Judul tab media bila tab itu tidak aktif (URL-nya tidak terbaca).</param>
/// <param name="Site">"youtube", "youtube-music", atau host lain (tanpa www.); dari URL, atau dari akhiran judul tab.</param>
public sealed record SourceInfo(SourceKind Kind, string? WindowTitle, string? TabTitle, string? Url, string? Site, string? VideoId);

/// <summary>
/// Alur pengenalan langkah 2: dari mana media diputar. Untuk browser, URL dibaca dari address bar
/// jendela yang judulnya memuat judul media (judul jendela = judul tab aktif) lewat UI Automation —
/// hanya membaca, tidak membuka/mengubah tab. Tab media yang tidak aktif: URL-nya tidak terekspos,
/// hanya nama tab di tab strip → situs ditebak dari akhiran judul ("… - YouTube").
/// Panggil di luar thread UI (UIA ke proses lain bisa lambat).
/// </summary>
public static class MediaSource
{
    public static SourceKind KindOf(string appId)
    {
        var id = appId.ToLowerInvariant();
        // Firefox memakai AUMID berupa hash jalur instalasi; 308046B0AF4A39CB = rilis standar
        if (id.Contains("firefox") || id.StartsWith("308046b0af4a39cb"))
            return SourceKind.Firefox;
        if (id.Contains("spotify"))
            return SourceKind.Spotify;
        if (id.StartsWith("msedge") || id.Contains("microsoft.edge"))
            return SourceKind.Edge;
        if (id.StartsWith("brave"))
            return SourceKind.Brave;
        if (id.StartsWith("opera"))
            return SourceKind.Opera;
        if (id.StartsWith("chrome") || id.Contains("google.chrome"))
            return SourceKind.Chrome;
        return SourceKind.Other;
    }

    private static string? ProcessName(SourceKind kind) => kind switch
    {
        SourceKind.Chrome => "chrome",
        SourceKind.Edge => "msedge",
        SourceKind.Firefox => "firefox",
        SourceKind.Brave => "brave",
        SourceKind.Opera => "opera",
        _ => null,
    };

    public static SourceInfo Detect(MediaSnapshot media, Action<string>? trace = null)
    {
        var kind = KindOf(media.AppId);
        if (ProcessName(kind) is not { } name || media.Title.Length == 0)
            return new SourceInfo(kind, null, null, null, null, null);

        try
        {
            var windows = BrowserWindows(name);
            var wanted = Normalize(media.Title);
            if (windows.FirstOrDefault(w => Normalize(w.Current.Name).Contains(wanted)) is { } window)
            {
                var title = window.Current.Name;
                var raw = ReadAddressBar(window);
                trace?.Invoke($"tab aktif di jendela \"{title}\", address bar \"{raw}\"");
                var url = NormalizeUrl(raw);
                var (site, videoId) = url is null ? (SiteFromTabTitle(title), null) : ParseUrl(url);
                return new SourceInfo(kind, title, null, url?.ToString(), site, videoId);
            }

            // Hanya tab yang cocok yang dicatat — daftar tab lain milik privasi user
            foreach (var w in windows)
            {
                var tab = w.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(t => Normalize(t.Current.Name).Contains(wanted));
                if (tab is null)
                    continue;
                var tabTitle = tab.Current.Name;
                trace?.Invoke($"tab tidak aktif \"{tabTitle}\" → URL tidak terbaca");
                return new SourceInfo(kind, w.Current.Name, tabTitle, null, SiteFromTabTitle(tabTitle), null);
            }
            trace?.Invoke($"tidak ada jendela/tab {name} yang judulnya memuat \"{media.Title}\"");
            return new SourceInfo(kind, null, null, null, null, null);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // Jendela bisa tertutup di tengah pembacaan UIA
            trace?.Invoke($"UIA gagal: {ex.GetType().Name}: {ex.Message}");
            return new SourceInfo(kind, null, null, null, null, null);
        }
    }

    private static List<AutomationElement> BrowserWindows(string processName)
    {
        var pids = Process.GetProcessesByName(processName);
        try
        {
            if (pids.Length == 0)
                return [];
            Condition byPid = pids.Length == 1
                ? new PropertyCondition(AutomationElement.ProcessIdProperty, pids[0].Id)
                : new OrCondition(pids.Select(p => (Condition)new PropertyCondition(AutomationElement.ProcessIdProperty, p.Id)).ToArray());
            return AutomationElement.RootElement.FindAll(TreeScope.Children, byPid).Cast<AutomationElement>().ToList();
        }
        finally
        {
            foreach (var p in pids)
                p.Dispose();
        }
    }

    /// <summary>Judul halaman YouTube = "&lt;judul video&gt; - YouTube"; Chrome bisa menambah status tab
    /// ("- Audio playing", dilokalkan), jadi cari penandanya, bukan akhiran persis.</summary>
    internal static string? SiteFromTabTitle(string tabTitle) =>
        tabTitle.Contains(" - YouTube Music", StringComparison.Ordinal) ? "youtube-music"
        : tabTitle.Contains(" - YouTube", StringComparison.Ordinal) ? "youtube"
        : null;

    // Address bar = kontrol Edit pertama di jendela browser (Chrome/Edge/Brave/Opera/Firefox).
    // Nama aksesibelnya dilokalkan ("Address and search bar" / "Bilah alamat…"), jadi jangan cari berdasarkan nama.
    private static string? ReadAddressBar(AutomationElement window)
    {
        var edit = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        return edit?.TryGetCurrentPattern(ValuePattern.Pattern, out var p) == true
            ? ((ValuePattern)p).Current.Value
            : null;
    }

    /// <summary>Chrome menyembunyikan "https://" di address bar; nilai yang bukan URL (teks pencarian) → null.</summary>
    internal static Uri? NormalizeUrl(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.Contains(' '))
            return null;
        if (!raw.Contains("://"))
            raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri : null;
    }

    internal static (string Site, string? VideoId) ParseUrl(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m." })
        {
            if (host.StartsWith(prefix))
                host = host[prefix.Length..];
        }
        var segments = url.AbsolutePath.Trim('/').Split('/');
        string? id = host switch
        {
            "youtu.be" => segments[0],
            "youtube.com" or "music.youtube.com" when segments[0] == "watch" => QueryValue(url, "v"),
            "youtube.com" when segments is ["shorts" or "live" or "embed", var x, ..] => x,
            _ => null,
        };
        var site = host switch
        {
            "youtube.com" or "youtu.be" => "youtube",
            "music.youtube.com" => "youtube-music",
            _ => host,
        };
        return (site, string.IsNullOrEmpty(id) ? null : id);
    }

    private static string? QueryValue(Uri url, string key)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// <summary>Huruf kecil, hanya huruf/angka, spasi tunggal — judul GSMTC, jendela, dan YouTube
    /// berbeda di spasi ganda & tanda baca.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.ToLowerInvariant())
        {
            // Harakat Arab = tanda gabung; lewati agar tidak memecah kata
            if (char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (space && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(c);
                space = false;
            }
            else
            {
                space = true;
            }
        }
        return sb.ToString();
    }
}
