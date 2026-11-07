using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LikeShot.Capture;

/// <summary>
/// 滚动长截图拼接器。
///
/// 坐标关系：画布行号 = 帧行号 + _offset（_offset = 这一帧第 0 行落在页面上的位置）。
/// 每帧要解决两件事：
/// 1. _offset 这次增加了多少（滚掉的行数）；
/// 2. 帧内哪些行是真新增的（画布还没覆盖到的那一段，且不含固定底栏）。
///
/// 第 1 件只能靠内容比对，但白行/纯色行在页面里成百次出现，拿它们当锚点会量出错误的偏移量，
/// 于是每帧都把重叠区当新内容重复贴一遍——这就是长图里内容翻倍的来源。这里只让
/// 「在画布里出现过且只出现过一次」的行投票，取众数，白行天然被排除。
/// 第 2 件要避开固定底栏：底栏每帧都在帧尾，不裁掉就会一路重复下去。
/// </summary>
public sealed class LongStitcher
{
    private const int SamplesPerRow = 96;
    public const int MaxHeight = 30000;
    private const int MinVotes = 8;

    private byte[] _buffer;
    private long[] _rowHash = [];
    private int _height;
    private readonly int _width;
    private readonly int _stride;
    private long[] _previous = [];
    private int _offset;
    private int _footer;
    private int _lastStep;
    private readonly int _reservedFooter;

    /// <summary>画布里只出现一次的行哈希 → 它的画布行号；出现第二次就从中剔除。</summary>
    private readonly Dictionary<long, int> _uniqueRow = [];
    private readonly Dictionary<long, int> _rowCount = [];
    private readonly StringBuilder _diag = new();

    /// <summary>
    /// reservedFooterRows：帧底固定留给自家工具栏的行数（工具栏压进选区底边时）。
    /// 这些行每一拍都被掩成纯色，从头到尾都不是页面内容——从第一帧起就不进画布，
    /// 滚动判定也把它们当固定底栏，不滚动也不会烤进长图。
    /// </summary>
    public LongStitcher(Int32Rect firstRect, byte[] firstFrame, int firstStride, int reservedFooterRows = 0)
    {
        if (firstStride != firstRect.Width * 4) throw new ArgumentException("帧步长必须等于宽×4", nameof(firstStride));
        _width = firstRect.Width;
        _stride = _width * 4;
        _reservedFooter = Math.Clamp(reservedFooterRows, 0, firstRect.Height / 2);
        int capacity = Math.Min(MaxHeight, Math.Max(firstRect.Height * 8, 2048));
        _buffer = new byte[capacity * _stride];
        _rowHash = new long[capacity];

        int rows = Math.Max(0, Math.Min(firstRect.Height - _reservedFooter, capacity));
        AppendRows(firstFrame, firstStride, 0, rows);
        _previous = RowHashes(firstFrame, firstStride, firstRect.Height);
        _footer = _reservedFooter;
    }

    public int Width => _width;
    public int Height => _height;
    public int Stride => _stride;
    public bool IsFull => _height >= MaxHeight;

    /// <summary>帧底整段预留掉的行数（自家工具栏/预览压在选区底边）；0 表示没预留。</summary>
    public int ReservedFooter => _footer;

    /// <summary>每帧的判定过程，排查「重复行」时靠这个而不是靠猜。</summary>
    public string Diag => _diag.ToString();

    /// <summary>喂一帧实时画面，返回真正追加的行数（0 表示没滚或内容没变）。</summary>
    public int Feed(byte[] frame, int frameStride, int frameHeight)
    {
        if (frameStride != _stride) throw new ArgumentException("帧宽度和选区不一致", nameof(frameStride));

        long[] hashes = RowHashes(frame, frameStride, frameHeight);

        // 整帧和上一帧一模一样：就是还没滚，别去猜固定带，也别追加。
        if (hashes.Length == _previous.Length && hashes.SequenceEqual(_previous))
        {
            _previous = hashes;
            _diag.AppendLine("整帧未变（没滚）");
            return 0;
        }

        var (top, bottom) = FixedBands(hashes);
        // 帧底预留（自家工具栏压在选区底边）按固定底栏算：那一块永远不算新增内容。
        int footer = Math.Max(frameHeight - bottom, _reservedFooter);

        // 投票量出来的是「这一帧第 0 行落在画布哪一行」这个绝对位置，不是这次滚掉的行数。
        // 之前把它当增量累加，偏移量成倍跑飞，长图里内容就是这么翻倍贴的。
        int offset = DetectOffset(hashes, top, bottom, out int votes, out int present);
        if (offset < 0)
        {
            // 票不够时的两种情况分开处理：
            // 帧里几乎没有一行在画布出现过 → 一跳跳过了一屏以上，整帧当新内容接上去；
            // 有得对但都对不上唯一行（整片白）→ 按上一帧的步长往前推，宁少贴几行也不重复贴。
            offset = present * 20 < bottom - top ? _height : _offset + _lastStep;
        }

        offset = Math.Max(offset, _offset); // 页面往回滚不回退画布，顶多这一帧不新增
        int step = offset - _offset;
        if (step > 0) _lastStep = step;
        _offset = offset;

        // 只有真的滚了才认这个尾带是固定底栏。没滚的时候「尾部一行不变」只是内容本来就静止，
        // 当底栏裁掉会凭空吃掉几百行真实画面（第一次实跑就把首帧从 1709 裁到了 1308）。
        if (step > 0 && footer > _footer)
        {
            TrimTail(footer - _footer);
            _footer = footer;
        }

        int appended = AppendRows(frame, frameStride, Math.Max(0, _height - _offset), frameHeight - _footer);

        _previous = hashes;
        Note(step, votes, present, top, bottom, appended);
        return appended;
    }

