using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Native;

namespace LikeShot.Capture;

/// <summary>
/// 按下热键那一刻的整屏冻结图。后续所有绘制、马赛克、导出都基于这份快照，
/// 所以覆盖层不需要真透明，也就没有 WPF 透明窗的性能和 DPI 坑。
/// </summary>
public sealed class ScreenSnapshot : IDisposable
{
    private byte[] _backing;

    private ScreenSnapshot(WriteableBitmap image, byte[] backing, Int32Rect virtualBounds)
    {
        Image = image;
        _backing = backing;
        VirtualBounds = virtualBounds;
    }

    public WriteableBitmap Image { get; }
    public byte[] Backing => _backing;
    public Int32Rect VirtualBounds { get; }
    public int Stride => VirtualBounds.Width * 4;

    public static ScreenSnapshot GrabVirtualDesktop()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            Win32.BitBltFromScreen(hdc, x, y, w, h);
            g.ReleaseHdc(hdc);
        }

        var backing = new byte[w * h * 4];
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, backing, 0, backing.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, w, h), backing, w * 4, 0);
        bmp.Dispose();

        return new ScreenSnapshot(image, backing, new Int32Rect(x, y, w, h));
    }

    /// <summary>
    /// 把任意一张位图包成一个「快照」。钉图窗复用整套标注/打码/导出机器，
    /// 靠的就是这个入口：它的虚拟边界就是图原本在屏幕上的位置。
    /// </summary>
    public static ScreenSnapshot FromBitmap(BitmapSource source, Int32Rect virtualBounds)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        int stride = w * 4;
        byte[] backing = new byte[stride * h];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(backing, stride, 0);

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, w, h), backing, stride, 0);
        image.Freeze();
        return new ScreenSnapshot(image, backing, virtualBounds);
    }

    /// <summary>取一个物理像素（坐标是虚拟桌面物理坐标）。越界返回透明。</summary>
    public Color PixelAt(int physicalX, int physicalY)
    {
        int localX = physicalX - VirtualBounds.X;
        int localY = physicalY - VirtualBounds.Y;
        if (localX < 0 || localY < 0 || localX >= VirtualBounds.Width || localY >= VirtualBounds.Height)
            return Colors.Transparent;

        int i = localY * Stride + localX * 4;
        return Color.FromRgb(Backing[i + 2], Backing[i + 1], Backing[i]);
    }

    public Int32Rect Intersect(Int32Rect rect)
    {
        int x0 = Math.Max(rect.X, VirtualBounds.X);
        int y0 = Math.Max(rect.Y, VirtualBounds.Y);
        int x1 = Math.Min(rect.Right(), VirtualBounds.Right());
        int y1 = Math.Min(rect.Bottom(), VirtualBounds.Bottom());
        // 完全不相交就给 0×0，让调用方判「这块区域不在屏幕上」。
        // 以前高度兜到 1，一个跑到屏幕外的选区会被当成「还剩一行像素」。
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>
    /// 把一张任意图当作「桌面的一块」包成快照：画布是整台显示器的物理区域，
    /// 图按 1:1 贴在指定位置，其余地方涂桌面底色。再标注（历史图丢回截图层）靠这个入口，
    /// 之后所有绘制、马赛克、导出都能沿用同一套坐标口径。
    /// </summary>
    public static ScreenSnapshot FromImageOnDesk(BitmapSource source, Int32Rect canvas, Int32Rect placeAt)
    {
        int w = Math.Max(1, canvas.Width), h = Math.Max(1, canvas.Height);
        int stride = w * 4;
        var backing = new byte[stride * h];

        // 底色取截图层暗区那一族：外面那圈本来还会被暗罩再压一层，看着就是「图摆在桌面上」。
        for (int i = 0; i < backing.Length; i += 4)
        {
            backing[i] = 0x2B; backing[i + 1] = 0x26; backing[i + 2] = 0x23; backing[i + 3] = 0xFF;
        }

        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int copyW = Math.Min(placeAt.Width, bgra.PixelWidth);
        int copyH = Math.Min(placeAt.Height, bgra.PixelHeight);
        int originX = placeAt.X - canvas.X, originY = placeAt.Y - canvas.Y;
        var row = new byte[copyW * 4];
        for (int y = 0; y < copyH; y++)
        {
            bgra.CopyPixels(new Int32Rect(0, y, copyW, 1), row, copyW * 4, 0);
            Buffer.BlockCopy(row, 0, backing, (originY + y) * stride + originX * 4, copyW * 4);
        }

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, w, h), backing, stride, 0);
        image.Freeze();
        return new ScreenSnapshot(image, backing, canvas);
    }

    /// <summary>
    /// 重新抓取当前屏幕的一块区域（长截图需要边滚边取实时画面，不能用冻结图）。
    /// </summary>
    public static WriteableBitmap GrabLiveRegion(Int32Rect physicalRect)
    {
        int w = Math.Max(1, physicalRect.Width);
        int h = Math.Max(1, physicalRect.Height);
        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        Win32.BitBltFromScreen(hdc, physicalRect.X, physicalRect.Y, w, h);
        g.ReleaseHdc(hdc);

        var backing = new byte[w * h * 4];
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, backing, 0, backing.Length); }
        finally { bmp.UnlockBits(data); }
        bmp.Dispose();

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, w, h), backing, w * 4, 0);
        return image;
    }

    public void Dispose()
    {
        // 3200×2000 的冻结图是 25MB，退出截图层时要把这份大数组交还给 GC。
        _backing = [];
    }
}
