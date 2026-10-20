using System.Diagnostics;
using System.Globalization;
using System.IO;
using NAudio.Wave;

namespace TaskbarSholawat;

/// <summary>
/// Uji M5: rekam loopback lalu tulis capture.wav (16 kHz mono, potongan terakhir ≤ 30 s dari ring
/// buffer) + capture.log di samping exe.
/// Pemakaian: TaskbarSholawat.exe --selftest --capture [--process chrome|auto] [--seconds 30]
/// "--process auto" = proses milik sesi media aktif (GSMTC); tanpa --process = loopback sistem.
/// </summary>
public static class CaptureProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var seconds = int.TryParse(SelfTest.ArgValue(args, "--seconds"), out var s) && s > 0 ? s : 30;
        await using var writer = new StreamWriter(Path.Combine(PortablePaths.AppDir, "capture.log"), append: false) { AutoFlush = true };
        // FormattableString: angka diformat invarian (locale Indonesia memakai koma desimal)
        // Dikunci: Stopped dipicu dari thread capture
        var gate = new object();
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            var source = CaptureSource.System;
            uint pid = 0;
            if (SelfTest.ArgValue(args, "--process") is { } target)
            {
                if (target == "auto")
                {
                    using var media = await MediaInfo.CreateAsync();
                    target = media.CurrentAppId ?? throw new InvalidOperationException("Tidak ada sesi media aktif untuk --process auto");
                    Log($"GSMTC sesi aktif: {target}");
                }
                pid = AudioCapture.FindProcessId(target) ?? throw new InvalidOperationException($"Proses untuk '{target}' tidak ditemukan");
                source = CaptureSource.Process;
                Log($"Target proses: {target} → PID {pid}");
            }

            await using var capture = await AudioCapture.StartAsync(source, pid);
            capture.Stopped += ex => Log($"STOPPED {ex?.Message ?? "(diminta)"}");
            Log($"Mulai: {capture.Description}, {seconds} s; kolom per detik: peak dBFS | RMS dBFS | paket | diskontinuitas | celah diisi");

            using var self = Process.GetCurrentProcess();
            var cpuStart = TimeSpan.Zero;
            var wall = new Stopwatch();
            for (var i = 0; i < seconds; i++)
            {
                await Task.Delay(1000);
                // CPU diukur setelah 2 s pemanasan: detik awal didominasi JIT & start WPF
                if (i == Math.Min(2, seconds - 1))
                {
                    self.Refresh();
                    cpuStart = self.TotalProcessorTime;
                    wall.Start();
                }
                var last = capture.GetLatest(TimeSpan.FromSeconds(1));
                var st = capture.GetStats();
                Log($"{i + 1,3} s | {Db(Peak(last)),6:0.0} | {Db(Rms(last)),6:0.0} | {st.Packets} | {st.Discontinuities} | {st.GapFilled.TotalSeconds:0.00} s");
            }

            self.Refresh();
            var cpu = wall.Elapsed > TimeSpan.Zero
                ? (self.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100
                : 0;
            var total = capture.GetStats();
            var clip = capture.GetLatest(TimeSpan.FromSeconds(Math.Min(seconds, AudioCapture.BufferLength.TotalSeconds)));
            var wav = Path.Combine(PortablePaths.AppDir, "capture.wav");
            await using (var wf = new WaveFileWriter(wav, new WaveFormat(AudioCapture.SampleRate, 16, 1)))
                wf.WriteSamples(clip, 0, clip.Length);

            Log($"Total: {total.Captured.TotalSeconds:0.00} s audio dari WASAPI, {total.Packets} paket, {total.SilentPackets} paket Silent, {total.Discontinuities} diskontinuitas, celah diisi {total.GapFilled.TotalSeconds:0.00} s");
            Log($"Level: peak {Db(total.Peak):0.0} dBFS ({total.Peak:0.000}), RMS {Db(total.Rms):0.0} dBFS");
            Log($"CPU proses (tanpa 2 s awal, {wall.Elapsed.TotalSeconds:0.0} s): {cpu:0.00}% satu core = {cpu / Environment.ProcessorCount:0.00}% total ({Environment.ProcessorCount} core)");
            Log($"WAV: {wav} ({clip.Length / (double)AudioCapture.SampleRate:0.0} s)");
            Log($"HASIL: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static float Peak(float[] x) => x.Length == 0 ? 0 : x.Max(Math.Abs);

    private static double Rms(float[] x) => x.Length == 0 ? 0 : Math.Sqrt(x.Average(v => (double)v * v));

    private static double Db(double v) => v <= 0 ? double.NegativeInfinity : 20 * Math.Log10(v);
}
