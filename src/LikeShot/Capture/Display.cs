using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LikeShot.Native;

namespace LikeShot.Capture;

/// <summary>一台显示器的物理像素信息（坐标基于整个虚拟桌面）。</summary>
public sealed record Display(int Index, Int32Rect Bounds, double Scale, bool IsPrimary, string DeviceName)
{
    public int PhysicalWidth => Bounds.Width;
    public int PhysicalHeight => Bounds.Height;

    /// <summary>物理像素 → 这台显示器上的 WPF DIP。</summary>
    public Point ToDip(int physicalX, int physicalY) =>
        new((physicalX - Bounds.X) / Scale, (physicalY - Bounds.Y) / Scale);

    public Rect DIPWindowRect =>
        new(Bounds.X / Scale, Bounds.Y / Scale, Bounds.Width / Scale, Bounds.Height / Scale);

    public bool Contains(int physicalX, int physicalY) =>
        physicalX >= Bounds.X && physicalX < Bounds.Right() && physicalY >= Bounds.Y && physicalY < Bounds.Bottom();

    public static IReadOnlyList<Display> All()
    {
        var list = new List<Display>();
        var proc = new Win32.MonitorEnumProc((hMonitor, _, _, _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                try { Win32.GetDpiForMonitor(hMonitor, Win32.EFFECTIVE_DPI, out dpiX, out dpiY); }
                catch { /* 老系统没有 shcore，退回 96 */ }
                double scale = Math.Max(dpiX, 1) / 96.0;
                list.Add(new Display(
                    list.Count,
                    new Int32Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height),
                    scale,
                    (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                    info.szDevice));
            }
            return true;
        });

        Win32.EnumDisplayMonitors(nint.Zero, nint.Zero, proc, nint.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    public static Display NearestTo(int physicalX, int physicalY)
    {
        var all = All();
        foreach (var d in all)
            if (d.Contains(physicalX, physicalY)) return d;
        return all.FirstOrDefault(d => d.IsPrimary) ?? all[0];
    }

    /// <summary>窗口句柄所在显示器的缩放比，用于已存在窗口的坐标换算。</summary>
    public static double ScaleOfWindow(nint hWnd)
    {
        uint dpi = Win32.GetDpiForWindow(hWnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static HwndSource? SourceOf(Window window) => (HwndSource?)PresentationSource.FromVisual(window);
}
