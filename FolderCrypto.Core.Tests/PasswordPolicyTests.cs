using FolderCrypto.Core.Security;
using Xunit;

namespace FolderCrypto.Core.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdefgh1!23")]     // 12位, 含字母/数字/特殊（恰好最小长度）
    [InlineData("a1!b2@c3#d4$")]     // 12位
    [InlineData("!!@@##aa11##BB")]   // 14位
    [InlineData("Xy9!Zz8@Ww7#Qq6$")] // 16位
    public void ValidPasswords_Pass(string password)
    {
        Assert.True(PasswordPolicy.IsSatisfied(password));
        Assert.Empty(PasswordPolicy.Validate(password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short!")]           // 6位：过短
    [InlineData("Abcdef1!")]         // 8位：低于新的 12 位下限
    [InlineData("Abcdefgh1!2")]      // 11位：差一位
    [InlineData("abcdefghijkl")]     // 有字母，无数字无特殊
    [InlineData("123456789012")]     // 只有数字
    [InlineData("!!!!!!!!!!!!")]     // 只有特殊
    [InlineData("abcdefghij12")]     // 字母+数字但无特殊
    public void InvalidPasswords_Fail(string password)
    {
        Assert.False(PasswordPolicy.IsSatisfied(password));
        Assert.NotEmpty(PasswordPolicy.Validate(password));
    }

    [Fact]
    public void BelowMinimumLength_Fails_AndMentionsRequiredLength()
    {
        // 11 位（差一位）应失败，且提示中应包含所需长度
        Assert.False(PasswordPolicy.IsSatisfied("Abcdefgh1!2"));
        Assert.Contains(PasswordPolicy.Validate("Abcdefgh1!2"),
            e => e.Contains(PasswordPolicy.MinLength.ToString()));
    }

    [Fact]
    public void ExactlyMinimumLength_Passes()
    {
        string pwd = new string('a', PasswordPolicy.MinLength - 3) + "1!B";
        Assert.Equal(PasswordPolicy.MinLength, pwd.Length);
        Assert.True(PasswordPolicy.IsSatisfied(pwd));
    }

    [Fact]
    public void NullPasswords_Fails()
    {
        Assert.False(PasswordPolicy.IsSatisfied(null));
    }

    // ---- 密码强度评分 ----

    [Theory]
    [InlineData("", 0)]
    [InlineData("short!1", 0)]              // 过短
    [InlineData("Abcdef1!", 0)]             // 8位：低于最小长度 → 0
    [InlineData("Abcdefgh1!2", 0)]          // 11位：差一位，仍为 0
    [InlineData("abcdefghij12", 1)]         // 12位但类别少（无大写/特殊）
    [InlineData("Abcdefgh1!23", 2)]         // 12位 + 类别齐全
    [InlineData("Abcdefgh123!@#XyZ", 3)]    // 17位 + 类别齐全
    public void Strength_Score_InRange(string pwd, int minScore)
    {
        int score = PasswordPolicy.ScoreStrength(pwd);
        Assert.InRange(score, 0, 4);
        Assert.True(score >= minScore, $"'{pwd}' 评分 {score} 应 >= {minScore}");
    }

    [Fact]
    public void Strength_Monotonic()
    {
        // 更长、更复杂的密码评分不应低于较短较简单的
        Assert.True(PasswordPolicy.ScoreStrength("Abcdefgh123!@#XyZ")
                    >= PasswordPolicy.ScoreStrength("Abcdefgh1!23"));
        Assert.True(PasswordPolicy.ScoreStrength("Abcdefgh1!23")
                    >= PasswordPolicy.ScoreStrength("Abcdefgh1!2"));
    }

    [Fact]
    public void Strength_LongMixed_HighLevel()
    {
        Assert.Equal(PasswordStrength.Strong,
            PasswordPolicy.LevelOf(PasswordPolicy.ScoreStrength("Abcdefgh123!@#XyZ")));
        Assert.Equal(PasswordStrength.VeryWeak,
            PasswordPolicy.LevelOf(PasswordPolicy.ScoreStrength("abc12")));
    }

    // ---- 迭代次数：新值与历史值必须同时存在，否则旧文件无法解开 ----

    [Fact]
    public void Iterations_AreRaisedButLegacyRetained()
    {
        Assert.True(PasswordHasher.Iterations >= 600_000,
            "新文件应使用 OWASP 建议的至少 600,000 次迭代。");
        Assert.True(PasswordHasher.LegacyIterations > 0,
            "必须保留历史迭代次数，否则旧文件将无法解密。");
        Assert.NotEqual(PasswordHasher.Iterations, PasswordHasher.LegacyIterations);
    }

    // ---- 密钥派生兼容性：同一密码/盐在不同迭代次数下应派生不同密钥 ----

    [Fact]
    public void DeriveKey_DifferentIterations_ProduceDifferentKeys()
    {
        byte[] salt = new byte[16];
        for (int i = 0; i < salt.Length; i++) salt[i] = (byte)i;

        byte[] modern = PasswordHasher.DeriveKey("Password123!", salt, PasswordHasher.Iterations);
        byte[] legacy = PasswordHasher.DeriveKey("Password123!", salt, PasswordHasher.LegacyIterations);

        Assert.Equal(32, modern.Length);
        Assert.Equal(32, legacy.Length);
        Assert.NotEqual(modern, legacy);   // 若相同，则提高迭代次数毫无意义
    }

    [Fact]
    public void DeriveKey_SameInputs_AreDeterministic()
    {
        byte[] salt = new byte[16];
        byte[] a = PasswordHasher.DeriveKey("Password123!", salt, PasswordHasher.LegacyIterations);
        byte[] b = PasswordHasher.DeriveKey("Password123!", salt, PasswordHasher.LegacyIterations);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeriveKey_InvalidIterations_Throws()
    {
        byte[] salt = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PasswordHasher.DeriveKey("Password123!", salt, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PasswordHasher.DeriveKey("Password123!", salt, -1));
    }

    // ---- Verify：长度非法的验证器必须安全返回 false（修复前可能静默误判）----

    [Fact]
    public void Verify_RejectsMalformedVerifier()
    {
        byte[] salt = new byte[16];
        byte[] wrongSize = new byte[16];   // 应为 32 字节

        Assert.False(PasswordHasher.Verify("Password123!", salt, wrongSize));
        Assert.False(PasswordHasher.Verify("Password123!", salt, Array.Empty<byte>()));
    }

    [Fact]
    public void Verify_AcceptsCorrectPassword_AcrossBothIterationCounts()
    {
        byte[] salt = new byte[16];
        for (int i = 0; i < salt.Length; i++) salt[i] = (byte)(i * 7);

        foreach (int iters in new[] { PasswordHasher.Iterations, PasswordHasher.LegacyIterations })
        {
            byte[] key = PasswordHasher.DeriveKey("Password123!", salt, iters);
            byte[] verifier = System.Security.Cryptography.SHA256.HashData(key);

            // Verify 内部会依次尝试两种迭代次数，因此两者都应通过
            Assert.True(PasswordHasher.Verify("Password123!", salt, verifier),
                $"以 {iters} 次迭代生成的验证器应能通过校验（旧文件兼容性）。");
            Assert.False(PasswordHasher.Verify("WrongPassword1!", salt, verifier));
        }
    }

    [Fact]
    public void Derive_NewHasher_UsesCurrentIterations()
    {
        var hasher = PasswordHasher.Derive("Password123!");
        Assert.Equal(PasswordHasher.Iterations, hasher.UsedIterations);
        Assert.Equal(32, hasher.Key.Length);
        Assert.Equal(16, hasher.Salt.Length);
        Assert.Equal(32, hasher.Verifier.Length);
    }

    [Fact]
    public void Derive_WeakPassword_Throws()
    {
        Assert.Throws<ArgumentException>(() => PasswordHasher.Derive("Abcdef1!"));
    }
}
