using System.Diagnostics;
using System.Windows.Threading;
using LikeShot.Native;

namespace LikeShot.Hotkeys;

/// <summary>
/// 快捷键避让仲裁。规则三条：
/// 1. 让位进程（默认 QQ / TIM）在跑、而且这个键确实是它在接 → 放行，绝不响应；
/// 2. 让位进程在跑、但连着按两次它一次都没弹窗 → 判定它把这个键关掉了，之后由我们接管；
/// 3. 让位进程都不在跑 → 先试探这个键有没有人注册：没人注册才由我们接管，
///    被别的程序占了就继续放行，避免把别人的热键抢过来变成「按了没反应」。
/// 全程不长期持有 RegisterHotKey，所以 QQ 登录时永远能注册成功，不会弹「热键被占用」。
/// </summary>
internal sealed class HotkeyArbiter : IDisposable
{
    private readonly DispatcherTimer _timer;
    private KeyboardHook? _hook;

    private Chord _chord;
    private string[] _yieldNames = [];
    private volatile bool _yieldAlive;
    private volatile bool _keyIsFree = true;
    private bool _trackedDown;

    /// <summary>让位进程是不是真的在接这个键。判错了也能自己纠回来，见 ObserveYieldReply。</summary>
    private volatile bool _yieldHandles = true;
    private uint[] _yieldPids = [];
    private uint[] _lastSeenPids = [];
    private int _strikes;
    private ForegroundObserver? _observer;
    private DispatcherTimer? _watchdog;

    /// <summary>按下之后给它这点时间弹窗；超过就当它没接。</summary>
    private const int YieldReplyMilliseconds = 900;

    /// <summary>要连按这么多次都见不到它响应，才敢判定「它没注册这个键」——一次误读不算数。</summary>
    private const int YieldStrikeLimit = 2;

    public HotkeyArbiter(AppConfig config, Action trigger)
    {
        Trigger = trigger;
        Apply(config);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshOwnership();
    }

    public Action Trigger { get; }

    public bool IsRunning => _hook != null;

    public string StatusText
    {
        get
        {
            if (!_yieldAlive) return _keyIsFree ? "热键待命：按下即截图" : "热键被其它程序占用：本次放行";
            if (_yieldHandles) return $"已避让：热键让给 {YieldNames()}";
            return _keyIsFree ? $"{YieldNames()} 没接这个键：按下由我们截图"
                              : "热键被其它程序占用：本次放行";
        }
    }

    private string YieldNames() => string.Join("/", _yieldNames.Where(Alive));

    private static bool Alive(string name) => Process.GetProcessesByName(name).Length > 0;

    private string ChordText() => _chord.Text;

    /// <summary>自检用：把仲裁器此刻的四项判定原样吐出来，不靠日志倒推。</summary>
    internal string YieldDiagForTest =>
        $"让位进程在跑={_yieldAlive} 它接这个键={_yieldHandles} 键空闲={_keyIsFree} 连按未见响应={_strikes} 批次进程={_yieldPids.Length}";

    public void Apply(AppConfig config)
    {
        _chord = Chord.Parse(config.CaptureChord);
        _yieldNames = config.YieldToProcesses.ToArray();
        _trackedDown = false;
        // 换了键位或换了名单，之前「它接不接这个键」的结论就不能沿用了。
        _yieldHandles = true;
        _strikes = 0;
        _lastSeenPids = [];
    }

    public void Start()
    {
        _hook?.Dispose();
        _hook = new KeyboardHook(Filter);
        _hook.Install();
        RefreshOwnership();
        _timer.Start();
    }

    public void Reload(AppConfig config)
    {
        Apply(config);
        RefreshOwnership();
    }

    /// <summary>自检用：只换让位名单，立刻重探一次归属，其余配置不动。</summary>
    public void SetYieldNames(string[] names)
    {
        _yieldNames = names;
        _trackedDown = false;
        _yieldHandles = true;
        _strikes = 0;
        _lastSeenPids = [];
        RefreshOwnership();
    }

    public void ApplyConfig(AppConfig config) => Apply(config);

