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

    /// <summary>
    /// 当前使用的 PBKDF2 迭代次数（OWASP 对 PBKDF2-HMAC-SHA256 的建议下限为 600,000）。
    /// 新加密的文件使用此值。
    /// </summary>
    public const int Iterations = 600_000;

    /// <summary>
    /// 历史迭代次数（200,000）。旧文件以此派生，
    /// 解密时需回退尝试，否则旧文件将无法解开。
    /// </summary>
    public const int LegacyIterations = 200_000;

    /// <summary>派生密钥。</summary>
    public byte[] Key { get; }

    /// <summary>用于派生该密钥的随机盐。</summary>
    public byte[] Salt { get; }

    /// <summary>用于验证密码正确性的校验哈希。</summary>
    public byte[] Verifier { get; }

    /// <summary>派生该密钥时实际使用的迭代次数（写入容器头，供解密时还原）。</summary>
    public int UsedIterations { get; }

    private PasswordHasher(byte[] key, byte[] salt, byte[] verifier, int usedIterations)
    {
        Key = key;
        Salt = salt;
        Verifier = verifier;
        UsedIterations = usedIterations;
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
        byte[] key = DeriveKey(password, salt, Iterations);
        byte[] verifier = ComputeVerifier(key);

        return new PasswordHasher(key, salt, verifier, Iterations);
    }

    /// <summary>使用给定盐从密码派生密钥（还原阶段用，默认当前迭代次数）。</summary>
    public static byte[] DeriveKey(string password, byte[] salt)
        => DeriveKey(password, salt, Iterations);

    /// <summary>使用给定盐与迭代次数从密码派生密钥。</summary>
    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations), "迭代次数必须为正数。");

        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySizeBytes);
    }

    /// <summary>计算密钥的验证哈希。</summary>
    private static byte[] ComputeVerifier(byte[] key)
        => SHA256.HashData(key);

    /// <summary>
    /// 校验密码是否正确。
    /// 依次尝试当前迭代次数与历史迭代次数（旧文件兼容），
    /// 使用固定时间比较防时序攻击。
    /// </summary>
    public static bool Verify(string password, byte[] salt, byte[] expectedVerifier)
    {
        if (expectedVerifier.Length != VerifierSizeBytes)
            return false;   // 校验器长度非法：直接判失败，避免 FixedTimeEquals 静默返回 false 造成误判

        // 依次尝试 [当前, 历史（旧文件）]
        Span<int> attempts = stackalloc int[2];
        attempts[0] = Iterations;
        attempts[1] = LegacyIterations;

        foreach (int iters in attempts)
        {
            if (iters <= 0) continue;
            byte[] key = DeriveKey(password, salt, iters);
            try
            {
                if (CryptographicOperations.FixedTimeEquals(ComputeVerifier(key), expectedVerifier))
                    return true;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);   // 派生密钥用完即清零
            }
        }
        return false;
    }

    /// <summary>序列化盐（供写入容器头部）。</summary>
    public static string SaltToBase64(byte[] salt) => Convert.ToBase64String(salt);
}
