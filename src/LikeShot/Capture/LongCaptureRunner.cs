using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LikeShot.Drawing;
using LikeShot.Native;
using LikeShot.UI;

namespace LikeShot.Capture;

/// <summary>
/// 长截图：把覆盖层换成「只描边、可穿透」的取景框，滚轮交给下面的真实窗口；
/// 自己按固定节奏抓实时画面并拼接，滚到底（连续几帧没有新增行）自动收尾。
/// </summary>
public sealed class LongCaptureRunner : IDisposable
{
    private readonly CaptureSession _session;
    private readonly Action _exitCapture;
    private readonly LongFrameWindow _frame;
    private readonly ToolbarWindow _bar;
    private readonly DispatcherTimer _timer;

    private LongStitcher? _stitcher;
    private Int32Rect _region;
    private bool _horizontal;
    private bool _autoScroll;
    private int _stableTicks;
    private bool _finished;

    public LongCaptureRunner(CaptureSession session, Action exitCapture)
    {
        _session = session;
        _exitCapture = exitCapture;
        // 选区可能贴着屏幕边缘，抓实时画面前先裁进虚拟桌面，避免拷到屏外内存。
        _region = session.Snapshot.Intersect(session.FinalRect());

        _frame = new LongFrameWindow(session.Displays, _region);
        _bar = new ToolbarWindow(session, this);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(90, App.Config.LongCapturePollMilliseconds)) };
        _timer.Tick += (_, _) => Poll();
    }

    public CaptureSession Session => _session;
    public bool IsHorizontal => _horizontal;
    public int AppendedTotal => _stitcher?.Height ?? 0;
    public string StitchDiag => _stitcher?.Diag ?? "";

    public void Start()
    {
        foreach (var overlay in _session.Overlays) overlay.Hide();
        _frame.ShowPassThrough();
        _bar.ShowBar(_session.Displays, _region, _session.UiScale);
        _timer.Start();
        Poll();
    }

    public void SetDirection(bool horizontal)
    {
        _horizontal = horizontal;
        _region = _session.FinalRect();
        _stitcher = null;
        _stableTicks = 0;
        _frame.UpdateRegion(_region);
    }

    public void ToggleAutoScroll()
    {
        _autoScroll = !_autoScroll;
        _bar.SetAutoScroll(_autoScroll);
        _stableTicks = 0;
    }

    private void Poll()
    {
        if (_finished) return;

        var live = ScreenSnapshot.GrabLiveRegion(_region);
        int stride = _region.Width * 4;
        var buffer = new byte[stride * _region.Height];
        // CopyPixels(Array pixels, int stride, int offset) —— 第二个参数是步长不是缓冲区大小。
        live.CopyPixels(buffer, stride, 0);

        if (_stitcher == null)
        {
            _stitcher = new LongStitcher(_region, buffer, stride);
        }
        else
        {
            int appended = _stitcher.Feed(buffer, stride, _region.Height);
            if (appended == 0) _stableTicks++;
            else _stableTicks = 0;
        }

        bool bottomedOut = _stableTicks >= 3;
        if (_autoScroll && !bottomedOut)
            ScrollOnce();

        if (bottomedOut && _autoScroll)
        {
            _autoScroll = false;
            _bar.SetAutoScroll(false);
            // 一次都没长高过，那不是到底了，是这个页面没接住我们投的滚轮消息——两种情况得说得不一样。
            _bar.SetStatus(_stitcher.Height > _region.Height
                ? "已滚到底，点「完成」收尾"
                : "这个页面没接住自动滚动：自己滚一下，它照样接着拼");
        }

        if (_stitcher.IsFull)
        {
            _autoScroll = false;
            _bar.SetAutoScroll(false);
            _bar.SetStatus("已到长度上限，点「完成」收尾");
        }
        else
        {
            _bar.SetStatus($"已拼接 {_stitcher.Height} 像素高");
        }
    }

    private void ScrollOnce()
    {
        int notches = Math.Clamp(App.Config.LongCaptureScrollNotches, 1, 5);
        int delta = (_horizontal ? Win32.WHEEL_DELTA : -Win32.WHEEL_DELTA) * notches;
        int cx = _region.X + _region.Width / 2, cy = _region.Y + _region.Height / 2;

        // 把滚轮消息直接投给选区中心那个窗口，坐标写在 lParam 里，真光标在哪儿都不用管。
        // 以前的写法是「搬光标到选区中心 → 合成滚轮 → 立刻搬回去」：系统真正处理这条
        // 滚轮事件时光标已经被搬走了，实测自动滚动一帧都没推动（自检报告整片「整帧未变」）。
        var target = Win32.WindowFromPoint(new Win32.POINT { X = cx, Y = cy });
        if (target == nint.Zero)
        {
            LogOnce($"自动滚动找不到目标窗口：选区中心 {cx},{cy}");
            return;
        }
        if (!_loggedTarget)
        {
            _loggedTarget = true;
            var className = new System.Text.StringBuilder(64);
            Win32.GetClassName(target, className, 64);
            Win32.GetWindowThreadProcessId(target, out uint pid);
            Log.Write($"自动滚动投给窗口 hwnd={target} 类名={className} 进程={pid} 消息=0x{(_horizontal ? Win32.WM_MOUSEHWHEEL : Win32.WM_MOUSEWHEEL):X}");
        }
        uint msg = _horizontal ? Win32.WM_MOUSEHWHEEL : Win32.WM_MOUSEWHEEL;
        if (!Win32.PostMessage(target, msg, Win32.WheelWParam(delta), Win32.ScreenPointLParam(cx, cy)))
            LogOnce("自动滚动投递滚轮消息失败");
    }

    private bool _loggedTarget;

    private string _lastScrollLog = "";

    private void LogOnce(string message)
    {
        if (_lastScrollLog == message) return;
        _lastScrollLog = message;
        Log.Write(message);
    }

    public BitmapSource? BuildResult()
    {
        if (_stitcher == null) return null;
        var stitched = _stitcher.ToBitmap();
        int width = stitched.PixelWidth, height = stitched.PixelHeight;
        var mosaics = _session.Mosaics;

        return RenderSurface.ToBitmap(width, height, dc =>
        {
            dc.DrawImage(stitched, new Rect(0, 0, width, height));
            // 进入长截图前画的标注贴在第一屏的位置上，拼出来的长图里它们仍在那块内容上。
            if (mosaics != null)
                _session.Layer.DrawAll(dc, new RenderContext(mosaics, 1.0, mosaics.Region));
        });
    }

    public void FinishToClipboard()
    {
        var image = BuildResult();
        _finished = true;
        Close();
        _exitCapture();
        if (image != null) Exporter.CopyImage(image);
    }

    public void FinishToFile()
    {
        var image = BuildResult();
        if (image == null) { Cancel(); return; }
        var path = Exporter.SaveDialog(image, App.Config);
        _finished = true;
        Close();
        _exitCapture();
        if (path != null && App.Config.CopyAfterSave) Exporter.CopyImage(image);
    }

    public void Edit()
    {
        var image = BuildResult();
        if (image == null) { Cancel(); return; }
        _finished = true;
        Close();
        _exitCapture();
        _session.Controller?.Pin(image, new Int32Rect(_region.X, _region.Y, image.PixelWidth, image.PixelHeight));
    }

    public void Cancel()
    {
        _finished = true;
        Close();
        _exitCapture();
    }

    public void Close()
    {
        _timer.Stop();
        _bar.Close();
        _frame.Close();
    }

    public void Dispose()
    {
        _timer.Stop();
        _frame.Close();
        _bar.Close();
    }
}

