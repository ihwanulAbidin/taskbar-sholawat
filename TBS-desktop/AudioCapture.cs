using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TaskbarSholawat;

/// <summary>Microphone = versi lanjut (keputusan user 2026-10-20), belum diimplementasi.</summary>
public enum CaptureSource { System, Process, Microphone }

public sealed record CaptureStats(
    long Packets,
    long Discontinuities,
    long SilentPackets,
    TimeSpan GapFilled,
    float Peak,
    double Rms,
    TimeSpan Captured);

/// <summary>
/// Loopback audio PC → 16 kHz mono float → ring buffer 30 s (PLAN bagian 3.2, M5).
/// Resample & downmix dikerjakan engine audio Windows (shared mode + AutoConvertPcm lewat
/// <see cref="WasapiRecorderBuilder.WithFormat"/>), bukan oleh kode ini.
/// </summary>
public sealed class AudioCapture : IAsyncDisposable
{
    public const int SampleRate = 16000;
    public static readonly TimeSpan BufferLength = TimeSpan.FromSeconds(30);

    // Celah di bawah ini dianggap jitter jadwal paket, bukan jeda audio
    private static readonly long GapThreshold100ns = TimeSpan.FromMilliseconds(30).Ticks;

    private readonly WasapiRecorder _recorder;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[SampleRate * (int)BufferLength.TotalSeconds];
    private long _written;      // total sampel termasuk pengisi celah; posisi tulis = _written % panjang
    private long _nextQpc;      // QPC (100 ns) yang diharapkan untuk paket berikutnya; 0 = belum ada paket
    private long _packets, _discontinuities, _silentPackets, _gapSamples;
    private float _peak;
    private double _sumSquares;
    private long _audioSamples;

    public string Description { get; }

    /// <summary>Galat dari thread capture (mis. perangkat dicabut); null = berhenti karena diminta.</summary>
    public event Action<Exception?>? Stopped;

