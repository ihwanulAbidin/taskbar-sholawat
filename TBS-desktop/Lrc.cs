using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskbarSholawat;

public sealed record LrcWord(TimeSpan Start, string Text);

/// <summary>Teks kosong = jeda/instrumental: tampilan dikosongkan sampai baris berikutnya.</summary>
public sealed record LrcLine(TimeSpan Start, string Text, IReadOnlyList<LrcWord> Words);

/// <summary>
/// LRC + Enhanced LRC (note.md bagian 2): <c>[mm:ss.xx]</c> per baris, <c>&lt;mm:ss.xx&gt;</c> per kata,
/// tag <c>[offset:±ms]</c>. Baris rusak dilewati dan dicatat di <see cref="Errors"/>, tidak melempar.
/// </summary>
public sealed partial class Lrc
{
    private readonly TimeSpan[] _starts;

    private Lrc(List<LrcLine> lines, TimeSpan offset, List<string> errors)
    {
        Lines = lines;
        Offset = offset;
        Errors = errors;
        _starts = lines.Select(l => l.Start).ToArray();
    }

    /// <summary>Urut menurut <see cref="LrcLine.Start"/>; offset sudah diterapkan.</summary>
    public IReadOnlyList<LrcLine> Lines { get; }

    public TimeSpan Offset { get; }

    public IReadOnlyList<string> Errors { get; }

    public static Lrc Load(string path) => Parse(File.ReadAllText(path));

    public static Lrc Parse(string text)
    {
        var lines = new List<LrcLine>();
        var errors = new List<string>();
        var offset = TimeSpan.Zero;

        var rows = text.Split('\n');
        for (var n = 0; n < rows.Length; n++)
        {
            var row = rows[n].Trim();
            if (row.Length == 0)
                continue;

            var stamps = new List<TimeSpan>();
            var pos = 0;
            var broken = false;
            while (pos < row.Length && row[pos] == '[')
            {
                var close = row.IndexOf(']', pos);
                if (close < 0)
                {
                    broken = true;
                    break;
                }

                var tag = row[(pos + 1)..close];
                if (TryParseTime(tag, out var t))
                {
                    stamps.Add(t);
                }
                else if (stamps.Count == 0 && MetaTag().Match(tag) is { Success: true } meta)
                {
                    if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out var ms))
                            offset = TimeSpan.FromMilliseconds(ms);
                        else
                            errors.Add($"baris {n + 1}: offset tidak valid \"{tag}\"");
                    }
                    pos = row.Length;
                    break;
                }
                else
                {
                    broken = true;
                    break;
                }
                pos = close + 1;
            }

            if (broken)
            {
                errors.Add($"baris {n + 1}: timestamp rusak \"{row}\"");
                continue;
            }
            if (stamps.Count == 0)
            {
                if (pos < row.Length)
                    errors.Add($"baris {n + 1}: tanpa timestamp \"{row}\"");
                continue;
            }

            var (lineText, words) = ParseBody(row[pos..]);
            // Satu teks dengan beberapa timestamp (mis. reff diulang): waktu kata digeser
            // relatif terhadap timestamp pertama
            foreach (var start in stamps)
            {
                var shift = start - stamps[0];
                lines.Add(new LrcLine(start, lineText,
                    shift == TimeSpan.Zero ? words : words.Select(w => w with { Start = w.Start + shift }).ToList()));
            }
        }

        // Spesifikasi LRC: offset positif = lirik tampil lebih awal
        if (offset != TimeSpan.Zero)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                lines[i] = new LrcLine(l.Start - offset, l.Text,
                    l.Words.Select(w => w with { Start = w.Start - offset }).ToList());
            }
        }

        // OrderBy stabil: baris bertimestamp sama tetap urut seperti di berkas
        return new Lrc(lines.OrderBy(l => l.Start).ToList(), offset, errors);
    }

    /// <summary>Indeks baris terakhir yang sudah mulai pada <paramref name="position"/>; -1 bila belum ada.</summary>
    public int LineIndexAt(TimeSpan position) => LastStartedIndex(_starts, position);

    /// <summary>Indeks kata aktif di baris <paramref name="lineIndex"/>; -1 bila baris tanpa timing kata
    /// atau kata pertama belum mulai.</summary>
    public int WordIndexAt(int lineIndex, TimeSpan position)
    {
        if (lineIndex < 0 || lineIndex >= Lines.Count)
            return -1;
        var words = Lines[lineIndex].Words;
        var lo = 0;
        var hi = words.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (words[mid].Start <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    private static int LastStartedIndex(TimeSpan[] starts, TimeSpan position)
    {
        var i = Array.BinarySearch(starts, position);
        if (i < 0)
            return ~i - 1;
        // Timestamp kembar: ambil yang terakhir
        while (i + 1 < starts.Length && starts[i + 1] == position)
            i++;
        return i;
    }

    private static (string Text, List<LrcWord> Words) ParseBody(string body)
    {
        var words = new List<LrcWord>();
        var text = new StringBuilder();
        var pos = 0;
        TimeSpan? wordStart = null;
        var wordText = new StringBuilder();

        void FlushWord()
        {
            var w = wordText.ToString().Trim();
            // Tag tanpa teks sesudahnya = penanda akhir kata terakhir, bukan kata
            if (wordStart is { } s && w.Length > 0)
                words.Add(new LrcWord(s, w));
            wordText.Clear();
        }

        while (pos < body.Length)
        {
            if (body[pos] == '<')
            {
                var close = body.IndexOf('>', pos);
                if (close > 0 && TryParseTime(body[(pos + 1)..close], out var t))
                {
                    FlushWord();
                    wordStart = t;
                    pos = close + 1;
                    continue;
                }
            }
            // '<' tanpa timestamp valid diperlakukan sebagai teks biasa
            text.Append(body[pos]);
            wordText.Append(body[pos]);
            pos++;
        }
        FlushWord();
        return (text.ToString().Trim(), words);
    }

    private static bool TryParseTime(string tag, out TimeSpan time)
    {
        time = default;
        var m = TimeTag().Match(tag.Trim());
        if (!m.Success)
            return false;

        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (seconds >= 60)
            return false;

        var ms = 0;
        if (m.Groups[3].Success)
        {
            var frac = m.Groups[3].Value;
            ms = int.Parse(frac, CultureInfo.InvariantCulture) * (frac.Length switch { 1 => 100, 2 => 10, _ => 1 });
        }
        time = new TimeSpan(0, 0, minutes, seconds, ms);
        return true;
    }

    // mm:ss, mm:ss.x / .xx / .xxx; sebagian alat menulis mm:ss:xx
    [GeneratedRegex(@"^(\d{1,4}):(\d{1,2})(?:[.:](\d{1,3}))?$")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^([A-Za-z#]+)\s*:(.*)$")]
    private static partial Regex MetaTag();
}
