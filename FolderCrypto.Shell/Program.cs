using System;
using FolderCrypto.Shell;

namespace FolderCrypto.Shell;

/// <summary>
/// 安装/卸载工具：
///   FolderCrypto.Shell install "C:\path\to\FolderCrypto.App.exe" [--dll "C:\path\to\FolderCrypto.ShellNative.dll"]
///   FolderCrypto.Shell uninstall [--dll "C:\path\to\FolderCrypto.ShellNative.dll"]
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  FolderCrypto.Shell install <主应用exe路径> [--dll <原生DLL路径>]");
            Console.WriteLine("  FolderCrypto.Shell uninstall [--dll <原生DLL路径>]");
            return 1;
        }

        string verb = args[0].ToLowerInvariant();
        string? dll = ReadOptionalArg(args, "--dll");

        try
        {
            switch (verb)
            {
                case "install":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("install 需要主应用 exe 路径参数。");
                        return 1;
                    }
                    Install(args[1], dll);
                    break;

                case "uninstall":
                    Uninstall(dll);
                    break;

                default:
                    Console.WriteLine($"未知命令: {verb}");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("操作失败: " + ex.Message);
            return 1;
        }

        return 0;
    }

    private static void Install(string exePath, string? nativeDllPath)
    {
        string dll = ResolveNativeDll(nativeDllPath);
        // 右键菜单图标目录：原生 DLL 同目录含 overlay-lock.ico(加密) 与 unlock.ico(解密)
        string iconDir = Path.GetDirectoryName(dll)!;

        Console.WriteLine("正在安装右键菜单...");
        ContextMenuRegistrar.Install(exePath, iconDir);

        Console.WriteLine("正在注册锁图标覆盖层（原生 ATL DLL）...");
        OverlayRegistrar.InstallOverlay(
            dll,
            adminAccessToHklm: IsAdministrator());

        ContextMenuRegistrar.RefreshShell();
        Console.WriteLine("安装完成。若锁图标未立即出现，请重启资源管理器或注销后重新登录。");
    }

    private static void Uninstall(string? nativeDllPath)
    {
        ContextMenuRegistrar.Uninstall();

        string? dll = null;
        try { dll = ResolveNativeDll(nativeDllPath, throwIfMissing: false); } catch { }
        OverlayRegistrar.UninstallOverlay(dll);

        ContextMenuRegistrar.RefreshShell();
        Console.WriteLine("卸载完成。");
    }

    /// <summary>
    /// 定位原生 ATL DLL。顺序：显式参数 &gt; 解决方案构建输出目录 &gt; 本目录。
    /// 目录布局为 $(SolutionDir)$(Platform)\$(Configuration)\FolderCrypto.ShellNative.dll。
    /// </summary>
    private static string ResolveNativeDll(string? explicitPath, bool throwIfMissing = true)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath))
            candidates.Add(explicitPath);

        string shellDir = AppContext.BaseDirectory;
        string solutionDir = Directory.GetParent(shellDir)?.Parent?.Parent?.Parent?.FullName ?? "";
        candidates.AddRange(new[]
        {
            Path.Combine(shellDir, OverlayRegistrar.NativeDllName),
            Path.Combine(solutionDir, "x64", "Release", OverlayRegistrar.NativeDllName),
            Path.Combine(solutionDir, "x64", "Debug", OverlayRegistrar.NativeDllName),
            Path.Combine(solutionDir, "Win32", "Release", OverlayRegistrar.NativeDllName),
        });

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        if (throwIfMissing)
            throw new FileNotFoundException("未找到原生 ATL DLL：" + OverlayRegistrar.NativeDllName + "，请在参数中指定。");
        return string.Empty;
    }

    /// <summary>读取形如 “--key value” 的可选参数值；未提供返回 null。</summary>
    private static string? ReadOptionalArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
