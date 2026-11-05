using Microsoft.Win32;

namespace LikeShot.Shell;

/// <summary>
/// 开机自启只走 HKCU 的 Run 键一处。托盘菜单和设置窗口都调这里，
/// 两边各写一份迟早会互相打脸（一处勾了另一处不认）。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LikeShot";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return false;
            if (enable) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("开机自启设置失败", ex);
            return false;
        }
    }
}
