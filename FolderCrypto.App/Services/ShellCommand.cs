namespace FolderCrypto.App.Services;

/// <summary>从 Shell 扩展或命令行传来的操作指令。</summary>
public enum CommandKind
{
    /// <summary>对路径加密。</summary>
    Encrypt,
    /// <summary>对容器解密。</summary>
    Decrypt,
}

/// <summary>一条待处理指令。</summary>
public sealed record ShellCommand(CommandKind Kind, string Path);

/// <summary>
/// 解析命令行参数。约定：<c>--encrypt &lt;path&gt;</c> / <c>--decrypt &lt;path&gt;</c>。
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

        return verb switch
        {
            "encrypt" or "e" when !string.IsNullOrEmpty(path)
                => new ShellCommand(CommandKind.Encrypt, path),
            "decrypt" or "d" when !string.IsNullOrEmpty(path)
                => new ShellCommand(CommandKind.Decrypt, path),
            _ => null
        };
    }
}
