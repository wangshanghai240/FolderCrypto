using System.Runtime.InteropServices;

namespace FolderCrypto.Shell;

/// <summary>
/// 通过注册表注册“右键菜单”项。
/// 采用静态命令动词（而非 COM IContextMenu），启动主应用并传入参数，
/// 简单可靠，无进程内 COM 兼容性问题。
///
/// 注册结构（v1.1.6+ 单一共享菜单项）：
///   HKCU\Software\Classes\*\shell\FolderCryptoToggle         \command = "app.exe" toggle "%1"
///   HKCU\Software\Classes\Directory\shell\FolderCryptoToggle \command = "app.exe" toggle "%1"
///   HKCU\Software\Classes\Directory\Background\shell\FolderCryptoEncrypt\command = "app.exe" encrypt-here "%1"
///
/// 为什么不再用「加密 + 解密」两个独立项：
///   部分第三方文件资源管理器（如 Pulse）只枚举静态 \shell\verb，
///   不支持 IExplorerCommand 动态显隐，也不支持 AppliesTo/SubCommands。
///   两个静态项必然同时出现且无法靠 COM 状态区分。
///   改为单一菜单项 + 程序内状态判定（toggle），在所有外壳中表现一致。
/// </summary>
public static class ContextMenuRegistrar
{
    /// <summary>共享菜单项动词（加密/解密二合一）。</summary>
    public const string ToggleVerb = "FolderCryptoToggle";

    /// <summary>历史遗留动词（v1.1.5 及更早），用于升级/卸载时清理。</summary>
    public const string LegacyEncryptVerb = "FolderCryptoEncrypt";
    public const string LegacyDecryptVerb = "FolderCryptoDecrypt";

    /// <summary>安装右键菜单。exePath 为主应用的完整路径；iconDir 为图标所在目录
    /// （含 overlay-lock.ico、unlock.ico 与 toggle-key.ico）。</summary>
    public static void Install(string exePath, string iconDir)
    {
        string lockIcon = Path.Combine(iconDir, "overlay-lock.ico");
        // 共享菜单项使用中性钥匙图标：合并后图标静态指定，
        // 无法随目标状态变化，用红锁会误导用户以为「一定是加密」。
        string toggleIcon = Path.Combine(iconDir, "toggle-key.ico");
        if (!File.Exists(toggleIcon)) toggleIcon = lockIcon;   // 兼容旧安装目录

        // 先清理旧版遗留的双菜单项，避免升级后出现重复/失效项
        UninstallLegacy();

        // 文件与文件夹：单一共享菜单项，程序自行判定加密还是解密
        RegisterVerb(@"*\shell", ToggleVerb, "加密 / 解密", exePath, "toggle", iconPath: toggleIcon);
        RegisterVerb(@"Directory\shell", ToggleVerb, "加密 / 解密", exePath, "toggle", iconPath: toggleIcon);

        // 文件夹空白处：加密选中项（交由主窗口选择）
        RegisterVerb(@"Directory\Background\shell", LegacyEncryptVerb, "加密选中", exePath, "encrypt-here", iconPath: lockIcon);
    }

    /// <summary>卸载右键菜单。</summary>
    public static void Uninstall()
    {
        DeleteVerb(@"*\shell", ToggleVerb);
        DeleteVerb(@"Directory\shell", ToggleVerb);
        DeleteVerb(@"Directory\Background\shell", LegacyEncryptVerb);
        UninstallLegacy();
    }

    /// <summary>清理 v1.1.5 及更早版本遗留的「加密」「解密」独立菜单项。</summary>
    private static void UninstallLegacy()
    {
        DeleteVerb(@"*\shell", LegacyEncryptVerb);
        DeleteVerb(@"*\shell", LegacyDecryptVerb);
        DeleteVerb(@"Directory\shell", LegacyEncryptVerb);
        DeleteVerb(@"Directory\shell", LegacyDecryptVerb);
    }

    private static void RegisterVerb(string shellPath, string verb, string label, string exePath, string arg, string? handlerClsid = null, string? iconPath = null)
    {
        // 写入 HKCU\Software\Classes，无需管理员权限；资源管理器会读取该位置的类注册。
        string rel = $@"Software\Classes\{shellPath}\{verb}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(rel))
        {
            // 菜单显示名称
            key.SetValue(null, label);
            key.SetValue("MUIVerb", label);

            // 菜单图标
            if (!string.IsNullOrEmpty(iconPath))
                key.SetValue("Icon", iconPath);

            if (!string.IsNullOrEmpty(handlerClsid))
            {
                // 动态状态：ExplorerCommandHandler（原生 IExplorerCommand，Win10/11 均可靠）。
                // 清理旧机制遗留的 CommandStateHandler（旧版在 Win10 下会显示为灰色不可用）。
                key.DeleteValue("CommandStateHandler", throwOnMissingValue: false);
                key.SetValue("ExplorerCommandHandler", handlerClsid);
            }
        }

        // ===================================================================
        // 静态 \command 兜底（所有菜单项都要写，包括带 ExplorerCommandHandler 的）
        //
        // 原因：部分第三方文件资源管理器忽略 ExplorerCommandHandler，
        // 只认静态 \command。缺少它时「加密」「解密」会同时出现且点击无反应，
        // 外壳甚至会回退成「打开文件夹」并报 1155（无文件关联）。
        //
        // Explorer 会优先用 ExplorerCommandHandler 做动态显隐；
        // 忽略它的外壳则回退到这里直接启动主程序，两者可以并存。
        // ===================================================================
        using (var commandKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{rel}\command"))
        {
            commandKey.SetValue(null, $@"""{exePath}"" {arg} ""%1""");
        }

        // 记录主程序路径，供原生 IExplorerCommand 的 Invoke 启动应用。
        if (!string.IsNullOrEmpty(handlerClsid))
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\FolderCrypto");
            root.SetValue("AppPath", exePath);
        }
    }

    private static void DeleteVerb(string shellPath, string verb)
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\{shellPath}\{verb}", throwOnMissingSubKey: false);
        }
        catch
        {
            // 键不存在等情况忽略
        }
    }

    /// <summary>通知 Explorer 刷新菜单缓存。</summary>
    public static void RefreshShell()
    {
        // 广播设置变更，让 Explorer 重新读取菜单/图标
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SETTINGCHANGE = 0x001A;
        _ = SHChangeNotify(0x8000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        _ = SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero,
                               SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);
    }

    [DllImport("shell32.dll")]
    private static extern int SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0000,
        SMTO_ABORTIFHUNG = 0x0002,
    }
}
