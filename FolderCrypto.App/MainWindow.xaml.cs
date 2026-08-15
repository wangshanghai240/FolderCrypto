using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Pickers;
using FolderCrypto.App.Services;
using FolderCrypto.Core.Services;

namespace FolderCrypto.App;

public sealed partial class MainWindow : Window
{
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
        InitSettingsUi();

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

    private void InitSettingsUi()
    {
        ThemeMode mode = ThemeService.Mode;
        rbSystem.IsChecked = mode == ThemeMode.System;
        rbLight.IsChecked = mode == ThemeMode.Light;
        rbDark.IsChecked = mode == ThemeMode.Dark;
        rbCustom.IsChecked = mode == ThemeMode.Custom;

        rbCustomLight.IsChecked = ThemeService.CustomIsLight;
        rbCustomDark.IsChecked = !ThemeService.CustomIsLight;

        UpdateAccentPreview();
        UpdateCustomPanelVisibility();
    }

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

    private void UpdateAccentPreview()
    {
        if (AccentPreviewText != null)
        {
            AccentPreviewText.Text = "当前强调色：" + ThemeService.AccentHex;
        }
    }

    private void UpdateCustomPanelVisibility()
    {
        if (CustomPanel != null)
        {
            CustomPanel.Visibility = ThemeService.Mode == ThemeMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // ---------- 加密/解密 ----------

    private async void OnEncryptFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinUIHelper.InitializePicker(this, picker);
        picker.FileTypeFilter.Add("*");
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null) return;

        // 打开加密输入窗口（独立窗口，Win11 圆角；取消/关闭不会影响主界面状态）
        var w = FolderCrypto.App.Dialogs.PromptWindow.ShowEncrypt(file.Path);
        w.Activate();
        SetStatus("已打开加密窗口");
    }

    private async void OnEncryptFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinUIHelper.InitializePicker(this, picker);
        picker.FileTypeFilter.Add("*");
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        // 打开加密输入窗口（独立窗口，Win11 圆角）
        var w = FolderCrypto.App.Dialogs.PromptWindow.ShowEncrypt(folder.Path);
        w.Activate();
        SetStatus("已打开加密窗口");
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

        // 已加密：打开解密输入窗口（独立窗口，Win11 圆角）
        var w = FolderCrypto.App.Dialogs.PromptWindow.ShowDecrypt(path);
        w.Activate();
        SetStatus("已打开解密窗口");
    }

    private void SetStatus(string message)
    {
        FooterText.Text = message;
        StatusBar.IsOpen = true;
        StatusBar.Message = message;
    }

    private void OnDismissStatus(object sender, RoutedEventArgs e)
    {
        StatusBar.IsOpen = false;
    }
}
