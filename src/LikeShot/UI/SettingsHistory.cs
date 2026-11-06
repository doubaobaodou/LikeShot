using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LikeShot.Capture;
using LikeShot.Shell;

namespace LikeShot.UI;

/// <summary>
/// 设置窗口的「历史截图」那一页。
/// 库归 HistoryStore 管，这页只做三件事：摆出来、给每条一个动作、把结果说一句。
/// </summary>
public sealed partial class SettingsWindow
{
    private const double HistoryCardWidth = 160;
    private const double HistoryThumbHeight = 96;

    private readonly TextBox _historySearch = new();
    private readonly PillGroup<string> _historySource = new(
        ["全部", "截图", "长截图", "钉图", "再标注"],
        ["全部", "截图", "长截图", "钉图", "再标注"],
        ["库里所有成品", "框选出来的截图", "滚动拼接的长图", "从钉图窗完成的", "从历史里再标注的"]);
    private readonly CheckBox _historyFavoritesOnly = Check();
    private readonly PillGroup<int> _historyLimit = new(
        [50, 200, 500], ["50 张", "200 张", "500 张"],
        ["最多留 50 张，收藏的不算", "最多留 200 张，收藏的不算", "最多留 500 张，收藏的不算"]);

    private readonly WrapPanel _historyGrid = new();
    private readonly TextBlock _historyCount = new();
    private readonly TextBlock _historyWarning = new();
    private readonly Border _historyBatch = new();
    private readonly TextBlock _historyBatchText = new();
    private readonly Dictionary<string, Border> _historyCards = new();
    private readonly Dictionary<string, Image> _historyThumbs = new();
    private readonly HashSet<string> _historySelected = new();
    private List<HistoryEntry> _historyShown = [];
    private int _historyLoadSeq;

    /// <summary>托盘菜单「历史截图…」要从这一页进来，序号只在这儿定死一次。</summary>
    internal const int HistorySectionIndex = 4;

    private UIElement BuildHistoryPage()
    {
        // 命名处理器：窗口关掉时要退订，否则关掉的窗连同整页图库一起留在托管堆里。
        HistoryStore.Shared.Changed += OnHistoryStoreChanged;
        Closed += (_, _) => HistoryStore.Shared.Changed -= OnHistoryStoreChanged;
        PreviewKeyDown += OnHistoryKeyDown;
        _historySource.Select("全部");

        var title = new TextBlock
        {
            Text = "历史截图",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(SettingsTheme.TextPrimary)
        };
        var openDir = StandardButton("打开历史目录");
        openDir.Click += (_, _) =>
        {
            var error = FolderActions.OpenFolder(HistoryStore.Shared.Root);
            _status.Text = error == null ? "已在资源管理器里打开历史目录" : $"目录打不开：{error}";
        };
        var clear = StandardButton("清空历史");
        clear.Click += (_, _) => ClearHistory();

        var titleButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        titleButtons.Children.Add(openDir);
        titleButtons.Children.Add(new Border { Width = 8 });
        titleButtons.Children.Add(clear);

        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(titleButtons, 1);
        titleRow.Children.Add(title);
        titleRow.Children.Add(titleButtons);

        _historySearch.Style = FieldStyle();
        _historySearch.Width = 200;
        _historySearch.ContextMenu = MenuCard.TextMenu(_historySearch);
        _historySearch.ToolTip = "按日期、尺寸或来源里的字找，例如 11-06 或 3200";
        _historySearch.TextChanged += (_, _) => RefreshHistory();
        _historySource.Changed += RefreshHistory;
        _historyFavoritesOnly.Checked += (_, _) => RefreshHistory();
        _historyFavoritesOnly.Unchecked += (_, _) => RefreshHistory();

        var batchDelete = StandardButton("删除所选");
        batchDelete.Click += (_, _) => DeleteSelected();
        var batchFavorite = StandardButton("收藏所选");
        batchFavorite.Click += (_, _) => FavoriteSelected();
        var batchClear = StandardButton("取消选择");
        batchClear.Click += (_, _) => { _historySelected.Clear(); UpdateHistoryChrome(HistoryStore.Shared.Entries); };

        _historyBatchText.VerticalAlignment = VerticalAlignment.Center;
        _historyBatchText.FontSize = 12.5;
        var batchButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        batchButtons.Children.Add(batchFavorite);
        batchButtons.Children.Add(new Border { Width = 6 });
        batchButtons.Children.Add(batchDelete);
        batchButtons.Children.Add(new Border { Width = 6 });
        batchButtons.Children.Add(batchClear);
        var batchGrid = new Grid { Margin = new Thickness(12, 6, 12, 6) };
        batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        batchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_historyBatchText, 0);
        Grid.SetColumn(batchButtons, 1);
        batchGrid.Children.Add(_historyBatchText);
        batchGrid.Children.Add(batchButtons);
        _historyBatch.Child = batchGrid;
        _historyBatch.Background = Theme.Brush(SettingsTheme.NavSelected);
        _historyBatch.CornerRadius = new CornerRadius(5);
        _historyBatch.BorderBrush = Theme.Brush(SettingsTheme.ControlBorder);
        _historyBatch.BorderThickness = new Thickness(1);
        _historyBatch.Visibility = Visibility.Collapsed;

