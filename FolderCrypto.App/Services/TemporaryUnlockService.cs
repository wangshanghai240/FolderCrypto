using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FolderCrypto.Core.Services;

namespace FolderCrypto.App.Services;

/// <summary>
/// 临时解密服务：用 Windows Hello 模式存储的密码临时解密文件/文件夹，
/// 在用户用完（文件不再被占用 / 文件夹内文件空闲 / 超时兜底）后自动重新加密，
/// 避免明文长期残留。维护一份 temp-unlock.json 清单，应用启动时兜底扫描。
/// </summary>
public static class TemporaryUnlockService
{
    // 占用程序关闭后再多等几秒确认空闲，避免误触发
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);
    // 超时兜底：无论是否空闲，超过该时长就尝试强制重新加密
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);
    private const int PollMs = 1500;

    private static readonly string ManifestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderCrypto", "temp-unlock.json");

    /// <summary>临时解密一个文件并打开；监测到文件不再被占用后自动重新加密。</summary>
    public static void TempDecryptFile(string path, string password)
    {
        InPlaceEncryptionService.DecryptFile(path, password);
        AddManifest(path);
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        _ = MonitorFileAsync(path, password, DateTime.UtcNow);
    }

    /// <summary>临时解密一个文件夹（解密 + 解除锁定），待文件夹内文件空闲后自动重新加密。</summary>
    public static void TempDecryptFolder(string path, string password)
    {
        InPlaceEncryptionService.DecryptFolder(path, password);
        AddManifest(path);
        _ = MonitorFolderAsync(path, password, DateTime.UtcNow);
    }

    // ---- 监测 ----

    private static async Task MonitorFileAsync(string path, string password, DateTime started)
    {
        bool wasIdle = false;
        DateTime idleSince = default;
        while (true)
        {
            await Task.Delay(PollMs);
            if (!File.Exists(path)) { RemoveManifest(path); return; }              // 已被删除
            if (InPlaceEncryptionService.IsFileEncrypted(path)) { RemoveManifest(path); return; } // 已重加密

            bool inUse = IsFileInUse(path);
            if (inUse)
            {
                wasIdle = false;
            }
            else
            {
                if (!wasIdle) { wasIdle = true; idleSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - idleSince >= GracePeriod)
                {
                    if (TryReencryptFile(path, password)) { RemoveManifest(path); return; }
                }
            }

            if (DateTime.UtcNow - started >= MaxTtl)
            {
                if (TryReencryptFile(path, password)) { RemoveManifest(path); return; }
            }
        }
    }

    private static async Task MonitorFolderAsync(string path, string password, DateTime started)
    {
        bool wasIdle = false;
        DateTime idleSince = default;
        while (true)
        {
            await Task.Delay(PollMs);
            if (!Directory.Exists(path)) { RemoveManifest(path); return; }
            if (InPlaceEncryptionService.IsFolderEncrypted(path)) { RemoveManifest(path); return; }

            bool anyInUse = AnyFileInUse(path);
            if (anyInUse)
            {
                wasIdle = false;
            }
            else
            {
                if (!wasIdle) { wasIdle = true; idleSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - idleSince >= GracePeriod)
                {
                    if (TryReencryptFolder(path, password)) { RemoveManifest(path); return; }
                }
            }

            if (DateTime.UtcNow - started >= MaxTtl)
            {
                if (TryReencryptFolder(path, password)) { RemoveManifest(path); return; }
            }
        }
    }

    private static bool TryReencryptFile(string path, string password)
    {
        try { InPlaceEncryptionService.EncryptFile(path, password); return true; }
        catch { return false; } // 可能又被打开发送到捕获循环，继续监测
    }

    private static bool TryReencryptFolder(string path, string password)
    {
        try { InPlaceEncryptionService.EncryptFolder(path, password); return true; }
        catch { return false; }
    }

    /// <summary>用独占打开探测文件是否仍被其它进程占用。</summary>
    private static bool IsFileInUse(string path)
    {
        try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch { return true; }
    }

    private static bool AnyFileInUse(string folder)
    {
        try
        {
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                if (IsFileInUse(f)) return true;
        }
        catch { return false; }
        return false;
    }

    // ---- 清单：供启动兜底扫描 ----

    private static void AddManifest(string path)
    {
        try
        {
            var list = ReadManifest();
            if (!list.Contains(path)) list.Add(path);
            WriteManifest(list);
        }
        catch { }
    }

    private static void RemoveManifest(string path)
    {
        try
        {
            var list = ReadManifest();
            if (list.Remove(path)) WriteManifest(list);
        }
        catch { }
    }

    private static List<string> ReadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new List<string>();
            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static void WriteManifest(List<string> list)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ManifestPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(list));
        }
        catch { }
    }

    /// <summary>应用启动时扫描清单：对仍处于明文（临时解锁）且可用密码的条目自动重新加密，兜底防明文残留。</summary>
    public static void CleanupOnStartup()
    {
        var list = ReadManifest();
        if (list.Count == 0) return;

        var secret = HelloSecretStore.TryGetSecret();
        string? password = (secret != null && !HelloSecretStore.IsRecoveryKind(secret.Value.Kind)) ? secret.Value.Secret : null;

        var remaining = new List<string>();
        foreach (var path in list)
        {
            bool handled = false;
            if (File.Exists(path))
            {
                if (InPlaceEncryptionService.IsFileEncrypted(path)) handled = true;
                else if (password != null && TryReencryptFile(path, password)) handled = true;
            }
            else if (Directory.Exists(path))
            {
                if (InPlaceEncryptionService.IsFolderEncrypted(path)) handled = true;
                else if (password != null && TryReencryptFolder(path, password)) handled = true;
            }
            else
            {
                handled = true; // 路径已不存在，视为已清理
            }

            if (!handled) remaining.Add(path);
        }
        WriteManifest(remaining);
    }
}
