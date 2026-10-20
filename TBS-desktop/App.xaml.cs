using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Deskband11Lib.Wpf;

namespace TaskbarSholawat;

public partial class App : Application
{
    // M1: baris statis (Burdah, domain publik) untuk uji tampilan; diganti pustaka lirik di M4
    private const string SampleArabic = "مَوْلَايَ صَلِّ وَسَلِّمْ دَائِمًا أَبَدًا، عَلَى حَبِيبِكَ خَيْرِ الْخَلْقِ كُلِّهِمِ";
    private const string SampleTranslation = "Wahai Tuhanku, limpahkanlah sholawat dan salam selamanya kepada kekasih-Mu, sebaik-baik seluruh makhluk";

    private const string ExitEventName = @"Local\TaskbarSholawat.Exit";

    private static Mutex? s_singleInstance;
    private static EventWaitHandle? s_exitRequest;
    private AppSettings _settings = new();
    private TaskbarChangeWatcher? _taskbarWatcher;
    // Tepat satu yang aktif: Mode A (di dalam taskbar), B (strip AppBar), atau C (melayang)
    private TaskbarWindow? _taskbarWindow;
    private AppBarWindow? _appBar;
    private FloatingWindow? _floating;
    private bool _updatingDisplay;
    private bool _displayUpdatePending;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

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
        if (!isFirst || e.Args.Contains("--exit"))
        {
            // --exit: minta instance yang berjalan keluar normal (AppBar dilepas). Mode A tidak punya
            // jendela top-level terlihat, jadi taskkill tanpa /F tidak bisa menutupnya
            if (e.Args.Contains("--exit") && EventWaitHandle.TryOpenExisting(ExitEventName, out var request))
                request.Set();
            Shutdown();
            return;
        }

        s_exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(s_exitRequest, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);

        _settings = AppSettings.Load();
        // Jendela tampilan dibuat ulang / berganti mode; jangan ikut menutup aplikasi
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _taskbarWatcher = new TaskbarChangeWatcher();
        _taskbarWatcher.Changed += OnTaskbarButtonsChanged;
        _taskbarWatcher.WorkAreaChanged += () => _ = UpdateDisplayAsync();
        _taskbarWatcher.TaskbarCreated += OnTaskbarCreated;
        _taskbarWatcher.CloseRequested += Shutdown;
        await UpdateDisplayAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _appBar?.Unregister();
        _taskbarWatcher?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Pilih Mode A/B/C dari pengaturan + sisi taskbar. Dipanggil ulang tiap ada perubahan; tidak berbuat apa-apa bila sudah sesuai.</summary>
    private async Task UpdateDisplayAsync()
    {
        if (_updatingDisplay)
        {
            _displayUpdatePending = true;
            return;
        }

        _updatingDisplay = true;
        try
        {
            do
            {
                _displayUpdatePending = false;
                try
                {
                    await ApplyDisplayAsync();
                }
                catch (Exception ex)
                {
                    // Pemanggil sering membuang Task-nya (_ =); catat supaya galat tidak hilang diam-diam
                    WriteErrorLog(ex);
                }
            }
            while (_displayUpdatePending);
        }
        finally
        {
            _updatingDisplay = false;
        }
    }

    private async Task ApplyDisplayAsync()
    {
        if (_settings.DisplayMode == DisplayMode.Floating)
        {
            CloseTaskbarWindow();
            CloseAppBar();
            if (_floating is null) ShowFloatingWindow();
            return;
        }

        // Taskbar belum ada (explorer restart) → TaskbarCreated memicu lagi
        if (TaskbarLocator.TryGet() is not { } taskbar) return;

        // Mode A hanya untuk taskbar bawah/atas: Deskband11Lib menghitung tata letak horizontal
        if (_settings.DisplayMode == DisplayMode.Taskbar && !taskbar.IsVertical)
        {
            CloseAppBar();
            CloseFloatingWindow();
            if (_taskbarWindow is null) await ShowTaskbarWindowAsync();
            return;
        }

        CloseTaskbarWindow();
        CloseFloatingWindow();
        var edge = taskbar.Edge == TaskbarEdge.Top ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        if (_appBar?.Edge == edge) return;

        CloseAppBar();
        var appBar = new AppBarWindow(edge);
        _appBar = appBar;
        appBar.LyricView.ShowLine(SampleArabic, SampleTranslation);
        PrepareLyricView(appBar.LyricView);
        appBar.SetTextScale(_settings.GetTextScale(DisplayMode.Strip));
        // Ditutup dari luar (bukan CloseAppBar) → keluar, jangan tersisa proses tanpa tampilan
        appBar.Closed += (_, _) =>
        {
            if (_appBar == appBar) Shutdown();
        };
        appBar.Show();
    }

