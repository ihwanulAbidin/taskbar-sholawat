using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using static TaskbarSholawat.NativeMethods;

namespace TaskbarSholawat;

/// <summary>
/// Mode B (PLAN bagian 3.2 AppBarWindow): strip AppBar horizontal yang memesan ruang layar,
/// di sisi dalam taskbar bawah/atas. Untuk taskbar kiri/kanan strip dipasang di tepi bawah
/// monitor (di samping taskbar), karena baris Arab tidak muat di strip vertikal.
/// </summary>
public partial class AppBarWindow : Window
{
    // Setebal taskbar Win11 (48 px pada 100%) supaya dua baris tampil sama seperti Mode A
    private const double BaseThicknessDip = 48;
    private double _textScale = 1;

    private readonly int _callbackMessage = RegisterWindowMessage("TaskbarSholawat.AppBar");
    private nint _hwnd;
    private bool _registered;
    private bool _positioning;

    /// <summary>Top atau Bottom.</summary>
    public TaskbarEdge Edge { get; }

    public LyricView LyricView => Lyric;

    public AppBarWindow(TaskbarEdge edge)
    {
        InitializeComponent();
        Edge = edge;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);

        var data = NewAppBarData(_hwnd);
        data.uCallbackMessage = (uint)_callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != 0;
        SetPosition();
    }

    /// <summary>Strip menebal sesuai skala teks; ruang layar yang dipesan ikut berubah.</summary>
    public void SetTextScale(double scale)
    {
        _textScale = scale;
        Lyric.SetTextScale(scale);
        SetPosition();
    }

    /// <summary>Lepaskan ruang layar yang dipesan. Aman dipanggil berkali-kali.</summary>
    public void Unregister()
    {
        if (!_registered) return;
        var data = NewAppBarData(_hwnd);
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    private void SetPosition()
    {
        if (!_registered || _positioning) return;
        _positioning = true;
        try
        {
            if (TaskbarLocator.TryGet() is not { } taskbar) return;

            var thickness = (int)Math.Round(BaseThicknessDip * _textScale * GetMonitorDpi(taskbar.Monitor) / 96.0);
            var rc = taskbar.MonitorBounds;
            // Taskbar vertikal: strip horizontal di samping taskbar, bukan menimpanya
            if (taskbar.Edge == TaskbarEdge.Left) rc.Left = Math.Max(rc.Left, taskbar.Bounds.Right);
            if (taskbar.Edge == TaskbarEdge.Right) rc.Right = Math.Min(rc.Right, taskbar.Bounds.Left);
            FitToEdge(ref rc, thickness);

            // QUERYPOS menggeser rc keluar dari taskbar & AppBar lain, lalu tebal dipulihkan
            var data = NewAppBarData(_hwnd);
            data.uEdge = (uint)Edge;
            data.rc = rc;
            SHAppBarMessage(ABM_QUERYPOS, ref data);
            FitToEdge(ref data.rc, thickness);
            SHAppBarMessage(ABM_SETPOS, ref data);

            rc = data.rc;
            // Hormati status layar penuh (ABN_FULLSCREENAPP) — jangan naik lagi di atas aplikasi layar penuh
            SetWindowPos(_hwnd, Topmost ? HWND_TOPMOST : HWND_NOTOPMOST, rc.Left, rc.Top, rc.Width, rc.Height, SWP_NOACTIVATE);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void FitToEdge(ref RECT rc, int thickness)
    {
        if (Edge == TaskbarEdge.Top) rc.Bottom = rc.Top + thickness;
        else rc.Top = rc.Bottom - thickness;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _callbackMessage)
        {
            switch ((int)wParam)
            {
                case ABN_POSCHANGED:
                    SetPosition();
                    break;
                case ABN_FULLSCREENAPP:
                    // Aplikasi layar penuh: jangan tetap di atasnya
                    Topmost = lParam == 0;
                    break;
            }
            handled = true;
            return 0;
        }

        switch (msg)
        {
            case WM_ACTIVATE:
            case WM_WINDOWPOSCHANGED:
                if (_registered)
                {
                    var data = NewAppBarData(_hwnd);
                    SHAppBarMessage(msg == WM_ACTIVATE ? ABM_ACTIVATE : ABM_WINDOWPOSCHANGED, ref data);
                }
                break;
            case WM_DPICHANGED:
                // Biarkan WPF memproses DPI baru (skala isi) dulu, lalu timpa kotak sarannya
                // dengan tebal strip dari DPI monitor. Jangan set handled: WPF tak akan menskala ulang isi
                Dispatcher.BeginInvoke(SetPosition);
                break;
        }
        return 0;
    }

    private void ApplyTheme()
    {
        Foreground = TaskbarTheme.Foreground;
        Background = TaskbarTheme.StripBackground;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplyTheme);

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Unregister();
        base.OnClosed(e);
    }
}
