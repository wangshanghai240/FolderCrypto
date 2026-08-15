using System;
using System.Threading.Tasks;
using FolderCrypto.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderCrypto.App.Dialogs;

/// <summary>设置加密密码的对话框，内置强度校验。</summary>
public sealed partial class SetPasswordDialog : ContentDialog
{
    public string? Password { get; private set; }

    public SetPasswordDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        string pwd = PasswordBox.Password;
        string confirm = ConfirmBox.Password;

        var errors = PasswordPolicy.Validate(pwd);

        // 实时更新密码强度条
        UpdateStrength(pwd);

        if (string.IsNullOrEmpty(pwd))
        {
            ValidationText.Text = "";
            ValidationText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
            return;
        }

        if (errors.Count > 0)
        {
            ValidationText.Text = string.Join("\n", errors);
            ValidationText.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = false;
            return;
        }

        if (!string.Equals(pwd, confirm))
        {
            ValidationText.Text = "两次输入的密码不一致。";
            ValidationText.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = false;
            return;
        }

        ValidationText.Text = "✓ 密码强度符合要求";
        ValidationText.Foreground = GetThemeBrush("SystemFillColorSuccessBrush");
        ValidationText.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = true;
    }

    /// <summary>实时更新密码强度条与文字。</summary>
    private void UpdateStrength(string? pwd)
    {
        int score = string.IsNullOrEmpty(pwd) ? 0 : PasswordPolicy.ScoreStrength(pwd);
        var level = PasswordPolicy.LevelOf(score);
        int segments = PasswordPolicy.Segments(score);

        Microsoft.UI.Xaml.Media.Brush color = GetThemeBrush("TextFillColorSecondaryBrush");
        switch (level)
        {
            case PasswordStrength.Weak: color = GetThemeBrush("SystemFillColorCriticalBrush"); break;
            case PasswordStrength.Medium: color = GetThemeBrush("SystemFillColorCautionBrush"); break;
            case PasswordStrength.Strong: color = GetThemeBrush("SystemFillColorSuccessBrush"); break;
        }

        Seg1.Fill = segments >= 1 ? color : GetThemeBrush("ControlFillColorSecondaryBrush");
        Seg2.Fill = segments >= 2 ? color : GetThemeBrush("ControlFillColorSecondaryBrush");
        Seg3.Fill = segments >= 3 ? color : GetThemeBrush("ControlFillColorSecondaryBrush");
        Seg4.Fill = segments >= 4 ? color : GetThemeBrush("ControlFillColorSecondaryBrush");

        StrengthText.Text = string.IsNullOrEmpty(pwd) ? "密码强度：未输入" : $"密码强度：{PasswordPolicy.LevelText(level)}";
        StrengthText.Foreground = string.IsNullOrEmpty(pwd) ? GetThemeBrush("TextFillColorSecondaryBrush") : color;
    }

    /// <summary>从应用资源取主题感知画刷（随浅色/深色自适应）。</summary>
    private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(string key)
    {
        if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(key, out var value) == true
            && value is Microsoft.UI.Xaml.Media.Brush brush)
        {
            return brush;
        }
        // 资源缺失时用主题感知的强调色（浅色/深色下都清晰）
        if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent) == true
            && accent is Microsoft.UI.Xaml.Media.Brush b)
            return b;

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Password = PasswordBox.Password;
    }
}
