using System.IO;
using System.Threading;
using System.Windows;
using LikeShot.Capture;
using LikeShot.Hotkeys;
using LikeShot.Shell;

namespace LikeShot;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private HotkeyArbiter? _arbiter;
    private TrayIcon? _tray;
    private CaptureController? _controller;

    public static AppConfig Config { get; private set; } = new();

    /// <summary>自检要直接读仲裁器的判定，不给它开后门就只能靠日志猜。</summary>
    internal static HotkeyArbiter? Arbiter { get; private set; }

    private static UI.SettingsWindow? _settings;

    /// <summary>设置窗口只开一个：已经开着就把它带到前面。</summary>
    public static void OpenSettings()
    {
        var app = (App)Current;
        if (_settings is null)
        {
            _settings = new UI.SettingsWindow(Config);
            _settings.Closed += (_, _) => _settings = null;
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Show();
        _settings.Activate();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 抓屏/自检这类一次性命令不该被单实例拦掉，否则常驻实例在跑就没法取证。
        bool oneShot = e.Args.Contains("--snap") || e.Args.Contains("--selftest");
        _singleInstance = new Mutex(true, @"Local\LikeShot.SingleInstance", out bool isNew);
        if (!isNew && !oneShot)
        {
            Shutdown();
            return;
        }

        Dispatcher.UnhandledException += (_, args) =>
        {
            Log.Error("未处理异常", args.Exception);
            // 截图层崩掉时必须退出捕获状态，否则全屏遮罩会把整个桌面卡住。
            _controller?.Abort();
            args.Handled = true;
        };

        Config = AppConfig.Load();
        UI.Theme.LightUi = Config.CaptureUiTheme == "light";
        Log.Write($"启动 pid={Environment.ProcessId} 热键={Config.CaptureChord} 让位进程={string.Join(",", Config.YieldToProcesses)} 日志={Log.PathHint}");
        _controller = new CaptureController(this);

        _tray = new TrayIcon(_controller, Config, () => _arbiter?.StatusText ?? "热键待命");
        _arbiter = new HotkeyArbiter(Config, () => _controller.BeginCapture());
        _arbiter.Start();
        Arbiter = _arbiter;

        if (e.Args.Contains("--capture"))
            Dispatcher.BeginInvoke(() => _controller.BeginCapture());
        else if (e.Args.Contains("--selftest") && e.Args.Contains("--settingstest"))
            Dispatcher.BeginInvoke(() => SelfTest.SettingsRun(_controller, () => Shutdown()));
        else if (e.Args.Contains("--selftest") && e.Args.Contains("--settingsui"))
            Dispatcher.BeginInvoke(SelfTest.ShowSettings);
        else if (e.Args.Contains("--selftest") && e.Args.Contains("--hotkeytest"))
            Dispatcher.BeginInvoke(() => SelfTest.HotkeyRun(_controller, () => Shutdown()));
        else if (e.Args.Contains("--selftest"))
            Dispatcher.BeginInvoke(() => SelfTest.Run(_controller, () => Shutdown()));
        else if (FirstValue(e.Args, "--snap") is string snapPath)
            Dispatcher.BeginInvoke(() =>
            {
                // 给自己留的验证出口：抓一张当前屏幕写盘，不弹任何界面。
                using var shot = Capture.ScreenSnapshot.GrabVirtualDesktop();
                var snapError = Capture.Exporter.TryWrite(shot.Image, snapPath);
                if (snapError != null) Log.Write($"--snap 写盘失败：{snapError}");
                Shutdown();
            });
    }

    public void ReloadHotkey() => _arbiter?.Reload(Config);

    private static string? FirstValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _arbiter?.Dispose();
        _tray?.Dispose();
        AppConfig.Save(Config);
        base.OnExit(e);
    }

    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LikeShot");

    internal static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    internal static string EnsureConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
        return ConfigDirectory;
    }
}
