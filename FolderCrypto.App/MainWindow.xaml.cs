using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Pickers;
using FolderCrypto.App.Dialogs;
using FolderCrypto.App.Services;
using FolderCrypto.Core.Services;

namespace FolderCrypto.App;

public sealed partial class MainWindow : Window
{
    /// <summary>初始化阶段抑制开机自启开关事件，避免启动时误触发。</summary>
    private bool _suppressAutoStartEvent = true;

    /// <summary>初始化阶段抑制「显示托盘图标」开关事件，避免初始化时误触发显示/隐藏。</summary>
    private bool _suppressTrayIconEvent = true;

    /// <summary>初始化阶段抑制「Windows Hello 解锁」开关事件，避免初始化时误触发。</summary>
    private bool _suppressHelloEvent = true;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Folder Crypto 文件夹加密";
        // Mica（亚克力）背景材质 + 表头也呈现 Mica（Windows 11）
        App.ApplyMicaBackdrop(this);
        // 注册到主题服务，由它应用主题并刷新标题栏(表头)
        ThemeService.RegisterWindow(this);
        try { SetTitleBar(DragTitleBar); } catch { }

        // 默认选中首页
        MainNav.SelectedItem = MainNav.MenuItems[0];

        // 根据已保存的主题设置初始化设置页 UI
        UpdateSettingsUi();

        // 开机自启开关：按当前注册表状态初始化（抑制事件，避免误触发开关/托盘）
        _suppressAutoStartEvent = true;
        AutoStartSwitch.IsOn = StartupService.IsEnabled;
        _suppressAutoStartEvent = false;
        UpdateAutoStartUi();

        // 托盘图标开关：按已保存设置初始化（抑制事件，避免初始化时误触发显示/隐藏）
        _suppressTrayIconEvent = true;
        ShowTrayIconSwitch.IsOn = SettingsService.ShowTrayIcon;
        _suppressTrayIconEvent = false;

        // Windows Hello 解锁开关：按已保存设置初始化
        _suppressHelloEvent = true;
        HelloSwitch.IsOn = SettingsService.WindowsHelloUnlock;
        _suppressHelloEvent = false;
        UpdateHelloUi();

        // 关于：显示当前版本号
        VersionText.Text = "v" + UpdateService.CurrentVersion;
        UpdateDownloadDirText();

        // 主题变化时刷新设置页 UI
        ThemeService.Changed += () => UpdateSettingsUi();

        // 先同步设定一个接近内容的窗口大小并居中，避免首次出现“默认大窗口(白屏)”再跳变。
        ApplyInitialWindowSize();

