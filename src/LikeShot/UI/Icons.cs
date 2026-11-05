using System.Windows.Media;

namespace LikeShot.UI;

/// <summary>
/// 工具栏图标：描边类数据取自 Lucide (ISC, https://lucide.dev) 的 24×24 视框原始路径，
/// 渲染时统一缩到 17×17、描边 1.5、圆头圆角，任意 DPI 都不糊；
/// 少数 Lucide 没有的（序号笔、空心实心小样、马赛克四分格）按同一视框手算。
/// </summary>
public static class Icons
{
    /// <summary>24 视框 → 17 画布的统一缩放；托盘菜单等小图用 16。</summary>
    public const double BoxSize = 24;

    internal static Geometry Parse(string data) => Parse(data, Theme.IconSize);

    internal static Geometry Parse(string data, double box)
    {
        // Geometry.Parse 返回的是冻结几何体，先拷贝成可写的才能挂缩放变换。
        var g = PathGeometry.CreateFromGeometry(Geometry.Parse(data));
        g.Transform = new ScaleTransform(box / BoxSize, box / BoxSize);
        g.Freeze();
        return g;
    }

    // 描边类图标用 Pen 画，填充类用 Geometry Fill；一个键可以带多个子路径。
    public static readonly Dictionary<string, string> StrokePaths = new()
    {
        // Lucide: square
        ["shape"] = "M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z",
        // Lucide: circle
        ["ellipse"] = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z",
        // Lucide: slash（形状条里的直线变体）
        ["line"] = "M22,2 2,22",
        // Lucide: arrow-up-right
        ["arrow"] = "M7,7 H17 V17 M7,17 17,7",
        // Lucide: brush
        ["brush"] = "M11,10 14,13 M6.5,21 A3.5,3.5 0 1 0 3,17.5 A2.62,2.62 0 0 1 2.292,19.292 A1,1 0 0 0 3,21 Z M9.969,17.031 21.378,5.624 A1,1 0 0 0 18.376,2.622 L6.967,14.031",
        // Lucide: type
        // 文字工具：QQ 用的是字母 A（Lucide 的 type 是 T，不像）
        ["text"] = "M4.2,19.8 L12,4.9 L19.8,19.8 M7.3,14.4 H16.6",
        // 序号笔：①，QQ 同款
        ["marker"] = "M12,3 A9,9 0 1 0 12.01,3 Z M10.9,10.2 L12.5,9 V15.6 M10.8,15.6 H14.2",
        // Lucide: undo-2
        ["undo"] = "M9,14 4,9 9,4 M4,9 H14.5 A5.5,5.5 0 0 1 20,14.5 A5.5,5.5 0 0 1 14.5,20 H11",
        // Lucide: download
        ["save"] = "M12,15 V3 M21,15 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V15 M7,10 12,15 17,10",
        // 长截图：帧叠 + 底线（Lucide gallery-vertical-end 改）
        ["long"] = "M7,2 H17 M5,6 H19 M5,10 H19 A2,2 0 0 1 21,12 V20 A2,2 0 0 1 19,22 H5 A2,2 0 0 1 3,20 V12 A2,2 0 0 1 5,10 Z",
        // Lucide: pin
        ["nail"] = "M12,17 V22 M9,10.76 A2,2 0 0 1 7.89,12.55 L6.11,13.45 A2,2 0 0 0 5,15.24 V16 A1,1 0 0 0 6,17 H18 A1,1 0 0 0 19,16 V15.24 A2,2 0 0 0 17.89,13.45 L16.11,12.55 A2,2 0 0 1 15,10.76 V7 A1,1 0 0 1 16,6 A2,2 0 0 0 16,2 H8 A2,2 0 0 0 8,6 A1,1 0 0 1 9,7 Z",
        // Lucide: x
        ["exit"] = "M18,6 6,18 M6,6 18,18",
        // Lucide: pencil（长截图里的「编辑工具」）
        ["edit"] = "M21.174,6.812 A1,1 0 0 0 17.188,2.825 L3.842,16.174 A2,2 0 0 0 3.342,17.004 L2.021,21.356 A0.5,0.5 0 0 0 2.644,21.978 L6.997,20.658 A2,2 0 0 0 7.827,20.161 Z M15,5 L19,9",
        // 纵向 / 横向滚动方向
        ["vertical"] = "M12,3.5 V16.8 M7.6,12.6 12,17 16.4,12.6",
        ["horizontal"] = "M3.5,12 H16.8 M12.6,7.6 17,12 12.6,16.4",
        // 自动滚动：页面 + 下箭头 + 底线
        ["scroll"] = "M5,4 H19 V17.5 H5 Z M12,6.8 V12.3 M9.6,10 12,12.4 14.4,10 M7.5,20.8 H16.5",
        // Lucide: contrast（圆 + 四分格填充读起来就是打码）
        ["mosaic"] = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 Z",
        // Lucide: check（完成按钮的绿勾）
        ["finish"] = "M20,6 9,17 4,12",
        // Lucide: pipette（取色板里的吸管）
        ["pipette"] = "M12,9 3.586,17.414 A2,2 0 0 0 3,18.828 V20.172 A2,2 0 0 1 2.414,21.586 A2,2 0 0 1 3.828,21 H5.172 A2,2 0 0 0 6.586,20.414 L15,12 M18,9 18.4,9.4 A1,1 0 1 1 15.4,12.4 L11.6,8.6 A1,1 0 1 1 14.6,5.6 L15,6 18.4,2.6 A1,1 0 1 1 21.4,5.6 Z M2,22 2.414,21.586",
        // 托盘菜单的几个入口（Lucide: camera / settings / folder / power）
        ["camera"] = "M14.5,4 H9.5 L7,7 H4 A2,2 0 0 0 2,9 V18 A2,2 0 0 0 4,20 H20 A2,2 0 0 0 22,18 V9 A2,2 0 0 0 20,7 H17 L14.5,4 Z M12,10 A3,3 0 1 0 12.01,10 Z",
        ["gear"] = "M12.22,2 H11.78 A2,2 0 0 0 9.78,4 V4.18 A2,2 0 0 1 8.78,5.91 L8.35,6.16 A2,2 0 0 1 6.35,6.16 L6.2,6.08 A2,2 0 0 0 3.47,6.81 L3.25,7.19 A2,2 0 0 0 3.98,9.92 L4.13,10.02 A2,2 0 0 1 5.13,11.74 V12.24 A2,2 0 0 1 4.13,13.98 L3.98,14.07 A2,2 0 0 0 3.25,16.8 L3.47,17.18 A2,2 0 0 0 6.2,17.91 L6.35,17.83 A2,2 0 0 1 8.35,17.83 L8.78,18.08 A2,2 0 0 1 9.78,19.81 V20 A2,2 0 0 0 11.78,22 H12.22 A2,2 0 0 0 14.22,20 V19.82 A2,2 0 0 1 15.22,18.09 L15.65,17.84 A2,2 0 0 1 17.65,17.84 L17.8,17.92 A2,2 0 0 0 20.53,17.19 L20.75,16.8 A2,2 0 0 0 20.02,14.07 L19.87,13.98 A2,2 0 0 1 18.87,12.24 V11.74 A2,2 0 0 1 19.87,10 L20.02,9.91 A2,2 0 0 0 20.75,7.18 L20.53,6.8 A2,2 0 0 0 17.8,6.07 L17.65,6.15 A2,2 0 0 1 15.65,6.15 L15.22,5.9 A2,2 0 0 1 14.22,4.17 V4 A2,2 0 0 0 12.22,2 Z M12,9 A3,3 0 1 0 12.01,9 Z",
        ["folder"] = "M20,20 A2,2 0 0 0 22,18 V8 A2,2 0 0 0 20,6 H12.1 A2,2 0 0 1 10.41,5.1 L9.6,3.9 A2,2 0 0 0 7.93,3 H4 A2,2 0 0 0 2,5 V18 A2,2 0 0 0 4,20 Z",
        ["power"] = "M18.36,6.64 A9,9 0 1 1 5.64,6.64 M12,2 V12"
    };

    public static readonly Dictionary<string, string> FillPaths = new()
    {
        // 马赛克：圆的右上四分之一实心，一眼读出「遮住」。
        ["mosaic"] = "M12,12 L12,2 A10,10 0 0 1 22,12 Z",
        // 空心/实心两档的小样：一个点、一块实心方。
        ["dot"] = "M12,9.4 A2.6,2.6 0 1 0 12.01,9.4 Z",
        ["filled"] = "M6,6 H18 V18 H6 Z"
    };

    /// <summary>给取色器等外部小画布用的解析入口（含 24→17 缩放）。</summary>
    internal static Geometry Icon(string key) => Parse(StrokePaths[key]);
}
