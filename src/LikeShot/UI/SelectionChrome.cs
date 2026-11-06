using System.Windows;
using System.Windows.Media;
using LikeShot.Capture;

namespace LikeShot.UI;

/// <summary>
/// 选中框：虚线包围盒 + 八个拖角手柄。截图层和钉图窗共用这一份画法，
/// 两边坐标都已经是虚拟桌面物理像素，只有每 DIP 对应多少物理像素不一样。
/// </summary>
internal static class SelectionChrome
{
    public static void Draw(DrawingContext dc, CaptureSession session, double pxPerDip)
    {
        if (session.Selected is not { } item) return;
        var bounds = item.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        double line = Math.Max(1, 1 * pxPerDip);
        var dash = new Pen(Theme.Brush(Theme.Accent), line)
        {
            DashStyle = new DashStyle([4, 3], 0)
        };
        dash.Freeze();
        dc.DrawRectangle(null, dash, bounds);

        double hs = Math.Max(4, 6 * pxPerDip);
        var edge = new Pen(Theme.Brush(Theme.Accent), line);
        edge.Freeze();
        foreach (var p in CaptureSession.HandlePoints(bounds))
            dc.DrawRectangle(Theme.Brush(Theme.HandleFill), edge,
                new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs));
    }
}
