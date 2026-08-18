using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using FolderCrypto.App.Services;
using FolderCrypto.Core.Services;

namespace FolderCrypto.App;

public partial class App : Application
{
    private Window? _window;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    // 主窗口引用与托盘状态（托盘常驻用）
    private static Window? _mainWindow;
    private static bool _trayShown;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 记录 UI 线程的调度器，供单实例转发指令时把窗口创建投递到 UI 线程。
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // 初始化并应用主题（浅色/深色/自定义）。
        ThemeService.Init();

        // 若已经有实例在运行，则把命令行指令转发给已有实例并退出，实现单实例。
        if (SingleInstanceManager.TryForwardOrBecomePrimary(Environment.GetCommandLineArgs(), OnShellCommand))
        {
            // 必须真正退出应用，否则 WinUI 的调度循环会一直运行，造成“无窗口的悬挂进程，
            // 再次打开时因单实例又转发到该悬挂进程，导致看起来无法打开主程序”。
            try { if (Microsoft.UI.Xaml.Application.Current != null) Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
            return;
        }

        string[] cloneArgs = Environment.GetCommandLineArgs();
        var cmd = CommandLineParser.ParseArgs(cloneArgs, skipExecutable: true);
        bool autostart = cloneArgs.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

        if (cmd != null)
        {
            // 从右键菜单启动（加密/解密）：直接弹出输入窗口（内容即表单），不显示主界面。
            _window = ShowPrompt(cmd);
        }
        else if (autostart)
        {
            // 开机自启：静默后台托盘常驻，不弹出主窗口。
            EnterBackgroundTray();
        }
        else
        {
            // 正常启动：显示主窗口（设置）。
            ShowMainWindow();
        }

        // 窗口创建后再次应用主题，确保自定义强调色在首屏渲染后也能立即生效。
        ThemeService.Apply();
    }

    /// <summary>创建（若已存在则激活）主窗口。</summary>
    private static void ShowMainWindow()
    {
        var w = _mainWindow;
        if (w == null)
        {
            w = new MainWindow();
            // 关闭主窗口时不退出应用，而是隐藏并进入「后台托盘常驻」，
            // 保留托盘图标以便用户随时重新打开或退出。
            w.Closed += (win, e) =>
            {
                _mainWindow = null;
                CurrentWindow = null;
                EnterBackgroundTray();
            };
            _mainWindow = w;
        }
        CurrentWindow = w;
        try { w.Activate(); } catch { }
    }

    /// <summary>进入后台托盘常驻模式（显示托盘图标；若已显示则幂等）。</summary>
    private static void EnterBackgroundTray()
    {
        if (_trayShown) return;
        _trayShown = true;

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        string? icon = ResolveLockIconPath();
        try
        {
            TrayIconService.Show(
                dispatcher,
                "Folder Crypto 文件夹加密",
                icon ?? string.Empty,
                onOpenSettings: ShowMainWindow,
                onExit: () => { try { if (Microsoft.UI.Xaml.Application.Current != null) Microsoft.UI.Xaml.Application.Current.Exit(); } catch { } });
        }
        catch { }
    }

    /// <summary>根据命令类型创建并显示对应的输入窗口（加密=密码+确认；解密=密码/恢复码），并返回该窗口。</summary>
    private static Microsoft.UI.Xaml.Window ShowPrompt(ShellCommand cmd)
    {
        var w = cmd.Kind == CommandKind.Encrypt
            ? FolderCrypto.App.Dialogs.PromptWindow.ShowEncrypt(cmd.Path)
            : FolderCrypto.App.Dialogs.PromptWindow.ShowDecrypt(cmd.Path);
        ActivateToForeground(w);
        return w;
    }

