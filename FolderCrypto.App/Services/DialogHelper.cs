using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Popups;
using FolderCrypto.App.Dialogs;

namespace FolderCrypto.App.Services;

/// <summary>统一的信息提示与密码对话框入口。</summary>
public static class DialogHelper
{
    public static async Task ShowInfo(Window? window, string message)
        => await ShowDialog(window, "提示", message);

    public static async Task ShowError(Window? window, string message)
        => await ShowDialog(window, "错误", message);

    private static async Task ShowDialog(Window? window, string title, string message)
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
    }

    /// <summary>带勾选图标的成功提示。</summary>
    public static async Task ShowSuccess(Window? window, string title, string message)
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
    }

    /// <summary>显示加密后生成的恢复码（供用户抄录保存）。</summary>
    public static async Task ShowRecoveryCode(Window? window, string recoveryCode)
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
    }

    /// <summary>弹出设置密码对话框；返回密码或 null（取消）。</summary>
    public static async Task<string?> ShowSetPasswordDialogAsync(Window? window)
    {
        var dlg = new SetPasswordDialog { XamlRoot = window?.Content?.XamlRoot };
        if (dlg.XamlRoot == null) return null;

        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary ? dlg.Password : null;
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
        var dlg = new PasswordDialog(targetName, remainingAttempts) { XamlRoot = window?.Content?.XamlRoot };
        if (dlg.XamlRoot == null) return null;

        if (startCooldown)
            dlg.StartCooldown();

        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary
            ? (dlg.Secret ?? string.Empty, dlg.IsRecovery)
            : null;
    }
}
