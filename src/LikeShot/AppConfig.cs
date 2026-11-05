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

    public bool ShowMagnifier { get; set; } = true;

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
