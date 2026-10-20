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
    /// <summary>null = default whisper.cpp (0,2). 0 = tanpa temperature fallback: decode yang gagal
    /// (entropi/logprob buruk, token berulang) tidak diulang pada suhu lebih tinggi.</summary>
    public float? TemperatureInc { get; init; }
    /// <summary>0 = tanpa batas.</summary>
    public int MaxTokensPerSegment { get; init; }
}

public sealed record AsrSegment(TimeSpan Start, TimeSpan End, string Text, float Probability);

/// <summary>
/// Model Whisper dimuat sekali + satu processor. Tidak thread-safe: satu <see cref="TranscribeAsync"/>
/// pada satu waktu (dipakai berurutan oleh <see cref="AsrWorker"/> dan mode file probe).
/// </summary>
public sealed class AsrEngine : IAsyncDisposable
{
    private readonly AsrOptions _options;
    private readonly WhisperFactory _factory;
    private WhisperProcessor? _processor;
    private string? _prompt;
    private volatile bool _promptChanged = true;

    private AsrEngine(AsrOptions options, WhisperFactory factory)
    {
        _options = options;
        _factory = factory;
        _prompt = options.Prompt;
    }

    public AsrOptions Options => _options;

    public static AsrEngine Load(AsrOptions options)
    {
        // Exe single-file: arahkan loader native ke folder ekstraksi (lihat WhisperNative)
        WhisperNative.Configure();
        return new AsrEngine(options, WhisperFactory.FromPath(options.ModelPath));
    }

    /// <summary>Teks lirik yang diketahui (langkah 3/4) sebagai konteks awal Whisper; null = tanpa prompt.
    /// Berlaku mulai transkripsi berikutnya — Whisper.net hanya menerima prompt lewat builder, jadi
    /// processor dibangun ulang (model tidak dimuat ulang).</summary>
    public void SetPrompt(string? prompt)
    {
        _prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        _promptChanged = true;
    }

    public async Task<(List<AsrSegment> Segments, long ProcessingMs)> TranscribeAsync(float[] samples, CancellationToken ct = default)
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
        return (segments, sw.ElapsedMilliseconds);
    }

    private WhisperProcessor BuildProcessor(string? prompt)
    {
        var builder = _factory.CreateBuilder()
            .WithLanguage(_options.Language)
            // Tiap jendela berdiri sendiri; konteks dari jendela sebelumnya (yang tumpang tindih)
            // membuat Whisper mengulang teks lama. Prompt awal tetap dipakai whisper.cpp
            .WithNoContext();
        if (_options.Threads > 0)
            builder.WithThreads(_options.Threads);
        if (_options.AudioContext > 0)
            builder.WithAudioContextSize(_options.AudioContext);
        if (_options.TemperatureInc is { } inc)
            builder.WithTemperatureInc(inc);
        if (_options.MaxTokensPerSegment > 0)
            builder.WithMaxTokensPerSegment(_options.MaxTokensPerSegment);
        if (_options.BeamSize >= 2)
            builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(_options.BeamSize));
        else
            builder.WithGreedySamplingStrategy(_ => { });
        if (prompt is not null)
            builder.WithPrompt(prompt);
        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
        _factory.Dispose();
    }
}
