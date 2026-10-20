using System.Runtime.InteropServices;

namespace TaskbarSholawat;

/// <summary>P/Invoke Win32 yang dipakai bersama (taskbar, AppBar, monitor, DPI).</summary>
internal static partial class NativeMethods
{
    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const uint ABM_QUERYPOS = 0x2;
    public const uint ABM_SETPOS = 0x3;
    public const uint ABM_GETTASKBARPOS = 0x5;
    public const uint ABM_ACTIVATE = 0x6;
    public const uint ABM_WINDOWPOSCHANGED = 0x9;

    public const int ABN_POSCHANGED = 0x1;
    public const int ABN_FULLSCREENAPP = 0x2;

    public const int WM_ACTIVATE = 0x0006;
    public const int WM_CLOSE = 0x0010;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int WM_DPICHANGED = 0x02E0;
    public const int SPI_SETWORKAREA = 0x002F;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;

    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"{Left},{Top},{Right},{Bottom}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public static APPBARDATA NewAppBarData(nint hwnd = 0) =>
        new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    public static nint MonitorFromRect(RECT rect) => MonitorFromRect(in rect, MONITOR_DEFAULTTONEAREST);

    /// <summary>Kotak (piksel fisik) bersinggungan dengan monitor mana pun.</summary>
    public static bool IsOnAnyMonitor(RECT rect) => MonitorFromRect(in rect, MONITOR_DEFAULTTONULL) != 0;

    public static nint PrimaryMonitor() => MonitorFromPoint(0, MONITOR_DEFAULTTOPRIMARY);

    public static bool TryGetMonitorRects(nint monitor, out RECT monitorRect, out RECT workArea)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var ok = GetMonitorInfo(monitor, ref info);
        monitorRect = info.rcMonitor;
        workArea = info.rcWork;
        return ok;
    }

    /// <returns>DPI efektif monitor, 96 bila gagal.</returns>
    public static uint GetMonitorDpi(nint monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : 96;

    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegisterWindowMessage(string message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterShellHookWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeregisterShellHookWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(in RECT rect, uint flags);

    // POINT {x, y} dikirim by value sebagai satu nilai 64-bit (x di 32 bit bawah)
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(long point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
