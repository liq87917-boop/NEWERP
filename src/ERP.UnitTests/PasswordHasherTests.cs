using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 密码哈希工具测试
/// </summary>
public class PasswordHasherTests
{
    [Fact]
    public void HashPassword_相同密码相同盐_哈希一致()
    {
        var salt = PasswordHasher.GenerateSalt();
        var hash1 = PasswordHasher.HashPassword("Admin@123", salt);
        var hash2 = PasswordHasher.HashPassword("Admin@123", salt);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashPassword_不同盐_哈希不同()
    {
        var salt1 = PasswordHasher.GenerateSalt();
        var salt2 = PasswordHasher.GenerateSalt();
        var hash1 = PasswordHasher.HashPassword("Admin@123", salt1);
        var hash2 = PasswordHasher.HashPassword("Admin@123", salt2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyPassword_正确密码_返回真()
    {
        var salt = PasswordHasher.GenerateSalt();
        var hash = PasswordHasher.HashPassword("Secret@123", salt);

        Assert.True(PasswordHasher.VerifyPassword("Secret@123", salt, hash));
    }

    [Fact]
    public void VerifyPassword_错误密码_返回假()
    {
        var salt = PasswordHasher.GenerateSalt();
        var hash = PasswordHasher.HashPassword("Secret@123", salt);

        Assert.False(PasswordHasher.VerifyPassword("WrongPass", salt, hash));
    }

    [Fact]
    public void GenerateSalt_多次生成_盐值不同()
    {
        var salt1 = PasswordHasher.GenerateSalt();
        var salt2 = PasswordHasher.GenerateSalt();

        Assert.NotEqual(salt1, salt2);
    }
}
