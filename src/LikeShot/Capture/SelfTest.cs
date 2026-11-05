using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        var log = new StringBuilder();
        string dir = Path.Combine(Path.GetTempPath(), "likeshot-selftest");
        Directory.CreateDirectory(dir);

        try
        {
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
            Exporter.Write(image, outPath);
            log.AppendLine($"rendered={image.PixelWidth}x{image.PixelHeight} bytes={new FileInfo(outPath).Length}");

            // 3) 撤销一次
            int before = session.Layer.Items.Count;
            session.Undo();
            log.AppendLine($"undo: {before} -> {session.Layer.Items.Count}");

            controller.Abort();

            // 4) 长截图拼接：造一个已知滚动量的两帧
            var stitchReport = StitchingCheck();
            log.AppendLine(stitchReport);

            // 5) 热键归属：现在 QQ 在跑吗
            log.AppendLine(HotkeyCheck());
        }
        catch (Exception ex)
        {
            log.AppendLine("FAILED: " + ex);
        }

        string report = Path.Combine(dir, "report.txt");
        File.WriteAllText(report, log.ToString());
        Console.Out.WriteLine(log);
        Console.Out.WriteLine("REPORT " + report + "  DIR " + dir);
        exit();
    }

    /// <summary>
    /// 长截图实跑：自己造一张「每条色带颜色都唯一」的可滚动页面，
    /// 让它自动滚到底，把拼出来的长图写盘。事后逐行认色就能判定不重不漏，
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

            // 自动滚到「连续几秒不再长高」为止，最多 40 秒；实跑的判据是长图本身，不是秒数。
            runner.ToggleAutoScroll();
            int last = runner.AppendedTotal, stuck = 0;
            for (int i = 0; i < 40 && stuck < 4; i++)
            {
                await Task.Delay(1000);
                int now = runner.AppendedTotal;
                log.AppendLine($"t+{i + 1}s 高度={now}");
                stuck = now > last ? 0 : stuck + 1;
                last = now;
            }

            var image = runner.BuildResult();
            if (image != null)
            {
                string path = Path.Combine(dir, "long.png");
                Exporter.Write(image, path);
                log.AppendLine($"成品={image.PixelWidth}x{image.PixelHeight} bytes={new FileInfo(path).Length} -> {path}");
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

        public LongPageWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
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
                    Background = new SolidColorBrush(LineColor(i)),
                });

            var dock = new DockPanel { Background = Brushes.White };
            var head = new Border { Height = 60, Background = new SolidColorBrush(Color.FromRgb(200, 30, 30)) };
            var foot = new Border { Height = 40, Background = new SolidColorBrush(Color.FromRgb(30, 200, 30)) };
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(foot, Dock.Bottom);
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = band,
            };
            dock.Children.Add(head);
            dock.Children.Add(foot);
            dock.Children.Add(scroll);
            Content = dock;
        }

        public static Color LineColor(int index) => Color.FromRgb((byte)(index + 1), 120, 120);
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

        var config = App.Config;
        var arbiter = App.Arbiter;
        if (arbiter == null) { log.AppendLine("FAILED: 仲裁器没起来"); }
        else
        {
            string realChord = config.CaptureChord;
            string[] realYield = config.YieldToProcesses.ToArray();
            log.AppendLine($"让位进程在跑：{string.Join(",", realYield.Where(n => Process.GetProcessesByName(n).Length > 0))}");

            try
            {
                config.CaptureChord = testChord;
                arbiter.ApplyConfig(config);

                // A) QQ 在名单里 → 我们不该接
                arbiter.SetYieldNames(realYield);
                await Settle();
                PressChord();
                await Settle();
                log.AppendLine($"A 名单含 QQ：状态「{arbiter.StatusText}」 我们的截图层={controller.SessionForTest != null}（应为 False）");

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
        }

        File.WriteAllText(Path.Combine(dir, "settings-report.txt"), log.ToString());
        exit();
    }

    /// <summary>--selftest --settingsui：把设置窗口开在那里不退出，方便抓屏看版式。</summary>
    public static void ShowSettings() => App.OpenSettings();

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
        var free = LikeShot.Hotkeys.ChordProbe.IsFree(LikeShot.Hotkeys.Chord.Parse(new AppConfig().CaptureChord));
        return $"hotkey: QQ进程={qq} TIM进程={tim} 本机可注册={free}（QQ 在跑时应为 True 之外都算异常）";
    }
}
