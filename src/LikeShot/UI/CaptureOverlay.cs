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
    private TextAnnotation? _editingText;
    private bool _pickingColor;   // 吸管模式：下一次点击屏幕就是取色
    private Border _propBar = null!;
    private TextBlock _propSizeText = null!;
    private Slider _propRadius = null!;
    private TextBlock _propRadiusValue = null!;
    private CheckBox _propShadow = null!;

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
        _toolbar.ColorPickRequested += () => _pickingColor = true;
        // 状态行会把工具栏撑高，撑高之后要立刻重挑落点，不然多出来那截会被窗口底边裁掉。
        _toolbar.RepositionRequested += PositionToolbar;
        BuildPropBar();

        var root = new Grid();
        root.Children.Add(_host);
        root.Children.Add(_chrome);
        Content = root;

        session.Changed += OnSessionChanged;
        session.TextPlacementRequested += PlaceTextEditor;
        session.TextEditRequested += EditTextEditor;

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
        var below = new Point(s.X, s.Y + s.Height + 10);
        // 选区贴着屏幕底边时锚点会落到桌面外，改取选区内部的下沿点——
        // 工具栏跟着塞进选区里，而不是整条消失。
        bool belowOnScreen = _session.Displays.Any(d =>
            d.Contains((int)below.X, (int)below.Y));
        return belowOnScreen ? below : new Point(s.X, s.Y + s.Height - 1);
    }

    private void OnSessionChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _host.InvalidateVisual();
            PositionToolbar();
            PositionPropBar();
        });
    }

    // ---------- 选区属性条：尺寸 + 圆角 + 阴影 ----------

    private void BuildPropBar()
    {
        _propSizeText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Theme.Brush(Theme.LoupeText),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 72
        };
        _propRadius = new Slider
        {
            Minimum = 0,
            Maximum = 32,
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
            Style = ToolbarStrip.SliderStyle()
        };
        _propRadius.ValueChanged += (_, e) =>
            _session.SetCornerRadius((int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero));
        _propRadiusValue = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.Brush(Theme.LoupeText),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 18,
            TextAlignment = TextAlignment.Center
        };
        _propShadow = MakeShadowCheck();
        _propShadow.Click += (_, _) => _session.SetDropShadow(_propShadow.IsChecked == true);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_propSizeText);
        row.Children.Add(new TextBlock
        {
            Text = "圆角",
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0)
        });
        row.Children.Add(_propRadius);
        row.Children.Add(_propRadiusValue);
        row.Children.Add(new TextBlock
        {
            Text = "阴影",
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.LoupeMuted),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0)
        });
        row.Children.Add(_propShadow);

        _propBar = new Border
        {
            Background = Theme.Brush(Theme.LoupeBackground),
            BorderBrush = Theme.Brush(Theme.BarBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 5, 10, 5),
            Child = row,
            Visibility = Visibility.Collapsed
        };
        _chrome.Children.Add(_propBar);
    }

    private void PositionPropBar()
    {
        if (!_session.HasSelection || _toolbar.Visibility != Visibility.Visible)
        {
            _propBar.Visibility = Visibility.Collapsed;
            return;
        }

        var sel = _session.FinalRect();
        _propSizeText.Text = $"{sel.Width} × {sel.Height}";
        if ((int)Math.Round(_propRadius.Value, MidpointRounding.AwayFromZero) != _session.CornerRadiusDp)
            _propRadius.Value = _session.CornerRadiusDp;
        _propRadiusValue.Text = _session.CornerRadiusDp.ToString();
        _propShadow.IsChecked = _session.DropShadow;

        var dip = _display.ToDip(sel.X, sel.Y);
        _propBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _propBar.DesiredSize.Width, h = _propBar.DesiredSize.Height;
        double left = Math.Clamp(dip.X, 4, Math.Max(4, _display.Bounds.Width / _display.Scale - w - 4));
        double top = dip.Y - h - 8;
        if (top < 2) top = dip.Y + 8;
        Canvas.SetLeft(_propBar, left);
        Canvas.SetTop(_propBar, top);
        _propBar.Visibility = Visibility.Visible;
    }

    /// <summary>属性条上的勾选框，和设置窗口那款同一画法，尺寸缩到 15。</summary>
    private static CheckBox MakeShadowCheck()
    {
        var style = new Style(typeof(CheckBox));
        var template = new ControlTemplate(typeof(CheckBox));
        var box = new FrameworkElementFactory(typeof(Border));
        box.Name = "box";
        box.SetValue(Border.WidthProperty, 15.0);
        box.SetValue(Border.HeightProperty, 15.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        box.SetValue(Border.BackgroundProperty, Theme.Brush(Colors.White));
        box.SetValue(Border.BorderBrushProperty, Theme.Brush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);

        var check = new FrameworkElementFactory(typeof(TextBlock));
        check.Name = "check";
        check.SetValue(TextBlock.TextProperty, "\uE73E");
        check.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        check.SetValue(TextBlock.FontSizeProperty, 9.0);
        check.SetValue(TextBlock.ForegroundProperty, Theme.Brush(Colors.White));
        check.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 0, 0));
        check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(check);
        template.VisualTree = box;

        var isChecked = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Brush(Theme.Accent), "box"));
        isChecked.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Brush(Theme.Accent), "box"));
        isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "check"));
        template.Triggers.Add(isChecked);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        return new CheckBox { Style = style, VerticalAlignment = VerticalAlignment.Center };
    }

    private void PositionToolbar()
    {
        if (!_session.HasSelection || !_display.Contains((int)ToolbarAnchor().X, (int)ToolbarAnchor().Y))
        {
            _toolbar.Visibility = Visibility.Collapsed;
            return;
        }

        var s = _session.FinalRect();
        double w = _toolbar.NaturalWidth, h = _toolbar.NaturalHeight;
        double scale = _display.Scale;
        double left = Math.Clamp((s.X - _display.Bounds.X) / scale, 4,
            Math.Max(4, _display.Bounds.Width / scale - w - 4));
        double maxTop = _display.Bounds.Height / scale - h - 4;

        // 三个落点依次挑：选区下方 → 选区上方 → 选区内部贴下沿（贴底选区的兜底）。
        double belowTop = (s.Y + s.Height + 10 - _display.Bounds.Y) / scale;
        double aboveTop = (s.Y - _display.Bounds.Y) / scale - h - 8;
        double insideTop = (s.Y + s.Height - 1 - _display.Bounds.Y) / scale - h - 8;
        double top = belowTop + h <= maxTop ? belowTop
            : aboveTop >= 4 ? aboveTop
            : Math.Max(4, insideTop);

        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);
        _toolbar.Visibility = Visibility.Visible;
    }

    // ---------- 输入 ----------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = ToPhysical(e.GetPosition(this));

        // 吸管模式：这一下点哪儿，色就从哪儿来，然后交还给工具栏。
        if (_pickingColor)
        {
            var picked = _session.Snapshot.PixelAt((int)p.X, (int)p.Y);
            if (picked.A == 0) picked = Colors.Black;
            _session.SetColor(picked);
            _pickingColor = false;
            e.Handled = true;
            return;
        }

        _session.PointerDown(p, LeftOrRight.Left);
        // 用户已经动手改东西了，上一条回执就该收起来，不然红色错误会一直挂着。
        _toolbar.ClearStatus();
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
        // 输入框开着的时候键盘归打字的人：这时候 Delete/Backspace 是删字，不是删标注。
        if (_textEditor != null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_session.OnKeyDown(e.Key, ctrl, shift)) e.Handled = true;
    }

    private void UpdateCursor(Point physical)
    {
        if (_pickingColor) { Cursor = Cursors.Cross; return; }
        if (!_session.HasSelection) { Cursor = Cursors.Cross; return; }

        // 选中内容的手柄优先于一切：它压在选择区的边框手柄之上，两者挨得很近时先想用户要改谁。
        int itemHandle = _session.HitItemHandle(physical);
        if (itemHandle >= 0) { Cursor = ResizeCursor(itemHandle); return; }
        if (_session.OverItem(physical)) { Cursor = Cursors.SizeAll; return; }
        if (_session.Tool != ToolKind.None) { Cursor = _session.Tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross; return; }

        // 手柄点和四条边框线都给缩放光标，边框外一 needle 的容差也算数。
        int handle = _session.HitHandle(physical);
        if (handle >= 0) { Cursor = ResizeCursor(handle); return; }

        if (!_session.Inside(physical)) { Cursor = Cursors.Cross; return; }
        Cursor = Cursors.SizeAll;
    }

    private static Cursor ResizeCursor(int handle) => handle switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        3 or 7 => Cursors.SizeWE,
        _ => Cursors.Arrow
    };

    // ---------- 文字工具 ----------

    private void PlaceTextEditor(Point physical)
    {
        if (!_display.Contains((int)physical.X, (int)physical.Y)) return;
        OpenTextEditor(physical, "", _session.Color, _session.FontSize, editTarget: null);
    }

    /// <summary>双击已有文字：原字先藏起来，编辑框里垫着旧内容，Enter 替换、Esc 不动。</summary>
    private void EditTextEditor(TextAnnotation target)
    {
        OpenTextEditor(target.Position, target.Text, target.Color, target.FontSize, editTarget: target);
    }

    private void OpenTextEditor(Point physical, string initial, Color color, int fontSizePx, TextAnnotation? editTarget)
    {
        CommitTextEditor();

        var dip = _display.ToDip((int)physical.X, (int)physical.Y);
        _textAnchor = physical;
        _editingText = editTarget;
        if (_editingText != null) _editingText.Hidden = true;
        _textEditor = new TextBox
        {
            Text = initial,
            AcceptsReturn = true,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            Foreground = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1, 1, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
            FontSize = fontSizePx / _display.Scale,
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
        Canvas.SetTop(_textEditor, dip.Y - fontSizePx / _display.Scale * 0.12);
        _textEditor.Focus();
        _textEditor.SelectAll();   // 双击改字：进来就全选，直接打字就替换
    }

    private void CommitTextEditor(bool discard = false)
    {
        if (_textEditor == null) return;
        var text = _textEditor.Text.TrimEnd('\r', '\n');
        _chrome.Children.Remove(_textEditor);
        _textEditor = null;
        var target = _editingText;
        _editingText = null;
        if (target != null)
        {
            target.Hidden = false;   // 不管改没改成，旧字都放回来
            if (!discard && text.Length > 0) _session.UpdateText(target, text);
        }
        else if (!discard && text.Length > 0)
        {
            _session.AddText(text, _textAnchor);
        }
        // 不把工具弹回「无」：QQ 落完字文字工具保持选中，点一下就能继续放下一个。
    }

    internal void CommitPendingText() => CommitTextEditor();

    /// <summary>把出图结果写在工具栏下面那句回执上；bad 时红字，旁边可以挂一个补救动作。</summary>
    internal void ShowStatus(string text, bool bad, string? actionLabel = null, Action? action = null)
        => Dispatcher.Invoke(() => _toolbar.ShowStatus(text, bad, actionLabel, action));

    internal void ClearStatus() => Dispatcher.Invoke(_toolbar.ClearStatus);

    /// <summary>
    /// 弹系统对话框期间把遮罩层收起来：截图层是 topmost，模态框会被它压住，
    /// 收起来又不销毁窗口，用户取消之后原样回到刚才的选区。
    /// </summary>
    internal void SetDialogVisible(bool visible) => Dispatcher.Invoke(() =>
    {
        if (visible)
        {
            Show();
            Activate();
            Keyboard.Focus(this);
        }
        else Hide();
    });

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
            double radiusPx = session.CornerRadiusDp * scale;
            if (session.Phase == CapturePhase.Selected)
            {
                keep = session.FinalRect();
                keepGeometry = session.PolygonPoints is { Count: >= 3 }
                    ? RectExt.ClosedPath(session.PolygonPoints)
                    : radiusPx > 0
                        ? new RectangleGeometry(RectFrom(keep), radiusPx, radiusPx)
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
                // 阴影实时预览：分层软影直接叠在暗区上；自由截图没有圆角和影子。
                if (session.DropShadow && session.PolygonPoints is not { Count: >= 3 })
                {
                    CaptureSession.DrawSoftShadow(dc, RectFrom(keep), radiusPx, 0, scale);
                }

                var mosaics = session.Mosaics;
                if (mosaics != null)
                {
                    // 标注裁在选区里：画笔拖出去不该漏到暗区上，导出时本来也是裁掉的。
                    dc.PushClip(keepGeometry);
                    session.Layer.DrawAll(dc, new RenderContext(mosaics, scale, mosaics.Region));
                    dc.Pop();
                }
                DrawSelectionChrome(dc, session, scale, keep);
                SelectionChrome.Draw(dc, session, scale);
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

            if (session.PolygonPoints is { Count: >= 3 })
            {
                dc.DrawRectangle(null, pen, rect);
                return;
            }

            double radiusPx = session.CornerRadiusDp * scale;
            if (radiusPx > 0) dc.DrawGeometry(null, pen, new RectangleGeometry(rect, radiusPx, radiusPx));
            else dc.DrawRectangle(null, pen, rect);

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
