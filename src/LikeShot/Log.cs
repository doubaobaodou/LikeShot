using System.IO;

namespace LikeShot;

/// <summary>
/// 崩溃与关键决策都要落盘：WinExe 没有控制台，界面又不弹错误框，
/// 没有这个文件就只能靠猜。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LikeShot", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志本身失败不能再抛，否则会把截图流程带崩。
        }
    }

    public static void Error(string where, Exception error) =>
        Write($"[错误] {where}: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");

    public static string PathHint => Path;
}
