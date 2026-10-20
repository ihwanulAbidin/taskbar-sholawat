using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using static TaskbarSholawat.NativeMethods;

namespace TaskbarSholawat;

/// <summary>
/// Mode C: lirik melayang seperti subtitle, selalu di atas, bisa digeser; posisi diingat.
/// Posisi disimpan & dipulihkan dalam piksel fisik (GetWindowRect/SetWindowPos), karena
/// Left/Top WPF dikonversi dengan DPI monitor yang sedang ditempati (Per-Monitor V2).
/// </summary>
public partial class FloatingWindow : Window
{
    // Jarak default dari tepi bawah area kerja, seperti posisi subtitle video
    private const double BottomMarginDip = 72;
    // Skala 1 di mode ini sedikit lebih besar dari taskbar, seperti subtitle
    private const double BaseScale = 1.4;
    // Sisi yang harus tetap berada di monitor agar posisi tersimpan dianggap terlihat
    private const int VisibleMarginPx = 20;

    private readonly int? _savedX;
    private readonly int? _savedY;
    private nint _hwnd;
    private bool _positioned;

    public LyricView LyricView => Lyric;

    /// <summary>Selesai digeser pengguna; argumen = posisi kiri-atas baru (piksel fisik).</summary>
    public event Action<int, int>? Moved;

    public FloatingWindow(int? x, int? y)
    {
        InitializeComponent();
        _savedX = x;
        _savedY = y;
        // Baris panjang mengecil (Viewbox di LyricView), tidak melebar melewati layar
        MaxWidth = SystemParameters.WorkArea.Width * 0.9;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; // di luar layar sampai ukuran diketahui (SizeToContent)
        Top = -32000;
    }

    public void SetTextScale(double scale) => Lyric.SetTextScale(BaseScale * scale);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Tidak mencuri fokus dari aplikasi yang sedang dipakai, tidak muncul di Alt+Tab
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_savedX is { } x && _savedY is { } y && IsVisibleAt(x, y))
        {
            MoveTo(x, y);
            _positioned = true;
        }
        else
        {
            ResetPosition();
        }
    }

    // Saat ukuran berubah (skala teks / baris baru), titik tengah-bawah tetap di tempat
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_positioned) return;
        if (sizeInfo.WidthChanged) Left -= (sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width) / 2;
        if (sizeInfo.HeightChanged) Top -= sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height;
    }

    /// <summary>Tengah bawah area kerja monitor utama.</summary>
    public void ResetPosition()
    {
        var monitor = PrimaryMonitor();
        if (!TryGetMonitorRects(monitor, out _, out var work)) return;

        // Pindah ke monitor utama dulu: bila DPI-nya beda, WPF mengubah ukuran jendela (WM_DPICHANGED);
        // ukuran fisik akhir baru terbaca setelah itu
        MoveTo(work.Left, work.Top);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!GetWindowRect(_hwnd, out var rc)) return;
            var margin = (int)Math.Round(BottomMarginDip * GetMonitorDpi(monitor) / 96.0);
            MoveTo(work.Left + (work.Width - rc.Width) / 2, work.Bottom - rc.Height - margin);
            _positioned = true;
        });
    }

    // Titik tengah jendela (± margin) harus berada di sebuah monitor, bukan sekadar di kotak
    // pembungkus semua monitor — celah antar-monitor yang tidak sejajar tidak terlihat
    private bool IsVisibleAt(int x, int y)
    {
        if (!GetWindowRect(_hwnd, out var rc)) return false;
        var centerX = x + rc.Width / 2;
        var centerY = y + rc.Height / 2;
        return IsOnAnyMonitor(new RECT
        {
            Left = centerX - VisibleMarginPx,
            Top = centerY - VisibleMarginPx,
            Right = centerX + VisibleMarginPx,
            Bottom = centerY + VisibleMarginPx,
        });
    }

    private void MoveTo(int x, int y) =>
        SetWindowPos(_hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DragMove melempar InvalidOperationException bila tombol kiri tidak tertekan (mis. sentuh/pena)
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (GetWindowRect(_hwnd, out var rc)) Moved?.Invoke(rc.Left, rc.Top);
    }
}
