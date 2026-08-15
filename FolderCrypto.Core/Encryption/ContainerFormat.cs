using System.Text;

namespace FolderCrypto.Core.Encryption;

/// <summary>
/// 定义 .fenc 容器文件的二进制布局。
///
/// 布局（所有多字节按小端序）：
///   [0..8)            magic    "FOLDERCR" (8 bytes, 文件格式标识)
///   [8..9)            version  byte = 1
///   [9..21)           salt     16 bytes
///   [21..53)          verifier 32 bytes (键验证哈希)
///   [53..57)          metaLen  int32, 加密元数据的长度
///   [57..57+metaLen)  meta     加密后的元数据(JSON 清单) = AES-GCM(metaKey, plainMeta)
///   [之后)             payload  加密后的文件内容块流
///
/// 说明：
///   - 元数据与负载使用同一派生密钥，但通过 HKDF 拆分为 metaKey 与 dataKey，
///     以符合密钥分离的最佳实践。
///   - payload 由若干“块”组成，每块 = AES-GCM(随机nonce, 内容段)。
/// </summary>
public static class ContainerFormat
{
    public const string Magic = "FOLDERCR";     // 8 bytes
    public const byte Version = 1;
    public const int SaltSize = 16;
    public const int VerifierSize = 32;
    public const int MetaLengthFieldSize = 4;

    /// <summary>自定义容器文件扩展名。</summary>
    public const string Extension = ".fenc";

    /// <summary>Header 固定部分长度（不含元数据）。</summary>
    public static readonly int FixedHeaderSize =
        Magic.Length + 1 + SaltSize + VerifierSize + MetaLengthFieldSize;

    public static ReadOnlySpan<byte> MagicBytes => Encoding.ASCII.GetBytes(Magic);

    public static bool HasMatchingMagic(ReadOnlySpan<byte> firstBytes)
    {
        if (firstBytes.Length < Magic.Length)
            return false;
        return firstBytes[..Magic.Length].SequenceEqual(MagicBytes);
    }
}
