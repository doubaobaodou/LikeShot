using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LikeShot.Capture;

namespace LikeShot.UI;

/// <summary>
/// QQ 那个自定义取色板：左边色域方块（横=饱和度，纵=明度），右侧竖色相条，
/// 下面吸管 + 当前色 + RGB 输入。拖动和输入都实时落到 session.SetColor；
/// 色板、吸管从会话改了色，也同步回方块位置。
/// </summary>
internal sealed class ColorPickerPanel : StackPanel
{
    private readonly CaptureSession _session;

    /// <summary>吸管：让遮罩层进入「点哪儿取哪儿」的模式。</summary>
    public event Action? PickRequested;

    private double _h, _s = 1, _v = 1;
    private bool _syncing;   // 自己往会话写的过程中，忽略会话的回写

    private readonly Rectangle _svBase;
    private readonly LinearGradientBrush _svBaseGradient;
    private readonly Border _svArea;
    private readonly Ellipse _svThumb;
    private readonly Border _hueTrack;
    private readonly Ellipse _hueThumb;
    private readonly Border _currentSwatch;
    private readonly TextBox _rBox, _gBox, _bBox;
    private const double SvW = 150, SvH = 110, HueW = 14;

    public ColorPickerPanel(CaptureSession session)
    {
        _session = session;
        Orientation = Orientation.Vertical;
        Margin = new Thickness(6, 4, 6, 6);

        _svBaseGradient = new LinearGradientBrush(
            ColorFromHsv(_h, 1, 1), Colors.White, 0);
        _svBase = new Rectangle { Fill = _svBaseGradient, RadiusX = 2, RadiusY = 2 };
        var svDark = new Rectangle
        {
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
            RadiusX = 2,
            RadiusY = 2
        };
        _svThumb = new Ellipse
        {
            Width = 11,
            Height = 11,
            Fill = Brushes.Transparent,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var svGrid = new Grid();
        svGrid.Children.Add(_svBase);
        svGrid.Children.Add(svDark);
        svGrid.Children.Add(_svThumb);
        _svArea = new Border
        {
            Width = SvW,
            Height = SvH,
            CornerRadius = new CornerRadius(3),
            Child = svGrid,
            Cursor = Cursors.Cross
        };
        _svArea.MouseLeftButtonDown += (_, e) => { _svArea.CaptureMouse(); ApplySv(e.GetPosition(_svArea)); };
        _svArea.MouseMove += (_, e) => { if (_svArea.IsMouseCaptured) ApplySv(e.GetPosition(_svArea)); };
        _svArea.MouseLeftButtonUp += (_, _) => _svArea.ReleaseMouseCapture();

        _hueThumb = new Ellipse
        {
            Width = HueW + 4,
            Height = 10,
            Fill = Brushes.Transparent,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var hueGrid = new Grid();
        hueGrid.Children.Add(new Rectangle
        {
            RadiusX = 2,
            RadiusY = 2,
            Fill = new LinearGradientBrush(
                Color.FromArgb(255, 255, 0, 0), Color.FromArgb(255, 255, 0, 0), 0)
            {
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(255, 0, 0), 0),
                    new GradientStop(Color.FromRgb(255, 255, 0), 1 / 6d),
                    new GradientStop(Color.FromRgb(0, 255, 0), 2 / 6d),
                    new GradientStop(Color.FromRgb(0, 255, 255), 3 / 6d),
                    new GradientStop(Color.FromRgb(0, 0, 255), 4 / 6d),
                    new GradientStop(Color.FromRgb(255, 0, 255), 5 / 6d),
                    new GradientStop(Color.FromRgb(255, 0, 0), 1)
                }
            }
        });
        hueGrid.Children.Add(_hueThumb);
        _hueTrack = new Border
        {
            Width = HueW,
            Height = SvH,
            CornerRadius = new CornerRadius(3),
            Child = hueGrid,
            Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0)
        };
        _hueTrack.MouseLeftButtonDown += (_, e) => { _hueTrack.CaptureMouse(); ApplyHue(e.GetPosition(_hueTrack)); };
        _hueTrack.MouseMove += (_, e) => { if (_hueTrack.IsMouseCaptured) ApplyHue(e.GetPosition(_hueTrack)); };
        _hueTrack.MouseLeftButtonUp += (_, _) => _hueTrack.ReleaseMouseCapture();

        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(_svArea);
        topRow.Children.Add(_hueTrack);
        Children.Add(topRow);

