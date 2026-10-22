using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Security.Credentials;

namespace FolderCrypto.App.Services;

/// <summary>
/// Windows Hello 解锁用的凭据存储：把解密用的「密码」或「恢复码」存入 Windows 凭据管理器
/// （PasswordVault，按当前 Windows 用户隔离、由系统保护），并配合 Windows Hello(PIN/人脸)
/// 作为门禁——只有通过 Windows Hello 认证后才能取回凭据，从而“用 Windows Hello 替代输入密码”。
///
/// ⚠️ 线程模型（重要，勿改回同步）：
///   PasswordVault 的 Add / Retrieve / Remove / FindAll* 都是 WinRT 同步调用，底层需与
///   系统凭据服务跨进程通信；首次写入时还可能创建凭据容器，耗时可达数百毫秒到数秒。
///   早期版本在 UI 线程、且恰好于 ContentDialog 关闭过程中直接调用它，
///   会与对话框的关闭/焦点归还流程互相等待，表现为整个窗口「未响应」。
///   因此本类对外一律提供 **异步** API，内部用 Task.Run 切到线程池执行，
///   绝不阻塞 UI 线程。调用方请始终 await 这些方法。
/// </summary>
public static class HelloSecretStore
{
    private const string Resource = "FolderCrypto.HelloUnlock";
    private const string KindRecovery = "recovery";
    private const string KindPassword = "password";

    /// <summary>
    /// 是否已启用 Windows Hello 解锁（设置开关开启 且 已保存凭据）。
    ///
    /// ⚠️ 会访问 Windows 凭据管理器，属跨进程 WinRT 调用。
    /// 仅在「已经确认过凭据存在」或非 UI 线程场景使用；
    /// UI 初始化阶段（如窗口构造函数）请改用 <see cref="IsEnabledAsync"/>。
    /// </summary>
    public static bool IsEnabled => SettingsService.WindowsHelloUnlock && HasSecret;

    /// <summary>异步版本：是否已启用 Windows Hello 解锁（不阻塞 UI 线程）。</summary>
    public static async Task<bool> IsEnabledAsync()
        => SettingsService.WindowsHelloUnlock && await HasSecretAsync();

    /// <summary>
    /// 缓存的「凭据是否已存在」结果：在后台线程预热一次，供 UI 构造函数等
    /// 不便 await 的场景同步读取，避免在 UI 线程现场发起跨进程调用。
    /// </summary>
    private static volatile bool _cachedHasSecret;
    private static volatile bool _cacheWarmed;

    /// <summary>
    /// 后台预热凭据存在性缓存（不阻塞调用方）。应用启动时调用一次即可，
    /// 之后 <see cref="HasSecret"/> 会直接返回缓存值。
    /// </summary>
    public static void WarmUpCache()
    {
        Task.Run(() =>
        {
            try
            {
                _cachedHasSecret = new PasswordVault().FindAllByResource(Resource).Count > 0;
                _cacheWarmed = true;
            }
            catch
            {
                _cachedHasSecret = false;
                _cacheWarmed = true;
            }
        });
    }

    /// <summary>
    /// 是否已保存可用的解锁凭据。
    /// 若已通过 <see cref="WarmUpCache"/> 预热，则直接返回缓存值（不触碰凭据管理器）；
    /// 否则执行一次同步查询——⚠️ UI 线程上应尽量避免走到该分支。
    /// </summary>
    public static bool HasSecret
    {
        get
        {
            if (_cacheWarmed) return _cachedHasSecret;
            try { return new PasswordVault().FindAllByResource(Resource).Count > 0; }
            catch { return false; }
        }
    }

    /// <summary>异步版本：是否已保存可用的解锁凭据（不阻塞 UI 线程）。</summary>
    public static Task<bool> HasSecretAsync() => Task.Run(() =>
    {
        try { return new PasswordVault().FindAllByResource(Resource).Count > 0; }
        catch { return false; }
    });

