using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using LikeShot.Capture;
using WF = System.Windows.Forms;
using GPoint = System.Drawing.Point;

namespace LikeShot.Shell;

/// <summary>托盘：截图入口、保存目录、开机自启、退出，外加保存成功后的气泡回执。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly CaptureController _controller;
    private readonly AppConfig _config;
    private readonly Func<string> _statusProvider;

    public TrayIcon(CaptureController controller, AppConfig config, Func<string> statusProvider)
    {
        _controller = controller;
        _config = config;
        _statusProvider = statusProvider;

        _icon = new WF.NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = "LikeShot 截图"
        };
        _icon.DoubleClick += (_, _) => BeginCapture();
        _icon.ContextMenuStrip = BuildMenu();
        _controller.SetSavedNotifier(NotifySaved);
    }

    private WF.ContextMenuStrip BuildMenu()
    {
        var menu = new WF.ContextMenuStrip();

        var capture = new WF.ToolStripMenuItem { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) };
        capture.Click += (_, _) => BeginCapture();
        menu.Items.Add(capture);

        menu.Items.Add(new WF.ToolStripSeparator());

        var settings = new WF.ToolStripMenuItem("设置…");
        settings.Click += (_, _) => Application.Current.Dispatcher.Invoke(() => App.OpenSettings());
        menu.Items.Add(settings);

        var folder = new WF.ToolStripMenuItem("打开保存目录");
        folder.Click += (_, _) => OpenFolder();
        menu.Items.Add(folder);

        var autostart = new WF.ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
        autostart.Click += (_, _) =>
        {
            if (!AutoStart.Set(autostart.Checked)) autostart.Checked = AutoStart.IsEnabled();
            _config.RunOnStartup = autostart.Checked;
            AppConfig.Save(_config);
        };
        menu.Items.Add(autostart);

        menu.Items.Add(new WF.ToolStripSeparator());

        var status = new WF.ToolStripMenuItem(StatusLine()) { Enabled = false };
        menu.Items.Add(status);

        var exit = new WF.ToolStripMenuItem("退出 LikeShot");
        exit.Click += (_, _) => Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        menu.Items.Add(exit);

        // 热键和自启状态都可能被设置窗口改掉，菜单每次展开都按当前配置重读一遍，
        // 免得两处显示打架。
        menu.Opening += (_, _) =>
        {
            capture.Text = $"截图  {_config.CaptureChord}";
            status.Text = StatusLine();
            autostart.Checked = AutoStart.IsEnabled();
        };

        return menu;
    }

    private string StatusLine() => _statusProvider();

    private void BeginCapture() => Application.Current.Dispatcher.Invoke(() => _controller.BeginCapture());

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_config.SaveDirectory);
            Process.Start("explorer.exe", _config.SaveDirectory);
        }
        catch
        {
            // 目录打不开就算了，不给用户弹错误框。
        }
    }

    public void NotifySaved(string path)
    {
        _icon.ShowBalloonTip(1200, "截图已保存", Path.GetFileName(path), WF.ToolTipIcon.Info);
    }

    private static Icon BuildIcon()
    {
        // 托盘图标不引外部位图：32×32 现画一个取景框 + 十字，缩放不糊。
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var pen = new Pen(Color.FromArgb(240, 240, 240), 2.6f);
        using var accent = new Pen(Color.FromArgb(49, 138, 255), 2.6f);

        g.DrawLines(pen, new[] { new GPoint(4, 10), new GPoint(4, 4), new GPoint(10, 4) });
        g.DrawLines(pen, new[] { new GPoint(22, 4), new GPoint(28, 4), new GPoint(28, 10) });
        g.DrawLines(pen, new[] { new GPoint(28, 22), new GPoint(28, 28), new GPoint(22, 28) });
        g.DrawLines(pen, new[] { new GPoint(10, 28), new GPoint(4, 28), new GPoint(4, 22) });
        g.DrawLine(accent, 16, 9, 16, 23);
        g.DrawLine(accent, 9, 16, 23, 16);

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
