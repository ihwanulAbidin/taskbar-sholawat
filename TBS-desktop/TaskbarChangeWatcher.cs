using System.Windows.Interop;
using System.Windows.Threading;
using static TaskbarSholawat.NativeMethods;

namespace TaskbarSholawat;

/// <summary>
/// Jendela tersembunyi penerima sinyal shell:
/// <list type="bullet">
/// <item><see cref="Changed"/> — tombol aplikasi di taskbar bertambah/berkurang (shell hook). Alasan:
/// Deskband11Lib mengukur ikon taskbar dengan UI Automation (FindAll seluruh pohon) tiap
/// LayoutRefreshInterval; polling 500 ms memakan ±14% CPU aplikasi + ±11% CPU explorer
/// (satu core, diukur M1). Dengan sinyal ini polling cukup jarang.</item>
/// <item><see cref="WorkAreaChanged"/> — taskbar pindah sisi / AppBar lain berubah (WM_SETTINGCHANGE SPI_SETWORKAREA).</item>
/// <item><see cref="TaskbarCreated"/> — explorer restart; pendaftaran AppBar hilang.</item>
/// <item><see cref="CloseRequested"/> — WM_CLOSE dari luar (mis. taskkill tanpa /F).</item>
/// </list>
/// </summary>
public sealed class TaskbarChangeWatcher : IDisposable
{
    private const int HSHELL_WINDOWCREATED = 1;
    private const int HSHELL_WINDOWDESTROYED = 2;
    private const int HSHELL_WINDOWREPLACED = 13;

    private readonly HwndSource _source;
    private readonly int _shellHookMessage;
    private readonly int _taskbarCreatedMessage;
    // Animasi geser ikon / perpindahan taskbar memicu event beruntun; kumpulkan jadi satu
    private readonly DispatcherTimer _buttonsSettle = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _workAreaSettle = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public event Action? Changed;
    public event Action? WorkAreaChanged;
    public event Action? TaskbarCreated;
    public event Action? CloseRequested;

    public TaskbarChangeWatcher()
    {
        // Top-level tanpa WS_VISIBLE: tetap menerima pesan broadcast (WM_SETTINGCHANGE, TaskbarCreated)
        _source = new HwndSource(new HwndSourceParameters("TaskbarSholawat.ShellHook")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _source.AddHook(WndProc);
        _shellHookMessage = RegisterWindowMessage("SHELLHOOK");
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        RegisterShellHookWindow(_source.Handle);

        _buttonsSettle.Tick += (_, _) =>
        {
            _buttonsSettle.Stop();
            Changed?.Invoke();
        };
        _workAreaSettle.Tick += (_, _) =>
        {
            _workAreaSettle.Stop();
            WorkAreaChanged?.Invoke();
        };
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _shellHookMessage && (wParam & 0x7FFF) is HSHELL_WINDOWCREATED or HSHELL_WINDOWDESTROYED or HSHELL_WINDOWREPLACED)
            Restart(_buttonsSettle);
        else if (msg == WM_SETTINGCHANGE && wParam == SPI_SETWORKAREA)
            Restart(_workAreaSettle);
        else if (msg == _taskbarCreatedMessage)
            TaskbarCreated?.Invoke();
        else if (msg == WM_CLOSE)
        {
            // Bawaan DefWindowProc menghancurkan jendela ini diam-diam dan proses tetap hidup
            handled = true;
            CloseRequested?.Invoke();
        }
        return 0;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    public void Dispose()
    {
        _buttonsSettle.Stop();
        _workAreaSettle.Stop();
        DeregisterShellHookWindow(_source.Handle);
        _source.Dispose();
    }
}
