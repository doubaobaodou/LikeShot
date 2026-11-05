using System.Windows;
using System.Windows.Media;
using LikeShot.UI;

namespace LikeShot.Capture;

/// <summary>
/// 截图会话的总控：拉快照、开覆盖层、把「完成/保存/钉图/长截图」这些出口接起来。
/// </summary>
public sealed class CaptureController
{
    private readonly Application _app;
    private List<CaptureOverlay> _overlays = [];
    private CaptureSession? _session;
    private LongCaptureRunner? _longRunner;
    private readonly List<PinWindow> _pins = [];

    public CaptureController(Application app) => _app = app;

    public bool IsActive => _session != null;

    public void BeginCapture()
    {
        if (_session != null) return;

        var snapshot = ScreenSnapshot.GrabVirtualDesktop();
        var displays = Display.All();
        if (displays.Count == 0)
        {
            snapshot.Dispose();
            return;
        }

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
            _session.Snapshot.Dispose();
            _session = null;
        }
    }

    private void OnSessionRefreshed()
    {
        // 覆盖层自己在 Changed 里重绘，这里只处理跨层的收尾。
    }

    private void OnFinish(FinishRequest request)
    {
        if (_session == null) return;
        // 编辑框还开着就先落字：不然正打一半的字丢了，双击改字时旧字还卡在隐藏态。
        foreach (var overlay in _overlays) overlay.CommitPendingText();
        var image = _session.RenderResult();

        switch (request.Mode)
        {
            case CopyMode.Clipboard:
                Abort();
                Exporter.CopyImage(image);
                break;

            case CopyMode.File:
                var saved = Exporter.SaveDialog(image, App.Config);
                Abort();
                if (saved != null)
                {
                    if (App.Config.CopyAfterSave) Exporter.CopyImage(image);
                    NotifySaved(saved);
                }
                break;

            case CopyMode.Pin:
                var anchor = _session.FinalRect();
                Abort();
                Pin(image, anchor);
                break;
        }
    }

    public void Pin(System.Windows.Media.Imaging.BitmapSource image, Int32Rect sourceRect)
    {
        Log.Write($"钉图 源区={sourceRect.Width}x{sourceRect.Height}@{sourceRect.X},{sourceRect.Y} 图={image.PixelWidth}x{image.PixelHeight}");
        var pin = new PinWindow(image, sourceRect, App.Config);
        pin.Closed += (_, _) => _pins.Remove(pin);
        _pins.Add(pin);
        pin.ShowPinned();
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

    public void NotifySaved(string path)
    {
        if (_trayNotify == null) return;
        _trayNotify(path);
    }

    private Action<string>? _trayNotify;

    public void SetSavedNotifier(Action<string> notifier) => _trayNotify = notifier;

    /// <summary>给自检用：拿到当前会话对象。</summary>
    internal CaptureSession? SessionForTest => _session;

    internal IReadOnlyList<CaptureOverlay> OverlaysForTest => _overlays;
}
