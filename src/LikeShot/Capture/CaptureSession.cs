using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Drawing;
using LikeShot.Native;
using LikeShot.UI;

namespace LikeShot.Capture;

public enum CapturePhase { Choosing, Selected }

/// <summary>
/// 一次截图会话的全部状态。所有坐标一律是「虚拟桌面的物理像素」，
/// 覆盖层只负责把它换算成自己那台显示器的 DIP。
/// </summary>
public sealed class CaptureSession
{
    private const int MinSelection = 4;

    public CaptureSession(ScreenSnapshot snapshot, IReadOnlyList<Display> displays, double uiScale)
    {
        Snapshot = snapshot;
        Displays = displays;
        UiScale = uiScale;
        Layer = new AnnotationLayer();
        Cursor = CurrentPhysicalCursor();
        Color = Theme.DefaultAnnotationColor;
    }

    public ScreenSnapshot Snapshot { get; }
    public IReadOnlyList<Display> Displays { get; }
    public double UiScale { get; }

    public CapturePhase Phase { get; private set; } = CapturePhase.Choosing;
    public Int32Rect Selection { get; private set; }
    public List<Point>? PolygonPoints { get; private set; }
    public Int32Rect? HoverWindow { get; private set; }
    public Point Cursor { get; private set; }

    public ToolKind Tool { get; private set; } = ToolKind.None;
    public Color Color { get; private set; }
    public ShapeKind ShapeCurrent { get; private set; } = ShapeKind.Rect;
    public bool ShapeFilled { get; private set; }

    /// <summary>成图的圆角（96dpi 像素）和投影，选区上方那条属性条里调。</summary>
    public int CornerRadiusDp { get; private set; }
    public bool DropShadow { get; private set; }

    /// <summary>线宽按 96dpi 的像素记，落笔时乘 UiScale——滑条上显示的数字就是它。</summary>
    public int WidthDp { get; private set; } = 4;
    public int FontIndex { get; private set; } = 1;
    public MosaicStyle MosaicKind { get; private set; } = MosaicStyle.Pixelate;
    public int MosaicStrength { get; private set; } = 10;

    public AnnotationLayer Layer { get; }
    public MosaicSource? Mosaics { get; private set; }

    /// <summary>本次会话的覆盖层（一屏一层），长截图时要整体隐藏。</summary>
    public List<LikeShot.UI.CaptureOverlay> Overlays { get; } = [];

    public CaptureController? Controller { get; set; }

    public event Action? Changed;
    public event Action<FinishRequest>? FinishRequested;
    public event Action? ExitRequested;
    public event Action? LongModeRequested;
    public event Action<Point>? TextPlacementRequested;
    public event Action<TextAnnotation>? TextEditRequested;

    private Point _dragStart;
    private Int32Rect _dragOriginal;
    private int _dragHandle = -1;
    private DragMode _drag = DragMode.None;
    private Annotation? _live;
    private Point? _polygonSeed;
    private Point _shapeAnchor;
    private TextAnnotation? _movingText;
    private Vector _textMoveOffset;

    private enum DragMode { None, NewRect, MoveRect, ResizeRect, Stroke, SizingShape, Polygon, MovingText }

    public int StrokeWidth => (int)Math.Round(WidthDp * UiScale);
    public int FontSize => Theme.FontSizesAt(UiScale)[Math.Clamp(FontIndex, 0, 2)];
    public int BadgeRadius => Theme.BadgeRadiusAt(UiScale);

    public bool HasSelection => Phase == CapturePhase.Selected;

    /// <summary>放大镜只在「还没框定」或「正在框」的时候出现，框定后交给尺寸标签。</summary>
    public bool ShowingLoupe => Phase == CapturePhase.Choosing || _drag is DragMode.NewRect or DragMode.Polygon;

    /// <summary>右键按下但没拖出形状，视为「右键点了一下」，弹上下文菜单。</summary>
    public bool RightClickWasEmpty { get; private set; }

