using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
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
    private static readonly TimeSpan FileGracePeriod = TimeSpan.FromSeconds(20);
    // 文件夹：
    private static readonly TimeSpan FolderGracePeriod = TimeSpan.FromSeconds(20);
    // 超时兜底：仅当未被占用且超时时才强制重新加密（绝不中断正在播放的内容）
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);
    private const int PollMs = 1500;

    /// <summary>
    /// 判定「空闲」所需的最少**连续**轮询次数（辅助信号，非充分条件）。
    /// 保留作为廉价的前置过滤，真正的判据见 <see cref="MinIdleAgeSinceAccess"/>。
    /// </summary>
    private const int RequiredConsecutiveIdlePolls = 3;   // 3 × 1.5s ≈ 4.5 秒

    /// <summary>
    /// 【核心判据】文件的最后访问时间需要「静止」多久，才认为确实没人再用它。
    ///
    /// ⚠️ 为什么不能只看「当前句柄」：
    /// 播放器采用大块读取 + 缓冲播放（读几十毫秒 → 关句柄 → 播几秒 → 再读）。
    /// 轮询间隔 1.5 秒远大于读取时长，因此轮询几乎总落在「不读」的间隙里，
    /// 文件被误判为空闲并重新加密 → 原文件被替换为密文 → 播放中断。
    ///
    /// ⚠️ 但也不能只看 LastAccessTime「最近是否变过」：
    /// Windows Defender（MsMpEng）与搜索索引器（SearchIndexer）会**持续读取**文件，
    /// 尤其是刚被创建/改写的文件（我们的解密恰好就是重建文件）。
    /// 实测：一个刚解密的文件，在【没有任何播放器】的情况下，
    /// LastAccessTime 每 1~2 秒就被刷新一次 → 「近来被访问」永远成立
    /// → 文件永远不会被重新加密（明文长期残留，与要解决的问题正好相反）。
    ///
    /// 因此判据改为：**最后访问时间在过去 N 秒内【完全静止不动】**。
    /// 播放时会持续读取，时间戳不断前移；Defender/索引器扫描完成后会停止，
    /// 之后时间戳静止，即可安全加密。
    /// </summary>
    private static readonly TimeSpan AccessMustBeStillFor = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 单个文件在「判定空闲后」到「真正加密前」的最短确认时间（叠加在句柄/访问判据之上）。
    /// </summary>
    private static readonly TimeSpan IdleConfirmWindow = TimeSpan.FromSeconds(9);

    /// <summary>
    /// 取文件的最后访问时间（UTC）；不可用时返回 null。
    /// </summary>
    private static DateTime? TryGetLastAccessTime(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return info.LastAccessTimeUtc;
        }
        catch { return null; }
    }

    /// <summary>
    /// 各路径「最后观察到的访问时间」及其「静止起点」。
    /// 用于判断访问时间是否已连续静止足够久。
    /// </summary>
    private sealed class AccessWatch
    {
        public DateTime LastSeen;
        public DateTime StillSince;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AccessWatch> AccessWatches
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 文件是否仍处于「被读取中」——判据是**最后访问时间尚未静止足够久**。
    ///
    /// 与「最近是否被访问过」不同：Defender / 搜索索引器会在解密后持续读取文件一段时间，
    /// 若只看「最近访问过」，这些后台扫描会让文件永远无法被加密。
    /// 改为要求时间戳**连续静止**达到 <see cref="AccessMustBeStillFor"/>，
    /// 后台扫描结束、时间戳不再变化后，即可正常加密。
    ///
    /// 若系统禁用了 last-access 更新，时间戳从一开始就静止，
    /// 会在经过静止窗口后放行（此时仍由句柄检测与连续空闲兜底）。
    /// </summary>
    private static bool IsAccessStillActive(string path)
    {
        var last = TryGetLastAccessTime(path);
        if (last == null) return false;   // 取不到：不据此阻挠

        var now = DateTime.UtcNow;
        var watch = AccessWatches.GetOrAdd(path, _ => new AccessWatch { LastSeen = last.Value, StillSince = now });

        if (last.Value != watch.LastSeen)
        {
            // 时间戳又前移了 → 仍在被读取，静止计时重新开始
            watch.LastSeen = last.Value;
            watch.StillSince = now;
            return true;
        }

        // 时间戳未变 → 观察它已静止多久
        return now - watch.StillSince < AccessMustBeStillFor;
    }

    /// <summary>路径不再需要监测时清理其观察状态，避免长期累积。</summary>
    private static void ClearAccessWatch(string path) => AccessWatches.TryRemove(path, out _);

    /// <summary>
    /// 被监测路径在「释放句柄后」的额外保护期。
    ///
    /// ⚠️ 这是修复「记事本类程序导致明文被误加密 / 文件损坏」的关键：
    /// Restart Manager 只能看到「当前仍持有文件句柄」的进程。而记事本、部分图片/PDF 阅读器
    /// 是「一次性读入内存后立刻释放句柄」的——此时 RM 报告“无占用”，但用户其实仍在查看内容，
    /// 若马上重新加密，用户随后保存就会把明文写到密文上，造成文件损坏。
    /// 因此在「检测到曾有过占用、随后变为空闲」时，额外等待这段时间再加密。
    /// </summary>
    private static readonly TimeSpan HandleReleaseProtection = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 监测循环的硬性存活上限。超过此后即便文件仍被占用也会停止监测，
    /// 避免「被长期占用的文件」导致监测协程永久存活、密码常驻内存。
    /// 退出时会**把该路径从清单中移除并提示**，不再静默留待下次启动。
    /// </summary>
    private static readonly TimeSpan MonitorHardLimit = TimeSpan.FromHours(8);

    /// <summary>
    /// 每个被监测路径对应的取消源：重复打开同一路径时先取消旧循环，
    /// 避免产生多个并行的监测协程（重复重加密竞争）。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        Monitors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清单读写的互斥锁：AddManifest / RemoveManifest 是「读-改-写」，必须串行化，
    /// 否则监测协程与 UI 线程并发调用会互相覆盖，导致条目丢失、启动兜底失效。</summary>
    private static readonly object ManifestLock = new();

    private static readonly string ManifestPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderCrypto", "temp-unlock.json");

    /// <summary>写诊断日志到 %LOCALAPPDATA%\FolderCrypto\debug.log（前缀 [TempUnlock]）。</summary>
    private static void Log(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderCrypto");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} [TempUnlock] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>临时解密一个文件并打开；监测到文件不再被占用后自动重新加密。</summary>
    public static void TempDecryptFile(string path, string password, IProgress<int>? progress = null)
    {
        InPlaceEncryptionService.DecryptFile(path, password, null, progress);
        AddManifest(path);
        Log($"临时解密(文件)完成，开始监测: {path}");
        // 记住本次临时解锁所用的密码，供退出时立即重新加密使用。
        // （凭据本身也存在密码管理器里，但那需要 Windows Hello 才能取；
        //   退出时是无人值守流程，必须用内存中的副本。）
        RememberSecret(path, password);
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        StartMonitor(path, ct => MonitorFileAsync(path, password, DateTime.UtcNow, ct));
    }

    /// <summary>临时解密一个文件夹（解密 + 解除锁定），待文件夹内文件空闲后自动重新加密。</summary>
    public static void TempDecryptFolder(string path, string password, IProgress<int>? progress = null)
    {
        InPlaceEncryptionService.DecryptFolder(path, password, null, progress);
        AddManifest(path);
        Log($"临时解密(文件夹)完成，开始监测: {path}");
        RememberSecret(path, password);
        StartMonitor(path, ct => MonitorFolderAsync(path, password, DateTime.UtcNow, ct));
    }

    /// <summary>路径 → 本次临时解锁所用的密码（仅存于内存，进程退出即消失）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SessionSecrets
        = new(StringComparer.OrdinalIgnoreCase);

    private static void RememberSecret(string path, string password) => SessionSecrets[path] = password;

    private static void ForgetSecret(string path) => SessionSecrets.TryRemove(path, out _);

    /// <summary>
    /// 应用退出前调用：把所有仍在清单中的临时解锁项**立即重新加密**。
    ///
    /// ⚠️ 这是修复「退出程序后明文永久残留」的关键。
    /// 监测循环是内存中的 Task，依附于进程；用户一关托盘程序，监测就消失，
    /// 明文再也不会被自动加密（只能等下次启动兜底，而用户可能很久不启动）。
    /// 因此在退出路径上主动收尾。
    ///
    /// 说明：此处**不再等待宽限期/保护期**——退出是用户明确表达「我用完了」，
    /// 此时应立即恢复加密，而不是把明文留在磁盘上。
    /// 若某个文件此刻仍被占用（如播放器还开着），加密会失败，该条目会保留在清单中，
    /// 交由下次启动兜底，不会被误删。
    /// </summary>
    /// <param name="timeout">整体超时，避免大文件加密让退出卡住过久。</param>
    /// <returns>成功重新加密的数量。</returns>
    public static int ReencryptAllOnExit(TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(20);
        var deadline = DateTime.UtcNow + budget;
        int done = 0;

        List<string> list;
        lock (ManifestLock) { list = ReadManifestUnlocked(); }
        if (list.Count == 0) return 0;

        // 先停掉所有监测循环，避免与本次收尾操作竞争同一文件
        foreach (var kv in Monitors.ToArray())
        {
            try { kv.Value.Cancel(); } catch { }
        }

        var remaining = new List<string>();
        foreach (var path in list)
        {
            if (DateTime.UtcNow >= deadline) { remaining.Add(path); continue; }

            // 取本次会话记录的密码；没有（例如换成新进程）则用凭据管理器里的
            string? pwd = SessionSecrets.TryGetValue(path, out var s) ? s : TryGetStoredPassword();

            bool handled = false;
            try
            {
                if (File.Exists(path))
                {
                    if (InPlaceEncryptionService.IsFileEncrypted(path)) handled = true;
                    else if (pwd != null && !IsPathInUse(path)) handled = TryReencryptFile(path, pwd);
                }
                else if (Directory.Exists(path))
                {
                    if (InPlaceEncryptionService.IsFolderEncrypted(path)) handled = true;
                    else if (pwd != null && !IsPathInUse(path)) handled = TryReencryptFolder(path, pwd);
                }
                else
                {
                    handled = true; // 路径已不存在
                }
            }
            catch { }

            if (handled) { done++; ForgetSecret(path); }
            else remaining.Add(path);
        }

        lock (ManifestLock) { WriteManifestUnlocked(remaining); }
        return done;
    }

    /// <summary>
    /// 路径是否仍在使用中（退出收尾用）。
    /// 文件：独占打开失败 或 近来仍被读取 → 视为在用。
    /// 文件夹：内部有文件被占用或被读取 → 视为在用。
    ///
    /// ⚠️ 必须包含「近来仍被读取」这一条：否则播放器处于读取间隙时，
    /// 退出收尾会误认为用完而立即加密，把正在播放的视频打断。
    /// </summary>
    private static bool IsPathInUse(string path)
    {
        if (File.Exists(path))
        {
            if (IsAccessStillActive(path)) return true;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;   // 能独占打开 → 无占用
            }
            catch { return true; }
        }

        if (Directory.Exists(path))
        {
            try
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    if (IsAccessStillActive(f)) return true;
                }
                return ComputeInUse(files);
            }
            catch { return true; }
        }
        return false;
    }

    /// <summary>从凭据管理器取回可用于加密的密码（恢复码不可用于加密）。</summary>
    private static string? TryGetStoredPassword()
    {
        var secret = HelloSecretStore.TryGetSecret();
        if (secret == null) return null;
        return HelloSecretStore.IsRecoveryKind(secret.Value.Kind) ? null : secret.Value.Secret;
    }

    /// <summary>启动（并替换）某路径的监测循环，确保同一路径只有一个监测在跑。</summary>
    private static void StartMonitor(string path, Func<CancellationToken, Task> body)
    {
        var cts = new CancellationTokenSource();
        // 若已有监测在跑，先取消它，避免重复重加密
        if (Monitors.TryGetValue(path, out var existing))
        {
            try { existing.Cancel(); } catch { }
        }
        Monitors[path] = cts;

        _ = body(cts.Token).ContinueWith(_ =>
        {
            // 循环结束后清理登记表，避免长期累积
            bool removed = Monitors.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cts));
            if (removed)
            {
                try { cts.Dispose(); } catch { }
                // 监测结束：同时清理该路径及其内部文件的访问观察状态
                ClearAccessWatch(path);
                if (Directory.Exists(path))
                {
                    try
                    {
                        foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                            ClearAccessWatch(f);
                    }
                    catch { }
                }
            }
        }, TaskScheduler.Default);
    }

    // ---- 监测 ----

    private static async Task MonitorFileAsync(string path, string password, DateTime started, CancellationToken ct)
    {
        // 是否「曾经观察到被占用」：决定空闲后要不要走额外的句柄释放保护期
        bool everInUse = false;
        int consecutiveIdle = 0;          // 连续空闲轮询计数
        DateTime idleSince = default;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < MonitorHardLimit)
        {
            try { await Task.Delay(PollMs, ct); } catch (OperationCanceledException) { return; }

            if (!File.Exists(path)) { RemoveManifest(path); return; }              // 已被删除
            if (InPlaceEncryptionService.IsFileEncrypted(path)) { RemoveManifest(path); return; } // 已重加密

            bool inUse = ComputeInUse(new[] { path });

            // 【核心】文件近来是否仍被读取（播放器会持续刷新最后访问时间）。
            // 这一条能可靠识别「正在播放」，而句柄检测做不到（轮询总落在读取间隙里）。
            bool accessedRecently = IsAccessStillActive(path);

            // 仅“确实有句柄占用”才阻断（独占打开能可靠检测播放/占用）；
            // 访问时间不可靠（Defender/索引器持续读取会一直刷新），不再作为阻断条件。
            if (inUse)
            {
                if (inUse) everInUse = true;
                if (accessedRecently) everInUse = true;
                consecutiveIdle = 0;      // 仍在被读取 → 连续空闲计数归零
                idleSince = default;
            }
            else
            {
                consecutiveIdle++;
                if (consecutiveIdle == 1) idleSince = DateTime.UtcNow;

                // 三重条件都满足才加密：
                //   1) 连续多次轮询都检测不到句柄（过滤瞬时占用）
                //   2) 最后访问时间已过去足够久（真正排除「正在播放」）
                //   3) 空闲时长达到宽限期（+ 曾占用时的额外保护期）
                if (consecutiveIdle >= RequiredConsecutiveIdlePolls
                    && DateTime.UtcNow - idleSince >= IdleConfirmWindow)
                {
                    TimeSpan required = FileGracePeriod
                        + (everInUse ? HandleReleaseProtection : TimeSpan.Zero);

                    if (DateTime.UtcNow - idleSince >= required)
                    {
                        Log($"判定空闲->重加密(文件): {path} inUse={inUse} accessed={accessedRecently} elapsed={(DateTime.UtcNow - started).TotalSeconds:F0}s everInUse={everInUse}");
                        if (TryReencryptFile(path, password)) { RemoveManifest(path); return; }
                        consecutiveIdle = 0;   // 加密失败（可能又被打开），重新计数
                    }
                }
            }

            // 兜底：超时强制加密也必须同时满足「无句柄 + 未在读取」，
            // 否则「播放中恰好落在读取间隙」仍会被误加密。
            if (!inUse
                && consecutiveIdle >= RequiredConsecutiveIdlePolls
                && DateTime.UtcNow - started >= MaxTtl)
            {
                if (TryReencryptFile(path, password)) { RemoveManifest(path); return; }
            }
        }

        // 到达硬性上限仍未完成：本循环退出，避免密码与协程永久驻留。
        // 不再静默留在清单中假装仍被监测——交由下次启动的 CleanupOnStartup 兜底。
    }

    private static async Task MonitorFolderAsync(string path, string password, DateTime started, CancellationToken ct)
    {
        bool everInUse = false;
        int consecutiveIdle = 0;
        DateTime idleSince = default;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < MonitorHardLimit)
        {
            try { await Task.Delay(PollMs, ct); } catch (OperationCanceledException) { return; }

            if (!Directory.Exists(path)) { RemoveManifest(path); return; }
            if (InPlaceEncryptionService.IsFolderEncrypted(path)) { RemoveManifest(path); return; }

            // ⚠️ 枚举失败时不能 `continue` 跳过本轮：否则一旦文件夹长期无法枚举
            //（权限异常 / 被 ACL 锁），下面的 MaxTtl 兜底将永远不被检查，明文无限期驻留。
            // 此处标记为「未知占用」，保守地视为在用，同时仍让超时兜底有机会生效。
            string[]? files = null;
            try { files = Directory.GetFiles(path, "*", SearchOption.AllDirectories); }
            catch { files = null; }

            bool inUse = files == null || ComputeInUse(files);

            // 【核心】文件夹内是否有文件近来仍被读取（播放其中的视频会持续刷新）。
            bool accessedRecently = false;
            if (files != null)
            {
                foreach (var f in files)
                {
                    if (IsAccessStillActive(f)) { accessedRecently = true; break; }
                }
            }

            if (inUse)
            {
                everInUse = true;
                consecutiveIdle = 0;
                idleSince = default;
            }
            else
            {
                consecutiveIdle++;
                if (consecutiveIdle == 1) idleSince = DateTime.UtcNow;

                if (consecutiveIdle >= RequiredConsecutiveIdlePolls
                    && DateTime.UtcNow - idleSince >= IdleConfirmWindow)
                {
                    TimeSpan required = FolderGracePeriod
                        + (everInUse ? HandleReleaseProtection : TimeSpan.Zero);
                    if (DateTime.UtcNow - idleSince >= required)
                    {
                        Log($"判定空闲->重加密(文件夹): {path} inUse={inUse} accessed={accessedRecently} elapsed={(DateTime.UtcNow - started).TotalSeconds:F0}s everInUse={everInUse}");
                        if (TryReencryptFolder(path, password)) { RemoveManifest(path); return; }
                        consecutiveIdle = 0;
                    }
                }
            }

            // 兜底：超时强制加密同样要求「无句柄」
            if (files != null && !inUse
                && consecutiveIdle >= RequiredConsecutiveIdlePolls
                && DateTime.UtcNow - started >= MaxTtl)
            {
                if (TryReencryptFolder(path, password)) { RemoveManifest(path); return; }
            }
        }
        // 到达硬性上限仍未完成：同上，交由下次启动兜底
    }

    private static bool TryReencryptFile(string path, string password)
    {
        try { Log($"重加密(文件)开始: {path}"); InPlaceEncryptionService.EncryptFile(path, password); Log($"重加密(文件)完成: {path}"); return true; }
        catch (Exception ex) { Log($"重加密(文件)失败: {path} :: {ex.GetType().Name}: {ex.Message}"); return false; } // 可能又被打开发送到捕获循环，继续监测
    }

    private static bool TryReencryptFolder(string path, string password)
    {
        try { Log($"重加密(文件夹)开始: {path}"); InPlaceEncryptionService.EncryptFolder(path, password); Log($"重加密(文件夹)完成: {path}"); return true; }
        catch (Exception ex) { Log($"重加密(文件夹)失败: {path} :: {ex.GetType().Name}: {ex.Message}"); return false; }
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
    /// 判断一组文件当前是否“被占用”：只要 Restart Manager 检测到仍有进程持有句柄即为使用中。
    /// 只依据“文件句柄 + 空闲超时”，不按进程存活判断——避免任何后台进程长期存活导致永不自动重加密。
    /// </summary>
    private static bool ComputeInUse(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            // 1) Restart Manager：能拿到持有句柄的进程
            var pids = GetUsingPids(f);
            if (pids != null && pids.Count > 0) return true;

            // 2) 独占打开探测（可靠）：只要有进程（如正在播放的播放器）持有句柄，
            //    以 FileShare.None 打开必然失败 → 视为占用，绝不在播放中重新加密。
            //    这一步弥补“NTFS 最后访问时间被系统关闭”导致无法靠访问时间判断播放的问题。
            if (!CanOpenExclusive(f)) return true;
        }
        return false;
    }

    /// <summary>
    /// 能否以“不共享”方式打开文件：能=当前无其它句柄占用；失败=有句柄占用或不可访问（保守视为占用）。
    /// 可靠检测“文件正在被播放/使用”，不依赖 LastAccessTime（系统常默认关闭该更新）。
    /// </summary>
    private static bool CanOpenExclusive(string path)
    {
        try
        {
            if (!File.Exists(path)) return true; // 已不存在：不据此阻挠（后续逻辑会清理）
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch { return false; }
    }

    // ---- 清单：供启动兜底扫描 ----
    // 所有读写均在 ManifestLock 内完成，避免「读-改-写」被并发调用互相覆盖。

    private static void AddManifest(string path)
    {
        try
        {
            lock (ManifestLock)
            {
                var list = ReadManifestUnlocked();
                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
                WriteManifestUnlocked(list);
            }
        }
        catch { }
    }

    private static void RemoveManifest(string path)
    {
        try
        {
            lock (ManifestLock)
            {
                var list = ReadManifestUnlocked();
                // 用忽略大小写的比较移除，避免同一路径因大小写差异残留
                int removed = list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) WriteManifestUnlocked(list);
            }
        }
        catch { }
    }

    /// <summary>读取清单（调用方需持有 ManifestLock，或确认无并发）。</summary>
    private static List<string> ReadManifestUnlocked()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new List<string>();
            string json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize(json, TempUnlockJsonContext.Default.ListString)
                   ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 是否还有未完成的临时解锁：清单里仍有条目，或仍有监测循环在运行。
    /// 用途：关闭「临时解密」窗口时，若仍有未完成项，需要让进程保持后台存活
    /// （托盘常驻），继续监测并在文件空闲后自动重新加密；否则进程一退出，
    /// 监测就消失，明文要等到下次启动才由 CleanupOnStartup 兜底。
    /// </summary>
    public static bool HasActiveUnlocks()
    {
        try
        {
            lock (ManifestLock)
            {
                if (ReadManifestUnlocked().Count > 0) return true;
            }
        }
        catch { }
        return !Monitors.IsEmpty;
    }

    /// <summary>返回当前仍处于临时解锁（待重新加密/明文）状态的路径快照。</summary>
    public static List<string> GetPendingPaths()
    {
        try
        {
            lock (ManifestLock) { return ReadManifestUnlocked(); }
        }
        catch { return new List<string>(); }
    }

    private static void WriteManifestUnlocked(List<string> list)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ManifestPath);
            if (dir != null) Directory.CreateDirectory(dir);

            // 原子写：先写临时文件再替换，避免写入过程中崩溃留下损坏的 JSON
            string tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, TempUnlockJsonContext.Default.ListString));
            if (File.Exists(ManifestPath)) File.Replace(tmp, ManifestPath, null);
            else File.Move(tmp, ManifestPath);
        }
        catch { }
    }

    /// <summary>
    /// 应用启动时扫描清单：对仍处于明文（临时解锁）的条目自动重新加密，兜底防明文残留。
    ///
    /// ⚠️ 本方法会访问 Windows 凭据管理器与磁盘，必须在**后台线程**调用，不要放在 UI 线程。
    ///
    /// 可恢复性说明：重新加密 **只能用密码**（EncryptFile/EncryptFolder 只接受密码，
    /// 不接受恢复码）。因此：
    ///   - 保存了密码 → 自动重新加密；
    ///   - 只保存了恢复码 → 无法自动重加密，条目会**保留在清单中**，
    ///     并在下次启动时继续尝试；同时写提示文件，避免静默留下明文。
    /// </summary>
    public static void CleanupOnStartup()
    {
        List<string> list;
        lock (ManifestLock) { list = ReadManifestUnlocked(); }
        if (list.Count == 0) return;

        var secret = HelloSecretStore.TryGetSecret();

        // 只有「密码」可用于重新加密；恢复码不行（核心 API 不支持）。
        string? password = (secret != null && !HelloSecretStore.IsRecoveryKind(secret.Value.Kind))
            ? secret.Value.Secret
            : null;

        var remaining = new List<string>();
        foreach (var path in list)
        {
            bool handled = false;
            if (File.Exists(path))
            {
                if (InPlaceEncryptionService.IsFileEncrypted(path)) handled = true;
                else if (password != null) handled = TryReencryptFile(path, password);
            }
            else if (Directory.Exists(path))
            {
                if (InPlaceEncryptionService.IsFolderEncrypted(path)) handled = true;
                else if (password != null) handled = TryReencryptFolder(path, password);
            }
            else
            {
                handled = true; // 路径已不存在，视为已清理
            }

            if (handled) continue;

            // 未能立即重新加密（很可能仍被占用，或该条目来自已退出的旧进程）。
            // 关键修复：为它启动后台监测，空闲后自动重新加密；
            // 否则只在“下次启动”再试一次，被长期占用/跨进程残留的明文会永远不加密。
            if (password != null)
            {
                if (File.Exists(path))
                    StartMonitor(path, ct => MonitorFileAsync(path, password, DateTime.UtcNow, ct));
                else if (Directory.Exists(path))
                    StartMonitor(path, ct => MonitorFolderAsync(path, password, DateTime.UtcNow, ct));
            }
            remaining.Add(path);   // 监测成功后会 RemoveManifest；在此之前保留在清单中
        }

        // 保留未能处理的条目（例如仅有恢复码），下一次启动继续尝试，
        // 而不是丢弃条目、把明文留成“无人跟踪”的状态。
        lock (ManifestLock) { WriteManifestUnlocked(remaining); }

        if (remaining.Count > 0 && password == null)
            LogCleanupPending(remaining.Count);
    }

    /// <summary>记录「有明文待重加密但缺少可用密码」的提示文件，便于用户察觉并处理。</summary>
    private static void LogCleanupPending(int count)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ManifestPath);
            if (dir == null) return;
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, "temp-unlock-pending.txt");
            File.WriteAllText(logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  有 {count} 个临时解锁的条目未能自动重新加密。\r\n" +
                "原因：当前保存的解锁凭据是「恢复码」，而重新加密需要使用密码。\r\n" +
                "处理建议：打开应用 →「设置 - 行为」→ 关闭再开启 Windows Hello 解锁，" +
                "并在提示时输入原始密码；或手动右键这些路径重新加密。\r\n");
        }
        catch { }
    }
}

/// <summary>
/// 临时解锁清单的 JSON 序列化上下文（源生成，兼容裁剪/AOT 场景）。
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal partial class TempUnlockJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
