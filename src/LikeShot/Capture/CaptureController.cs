using System.Runtime;
using System.Windows;
using System.Windows.Media.Imaging;
using LikeShot.UI;

namespace LikeShot.Capture;

/// <summary>
/// 截图会话的总控：拉快照、开覆盖层、把「完成/保存/钉图/长截图」这些出口接起来。
/// 出口统一按「先入库、再出图、失败就留在截图层说一句」走——
/// 图先落到历史里，后面复制或保存再怎么失败，都不会真的丢东西。
/// </summary>
public sealed class CaptureController
{
    private readonly Application _app;
    private List<CaptureOverlay> _overlays = [];
    private CaptureSession? _session;
    private LongCaptureRunner? _longRunner;
    private readonly List<PinWindow> _pins = [];
    private bool _replayMode;

    public CaptureController(Application app) => _app = app;

    public bool IsActive => _session != null;

    public void BeginCapture()
    {
        if (_session != null) return;
        StartSession(ScreenSnapshot.GrabVirtualDesktop(), Display.All(), null, replay: false);
    }

    /// <summary>
    /// 把一张历史截图丢回截图层接着画（再标注）。
    /// 比整台显示器还大的图（长截图）没法 1:1 铺开，那种交给钉图窗——它能缩放，标注机器是同一套。
    /// </summary>
    public void BeginReplay(BitmapSource image)
    {
        if (_session != null) return;
        var all = Display.All();
        if (all.Count == 0) return;
        var target = all.FirstOrDefault(d => d.IsPrimary) ?? all[0];

        if (image.PixelWidth > target.PhysicalWidth || image.PixelHeight > target.PhysicalHeight)
        {
            Log.Write($"再标注改走钉图窗：图={image.PixelWidth}x{image.PixelHeight} 屏幕={target.PhysicalWidth}x{target.PhysicalHeight}");
            Pin(image, new Int32Rect(target.Bounds.X + 80, target.Bounds.Y + 80, image.PixelWidth, image.PixelHeight));
            return;
        }

        int x = target.Bounds.X + (target.Bounds.Width - image.PixelWidth) / 2;
        int y = target.Bounds.Y + (target.Bounds.Height - image.PixelHeight) / 2;
        var rect = new Int32Rect(x, y, image.PixelWidth, image.PixelHeight);
        StartSession(ScreenSnapshot.FromImageOnDesk(image, target.Bounds, rect), all, rect, replay: true);
    }

    private void StartSession(ScreenSnapshot snapshot, IReadOnlyList<Display> allDisplays, Int32Rect? preselect, bool replay)
    {
        // 快照盖住哪几台显示器就在哪几台开遮罩层：再标注的画布只有一台那么宽，
        // 给别的屏也开一层只会得到一块涂了底色的死屏。
        var vb = snapshot.VirtualBounds;
        var displays = allDisplays.Where(d =>
            d.Bounds.X < vb.Right() && vb.X < d.Bounds.Right() &&
            d.Bounds.Y < vb.Bottom() && vb.Y < d.Bounds.Bottom()).ToList();
        if (displays.Count == 0) return;

        _replayMode = replay;
        double uiScale = displays.FirstOrDefault(d => d.IsPrimary)?.Scale ?? displays[0].Scale;
        _session = new CaptureSession(snapshot, displays, uiScale);
        _session.Controller = this;
        _session.FinishRequested += OnFinish;
        _session.ExitRequested += Abort;
        _session.LongModeRequested += BeginLongCapture;

        foreach (var display in displays)
        {
            var overlay = new CaptureOverlay(_session, display);
            _overlays.Add(overlay);
            _session.Overlays.Add(overlay);
        }

        foreach (var overlay in _overlays) overlay.FinishSetup();
        if (preselect is { } rect) _session.SelectRect(rect);
    }

    public void Abort()
    {
        _longRunner?.Dispose();
        _longRunner = null;

        foreach (var overlay in _overlays)
        {
            try { overlay.Close(); } catch { /* 已经关掉了就算成功 */ }
        }
        _overlays = [];

        if (_session != null)
        {
            _session = null;
        }

        TrimWorkingSet();
    }

