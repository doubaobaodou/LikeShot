using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LikeShot.Capture;

/// <summary>一条历史截图。File/Thumb 存的是相对根目录的路径，整目录搬走也不失联。</summary>
public sealed record HistoryEntry(
    string Id, string File, string Thumb, DateTime Time,
    int Width, int Height, long Bytes, string Source, bool Favorite)
{
    public string Title => $"{Time:MM-dd HH:mm:ss}  {Width}×{Height}";
    public string SizeText => Bytes >= 1024 * 1024 ? $"{Bytes / 1024.0 / 1024.0:0.##} MB" : $"{Bytes / 1024.0:0.#} KB";
}

/// <summary>
/// 历史截图库：每张成品图落一份原图 + 一份缩略图，另配一个 index.json。
/// 有了它，复制失败、保存失败、存完又被覆盖掉的那些图都还能找回来，
/// 所以写库这条路自己绝不能抛——它排在导出之前，是最后一道保险。
/// </summary>
public sealed class HistoryStore
{
    public const string SourceCapture = "截图";
    public const string SourceLong = "长截图";
    public const string SourcePin = "钉图";
    public const string SourceReplay = "再标注";

    /// <summary>缩略图长边像素：设置页的卡片 240 宽，175% 缩放下正好不糊。</summary>
    private const int ThumbEdge = 420;

    private static readonly object Gate = new();
    private static HistoryStore? _shared;

    private readonly string _root;
    private readonly List<HistoryEntry> _items = [];
    private readonly Dictionary<string, BitmapSource> _thumbCache = new();
    private bool _loaded;

    public HistoryStore(string root) => _root = root;

    public static HistoryStore Shared
    {
        get
        {
            lock (Gate)
            {
                return _shared ??= new HistoryStore(
                    System.IO.Path.Combine(App.ConfigDirectory, "history"));
            }
        }
    }

    /// <summary>自检用：把库指到临时目录，不去动他真实的历史。</summary>
    internal static HistoryStore UseRootForTest(string root)
    {
        lock (Gate)
        {
            _shared = new HistoryStore(root);
            return _shared;
        }
    }

    private string IndexPath => System.IO.Path.Combine(_root, "index.json");
    private string ThumbPathOf(HistoryEntry e) => System.IO.Path.Combine(_root, e.Thumb);

    /// <summary>原图在磁盘上的绝对路径（界面要拿来定位、另存默认名）。</summary>
    public string FullPathOf(HistoryEntry e) => System.IO.Path.Combine(_root, e.File);

    /// <summary>最近一次入库失败的原因；null 表示一路都 good。</summary>
    public string? LastError { get; private set; }