    /// <summary>
    /// 收尾补拍：工具栏/预览藏起来之后补的那一帧，把之前整段预留掉的底栏接回来。
    /// 没有预留过底栏就是空操作——那种情况这一条本来就一路拍到了。
    /// </summary>
    public int AppendTail(byte[] frame, int frameStride, int frameHeight)
    {
        if (frameStride != _stride || _footer <= 0 || IsFull) return 0;

        // 补拍这一刻页面可能又挪了几行：先在「上一帧没被掩住的那一段」上投一次票校正落点，
        // 票不够就沿用上一帧的落点（页面没动就是它）。
        long[] hashes = RowHashes(frame, frameStride, frameHeight);
        int offset = DetectOffset(hashes, 0, Math.Max(0, frameHeight - _footer), out int votes, out int present);
        if (offset > _offset) _offset = offset;

        // 落点对不上就一行都不接：硬接只会把已经拼过的重叠区再贴一遍，那正是长图内容翻倍的来源。
        int fromRow = _height - _offset;
        if (fromRow < 0 || fromRow >= frameHeight)
        {
            _diag.AppendLine(fromRow < 0
                ? $"收尾补拍：画布落后于落点（画布={_height} 落点={_offset} 帧高={frameHeight}），一行没接"
                : $"收尾补拍：画布已经盖到这一帧末尾（画布={_height} 落点={_offset} 帧高={frameHeight}），不用接");
            return 0;
        }

        int appended = AppendRows(frame, frameStride, fromRow, frameHeight);
        _diag.AppendLine($"收尾补拍：底栏={_footer} 落点={_offset} 票数={votes} 认出行数={present} 追加={appended} 画布={_height}");
        return appended;
    }

    private void Note(int step, int votes, int present, int top, int bottom, int appended)
    {
        if (_diag.Length > 60000) return;
        _diag.AppendLine($"带=[{top},{bottom}) 步进={step} 落点={_offset} 票={votes} 认出行数={present} 追加={appended} 底栏={_footer} 画布={_height}");
    }

    /// <summary>和上一帧相比，首尾各有多少行一动不动。</summary>
    private (int top, int bottom) FixedBands(long[] hashes)
    {
        int n = Math.Min(hashes.Length, _previous.Length);
        int top = 0;
        while (top < n && hashes[top] == _previous[top]) top++;

        int bottom = n;
        while (bottom > top && hashes[bottom - 1] == _previous[bottom - 1]) bottom--;

        if (bottom - top < Math.Max(16, n / 4)) return (0, n);
        return (top, bottom);
    }

