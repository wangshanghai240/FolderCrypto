namespace FolderCrypto.Core.Model;

/// <summary>
/// 容器内的清单，记录被加密的对象元信息，便于解密时还原原文件/目录结构。
/// 以 JSON 形式序列化，并作为元数据块加密后写入容器。
/// </summary>
public sealed class ContainerManifest
{
    /// <summary>原始路径是文件还是目录。</summary>
    public bool IsDirectory { get; set; }

    /// <summary>原始文件/目录名（不含完整路径，保证容器可迁移）。</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>原始内容的总字节数（仅用于展示）。</summary>
    public long OriginalSize { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>内部条目清单（用于目录）。值为相对路径，键为可解析标识。</summary>
    public List<ManifestEntry> Entries { get; set; } = new();
}

/// <summary>容器中的单个条目。</summary>
public sealed class ManifestEntry
{
    /// <summary>相对路径（使用 '/' 分隔）。文件条目为文件路径，目录条目以 '/' 结尾。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; set; }

    /// <summary>该文件在 payload 中的偏移（字节）。目录条目忽略。</summary>
    public long PayloadOffset { get; set; }

    /// <summary>该文件加密后的长度（字节）。</summary>
    public long EncryptedLength { get; set; }
}
