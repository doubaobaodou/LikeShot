using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LikeShot.Capture;
using LikeShot.Hotkeys;

namespace LikeShot.UI;

/// <summary>
/// 设置窗口。热键是「点一下把组合键按出来」，其余项按用途分组，
/// 底部「应用」一次改完立刻生效——热键这种东西改完就是要马上试一下的。
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly Color Surface = Color.FromRgb(0x1F, 0x23, 0x29);
    private static readonly Color Card = Color.FromRgb(0x2B, 0x2F, 0x36);
    private static readonly Color Field = Color.FromRgb(0x17, 0x1A, 0x1F);
    private static readonly Color Warn = Color.FromRgb(0xF7, 0xB2, 0x3A);
    private static readonly Color Bad = Color.FromRgb(0xFB, 0x78, 0x78);
    private static readonly Color Good = Color.FromRgb(0x7A, 0xC9, 0x7A);
    private static readonly Color Muted = Theme.LoupeMuted;

    private readonly AppConfig _config;
    private readonly ChordBox _chord = new();
    private readonly TextBox _yieldNames = new();
    private readonly TextBox _saveDir = new();
    private readonly TextBlock _fileName = new();
    private readonly TextBlock _chordHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _magnifier = Check("截图时显示放大镜");
    private readonly CheckBox _copyAfterSave = Check("保存文件后同时复制到剪贴板");
    private readonly CheckBox _autoStart = Check("开机自动启动 LikeShot");
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Muted) };
    private readonly PillGroup<string> _format = new(
        ["png", "jpg", "bmp"], ["PNG", "JPG", "BMP"], ["存成无损 PNG", "体积小一些，适合照片", "兼容性最好，体积最大"]);
    private readonly PillGroup<int> _poll = new(
        [120, 220, 350], ["快", "标准", "慢"], ["每 0.12 秒抓一帧", "每 0.22 秒抓一帧", "每 0.35 秒抓一帧"]);
    private readonly PillGroup<int> _notches = new(
        [1, 2, 3], ["1 格", "2 格", "3 格"], ["每帧推 1 格滚轮", "每帧推 2 格滚轮", "每帧推 3 格滚轮"]);

    public SettingsWindow(AppConfig config)
    {
        _config = config;

        Title = "LikeShot 设置";
        Width = 560;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Background = Theme.Brush(Surface);
        Foreground = Theme.Brush(Theme.IconBrush);
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 0) };

        body.Children.Add(Header("截图热键"));
        body.Children.Add(FieldWithButton(_chord, "恢复默认", () =>
        {
            _chord.Text = "Ctrl+Alt+A";
            RefreshChordHint();
            MarkDirty();
        }));
        body.Children.Add(_chordHint);
        _chord.ChordChanged += (_, _) => { RefreshChordHint(); MarkDirty(); };

        body.Children.Add(Header("让位名单"));
        body.Children.Add(FieldBox(_yieldNames, "进程名之间用逗号分开，例如 QQ, Tim"));
        body.Children.Add(Hint("进程名（不带 .exe），逗号分隔。名单里的进程只要在跑，上面的热键就先让给它。"));
        _yieldNames.TextChanged += (_, _) => { RefreshChordHint(); MarkDirty(); };

        body.Children.Add(Header("保存"));
        body.Children.Add(FieldWithButton(FieldBox(_saveDir, "截图存到哪个目录"), "浏览…", PickFolder));
        _saveDir.TextChanged += (_, _) => { RefreshFileName(); MarkDirty(); };
        body.Children.Add(LabelRow("格式", _format.Host));
        _format.Changed += () => { RefreshFileName(); MarkDirty(); };
        body.Children.Add(_fileName);

        body.Children.Add(Header("截图时"));
        body.Children.Add(_magnifier);
        body.Children.Add(_copyAfterSave);
        _magnifier.Checked += (_, _) => MarkDirty();
        _magnifier.Unchecked += (_, _) => MarkDirty();
        _copyAfterSave.Checked += (_, _) => MarkDirty();
        _copyAfterSave.Unchecked += (_, _) => MarkDirty();

        body.Children.Add(Header("长截图"));
        body.Children.Add(LabelRow("抓取间隔", _poll.Host));
        body.Children.Add(LabelRow("每次滚动", _notches.Host));
        _poll.Changed += MarkDirty;
        _notches.Changed += MarkDirty;
        body.Children.Add(Hint("自动滚动时每抓一帧推一次滚轮。页面滚得快就间隔短一点、每次多滚几格；两个旋钮各管一件事。"));

        body.Children.Add(Header("开机"));
        body.Children.Add(_autoStart);
        _autoStart.Checked += (_, _) => MarkDirty();
        _autoStart.Unchecked += (_, _) => MarkDirty();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(18, 12, 18, 14)
        };
        var apply = FlatButton("应用", primary: true);
        apply.Click += (_, _) => Apply();
        var close = FlatButton("关闭");
        close.Click += (_, _) => Close();
        footer.Children.Add(_status);
        footer.Children.Add(new FrameworkElement { Width = 8 });
        footer.Children.Add(apply);
        footer.Children.Add(new FrameworkElement { Width = 6 });
        footer.Children.Add(close);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = root;

        LoadFrom(config);
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

        var dir = _saveDir.Text.Trim();
        if (dir.Length > 0) _config.SaveDirectory = dir;

        bool wantAutoStart = _autoStart.IsChecked == true;
        _config.RunOnStartup = wantAutoStart && Shell.AutoStart.Set(wantAutoStart);
        _autoStart.IsChecked = _config.RunOnStartup;

        AppConfig.Save(_config);
        if (Application.Current is App app) app.ReloadHotkey();
        Log.Write($"设置已应用 热键={_config.CaptureChord} 让位={string.Join(",", _config.YieldToProcesses)} " +
                  $"格式={_config.SaveFormat} 间隔={_config.LongCapturePollMilliseconds}ms 每帧{_config.LongCaptureScrollNotches}格 " +
                  $"放大镜={_config.ShowMagnifier} 存后复制={_config.CopyAfterSave} 自启={_config.RunOnStartup}");

        RefreshChordHint();
        RefreshFileName();
        _status.Text = $"已应用 {DateTime.Now:HH:mm:ss}";
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
        _fileName.Foreground = Theme.Brush(Muted);
        _fileName.FontSize = 11.5;
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
        _chordHint.Foreground = Theme.Brush(attention ? Warn : Good);
    }

    private void ShowChordError(string message)
    {
        _chordHint.Text = message;
        _chordHint.Foreground = Theme.Brush(Bad);
    }

    private void MarkDirty() => _status.Text = "有改动没应用";

    private static List<string> SplitNames(string text) =>
        text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- 小控件 ----

    private static CheckBox Check(string text) => new()
    {
        Content = text,
        Foreground = Theme.Brush(Theme.IconBrush),
        Margin = new Thickness(0, 5, 0, 0),
        FontSize = 12.5
    };

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Theme.Brush(Theme.Accent),
        Margin = new Thickness(0, 16, 0, 6)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = Theme.Brush(Muted),
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBox FieldBox(TextBox box, string tooltip)
    {
        box.Background = Theme.Brush(Field);
        box.Foreground = Theme.Brush(Theme.IconBrush);
        box.CaretBrush = Theme.Brush(Theme.IconBrush);
        box.BorderBrush = Theme.Brush(Theme.BarBorder);
        box.BorderThickness = new Thickness(1);
        box.Padding = new Thickness(7, 5, 7, 5);
        box.Margin = new Thickness(0, 2, 0, 0);
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTipService.SetToolTip(box, tooltip);
        return box;
    }

    private static StackPanel LabelRow(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 66, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    /// <summary>输入框占满一行，小按钮贴在它右边同一行——按钮是这行的动作，不该另起一行。</summary>
    private DockPanel FieldWithButton(UIElement field, string buttonLabel, Action onClick)
    {
        var btn = FlatButton(buttonLabel);
        btn.Click += (_, _) => onClick();
        btn.Margin = new Thickness(6, 0, 0, 0);
        if (field is FrameworkElement element) element.Margin = new Thickness(0);

        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(field);
        return dock;
    }

    private Button FlatButton(string text, bool primary = false) => new()
    {
        Content = text,
        Padding = new Thickness(12, 5, 12, 5),
        MinWidth = 68,
        FontSize = 12.5,
        Foreground = Theme.Brush(primary ? Color.FromRgb(0x12, 0x16, 0x1C) : Theme.IconBrush),
        Background = Theme.Brush(primary ? Theme.Accent : Card),
        BorderThickness = new Thickness(0),
        Style = FlatStyle()
    };

    private static Style FlatStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Brush(Theme.ButtonHover)));
        style.Triggers.Add(hover);
        return style;
    }
}

