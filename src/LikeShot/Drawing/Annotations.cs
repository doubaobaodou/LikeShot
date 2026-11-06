using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Geom;

namespace LikeShot.Drawing;

/// <summary>绘图工具。矩形/椭圆/直线在 QQ 里是同一个「形状」工具的三档变体。</summary>
public enum ToolKind { None, Shape, Arrow, Brush, Mosaic, Text, Marker }

/// <summary>形状工具的变体。</summary>
public enum ShapeKind { Rect, Ellipse, Line }

public enum MosaicStyle { Pixelate, Blur }

/// <summary>绘制上下文：所有坐标都是虚拟桌面的物理像素。</summary>
public sealed record RenderContext(
    MosaicSource Mosaics,
    double PixelsPerDip,
    Int32Rect ClipBounds,
    bool ForExport = false);

public abstract class Annotation
{
    public Color Color { get; set; } = Colors.Red;
    public int StrokeWidth { get; set; } = 4;

    /// <summary>文字进编辑态时先把旧字藏起来，不然编辑框底下还垫着一份。</summary>
    public bool Hidden { get; set; }

    protected Annotation() { }

    /// <summary>子类主构造函数收到的颜色/粗细必须走这里落到属性上，
    /// 否则就一直是默认的红细线——SetColor 的实时补丁补的也是这两个属性。</summary>
    protected Annotation(Color color, int width)
    {
        Color = color;
        StrokeWidth = width;
    }

    protected Annotation(Color color) => Color = color;

    public abstract void Draw(DrawingContext dc, RenderContext ctx);

    protected Pen MakePen(Brush brush, bool capRound = true)
    {
        var pen = new Pen(brush, StrokeWidth)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = capRound ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = capRound ? PenLineCap.Round : PenLineCap.Triangle
        };
        pen.Freeze();
        return pen;
    }

    // ---------- 选中与调整 ----------

    /// <summary>包围盒（虚拟桌面物理像素）。选中框、拖角手柄、命中容差都以它为准。</summary>
    public abstract Rect Bounds { get; }

    /// <summary>点中了吗。tol 是物理像素容差；空心形状只认边线，实心/文字/圆牌/打码认整个框。</summary>
    public abstract bool Hit(Point p, double tol);

    /// <summary>整体平移。</summary>
    public abstract void Translate(double dx, double dy);

    /// <summary>把包围盒拉成 target：形状改框，笔画和打码按点位缩放，文字改字号，圆牌改半径。</summary>
    public abstract void SetBounds(Rect target);

    protected static double DistanceToSegment(Point a, Point b, Point p)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double len2 = vx * vx + vy * vy;
        if (len2 < 1e-6) return (p - a).Length;
        double t = Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
        return (p - new Point(a.X + vx * t, a.Y + vy * t)).Length;
    }

    /// <summary>环形命中：落在外扩框里但不在内缩框里就是压在边线上；框薄到容差盖满时整条都算命中。</summary>
    protected static bool OnRing(Rect r, Point p, double tol)
    {
        if (!new Rect(r.X - tol, r.Y - tol, r.Width + tol * 2, r.Height + tol * 2).Contains(p)) return false;
        var inner = new Rect(r.X + tol, r.Y + tol, r.Width - tol * 2, r.Height - tol * 2);
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p);
    }

    /// <summary>把点从原包围盒按比例映到新包围盒；原盒某一轴退化成一条线时，那一轴取中点。</summary>
    protected static Point MapPoint(Point p, Rect from, Rect to)
    {
        double fx = from.Width < 1 ? 0.5 : (p.X - from.X) / from.Width;
        double fy = from.Height < 1 ? 0.5 : (p.Y - from.Y) / from.Height;
        return new Point(to.X + to.Width * fx, to.Y + to.Height * fy);
    }

    protected static Int32Rect IntRect(Rect r) => new((int)Math.Round(r.X), (int)Math.Round(r.Y),
        Math.Max(1, (int)Math.Round(r.Width)), Math.Max(1, (int)Math.Round(r.Height)));

    protected static Rect BoundsOf(IEnumerable<Point> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var p in points)
        {
            any = true;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return any ? new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY)) : Rect.Empty;
    }
}

