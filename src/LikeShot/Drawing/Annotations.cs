using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LikeShot.Drawing;

public enum ToolKind { None, Rect, Ellipse, Arrow, Brush, Mosaic, Text, Marker }

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
}

public sealed class RectAnnotation(Int32Rect box, Color color, int width) : Annotation
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
}

public sealed class EllipseAnnotation(Int32Rect box, Color color, int width) : Annotation
{
    public Int32Rect Box { get; set; } = box;

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        var rect = RectAnnotation.ToRect(Box);
        dc.DrawEllipse(null, MakePen(new SolidColorBrush(Color)),
            new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
            Math.Abs(rect.Width / 2), Math.Abs(rect.Height / 2));
    }
}

public sealed class ArrowAnnotation(Point from, Point to, Color color, int width) : Annotation
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

        Point headBase = To - vector / length * Math.Min(18 + width * 2, length);
        var perpendicular = new Vector(-vector.Y, vector.X);
        double headWidth = 9 + width * 1.4;
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
}

public sealed class StrokeAnnotation(Color color, int width) : Annotation
{
    public List<Point> Points { get; } = [];

    public override void Draw(DrawingContext dc, RenderContext ctx)
    {
        if (Points.Count == 1)
        {
            dc.DrawEllipse(new SolidColorBrush(Color), null, Points[0], width / 2.0, width / 2.0);
            return;
        }
        if (Points.Count < 2) return;
        dc.DrawPolyline(MakePen(new SolidColorBrush(Color)), Points);
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
}

public sealed class TextAnnotation(string text, Point position, Color color, int fontSize) : Annotation
{
    public string Text { get; set; } = text;
    public Point Position { get; set; } = position;
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
}

/// <summary>序号笔：QQ 里点一下就落下一个自增圆牌。</summary>
public sealed class BadgeAnnotation(int number, Point center, Color color, int radiusPx) : Annotation
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
        foreach (var item in _items) item.Draw(dc, ctx);
    }
}
