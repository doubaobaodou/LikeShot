using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LikeShot.Capture;
using LikeShot.UI;
using WF = System.Windows.Forms;
using GPoint = System.Drawing.Point;
using DIcon = System.Drawing.Icon;
using DBitmap = System.Drawing.Bitmap;
using DGraphics = System.Drawing.Graphics;
using DColor = System.Drawing.Color;
using DPen = System.Drawing.Pen;

namespace LikeShot.Shell;

/// <summary>
/// 托盘：截图入口、保存目录、开机自启、退出，外加保存成功后的气泡回执。
/// 菜单不用 WinForms 那条白板样式，走 WPF 自绘——和设置页同一套 Win11 观感：
/// 圆角 8、投影、悬停圆角高亮、每项带 Lucide 图标。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly CaptureController _controller;
    private readonly AppConfig _config;
    private readonly Func<string> _statusProvider;
    private readonly ContextMenu _menu;
    private readonly MenuItem _captureItem, _statusItem, _autostartItem;
    private bool _disposed;
    private bool _balloonIsUpdate;

    public TrayIcon(CaptureController controller, AppConfig config, Func<string> statusProvider)
    {
        _controller = controller;
        _config = config;
        _statusProvider = statusProvider;

        _icon = new WF.NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = $"LikeShot 截图 {UpdateChecker.CurrentVersionText}"
        };
        _icon.DoubleClick += (_, _) => BeginCapture();
        // 右键弹自己画的 WPF 菜单：WinForms 的 ContextMenuStrip 样式没救，托盘事件留在这里收。
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonIsUpdate) Application.Current.Dispatcher.Invoke(() => App.OpenSettings(UI.SettingsWindow.AboutSectionIndex));
        };

        _captureItem = MenuCard.Item("camera", "截图", accentIcon: true);
        _captureItem.Click += (_, _) => BeginCapture();

        var settings = MenuCard.Item("gear", "设置…",
            () => Application.Current.Dispatcher.Invoke(() => App.OpenSettings()));

        var update = MenuCard.Item("refresh", "检查更新…",
            () => Application.Current.Dispatcher.Invoke(() => App.OpenSettings(UI.SettingsWindow.AboutSectionIndex)));

        var history = MenuCard.Item("history", "历史截图…",
            () => Application.Current.Dispatcher.Invoke(
                () => App.OpenSettings(SettingsWindow.HistorySectionIndex)));

        var folder = MenuCard.Item("folder", "打开保存目录", OpenFolder);

        _autostartItem = MenuCard.Item("power", "开机自动启动", checkable: true);
        _autostartItem.Click += (_, _) =>
        {
            bool on = _autostartItem.IsChecked;
            if (!AutoStart.Set(on)) _autostartItem.IsChecked = AutoStart.IsEnabled();
            _config.RunOnStartup = _autostartItem.IsChecked;
            var error = AppConfig.Save(_config);
            if (error != null) NotifyProblem("开机自启没存下来", error);
        };

        _statusItem = MenuCard.StatusItem("");

        var exit = MenuCard.Item("exit", "退出 LikeShot",
            () => Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown()),
            red: true);

        _menu = MenuCard.New();
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        // 卡片四周留了 14px 透明边距给投影，不往回挪一点，鼠标会落在投影空白上。
        _menu.HorizontalOffset = -14;
        _menu.VerticalOffset = -14;
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(MenuCard.Sep());
        _menu.Items.Add(settings);
        _menu.Items.Add(update);
        _menu.Items.Add(history);
        _menu.Items.Add(folder);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(MenuCard.Sep());
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(exit);

        RefreshTexts();
        _controller.SetNotifier(NotifySaved, NotifyProblem);
    }

    private void ShowMenu()
    {
        // 热键和自启状态都可能被设置窗口改掉，展开前按当前配置重读一遍，
        // 免得两处显示打架。
        RefreshTexts();
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_menu.IsOpen) { _menu.IsOpen = false; return; }
            _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _menu.IsOpen = true;
            // 系统有「前台锁」：后台进程拿不住鼠标捕获，而 Popup 的「点外面收起」
            // 正靠那份捕获——托盘弹菜单时我们在后台，捕获落空，菜单就挂着不收。
            // 先把菜单的弹出窗顶到前台再捕获，外点才走得到收单逻辑。
            if (System.Windows.PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource src)
                LikeShot.Native.Win32.ForceForeground(src.Handle);
            System.Windows.Input.Mouse.Capture(_menu, System.Windows.Input.CaptureMode.SubTree);
        });
    }

    /// <summary>无头验证用：把菜单本体拿出来渲染 / 按真实路径弹出。</summary>
    internal ContextMenu MenuForTest => _menu;

    internal void ShowMenuForTest() => ShowMenu();

    private void RefreshTexts()
    {
        // Win11 习惯：标题归标题，快捷键放右侧灰字，不和标题挤在一起。
        _captureItem.Header = "截图";
        _captureItem.InputGestureText = _config.CaptureChord;
        _statusItem.Header = _statusProvider();
        _autostartItem.IsChecked = AutoStart.IsEnabled();
    }

    // ---------- 行为 ----------

    private void BeginCapture() => Application.Current.Dispatcher.Invoke(() => _controller.BeginCapture());

    private void OpenFolder() => FolderActions.OpenFolder(_config.SaveDirectory);

    public void NotifySaved(string path)
    {
        _balloonIsUpdate = false;
        _icon.ShowBalloonTip(1200, "截图已保存", Path.GetFileName(path), WF.ToolTipIcon.Info);
    }

    /// <summary>界面已经关掉、没地方挂回执的失败才走气泡；能挂在截图层里的失败别用这条。</summary>
    public void NotifyProblem(string title, string message)
    {
        _balloonIsUpdate = false;
        _icon.ShowBalloonTip(3000, title, message, WF.ToolTipIcon.Warning);
    }

    /// <summary>发现新版：气泡提一次，点进气泡直接落在「关于」页，那里能下载并安装。</summary>
    public void NotifyUpdate(UpdateChecker.ReleaseInfo release)
    {
        _balloonIsUpdate = true;
        _icon.ShowBalloonTip(5000, "LikeShot 有新版",
            $"{release.VersionText} 已发布（当前 {UpdateChecker.CurrentVersionText}），点这条气泡去更新",
            WF.ToolTipIcon.Info);
    }

    private static DIcon BuildIcon()
    {
        // 托盘图标不引外部位图：32×32 现画一个取景框 + 十字，缩放不糊。
        using var bmp = new DBitmap(32, 32);
        using var g = DGraphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(DColor.Transparent);
        using var pen = new DPen(DColor.FromArgb(240, 240, 240), 2.6f);
        using var accent = new DPen(DColor.FromArgb(49, 138, 255), 2.6f);

        g.DrawLines(pen, new[] { new GPoint(4, 10), new GPoint(4, 4), new GPoint(10, 4) });
        g.DrawLines(pen, new[] { new GPoint(22, 4), new GPoint(28, 4), new GPoint(28, 10) });
        g.DrawLines(pen, new[] { new GPoint(28, 22), new GPoint(28, 28), new GPoint(22, 28) });
        g.DrawLines(pen, new[] { new GPoint(10, 28), new GPoint(4, 28), new GPoint(4, 22) });
        g.DrawLine(accent, 16, 9, 16, 23);
        g.DrawLine(accent, 9, 16, 23, 16);

        return DIcon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        // 自检会先把托盘收掉再退，随后 OnExit 还会再收一次，重复调用不能炸。
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