    public IEnumerable<(int Index, Point Point)> HandleWithPositions()
    {
        var points = HandlePoints(FinalRect());
        for (int i = 0; i < points.Length; i++) yield return (i, points[i]);
    }

    public void ResetSelection()
    {
        Phase = CapturePhase.Choosing;
        Selection = default;
        PolygonPoints = null;
        Mosaics = null;
        Layer.Clear();
        Tool = ToolKind.None;
        Changed?.Invoke();
    }

    /// <summary>
    /// 把整张快照当成选区。钉图窗的会话没有「框选」这一步，但不设选区的话
    /// 打码源建不出来、导出会直接退回原图把标注全丢掉——按全图路径走一遍就行。
    /// </summary>
    public void UseFullSelection()
    {
        Selection = Snapshot.VirtualBounds;
        Phase = CapturePhase.Selected;
        RebuildMosaics();
    }

    /// <summary>
    /// 直接以给定矩形进入「已框选」状态。再标注（从历史截图里拿一张图接着画）
    /// 走的就是这个入口：那张图没有「框选」这一步，但后面的机器全都要靠选区。
    /// </summary>
    public void SelectRect(Int32Rect rect)
    {
        Selection = rect;
        PolygonPoints = null;
        Phase = CapturePhase.Selected;
        RebuildMosaics();
        Changed?.Invoke();
    }

