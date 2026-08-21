using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    // 文件：占用程序关闭后再多等一会确认空闲，避免误触发
    private static readonly TimeSpan FileGracePeriod = TimeSpan.FromSeconds(30);
    // 文件夹：可能含音视频等正在播放的内容（部分播放器不保持文件句柄），空闲确认窗口更长，
    // 避免正在播放时被提前重新加密
    private static readonly TimeSpan FolderGracePeriod = TimeSpan.FromMinutes(10);
    // 超时兜底：无论是否空闲，超过该时长就尝试强制重新加密
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);
    private const int PollMs = 1500;
    // 连续观察到某个进程占用 ≥3 次(约4.5秒) 才将其纳入跟踪，过滤掉杀毒/缩略图等瞬时访问
    private const int TrackThreshold = 3;

    private static readonly string ManifestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderCrypto", "temp-unlock.json");

    /// <summary>临时解密一个文件并打开；监测到文件不再被占用后自动重新加密。</summary>
    public static void TempDecryptFile(string path, string password, IProgress<int>? progress = null)
    {
        InPlaceEncryptionService.DecryptFile(path, password, null, progress);
        AddManifest(path);
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        _ = MonitorFileAsync(path, password, DateTime.UtcNow);
    }

    /// <summary>临时解密一个文件夹（解密 + 解除锁定），待文件夹内文件空闲后自动重新加密。</summary>
    public static void TempDecryptFolder(string path, string password, IProgress<int>? progress = null)
    {
        InPlaceEncryptionService.DecryptFolder(path, password, null, progress);
        AddManifest(path);
        _ = MonitorFolderAsync(path, password, DateTime.UtcNow);
    }

    // ---- 监测 ----

    private static async Task MonitorFileAsync(string path, string password, DateTime started)
    {
        var counts = new Dictionary<int, int>();
        var tracked = new HashSet<int>();
        bool wasIdle = false;
        DateTime idleSince = default;
        while (true)
        {
            await Task.Delay(PollMs);
            if (!File.Exists(path)) { RemoveManifest(path); return; }              // 已被删除
            if (InPlaceEncryptionService.IsFileEncrypted(path)) { RemoveManifest(path); return; } // 已重加密

            bool inUse = ComputeInUse(new[] { path }, counts, tracked);
            if (inUse)
            {
                wasIdle = false;
            }
            else
            {
                if (!wasIdle) { wasIdle = true; idleSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - idleSince >= FileGracePeriod)
                {
                    if (TryReencryptFile(path, password)) { RemoveManifest(path); return; }
                }
            }

            // 兜底：仅当未被占用且超时时才强制重新加密（绝不中断正在播放的内容）
            if (!inUse && DateTime.UtcNow - started >= MaxTtl)
            {
                if (TryReencryptFile(path, password)) { RemoveManifest(path); return; }
            }
        }
    }

    private static async Task MonitorFolderAsync(string path, string password, DateTime started)
    {
        var counts = new Dictionary<int, int>();
        var tracked = new HashSet<int>();
        bool wasIdle = false;
        DateTime idleSince = default;
        while (true)
        {
            await Task.Delay(PollMs);
            if (!Directory.Exists(path)) { RemoveManifest(path); return; }
            if (InPlaceEncryptionService.IsFolderEncrypted(path)) { RemoveManifest(path); return; }

            string[] files;
            try { files = Directory.GetFiles(path, "*", SearchOption.AllDirectories); }
            catch { continue; } // 枚举失败：保持解锁，下一轮再试

            bool inUse = ComputeInUse(files, counts, tracked);
            if (inUse)
            {
                wasIdle = false;
            }
            else
            {
                if (!wasIdle) { wasIdle = true; idleSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - idleSince >= FolderGracePeriod)
                {
                    if (TryReencryptFolder(path, password)) { RemoveManifest(path); return; }
                }
            }

            // 兜底：仅当未被占用且超时时才强制重新加密（绝不中断正在播放的内容）
            if (!inUse && DateTime.UtcNow - started >= MaxTtl)
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

    /// <summary>
    /// 用 Restart Manager 取得当前持有该文件的所有进程 PID。
    /// 返回 null 表示调用出错（保守处理：视为“未知/仍在占用”）。
    /// </summary>
    private static List<int>? GetUsingPids(string path)
    {
        uint session = 0;
        var key = new StringBuilder(64);
        try
        {
            if (RmStartSession(out session, 0, key) != 0) return null;
            string[] files = { path };
            if (RmRegisterResources(session, 1, files, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return null;

            uint needed = 0, count = 0, reasons = 0;
            int r = RmGetList(session, out needed, ref count, null, out reasons);
            var pids = new List<int>();
            if (r == 234) // ERROR_MORE_DATA: 有进程占用，缓冲区不足
            {
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                r = RmGetList(session, out needed, ref count, info, out reasons);
                if (r == 0)
                    for (uint i = 0; i < count; i++)
                        pids.Add(info[i].Process.dwProcessId);
                return pids;
            }
            if (r != 0) return null; // 出错：保守视为未知
            return pids;             // r==0：空列表 = 无进程占用
        }
        catch { return null; }
        finally { if (session != 0) RmEndSession(session); }
    }

    #region Restart Manager 互操作 (rstrtmgr.dll)

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint dwSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, IntPtr rgApplications, uint nServices, IntPtr rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint dwSessionHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    #endregion

    /// <summary>
    /// 聚合判断一组文件当前是否“在使用中”：只要还有进程持有句柄，或曾经持续占用（≥TrackThreshold 次）
    /// 的进程仍存活，就视为使用中。这样即使播放器读完后关闭句柄，只要它的进程还在（视频仍在播放），
    /// 也会保持解锁，直到进程退出才允许重新加密——可覆盖数小时的长视频。
    /// </summary>
    private static bool ComputeInUse(IEnumerable<string> files, Dictionary<int, int> counts, HashSet<int> tracked)
    {
        var current = new HashSet<int>();
        foreach (var f in files)
        {
            var pids = GetUsingPids(f);
            if (pids == null) return true; // RM 出错：保守视为仍被占用
            foreach (var pid in pids) current.Add(pid);
        }

        foreach (var pid in current)
        {
            counts.TryGetValue(pid, out var c);
            c++;
            counts[pid] = c;
            if (c >= TrackThreshold) tracked.Add(pid);
        }
        foreach (var pid in counts.Keys.ToList())
            if (!current.Contains(pid)) counts[pid] = 0;

        return current.Count > 0 || tracked.Any(IsProcessAlive);
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return true; }
        catch { return false; }
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
