using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using LikeShot.Capture;
using LikeShot.Drawing;

namespace LikeShot.UI;

public enum ToolbarMode { Standard, LongCapture, Pin }

/// <summary>
/// QQ 的那一条工具栏。按钮顺序、提示文案、二级面板都按 QQScreenShot.rdb 里的
/// 节点顺序和 StringBundle 原文来，不自创、不重排。
/// </summary>
public sealed class ToolbarStrip : Border
{
    private readonly CaptureSession _session;
    private readonly ToolbarMode _mode;
    private readonly StackPanel _row;
    private readonly Border _paletteHost;
    private readonly Dictionary<ToolKind, Button> _toolButtons = new();
    private Slider? _mosaicSlider;
    private StackPanel? _mosaicGroup;

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

        // 二级调色板贴在工具栏上方，同一条深色带里，不用 Popup：
        // Popup 在多层 topmost 窗口之间容易被截图层自己的 z-order 压住。
        _paletteHost = new Border
        {
            Background = Theme.Brush(Theme.BarBackground),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(2, 4, 2, 2),
            Margin = new Thickness(-Theme.BarPadding + 2, -4, -Theme.BarPadding + 2, 4),
            Visibility = Visibility.Collapsed,
            Child = BuildPalettePanel()
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_paletteHost);
        stack.Children.Add(_row);
        Child = stack;

        switch (mode)
        {
            case ToolbarMode.Standard:
                AddTool(ToolKind.Rect, "rect", "矩形工具");
                AddTool(ToolKind.Ellipse, "ellipse", "椭圆工具");
                AddTool(ToolKind.Arrow, "arrow", "箭头工具");
                AddTool(ToolKind.Brush, "brush", "画刷工具");
                AddTool(ToolKind.Mosaic, "mosaic", "马赛克工具");
                AddTool(ToolKind.Text, "text", "文字工具");
                AddTool(ToolKind.Marker, "marker", "序号笔工具");
                AddAction("undo", "撤销编辑 Ctrl+Z", () => _session.Undo());
                AddAction("save", "保存 Ctrl+S", () => RequestFinish(CopyMode.File));
                AddAction("long", "长截图", () => _session.EnterLongMode());
                AddAction("nail", "钉在桌面上", () => _session.Finish(CopyMode.Pin));
                AddSeparator();
                AddAction("exit", "退出截图", () => _session.Exit());
                AddFinish();
                break;

            case ToolbarMode.Pin:
                AddTool(ToolKind.Rect, "rect", "矩形工具");
                AddTool(ToolKind.Ellipse, "ellipse", "椭圆工具");
                AddTool(ToolKind.Arrow, "arrow", "箭头工具");
                AddTool(ToolKind.Brush, "brush", "画刷工具");
                AddTool(ToolKind.Mosaic, "mosaic", "马赛克工具");
                AddTool(ToolKind.Text, "text", "文字工具");
                AddTool(ToolKind.Marker, "marker", "序号笔工具");
                AddAction("undo", "撤销编辑 Ctrl+Z", () => _session.Undo());
                AddAction("save", "保存 Ctrl+S", () => RequestFinish(CopyMode.File));
                AddSeparator();
                AddAction("exit", "关闭钉图", () => _session.Exit());
                AddFinish();
                break;

            case ToolbarMode.LongCapture:
                _directionVertical = AddToggle("vertical", "纵向长截图", () => SetDirection(false));
                _directionHorizontal = AddToggle("horizontal", "横向长截图", () => SetDirection(true));
                _autoScroll = AddToggle("scroll", "自动滚动", () => AutoScrollToggled?.Invoke());
                AddSeparator();
                AddAction("edit", "编辑工具", () => EditRequested?.Invoke());
                AddAction("save", "保存 Ctrl+S", () => RequestFinish(CopyMode.File));
                AddAction("exit", "退出截图", () => _session.Exit());
                AddFinish();
                break;
        }

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

    public double NaturalWidth => MeasureOr(ActualWidth);
    public double NaturalHeight => MeasureOr(ActualHeight);

    private double MeasureOr(double fallback)
    {
        if (fallback > 0) return fallback;
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _mode == ToolbarMode.LongCapture ? DesiredSize.Width : DesiredSize.Width;
    }

