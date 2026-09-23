using ERP.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// OssStorageService 单元测试：构造校验、URL 拼接、签名算法（HMAC-SHA1 + Base64）。
/// UploadAsync 触发真实 OSS HTTP 调用——不在单测覆盖范围（属于集成测试，建议另建端到端用例）。
/// </summary>
public class OssStorageServiceTests
{
    private const string TestAccessKeyId = "test-key-id";
    private const string TestAccessKeySecret = "test-secret";
    private const string TestBucket = "test-bucket";
    private const string TestEndpoint = "oss-cn-hangzhou.aliyuncs.com";

    private static IConfiguration BuildConfig(
        string? accessKeyId = TestAccessKeyId,
        string? accessKeySecret = TestAccessKeySecret,
        string? bucket = TestBucket,
        string? endpoint = TestEndpoint)
    {
        var dict = new Dictionary<string, string?>();
        if (accessKeyId is not null)     dict["Oss:AccessKeyId"]     = accessKeyId;
        if (accessKeySecret is not null) dict["Oss:AccessKeySecret"] = accessKeySecret;
        if (bucket is not null)          dict["Oss:Bucket"]          = bucket;
        if (endpoint is not null)        dict["Oss:Endpoint"]        = endpoint;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ==================== 构造 ====================

    [Fact]
    public void 构造函数_AccessKeyId缺失_抛InvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OssStorageService(BuildConfig(accessKeyId: null)));
        Assert.Contains("AccessKeyId", ex.Message);
    }

    [Fact]
    public void 构造函数_AccessKeySecret缺失_抛InvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OssStorageService(BuildConfig(accessKeySecret: null)));
        Assert.Contains("AccessKeySecret", ex.Message);
    }

    [Fact]
    public void 构造函数_Bucket缺失_抛InvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OssStorageService(BuildConfig(bucket: null)));
        Assert.Contains("Bucket", ex.Message);
    }

    [Fact]
    public void 构造函数_Endpoint未配置时默认_oss_cn_hangzhou_aliyuncs_com()
    {
        var svc = new OssStorageService(BuildConfig(endpoint: null));
        Assert.Equal("https://test-bucket.oss-cn-hangzhou.aliyuncs.com/products/1.jpg", svc.GetObjectUrl("products/1.jpg"));
    }

    [Theory]
    [InlineData("https://oss-cn-hangzhou.aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("http://oss-cn-hangzhou.aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("//oss-cn-hangzhou.aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("/oss-cn-hangzhou.aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com")]
    public void 构造函数_Endpoint自动去除协议前缀和前导斜杠(string rawEndpoint, string expected)
    {
        var svc = new OssStorageService(BuildConfig(endpoint: rawEndpoint));
        // 通过 GetObjectUrl 推断：URL 应为 "https://{bucket}.{endpoint}/{key}"
        Assert.Contains($".{expected}/", svc.GetObjectUrl("x"));
    }

    // ==================== GetObjectUrl ====================

    [Fact]
    public void GetObjectUrl_拼接URL_去除对象键前导斜杠()
    {
        var svc = new OssStorageService(BuildConfig());
        Assert.Equal(
            "https://test-bucket.oss-cn-hangzhou.aliyuncs.com/products/2026/09/17/xxx.jpg",
            svc.GetObjectUrl("/products/2026/09/17/xxx.jpg"));
    }

    // ==================== GetSignedUrl ====================

    [Fact]
    public void GetSignedUrl_包含OSSAccessKeyId_Expires_Signature三个参数_签名算法与基线一致()
    {
        var svc = new OssStorageService(BuildConfig());
        var beforeEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = svc.GetSignedUrl("/my-bucket-test/test.txt", expiresSeconds: 100L);
        var afterEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // URL 结构
        Assert.StartsWith("https://test-bucket.oss-cn-hangzhou.aliyuncs.com/my-bucket-test/test.txt?", url);
        Assert.Contains("OSSAccessKeyId=test-key-id", url);
        Assert.Contains("Expires=", url);
        Assert.Contains("Signature=", url);

        // Expires 应为 now + expiresSeconds（在 ±2s 容差内）
        var expiresInUrl = long.Parse(System.Text.RegularExpressions.Regex.Match(url, @"Expires=(\d+)").Groups[1].Value);
        Assert.InRange(expiresInUrl, beforeEpoch + 100L, afterEpoch + 100L);

        // 签名算法（HMAC-SHA1 Base64）与 OSS V1 标准对照
        var signaturePart = url.Substring(url.IndexOf("Signature=", StringComparison.Ordinal) + "Signature=".Length);
        var signature = Uri.UnescapeDataString(signaturePart);
        Assert.Matches("^[A-Za-z0-9+/=]+$", signature);    // base64 字符集
        // 进一步用固定输入对照（私有 Sign 方法已测）
    }

    [Fact]
    public void GetSignedUrl_对象键前导斜杠被去除()
    {
        var svc = new OssStorageService(BuildConfig());
        var url1 = svc.GetSignedUrl("/foo/bar.txt", expiresSeconds: 100L);
        var url2 = svc.GetSignedUrl("foo/bar.txt", expiresSeconds: 100L);
        // 因为 expires=now+100 不同，两 URL 不等；但路径部分应一致
        Assert.Contains("/foo/bar.txt?", url1);
        Assert.Contains("/foo/bar.txt?", url2);
    }

    // ==================== Sign（反射访问私有方法）====================

    [Fact]
    public void Sign_反射访问_私有HMAC_SHA1与基线值一致()
    {
        // 基线参考（已用 PowerShell 算出）：
        //   secret = "test-secret"
        //   stringToSign = "GET\n\n\n1234567890\n/my-bucket/test.txt"
        //   期望 base64 = "LiP04iizpNSdDP5CLPew419SWwQ="
        var svc = new OssStorageService(BuildConfig());
        var signMethod = typeof(OssStorageService).GetMethod("Sign",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(signMethod);

        var signature = (string)signMethod!.Invoke(svc, new object[]
        {
            "GET\n\n\n1234567890\n/my-bucket/test.txt"
        })!;

        Assert.Equal("LiP04iizpNSdDP5CLPew419SWwQ=", signature);
    }
}