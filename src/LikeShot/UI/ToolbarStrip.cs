using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using LikeShot.Capture;
using LikeShot.Drawing;

namespace LikeShot.UI;

public enum ToolbarMode { Standard, LongCapture, Pin }

/// <summary>
/// QQ 的那一条工具栏。按钮顺序、提示文案按 QQScreenShot 的习惯来：
/// 主条上形状/文字/马赛克带下拉小箭头，选中工具后上方展开二级工具条——
/// 形状条里有 矩形/椭圆/直线 变体、空心/实心、粗细滑条和颜色下拉；
/// 其余画具至少给 粗细滑条 + 颜色。色板收在颜色小方块的下拉里，不再常驻。
/// </summary>
public sealed class ToolbarStrip : Border
{
    private readonly CaptureSession _session;
    private readonly ToolbarMode _mode;
    private readonly StackPanel _row;
    private readonly Border _subHost;
    private readonly StackPanel _subStack;
    private readonly Border _statusHost;
    private readonly TextBlock _statusText;
    private readonly Button _statusButton;
    private readonly Dictionary<ToolKind, Button> _toolButtons = new();
    private readonly Dictionary<ToolKind, FrameworkElement> _subPanels = new();

    // 各面板里的活控件，SyncState 要跟着会话状态刷。
    // 粗细滑条和颜色小方块在好几个面板里各有一份，全部收进列表统一刷新，
    // 只记最后一个的话，切了工具屏幕上就是陈旧值。
    private readonly Dictionary<ShapeKind, Button> _shapeButtons = new();
    private Button? _outlineButton;
    private Button? _filledButton;
    private readonly List<Slider> _widthSliders = [];
    private readonly List<TextBlock> _widthValues = [];
    private readonly List<Slider> _fontSliders = [];
    private readonly List<TextBlock> _fontValues = [];
    private Button? _pixelButton;
    private Button? _blurButton;
    private Slider? _mosaicSlider;
    private readonly List<Border> _colorSwatches = [];
    private readonly List<(Color Color, Button Button)> _colorButtons = [];
    private StackPanel _colorStrip = new();
    private Button? _rainbowButton;
    private ColorPickerPanel? _picker;
    private bool _colorStripOpen;
    private ToolKind _lastTool = ToolKind.None;

    /// <summary>吸管按钮按下去之后由遮罩层接管下一次点击。</summary>
    public event Action? ColorPickRequested;