public sealed class RectAnnotation(Int32Rect box, Color color, int width) : Annotation(color, width)
{
    public Int32Rect Box { get; set; } = box;
    public Color FillColor { get; set; } = Colors.Transparent;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var rect = ToRect(Box);
        if (FillColor.A != 0) dc.DrawRectangle(new SolidColorBrush(FillColor), null, rect);
        dc.DrawRectangle(null, MakePen(new SolidColorBrush(Color)), rect);
    }

    internal static Rect ToRect(Int32Rect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    public override Rect Bounds => ToRect(Box);

    public override bool Hit(Point p, double tol) =>
        OnRing(ToRect(Box), p, Math.Max(tol, StrokeWidth / 2.0)) || (FillColor.A != 0 && ToRect(Box).Contains(p));

    public override void Translate(double dx, double dy) =>
        Box = new Int32Rect(Box.X + (int)Math.Round(dx), Box.Y + (int)Math.Round(dy), Box.Width, Box.Height);

    public override void SetBounds(Rect target) => Box = IntRect(target);
}

public sealed class EllipseAnnotation(Int32Rect box, Color color, int width) : Annotation(color, width)
{
    public Int32Rect Box { get; set; } = box;
    public Color FillColor { get; set; } = Colors.Transparent;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var rect = RectAnnotation.ToRect(Box);
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        if (FillColor.A != 0) dc.DrawEllipse(new SolidColorBrush(FillColor), null, center,
            Math.Abs(rect.Width / 2), Math.Abs(rect.Height / 2));
        dc.DrawEllipse(null, MakePen(new SolidColorBrush(Color)), center,
            Math.Abs(rect.Width / 2), Math.Abs(rect.Height / 2));
    }

    public override Rect Bounds => RectAnnotation.ToRect(Box);

    public override bool Hit(Point p, double tol)
    {
        var r = RectAnnotation.ToRect(Box);
        double ring = Math.Max(tol, StrokeWidth / 2.0);
        if (!InsideEllipse(new Rect(r.X - ring, r.Y - ring, r.Width + ring * 2, r.Height + ring * 2), p)) return false;
        if (FillColor.A != 0) return true;
        var inner = new Rect(r.X + ring, r.Y + ring, r.Width - ring * 2, r.Height - ring * 2);
        return inner.Width <= 0 || inner.Height <= 0 || !InsideEllipse(inner, p);
    }

    public override void Translate(double dx, double dy) =>
        Box = new Int32Rect(Box.X + (int)Math.Round(dx), Box.Y + (int)Math.Round(dy), Box.Width, Box.Height);

    public override void SetBounds(Rect target) => Box = IntRect(target);

    private static bool InsideEllipse(Rect r, Point p)
    {
        if (r.Width < 1 || r.Height < 1) return r.Contains(p);
        double nx = (p.X - (r.X + r.Width / 2)) / (r.Width / 2);
        double ny = (p.Y - (r.Y + r.Height / 2)) / (r.Height / 2);
        return nx * nx + ny * ny <= 1;
    }
}

public sealed class ArrowAnnotation(Point from, Point to, Color color, int width) : Annotation(color, width)
{
    public Point From { get; set; } = from;
    public Point To { get; set; } = to;
    public Point? Tail { get; set; }

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var pen = MakePen(new SolidColorBrush(Color), capRound: false);
        var vector = To - From;
        double length = vector.Length;
        if (length < 1) return;

        Point headBase = To - vector / length * Math.Min(18 + StrokeWidth * 2, length);
        var perpendicular = new Vector(-vector.Y, vector.X);
        double headWidth = 9 + StrokeWidth * 1.4;
        perpendicular.Normalize();
        Point left = headBase + perpendicular * headWidth / 2;
        Point right = headBase - perpendicular * headWidth / 2;

        dc.DrawLine(pen, From, headBase);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(To, true, true);
            g.LineTo(left, false, false);
            g.LineTo(right, false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color), null, geo);
    }

    public override Rect Bounds => new(Math.Min(From.X, To.X), Math.Min(From.Y, To.Y),
        Math.Abs(To.X - From.X), Math.Abs(To.Y - From.Y));

    public override bool Hit(Point p, double tol) =>
        DistanceToSegment(From, To, p) <= Math.Max(tol, StrokeWidth / 2.0 + 2);

    public override void Translate(double dx, double dy)
    {
        From = new Point(From.X + dx, From.Y + dy);
        To = new Point(To.X + dx, To.Y + dy);
    }

    public override void SetBounds(Rect target)
    {
        var from = Bounds;
        From = MapPoint(From, from, target);
        To = MapPoint(To, from, target);
    }
}