    public static Point CurrentPhysicalCursor()
    {
        Win32.GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    public Int32Rect FinalRect() => PolygonPoints is { Count: >= 3 }
        ? BoundingBox(PolygonPoints)
        : Selection;

    public bool Inside(Point physical)
    {
        if (PolygonPoints is { Count: >= 3 })
            return RectExt.ClosedPath(PolygonPoints).FillContains(physical);
        return Contains(Selection, physical);
    }

    private static bool Contains(Int32Rect r, Point p) =>
        p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height;

    private static Int32Rect BoundingBox(IEnumerable<Point> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new Int32Rect((int)Math.Round(minX), (int)Math.Round(minY),
            Math.Max(1, (int)Math.Round(maxX - minX)), Math.Max(1, (int)Math.Round(maxY - minY)));
    }

    private static Int32Rect RectFrom(Point a, Point b) => Normalize(
        new Int32Rect((int)a.X, (int)a.Y, (int)(b.X - a.X), (int)(b.Y - a.Y)));

    public static Int32Rect Normalize(Int32Rect r) =>
        new(Math.Min(r.X, r.X + r.Width), Math.Min(r.Y, r.Y + r.Height), Math.Abs(r.Width), Math.Abs(r.Height));

    // ---------- 指针 ----------

    public void PointerDown(Point physical, LeftOrRight button)
    {
        Cursor = physical;
        RightClickWasEmpty = false;

        if (button == LeftOrRight.Right)
        {
            // 有选区时右键只用来弹菜单，不能把选区吃成自由截图的起点；
            // 自由截图只在没有选区的状态下右键拖出来。
            if (HasSelection)
            {
                RightClickWasEmpty = true;
                return;
            }
            PolygonPoints = [physical];
            _polygonSeed = physical;
            _drag = DragMode.Polygon;
            Phase = CapturePhase.Selected;
            Selection = new Int32Rect((int)physical.X, (int)physical.Y, 1, 1);
            Changed?.Invoke();
            return;
        }

        // 点在已放置的文字上就拖动它——不管当前是什么画具，QQ 也这样；
        // 想在文字旁边新建，点文字外面就行。
        if (HasSelection && Inside(physical))
        {
            var hit = HitText(physical);
            if (hit != null)
            {
                _movingText = hit;
                _textMoveOffset = physical - hit.Position;
                _drag = DragMode.MovingText;
                return;
            }
        }

        if (HasSelection && Tool != ToolKind.None && Inside(physical))
        {
            BeginAnnotation(physical);
            return;
        }

        if (HasSelection)
        {
            int handle = HitHandle(physical);
            if (handle >= 0)
            {
                _dragHandle = handle;
                _dragOriginal = Selection;
                _drag = DragMode.ResizeRect;
                return;
            }

            if (Contains(Selection, physical))
            {
                _dragStart = physical;
                _dragOriginal = Selection;
                _drag = DragMode.MoveRect;
                return;
            }
        }

        // 已经有选区了：点选区外面不再重新框选（QQ 里外面的暗区是死的），
        // 要调选区用边框手柄，要重选走右键菜单的「重新选择截图区域」。
        if (HasSelection) return;

        _dragStart = physical;
        _drag = DragMode.NewRect;
        PolygonPoints = null;
        Selection = new Int32Rect((int)physical.X, (int)physical.Y, 1, 1);
        Phase = CapturePhase.Selected;
        Changed?.Invoke();
    }

    public void PointerMove(Point physical)
    {
        bool movedFarEnough = Cursor.X != physical.X || Cursor.Y != physical.Y;
        Cursor = physical;

        switch (_drag)
        {
            case DragMode.Polygon when PolygonPoints is { Count: > 0 }:
                if ((physical - PolygonPoints[^1]).Length > 5) PolygonPoints.Add(physical);
                Selection = BoundingBox(PolygonPoints);
                break;

            case DragMode.NewRect:
                Selection = RectFrom(_dragStart, physical);
                break;

            case DragMode.MoveRect:
                Selection = new Int32Rect(
                    _dragOriginal.X + (int)Math.Round(physical.X - _dragStart.X),
                    _dragOriginal.Y + (int)Math.Round(physical.Y - _dragStart.Y),
                    _dragOriginal.Width, _dragOriginal.Height);
                break;

            case DragMode.ResizeRect:
                Selection = Resize(_dragOriginal, _dragHandle, physical);
                break;

            case DragMode.Stroke:
                GrowLive(physical);
                break;

            case DragMode.SizingShape:
                SizeLive(physical);
                break;

            case DragMode.MovingText:
                if (_movingText != null) _movingText.Position = physical - _textMoveOffset;
                break;

            default:
                if (Phase == CapturePhase.Choosing)
                {
                    var snapped = WindowSnap.AtPoint((int)physical.X, (int)physical.Y);
                    if (!snapped.Equals(HoverWindow)) HoverWindow = snapped;
                }
                break;
        }

        if (movedFarEnough || _drag != DragMode.None) Changed?.Invoke();
    }

    public void PointerUp(Point physical)
    {
        switch (_drag)
        {
            case DragMode.Polygon:
                _polygonSeed = null;
                RightClickWasEmpty = PolygonPoints is { Count: < 3 };
                if (PolygonPoints is { Count: < 3 })
                {
                    // 没拖出形状就松手：回到未选择状态，别留一个 1×1 的洞把全屏罩暗。
                    PolygonPoints = null;
                    Phase = CapturePhase.Choosing;
                    Selection = default;
                }
                RebuildMosaics();
                break;

            case DragMode.NewRect:
                Selection = RectFrom(_dragStart, physical);
                if (Selection.Width < MinSelection || Selection.Height < MinSelection)
                    Selection = WindowSnap.AtPoint((int)physical.X, (int)physical.Y) ?? Selection;
                HoverWindow = null;
                RebuildMosaics();
                break;

            case DragMode.MoveRect:
            case DragMode.ResizeRect:
                _dragHandle = -1;
                RebuildMosaics();
                break;

            case DragMode.MovingText:
                _movingText = null;
                break;

            case DragMode.Stroke:
            case DragMode.SizingShape:
                _live = null;
                break;
        }

        _drag = DragMode.None;
        Changed?.Invoke();
    }

    public void DoubleClick(Point physical)
    {
        if (!HasSelection || !Inside(physical)) return;

        // 双击文字 = 进编辑态；双击空白 = 完成复制。前者优先。
        var hit = HitText(physical);
        if (hit != null)
        {
            _drag = DragMode.None;      // 双击的第二次按下可能还挂着拖动
            _movingText = null;
            TextEditRequested?.Invoke(hit);
            return;
        }
        if (Tool != ToolKind.None) return;
        FinishRequested?.Invoke(new FinishRequest(CopyMode.Clipboard));
    }

    // ---------- 键盘 ----------

    public bool OnKeyDown(Key key, bool ctrl, bool shift)
    {
        if (key == Key.Escape)
        {
            if (Tool != ToolKind.None)
            {
                Tool = ToolKind.None;
                Changed?.Invoke();
                return true;
            }
            ExitRequested?.Invoke();
            return true;
        }

        if (key == Key.C && ctrl && HasSelection)
        {
            // Ctrl+C = 完成并复制，QQ 的标配，跟放大镜的「按 C 复制色号」不冲突。
            FinishRequested?.Invoke(new FinishRequest(CopyMode.Clipboard));
            return true;
        }

        if (key == Key.C && !ctrl && !shift && Tool == ToolKind.None)
        {
            var c = Snapshot.PixelAt((int)Cursor.X, (int)Cursor.Y);
            if (c.A != 0) Exporter.TrySetText($"#{c.R:X2}{c.G:X2}{c.B:X2}");            return true;
        }

        if (key is Key.Enter or Key.Return && HasSelection)
        {
            FinishRequested?.Invoke(new FinishRequest(CopyMode.Clipboard));
            return true;
        }

        if (key == Key.Z && ctrl)
        {
            Layer.UndoLast();
            return true;
        }

        if (key == Key.S && ctrl)
        {
            FinishRequested?.Invoke(new FinishRequest(CopyMode.File));
            return true;
        }

        return false;
    }

    // ---------- 工具栏指令 ----------

    public void SetTool(ToolKind tool)
    {
        Tool = Tool == tool ? ToolKind.None : tool;
        if (Tool == ToolKind.Mosaic) RebuildMosaics();
        Changed?.Invoke();
    }

    public void SetColor(Color color)
    {
        Color = color;
        if (_live != null) _live.Color = color;
        ApplyLiveFill();
        Changed?.Invoke();
    }

    public void SetShapeKind(ShapeKind kind)
    {
        ShapeCurrent = kind;
        Changed?.Invoke();
    }

    public void SetShapeFilled(bool filled)
    {
        ShapeFilled = filled;
        ApplyLiveFill();
        Changed?.Invoke();
    }

    public void SetStrokeWidth(int dp)
    {
        WidthDp = Math.Clamp(dp, 1, 20);
        if (_live != null) _live.StrokeWidth = StrokeWidth;
        Changed?.Invoke();
    }

    public void SetFontIndex(int index)
    {
        FontIndex = Math.Clamp(index, 0, 2);
        Changed?.Invoke();
    }

    public void SetCornerRadius(int dp)
    {
        CornerRadiusDp = Math.Clamp(dp, 0, 32);
        Changed?.Invoke();
    }

    public void SetDropShadow(bool on)
    {
        DropShadow = on;
        Changed?.Invoke();
    }

    /// <summary>实心/空心切换和换色时，正在拖的那一笔也要跟着变。</summary>
    private void ApplyLiveFill()
    {
        var fill = ShapeFilled ? Color : Colors.Transparent;
        switch (_live)
        {
            case RectAnnotation r: r.FillColor = fill; break;
            case EllipseAnnotation e: e.FillColor = fill; break;
        }
    }

    public void SetMosaicStyle(MosaicStyle style)
    {
        MosaicKind = style;
        if (_live is MosaicAnnotation m) m.Style = style;
        Changed?.Invoke();
    }

    public void SetMosaicStrength(int strength)
    {
        MosaicStrength = Math.Clamp(strength, 2, 40);
        if (_live is MosaicAnnotation liveMosaic) liveMosaic.Strength = MosaicStrength;
        // 不能把 Mosaics 置空：预览层的绘制以它是否为 null 做开关，置空会让
        // 已经画好的所有标注一起消失，直到下一次拖动选区才回来。
        RebuildMosaics();
        Changed?.Invoke();
    }

    public void Undo() => Layer.UndoLast();

    public void Finish(CopyMode mode) => FinishRequested?.Invoke(new FinishRequest(mode));

    public void Exit() => ExitRequested?.Invoke();

    public void EnterLongMode()
    {
        if (HasSelection) LongModeRequested?.Invoke();
    }

    public void PlaceText(Point at) => TextPlacementRequested?.Invoke(at);

    public void AddText(string text, Point at)
    {
        if (!string.IsNullOrEmpty(text)) Layer.Add(new TextAnnotation(text, at, Color, FontSize));
        Changed?.Invoke();
    }

    /// <summary>双击已有文字进编辑态：替换文本内容，位置样式不动。</summary>
    public void UpdateText(TextAnnotation target, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        target.Text = text;
        Changed?.Invoke();
    }

    // ---------- 结果 ----------

    /// <summary>阴影的渲染参数：四周留出的边距要盖住最外一层影。</summary>
    private const double ShadowSpreadDp = 24, ShadowPadDp = 26;

    /// <summary>
    /// 成图。选区被拖到屏幕外、退化成一个点的时候返回 null——
    /// 以前这里返回整张桌面快照，等于「只想取消这一下，结果存了一张全屏图」。
    /// </summary>
    public BitmapSource? RenderResult()
    {
        var bounds = Snapshot.Intersect(FinalRect());
        if (bounds.Width < 1 || bounds.Height < 1) return null;

        double radiusPx = CornerRadiusDp * UiScale;
        double pad = DropShadow ? ShadowPadDp * UiScale : 0;
        var mosaics = Mosaics ?? new MosaicSource(Snapshot, bounds);
        var mask = PolygonPoints is { Count: >= 3 }
            ? RectExt.ClosedPath(PolygonPoints.Select(p => new Point(p.X - bounds.X + pad, p.Y - bounds.Y + pad)))
            : null;

        int w = (int)(bounds.Width + pad * 2), h = (int)(bounds.Height + pad * 2);
        // CroppedBitmap 要的是位图内坐标；钉图快照的虚拟边界可能在负坐标区（副屏在左/上），
        // 直接拿虚拟坐标裁会当场炸掉，先平移回位图原点。
        var local = new Int32Rect(bounds.X - Snapshot.VirtualBounds.X, bounds.Y - Snapshot.VirtualBounds.Y,
            bounds.Width, bounds.Height);
        return RenderSurface.ToBitmap(w, h, dc =>
        {
            var rect = new Rect(pad, pad, bounds.Width, bounds.Height);
            var round = radiusPx > 0 ? new RectangleGeometry(rect, radiusPx, radiusPx) : null;

            if (DropShadow) DrawSoftShadow(dc, rect, radiusPx, pad, UiScale);

            Geometry? clip = mask ?? round;
            if (clip != null) dc.PushClip(clip);
            dc.DrawImage(new CroppedBitmap(Snapshot.Image, local), rect);
            dc.PushTransform(new TranslateTransform(-bounds.X + pad, -bounds.Y + pad));
            Layer.DrawAll(dc, new RenderContext(mosaics, 1.0, mosaics.Region));
            dc.Pop();
            if (clip != null) dc.Pop();
        });
    }

    /// <summary>
    /// 分层软影：由深到浅向外画八层圆角矩形叠出来。DrawingContext.PushEffect 只收
    /// 已淘汰的 BitmapEffect，现代 DropShadowEffect 塞不进去，叠层是最稳的软影。
    /// </summary>
    internal static void DrawSoftShadow(DrawingContext dc, Rect rect, double radiusPx, double pad, double scale)
    {
        const int layers = 8;
        double spread = ShadowSpreadDp * scale;
        for (int i = layers; i >= 1; i--)
        {
            double t = (double)i / layers;
            double inflate = spread * t;
            byte alpha = (byte)Math.Round(28 * (1 - t) + 4);
            var r = new Rect(rect.X - inflate, rect.Y - inflate, rect.Width + inflate * 2, rect.Height + inflate * 2);
            double rPx = radiusPx > 0 ? radiusPx + inflate : 0;
            Geometry geo = rPx > 0 ? new RectangleGeometry(r, rPx, rPx) : new RectangleGeometry(r);
            dc.DrawGeometry(Theme.Brush(Color.FromArgb(alpha, 0, 0, 0)), null, geo);
        }
    }

    public void RebuildMosaics()
    {
        var bounds = Snapshot.Intersect(FinalRect());
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            Mosaics = null;
            return;
        }
        Mosaics = new MosaicSource(Snapshot, bounds);
    }

