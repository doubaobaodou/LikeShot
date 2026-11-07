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
    private readonly LongPreviewWindow _preview;
    private readonly DispatcherTimer _timer;

    private LongStitcher? _stitcher;
    private readonly ScreenSnapshot.LiveGrabber _grabber = new();
    private Int32Rect _region;
    private bool _horizontal;
    private bool _autoScroll;
    private int _stableTicks;
    private bool _finished;
    private bool _closed;       // 窗口已经关掉：补拍的收尾不能再把它们 Show 回来

    public LongCaptureRunner(CaptureSession session, Action exitCapture)
    {
        _session = session;
        _exitCapture = exitCapture;
        // 选区可能贴着屏幕边缘，抓实时画面前先裁进虚拟桌面，避免拷到屏外内存。
        _region = session.Snapshot.Intersect(session.FinalRect());

        _frame = new LongFrameWindow(session.Displays, _region);
        _bar = new ToolbarWindow(session, this);
        _preview = new LongPreviewWindow(_horizontal);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(90, App.Config.LongCapturePollMilliseconds)) };
        _timer.Tick += (_, _) => Poll();
    }

    public CaptureSession Session => _session;
    public bool IsHorizontal => _horizontal;
    public int AppendedTotal => _stitcher?.Height ?? 0;
    public string StitchDiag => _stitcher?.Diag ?? "";
    internal LongPreviewWindow PreviewForTest => _preview;

    /// <summary>选区底边被自家悬浮窗压住、整段预留掉的行数；0 表示工具栏和预览都在选区外。</summary>
    internal int ReservedFooterForTest => _stitcher?.ReservedFooter ?? 0;

    public void Start()
    {
        // 先立取景框、再收截图层：这两步之间不留「两层都不在」的空档。
        // 第一次进长截图这条路径全是首次 JIT，先收后立的写法会拉出几百毫秒的
        // 光板屏幕——看着就像「透出来的不是我要截的页面」。
        _frame.ShowPassThrough();
        foreach (var overlay in _session.Overlays) overlay.Hide();
        _bar.ShowBar(_session.Displays, _region, _session.UiScale);
        _preview.Reset(_horizontal);
        _preview.Attach(_session.Displays, _region);
        SettleBeforeFirstFrame();
    }

    /// <summary>
    /// 首拍前的安顿：截图层藏掉之后，屏幕合成要一两帧才真正翻篇；被整窗盖了一路的目标
    /// 窗口（尤其走显卡合成的浏览器）回神更慢——露出来那一拍还可能是它底下的窗口，
    /// 「第一次长截图透出底下窗口」就是这一拍被抓进去了。所以先踢它一下
    /// （强制重绘 + 一条鼠标移动消息），前后各等一拍，才开始拼第一帧。
    /// 踢之前先抓一张探测帧落盘：如果合成层还有残影（长图里的黑块），能看出
    /// 它是「一开始就在、踢完就好了」还是「拍到一半才冒出来」。
    /// </summary>
    private async void SettleBeforeFirstFrame()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await System.Threading.Tasks.Task.Delay(200);
        if (_finished) return;

        int cx = _region.X + _region.Width / 2, cy = _region.Y + _region.Height / 2;
        var target = Win32.WindowFromPoint(new Win32.POINT { X = cx, Y = cy });
        if (target == nint.Zero) target = nint.Zero;
        Log.Write($"长截图首拍前目标窗口 {DescribeWindow(target)}");

        // 对照取证：截图层当时显示的那张快照的选区裁剪。它和下面几帧逐张比，
        // 就能分清「抓屏抓错了」还是「屏幕那一刻真的不是用户选的页面」。
        DumpSnapshotRegionForEvidence();

        // 探测帧（踢之前）：合成层这时是什么样，留个底。
        var (probe, probeStride) = _grabber.Grab(_region);
        DumpFrameForEvidence(probe, probeStride, "pre-kick.png");

        for (int beat = 0; beat < 2; beat++)
        {
            if (target != nint.Zero)
            {
                Win32.RedrawWindow(target, nint.Zero, nint.Zero,
                    Win32.RDW_INVALIDATE | Win32.RDW_ERASE | Win32.RDW_ALLCHILDREN | Win32.RDW_FRAME | Win32.RDW_UPDATENOW);
                Win32.PostMessage(target, Win32.WM_MOUSEMOVE, nint.Zero, Win32.ScreenPointLParam(cx, cy));
            }
            await System.Threading.Tasks.Task.Delay(250);
            if (_finished) return;
            var (after, afterStride) = _grabber.Grab(_region);
            DumpFrameForEvidence(after, afterStride, $"kick{beat + 1}.png");
        }
        Log.Write($"长截图首拍前唤醒目标窗口 hwnd={target}（踢了两轮）");
        _timer.Start();
        Poll();
    }

    /// <summary>
    /// 抓屏内容落盘留证：用户报「长图里有黑块/别的窗口」时，先看这几个文件——
    /// 抓屏那一刻选区里到底是什么，一目了然。每次长截图覆盖，落在 Temp 不沾用户数据目录。
    /// </summary>
    private static string DiagDirectory =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "likeshot-longdiag");

    private void DumpSnapshotRegionForEvidence()
    {
        try
        {
            byte[] pixels = _session.Snapshot.CopyRegion(_region);
            WriteEvidenceOffThread(pixels, _region.Width * 4, "snap-region.png");
        }
        catch (Exception ex)
        {
            Log.Error("长截图快照留证失败", ex);
        }
    }

    /// <summary>被踢的窗口到底是谁：子窗口 hwnd 看不出归属，追到根窗口报进程名和主标题。</summary>
    private static string DescribeWindow(nint hwnd)
    {
        if (hwnd == nint.Zero) return "hwnd=无";
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOTOWNER);
        if (root == nint.Zero) root = hwnd;
        Win32.GetWindowThreadProcessId(root, out uint pid);
        string who = "进程已退出";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            who = $"{process.ProcessName}「{process.MainWindowTitle}」";
        }
        catch { /* 进程刚好退了：按退出算 */ }
        return $"hwnd={root} pid={pid} {who}";
    }

    /// <summary>
    /// 抓屏那块缓冲是复用的（自检有一条盯着它），后台不能直接读，所以拷一份再交出去。
    /// 编码写盘也不能留在首拍之前：一张选区图编成 PNG 要几百毫秒，四张压在一起，
    /// 用户点了「长截图」就得对着静止的屏幕干等一秒多。留证可以慢，首拍不行。
    /// </summary>
    private void DumpFrameForEvidence(byte[] buffer, int stride, string fileName)
    {
        var copy = new byte[stride * _region.Height];
        Buffer.BlockCopy(buffer, 0, copy, 0, copy.Length);
        WriteEvidenceOffThread(copy, stride, fileName);
    }

    private void WriteEvidenceOffThread(byte[] pixels, int stride, string fileName)
    {
        var region = _region;   // 带上这一刻的选区：中途翻方向改了 _region，后台也不会按新尺寸解旧图
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var bmp = BitmapSource.Create(region.Width, region.Height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, stride);
                bmp.Freeze();
                System.IO.Directory.CreateDirectory(DiagDirectory);
                var error = Exporter.TryWrite(bmp, System.IO.Path.Combine(DiagDirectory, fileName));
                Log.Write(error == null ? $"长截图抓屏留证 {fileName}" : $"长截图抓屏留证失败：{error}");
            }
            catch (Exception ex)
            {
                Log.Error("长截图抓屏留证失败", ex);
            }
        });
    }

    public void SetDirection(bool horizontal)
    {
        _horizontal = horizontal;
        _region = _session.FinalRect();
        _stitcher = null;
        _stableTicks = 0;
        _frame.UpdateRegion(_region);
        // 换了方向选区可能换了一台显示器，暗带也换了位置：预览重新挑地方安家。
        _preview.Reset(horizontal);
        _preview.Attach(_session.Displays, _region);
    }

    public void ToggleAutoScroll()
    {
        _autoScroll = !_autoScroll;
        _bar.SetAutoScroll(_autoScroll);
        _stableTicks = 0;
    }

    /// <summary>
    /// 第一屏用截图层留的那张快照，不用首拍的实时帧。
    /// 覆盖层刚藏起来那阵，走显卡合成的窗口（浏览器最典型）常常还留着一块没重绘的黑——
    /// 用户看到的就是「长截图一开始左上角有个黑方块，滚一下就没了」。实时帧把它烤进第一屏，
    /// 之后滚得再干净，长图开头那块黑也永远在。快照是覆盖层出现之前抓的，没有这层残影。
    /// </summary>
    private LongStitcher? SeedFromSnapshot(int reserved)
    {
        try
        {
            // 翻过方向之后选区可能探出快照范围，那时只能退回实时首帧。
            var clipped = _session.Snapshot.Intersect(_region);
            if (clipped.Width != _region.Width || clipped.Height != _region.Height) return null;
            var pixels = _session.Snapshot.CopyRegion(_region);
            Log.Write($"长截图第一屏取自截图层快照（{_region.Width}x{_region.Height}，底栏预留 {reserved} 行）");
            return new LongStitcher(_region, pixels, _region.Width * 4, reserved);
        }
        catch (Exception ex)
        {
            Log.Error("长截图取快照当第一屏失败，退回实时首帧", ex);
            return null;
        }
    }

    private void Poll()
    {
        if (_finished) return;

        // GDI 位图、Graphics、托管帧缓冲都是抓屏器自己留着的：区域尺寸不变就一直复用，
        // 不再每一拍新配三块 25MB——长截图期间内存爬坡的主源就是原来那个「每拍全换」。
        var (buffer, stride) = _grabber.Grab(_region);

        // 自家的悬浮窗（工具栏、预览）要是压在选区里，GDI 抓屏会把它们一起抓进来：
        // 工具栏的像素被烤进长图，预览更是每一拍都在变，行哈希全被搅乱，
        // 拼接器认不出滚动量就整帧整帧往画布上贴——滚到底了还在自己拼就是它。
        // 所以统一把这些矩形填成恒定纯色：哈希稳了，长图里也只是一块纯色而不是我们的界面。
        // 压在选区底边的那条更干脆：告诉拼接器「这几行是自家像素」，从第一帧起就不进画布。
        bool occluded = false;
        int reserved = 0;
        foreach (var rect in OccludingRects())
        {
            MaskRect(buffer, stride, _region, rect);
            occluded = true;
            if (rect.Y + rect.Height >= _region.Y + _region.Height)
                reserved = Math.Max(reserved, rect.Height);
        }
        // 预览压在选区底边时它还会往长里长：按长满的预算整段预留，不按当前高度——
        // 不然超出当前预留的那一截又把行哈希搅了。
        double previewBudget = _preview.IsVisible ? _preview.BottomBandBudgetDip : 0;
        if (previewBudget > 0)
            reserved = Math.Max(reserved, (int)Math.Round(previewBudget * _session.Displays[0].Scale));

        if (_stitcher == null)
        {
            _stitcher = SeedFromSnapshot(reserved) ?? new LongStitcher(_region, buffer, stride, reserved);
            DumpFrameForEvidence(buffer, stride, "first-frame.png");
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
            _bar.SetStatus($"已拼接 {_stitcher.Height} 像素高" +
                           (reserved > 0 ? "（工具栏/预览压在选区底边：收尾时会补拍这一条）"
                           : occluded ? "（自家悬浮窗压在选区里：长图里会留一块纯色）" : ""));
        }

        _preview.UpdateStitch(_stitcher, _region);
    }

    /// <summary>
    /// 收尾补拍：工具栏/预览压在选区底边时，那几行从第一帧起整段预留、永远不进画布。
    /// 滚动途中下面的内容会升上来被拼到，可页面一滚到底，被压住的那一截就再也拍不着——
    /// 「框到底、也滚到底，长图还是少最后一条」就是它。所以收尾时把自家悬浮窗藏起来，
    /// 等合成翻篇、把被盖住的页面踢一下重绘，补一帧干净的接上。
    /// 没预留过底栏就整个跳过：那种情况本来就一路拍到了，工具栏也不必闪一下。
    /// </summary>
    internal async System.Threading.Tasks.Task CaptureTailAsync()
    {
        if (_stitcher == null || _stitcher.ReservedFooter <= 0) return;

        int reserved = _stitcher.ReservedFooter;
        _timer.Stop();
        bool barWas = _bar.IsVisible, previewWas = _preview.IsVisible;
        _bar.Hide();
        _preview.Hide();
        try
        {
            await System.Threading.Tasks.Task.Delay(300);
            if (!KickCoveredPage(reserved))
            {
                Log.Write("长截图收尾补拍放弃：藏掉工具栏之后那一块仍被自家窗口压着，接上去就是把按钮烤进长图");
                return;
            }
            await System.Threading.Tasks.Task.Delay(220);

            var (buffer, stride) = _grabber.Grab(_region);
            DumpFrameForEvidence(buffer, stride, "tail-frame.png");
            int before = _stitcher.Height;
            int appended = _stitcher.AppendTail(buffer, stride, _region.Height);
            Log.Write(appended > 0
                ? $"长截图收尾补拍：底栏 {reserved} 行接回 {appended} 行（画布 {before} → {_stitcher.Height}）"
                : $"长截图收尾补拍：这一帧没有落在画布外的行（底栏 {reserved} 行，画布 {before}）——原因见拼接判定末尾");
        }
        catch (Exception ex)
        {
            Log.Error("长截图收尾补拍失败", ex);
        }
        finally
        {
            // 收尾没成（复制失败、存盘点了取消）还要接着拼，工具栏和预览得原地回来。
            // 成了的话调用方紧接着 Close()，中间不让出线程，看不见这一下。
            if (!_closed)
            {
                if (barWas) _bar.Show();
                if (previewWas) _preview.Show();
            }
        }
    }

    /// <summary>
    /// 把刚被自家悬浮窗盖住的那块页面踢一下重绘（藏窗之后合成器不一定马上翻篇），
    /// 并确认那块真的露出来了：还压着自家工具栏/预览就报 false，这一帧不能接。
    /// </summary>
    private bool KickCoveredPage(int reservedRows)
    {
        int cx = _region.X + _region.Width / 2;
        int cy = _region.Y + _region.Height - Math.Max(1, reservedRows / 2);
        var target = Win32.WindowFromPoint(new Win32.POINT { X = cx, Y = cy });
        if (target == nint.Zero) return true;   // 认不出是谁，但不是自家悬浮窗挡着

        var root = Win32.GetAncestor(target, Win32.GA_ROOTOWNER);
        var bar = new System.Windows.Interop.WindowInteropHelper(_bar).Handle;
        var preview = new System.Windows.Interop.WindowInteropHelper(_preview).Handle;
        if (root == bar || root == preview) return false;

        Win32.RedrawWindow(target, nint.Zero, nint.Zero,
            Win32.RDW_INVALIDATE | Win32.RDW_ERASE | Win32.RDW_ALLCHILDREN | Win32.RDW_FRAME | Win32.RDW_UPDATENOW);
        Win32.PostMessage(target, Win32.WM_MOUSEMOVE, nint.Zero, Win32.ScreenPointLParam(cx, cy));
        return true;
    }

    /// <summary>压在选区里的自家悬浮窗（工具栏、预览），换算成物理像素并裁进选区的矩形。</summary>
    private IEnumerable<Int32Rect> OccludingRects()
    {
        double scale = _session.Displays[0].Scale;
        foreach (var w in new Window[] { _bar, _preview })
        {
            if (w == null || !w.IsVisible || w.ActualWidth < 1 || w.ActualHeight < 1) continue;
            var r = new Int32Rect(
                (int)Math.Round(w.Left * scale), (int)Math.Round(w.Top * scale),
                (int)Math.Round(w.ActualWidth * scale), (int)Math.Round(w.ActualHeight * scale));
            int x1 = Math.Max(r.X, _region.X), y1 = Math.Max(r.Y, _region.Y);
            int x2 = Math.Min(r.X + r.Width, _region.X + _region.Width);
            int y2 = Math.Min(r.Y + r.Height, _region.Y + _region.Height);
            if (x2 > x1 && y2 > y1) yield return new Int32Rect(x1, y1, x2 - x1, y2 - y1);
        }
    }

    /// <summary>
    /// 把帧里 rect ∩ frame 的部分填成恒定纯色（深灰）。帧坐标系 = 区域坐标系，起点在 frame.X/Y。
    /// internal 供自检直测：矩形内必须变成纯色，矩形外必须一字不动。
    /// </summary>
    internal static void MaskRect(byte[] buffer, int stride, Int32Rect frame, Int32Rect rect)
    {
        int x1 = Math.Max(rect.X, frame.X), y1 = Math.Max(rect.Y, frame.Y);
        int x2 = Math.Min(rect.X + rect.Width, frame.X + frame.Width);
        int y2 = Math.Min(rect.Y + rect.Height, frame.Y + frame.Height);
        for (int y = y1; y < y2; y++)
        {
            int start = (y - frame.Y) * stride + (x1 - frame.X) * 4;
            for (int x = 0; x < (x2 - x1) * 4; x += 4)
            {
                buffer[start + x] = 0x20;
                buffer[start + x + 1] = 0x20;
                buffer[start + x + 2] = 0x20;
                buffer[start + x + 3] = 0xFF;
            }
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

    private bool _finishing;

    /// <summary>
    /// 收尾第一步：补拍最后一帧，期间不再按节奏抓帧。
    /// 已经在收尾途中（补拍要几百毫秒）就把后到的点击吃掉，免得同一趟收两次。
    /// </summary>
    private async System.Threading.Tasks.Task<bool> BeginFinishAsync()
    {
        if (_finishing) return false;
        _finishing = true;
        _finished = true;
        await CaptureTailAsync();
        return true;
    }

    /// <summary>这一趟没收成（复制失败、存盘点了取消）：退回接着拼的状态。</summary>
    private void AbortFinish()
    {
        _finished = false;
        _finishing = false;
        _timer.Start();
    }

    public async void FinishToClipboard()
    {
        if (!await BeginFinishAsync()) return;
        var image = BuildResult();
        if (image == null)
        {
            AbortFinish();
            _bar.ShowResult("还没抓到任何一帧：先滚一段页面，再点完成", true);
            return;
        }
        Record(image);
        var error = Exporter.TryCopyImage(image);
        if (error != null)
        {
            // 复制没成就不收摊：拼接结果还在，重试和保存都点得到。
            AbortFinish();
            _bar.ShowResult($"复制到剪贴板失败：{error}", true, "重试复制", FinishToClipboard);
            return;
        }
        _finishing = false;
        Close();
        _exitCapture();
    }

    public async void FinishToFile()
    {
        if (!await BeginFinishAsync()) return;
        var image = BuildResult();
        if (image == null)
        {
            AbortFinish();
            _bar.ShowResult("还没抓到任何一帧：先滚一段页面，再点保存", true);
            return;
        }
        Record(image);
        var controller = _session.Controller;
        var path = Exporter.SaveDialog(image, App.Config, out var writeError);
        if (writeError != null)
        {
            AbortFinish();
            _bar.ShowResult($"保存失败：{writeError}", true, "换个位置再存", FinishToFile);
            return;
        }
        if (path == null) { AbortFinish(); return; }   // 点了取消，长截图照旧接着拼
        _finishing = false;
        Close();
        _exitCapture();
        if (App.Config.CopyAfterSave)
        {
            var copyError = Exporter.TryCopyImage(image);
            if (copyError != null) controller?.NotifyProblem("没能顺带复制", $"{copyError}；文件已经存好了");
        }
        controller?.NotifySaved(path);
    }

    private static void Record(BitmapSource image) =>
        HistoryStore.Shared.AddLater(image, HistoryStore.SourceLong);

    public async void Edit()
    {
        if (!await BeginFinishAsync()) return;
        var image = BuildResult();
        if (image == null) { Cancel(); return; }
        _finishing = false;
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
        _finished = true;
        _closed = true;
        _timer.Stop();
        _bar.Close();
        _frame.Close();
        _preview.Close();
        _grabber.Dispose();
    }

    public void Dispose()
    {
        _finished = true;
        _closed = true;
        _timer.Stop();
        _frame.Close();
        _bar.Close();
        _preview.Close();
        _grabber.Dispose();
    }
}

/// <summary>
/// 长截图的实时预览：贴在屏幕右上角，图拼到哪儿它就长到哪儿，蓝框标出画面此刻停在长图的哪一段。
/// 自己维护一张「预览分辨率」的小位图，每一拍把整幅画布按行抽稀铺进去（画布比预览还短时就 1:1）。
/// 不给长图做全量拷贝——拼接画布本身已经是几十上百 MB 那份，预览每拍再复制一份，内存和节奏都扛不住。
/// 窗口完全点击穿透：预览只是给人看的，绝不能挡住下面页面的滚动。
/// </summary>
internal sealed class LongPreviewWindow : Window
{
    private const double ShortDip = 190;    // 预览短边（纵向是宽、横向是高）——暗带够宽时的上限
    private const double EdgeMargin = 14;   // 离屏幕上/右边缘的距离
    private const double MinStripDip = 90;  // 选区外的暗带至少要这么高才放得下最小号预览，再窄才藏

    private readonly Image _image;
    private readonly Border _cursor;
    private WriteableBitmap? _bmp;
    private byte[]? _scratch;               // 抽稀重铺用的整幅缓冲，常驻不重配，避免每拍上大对象堆
    private bool _horizontal;
    private double _shortDip = ShortDip;    // 实际用的短边：暗带窄就缩一号，尽量不整条藏
    private int _capPx;                     // 拼接轴的位图像素上限（≈屏幕能给的 DIP 高度，1px≈1DIP）
    private double _capDip;                 // 拼接轴的显示 DIP 上限
    private double _topDip;                 // 落在上方暗带时的固定顶边
    private double _bottomDip;              // 落在下方暗带时的固定底边（长的时候往上长）
    private bool _bottomAnchored;
    private bool _inside;                   // 暗带连最小号都放不下：压进选区底边右角（预留带里）
    private double _insideRightDip;         // 压边模式：钉在选区右缘/底缘（屏幕 DIP）
    private double _insideBottomDip;
    private Display _display = null!;
    private double _lastDisplayLen;
    private int _lastCanvasLen;             // 上一拍铺进位图时的画布长度：没长就整拍跳过

    public LongPreviewWindow(bool horizontal)
    {
        _horizontal = horizontal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;

        _image = new Image { Stretch = Stretch.Fill };
        _cursor = new Border
        {
            BorderBrush = Theme.Brush(Theme.Accent),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        var overlay = new Canvas();
        overlay.Children.Add(_cursor);
        var grid = new Grid();
        grid.Children.Add(_image);
        grid.Children.Add(overlay);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)) { Opacity = 0.9 },
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            Child = grid
        };
    }

    /// <summary>自检用：预览此刻显示出来的拼接轴长度（DIP）。</summary>
    internal double DisplayedLengthDip => _lastDisplayLen;

    /// <summary>
    /// 压边模式下预览长满要占的高度（窗口整体，DIP）；拼接器按这个预算把选区底边整段留成固定底栏。
    /// 不压边就是 0。
    /// </summary>
    internal double BottomBandBudgetDip => _inside ? _capDip + 12 : 0;

    /// <summary>换了方向或重开一轮：画布横竖换了轴，位图得推倒重来。</summary>
    public void Reset(bool horizontal)
    {
        _horizontal = horizontal;
        _bmp = null;
        _scratch = null;
        _inside = false;
        _cursor.Visibility = Visibility.Collapsed;
    }

    public void Attach(IReadOnlyList<Display> displays, Int32Rect region)
    {
        _display = displays.FirstOrDefault(d =>
            d.Contains(region.X + region.Width / 2, region.Y + region.Height / 2)) ?? displays[0];
        double scale = _display.Scale;
        double top = _display.Bounds.Y / scale, height = _display.Bounds.Height / scale;
        double regionTop = (region.Y - _display.Bounds.Y) / scale;
        double regionBottom = (region.Bottom() - _display.Bounds.Y) / scale;
        double regionHeightDip = region.Height / scale;

        // 预览绝不能压在选区里被拼进长图……除非它压在选区底边、而那条底边整段被拼接器
        // 预留成固定底栏——那些行从第一帧起就不进画布，预览怎么变都搅不了行哈希。
        // 所以安家顺序：上方暗带 / 下方暗带挑宽的 → 都太窄就压进选区底边右角（长边封顶在预留带高度里）。
        double topStrip = regionTop - top;
        double bottomStrip = top + height - regionBottom;
        double strip = Math.Max(topStrip, bottomStrip);
        if (strip >= MinStripDip)
        {
            _inside = false;
            _bottomAnchored = bottomStrip > topStrip;
            if (_bottomAnchored) _bottomDip = top + height - 6;
            else _topDip = top + 6;
            _capDip = strip - 20;
            _shortDip = Math.Clamp(strip - 24, 60, ShortDip);
        }
        else
        {
            double budget = Math.Min(220, regionHeightDip / 3);
            if (budget < 70) { Hide(); _capDip = 0; return; }   // 选区矮到连压边都放不下才真的藏
            _inside = true;
            _bottomAnchored = true;
            _insideBottomDip = region.Bottom() / scale;
            _insideRightDip = region.Right() / scale;
            _bottomDip = _insideBottomDip;
            _capDip = budget - 8;
            _shortDip = ShortDip;
        }
        _capPx = (int)Math.Min(_capDip, 1600);   // 位图 1px≈1DIP 就够看，再大只是白吃内存
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 每一拍把画布重铺进预览位图：目标行 j 显示画布行 j*canvasLen/mappedRows（画布短于上限时就是 1:1）。
    /// 画布每拍都在长，映射随之整体移动，所以纵向追加那种增量写法在这里不成立，整幅重铺反而是最省事的正确做法。
    /// </summary>
    public void UpdateStitch(LongStitcher? stitcher, Int32Rect region)
    {
        if (stitcher == null || _display == null || !IsVisible) return;
        int canvasLen = _horizontal ? stitcher.Width : stitcher.Height;
        int crossLen = _horizontal ? stitcher.Height : stitcher.Width;
        if (canvasLen <= 0 || crossLen <= 0) return;
        if (canvasLen == _lastCanvasLen && _bmp != null) return;   // 一帧没长：画面和蓝框都不会动，白铺一遍
        _lastCanvasLen = canvasLen;

        try
        {
            int mappedRows = Math.Min(canvasLen, _capPx);
            EnsureBitmap(crossLen, stitcher.Stride);

            if (_horizontal)
            {
                int bmpStride = _bmp!.PixelWidth * 4;
                Array.Clear(_scratch!, 0, _scratch!.Length);
                int columns = Math.Min(_bmp.PixelWidth, canvasLen);
                for (int x = 0; x < columns; x++)
                {
                    int srcX = (int)((long)x * canvasLen / columns);
                    stitcher.CopyCanvasColumns(srcX, 1, _scratch!, bmpStride, x * 4);
                }
                _bmp.WritePixels(new Int32Rect(0, 0, _bmp.PixelWidth, crossLen), _scratch!, bmpStride, 0);
            }
            else
            {
                int stride = stitcher.Stride;
                for (int j = 0; j < mappedRows; j++)
                {
                    int srcRow = (int)((long)j * canvasLen / mappedRows);
                    stitcher.CopyCanvasRows(srcRow, 1, _scratch!, j * stride);
                }
                _bmp!.WritePixels(new Int32Rect(0, 0, stitcher.Width, mappedRows), _scratch!, stride, 0);
            }
            _image.Source = _bmp;

            // 显示尺寸：短边固定，长边按真实比例长，长到屏幕装不下就封顶。
            double lenDip = Math.Min(_capDip, _shortDip * canvasLen / crossLen);
            _lastDisplayLen = lenDip;
            if (_horizontal)
            {
                _image.Height = _shortDip;
                _image.Width = lenDip;
            }
            else
            {
                _image.Width = _shortDip;
                _image.Height = lenDip;
            }

            // 蓝框：当前实时画面在整幅长图里的位置。取最后一帧对应的那一段（近似，够指路用）。
            int viewportLen = _horizontal ? region.Width : region.Height;
            if (viewportLen > 0 && viewportLen < canvasLen)
            {
                double ratio = lenDip / canvasLen;
                double offset = (canvasLen - viewportLen) * ratio;
                _cursor.Visibility = Visibility.Visible;
                if (_horizontal)
                {
                    Canvas.SetLeft(_cursor, offset);
                    Canvas.SetTop(_cursor, 0);
                    _cursor.Width = Math.Max(2, viewportLen * ratio);
                    _cursor.Height = _shortDip;
                }
                else
                {
                    Canvas.SetLeft(_cursor, 0);
                    Canvas.SetTop(_cursor, offset);
                    _cursor.Width = _shortDip;
                    _cursor.Height = Math.Max(2, viewportLen * ratio);
                }
            }
            else _cursor.Visibility = Visibility.Collapsed;

            // 右缘钉死、往左生长；纵向位置看安家在哪条暗带：下方暗带顶边固定，上方暗带底边固定往上长。
            // 压边模式钉在选区底边右角：右缘=选区右缘往里缩，底缘=选区底边（整条都在预留带里）。
            double right = _inside ? _insideRightDip
                                   : (_display.Bounds.X + _display.Bounds.Width) / _display.Scale;
            Left = right - ActualWidth - EdgeMargin;
            Top = _bottomAnchored ? _bottomDip - ActualHeight : _topDip;
        }
        catch (Exception ex)
        {
            // 预览是配菜，绝不能把拼接本体带崩：这一拍画不了就下一拍再说。
            Log.Error("长截图预览更新失败", ex);
            _cursor.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsureBitmap(int crossLen, int canvasStride)
    {
        int bmpW = _horizontal ? _capPx : canvasStride / 4;   // 纵向：位图宽=画布宽；横向：位图宽=上限
        int bmpH = _horizontal ? crossLen : _capPx;
        if (_bmp != null && _bmp.PixelWidth == bmpW && _bmp.PixelHeight == bmpH) return;
        _bmp = new WriteableBitmap(bmpW, bmpH, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        _scratch = new byte[bmpW * 4 * bmpH];
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
        UpdateLayout();   // SizeToContent 的实际高宽要等一轮布局才有，否则落位全程按 0 算
        var display = displays.FirstOrDefault(d => d.Contains(region.X + region.Width / 2, region.Bottom())) ?? displays[0];
        double w = ActualWidth > 0 ? ActualWidth : 420;
        double left = (region.X + region.Width / 2d - display.Bounds.X) / display.Scale - w / 2;
        Top = PickBarTop(
            (region.Y - display.Bounds.Y) / display.Scale,
            (region.Bottom() - display.Bounds.Y) / display.Scale,
            display.Bounds.Y / display.Scale,
            (display.Bounds.Y + display.Bounds.Height) / display.Scale,
            ActualHeight);

        Left = Math.Clamp(left, display.Bounds.X / display.Scale + 4, (display.Bounds.X + display.Bounds.Width) / display.Scale - w - 4);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) | Win32.WS_EX_TOOLWINDOW);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        _ = scale;
    }

    /// <summary>
    /// 工具栏落位：选区下方 → 选区上方 → 压进选区底边内侧。
    /// 以前最后一档是兜到屏幕顶上，工具栏压住选区顶部：每一帧都被抓进去、掩成一块纯色，
    /// 黑框就这么烤在长图顶上了。压底边没事——拼接器把这一条当固定底栏预留，一帧都不进长图。
    /// internal 供自检直测。
    /// </summary>
    internal static double PickBarTop(double regionTop, double regionBottom, double screenTop, double screenBottom, double barHeight)
    {
        double below = regionBottom + 10;
        if (below + barHeight <= screenBottom - 4) return below;
        double above = regionTop - barHeight - 10;
        if (above >= screenTop + 4) return above;
        return Math.Clamp(regionBottom - barHeight, screenTop + 4,
            Math.Max(screenTop + 4, screenBottom - 4 - barHeight));
    }

    public void SetAutoScroll(bool on)
    {
        if (on) _strip.ToggleAutoScrollOn();
        else _strip.ToggleAutoScrollOff();
    }

    public void SetStatus(string text) => Dispatcher.Invoke(() => _status.Text = text);

    /// <summary>出图结果挂在工具栏下面那句红/白回执上，和截图层同一个口径。</summary>
    public void ShowResult(string text, bool bad, string? actionLabel = null, Action? action = null)
        => Dispatcher.Invoke(() => _strip.ShowStatus(text, bad, actionLabel, action));
}
