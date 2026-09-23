using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace ERP.Infrastructure.Storage;

/// <summary>
/// 阿里云 OSS 对象存储服务：实现文件/图片上传（OSS V1 签名，PUT Object）
/// </summary>
public class OssStorageService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly string _accessKeyId;
    private readonly string _accessKeySecret;
    private readonly string _bucket;
    private readonly string _endpoint;

    public OssStorageService(IConfiguration configuration)
    {
        _accessKeyId = configuration["Oss:AccessKeyId"]
            ?? throw new InvalidOperationException("未配置 OSS AccessKeyId");
        _accessKeySecret = configuration["Oss:AccessKeySecret"]
            ?? throw new InvalidOperationException("未配置 OSS AccessKeySecret");
        _bucket = configuration["Oss:Bucket"]
            ?? throw new InvalidOperationException("未配置 OSS Bucket");
        // 兼容 "//oss-cn-hangzhou.aliyuncs.com" 或 "https://..." 等写法，统一去除协议与斜杠前缀
        _endpoint = (configuration["Oss:Endpoint"] ?? "oss-cn-hangzhou.aliyuncs.com")
            .Replace("https://", "").Replace("http://", "").TrimStart('/');
    }

    /// <summary>对象公网访问地址（未签名，私有桶直接访问会 403 AccessDenied）</summary>
    public string GetObjectUrl(string objectKey)
        => $"https://{_bucket}.{_endpoint}/{objectKey.TrimStart('/')}";

    /// <summary>生成带有效期的签名访问地址（私有桶读取用，默认 10 年有效）</summary>
    public string GetSignedUrl(string objectKey, long expiresSeconds = 315360000)
    {
        var key = objectKey.TrimStart('/');
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresSeconds;
        // OSS V1 签名 URL：GET\n\n\nExpires\nCanonicalizedResource
        var stringToSign = $"GET\n\n\n{expires}\n/{_bucket}/{key}";
        var signature = Sign(stringToSign);
        return $"https://{_bucket}.{_endpoint}/{key}?OSSAccessKeyId={Uri.EscapeDataString(_accessKeyId)}&Expires={expires}&Signature={Uri.EscapeDataString(signature)}";
    }

    /// <summary>上传文件到 OSS，返回对象公网地址（原图）</summary>
    /// <param name="content">文件流</param>
    /// <param name="objectKey">对象键（如 products/20260821/xxx.jpg）</param>
    /// <param name="contentType">MIME 类型</param>
    public async Task<string> UploadAsync(Stream content, string objectKey, string contentType)
    {
        var key = objectKey.TrimStart('/');
        var url = GetObjectUrl(key);
        var date = DateTime.UtcNow.ToString("R");
        var contentTypeValue = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        // OSS V1 签名：PUT\nContent-MD5\nContent-Type\nDate\nCanonicalizedResource
        var stringToSign = $"PUT\n\n{contentTypeValue}\n{date}\n/{_bucket}/{key}";
        var signature = Sign(stringToSign);
        var authorization = $"OSS {_accessKeyId}:{signature}";

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("Date", date);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentTypeValue);

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"OSS 上传失败（{(int)response.StatusCode}）：{body}");
        }
        // 私有桶：返回签名地址（否则直接访问会 403 AccessDenied）
        return GetSignedUrl(key);
    }

    /// <summary>计算 OSS V1 签名（HMAC-SHA1 + Base64）</summary>
    private string Sign(string stringToSign)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_accessKeySecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hash);
    }
}
