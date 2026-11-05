using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LikeShot.Drawing;

/// <summary>
/// WPF 的 DrawingContext 只能从 DrawingVisual 开，不能直接从 RenderTargetBitmap 开，
/// 所以「位图 + 标注 → 成品图」统一走这里。
/// </summary>
public static class RenderSurface
{
    public static BitmapSource ToBitmap(int width, int height, Action<DrawingContext> draw)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
