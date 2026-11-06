using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Capture;
using LikeShot.Hotkeys;

namespace LikeShot.UI;

/// <summary>
/// 设置窗口，按 Windows 11 设置页的样子来的：浅色底、左侧导航、右侧圆角卡片。
/// 热键还是「点一下把组合键按出来」，底部「应用」一次改完立刻生效——
/// 热键这种东西改完就是要马上试一下的。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ChordBox _chord = new();
    private readonly TextBox _yieldNames = new();
    private readonly TextBox _saveDir = new();
    private readonly TextBlock _fileName = new()
    {
        FontSize = 12,
        Foreground = Theme.Brush(SettingsTheme.TextSecondary),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0)
    };
    private readonly TextBlock _chordHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly CheckBox _magnifier = Check();
    private readonly CheckBox _copyAfterSave = Check();
    private readonly CheckBox _autoStart = Check();
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = Theme.Brush(SettingsTheme.TextSecondary),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly PillGroup<string> _format = new(
        ["png", "jpg", "bmp"], ["PNG", "JPG", "BMP"], ["存成无损 PNG", "体积小一些，适合照片", "兼容性最好，体积最大"]);
    private readonly PillGroup<int> _poll = new(
        [120, 220, 350], ["快", "标准", "慢"], ["每 0.12 秒抓一帧", "每 0.22 秒抓一帧", "每 0.35 秒抓一帧"]);
    private readonly PillGroup<int> _notches = new(
        [1, 2, 3], ["1 格", "2 格", "3 格"], ["每帧推 1 格滚轮", "每帧推 2 格滚轮", "每帧推 3 格滚轮"]);
    private readonly PillGroup<string> _captureTheme = new(
        ["dark", "light"], ["深色", "浅色"], ["和 QQ 一样的深色工具栏", "白色工具栏，亮色环境下看得清"]);

    private readonly List<NavItem> _navItems = [];
    private readonly List<FrameworkElement> _pages = [];

    public SettingsWindow(AppConfig config)
    {
        _config = config;

        Title = "LikeShot 设置";
        Width = 780;
        Height = 600;
        MinWidth = 680;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Background = Theme.Brush(SettingsTheme.PageBackground);
        Foreground = Theme.Brush(SettingsTheme.TextPrimary);
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        UseLayoutRounding = true;
        LoadWindowIcon();

        BuildLayout();

        _chord.ChordChanged += (_, _) => { RefreshChordHint(); MarkDirty(); };
        _yieldNames.TextChanged += (_, _) => { RefreshChordHint(); MarkDirty(); };
        _saveDir.TextChanged += (_, _) => { RefreshFileName(); MarkDirty(); };
        _format.Changed += () => { RefreshFileName(); MarkDirty(); };
        _magnifier.Checked += (_, _) => MarkDirty();
        _magnifier.Unchecked += (_, _) => MarkDirty();
        _copyAfterSave.Checked += (_, _) => MarkDirty();
        _copyAfterSave.Unchecked += (_, _) => MarkDirty();
        _captureTheme.Changed += MarkDirty;
        _poll.Changed += MarkDirty;
        _notches.Changed += MarkDirty;
        _historyLimit.Changed += MarkDirty;
        _autoStart.Checked += (_, _) => MarkDirty();
        _autoStart.Unchecked += (_, _) => MarkDirty();

        LoadFrom(config);
        SwitchSection(0);
    }

    /// <summary>把控件填成配置里的值。</summary>
    public void LoadFrom(AppConfig config)
    {
        _chord.Text = config.CaptureChord;
        _yieldNames.Text = string.Join(", ", config.YieldToProcesses);
        _saveDir.Text = config.SaveDirectory;
        _format.Select(config.SaveFormat);
        _poll.Select(config.LongCapturePollMilliseconds);
        _notches.Select(config.LongCaptureScrollNotches);
        _magnifier.IsChecked = config.ShowMagnifier;
        _copyAfterSave.IsChecked = config.CopyAfterSave;
        _captureTheme.Select(config.CaptureUiTheme);
        _historyLimit.Select(config.HistoryLimit);
        _autoStart.IsChecked = Shell.AutoStart.IsEnabled();

        RefreshChordHint();
        RefreshFileName();
        _status.Text = "已应用";
    }

    /// <summary>读控件 → 写配置 → 落盘 → 重载热键。热键非法时什么都不改，只在提示里说。</summary>
    public bool Apply()
    {
        Chord parsed;
        try
        {
            parsed = Chord.Parse(_chord.Text);
        }
        catch (FormatException ex)
        {
            ShowChordError(ex.Message);
            _status.Text = "热键没设好，这次没应用";
            return false;
        }

        _config.CaptureChord = parsed.Text;
        _config.YieldToProcesses = SplitNames(_yieldNames.Text);
        _config.SaveFormat = _format.Selected;
        _config.LongCapturePollMilliseconds = _poll.Selected;
        _config.LongCaptureScrollNotches = _notches.Selected;
        _config.ShowMagnifier = _magnifier.IsChecked == true;
        _config.CopyAfterSave = _copyAfterSave.IsChecked == true;
        _config.CaptureUiTheme = _captureTheme.Selected;
        _config.HistoryLimit = _historyLimit.Selected;
        Theme.LightUi = _captureTheme.Selected == "light";

        var dir = _saveDir.Text.Trim();
        if (dir.Length > 0) _config.SaveDirectory = dir;

        bool wantAutoStart = _autoStart.IsChecked == true;
        _config.RunOnStartup = wantAutoStart && Shell.AutoStart.Set(wantAutoStart);
        _autoStart.IsChecked = _config.RunOnStartup;

        // 先让改动当场生效，再谈记住：写盘失败不该把用户已经按下的「应用」变成白点一次。
        if (Application.Current is App app) app.ReloadHotkey();
        RefreshChordHint();
        RefreshFileName();

        var saveError = AppConfig.Save(_config);
        if (saveError != null)
        {
            // 生效了但没记住——这两件事必须分开说，合成一句「失败」会让人以为白点了。
            _status.Text = $"改动已生效，但没能记住：{saveError}（下次启动会回到旧设置）";
            Log.Write($"设置应用：已生效，写盘失败 {saveError}");
            return false;
        }

        int pruned = HistoryStore.Shared.PruneTo(_config.HistoryLimit);
        Log.Write($"设置已应用 热键={_config.CaptureChord} 让位={string.Join(",", _config.YieldToProcesses)} " +
                  $"格式={_config.SaveFormat} 间隔={_config.LongCapturePollMilliseconds}ms 每帧{_config.LongCaptureScrollNotches}格 " +
                  $"放大镜={_config.ShowMagnifier} 存后复制={_config.CopyAfterSave} 配色={_config.CaptureUiTheme} " +
                  $"自启={_config.RunOnStartup} 历史留={_config.HistoryLimit}张 顺手删掉={pruned}张");
        _status.Text = pruned > 0
            ? $"已应用 {DateTime.Now:HH:mm:ss}，并按新的保留数量删掉 {pruned} 张最旧的"
            : $"已应用 {DateTime.Now:HH:mm:ss}";
        return true;
    }

    /// <summary>自检用：绕开鼠标键盘，直接把控件填成给定值。</summary>
    internal string ChordForTest { set { _chord.Text = value; RefreshChordHint(); } }
    internal string YieldForTest { set => _yieldNames.Text = value; }
    internal string SaveDirForTest { set => _saveDir.Text = value; }
    internal string StatusForTest => _status.Text;
    internal string ChordHintForTest => _chordHint.Text;
    internal void PickForTest(string format, int poll, int notches)
    {
        _format.Select(format);
        _poll.Select(poll);
        _notches.Select(notches);
    }

    /// <summary>截图/自检/托盘用：直接切到第 index 个导航页。</summary>
    internal void SelectSection(int index) => SwitchSection(index);

    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选保存目录" };
        try
        {
            if (System.IO.Directory.Exists(_saveDir.Text.Trim())) dialog.InitialDirectory = _saveDir.Text.Trim();
        }
        catch
        {
            // 初始目录给不了就用系统默认位置，不拦这次选择。
        }
        if (dialog.ShowDialog(this) == true) _saveDir.Text = dialog.FolderName;
    }

    private void RefreshFileName()
    {
        var name = Exporter.SuggestedFileName(_format.Selected);
        var dir = _saveDir.Text.Trim();
        _fileName.Text = dir.Length == 0
            ? $"下次存为：{name}（目录没填，会用系统默认位置）"
            : $"下次存为：{System.IO.Path.Combine(dir, name)}";
    }

    private void RefreshChordHint()
    {
        Chord chord;
        try
        {
            chord = Chord.Parse(_chord.Text);
        }
        catch (FormatException ex)
        {
            ShowChordError(ex.Message);
            return;
        }

        var (attention, text) = ChordProbe.Describe(chord, SplitNames(_yieldNames.Text));
        _chordHint.Text = text;
        _chordHint.Foreground = Theme.Brush(attention ? SettingsTheme.Warn : SettingsTheme.Good);
    }

    private void ShowChordError(string message)
    {
        _chordHint.Text = message;
        _chordHint.Foreground = Theme.Brush(SettingsTheme.Bad);
    }

    private void MarkDirty() => _status.Text = "有改动没应用";

    private static List<string> SplitNames(string text) =>
        text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- 布局 ----

    private void BuildLayout()
    {
        // 每个导航项一个页面；让位名单跟着热键走，它们本来就是一件事。
        // 历史截图那页自己带滚动区和常驻筛选条，所以不再套外层 ScrollViewer。
        var sections = new (string Title, string Glyph, Func<UIElement> Build, bool WrapScroll)[]
        {
            ("截图热键", "\uE765", BuildHotkeyPage, true),
            ("保存", "\uE74E", BuildSavePage, true),
            ("截图时", "\uE722", BuildCapturePage, true),
            ("长截图", "\uE81E", BuildLongPage, true),
            ("历史截图", "\uE823", BuildHistoryPage, false),
            ("启动", "\uE7E8", BuildStartupPage, true),
        };

        var nav = new StackPanel { Margin = new Thickness(8, 10, 8, 8) };
        var content = new Grid();

        for (int i = 0; i < sections.Length; i++)
        {
            int index = i;
            var item = new NavItem(sections[i].Glyph, sections[i].Title);
            item.Clicked += () => SwitchSection(index);
            nav.Children.Add(item);
            _navItems.Add(item);

            var page = sections[i].Build();
            FrameworkElement host = sections[i].WrapScroll
                ? new ScrollViewer
                {
                    Content = page,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
                : page as FrameworkElement ?? new Border { Child = page };
            host.Visibility = Visibility.Collapsed;
            content.Children.Add(host);
            _pages.Add(host);
        }

        var divider = new Border { Width = 1, Background = Theme.Brush(SettingsTheme.CardBorder) };

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(nav, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(content, 2);
        main.Children.Add(nav);
        main.Children.Add(divider);
        main.Children.Add(content);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var apply = StandardButton("应用", accent: true);
        apply.MinWidth = 88;
        apply.Click += (_, _) => Apply();
        var close = StandardButton("关闭");
        close.Click += (_, _) => Close();
        buttons.Children.Add(apply);
        buttons.Children.Add(new Border { Width = 10 });
        buttons.Children.Add(close);

        var footer = new Grid { Margin = new Thickness(28, 8, 28, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var footerBar = new Border
        {
            Background = Theme.Brush(SettingsTheme.PageBackground),
            BorderBrush = Theme.Brush(SettingsTheme.CardBorder),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footer
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(main, 0);
        Grid.SetRow(footerBar, 1);
        root.Children.Add(main);
        root.Children.Add(footerBar);
        Content = root;
    }

    private void SwitchSection(int index)
    {
        for (int i = 0; i < _navItems.Count; i++)
        {
            _navItems[i].Selected = i == index;
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        // 每次进历史页都重读一遍库：截图刚存进去的、另一个窗口删掉的，都要在这儿对得上。
        if (index == HistorySectionIndex) RefreshHistory();
    }

    private UIElement BuildHotkeyPage()
    {
        var restore = StandardButton("恢复默认");
        restore.Click += (_, _) =>
        {
            _chord.Text = "Ctrl+Alt+A";
            RefreshChordHint();
            MarkDirty();
        };

        var chordRow = new StackPanel { Orientation = Orientation.Horizontal };
        chordRow.Children.Add(_chord);
        chordRow.Children.Add(new Border { Width = 6 });
        chordRow.Children.Add(restore);

        var hotkeyCard = Card(
            Row("热键", Desc("点一下输入框，直接把组合键按出来"), chordRow),
            HintRow(_chordHint));

        var yieldCard = Card(Row("让位名单",
            Desc("名单里的进程在跑时，上面的热键先让给它。进程名不带 .exe，逗号分开，例如 QQ, Tim"),
            FieldBox(_yieldNames, "进程名之间用逗号分开，例如 QQ, Tim")));

        return Page("截图热键", hotkeyCard, yieldCard);
    }

    private UIElement BuildSavePage()
    {
        var browse = StandardButton("浏览…");
        browse.Click += (_, _) => PickFolder();

        var dirRow = new StackPanel { Orientation = Orientation.Horizontal };
        dirRow.Children.Add(FieldBox(_saveDir, "截图存到哪个目录"));
        dirRow.Children.Add(new Border { Width = 6 });
        dirRow.Children.Add(browse);

        var card = Card(
            Row("保存目录", Desc("截图保存的位置"), dirRow),
            Sep(),
            Row("图片格式", _fileName, _format.Host));

        return Page("保存", card);
    }

    private UIElement BuildCapturePage() => Page("截图时", Card(
        Row("显示放大镜", Desc("选区时显示尺寸和像素颜色"), _magnifier),
        Sep(),
        Row("保存后复制", Desc("保存文件后同时复制一份到剪贴板"), _copyAfterSave),
        Sep(),
        Row("界面配色", Desc("截图时的工具栏、放大镜用深色还是浅色"), _captureTheme.Host)));

    private UIElement BuildLongPage() => Page("长截图",
        Card(
            Row("抓取间隔", Desc("自动滚动时每隔多久抓一帧"), _poll.Host),
            Sep(),
            Row("每次滚动", Desc("每抓一帧推几格滚轮"), _notches.Host)),
        Caption("页面滚得快就把间隔调短、每次多滚几格；两个旋钮各管一件事。"));

    private UIElement BuildStartupPage() => Page("启动", Card(
        Row("开机自动启动", Desc("登录 Windows 后自动运行 LikeShot"), _autoStart)));

    // ---- 小构件 ----

    private static StackPanel Page(string title, params UIElement[] blocks)
    {
        var stack = new StackPanel { Margin = new Thickness(28, 16, 28, 20) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(SettingsTheme.TextPrimary),
            Margin = new Thickness(0, 0, 0, 12)
        });
        foreach (var block in blocks)
        {
            if (stack.Children.Count > 1) stack.Children.Add(new Border { Height = 8 });
            stack.Children.Add(block);
        }
        return stack;
    }

    /// <summary>白底圆角卡片，行与行之间自己插 Sep() 分隔线。</summary>
    private static Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return new Border
        {
            Background = Theme.Brush(SettingsTheme.Card),
            BorderBrush = Theme.Brush(SettingsTheme.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = stack
        };
    }

    private static FrameworkElement Sep() => new Border
    {
        Height = 1,
        Background = Theme.Brush(SettingsTheme.Divider),
        Margin = new Thickness(16, 0, 16, 0)
    };

    /// <summary>卡片里的一行：左边标题+说明，右边控件，垂直居中。</summary>
    private static Grid Row(string title, UIElement? desc, FrameworkElement? control)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, FontSize = 13.5, Foreground = Theme.Brush(SettingsTheme.TextPrimary) });
        if (desc != null) left.Children.Add(desc);

        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return grid;
    }

    private static TextBlock Desc(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Theme.Brush(SettingsTheme.TextSecondary),
        Margin = new Thickness(0, 2, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Theme.Brush(SettingsTheme.TextSecondary),
        Margin = new Thickness(2, 0, 2, 0),
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>跟在热键行下面的一条动态提示，占满卡片宽度。</summary>
    private static FrameworkElement HintRow(TextBlock hint) => new Border
    {
        Margin = new Thickness(16, 0, 16, 12),
        Child = hint
    };

    private static TextBox FieldBox(TextBox box, string tooltip)
    {
        box.Style = FieldStyle();
        box.Width = 230;
        ToolTipService.SetToolTip(box, tooltip);
        return box;
    }

    private static CheckBox Check() => new()
    {
        Style = CheckStyle(),
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>取证用：从命令行拉起来拿不到前台权限，只能置顶再抢一次焦点。</summary>
    internal void RaiseTopmostForTest()
    {
        Topmost = true;
        Activate();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Native.Win32.SetWindowPos(hwnd, Native.Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Native.Win32.SWP_NOMOVE | Native.Win32.SWP_NOSIZE);
    }

    private void LoadWindowIcon()
    {
        try
        {
            if (Environment.ProcessPath is not string exe) return;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Icon = source;
        }
        catch
        {
            // 图标拿不到就用默认的，不影响设置本身。
        }
    }

    // ---- 样式 ----

    private static Style FieldStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Brush(Colors.White)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Brush(SettingsTheme.TextPrimary)));
        style.Setters.Add(new Setter(TextBox.CaretBrushProperty, Theme.Brush(SettingsTheme.TextPrimary)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Brush(SettingsTheme.ControlBorder)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));

        // 两个都触发时后加的赢：悬停先加，聚焦后加，聚焦态保持蓝色边。
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Brush(Color.FromRgb(0xB3, 0xB3, 0xB3))));
        style.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Brush(SettingsTheme.Accent)));
        style.Triggers.Add(focus);
        return style;
    }

    private static Style CheckStyle()
    {
        var style = new Style(typeof(CheckBox));
        var template = new ControlTemplate(typeof(CheckBox));

        var box = new FrameworkElementFactory(typeof(Border));
        box.Name = "box";
        box.SetValue(Border.WidthProperty, 18.0);
        box.SetValue(Border.HeightProperty, 18.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        box.SetValue(Border.BackgroundProperty, Theme.Brush(Colors.White));
        box.SetValue(Border.BorderBrushProperty, Theme.Brush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        box.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);

        var check = new FrameworkElementFactory(typeof(TextBlock));
        check.Name = "check";
        check.SetValue(TextBlock.TextProperty, "\uE73E");
        check.SetValue(TextBlock.FontFamilyProperty, SettingsTheme.IconFont);
        check.SetValue(TextBlock.FontSizeProperty, 10.0);
        check.SetValue(TextBlock.ForegroundProperty, Theme.Brush(Colors.White));
        check.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 0, 0));
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(check);
        template.VisualTree = box;

        // 悬停在前、选中在后：勾着的时候悬停也保持蓝底。
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush(SettingsTheme.ControlHover), "box"));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush(SettingsTheme.TextPrimary), "box"));
        template.Triggers.Add(hover);
        var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush(SettingsTheme.Accent), "box"));
        isChecked.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush(SettingsTheme.Accent), "box"));
        isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
        template.Triggers.Add(isChecked);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        return style;
    }

    /// <summary>Win11 款按钮：白底灰边的普通款，或者蓝底白字的主按钮。颜色走 style，触发器才够得着。</summary>
    private static Button StandardButton(string text, bool accent = false)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            Theme.Brush(accent ? SettingsTheme.Accent : Colors.White)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            Theme.Brush(accent ? SettingsTheme.Accent : SettingsTheme.ControlBorder)));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            Theme.Brush(accent ? Colors.White : SettingsTheme.TextPrimary)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            Theme.Brush(accent ? SettingsTheme.AccentHover : SettingsTheme.ControlHover)));
        style.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty,
            Theme.Brush(accent ? SettingsTheme.AccentPressed : SettingsTheme.ControlPressed)));
        style.Triggers.Add(pressed);

        return new Button
        {
            Content = text,
            Style = style,
            MinHeight = 30,
            MinWidth = 68,
            Padding = new Thickness(14, 5, 14, 5),
            FontSize = 13
        };
    }

    /// <summary>左侧导航项：选中时左边一根蓝色小竖条，配浅灰底。</summary>
    private sealed class NavItem : Border
    {
        private readonly Border _indicator;
        private bool _selected;

        public event Action? Clicked;

        public NavItem(string glyph, string label)
        {
            Height = 42;
            CornerRadius = new CornerRadius(4);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;

            _indicator = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                Background = Theme.Brush(SettingsTheme.Accent),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 8, 0)
            };
            stack.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = SettingsTheme.IconFont,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(SettingsTheme.TextPrimary)
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = Theme.Brush(SettingsTheme.TextPrimary)
            });

            var grid = new Grid();
            grid.Children.Add(_indicator);
            grid.Children.Add(stack);
            Child = grid;

            MouseEnter += (_, _) => { if (!_selected) Background = Theme.Brush(SettingsTheme.NavHover); };
            MouseLeave += (_, _) => { if (!_selected) Background = Brushes.Transparent; };
            MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        }

        public bool Selected
        {
            set
            {
                _selected = value;
                Background = value ? Theme.Brush(SettingsTheme.NavSelected) : Brushes.Transparent;
                _indicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}

/// <summary>设置窗口自己的浅色配色，按 Windows 11 设置页取的色值，不和截图层那套深色混用。</summary>
internal static class SettingsTheme
{
    public static readonly Color Accent = Color.FromRgb(0x00, 0x67, 0xC0);
    public static readonly Color AccentHover = Color.FromRgb(0x19, 0x75, 0xC5);
    public static readonly Color AccentPressed = Color.FromRgb(0x00, 0x5B, 0xA1);
    public static readonly Color PageBackground = Color.FromRgb(0xF3, 0xF3, 0xF3);
    public static readonly Color Card = Colors.White;
    public static readonly Color CardBorder = Color.FromRgb(0xE7, 0xE7, 0xE7);
    public static readonly Color Divider = Color.FromRgb(0xEE, 0xEE, 0xEE);
    public static readonly Color NavSelected = Color.FromRgb(0xE6, 0xE6, 0xE6);
    public static readonly Color NavHover = Color.FromRgb(0xEA, 0xEA, 0xEA);
    public static readonly Color TextPrimary = Color.FromRgb(0x1B, 0x1B, 0x1B);
    public static readonly Color TextSecondary = Color.FromRgb(0x5F, 0x5F, 0x5F);
    public static readonly Color ControlBorder = Color.FromRgb(0xD1, 0xD1, 0xD1);
    public static readonly Color ControlHover = Color.FromRgb(0xF4, 0xF4, 0xF4);
    public static readonly Color ControlPressed = Color.FromRgb(0xED, 0xED, 0xED);

    /// <summary>浅色底上的语义色，加深到能读清的程度。</summary>
    public static readonly Color Warn = Color.FromRgb(0x9D, 0x5D, 0x00);
    public static readonly Color Bad = Color.FromRgb(0xC4, 0x2B, 0x1C);
    public static readonly Color Good = Color.FromRgb(0x0F, 0x7B, 0x0F);

    /// <summary>图标字体，Win11 是 Fluent，Win10 退回 MDL2。</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

/// <summary>「点一下把组合键按出来」的输入框。只收带修饰键的组合，Esc 取消。</summary>
internal sealed class ChordBox : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = Theme.Brush(SettingsTheme.TextPrimary) };
    private readonly TextBlock _cue = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11.5, Foreground = Theme.Brush(Color.FromRgb(0x76, 0x76, 0x76)) };
    private string _committed = "Ctrl+Alt+A";
    private bool _capturing;

    public event EventHandler? ChordChanged;

    public ChordBox()
    {
        Width = 190;
        Background = Theme.Brush(Colors.White);
        BorderBrush = Theme.Brush(SettingsTheme.ControlBorder);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Height = 32;
        Focusable = true;
        Cursor = Cursors.Hand;

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };
        stack.Children.Add(_text);
        stack.Children.Add(_cue);
        Child = stack;

        MouseLeftButtonUp += (_, _) => Focus();
        GotKeyboardFocus += (_, _) => BeginCapture();
        LostKeyboardFocus += (_, _) => EndCapture();
        PreviewKeyDown += OnKeyDown;
        _text.Text = _committed;
        _cue.Text = "点一下可改";
    }

    public string Text
    {
        get => _committed;
        set
        {
            _committed = value;
            _text.Text = value;
            _cue.Text = _capturing ? "按下组合键…（Esc 取消）" : "点一下可改";
        }
    }

    private void BeginCapture()
    {
        _capturing = true;
        BorderBrush = Theme.Brush(SettingsTheme.Accent);
        _cue.Text = "按下组合键…（Esc 取消）";
    }

    private void EndCapture()
    {
        _capturing = false;
        BorderBrush = Theme.Brush(SettingsTheme.ControlBorder);
        _text.Text = _committed;
        _cue.Text = "点一下可改";
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            if (e.Key is Key.Enter or Key.Space) BeginCapture();
            return;
        }

        if (e.Key == Key.Escape)
        {
            EndCapture();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // Alt 按住时 WPF 把按键报成 Key.System，真正的主键在 SystemKey 里，先还原再判修饰键。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            var pending = ModifiersText(Keyboard.Modifiers);
            _text.Text = pending.Length == 0 ? "按下组合键…" : pending + " + …";
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            _text.Text = "至少要带一个修饰键";
            e.Handled = true;
            return;
        }

        var main = KeyToken(key);
        var prefix = ModifiersText(Keyboard.Modifiers);
        if (main == null || prefix.Length == 0)
        {
            _text.Text = "这个键不支持，换一个";
            e.Handled = true;
            return;
        }

        Text = $"{prefix}+{main}";
        Keyboard.ClearFocus();
        ChordChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    internal static string ModifiersText(ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    internal static string? KeyToken(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "space",
            Key.Enter or Key.Return => "enter",
            Key.Tab => "tab",
            Key.PrintScreen => "printscreen",
            Key.Insert => "insert",
            Key.Delete => "delete",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            Key.Left => "left",
            Key.Up => "up",
            Key.Right => "right",
            Key.Down => "down",
            _ => null
        };
    }
}