    // ---------- 画具 ----------

    private void BeginAnnotation(Point physical)
    {
        _shapeAnchor = physical;

        switch (Tool)
        {
            case ToolKind.Shape when ShapeCurrent == ShapeKind.Rect:
                _live = new RectAnnotation(new Int32Rect((int)physical.X, (int)physical.Y, 1, 1), Color, StrokeWidth)
                { FillColor = ShapeFilled ? Color : Colors.Transparent };
                _drag = DragMode.SizingShape;
                break;
            case ToolKind.Shape when ShapeCurrent == ShapeKind.Ellipse:
                _live = new EllipseAnnotation(new Int32Rect((int)physical.X, (int)physical.Y, 1, 1), Color, StrokeWidth)
                { FillColor = ShapeFilled ? Color : Colors.Transparent };
                _drag = DragMode.SizingShape;
                break;
            case ToolKind.Shape:
                _live = new LineAnnotation(physical, physical, Color, StrokeWidth);
                _drag = DragMode.SizingShape;
                break;
            case ToolKind.Arrow:
                _live = new ArrowAnnotation(physical, physical, Color, StrokeWidth);
                _drag = DragMode.SizingShape;
                break;
            case ToolKind.Brush:
                _live = new StrokeAnnotation(Color, StrokeWidth);
                ((StrokeAnnotation)_live).Points.Add(physical);
                _drag = DragMode.Stroke;
                break;
            case ToolKind.Mosaic:
                _live = new MosaicAnnotation(MosaicKind, MosaicStrength)
                {
                    AsRectangle = true,
                    Points = { physical }
                };
                _drag = DragMode.SizingShape;
                break;
            case ToolKind.Marker:
                _live = new BadgeAnnotation(Layer.NextBadgeNumber++, physical, Color, BadgeRadius);
                _drag = DragMode.None;
                break;
            case ToolKind.Text:
                TextPlacementRequested?.Invoke(physical);
                return;
        }

        if (_live != null) Layer.Add(_live);
        Changed?.Invoke();
    }

