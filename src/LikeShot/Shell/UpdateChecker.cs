using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LikeShot.Shell;

/// <summary>
/// 更新检测：拿 GitHub 上最新 release 的 tag 和当前版本比，新了就提示。
/// 仓库只留最新一个 release，所以 releases/latest 就是「该不该更新」的唯一答案。
/// 启动查一次、之后每 6 小时查一次；自动检查失败只记日志不打扰人，手动检查失败要明说。
/// </summary>
public sealed class UpdateChecker
{
    public const string RepoUrl = "https://github.com/doubaobaodou/LikeShot";
    private const string LatestApiUrl = "https://api.github.com/repos/doubaobaodou/LikeShot/releases/latest";

    public enum State { Idle, Checking, UpToDate, UpdateAvailable, Failed }

    private static readonly HttpClient Http = CreateClient();

    private readonly AppConfig _config;
    private System.Threading.Timer? _timer;
    private int _checking;   // 0=空闲 1=在查：手动点几次也只发一份请求

    public UpdateChecker(AppConfig config)
    {
        _config = config;
    }

    /// <summary>当前版本，从 exe 的文件版本读——csproj 的 Version 是唯一来源，打包时会嵌进去。</summary>
    public static Version CurrentVersion => ParseVersion(
        Environment.ProcessPath is string exe
            ? FileVersionInfo.GetVersionInfo(exe).ProductVersion
            : null) ?? new Version(0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    public State Status { get; private set; } = State.Idle;
    public ReleaseInfo? Latest { get; private set; }
    public string? Error { get; private set; }
    public DateTime LastCheckedUtc { get; private set; }

    /// <summary>状态变了（UI 线程上触发）。托盘和关于页都挂这里。</summary>
    public event Action? Changed;

    /// <summary>发现比当前新的版本时触发一次（每个版本只触发一次），参数是新版信息。</summary>
    public event Action<ReleaseInfo>? UpdateFound;

    private Version? _notifiedFor;

    public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? SetupUrl, string? SetupName)
    {
        public string VersionText => Version.ToString(3);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = true, AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LikeShot-update-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>普通常驻启动才调用；自检/抓屏这些一次性进程不联网。</summary>
    public void Start()
    {
        if (!_config.CheckForUpdates) return;
        _ = CheckAsync(manual: false);
        _timer = new System.Threading.Timer(_ =>
        {
            if (_config.CheckForUpdates) _ = CheckAsync(manual: false);
        }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    public async Task CheckAsync(bool manual)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            SetStatus(State.Checking, null);
            var json = await Http.GetStringAsync(LatestApiUrl);
            var release = ParseLatest(json);
            if (release == null)
            {
                SetStatus(State.Failed, "GitHub 返回的内容认不出来");
                Log.Write("更新检查：release JSON 解析失败");
                return;
            }

            Latest = release;
            LastCheckedUtc = DateTime.UtcNow;
            bool newer = IsNewer(release.Version, CurrentVersion);
            SetStatus(newer ? State.UpdateAvailable : State.UpToDate, null);
            Log.Write($"更新检查：当前 {CurrentVersionText}，最新 {release.VersionText}（{release.Tag}），{(newer ? "有新版" : "已是最新")}");

            if (newer && _notifiedFor != release.Version)
            {
                _notifiedFor = release.Version;
                UpdateFound?.Invoke(release);
            }
        }
        catch (Exception ex)
        {
            // 自动检查赶上断网/代理没开是常事，记一笔就好；手动检查要把原因摆出来。
            SetStatus(State.Failed, manual ? DescribeFailure(ex) : "没连上 GitHub");
            Log.Write($"更新检查失败（{(manual ? "手动" : "自动")}）：{ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        TaskCanceledException => "连接 GitHub 超时（15 秒）",
        HttpRequestException h when h.StatusCode == System.Net.HttpStatusCode.Forbidden =>
            "GitHub 拒绝了请求（403，可能是接口限流，过会儿再试）",
        HttpRequestException h when h.StatusCode != null => $"GitHub 返回 {(int)h.StatusCode}",
        HttpRequestException => "连不上 GitHub（检查网络或代理）",
        JsonException => "GitHub 返回的内容认不出来",
        _ => ex.Message
    };

    private void SetStatus(State state, string? error)
    {
        Status = state;
        Error = error;
        var app = System.Windows.Application.Current;
        if (app != null) app.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        else Changed?.Invoke();
    }

    // ---- 解析与比较（纯函数，自检直接打这里） ----

    /// <summary>从 releases/latest 的 JSON 里取 tag、页面地址和 setup 资产下载地址。</summary>
    public static ReleaseInfo? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            string tag = tagEl.GetString() ?? "";
            var version = ParseVersion(tag);
            if (version == null) return null;

            string page = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
            if (page.Length == 0) page = $"{RepoUrl}/releases/tag/{tag}";

            string? setupUrl = null, setupName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    setupName = name;
                    setupUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    break;
                }
            }
            return new ReleaseInfo(version, tag, page, setupUrl, setupName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>「v0.1.26」「0.1.26+git_hash」都认；认不出给 null。</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        return Version.TryParse(text, out var v) ? v : null;
    }

    /// <summary>严格「比当前新」才提示：相等、更旧都不提示。缺段按 0 算（0.1 == 0.1.0）。</summary>
    public static bool IsNewer(Version candidate, Version current)
    {
        int c1 = candidate.Major, c2 = current.Major;
        if (c1 != c2) return c1 > c2;
        c1 = candidate.Minor; c2 = current.Minor;
        if (c1 != c2) return c1 > c2;
        c1 = Math.Max(candidate.Build, 0); c2 = Math.Max(current.Build, 0);
        if (c1 != c2) return c1 > c2;
        return Math.Max(candidate.Revision, 0) > Math.Max(current.Revision, 0);
    }

    // ---- 动作 ----

    public void OpenReleasePage()
    {
        string url = Latest?.PageUrl is { Length: > 0 } page ? page : $"{RepoUrl}/releases/latest";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开下载页失败", ex);
        }
    }

    /// <summary>
    /// 把 setup 资产下载到 Temp 再拉起安装器（安装器会自己关掉旧实例）。
    /// 进度按字节回调；返回 null 表示下载完成且安装器已启动，否则是给用户看的原因。
    /// </summary>
    public async Task<string?> DownloadAndRunAsync(Action<double>? progress)
    {
        var release = Latest;
        if (release?.SetupUrl is not string url || release.SetupName is not string name)
            return "这个版本没有安装器资产，请打开下载页手动下载";

        string path = Path.Combine(Path.GetTempPath(), "likeshot-update", name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            await using var from = await response.Content.ReadAsStreamAsync();
            await using var to = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Invoke((double)done / total);
            }
        }
        catch (Exception ex)
        {
            Log.Error("下载更新失败", ex);
            return $"下载没完成：{DescribeFailure(ex)}";
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log.Write($"更新安装器已启动：{path}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("启动更新安装器失败", ex);
            return $"安装器没拉起来：{ex.Message}（文件已存到 {path}）";
        }
    }
}