    /// <summary>
    /// 收摊压堆：整屏那份位图（3200×2000 就是 25MB）加上放大镜、打码底图那些一次性的整块缓冲，
    /// 都落在大对象堆上；.NET 收掉之后不主动把段还给系统，任务管理器里的数字就一直挂着。
    /// 收摊时压一次堆，让下一次截图用同一块，而不是接着往上要。
    /// GC 收完只是「可用」，任务管理器里的数字要等系统自己来要页面才掉。
    /// 轻量常驻软件的惯例：收摊时主动把工作集还回去，下次用哪页再进哪页。
    /// </summary>
    private static void TrimWorkingSet()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        LikeShot.Native.Win32.SetProcessWorkingSetSize(
            LikeShot.Native.Win32.GetCurrentProcess(), -1, -1);
    }

    /// <summary>入库后的自动收摊跑了几回；自检用它断言这条链路活着。</summary>
    internal static int SettlesForTest { get; private set; }

    /// <summary>
    /// Abort 里那次收拾接不到最后一批垃圾：历史入库排在空闲优先级，
    /// PNG 编码、缩略图、剪贴板转位图这些最大块的缓冲都是入库/复制跑起来才分配的。
    /// 所以入库跑完再补一次，每截一张都回到干净的低水位，而不是越截越高。
    /// </summary>
    internal static void SettleAfterHistoryWrite() => Settle();

    /// <summary>
    /// 大块 UI 收摊后也压一次堆：设置页这种一开就是几十 MB 的窗口，关掉之后
    /// 垃圾 GC 自己会收，但大对象堆不压、工作集不还，任务管理器里的数字就一直挂着。
    /// 排到 ContextIdle：等窗口真正拆完再收。
    /// </summary>
    internal static void ScheduleSettle()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { Settle(); return; }
        dispatcher.InvokeAsync(Settle, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static void Settle()
    {
        // 截图层还开着（手快连截）就跳过——截屏进行中压堆只会白卡一下，等 Abort 再收。
        if (Application.Current?.Windows.OfType<CaptureOverlay>().Any() == true) return;
        TrimWorkingSet();
        SettlesForTest++;
    }

    private void OnFinish(FinishRequest request)
    {
        if (_session == null) return;
        // 编辑框还开着就先落字：不然正打一半的字丢了，双击改字时旧字还卡在隐藏态。
        foreach (var overlay in _overlays) overlay.CommitPendingText();

        var image = _session.RenderResult();
        if (image == null)
        {
            Say("选区已经不在屏幕上，没有可导出的图", true, "重新框选", () => _session?.ResetSelection());
            return;
        }

        // 入库排在出图之前发起（真正写盘放到空闲优先级，不挡收摊）：
        // 后面复制或保存再怎么失败，这张图都还能从「设置 → 历史截图」里找回来。
        HistoryStore.Shared.AddLater(image, _replayMode ? HistoryStore.SourceReplay : HistoryStore.SourceCapture);

        switch (request.Mode)
        {
            case CopyMode.Clipboard:
                var copyError = Exporter.TryCopyImage(image);
                if (copyError != null)
                {
                    Say($"复制到剪贴板失败：{copyError}", true, "重试复制", () => OnFinish(request));
                    return;
                }
                Abort();
                break;

            case CopyMode.File:
                // 模态保存框会被 topmost 的截图层压住，先把遮罩层收起来；
                // 存坏了不关会话，让人能在原地换个位置再存一次。
                foreach (var overlay in _overlays) overlay.SetDialogVisible(false);
                var saved = Exporter.SaveDialog(image, App.Config, out var saveError);
                foreach (var overlay in _overlays) overlay.SetDialogVisible(true);
                if (saveError != null)
                {
                    Say($"保存失败：{saveError}", true, "换个位置再存", () => OnFinish(request));
                    return;
                }
                if (saved == null) return;   // 用户自己取消：没有结果要报，回到编辑态等着
                Abort();
                if (App.Config.CopyAfterSave)
                {
                    var afterSaveCopy = Exporter.TryCopyImage(image);
                    if (afterSaveCopy != null) NotifyProblem("没能顺带复制", $"{afterSaveCopy}；文件已经存好了");
                }
                NotifySaved(saved);
                break;

            case CopyMode.Pin:
                var anchor = _session.FinalRect();
                Abort();
                Pin(image, anchor);
                break;
        }
    }

    /// <summary>把一句回执挂在带工具栏那一层的下方；宿主找不到就不报（会话已经关了）。</summary>
    private void Say(string text, bool bad, string? actionLabel = null, Action? action = null)
    {
        var host = _overlays.FirstOrDefault(o => o.OwnsToolbarAnchor) ?? _overlays.FirstOrDefault();
        host?.ShowStatus(text, bad, actionLabel, action);
    }

    /// <summary>自检用：不走失败路径也能把回执那条渲染出来看一眼版式。</summary>
    internal void SayForTest(string text, bool bad, string? actionLabel = null, Action? action = null) =>
        Say(text, bad, actionLabel, action);

    public void Pin(BitmapSource image, Int32Rect sourceRect)
    {
        Log.Write($"钉图 源区={sourceRect.Width}x{sourceRect.Height}@{sourceRect.X},{sourceRect.Y} 图={image.PixelWidth}x{image.PixelHeight}");
        var pin = new PinWindow(image, sourceRect, App.Config);
        pin.Closed += (_, _) => _pins.Remove(pin);
        _pins.Add(pin);
        pin.ShowPinned();
    }

    /// <summary>历史里一键钉到桌面：钉在光标所在那台显示器的正中。</summary>
    public void PinFromHistory(BitmapSource image)
    {
        var cursor = CaptureSession.CurrentPhysicalCursor();
        var display = Display.NearestTo((int)cursor.X, (int)cursor.Y);
        int x = display.Bounds.X + Math.Max(0, (display.Bounds.Width - image.PixelWidth) / 2);
        int y = display.Bounds.Y + Math.Max(0, (display.Bounds.Height - image.PixelHeight) / 2);
        Pin(image, new Int32Rect(x, y, image.PixelWidth, image.PixelHeight));
    }

    private void BeginLongCapture()
    {
        if (_session == null) return;
        _longRunner = new LongCaptureRunner(_session, Abort);
        _longRunner.Start();
    }

    /// <summary>自检用：直接进长截图模式并拿到 runner。</summary>
    internal void BeginLongCaptureForTest() => BeginLongCapture();

    internal LongCaptureRunner? RunnerForTest => _longRunner;

    public void NotifySaved(string path) => _savedNotify?.Invoke(path);

    /// <summary>截图层已经关掉、界面上没地方挂回执的那类失败，走托盘气泡。</summary>
    public void NotifyProblem(string title, string message)
    {
        Log.Write($"[提示] {title}：{message}");
        _problemNotify?.Invoke(title, message);
    }

    private Action<string>? _savedNotify;
    private Action<string, string>? _problemNotify;

    public void SetNotifier(Action<string> saved, Action<string, string> problem)
    {
        _savedNotify = saved;
        _problemNotify = problem;
    }

    /// <summary>给自检用：拿到当前会话对象。</summary>
    internal CaptureSession? SessionForTest => _session;

    internal IReadOnlyList<CaptureOverlay> OverlaysForTest => _overlays;
}
