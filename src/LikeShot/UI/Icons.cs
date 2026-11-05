using System.Windows.Media;

namespace LikeShot.UI;

/// <summary>
/// 工具栏图标全部用 path 数据手算，不引入任何位图：
/// 17×17 视框、1.5 描边，缩放到任意 DPI 都不糊。
/// </summary>
public static class Icons
{
    private static Geometry Parse(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    // 描边类图标用 Pen 画，填充类用 Geometry Fill。
    public static readonly Dictionary<string, string> StrokePaths = new()
    {
        ["rect"] = "M3,5 H14 V13 H3 Z",
        ["ellipse"] = "M8.5,4 C12.4,4 15,6.1 15,8.5 C15,10.9 12.4,13 8.5,13 C4.6,13 2,10.9 2,8.5 C2,6.1 4.6,4 8.5,4 Z",
        ["arrow"] = "M3.5,13.5 L12,5 M12,5 H7.6 M12,5 V9.4",
        ["brush"] = "M3,14 L6.2,13.4 L13.2,6.4 A1.9,1.9 0 0 0 10.5,3.7 L3.5,10.7 Z",
        ["text"] = "M3,14 L8.5,3.5 L14,14 M5.2,10.2 H11.8",
        ["undo"] = "M4,8.5 H11 A3.2,3.2 0 0 1 11,15 H8 M6.8,5.4 L3.4,8.5 L6.8,11.6",
        ["save"] = "M8.5,2.5 V10.5 M4.8,7 L8.5,10.8 L12.2,7 M3,14.5 H14",
        ["long"] = "M2.5,4 H14.5 V13 H2.5 Z M8.5,5.8 V10.6 M6,8.4 L8.5,11 L11,8.4",
        ["translate"] = "M2.5,5 H8 M5.2,3 V5 M6.8,5 C6,8.6 3.8,10.6 2.6,11.4 M4.4,8 C5.6,9.8 7.4,10.7 9,11 M10,14.5 L12.8,7.6 L15.6,14.5 M10.9,12.4 H14.7",
        ["ocr"] = "M2.5,6 V2.5 H6 M11,2.5 H14.5 V6 M14.5,11 V14.5 H11 M6,14.5 H2.5 V11 M5.5,9.8 H11.5",
        ["nail"] = "M8.5,2.5 V7 M5.4,7 H11.6 L13,9.6 H4 Z M8.5,9.6 V14.5",
        ["exit"] = "M4,4 L13,13 M13,4 L4,13",
        ["edit"] = "M11.4,2.8 L14.2,5.6 L6.6,13.2 L3,14.2 L4,10.6 Z",
        ["transfer"] = "M2.5,8.5 H10.5 M8,5.5 L11,8.5 L8,11.5 M13.5,3 V14",
        ["vertical"] = "M8.5,2.5 V12 M5.4,9.2 L8.5,12.4 L11.6,9.2",
        ["horizontal"] = "M2.5,8.5 H12 M9.2,5.4 L12.4,8.5 L9.2,11.6",
        ["scroll"] = "M5.5,2.5 H11.5 V12.5 H5.5 Z M8.5,4.4 V6.6 M6.4,14.5 H10.6",
        ["marker"] = "M8.5,3 A5.5,5.5 0 1 0 8.51,3 M7.4,6.2 H8.9 V12 H7"
    };

    public static readonly Dictionary<string, string> FillPaths = new()
    {
        // 马赛克：四格里两格实心，读起来就是「打码」。
        ["mosaic"] = "M2.5,2.5 H7 V7 H2.5 Z M9.9,9.9 H14.4 V14.4 H9.9 Z M9.9,2.5 H14.4 V7 H9.9 Z M2.5,9.9 H7 V14.4 H2.5 Z"
    };

    /// <summary>马赛克图标里只有两格是实的，所以单独给它做半透明。</summary>
    public static readonly HashSet<string> MosaicAlternate = ["mosaic"];
}