        // 吸管 + 当前色 + RGB 输入
        var pipette = MakePipetteButton();
        pipette.Click += (_, _) => PickRequested?.Invoke();
        pipette.ToolTip = "屏幕取色";

        _currentSwatch = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = Theme.Brush(_session.Color),
            BorderBrush = Theme.Brush(Theme.SwatchBorder),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };

        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        bottomRow.Children.Add(pipette);
        bottomRow.Children.Add(_currentSwatch);
        _rBox = MakeChannelBox();
        _gBox = MakeChannelBox();
        _bBox = MakeChannelBox();
        for (int i = 0; i < 3; i++)
        {
            var box = i switch { 0 => _rBox, 1 => _gBox, _ => _bBox };
            var label = i switch { 0 => "R", 1 => "G", _ => "B" };
            bottomRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = Theme.Brush(Theme.LoupeMuted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 3, 0)
            });
            bottomRow.Children.Add(box);
        }
        Children.Add(bottomRow);

        foreach (var box in new[] { _rBox, _gBox, _bBox })
        {
            box.LostFocus += (_, _) => ApplyRgbBoxes();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyRgbBoxes();
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
            };
        }
    }

    /// <summary>会话里的颜色变了（色板/吸管）就把方块和输入框对齐过去；自己正在拖就不回写。</summary>
    public void SyncFromColor(Color color)
    {
        if (_syncing || !IsVisible) return;
        (_h, _s, _v) = HsvFromColor(color);
        RefreshVisuals(color);
    }

    private void ApplySv(Point pos)
    {
        _s = Math.Clamp(pos.X / SvW, 0, 1);
        _v = Math.Clamp(1 - pos.Y / SvH, 0, 1);
        ApplyHsv();
    }

    private void ApplyHue(Point pos)
    {
        _h = Math.Clamp(pos.Y / SvH, 0, 0.9999) * 360;
        ApplyHsv();
    }

    private void ApplyHsv()
    {
        _syncing = true;
        var color = ColorFromHsv(_h, _s, _v);
        _session.SetColor(color);
        RefreshVisuals(color);
        _syncing = false;
    }

    private void ApplyRgbBoxes()
    {
        if (!byte.TryParse(_rBox.Text, out byte r) || !byte.TryParse(_gBox.Text, out byte g) ||
            !byte.TryParse(_bBox.Text, out byte b)) return;
        _syncing = true;
        var color = Color.FromRgb(r, g, b);
        (_h, _s, _v) = HsvFromColor(color);
        _session.SetColor(color);
        RefreshVisuals(color);
        _syncing = false;
    }

    private void RefreshVisuals(Color color)
    {
        _svBaseGradient.GradientStops[0].Color = ColorFromHsv(_h, 1, 1);
        _svThumb.Margin = new Thickness(_s * SvW - 5.5, (1 - _v) * SvH - 5.5, 0, 0);
        _hueThumb.Margin = new Thickness(-2, _h / 360 * SvH - 5, 0, 0);
        _currentSwatch.Background = Theme.Brush(color);
        SetChannelText(_rBox, color.R);
        SetChannelText(_gBox, color.G);
        SetChannelText(_bBox, color.B);
    }

    private static void SetChannelText(TextBox box, byte value)
    {
        if (!box.IsKeyboardFocused) box.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    private static TextBox MakeChannelBox() => new()
    {
        Width = 40,
        FontSize = 11.5,
        Padding = new Thickness(3, 2, 3, 2),
        BorderBrush = Theme.Brush(Theme.BarBorder),
        Background = Theme.Brush(Colors.Transparent),
        Foreground = Theme.Brush(Theme.LoupeText),
        CaretBrush = Theme.Brush(Theme.LoupeText),
        Text = "0"
    };

    private Button MakePipetteButton()
    {
        var canvas = new Canvas { Width = 17, Height = 17 };
        canvas.Children.Add(new Path
        {
            Data = Icons.Parse(Icons.StrokePaths["pipette"]),
            Stroke = Theme.Brush(Theme.IconBrush),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        return new Button
        {
            Content = canvas,
            Width = 26,
            Height = 26,
            Focusable = false,
            Style = ButtonStyle(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
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
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Brush(Theme.ButtonHover)));
        style.Triggers.Add(hover);
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    internal static Color ColorFromHsv(double h, double s, double v)
    {
        h %= 360;
        if (h < 0) h += 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    internal static (double H, double S, double V) HsvFromColor(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0
            ? 0
            : max == r ? 60 * (((g - b) / d) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
