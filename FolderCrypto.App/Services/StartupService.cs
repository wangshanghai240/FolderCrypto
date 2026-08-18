using System;
using Microsoft.Win32;

namespace FolderCrypto.App.Services;

/// <summary>
/// “跟随系统启动”服务：通过写入当前用户的
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> 键实现开机自启。
/// 选用 HKCU 而非 HKLM，与右键菜单注册方式一致，无需管理员权限，
/// 且仅影响当前用户，行为可预期、易卸载。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FolderCrypto";

    /// <summary>当前是否已启用开机自启（核对可执行路径是否一致，避免失效条目残留误判）。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                if (key?.GetValue(ValueName) is not string current) return false;
                return !string.IsNullOrEmpty(current)
                       && string.Equals(current, BuildCommandLine(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>设置开机自启。</summary>
    public static bool Enable()
        => WriteEntry(BuildCommandLine());

    /// <summary>取消开机自启。</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return false;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入开机自启注册表项。</summary>
    private static bool WriteEntry(string commandLine)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return false;
            key.SetValue(ValueName, commandLine, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 依据当前启用的状态执行设置/取消。
    /// </summary>
    public static bool SetEnabled(bool enabled)
        => enabled ? Enable() : Disable();

    /// <summary>
    /// 组装开机自启命令行：带引号的当前可执行文件路径（非打包时为 DLL 宿主场景，
    /// 但桌面应用以 EXE 运行，ProcessPath 即为可执行文件；若拿不到则回退 exe 目录探测）。
    /// 追加 <c>--autostart</c> 标志，让应用开机时以「后台托盘常驻」方式启动而不弹出主界面。
    /// </summary>
    private static string BuildCommandLine()
    {
        string? exe = null;
        try { exe = Environment.ProcessPath; } catch { }

        if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
        {
            // 回退：WinUI 应用通常以托管 EXE 启动
            var candidate = System.IO.Path.Combine(AppContext.BaseDirectory, "FolderCrypto.App.exe");
            if (System.IO.File.Exists(candidate)) exe = candidate;
        }

        if (string.IsNullOrEmpty(exe))
            exe = System.IO.Path.Combine(AppContext.BaseDirectory, "FolderCrypto.App.exe");

        return "\"" + exe + "\" --autostart";
    }
}
