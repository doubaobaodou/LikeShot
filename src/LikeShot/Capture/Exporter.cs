using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LikeShot.Capture;

public static class Exporter
{
    public static bool TrySetText(string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(40);
            }
        }
        return false;
    }

    public static bool CopyImage(BitmapSource image)
    {
        // 剪贴板常被别的程序锁住；截图层刚关闭时尤其容易撞上，重试几次比丢图好。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.Clear();
                Clipboard.SetImage(image);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(60);
            }
        }
        return false;
    }

    public static string SuggestedFileName(string format = "png") =>
        $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";

    public static string? SaveDialog(BitmapSource image, AppConfig config)
    {
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
        Write(image, dialog.FileName);
        return dialog.FileName;
    }

    public static void Write(BitmapSource image, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
