using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Uji AsrWorker pada audio yang sedang diputar, atau (--wav) pada berkas WAV 16 kHz mono agar bisa diulang.
/// Pemakaian: TaskbarSholawat.exe --selftest --asr [--seconds 60] [--model path] [--prompt-file f]
///   [--process chrome|auto] [--hop 3] [--window 8] [--beam 0] [--threads 0] [--audio-ctx 0] [--silence 0.003]
///   [--temp-inc 0.2] [--max-tokens 0] [--wav berkas.wav]
/// → asr.log di samping exe. Tanpa --process = loopback sistem; tanpa --model = *.bin pertama di models/.
/// Mode --wav: jendela digeser per hop di sepanjang berkas dan diproses berurutan (tanpa jam dinding).
/// </summary>
public static class AsrProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "asr.log"), append: false) { AutoFlush = true };
        // Dikunci: event worker datang dari thread latar
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            var seconds = IntArg(args, "--seconds", 60);
            var model = SelfTest.ArgValue(args, "--model")
                ?? (Directory.Exists(PortablePaths.Models)
                    ? Directory.EnumerateFiles(PortablePaths.Models, "*.bin").OrderBy(f => f).FirstOrDefault()
                    : null)
                ?? throw new FileNotFoundException($"Tidak ada model *.bin di {PortablePaths.Models}");
            var promptFile = SelfTest.ArgValue(args, "--prompt-file");
            var options = new AsrOptions
            {
                ModelPath = model,
                Hop = TimeSpan.FromSeconds(DoubleArg(args, "--hop", 3)),
                Window = TimeSpan.FromSeconds(DoubleArg(args, "--window", 8)),
                BeamSize = IntArg(args, "--beam", 0),
                Threads = IntArg(args, "--threads", 0),
                AudioContext = IntArg(args, "--audio-ctx", 0),
                TemperatureInc = SelfTest.ArgValue(args, "--temp-inc") is null ? null : (float)DoubleArg(args, "--temp-inc", 0.2),
                MaxTokensPerSegment = IntArg(args, "--max-tokens", 0),
                SilenceRms = DoubleArg(args, "--silence", 0.003),
                Prompt = promptFile is null ? null : await File.ReadAllTextAsync(promptFile),
            };

            Log($"Model: {model} ({new FileInfo(model).Length / 1024 / 1024} MB); hop {options.Hop.TotalSeconds} s, jendela {options.Window.TotalSeconds} s, beam {options.BeamSize}, threads {options.Threads}, audio-ctx {options.AudioContext}, temp-inc {options.TemperatureInc?.ToString(CultureInfo.InvariantCulture) ?? "default"}, max-tokens {options.MaxTokensPerSegment}, ambang RMS {options.SilenceRms}, prompt {(options.Prompt is null ? "tidak ada" : $"{options.Prompt.Length} karakter")}");
            if (SelfTest.ArgValue(args, "--wav") is { } wav)
                return await RunFileAsync(wav, options, Log);

            using var media = await MediaInfo.CreateAsync();
            var source = CaptureSource.System;
            uint pid = 0;
            if (SelfTest.ArgValue(args, "--process") is { } target)
            {
                if (target == "auto")
                    target = media.CurrentAppId ?? throw new InvalidOperationException("Tidak ada sesi media aktif untuk --process auto");
                pid = AudioCapture.FindProcessId(target) ?? throw new InvalidOperationException($"Proses untuk '{target}' tidak ditemukan");
                source = CaptureSource.Process;
            }
            await using var capture = await AudioCapture.StartAsync(source, pid);
            if (await media.ReadCurrentAsync() is { } snap)
                Log($"Media: {snap.AppId} | {snap.Status} | \"{snap.Title}\"");
            Log($"Audio: {capture.Description}");

            using var self = Process.GetCurrentProcess();
            var cpuLast = self.TotalProcessorTime;
            var wallLast = Stopwatch.StartNew();
            var cpuStart = cpuLast;
            var wallTotal = new Stopwatch();
            var latencies = new List<long>();
            var skippedHops = 0;
            var silent = 0;
            var failed = 0;

            // CPU sejak jendela sebelumnya (% satu core) — termasuk inferensi Whisper
            double CpuSinceLast()
            {
                self.Refresh();
                var now = self.TotalProcessorTime;
                var pct = (now - cpuLast).TotalMilliseconds / wallLast.Elapsed.TotalMilliseconds * 100;
                cpuLast = now;
                wallLast.Restart();
                return pct;
            }

            var load = Stopwatch.StartNew();
            await using (var worker = AsrWorker.Start(capture, options, media))
            {
                Log($"Model dimuat dalam {load.ElapsedMilliseconds} ms; kolom: jam jendela | posisi media | ms proses | hop dibuang | CPU % satu core | teks");
                self.Refresh();
                cpuStart = self.TotalProcessorTime;
                wallTotal.Start();
                worker.Transcribed += r =>
                {
                    lock (gate)
                    {
                        latencies.Add(r.ProcessingMs);
                        skippedHops += r.SkippedHops;
                    }
                    var pos = r.MediaStart is { } a && r.MediaEnd is { } b ? $"{Fmt(a)}–{Fmt(b)}" : "-";
                    Log($"{r.WindowStart:HH:mm:ss}–{r.WindowEnd:HH:mm:ss} | {pos} | {r.ProcessingMs} ms | {r.SkippedHops} | {CpuSinceLast():0} | {r.Text}");
                };
                worker.SkippedSilent += (end, rms) =>
                {
                    Interlocked.Increment(ref silent);
                    Log($"{end:HH:mm:ss} senyap (RMS {rms:0.00000}) — dilewati");
                };
                worker.Failed += ex =>
                {
                    Interlocked.Increment(ref failed);
                    Log($"GAGAL inferensi: {ex}");
                };
                await Task.Delay(TimeSpan.FromSeconds(seconds));
            }

            self.Refresh();
            var cpuAvg = (self.TotalProcessorTime - cpuStart).TotalMilliseconds / wallTotal.Elapsed.TotalMilliseconds * 100;
            lock (gate)
            {
                Log($"Ringkasan: {latencies.Count} jendela diproses, {silent} senyap, {skippedHops} hop dibuang, {failed} gagal");
                LogSummary(latencies, cpuAvg, Log);
            }
            Log($"HASIL: {(latencies.Count > 0 && failed == 0 ? "OK" : "GAGAL")}");
            return latencies.Count > 0 && failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static async Task<int> RunFileAsync(string path, AsrOptions options, Action<FormattableString> log)
    {
        float[] audio;
        using (var reader = new NAudio.Wave.WaveFileReader(path))
        {
            if (reader.WaveFormat.SampleRate != AudioCapture.SampleRate || reader.WaveFormat.Channels != 1)
                throw new NotSupportedException($"WAV {reader.WaveFormat} — diharapkan {AudioCapture.SampleRate} Hz mono");
            var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
            var buffer = new List<float>((int)reader.SampleCount);
            var chunk = new float[AudioCapture.SampleRate];
            int n;
            while ((n = provider.Read(chunk.AsSpan())) > 0)
                buffer.AddRange(chunk.AsSpan(0, n));
            audio = buffer.ToArray();
        }
        log($"WAV: {path} ({audio.Length / (double)AudioCapture.SampleRate:0.0} s)");

        var window = (int)(options.Window.TotalSeconds * AudioCapture.SampleRate);
        var hop = (int)(options.Hop.TotalSeconds * AudioCapture.SampleRate);
        var latencies = new List<long>();
        using var self = Process.GetCurrentProcess();
        var load = Stopwatch.StartNew();
        await using var engine = AsrEngine.Load(options);
        log($"Model dimuat dalam {load.ElapsedMilliseconds} ms; kolom: offset berkas | ms proses | segmen | teks");
        self.Refresh();
        var cpuStart = self.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        for (var end = Math.Min(window, audio.Length); end <= audio.Length; end += hop)
        {
            var samples = audio[Math.Max(0, end - window)..end];
            var rms = AsrWorker.Rms(samples);
            if (rms < options.SilenceRms)
            {
                log($"{Sec(end - samples.Length)}–{Sec(end)} senyap (RMS {rms:0.00000}) — dilewati");
                continue;
            }
            var (segments, ms) = await engine.TranscribeAsync(samples);
            latencies.Add(ms);
            log($"{Sec(end - samples.Length)}–{Sec(end)} | {ms} ms | {segments.Count} | {AsrWorker.JoinText(segments)}");
        }
        self.Refresh();
        var cpu = (self.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
        log($"Ringkasan: {latencies.Count} jendela diproses berurutan dalam {wall.Elapsed.TotalSeconds:0.0} s");
        // CPU di mode file = selama inferensi tanpa jeda (bukan beban live)
        LogSummary(latencies, cpu, log);
        log($"HASIL: {(latencies.Count > 0 ? "OK" : "GAGAL")}");
        return latencies.Count > 0 ? 0 : 1;
    }

    private static void LogSummary(List<long> latencies, double cpu, Action<FormattableString> log)
    {
        var sorted = latencies.Order().ToList();
        if (sorted.Count > 0)
            log($"Latensi ms: min {sorted[0]}, median {sorted[sorted.Count / 2]}, maks {sorted[^1]}, rata-rata {sorted.Average():0}");
        log($"CPU proses rata-rata: {cpu:0.0}% satu core = {cpu / Environment.ProcessorCount:0.0}% total ({Environment.ProcessorCount} core)");
    }

    private static string Sec(int samples) =>
        string.Create(CultureInfo.InvariantCulture, $"{samples / (double)AudioCapture.SampleRate:0.0}");

    private static int IntArg(string[] args, string name, int fallback) =>
        int.TryParse(SelfTest.ArgValue(args, name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double DoubleArg(string[] args, string name, double fallback) =>
        double.TryParse(SelfTest.ArgValue(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Fmt(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t:ss\\.f}");
}
