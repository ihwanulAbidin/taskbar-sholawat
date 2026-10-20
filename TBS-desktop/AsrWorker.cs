using System.Diagnostics;
using Whisper.net;

namespace TaskbarSholawat;

/// <summary>Semua nilai bisa diatur; default sementara sampai hasil pengukuran model/prompt/chunk final.</summary>
public sealed record AsrOptions
{
    public required string ModelPath { get; init; }
    public string Language { get; init; } = "ar";
    public TimeSpan Hop { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>RMS linear; 0,003 ≈ −50 dBFS. Jendela di bawahnya tidak dikirim ke Whisper.</summary>
    public double SilenceRms { get; init; } = 0.003;
    /// <summary>0 = greedy; ≥ 2 = beam search dengan ukuran ini.</summary>
    public int BeamSize { get; init; }
    /// <summary>0 = default whisper.cpp (min(4, jumlah core)).</summary>
    public int Threads { get; init; }
    public string? Prompt { get; init; }
    /// <summary>0 = penuh (1500 = 30 s). Encoder Whisper selalu menghitung 30 s walau jendela lebih
    /// pendek; nilai kecil (≈ jendela/30 × 1500) mempercepat dengan risiko akurasi turun.</summary>
    public int AudioContext { get; init; }
}

public sealed record AsrSegment(TimeSpan Start, TimeSpan End, string Text, float Probability);

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
    private readonly AsrOptions _options;
    private readonly WhisperFactory _factory;
    private readonly CancellationTokenSource _stop = new();
    private WhisperProcessor? _processor;
    private string? _prompt;
    private volatile bool _promptChanged;
    private Task? _loop;

    public event Action<AsrResult>? Transcribed;

    /// <summary>Jendela senyap yang dilewati (waktu akhir jendela, RMS).</summary>
    public event Action<DateTimeOffset, double>? SkippedSilent;

    /// <summary>Galat inferensi; loop tetap berjalan ke hop berikutnya.</summary>
    public event Action<Exception>? Failed;

    private AsrWorker(AudioCapture capture, MediaInfo? media, AsrOptions options, WhisperFactory factory)
    {
        _capture = capture;
        _media = media;
        _options = options;
        _factory = factory;
        _prompt = options.Prompt;
        _promptChanged = true;
    }

    /// <summary>Muat model sekali (ratusan ms – detik), lalu mulai loop.</summary>
    public static AsrWorker Start(AudioCapture capture, AsrOptions options, MediaInfo? media = null)
    {
        if (options.Window <= TimeSpan.Zero || options.Window > AudioCapture.BufferLength)
            throw new ArgumentOutOfRangeException(nameof(options), $"Window harus 0 < x ≤ {AudioCapture.BufferLength.TotalSeconds} s");
        if (options.Hop <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Hop harus > 0");

        // Exe single-file: arahkan loader native ke folder ekstraksi (lihat WhisperNative)
        WhisperNative.Configure();
        var worker = new AsrWorker(capture, media, options, WhisperFactory.FromPath(options.ModelPath));
        worker._loop = Task.Run(() => worker.RunAsync(worker._stop.Token));
        return worker;
    }

    /// <summary>Teks lirik yang diketahui (langkah 3/4) sebagai konteks awal Whisper; null = tanpa prompt.
    /// Berlaku mulai jendela berikutnya.</summary>
    public void SetPrompt(string? prompt)
    {
        _prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        _promptChanged = true;
    }

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
            if (_promptChanged || _processor is null)
            {
                _promptChanged = false;
                if (_processor is not null)
                    await _processor.DisposeAsync();
                _processor = BuildProcessor(_prompt);
            }

            var sw = Stopwatch.StartNew();
            var segments = new List<AsrSegment>();
            await foreach (var seg in _processor.ProcessAsync(samples, ct))
                segments.Add(new AsrSegment(seg.Start, seg.End, seg.Text.Trim(), seg.Probability));
            sw.Stop();

            TimeSpan? mediaEnd = media?.PositionAt(end);
            TimeSpan? mediaStart = media is null ? null
                : media.IsPlaying ? Max(TimeSpan.Zero, mediaEnd!.Value - _options.Window * media.PlaybackRate)
                : mediaEnd;
            Transcribed?.Invoke(new AsrResult(start, end, mediaStart, mediaEnd,
                string.Join(" ", segments.Select(s => s.Text).Where(t => t.Length > 0)),
                segments, sw.ElapsedMilliseconds, skippedHops));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(ex);
        }
    }

    private WhisperProcessor BuildProcessor(string? prompt)
    {
        var builder = _factory.CreateBuilder()
            .WithLanguage(_options.Language)
            // Tiap jendela berdiri sendiri; konteks dari jendela sebelumnya (yang tumpang tindih)
            // membuat Whisper mengulang teks lama
            .WithNoContext();
        if (_options.Threads > 0)
            builder.WithThreads(_options.Threads);
        if (_options.AudioContext > 0)
            builder.WithAudioContextSize(_options.AudioContext);
        if (_options.BeamSize >= 2)
            builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(_options.BeamSize));
        else
            builder.WithGreedySamplingStrategy(_ => { });
        if (prompt is not null)
            builder.WithPrompt(prompt);
        return builder.Build();
    }

    private static double Rms(float[] x)
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
        if (_processor is not null)
            await _processor.DisposeAsync();
        _factory.Dispose();
        _stop.Dispose();
    }
}
