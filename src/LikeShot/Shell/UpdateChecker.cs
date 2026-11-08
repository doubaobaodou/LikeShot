using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    // 不走 api.github.com：匿名接口每 IP 每小时 60 次，代理出口 IP 一堆人共用，
    // 配额常被别人用光（403）。releases 网页没这条规矩，latest 会重定向到最新 tag 页。
    private const string LatestPageUrl = $"{RepoUrl}/releases/latest";

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
            using var resp = await Http.GetAsync(LatestPageUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var release = ParseLatestPage(resp.RequestMessage?.RequestUri);
            if (release == null)
            {
                SetStatus(State.Failed, "没从 GitHub 读到最新版本号");
                Log.Write("更新检查：releases 页重定向地址里认不出 tag");
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
            "GitHub 拒绝了请求（403，过会儿再试）",
        HttpRequestException h when h.StatusCode != null => $"GitHub 返回 {(int)h.StatusCode}",
        HttpRequestException => "连不上 GitHub（检查网络或代理）",
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

    /// <summary>
    /// 从 releases/latest 重定向后的地址里取 tag（…/releases/tag/v0.1.27）。
    /// 安装器地址按 pack.cmd 的命名约定拼：LikeShot-<三段版本>-win-x64-setup.exe。
    /// </summary>
    public static ReleaseInfo? ParseLatestPage(Uri? finalUri)
    {
        if (finalUri == null) return null;
        string path = finalUri.AbsolutePath;
        const string marker = "/releases/tag/";
        int tagAt = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (tagAt < 0) return null;
        string tag = path[(tagAt + marker.Length)..].Trim('/');
        var version = ParseVersion(tag);
        if (version == null) return null;

        string setupName = $"LikeShot-{version.ToString(3)}-win-x64-setup.exe";
        return new ReleaseInfo(version, tag, finalUri.ToString(),
            $"{RepoUrl}/releases/download/{tag}/{setupName}", setupName);
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

    public static void OpenRepoPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开 GitHub 仓库页失败", ex);
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