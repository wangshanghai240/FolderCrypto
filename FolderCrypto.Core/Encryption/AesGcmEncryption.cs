using System.Security.Cryptography;

namespace FolderCrypto.Core.Encryption;

/// <summary>
/// AES-256-GCM 对称加密工具。提供对流式数据的加密与解密，
/// 自动生成/校验随机 nonce，并附带认证标签与密文。
/// </summary>
public static class AesGcmEncryption
{
    private const int NonceSizeBytes = 12;   // 96-bit recommended GCM nonce
    private const int TagSizeBytes = 16;     // 128-bit authentication tag

    /// <summary>
    /// 加密明文：返回 [nonce(12) | ciphertext | tag(16)] 拼接后的数据。
    /// </summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes + ciphertext.Length, TagSizeBytes);
        return result;
    }

    /// <summary>
    /// 解密由 <see cref="Encrypt"/> 产生的数据。
    /// 认证失败（密码错误/数据被篡改）会抛出 <see cref="CryptographicException"/>。
    /// </summary>
    public static byte[] Decrypt(byte[] key, byte[] data)
    {
        if (data.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("数据长度不足，无法解密（可能密码错误或文件已损坏）。");

        byte[] nonce = new byte[NonceSizeBytes];
        Array.Copy(data, 0, nonce, 0, NonceSizeBytes);

        int cipherLen = data.Length - NonceSizeBytes - TagSizeBytes;
        byte[] ciphertext = new byte[cipherLen];
        byte[] tag = new byte[TagSizeBytes];
        Array.Copy(data, NonceSizeBytes, ciphertext, 0, cipherLen);
        Array.Copy(data, NonceSizeBytes + cipherLen, tag, 0, TagSizeBytes);

        byte[] plaintext = new byte[cipherLen];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
