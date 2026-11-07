using LikeShot.Native;

namespace LikeShot.Hotkeys;

/// <summary>形如 "Ctrl+Alt+A" 的组合键，解析成 Win32 修饰键掩码 + 虚拟键码。</summary>
public readonly record struct Chord(uint Modifiers, uint Vk, string Text)
{
    public static Chord Parse(string text)
    {
        uint mods = 0;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Win32.MOD_CONTROL; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "win" or "super": mods |= Win32.MOD_WIN; break;
                default:
                    vk = FromKey(raw);
                    break;
            }
        }

        if (vk == 0) throw new FormatException($"无法识别的组合键：{text}");
        if (mods == 0) throw new FormatException($"组合键至少要带一个修饰键：{text}");
        return new Chord(mods, vk, text);
    }

    private static uint FromKey(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (key.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1);

        return key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "printscreen" or "prtsc" => 0x2C,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "prior" => 0x21,
            "pagedown" or "next" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => 0
        };
    }

    /// <summary>钩子里看到的按键是否命中本组合键（Alt 由钩子结构体标记给出）。</summary>
    public bool Matches(uint vkCode, bool altDown, bool ctrlDown, bool shiftDown, bool winDown)
    {
        if (vkCode != Vk) return false;
        bool wantAlt = (Modifiers & Win32.MOD_ALT) != 0;
        bool wantCtrl = (Modifiers & Win32.MOD_CONTROL) != 0;
        bool wantShift = (Modifiers & Win32.MOD_SHIFT) != 0;
        bool wantWin = (Modifiers & Win32.MOD_WIN) != 0;
        return altDown == wantAlt && ctrlDown == wantCtrl && shiftDown == wantShift && winDown == wantWin;
    }
}

internal static class ChordProbe
{
    /// <summary>
    /// 试探系统里这个组合键是否没人注册。注册成功立刻注销。
    /// 只有在确认「让位进程都不在跑」时才允许调用，否则会把 QQ 的注册机会抢掉。
    /// </summary>
    public static bool IsFree(Chord chord)
    {
        const int probeId = 0xC0DE;
        var hwnd = nint.Zero;
        bool ok = Win32.RegisterHotKey(hwnd, probeId, chord.Modifiers | Win32.MOD_NOREPEAT, chord.Vk);
        if (ok) Win32.UnregisterHotKey(hwnd, probeId);
        return ok;
    }

    /// <summary>
    /// 给设置界面用的那句「这个键按下去会是谁」。判定口径和仲裁器一致：
    /// 让位进程活着先给它；它连着几次都没接，才改由我们接管。它不在跑才注册一下看看。
    /// heldByUs 必须先问仲裁器：那个键正被我们自己挂着时，IsFree 探出来必然是「占用」，
    /// 不区分就会把「我们自己在接」说成「被别的程序占了，建议换一个」。
    /// </summary>
    public static (bool Attention, string Text) Describe(Chord chord, IEnumerable<string> yieldNames, bool heldByUs = false)
    {
        foreach (var name in yieldNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (System.Diagnostics.Process.GetProcessesByName(name.Trim()).Length == 0) continue;
            return (true, $"{name} 正在运行：按 {chord.Text} 先给 {name}；{name} 连着几次都没接（比如它的截图热键被关了），就改由我们截图");
        }

        if (heldByUs)
            return (false, $"这个键现在就是我们自己在接：按 {chord.Text} 出我们的截图");

        return IsFree(chord)
            ? (false, $"没人注册这个键：按 {chord.Text} 就是我们的截图")
            : (true, $"这个键已被其它程序注册：按下去不会是我们的截图，建议换一个");
    }
}
