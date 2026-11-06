using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LikeShot;

public sealed class AppConfig
{
    public string CaptureChord { get; set; } = "Ctrl+Alt+A";

    /// <summary>进程名（不含 .exe）。命中任意一个就说明热键该让给它们。</summary>
    public List<string> YieldToProcesses { get; set; } = ["QQ", "Tim"];

    public string SaveDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "屏幕截图");

    public string SaveFormat { get; set; } = "png";

    public bool RunOnStartup { get; set; }

    public int LongCapturePollMilliseconds { get; set; } = 220;

    /// <summary>长截图自动滚动时每帧推几格滚轮。和抓取间隔是两个独立的旋钮，合成一档就没法单独调。</summary>
    public int LongCaptureScrollNotches { get; set; } = 1;

    public bool ShowMagnifier { get; set; } = true;

    /// <summary>点「保存」之后是否顺带把图塞进剪贴板。QQ 默认是会带的。</summary>
    public bool CopyAfterSave { get; set; } = true;

    /// <summary>截图那层 UI（工具栏、放大镜）的配色：dark 或 light。浅色就是 QQ 那种白工具栏。</summary>
    public string CaptureUiTheme { get; set; } = "dark";

    [JsonIgnore]
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>每次成功写入都同步一份备份：主配置写坏时还能从这儿回来。</summary>
    private static string BackupPath => App.ConfigPath + ".bak";

    public static AppConfig Load()
    {
        var main = TryRead(App.ConfigPath);
        if (main != null) return main;

        // 文件在、读不出来才算「坏」；不存在是第一次运行，直接给默认值。
        if (!System.IO.File.Exists(App.ConfigPath)) return new AppConfig();

        var aside = $"{App.ConfigPath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            System.IO.File.Move(App.ConfigPath, aside, true);
            Log.Write($"配置读不出来，原文件留证到 {aside}");
        }
        catch (Exception ex)
        {
            Log.Error("配置留证失败", ex);
        }

        var fromBackup = TryRead(BackupPath);
        if (fromBackup != null)
        {
            Log.Write("已用备份恢复设置");
            return fromBackup;
        }

        Log.Write("备份也不可用，回到默认设置");
        return new AppConfig();
    }

    private static AppConfig? TryRead(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var text = System.IO.File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<AppConfig>(text, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>返回 null 表示写进去了；否则是给用户看的那句原因。</summary>
    public static string? Save(AppConfig config)
    {
        try
        {
            App.EnsureConfigDirectory();
            var json = JsonSerializer.Serialize(config, Options);
            System.IO.File.WriteAllText(App.ConfigPath, json);
            System.IO.File.Copy(App.ConfigPath, BackupPath, true);
            return null;
        }
        catch (Exception ex)
        {
            // 配置写失败不该影响截图本身，但得让人知道设置没存下来。
            Log.Error("配置写入失败", ex);
            return ex.Message;
        }
    }
}