/// <summary>取景框：盖满整个虚拟桌面但完全点击穿透，只画暗区和边框。</summary>
internal sealed class LongFrameWindow : Window
{
    private readonly IReadOnlyList<Display> _displays;
    private Int32Rect _region;
    private readonly FrameSurface _surface;

    public LongFrameWindow(IReadOnlyList<Display> displays, Int32Rect region)
    {
        _displays = displays;
        _region = region;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        // 必须真透明：选区里面要露出下面的真实页面，用户才能滚它。
        // 之前用实心黑底 + 只画暗区，结果选区里也是一片黑，整个页面被自己盖死了。
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        int minX = displays.Min(d => d.Bounds.X), minY = displays.Min(d => d.Bounds.Y);
        int maxX = displays.Max(d => d.Bounds.X + d.Bounds.Width);
        int maxY = displays.Max(d => d.Bounds.Y + d.Bounds.Height);
        double scale = displays[0].Scale;

        Left = minX / scale;
        Top = minY / scale;
        Width = (maxX - minX) / scale;
        Height = (maxY - minY) / scale;
        _totalBounds = new Int32Rect(minX, minY, maxX - minX, maxY - minY);

        _surface = new FrameSurface(this, displays[0], () => _region);
        Content = _surface;
    }

    private readonly Int32Rect _totalBounds;
    internal Int32Rect TotalBounds => _totalBounds;

