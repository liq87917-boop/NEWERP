using ERP.Application.Services;
using Microsoft.Extensions.Configuration;

namespace ERP.Infrastructure.Storage;

/// <summary>
/// 附件内容存储配置（ERP-061）。
/// <para><b>只用于隔离的非生产本地存储</b>：生产对象存储（OSS）的凭据、桶与endpoint 一律不在本配置中，
/// 其激活属于生产 OSS Human Gate（本阶段不实现、不注册、不激活）。</para>
/// </summary>
public sealed class AttachmentStorageOptions
{
    /// <summary>提供程序编码（缺省 = 隔离的非生产本地存储）</summary>
    public string Provider { get; init; } = AttachmentEvidenceRules.ProviderIsolatedLocal;

    /// <summary>
    /// 隔离存储根目录（缺省 = 应用目录下的 <c>var/attachment-evidence</c>）。
    /// <para>解析后的根目录**必须**位于 Web 根目录（wwwroot）之外：上传内容绝不落在静态文件目录里。</para>
    /// </summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>从配置读取（只读取提供程序与隔离根目录两个键，不读取任何密钥）</summary>
    public static AttachmentStorageOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = (configuration[AttachmentEvidenceRules.ProviderConfigurationKey] ?? string.Empty).Trim();
        var rootPath = (configuration[AttachmentEvidenceRules.LocalRootConfigurationKey] ?? string.Empty).Trim();

        return new AttachmentStorageOptions
        {
            Provider = provider.Length == 0 ? AttachmentEvidenceRules.ProviderIsolatedLocal : provider,
            RootPath = rootPath
        };
    }

    /// <summary>解析隔离存储根目录（相对路径按当前工作目录解析；缺省在应用目录的 var 子目录）</summary>
    public string ResolveRootPath()
        => Path.GetFullPath(RootPath.Length == 0
            ? Path.Combine(AppContext.BaseDirectory, "var", "attachment-evidence")
            : RootPath);
}
