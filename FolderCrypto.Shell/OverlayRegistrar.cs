using System.Diagnostics;
using Microsoft.Win32;

namespace FolderCrypto.Shell;

/// <summary>
/// 负责“锁图标”覆盖层的注册（原生 C++ ATL DLL 版本）。
///
/// 本版本使用 <c>FolderCrypto.ShellNative.dll</c>（原生 ATL 实现），
/// 通过 regsvr32 调用其 DllRegisterServer/DllUnregisterServer 完成 COM 类注册，
/// 然后我们在 HKLM 的 ShellIconOverlayIdentifiers 登记覆盖层。
///
/// 原生实现由 Explorer 直接进程内加载，兼容性与稳定性远优于托管 COM 版本。
/// </summary>
public static class OverlayRegistrar
{
    public const string OverlayKeyName = "  FolderCryptoLock"; // 前导空格确保排序靠前

    /// <summary>原生 ATL DLL 的文件名。</summary>
    public const string NativeDllName = "FolderCrypto.ShellNative.dll";

    /// <summary>覆盖层图标文件名（与原生 DLL 同目录）。</summary>
    public const string OverlayIconName = "overlay-lock.ico";

    /// <summary>与原生 ATL 实现一致的 CLSID。</summary>
    public const string Clsid = "F8A2C000-1234-4A5B-9C6D-7E8F9A0B1C2D";

    /// <summary>使用 regsvr32 注册/注销原生 DLL，并登记 ShellIconOverlayIdentifiers。</summary>
    /// <param name="nativeDllPath">FolderCrypto.ShellNative.dll 的完整路径。</param>
    /// <param name="adminAccessToHklm">是否具备 HKLM 写入权限。</param>
    public static void InstallOverlay(string nativeDllPath, bool adminAccessToHklm)
    {
        RegisterNativeDll(nativeDllPath, register: true);
        RegisterOverlayRegistration(adminAccessToHklm);
    }

    public static void UninstallOverlay(string? nativeDllPath = null)
    {
        if (!string.IsNullOrEmpty(nativeDllPath) && File.Exists(nativeDllPath))
        {
            RegisterNativeDll(nativeDllPath, register: false);
        }

        // 移除 HKLM 覆盖层登记（尽力而为）
        try
        {
            using RegistryKey? hklm = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", writable: true);
            hklm?.DeleteSubKeyTree(OverlayKeyName, false);
            // 兼容清理：旧版曾误注册为“FolderCryptoLock”（无前导空格）
            hklm?.DeleteSubKeyTree("FolderCryptoLock", false);
        }
        catch { }
    }

    private static void RegisterOverlayRegistration(bool adminAccessToHklm)
    {
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", writable: true)
                ?? Registry.LocalMachine.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers");

            // 注意：使用带前导空格的完整键名（OverlayKeyName），
            // 空格(0x20)排序在最前，确保覆盖层必然排在 Windows 显示的前 15 名之内。
            using RegistryKey sub = root.CreateSubKey(OverlayKeyName);
            sub.SetValue("", Clsid);

            // 兼容清理：删除旧版误注册的无前导空格键，避免两个同名覆盖层互相干扰导致图标不显示。
            root.DeleteSubKeyTree("FolderCryptoLock", false);
        }
        catch
        {
            // 无 HKLM 权限时（非管理员），提示以管理员安装。
        }
    }

    private static void RegisterNativeDll(string dllPath, bool register)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("未找到原生 ATL DLL：" + dllPath, dllPath);

        string regsvr = Path.Combine(Environment.SystemDirectory, "regsvr32.exe");
        var psi = new ProcessStartInfo
        {
            FileName = regsvr,
            Arguments = (register ? "" : "/u ") + $"\"{dllPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 regsvr32。");
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"regsvr32 {(register ? "注册" : "注销")} 原生 DLL 失败（退出码 {proc.ExitCode}）。");
        }
    }
}
