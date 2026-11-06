using System.IO;

namespace LikeShot;

/// <summary>
/// 崩溃与关键决策都要落盘：WinExe 没有控制台，界面又不弹错误框，
/// 没有这个文件就只能靠猜。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Directory_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LikeShot");
    private static readonly string Path = System.IO.Path.Combine(Directory_, "log.txt");
    private static readonly string OlderPath = System.IO.Path.Combine(Directory_, "log-1.txt");

    /// <summary>日志只留最近两段：出问题时能翻到当下，不会几年后变成几百 MB 没人敢动的文件。</summary>
    private const long MaxBytes = 1024 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Directory_);
                RotateIfNeeded();
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志本身失败不能再抛，否则会把截图流程带崩。
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(Path)) return;
        try
        {
            if (new FileInfo(Path).Length < MaxBytes) return;
            if (File.Exists(OlderPath)) File.Delete(OlderPath);
            File.Move(Path, OlderPath);
        }
        catch
        {
            // 轮转失败也不能挡住这次写入。
        }
    }

    public static void Error(string where, Exception error) =>
        Write($"[错误] {where}: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");

    public static string PathHint => Path;
}
