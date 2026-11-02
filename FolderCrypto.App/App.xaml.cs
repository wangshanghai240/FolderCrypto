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

    public App()
    {
        InitializeComponent();

        // 全局退出兜底：无论进程以何种方式结束（托盘退出、任务管理器结束、
        // 系统关机、未处理异常……），都尽力把临时解锁的明文重新加密。
        //
        // ⚠️ 注意 ProcessExit 的时间预算很有限（尤其关机场景），且此时
        // 工作线程可能已被回收，所以这里只做「尽力而为」的一次尝试；
        // 真正可靠的是托盘「退出程序」路径上的 ExitWithCleanup（有明确超时预算）。
        // 任何未完成项都会保留在 temp-unlock.json 中，由下次启动的
        // CleanupOnStartup 继续处理，不会丢失。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { TemporaryUnlockService.ReencryptAllOnExit(TimeSpan.FromSeconds(5)); }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 标记 XAML 已真正启动，供 Program 的启动日志判断是否「静默退出」。
        StartupLog.ReachedOnLaunched = true;
        StartupLog.Trace("OnLaunched 进入");

        // 记录 UI 线程的调度器，供单实例转发指令时把窗口创建投递到 UI 线程。
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // 初始化并应用主题（浅色/深色/自定义）。
        StartupLog.Trace("ThemeService.Init");
        ThemeService.Init();
        // 初始化应用设置（如是否显示系统托盘图标）。
        StartupLog.Trace("SettingsService.Init");
        SettingsService.Init();
        // 后台预热「Windows Hello 凭据是否已存在」的缓存，
        // 使后续 UI 构造函数能同步读取而不必在 UI 线程发起跨进程调用。
        StartupLog.Trace("HelloSecretStore.WarmUpCache");
        HelloSecretStore.WarmUpCache();
        // 兜底：扫描临时解密清单，自动重新加密残留的明文。
        // ⚠️ 必须放到后台线程：它会访问 Windows 凭据管理器（PasswordVault，跨进程 WinRT 调用），
        // 在 UI 线程同步执行会拖慢甚至卡住启动。此处不等待完成即可继续启动流程。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { TemporaryUnlockService.CleanupOnStartup(); } catch { }
        });

        // 一级右键菜单自愈：确保当前用户已注册 sparse 包。
        // 覆盖“跨账户提权安装时安装器无法为实际用户注册”的情形；后台执行、best-effort。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { SparseMenuRegistrar.EnsureRegistered(); } catch { }
        });

        // 若已经有实例在运行，则把命令行指令转发给已有实例并退出，实现单实例。
        StartupLog.Trace("SingleInstanceManager");
        if (SingleInstanceManager.TryForwardOrBecomePrimary(Environment.GetCommandLineArgs(), OnShellCommand))
        {
            StartupLog.Trace("检测到已有实例 -> 转发后退出");
            // 必须真正退出应用，否则 WinUI 的调度循环会一直运行，造成“无窗口的悬挂进程，
            // 再次打开时因单实例又转发到该悬挂进程，导致看起来无法打开主程序”。
            try { if (Microsoft.UI.Xaml.Application.Current != null) Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
            return;
        }

        string[] cloneArgs = Environment.GetCommandLineArgs();
        var cmd = CommandLineParser.ParseArgs(cloneArgs, skipExecutable: true);
        bool autostart = cloneArgs.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));
        StartupLog.Trace($"命令解析完成: cmd={cmd?.Kind.ToString() ?? "null"}, autostart={autostart}");

        if (cmd != null)
        {
            // 从右键菜单启动（加密/解密）：直接弹出输入窗口（内容即表单），不显示主界面。
            _window = ShowPrompt(cmd);

            // 若本次弹窗启动了「临时解密」的后台监测，关闭窗口时不能让进程退出，
            // 否则监测随进程消失、不会自动重新加密。
            // 注意：仅仅 EnterBackgroundTray（托盘消息窗口）无法阻止 WinUI 在最后一个
            // Window 关闭后退出进程。因此这里在窗口“即将关闭”时拦截：仍有未完成临时解锁
            // 就 Cancel 关闭 + 隐藏窗口（窗口对象仍在 → 进程存活），待全部重新加密完成后再关窗退出。
            if (_window != null)
                SetupTempUnlockKeepAlive(_window);
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

            // 关闭主窗口时的行为，由设置决定：
            //   最小化到托盘（默认）：拦截关闭 + 隐藏窗口（窗口对象存活 → 进程常驻后台），
            //                        托盘/监测继续工作，可随时从托盘重新打开。
            //   直接退出：拦截默认关闭 → 隐藏窗口 → 先做退出收尾（重新加密临时解锁项）→ 退出进程。
            // 注意：仅靠 EnterBackgroundTray 无法阻止 WinUI 在窗口关闭后退出进程，必须隐藏窗口保活。
            var appWin = w.AppWindow;
            appWin.Closing += (s, e) =>
            {
                bool toTray = SettingsService.CloseToTray && SettingsService.ShowTrayIcon;
                e.Cancel = true;                    // 先拦截，避免窗口直接关闭导致进程退出
                try { appWin.Hide(); } catch { }    // 隐藏窗口（窗口对象仍在 → 进程存活）

                if (toTray)
                {
                    EnterBackgroundTray();          // 后台常驻，托盘图标可重新打开
                }
                else
                {
                    ExitWithCleanupCore(fromWindowClose: true); // 直接退出：先弹窗告知/确认并重新加密临时解锁项，再退出
                }
            };

            w.Closed += (win, e) =>
            {
                _mainWindow = null;
                CurrentWindow = null;
            };
            _mainWindow = w;
        }
        else
        {
            // 若窗口之前被「最小化到托盘」隐藏，重新打开时先显示出来。
            try { w.AppWindow.Show(); } catch { }
        }
        CurrentWindow = w;
        try { w.Activate(); } catch { }
    }

    /// <summary>
    /// 为右键弹窗设置「临时解锁期间保活」：窗口被关闭时，若仍有未完成的临时解锁，
    /// 则取消关闭并隐藏窗口（窗口对象仍在 → 进程存活，监测可继续运行），并显示托盘图标；
    /// 待全部重新加密完成后，再真正关闭窗口、进程退出。
    /// </summary>
    private static void SetupTempUnlockKeepAlive(Window w)
    {
        try
        {
            var appWin = w.AppWindow;
            bool forcedClose = false;

            appWin.Closing += (s, e) =>
            {
                if (forcedClose) return;

                bool pending;
                try { pending = TemporaryUnlockService.HasActiveUnlocks(); } catch { pending = false; }
                if (!pending) return;   // 没有待办 → 正常关闭，进程退出

                e.Cancel = true;                          // 阻止关闭，保留窗口
                try { appWin.Hide(); } catch { }          // 隐藏窗口（窗口对象仍在 → 进程存活）
                try { EnterBackgroundTray(); } catch { }  // 显示托盘图标，提示后台仍在守护

                // 后台轮询：所有临时解锁都重新加密完成后，真正关闭窗口（进程随后退出）
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            bool still;
                            try { still = TemporaryUnlockService.HasActiveUnlocks(); } catch { still = false; }
                            if (!still) break;
                            await System.Threading.Tasks.Task.Delay(2000).ConfigureAwait(false);
                        }
                        w.DispatcherQueue?.TryEnqueue(() =>
                        {
                            forcedClose = true;
                            try { w.Close(); } catch { }
                        });
                    }
                    catch { }
                });
            };
        }
        catch { }
    }

    /// <summary>进入后台托盘常驻模式（显示托盘图标；若已显示则幂等）。</summary>
    private static void EnterBackgroundTray()
    {
        TrayIconService.Log($"EnterBackgroundTray: ShowTrayIcon={SettingsService.ShowTrayIcon}");

        // 用户关闭「显示系统托盘图标」时不显示图标，仅保持后台静默常驻。
        if (!SettingsService.ShowTrayIcon) return;

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        string? icon = ResolveLockIconPath();
        try
        {
            TrayIconService.Show(
                dispatcher,
                "Folder Crypto 文件夹加密",
                icon ?? string.Empty,
                onOpenSettings: ShowMainWindow,
                onExit: ExitWithCleanup);
        }
        catch (Exception ex) { TrayIconService.Log($"EnterBackgroundTray 异常: {ex}"); }
    }

    /// <summary>
    /// 退出应用，并在退出前先把仍处于临时解锁（明文）状态的项重新加密。
    ///
    /// ⚠️ 没有这一步，用户从托盘「退出程序」后：
    /// 监测循环随进程消失 → 明文永久留在磁盘上，且不会被任何机制发现，
    /// 直到用户下次启动应用才由 CleanupOnStartup 兜底（可能很久之后）。
    ///
    /// 收尾在后台线程执行并有超时，避免大文件加密把退出流程卡住。
    /// </summary>
    private static void ExitWithCleanup() => ExitWithCleanupCore(fromWindowClose: false);

    /// <summary>
    /// 退出前收尾：若仍有临时解密（明文）项，先弹窗列出并确认，再重新加密并退出。
    /// </summary>
    /// <param name="fromWindowClose">true 表示由「关闭主窗口→直接退出」触发（取消退出时需重新显示窗口）。</param>
    private static void ExitWithCleanupCore(bool fromWindowClose)
    {
        try
        {
            var pending = TemporaryUnlockService.GetPendingPaths();
            if (pending.Count > 0 && !ConfirmReencryptOnExit(pending))
            {
                // 用户选择取消退出
                if (fromWindowClose && _mainWindow != null)
                {
                    try { _mainWindow.AppWindow.Show(); } catch { }
                    try { _mainWindow.Activate(); } catch { }
                }
                return;   // 不退出
            }

            // 用较短超时保证"点了退出就退出"；未完成的条目保留在清单里，
            // 下次启动会继续处理，不会丢。
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                try { TemporaryUnlockService.ReencryptAllOnExit(TimeSpan.FromSeconds(20)); }
                catch (Exception ex) { TrayIconService.Log($"退出收尾异常: {ex}"); }
            });

            // 最多再等 25 秒，超时也照常退出（剩余项交由下次启动兜底）
            task.Wait(TimeSpan.FromSeconds(25));
        }
        catch { }

        try { if (Microsoft.UI.Xaml.Application.Current != null) Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
    }

    /// <summary>退出前弹窗列出仍处于临时解密（明文）状态的项，询问是否重新加密并退出。返回 false 表示取消退出。</summary>
    private static bool ConfirmReencryptOnExit(System.Collections.Generic.List<string> pending)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("以下文件/文件夹仍处于「临时解密（明文）」状态：");
            sb.AppendLine();
            const int max = 15;
            int shown = 0;
            foreach (var p in pending)
            {
                if (shown >= max) { sb.AppendLine($"… 等，共 {pending.Count} 项"); break; }
                sb.AppendLine("· " + p);
                shown++;
            }
            sb.AppendLine();
            sb.AppendLine("退出前会尝试重新加密它们；正被其他程序占用（如正在播放/编辑）的会保留，下次启动时再处理。");
            sb.AppendLine();
            sb.Append("是否重新加密并退出？");

            System.IntPtr owner = System.IntPtr.Zero;
            try { if (_mainWindow != null) owner = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow); } catch { }

            const uint MB_OKCANCEL = 0x00000001;
            const uint MB_ICONWARNING = 0x00000030;
            const uint MB_TOPMOST = 0x00040000;
            int r = NativeMethods.MessageBoxW(owner, sb.ToString(), "FolderCrypto 退出", MB_OKCANCEL | MB_ICONWARNING | MB_TOPMOST);
            return r == 1; // IDOK
        }
        catch { return true; } // 弹窗失败时默认继续退出，避免卡住
    }

    /// <summary>根据命令类型创建并显示对应的输入窗口（加密=密码+确认；解密=密码/恢复码），并返回该窗口。</summary>
    private static Microsoft.UI.Xaml.Window ShowPrompt(ShellCommand cmd)
    {
        Microsoft.UI.Xaml.Window? w = cmd.Kind switch
        {
            CommandKind.Encrypt => FolderCrypto.App.Dialogs.PromptWindow.ShowEncrypt(cmd.Path),
            CommandKind.Decrypt => FolderCrypto.App.Dialogs.PromptWindow.ShowDecrypt(cmd.Path),

            // 共享菜单项：先检测目标当前状态，再决定弹哪个窗口。
            // 这是为了兼容只枚举静态注册表动词、不支持 IExplorerCommand
            // 动态显隐的第三方文件资源管理器（如 Pulse）。
            CommandKind.Toggle => ShowTogglePrompt(cmd.Path),

            // 文件夹空白处「加密选中」：没有明确目标项，交给主窗口让用户自行选择。
            _ => null,
        };

        if (w == null)
        {
            ShowMainWindow();
            return _mainWindow!;
        }

        ActivateToForeground(w);
        return w;
    }

    /// <summary>
    /// 「加密/解密」共享菜单项：按目标的实际状态决定动作。
    /// 已加密（含 .folderlock 文件夹、FCENC000 文件、FOLDERCR 容器）→ 解密；
    /// 否则 → 加密。
    /// </summary>
    private static Microsoft.UI.Xaml.Window? ShowTogglePrompt(string path)
    {
        bool encrypted;
        try
        {
            encrypted = InPlaceEncryptionService.IsEncrypted(path) || ContainerService.IsContainer(path);
        }
        catch (Exception ex)
        {
            StartupLog.Trace($"Toggle 状态检测异常({path}): {ex.Message}");
            // 检测失败时默认按「加密」处理，至少给出可见反馈而不是什么都不做。
            encrypted = false;
        }

        StartupLog.Trace($"Toggle: {path} -> {(encrypted ? "已加密，执行解密" : "未加密，执行加密")}");

        return encrypted
            ? FolderCrypto.App.Dialogs.PromptWindow.ShowDecrypt(path)
            : FolderCrypto.App.Dialogs.PromptWindow.ShowEncrypt(path);
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

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
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

    /// <summary>立即显示系统托盘图标（用户开启“随系统启动”后进入后台常驻，无需等下次登录）。</summary>
    public static void ShowTrayIcon() => EnterBackgroundTray();

    /// <summary>隐藏系统托盘图标（用户关闭「显示系统托盘图标」时调用）。</summary>
    public static void HideTrayIcon()
    {
        try { TrayIconService.Hide(); } catch { }
    }
}
