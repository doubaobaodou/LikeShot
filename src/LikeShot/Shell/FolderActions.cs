using System.Diagnostics;
using System.IO;

namespace LikeShot.Shell;

/// <summary>
/// 和资源管理器打交道的两个动作。都返回 null=成功、否则是要显示在界面上的原因，
/// 因为「点了没反应」最难查——打开目录这种事也一样。
/// </summary>
public static class FolderActions
{
    public static string? OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"打开目录失败 {dir}", ex);
            return ex.Message;
        }
    }

    /// <summary>打开文件所在目录并把那个文件选中。</summary>
    public static string? Reveal(string filePath)
    {
        var dir = Directory.Exists(filePath) ? filePath : Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "文件已经不在原来的位置了";

        // 路径里带引号就没法安全拼进 explorer 的命令行，退一步只开目录。
        if (filePath.Contains('"')) return OpenFolder(dir);

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"定位文件失败 {filePath}", ex);
            return ex.Message;
        }
    }
}
