using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace FolderCrypto.Bootstrapper.Services;

/// <summary>
/// 安装完成后，把 Sparse MSIX 注册到“当前用户”，从而让「加密 / 解密」
/// 出现在 Windows 11 的一级右键菜单（而不是「显示更多选项」里）。
///
/// 步骤（全部 best-effort，任何失败都不影响 MSI 安装结果）：
///   1) 定位安装目录（优先用传入值，否则读 HKCU\Software\FolderCrypto\AppPath）。
///   2) 把自签名证书 <c>&lt;安装目录&gt;\sparse\FolderCrypto.cer</c> 信任到
///      当前用户的「受信任人 / 受信任的根证书颁发机构」。
///   3) Remove-AppxPackage（旧包）+ Add-AppxPackage -ExternalLocation &lt;安装目录&gt;。
///
/// 若安装目录内没有 sparse 包（例如构建时未生成），直接跳过，经典右键菜单仍然可用。
/// </summary>
public static class SparseMenuInstaller
{
    private const string PackageName = "FolderCrypto.SparseMenu";

    /// <summary>尽力注册一级菜单。返回 true 表示注册命令成功执行。</summary>
    public static bool RegisterBestEffort(string? installDir)
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(installDir)
                ? installDir!.Trim().TrimEnd('\\')
                : ReadInstallDirFromRegistry();

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;

            var sparseDir = Path.Combine(dir, "sparse");
            var msix = Directory.Exists(sparseDir)
                ? Directory.GetFiles(sparseDir, "*.msix").FirstOrDefault()
                : null;
            if (msix == null || !File.Exists(msix))
                return false; // 构建未生成 sparse 包 → 跳过

            var cer = Path.Combine(sparseDir, "FolderCrypto.cer");
            if (File.Exists(cer))
                TrustCertificateForCurrentUser(cer);

            return AddAppxPackage(msix, dir);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 MSI 写入的主程序路径，推导安装目录。</summary>
    private static string? ReadInstallDirFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\FolderCrypto");
            if (key?.GetValue("AppPath") is string exe && exe.Length > 0)
                return Path.GetDirectoryName(exe);
        }
        catch { }
        return null;
    }

    /// <summary>把证书加入当前用户的受信任存储（受信任人 + 根），否则签名包无法安装。</summary>
    private static void TrustCertificateForCurrentUser(string cerPath)
    {
        var cert = new X509Certificate2(cerPath);
        foreach (var name in new[] { StoreName.TrustedPeople, StoreName.Root })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                if (!store.Certificates.Contains(cert))
                    store.Add(cert);
                store.Close();
            }
            catch { /* 存储写入失败也不终止；可能部分地区策略限制 Root 写入 */ }
        }
    }

    /// <summary>先移除旧注册，再以外部内容目录注册 sparse 包。</summary>
    private static bool AddAppxPackage(string msixPath, string externalLocation)
    {
        string Esc(string s) => s.Replace("'", "''");

        var script =
            $"$ErrorActionPreference='SilentlyContinue'; " +
            $"Get-AppxPackage -Name '{PackageName}' | Remove-AppxPackage; " +
            $"Add-AppxPackage -Path '{Esc(msixPath)}' -ExternalLocation '{Esc(externalLocation)}' -ErrorAction Stop; " +
            $"exit 0";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            return false;

        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }
}