    private bool Filter(in KeyboardHook.KeyPacket packet)
    {
        bool chordKey = packet.Vk == _chord.Vk;
        if (!chordKey) return false;

        if (!packet.IsKeyDown)
        {
            bool swallowUp = _trackedDown;
            _trackedDown = false;
            return swallowUp;
        }

        if (_yieldAlive && _yieldHandles)
        {
            LogFromHook($"热键 {ChordText()} 让位：{YieldNames()} 在跑，交还给系统");
            ObserveYieldReply();
            return false;
        }

        if (_yieldAlive && !_keyIsFree)
        {
            LogFromHook($"热键 {ChordText()} 放行：已被其它程序注册");
            return false;
        }

        if (!_chord.Matches(packet.Vk, packet.Alt, packet.Control, packet.Shift, packet.Win))
        {
            // 单敲 F9 不算异常，只有「像是要按组合键却没按对」才记一笔——「按了没反应」最难查。
            if (packet.Alt || packet.Control || packet.Shift || packet.Win)
                LogFromHook($"热键 {ChordText()} 主键命中但修饰键不符：Ctrl={packet.Control} Alt={packet.Alt} Shift={packet.Shift} Win={packet.Win}");
            return false;
        }

        if (_trackedDown) return false; // 长按自动重复不再触发第二次
        _trackedDown = true;
        LogFromHook($"热键 {ChordText()} 接管：无人注册，启动自己的截图层");

        // 低级钩子有 300ms 超时，超时会连带整个系统输入变慢，所以这里只投递不执行。
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Trigger);
        return true;
    }

    /// <summary>
    /// 钩子里绝不做文件 I/O：AppendAllText 一次要开/写/关文件，撞上 300ms 超时
    /// Windows 会把钩子默默摘掉，之后热键就再也不经过我们了（实测第一次让位后就哑了）。
    /// </summary>
    private static void LogFromHook(string message)
    {
        var app = System.Windows.Application.Current;
        if (app == null) Log.Write(message);
        else app.Dispatcher.BeginInvoke(() => Log.Write(message));
    }

    private void RefreshOwnership()
    {
        var pids = new List<uint>();
        bool alive = false;
        foreach (var name in _yieldNames)
        {
            var found = Process.GetProcessesByName(name);
            if (found.Length == 0) continue;
            alive = true;
            foreach (var process in found)
            {
                pids.Add((uint)process.Id);
                process.Dispose();
            }
        }

        if (alive)
        {
            var current = pids.Distinct().OrderBy(id => id).ToArray();
            // 让位进程换了一批（重启过、又开了一个号）→ 之前的判定不作数，重新当成「它在接」。
            if (!_lastSeenPids.SequenceEqual(current))
            {
                if (_yieldAlive && !_yieldHandles) Log.Write("让位进程换了批次，避让判定重新来过");
                _yieldHandles = true;
                _strikes = 0;
                _lastSeenPids = current;
            }
            _yieldAlive = true;
            _yieldPids = current;
            // 它还在接的时候绝不试探：那一次极短的注册有可能正好卡在它注册之前，把它挤掉。
            // 只有已经判过「它不接这个键」才敢探一下——探出来被别人占了照样放行，
            // 这一条也顺带兜住了「他中途把 QQ 的截图热键打开」那种情况。
            _keyIsFree = _yieldHandles ? false : ChordProbe.IsFree(_chord);
            return;
        }

        bool wasAlive = _yieldAlive;
        _yieldAlive = false;
        _lastSeenPids = [];
        _yieldPids = [];
        if (wasAlive)
        {
            _trackedDown = false;
            _yieldHandles = true;
            _strikes = 0;
        }
        _keyIsFree = ChordProbe.IsFree(_chord);
    }

    /// <summary>
    /// 让位这一次顺手看一眼它到底响没响：QQ 开着但把截图热键关掉的人，
    /// 按下去原本是谁都不动——这就是「已知边界」里最难查的那一种「按了没反应」。
    /// 它弹了窗 → 继续让位；连着几次都没弹 → 改判由我们接管，并当场把这一下补上。
    /// </summary>
    private void ObserveYieldReply()
    {
        if (_observer != null || _yieldPids.Length == 0) return;
        _observer = new ForegroundObserver(_yieldPids);
        _watchdog ??= BuildWatchdog();
        _watchdog.Start();
    }

    private DispatcherTimer BuildWatchdog()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(YieldReplyMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_observer == null) return;
            bool replied = _observer.Responded;
            _observer.Dispose();
            _observer = null;

            if (replied)
            {
                if (_strikes != 0) Log.Write("让位进程又接住这个键了，回到避让");
                _strikes = 0;
                _yieldHandles = true;
                return;
            }

            _strikes++;
            if (_strikes < YieldStrikeLimit)
            {
                Log.Write($"让位后 {YieldReplyMilliseconds}ms 内没见 {YieldNames()} 弹窗（{_strikes}/{YieldStrikeLimit}），再按一次确认");
                return;
            }

            _yieldHandles = false;
            Log.Write($"连着 {YieldStrikeLimit} 次让位都没见 {YieldNames()} 响应：判定它没注册这个键，之后由我们接管");
            RefreshOwnership();
            Trigger();   // 这一下不该白按：现在就把截图层开起来
        };
        return timer;
    }

    /// <summary>盯住「这几个进程里有没有窗口抢到前台」。</summary>
    private sealed class ForegroundObserver : IDisposable
    {
        private readonly Win32.WinEventProc _proc;
        private readonly uint[] _pids;
        private nint _hook;
        private int _responded;

        public ForegroundObserver(uint[] pids)
        {
            _pids = pids;
            _proc = OnEvent;
            // 回调要打在装有消息泵的线程上，所以这个钩子只允许从 UI 线程安装（Filter 就在那儿跑）。
            _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
                nint.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        }

        public bool Responded => System.Threading.Volatile.Read(ref _responded) != 0;

        private void OnEvent(nint hook, uint eventType, nint hWnd, int idObject, int idChild, uint threadId, uint eventTime)
        {
            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            if (System.Array.IndexOf(_pids, pid) >= 0)
                System.Threading.Interlocked.Exchange(ref _responded, 1);
        }

        public void Dispose()
        {
            if (_hook == nint.Zero) return;
            Win32.UnhookWinEvent(_hook);
            _hook = nint.Zero;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _watchdog?.Stop();
        _observer?.Dispose();
        _observer = null;
        _hook?.Dispose();
        _hook = null;
    }
}
