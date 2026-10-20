using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Deskband11Lib.Wpf;

namespace TaskbarSholawat;

public partial class App : Application
{
    // M1: baris statis (Burdah, domain publik) untuk uji tampilan; diganti pustaka lirik di M4
    private const string SampleArabic = "مَوْلَايَ صَلِّ وَسَلِّمْ دَائِمًا أَبَدًا، عَلَى حَبِيبِكَ خَيْرِ الْخَلْقِ كُلِّهِمِ";
    private const string SampleTranslation = "Wahai Tuhanku, limpahkanlah sholawat dan salam selamanya kepada kekasih-Mu, sebaik-baik seluruh makhluk";

    private static Mutex? s_singleInstance;
    private TaskbarWindow? _window;
    private TaskbarChangeWatcher? _taskbarWatcher;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => WriteErrorLog(ex.Exception);

        // Render GPU (D3D9) di Intel UHD memakan ±580 MB & ±38 thread untuk satu strip teks;
        // render software ±40 MB. Diukur M1, 2026-10-20 (DECISIONS.md)
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        if (e.Args.Contains("--selftest"))
        {
            WhisperNative.Configure();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfTest.RunAsync(e.Args));
            return;
        }

        s_singleInstance = new Mutex(true, @"Local\TaskbarSholawat", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // Jendela taskbar dibuat ulang saat explorer restart / DPI berubah; jangan ikut menutup aplikasi
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _taskbarWatcher = new TaskbarChangeWatcher();
        _taskbarWatcher.Changed += OnTaskbarButtonsChanged;
        await ShowTaskbarWindowAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbarWatcher?.Dispose();
        base.OnExit(e);
    }

    // RefreshLayout memakai ukuran ikon hasil UIA sebelumnya lalu mengantre pembacaan baru,
    // jadi panggil lagi setelah pembacaan selesai agar posisi akhir terpakai
    private async void OnTaskbarButtonsChanged()
    {
        _window?.Host.RefreshLayout();
        await Task.Delay(800);
        _window?.Host.RefreshLayout();
    }

    private async Task ShowTaskbarWindowAsync()
    {
        var window = new TaskbarWindow();
        _window = window;
        window.Host.TaskbarWindowRecreationRequired += OnTaskbarWindowRecreationRequired;
        window.ShowLine(SampleArabic, SampleTranslation);

        await window.AttachAsync();
        window.Show();
    }

    // Pola dari Deskband11Lib.Wpf.Sample: lepas jendela lama, tunggu taskbar siap, buat jendela baru
    private async void OnTaskbarWindowRecreationRequired(object? sender, EventArgs e)
    {
        var old = _window;
        if (old is not null)
        {
            old.Host.TaskbarWindowRecreationRequired -= OnTaskbarWindowRecreationRequired;
            old.Host.Dispose();
            _window = null;
        }

        await Task.Delay(1000);
        await ShowTaskbarWindowAsync();

        if (old?.IsWindowAlive() == true) old.Close();
    }

    /// <summary>WinExe tidak punya konsol — galat dicatat ke error.log di samping exe.</summary>
    private static void WriteErrorLog(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(PortablePaths.AppDir, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
