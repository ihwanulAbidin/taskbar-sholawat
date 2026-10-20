using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TaskbarSholawat;

/// <summary>
/// Memberi tahu saat tombol aplikasi di taskbar bertambah/berkurang (jendela muncul/hilang),
/// lewat shell hook — sinyal yang sama yang dipakai taskbar.
/// Alasan: Deskband11Lib mengukur ikon taskbar dengan UI Automation (FindAll seluruh pohon)
/// tiap LayoutRefreshInterval; polling 500 ms memakan ±14% CPU aplikasi + ±11% CPU explorer
/// (satu core, diukur M1). Dengan watcher ini polling cukup jarang.
/// </summary>
public sealed partial class TaskbarChangeWatcher : IDisposable
{
    private const int HSHELL_WINDOWCREATED = 1;
    private const int HSHELL_WINDOWDESTROYED = 2;
    private const int HSHELL_WINDOWREPLACED = 13;
    private const int WS_EX_TOOLWINDOW = 0x80;

    private readonly HwndSource _source;
    private readonly int _shellHookMessage;
    // Animasi geser ikon taskbar perlu waktu; kumpulkan event beruntun jadi satu
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public event Action? Changed;

    public TaskbarChangeWatcher()
    {
        // Jendela top-level tersembunyi (tanpa WS_VISIBLE) khusus penerima pesan shell hook
        _source = new HwndSource(new HwndSourceParameters("TaskbarSholawat.ShellHook")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _source.AddHook(WndProc);
        _shellHookMessage = RegisterWindowMessage("SHELLHOOK");
        RegisterShellHookWindow(_source.Handle);

        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Changed?.Invoke();
        };
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _shellHookMessage && (wParam & 0x7FFF) is HSHELL_WINDOWCREATED or HSHELL_WINDOWDESTROYED or HSHELL_WINDOWREPLACED)
        {
            _settle.Stop();
            _settle.Start();
        }
        return 0;
    }

    public void Dispose()
    {
        _settle.Stop();
        DeregisterShellHookWindow(_source.Handle);
        _source.Dispose();
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterShellHookWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeregisterShellHookWindow(nint hwnd);
}
