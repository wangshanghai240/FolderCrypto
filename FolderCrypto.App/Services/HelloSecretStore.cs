using System;
using Windows.Security.Credentials;

namespace FolderCrypto.App.Services;

/// <summary>
/// Windows Hello 解锁用的凭据存储：把解密用的「密码」或「恢复码」存入 Windows 凭据管理器
/// （PasswordVault，按当前 Windows 用户隔离、由系统保护），并配合 Windows Hello(PIN/人脸)
/// 作为门禁——只有通过 Windows Hello 认证后才能取回凭据，从而“用 Windows Hello 替代输入密码”。
/// </summary>
public static class HelloSecretStore
{
    private const string Resource = "FolderCrypto.HelloUnlock";
    private const string KindRecovery = "recovery";
    private const string KindPassword = "password";

    /// <summary>是否已启用 Windows Hello 解锁（设置开关开启 且 已保存凭据）。</summary>
    public static bool IsEnabled => SettingsService.WindowsHelloUnlock && HasSecret;

    /// <summary>是否已保存可用的解锁凭据。</summary>
    public static bool HasSecret
    {
        get
        {
            try { return new PasswordVault().FindAllByResource(Resource).Count > 0; }
            catch { return false; }
        }
    }

    /// <summary>保存解锁凭据。kind 传 <c>"password"</c> 或 <c>"recovery"</c>。</summary>
    public static void SaveSecret(string kind, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        ClearSecret();
        var vault = new PasswordVault();
        vault.Add(new PasswordCredential(Resource, kind, secret));
    }

    /// <summary>取回已保存的解锁凭据；返回 (kind, secret)，未保存返回 null。</summary>
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

    /// <summary>清除已保存的解锁凭据。</summary>
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
}
