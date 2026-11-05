using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Capture;
using LikeShot.Drawing;
using LikeShot.Native;

namespace LikeShot.UI;

/// <summary>
/// 钉在桌面上的图：置顶、无边框、可拖动、可拖边改大小、滚轮等比缩放、能继续标注。
/// 窗口分两段：上面是图本身，下面是工具条（悬停才出现）——图再小工具条也不会被裁掉。
/// 继续标注复用整套截图层的机器：把这张图包成一个「快照」即可。
/// </summary>
public sealed class PinWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Int32Rect _sourceRect;
    private readonly Display _display;
    private readonly double _baseScale;
    private readonly Canvas _root;
    private readonly PinSurface _surface;
    private readonly ToolbarStrip _toolbar;
    private readonly CaptureSession _session;

    private double _imageW, _imageH;   // 图片区尺寸（DIP），窗口可能比它宽/高

    private Grip _grip;
    private Rect _resizeStart;
    private Point _resizeAnchor;
    private bool _hovering;
    private bool _drawing;         // 画具落笔中：要捕获鼠标，松开时把 PointerUp 交还 session
    private bool _pickingColor;    // 吸管模式：下一次点图就是取色
    private TextBox? _pinEditor;
    private Point _pinTextAnchor;
    private TextAnnotation? _pinEditing;

    private const double GripTolerance = 8;
    private const double MinDip = 40;

    private enum Grip { None, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public PinWindow(BitmapSource image, Int32Rect sourceRect, AppConfig config)
    {
        _image = image;
        _sourceRect = sourceRect;

        var displays = Display.All();
        _display = displays.FirstOrDefault(d => d.Contains(sourceRect.X + sourceRect.Width / 2, sourceRect.Y + sourceRect.Height / 2))
                   ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
        _baseScale = _display.Scale;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Title = "LikeShot 钉图";
        Topmost = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _session = new CaptureSession(
            ScreenSnapshot.FromBitmap(image, sourceRect),
            displays,
            _baseScale);
        // 钉图没有框选这一步，但打码源、导出、命中检测全都建立在「有选区」上；
        // 不设的话标注画不出来、完成时也会退回原图丢掉全部编辑。
        _session.UseFullSelection();

        _surface = new PinSurface(_session, this);
        _root = new Canvas();
        _root.Children.Add(_surface);

        _toolbar = new ToolbarStrip(_session, _display, ToolbarMode.Pin)
        {
            Visibility = Visibility.Collapsed,
            FinishInterceptor = mode =>
            {
                if (mode == CopyMode.File) SaveToDisk();
                else FinishToClipboard();
                return true;
            }
        };
        _root.Children.Add(_toolbar);
        // 二级条里的吸管：点下去由钉图窗自己接管下一次点击，主截图层是遮罩层接的。
        _toolbar.ColorPickRequested += () => _pickingColor = true;

        Content = _root;
        _session.Changed += () => Dispatcher.Invoke(_surface.InvalidateVisual);
        // 工具栏那颗「关闭钉图」走的是 session.Exit()，这条 session 是钉图窗自己的，
        // 不接就没人响应，点下去什么都不发生。
        _session.ExitRequested += Close;
        // 文字工具：主截图层的编辑框开在遮罩层的 Canvas 上，这里开在自己的 Canvas 上。
        _session.TextPlacementRequested += p => OpenPinTextEditor(p, "", _session.Color, _session.FontSize, editTarget: null);
        _session.TextEditRequested += t => OpenPinTextEditor(t.Position, t.Text, t.Color, t.FontSize, editTarget: t);

        // 初始大小 = 它在屏幕上原本占的那块，看起来就是「原地钉住」。
        _imageW = _image.PixelWidth / _baseScale;
        _imageH = _image.PixelHeight / _baseScale;
        Left = _sourceRect.X / _baseScale;
        Top = _sourceRect.Y / _baseScale;
        ApplyWindowSize();

        MouseLeftButtonDown += OnLeftDown;
        MouseLeftButtonUp += OnLeftUp;
        MouseMove += OnMove;
        MouseWheel += OnWheel;
        MouseRightButtonUp += OnRightUp;
        MouseEnter += (_, _) => { _hovering = true; ShowToolbar(true); };
        MouseLeave += (_, _) => { _hovering = false; ShowToolbar(false); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) FinishToClipboard();
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) FinishToClipboard();
            else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SaveToDisk();
            else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _session.Undo();
        };
    }

    public CaptureSession Session => _session;
    public double BaseScale => _baseScale;
    public bool IsHovering => _hovering;

    /// <summary>图片区尺寸，PinSurface 画边框用。</summary>
    public double ImageAreaW => _imageW;
    public double ImageAreaH => _imageH;

    /// <summary>一个图片物理像素在图片区里占多少 DIP。拖边后 X/Y 可以不相等。</summary>
    public double DipPerPhysicalX => _imageW > 0 ? _imageW / _image.PixelWidth : 1 / _baseScale;
    public double DipPerPhysicalY => _imageH > 0 ? _imageH / _image.PixelHeight : 1 / _baseScale;

    public void ShowPinned()
    {
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Activate();
        Keyboard.Focus(this);
        ShowToolbar(true);
    }

    // ---------- 输入 ----------

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);

        // 吸管模式：这一下点图上的哪，色就从哪来。
        if (_pickingColor)
        {
            _pickingColor = false;
            var sp = ToPhysical(pos);
            var picked = _session.Snapshot.PixelAt((int)sp.X, (int)sp.Y);
            if (picked.A == 0) picked = Colors.Black;
            _session.SetColor(picked);
            return;
        }

        // 输入框开着时先落字；工具还是文字的话，这一下顺势再开下一个框。
        if (_pinEditor != null) CommitPinText();

        if (e.ClickCount == 2 && _session.Tool == ToolKind.None)
        {
            // 双击文字 = 改字；双击空白 = 复制收工。
            var hit = _session.HitText(ToPhysical(pos));
            if (hit != null)
            {
                OpenPinTextEditor(hit.Position, hit.Text, hit.Color, hit.FontSize, editTarget: hit);
                return;
            }
            FinishToClipboard();
            return;
        }

        // 画具激活时点图片区落笔；点在下方工具条区域不算。
        if (_session.Tool != ToolKind.None && pos.X <= _imageW && pos.Y <= _imageH)
        {
            // 捕获鼠标：拖出图片（甚至窗口）外笔画也不能断，松手时才收笔。
            _drawing = true;
            CaptureMouse();
            _session.PointerDown(ToPhysical(pos), LeftOrRight.Left);
            _surface.InvalidateVisual();
            return;
        }

        _grip = HitGrip(pos);
        if (_grip != Grip.None)
        {
            _resizeStart = new Rect(Left, Top, _imageW, _imageH);
            _resizeAnchor = PointToScreen(pos);
            CaptureMouse();
            return;
        }

        DragMove();
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_grip != Grip.None)
        {
            _grip = Grip.None;
            ReleaseMouseCapture();
            ShowToolbar(true);
            _surface.InvalidateVisual();
        }

        if (_drawing)
        {
            // 之前这里从不转发 PointerUp，会话的拖动状态一直挂着，
            // 松开鼠标后笔画还会跟着鼠标继续长。
            _drawing = false;
            ReleaseMouseCapture();
            _session.PointerUp(ToPhysical(e.GetPosition(this)));
            _surface.InvalidateVisual();
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        if ((_grip != Grip.None || _drawing) && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_grip != Grip.None) ResizeTo(pos);
            else _session.PointerMove(ToPhysical(pos));
            _surface.InvalidateVisual();
            return;
        }

        _session.PointerMove(ToPhysical(pos));
        if (_session.Tool != ToolKind.None) _surface.InvalidateVisual();
        else UpdateGripCursor(pos);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
        ZoomBy(factor, e.GetPosition(this));
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        MenuItem Item(string text, Action action)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(Item("完成（复制到剪贴板）", FinishToClipboard));
        menu.Items.Add(Item("保存", SaveToDisk));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("恢复原始大小", () => ZoomTo(1.0)));
        menu.Items.Add(Item("放大", () => ZoomBy(1.25, new Point(0, 0))));
        menu.Items.Add(Item("缩小", () => ZoomBy(0.8, new Point(0, 0))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("关闭", Close));
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------- 尺寸 ----------

    private void ZoomBy(double factor, Point anchorInWindow)
    {
        double baseW = _image.PixelWidth / _baseScale;
        double applied = Math.Clamp(factor, baseW * 0.15 / _imageW, baseW * 8 / _imageW);
        // 最小尺寸优先于缩放上下限：太小的图缩到底也不能小于 MinDip，且保持等比不变形。
        applied = Math.Max(Math.Max(applied, MinDip / _imageW), MinDip / _imageH);
        double newW = _imageW * applied;
        double newH = _imageH * applied;

        // 光标在屏幕上那点不动：窗口原点按缩放量反向挪。
        double screenX = Left + anchorInWindow.X;
        double screenY = Top + anchorInWindow.Y;

        _imageW = newW;
        _imageH = newH;
        ApplyWindowSize();
        Left = screenX - anchorInWindow.X * applied;
        Top = screenY - anchorInWindow.Y * applied;

        _surface.InvalidateVisual();
    }

    private void ZoomTo(double target)
    {
        double factor = target * (_image.PixelWidth / _baseScale) / _imageW;
        ZoomBy(factor, new Point(0, 0));
    }

    private void ResizeTo(Point windowPos)
    {
        var screenNow = PointToScreen(windowPos);
        double dx = (screenNow.X - _resizeAnchor.X) / _baseScale;
        double dy = (screenNow.Y - _resizeAnchor.Y) / _baseScale;

        double left = _resizeStart.Left, top = _resizeStart.Top;
        double right = _resizeStart.Right, bottom = _resizeStart.Bottom;

        if (_grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft) left = Math.Min(_resizeStart.Left + dx, right - MinDip);
        if (_grip is Grip.Right or Grip.TopRight or Grip.BottomRight) right = Math.Max(_resizeStart.Right + dx, left + MinDip);
        if (_grip is Grip.Top or Grip.TopLeft or Grip.TopRight) top = Math.Min(_resizeStart.Top + dy, bottom - MinDip);
        if (_grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) bottom = Math.Max(_resizeStart.Bottom + dy, top + MinDip);

        double w = right - left, h = bottom - top;

        bool corner = _grip is Grip.TopLeft or Grip.TopRight or Grip.BottomLeft or Grip.BottomRight;
        if (corner)
        {
            // 角上拖是等比，图不变形；边上拖是单向拉伸。
            double aspect = _resizeStart.Width / _resizeStart.Height;
            h = w / aspect;
            if (_grip is Grip.TopLeft or Grip.TopRight) top = bottom - h;
            else bottom = top + h;
        }

        // 拖的是图片区，窗口跟着包住图片 + 工具条。
        _imageW = w;
        _imageH = h;
        Left = left;
        Top = top;
        ApplyWindowSize();
        _surface.InvalidateVisual();
    }

    private Grip HitGrip(Point pos)
    {
        // 手柄贴着图片区的边（不是窗口边——窗口比图宽的时候以图为准）。
        bool nearLeft = pos.X <= GripTolerance;
        bool nearRight = Math.Abs(pos.X - _imageW) <= GripTolerance && pos.X <= _imageW + GripTolerance;
        bool nearTop = pos.Y <= GripTolerance;
        bool nearBottom = Math.Abs(pos.Y - _imageH) <= GripTolerance && pos.Y <= _imageH + GripTolerance;

        if (nearLeft && nearTop) return Grip.TopLeft;
        if (nearRight && nearTop) return Grip.TopRight;
        if (nearLeft && nearBottom) return Grip.BottomLeft;
        if (nearRight && nearBottom) return Grip.BottomRight;
        if (nearLeft) return Grip.Left;
        if (nearRight) return Grip.Right;
        if (nearTop) return Grip.Top;
        if (nearBottom) return Grip.Bottom;
        return Grip.None;
    }

    private void UpdateGripCursor(Point pos)
    {
        Cursor = HitGrip(pos) switch
        {
            Grip.Left or Grip.Right => Cursors.SizeWE,
            Grip.Top or Grip.Bottom => Cursors.SizeNS,
            Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
            Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Arrow
        };
    }

    private Point ToPhysical(Point dip) => new(
        _sourceRect.X + dip.X / DipPerPhysicalX,
        _sourceRect.Y + dip.Y / DipPerPhysicalY);

    // ---------- 文字工具 ----------

    /// <summary>和截图层同一套交互：原字先藏、Enter 落字、Esc 丢弃、点外面自动提交。</summary>
    private void OpenPinTextEditor(Point physical, string initial, Color color, int fontSizePx, TextAnnotation? editTarget)
    {
        CommitPinText();

        _pinTextAnchor = physical;
        _pinEditing = editTarget;
        if (_pinEditing != null) _pinEditing.Hidden = true;

        double dx = (physical.X - _sourceRect.X) * DipPerPhysicalX;
        double dy = (physical.Y - _sourceRect.Y) * DipPerPhysicalY;
        _pinEditor = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            Foreground = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1, 1, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
            FontSize = fontSizePx * DipPerPhysicalY,
            Padding = new Thickness(0),
            MinWidth = 24,
            MaxWidth = Math.Max(24, _imageW - dx - 2)
        };
        _pinEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CommitPinText(discard: true); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitPinText();
                e.Handled = true;
            }
        };
        _pinEditor.LostFocus += (_, _) => CommitPinText();
        _root.Children.Add(_pinEditor);
        Canvas.SetLeft(_pinEditor, dx);
        Canvas.SetTop(_pinEditor, dy - fontSizePx * DipPerPhysicalY * 0.12);
        _pinEditor.Focus();
        _pinEditor.SelectAll();
    }

    private void CommitPinText(bool discard = false)
    {
        if (_pinEditor == null) return;
        var editor = _pinEditor;
        _pinEditor = null;
        var text = editor.Text.TrimEnd('\r', '\n');
        _root.Children.Remove(editor);

        var target = _pinEditing;
        _pinEditing = null;
        if (target != null)
        {
            target.Hidden = false;   // 不管改没改成，旧字都放回来
            if (!discard && text.Length > 0) _session.UpdateText(target, text);
        }
        else if (!discard && text.Length > 0)
        {
            _session.AddText(text, _pinTextAnchor);
        }
        _surface.InvalidateVisual();
    }

    // ---------- 出口 ----------

    private void ShowToolbar(bool visible)
    {
        _toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApplyWindowSize();
    }

    /// <summary>窗口 = 图片区 + 工具条（悬停时在图片下方）；图片再窄窗口也会包住整条工具条。</summary>
    private void ApplyWindowSize()
    {
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = _toolbar.DesiredSize.Width, th = _toolbar.DesiredSize.Height;
        bool stripVisible = _toolbar.Visibility == Visibility.Visible;

        _surface.Width = _imageW;
        _surface.Height = _imageH;
        Canvas.SetLeft(_surface, 0);
        Canvas.SetTop(_surface, 0);

        Width = Math.Max(_imageW, stripVisible ? tw + 4 : 0);
        Height = _imageH + (stripVisible ? th + 4 : 0);

        Canvas.SetLeft(_toolbar, Math.Max(2, (Width - tw) / 2));
        Canvas.SetTop(_toolbar, _imageH + 2);
    }

    public void FinishToClipboard()
    {
        CommitPinText();
        var image = _session.RenderResult();
        Close();
        Exporter.CopyImage(image);
    }

    public void SaveToDisk()
    {
        CommitPinText();
        var image = _session.RenderResult();
        var saved = Exporter.SaveDialog(image, App.Config);
        if (saved != null && App.Config.CopyAfterSave) Exporter.CopyImage(image);
    }

    /// <summary>把标注按当前窗口的拉伸比例画出来，X/Y 分开，拖过边之后也不会错位。</summary>
    private sealed class PinSurface : FrameworkElement
    {
        private readonly CaptureSession _session;
        private readonly PinWindow _owner;

        public PinSurface(CaptureSession session, PinWindow owner) => (_session, _owner) = (session, owner);

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = _session.Snapshot.VirtualBounds;
            double sx = _owner.DipPerPhysicalX;
            double sy = _owner.DipPerPhysicalY;

            RenderOptions.SetBitmapScalingMode(this,
                sx * _owner.BaseScale > 1.6 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

            // 先缩放后平移：绘制坐标用「虚拟桌面物理像素」，映射到窗口 DIP 是 (p-origin)*s。
            dc.PushTransform(new ScaleTransform(sx, sy));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            // 标注裁在图片区里：拖出图片的笔画不该漏到下方工具条上，导出时本来也是裁掉的。
            dc.PushClip(new RectangleGeometry(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
            dc.DrawImage(_session.Snapshot.Image, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            if (_session.Mosaics != null)
                _session.Layer.DrawAll(dc, new RenderContext(_session.Mosaics, sx * _owner.BaseScale, _session.Mosaics.Region));
            dc.Pop();
            dc.Pop();
            dc.Pop();

            DrawFrame(dc);
        }

        private void DrawFrame(DrawingContext dc)
        {
            // 边框和四角手柄贴着图片区画，不包含下方工具条。
            double w = _owner.ImageAreaW, h = _owner.ImageAreaH;
            var border = new Pen(Theme.Brush(Theme.BarBorder), 1);
            dc.DrawRectangle(null, border, new Rect(0.5, 0.5, Math.Max(1, w - 1), Math.Max(1, h - 1)));

            if (!_owner.IsHovering) return;

            double len = 9, thick = 2.5;
            var grip = new Pen(Theme.Brush(Theme.Accent), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            Point[] corners = [new(0, 0), new(w, 0), new(0, h), new(w, h)];
            foreach (var c in corners)
            {
                double dx = c.X == 0 ? 1 : -1, dy = c.Y == 0 ? 1 : -1;
                dc.DrawLine(grip, new Point(c.X + 2 * dx, c.Y + 2 * dy), new Point(c.X + (2 + len) * dx, c.Y + 2 * dy));
                dc.DrawLine(grip, new Point(c.X + 2 * dx, c.Y + 2 * dy), new Point(c.X + 2 * dx, c.Y + (2 + len) * dy));
            }
        }
    }
}