    public void UpdateRegion(Int32Rect region)
    {
        _region = region;
        _surface.InvalidateVisual();
    }

    public void ShowPassThrough()
    {
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private sealed class FrameSurface : FrameworkElement
    {
        private readonly LongFrameWindow _owner;
        private readonly Display _display;
        private readonly Func<Int32Rect> _region;

        public FrameSurface(LongFrameWindow owner, Display display, Func<Int32Rect> region)
            => (_owner, _display, _region) = (owner, display, region);

        protected override void OnRender(DrawingContext dc)
        {
            double scale = _display.Scale;
            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform(-_display.Bounds.X, -_display.Bounds.Y));
            group.Children.Add(new ScaleTransform(1 / scale, 1 / scale));
            dc.PushTransform(group);

            var wholeRect = _owner.TotalBounds;
            var whole = new Rect(wholeRect.X, wholeRect.Y, wholeRect.Width, wholeRect.Height);
            var region = _region();
            var keep = new Rect(region.X, region.Y, region.Width, region.Height);

            dc.DrawGeometry(Theme.Brush(Theme.DimOutside), null,
                new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(whole), new RectangleGeometry(keep)));

            // 边框画在选区外面一圈：画在选区内沿的话，BitBlt 抓实时画面会把这条线一起抓进去，
            // 长图里每隔一帧就多出一道蓝线。
            var outside = new Rect(keep.X - 3 * scale, keep.Y - 3 * scale, keep.Width + 6 * scale, keep.Height + 6 * scale);
            dc.DrawRectangle(null, new Pen(Theme.Brush(Theme.Accent), Math.Max(1, 2 * scale)), outside);
            dc.Pop();
        }
    }
}

/// <summary>长截图的第二条工具栏，独立可点击窗口（取景框是穿透的，不能带它）。</summary>
internal sealed class ToolbarWindow : Window
{
    private readonly ToolbarStrip _strip;
    private readonly TextBlock _status;

    public ToolbarWindow(CaptureSession session, LongCaptureRunner runner)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "LikeShot 长截图";
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;

        _strip = new ToolbarStrip(session, session.Displays[0], ToolbarMode.LongCapture)
        {
            FinishInterceptor = mode =>
            {
                if (mode == CopyMode.File) runner.FinishToFile();
                else runner.FinishToClipboard();
                return true;
            }
        };
        _strip.DirectionChanged += runner.SetDirection;
        _strip.AutoScrollToggled += runner.ToggleAutoScroll;
        _strip.EditRequested += runner.Edit;

        _status = new TextBlock
        {
            FontSize = 11,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            Margin = new Thickness(8, 0, 8, 4),
            Text = "滚动页面开始拼接"
        };

        var stack = new StackPanel();
        stack.Children.Add(_strip);
        stack.Children.Add(_status);
        Content = stack;
    }

    public void ShowBar(IReadOnlyList<Display> displays, Int32Rect region, double scale)
    {
        Show();
        var display = displays.FirstOrDefault(d => d.Contains(region.X + region.Width / 2, region.Bottom())) ?? displays[0];
        double w = ActualWidth > 0 ? ActualWidth : 420;
        double left = (region.X + region.Width / 2d - display.Bounds.X) / display.Scale - w / 2;
        double top = (region.Bottom() - display.Bounds.Y) / display.Scale + 10;
        if (top + ActualHeight > display.Bounds.Height / display.Scale)
            top = (region.Y - display.Bounds.Y) / display.Scale - ActualHeight - 10;

        Left = Math.Clamp(left, display.Bounds.X / display.Scale + 4, (display.Bounds.X + display.Bounds.Width) / display.Scale - w - 4);
        Top = Math.Max(display.Bounds.Y / display.Scale + 4, top);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        _ = scale;
    }

    public void SetAutoScroll(bool on)
    {
        if (on) _strip.ToggleAutoScrollOn();
        else _strip.ToggleAutoScrollOff();
    }

    public void SetStatus(string text) => Dispatcher.Invoke(() => _status.Text = text);
}