    private void GrowLive(Point physical)
    {
        if (_live is StrokeAnnotation stroke) stroke.Points.Add(physical);
        else if (_live is MosaicAnnotation mosaic && !mosaic.AsRectangle) mosaic.Points.Add(physical);
    }

    private void SizeLive(Point physical)
    {
        switch (_live)
        {
            case RectAnnotation r: r.Box = RectFrom(_shapeAnchor, physical); break;
            case EllipseAnnotation e: e.Box = RectFrom(_shapeAnchor, physical); break;
            case LineAnnotation l: l.To = physical; break;
            case ArrowAnnotation a: a.To = physical; break;
            case MosaicAnnotation m:
                if (m.AsRectangle) m.RectBox = RectFrom(_shapeAnchor, physical);
                else m.Points.Add(physical);
                break;
        }
    }

    /// <summary>命中检测：点落在了哪条已放置的文字上（从最上层往下找），没有就 null。</summary>
    public TextAnnotation? HitText(Point p)
    {
        for (int i = Layer.Items.Count - 1; i >= 0; i--)
        {
            if (Layer.Items[i] is not TextAnnotation t || string.IsNullOrEmpty(t.Text)) continue;
            var formatted = new FormattedText(
                t.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei, PingFang SC, Segoe UI"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                t.FontSize,
                Theme.Brush(Theme.LoupeText),
                1);
            // 和绘制时一样留 4px 右边距，命中框略放宽 3px 手感更好。
            var bounds = new Rect(t.Position.X - 3, t.Position.Y - 3,
                Math.Min(formatted.Width, Selection.X + Selection.Width - t.Position.X) + 9,
                formatted.Height + 6);
            if (bounds.Contains(p)) return t;
        }
        return null;
    }

