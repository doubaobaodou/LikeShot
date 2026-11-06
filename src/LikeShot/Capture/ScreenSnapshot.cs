using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Native;

namespace LikeShot.Capture;

/// <summary>
/// 按下热键那一刻的整屏冻结图。后续所有绘制、马赛克、导出都基于这份快照，
/// 所以覆盖层不需要真透明，也就没有 WPF 透明窗的性能和 DPI 坑。
/// 像素只留一份（位图自己那份）：3200×2000 一份就是 25MB，再配一个字节数组等于每次截图
/// 都多占一块大对象堆。读像素改成按需拷，放大镜改成一次拷一整块。
/// </summary>
public sealed class ScreenSnapshot
{
    private ScreenSnapshot(WriteableBitmap image, Int32Rect virtualBounds)
    {
        Image = image;
        VirtualBounds = virtualBounds;
    }

    public WriteableBitmap Image { get; }
    public Int32Rect VirtualBounds { get; }

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

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 从 GDI 那块缓冲直接灌进位图，中间不过 byte[]。
            image.WritePixels(new Int32Rect(0, 0, w, h), data.Scan0, w * h * 4, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
            bmp.Dispose();
        }

        return new ScreenSnapshot(image, new Int32Rect(x, y, w, h));
    }

    /// <summary>
    /// 把任意一张位图包成一个「快照」。钉图窗复用整套标注/打码/导出机器，
    /// 靠的就是这个入口：它的虚拟边界就是图原本在屏幕上的位置。
    /// </summary>
    public static ScreenSnapshot FromBitmap(BitmapSource source, Int32Rect virtualBounds)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, stride, 0);

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
        image.Freeze();
        return new ScreenSnapshot(image, virtualBounds);
    }

    /// <summary>取一个物理像素（坐标是虚拟桌面物理坐标）。越界返回透明。</summary>
    public Color PixelAt(int physicalX, int physicalY)
    {
        int localX = physicalX - VirtualBounds.X;
        int localY = physicalY - VirtualBounds.Y;
        if (localX < 0 || localY < 0 || localX >= VirtualBounds.Width || localY >= VirtualBounds.Height)
            return Colors.Transparent;

        var px = new byte[4];
        Image.CopyPixels(new Int32Rect(localX, localY, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    /// <summary>拷一块物理区域出来（打码底图用）。区域要在快照范围内。</summary>
    public byte[] CopyRegion(Int32Rect physical)
    {
        int w = Math.Max(1, physical.Width), h = Math.Max(1, physical.Height);
        var buffer = new byte[w * 4 * h];
        Image.CopyPixels(new Int32Rect(physical.X - VirtualBounds.X, physical.Y - VirtualBounds.Y, w, h),
            buffer, w * 4, 0);
        return buffer;
    }

    /// <summary>
    /// 放大镜那种小块采样：整块一次拷出来，不是一格跨边界读一次。
    /// 探出快照的那几格留成全透明，由画的人自己决定涂什么。
    /// </summary>
    public byte[] SampleBlock(int originX, int originY, int w, int h)
    {
        var buffer = new byte[w * h * 4];
        var inside = Intersect(new Int32Rect(originX, originY, w, h));
        if (inside.Width <= 0 || inside.Height <= 0) return buffer;

        int pieceStride = inside.Width * 4;
        var piece = new byte[pieceStride * inside.Height];
        Image.CopyPixels(new Int32Rect(inside.X - VirtualBounds.X, inside.Y - VirtualBounds.Y,
            inside.Width, inside.Height), piece, pieceStride, 0);

        int rowOffset = (inside.Y - originY) * w * 4 + (inside.X - originX) * 4;
        for (int y = 0; y < inside.Height; y++)
            Buffer.BlockCopy(piece, y * pieceStride, buffer, rowOffset + y * w * 4, pieceStride);
        return buffer;
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
        return new ScreenSnapshot(image, canvas);
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

        var image = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            image.WritePixels(new Int32Rect(0, 0, w, h), data.Scan0, w * h * 4, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
            bmp.Dispose();
        }
        return image;
    }
}
