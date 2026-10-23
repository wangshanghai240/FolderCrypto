namespace FolderCrypto.App.Services;

/// <summary>从 Shell 扩展或命令行传来的操作指令。</summary>
public enum CommandKind
{
    /// <summary>对路径加密。</summary>
    Encrypt,
    /// <summary>对容器解密。</summary>
    Decrypt,
    /// <summary>从文件夹空白处触发：让用户在程序内选择要加密的项。</summary>
    EncryptHere,
    /// <summary>
    /// 共享菜单项（推荐）：由程序自行检测路径状态，
    /// 未加密则加密，已加密则解密。
    ///
    /// 为什么需要这个模式：
    ///   部分第三方文件资源管理器（如 Pulse）只枚举注册表里的静态 \shell\verb，
    ///   既不支持 IExplorerCommand 动态显隐，也不支持 AppliesTo / SubCommands 过滤。
    ///   于是「加密」「解密」两个静态项会同时出现，无法靠 COM 提供状态来区分。
    ///   改用单一菜单项 + 运行时判定，即可在所有外壳中获得正确行为。
    /// </summary>
    Toggle,
}

/// <summary>一条待处理指令。</summary>
public sealed record ShellCommand(CommandKind Kind, string Path);

/// <summary>
/// 解析命令行参数。约定：
///   <c>--encrypt &lt;path&gt;</c> / <c>--decrypt &lt;path&gt;</c> / <c>--encrypt-here &lt;folder&gt;</c>
/// 参数前导的 <c>-</c> 与 <c>/</c> 都会被去掉，因此 <c>encrypt</c>、<c>-encrypt</c>、
/// <c>/encrypt</c> 三种写法等价。
/// </summary>
public static class CommandLineParser
{
    public static ShellCommand? Parse(string[] args)
        => ParseArgs(args, skipExecutable: false);

    /// <summary>
    /// 解析命令行参数。约定：<c>--encrypt &lt;path&gt;</c> / <c>--decrypt &lt;path&gt;</c>。
    /// 当 <paramref name="skipExecutable"/> 为 true 时忽略 args[0]（程序自身路径）。
    /// </summary>
    public static ShellCommand? ParseArgs(string[] args, bool skipExecutable)
    {
        if (args == null) return null;

        int start = skipExecutable ? 1 : 0;
        if (args.Length - start < 2)
            return null;

        string verb = args[start].TrimStart('-', '/').ToLowerInvariant();
        string path = args[start + 1];

        if (string.IsNullOrWhiteSpace(path))
            return null;

        return verb switch
        {
            "encrypt" or "e"
                => new ShellCommand(CommandKind.Encrypt, path),

            "decrypt" or "d"
                => new ShellCommand(CommandKind.Decrypt, path),

            // 文件夹空白处右键「加密选中」：注册表中使用的参数名是 encrypt-here，
            // 早期版本漏处理该动词，导致背景菜单点击后程序按「普通启动」打开主窗口，
            // 或（未打包运行时）静默退出。这里补齐。
            "encrypt-here" or "encrypthere" or "encrypt_here" or "eh"
                => new ShellCommand(CommandKind.EncryptHere, path),

            // 共享菜单项：由程序检测状态后决定加密还是解密。
            "toggle" or "t" or "encrypt-or-decrypt"
                => new ShellCommand(CommandKind.Toggle, path),

            _ => null
        };
    }
}
