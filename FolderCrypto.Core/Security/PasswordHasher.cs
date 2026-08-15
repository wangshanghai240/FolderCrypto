using System.Security.Cryptography;
using System.Text;

namespace FolderCrypto.Core.Security;

/// <summary>
/// 基于 PBKDF2 的密钥派生与验证器。
/// 从用户密码派生 256 位 AES 密钥，并生成固定的验证哈希，
/// 用于校验用户输入的密码是否正确（无需存储明文密码）。
/// </summary>
public sealed class PasswordHasher
{
    private const int KeySizeBytes = 32;          // 256-bit AES key
    private const int SaltSizeBytes = 16;         // 128-bit salt
    private const int VerifierSizeBytes = 32;     // SHA-256 verifier
    private const int Iterations = 200_000;       // PBKDF2 iteration count

    /// <summary>派生密钥。</summary>
    public byte[] Key { get; }

    /// <summary>用于派生该密钥的随机盐。</summary>
    public byte[] Salt { get; }

    /// <summary>用于验证密码正确性的校验哈希。</summary>
    public byte[] Verifier { get; }

    private PasswordHasher(byte[] key, byte[] salt, byte[] verifier)
    {
        Key = key;
        Salt = salt;
        Verifier = verifier;
    }

    /// <summary>从明文密码派生新的密钥、盐与验证器。</summary>
    public static PasswordHasher Derive(string password)
    {
        if (!PasswordPolicy.IsSatisfied(password))
        {
            throw new ArgumentException(
                $"密码不符合强度要求：{string.Join("；", PasswordPolicy.Validate(password))}",
                nameof(password));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] key = DeriveKey(password, salt);
        byte[] verifier = ComputeVerifier(key);

        return new PasswordHasher(key, salt, verifier);
    }

    /// <summary>使用给定盐从密码派生密钥（还原阶段用）。</summary>
    public static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySizeBytes);
    }

    /// <summary>计算密钥的验证哈希。</summary>
    private static byte[] ComputeVerifier(byte[] key)
        => SHA256.HashData(key);

    /// <summary>使用固定常量时间比较验证器是否一致，防时序攻击。</summary>
    public static bool Verify(string password, byte[] salt, byte[] expectedVerifier)
    {
        byte[] key = DeriveKey(password, salt);
        byte[] actual = ComputeVerifier(key);
        return CryptographicOperations.FixedTimeEquals(actual, expectedVerifier);
    }

    /// <summary>序列化盐（供写入容器头部）。</summary>
    public static string SaltToBase64(byte[] salt) => Convert.ToBase64String(salt);
}
