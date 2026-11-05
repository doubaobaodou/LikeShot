using System.Diagnostics;
using System.Windows.Threading;

namespace LikeShot.Hotkeys;

/// <summary>
/// 快捷键避让仲裁。规则只有两条：
/// 1. 让位进程（默认 QQ / TIM）在跑 → 这个组合键一定是它的，我们放行，绝不响应；
/// 2. 让位进程都不在跑 → 先试探这个键有没有人注册：没人注册才由我们接管，
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

    public HotkeyArbiter(AppConfig config, Action trigger)
    {
        Trigger = trigger;
        Apply(config);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshOwnership();
    }

    public Action Trigger { get; }

    public bool IsRunning => _hook != null;

    public string StatusText =>
        _yieldAlive ? $"已避让：热键让给 {string.Join("/", _yieldNames.Where(Alive))}"
        : _keyIsFree ? "热键待命：按下即截图"
        : "热键被其它程序占用：本次放行";

    private static bool Alive(string name) => Process.GetProcessesByName(name).Length > 0;

    private string ChordText() => _chord.Text;

    public void Apply(AppConfig config)
    {
        _chord = Chord.Parse(config.CaptureChord);
        _yieldNames = config.YieldToProcesses.ToArray();
        _trackedDown = false;
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

        if (_yieldAlive)
        {
            LogFromHook($"热键 {ChordText()} 让位：QQ/TIM 在跑，交还给系统");
            return false;
        }

        if (!_keyIsFree)
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
        bool alive = false;
        foreach (var name in _yieldNames)
        {
            if (Process.GetProcessesByName(name).Length > 0) { alive = true; break; }
        }

        bool wasAlive = _yieldAlive;
        _yieldAlive = alive;

        if (alive)
        {
            // 让位进程在跑时禁止试探：那一次极短的注册有可能正好卡在它注册之前，把它挤掉。
            _keyIsFree = false;
            return;
        }

        if (wasAlive) _trackedDown = false;
        _keyIsFree = ChordProbe.IsFree(_chord);
    }

    public void Dispose()
    {
        _timer.Stop();
        _hook?.Dispose();
        _hook = null;
    }
}
