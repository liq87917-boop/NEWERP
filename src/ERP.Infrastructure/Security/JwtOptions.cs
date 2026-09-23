namespace ERP.Infrastructure.Security;

/// <summary>
/// JWT 配置项
/// </summary>
public class JwtOptions
{
    /// <summary>签名密钥</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>签发方</summary>
    public string Issuer { get; set; } = "ERP.Api";

    /// <summary>受众</summary>
    public string Audience { get; set; } = "ERP.Client";

    /// <summary>令牌有效期（分钟）</summary>
    public int ExpireMinutes { get; set; } = 120;
}