/// <summary>「点一下把组合键按出来」的输入框。只收带修饰键的组合，Esc 取消。</summary>
internal sealed class ChordBox : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Foreground = Theme.Brush(Theme.IconBrush) };
    private readonly TextBlock _cue = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11.5, Foreground = Theme.Brush(Color.FromRgb(0x9A, 0xA3, 0xAF)) };
    private string _committed = "Ctrl+Alt+A";
    private bool _capturing;

    public event EventHandler? ChordChanged;

    public ChordBox()
    {
        Background = Theme.Brush(Color.FromRgb(0x17, 0x1A, 0x1F));
        BorderBrush = Theme.Brush(Theme.BarBorder);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Height = 34;
        Focusable = true;
        Cursor = Cursors.Hand;

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
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
        _cue.Text = "按下组合键…（Esc 取消）";
    }

    private void EndCapture()
    {
        _capturing = false;
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

/// <summary>平铺档位：一档只管一件事，选中的高亮，鼠标停上去说这档是什么。</summary>
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
                Padding = new Thickness(11, 4, 11, 4),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12.5,
                Foreground = Theme.Brush(Color.FromRgb(0x12, 0x16, 0x1C)),
                BorderThickness = new Thickness(0),
                ToolTip = tips[i],
                Style = PillStyle()
            };
            T captured = values[i];
            btn.Click += (_, _) => { Select(captured); Changed?.Invoke(); };
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
            btn.Background = Theme.Brush(EqualityComparer<T>.Default.Equals(item, value) ? Theme.Accent : Color.FromRgb(0x3D, 0x42, 0x4B));
    }

    private static Style PillStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Brush(Theme.ButtonHover)));
        style.Triggers.Add(hover);
        return style;
    }
}
