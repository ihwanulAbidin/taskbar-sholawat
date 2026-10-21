using System.Windows;
using System.Windows.Interop;
using Deskband11Lib.Wpf;
using Microsoft.Win32;
using static TaskbarSholawat.NativeMethods;

namespace TaskbarSholawat;

/// <summary>
/// Mode A (PLAN bagian 3.2 TaskbarHost): baris Arab + terjemah di dalam taskbar bawah/atas.
/// Penempatan di celah kosong taskbar, pemulihan saat explorer restart, dan perubahan DPI
/// ditangani Deskband11Lib; jendela ini hanya isi dan tema.
/// </summary>
public partial class TaskbarWindow : Window
{
    public TaskbarContentHost Host { get; }

    public LyricView LyricView => Lyric;

    public TaskbarWindow()
    {
        InitializeComponent();
        Host = new TaskbarContentHost(this, Lyric, new()
        {
            PreferredWidth = double.MaxValue,
            // Polling UIA hanya cadangan; perubahan ikon dipicu TaskbarChangeWatcher (App)
            LayoutRefreshInterval = TimeSpan.FromSeconds(10),
        });
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public Task AttachAsync() => Host.AttachWhenLayoutReadyAsync();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Sebelum dijadikan anak taskbar, jendela ini sempat tampil sebagai top-level; shell lalu
        // mencatatnya sebagai jendela aplikasi (kotak abu-abu di Task View / Alt+Tab) dan tidak
        // menghapusnya setelah SetParent. Tool window tidak pernah dicatat
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Tinggi terkunci taskbar (48 px pada 100%). ≤ 1: kedua baris mengecil. &gt; 1: baris Arab
    /// membesar memakai seluruh tinggi (Viewbox membatasi), terjemah pindah ke tooltip.
    /// </summary>
    public void SetTextScale(double scale)
    {
        Lyric.SetTextScale(scale);
        Lyric.TranslationInTooltip = scale > 1;
    }

    private void ApplyTheme() => Foreground = TaskbarTheme.Foreground;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplyTheme);

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Host.Dispose();
        base.OnClosed(e);
    }
}
