using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LikeShot.Native;

namespace LikeShot.Capture;

/// <summary>
/// 光标下方那个「真正属于某个窗口」的矩形，用来做整窗吸附。
/// 优先用 DWM 的扩展边框（去掉 Win11 的透明阴影边），拿不到才退回 GetWindowRect。
/// </summary>
public static class WindowSnap
{
    private static readonly string[] IgnoredClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ApplicationWindowShadow"
    ];

    public static Int32Rect? AtPoint(int physicalX, int physicalY)
    {
        int selfPid = Environment.ProcessId;
        Int32Rect? found = null;

        var proc = new Win32.EnumWindowsProc((hWnd, _) =>
        {
            if (found.HasValue) return false;
            if (!Win32.IsWindowVisible(hWnd)) return true;
            if (Win32.GetAncestor(hWnd, Win32.GA_ROOTOWNER) != hWnd) return true;

            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            if ((int)pid == selfPid) return true;

            if (IsCloaked(hWnd)) return true;

            var sb = new StringBuilder(64);
            Win32.GetClassName(hWnd, sb, sb.Capacity);
            if (Array.IndexOf(IgnoredClasses, sb.ToString()) >= 0) return true;

            if (!TryFrame(hWnd, out var rect)) return true;
            if (rect.Width <= 8 || rect.Height <= 8) return true;

            // 覆盖整个虚拟桌面的多半是别的全屏遮罩，不吸附。
            if (rect.Width >= Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN) &&
                rect.Height >= Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN))
                return true;

            if (rect.Left <= physicalX && physicalX < rect.Right &&
                rect.Top <= physicalY && physicalY < rect.Bottom)
                found = new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height);

            return true;
        });

        Win32.EnumWindows(proc, nint.Zero);
        GC.KeepAlive(proc);
        return found;
    }

    private static bool IsCloaked(nint hWnd)
    {
        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0) return false;
        return cloaked != 0;
    }

    private static bool TryFrame(nint hWnd, out Win32.RECT rect)
    {
        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<Win32.RECT>()) == 0
            && rect.Width > 0)
            return true;
        return Win32.GetWindowRect(hWnd, out rect);
    }
}