    private async Task ShowTaskbarWindowAsync()
    {
        var window = new TaskbarWindow();
        _taskbarWindow = window;
        window.Host.TaskbarWindowRecreationRequired += OnTaskbarWindowRecreationRequired;
        // HWND bisa hancur dari luar (ikut Shell_TrayWnd lama saat explorer restart) — jangan simpan jendela mati
        window.Closed += (_, _) =>
        {
            if (_taskbarWindow == window) _taskbarWindow = null;
        };
        window.LyricView.ShowLine(SampleArabic, SampleTranslation);
        PrepareLyricView(window.LyricView);
        window.SetTextScale(_settings.GetTextScale(DisplayMode.Taskbar));

        try
        {
            await window.AttachAsync();
        }
        catch (InvalidOperationException ex)
        {
            // Deskband11Lib menyerah menunggu taskbar (mis. tepat setelah explorer restart): coba lagi nanti
            WriteErrorLog(ex);
            if (_taskbarWindow == window) _taskbarWindow = null;
            window.Close();
            _ = RetryDisplayLaterAsync();
            return;
        }
        if (_taskbarWindow == window) window.Show();
    }

    private async Task RetryDisplayLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        await UpdateDisplayAsync();
    }

    private void CloseTaskbarWindow()
    {
        if (_taskbarWindow is not { } window) return;
        _taskbarWindow = null;
        window.Host.TaskbarWindowRecreationRequired -= OnTaskbarWindowRecreationRequired;
        window.Host.Dispose();
        window.Close();
    }

    private void CloseAppBar()
    {
        if (_appBar is not { } appBar) return;
        _appBar = null;
        appBar.Close();
    }

    private void ShowFloatingWindow()
    {
        var window = new FloatingWindow(_settings.FloatingX, _settings.FloatingY);
        _floating = window;
        window.LyricView.ShowLine(SampleArabic, SampleTranslation);
        PrepareLyricView(window.LyricView);
        window.SetTextScale(_settings.GetTextScale(DisplayMode.Floating));
        window.Moved += (x, y) =>
        {
            _settings.FloatingX = x;
            _settings.FloatingY = y;
            _settings.TrySave();
        };
        window.Closed += (_, _) =>
        {
            if (_floating == window) Shutdown();
        };
        window.Show();
    }

    private void CloseFloatingWindow()
    {
        if (_floating is not { } window) return;
        _floating = null;
        window.Close();
    }

    // Pola dari Deskband11Lib.Wpf.Sample: lepas jendela lama, tunggu taskbar siap, buat jendela baru
    private async void OnTaskbarWindowRecreationRequired(object? sender, EventArgs e)
    {
        // Hanya untuk host jendela yang masih aktif: bila TaskbarCreated sudah membuat jendela
        // baru, event terlambat dari host lama tidak boleh membuang jendela baru itu
        var old = _taskbarWindow;
        if (old is null || !ReferenceEquals(old.Host, sender)) return;

        old.Host.TaskbarWindowRecreationRequired -= OnTaskbarWindowRecreationRequired;
        old.Host.Dispose();
        _taskbarWindow = null;

        await Task.Delay(1000);
        await UpdateDisplayAsync();

        if (old.IsWindowAlive()) old.Close();
    }

    // Explorer restart menghapus pendaftaran AppBar; Mode A dipulihkan Deskband11Lib sendiri
    private async void OnTaskbarCreated()
    {
        CloseAppBar();
        await Task.Delay(1000);
        await UpdateDisplayAsync();
    }

    // RefreshLayout memakai ukuran ikon hasil UIA sebelumnya lalu mengantre pembacaan baru,
    // jadi panggil lagi setelah pembacaan selesai agar posisi akhir terpakai
    private async void OnTaskbarButtonsChanged()
    {
        _taskbarWindow?.Host.RefreshLayout();
        await Task.Delay(800);
        _taskbarWindow?.Host.RefreshLayout();
    }

    private ContextMenu CreateContextMenu()
    {
        var inTaskbar = new MenuItem();
        var strip = new MenuItem { Header = "Di atas taskbar (strip)" };
        var floating = new MenuItem { Header = "Melayang (subtitle)" };
        var resetPosition = new MenuItem { Header = "Kembalikan posisi melayang" };
        var size = new MenuItem { Header = "Ukuran teks" };
        var sizeOptions = TextScaleOptions.Select(scale =>
        {
            var item = new MenuItem { Header = $"{scale * 100:0}%", Tag = scale };
            item.Click += (_, _) => SetTextScale(scale);
            size.Items.Add(item);
            return item;
        }).ToList();
        var sizeNote = new MenuItem { IsEnabled = false };
        size.Items.Add(new Separator());
        size.Items.Add(sizeNote);
        var exit = new MenuItem { Header = "Keluar" };

        var menu = new ContextMenu { Items = { inTaskbar, strip, floating, resetPosition, new Separator(), size, new Separator(), exit } };
        menu.Opened += (_, _) =>
        {
            var vertical = TaskbarLocator.TryGet() is { IsVertical: true };
            inTaskbar.Header = vertical ? "Di dalam taskbar (tidak bisa saat taskbar di kiri/kanan)" : "Di dalam taskbar";
            inTaskbar.IsEnabled = !vertical;
            inTaskbar.IsChecked = _settings.DisplayMode == DisplayMode.Taskbar;
            strip.IsChecked = _settings.DisplayMode == DisplayMode.Strip;
            floating.IsChecked = _settings.DisplayMode == DisplayMode.Floating;
            resetPosition.Visibility = _settings.DisplayMode == DisplayMode.Floating ? Visibility.Visible : Visibility.Collapsed;
            var current = _settings.GetTextScale(ActiveMode);
            foreach (var item in sizeOptions) item.IsChecked = Math.Abs((double)item.Tag - current) < 0.001;
            size.Header = $"Ukuran teks ({current * 100:0}%)";
            sizeNote.Header = ActiveMode == DisplayMode.Taskbar
                ? "Di taskbar: > 100% → terjemah di tooltip · Ctrl + gulir"
                : "Ctrl + gulir mouse di atas teks";
        };
        inTaskbar.Click += (_, _) => SetDisplayMode(DisplayMode.Taskbar);
        strip.Click += (_, _) => SetDisplayMode(DisplayMode.Strip);
        floating.Click += (_, _) => SetDisplayMode(DisplayMode.Floating);
        resetPosition.Click += (_, _) =>
        {
            _floating?.ResetPosition();
            _settings.FloatingX = _settings.FloatingY = null;
            _settings.TrySave();
        };
        exit.Click += (_, _) => Shutdown();
        return menu;
    }

    private static readonly double[] TextScaleOptions = [0.75, 1, 1.25, 1.5, 2];
    private const double TextScaleWheelStep = 0.1;

    /// <summary>Mode yang sedang tampil (bisa beda dari pengaturan: Mode A di taskbar vertikal → strip).</summary>
    private DisplayMode ActiveMode =>
        _floating is not null ? DisplayMode.Floating
        : _appBar is not null ? DisplayMode.Strip
        : DisplayMode.Taskbar;

    private void PrepareLyricView(LyricView view)
    {
        view.ContextMenu = CreateContextMenu();
        view.PreviewMouseWheel += OnLyricMouseWheel;
    }

    private void OnLyricMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        SetTextScale(_settings.GetTextScale(ActiveMode) + Math.Sign(e.Delta) * TextScaleWheelStep);
    }

    /// <summary>Ukuran teks disimpan per mode; berlaku untuk mode yang sedang tampil.</summary>
    private void SetTextScale(double scale)
    {
        var mode = ActiveMode;
        _settings.SetTextScale(mode, scale);
        _settings.TrySave();
        scale = _settings.GetTextScale(mode);
        _taskbarWindow?.SetTextScale(scale);
        _appBar?.SetTextScale(scale);
        _floating?.SetTextScale(scale);
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        if (_settings.DisplayMode == mode) return;
        _settings.DisplayMode = mode;
        _settings.TrySave();
        // Jendela pemilik menu akan ditutup; tunggu menu selesai menutup dulu
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = UpdateDisplayAsync());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Proses akan berhenti: lepaskan ruang layar AppBar supaya desktop tidak tersisa celah
        _appBar?.Unregister();
        WriteErrorLog(e.Exception);
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
