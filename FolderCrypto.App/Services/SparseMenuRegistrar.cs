using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace FolderCrypto.App.Services;

/// <summary>
/// 一级右键菜单「自愈」注册：确保**当前用户**已注册 Sparse MSIX 包。
///
/// 作用：弥补「跨账户提权安装」场景——当由标准用户 + 单独管理员凭据安装时，
/// 提权后的 msiexec 把 HKCU 写到管理员账户，安装器无法为真正的交互用户注册
/// sparse 包；此时真正的用户在首次运行本应用时，由这里为当前用户补注册。
///
/// 设计要点：
///  * 仅在后台线程调用（会起一个短命的 powershell，不能阻塞 UI/启动）。
///  * 幂等、best-effort：已注册则跳过；任何异常都吞掉，不影响应用运行。
///  * 证书信任落在 CurrentUser（无需管理员）。
/// </summary>
public static class SparseMenuRegistrar
{
    private const string PackageName = "FolderCrypto.SparseMenu";

    /// <summary>尽力确保当前用户已注册一级菜单 sparse 包。</summary>
    public static void EnsureRegistered()
    {
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd('\\');
            var sparseDir = Path.Combine(dir, "sparse");
            if (!Directory.Exists(sparseDir))
                return;

            var msix = Directory.GetFiles(sparseDir, "*.msix").FirstOrDefault();
            if (msix is null || !File.Exists(msix))
                return;

            var cer = Path.Combine(sparseDir, "FolderCrypto.cer");
            if (File.Exists(cer))
                TrustCertificateForCurrentUser(cer);

            // 仅当当前用户尚未注册时才添加（升级由安装器负责 remove+add）。
            var script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                $"if (-not (Get-AppxPackage -Name '{PackageName}')) {{ " +
                $"Add-AppxPackage -Path '{Esc(msix)}' -ExternalLocation '{Esc(dir)}' -ErrorAction SilentlyContinue " +
                "}";

            RunPowershell(script);
        }
        catch
        {
            // 自愈失败不影响主功能
        }
    }

    private static string Esc(string s) => s.Replace("'", "''");

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
            catch { }
        }
    }

    private static void RunPowershell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + script + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            return;

        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
    }
}
