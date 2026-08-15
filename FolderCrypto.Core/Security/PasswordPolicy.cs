namespace FolderCrypto.Core.Security;

/// <summary>密码强度等级。</summary>
public enum PasswordStrength
{
    /// <summary>空白/过短（未满足基本长度）。</summary>
    VeryWeak = 0,
    /// <summary>弱。</summary>
    Weak = 1,
    /// <summary>中。</summary>
    Medium = 2,
    /// <summary>强。</summary>
    Strong = 3,
}

/// <summary>
/// 密码强度校验规则：
///   - 长度必须超过 6 位（&gt;= 7）
///   - 必须同时包含：数字、字母、特殊字符
/// 并额外提供一个 0..4 的强度评分，用于 UI 强度条显示。
/// </summary>
public static class PasswordPolicy
{
    /// <summary>密码最小允许长度（必须大于 6，因此最小为 7）。</summary>
    public const int MinLength = 7;

    /// <summary>错误原因描述列表。</summary>
    public static IReadOnlyList<string> Validate(string? password)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("密码不能为空");
            return errors;
        }

        if (password.Length <= 6)
        {
            errors.Add($"密码长度需大于 6 位（当前 {password.Length} 位）");
        }

        if (!password.Any(char.IsDigit))
        {
            errors.Add("密码必须包含数字");
        }

        if (!password.Any(char.IsLetter))
        {
            errors.Add("密码必须包含字母");
        }

        if (!password.Any(IsSpecialCharacter))
        {
            errors.Add("密码必须包含特殊字符，如 !@#$%^&* 等");
        }

        return errors;
    }

    /// <summary>是否通过所有强度校验。</summary>
    public static bool IsSatisfied(string? password) => Validate(password).Count == 0;

    /// <summary>判断是否为“特殊字符”（非字母数字、非空白）。</summary>
    public static bool IsSpecialCharacter(char c)
        => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);

    /// <summary>返回一个可读的规则说明（用于 UI 提示）。</summary>
    public static string DescribeRules()
        => "密码需超过 6 位，且同时包含数字、字母和特殊字符（如 !@#$%^&*）。";

    /// <summary>
    /// 评估密码强度，返回 0..4 的评分（0=极弱，4=最强）。
    /// 综合长度与字符集多样性（数字/小写/大写/特殊）。
    /// </summary>
    public static int ScoreStrength(string? password)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        int len = password.Length;
        if (len <= 6) return 0; // 不满足基本长度

        int raw = 0;
        if (len >= 8) raw++;
        if (len >= 12) raw++;
        if (len >= 16) raw++;

        // 多样字符类别数（0..4）
        int cats = 0;
        if (password.Any(char.IsDigit)) cats++;
        if (password.Any(char.IsLower)) cats++;
        if (password.Any(char.IsUpper)) cats++;
        if (password.Any(IsSpecialCharacter)) cats++;
        if (cats >= 2) raw++;
        if (cats >= 4) raw++;

        // raw 0..5 → 0..4
        return raw switch
        {
            0 => 0,
            1 => 1,
            2 => 1,
            3 => 2,
            4 => 3,
            _ => 4,
        };
    }

    /// <summary>把 0..4 的评分映射为强度等级。</summary>
    public static PasswordStrength LevelOf(int score)
        => score switch
        {
            0 => PasswordStrength.VeryWeak,
            1 => PasswordStrength.Weak,
            2 => PasswordStrength.Medium,
            _ => PasswordStrength.Strong,
        };

    /// <summary>获取强度等级对应的文字。</summary>
    public static string LevelText(PasswordStrength level)
        => level switch
        {
            PasswordStrength.VeryWeak => "太弱",
            PasswordStrength.Weak => "弱",
            PasswordStrength.Medium => "中",
            PasswordStrength.Strong => "强",
            _ => "未输入",
        };

    /// <summary>强度条显示用的“点亮段数”（1..4）。</summary>
    public static int Segments(int score) => Math.Clamp(score, 1, 4);
}