    /// <summary>激活窗口并强制带到前台（当主程序已在任务栏时，确保右键弹窗显示到用户屏幕）。</summary>
    private static void ActivateToForeground(Microsoft.UI.Xaml.Window w)
    {
        try { w.Activate(); } catch { }
        try
        {
            IntPtr h = WinRT.Interop.WindowNative.GetWindowHandle(w);
            if (h != IntPtr.Zero)
            {
                // 置顶再取消置顶，确保它跑到 Z 次序最前；然后设为前台。
                NativeMethods.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 /*SWP_NOMOVE|SWP_NOSIZE*/);
                NativeMethods.SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0040 /*SWP_NOACTIVATE*/);
                NativeMethods.SetForegroundWindow(h);
            }
        }
        catch { }
    }

    /// <summary>
    /// 让窗口标题栏(表头)跟随当前深浅主题，避免黑暗模式下标题栏仍是浅色。
    /// </summary>
    public static void ApplyWindowTitleBar(Window window)
    {
        try
        {
            var tb = window.AppWindow.TitleBar;
            if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;

            // 从窗口内容的「实际解析主题」判断深浅（跟随系统时 root.RequestedTheme=Default，
            // ActualTheme 反映系统真实主题）。
            bool dark;
            try
            {
                dark = window.Content is FrameworkElement fe &&
                       fe.ActualTheme == ElementTheme.Dark;
            }
            catch
            {
                dark = Services.ThemeService.ResolvedTheme == ApplicationTheme.Dark;
            }
            byte r = dark ? (byte)32 : (byte)243;
            byte g = dark ? (byte)32 : (byte)243;
            byte b = dark ? (byte)32 : (byte)243;
            var bg = Windows.UI.Color.FromArgb(255, r, g, b);
            var fg = dark
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 0, 0, 0);

            tb.BackgroundColor = bg;
            tb.ForegroundColor = fg;
            tb.InactiveBackgroundColor = bg;
            tb.InactiveForegroundColor = fg;
            // 按钮底色透明，让 Mica 材质透出（悬停/按下时才有反馈底色）
            var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            tb.ButtonBackgroundColor = transparent;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveBackgroundColor = transparent;
            tb.ButtonInactiveForegroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 200, 200, 200)
                : Windows.UI.Color.FromArgb(255, 90, 90, 90);
            tb.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 60, 60, 60)
                : Windows.UI.Color.FromArgb(255, 220, 220, 220);
            tb.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(255, 90, 90, 90)
                : Windows.UI.Color.FromArgb(255, 190, 190, 190);
        }
        catch
        {
            // 标题栏自定义不可用时忽略
        }
    }

    /// <summary>
    /// 为窗口启用 Mica（亚克力）材质，并把内容延伸到标题栏区域，
    /// 让「表头/标题栏」也呈现 Mica 背景（仅 Windows 11；不支持时自动回退）。
    /// </summary>
    public static void ApplyMicaBackdrop(Window window)
    {
        try
        {
            window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        catch { }

        try
        {
            window.ExtendsContentIntoTitleBar = true;
            var tb = window.AppWindow.TitleBar;
            if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                // 让标题栏按钮底色透明，融合到 Mica 中
                var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                bool dark;
                try
                {
                    dark = window.Content is FrameworkElement rfe &&
                           rfe.ActualTheme == ElementTheme.Dark;
                }
                catch
                {
                    dark = Services.ThemeService.ResolvedTheme == ApplicationTheme.Dark;
                }
                var fg = dark
                    ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(255, 0, 0, 0);
                tb.ButtonBackgroundColor = transparent;
                tb.ButtonInactiveBackgroundColor = transparent;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = fg;
                tb.ButtonHoverBackgroundColor = dark
                    ? Windows.UI.Color.FromArgb(255, 60, 60, 60)
                    : Windows.UI.Color.FromArgb(255, 220, 220, 220);
                tb.ButtonPressedBackgroundColor = dark
                    ? Windows.UI.Color.FromArgb(255, 90, 90, 90)
                    : Windows.UI.Color.FromArgb(255, 190, 190, 190);
            }
        }
        catch { }

        // Windows 11 圆角窗口
        SetWindowRoundedCorners(window);
        // 任务栏/标题栏图标
        ApplyWindowIcon(window);
    }

    /// <summary>设置圆形(HWND)窗口圆角（DWMWA_WINDOW_CORNER_PREFERENCE=ROUND），仅 Windows 11 生效。</summary>
    public static void SetWindowRoundedCorners(Window window)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            int pref = DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>为窗口设置任务栏/标题栏图标（确保任务栏显示清晰的锁图标而非灰色块）。</summary>
    public static void ApplyWindowIcon(Window window)
    {
        try
        {
            string? ico = ResolveLockIconPath();
            if (ico != null && System.IO.File.Exists(ico))
            {
                // 1) AppWindow 方式（标题栏 + 任务栏 + Alt+Tab）
                window.AppWindow.SetIcon(ico);

                // 2) Win32 兜底：直接对 HWND 设置图ICON（小图标用于任务栏/标题栏）
                SetWindowIconWin32(window, ico);
            }
        }
        catch { }
    }

    /// <summary>通过 Win32 直接设置窗口的小/大图标（任务栏灰色块问题的确定性修复）。</summary>
    private static void SetWindowIconWin32(Window window, string icoPath)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            const int ICON_SMALL = 0;
            const int ICON_BIG = 1;
            const int LR_LOADFROMFILE = 0x0010;
            const int LR_DEFAULTSIZE = 0x0040;
            const int WM_SETICON = 0x0080;

            // 大图标(32x32, 用于 Alt+Tab) + 小图标(16x16, 用于任务栏/标题栏)
            IntPtr big = NativeMethods.LoadImage(
                IntPtr.Zero, icoPath, 1 /*IMAGE_ICON*/,
                32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            IntPtr small = NativeMethods.LoadImage(
                IntPtr.Zero, icoPath, 1 /*IMAGE_ICON*/,
                16, 16, LR_LOADFROMFILE | LR_DEFAULTSIZE);

            if (big != IntPtr.Zero)
            {
                NativeMethods.SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), big);
            }
            if (small != IntPtr.Zero)
            {
                NativeMethods.SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), small);
            }
            // 注意：SendMessage 转交后由窗口/系统负责销毁图标（不可手动 DestroyIcon 这里）
        }
        catch { }
    }

    /// <summary>解析 LockIcon.ico 的运行时路径（支持打包 MSIX 与未打包运行）。</summary>
    private static string? ResolveLockIconPath()
    {
        // 打包(MSIX)环境下图标在安装目录 Assets 下（Package.Current 在未打包时访问会抛异常，故置于 try 内）
        try
        {
            string p = System.IO.Path.Combine(
                Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                "Assets", "LockIcon.ico");
            if (System.IO.File.Exists(p)) return p;
        }
        catch { }

        // 未打包/开发运行：尝试输出目录 Assets 下
        try
        {
            string p = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "LockIcon.ico");
            if (System.IO.File.Exists(p)) return p;
        }
        catch { }
        return null;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint type, int cx, int cy, uint fuLoad);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }

    /// <summary>
    /// 单实例转发过来的命令行。可能由后台管道线程调用，
    /// 必须投递到 UI 线程后再创建并显示输入窗口。
    /// 若为普通启动（无加密/解密指令），则在后台常驻实例上打开主窗口。
    /// </summary>
    private void OnShellCommand(string[] args)
    {
        var cmd = CommandLineParser.ParseArgs(args, skipExecutable: true);

        void show()
        {
            if (cmd != null)
            {
                var w = ShowPrompt(cmd);
                _window = w;
                CurrentWindow = w;
            }
            else
            {
                // 前台再次启动主程序：打开主窗口（设置）。
                ShowMainWindow();
            }
        }

        // 若已在 UI 线程则直接执行；否则通过启动时记录的 UI 调度器投递。
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() == _uiDispatcher)
        {
            show();
        }
        else
        {
            _uiDispatcher?.TryEnqueue(show);
        }
    }

    /// <summary>提供主窗口引用供其它对话框宿主使用。</summary>
    public static Window? CurrentWindow { get; private set; }
}
