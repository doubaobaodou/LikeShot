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
/// 窗口客户区就是这张图本身，所以拖边是拉伸、滚轮是等比，两者都不会让图和窗错位。
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

    private Grip _grip;
    private Rect _resizeStart;
    private Point _resizeAnchor;
    private bool _hovering;

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

        Content = _root;
        _session.Changed += () => Dispatcher.Invoke(_surface.InvalidateVisual);
        // 工具栏那颗「关闭钉图」走的是 session.Exit()，这条 session 是钉图窗自己的，
        // 不接就没人响应，点下去什么都不发生。
        _session.ExitRequested += Close;

        // 初始大小 = 它在屏幕上原本占的那块，看起来就是「原地钉住」。
        Width = _image.PixelWidth / _baseScale;
        Height = _image.PixelHeight / _baseScale;
        Left = _sourceRect.X / _baseScale;
        Top = _sourceRect.Y / _baseScale;

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

    /// <summary>一个图片物理像素在当前窗口里占多少 DIP。拖边后 X/Y 可以不相等。</summary>
    public double DipPerPhysicalX => ActualWidth > 0 ? ActualWidth / _image.PixelWidth : 1 / _baseScale;
    public double DipPerPhysicalY => ActualHeight > 0 ? ActualHeight / _image.PixelHeight : 1 / _baseScale;

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

        if (e.ClickCount == 2 && _session.Tool == ToolKind.None)
        {
            FinishToClipboard();
            return;
        }

        if (_session.Tool != ToolKind.None)
        {
            _session.PointerDown(ToPhysical(pos), LeftOrRight.Left);
            _surface.InvalidateVisual();
            return;
        }

        _grip = HitGrip(pos);
        if (_grip != Grip.None)
        {
            _resizeStart = new Rect(Left, Top, Width, Height);
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
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_grip != Grip.None && e.LeftButton == MouseButtonState.Pressed)
        {
            ResizeTo(pos);
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
        double newW = Math.Clamp(Width * factor, baseW * 0.15, baseW * 8);
        double applied = newW / Width;
        double newH = Math.Max(MinDip, Height * applied);

        // 光标在屏幕上那点不动：窗口原点按缩放量反向挪。
        double screenX = Left + anchorInWindow.X;
        double screenY = Top + anchorInWindow.Y;

        Width = newW;
        Height = newH;
        Left = screenX - anchorInWindow.X * applied;
        Top = screenY - anchorInWindow.Y * applied;

        ShowToolbar(_toolbar.Visibility == Visibility.Visible);
        _surface.InvalidateVisual();
    }

    private void ZoomTo(double target)
    {
        double factor = target * (_image.PixelWidth / _baseScale) / Width;
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

        Left = left;
        Top = top;
        Width = w;
        Height = h;
        ShowToolbar(true);
        _surface.InvalidateVisual();
    }

    private Grip HitGrip(Point pos)
    {
        bool nearLeft = pos.X <= GripTolerance;
        bool nearRight = pos.X >= ActualWidth - GripTolerance;
        bool nearTop = pos.Y <= GripTolerance;
        bool nearBottom = pos.Y >= ActualHeight - GripTolerance;

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

    // ---------- 出口 ----------

    private void ShowToolbar(bool visible)
    {
        if (!visible)
        {
            _toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        _toolbar.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _toolbar.DesiredSize.Width, h = _toolbar.DesiredSize.Height;
        Canvas.SetLeft(_toolbar, Math.Max(2, (ActualWidth - w) / 2));
        Canvas.SetTop(_toolbar, Math.Max(2, ActualHeight - h - 4));
    }

    public void FinishToClipboard()
    {
        var image = _session.RenderResult();
        Close();
        Exporter.CopyImage(image);
    }

    public void SaveToDisk()
    {
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
            dc.DrawImage(_session.Snapshot.Image, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            if (_session.Mosaics != null)
                _session.Layer.DrawAll(dc, new RenderContext(_session.Mosaics, sx * _owner.BaseScale, _session.Mosaics.Region));
            dc.Pop();
            dc.Pop();

            DrawFrame(dc);
        }

        private void DrawFrame(DrawingContext dc)
        {
            var border = new Pen(Theme.Brush(Theme.BarBorder), 1);
            dc.DrawRectangle(null, border, new Rect(0.5, 0.5, Math.Max(1, ActualWidth - 1), Math.Max(1, ActualHeight - 1)));

            if (!_owner.IsHovering) return;

            double len = 9, thick = 2.5;
            var grip = new Pen(Theme.Brush(Theme.Accent), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            Point[] corners = [new(0, 0), new(ActualWidth, 0), new(0, ActualHeight), new(ActualWidth, ActualHeight)];
            foreach (var c in corners)
            {
                double dx = c.X == 0 ? 1 : -1, dy = c.Y == 0 ? 1 : -1;
                dc.DrawLine(grip, new Point(c.X + 2 * dx, c.Y + 2 * dy), new Point(c.X + (2 + len) * dx, c.Y + 2 * dy));
                dc.DrawLine(grip, new Point(c.X + 2 * dx, c.Y + 2 * dy), new Point(c.X + 2 * dx, c.Y + (2 + len) * dy));
            }
        }
    }
}
