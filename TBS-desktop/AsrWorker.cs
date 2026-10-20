using System.Diagnostics;

namespace TaskbarSholawat;

/// <param name="MediaStart">Posisi media terekstrapolasi (GSMTC) di awal jendela; null bila tidak ada sesi media.</param>
/// <param name="SkippedHops">Hop yang dilewati karena inferensi sebelumnya lebih lama dari hop.</param>
public sealed record AsrResult(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    TimeSpan? MediaStart,
    TimeSpan? MediaEnd,
    string Text,
    IReadOnlyList<AsrSegment> Segments,
    long ProcessingMs,
    int SkippedHops);

/// <summary>
/// Alur pengenalan langkah 5 (sisi audio→teks): tiap hop ambil jendela audio terakhir dari
/// <see cref="AudioCapture"/> → Whisper. Satu inferensi pada satu waktu; bila tertinggal, hop yang
/// terlewat dibuang dan yang diproses selalu jendela terbaru. Event dipicu dari thread latar.
/// </summary>
public sealed class AsrWorker : IAsyncDisposable
{
    private readonly AudioCapture _capture;
    private readonly MediaInfo? _media;
    private readonly AsrEngine _engine;
    private readonly AsrOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public event Action<AsrResult>? Transcribed;

    /// <summary>Jendela senyap yang dilewati (waktu akhir jendela, RMS).</summary>
    public event Action<DateTimeOffset, double>? SkippedSilent;

    /// <summary>Galat inferensi; loop tetap berjalan ke hop berikutnya.</summary>
    public event Action<Exception>? Failed;

    private AsrWorker(AudioCapture capture, MediaInfo? media, AsrEngine engine)
    {
        _capture = capture;
        _media = media;
        _engine = engine;
        _options = engine.Options;
    }

    /// <summary>Muat model sekali (ratusan ms – detik), lalu mulai loop.</summary>
    public static AsrWorker Start(AudioCapture capture, AsrOptions options, MediaInfo? media = null)
    {
        if (options.Window <= TimeSpan.Zero || options.Window > AudioCapture.BufferLength)
            throw new ArgumentOutOfRangeException(nameof(options), $"Window harus 0 < x ≤ {AudioCapture.BufferLength.TotalSeconds} s");
        if (options.Hop <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Hop harus > 0");

        var worker = new AsrWorker(capture, media, AsrEngine.Load(options));
        worker._loop = Task.Run(() => worker.RunAsync(worker._stop.Token));
        return worker;
    }

    /// <inheritdoc cref="AsrEngine.SetPrompt"/>
    public void SetPrompt(string? prompt) => _engine.SetPrompt(prompt);

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.Hop);
        var lastRun = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // PeriodicTimer menggabungkan tick yang terlewat jadi satu, jadi jendela yang diambil
                // di bawah selalu yang terbaru; hitung berapa hop yang terbuang
                var skipped = Math.Max(0, (int)(lastRun.Elapsed / _options.Hop) - 1);
                lastRun.Restart();
                await ProcessLatestAsync(skipped, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessLatestAsync(int skippedHops, CancellationToken ct)
    {
        var end = DateTimeOffset.Now;
        var samples = _capture.GetLatest(_options.Window);
        var start = end - _options.Window;
        var media = _media is null ? null : await _media.ReadCurrentAsync();

        var rms = Rms(samples);
        if (rms < _options.SilenceRms)
        {
            SkippedSilent?.Invoke(end, rms);
            return;
        }

        try
        {
            var (segments, ms) = await _engine.TranscribeAsync(samples, ct);
            TimeSpan? mediaEnd = media?.PositionAt(end);
            TimeSpan? mediaStart = media is null ? null
                : media.IsPlaying ? Max(TimeSpan.Zero, mediaEnd!.Value - _options.Window * media.PlaybackRate)
                : mediaEnd;
            Transcribed?.Invoke(new AsrResult(start, end, mediaStart, mediaEnd, JoinText(segments),
                segments, ms, skippedHops));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(ex);
        }
    }

    public static string JoinText(IEnumerable<AsrSegment> segments) =>
        string.Join(" ", segments.Select(s => s.Text).Where(t => t.Length > 0));

    public static double Rms(float[] x)
    {
        if (x.Length == 0)
            return 0;
        double sum = 0;
        foreach (var v in x)
            sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            // Tunggu loop keluar agar processor tidak di-dispose saat masih dipakai inferensi
            await _loop;
        }
        await _engine.DisposeAsync();
        _stop.Dispose();
    }
}