    private Button MakeButton(string iconKey, string tooltip)
    {
        var canvas = new Canvas { Width = Theme.IconSize, Height = Theme.IconSize };
        if (Icons.FillPaths.TryGetValue(iconKey, out var fillData))
        {
            var fill = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(fillData),
                Fill = Theme.Brush(Theme.IconBrush),
                Opacity = iconKey == "mosaic" ? 0.85 : 1
            };
            canvas.Children.Add(fill);
            if (iconKey == "mosaic")
            {
                var inner = new System.Windows.Shapes.Path { Data = Geometry.Parse("M4.4,4.4 H5.6 V5.6 H4.4 Z M11.3,11.3 H12.5 V12.5 H11.3 Z"), Fill = Theme.Brush(Theme.BarBackground) };
                canvas.Children.Add(inner);
            }
        }
        if (Icons.StrokePaths.TryGetValue(iconKey, out var data))
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = Theme.Brush(Theme.IconBrush),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            canvas.Children.Add(path);
        }

        var btn = new Button
        {
            Content = canvas,
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

    private void AddTool(ToolKind tool, string icon, string tooltip)
    {
        var btn = MakeButton(icon, tooltip);
        btn.Click += (_, _) => _session.SetTool(tool);
        _toolButtons[tool] = btn;
        _row.Children.Add(btn);
    }

    private void AddAction(string icon, string tooltip, Action action)
    {
        var btn = MakeButton(icon, tooltip);
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

    private void AddFinish()
    {
        var btn = new Button
        {
            Content = "完成",
            Width = Theme.FinishButtonWidth,
            Height = Theme.FinishButtonHeight + 6,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.FinishText),
            Background = Theme.Brush(Theme.FinishButton),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 0),
            ToolTip = "完成截图",
            Focusable = false
        };
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

    private UIElement BuildPalettePanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = Theme.Brush(Theme.BarBackground),
        };
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var widths = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 5, 4, 5) };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            int px = new[] { 4, 7, 11 }[i];
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                Background = Brushes.Transparent
            };
            dot.Child = new Ellipse { Width = px, Height = px, Fill = Theme.Brush(Theme.IconBrush), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var btn = new Button
            {
                Content = dot,
                Style = ButtonStyle(),
                Background = Brushes.Transparent,
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 2, 0),
                Focusable = false,
                ToolTip = new[] { "细", "中", "粗" }[i]
            };
            btn.Click += (_, _) => _session.SetWidthIndex(index);
            _widthButtons.Add((index, btn));
            widths.Children.Add(btn);
        }
        panel.Children.Add(widths);

        var colors = new WrapPanel { MaxWidth = 17 * 19, Margin = new Thickness(2, 4, 6, 4) };
        foreach (var (name, color) in Theme.Palette)
        {
            var sw = new Button
            {
                Width = 15,
                Height = 15,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                ToolTip = $"{name} #{color.R:X2}{color.G:X2}{color.B:X2}",
                Focusable = false,
                Style = SwatchStyle()
            };
            sw.Click += (_, _) => _session.SetColor(color);
            _colorButtons.Add((color, sw));
            colors.Children.Add(sw);
        }
        panel.Children.Add(colors);

        if (_mode == ToolbarMode.Standard)
        {
            var mosaic = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 6, 4) };
            _mosaicGroup = mosaic;
            var pixelBtn = new Button { Content = "马赛克", FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0), Foreground = Theme.Brush(Theme.IconBrush), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Style = ButtonStyle(), Focusable = false };
            var blurBtn = new Button { Content = "模糊", FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 0), Foreground = Theme.Brush(Theme.IconBrush), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Style = ButtonStyle(), Focusable = false };
            pixelBtn.Click += (_, _) => _session.SetMosaicStyle(MosaicStyle.Pixelate);
            blurBtn.Click += (_, _) => _session.SetMosaicStyle(MosaicStyle.Blur);
            _mosaicSlider = new Slider
            {
                Minimum = 2,
                Maximum = 30,
                Value = _session.MosaicStrength,
                Width = 111,
                VerticalAlignment = VerticalAlignment.Center,
                IsMoveToPointEnabled = true,
                ToolTip = "模糊度"
            };
            _mosaicSlider.ValueChanged += (_, e) => _session.SetMosaicStrength((int)e.NewValue);
            mosaic.Children.Add(pixelBtn);
            mosaic.Children.Add(blurBtn);
            mosaic.Children.Add(new TextBlock { Text = "模糊度", FontSize = 11, Foreground = Theme.Brush(Theme.LoupeMuted), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            mosaic.Children.Add(_mosaicSlider);
            panel.Children.Add(mosaic);
        }

        return panel;
    }

    private readonly List<(Color Color, Button Button)> _colorButtons = [];
    private readonly List<(int Index, Button Button)> _widthButtons = [];

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

    private void SyncState()
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var (tool, btn) in _toolButtons)
                btn.Background = tool == _session.Tool ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;

            foreach (var (color, btn) in _colorButtons)
                btn.BorderBrush = color == _session.Color ? Theme.Brush(Theme.Accent) : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

            foreach (var (index, btn) in _widthButtons)
                btn.Background = index == _session.WidthIndex ? Theme.Brush(Theme.ButtonPushed) : Brushes.Transparent;

            if (_mode == ToolbarMode.LongCapture)
            {
                if (_directionVertical != null) _directionVertical.IsChecked = !IsHorizontal;
                if (_directionHorizontal != null) _directionHorizontal.IsChecked = IsHorizontal;
                if (_autoScroll != null) _autoScroll.IsChecked = _autoScrollOn;
            }

            bool showPalette = _mode != ToolbarMode.LongCapture && _session.Tool != ToolKind.None;
            _paletteHost.Visibility = showPalette ? Visibility.Visible : Visibility.Collapsed;
            if (_mosaicGroup != null)
                _mosaicGroup.Visibility = _session.Tool == ToolKind.Mosaic ? Visibility.Visible : Visibility.Collapsed;
            if (showPalette && _mosaicSlider != null && _session.MosaicStrength != (int)_mosaicSlider.Value)
                _mosaicSlider.Value = _session.MosaicStrength;
        });
    }

    public void ToggleAutoScrollOn() => _autoScrollOn = true;
    public void ToggleAutoScrollOff() => _autoScrollOn = false;

    internal void ClosePalette() => _paletteHost.Visibility = Visibility.Collapsed;
}
