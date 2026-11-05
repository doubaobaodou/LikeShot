using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Capture;
using LikeShot.Drawing;
using LikeShot.Native;

namespace LikeShot.UI;

/// <summary>
/// 一屏一个覆盖层。层内所有绘制坐标先用「物理像素」表达，再由一个
/// 平移+缩放变换映射到这台显示器的 DIP，所以 3200×2000 @175% 上也是逐像素对齐的。
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly CaptureSession _session;
    private readonly Display _display;
    private readonly RenderHost _host;
    private readonly Canvas _chrome;
    private readonly ToolbarStrip _toolbar;
    private TextBox? _textEditor;
    private Point _textAnchor;

    public CaptureOverlay(CaptureSession session, Display display)
    {
        _session = session;
        _display = display;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var rect = display.DIPWindowRect;
        Left = rect.X;
        Top = rect.Y;
        Width = rect.Width;
        Height = rect.Height;

        _host = new RenderHost(this);
        _chrome = new Canvas { IsHitTestVisible = true };
        _toolbar = new ToolbarStrip(session, display) { Visibility = Visibility.Collapsed };
        _chrome.Children.Add(_toolbar);

        var root = new Grid();
        root.Children.Add(_host);
        root.Children.Add(_chrome);
        Content = root;

        session.Changed += OnSessionChanged;
        session.TextPlacementRequested += PlaceTextEditor;

        MouseLeftButtonDown += OnMouseDown;
        MouseRightButtonDown += OnRightDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += OnUp;
        KeyDown += OnKeyDownLocal;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Tab) e.Handled = true; };
    }

    public CaptureSession Session => _session;
    public Display DisplayInfo => _display;
    public double Scale => _display.Scale;

    public void FinishSetup()
    {
        Title = "LikeShot 截图";
        Show();
        Activate();
        Focusable = true;
        Keyboard.Focus(this);
        var hwnd = new WindowInteropHelper(this).Handle;
        // 只置顶，不加 WS_EX_TOOLWINDOW：ShowInTaskbar=false 已经够干净，
        // 而 TOOLWINDOW 会让截图层从窗口枚举里消失，自动化测试抓不到它。
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public Point ToPhysical(Point dip) =>
        new(dip.X * _display.Scale + _display.Bounds.X, dip.Y * _display.Scale + _display.Bounds.Y);

    public Rect ToDip(Int32Rect physical) =>
        new((physical.X - _display.Bounds.X) / _display.Scale,
           (physical.Y - _display.Bounds.Y) / _display.Scale,
            physical.Width / _display.Scale,
            physical.Height / _display.Scale);

    public bool OwnsToolbarAnchor =>
        _display.Contains((int)ToolbarAnchor().X, (int)ToolbarAnchor().Y);

    private Point ToolbarAnchor()
    {
        var s = _session.FinalRect();
        return new Point(s.X, s.Y + s.Height + 10);
    }

    private void OnSessionChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _host.InvalidateVisual();
            PositionToolbar();
        });
    }

    private void PositionToolbar()
    {
        if (!_session.HasSelection || !_display.Contains((int)ToolbarAnchor().X, (int)ToolbarAnchor().Y))
        {
            _toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        var anchor = ToolbarAnchor();
        double w = _toolbar.NaturalWidth, h = _toolbar.NaturalHeight;
        var dip = _display.ToDip((int)anchor.X, (int)anchor.Y);
        double top = dip.Y;
        if (top + h > _display.Bounds.Height / _display.Scale - 4)
        {
            var s = _session.FinalRect();
            top = Math.Max(4, (s.Y - _display.Bounds.Y) / _display.Scale - h - 8);
        }
        double left = Math.Clamp(dip.X, 4, Math.Max(4, _display.Bounds.Width / _display.Scale - w - 4));

        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);
        _toolbar.Visibility = Visibility.Visible;
    }

    // ---------- 输入 ----------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(this));
        _session.PointerDown(p, LeftOrRight.Left);
        if (e.ClickCount == 2) _session.DoubleClick(p);
        _host.InvalidateVisual();
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        _session.PointerDown(ToPhysical(e.GetPosition(this)), LeftOrRight.Right);
        _host.InvalidateVisual();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(this));
        _session.PointerMove(p);
        UpdateCursor(p);
        _host.InvalidateVisual();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(this));
        _session.PointerUp(p);
        _host.InvalidateVisual();
        if (e.ChangedButton == MouseButton.Right && _session.RightClickWasEmpty) ShowContextMenu(p);
    }

    private void ShowContextMenu(Point physical)
    {
        var menu = new ContextMenu();

        MenuItem Item(string text, Action action, string gesture)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture };
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(Item("完成截图", () => _session.Finish(CopyMode.Clipboard), "双击 / Enter"));
        menu.Items.Add(Item("保存", () => _session.Finish(CopyMode.File), "Ctrl+S"));
        menu.Items.Add(Item("钉在桌面上", () => _session.Finish(CopyMode.Pin), ""));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("重新选择截图区域", () => _session.ResetSelection(), ""));
        menu.Items.Add(Item("撤销编辑", () => _session.Undo(), "Ctrl+Z"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出截图", () => _session.Exit(), "Esc"));

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = (physical.X - _display.Bounds.X) / _display.Scale;
        menu.VerticalOffset = (physical.Y - _display.Bounds.Y) / _display.Scale;
        menu.IsOpen = true;
    }

    private void OnKeyDownLocal(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_session.OnKeyDown(e.Key, ctrl, shift)) e.Handled = true;
    }

    private void UpdateCursor(Point physical)
    {
        if (!_session.HasSelection) { Cursor = Cursors.Cross; return; }
        if (_session.Tool != ToolKind.None) { Cursor = _session.Tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross; return; }

        var r = _session.FinalRect();
        if (!_session.Inside(physical)) { Cursor = Cursors.Cross; return; }

        foreach (var (handle, point) in _session.HandleWithPositions())
        {
            double tol = Math.Max(6, 10 / _display.Scale);
            if (Math.Abs(point.X - physical.X) <= tol && Math.Abs(point.Y - physical.Y) <= tol)
            {
                Cursor = handle switch
                {
                    0 or 4 => Cursors.SizeNWSE,
                    2 or 6 => Cursors.SizeNESW,
                    1 or 5 => Cursors.SizeNS,
                    3 or 7 => Cursors.SizeWE,
                    _ => Cursors.Arrow
                };
                return;
            }
        }
        Cursor = Cursors.SizeAll;
    }

    // ---------- 文字工具 ----------

    private void PlaceTextEditor(Point physical)
    {
        if (!_display.Contains((int)physical.X, (int)physical.Y)) return;
        CommitTextEditor();

        var dip = _display.ToDip((int)physical.X, (int)physical.Y);
        _textAnchor = physical;
        _textEditor = new TextBox
        {
            Text = "",
            AcceptsReturn = true,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            Foreground = new SolidColorBrush(_session.Color),
            BorderBrush = new SolidColorBrush(_session.Color),
            BorderThickness = new Thickness(1, 1, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
            FontSize = _session.FontSize / _display.Scale,
            Padding = new Thickness(0),
            MinWidth = 24,
            MaxWidth = Math.Max(24, _display.Bounds.Width / _display.Scale - dip.X - 8)
        };
        _textEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CommitTextEditor(discard: true); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                CommitTextEditor();
                e.Handled = true;
            }
        };
        _chrome.Children.Add(_textEditor);
        Canvas.SetLeft(_textEditor, dip.X);
        Canvas.SetTop(_textEditor, dip.Y - _session.FontSize / _display.Scale * 0.12);
        _textEditor.Focus();
    }

    private void CommitTextEditor(bool discard = false)
    {
        if (_textEditor == null) return;
        var text = _textEditor.Text.TrimEnd('\r', '\n');
        _chrome.Children.Remove(_textEditor);
        _textEditor = null;
        if (!discard && text.Length > 0) _session.AddText(text, _textAnchor);
        _session.SetTool(ToolKind.None);
    }

    internal void CommitPendingText() => CommitTextEditor();

    // ---------- 绘制 ----------

    private sealed class RenderHost : FrameworkElement
    {
        private readonly CaptureOverlay _owner;
        public RenderHost(CaptureOverlay owner) => _owner = owner;

        protected override void OnRender(DrawingContext dc)
        {
            var session = _owner._session;
            var display = _owner._display;
            double scale = display.Scale;

            var transform = new TransformGroup();
            transform.Children.Add(new TranslateTransform(-display.Bounds.X, -display.Bounds.Y));
            transform.Children.Add(new ScaleTransform(1 / scale, 1 / scale));
            dc.PushTransform(transform);

            var full = session.Snapshot.VirtualBounds;
            dc.DrawImage(session.Snapshot.Image, new Rect(full.X, full.Y, full.Width, full.Height));

            Int32Rect keep = Int32Rect.Empty;
            Geometry? keepGeometry = null;
            if (session.Phase == CapturePhase.Selected)
            {
                keep = session.FinalRect();
                keepGeometry = session.PolygonPoints is { Count: >= 3 }
                    ? RectExt.ClosedPath(session.PolygonPoints)
                    : new RectangleGeometry(RectFrom(keep));
                DrawDimOutside(dc, full, keepGeometry);
            }
            else if (session.HoverWindow is { } hover)
            {
                DrawDimOutside(dc, full, new RectangleGeometry(RectFrom(hover)));
                DrawWindowSuggestion(dc, hover, scale);
            }
            else
            {
                dc.DrawRectangle(Theme.Brush(Theme.DimOutside), null, new Rect(full.X, full.Y, full.Width, full.Height));
            }

            if (session.Phase == CapturePhase.Selected)
            {
                var mosaics = session.Mosaics;
                if (mosaics != null)
                {
                    session.Layer.DrawAll(dc, new RenderContext(mosaics, scale, mosaics.Region));
                }
                DrawSelectionChrome(dc, session, scale, keep);
            }

            if (session.ShowingLoupe && App.Config.ShowMagnifier)
                DrawLoupe(dc, session, display, scale);

            dc.Pop();
        }

        private static Rect RectFrom(Int32Rect r) => new(r.X, r.Y, r.Width, r.Height);

        private static void DrawDimOutside(DrawingContext dc, Int32Rect full, Geometry keep)
        {
            var everything = new RectangleGeometry(RectFrom(full));
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, everything, keep);
            dc.DrawGeometry(Theme.Brush(Theme.DimOutside), null, dim);
        }

        private static void DrawWindowSuggestion(DrawingContext dc, Int32Rect hover, double scale)
        {
            var pen = new Pen(Theme.Brush(Theme.Accent), Math.Max(1, 2 * scale));
            dc.DrawRectangle(null, pen, RectFrom(hover));
        }

        private static void DrawSelectionChrome(DrawingContext dc, CaptureSession session, double scale, Int32Rect sel)
        {
            var rect = RectFrom(sel);
            var pen = new Pen(Theme.Brush(Theme.SelectionBorder), Math.Max(1, 1 * scale));
            dc.DrawRectangle(null, pen, rect);

            if (session.PolygonPoints is { Count: >= 3 }) return;

            double hs = Math.Max(5, 7 * scale);
            foreach (var p in session.HandleWithPositions().Select(x => x.Point))
                dc.DrawRectangle(Theme.Brush(Theme.HandleFill), pen,
                    new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs));

            // 尺寸标签贴在选区左上角外侧，放不下就贴内侧。
            string label = $"{sel.Width} × {sel.Height}";
            var text = Text(dc, label, 12 * scale, Theme.LoupeText, scale);
            double lx = rect.X, ly = rect.Y - text.Height - 4 * scale;
            if (ly < 0) ly = rect.Y + 2 * scale;
            dc.DrawRectangle(Theme.Brush(Theme.LoupeBackground), null,
                new Rect(lx, ly, text.Width + 10 * scale, text.Height + 4 * scale));
            dc.DrawText(text, new Point(lx + 5 * scale, ly + 2 * scale));
        }

        private static FormattedText Text(DrawingContext dc, string s, double sizePx, Color color, double scale) =>
            new(s,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                sizePx,
                Theme.Brush(color),
                scale);

        private void DrawLoupe(DrawingContext dc, CaptureSession session, Display display, double scale)
        {
            var cursor = session.Cursor;
            var sel = session.FinalRect();
            string sizeLine = session.HasSelection
                ? $"{sel.Width} × {sel.Height}"
                : $"{(int)cursor.X} , {(int)cursor.Y}";
            var color = session.Snapshot.PixelAt((int)cursor.X, (int)cursor.Y);
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}   RGB {color.R},{color.G},{color.B}";

            // 132 是 QQ 的基准宽，但色值行带上 RGB 可能放不下——按最宽一行加宽，
            // 格子尺寸只跟 boxW 走（永远 8×scale 方格），宽一点只是多看几列。
            var sizeText = Text(dc, sizeLine, 12 * scale, Theme.LoupeText, scale);
            var hexText = Text(dc, hex, 12 * scale, Theme.LoupeText, scale);
            var hintText = Text(dc, "按 C 复制色号 · 右键自由截图", 11 * scale, Theme.LoupeMuted, scale);
            double boxW = Math.Max(Theme.LoupeWidth * scale,
                Math.Max(sizeText.Width, Math.Max(hexText.Width, hintText.Width)) + 14 * scale);

            double imgH = Theme.LoupeImageHeight * scale;
            double rowH = 20 * scale;
            double boxH = imgH + rowH * 3 + 10 * scale;

            double x = cursor.X + 24 * scale;
            double y = cursor.Y + 24 * scale;
            var vb = session.Snapshot.VirtualBounds;
            if (x + boxW > vb.X + vb.Width) x = cursor.X - boxW - 24 * scale;
            if (y + boxH > vb.Y + vb.Height) y = cursor.Y - boxH - 24 * scale;

            dc.DrawRectangle(Theme.Brush(Theme.LoupeBackground),
                new Pen(Theme.Brush(Theme.BarBorder), Math.Max(1, scale)),
                new Rect(x, y, boxW, boxH));

            int cw = Math.Max(1, (int)(boxW / (8 * scale)));
            int ch = Math.Max(1, (int)(imgH / (8 * scale)));
            double cell = boxW / cw;
            int originX = (int)cursor.X - cw / 2;
            int originY = (int)cursor.Y - ch / 2;

            for (int gy = 0; gy < ch; gy++)
                for (int gx = 0; gx < cw; gx++)
                {
                    var c = session.Snapshot.PixelAt(originX + gx, originY + gy);
                    if (c.A == 0) c = Colors.Black;
                    dc.DrawRectangle(new SolidColorBrush(c), null,
                        new Rect(x + gx * cell, y + 5 * scale + gy * cell, cell + 0.5, cell + 0.5));
                }

            double cx = x + (cw / 2d) * cell, cy = y + 5 * scale + (ch / 2d) * cell;
            var cross = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x40, 0x40)), Math.Max(1, scale));
            dc.DrawLine(cross, new Point(x, cy), new Point(x + boxW, cy));
            dc.DrawLine(cross, new Point(cx, y + 5 * scale), new Point(cx, y + 5 * scale + imgH));

            double ty = y + 8 * scale + imgH;

            dc.DrawText(sizeText, new Point(x + 6 * scale, ty));
            dc.DrawText(hexText, new Point(x + 6 * scale, ty + rowH));
            dc.DrawText(hintText, new Point(x + 6 * scale, ty + rowH * 2));
        }
    }
}
