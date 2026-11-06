using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LikeShot.Capture;

/// <summary>
/// 出图的三条路：剪贴板、文件、钉图。每条都必须给个结果——
/// 返回值 null 就是成功，非 null 就是「这句要让用户看到」的原因。
/// </summary>
public static class Exporter
{
    public static string? TrySetText(string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return null;
            }
            catch (Exception ex)
            {
                if (i == 2) return Reason(ex);
                System.Threading.Thread.Sleep(40);
            }
        }
        return "剪贴板没打开";
    }

    /// <summary>复制图片。返回 null 表示剪贴板里已经有这张图了。</summary>
    public static string? TryCopyImage(BitmapSource image)
    {
        // 剪贴板常被别的程序锁住；截图层刚关闭时尤其容易撞上，重试几次比丢图好。
        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.Clear();
                Clipboard.SetImage(image);
                FlushClipboard();
                return null;
            }
            catch (Exception ex)
            {
                last = ex;
                System.Threading.Thread.Sleep(60);
            }
        }
        Log.Error("复制到剪贴板失败", last!);
        return Reason(last!);
    }

    /// <summary>
    /// SetImage 是延迟渲染：数据留在我们进程里当剪贴板所有者，谁贴一次我们画一次，
    /// 最后那张成品图就一直被扣在内存里。Flush 把数据转成交给系统保管的那份，
    /// 我们这边马上就能释放——顺带剪贴板里的图也不会随我们退出而丢。
    /// 代价是当场多转一次位图，几十毫秒，复制这一下本来就重。
    /// </summary>
    private static void FlushClipboard()
    {
        try { Clipboard.Flush(); }
        catch (Exception ex) { Log.Error("剪贴板 Flush 失败（不影响复制结果）", ex); }
    }

    public static string SuggestedFileName(string format = "png") =>
        $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";

    /// <summary>
    /// 弹保存框并写盘。返回路径＝存好了；返回 null 且 error 也为 null＝用户自己取消的；
    /// error 非 null＝对话框点了保存但文件没落地，这句原因得还给界面。
    /// </summary>
    public static string? SaveDialog(BitmapSource image, AppConfig config, out string? error)
    {
        error = null;
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp",
            FileName = SuggestedFileName(config.SaveFormat),
            AddExtension = true,
            DefaultExt = config.SaveFormat
        };

        try
        {
            Directory.CreateDirectory(config.SaveDirectory);
            dialog.InitialDirectory = config.SaveDirectory;
        }
        catch
        {
            // 目录不可写就用系统默认位置，不拦这次保存。
        }

        if (dialog.ShowDialog() != true) return null;
        error = TryWrite(image, dialog.FileName);
        return error == null ? dialog.FileName : null;
    }

    /// <summary>写盘。返回 null 表示成功，否则是要给用户看的那句原因。</summary>
    public static string? TryWrite(BitmapSource image, string path)
    {
        try
        {
            BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".bmp" => new BmpBitmapEncoder(),
                ".gif" => new GifBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };
            encoder.Frames.Add(BitmapFrame.Create(image));

            var dir = Path.GetDirectoryName(path);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            using var stream = File.Create(path);
            encoder.Save(stream);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"写盘失败 {path}", ex);
            return Reason(ex);
        }
    }

    /// <summary>Win32 的错误消息又长又是系统语言，界面上只留能看懂的那一截。</summary>
    private static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有写入权限，换一个目录",
        IOException io when io.Message.Contains("disk") || io.Message.Contains("空间") => "磁盘空间不够",
        IOException => "文件正被其它程序占用，或路径不合法",
        System.Runtime.InteropServices.ExternalException => "剪贴板被别的程序占着，稍一下再复制",
        _ => ex.Message
    };
}
