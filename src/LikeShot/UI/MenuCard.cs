using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LikeShot.UI;

/// <summary>
/// 全站共用的右键菜单：托盘那套 Win11 观感——透明边距里挂投影、圆角 8 的卡片、
/// 悬停圆角灰块、左边 Lucide 图标、右边灰字键位。
/// 截图层、钉图窗、历史页以前各弹一块系统默认菜单，和托盘不是一个时代的样式，
/// 这里统一收进来；托盘也走这一份，免得两处各画一套迟早对不上。
/// </summary>
internal static class MenuCard
{
    private static readonly Color GlyphGray = Color.FromRgb(0x4A, 0x4A, 0x4A);

    public static ContextMenu New() => new()
    {
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
        FontSize = 13,
        Style = MenuStyle()
    };

    /// <summary>一个菜单项。onClick 传 null 时由调用处自己接 Click（托盘那种要回读状态的项）。</summary>
    public static MenuItem Item(string iconKey, string text, Action? onClick = null,
        string gesture = "", bool red = false, bool accentIcon = false, bool checkable = false)
    {
        var color = accentIcon ? Theme.Accent : red ? Theme.DangerRed : GlyphGray;
        var item = new MenuItem
        {
            Header = text,
            InputGestureText = gesture,
            Icon = Glyph(iconKey, color),
            Margin = new Thickness(1, 0, 1, 0),
            Style = ItemStyle()
        };
        if (checkable) item.IsCheckable = true;
        if (onClick != null) item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>托盘里那行只读状态：灰掉、不带图标，字号和其它项一致。</summary>
    public static MenuItem StatusItem(string text) => new()
    {
        Header = text,
        IsEnabled = false,
        Margin = new Thickness(1, 0, 1, 0),
        Style = ItemStyle()
    };

    /// <summary>
    /// 文本框的右键菜单：系统自带那份是经典灰菜单，和这套卡片不是一个时代。
    /// 每一项按「这个框此刻能不能做这件事」点亮，不做点了没反应的摆设。
    /// </summary>
    public static ContextMenu TextMenu(TextBox box)
    {
        var menu = New();
        var cut = Item("cut", "剪切", box.Cut, "Ctrl+X");
        var copy = Item("copy", "复制", box.Copy, "Ctrl+C");
        var paste = Item("paste", "粘贴", box.Paste, "Ctrl+V");
        var selectAll = Item("select", "全选", box.SelectAll, "Ctrl+A");
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(Sep());
        menu.Items.Add(selectAll);
        menu.Opened += (_, _) =>
        {
            bool picked = box.SelectionLength > 0;
            cut.IsEnabled = picked && !box.IsReadOnly;
            copy.IsEnabled = picked;
            paste.IsEnabled = !box.IsReadOnly && HasClipboardText();
            selectAll.IsEnabled = box.Text.Length > 0;
        };
        return menu;
    }

    // 别的进程正占着剪贴板时 ContainsText 会抛，这时候当作没内容就行。
    private static bool HasClipboardText()
    {
        try { return Clipboard.ContainsText(); }
        catch { return false; }
    }

    public static Separator Sep() => new()
    {
        Margin = new Thickness(6, 4, 6, 4),
        Style = SepStyle()
    };

    // 模板里有 Grid 列定义和 TargetName 触发器，工厂树拼不动，按 SliderStyle 的办法
    // 用 XAML 生成、色值插进去。

    private static Style MenuStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ContextMenu">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ContextMenu">
                            <Grid Margin="14">
                                <!-- 四周留 14px 给投影，整个弹出窗是透明的，阴影落得下去 -->
                                <Border CornerRadius="9" Background="White">
                                    <Border.Effect>
                                        <DropShadowEffect BlurRadius="16" ShadowDepth="3" Direction="270" Opacity="0.26"/>
                                    </Border.Effect>
                                </Border>
                                <Border Background="{BG}" BorderBrush="{BORDER}" BorderThickness="1"
                                        CornerRadius="8" Padding="5">
                                    <StackPanel IsItemsHost="True"/>
                                </Border>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml
            .Replace("{BG}", "#FFF9F9F9")
            .Replace("{BORDER}", "#FFE3E3E3"));
    }

    private static Style SepStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="Separator">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Separator">
                            <Border Height="1" Background="#FFE7E7E7" CornerRadius="0.5"/>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Setter Property="IsHitTestVisible" Value="False"/>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>菜单项模板：左图标、中标题、右侧选中勾（或快捷键），悬停圆角灰块。</summary>
    private static Style ItemStyle()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="MenuItem">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="MenuItem">
                            <Border x:Name="hover" Height="34" CornerRadius="5" Background="Transparent">
                                <Grid Margin="10,0,10,0">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="27"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <ContentPresenter Grid.Column="0" ContentSource="Icon" VerticalAlignment="Center" Width="17"/>
                                    <ContentPresenter x:Name="header" Grid.Column="1" ContentSource="Header"
                                                      VerticalAlignment="Center"
                                                      TextBlock.Foreground="#FF1B1B1B" TextBlock.FontSize="13"/>
                                    <TextBlock x:Name="gesture" Grid.Column="2" Text="{TemplateBinding InputGestureText}"
                                               VerticalAlignment="Center" Foreground="#FF8C8C8C" FontSize="12" Margin="18,0,2,0"/>
                                    <TextBlock x:Name="check" Grid.Column="2" Text="&#xE73E;"
                                               FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="13"
                                               VerticalAlignment="Center" Margin="18,0,2,0" Foreground="{CHECK}"
                                               Visibility="Collapsed"/>
                                </Grid>
                            </Border>
                            <ControlTemplate.Triggers>
                                <MultiTrigger>
                                    <MultiTrigger.Conditions>
                                        <Condition Property="IsMouseOver" Value="True"/>
                                        <Condition Property="IsEnabled" Value="True"/>
                                    </MultiTrigger.Conditions>
                                    <Setter TargetName="hover" Property="Background" Value="#FFEBEBEB"/>
                                </MultiTrigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter TargetName="header" Property="TextBlock.Foreground" Value="#FF9E9E9E"/>
                                </Trigger>
                                <Trigger Property="IsChecked" Value="True">
                                    <Setter TargetName="check" Property="Visibility" Value="Visible"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml.Replace("{CHECK}", "#FF318AFF"));
    }

    /// <summary>17px 描边小图标，和工具栏同一套 Lucide 路径。</summary>
    private static FrameworkElement Glyph(string iconKey, Color color)
    {
        var brush = Theme.Brush(color);
        var canvas = new Canvas { Width = 17, Height = 17 };
        if (Icons.StrokePaths.TryGetValue(iconKey, out var data))
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(data),
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        return canvas;
    }
}