public sealed class StrokeAnnotation(Color color, int width) : Annotation(color, width)
{
    public List<Point> Points { get; } = [];

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count == 1)
        {
            dc.DrawEllipse(new SolidColorBrush(Color), null, Points[0], StrokeWidth / 2.0, StrokeWidth / 2.0);
            return;
        }
        if (Points.Count < 2) return;
        dc.DrawPolyline(MakePen(new SolidColorBrush(Color)), Points);
    }

    public override Rect Bounds => BoundsOf(Points);

    public override bool Hit(Point p, double tol)
    {
        double reach = Math.Max(tol, StrokeWidth / 2.0 + 2);
        if (Points.Count == 1) return (Points[0] - p).Length <= reach;
        for (int i = 0; i + 1 < Points.Count; i++)
            if (DistanceToSegment(Points[i], Points[i + 1], p) <= reach) return true;
        return false;
    }

    public override void Translate(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public override void SetBounds(Rect target)
    {
        var from = Bounds;
        for (int i = 0; i < Points.Count; i++) Points[i] = MapPoint(Points[i], from, target);
    }
}

/// <summary>直线：形状工具的第三档，就是一根不带箭头的线。</summary>
public sealed class LineAnnotation(Point from, Point to, Color color, int width) : Annotation(color, width)
{
    public Point From { get; set; } = from;
    public Point To { get; set; } = to;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        dc.DrawLine(MakePen(new SolidColorBrush(Color), capRound: false), From, To);
    }

    public override Rect Bounds => new(Math.Min(From.X, To.X), Math.Min(From.Y, To.Y),
        Math.Abs(To.X - From.X), Math.Abs(To.Y - From.Y));

    public override bool Hit(Point p, double tol) =>
        DistanceToSegment(From, To, p) <= Math.Max(tol, StrokeWidth / 2.0 + 2);

    public override void Translate(double dx, double dy)
    {
        From = new Point(From.X + dx, From.Y + dy);
        To = new Point(To.X + dx, To.Y + dy);
    }

    public override void SetBounds(Rect target)
    {
        var from = Bounds;
        From = MapPoint(From, from, target);
        To = MapPoint(To, from, target);
    }
}

public sealed class MosaicAnnotation(MosaicStyle style, int strength) : Annotation
{
    public List<Point> Points { get; } = [];
    public MosaicStyle Style { get; set; } = style;
    public int Strength { get; set; } = strength;
    public bool AsRectangle { get; set; }
    public Int32Rect RectBox { get; set; }

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        Geometry? clip = BuildClip();
        if (clip == null) return;

        BitmapSource source = ctx.Mosaics.Get(Style, Strength);
        var bounds = ctx.ClipBounds;
        dc.PushClip(clip);
        dc.DrawImage(source, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        dc.Pop();
    }

    private Geometry? BuildClip()
    {
        if (AsRectangle)
        {
            var r = RectAnnotation.ToRect(RectBox);
            return r.Width < 1 || r.Height < 1 ? null : new RectangleGeometry(r);
        }

        if (Points.Count < 2) return null;
        var figure = new PathFigure { StartPoint = Points[0], IsClosed = true, IsFilled = true };
        foreach (var p in Points.Skip(1)) figure.Segments.Add(new LineSegment(p, false));
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    public override Rect Bounds => AsRectangle ? RectAnnotation.ToRect(RectBox) : BoundsOf(Points);

    /// <summary>矩形打码整块都算命中；自由圈出来的认「在里面」也认「压着那条圈」。</summary>
    public override bool Hit(Point p, double tol)
    {
        if (AsRectangle) return RectAnnotation.ToRect(RectBox).Contains(p);
        if (Points.Count < 2) return false;
        if (RectExt.ClosedPath(Points).FillContains(p)) return true;
        double reach = Math.Max(tol, 4);
        for (int i = 0; i < Points.Count; i++)
        {
            var a = Points[i];
            var b = Points[(i + 1) % Points.Count];
            if (DistanceToSegment(a, b, p) <= reach) return true;
        }
        return false;
    }

    public override void Translate(double dx, double dy)
    {
        if (AsRectangle)
        {
            RectBox = new Int32Rect(RectBox.X + (int)Math.Round(dx), RectBox.Y + (int)Math.Round(dy),
                RectBox.Width, RectBox.Height);
            return;
        }
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + dx, Points[i].Y + dy);
    }

    public override void SetBounds(Rect target)
    {
        if (AsRectangle)
        {
            RectBox = IntRect(target);
            return;
        }
        var from = Bounds;
        for (int i = 0; i < Points.Count; i++) Points[i] = MapPoint(Points[i], from, target);
    }
}

