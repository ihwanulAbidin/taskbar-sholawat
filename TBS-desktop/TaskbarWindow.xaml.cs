using System.Windows;
using Deskband11Lib.Wpf;
using Microsoft.Win32;

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
