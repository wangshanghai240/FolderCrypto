using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using FolderCrypto.Core.Encryption;
using FolderCrypto.Core.Services;
using FolderCrypto.Core.Security;

namespace FolderCrypto.App.Services;
/// <summary>
/// 加密/解密的高层操作流程，负责 UI（密码输入/恢复码）与核心库的衔接。
/// 采用原地加密：右键加密/解密直接作用于目标文件或文件夹本身。
/// </summary>
public static class Operations
{
    private const int MaxAttempts = 3;
    private const int CooldownSeconds = 30;

    /// <summary>执行原地加密：为指定路径输入密码并直接加密，加密后展示恢复码。</summary>
    public static async Task StartEncrypt(string sourcePath, Window? window)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            await DialogHelper.ShowInfo(window, "目标文件或文件夹不存在。");
            return;
        }

        if (InPlaceEncryptionService.IsEncrypted(sourcePath))
        {
            await DialogHelper.ShowInfo(window, "该文件/文件夹已加密。");
            return;
        }

        // 让用户设置密码（含强度校验）
        var pwd = await DialogHelper.ShowSetPasswordDialogAsync(window);
        if (pwd == null) return; // 用户取消

        try
        {
            string recoveryCode = Directory.Exists(sourcePath)
                ? InPlaceEncryptionService.EncryptFolder(sourcePath, pwd)
                : InPlaceEncryptionService.EncryptFile(sourcePath, pwd);

            // 加密完成 + 展示恢复码（不隐藏源文件）
            await DialogHelper.ShowRecoveryCode(window, recoveryCode);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowError(window, "加密失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 执行原地解密：支持“密码”或“恢复码”解锁。
    /// 连续错误达到 <see cref="MaxAttempts"/> 次后进入 <see cref="CooldownSeconds"/> 秒冷却。
    /// </summary>
    public static async Task StartDecrypt(string targetPath, Window? window)
    {
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            await DialogHelper.ShowInfo(window, "目标文件或文件夹不存在。");
            return;
        }

        if (!InPlaceEncryptionService.IsEncrypted(targetPath))
        {
            await DialogHelper.ShowInfo(window, "该文件/文件夹未加密，无需解密。");
            return;
        }

        int wrongCount = 0;
        var secret = (Secret: (string?)null, IsRecovery: false);

        while (true)
        {
            int attempt = wrongCount + 1;
            // 若已达上限，先进入冷却（30秒）
            if (wrongCount >= MaxAttempts)
            {
                await DialogHelper.ShowInfo(window,
                    $"已连续错误 {MaxAttempts} 次，请等待 {CooldownSeconds} 秒后再试。");
                await Task.Delay(TimeSpan.FromSeconds(CooldownSeconds));
                wrongCount = 0;
                continue;
            }

            var result = await DialogHelper.ShowUnlockDialogAsync(
                window, System.IO.Path.GetFileName(targetPath), MaxAttempts - wrongCount, startCooldown: false);
            if (result == null) return; // 用户取消

            secret = (result.Value.Secret, result.Value.IsRecovery);

            bool ok = InPlaceEncryptionService.VerifyPassword(targetPath, secret.Secret, secret.IsRecovery);
            if (ok)
                break;

            wrongCount++;
            int remaining = MaxAttempts - wrongCount;
            string kind = secret.IsRecovery ? "恢复码" : "密码";
            if (wrongCount < MaxAttempts)
            {
                await DialogHelper.ShowInfo(window, $"{kind}错误，剩余 {remaining} 次机会。");
            }
            else
            {
                await DialogHelper.ShowInfo(window, $"{kind}错误次数已达上限，需等待 {CooldownSeconds} 秒。");
            }
        }

        try
        {
            if (Directory.Exists(targetPath))
            {
                if (secret.IsRecovery)
                    InPlaceEncryptionService.DecryptFolder(targetPath, null, secret.Secret);
                else
                    InPlaceEncryptionService.DecryptFolder(targetPath, secret.Secret, null);
            }
            else
            {
                if (secret.IsRecovery)
                    InPlaceEncryptionService.DecryptFile(targetPath, null, secret.Secret);
                else
                    InPlaceEncryptionService.DecryptFile(targetPath, secret.Secret, null);
            }

            await DialogHelper.ShowSuccess(window, "解密完成", "解密完成，文件/文件夹已还原。");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await DialogHelper.ShowInfo(window, "密码/恢复码错误或数据损坏，无法解密。");
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowError(window, "解密失败：" + ex.Message);
        }
    }
}