    private AudioCapture(WasapiRecorder recorder, string description)
    {
        _recorder = recorder;
        Description = description;
        var f = recorder.WaveFormat;
        if (f.Encoding != WaveFormatEncoding.IeeeFloat || f.SampleRate != SampleRate || f.Channels != 1)
            throw new NotSupportedException($"Format capture {f} — diharapkan float {SampleRate} Hz mono");
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    /// <param name="processId">Wajib untuk <see cref="CaptureSource.Process"/>: proses akar (pohon prosesnya ikut direkam).</param>
    public static async Task<AudioCapture> StartAsync(CaptureSource source, uint processId = 0)
    {
        var builder = new WasapiRecorderBuilder()
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        string description;
        switch (source)
        {
            case CaptureSource.System:
                builder.WithLoopbackCapture();
                description = "loopback sistem (perangkat putar default)";
                break;
            case CaptureSource.Process:
                if (processId == 0)
                    throw new ArgumentException("processId wajib untuk loopback per-proses", nameof(processId));
                builder.WithProcessLoopback(processId, ProcessLoopbackMode.IncludeTargetProcessTree);
                description = $"loopback proses {processId} + anaknya";
                break;
            default:
                throw new NotSupportedException("Mikrofon ditunda ke versi lanjut");
        }

        var recorder = await builder.BuildAsync();
        AudioCapture? capture = null;
        try
        {
            capture = new AudioCapture(recorder, description + $", {recorder.DeviceFriendlyName ?? "tanpa endpoint tetap"}");
            recorder.StartRecording();
            return capture;
        }
        catch
        {
            await recorder.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Proses akar untuk loopback per-proses dari AppUserModelId GSMTC ("Chrome", "MSEdge",
    /// "Spotify.exe", …) atau nama proses. Browser memutar audio di proses anak (audio service),
    /// jadi pilih proses yang memiliki jendela utama — itulah proses browser induk.
    /// </summary>
    public static uint? FindProcessId(string appIdOrName)
    {
        var name = Path.GetFileNameWithoutExtension(appIdOrName.Split('!')[0]);
        name = name.ToLowerInvariant() switch
        {
            "chrome" or "google.chrome" => "chrome",
            "msedge" or "microsoft.edge" => "msedge",
            _ => name,
        };
        var candidates = Process.GetProcessesByName(name);
        try
        {
            var root = candidates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                ?? candidates.OrderBy(p => SafeStartTime(p)).FirstOrDefault();
            return root is null ? null : (uint)root.Id;
        }
        finally
        {
            foreach (var p in candidates)
                p.Dispose();
        }
    }

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return DateTime.MaxValue; }
    }

    /// <summary>Salinan potongan terakhir (maks 30 s). Jeda sejak paket terakhir diisi nol, supaya
    /// audio lama tidak dikira sedang diputar.</summary>
    public float[] GetLatest(TimeSpan length)
    {
        lock (_gate)
        {
            var trailing = 0L;
            if (_nextQpc != 0)
            {
                var lag = NowQpc100ns() - _nextQpc;
                if (lag > GapThreshold100ns)
                    trailing = Math.Min(lag * SampleRate / TimeSpan.TicksPerSecond, _ring.Length);
            }

            var want = (int)Math.Min(length.Ticks * SampleRate / TimeSpan.TicksPerSecond, _ring.Length);
            var result = new float[want];
            var fromRing = (int)Math.Min(Math.Min(want - trailing, _written), _ring.Length);
            if (fromRing <= 0)
                return result;

            // Sampel ring [_written - fromRing, _written) ditaruh tepat sebelum ekor nol
            var dest = want - (int)trailing - fromRing;
            var start = (int)((_written - fromRing) % _ring.Length);
            var first = Math.Min(fromRing, _ring.Length - start);
            Array.Copy(_ring, start, result, dest, first);
            Array.Copy(_ring, 0, result, dest + first, fromRing - first);
            return result;
        }
    }

    public CaptureStats GetStats()
    {
        lock (_gate)
        {
            return new CaptureStats(_packets, _discontinuities, _silentPackets,
                TimeSpan.FromSeconds((double)_gapSamples / SampleRate), _peak,
                _audioSamples == 0 ? 0 : Math.Sqrt(_sumSquares / _audioSamples),
                TimeSpan.FromSeconds((double)_audioSamples / SampleRate));
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        lock (_gate)
        {
            _packets++;
            if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
                _discontinuities++;

            // Loopback tidak mengirim paket saat tidak ada yang diputar; isi jedanya dengan nol
            if (_nextQpc != 0 && !flags.HasFlag(AudioClientBufferFlags.TimestampError))
            {
                var gap = qpcPosition - _nextQpc;
                if (gap > GapThreshold100ns)
                {
                    var n = Math.Min(gap * SampleRate / TimeSpan.TicksPerSecond, _ring.Length);
                    WriteSilence(n);
                    _gapSamples += n;
                }
            }

            if (flags.HasFlag(AudioClientBufferFlags.Silent))
            {
                // Isi buffer tidak bermakna saat Silent — harus dianggap nol (dokumentasi WASAPI)
                _silentPackets++;
                WriteSilence(samples.Length);
            }
            else
            {
                foreach (var s in samples)
                {
                    var a = Math.Abs(s);
                    if (a > _peak)
                        _peak = a;
                    _sumSquares += (double)s * s;
                }
                Write(samples);
            }
            _audioSamples += samples.Length;
            _nextQpc = qpcPosition + samples.Length * TimeSpan.TicksPerSecond / SampleRate;
        }
    }

    private void Write(ReadOnlySpan<float> samples)
    {
        // Paket lebih panjang dari ring: hanya ekornya yang tersisa
        if (samples.Length > _ring.Length)
        {
            _written += samples.Length - _ring.Length;
            samples = samples[^_ring.Length..];
        }
        var pos = (int)(_written % _ring.Length);
        var first = Math.Min(samples.Length, _ring.Length - pos);
        samples[..first].CopyTo(_ring.AsSpan(pos));
        samples[first..].CopyTo(_ring);
        _written += samples.Length;
    }

    private void WriteSilence(long count)
    {
        var n = (int)Math.Min(count, _ring.Length);
        var pos = (int)(_written % _ring.Length);
        var first = Math.Min(n, _ring.Length - pos);
        Array.Clear(_ring, pos, first);
        Array.Clear(_ring, 0, n - first);
        _written += count;
    }

    // QPC yang dilaporkan WASAPI memakai satuan 100 ns; Stopwatch di Windows = QPC
    private static long NowQpc100ns() =>
        (long)((double)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    public async ValueTask DisposeAsync()
    {
        _recorder.DataAvailable -= OnData;
        await _recorder.DisposeAsync();
    }
}