        // 关键：不立刻 Activate()（由 App 配合）。等当前帧布局完成后，再统一
        // “拟合大小 + 内容可见 + 激活”，确保首帧不会闪现白屏/Mica 大窗口。
        DispatcherQueue.TryEnqueue(() =>
        {
            RootGrid.Opacity = 1;
            FitWindowToContent();
            RevealAfterReady();
        });
    }

    /// <summary>在布局就绪后显示窗口（由 App 延迟 Activate 配合，消除白屏闪动）。</summary>
    internal void RevealAfterReady()
    {
        try
        {
            // 重新应用窗口材质与图标（确保激活时就是正确外观）
            App.ApplyMicaBackdrop(this);
            App.ApplyWindowTitleBar(this);
            if (!_activated)
            {
                _activated = true;
                Activate();
            }
        }
        catch { }
    }

    private bool _activated;

    /// <summary>在窗口首次显示前给出接近内容的初始尺寸，避免“白屏大窗口”闪烁。</summary>
    private void ApplyInitialWindowSize()
    {
        try
        {
            // 估算宽 = 导航栏(260) + 内容卡片(约 420) + 边距；高 600
            int w = 260 + 420 + 56; // ≈ 736
            if (w < 520) w = 520;
            if (w > 1100) w = 1100;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, 600));
            CenterOnScreen();
        }
        catch { }
    }

    /// <summary>让主窗口宽度自适应内容（随卡片/导航栏实际宽度），并居中到屏幕。</summary>
    private void FitWindowToContent()
    {
        try
        {
            // 请求一次布局，让控件算出自然宽度
            MainNav.UpdateLayout();
            var contentStack = FindContentStack();
            double contentWidth = contentStack?.ActualWidth ?? 0;
            if (contentWidth < 200) contentWidth = 420; // 兜底

            // 窗口总宽 = 内容区宽 + 导航栏宽度 + 标题栏边距
            double navWidth = MainNav.OpenPaneLength > 100 ? MainNav.OpenPaneLength : 260;
            int w = (int)Math.Ceiling(contentWidth + navWidth + 56);
            if (w < 520) w = 520;                        // 最小宽度
            if (w > 1100) w = 1100;                      // 最大宽度
            int h = 600;
            if (h > 820) h = 820;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            CenterOnScreen();
        }
        catch { }
    }

    /// <summary>在内容区找到卡片 StackPanel（其 ActualWidth 反映内容需要的宽度）。</summary>
    private Microsoft.UI.Xaml.FrameworkElement? FindContentStack()
    {
        // HomeContent 是 ScrollViewer，其内容 StackPanel 决定所需宽度
        if (HomeContent is Microsoft.UI.Xaml.Controls.ScrollViewer sv && sv.Content is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            return fe;
        }
        return null;
    }

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

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        bool settings = string.Equals(tag, "Settings", StringComparison.Ordinal);
        var show = settings ? SettingsContent : HomeContent;
        var hide = settings ? HomeContent : SettingsContent;
        if (show == hide) return;

        PanContent(show, hide, fromRight: settings);
    }

    /// <summary>
    /// 切换页面时让右侧内容「平移」滑入（而不是压缩/硬切）：
    /// 切到设置从右滑入，切回首页从左滑入。像面包屑一样平移动画。
    /// </summary>
    private void PanContent(FrameworkElement show, FrameworkElement hide, bool fromRight)
    {
        const double pan = 60; // 平移距离(px)

        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;

        var translate = new TranslateTransform { X = 0, Y = 0 };
        show.RenderTransform = translate;

        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = fromRight ? pan : -pan,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, translate);
        Storyboard.SetTargetProperty(anim, "X");
        sb.Children.Add(anim);
        sb.Begin();
        sb.Completed += (s, e) =>
        {
            // 动画结束复位，避免残留偏移
            translate.X = 0;
        };
    }

    // ---------- 主题设置 UI ----------

    private void UpdateSettingsUi()
    {
        // 控件在构造函数 InitializeComponent 后已创建，此回调必定在窗口就绪后触发。
        rbSystem.IsChecked = ThemeService.Mode == ThemeMode.System;
        rbLight.IsChecked = ThemeService.Mode == ThemeMode.Light;
        rbDark.IsChecked = ThemeService.Mode == ThemeMode.Dark;
        rbCustom.IsChecked = ThemeService.Mode == ThemeMode.Custom;
        rbCustomLight.IsChecked = ThemeService.CustomIsLight;
        rbCustomDark.IsChecked = !ThemeService.CustomIsLight;
        UpdateAccentPreview();
        UpdateCustomPanelVisibility();
    }

    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true) return;
        var mode = (rb.Tag?.ToString()) switch
        {
            "System" => ThemeMode.System,
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            "Custom" => ThemeMode.Custom,
            _ => ThemeMode.System,
        };
        ThemeService.SetMode(mode);
    }

    private void OnCustomBaseChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true) return;
        ThemeService.SetCustomBase(rb == rbCustomLight);
    }

    private void OnAccentSwatch(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hex) return;
        ThemeService.SetCustomAccent(hex);
    }

    // ---------- 开机自启 ----------

    /// <summary>开/关滑动开关：点击切换“随系统启动”；开启时立即显示托盘图标进入后台常驻。</summary>
    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoStartEvent) return;

        bool want = AutoStartSwitch.IsOn;
        if (!StartupService.SetEnabled(want))
        {
            // 写入失败：回滚开关并提示（例如注册表被策略锁定）
            _suppressAutoStartEvent = true;
            AutoStartSwitch.IsOn = !want;
            _suppressAutoStartEvent = false;
            _ = DialogHelper.ShowInfo(this, want ? "启用开机自启失败。" : "取消开机自启失败。");
            return;
        }

        if (want)
        {
            // 开启后立即显示系统托盘图标，进入后台常驻（无需等待重启/关闭窗口）
            App.ShowTrayIcon();
        }

        UpdateAutoStartUi();
        SetStatus(want ? "已开启开机自启" : "已关闭开机自启");
    }

    private void UpdateAutoStartUi()
    {
        AutoStartStatusText.Text = AutoStartSwitch.IsOn
            ? "当前状态：已开启，登录后将在后台静默常驻。"
            : "当前状态：未开启。";
    }

    // ---------- 系统托盘图标显示 ----------

    /// <summary>开/关滑动开关：切换是否在系统托盘中显示程序图标。</summary>
    private void OnShowTrayIconToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressTrayIconEvent) return;

        bool show = ShowTrayIconSwitch.IsOn;
        SettingsService.ShowTrayIcon = show;

        if (show)
        {
            // 打开后立即显示托盘图标
            App.ShowTrayIcon();
        }
        else
        {
            // 关闭后立即隐藏托盘图标
            App.HideTrayIcon();
        }
    }

    // ---------- Windows Hello 解锁 ----------

    /// <summary>开/关滑动开关：开启时需先保存一份密码或恢复码作为解锁凭据。</summary>
    private async void OnHelloToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHelloEvent) return;

        bool want = HelloSwitch.IsOn;
        if (want)
        {
            // 开启前必须通过 Windows Hello 认证（PIN/人脸/指纹）
            var status = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(
                "开启 Windows Hello 解锁");
            if (status != Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified)
            {
                // 认证失败：回滚开关并提示
                _suppressHelloEvent = true;
                HelloSwitch.IsOn = false;
                _suppressHelloEvent = false;
                UpdateHelloUi();
                await DialogHelper.ShowInfo(this, "Windows Hello 验证未通过，无法开启。");
                return;
            }

            // 认证通过后，要求设置一份用于替代密码的解锁密码（存入凭据管理器）
            string? pwd = await DialogHelper.ShowSetPasswordDialogAsync(this);
            if (string.IsNullOrEmpty(pwd))
            {
                // 取消：回滚开关
                _suppressHelloEvent = true;
                HelloSwitch.IsOn = false;
                _suppressHelloEvent = false;
                UpdateHelloUi();
                return;
            }
            HelloSecretStore.SaveSecret("password", pwd);
            SettingsService.WindowsHelloUnlock = true;
            SetStatus("已开启 Windows Hello 解锁");
        }
        else
        {
            // 关闭：清除已保存的凭据
            HelloSecretStore.ClearSecret();
            SettingsService.WindowsHelloUnlock = false;
            SetStatus("已关闭 Windows Hello 解锁");
        }
        UpdateHelloUi();
    }

    private void UpdateHelloUi()
    {
        if (HelloStatusText == null) return;
        HelloStatusText.Text = SettingsService.WindowsHelloUnlock
            ? "已开启：右键“解密”时将用 Windows Hello（PIN/人脸）代替输入密码。"
            : "开启后，右键“解密”可用 Windows Hello（PIN/人脸）代替输入密码解锁文件/文件夹。";
    }

    // ---------- 软件更新 ----------

    /// <summary>刷新「下载位置」显示文本。</summary>
    private void UpdateDownloadDirText()
    {
        var dir = SettingsService.DownloadDirectory;
        DownloadDirText.Text = string.IsNullOrWhiteSpace(dir)
            ? "默认（系统下载文件夹）"
            : dir;
    }

    /// <summary>「更改…」按钮：让用户自选安装包下载目录并持久化保存。</summary>
    private async void OnChangeDownloadDir(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        catch { }

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        SettingsService.DownloadDirectory = folder.Path;
        UpdateDownloadDirText();
        UpdateStatusText.Text = $"下载目录已设为：{folder.Path}";
    }

    /// <summary>「检查更新」按钮：查询 GitHub Releases 最新版本并给出下载入口。</summary>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var result = await UpdateService.CheckAsync();

            if (result.CheckFailed)
            {
                UpdateStatusText.Text = "检查更新失败，请检查网络连接后重试。";
                return;
            }
            if (result.NoRelease)
            {
                UpdateStatusText.Text = "发布仓库暂无可用版本。";
                return;
            }
            if (!result.Available)
            {
                UpdateStatusText.Text = $"当前已是最新版本（v{UpdateService.CurrentVersion}）。";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 v{result.LatestVersion}。";
            await PromptUpdateAsync(result);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>弹窗询问用户如何处理新版本。</summary>
    private async Task PromptUpdateAsync(UpdateCheckResult result)
    {
        var content = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 12, Width = 380 };
        content.Children.Add(new TextBlock
        {
            Text = $"当前版本：v{UpdateService.CurrentVersion}　→　最新版本：v{result.LatestVersion}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(result.Notes))
        {
            var notes = new TextBlock
            {
                Text = result.Notes,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 220
            };
            ScrollViewer.SetVerticalScrollBarVisibility(notes, ScrollBarVisibility.Auto);
            content.Children.Add(notes);
        }

        var dialog = new ContentDialog
        {
            Title = "发现新版本",
            Content = content,
            PrimaryButtonText = "下载更新",
            SecondaryButtonText = "前往发布页",
            CloseButtonText = "稍后再说",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content?.XamlRoot
        };

        if (dialog.XamlRoot == null) return;

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary)
        {
            await DownloadAndInstallAsync(result);
        }
        else if (choice == ContentDialogResult.Secondary)
        {
            OpenUrl(result.ReleasePageUrl ?? "https://github.com/wangshanghai240/FolderCrypto/releases");
        }
    }

    /// <summary>下载安装包（显示不确定进度对话框），完成后启动安装或打开下载目录。</summary>
    private async Task DownloadAndInstallAsync(UpdateCheckResult result)
    {
        if (string.IsNullOrEmpty(result.DownloadUrl))
        {
            // 没有找到安装包附件，引导用户去发布页下载
            OpenUrl(result.ReleasePageUrl ?? "https://github.com/wangshanghai240/FolderCrypto/releases");
            return;
        }

        var downloadBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        var downloadText = new TextBlock
        {
            Text = $"正在下载 v{result.LatestVersion} 安装包到「下载」目录… 0%",
            TextWrapping = TextWrapping.Wrap
        };
        var progress = new ContentDialog
        {
            Title = "正在下载更新",
            Content = new Microsoft.UI.Xaml.Controls.StackPanel
            {
                Spacing = 12,
                Children =
                {
                    downloadBar,
                    downloadText
                }
            },
            CloseButtonText = "取消",
            XamlRoot = Content?.XamlRoot
        };
        if (progress.XamlRoot == null) return;

        string url = result.DownloadUrl;
        string fileName = url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            ? $"FolderCrypto-Setup-{result.LatestVersion}-x64.msi"
            : url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? $"FolderCrypto-Setup-{result.LatestVersion}-x64.exe"
                : $"FolderCrypto-便携版-{result.LatestVersion}.zip";

        // 模态显示下载进度对话框，同时后台下载；用户点「取消」关闭对话框即中止
        var dialogClosed = new TaskCompletionSource<bool>();
        progress.Closed += (s, ev) => dialogClosed.TrySetResult(true);
        _ = progress.ShowAsync();

        var downloadProgress = new Progress<int>(p =>
        {
            downloadBar.Value = Math.Clamp(p, 0, 100);
            downloadText.Text = $"正在下载 v{result.LatestVersion} 安装包到「下载」目录… {Math.Clamp(p, 0, 100)}%";
        });
        var downloadTask = UpdateService.DownloadAsync(url, fileName, downloadProgress);
        var finished = await Task.WhenAny(dialogClosed.Task, downloadTask);
        if (finished == dialogClosed.Task)
        {
            UpdateStatusText.Text = "已取消下载。";
            return;
        }

        var (path, error) = await downloadTask;
        try { progress.Hide(); } catch { }

        if (string.IsNullOrEmpty(path))
        {
            UpdateStatusText.Text = $"下载失败：{error ?? "未知错误"}。";
            await ShowDownloadFailedDialogAsync(result, error);
            return;
        }

        UpdateStatusText.Text = $"已下载安装包：{path}";
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // 便携版 ZIP：打开下载目录由用户自行解压
            OpenExplorer(System.IO.Path.GetDirectoryName(path));
        }
        else
        {
            // EXE / MSI 安装包：直接启动（会触发 UAC/安装向导）
            UpdateService.Launch(path);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>下载失败时弹出对话框，可一键前往发布页用浏览器手动下载（可绕开应用内 TLS 问题）。</summary>
    private async Task ShowDownloadFailedDialogAsync(UpdateCheckResult result, string? error)
    {
        var dialog = new ContentDialog
        {
            Title = "下载失败",
            Content = new TextBlock
            {
                Text = $"未能自动下载安装包。\n\n原因：{error ?? "未知错误"}\n\n你可以前往 GitHub 发布页手动下载并安装。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "前往发布页",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content?.XamlRoot
        };
        if (dialog.XamlRoot == null) return;

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary)
        {
            OpenUrl(result.ReleasePageUrl ?? "https://github.com/wangshanghai240/FolderCrypto/releases");
        }
    }

    private static void OpenExplorer(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
    }

    private void UpdateAccentPreview()
    {
        AccentPreviewText.Text = "当前强调色：" + ThemeService.AccentHex;
    }

    private void UpdateCustomPanelVisibility()
    {
        CustomPanel.Visibility = ThemeService.Mode == ThemeMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- 加密/解密 ----------

    private async void OnEncryptFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinUIHelper.InitializePicker(this, picker);
        picker.FileTypeFilter.Add("*");
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null) return;

        // 打开加密输入窗口（独立窗口，Win11 圆角）；窗口关闭时恢复底部状态
        OpenPrompt(PromptWindow.ShowEncrypt(file.Path), "已打开加密窗口");
    }

    private async void OnEncryptFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinUIHelper.InitializePicker(this, picker);
        picker.FileTypeFilter.Add("*");
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        // 打开加密输入窗口（独立窗口，Win11 圆角）；窗口关闭时恢复底部状态
        OpenPrompt(PromptWindow.ShowEncrypt(folder.Path), "已打开加密窗口");
    }

    private async void OnDecrypt(object sender, RoutedEventArgs e)
    {
        // 先选择要解密的是「文件」还是「文件夹」
        var kindDialog = new ContentDialog
        {
            Title = "选择要解密的对象",
            Content = "请选择是解密一个文件，还是解密一个文件夹。",
            PrimaryButtonText = "选择文件",
            SecondaryButtonText = "选择文件夹",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        var kind = await kindDialog.ShowAsync();
        if (kind != ContentDialogResult.Primary && kind != ContentDialogResult.Secondary) return;
        bool chooseFolder = kind == ContentDialogResult.Secondary;

        // 让用户选择目标路径
        string? path = null;
        if (chooseFolder)
        {
            var fp = new FolderPicker();
            WinUIHelper.InitializePicker(this, fp);
            fp.FileTypeFilter.Add("*");
            var folder = await fp.PickSingleFolderAsync();
            path = folder?.Path;
        }
        else
        {
            var picker = new FileOpenPicker();
            WinUIHelper.InitializePicker(this, picker);
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            path = file?.Path;
        }
        if (string.IsNullOrEmpty(path)) return;

        // 校验该文件/文件夹是否确实被加密过；未加密则提示，不再弹出解密窗口。
        if (!InPlaceEncryptionService.IsEncrypted(path))
        {
            SetStatus("未加密，无需解密");
            await DialogHelper.ShowInfo(this, "该文件/文件夹未被加密，无需解密。");
            return;
        }

        // 已加密：打开解密输入窗口（独立窗口，Win11 圆角）；窗口关闭时恢复底部状态
        OpenPrompt(PromptWindow.ShowDecrypt(path), "已打开解密窗口");
    }

    private void SetStatus(string message)
    {
        FooterText.Text = message;
        StatusBar.IsOpen = true;
        StatusBar.Message = message;
    }

    /// <summary>打开加密/解密窗口并刷新状态；窗口关闭时把底部状态栏恢复为“就绪”。</summary>
    private void OpenPrompt(PromptWindow window, string openingStatus)
    {
        window.Closed += (_, _) => ResetStatus();
        window.Activate();
        SetStatus(openingStatus);
    }

    /// <summary>恢复底部状态栏为“就绪”，并收起提示条。</summary>
    private void ResetStatus()
    {
        FooterText.Text = "就绪";
        StatusBar.IsOpen = false;
    }
}