    public string Root => _root;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _items.ToArray();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _items.Clear();
        try
        {
            if (!File.Exists(IndexPath)) return;
            var parsed = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(IndexPath), AppConfig.Options);
            if (parsed == null) return;
            foreach (var e in parsed)
            {
                if (File.Exists(FullPathOf(e))) _items.Add(e);
            }
        }
        catch (Exception ex)
        {
            Log.Error("历史索引读取失败", ex);
        }
    }

    private void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(_items, AppConfig.Options));
        }
        catch (Exception ex)
        {
            Log.Error("历史索引写入失败", ex);
        }
    }

    /// <summary>
    /// 存一张成品图。失败只记原因、不抛异常——调用它的截图出口不能因为库出问题而丢图。
    /// </summary>
    public HistoryEntry? Add(BitmapSource image, string source)    {
        LastError = null;
        try
        {
            var now = DateTime.Now;
            string id = $"{source}_{now:yyyyMMdd_HHmmss_fff}";
            string relFile = System.IO.Path.Combine(now.ToString("yyyy-MM-dd"), id + ".png");
            string relThumb = System.IO.Path.Combine("thumb", id + ".png");

            lock (Gate)
            {
                EnsureLoaded();
                Directory.CreateDirectory(System.IO.Path.Combine(_root, now.ToString("yyyy-MM-dd")));
                Directory.CreateDirectory(System.IO.Path.Combine(_root, "thumb"));

                string fullPath = System.IO.Path.Combine(_root, relFile);
                // 库里的写入失败必须冒出来：外层catch 会把它记成 LastError，
                // 不能让「以为存进去了、其实没有」这种状态留在索引里。
                var writeError = Exporter.TryWrite(image, fullPath);
                if (writeError != null) throw new IOException($"原图没写进去：{writeError}");
                long bytes = new FileInfo(fullPath).Length;
                WriteThumb(image, System.IO.Path.Combine(_root, relThumb));

                var entry = new HistoryEntry(id, relFile, relThumb, now,
                    image.PixelWidth, image.PixelHeight, bytes, source, false);
                _items.Insert(0, entry);
                PruneLocked(App.Config.HistoryLimit);
                SaveIndex();
                Log.Write($"历史入库 {id} {entry.Width}x{entry.Height} {bytes}B 来源={source}");
                Changed?.Invoke();
                return entry;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("历史入库失败", ex);
            return null;
        }
    }

    /// <summary>库里条目变了（入库/删除/收藏），让已经打开的历史页自己刷新。</summary>
    public event Action? Changed;

    /// <summary>
    /// 出口点完就该收摊，不该为一次 PNG 编码卡住那一下。所以入库排在出图之前发起，
    /// 但真正写盘放到空闲优先级：截图层已经关了，鼠标键盘都不等它。
    /// </summary>
    public void AddLater(BitmapSource image, string source)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { Add(image, source); return; }
        dispatcher.InvokeAsync(() => Add(image, source),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static void WriteThumb(BitmapSource image, string path)
    {
        double factor = (double)ThumbEdge / Math.Max(1, Math.Max(image.PixelWidth, image.PixelHeight));
        var scaled = new TransformedBitmap(image, new ScaleTransform(factor, factor));
        scaled.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 立刻按新的保留数量清一次：改了设置不该等到下一次截图才生效。
    /// </summary>
    public int PruneTo(int limit)
    {
        lock (Gate)
        {
            EnsureLoaded();
            int before = _items.Count;
            PruneLocked(limit);
            if (before != _items.Count) SaveIndex();
            Changed?.Invoke();
            return before - _items.Count;
        }
    }

    /// <summary>淘汰：只砍没收藏的，从最旧的那张开始砍。</summary>
    private void PruneLocked(int limit)
    {
        if (limit <= 0) limit = 200;
        var droppable = _items.Where(e => !e.Favorite).ToArray();
        for (int i = droppable.Length - 1; i >= limit; i--) DeleteLocked(droppable[i]);
    }

    private void DeleteLocked(HistoryEntry e)
    {
        _items.Remove(e);
        _thumbCache.Remove(e.Id);
        TryDeleteFile(FullPathOf(e));
        TryDeleteFile(ThumbPathOf(e));
        // 日期目录空了就收掉，别让库里堆一串空文件夹。
        try
        {
            string day = Path.GetDirectoryName(FullPathOf(e))!;
            if (Directory.Exists(day) && Directory.GetFiles(day).Length == 0 && Directory.GetDirectories(day).Length == 0)
                Directory.Delete(day);
        }
        catch { /* 空目录留着也不算错 */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Error($"历史文件删除失败 {path}", ex); }
    }

    public bool SetFavorite(string id, bool favorite)
    {
        lock (Gate)
        {
            EnsureLoaded();
            int index = _items.FindIndex(e => e.Id == id);
            if (index < 0) return false;
            _items[index] = _items[index] with { Favorite = favorite };
            SaveIndex();
            Changed?.Invoke();
            return true;
        }
    }

    public int Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        lock (Gate)
        {
            EnsureLoaded();
            int removed = 0;
            foreach (var e in _items.Where(e => set.Contains(e.Id)).ToArray())
            {
                DeleteLocked(e);
                removed++;
            }
            if (removed > 0) { SaveIndex(); Changed?.Invoke(); }
            return removed;
        }
    }

    /// <summary>清空：收藏的一律留下，除非明确连收藏一起清。</summary>
    public int Clear(bool includeFavorites)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var doomed = _items.Where(e => includeFavorites || !e.Favorite).ToArray();
            foreach (var e in doomed) DeleteLocked(e);
            SaveIndex();
            Changed?.Invoke();
            return doomed.Length;
        }
    }

    public int Count(bool includeFavorites = true)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return includeFavorites ? _items.Count : _items.Count(e => !e.Favorite);
        }
    }

    public int FavoriteCount
    {
        get { lock (Gate) { EnsureLoaded(); return _items.Count(e => e.Favorite); } }
    }

    /// <summary>卡片上的缩略图：优先读缩略图文件，缺了就从原图解一份小的。</summary>
    public BitmapSource? Thumbnail(HistoryEntry e)
    {
        lock (Gate)
        {
            if (_thumbCache.TryGetValue(e.Id, out var cached)) return cached;
        }
        var loaded = Decode(ThumbPathOf(e), ThumbEdge) ?? Decode(FullPathOf(e), ThumbEdge);
        if (loaded == null) return null;
        lock (Gate)
        {
            if (_thumbCache.Count > 400) _thumbCache.Clear();
            _thumbCache[e.Id] = loaded;
        }
        return loaded;
    }

    /// <summary>原图。复制、另存、再标注都从这里取。</summary>
    public BitmapSource? LoadFull(HistoryEntry e) => Decode(FullPathOf(e), 0);

    private static BitmapSource? Decode(string path, int decodeEdge)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeEdge > 0) bmp.DecodePixelWidth = decodeEdge;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error($"历史图解码失败 {path}", ex);
            return null;
        }
    }

    /// <summary>搜索按 日期时间、来源、尺寸、文件名 找；不区分大小写。</summary>
    public static bool Matches(HistoryEntry e, string query, string? source, bool favoritesOnly)
    {
        if (favoritesOnly && !e.Favorite) return false;
        if (source != null && source != "全部" && e.Source != source) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;

        string hay = string.Join(' ',
            e.Time.ToString("yyyy-MM-dd HH:mm:ss"), e.Source,
            $"{e.Width}x{e.Height}", $"{e.Width}×{e.Height}", e.Id);
        return hay.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