/// <summary>平铺档位：一档只管一件事，选中的蓝底白字，鼠标停上去说这档是什么。</summary>
internal sealed class PillGroup<T>
{
    private readonly List<(T Value, Button Button)> _items = [];

    public PillGroup(T[] values, string[] labels, string[] tips)
    {
        Host = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < values.Length; i++)
        {
            var btn = new Button
            {
                Content = labels[i],
                Height = 28,
                MinWidth = 48,
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12.5,
                Cursor = Cursors.Hand,
                ToolTip = tips[i],
                Style = PillTemplate()
            };
            T captured = values[i];
            btn.Click += (_, _) => { Select(captured); Changed?.Invoke(); };
            btn.MouseEnter += (_, _) => Look(btn, IsSelected(captured), hovered: true);
            btn.MouseLeave += (_, _) => Look(btn, IsSelected(captured), hovered: false);
            _items.Add((values[i], btn));
            Host.Children.Add(btn);
        }
    }

    public StackPanel Host { get; }
    public T Selected { get; private set; } = default!;
    public Action? Changed;

    public void Select(T value)
    {
        Selected = value;
        foreach (var (item, btn) in _items)
            Look(btn, EqualityComparer<T>.Default.Equals(item, value), hovered: false);
    }

    private bool IsSelected(T value) => EqualityComparer<T>.Default.Equals(value, Selected);

    private static void Look(Button btn, bool selected, bool hovered)
    {
        if (selected)
        {
            btn.Background = Theme.Brush(hovered ? SettingsTheme.AccentHover : SettingsTheme.Accent);
            btn.BorderBrush = Theme.Brush(SettingsTheme.Accent);
            btn.Foreground = Theme.Brush(Colors.White);
        }
        else
        {
            btn.Background = Theme.Brush(hovered ? SettingsTheme.ControlHover : Colors.White);
            btn.BorderBrush = Theme.Brush(SettingsTheme.ControlBorder);
            btn.Foreground = Theme.Brush(SettingsTheme.TextPrimary);
        }
    }

    private static Style PillTemplate()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);
        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
