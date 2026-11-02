using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Popups;
using FolderCrypto.App.Dialogs;

namespace FolderCrypto.App.Services;

/// <summary>统一的信息提示与密码对话框入口。</summary>
public static class DialogHelper
{
    /// <summary>
    /// 对话框串行化闸门。
    ///
    /// 背景（重要，勿删）：WinUI 3 的 ContentDialog 在「关闭」与「弹窗树更新完成」之间存在
    /// 一个时间窗口（见 microsoft-ui-xaml#10806）。若在这个窗口内立刻 ShowAsync 下一个
    /// ContentDialog，两者会互相等待，表现为整个窗口「未响应」且长时间不恢复。
    ///
    /// 典型触发路径：设置页开启 Windows Hello → SetPasswordDialog 点确定关闭
    /// → 紧接着 ShowError/ShowInfo 弹第二个对话框 → 卡死。
    ///
    /// 对策：所有 ContentDialog 通过本闸门串行化，并且每个对话框关闭后
    /// 让出若干次 UI 帧，确保弹窗树已清理，再允许下一个对话框打开。
    /// </summary>
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    /// <summary>
    /// 等待弹窗树稳定：让出 UI 线程若干帧。
    /// 仅 Task.Yield 不足以覆盖 ContentDialog 的关闭动画与 popup 注销延迟。
    /// </summary>
    private static async Task SettleDialogsAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Yield();
            await Task.Delay(30).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 串行执行一个对话框：获取闸门 → 再等弹窗树稳定 → 显示 → 释放前再稳定一次。
    /// 所有需要 ShowAsync 的 ContentDialog 都必须经由此方法。
    /// </summary>
    private static async Task<T> RunSerializedAsync<T>(Func<Task<T>> showAsync)
    {
        await DialogGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // 上一个对话框可能刚关闭，先等弹窗树稳定再打开新的
            await SettleDialogsAsync();
            return await showAsync();
        }
        finally
        {
            // 关闭后也让出几帧，避免紧接着的下一个对话框撞上关闭窗口期
            await SettleDialogsAsync();
            DialogGate.Release();
        }
    }

    /// <summary>
    /// 供调用方自定义 ContentDialog 时使用的串行化执行入口。
    ///
    /// ⚠️ 凡是在本应用内自行 <c>new ContentDialog(...)</c> 并 <c>ShowAsync()</c> 的地方，
    /// 都必须改为通过本方法显示；否则会绕过闸门，与其它对话框相撞导致窗口「未响应」。
    /// </summary>
    public static Task<T> ShowSerializedAsync<T>(Func<Task<T>> showAsync)
        => RunSerializedAsync(showAsync);

    /// <summary>串行化执行一个无返回值的对话框操作。</summary>
    public static Task ShowSerializedAsync(Func<Task> showAsync)
        => RunSerializedAsync(async () => { await showAsync(); return true; });

    public static async Task ShowInfo(Window? window, string message)
        => await ShowDialog(window, "提示", message);

    public static async Task ShowError(Window? window, string message)
        => await ShowDialog(window, "错误", message);

    /// <summary>
    /// WinUI 风格的确认对话框（主按钮 + 关闭按钮）。返回 true 表示点了主按钮。
    /// 无法获取 XamlRoot（无窗口）时返回 true，避免调用方卡住。
    /// </summary>
    public static async Task<bool> ConfirmAsync(Window? window, string title, string message,
        string primaryText, string closeText = "取消")
    {
        return await RunSerializedAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new ScrollViewer
                {
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }
                },
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = window?.Content?.XamlRoot
            };

            if (dialog.XamlRoot == null)
                return true;   // 无窗口无法弹窗，默认继续

            var r = await dialog.ShowAsync();
            return r == ContentDialogResult.Primary;
        });
    }

    private static async Task ShowDialog(Window? window, string title, string message)
    {
        await RunSerializedAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = window?.Content?.XamlRoot
            };

            if (dialog.XamlRoot != null)
                await dialog.ShowAsync();

            return true;
        });
    }

    /// <summary>带勾选图标的成功提示。</summary>
    public static async Task ShowSuccess(Window? window, string title, string message)
    {
        await RunSerializedAsync(async () =>
        {
            var stack = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Spacing = 12,
                Width = 340
            };
            stack.Children.Add(new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 42,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorSuccessBrush"]
            });
            stack.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            });

            var dialog = new ContentDialog
            {
                Title = title,
                Content = stack,
                CloseButtonText = "确定",
                XamlRoot = window?.Content?.XamlRoot
            };

            if (dialog.XamlRoot != null)
                await dialog.ShowAsync();

            return true;
        });
    }

    /// <summary>显示加密后生成的恢复码（供用户抄录保存）。</summary>
    public static async Task ShowRecoveryCode(Window? window, string recoveryCode)
    {
        await RunSerializedAsync(async () =>
        {
            var stack = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 12, Width = 380 };

            stack.Children.Add(new TextBlock
            {
                Text = "加密完成！请务必保存以下恢复码：",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var codeBox = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
                Padding = new Microsoft.UI.Xaml.Thickness(16, 12, 16, 12)
            };
            codeBox.Child = new TextBlock
            {
                Text = recoveryCode,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                HorizontalTextAlignment = TextAlignment.Center
            };
            stack.Children.Add(codeBox);

            stack.Children.Add(new TextBlock
            {
                Text = "忘记密码时可用此恢复码解锁。请妥善保存（写在纸上或安全位置），不要与资料放在一起。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var dialog = new ContentDialog
            {
                Title = "加密完成",
                Content = stack,
                PrimaryButtonText = "我已保存",
                XamlRoot = window?.Content?.XamlRoot
            };

            if (dialog.XamlRoot != null)
                await dialog.ShowAsync();

            return true;
        });
    }

    /// <summary>弹出设置密码对话框；返回密码或 null（取消）。</summary>
    public static async Task<string?> ShowSetPasswordDialogAsync(Window? window)
    {
        return await RunSerializedAsync(async () =>
        {
            var dlg = new SetPasswordDialog { XamlRoot = window?.Content?.XamlRoot };
            if (dlg.XamlRoot == null) return (string?)null;

            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary ? dlg.Password : null;
        });
    }

    /// <summary>弹出输入密码对话框；返回密码或 null（取消）。</summary>
    public static async Task<string?> ShowPasswordDialogAsync(Window? window, string targetName, int remainingAttempts)
    {
        var result = await ShowUnlockDialogAsync(window, targetName, remainingAttempts, startCooldown: false);
        return result?.IsRecovery == false ? result.Value.Secret : null;
    }

    /// <summary>弹出解锁对话框（返回密码/恢复码及模式）。参数 startCooldown=true 时直接进入冷却态。</summary>
    public static async Task<(string Secret, bool IsRecovery)?> ShowUnlockDialogAsync(
        Window? window, string targetName, int remainingAttempts, bool startCooldown)
    {
        return await RunSerializedAsync(async () =>
        {
            var dlg = new PasswordDialog(targetName, remainingAttempts) { XamlRoot = window?.Content?.XamlRoot };
            if (dlg.XamlRoot == null) return ((string Secret, bool IsRecovery)?)null;

            if (startCooldown)
                dlg.StartCooldown();

            var result = await dlg.ShowAsync();
            return result == ContentDialogResult.Primary
                ? (dlg.Secret ?? string.Empty, dlg.IsRecovery)
                : null;
        });
    }
}
