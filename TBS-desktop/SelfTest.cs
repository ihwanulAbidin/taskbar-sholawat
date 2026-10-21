using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace TaskbarSholawat;

/// <summary>
/// Uji risiko M0: exe single-file berjalan tanpa .NET terpasang, WPF merender teks Arab,
/// dan Whisper.net memuat native + model dari models/.
/// Pemakaian: TaskbarSholawat.exe --selftest [--wav berkas.wav] [--lang ar]
/// M3: --selftest --media [--seconds 60] → probe GSMTC (<see cref="MediaProbe"/>).
/// M5: --selftest --capture [--process chrome|auto] [--seconds 30] → probe loopback (<see cref="CaptureProbe"/>).
/// Alur pengenalan 1–3: --selftest --source → judul, sumber, URL, description (<see cref="SourceProbe"/>).
/// Alur 1→5 nyata: --selftest --recognize [--seconds 180] [--known id] [--no-prompt] … (<see cref="RecognizeProbe"/>).
/// Langkah 5 (audio→teks): --selftest --asr [--seconds 60] [--model path] [--prompt-file f] … (<see cref="AsrProbe"/>).
/// Takarir YouTube: --selftest --transcript [--video ID] [--lang ar] [--sync 10] (<see cref="TranscriptProbe"/>).
/// Takarir → timing baris lirik → .lrc: --selftest --align [--video ID] [--known id] [--no-save] [--sync 20] (<see cref="AlignProbe"/>).
/// Langkah 4 (teks dari web): --selftest --lyrics [--title "judul"] [--no-save] (<see cref="LyricProbe"/>).
/// Hasil ditulis ke selftest.log di samping exe (WinExe tidak punya konsol).
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--media"))
            return await MediaProbe.RunAsync(args);
        if (args.Contains("--capture"))
            return await CaptureProbe.RunAsync(args);
        if (args.Contains("--source"))
            return await SourceProbe.RunAsync(args);
        if (args.Contains("--recognize"))
            return await RecognizeProbe.RunAsync(args);
        if (args.Contains("--asr"))
            return await AsrProbe.RunAsync(args);
        if (args.Contains("--align"))
            return await AlignProbe.RunAsync(args);
        if (args.Contains("--transcript"))
            return await TranscriptProbe.RunAsync(args);
        if (args.Contains("--lyrics"))
            return await LyricProbe.RunAsync(args);

        var log = new StringBuilder();
        void Log(string msg) => log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");
        var failed = false;

        Log($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Log($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Log($"ProcessPath: {Environment.ProcessPath}");
        Log($"AppContext.BaseDirectory: {AppContext.BaseDirectory}");
        Log($"Single-file: {string.IsNullOrEmpty(typeof(SelfTest).Assembly.Location)}");

        try
        {
            await RenderWindowAsync();
            Log("WPF: jendela teks Arab ter-render [OK]");
        }
        catch (Exception ex)
        {
            failed = true;
            Log($"WPF: GAGAL {ex}");
        }

        try
        {
            await RunWhisperAsync(args, Log);
        }
        catch (Exception ex)
        {
            failed = true;
            Log($"Whisper: GAGAL {ex}");
        }

        Log(failed ? "HASIL: GAGAL" : "HASIL: OK");
        await File.WriteAllTextAsync(Path.Combine(PortablePaths.AppDir, "selftest.log"), log.ToString());
        return failed ? 1 : 0;
    }

    private static async Task RenderWindowAsync()
    {
        var rendered = new TaskCompletionSource();
        var window = new Window
        {
            Title = "Taskbar Sholawat — selftest",
            Width = 480,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock
            {
                FlowDirection = FlowDirection.RightToLeft,
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "اَللّٰهُمَّ صَلِّ عَلٰى سَيِّدِنَا مُحَمَّدٍ",
            },
        };
        window.ContentRendered += (_, _) => rendered.TrySetResult();
        window.Show();
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1000);
        window.Close();
    }

    private static async Task RunWhisperAsync(string[] args, Action<string> log)
    {
        using var _ = LogProvider.AddLogger((level, msg) =>
        {
            // Pesan loader (Debug) yang menyebut jalur DLL native — inti uji M0
            if (msg is not null && (level <= WhisperLogLevel.Warning
                || msg.Contains("load", StringComparison.OrdinalIgnoreCase)))
                log($"  [whisper {level}] {msg.TrimEnd()}");
        });

        var model = Directory.Exists(PortablePaths.Models)
            ? Directory.EnumerateFiles(PortablePaths.Models, "*.bin").OrderBy(f => f).FirstOrDefault()
            : null;
        if (model is null)
            throw new FileNotFoundException($"Tidak ada model *.bin di {PortablePaths.Models}");
        log($"Whisper: model {model} ({new FileInfo(model).Length / 1024 / 1024} MB)");

        log($"Whisper: NATIVE_DLL_SEARCH_DIRECTORIES = {AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")}");
        log($"Whisper: RuntimeOptions.LibraryPath = {RuntimeOptions.LibraryPath ?? "(default)"}");

        var sw = Stopwatch.StartNew();
        using var factory = WhisperFactory.FromPath(model);
        log($"Whisper: native termuat ({RuntimeOptions.LoadedLibrary}), model dimuat dalam {sw.ElapsedMilliseconds} ms [OK]");

        var wav = ArgValue(args, "--wav");
        if (wav is null)
        {
            log("Whisper: --wav tidak diberikan, transkripsi dilewati");
            return;
        }

        using var processor = factory.CreateBuilder()
            .WithLanguage(ArgValue(args, "--lang") ?? "auto")
            .Build();
        await using var stream = File.OpenRead(wav);
        sw.Restart();
        var segments = 0;
        await foreach (var seg in processor.ProcessAsync(stream))
        {
            segments++;
            log($"  {seg.Start:mm\\:ss\\.ff}->{seg.End:mm\\:ss\\.ff} [{seg.Language}] {seg.Text.Trim()}");
        }
        log($"Whisper: {segments} segmen dalam {sw.ElapsedMilliseconds} ms [{(segments > 0 ? "OK" : "KOSONG")}]");
        if (segments == 0)
            throw new InvalidOperationException("Transkripsi tidak menghasilkan segmen");
    }

    internal static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
