using System;
using System.Threading.Tasks;
using FolderCrypto.Core.Security;
using FolderCrypto.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FolderCrypto.App.Dialogs;

/// <summary>
/// “右键加密/解密”的小型宿主窗口：内容直接作为窗口内容渲染，
/// 不依赖 ContentDialog，因此不存在“无可见窗口无法弹窗”的问题。
/// </summary>
public sealed partial class PromptWindow : Window
{
    private readonly string _targetPath;
    private readonly bool _encryptMode;
    private int _wrongCount;
    private ProgressBar? _progressBar;
    private TextBlock? _progressText;

    private PromptWindow(string targetPath, bool encryptMode)
    {
        InitializeComponent();
        _targetPath = targetPath;
        _encryptMode = encryptMode;

        // Mica（亚克力）背景材质 + 表头也呈现 Mica（Windows 11；不支持时回退纯色）
        App.ApplyMicaBackdrop(this);

        // 注册到主题服务，由它应用主题并刷新标题栏(表头)，与当前主题保持一致
        Services.ThemeService.RegisterWindow(this);

        // 顶部区域作为可拖拽的标题栏
        try { SetTitleBar(DragTitleBar); }
        catch { }

        // 固定舒适的窗口大小，水平垂直居中到屏幕
        //（高度需容纳 标题/说明/密码/确认/强度条/提示/按钮，避免底部按钮被裁切）
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 480)); } catch { }
        CenterOnScreen();

        // 窗口真正显示后，再次设定大小并居中，确保可见到用户屏幕上
        // （否则可能在任务栏有、但窗口落在屏外/大小不对）。
        (Content as FrameworkElement).Loaded += (s, e) =>
        {
            try { AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 480)); } catch { }
            try { CenterOnScreen(); } catch { }
            try { AppWindow.Show(); } catch { }
            // 窗口布局/移动后重新断言密码框的显示按钮(小眼睛)，避免有时不显示
            try
            {
                PasswordBox.PasswordRevealMode = PasswordBox.PasswordRevealMode;
                ConfirmBox.PasswordRevealMode = ConfirmBox.PasswordRevealMode;
            }
            catch { }
        };

        if (encryptMode)
        {
            Title = "Folder Crypto - 加密";
            TitleIcon.Glyph = "\uE8F1";
            TitleText.Text = "加密";
            DescText.Text = $"正在加密：{System.IO.Path.GetFileName(targetPath)}\n密码需超过 6 位，且包含数字、字母和特殊字符。";
            ModeSwitch.Visibility = Visibility.Collapsed;
            StrengthPanel.Visibility = Visibility.Visible;
        }
        else
        {
            Title = "Folder Crypto - 解密";
            TitleIcon.Glyph = "\uE72E";
            TitleText.Text = "解密";
            DescText.Text = $"正在解锁：{System.IO.Path.GetFileName(targetPath)}";
            ConfirmBox.Visibility = Visibility.Collapsed;
            // 解密时必须显示“使用恢复码”切换开关
            ModeSwitch.Visibility = Visibility.Visible;
        }
    }

    /// <summary>回车触发“确定”，ESC 触发“取消”。</summary>
    private void OnGridKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (OkButton.IsEnabled)
                OnOk(OkButton, new RoutedEventArgs());
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_encryptMode)
                await DoEncryptAsync();
            else
                await DoDecryptAsync();
        }
        catch (Exception ex)
        {
            ShowHint("操作失败：" + ex.Message);
        }
    }

    private async Task DoEncryptAsync()
    {
        string pwd = PasswordBox.Password;
        var errors = PasswordPolicy.Validate(pwd);
        if (errors.Count > 0) { ShowHint(string.Join("；", errors)); return; }
        if (pwd != ConfirmBox.Password) { ShowHint("两次输入的密码不一致。"); return; }

        SetBusy(true);
        string recovery = "";
        bool isFolder = System.IO.Directory.Exists(_targetPath);

        if (isFolder)
        {
            // 文件夹：近即时，直接执行（展示进度条短暂停留以体现“加密中”）
            ShowProgress("正在加密…");
            await Task.Run(() => recovery = InPlaceEncryptionService.EncryptFolder(_targetPath, pwd));
        }
        else
        {
            // 文件：展示实时进度
            ShowProgress("正在加密… 0%");
            var progress = new Progress<int>(p => UpdateProgress(p, $"正在加密… {p}%"));
            await Task.Run(() => recovery = InPlaceEncryptionService.EncryptFile(_targetPath, pwd, progress));
        }

        ShowRecovery(recovery);
    }

    private async Task DoDecryptAsync()
    {
        bool isRecovery = ModeSwitch.IsOn;
        string secret = isRecovery ? (RecoveryBox.Text ?? "").Trim() : PasswordBox.Password;

        if (!InPlaceEncryptionService.VerifyPassword(_targetPath, secret, isRecovery))
        {
            _wrongCount++;
            if (_wrongCount >= 3)
            {
                // 超过 3 次：清空输入，禁用输入并进入 30 秒读秒倒计时
                PasswordBox.Password = "";
                RecoveryBox.Text = "";
                OkButton.IsEnabled = false;
                PasswordBox.IsEnabled = false;
                RecoveryBox.IsEnabled = false;
                ModeSwitch.IsEnabled = false;

                const int total = 30;
                for (int left = total; left > 0; left--)
                {
                    ShowHint($"连续错误次数过多，请等待 {left} 秒后重试…");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                _wrongCount = 0;
                PasswordBox.IsEnabled = true;
                RecoveryBox.IsEnabled = true;
                ModeSwitch.IsEnabled = true;
                HintText.Visibility = Visibility.Collapsed;
                UpdateOkEnabled();
            }
            else
            {
                ShowHint($"密码或恢复码错误，剩余 {3 - _wrongCount} 次机会。");
            }
            return;
        }

        SetBusy(true);
        bool isFolder = System.IO.Directory.Exists(_targetPath);
        if (isFolder)
        {
            ShowProgress("正在解密…");
            await Task.Run(() =>
            {
                if (isRecovery) InPlaceEncryptionService.DecryptFolder(_targetPath, null, secret);
                else InPlaceEncryptionService.DecryptFolder(_targetPath, secret, null);
            });
        }
        else
        {
            ShowProgress("正在解密… 0%");
            var progress = new Progress<int>(p => UpdateProgress(p, $"正在解密… {p}%"));
            await Task.Run(() =>
            {
                if (isRecovery) InPlaceEncryptionService.DecryptFile(_targetPath, null, secret, progress);
                else InPlaceEncryptionService.DecryptFile(_targetPath, secret, null, progress);
            });
        }

        ShowDone("解密完成", "文件/文件夹已还原。");
    }

    /// <summary>把窗口内容替换为“进行中 + 进度条”视图。</summary>
    private void ShowProgress(string status)
    {
        _progressText = new TextBlock
        {
            Text = status,
            TextWrapping = TextWrapping.Wrap,
            HorizontalTextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var stack = new StackPanel { Padding = new Thickness(24, 36, 24, 24), Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 400 };
        stack.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        stack.Children.Add(_progressText);
        stack.Children.Add(_progressBar);
        stack.Children.Add(new TextBlock { Text = "请稍候…", HorizontalTextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        ShowResult(stack);
    }

    /// <summary>把内容显示到窗口中，并保留顶部 Mica 标题栏拖拽区域与自适应大小。</summary>
    private void ShowResult(FrameworkElement content, Action? onEnter = null)
    {
        var root = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var drag = new Rectangle { Height = 36, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(drag);
        root.Children.Add(content);

        // 回车触发指定动作（默认无）
        root.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                onEnter?.Invoke();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        Content = root;
        try { SetTitleBar(drag); } catch { }
        FitToContent();
    }

    /// <summary>更新进度条与状态文字。</summary>
    private void UpdateProgress(int percent, string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_progressBar != null) _progressBar.Value = Math.Clamp(percent, 0, 100);
        });
        if (_progressText != null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_progressText != null) _progressText.Text = status;
            });
        }
    }

    /// <summary>把窗口内容替换为“加密完成 + 恢复码”结果页。</summary>
    private void ShowRecovery(string recovery)
    {
        var stack = new StackPanel { Padding = new Thickness(24, 36, 24, 24), Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = "\uE73E", FontSize = 22, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] },
                new TextBlock { Text = "加密完成", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] }
            }
        });
        stack.Children.Add(new TextBlock
        {
            Text = "请务必保存以下恢复码（忘记密码时可用它解锁）：",
            TextWrapping = TextWrapping.Wrap,
            HorizontalTextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = recovery, FontFamily = new FontFamily("Consolas"), FontSize = 18, TextWrapping = TextWrapping.Wrap, HorizontalTextAlignment = TextAlignment.Center }
        });

        // 操作栏：复制 + 我已保存（居中）
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var copy = new Button { Content = "复制" };
        copy.Click += (_, _) =>
        {
            copy.Content = "已复制";
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(recovery);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            finally
            {
                // 保持可粘贴（否则应用退出后剪贴板内容失效）
                try { Windows.ApplicationModel.DataTransfer.Clipboard.Flush(); } catch { }
            }
        };
        var save = new Button { Content = "我已保存" };
        save.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        ShowResult(stack);
    }

    /// <summary>把窗口内容替换为完成提示。</summary>
    private void ShowDone(string title, string message)
    {
        var stack = new StackPanel { Padding = new Thickness(24, 36, 24, 24), Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = "\uE73E", FontSize = 22, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] },
                new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] }
            }
        });
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, HorizontalTextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var ok = new Button
        {
            Content = "确定",
            HorizontalAlignment = HorizontalAlignment.Center,
            MinWidth = 96,
            // 与加密确认按钮一致：白字 + 主题蓝背景
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        ok.Click += (_, _) => Close();
        stack.Children.Add(ok);
        ShowResult(stack, onEnter: () => Close());
    }

    private void OnModeToggled(object sender, RoutedEventArgs e)
    {
        bool rec = ModeSwitch.IsOn;
        PasswordBox.Visibility = rec ? Visibility.Collapsed : Visibility.Visible;
        RecoveryBox.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        UpdateOkEnabled();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdateOkEnabled();

    /// <summary>实时更新密码强度条与文字。</summary>
    private void UpdateStrength(string? pwd)
    {
        int score = string.IsNullOrEmpty(pwd)
            ? 0
            : PasswordPolicy.ScoreStrength(pwd);
        var level = PasswordPolicy.LevelOf(score);
        int segments = PasswordPolicy.Segments(score);

        // 颜色随强度变化
        Microsoft.UI.Xaml.Media.Brush color = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        switch (level)
        {
            case PasswordStrength.Weak: color = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]; break;
            case PasswordStrength.Medium: color = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]; break;
            case PasswordStrength.Strong: color = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]; break;
        }

        Seg1.Fill = segments >= 1 ? color : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        Seg2.Fill = segments >= 2 ? color : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        Seg3.Fill = segments >= 3 ? color : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        Seg4.Fill = segments >= 4 ? color : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];

        StrengthText.Text = pwd is null or "" ? "密码强度：未输入" : $"密码强度：{PasswordPolicy.LevelText(level)}";
        StrengthText.Foreground = pwd is null or "" ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : color;
    }

    /// <summary>根据输入内容决定“确定”按钮是否可用，并实时校验两次密码是否一致。</summary>
    private void UpdateOkEnabled()
    {
        if (_encryptMode)
        {
            string pwd = PasswordBox.Password;
            string confirm = ConfirmBox.Password;

            bool satisfied = PasswordPolicy.IsSatisfied(pwd);
            bool matched = pwd == confirm;

            // 实时更新密码强度条
            UpdateStrength(pwd);

            // 实时校验：确认密码已填写且不一致时，提示“密码不一致”，并保持确定按钮不可用
            // 提示区始终占据固定高度（MinHeight），只改文字，不改变窗口大小/位置。
            if (confirm.Length > 0 && !matched)
            {
                HintText.Text = "两次输入的密码不一致。";
            }
            else if (HintText.Text == "两次输入的密码不一致。")
            {
                HintText.Text = "";
            }

            OkButton.IsEnabled = satisfied && matched;
        }
        else
        {
            // 解密：密码或恢复码任一非空即可确定
            bool rec = ModeSwitch.IsOn;
            OkButton.IsEnabled = rec
                ? !string.IsNullOrEmpty(RecoveryBox.Text)
                : !string.IsNullOrEmpty(PasswordBox.Password);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        OkButton.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmBox.IsEnabled = !busy;
        RecoveryBox.IsEnabled = !busy;
        ModeSwitch.IsEnabled = !busy;
    }

    private void ShowHint(string msg)
    {
        HintText.Text = msg;
        HintText.Visibility = Visibility.Visible;
        // 注意：不在此处调整窗口大小，避免输入时窗口跳动；
        // 表单已预留足够高度容纳提示。
    }

    /// <summary>根据当前内容自适应窗口大小（宽与高都适应）并放在鼠标附近。</summary>
    private void FitToContent()
    {
        FitOnce(0);
    }

    private void FitOnce(int attempt)
    {
        if (attempt > 10) return; // 上限
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (Content is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
                {
                    int w = (int)Math.Ceiling(fe.ActualWidth) + 70;
                    int h = (int)Math.Ceiling(fe.ActualHeight) + 100;
                    if (w < 380) w = 380;
                    if (h < 440) h = 440;
                    if (h > 680) h = 680;
                    // 调整大小后保持屏幕居中（本方法仅用于结果页）
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
                    CenterOnScreen();
                }
                else
                {
                    FitOnce(attempt + 1);
                }
            }
            catch { }
        });
    }

    /// <summary>把窗口水平垂直居中到屏幕中央（所在显示器工作区）。</summary>
    private void CenterOnScreen()
    {
        try
        {
            var wa = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            var sz = AppWindow.Size;
            int x = wa.X + (wa.Width - sz.Width) / 2;
            int y = wa.Y + (wa.Height - sz.Height) / 2;
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { }
    }

    #region 静态入口
    /// <summary>创建加密输入窗口（调用方负责 Activate）。</summary>
    public static PromptWindow ShowEncrypt(string path) => new PromptWindow(path, true);

    /// <summary>创建解密输入窗口（调用方负责 Activate）。</summary>
    public static PromptWindow ShowDecrypt(string path) => new PromptWindow(path, false);
    #endregion
}