    public ToolbarStrip(CaptureSession session, Display display, ToolbarMode mode = ToolbarMode.Standard)
    {
        _session = session;
        _mode = mode;

        Background = Theme.Brush(Theme.BarBackground);
        BorderBrush = Theme.Brush(Theme.BarBorder);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(Theme.BarPadding, 4, Theme.BarPadding - 2, 4);
        SnapsToDevicePixels = true;

        _row = new StackPanel { Orientation = Orientation.Horizontal };

        // 二级工具条 + 色板贴在主条下方展开（QQ 是往下弹的），同一条带里，不用 Popup：
        // Popup 在多层 topmost 窗口之间容易被截图层自己的 z-order 压住。
        _subStack = new StackPanel { Orientation = Orientation.Vertical };
        _subHost = new Border
        {
            Background = Theme.Brush(Theme.BarBackground),
            CornerRadius = new CornerRadius(0, 0, 4, 4),
            Padding = new Thickness(2, 2, 2, 4),
            Margin = new Thickness(-Theme.BarPadding + 2, 0, -Theme.BarPadding + 2, -4),
            Visibility = Visibility.Collapsed,
            Child = _subStack
        };

        _statusText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.LoupeText),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            TextWrapping = TextWrapping.Wrap
        };
        _statusButton = new Button
        {
            Height = 22,
            Padding = new Thickness(9, 1, 9, 1),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11.5,
            Focusable = false,
            Style = ButtonStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        // 挂一次固定的转发，换文案时只改 _statusAction，不用反复摘装事件。
        _statusButton.Click += (_, _) => _statusAction?.Invoke();
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(_statusText);
        statusRow.Children.Add(_statusButton);
        _statusHost = new Border
        {
            Background = Theme.Brush(Theme.BarBackground),
            BorderBrush = Theme.Brush(Theme.BarBorder),
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 4, 4),
            Padding = new Thickness(9, 4, 7, 4),
            Visibility = Visibility.Collapsed,
            Child = statusRow
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_row);
        stack.Children.Add(_subHost);
        stack.Children.Add(_statusHost);
        Child = stack;

        switch (mode)
        {
            case ToolbarMode.Standard:
                AddTool(ToolKind.Shape, "shape", "形状工具", chevron: true);
                AddTool(ToolKind.Arrow, "arrow", "箭头工具");
                AddTool(ToolKind.Brush, "brush", "画刷工具");
                AddTool(ToolKind.Mosaic, "mosaic", "马赛克工具", chevron: true);
                AddTool(ToolKind.Text, "text", "文字工具", chevron: true);
                AddTool(ToolKind.Marker, "marker", "序号笔工具");
                AddAction("undo", "撤销编辑 Ctrl+Z", () => _session.Undo());
                AddAction("save", "保存 Ctrl+S", () => RequestFinish(CopyMode.File));
                AddAction("long", "长截图", () => _session.EnterLongMode());
                AddAction("nail", "钉在桌面上", () => _session.Finish(CopyMode.Pin));
                AddSeparator();
                AddAction("exit", "退出截图", () => _session.Exit(), stroke: Theme.Brush(Theme.DangerRed));
                AddFinish();
                break;

            case ToolbarMode.Pin:
                AddTool(ToolKind.Shape, "shape", "形状工具", chevron: true);
                AddTool(ToolKind.Arrow, "arrow", "箭头工具");
                AddTool(ToolKind.Brush, "brush", "画刷工具");
                AddTool(ToolKind.Mosaic, "mosaic", "马赛克工具", chevron: true);
                AddTool(ToolKind.Text, "text", "文字工具", chevron: true);
                AddTool(ToolKind.Marker, "marker", "序号笔工具");
                AddAction("undo", "撤销编辑 Ctrl+Z", () => _session.Undo());
                AddAction("save", "保存 Ctrl+S", () => RequestFinish(CopyMode.File));
                AddSeparator();
                AddAction("exit", "关闭钉图", () => _session.Exit(), stroke: Theme.Brush(Theme.DangerRed));
                AddFinish();
                break;

            case ToolbarMode.LongCapture:
                _directionVertical = AddToggle("vertical", "纵向长截图", () => SetDirection(false));
                _directionHorizontal = AddToggle("horizontal", "横向长截图", () => SetDirection(true));
                _autoScroll = AddToggle("scroll", "自动滚动", () => AutoScrollToggled?.Invoke());
                AddSeparator();
                AddAction("edit", "编辑工具", () => EditRequested?.Invoke());
                AddAction("save", "保存 Ctrl+S", () => RequestFinish(CopyMode.File));
                AddAction("exit", "退出截图", () => _session.Exit(), stroke: Theme.Brush(Theme.DangerRed));
                AddFinish();
                break;
        }

        BuildSubPanels();
        _session.Changed += SyncState;
        Loaded += (_, _) => SyncState();
    }

    public event Action<bool>? DirectionChanged;
    public event Action? AutoScrollToggled;
    public event Action? EditRequested;

    /// <summary>长截图/钉图模式下接管「完成 / 保存」，产出各自的成品图。</summary>
    public Func<CopyMode, bool>? FinishInterceptor { get; set; }

    public bool IsHorizontal { get; private set; }

    private void RequestFinish(CopyMode mode)
    {
        if (FinishInterceptor?.Invoke(mode) == true) return;
        _session.Finish(mode);
    }

    private ToggleButton? _directionVertical;
    private ToggleButton? _directionHorizontal;
    private ToggleButton? _autoScroll;
    private bool _autoScrollOn;

    /// <summary>无头验证与日志用：二级条当前是否展开。</summary>
    internal Visibility SubHostVisibility => _subHost.Visibility;

    /// <summary>无头验证与日志用：状态行的文字，空字符串表示没在报任何东西。</summary>
    internal string StatusTextForTest => _statusText.Text ?? "";

    /// <summary>状态行会改变整条工具栏的高度，宿主（遮罩层/钉图窗）要重新定位。</summary>
    public Action? RepositionRequested;

    /// <summary>
    /// 在工具栏下面挂一句回执。bad=true 用红字，actionLabel 非空时在句子右边给一个补救按钮——
    /// 「失败了」必须同时回答「下一步点哪里」，只留一句错误没意义。
    /// </summary>
    public void ShowStatus(string text, bool bad, string? actionLabel = null, Action? action = null)
    {
        _statusText.Text = text;
        _statusText.Foreground = Theme.Brush(bad ? Theme.DangerRed : Theme.LoupeText);
        _statusAction = action;
        if (actionLabel != null && action != null)
        {
            _statusButton.Content = actionLabel;
            _statusButton.Visibility = Visibility.Visible;
        }
        else _statusButton.Visibility = Visibility.Collapsed;
        _statusHost.Visibility = Visibility.Visible;
        InvalidateMeasure();
        RepositionRequested?.Invoke();
    }

    private Action? _statusAction;

    public void ClearStatus()
    {
        _statusAction = null;
        _statusText.Text = "";
        _statusButton.Visibility = Visibility.Collapsed;
        _statusHost.Visibility = Visibility.Collapsed;
        InvalidateMeasure();
        RepositionRequested?.Invoke();
    }

    // 尺寸每次都现量：二级条刚展开的那一下 ActualHeight 还是旧值，
    // 拿它定位会让工具条看起来晚一拍才长高。
    public double NaturalWidth => FreshMeasure().Width;
    public double NaturalHeight => FreshMeasure().Height;

    private Size FreshMeasure()
    {
        // 二级条 Visibility 刚翻转完就量的话，Measure 会直接吐缓存的旧 DesiredSize，
        // 先强制失效再量才能拿到展开后的真实尺寸。
        InvalidateMeasure();
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return DesiredSize;
    }

    // ---------- 主条 ----------

    private Button MakeButton(string iconKey, string tooltip, bool chevron = false, Brush? stroke = null)
    {
        var canvas = new Canvas { Width = Theme.IconSize, Height = Theme.IconSize };
        var strokeBrush = stroke ?? Theme.Brush(Theme.IconBrush);
        if (Icons.FillPaths.TryGetValue(iconKey, out var fillData))
        {
            var fill = new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(fillData),
                Fill = strokeBrush,
                Opacity = iconKey == "mosaic" ? 0.85 : 1
            };
            canvas.Children.Add(fill);
        }
        if (Icons.StrokePaths.TryGetValue(iconKey, out var data))
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(data),
                Stroke = strokeBrush,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            canvas.Children.Add(path);
        }
        FrameworkElement content = canvas;
        if (chevron)
        {
            // QQ 的下拉小箭头：挂在按钮自己的右下角，别压住主图标的笔画。
            var grid = new Grid();
            grid.Children.Add(canvas);
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L6.4,0 L3.2,4.2 Z"),
                Fill = Theme.Brush(Theme.LoupeMuted),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 4)
            });
            content = grid;
        }

        var btn = new Button
        {
            Content = content,
            Width = Theme.ButtonSize,
            Height = Theme.ButtonSize,
            Margin = new Thickness(0, 0, Theme.ButtonGap, 0),
            Focusable = false,
            ToolTip = tooltip,
            Style = ButtonStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, tooltip);
        return btn;
    }

    private static Style ButtonStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, Theme.Brush(Theme.ButtonHover)));
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(BackgroundProperty, Theme.Brush(Theme.ButtonPushed)));
        style.Triggers.Add(hover);
        style.Triggers.Add(pressed);
        return style;
    }

    private void AddTool(ToolKind tool, string icon, string tooltip, bool chevron = false)
    {
        var btn = MakeButton(icon, tooltip, chevron);
        btn.Click += (_, _) => _session.SetTool(tool);
        _toolButtons[tool] = btn;
        _row.Children.Add(btn);
    }

    private void AddAction(string icon, string tooltip, Action action, Brush? stroke = null)
    {
        var btn = MakeButton(icon, tooltip, stroke: stroke);
        btn.Click += (_, _) => action();
        _row.Children.Add(btn);
    }

    private ToggleButton AddToggle(string icon, string tooltip, Action onClick)
    {
        var tb = new ToggleButton
        {
            Content = MakeButton(icon, tooltip).Content,
            Width = Theme.ButtonSize,
            Height = Theme.ButtonSize,
            Margin = new Thickness(0, 0, Theme.ButtonGap, 0),
            ToolTip = tooltip,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false
        };
        tb.Checked += (_, _) => onClick();
        tb.Unchecked += (_, _) => onClick();
        _row.Children.Add(tb);
        return tb;
    }

    /// <summary>完成按钮：QQ 是一枚绿色对勾，不再是白底文字。</summary>
    private void AddFinish()
    {
        var btn = MakeButton("finish", "完成截图 Enter", stroke: Theme.Brush(Theme.SuccessGreen));
        btn.ToolTip = "完成截图 Enter";
        btn.Click += (_, _) => RequestFinish(CopyMode.Clipboard);
        _row.Children.Add(btn);
    }

    private void AddSeparator()
    {
        _row.Children.Add(new Border
        {
            Width = 1,
            Height = 18,
            Background = Theme.Brush(Theme.BarBorder),
            Margin = new Thickness(0, 6, Theme.ButtonGap, 6)
        });
    }

    private void SetDirection(bool horizontal)
    {
        IsHorizontal = horizontal;
        if (_directionVertical != null) _directionVertical.IsChecked = !horizontal;
        if (_directionHorizontal != null) _directionHorizontal.IsChecked = horizontal;
        DirectionChanged?.Invoke(horizontal);
    }

    // ---------- 二级工具条 ----------

    private void BuildSubPanels()
    {
        if (_mode == ToolbarMode.LongCapture) return;

        _subPanels[ToolKind.Shape] = BuildShapePanel();
        _subPanels[ToolKind.Arrow] = BuildWidthPanel();
        _subPanels[ToolKind.Brush] = BuildWidthPanel();
        _subPanels[ToolKind.Marker] = BuildColorOnlyPanel();   // 序号牌尺寸固定，粗细滑条对它没意义
        _subPanels[ToolKind.Text] = BuildTextPanel();
        _subPanels[ToolKind.Mosaic] = BuildMosaicPanel();

        foreach (var panel in _subPanels.Values)
        {
            panel.Visibility = Visibility.Collapsed;
            _subStack.Children.Add(panel);
        }

        _colorStrip = BuildColorStrip();
        _colorStrip.Visibility = Visibility.Collapsed;
        _subStack.Children.Add(_colorStrip);
    }

    private static FrameworkElement SubSeparator() => new Border
    {
        Width = 1,
        Height = 16,
        Background = Theme.Brush(Theme.BarBorder),
        Margin = new Thickness(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>形状条：变体三档 + 空心/实心 + 粗细滑条 + 颜色下拉，一条排开。</summary>
    private FrameworkElement BuildShapePanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        void AddShape(ShapeKind kind, string icon, string tip)
        {
            var btn = SubButton(MakeSubIcon(icon));
            btn.ToolTip = tip;
            btn.Click += (_, _) => _session.SetShapeKind(kind);
            _shapeButtons[kind] = btn;
            row.Children.Add(btn);
        }
        AddShape(ShapeKind.Rect, "shape", "矩形");
        AddShape(ShapeKind.Ellipse, "ellipse", "椭圆");
        AddShape(ShapeKind.Line, "line", "直线");
        row.Children.Add(SubSeparator());

        _outlineButton = SubButton(MakeSubIcon("dot"));
        _outlineButton.ToolTip = "空心";
        _outlineButton.Click += (_, _) => _session.SetShapeFilled(false);
        row.Children.Add(_outlineButton);
        _filledButton = SubButton(MakeSubIcon("filled"));
        _filledButton.ToolTip = "实心";
        _filledButton.Click += (_, _) => _session.SetShapeFilled(true);
        row.Children.Add(_filledButton);
        row.Children.Add(SubSeparator());

        row.Children.Add(WidthSlider());
        row.Children.Add(SubSeparator());
        row.Children.Add(ColorButton());
        return WrapPanel(row);
    }

    /// <summary>箭头/画笔共用：粗细滑条 + 颜色下拉。</summary>
    private FrameworkElement BuildWidthPanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(WidthSlider());
        row.Children.Add(SubSeparator());
        row.Children.Add(ColorButton());
        return WrapPanel(row);
    }

    /// <summary>只有颜色可调的画具（序号笔）。</summary>
    private FrameworkElement BuildColorOnlyPanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "颜色",
            FontSize = 11,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        row.Children.Add(ColorButton());
        return WrapPanel(row);
    }

    private FrameworkElement BuildTextPanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(FontSlider());
        row.Children.Add(SubSeparator());
        row.Children.Add(ColorButton());
        return WrapPanel(row);
    }

    /// <summary>
    /// 字号滑条：8–72（96dpi 值）连续可调，右边跟着当前数值。
    /// 以前只有小/中/大三档，想要「比中号大一点」只能调不出来；现在拖到哪就是哪，
    /// 选中一条已放好的文字时，拖它改的就是那条文字自己的字号。
    /// </summary>
    private FrameworkElement FontSlider()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "字号",
            FontSize = 11,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        var slider = new Slider
        {
            Minimum = 8,
            Maximum = 72,
            Value = _session.FontDp,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            ToolTip = "文字大小",
            Style = SliderStyle()
        };
        slider.ValueChanged += (_, e) =>
            _session.SetFontDp((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero));
        _fontSliders.Add(slider);
        row.Children.Add(slider);
        var value = new TextBlock
        {
            Text = _session.FontDp.ToString(),
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.IconBrush),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 16,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        _fontValues.Add(value);
        row.Children.Add(value);
        return row;
    }

    private FrameworkElement BuildMosaicPanel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        _pixelButton = SubButton(new TextBlock
        {
            Text = "马赛克",
            FontSize = 11,
            Foreground = Theme.Brush(Theme.IconBrush)
        }, autoWidth: true);
        _pixelButton.Click += (_, _) => _session.SetMosaicStyle(MosaicStyle.Pixelate);
        row.Children.Add(_pixelButton);

        _blurButton = SubButton(new TextBlock
        {
            Text = "模糊",
            FontSize = 11,
            Foreground = Theme.Brush(Theme.IconBrush)
        }, autoWidth: true);
        _blurButton.Click += (_, _) => _session.SetMosaicStyle(MosaicStyle.Blur);
        row.Children.Add(_blurButton);

        row.Children.Add(SubSeparator());
        row.Children.Add(new TextBlock
        {
            Text = "强度",
            FontSize = 11,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        _mosaicSlider = new Slider
        {
            Minimum = 2,
            Maximum = 40,
            Value = _session.MosaicStrength,
            Width = 100,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            ToolTip = "模糊度",
            Style = SliderStyle()
        };
        _mosaicSlider.ValueChanged += (_, e) => _session.SetMosaicStrength((int)e.NewValue);
        row.Children.Add(_mosaicSlider);
        return WrapPanel(row);
    }

    /// <summary>粗细滑条：1–20 连续可调，右边跟着当前数值。</summary>
    private FrameworkElement WidthSlider()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "粗细",
            FontSize = 11,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 20,
            Value = _session.WidthDp,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            ToolTip = "线条粗细",
            Style = SliderStyle()
        };
        slider.ValueChanged += (_, e) =>
            _session.SetStrokeWidth((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero));
        _widthSliders.Add(slider);
        row.Children.Add(slider);
        var value = new TextBlock
        {
            Text = _session.WidthDp.ToString(),
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.IconBrush),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 16,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        _widthValues.Add(value);
        row.Children.Add(value);
        return row;
    }

    /// <summary>颜色下拉：当前色的小方块，点开才铺开 16 色色板。每个面板各一份，颜色统一刷。</summary>
    private FrameworkElement ColorButton()
    {
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = Theme.Brush(_session.Color),
            BorderBrush = Theme.Brush(Theme.SwatchBorder),
            BorderThickness = new Thickness(1)
        };
        _colorSwatches.Add(swatch);
        var chev = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L5,0 L2.5,3.4 Z"),
            Fill = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 1, 0, 0)
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatch);
        content.Children.Add(chev);

        var colorButton = new Button
        {
            Content = content,
            Height = 24,
            Padding = new Thickness(5, 0, 5, 0),
            Focusable = false,
            ToolTip = "选择颜色",
            Style = ButtonStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        colorButton.Click += (_, _) =>
        {
            _colorStripOpen = !_colorStripOpen;
            _colorStrip.Visibility = _colorStripOpen ? Visibility.Visible : Visibility.Collapsed;
        };
        return colorButton;
    }

    private StackPanel BuildColorStrip()
    {
        var colors = new WrapPanel { MaxWidth = 17 * 19, Margin = new Thickness(6, 4, 6, 4) };
        foreach (var (name, color) in Theme.Palette)
        {
            var sw = new Button
            {
                Width = 15,
                Height = 15,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Brush(Theme.SwatchBorder),
                ToolTip = $"{name} #{color.R:X2}{color.G:X2}{color.B:X2}",
                Focusable = false,
                Style = SwatchStyle()
            };
            sw.Click += (_, _) => _session.SetColor(color);
            _colorButtons.Add((color, sw));
            colors.Children.Add(sw);
        }

        // 行尾的彩虹块：打开自定义取色板（色域方块 + 色相条 + RGB + 吸管）。
        _rainbowButton = new Button
        {
            Width = 15,
            Height = 15,
            Margin = new Thickness(2),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush(Theme.SwatchBorder),
            ToolTip = "自定义颜色",
            Focusable = false,
            Style = SwatchStyle(),
            Background = new LinearGradientBrush(
                Color.FromRgb(255, 64, 64), Color.FromRgb(64, 160, 255), 30)
            {
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(255, 70, 70), 0),
                    new GradientStop(Color.FromRgb(255, 200, 60), 0.35),
                    new GradientStop(Color.FromRgb(90, 220, 90), 0.65),
                    new GradientStop(Color.FromRgb(80, 140, 255), 1)
                }
            }
        };
        _rainbowButton.Click += (_, _) =>
        {
            if (_picker == null) return;
            _picker.Visibility = _picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (_picker.Visibility == Visibility.Visible) _picker.SyncFromColor(_session.Color);
        };
        colors.Children.Add(_rainbowButton);

        _picker = new ColorPickerPanel(_session);
        _picker.PickRequested += () => ColorPickRequested?.Invoke();
        _picker.Visibility = Visibility.Collapsed;

        return new StackPanel { Orientation = Orientation.Vertical, Children = { colors, _picker } };
    }

    private static FrameworkElement WrapPanel(StackPanel row)
    {
        row.Margin = new Thickness(6, 3, 6, 5);
        return row;
    }

    /// <summary>二级条里的小按钮：图标款 26×26；文字款（马赛克/模糊）宽度跟着字走，写死会把字截掉。</summary>
    private Button SubButton(object content, bool autoWidth = false)
    {
        var btn = new Button
        {
            Content = content,
            Width = autoWidth ? double.NaN : 26,
            Height = 26,
            Padding = autoWidth ? new Thickness(6, 0, 6, 0) : new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Focusable = false,
            Style = ButtonStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        return btn;
    }

    private FrameworkElement MakeSubIcon(string iconKey)
    {
        var canvas = new Canvas { Width = 17, Height = 17 };
        if (Icons.FillPaths.TryGetValue(iconKey, out var fillData))
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(fillData),
                Fill = Theme.Brush(Theme.IconBrush)
            });
        if (Icons.StrokePaths.TryGetValue(iconKey, out var data))
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(data),
                Stroke = Theme.Brush(Theme.IconBrush),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        return canvas;
    }

    private static Style SwatchStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush(Theme.Accent)));
        style.Triggers.Add(hover);
        return style;
    }

    /// <summary>
    /// QQ 那种细滑条：3px 圆角轨道 + 12px 小圆头。默认滑条又粗又方，放不进二级条。
    /// 模板里有 Track/Thumb 嵌套，用工厂树拼太绕，改按当前主题把色值插进 XAML 生成。
    /// </summary>
    internal static Style SliderStyle()
    {
        string track = Theme.LightUi ? "#FFDFDFDF" : "#FF4A5158";
        string thumb = Theme.LightUi ? "#FFFFFFFF" : "#FFE8EAED";
        string thumbStroke = Theme.LightUi ? "#FFC9C9C9" : "#66000000";
        string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="Slider">
                <Setter Property="Focusable" Value="False"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Slider">
                            <Grid Height="18" VerticalAlignment="Center">
                                <Border Height="3" CornerRadius="1.5" Background="{ThemeKey}" VerticalAlignment="Center"/>
                                <Track x:Name="PART_Track">
                                    <Track.DecreaseRepeatButton>
                                        <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                                            <RepeatButton.Template>
                                                <ControlTemplate TargetType="RepeatButton">
                                                    <Border Background="Transparent" Height="18"/>
                                                </ControlTemplate>
                                            </RepeatButton.Template>
                                        </RepeatButton>
                                    </Track.DecreaseRepeatButton>
                                    <Track.IncreaseRepeatButton>
                                        <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                                            <RepeatButton.Template>
                                                <ControlTemplate TargetType="RepeatButton">
                                                    <Border Background="Transparent" Height="18"/>
                                                </ControlTemplate>
                                            </RepeatButton.Template>
                                        </RepeatButton>
                                    </Track.IncreaseRepeatButton>
                                    <Track.Thumb>
                                        <Thumb Width="12" Height="12" Focusable="False">
                                            <Thumb.Template>
                                                <ControlTemplate TargetType="Thumb">
                                                    <Ellipse Fill="{ThumbKey}" Stroke="{ThumbStrokeKey}" StrokeThickness="1"/>
                                                </ControlTemplate>
                                            </Thumb.Template>
                                        </Thumb>
                                    </Track.Thumb>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)XamlReader.Parse(xaml
            .Replace("{ThemeKey}", track)
            .Replace("{ThumbKey}", thumb)
            .Replace("{ThumbStrokeKey}", thumbStroke));
    }

    // ---------- 状态同步 ----------

    private void SyncState()
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var (tool, btn) in _toolButtons)
                btn.Background = tool == _session.Tool ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;

            // 二级条认的是「现在在调哪一类」：工具激活时是工具，
            // 没激活工具但选中了已画内容时，显示那一类的属性面板，不然选中了却没地方改。
            ToolKind panelTool = _session.Tool != ToolKind.None ? _session.Tool : _session.PanelTool ?? ToolKind.None;
            bool showSub = _mode != ToolbarMode.LongCapture && panelTool != ToolKind.None;
            _subHost.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;

            // 换面板（含切回「无」）时把颜色下拉和取色板收起来，别让上一支工具的色板挂着。
            if (panelTool != _lastTool)
            {
                if (_colorStripOpen)
                {
                    _colorStripOpen = false;
                    _colorStrip.Visibility = Visibility.Collapsed;
                }
                if (_picker != null && _picker.Visibility == Visibility.Visible)
                    _picker.Visibility = Visibility.Collapsed;
                _lastTool = panelTool;
            }

            if (showSub)
            {
                foreach (var (tool, panel) in _subPanels)
                    panel.Visibility = tool == panelTool ? Visibility.Visible : Visibility.Collapsed;
            }

            foreach (var (kind, btn) in _shapeButtons)
                btn.Background = kind == _session.ShapeCurrent ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;
            bool linePicked = _session.ShapeCurrent == ShapeKind.Line;
            if (_outlineButton != null)
            {
                // 直线没有空心实心可言，把这两档藏起来，免得点了没反应。
                _outlineButton.Visibility = linePicked ? Visibility.Collapsed : Visibility.Visible;
                _outlineButton.Background = !_session.ShapeFilled && !linePicked ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;
            }
            if (_filledButton != null)
            {
                _filledButton.Visibility = linePicked ? Visibility.Collapsed : Visibility.Visible;
                _filledButton.Background = _session.ShapeFilled && !linePicked ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;
            }

            foreach (var slider in _widthSliders)
            {
                // 拖动中 session 的取整值和滑条小数位置本来就对不上，
                // 只有四舍五入后还不同（别的路径改了粗细）才回写，不然滑块会跟手打架。
                if ((int)Math.Round(slider.Value, MidpointRounding.AwayFromZero) != _session.WidthDp)
                    slider.Value = _session.WidthDp;
            }
            foreach (var value in _widthValues)
                value.Text = _session.WidthDp.ToString();

            foreach (var slider in _fontSliders)
            {
                // 和粗细滑条同一套：只有取整后还对不上（别的路径改了字号，比如拖角缩放）才回写，
                // 不然手正拖着滑块会被自己写回去的值顶一下。
                if ((int)Math.Round(slider.Value, MidpointRounding.AwayFromZero) != _session.FontDp)
                    slider.Value = _session.FontDp;
            }
            foreach (var value in _fontValues)
                value.Text = _session.FontDp.ToString();

            if (_pixelButton != null)
                _pixelButton.Background = _session.MosaicKind == MosaicStyle.Pixelate ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;
            if (_blurButton != null)
                _blurButton.Background = _session.MosaicKind == MosaicStyle.Blur ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;
            if (_mosaicSlider != null && _session.MosaicStrength != (int)_mosaicSlider.Value)
                _mosaicSlider.Value = _session.MosaicStrength;

            foreach (var swatch in _colorSwatches)
            {
                swatch.Background = Theme.Brush(_session.Color);
                swatch.BorderBrush = Theme.Brush(Theme.SwatchBorder);
            }
            foreach (var (color, btn) in _colorButtons)
                btn.BorderBrush = color == _session.Color ? Theme.Brush(Theme.Accent) : Theme.Brush(Theme.SwatchBorder);
            _picker?.SyncFromColor(_session.Color);

            if (_mode == ToolbarMode.LongCapture)
            {
                if (_directionVertical != null) _directionVertical.IsChecked = !IsHorizontal;
                if (_directionHorizontal != null) _directionHorizontal.IsChecked = IsHorizontal;
                if (_autoScroll != null) _autoScroll.IsChecked = _autoScrollOn;
            }
        });
    }

    public void ToggleAutoScrollOn() => _autoScrollOn = true;
    public void ToggleAutoScrollOff() => _autoScrollOn = false;
}
