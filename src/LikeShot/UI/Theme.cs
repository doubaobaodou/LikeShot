using System.Windows;
using System.Windows.Media;

namespace LikeShot.UI;

/// <summary>
/// 尺寸、字号、色值都按 QQ NT 9.9.36 的 UI 定义抄：
/// 按钮 30×30、右外边距 6、放大镜整块 132×152、放大镜画面 117×84、完成按钮 56×18。
/// 截图层的 UI 颜色分深浅两套（浅色就是 QQ 那种白工具栏），由 LightUi 开关切换；
/// 工具栏这些元素每次截图都重新构建，所以切了开关下一张截图就换装。
/// </summary>
public static class Theme
{
    /// <summary>截图 UI 用浅色还是深色。设置里改完「应用」就写进来，不用重启。</summary>
    public static bool LightUi { get; set; }

    public static Color BarBackground => LightUi
        ? Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0xF2, 0x2B, 0x2F, 0x36);
    public static Color BarBorder => LightUi
        ? Color.FromRgb(0xE3, 0xE3, 0xE3)
        : Color.FromRgb(0x3D, 0x42, 0x4B);
    public static Color IconBrush => LightUi
        ? Color.FromRgb(0x38, 0x38, 0x38)
        : Color.FromRgb(0xE8, 0xEA, 0xED);
    public static Color ButtonHover => LightUi
        ? Color.FromRgb(0xEC, 0xEC, 0xEC)
        : Color.FromRgb(0x43, 0x49, 0x54);
    public static Color ButtonPushed => LightUi
        ? Color.FromRgb(0xDE, 0xDE, 0xDE)
        : Color.FromRgb(0x1F, 0x23, 0x29);
    public static readonly Color Accent = Color.FromRgb(0x31, 0x8A, 0xFF);
    public static readonly Color SelectionBorder = Color.FromRgb(0x31, 0x8A, 0xFF);
    public static readonly Color HandleFill = Colors.White;
    public static readonly Color DimOutside = Color.FromArgb(0x8C, 0x00, 0x00, 0x00);
    public static Color LoupeBackground => LightUi
        ? Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0xF2, 0x23, 0x26, 0x2B);
    public static Color LoupeText => LightUi
        ? Color.FromRgb(0x1F, 0x23, 0x29)
        : Color.FromRgb(0xDF, 0xE3, 0xE8);
    public static Color LoupeMuted => LightUi
        ? Color.FromRgb(0x76, 0x76, 0x76)
        : Color.FromRgb(0x9A, 0xA3, 0xAF);
    /// <summary>工具条尾部那对确认/取消：QQ 的 ✓ 是绿的，✕ 是红的。</summary>
    public static readonly Color SuccessGreen = Color.FromRgb(0x0B, 0xBF, 0x5E);
    public static readonly Color DangerRed = Color.FromRgb(0xFB, 0x38, 0x38);

    /// <summary>色板上未选中色块的描边：深色带里描白，白色带里描灰，否则白色块就隐形了。</summary>
    public static Color SwatchBorder => LightUi
        ? Color.FromRgb(0xC9, 0xC9, 0xC9)
        : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);

    public const double ButtonSize = 30;
    public const double ButtonGap = 6;
    public const double IconSize = 19;
    public const double BarPadding = 6;
    public const double LoupeWidth = 132;
    public const double LoupeImageHeight = 84;

    /// <summary>QQ 调色板 16 色（含「当前色」），十六进制直接来自它的 UI 定义。</summary>
    public static readonly (string Name, Color Color)[] Palette =
    [
        ("黑", Color.FromRgb(0x00, 0x00, 0x00)),
        ("白", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("深灰", Color.FromRgb(0x80, 0x80, 0x80)),
        ("灰", Color.FromRgb(0xC0, 0xC0, 0xC0)),
        ("棕", Color.FromRgb(0x80, 0x00, 0x00)),
        ("红", Color.FromRgb(0xFB, 0x38, 0x38)),
        ("深黄", Color.FromRgb(0xF7, 0x88, 0x3A)),
        ("黄", Color.FromRgb(0xFF, 0xFF, 0x00)),
        ("深绿", Color.FromRgb(0x30, 0x84, 0x30)),
        ("绿", Color.FromRgb(0x99, 0xCC, 0x00)),
        ("深蓝", Color.FromRgb(0x38, 0x5A, 0xD3)),
        ("蓝", Color.FromRgb(0x38, 0x94, 0xE4)),
        ("深粉", Color.FromRgb(0x80, 0x00, 0x80)),
        ("粉", Color.FromRgb(0xF3, 0x1B, 0xF3)),
        ("深青", Color.FromRgb(0x00, 0x99, 0x99)),
        ("青", Color.FromRgb(0x16, 0xDC, 0xDC))
    ];

    public static readonly Color DefaultAnnotationColor = Color.FromRgb(0xFB, 0x38, 0x38);

    public static int[] FontSizesAt(double scale) => [(int)Math.Round(16 * scale), (int)Math.Round(22 * scale), (int)Math.Round(30 * scale)];

    public static int BadgeRadiusAt(double scale) => (int)Math.Round(10 * scale);

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
