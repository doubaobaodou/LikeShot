using System.Diagnostics;
using System.Runtime.InteropServices;
using LikeShot.Native;

namespace LikeShot.Hotkeys;

/// <summary>
/// 全局低级键盘钩子。用它而不是 RegisterHotKey，是因为低级钩子不占用热键注册名额：
/// 我们既看得见 Ctrl+Alt+A，又不会让 QQ 登录时注册失败。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>返回 true 表示这次按键被我们吃掉，不再往下传。</summary>
    public delegate bool KeyFilter(in KeyPacket packet);

    public readonly record struct KeyPacket(uint Vk, bool IsKeyDown, bool Alt, bool Control, bool Shift, bool Win);

    private readonly Win32.LowLevelKeyboardProc _proc;
    private nint _handle;

    public KeyboardHook(KeyFilter filter) => (_proc, Filter) = (HookProc, filter);

    public KeyFilter Filter { get; }

    public bool Install()
    {
        if (_handle != 0) return true;
        // 低级键盘钩子是全局的，hMod 允许为 0，线程号 0 表示挂到所有线程。
        _handle = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, nint.Zero, 0);
        return _handle != 0;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            bool isUp = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var packet = new KeyPacket(
                    data.vkCode,
                    isDown,
                    // Alt 不能只看钩子标志：注入的按键（自检）和个别键盘/输入法状态下
                    // LLKHF_ALTDOWN 会漏，Ctrl/Shift 又是靠 GetAsyncKeyState 读的，两边取或才一致。
                    Alt: (data.flags & Win32.LLKHF_ALTDOWN) != 0 || IsDown(VK_MENU),
                    Control: IsDown(VK_CONTROL),
                    Shift: IsDown(VK_SHIFT),
                    Win: IsDown(VK_LWIN) || IsDown(VK_RWIN));

                if (Filter(packet)) return 1;
            }
        }

        return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    public void Dispose()
    {
        if (_handle != 0) Win32.UnhookWindowsHookEx(_handle);
        _handle = 0;
    }
}
