using FolderCrypto.Core.Security;
using Xunit;

namespace FolderCrypto.Core.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Abcdef1!")]      // 8位, 含字母/数字/特殊
    [InlineData("a1!b2@c3#")]     // 9位
    [InlineData("!!@@##aa11##")]  // 长密码
    public void ValidPasswords_Pass(string password)
    {
        Assert.True(PasswordPolicy.IsSatisfied(password));
        Assert.Empty(PasswordPolicy.Validate(password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short!")]        // 6位 (不大于6)
    [InlineData("abcdefg")]       // 有字母无数字无特殊
    [InlineData("1234567")]       // 只有数字
    [InlineData("!!!!!!!")]       // 只有特殊
    [InlineData("abc1234")]       // 字母+数字但无特殊
    public void InvalidPasswords_Fail(string password)
    {
        Assert.False(PasswordPolicy.IsSatisfied(password));
        Assert.NotEmpty(PasswordPolicy.Validate(password));
    }

    [Fact]
    public void ExactlySixCharacters_Fails()
    {
        // "长度必须大于6"，6位应失败
        Assert.False(PasswordPolicy.IsSatisfied("Ab1!2d"));
        Assert.Contains(PasswordPolicy.Validate("Ab1!2d"), e => e.Contains("6 位"));
    }

    [Fact]
    public void NullPasswords_Fails()
    {
        Assert.False(PasswordPolicy.IsSatisfied(null));
    }

    // ---- 密码强度评分 ----

    [Theory]
    [InlineData("", 0)]
    [InlineData("short!1", 0)]        // 不满足基本长度(<=6)
    [InlineData("abc123", 0)]         // 6位
    [InlineData("abc12345", 1)]       // 8位, 字母+数字
    [InlineData("Abcdef1!", 2)]       // 8位, 多样类别全
    [InlineData("Abcdef12@#", 2)]     // 10位, 混合
    [InlineData("Abcdefgh123!@#XyZ", 4)] // 长+全多样
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
        Assert.True(PasswordPolicy.ScoreStrength("Abcdefgh123!@#XyZ") >= PasswordPolicy.ScoreStrength("Abcdef1!"));
        Assert.True(PasswordPolicy.ScoreStrength("Abcdef1!") >= PasswordPolicy.ScoreStrength("abc12345"));
    }

    [Fact]
    public void Strength_LongMixed_HighLevel()
    {
        Assert.Equal(PasswordStrength.Strong, PasswordPolicy.LevelOf(PasswordPolicy.ScoreStrength("Abcdefgh123!@#XyZ")));
        Assert.Equal(PasswordStrength.VeryWeak, PasswordPolicy.LevelOf(PasswordPolicy.ScoreStrength("abc12")));
    }
}