public sealed class TextAnnotation(string text, Point position, Color color, int fontSize) : Annotation(color)
{
    public string Text { get; set; } = text;
    public Point Position { get; set; } = position;

    /// <summary>物理像素字号。滑条上显示的是它对 96dpi 的等价值，落笔和拖角缩放都用这个。</summary>
    public int FontSize { get; set; } = fontSize;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (string.IsNullOrEmpty(Text)) return;
        var formatted = new FormattedText(
            Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei, PingFang SC, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            FontSize,
            new SolidColorBrush(Color),
            ctx.PixelsPerDip)
        {
            MaxTextWidth = Math.Max(40, ctx.ClipBounds.Right() - Position.X - 4),
            Trimming = TextTrimming.None
        };
        dc.DrawText(formatted, Position);
    }

    /// <summary>文字没有「框」，包围盒就是这一行的字形范围，四周各放宽 3px 才好点中。</summary>
    public override Rect Bounds
    {
        get
        {
            if (string.IsNullOrEmpty(Text)) return Rect.Empty;
            var f = Measure(1.0);
            return new Rect(Position.X - 3, Position.Y - 3, f.Width + 6, f.Height + 6);
        }
    }

    public override bool Hit(Point p, double tol) => !Bounds.IsEmpty && Bounds.Contains(p);

    public override void Translate(double dx, double dy) =>
        Position = new Point(Position.X + dx, Position.Y + dy);

    /// <summary>拖角缩放文字 = 改字号：按高度比值换算，字形宽度自己跟着长。</summary>
    public override void SetBounds(Rect target)
    {
        var from = Bounds;
        if (from.Height < 1 || target.Height < 1) return;
        FontSize = Math.Clamp((int)Math.Round(FontSize * target.Height / from.Height), 8, 400);
    }

    private FormattedText Measure(double pixelsPerDip) => new(
        Text,
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei, PingFang SC, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        FontSize,
        new SolidColorBrush(Color),
        pixelsPerDip);
}

/// <summary>序号笔：QQ 里点一下就落下一个自增圆牌。</summary>
public sealed class BadgeAnnotation(int number, Point center, Color color, int radiusPx) : Annotation(color)
{
    public int Number { get; set; } = number;
    public Point Center { get; set; } = center;
    public int Radius { get; set; } = radiusPx;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var brush = new SolidColorBrush(Color);
        dc.DrawEllipse(brush, null, Center, Radius, Radius);
        var text = new FormattedText(
            Number.ToString(),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Radius * 1.25,
            Brushes.White,
            ctx.PixelsPerDip)
        {
            TextAlignment = TextAlignment.Center
        };
        dc.DrawText(text, new Point(Center.X - Radius, Center.Y - text.Height / 2));
    }

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override bool Hit(Point p, double tol) => (p - Center).Length <= Radius + tol;

    public override void Translate(double dx, double dy) =>
        Center = new Point(Center.X + dx, Center.Y + dy);

    /// <summary>拖角改的是直径，取宽高中较小的一边，圆不会被拉成椭圆。</summary>
    public override void SetBounds(Rect target)
    {
        Radius = Math.Clamp((int)Math.Round(Math.Min(target.Width, target.Height) / 2), 4, 400);
        Center = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
    }
}

/// <summary>撤销栈。QQ 只有撤销、没有重做，这里保持一致。</summary>
public sealed class AnnotationLayer
{
    private readonly List<Annotation> _items = [];
    public IReadOnlyList<Annotation> Items => _items;
    public event Action? Changed;

    public int NextBadgeNumber { get; set; } = 1;

    public void Add(Annotation item)
    {
        _items.Add(item);
        Changed?.Invoke();
    }

    public void Remove(Annotation item)
    {
        if (_items.Remove(item)) Changed?.Invoke();
    }

    public void UndoLast()
    {
        if (_items.Count == 0) return;
        _items.RemoveAt(_items.Count - 1);
        if (_items.Count == 0) NextBadgeNumber = 1;
        Changed?.Invoke();
    }

    public bool IsEmpty => _items.Count == 0;
    public void Clear()
    {
        _items.Clear();
        NextBadgeNumber = 1;
        Changed?.Invoke();
    }

    public void DrawAll(DrawingContext dc, RenderContext ctx)
    {
        foreach (var item in _items)
            if (!item.Hidden) item.Draw(dc, ctx);
    }
}
