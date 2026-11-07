using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Drawing;

namespace LikeShot.Capture;

/// <summary>
/// 无键盘也能跑的自检：驱动状态机走一遍选区→标注→导出，再验证长截图拼接。
/// 结果写成 Temp 下的报告和 PNG，方便用像素级比对确认对齐是否真的准。
/// </summary>
public static class SelfTest
{
    public static void Run(CaptureController controller, Action exit)
    {
        if (Environment.GetCommandLineArgs().Contains("--longtest"))
        {
            LongRun(controller, exit);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--historytest"))
        {
            HistoryRun(exit);
            return;
        }

        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);

        // 自检期间整库改道到 Temp：不然跑一轮就在他的真实历史里塞十几张测试图。
        var store = HistoryStore.UseRootForTest(Path.Combine(dir, "history"));
        int limitWas = App.Config.HistoryLimit;

        try
        {
            App.Config.HistoryLimit = 50;
            var displays = Display.All();
            log.AppendLine($"displays={displays.Count} " +
                          string.Join(" | ", displays.Select(d => $"{d.DeviceName} {d.Bounds.Width}x{d.Bounds.Height} @{d.Scale:0.##}{(d.IsPrimary ? " primary" : "")}")));

            controller.BeginCapture();
            var session = controller.SessionForTest;
            if (session == null) throw new InvalidOperationException("截图会话没起来");

            var d0 = displays.First(x => x.IsPrimary) ?? displays[0];
            int ox = d0.Bounds.X + 120, oy = d0.Bounds.Y + 120;

            // 1) 框选：按下-拖动-松开
            session.PointerDown(new Point(ox, oy), LeftOrRight.Left);
            for (int i = 1; i <= 10; i++) session.PointerMove(new Point(ox + i * 24, oy + i * 16));
            session.PointerUp(new Point(ox + 240, oy + 160));
            log.AppendLine($"selection={session.FinalRect().Width}x{session.FinalRect().Height} phase={session.Phase}");

            // 2) 标注：矩形 + 箭头 + 画笔 + 马赛克 + 序号
            session.SetTool(ToolKind.Shape);
            session.SetShapeKind(ShapeKind.Rect);
            session.PointerDown(new Point(ox + 20, oy + 20), LeftOrRight.Left);
            session.PointerMove(new Point(ox + 120, oy + 70));
            session.PointerUp(new Point(ox + 120, oy + 70));

            session.SetTool(ToolKind.Arrow);
            session.PointerDown(new Point(ox + 140, oy + 100), LeftOrRight.Left);
            session.PointerMove(new Point(ox + 210, oy + 140));
            session.PointerUp(new Point(ox + 210, oy + 140));

            session.SetTool(ToolKind.Brush);
            session.PointerDown(new Point(ox + 30, oy + 120), LeftOrRight.Left);
            for (int i = 0; i < 8; i++) session.PointerMove(new Point(ox + 30 + i * 10, oy + 120 + Math.Sin(i) * 12));
            session.PointerUp(new Point(ox + 100, oy + 120));

            session.SetTool(ToolKind.Mosaic);
            session.PointerDown(new Point(ox + 150, oy + 20), LeftOrRight.Left);
            session.PointerMove(new Point(ox + 230, oy + 60));
            session.PointerUp(new Point(ox + 230, oy + 60));

            // 快照只留一份像素了：读单点、放大镜取块、打码底图三条路必须读到同一颗像素。
            var snap = session.Snapshot;
            int sx = ox + 160, sy = oy + 30;
            var pxOne = snap.PixelAt(sx, sy);
            var pxBlock = snap.SampleBlock(sx - 1, sy - 1, 3, 3);
            var pxMid = Color.FromRgb(pxBlock[(1 * 3 + 1) * 4 + 2], pxBlock[(1 * 3 + 1) * 4 + 1], pxBlock[(1 * 3 + 1) * 4]);
            var pxRegion = snap.CopyRegion(new Int32Rect(sx, sy, 1, 1));
            log.AppendLine($"快照读像素三查：单点#{pxOne.R:X2}{pxOne.G:X2}{pxOne.B:X2} " +
                           $"放大镜块中心#{pxMid.R:X2}{pxMid.G:X2}{pxMid.B:X2} 区域#{pxRegion[2]:X2}{pxRegion[1]:X2}{pxRegion[0]:X2} " +
                           $"（三者必须一致，不一致就是唯一那份像素被读歪了）");

            session.SetTool(ToolKind.Marker);
            session.PointerDown(new Point(ox + 60, oy + 90), LeftOrRight.Left);
            session.PointerUp(new Point(ox + 60, oy + 90));
            session.PointerDown(new Point(ox + 80, oy + 90), LeftOrRight.Left);
            session.PointerUp(new Point(ox + 80, oy + 90));

            // 换色换粗再画一笔：标注必须带上，不然主构造函数的参数就是悄悄丢掉的。
            var probe = Color.FromRgb(0x00, 0xC8, 0x00);
            session.SetColor(probe);
            session.SetStrokeWidth(7);
            session.SetTool(ToolKind.Shape);
            session.SetShapeKind(ShapeKind.Ellipse);
            session.PointerDown(new Point(ox + 170, oy + 150), LeftOrRight.Left);
            session.PointerMove(new Point(ox + 220, oy + 185));
            session.PointerUp(new Point(ox + 220, oy + 185));
            var last = session.Layer.Items[^1];
            log.AppendLine($"颜色粗细回环：{last.GetType().Name} color={last.Color} width={last.StrokeWidth} " +
                           $"（应为 {probe} / {session.StrokeWidth}，不符就是标注没接住参数）");

            session.SetTool(ToolKind.None);
            log.AppendLine($"annotations={session.Layer.Items.Count} badges={session.Layer.NextBadgeNumber - 1}");

            var image = session.RenderResult();
            string outPath = Path.Combine(dir, "annotated.png");
            var writeError = Exporter.TryWrite(image!, outPath);
            log.AppendLine(writeError != null ? $"导出写盘失败：{writeError}"
                : $"rendered={image!.PixelWidth}x{image.PixelHeight} bytes={new FileInfo(outPath).Length}");

            // 3) 撤销一次
            int before = session.Layer.Items.Count;
            session.Undo();
            log.AppendLine($"undo: {before} -> {session.Layer.Items.Count}");

            // 3.5) 选中已画内容：带着画具点它也必须选中（不能再落一笔），然后能拖走、拖角放大、删掉。
            {
                var rectItem = session.Layer.Items.OfType<RectAnnotation>().FirstOrDefault();
                if (rectItem == null) log.AppendLine("选中测试：前面没画出矩形，跳过");
                else
                {
                    // 上边线四分之一处：既在边线上，又避开八个手柄。
                    var onEdge = new Point(rectItem.Box.X + rectItem.Box.Width / 4.0, rectItem.Box.Y);
                    int countBefore = session.Layer.Items.Count;
                    session.SetTool(ToolKind.Shape);
                    session.PointerDown(onEdge, LeftOrRight.Left);
                    session.PointerUp(onEdge);
                    log.AppendLine($"带着画具点矩形边线：选中={session.Selected?.GetType().Name ?? "无（不对）"} " +
                                   $"手柄={session.HitItemHandle(onEdge)}（应为 -1） 新增标注={session.Layer.Items.Count - countBefore}（应为 0）");

                    var boxBefore = rectItem.Box;
                    session.PointerDown(onEdge, LeftOrRight.Left);
                    session.PointerMove(new Point(onEdge.X + 40, onEdge.Y + 30));
                    session.PointerUp(new Point(onEdge.X + 40, onEdge.Y + 30));
                    log.AppendLine($"拖走：左上 {boxBefore.X},{boxBefore.Y} → {rectItem.Box.X},{rectItem.Box.Y}（应 +40/+30） " +
                                   $"大小={rectItem.Box.Width}x{rectItem.Box.Height}（应仍是 {boxBefore.Width}x{boxBefore.Height}）");

                    var corner = new Point(rectItem.Box.X + rectItem.Box.Width, rectItem.Box.Y + rectItem.Box.Height);
                    var sizeBefore = rectItem.Box;
                    log.AppendLine($"右下角手柄命中={session.HitItemHandle(corner)}（应为 4）");
                    session.PointerDown(corner, LeftOrRight.Left);
                    session.PointerMove(new Point(corner.X + 50, corner.Y + 40));
                    session.PointerUp(new Point(corner.X + 50, corner.Y + 40));
                    log.AppendLine($"拖右下角放大：{sizeBefore.Width}x{sizeBefore.Height} → {rectItem.Box.Width}x{rectItem.Box.Height}（应 +50/+40）");

                    session.SetFontDp(40);
                    log.AppendLine($"字号滑条到 40：FontSize={session.FontSize}（应为 {(int)Math.Round(40 * session.UiScale)}）");
                    var textItem = new TextAnnotation("字号测试",
                        new Point(rectItem.Box.X + 6, rectItem.Box.Y + 6), session.Color, session.FontSize);
                    session.Layer.Add(textItem);
                    session.Select(textItem);
                    session.SetFontDp(64);
                    log.AppendLine($"选中这条文字再拖到 64：它自己={textItem.FontSize}（应为 {(int)Math.Round(64 * session.UiScale)}）");

                    int total = session.Layer.Items.Count;
                    session.OnKeyDown(Key.Delete, false, false);
                    log.AppendLine($"Delete 删选中：{total} → {session.Layer.Items.Count}（应 -1） 选中={session.Selected?.GetType().Name ?? "无（对）"}");

                    session.SetTool(ToolKind.None);
                    session.Select(rectItem);
                    var emptySpot = new Point(session.Selection.X + 235, session.Selection.Y + 155);
                    session.PointerDown(emptySpot, LeftOrRight.Left);
                    session.PointerUp(emptySpot);
                    log.AppendLine($"点空白处：选中={session.Selected?.GetType().Name ?? "无（对）"}");
                }
            }

            controller.Abort();

            // 回归（0.1.13）：带着矩形画具在选区内按下时，1×1 活动矩形立即进图层，
            // 鼠标一动命中检测就碰到它——以前 OnRing 对负宽高的内缩框直接抛异常，
            // 整个截图被收摊（用户看到的就是「选了矩形想画，一下就退出截图」）。
            // 另外原地松手不得留幽灵 1×1 图形。
            try
            {
                session.SetShapeKind(ShapeKind.Rect);
                session.SetTool(ToolKind.Shape);
                int layersBefore = session.Layer.Items.Count;
                var anchor = new Point(session.Selection.X + 235, session.Selection.Y + 155);
                session.PointerDown(anchor, LeftOrRight.Left);
                bool hitOk = true;
                try { session.HitAnnotation(anchor); session.HitItemHandle(anchor); session.OverItem(anchor); }
                catch { hitOk = false; }
                session.PointerMove(new Point(anchor.X + 90, anchor.Y + 50));
                session.PointerUp(new Point(anchor.X + 90, anchor.Y + 50));
                log.AppendLine($"矩形画具回归：命中检测={(hitOk ? "没炸（对）" : "炸了（不对）")} " +
                               $"图层 {layersBefore}→{session.Layer.Items.Count}（拖一下成图应 +1）");

                int beforeClick = session.Layer.Items.Count;
                session.PointerDown(new Point(anchor.X + 40, anchor.Y + 25), LeftOrRight.Left);
                session.PointerUp(new Point(anchor.X + 40, anchor.Y + 25));
                log.AppendLine($"原地空点：图层 {beforeClick} → {session.Layer.Items.Count}（应不变，不留幽灵框）");
                session.SetTool(ToolKind.None);
            }
            catch (Exception ex) { log.AppendLine("矩形画具回归 FAILED: " + ex.Message); }

            // 4) 退化选区不该成图。以前这里退回整张桌面快照，「只想取消这一下」会变成「导出一张全屏」。
            //    要验这条得用一个真的没框过选区的会话：Abort 只关窗口，旧会话的选区还挂着。
            var blank = ScreenSnapshot.GrabVirtualDesktop();
            var empty = new CaptureSession(blank, displays, 1.0);
            log.AppendLine($"空选区成图：{(empty.RenderResult() == null ? "null（对）" : "非 null（错：会导出整张桌面）")}");

            // 回归（0.1.13）：悬停出红框的窗口，按下鼠标那一刻就该选中，不等松手
            // （以前按下只落一个 1×1 的自由框选遮罩，屏幕闪一下，松手才吸附窗口）。
            {
                var snapSession = new CaptureSession(ScreenSnapshot.GrabVirtualDesktop(), displays, 1.0);
                Point? snapPoint = null;
                Int32Rect snapRect = default;
                var dPrimary = displays.First(x => x.IsPrimary) ?? displays[0];
                for (int gx = 60; gx < dPrimary.Bounds.Width - 60 && snapPoint == null; gx += 90)
                {
                    for (int gy = 60; gy < dPrimary.Bounds.Height - 60; gy += 90)
                    {
                        var cand = new Point(dPrimary.Bounds.X + gx, dPrimary.Bounds.Y + gy);
                        if (WindowSnap.AtPoint((int)cand.X, (int)cand.Y) is { } f && f.Width >= 40 && f.Height >= 40)
                        {
                            snapPoint = cand; snapRect = f; break;
                        }
                    }
                }
                if (snapPoint is { } sp)
                {
                    snapSession.PointerDown(sp, LeftOrRight.Left);
                    log.AppendLine($"点窗口按下即选：按下后选区={snapSession.Selection.Width}x{snapSession.Selection.Height}" +
                                   $"（应为窗口 {snapRect.Width}x{snapRect.Height}） 相符={snapSession.Selection.Equals(snapRect)}（应为 True）");
                    snapSession.PointerUp(sp);
                    log.AppendLine($"点窗口松手保持：选区={snapSession.Selection.Width}x{snapSession.Selection.Height} 相符={snapSession.Selection.Equals(snapRect)}（应为 True）");
                }
                else log.AppendLine("点窗口按下即选：桌面上没找到可吸附窗口（跳过）");
            }

            // 5) 出图必须给结果：坏路径要还回那句原因，而不是把异常甩给全局兜底把会话崩掉。
            if (image != null)
            {
                var writeFailure = Exporter.TryWrite(image, Path.Combine(dir, "坏名字?.png"));
                log.AppendLine($"写盘失败要能报原因：{(writeFailure == null ? "没报错（不对）" : $"报了「{writeFailure}」")}");
                var copyFailure = Exporter.TryCopyImage(image);
                log.AppendLine($"复制到剪贴板：{(copyFailure == null ? "成功" : $"失败「{copyFailure}」")}");
            }

            // 6) 走一次真实出口（钉图）：图得进历史库，而且读得回来。
            int entriesBefore = store.Entries.Count;
            controller.BeginCapture();
            var s2 = controller.SessionForTest;
            if (s2 != null)
            {
                s2.PointerDown(new Point(ox, oy), LeftOrRight.Left);
                s2.PointerMove(new Point(ox + 200, oy + 140));
                s2.PointerUp(new Point(ox + 200, oy + 140));
                s2.Finish(CopyMode.Pin);
            }
            FlushIdle();   // 入库排在 ContextIdle，等消息泵转到那一级才真落盘
            var newest = store.Entries.FirstOrDefault();
            log.AppendLine($"完成截图入库：新增={store.Entries.Count - entriesBefore}（应为 1） " +
                           $"最新={(newest?.Source)}{(newest == null ? "" : $" {newest.Width}x{newest.Height}")} " +
                           $"原图读回={(newest != null && store.LoadFull(newest) != null)} 缩略图读回={(newest != null && store.Thumbnail(newest) != null)}");
            log.AppendLine($"关掉这一轮开出来的钉图窗：{ClosePins()} 扇");

            // 7) 再标注入口：把一张图丢回截图层，选区就该正好是那张图。
            controller.Abort();
            var replayImage = MakeTestImage(800, 500, Color.FromRgb(0x30, 0x60, 0xF0));
            controller.BeginReplay(replayImage);
            var s3 = controller.SessionForTest;
            if (s3 == null) log.AppendLine("再标注：会话没起来（不对）");
            else
            {
                var rect = s3.FinalRect();
                s3.SetTool(ToolKind.Shape);
                s3.SetShapeKind(ShapeKind.Rect);
                s3.PointerDown(new Point(rect.X + 20, rect.Y + 20), LeftOrRight.Left);
                s3.PointerMove(new Point(rect.X + 220, rect.Y + 160));
                s3.PointerUp(new Point(rect.X + 220, rect.Y + 160));
                var replayOut = s3.RenderResult();
                log.AppendLine($"再标注：选区={rect.Width}x{rect.Height}（应为 800x500） 覆盖层={controller.OverlaysForTest.Count} 层 " +
                               $"标注={s3.Layer.Items.Count} 成图={(replayOut?.PixelWidth ?? 0)}x{(replayOut?.PixelHeight ?? 0)}");
            }
            controller.Abort();

            // 8) 钉图窗的标注链路：合成一张图钉上去，画一笔，成图尺寸要对得上。
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
            var pin = new LikeShot.UI.PinWindow(
                MakeTestImage(600, 400, Color.FromRgb(0xC0, 0x30, 0x30)),
                new Int32Rect(primary.Bounds.X + 160, primary.Bounds.Y + 160, 600, 400), App.Config);
            pin.ShowPinned();
            pin.Session.SetTool(ToolKind.Brush);
            pin.Session.PointerDown(new Point(primary.Bounds.X + 200, primary.Bounds.Y + 200), LeftOrRight.Left);
            pin.Session.PointerMove(new Point(primary.Bounds.X + 300, primary.Bounds.Y + 260));
            pin.Session.PointerMove(new Point(primary.Bounds.X + 380, primary.Bounds.Y + 220));
            pin.Session.PointerUp(new Point(primary.Bounds.X + 380, primary.Bounds.Y + 220));
            var pinOut = pin.Session.RenderResult();
            log.AppendLine($"钉图窗标注：标注={pin.Session.Layer.Items.Count} 成图={pinOut?.PixelWidth}x{pinOut?.PixelHeight}（应为 600x400）");

            // 8.5) 钉图窗里点已画内容：这一下必须归会话（选中/拖动），不能变成「拖着窗口走」。
            var pinStroke = pin.Session.Layer.Items.OfType<StrokeAnnotation>().FirstOrDefault();
            if (pinStroke == null) log.AppendLine("钉图窗选中测试：前面没画出笔画，跳过");
            else
            {
                pin.Session.SetTool(ToolKind.None);
                var mid = pinStroke.Points[pinStroke.Points.Count / 2];
                bool onStroke = pin.ClickGoesToSession(mid);
                bool offImage = pin.ClickGoesToSession(new Point(primary.Bounds.X + 160 + 700, primary.Bounds.Y + 200));
                pin.Session.PointerDown(mid, LeftOrRight.Left);
                pin.Session.PointerUp(mid);
                log.AppendLine($"钉图窗点笔画正中：交给会话={onStroke}（应为 True，False 就是这一下变成拖窗口） " +
                               $"点到图片外={offImage}（应为 False） 选中={pin.Session.Selected?.GetType().Name ?? "无（不对）"}");
            }
            pin.Close();

            // 9) 长截图拼接：造一个已知滚动量的两帧
            var stitchReport = StitchingCheck();
            log.AppendLine(stitchReport);

            // 10) 热键归属：现在 QQ 在跑吗
            log.AppendLine(HotkeyCheck());
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        finally
        {
            App.Config.HistoryLimit = limitWas;
        }

        string report = Path.Combine(dir, "report.txt");
        File.WriteAllText(report, log.ToString());
        Console.Out.WriteLine(log);
        Console.Out.WriteLine("REPORT " + report + "  DIR " + dir);
        exit();
    }

    /// <summary>
    /// ContextIdle 排队的入库要等消息泵转到那一级才跑；
    /// 用同优先级的空 Invoke 排一次队，就等于把它冲干净了。
    /// </summary>
    private static void FlushIdle() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    private static int ClosePins()
    {
        var pins = System.Windows.Application.Current.Windows
            .OfType<LikeShot.UI.PinWindow>().ToArray();
        foreach (var pin in pins) pin.Close();
        return pins.Length;
    }

    private static System.Windows.Media.Imaging.BitmapSource MakeTestImage(
        int width, int height, System.Windows.Media.Color color) =>
        LikeShot.Drawing.RenderSurface.ToBitmap(width, height,
            dc => dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height)));

    /// <summary>
    /// 历史库实跑：往 Temp 的独立目录里灌图，验入库、淘汰（收藏不参与）、
    /// 重开还能认出来、搜索口径、删除和清空。绝不碰他真实的历史目录。
    /// </summary>
    public static async void HistoryRun(Action exit)
    {
        var log = new StringBuilder();
        string root = Path.Combine(Path.GetTempPath(), "likeshot-history-test");
        string report = Path.Combine(Path.GetTempPath(), "likeshot-selftest", "history-report.txt");
        int limitWas = App.Config.HistoryLimit;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            App.Config.HistoryLimit = 5;
            var store = HistoryStore.UseRootForTest(root);

            // 1) 入库：原图、缩略图、索引三样都得有
            var first = store.Add(MakeTestImage(640, 400, Color.FromRgb(10, 20, 30)), HistoryStore.SourceCapture);
            var thumb = first == null ? null : store.Thumbnail(first);
            log.AppendLine($"入库：条目={(first == null ? "没有（不对）" : "有")} 原图在盘={(first != null && File.Exists(store.FullPathOf(first)))} " +
                           $"缩略图={thumb?.PixelWidth}x{thumb?.PixelHeight}（长边应 ≤420）");

            // 1.5) 入库后的自动收摊：AddLater 后面排的那次压堆必须真的跟着入库跑，
            //      不然每截一张漏几十 MB，内存越截越高。截图层开着时收尾会主动跳过，
            //      所以历史页这段流程里它必须跑成。
            int settlesBefore = CaptureController.SettlesForTest;
            store.AddLater(MakeTestImage(320, 200, Color.FromRgb(5, 6, 7)), HistoryStore.SourceCapture);
            FlushIdle();
            log.AppendLine($"入库收摊：{(CaptureController.SettlesForTest == settlesBefore + 1 ? "跟着入库跑了" : "没跑（不对）")}");

            // 2) 淘汰：连灌过上限，收藏的那张必须活着。
            //    注意上限是「每次入库都当场兑现」的，所以要挑一张此刻还在库里的最旧的去收藏，
            //    不能拿最早灌进去的那张——它很可能早就被自己的上限挤掉了。
            for (int i = 0; i < 6; i++)
            {
                store.Add(MakeTestImage(300, 200, Color.FromRgb((byte)(i * 30), 90, 140)), HistoryStore.SourceLong);
                await Task.Delay(12);   // 文件名带毫秒，隔一点免得挤在同一毫秒撞名
            }
            var oldestAlive = store.Entries[^1];
            store.SetFavorite(oldestAlive.Id, true);
            for (int i = 0; i < 4; i++) { store.Add(MakeTestImage(200, 120, Colors.Black), HistoryStore.SourcePin); await Task.Delay(12); }

            log.AppendLine($"上限 5 张、收藏不参与：现在 {store.Entries.Count} 张（应为 6＝5 非收藏 + 1 收藏） " +
                           $"非收藏={store.Count(false)} 收藏={store.FavoriteCount}");
            log.AppendLine($"最旧那张被标了收藏所以还在={store.Entries.Any(e => e.Id == oldestAlive.Id)}（应为 True）");
            log.AppendLine($"库里每条的来源={string.Join(",", store.Entries.Select(e => e.Source))}");

            // 3) 换一个实例读同一个目录：模拟重启
            var reloaded = new HistoryStore(root);
            log.AppendLine($"重开一遍读到 {reloaded.Entries.Count} 条（应为 {store.Entries.Count}） " +
                           $"原图都在={reloaded.Entries.All(e => File.Exists(store.FullPathOf(e)))} " +
                           $"收藏状态认得出={reloaded.Entries.Any(e => e.Favorite)}");

            // 4) 搜索口径：字面、来源、只看收藏
            int width = reloaded.Entries[0].Width;
            log.AppendLine($"搜索「长截图」={reloaded.Entries.Count(e => HistoryStore.Matches(e, "长截图", null, false))} " +
                           $"搜尺寸「{width}」={reloaded.Entries.Count(e => HistoryStore.Matches(e, width.ToString(), null, false))} " +
                           $"来源筛「钉图」={reloaded.Entries.Count(e => HistoryStore.Matches(e, "", "钉图", false))} " +
                           $"只看收藏={reloaded.Entries.Count(e => HistoryStore.Matches(e, "", null, true))}");

            // 5) 删一条、再清空：文件要跟着从盘上走
            var victim = reloaded.Entries.First(e => e.Favorite);
            string victimPath = store.FullPathOf(victim);
            int removedOne = store.Remove([victim.Id]);
            log.AppendLine($"删收藏那张：删了={removedOne} 原图还在={File.Exists(victimPath)} 缩略图还在={File.Exists(Path.Combine(root, victim.Thumb))}（都应为 False）");

            int cleared = store.Clear(true);
            bool anyPngLeft = Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Any();
            log.AppendLine($"清空：删了 {cleared} 条 剩余={store.Entries.Count} 盘上还有图={anyPngLeft} 索引还在={File.Exists(Path.Combine(root, "index.json"))}");
            log.AppendLine($"一路的入库失败记录={store.LastError ?? "无（应为无）"}");
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        finally
        {
            App.Config.HistoryLimit = limitWas;
        }

        File.WriteAllText(report, log.ToString());
        Console.Out.WriteLine(log);
        Console.Out.WriteLine("REPORT " + report + "  DIR " + root);
        exit();
    }

    /// <summary>
    /// 长截图实跑：自己造一张「每一行颜色都不重复」的可滚动页面，
    /// 让它自动滚到底，把拼出来的长图写盘，再拿页面自己的可滚总高核对拼得对不对。
    /// 不用靠肉眼盯着截图猜，也不用借浏览器（它会弹同步授权框，点也不是不点也不是）。
    /// </summary>
    private static async void LongRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);

        LongPageWindow? page = null;
        try
        {
            page = new LongPageWindow();
            page.Show();
            await Task.Delay(700); // 等它把 250 条色带画完，否则首帧抓到的是白屏

            controller.BeginCapture();
            var session = controller.SessionForTest!;
            var d = Display.All().First(x => x.IsPrimary);

            // 选区底部要留出工具栏的位置：工具栏放不下时会翻到选区上沿，
            // 那就等于把自己的按钮拍进每一帧里，拼接必然认不出滚动量。
            var box = new Int32Rect(d.Bounds.X + 30, d.Bounds.Y + 30, d.Bounds.Width - 60, d.Bounds.Height - 340);

            int x0 = box.X, y0 = box.Y, x1 = box.X + box.Width, y1 = box.Y + box.Height;

            session.PointerDown(new System.Windows.Point(x0, y0), LeftOrRight.Left);
            for (int i = 1; i <= 8; i++)
                session.PointerMove(new System.Windows.Point(x0 + (x1 - x0) * i / 8, y0 + (y1 - y0) * i / 8));
            session.PointerUp(new System.Windows.Point(x1, y1));
            log.AppendLine($"选区={session.FinalRect().Width}x{session.FinalRect().Height}@{session.FinalRect().X},{session.FinalRect().Y}");

            controller.BeginLongCaptureForTest();
            var runner = controller.RunnerForTest;
            if (runner == null) throw new InvalidOperationException("长截图没起来");

            int seed = runner.AppendedTotal;
            log.AppendLine($"首帧高度={seed}");

            // 自拼回归（0.1.15）：页面静止、自动滚动还没开，画布绝不能自己长——
            // 以前预览窗压在选区里每拍都变，行哈希被搅乱，滚到底了还在整帧整帧地自己拼。
            int idleBefore = runner.AppendedTotal;
            await Task.Delay(1500);
            log.AppendLine($"静止不滚：画布 {idleBefore} → {runner.AppendedTotal} " +
                           $"{(runner.AppendedTotal == idleBefore ? "（没自己长，对）" : "（自己长了，不对）")}");

            // 遮罩回归（0.1.15）：压在选区里的悬浮窗矩形必须被填成恒定纯色，
            // 矩形外一字不动——不然自家界面就拼进长图了。
            {
                var maskBuf = new byte[100 * 400];
                for (int i = 0; i < maskBuf.Length; i++) maskBuf[i] = 0x11;
                LongCaptureRunner.MaskRect(maskBuf, 400, new Int32Rect(0, 0, 100, 100), new Int32Rect(40, 40, 20, 10));
                bool maskOk = maskBuf[45 * 400 + 45 * 4] == 0x20 && maskBuf[10 * 400 + 10 * 4] == 0x11;
                log.AppendLine($"悬浮窗遮罩：{(maskOk ? "矩形内变纯色、矩形外原样（对）" : "（不对）")}");
            }

            // 抓屏复用回归（0.1.17）：连续抓两帧必须是同一块缓冲——
            // 以前每一拍都新配 GDI 位图 + WriteableBitmap + byte[] 三块 25MB，
            // 长截图滚一分钟就是几百次这样的大对象分配，内存跟着一路涨。
            {
                using var grabber = new ScreenSnapshot.LiveGrabber();
                var f1 = grabber.Grab(new Int32Rect(d.Bounds.X + 10, d.Bounds.Y + 10, 120, 90));
                var f2 = grabber.Grab(new Int32Rect(d.Bounds.X + 10, d.Bounds.Y + 10, 120, 90));
                bool reused = ReferenceEquals(f1.Buffer, f2.Buffer);
                log.AppendLine($"抓屏复用：连续两帧{(reused ? "同一块缓冲（不再每拍新配，对）" : "（换了缓冲，不对）")}");
            }

            // 工具栏落位回归（0.1.18）：选区几乎满屏、上下都放不下工具栏时，
            // 只能压进选区底边内侧——以前会兜到屏幕顶上，工具栏压住选区顶部，
            // 每一帧被抓进去再掩成纯色，黑框就这么烤在长图顶上了。
            {
                double t1 = ToolbarWindow.PickBarTop(100, 800, 0, 1000, 70);   // 下面放得下
                double t2 = ToolbarWindow.PickBarTop(100, 990, 0, 1000, 70);   // 下面放不下、上面放得下
                double t3 = ToolbarWindow.PickBarTop(0, 1000, 0, 1000, 70);    // 都放不下：压底边内侧
                bool ok = t1 == 810 && t2 == 20 && t3 == 926;
                log.AppendLine($"工具栏落位：下方={t1:0} 上方={t2:0} 满屏={t3:0} " +
                               $"{(ok ? "（下方→上方→压底边，绝不压顶，对）" : "（不对）")}");
            }

            // 预留底栏回归（0.1.18）：压在选区底边的工具栏那几行是自家像素，
            // 从第一帧起就不能进画布——不滚动也不该在长图里留一块纯色。
            {
                int fw = 60, fh = 200, resv = 50, pageH = 300;
                static byte[] Frame(int off, int pageH, int fw, int fh, int resv)
                {
                    var f = new byte[fh * fw * 4];
                    for (int y = 0; y < fh - resv; y++)
                    {
                        int pageRow = Math.Min(off + y, pageH - 1);
                        byte v = (byte)(pageRow + 1);
                        for (int x = 0; x < fw; x++)
                        {
                            int i = y * fw * 4 + x * 4;
                            f[i] = v; f[i + 1] = v; f[i + 2] = v; f[i + 3] = 0xFF;
                        }
                    }
                    return f;   // 底部 resv 行全 0：模拟掩成纯色的自家工具栏
                }
                int stride = fw * 4;
                var s = new LongStitcher(new Int32Rect(0, 0, fw, fh), Frame(0, pageH, fw, fh, resv), stride, resv);
                int h1 = s.Height;
                int h2 = h1 + s.Feed(Frame(40, pageH, fw, fh, resv), stride, fh);
                int h3 = h2 + s.Feed(Frame(80, pageH, fw, fh, resv), stride, fh);
                var row = new byte[stride];
                for (int x = 0; x < fw; x++) { row[x * 4] = 150; row[x * 4 + 1] = 150; row[x * 4 + 2] = 150; row[x * 4 + 3] = 0xFF; }
                bool rowsOk = s.CanvasRowHashes()[149] == LongStitcher.HashRow(row, 0, stride);
                bool ok = h1 == 150 && h2 == 190 && h3 == 230 && rowsOk;
                log.AppendLine($"预留底栏：画布 {h1}→{h2}→{h3}（应 150→190→230，纯色条一帧不进） " +
                               $"内容对位={(rowsOk ? "对" : "错")} 判定={(ok ? "对" : "（不对）")}");
            }

            // 实时预览：得在选区外的暗带里安家（上方暗带 → 下方暗带 → 都放不下就藏），
            // 压回选区里就会把行哈希搅乱。
            var preview = runner.PreviewForTest;
            double previewLen0 = preview.DisplayedLengthDip;

            // 自动滚到「页面自己报到底了、且画布不再长高」为止，最多 90 秒。
            // 40 秒那版是掐着秒表收的，页面还没滚完就被判成「少了」——判据得是页面到底，不是秒数。
            runner.ToggleAutoScroll();
            int last = runner.AppendedTotal, stuck = 0, ticks = 0;
            for (; ticks < 90; ticks++)
            {
                await Task.Delay(1000);
                int now = runner.AppendedTotal;
                var (offset, scrollable) = page.ScrollOffsets();
                stuck = now > last ? 0 : stuck + 1;
                last = now;
                bool atBottom = offset >= scrollable - 1;
                if (ticks % 10 == 0 || atBottom)
                    log.AppendLine($"t+{ticks + 1}s 画布={now} 页面已滚={offset:0}/{scrollable:0} DIP");
                if (now >= LongStitcher.MaxHeight) { log.AppendLine("撞到长度上限"); break; }
                if (atBottom && stuck >= 2) break;
            }
            log.AppendLine($"跑了 {ticks + 1} 秒收工");

            double previewLen1 = preview.DisplayedLengthDip;
            log.AppendLine($"实时预览：可见={preview.IsVisible}（上方暗带放不下时应已让位给下方暗带或隐藏） " +
                           $"长度 {previewLen0:0}→{previewLen1:0}DIP");
            FlushIdle();
            preview.UpdateLayout();
            if (preview.IsVisible && preview.ActualWidth >= 1 && preview.ActualHeight >= 1)
            {
                int pw = (int)Math.Ceiling(preview.ActualWidth), ph = (int)Math.Ceiling(preview.ActualHeight);
                var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(preview);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string previewPath = Path.Combine(dir, "preview.png");
                using (var fs = File.Create(previewPath)) enc.Save(fs);
                log.AppendLine($"预览取证={pw}x{ph} -> {previewPath}");
            }

            var image = runner.BuildResult();
            if (image != null)
            {
                string path = Path.Combine(dir, "long.png");
                var longWriteError = Exporter.TryWrite(image, path);
                log.AppendLine(longWriteError != null ? $"长图写盘失败：{longWriteError}"
                    : $"成品={image.PixelWidth}x{image.PixelHeight} bytes={new FileInfo(path).Length} -> {path}");

                // 页面自己知道它能滚多远：拼出来的高度对不上这个数，就是重复贴（超了）
                // 或者漏贴（不到）。以前只报「滚没滚」，看不出这两件事。
                var (extent, viewport) = page.ScrollExtents();
                double scale = Display.All().First(d => d.IsPrimary).Scale;
                int expected = (int)Math.Round(extent * scale);
                int got = image.PixelHeight;
                int off = got - expected;
                bool fits = Math.Abs(off) <= Math.Max(200, expected / 10);
                int lines = runner.StitchDiag.Split('\n').Length;
                int zeroVotes = runner.StitchDiag.Split('\n').Count(l => l.Contains("票=0"));
                log.AppendLine($"应得高度={expected}px（页面可滚总高，视口 {viewport:0} DIP） 实拼={got}px 差={off} " +
                               $"判定={(fits ? "对得上" : "对不上：超了是重复贴，少了是漏贴")}");
                log.AppendLine($"认不出重叠量、只能整帧追加的帧数={zeroVotes}/{lines}");
            }

            runner.Cancel();
            log.AppendLine("—— 拼接逐帧判定 ——");
            log.AppendLine(runner.StitchDiag);
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        finally
        {
            page?.Close();
        }

        string report = Path.Combine(dir, "long-report.txt");
        File.WriteAllText(report, log.ToString());
        exit();
    }

    /// <summary>
    /// 自检用的可滚动页面：250 条唯一颜色的色带 + 固定表头/底栏。
    /// 颜色按 LineColor(i) 编码，拼完逐行认色就能算出重贴/漏贴。
    /// </summary>
    internal sealed class LongPageWindow : Window
    {
        public const int LineCount = 250;
        private readonly ScrollViewer _scroll = new();

        public LongPageWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            // 必须置顶：从命令行拉起来拿不到前台权限，不置顶就会被浏览器压在下面，
            // 于是「长截图实跑」截的其实是桌面现状——前两轮就是这么把真实网页拼进长图的。
            Topmost = true;
            Title = "LikeShot 长页测试";
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            var band = new StackPanel();
            for (int i = 0; i < LineCount; i++)
                band.Children.Add(new Border
                {
                    Height = 14 + i % 7 * 6,
                    // 纵向大跨度渐变：一条带里每一行都要不同。
                    // 整片纯色时同一颜色会连续重复几十行，逐行哈希的「只认出现过一次的行」会把它们全滤掉，
                    // 拼接器认不出重叠量，只能整帧追加——那样这份实跑就只验得出「滚没滚」，验不出「不重不漏」。
                    Background = new LinearGradientBrush(TopColor(i), BottomColor(i), 90),
                });

            var head = new Border { Height = 60, Background = new SolidColorBrush(Color.FromRgb(200, 30, 30)) };
            var foot = new Border { Height = 40, Background = new SolidColorBrush(Color.FromRgb(30, 200, 30)) };
            var dock = new DockPanel { Background = Brushes.White };
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Content = band;
            dock.Children.Add(head);
            dock.Children.Add(foot);
            dock.Children.Add(_scroll);
            Content = dock;
        }

        /// <summary>页面在 DIP 下的可滚总高和视口高——用来核对拼出来的长图对不对。</summary>
        public (double Extent, double Viewport) ScrollExtents() => (_scroll.ExtentHeight, _scroll.ViewportHeight);

        /// <summary>此刻已经滚了多远、还能滚多远（DIP）：当「真实滚动量」的尺子用。</summary>
        public (double Offset, double Scrollable) ScrollOffsets() => (_scroll.VerticalOffset, _scroll.ScrollableHeight);

        private static Color TopColor(int i) =>
            Color.FromRgb((byte)(i * 37 + 5), (byte)(i * 53 + 90), (byte)(i * 71 + 170));

        private static Color BottomColor(int i) =>
            Color.FromRgb((byte)(i * 37 + 92), (byte)(i * 53 + 24), (byte)(i * 71 + 44));
    }

    /// <summary>
    /// 历史页实跑：不走鼠标，把这一页的筛选、多选、卡片上的每个动作都点一遍，
    /// 看界面当时说的那句回执和库里的真实状态对不对得上。库改道 Temp，不动他真实的历史。
    /// </summary>
    public static async void HistoryUiRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);
        string root = Path.Combine(dir, "history-ui");
        int limitWas = App.Config.HistoryLimit;
        LikeShot.UI.SettingsWindow? win = null;

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var store = HistoryStore.UseRootForTest(root);
            App.Config.HistoryLimit = 50;
            for (int i = 0; i < 6; i++)
            {
                store.Add(TestTile(600 + i * 40, 400, i),
                    i % 2 == 0 ? HistoryStore.SourceCapture : HistoryStore.SourceLong);
                await Task.Delay(12);
            }

            win = new LikeShot.UI.SettingsWindow(App.Config);
            win.Show();
            win.SelectSection(LikeShot.UI.SettingsWindow.HistorySectionIndex);
            log.AppendLine($"进页面：卡片={win.HistoryCardCount}（应为 6） 计数「{win.HistoryCountTextForTest}」");

            win.HistorySourceForTest(HistoryStore.SourcePin);
            log.AppendLine($"来源筛「钉图」：卡片={win.HistoryCardCount}（应为 0） 空态在不在={win.HistoryShowsEmptyNote}");
            win.HistorySourceForTest("全部");

            win.HistorySearchForTest(HistoryStore.SourceLong);
            log.AppendLine($"搜「长截图」：卡片={win.HistoryCardCount}（应为 3）");
            win.HistorySearchForTest("400");
            log.AppendLine($"搜尺寸「400」：卡片={win.HistoryCardCount}（应为 6）");
            win.HistorySearchForTest("");

            log.AppendLine($"点复制：回执「{win.HistoryDoForTest("copy", 0)}」 剪贴板里有图={Clipboard.ContainsImage()}");
            log.AppendLine($"点钉图：回执「{win.HistoryDoForTest("pin", 0)}」 关掉了 {ClosePins()} 扇钉图窗");
            log.AppendLine($"点收藏：回执「{win.HistoryDoForTest("favorite", 0)}」 库里收藏={store.FavoriteCount}（应为 1）");

            win.HistoryPickForTest(0, false);
            win.HistoryPickForTest(1, true);
            win.HistoryPickForTest(2, true);
            int selected = win.HistorySelectedCount;
            int beforeDelete = store.Entries.Count;
            string deleteNote = win.HistoryDoForTest("deleteSelected", 0);
            log.AppendLine($"多选 {selected} 张后批删（应为 3）：回执「{deleteNote}」 库里 {beforeDelete} → {store.Entries.Count}");

            // 删除走的是事件异步刷新，不等一轮消息泵就按下标取卡片，取到的会是刚删掉那张——
            // 界面会如实说「文件已经不在库里了」。这里先等刷新，再验真正的再标注。
            await Task.Delay(400);
            string replayNote = win.HistoryDoForTest("replay", 0);
            await Task.Delay(800);
            var session = controller.SessionForTest;
            log.AppendLine($"点再标注：回执「{replayNote}」 会话起来={session != null} " +
                           $"选区={(session == null ? "-" : $"{session.FinalRect().Width}x{session.FinalRect().Height}")} " +
                           $"覆盖层={controller.OverlaysForTest.Count} 层 设置窗最小化={win.WindowState}");
            controller.Abort();
            await Task.Delay(300);

            store.Clear(true);
            await Task.Delay(400);   // 入库/清空走 Changed 事件异步刷新，给它一轮消息泵
            log.AppendLine($"清空后：卡片={win.HistoryCardCount} 空态在不在={win.HistoryShowsEmptyNote}");
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        finally
        {
            App.Config.HistoryLimit = limitWas;
            win?.Close();
        }

        string report = Path.Combine(dir, "history-ui-report.txt");
        File.WriteAllText(report, log.ToString());
        Console.Out.WriteLine(log);
        Console.Out.WriteLine("REPORT " + report);
        exit();
    }

    /// <summary>
    /// 版式取证：把「失败回执」真的挂到截图层上渲染一张出来。
    /// 这是本轮新加的一面，光读代码看不出红字会不会把工具栏撑到屏幕外、重试按钮点不点得到。
    /// </summary>
    public static async void StatusShotRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);
        try
        {
            controller.BeginCapture();
            var session = controller.SessionForTest;
            if (session == null) throw new InvalidOperationException("截图会话没起来");
            var d = Display.All().First(x => x.IsPrimary);
            int x0 = d.Bounds.X + 400, y0 = d.Bounds.Y + 300;
            session.PointerDown(new Point(x0, y0), LeftOrRight.Left);
            for (int i = 1; i <= 6; i++) session.PointerMove(new Point(x0 + i * 60, y0 + i * 40));
            session.PointerUp(new Point(x0 + 360, y0 + 240));
            await Task.Delay(500);

            controller.SayForTest("复制到剪贴板失败：剪贴板被别的程序占着，稍一下再复制", true, "重试复制", () => { });
            await Task.Delay(500);
            var overlay = controller.OverlaysForTest.First();
            var badError = RenderWindowToFile(overlay, Path.Combine(dir, "status-bad.png"));
            log.AppendLine($"红字回执挂上：渲染={(badError ?? "成功")} 整窗={overlay.ActualWidth:0}x{overlay.ActualHeight:0} DIP");

            controller.SayForTest("已保存到 截图_20261106_163012.png", false);
            await Task.Delay(400);
            var goodError = RenderWindowToFile(overlay, Path.Combine(dir, "status-good.png"));
            log.AppendLine($"白字回执挂上：渲染={(goodError ?? "成功")}");

            controller.Abort();
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }

        File.WriteAllText(Path.Combine(dir, "status-report.txt"), log.ToString());
        Console.Out.WriteLine(log);
        exit();
    }

    /// <summary>
    /// 版式取证：把「选中框」真的画到截图层上渲染一张出来。
    /// 虚线框、八个手柄在高分屏上到底多大、和选区自己的框会不会混成一团，只有看图才知道。
    /// </summary>
    public static async void SelectShotRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);
        try
        {
            controller.BeginCapture();
            var session = controller.SessionForTest;
            if (session == null) throw new InvalidOperationException("截图会话没起来");
            var d = Display.All().First(x => x.IsPrimary);
            int x0 = d.Bounds.X + 400, y0 = d.Bounds.Y + 300;
            session.PointerDown(new Point(x0, y0), LeftOrRight.Left);
            session.PointerUp(new Point(x0, y0));
            session.PointerMove(new Point(x0 + 500, y0 + 320));
            session.PointerUp(new Point(x0 + 500, y0 + 320));
            await Task.Delay(400);

            session.SetTool(ToolKind.Shape);
            session.SetShapeKind(ShapeKind.Rect);
            session.PointerDown(new Point(x0 + 40, y0 + 40), LeftOrRight.Left);
            session.PointerMove(new Point(x0 + 260, y0 + 150));
            session.PointerUp(new Point(x0 + 260, y0 + 150));

            session.SetTool(ToolKind.Arrow);
            session.PointerDown(new Point(x0 + 300, y0 + 60), LeftOrRight.Left);
            session.PointerMove(new Point(x0 + 440, y0 + 220));
            session.PointerUp(new Point(x0 + 440, y0 + 220));

            var rect = session.Layer.Items.OfType<RectAnnotation>().First();
            session.Select(rect);
            await Task.Delay(400);
            var overlay = controller.OverlaysForTest.First();
            var errRect = RenderWindowToFile(overlay, Path.Combine(dir, "select-rect.png"));
            log.AppendLine($"选中矩形：渲染={(errRect ?? "成功")} 包围盒={rect.Bounds.Width:0}x{rect.Bounds.Height:0} " +
                           $"手柄数={session.ItemHandlePoints().Length}");

            session.Select(session.Layer.Items.OfType<ArrowAnnotation>().First());
            await Task.Delay(300);
            var errArrow = RenderWindowToFile(overlay, Path.Combine(dir, "select-arrow.png"));
            log.AppendLine($"选中箭头：渲染={(errArrow ?? "成功")}");

            // 顺手把文字工具的二级条也渲染一张：字号滑条是新换的，得看一眼拖起来顺不顺、数值跟不跟。
            session.Select(null);
            session.SetTool(ToolKind.Text);
            session.SetFontDp(34);
            await Task.Delay(400);
            var errFont = RenderWindowToFile(overlay, Path.Combine(dir, "font-slider.png"));
            log.AppendLine($"字号滑条面板：渲染={(errFont ?? "成功")} FontDp={session.FontDp} FontSize={session.FontSize}");

            controller.Abort();
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }

        File.WriteAllText(Path.Combine(dir, "select-report.txt"), log.ToString());
        Console.Out.WriteLine(log);
        exit();
    }

    /// <summary>
    /// 版式取证：四个右键菜单各渲染一张。截图层、钉图窗、历史页卡片以前弹的是系统默认菜单，
    /// 和托盘那套圆角卡片是两个时代的样式——「现在统一成一套了」这句话得看图算数。
    /// </summary>
    public static async void MenuShotRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);
        string root = Path.Combine(dir, "menu-history");
        LikeShot.UI.SettingsWindow? win = null;
        var openedPins = new List<LikeShot.UI.PinWindow>();

        try
        {
            var d = Display.All().First(x => x.IsPrimary);

            // 1) 截图层
            controller.BeginCapture();
            var session = controller.SessionForTest;
            if (session == null) throw new InvalidOperationException("截图会话没起来");
            session.PointerDown(new Point(d.Bounds.X + 300, d.Bounds.Y + 260), LeftOrRight.Left);
            session.PointerUp(new Point(d.Bounds.X + 300, d.Bounds.Y + 260));
            session.PointerMove(new Point(d.Bounds.X + 800, d.Bounds.Y + 560));
            session.PointerUp(new Point(d.Bounds.X + 800, d.Bounds.Y + 560));
            await Task.Delay(400);
            var overlay = controller.OverlaysForTest.First();
            var capMenu = overlay.BuildContextMenu();
            capMenu.PlacementTarget = overlay;
            capMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
            capMenu.HorizontalOffset = 300;
            capMenu.VerticalOffset = 300;
            log.AppendLine($"截图层菜单：{await RenderMenuToFile(capMenu, Path.Combine(dir, "menu-capture.png"))} 项={capMenu.Items.Count}");
            capMenu.IsOpen = false;

            // 1b) 截图层里的字框：以前右键弹的是系统那份灰菜单
            overlay.OpenTextEditorForTest(new Point(d.Bounds.X + 420, d.Bounds.Y + 320));
            if (overlay.TextEditorMenuForTest is not { } textMenu)
                log.AppendLine("字框菜单：没拿到（不对）");
            else
            {
                log.AppendLine($"字框菜单：{await RenderMenuToFile(textMenu, Path.Combine(dir, "menu-text.png"))} 项={textMenu.Items.Count}");
                textMenu.IsOpen = false;
            }
            controller.Abort();

            // 2) 钉图窗
            var pin = new LikeShot.UI.PinWindow(
                MakeTestImage(600, 400, Color.FromRgb(0x20, 0x50, 0xC0)),
                new Int32Rect(d.Bounds.X + 200, d.Bounds.Y + 200, 600, 400), App.Config);
            pin.ShowPinned();
            openedPins.Add(pin);
            await Task.Delay(300);
            var pinMenu = pin.BuildContextMenu();
            pinMenu.PlacementTarget = pin;
            log.AppendLine($"钉图窗菜单：{await RenderMenuToFile(pinMenu, Path.Combine(dir, "menu-pin.png"))} 项={pinMenu.Items.Count}");
            pinMenu.IsOpen = false;

            // 3) 历史页卡片的右键菜单（库改道 Temp，不动真实历史）
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var store = HistoryStore.UseRootForTest(root);
            for (int i = 0; i < 2; i++)
            {
                store.Add(TestTile(600, 400, i), HistoryStore.SourceCapture);
                await Task.Delay(12);
            }
            win = new LikeShot.UI.SettingsWindow(App.Config);
            win.Show();
            win.SelectSection(LikeShot.UI.SettingsWindow.HistorySectionIndex);
            await Task.Delay(400);
            var cardMenu = win.CardMenuForTest(0);
            if (cardMenu == null) log.AppendLine("历史页菜单：没拿到卡片的菜单（不对）");
            else
            {
                log.AppendLine($"历史页菜单：{await RenderMenuToFile(cardMenu, Path.Combine(dir, "menu-history.png"))} 项={cardMenu.Items.Count}");
                cardMenu.IsOpen = false;
            }

            // 3b) 设置页输入框：整段选中时，剪切/复制这两项该亮起来
            win.SelectSection(0);
            win.SaveDirForTest = @"D:\截图";
            win.SelectSaveDirForTest();
            await Task.Delay(200);
            if (win.SaveDirMenuForTest is not { } fieldMenu) log.AppendLine("输入框菜单：没拿到（不对）");
            else
            {
                log.AppendLine($"输入框菜单：{await RenderMenuToFile(fieldMenu, Path.Combine(dir, "menu-field.png"))} 项={fieldMenu.Items.Count}");
                fieldMenu.IsOpen = false;
            }

            // 4) 托盘：样式就是照它对齐的，一起出图好比对
            if (App.Current is App app && app.TrayForTest is { } tray)
            {
                tray.ShowMenuForTest();
                var trayMenu = tray.MenuForTest;
                log.AppendLine($"托盘菜单：{await RenderMenuToFile(trayMenu, Path.Combine(dir, "menu-tray.png"))} 项={trayMenu.Items.Count}");
                trayMenu.IsOpen = false;
            }
            else log.AppendLine("托盘菜单：拿不到托盘（不对）");
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        finally
        {
            foreach (var p in openedPins) p.Close();
            win?.Close();
        }

        File.WriteAllText(Path.Combine(dir, "menu-report.txt"), log.ToString());
        Console.Out.WriteLine(log);
        exit();
    }

    /// <summary>
    /// 把弹出来的菜单单独渲染成图：菜单有自己的顶层可视（PopupRoot），
    /// 截屏抓不到它（无头进程没有前台权限），只能直接渲它的可视树。
    /// </summary>
    private static async Task<string> RenderMenuToFile(System.Windows.Controls.ContextMenu menu, string path)
    {
        menu.IsOpen = true;
        // 菜单弹出带淡入动画，只泵消息不真等时间，拍下来是一张全透明的空壳。
        await Task.Delay(220);
        FlushIdle();
        menu.UpdateLayout();
        if (PresentationSource.FromVisual(menu)?.RootVisual is not FrameworkElement root ||
            root.ActualWidth < 1 || root.ActualHeight < 1) return "没弹出可视根（不对）";
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        int ink = OpaquePixels(rtb);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        // 只报尺寸会放过「弹出来了但什么都没画」，所以把实心像素数也写进回执。
        return $"{w}x{h} 画到的像素={ink}{(ink < 500 ? "（几乎是空的，不对）" : "")}";
    }

    private static int OpaquePixels(RenderTargetBitmap rtb)
    {
        var buf = new byte[rtb.PixelWidth * rtb.PixelHeight * 4];
        rtb.CopyPixels(buf, rtb.PixelWidth * 4, 0);
        int ink = 0;
        for (int i = 3; i < buf.Length; i += 4)
            if (buf[i] > 200) ink++;
        return ink;
    }

    /// <summary>
    /// 热键仲裁实跑：自己往系统注入一次 Ctrl+Alt+Shift+F9，看谁来接。
    /// 同一个键、同一个钩子，只改「让位名单」这一个变量：
    /// 名单里有 QQ 时必须放行，名单里换成不存在的进程时必须我们自己接管。
    /// 用 F9 而不是 Ctrl+Alt+A，是为了不去触发 QQ 自己的截图界面。
    /// </summary>
    public static async void HotkeyRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);
        const string testChord = "Ctrl+Alt+Shift+F9";
        // 一个一定在跑、又永远不可能接热键的进程（会话 0，没有窗口）：
        // 拿它当让位对象，D 那条就不依赖本机装没装 QQ、也不依赖 QQ 的截图热键开着没开。
        string[] deadYieldNames = ["services"];

        var config = App.Config;
        var arbiter = App.Arbiter;
        if (arbiter == null) { log.AppendLine("FAILED: 仲裁器没起来"); }
        else
        {
            string realChord = config.CaptureChord;
            string[] realYield = config.YieldToProcesses.ToArray();
            var aliveNames = realYield.Where(n => Process.GetProcessesByName(n).Length > 0).ToArray();
            log.AppendLine($"让位进程在跑：{(aliveNames.Length == 0 ? "无（A、C 两条只作参考，判定要看 D）" : string.Join(",", aliveNames))}");

            try
            {
                config.CaptureChord = testChord;
                arbiter.ApplyConfig(config);

                // A) QQ 在名单里 → 我们不该接。本机 QQ 没跑的话这条测不到让位，只报事实不装成断言。
                arbiter.SetYieldNames(realYield);
                await Settle();
                PressChord();
                await Settle();
                string expectA = aliveNames.Length == 0 ? "本机没有名单里的进程，这条测不到" : "应为 False";
                log.AppendLine($"A 名单含 {string.Join("/", realYield)}：状态「{arbiter.StatusText}」 " +
                               $"我们的截图层={controller.SessionForTest != null}（{expectA}）");
                log.AppendLine($"A2 让位之后开始观察它响没响：{arbiter.YieldDiagForTest}");
                if (controller.SessionForTest != null) controller.Abort();

                // B) 名单换成不存在的进程 → 试探到没人注册就该由我们接管
                arbiter.SetYieldNames(["__NoSuchProcessForTest"]);
                await Settle();
                log.AppendLine($"B 试探结果：状态「{arbiter.StatusText}」");
                PressChord();
                await Settle();
                bool took = controller.SessionForTest != null;
                log.AppendLine($"B 名单不含 QQ：我们的截图层={took}（应为 True）");
                if (took) controller.Abort();
                await Task.Delay(400);

                // D) 这条是新的让位判定的正解，而且不依赖本机装没装 QQ：
                //    名单里放一个「一定在跑、但永远不可能接热键」的进程（services.exe，会话 0，没有窗口）。
                //    老逻辑是它活着就永远让位，结果这个键谁都不响应；
                //    现在第一下让位并观察，第二下判定它不接、当场接管，第三下开始就是即刻的。
                config.CaptureChord = testChord;
                arbiter.ApplyConfig(config);
                arbiter.SetYieldNames(deadYieldNames);
                await Settle();
                log.AppendLine($"D 名单={deadYieldNames[0]}（在跑={Process.GetProcessesByName(deadYieldNames[0]).Length > 0}）：{arbiter.YieldDiagForTest}");

                PressChord();
                await Settle();
                bool dFirst = controller.SessionForTest != null;
                log.AppendLine($"D 第一下（让位 + 观察）：我们的截图层={dFirst}（应为 False） {arbiter.YieldDiagForTest}");
                if (dFirst) controller.Abort();

                PressChord();
                await Settle();
                bool dSecond = controller.SessionForTest != null;
                log.AppendLine($"D 第二下（判定它不接，补上这一张）：我们的截图层={dSecond}（应为 True） {arbiter.YieldDiagForTest}");
                if (dSecond) controller.Abort();
                await Task.Delay(400);

                PressChord();
                await Settle();
                bool dThird = controller.SessionForTest != null;
                log.AppendLine($"D 第三下（改判之后即刻接管）：我们的截图层={dThird}（应为 True） {arbiter.YieldDiagForTest}");
                if (dThird) controller.Abort();
                await Task.Delay(400);

                // E) 让位应答回归（0.1.15）：QQ 的截图层那种「不抢前台」的工具窗弹出来，
                //    以前只盯前台切换的观察器永远等不到，攒两下就把我们的截图层压到 QQ 上面——
                //    「QQ 截完图之后我们的也冒出来了」就是它。现在窗口显形（OBJECT_SHOW）也算回应。
                //    让位名单指向我们自己，测试窗才能被观察器看见；这个窗 Show 时不激活、不抢前台。
                arbiter.SetYieldNames(["LikeShot"]);
                await Task.Delay(500);
                PressChord();
                await Task.Delay(250);
                var ghost = new Window
                {
                    Width = 140, Height = 90,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    Content = new System.Windows.Controls.TextBlock { Text = "e" }
                };
                ghost.Show();
                await Task.Delay(250);   // OBJECT_SHOW 走消息泵异步送达，得泵一拍才算数
                bool heardEarly = arbiter.ObserverForTest?.Responded ?? false;
                await Task.Delay(1400);  // 等观察器的 900ms 判定跑完
                log.AppendLine($"E 不抢前台的窗口也算回应={heardEarly}（应为 True） 判定={arbiter.YieldDiagForTest}（连按未见响应应为 0）");
                ghost.Close();
                await Task.Delay(400);

                // C) 让位名单清空、键位换回 Ctrl+Alt+A：QQ 真的注册着它，试探必须判「被占用」。
                //    这条只验状态，不再按键——按了就等于替用户唤起 QQ 的截图界面。
                config.CaptureChord = realChord;
                arbiter.ApplyConfig(config);
                arbiter.SetYieldNames(["__NoSuchProcessForTest"]);
                await Task.Delay(700);
                log.AppendLine($"C 键位归还给系统：状态「{arbiter.StatusText}」（QQ 在跑时应显示被占用）");
            }
            finally
            {
                config.CaptureChord = realChord;
                config.YieldToProcesses = realYield.ToList();
                arbiter.ApplyConfig(config);
            }
        }

        File.WriteAllText(Path.Combine(dir, "hotkey-report.txt"), log.ToString());
        exit();

        static async Task Settle()
        {
            await Task.Delay(900);   // 等注入的按键走完钩子 + Dispatcher
            await Task.Delay(400);   // 等截图层把窗口建起来
        }
    }

    /// <summary>用 keybd_event 打一次真组合键：修饰键按住不放，最后敲主键，再逆序松开。</summary>
    private static void PressChord(byte mainKey = 0x78)
    {
        const byte Ctrl = 0x11, Alt = 0x12, Shift = 0x10;
        void Down(byte vk) => LikeShot.Native.Win32.keybd_event(vk, 0, 0, UIntPtr.Zero);
        void Up(byte vk) => LikeShot.Native.Win32.keybd_event(vk, 0, LikeShot.Native.Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);

        Down(Ctrl); Down(Alt); Down(Shift); Down(mainKey);
        Thread.Sleep(60);
        Up(mainKey); Up(Shift); Up(Alt); Up(Ctrl);
    }

    /// <summary>
    /// 设置窗口实跑：不靠点鼠标，验三件事——
    /// 捕获框能产出的每个键位都能被 Chord.Parse 认回去；「应用」真的落到配置和磁盘；
    /// 应用之后不用重启，注入新组合键就该由我们的截图层接住。
    /// 结束前把配置原样写回去，不把他机器上的设置留在测试状态。
    /// </summary>
    public static async void SettingsRun(CaptureController controller, Action exit)
    {
        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);
        const string testChord = "Ctrl+Alt+Shift+F10";
        const byte F10 = 0x79;

        string realChord = App.Config.CaptureChord;
        string realFormat = App.Config.SaveFormat;
        int realPoll = App.Config.LongCapturePollMilliseconds;
        int realNotches = App.Config.LongCaptureScrollNotches;
        var realYield = App.Config.YieldToProcesses.ToList();
        string configPath = App.ConfigPath;
        byte[]? backup = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;

        LikeShot.UI.SettingsWindow? win = null;
        try
        {
            // 1) 键位映射回环
            var tokens = new List<string>();
            for (int k = (int)System.Windows.Input.Key.A; k <= (int)System.Windows.Input.Key.Z; k++) AddToken((System.Windows.Input.Key)k);
            for (int k = (int)System.Windows.Input.Key.D0; k <= (int)System.Windows.Input.Key.D9; k++) AddToken((System.Windows.Input.Key)k);
            for (int k = (int)System.Windows.Input.Key.F1; k <= (int)System.Windows.Input.Key.F24; k++) AddToken((System.Windows.Input.Key)k);
            foreach (var k in new[] { System.Windows.Input.Key.Space, System.Windows.Input.Key.Enter, System.Windows.Input.Key.Tab,
                System.Windows.Input.Key.PrintScreen, System.Windows.Input.Key.Insert, System.Windows.Input.Key.Delete,
                System.Windows.Input.Key.Home, System.Windows.Input.Key.End, System.Windows.Input.Key.PageUp,
                System.Windows.Input.Key.PageDown, System.Windows.Input.Key.Left, System.Windows.Input.Key.Up,
                System.Windows.Input.Key.Right, System.Windows.Input.Key.Down }) AddToken(k);
            void AddToken(System.Windows.Input.Key key)
            {
                var token = LikeShot.UI.ChordBox.KeyToken(key);
                if (token != null) tokens.Add(token);
            }
            int unparsable = tokens.Count(t =>
            {
                try { LikeShot.Hotkeys.Chord.Parse("Ctrl+" + t); return false; } catch { return true; }
            });
            log.AppendLine($"键位映射：捕获框支持 {tokens.Count} 个主键，Ctrl+主键 解析失败={unparsable}（应为 0）");

            // 2) 应用
            win = new LikeShot.UI.SettingsWindow(App.Config);
            log.AppendLine($"打开时状态「{win.StatusForTest}」 提示「{win.ChordHintForTest}」");
            win.ChordForTest = testChord;
            win.YieldForTest = "__NoSuchProcessForTest";
            win.PickForTest("bmp", 120, 2);
            bool applied = win.Apply();
            log.AppendLine($"应用返回={applied} 状态「{win.StatusForTest}」 提示「{win.ChordHintForTest}」");
            log.AppendLine($"内存配置：热键={App.Config.CaptureChord} 格式={App.Config.SaveFormat} " +
                           $"间隔={App.Config.LongCapturePollMilliseconds}ms 每帧{App.Config.LongCaptureScrollNotches}格 " +
                           $"让位={string.Join(",", App.Config.YieldToProcesses)}");
            string disk = File.ReadAllText(configPath);
            log.AppendLine($"磁盘配置：含新热键={disk.Contains(testChord)} 含 bmp={disk.Contains("\"bmp\"")}");

            // 3) 不重启，注入新键就该由我们接管
            await Task.Delay(700);   // 等仲裁器 500ms 的归属轮询跑到新配置
            PressChord(F10);
            await Task.Delay(900);
            await Task.Delay(400);
            bool took = controller.SessionForTest != null;
            log.AppendLine($"注入 {testChord}：我们的截图层={took}（应为 True，这条证明「应用即生效」）");
            if (took) controller.Abort();
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }
        finally
        {
            win?.Close();
            // 配置原样退回，别让他的机器停在测试用的键位上。
            App.Config.CaptureChord = realChord;
            App.Config.SaveFormat = realFormat;
            App.Config.LongCapturePollMilliseconds = realPoll;
            App.Config.LongCaptureScrollNotches = realNotches;
            App.Config.YieldToProcesses = realYield;
            AppConfig.Save(App.Config);
            if (backup != null) File.WriteAllBytes(configPath, backup);
            if (Application.Current is App app) app.ReloadHotkey();
            log.AppendLine($"已退回：热键={App.Config.CaptureChord} 格式={App.Config.SaveFormat}");

            // 内存收摊回归（0.1.15）：设置窗关掉必须自动压堆还工作集，不能涨上去就不下来。
            // 走真实的 OpenSettings 入口——直 new 的窗口没挂 Close 里的收摊逻辑，测了个寂寞。
            int settlesBefore = CaptureController.SettlesForTest;
            App.OpenSettings();
            FlushIdle();
            foreach (var w in Application.Current.Windows.OfType<LikeShot.UI.SettingsWindow>().ToArray()) w.Close();
            FlushIdle();
            log.AppendLine($"设置窗收摊：{(CaptureController.SettlesForTest > settlesBefore ? "关窗即收（对）" : "没收（不对）")}");

        }

        File.WriteAllText(Path.Combine(dir, "settings-report.txt"), log.ToString());
        exit();
    }

    /// <summary>
    /// --selftest --settingsui：把设置窗口开在那里不退出，方便抓屏看版式。
    /// 带 --seedhistory 就先往 Temp 的独立库里灌一份假条目（各种来源、尺寸、收藏），
    /// 不然历史那一页永远是空的，看不出卡片排得对不对；带 --shot 就自己抓一张再收摊。
    /// </summary>
    public static async void ShowSettings()
    {
        var args = Environment.GetCommandLineArgs();
        int section = args.Contains("--historyui") ? LikeShot.UI.SettingsWindow.HistorySectionIndex : 0;

        if (args.Contains("--seedhistory"))
        {
            string root = Path.Combine(Path.GetTempPath(), "likeshot-history-ui");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var store = HistoryStore.UseRootForTest(root);
            var kinds = new[] { HistoryStore.SourceCapture, HistoryStore.SourceLong, HistoryStore.SourcePin, HistoryStore.SourceReplay };
            var sizes = new (int w, int h)[] { (1440, 900), (900, 1600), (2400, 1200), (640, 480), (1920, 1080), (760, 1400) };
            for (int i = 0; i < 14; i++)
            {
                var (w, h) = sizes[i % sizes.Length];
                store.Add(TestTile(w, h, i), kinds[i % kinds.Length]);
                await Task.Delay(10);
            }
            var favorite = store.Entries[^1];
            store.SetFavorite(favorite.Id, true);
            Log.Write($"取证用历史库灌了 {store.Entries.Count} 张，目录={root}");
        }

        App.OpenSettings(section);
        var window = System.Windows.Application.Current.Windows.OfType<LikeShot.UI.SettingsWindow>().FirstOrDefault();
        window?.RaiseTopmostForTest();

        if (!args.Contains("--shot")) return;
        await Task.Delay(2500);   // 等缩略图逐张解完、版式落定
        Log.Write($"取证窗口位置 DIP={window?.Left:0},{window?.Top:0} 尺寸={window?.ActualWidth:0}x{window?.ActualHeight:0} " +
                  $"已显示={window?.IsVisible} 已加载={window?.IsLoaded} 置顶={window?.Topmost} 进的第 {section} 页");

        string shot = Path.Combine(Path.GetTempPath(), "likeshot-selftest", "settings.png");
        Directory.CreateDirectory(Path.GetDirectoryName(shot)!);
        var error = RenderWindowToFile(window, shot);
        Log.Write(error == null ? $"版式取证已渲染 {shot}" : $"版式取证渲染失败：{error}");
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 版式取证直接渲染那扇窗的视觉树，而不是截整张桌面：
    /// 从命令行拉起来的进程拿不到前台权限，截屏只会拍到压在上面的浏览器，取证就白做了。
    /// </summary>
    private static string? RenderWindowToFile(Window? window, string path)
    {
        if (window == null) return "设置窗口没开起来";
        int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
        if (width < 10 || height < 10) return $"窗口尺寸没落定 {width}x{height}";
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(window);
        target.Freeze();
        return Exporter.TryWrite(target, path);
    }

    /// <summary>取证用的假图：一块斜向渐变 + 序号色块，缩略图之间一眼能分辨。</summary>
    private static System.Windows.Media.Imaging.BitmapSource TestTile(int width, int height, int index)
    {
        var from = Color.FromRgb((byte)(40 + index * 15), 90, (byte)(200 - index * 11));
        var to = Color.FromRgb(240, (byte)(60 + index * 13), (byte)(90 + index * 9));
        return LikeShot.Drawing.RenderSurface.ToBitmap(width, height, dc =>
        {
            var brush = new LinearGradientBrush(from, to, 45);
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 250, 250)), null,
                new Rect(width * 0.12, height * 0.12, width * 0.24, height * 0.12));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null,
                new Rect(width * 0.12, height * 0.42, width * 0.6, height * 0.3));
        });
    }

    /// <summary>
    /// 造一个「真实页面」来验拼接：大片纯白 + 零星文字行 + 固定表头/底栏。
    /// 白行在页面里成百次出现，正是把滚动量量错、把重叠区重复贴一遍的元凶，
    /// 所以断言不看高度对不对，而是逐行比画布和原页面——重复一行都算不过。
    /// </summary>
    private static string StitchingCheck()
    {
        var sb = new StringBuilder();
        sb.AppendLine(StitchScenario("有固定表头+底栏", header: 40, footer: 30));
        sb.AppendLine(StitchScenario("无固定条", header: 0, footer: 0));
        sb.AppendLine(StitchScenario("只有底栏", header: 0, footer: 26));
        return sb.ToString().TrimEnd();
    }

    private static string StitchScenario(string label, int header, int footer)
    {
        const int width = 600, frameHeight = 400, pageRows = 6000, scroll = 90, frames = 40;
        int stride = width * 4;
        var rnd = new Random(11);

        // 页面：整张涂白，再按不规则间距撒「文字行」——每行是一段随机深色像素。
        var page = new byte[stride * pageRows];
        for (int i = 0; i < page.Length; i += 4) { page[i] = 255; page[i + 1] = 255; page[i + 2] = 255; page[i + 3] = 255; }
        for (int y = 3; y < pageRows; y += 3 + rnd.Next(9))
        {
            int run = 20 + rnd.Next(width - 20);
            for (int x = rnd.Next(40); x < run; x++)
            {
                int i = y * stride + x * 4;
                byte v = (byte)rnd.Next(120);
                page[i] = v; page[i + 1] = v; page[i + 2] = v;
            }
        }

        var head = new byte[stride * header];
        var foot = new byte[stride * footer];
        for (int i = 0; i < head.Length; i += 4) { head[i] = 200; head[i + 1] = 210; head[i + 2] = 220; head[i + 3] = 255; }
        for (int i = 0; i < foot.Length; i += 4) { foot[i] = 30; foot[i + 1] = 40; foot[i + 2] = 50; foot[i + 3] = 255; }

        byte[] Frame(int pageTop)
        {
            var f = new byte[stride * frameHeight];
            Buffer.BlockCopy(head, 0, f, 0, head.Length);
            int band = frameHeight - header - footer;
            Buffer.BlockCopy(page, pageTop * stride, f, header * stride, band * stride);
            Buffer.BlockCopy(foot, 0, f, (frameHeight - footer) * stride, foot.Length);
            return f;
        }

        var rect = new Int32Rect(0, 0, width, frameHeight);
        var stitcher = new LongStitcher(rect, Frame(0), stride);
        var appends = new List<int>();
        for (int k = 1; k <= frames; k++)
        {
            appends.Add(stitcher.Feed(Frame(k * scroll), stride, frameHeight));
            if (k == 5) appends.Add(stitcher.Feed(Frame(k * scroll), stride, frameHeight)); // 中途喂一帧重复的，应 +0
        }

        // 文字行在页面里各不重复：画布从上往下读它们，页码必须严格递增。
        // 重复贴一段（老 bug）或者贴歪了都会当场破坏这条性质；空白行被当底栏裁掉则不受影响。
        var pageIndex = new Dictionary<long, int>();
        for (int y = 0; y < pageRows; y++)
        {
            long h = LongStitcher.HashRow(page, y * stride, stride);
            if (h == WhiteRowHash(page, stride)) continue;
            if (pageIndex.ContainsKey(h)) pageIndex.Remove(h); // 撞哈希的行不可信，整条弃用
            else pageIndex[h] = y;
        }

        long[] canvasHashes = stitcher.CanvasRowHashes();
        int prevPage = -1, outOfOrder = 0, firstBad = -1, inkRows = 0;
        for (int i = header; i < canvasHashes.Length; i++)
        {
            if (!pageIndex.TryGetValue(canvasHashes[i], out int pageRow)) continue;
            inkRows++;
            if (pageRow <= prevPage) { outOfOrder++; if (firstBad < 0) firstBad = i; }
            prevPage = pageRow;
        }

        int expected = frames * scroll + frameHeight - footer;
        bool reachedDeep = prevPage >= (frames - 2) * scroll;   // 最后一屏的内容得在里面
        bool heightOk = stitcher.Height <= expected + 4 && stitcher.Height >= expected - 200;
        bool pass = outOfOrder == 0 && reachedDeep && heightOk && inkRows > 150 && appends.Count(a => a == 0) >= 1;
        return $"{label}: 高度={stitcher.Height} 期望≈{expected}{(heightOk ? "" : " ✗高度")} " +
               $"文字行={inkRows} 乱序/重复={outOfOrder}{(firstBad >= 0 ? $" 首错画布行={firstBad}" : "")} " +
               $"最深页行={prevPage} 每帧追加=[{string.Join(",", appends.Take(10))}] " +
               $"每帧追加(尾)=[{string.Join(",", appends.TakeLast(6))}] {(pass ? "通过" : "不通过")}";
    }

    private static long WhiteRowHash(byte[] page, int stride)
        => LongStitcher.HashRow(page, 0, stride);

    private static string HotkeyCheck()
    {
        var qq = Process.GetProcessesByName("QQ").Length;
        var tim = Process.GetProcessesByName("Tim").Length;
        var chord = LikeShot.Hotkeys.Chord.Parse(new AppConfig().CaptureChord);
        var free = LikeShot.Hotkeys.ChordProbe.IsFree(chord);
        return "hotkey: " +
               $"QQ进程={qq} TIM进程={tim} 本机可注册={free}（QQ 在跑时应为 True 之外都算异常） " +
               $"托盘那句状态=「{App.Arbiter?.StatusText ?? "仲裁器没起来"}」 " +
               $"判定明细={App.Arbiter?.YieldDiagForTest ?? "无"}";
    }
}
