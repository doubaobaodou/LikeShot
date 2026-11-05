using System.Windows;
using System.Windows.Media;

namespace LikeShot.Geom;

/// <summary>Int32Rect 没有 Right/Bottom，也没有从点集造闭合几何的现成方法，这里补齐。</summary>
public static class RectExt
{
    public static int Right(this Int32Rect r) => r.X + r.Width;
    public static int Bottom(this Int32Rect r) => r.Y + r.Height;

    public static bool ContainsPoint(this Int32Rect r, Point p) =>
        p.X >= r.X && p.X < r.Right() && p.Y >= r.Y && p.Y < r.Bottom();

    public static Rect ToRect(this Int32Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>WPF 没有 PolygonGeometry，用闭合的 PathGeometry 顶替。</summary>
    public static Geometry ClosedPath(IEnumerable<Point> points)
    {
        var figure = new PathFigure();
        bool first = true;
        foreach (var p in points)
        {
            if (first) { figure.StartPoint = p; first = false; }
            // 第二个参数是 isStroked：填 false 整段不描边，笔画会凭空消失
            else figure.Segments.Add(new LineSegment(p, true));
        }
        figure.IsClosed = true;
        figure.IsFilled = true;

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    public static Geometry OpenPath(IEnumerable<Point> points)
    {
        var figure = new PathFigure();
        bool first = true;
        foreach (var p in points)
        {
            if (first) { figure.StartPoint = p; first = false; }
            else figure.Segments.Add(new LineSegment(p, true));
        }
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    /// <summary>DrawingContext 没有 DrawPolyline，画开放折线走几何。</summary>
    public static void DrawPolyline(this DrawingContext dc, Pen pen, IEnumerable<Point> points) =>
        dc.DrawGeometry(null, pen, OpenPath(points));
}