    /// <summary>
    /// 保存解锁凭据（异步，不阻塞 UI）。kind 传 <c>"password"</c> 或 <c>"recovery"</c>。
    /// 返回是否保存成功。
    /// </summary>
    public static Task<bool> SaveSecretAsync(string kind, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return Task.FromResult(false);

        // 明文副本用于写入；方法返回后不留引用
        return Task.Run(() =>
        {
            try
            {
                var vault = new PasswordVault();

                // 先清掉同 Resource 下的旧凭据，避免“开启→关闭→再开启”后残留多条。
                // 与 Add 合并到同一次后台往返，减少跨进程调用次数。
                try
                {
                    var existing = vault.FindAllByResource(Resource);
                    if (existing != null)
                    {
                        foreach (var cred in existing)
                        {
                            try { vault.Remove(cred); } catch { }
                        }
                    }
                }
                catch
                {
                    // 尚无凭据时会抛异常（未找到），属正常情况，忽略
                }

                vault.Add(new PasswordCredential(Resource, kind, secret));
                _cachedHasSecret = true;      // 更新缓存，保持同步读取的准确性
                _cacheWarmed = true;
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>
    /// 取回已保存的解锁凭据（异步，不阻塞 UI）；返回 (kind, secret)，未保存返回 null。
    /// </summary>
    public static Task<(string Kind, string Secret)?> TryGetSecretAsync() => Task.Run(() =>
    {
        try
        {
            var vault = new PasswordVault();
            var creds = vault.FindAllByResource(Resource);
            if (creds != null)
            {
                foreach (var cred in creds)
                {
                    try
                    {
                        var c = vault.Retrieve(Resource, cred.UserName);
                        if (!string.IsNullOrEmpty(c.Password))
                            return ((string Kind, string Secret)?)(c.UserName, c.Password);
                    }
                    catch
                    {
                        // 单条读取失败不影响其它条目
                    }
                }
            }
        }
        catch { }
        return null;
    });

    /// <summary>
    /// 同步取回凭据（保留给后台/非 UI 场景，如启动兜底扫描）。
    /// ⚠️ 不要在 UI 线程调用，否则可能造成界面卡顿甚至无响应。
    /// </summary>
    public static (string Kind, string Secret)? TryGetSecret()
    {
        try
        {
            var vault = new PasswordVault();
            var creds = vault.FindAllByResource(Resource);
            foreach (var cred in creds)
            {
                var c = vault.Retrieve(Resource, cred.UserName);
                if (!string.IsNullOrEmpty(c.Password))
                    return (c.UserName, c.Password);
            }
        }
        catch { }
        return null;
    }

    /// <summary>清除已保存的解锁凭据（异步，不阻塞 UI）。返回是否有凭据被删除。</summary>
    public static Task<bool> ClearSecretAsync() => Task.Run(() =>
    {
        bool removed = false;
        try
        {
            var vault = new PasswordVault();
            var creds = vault.FindAllByResource(Resource);
            if (creds != null)
            {
                foreach (var cred in creds)
                {
                    try { vault.Remove(cred); removed = true; } catch { }
                }
            }
        }
        catch { }

        _cachedHasSecret = false;      // 更新缓存
        _cacheWarmed = true;
        return removed;
    });

    /// <summary>同步清除凭据（保留兼容）。⚠️ 避免在 UI 线程调用。</summary>
    public static void ClearSecret()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var cred in vault.FindAllByResource(Resource))
                vault.Remove(cred);
        }
        catch { }
    }

    /// <summary>凭据类型是否为恢复码（否则视为密码）。</summary>
    public static bool IsRecoveryKind(string kind)
        => string.Equals(kind, KindRecovery, StringComparison.OrdinalIgnoreCase);

    /// <summary>密码类型常量（供调用方判断）。</summary>
    public const string PasswordKind = KindPassword;

    /// <summary>恢复码类型常量（供调用方判断）。</summary>
    public const string RecoveryKind = KindRecovery;
}
