using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace FolderCrypto.Bootstrapper.Services;

/// <summary>
/// 把内置的 MSI 解出到临时目录，并以静默方式(/qn)调用 msiexec 安装。
/// 引导程序只负责“收集安装目录 + 触发安装”，文件/右键菜单/卸载/快捷方式
/// 全部由既有 MSI 完成（与手动双击 MSI 完全等价，但 /qn 不展示 MSI 自带 UI）。
/// </summary>
public static class MsiInstaller
{
    private const string EmbeddedResource = "FolderCryptoSetup.msi";

    public sealed class InstallResult
    {
        public bool Success { get; init; }
        public int ExitCode { get; init; }
        public string Message { get; init; } = "";
        public string LogPath { get; init; } = "";
    }

    /// <summary>
    /// 静默安装内置 MSI。targetDir 为空时使用 MSI 默认目录（C:\Program Files (x86)\FolderCrypto）。
    /// 走 ShellExecute + Verb=runas，点击“安装”时触发一次 UAC 提权（perMachine 安装必需）。
    /// </summary>
    public static InstallResult Run(string? targetDir)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "FolderCryptoSetup");
        Directory.CreateDirectory(tempRoot);
        var msiPath = Path.Combine(tempRoot, "FolderCrypto-Setup.msi");
        var logPath = Path.Combine(tempRoot, "setup.log");
        if (File.Exists(logPath))
            File.Delete(logPath);

        ExtractMsi(msiPath);

        // 安装/升级前先彻底清理旧版：
        //  - 结束正在运行的 FolderCrypto.App（避免 exe 被占用）
        //  - 注销 shell 集成并重启 explorer（释放被 explorer 常驻加载的 ShellNative.dll）
        //  - 提权清理孤儿 ARP 卸载项、残留注册表与旧安装目录
        // 否则 RemoveExistingProducts 常因文件被占用而失败，导致旧版残留在“程序和功能”/注册表。
        CleanUpLegacyInstall();

        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            UseShellExecute = true,   // 通过 ShellExecute 触发 UAC 提权
            Verb = "runas",           // 强制管理员权限（perMachine 安装必需）
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        var args = $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"";
        if (!string.IsNullOrWhiteSpace(targetDir))
            args += $" INSTALLFOLDER=\"{targetDir.Trim().TrimEnd('\\')}\"";
        psi.Arguments = args;

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return new InstallResult
            {
                Success = false,
                ExitCode = -1,
                Message = "无法启动 Windows Installer。",
                LogPath = logPath,
            };
        }

        proc.WaitForExit();
        var code = proc.ExitCode;

        return code switch
        {
            0 => new InstallResult { Success = true, ExitCode = code, Message = "FolderCrypto 已成功安装。", LogPath = logPath },
            3010 => new InstallResult { Success = true, ExitCode = code, Message = "安装完成，需要重启计算机才能生效。", LogPath = logPath },
            1602 => new InstallResult { Success = false, ExitCode = code, Message = "安装已取消（未授予管理员权限）。", LogPath = logPath },
            _ => new InstallResult { Success = false, ExitCode = code, Message = $"安装失败（错误码 {code}）。详细日志：{logPath}", LogPath = logPath },
        };
    }

    /// <summary>结束正在运行的 FolderCrypto.App 进程并等待其退出（升级/安装时需替换其 exe 文件）。</summary>
    private static void StopRunningApp()
    {
        foreach (var p in Process.GetProcessesByName("FolderCrypto.App"))
        {
            try { p.Kill(); } catch { }
        }

        // 等待进程真正退出，确保文件句柄已释放，避免 MSI 替换失败
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("FolderCrypto.App").Length == 0)
                return;
            System.Threading.Thread.Sleep(200);
        }
    }

    /// <summary>
    /// 安装/升级前彻底清理旧版残留：
    /// 结束运行中的应用 → 注销 HKCU 右键菜单 → 提权清理 HKLM 覆盖层/孤儿 ARP/旧目录并重启 explorer。
    /// 否则 RemoveExistingProducts 常因 ShellNative.dll 被 explorer 占用而失败，导致旧版残留在
    /// “程序和功能”与注册表。
    /// </summary>
    private static void CleanUpLegacyInstall()
    {
        try { StopRunningApp(); } catch { }

        // 注销 HKCU 右键菜单与应用设置键（无需管理员；新版本安装时会重新注册）
        try
        {
            foreach (var sub in new[]
            {
                @"Software\Classes\*\shell\FolderCryptoEncrypt",
                @"Software\Classes\*\shell\FolderCryptoDecrypt",
                @"Software\Classes\Directory\shell\FolderCryptoEncrypt",
                @"Software\Classes\Directory\shell\FolderCryptoDecrypt",
                @"Software\Classes\Directory\Background\shell\FolderCryptoEncrypt",
            })
            {
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
            }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\FolderCrypto", throwOnMissingSubKey: false); } catch { }
        }
        catch { }

        // 仅当检测到旧版残留时才提权清理（避免全新安装时无谓地弹 UAC/重启 explorer）
        if (HasLegacyInstall())
            TryElevatedCleanup();
    }

    /// <summary>是否存在需要清理的旧版残留（旧 ARP 卸载项 / 旧安装目录 / 覆盖层注册）。</summary>
    private static bool HasLegacyInstall()
    {
        try
        {
            foreach (var rootKey in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(rootKey);
                if (hklm != null)
                {
                    foreach (var name in hklm.GetSubKeyNames())
                    {
                        using var sub = hklm.OpenSubKey(name);
                        if (sub?.GetValue("DisplayName") is string dn
                            && dn.IndexOf("FolderCrypto", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            using (var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (hkcu != null)
                {
                    foreach (var name in hkcu.GetSubKeyNames())
                    {
                        using var sub = hkcu.OpenSubKey(name);
                        if (sub?.GetValue("DisplayName") is string dn
                            && dn.IndexOf("FolderCrypto", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            if (File.Exists(@"D:\FolderCrypto\FolderCrypto.App.exe")) return true;
            using (var overlay = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers"))
            {
                if (overlay != null)
                    foreach (var n in overlay.GetSubKeyNames())
                        if (n.IndexOf("FolderCrypto", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>以管理员权限执行旧版清理（删除覆盖层注册、孤儿 ARP 卸载项、旧安装目录，并重启 explorer 释放 DLL）。</summary>
    private static void TryElevatedCleanup()
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "FolderCryptoSetup");
        string tmp = Path.Combine(tmpDir, "cleanup.ps1");
        try
        {
            Directory.CreateDirectory(tmpDir);
            File.WriteAllText(tmp, BuildCleanupScript(), new System.Text.UTF8Encoding(true));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tmp}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p != null && !p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch
        {
            // 用户取消提权或执行失败：不阻断安装（MajorUpgrade 仍会尽力处理已注册的旧产品）
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>生成提权清理脚本：删除 FolderCrypto 孤儿 ARP、覆盖层注册与旧安装目录，并重启 explorer。</summary>
    private static string BuildCleanupScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'
# 1) 收集并删除 FolderCrypto 的“孤儿”ARP 卸载项（未真正注册为 MSI 产品的残留）
$paths = @(
  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
foreach ($p in $paths) {
  Get-ItemProperty $p -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'FolderCrypto|文件夹加密' } | ForEach-Object {
    $code = $_.PSChildName
    $rev = ($code -replace '[{}]','' -replace '-','')
    if ($rev.Length -eq 32) {
      $regKey = 'HKLM:\Software\Classes\Installer\Products\' + ($rev.Substring(6,2)+$rev.Substring(4,2)+$rev.Substring(2,2)+$rev.Substring(0,2)+$rev.Substring(10,2)+$rev.Substring(8,2)+$rev.Substring(14,2)+$rev.Substring(12,2)+$rev.Substring(18,2)+$rev.Substring(16,2)+$rev.Substring(20,12))
      # 仅清理“未注册”的孤儿项；已注册的旧产品由 MSI 的 RemoveExistingProducts 处理
      if (-not (Test-Path $regKey)) {
        Remove-Item ('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $code) -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item ('HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\' + $code) -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item ('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $code) -Recurse -Force -ErrorAction SilentlyContinue
      }
    }
  }
}
# 2) 删除覆盖层注册（旧版 ShellNative.dll 被 explorer 加载的根源）
Remove-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\  FolderCryptoLock' -Recurse -Force -ErrorAction SilentlyContinue
# 3) 重启 explorer，释放被其加载的 FolderCrypto.ShellNative.dll
Get-Process explorer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
# 4) 删除已知旧安装目录（仅当其中确有 FolderCrypto.App.exe，视为程序目录才删）
foreach ($d in @('D:\FolderCrypto')) {
  if ((Test-Path $d) -and (Test-Path (Join-Path $d 'FolderCrypto.App.exe'))) {
    Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
  }
}
exit 0
";
    }

    private static void ExtractMsi(string dest)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedResource)
            ?? throw new InvalidOperationException("安装包内置 MSI 资源缺失。");
        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);
    }
}