    /// <summary>这一帧第 0 行对应画布哪一行；只让「画布里唯一」的行投票，依据不足返回 -1。</summary>
    private int DetectOffset(long[] hashes, int bandTop, int bandBottom, out int bestVotes, out int present)
    {
        bestVotes = 0;
        present = 0;
        if (bandBottom <= bandTop) return -1;

        var tally = new Dictionary<int, int>();
        for (int r = bandTop; r < bandBottom; r++)
        {
            if (!_rowCount.ContainsKey(hashes[r])) continue;
            present++;
            if (!_uniqueRow.TryGetValue(hashes[r], out int canvasRow)) continue;
            int offset = canvasRow - r;
            if (offset < _offset || offset > _offset + hashes.Length * 2) continue;
            tally[offset] = tally.TryGetValue(offset, out int v) ? v + 1 : 1;
        }

        int best = -1, bestCount = 0;
        foreach (var kv in tally)
        {
            // 同票取更大的滚动量：低估会把重叠区再贴一次（重复），高估只是漏几行。
            if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key > best))
            {
                best = kv.Key;
                bestCount = kv.Value;
            }
        }

        bestVotes = bestCount;
        return bestCount >= MinVotes ? best : -1;
    }

    private void TrimTail(int rows)
    {
        rows = Math.Min(rows, Math.Max(0, _height - 1));
        _height -= rows;
        RebuildIndex();
    }

    private int AppendRows(byte[] frame, int frameStride, int fromRow, int toRow)
    {
        int rows = Math.Min(toRow - fromRow, MaxHeight - _height);
        if (rows <= 0) return 0;

        EnsureCapacity(_height + rows);
        for (int r = 0; r < rows; r++)
        {
            int canvasRow = _height + r;
            Buffer.BlockCopy(frame, (fromRow + r) * frameStride, _buffer, canvasRow * _stride, _stride);
            _rowHash[canvasRow] = HashRow(frame, (fromRow + r) * frameStride, _stride);
            Register(canvasRow, _rowHash[canvasRow]);
        }

        _height += rows;
        return rows;
    }

    private void EnsureCapacity(int rows)
    {
        if (rows <= _rowHash.Length) return;
        int capacity = Math.Min(MaxHeight, Math.Max(rows, _rowHash.Length * 2));
        var next = new byte[capacity * _stride];
        Buffer.BlockCopy(_buffer, 0, next, 0, _height * _stride);
        _buffer = next;
        var hashes = new long[capacity];
        Array.Copy(_rowHash, hashes, _height);
        _rowHash = hashes;
    }

    private void Register(int canvasRow, long hash)
    {
        if (_rowCount.TryGetValue(hash, out int c) && c > 0)
        {
            _rowCount[hash] = c + 1;
            _uniqueRow.Remove(hash);
            return;
        }
        _rowCount[hash] = 1;
        _uniqueRow[hash] = canvasRow;
    }

    /// <summary>裁尾之后「只出现一次」的判定全变了，只能整表重建。</summary>
    private void RebuildIndex()
    {
        _uniqueRow.Clear();
        _rowCount.Clear();
        for (int r = 0; r < _height; r++) Register(r, _rowHash[r]);
    }

    private static long[] RowHashes(byte[] data, int stride, int rows)
    {
        var hashes = new long[rows];
        for (int y = 0; y < rows; y++) hashes[y] = HashRow(data, y * stride, stride);
        return hashes;
    }

    /// <summary>一行的指纹：横向等距取 ~96 个像素。自检也用它逐行核对画布。</summary>
    public static long HashRow(byte[] data, int rowOffset, int stride)
    {
        int width = stride / 4;
        int step = Math.Max(1, width / SamplesPerRow);
        long h = 1469598103934665603L;
        unchecked
        {
            for (int x = 0; x < width; x += step)
            {
                int i = rowOffset + x * 4;
                h = (h ^ data[i]) * 1099511628211L;
                h = (h ^ data[i + 1]) * 1099511628211L;
                h = (h ^ data[i + 2]) * 1099511628211L;
            }
        }
        return h;
    }

    /// <summary>自检用：当前画布每一行的指纹。</summary>
    public long[] CanvasRowHashes() => _rowHash[.._height].ToArray();

    public WriteableBitmap ToBitmap()
    {
        var bmp = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, _width, _height), _buffer, _stride, 0);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// 实时预览用：把画布 [startRow, startRow+count) 行整块拷进 dest（Bgra32，dest 从 destOffset 字节起）。
    /// 只拷要的那几行，不为预览把整幅长图再复制一份——画布本身已经是几十上百 MB 那份了。
    /// </summary>
    public void CopyCanvasRows(int startRow, int count, byte[] dest, int destOffset)
    {
        startRow = Math.Clamp(startRow, 0, _height);
        count = Math.Min(count, Math.Max(0, _height - startRow));
        if (count > 0) Buffer.BlockCopy(_buffer, startRow * _stride, dest, destOffset, count * _stride);
    }

    /// <summary>
    /// 横向拼接时预览用：把画布 [startCol, startCol+count) 列拷进 dest，
    /// dest 按行存放（每行 destStride 字节，从 destOffset 字节起）。列在源里不连续，只能逐像素搬。
    /// </summary>
    public void CopyCanvasColumns(int startCol, int count, byte[] dest, int destStride, int destOffset)
    {
        startCol = Math.Clamp(startCol, 0, _width);
        count = Math.Min(count, Math.Max(0, _width - startCol));
        for (int y = 0; y < _height; y++)
        {
            int src = y * _stride + startCol * 4;
            int dst = destOffset + y * destStride;
            for (int x = 0; x < count * 4; x += 4)
            {
                dest[dst + x] = _buffer[src + x];
                dest[dst + x + 1] = _buffer[src + x + 1];
                dest[dst + x + 2] = _buffer[src + x + 2];
                dest[dst + x + 3] = _buffer[src + x + 3];
            }
        }
    }
}
