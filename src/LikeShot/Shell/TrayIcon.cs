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
        // 右键弹自己画的 WPF 菜单：WinForms 的 ContextMenuStrip 样式没救，托盘事件留在这里收。
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Right) ShowMenu();
        };

        _captureItem = MakeItem("camera", accentIcon: true);
        _captureItem.Click += (_, _) => BeginCapture();

        var settings = MakeItem("gear");
        settings.Header = "设置…";
        settings.Click += (_, _) => Application.Current.Dispatcher.Invoke(() => App.OpenSettings());

        var folder = MakeItem("folder");
        folder.Header = "打开保存目录";
        folder.Click += (_, _) => OpenFolder();

        _autostartItem = MakeItem("power", checkable: true);
        _autostartItem.Header = "开机自动启动";
        _autostartItem.Click += (_, _) =>
        {
            bool on = _autostartItem.IsChecked;
            if (!AutoStart.Set(on)) _autostartItem.IsChecked = AutoStart.IsEnabled();
            _config.RunOnStartup = _autostartItem.IsChecked;
            var error = AppConfig.Save(_config);
            if (error != null) NotifyProblem("开机自启没存下来", error);
        };

        _statusItem = new MenuItem { IsEnabled = false, Style = ItemStyle() };

        var exit = MakeItem("exit", redIcon: true);
        exit.Header = "退出 LikeShot";
        exit.Click += (_, _) => Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());

        _menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            HorizontalOffset = -14,
            VerticalOffset = -14,
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
            FontSize = 13,
            Style = MenuStyle()
        };
        _menu.Items.Add(_captureItem);
        _menu.Items.Add(Sep());
        _menu.Items.Add(settings);
        _menu.Items.Add(folder);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(Sep());
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
            // 从托盘弹出时没有一个被激活的宿主窗口，WPF 默认的「点菜单外面就关」
            // 不会生效——菜单会一直挂着，只能再点一次托盘。自己把鼠标捕获拿过来，
            // 之后任何一次菜单外的按下都会触发 MenuBase 的关单逻辑。
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

    // ---------- 样式 ----------
    // 模板里有 Grid 列定义和 TargetName 触发器，工厂树拼不动，按 SliderStyle 的办法
    // 用 XAML 生成、色值插进去。

    private Style MenuStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ContextMenu">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ContextMenu">
                            <Grid Margin="14">
                                <!-- 四周留 14px 给投影，整个弹出窗是透明的，阴影落得下去 -->
                                <Border CornerRadius="9" Background="White">
                                    <Border.Effect>
                                        <DropShadowEffect BlurRadius="16" ShadowDepth="3" Direction="270" Opacity="0.26"/>
                                    </Border.Effect>
                                </Border>
                                <Border Background="{BG}" BorderBrush="{BORDER}" BorderThickness="1"
                                        CornerRadius="8" Padding="5">
                                    <StackPanel IsItemsHost="True"/>
                                </Border>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml
            .Replace("{BG}", "#FFF9F9F9")
            .Replace("{BORDER}", "#FFE3E3E3"));
    }

    private static Separator Sep() => new()
    {
        Margin = new Thickness(6, 4, 6, 4),
        Style = SepStyle()
    };

    private static Style SepStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="Separator">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Separator">
                            <Border Height="1" Background="#FFE7E7E7" CornerRadius="0.5"/>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Setter Property="IsHitTestVisible" Value="False"/>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private MenuItem MakeItem(string iconKey, bool accentIcon = false, bool redIcon = false, bool checkable = false)
    {
        var item = new MenuItem
        {
            Header = "",   // 标题由调用处给，键位这类会变的内容展开时再刷
            Icon = TrayGlyph(iconKey, accentIcon ? Theme.Accent : redIcon ? Theme.DangerRed : Color.FromRgb(0x4A, 0x4A, 0x4A)),
            Margin = new Thickness(1, 0, 1, 0),
            Style = ItemStyle()
        };
        if (checkable) item.IsCheckable = true;
        return item;
    }

    /// <summary>菜单项模板：左图标、中标题、右侧选中勾（或快捷键），悬停圆角灰块。</summary>
    private static Style ItemStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="MenuItem">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="MenuItem">
                            <Border x:Name="hover" Height="34" CornerRadius="5" Background="Transparent">
                                <Grid Margin="10,0,10,0">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="27"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <ContentPresenter Grid.Column="0" ContentSource="Icon" VerticalAlignment="Center" Width="17"/>
                                    <ContentPresenter x:Name="header" Grid.Column="1" ContentSource="Header"
                                                      VerticalAlignment="Center"
                                                      TextBlock.Foreground="#FF1B1B1B" TextBlock.FontSize="13"/>
                                    <TextBlock x:Name="gesture" Grid.Column="2" Text="{TemplateBinding InputGestureText}"
                                               VerticalAlignment="Center" Foreground="#FF8C8C8C" FontSize="12" Margin="18,0,2,0"/>
                                    <TextBlock x:Name="check" Grid.Column="2" Text="&#xE73E;"
                                               FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="13"
                                               VerticalAlignment="Center" Margin="18,0,2,0" Foreground="{CHECK}"
                                               Visibility="Collapsed"/>
                                </Grid>
                            </Border>
                            <ControlTemplate.Triggers>
                                <MultiTrigger>
                                    <MultiTrigger.Conditions>
                                        <Condition Property="IsMouseOver" Value="True"/>
                                        <Condition Property="IsEnabled" Value="True"/>
                                    </MultiTrigger.Conditions>
                                    <Setter TargetName="hover" Property="Background" Value="#FFEBEBEB"/>
                                </MultiTrigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter TargetName="header" Property="TextBlock.Foreground" Value="#FF9E9E9E"/>
                                </Trigger>
                                <Trigger Property="IsChecked" Value="True">
                                    <Setter TargetName="check" Property="Visibility" Value="Visible"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml.Replace("{CHECK}", "#FF318AFF"));
    }

    /// <summary>托盘菜单的 17px 描边小图标，和工具栏同一套 Lucide 路径。</summary>
    private static FrameworkElement TrayGlyph(string iconKey, Color color)
    {
        var brush = Theme.Brush(color);
        var canvas = new Canvas { Width = 17, Height = 17 };
        if (Icons.StrokePaths.TryGetValue(iconKey, out var data))
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(data),
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        return canvas;
    }

    // ---------- 行为 ----------

    private void BeginCapture() => Application.Current.Dispatcher.Invoke(() => _controller.BeginCapture());

    private void OpenFolder() => FolderActions.OpenFolder(_config.SaveDirectory);

    public void NotifySaved(string path)
    {
        _icon.ShowBalloonTip(1200, "截图已保存", Path.GetFileName(path), WF.ToolTipIcon.Info);
    }

    /// <summary>界面已经关掉、没地方挂回执的失败才走气泡；能挂在截图层里的失败别用这条。</summary>
    public void NotifyProblem(string title, string message)
    {
        _icon.ShowBalloonTip(3000, title, message, WF.ToolTipIcon.Warning);
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
