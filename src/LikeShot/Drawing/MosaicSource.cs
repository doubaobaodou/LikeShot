using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Capture;

namespace LikeShot.Drawing;

/// <summary>
/// 按选区生成打码底图（像素块 / 模糊），带缓存。
/// 只在选区变化时重建，避免每帧对整屏做模糊。
/// </summary>
public sealed class MosaicSource
{
    private readonly ScreenSnapshot _snapshot;
    private readonly Int32Rect _region;
    private readonly Dictionary<(MosaicStyle, int), BitmapSource> _cache = new();

    public MosaicSource(ScreenSnapshot snapshot, Int32Rect region)
    {
        _snapshot = snapshot;
        _region = snapshot.Intersect(region);
        if (_region.Width < 1) _region = new Int32Rect(_region.X, _region.Y, 1, 1);
        if (_region.Height < 1) _region = new Int32Rect(_region.X, _region.Y, 1, Math.Max(1, _region.Height));
    }

    public Int32Rect Region => _region;

    public BitmapSource Get(MosaicStyle style, int strength)
    {
        strength = Math.Clamp(strength, 1, 40);
        var key = (style, strength);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        int w = _region.Width, h = _region.Height;
        byte[] src = GrabRegion();
        byte[] dst = style == MosaicStyle.Pixelate ? Pixelate(src, w, h, strength) : BoxBlur(src, w, h, strength);

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), dst, w * 4, 0);
        bmp.Freeze();
        _cache[key] = bmp;
        return bmp;
    }

    /// <summary>
    /// 打码底图必须来自按下热键那一刻的冻结图。
    /// 如果去抓实时屏幕，抓到的会是盖在上面的截图层自己。
    /// </summary>
    private byte[] GrabRegion() => _snapshot.CopyRegion(_region);

    private static unsafe byte[] Pixelate(byte[] src, int w, int h, int block)
    {
        var dst = new byte[src.Length];
        fixed (byte* s = src) fixed (byte* d = dst)
        {
            for (int by = 0; by < h; by += block)
            {
                int yEnd = Math.Min(by + block, h);
                for (int bx = 0; bx < w; bx += block)
                {
                    int xEnd = Math.Min(bx + block, w);
                    long r = 0, g = 0, b2 = 0, a = 0, count = 0;
                    for (int y = by; y < yEnd; y++)
                    {
                        byte* row = s + (long)y * w * 4;
                        for (int x = bx; x < xEnd; x++)
                        {
                            byte* px = row + x * 4;
                            b2 += px[0]; g += px[1]; r += px[2]; a += px[3];
                            count++;
                        }
                    }
                    byte rr = (byte)(r / count), gg = (byte)(g / count), bb = (byte)(b2 / count);
                    for (int y = by; y < yEnd; y++)
                    {
                        byte* row = d + (long)y * w * 4;
                        for (int x = bx; x < xEnd; x++)
                        {
                            byte* px = row + x * 4;
                            px[0] = bb; px[1] = gg; px[2] = rr; px[3] = 255;
                        }
                    }
                }
            }
        }
        return dst;
    }

    private static unsafe byte[] BoxBlur(byte[] src, int w, int h, int radius)
    {
        radius = Math.Max(1, radius);
        var temp = new byte[src.Length];
        var dst = new byte[src.Length];
        fixed (byte* s = src) fixed (byte* t = temp) fixed (byte* d = dst)
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = s + (long)y * w * 4;
                byte* trow = t + (long)y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int from = Math.Max(0, x - radius), to = Math.Min(w - 1, x + radius);
                    int n = to - from + 1;
                    int b = 0, g = 0, r = 0;
                    for (int k = from; k <= to; k++)
                    {
                        b += row[k * 4]; g += row[k * 4 + 1]; r += row[k * 4 + 2];
                    }
                    trow[x * 4] = (byte)(b / n);
                    trow[x * 4 + 1] = (byte)(g / n);
                    trow[x * 4 + 2] = (byte)(r / n);
                    trow[x * 4 + 3] = 255;
                }
            }

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int from = Math.Max(0, y - radius), to = Math.Min(h - 1, y + radius);
                    int n = to - from + 1;
                    int b = 0, g = 0, r = 0;
                    for (int k = from; k <= to; k++)
                    {
                        byte* row = t + (long)k * w * 4;
                        b += row[x * 4]; g += row[x * 4 + 1]; r += row[x * 4 + 2];
                    }
                    byte* drow = d + (long)y * w * 4;
                    drow[x * 4] = (byte)(b / n);
                    drow[x * 4 + 1] = (byte)(g / n);
                    drow[x * 4 + 2] = (byte)(r / n);
                    drow[x * 4 + 3] = 255;
                }
            }
        }
        return dst;
    }
}