        _historyWarning.FontSize = 12;
        _historyWarning.TextWrapping = TextWrapping.Wrap;
        _historyWarning.Foreground = Theme.Brush(SettingsTheme.Bad);
        _historyWarning.Visibility = Visibility.Collapsed;

        var header = new StackPanel();
        header.Children.Add(titleRow);
        header.Children.Add(FilterCard(
            MiniRow("搜索", _historySearch),
            MiniRow("来源", _historySource.Host, trailing: WrapLabel(_historyFavoritesOnly, "只看收藏")),
            MiniRow("保留", _historyLimit.Host, trailing: _historyCount)));
        header.Children.Add(new Border { Height = 8 });
        header.Children.Add(_historyBatch);
        header.Children.Add(_historyWarning);
        header.Children.Add(new Border { Height = 8 });

        var grid = new ScrollViewer
        {
            Content = _historyGrid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 4, 12)
        };

        var dock = new DockPanel { Margin = new Thickness(28, 16, 24, 0) };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(grid);
        return dock;
    }

    /// <summary>空态要说清「这儿本该有什么」，筛空了还要说清下一步动哪个筛选条件。</summary>
    private static FrameworkElement HistoryEmptyNote(int total) => new TextBlock
    {
        Text = total == 0
            ? "还没有历史截图。每张完成的截图都会自动存一份在这儿——复制或保存万一失败，从这儿找回。"
            : $"库里一共 {total} 张，但没有符合当前条件的。清空搜索、来源换回「全部」、取消「只看收藏」就都在了。",
        FontSize = 12.5,
        Foreground = Theme.Brush(SettingsTheme.TextSecondary),
        Margin = new Thickness(2, 6, 2, 6),
        TextWrapping = TextWrapping.Wrap
    };

    private static Border FilterCard(params UIElement[] rows)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        foreach (var row in rows) stack.Children.Add(row);
        return new Border
        {
            Background = Theme.Brush(SettingsTheme.Card),
            BorderBrush = Theme.Brush(SettingsTheme.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = stack
        };
    }

    /// <summary>筛选条里的一行：窄标签 + 控件 + 可选的右侧部件。</summary>
    private static FrameworkElement MiniRow(string label, FrameworkElement control, FrameworkElement? trailing = null)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Theme.Brush(SettingsTheme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center
        });
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        if (trailing != null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }
        return row;
    }

    private static FrameworkElement WrapLabel(FrameworkElement control, string label)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(control);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Theme.Brush(SettingsTheme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        });
        return stack;
    }

    // ---------- 列表 ----------

    private void OnHistoryStoreChanged() => Dispatcher.BeginInvoke(RefreshHistory);

    private void RefreshHistory()
    {
        var store = HistoryStore.Shared;
        bool favoritesOnly = _historyFavoritesOnly.IsChecked == true;
        var all = store.Entries;
        var shown = all
            .Where(e => HistoryStore.Matches(e, _historySearch.Text, _historySource.Selected, favoritesOnly))
            .OrderByDescending(e => e.Favorite)
            .ThenByDescending(e => e.Time)
            .ToList();

        _historySelected.RemoveWhere(id => !shown.Any(e => e.Id == id));
        _historyGrid.Children.Clear();
        _historyCards.Clear();
        _historyThumbs.Clear();
        foreach (var entry in shown) _historyGrid.Children.Add(HistoryCard(entry));
        if (shown.Count == 0) _historyGrid.Children.Add(HistoryEmptyNote(all.Count));
        _historyShown = shown;

        int seq = ++_historyLoadSeq;
        _ = LoadThumbsAsync(seq, shown);
        UpdateHistoryChrome(all);
    }

    private void UpdateHistoryChrome(IReadOnlyList<HistoryEntry> all)
    {
        var store = HistoryStore.Shared;
        _historyCount.Text = $"共 {all.Count} 张 · 收藏 {all.Count(e => e.Favorite)} 张 · {FormatBytes(all.Sum(e => e.Bytes))}";

        _historyBatchText.Text = _historySelected.Count == 0 ? "" : $"已选 {_historySelected.Count} 张";
        _historyBatch.Visibility = _historySelected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 入库失败只在真失败时占一行，平时不地方占。
        if (store.LastError is string error)
        {
            _historyWarning.Text = $"最近有一张图没能存进历史：{error}（截图本身没丢，可以另存一份）";
            _historyWarning.Visibility = Visibility.Visible;
        }
        else _historyWarning.Visibility = Visibility.Collapsed;

        foreach (var (id, card) in _historyCards)
        {
            bool picked = _historySelected.Contains(id);
            card.BorderBrush = Theme.Brush(picked ? SettingsTheme.Accent : SettingsTheme.CardBorder);
            card.BorderThickness = new Thickness(picked ? 2 : 1);
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.##} MB",
        _ => $"{bytes / 1024.0:0.#} KB"
    };

    private FrameworkElement HistoryCard(HistoryEntry e)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _historyThumbs[e.Id] = image;

        var thumb = new Border
        {
            Width = HistoryCardWidth - 16,
            Height = HistoryThumbHeight,
            Background = Theme.Brush(Color.FromRgb(0xF5, 0xF5, 0xF5)),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = image,
            Cursor = Cursors.Hand,
            ToolTip = "双击＝丢回截图层接着标注；点一下＝选中，Ctrl+点＝多选"
        };
        thumb.MouseLeftButtonUp += (_, ev) =>
        {
            if (ev.ClickCount >= 2) return;
            PickCard(e, toggle: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        };
        thumb.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2) HistoryReplay(e); };

        // 星标挂在缩略图右上角：收藏是这条记录自己的属性，不该去占底部动作那一排。
        var star = new Button
        {
            Content = Glyph(e.Favorite ? "star-fill" : "star",
                e.Favorite ? Color.FromRgb(0xD9, 0x9A, 0x00) : Color.FromRgb(0x8C, 0x8C, 0x8C), 13),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Background = Theme.Brush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = e.Favorite ? "取消收藏" : "收藏（不参与自动清理）",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0)
        };
        star.Click += (_, _) => ToggleFavorite(e);

        var cover = new Grid();
        cover.Children.Add(thumb);
        cover.Children.Add(star);

        string path = HistoryStore.Shared.FullPathOf(e);
        var detail = new TextBlock
        {
            Text = $"{e.Source} · {FormatBytes(e.Bytes)}",
            FontSize = 11.5,
            Foreground = Theme.Brush(SettingsTheme.TextSecondary),
            ToolTip = path
        };
        var info = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        info.Children.Add(new TextBlock
        {
            Text = $"{e.Time:MM-dd HH:mm:ss}  {e.Width}×{e.Height}",
            FontSize = 12,
            Foreground = Theme.Brush(SettingsTheme.TextPrimary)
        });
        info.Children.Add(detail);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        actions.Children.Add(IconAction("copy", "复制到剪贴板", () => HistoryCopy(e)));
        actions.Children.Add(IconAction("nail", "钉到桌面", () => HistoryPin(e)));
        actions.Children.Add(IconAction("edit", "再标注", () => HistoryReplay(e)));
        actions.Children.Add(IconAction("save", "另存为", () => HistorySaveAs(e)));
        actions.Children.Add(IconAction("trash", "删除", () => HistoryStore.Shared.Remove([e.Id]), danger: true));

        var body = new StackPanel();
        body.Children.Add(cover);
        body.Children.Add(info);
        body.Children.Add(actions);

        var card = new Border
        {
            Width = HistoryCardWidth,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(7, 7, 7, 5),
            Background = Theme.Brush(SettingsTheme.Card),
            BorderBrush = Theme.Brush(SettingsTheme.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = body
        };
        card.ContextMenu = HistoryMenu(e, path);
        _historyCards[e.Id] = card;
        return card;
    }

    /// <summary>卡片上的小图标：和工具栏同一套 Lucide 路径，按边长现缩放，不另画一份。</summary>
    private static FrameworkElement Glyph(string key, Color color, double size = 15)
    {
        var canvas = new Canvas { Width = size, Height = size };
        var brush = Theme.Brush(color);
        if (Icons.StrokePaths.TryGetValue(key, out var stroke))
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Icons.Parse(stroke, size),
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        if (Icons.FillPaths.TryGetValue(key, out var fill))
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Icons.Parse(fill, size), Fill = brush });
        return canvas;
    }

    private ContextMenu HistoryMenu(HistoryEntry e, string path)
    {
        // 卡片上那排按钮用的同一套图标，右键菜单也走 MenuCard，样式和托盘对齐。
        var menu = MenuCard.New();
        menu.Items.Add(MenuCard.Item("folder", "打开所在文件夹", () =>
        {
            var error = FolderActions.Reveal(path);
            if (error != null) _status.Text = $"打不开：{error}";
        }));
        menu.Items.Add(MenuCard.Item("copy", "复制文件路径", () =>
        {
            var error = Exporter.TrySetText(path);
            _status.Text = error == null ? "路径已复制到剪贴板" : $"路径没复制上：{error}";
        }));
        menu.Items.Add(MenuCard.Sep());
        menu.Items.Add(MenuCard.Item(e.Favorite ? "star-fill" : "star",
            e.Favorite ? "取消收藏" : "收藏这张", () => ToggleFavorite(e)));
        return menu;
    }

    /// <summary>收藏翻转要有回执：星标本身变了，但底部那句得说清它从此不参与自动清理。</summary>
    private void ToggleFavorite(HistoryEntry e)
    {
        bool on = !e.Favorite;
        if (!HistoryStore.Shared.SetFavorite(e.Id, on))
        {
            _status.Text = "这张已经不在库里了";
            RefreshHistory();
            return;
        }
        _status.Text = on ? "已收藏这张，它不参与自动清理" : "已取消收藏，它会跟着一起淘汰";
    }

    private Button IconAction(string iconKey, string tooltip, Action action, bool danger = false)
    {
        var btn = new Button
        {
            Content = Glyph(iconKey, danger ? SettingsTheme.Bad : Color.FromRgb(0x4A, 0x4A, 0x4A)),
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Focusable = false
        };
        btn.Click += (_, _) => action();
        return btn;
    }

    private void PickCard(HistoryEntry e, bool toggle)
    {
        if (toggle)
        {
            if (!_historySelected.Add(e.Id)) _historySelected.Remove(e.Id);
        }
        else
        {
            _historySelected.Clear();
            _historySelected.Add(e.Id);
        }
        UpdateHistoryChrome(HistoryStore.Shared.Entries);
    }

    private void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _historySelected.Count == 0) return;
        if (_pages[HistorySectionIndex].Visibility != Visibility.Visible) return;
        if (Keyboard.FocusedElement is TextBox) return;   // 搜索框里删字不该删图
        DeleteSelected();
        e.Handled = true;
    }

    private void DeleteSelected()
    {
        int n = HistoryStore.Shared.Remove(_historySelected.ToArray());
        _historySelected.Clear();
        _status.Text = n > 0 ? $"已删除 {n} 张" : "一张都没删掉：文件可能被别的程序占着";
    }

    private void FavoriteSelected()
    {
        int n = _historySelected.Count(id => HistoryStore.Shared.SetFavorite(id, true));
        _status.Text = n > 0 ? $"已收藏 {n} 张，它们不参与自动清理" : "没收藏成";
    }

    private void ClearHistory()
    {
        var store = HistoryStore.Shared;
        var all = store.Entries;
        if (all.Count == 0) { _status.Text = "历史里本来就没有图"; return; }
        int favorites = all.Count(e => e.Favorite);
        string scope = favorites > 0 ? $"里面有 {favorites} 张是收藏的，也会一起删掉" : "删掉之后找不回来";
        var answer = MessageBox.Show(this, $"要清空历史截图吗？共 {all.Count} 张，{scope}。",
            "清空历史截图", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _historySelected.Clear();
        int removed = store.Clear(includeFavorites: true);
        _status.Text = $"已清空 {removed} 张";
    }

    // ---------- 单条动作 ----------

    private BitmapSource? LoadForAction(HistoryEntry e)
    {
        var image = HistoryStore.Shared.LoadFull(e);
        if (image == null)
        {
            _status.Text = "这张图的文件已经不在库里了，列表已经把它去掉";
            RefreshHistory();
        }
        return image;
    }

    private void HistoryCopy(HistoryEntry e)
    {
        if (LoadForAction(e) is not BitmapSource image) return;
        var error = Exporter.TryCopyImage(image);
        _status.Text = error == null ? $"已复制 {e.Width}×{e.Height} 到剪贴板" : $"复制失败：{error}";
    }

    private void HistoryPin(HistoryEntry e)
    {
        if (LoadForAction(e) is not BitmapSource image) return;
        if (Application.Current is not App app || app.Controller is not CaptureController controller)
        {
            _status.Text = "截图模块还没起来，钉不了";
            return;
        }
        controller.PinFromHistory(image);
        _status.Text = "已钉到桌面上";
    }

    private void HistoryReplay(HistoryEntry e)
    {
        if (LoadForAction(e) is not BitmapSource image) return;
        if (Application.Current is not App app || app.Controller is not CaptureController controller)
        {
            _status.Text = "截图模块还没起来，再标注开不了";
            return;
        }
        if (controller.IsActive)
        {
            _status.Text = "截图层已经开着了，先把它收掉再回来标注";
            return;
        }
        // 截图层是全屏置顶的，把这扇窗最小化；标完之后从任务栏点回来还是这张列表。
        WindowState = WindowState.Minimized;
        controller.BeginReplay(image);
    }

    private void HistorySaveAs(HistoryEntry e)
    {
        if (LoadForAction(e) is not BitmapSource image) return;
        var path = Exporter.SaveDialog(image, App.Config, out var error);
        _status.Text = error != null ? $"保存失败：{error}"
            : path == null ? "没存，对话框取消了"
            : $"已另存为 {path}";
    }

    // ---------- 缩略图 ----------

    /// <summary>
    /// 缩略图逐张在后台线程解码，解完回主线程填。200 张一次在界面上解会把整扇窗冻住，
    /// 所以这里只保证「卡片先摆出来、图陆续出现」；列表刷过一轮之后，旧的回执一律丢弃。
    /// </summary>
    private async System.Threading.Tasks.Task LoadThumbsAsync(int seq, System.Collections.Generic.List<HistoryEntry> entries)
    {
        foreach (var e in entries)
        {
            if (seq != _historyLoadSeq) return;
            var bitmap = await System.Threading.Tasks.Task.Run(() => HistoryStore.Shared.Thumbnail(e));
            if (seq != _historyLoadSeq || bitmap == null) continue;
            if (_historyThumbs.TryGetValue(e.Id, out var image)) image.Source = bitmap;
        }
    }

    // ---------- 自检钩子：这一页的动作得能在不摸鼠标的前提下点一遍 ----------

    internal int HistoryCardCount => _historyShown.Count;
    internal int HistorySelectedCount => _historySelected.Count;
    internal string HistoryStatusForTest => _status.Text;
    internal string HistoryCountTextForTest => _historyCount.Text;
    internal bool HistoryShowsEmptyNote => _historyGrid.Children.Count > 0 && _historyGrid.Children[0] is TextBlock;

    internal void HistorySearchForTest(string text)
    {
        _historySearch.Text = text;
        RefreshHistory();
    }

    internal void HistorySourceForTest(string source)
    {
        _historySource.Select(source);
        RefreshHistory();
    }

    internal void HistoryFavoritesOnlyForTest(bool on)
    {
        _historyFavoritesOnly.IsChecked = on;
        RefreshHistory();
    }

    internal void HistoryPickForTest(int index, bool toggle) => PickCard(_historyShown[index], toggle);

    /// <summary>自检用：把某张卡片真实的右键菜单取出来（不另建一份，免得测的和用的不是同一个）。</summary>
    internal ContextMenu? CardMenuForTest(int index) =>
        index < _historyShown.Count && _historyCards.TryGetValue(_historyShown[index].Id, out var card)
            ? card.ContextMenu
            : null;

    /// <summary>按名字点卡片底部那个动作，返回界面当时说的那句回执。</summary>
    internal string HistoryDoForTest(string action, int index)
    {
        var entry = _historyShown[index];
        switch (action)
        {
            case "copy": HistoryCopy(entry); break;
            case "pin": HistoryPin(entry); break;
            case "replay": HistoryReplay(entry); break;
            case "favorite": ToggleFavorite(entry); break;
            case "delete": HistoryStore.Shared.Remove([entry.Id]); break;
            case "deleteSelected": DeleteSelected(); break;
            default: throw new System.ArgumentException($"未知的历史页动作：{action}");
        }
        return _status.Text;
    }
}
