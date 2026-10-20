using System.Windows;
using System.Windows.Media;
using Deskband11Lib.Wpf;
using Microsoft.Win32;

namespace TaskbarSholawat;

/// <summary>
/// Mode A (PLAN bagian 3.2 TaskbarHost): baris Arab + terjemah di dalam taskbar.
/// Penempatan di celah kosong taskbar, pemulihan saat explorer restart, dan perubahan DPI
/// ditangani Deskband11Lib; jendela ini hanya isi dan tema.
/// </summary>
public partial class TaskbarWindow : Window
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public TaskbarContentHost Host { get; }

    public TaskbarWindow()
    {
        InitializeComponent();
        Host = new TaskbarContentHost(this, Root, new()
        {
            PreferredWidth = double.MaxValue,
            // Polling UIA hanya cadangan; perubahan ikon dipicu TaskbarChangeWatcher (App)
            LayoutRefreshInterval = TimeSpan.FromSeconds(10),
        });
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void ShowLine(string arabic, string? translation)
    {
        Arabic.Text = arabic;
        Translation.Text = translation ?? "";
        Translation.Visibility = string.IsNullOrEmpty(translation) ? Visibility.Collapsed : Visibility.Visible;
    }

    public Task AttachAsync() => Host.AttachWhenLayoutReadyAsync();

    /// <summary>Warna teks mengikuti tema taskbar (SystemUsesLightTheme), bukan tema aplikasi.</summary>
    private void ApplyTheme()
    {
        var light = Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 0) is int v && v != 0;
        Foreground = light ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : Brushes.White;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplyTheme);

    private void OnExitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Host.Dispose();
        base.OnClosed(e);
    }
}
