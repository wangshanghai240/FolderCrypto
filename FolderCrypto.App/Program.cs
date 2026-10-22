// ===========================================================================
//  显式入口点（替代 XAML 编译器生成的 Main）
//
//  为什么需要自己写 Main：
//  ---------------------------------------------------------------------------
//  FolderCrypto.App 以 MSI 方式「未打包（unpackaged）」部署，但 csproj 里
//  一直是 <WindowsPackageType>MSIX</WindowsPackageType>。XAML 编译器据此生成的
//  Main 只调用 Application.Start，**不会**初始化 Windows App SDK 的
//  Bootstrap（动态依赖）。未打包进程缺少包身份，XAML 初始化会失败；
//  而 Application.Start 内部失败时进程直接 exit(0)，
//  既不弹窗、也不写事件日志 —— 表现就是「双击/右键菜单点了没反应」。
//
//  这里显式调用 Bootstrap.Initialize 完成运行时引导，并把任何异常写到
//  %LOCALAPPDATA%\FolderCrypto\startup-error.log 并弹窗，杜绝静默退出。
// ===========================================================================

using System;
using System.IO;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace FolderCrypto.App;

public static class Program
{
    private const int WindowsAppSDKRuntimeMajorMinor = 0x00010006; // Windows App SDK 1.6

    [STAThread]
    private static int Main(string[] args)
    {
        var log = new StartupLog();

        try
        {
            log.Step("Main 入口");

            // 1) 引导 Windows App SDK（未打包场景必需）。
            //    若已通过其它方式引导（例如以 MSIX 包身份运行），会返回失败，
            //    这种情况下直接继续即可，不应视为致命错误。
            BootstrapInitializer.Ensure(log);

            // 2) 初始化 COM 包装器并启动 XAML 应用。
            log.Step("InitializeComWrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();

            log.Step("Application.Start");
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });

            log.Step("Application.Start 正常返回");
            return 0;
        }
        catch (Exception ex)
        {
            log.Failure(ex);
            return 1;
        }
        finally
        {
            StartupLog.FlushPending(); // 记录「OnLaunched 是否真的跑到」用于排查
        }
    }

    /// <summary>Windows App SDK 自包含引导。</summary>
    private static class BootstrapInitializer
    {
        public static void Ensure(StartupLog log)
        {
            try
            {
                log.Step("Bootstrap.TryInitialize");
                bool ok = Bootstrap.TryInitialize(
                    WindowsAppSDKRuntimeMajorMinor,
                    /* versionTag */ null,
                    out int hr);

                if (ok)
                {
                    log.Step("Bootstrap.TryInitialize 成功");
                }
                else
                {
                    // 已存在引导（MSIX 包身份运行时即为这种情形）时继续。
                    log.Step($"Bootstrap.TryInitialize 返回 false (hr=0x{hr:X8})，按已引导处理");
                }
            }
            catch (Exception ex)
            {
                // 引导失败不直接抛出：让 Application.Start 给出更准确的错误。
                log.Step("Bootstrap.TryInitialize 异常: " + ex.Message);
            }
        }
    }
}

/// <summary>启动诊断日志：记录启动阶段，出错时落盘并提示用户。</summary>
internal sealed class StartupLog
{
    private static readonly StringBuilder _trace = new();
    private static readonly DateTime _traceStart = DateTime.Now;
    private readonly StringBuilder _sb = new();
    private readonly DateTime _start = DateTime.Now;

    public static bool ReachedOnLaunched { get; set; }

    /// <summary>OnLaunched 内部的细粒度追踪（每次调用立即落盘）。</summary>
    public static void Trace(string msg)
    {
        lock (_trace)
        {
            _trace.AppendLine($"  +{DateTime.Now.Subtract(_traceStart).TotalMilliseconds,7:F0}ms  {msg}");
            try
            {
                File.AppendAllText(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FolderCrypto", "startup-trace.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { }
        }
    }

    public StartupLog()
    {
        _sb.AppendLine($"[{_start:yyyy-MM-dd HH:mm:ss}] FolderCrypto 启动");
        _sb.AppendLine($"  命令行: {string.Join(' ', Environment.GetCommandLineArgs())}");
        _sb.AppendLine($"  基目录: {AppContext.BaseDirectory}");
        _sb.AppendLine($"  是否打包: {IsPackaged()}");
    }

    public void Step(string msg)
    {
        _sb.AppendLine($"  +{DateTime.Now.Subtract(_start).TotalMilliseconds,7:F0}ms  {msg}");
        // 每步都落盘，保证进程被强杀/静默退出时也有记录可查。
        WriteToDisk(_sb.ToString());
    }

    public void Failure(Exception ex)
    {
        _sb.AppendLine("  !! 异常: " + ex);
        WriteToDisk(_sb.ToString());
        try
        {
            MessageBoxW(IntPtr.Zero,
                "FolderCrypto 启动失败：\r\n" + ex.Message +
                "\r\n\r\n详细信息已写入：\r\n" + LogPath(),
                "FolderCrypto", 0x00000010 /*MB_ICONERROR*/);
        }
        catch { }
    }

    public static void FlushPending()
    {
        try
        {
            if (!ReachedOnLaunched)
            {
                WriteToDisk(
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 警告：Application.Start 返回前未进入 OnLaunched。\r\n");
            }
        }
        catch { }
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current.Id.Name;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string LogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderCrypto", "startup-error.log");

    private static void WriteToDisk(string content)
    {
        try
        {
            string p = LogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content, Encoding.UTF8);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
