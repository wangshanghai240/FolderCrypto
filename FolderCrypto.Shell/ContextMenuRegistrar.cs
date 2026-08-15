using System.Runtime.InteropServices;

namespace FolderCrypto.Shell;

/// <summary>
/// 通过注册表注册“右键菜单”项。
/// 采用静态命令动词（而非 COM IContextMenu），启动主应用并传入参数，
/// 简单可靠，无进程内 COM 兼容性问题。
///
/// 注册结构：
///   HKCU\Software\Classes\*\shell\FolderCryptoEncrypt\command = "app.exe" encrypt "%1"
///   HKCU\Software\Classes\*\shell\FolderCryptoDecrypt\command = "app.exe" decrypt "%1"
///   HKCU\Software\Classes\Directory\shell\FolderCryptoEncrypt\command = ... (文件夹)
///   以及 Directory\shell\FolderCryptoDecrypt
/// </summary>
public static class ContextMenuRegistrar
{
    public const string EncryptVerb = "FolderCryptoEncrypt";
    public const string DecryptVerb = "FolderCryptoDecrypt";

    // 右键菜单状态处理器 CLSID（原生 DLL 实现 IExplorerCommandState）
    private const string EncryptStateHandlerClsid = "{F8A2B000-1234-4A5B-9C6D-7E8F9A0B1C2D}"; // 未加密时显示“加密”
    private const string DecryptStateHandlerClsid = "{F8A2C100-1234-4A5B-9C6D-7E8F9A0B1C2D}"; // 已加密时显示“解密”

    /// <summary>安装右键菜单。exePath 为主应用的完整路径；iconDir 为图标所在目录（含 overlay-lock.ico 与 unlock.ico）。</summary>
    public static void Install(string exePath, string iconDir)
    {
        string encryptIcon = Path.Combine(iconDir, "overlay-lock.ico");
        string decryptIcon = Path.Combine(iconDir, "unlock.ico");

        // 文件
        RegisterVerb(@"*\shell", EncryptVerb, "加密", exePath, "encrypt", EncryptStateHandlerClsid, encryptIcon);
        RegisterVerb(@"*\shell", DecryptVerb, "解密", exePath, "decrypt", DecryptStateHandlerClsid, decryptIcon);
        // 文件夹
        RegisterVerb(@"Directory\shell", EncryptVerb, "加密", exePath, "encrypt", EncryptStateHandlerClsid, encryptIcon);
        RegisterVerb(@"Directory\shell", DecryptVerb, "解密", exePath, "decrypt", DecryptStateHandlerClsid, decryptIcon);
        // 文件夹空白处（仅加密选中项对话框，暂不做状态判断）
        RegisterVerb(@"Directory\Background\shell", EncryptVerb, "加密选中", exePath, "encrypt-here", iconPath: encryptIcon);
    }

    /// <summary>卸载右键菜单。</summary>
    public static void Uninstall()
    {
        DeleteVerb(@"*\shell", EncryptVerb);
        DeleteVerb(@"*\shell", DecryptVerb);
        DeleteVerb(@"Directory\shell", EncryptVerb);
        DeleteVerb(@"Directory\shell", DecryptVerb);
        DeleteVerb(@"Directory\Background\shell", EncryptVerb);
    }

    private static void RegisterVerb(string shellPath, string verb, string label, string exePath, string arg, string? stateHandlerClsid = null, string? iconPath = null)
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

            // 状态处理器：右键时按“是否已加密”动态显示/隐藏本项
            if (!string.IsNullOrEmpty(stateHandlerClsid))
                key.SetValue("CommandStateHandler", stateHandlerClsid);
        }

        using (var commandKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{rel}\command"))
        {
            commandKey.SetValue(null, $@"""{exePath}"" {arg} ""%1""");
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