    /// <summary>鼠标悬停在文字上时给个抓手光标。</summary>
    public bool OverText(Point p) => HasSelection && HitText(p) != null;

    /// <summary>
    /// 命中检测：八个手柄点之外，四条边框线整条也能拽（QQ 就是拖着边放大缩小的），
    /// 边上映射到那条边的中点手柄；角上的点先判，优先级更高。
    /// </summary>
    public int HitHandle(Point p)
    {
        double tol = Math.Max(6, 10 / UiScale);
        var s = Selection;
        var handles = HandlePoints(s);
        for (int i = 0; i < handles.Length; i++)
            if (Math.Abs(handles[i].X - p.X) <= tol && Math.Abs(handles[i].Y - p.Y) <= tol) return i;

        if (Math.Abs(p.X - s.X) <= tol && p.Y >= s.Y - tol && p.Y <= s.Y + s.Height + tol) return 7;
        if (Math.Abs(p.X - (s.X + s.Width)) <= tol && p.Y >= s.Y - tol && p.Y <= s.Y + s.Height + tol) return 3;
        if (Math.Abs(p.Y - s.Y) <= tol && p.X >= s.X - tol && p.X <= s.X + s.Width + tol) return 1;
        if (Math.Abs(p.Y - (s.Y + s.Height)) <= tol && p.X >= s.X - tol && p.X <= s.X + s.Width + tol) return 5;
        return -1;
    }

