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

    public static AppConfig Load()
    {
        try
        {
            var path = App.ConfigPath;
            if (!File.Exists(path)) return new AppConfig();
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
            return loaded ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            App.EnsureConfigDirectory();
            File.WriteAllText(App.ConfigPath, JsonSerializer.Serialize(config, Options));
        }
        catch
        {
            // 配置写失败不该影响截图本身。
        }
    }
}
