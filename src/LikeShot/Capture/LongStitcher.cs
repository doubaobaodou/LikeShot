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

    /// <summary>画布里只出现一次的行哈希 → 它的画布行号；出现第二次就从中剔除。</summary>
    private readonly Dictionary<long, int> _uniqueRow = [];
    private readonly Dictionary<long, int> _rowCount = [];
    private readonly StringBuilder _diag = new();

    public LongStitcher(Int32Rect firstRect, byte[] firstFrame, int firstStride)
    {
        if (firstStride != firstRect.Width * 4) throw new ArgumentException("帧步长必须等于宽×4", nameof(firstStride));
        _width = firstRect.Width;
        _stride = _width * 4;
        int capacity = Math.Min(MaxHeight, Math.Max(firstRect.Height * 8, 2048));
        _buffer = new byte[capacity * _stride];
        _rowHash = new long[capacity];

        int rows = Math.Min(firstRect.Height, capacity);
        AppendRows(firstFrame, firstStride, 0, rows);
        _previous = RowHashes(firstFrame, firstStride, rows);
    }

    public int Width => _width;
    public int Height => _height;
    public int Stride => _stride;
    public bool IsFull => _height >= MaxHeight;

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
        int footer = frameHeight - bottom;

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
}