    public static Point[] HandlePoints(Int32Rect s) =>
    [
        new(s.X, s.Y), new(s.X + s.Width / 2d, s.Y), new(s.X + s.Width, s.Y),
        new(s.X + s.Width, s.Y + s.Height / 2d), new(s.X + s.Width, s.Y + s.Height),
        new(s.X + s.Width / 2d, s.Y + s.Height), new(s.X, s.Y + s.Height), new(s.X, s.Y + s.Height / 2d)
    ];

    private static Int32Rect Resize(Int32Rect original, int handle, Point p)
    {
        int left = original.X, top = original.Y, right = original.X + original.Width, bottom = original.Y + original.Height;
        switch (handle)
        {
            case 0: left = (int)p.X; top = (int)p.Y; break;
            case 1: top = (int)p.Y; break;
            case 2: right = (int)p.X; top = (int)p.Y; break;
            case 3: right = (int)p.X; break;
            case 4: right = (int)p.X; bottom = (int)p.Y; break;
            case 5: bottom = (int)p.Y; break;
            case 6: left = (int)p.X; bottom = (int)p.Y; break;
            case 7: left = (int)p.X; break;
        }
        return new Int32Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));
    }
}

public enum LeftOrRight { Left, Right }
public enum CopyMode { Clipboard, File, Pin }

public readonly record struct FinishRequest(CopyMode Mode);
