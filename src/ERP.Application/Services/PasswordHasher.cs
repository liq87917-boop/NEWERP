using System.Security.Cryptography;

namespace ERP.Application.Services;

/// <summary>
/// 密码哈希工具：使用 PBKDF2 算法（随机盐 + 迭代），不可逆存储
/// </summary>
public static class PasswordHasher
{
    /// <summary>迭代次数</summary>
    private const int Iterations = 100_000;

    /// <summary>哈希长度（字节）</summary>
    private const int HashBytes = 32;

    /// <summary>盐长度（字节）</summary>
    private const int SaltBytes = 16;

    /// <summary>生成随机盐值（Base64 字符串）</summary>
    public static string GenerateSalt()
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return Convert.ToBase64String(salt);
    }

    /// <summary>计算密码哈希</summary>
    public static string HashPassword(string password, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>校验密码是否匹配</summary>
    public static bool VerifyPassword(string password, string salt, string passwordHash)
    {
        var computed = HashPassword(password, salt);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(computed),
            Convert.FromBase64String(passwordHash));
    }
}
