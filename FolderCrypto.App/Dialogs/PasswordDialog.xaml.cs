using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderCrypto.App.Dialogs;

/// <summary>
/// 解锁对话框：支持“密码”或“恢复码”两种方式解锁，
/// 并在连续输错 3 次后进入 <see cref="CooldownSeconds"/> 秒的冷却倒计时。
/// </summary>
public sealed partial class PasswordDialog : ContentDialog
{
    public const int CooldownSeconds = 30;

    /// <summary>用户输入的密码或恢复码。</summary>
    public string? Secret { get; private set; }

    /// <summary>本次是否为恢复码方式。</summary>
    public bool IsRecovery { get; private set; }

    private CancellationTokenSource? _cts;
    private bool _coolingDown;

    public PasswordDialog(string targetName, int remainingAttempts)
    {
        InitializeComponent();
        NameText.Text = $"解锁“{targetName}”";
        AttemptText.Text = remainingAttempts <= 2
            ? $"注意：剩余 {remainingAttempts} 次尝试；连续错误 {remainingAttempts} 次后需等待 {CooldownSeconds} 秒。"
            : string.Empty;
    }

    private void OnModeToggled(object sender, RoutedEventArgs e)
    {
        bool rec = ModeSwitch.IsOn;
        PasswordBox.Visibility = rec ? Visibility.Collapsed : Visibility.Visible;
        RecoveryBox.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_coolingDown)
        {
            // 冷却中不允许确认
            args.Cancel = true;
            return;
        }

        if (ModeSwitch.IsOn)
        {
            IsRecovery = true;
            Secret = RecoveryBox.Text?.Trim();
        }
        else
        {
            IsRecovery = false;
            Secret = PasswordBox.Password;
        }
    }

    /// <summary>进入冷却：禁用输入与确认，倒计时 <see cref="CooldownSeconds"/> 秒。</summary>
    public void StartCooldown()
    {
        _coolingDown = true;
        IsPrimaryButtonEnabled = false;
        PasswordBox.IsEnabled = false;
        RecoveryBox.IsEnabled = false;
        ModeSwitch.IsEnabled = false;

        _cts = new CancellationTokenSource();
        _ = RunCooldownAsync(_cts.Token);
    }

    public void StopCooldown()
    {
        _coolingDown = false;
        IsPrimaryButtonEnabled = true;
        PasswordBox.IsEnabled = true;
        RecoveryBox.IsEnabled = true;
        ModeSwitch.IsEnabled = true;
        CooldownText.Text = string.Empty;
    }

    private async Task RunCooldownAsync(CancellationToken token)
    {
        int sec = CooldownSeconds;
        while (sec > 0 && !token.IsCancellationRequested)
        {
            CooldownText.Text = $"连续错误次数过多，请等待 {sec} 秒后再试…";
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            sec--;
        }
        if (!token.IsCancellationRequested)
            StopCooldown();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
