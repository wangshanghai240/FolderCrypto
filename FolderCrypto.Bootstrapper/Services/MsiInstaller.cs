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

    private static void ExtractMsi(string dest)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedResource)
            ?? throw new InvalidOperationException("安装包内置 MSI 资源缺失。");
        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);
    }
}
