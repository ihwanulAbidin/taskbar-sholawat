using static TaskbarSholawat.NativeMethods;

namespace TaskbarSholawat;

/// <summary>Nilai uEdge APPBARDATA (ABE_*).</summary>
public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <param name="Bounds">Kotak taskbar dalam piksel fisik.</param>
internal readonly record struct TaskbarInfo(TaskbarEdge Edge, RECT Bounds, nint Monitor, RECT MonitorBounds)
{
    /// <summary>Taskbar kiri/kanan (Win11 26200.9278+): terlalu sempit untuk baris Arab horizontal.</summary>
    public bool IsVertical => Edge is TaskbarEdge.Left or TaskbarEdge.Right;
}

/// <summary>Posisi taskbar utama (PLAN bagian 3.2 TaskbarLocator).</summary>
internal static class TaskbarLocator
{
    /// <returns>null bila taskbar belum ada (explorer sedang restart).</returns>
    public static TaskbarInfo? TryGet()
    {
        var data = NewAppBarData();
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0 || !Enum.IsDefined((TaskbarEdge)data.uEdge))
            return null;

        var monitor = MonitorFromRect(data.rc);
        if (!TryGetMonitorRects(monitor, out var monitorBounds, out _))
            return null;

        return new TaskbarInfo((TaskbarEdge)data.uEdge, data.rc, monitor, monitorBounds);
    }
}
