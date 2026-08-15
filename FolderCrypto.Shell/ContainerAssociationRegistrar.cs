using Microsoft.Win32;

namespace FolderCrypto.Shell;

/// <summary>
/// 注册 .fenc 文件关联，使双击 .fenc 容器自动打开主应用并弹出解密密码框。
/// </summary>
public static class ContainerAssociationRegistrar
{
    private const string ProgId = "FolderCrypto.Container";

    /// <summary>安装 .fenc 关联。</summary>
    public static void Install(string exePath)
    {
        // ProgId
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            key.SetValue("", "FolderCrypto 加密容器");
        }
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
        {
            key.SetValue("", $@"""{exePath}"",0");
        }
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            key.SetValue("", $@"""{exePath}"" decrypt ""%1""");
        }

        // 扩展名映射
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.fenc"))
        {
            key.SetValue("", ProgId);
        }
    }

    /// <summary>卸载 .fenc 关联。</summary>
    public static void Uninstall()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.fenc", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false); } catch { }
    }
}
