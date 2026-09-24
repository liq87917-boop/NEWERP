using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.Extensions.Configuration;

namespace ERP.Infrastructure.Storage;

/// <summary>
/// 附件内容存储工厂（ERP-061）：把配置映射到**唯一**的内容提供程序。
/// <list type="bullet">
/// <item><c>Attachments:Provider</c> = <c>local-isolated</c>（缺省）→ 隔离的非生产本地存储，
/// 开发与测试唯一启用的提供程序；</item>
/// <item><c>Attachments:Provider</c> = <c>oss</c> → **显式拒绝**：生产对象存储的凭据 / 桶配置 / 迁移与激活
/// 属于生产 OSS Human Gate，本阶段不实现、不注册、不激活；</item>
/// <item>其他取值 → 显式拒绝（避免拼写错误悄悄退化成默认提供程序）。</item>
/// </list>
/// </summary>
public static class AttachmentContentStoreFactory
{
    /// <summary>按配置创建内容存储提供程序（缺省为隔离的非生产本地存储）</summary>
    public static IAttachmentContentStore Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = (configuration[AttachmentEvidenceRules.ProviderConfigurationKey] ?? string.Empty).Trim();
        if (provider.Length == 0) provider = AttachmentEvidenceRules.ProviderIsolatedLocal;

        if (string.Equals(provider, AttachmentEvidenceRules.ProviderIsolatedLocal, StringComparison.OrdinalIgnoreCase))
            return new IsolatedLocalAttachmentContentStore(AttachmentStorageOptions.FromConfiguration(configuration));

        if (string.Equals(provider, AttachmentEvidenceRules.ProviderOss, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "附件内容存储的生产 OSS 提供程序未实现、未注册、未激活：生产对象存储的凭据 / 桶配置 / 迁移与启用"
                + "属于生产 OSS Human Gate；本阶段只启用隔离的非生产本地存储"
                + $"（{AttachmentEvidenceRules.ProviderConfigurationKey} = {AttachmentEvidenceRules.ProviderIsolatedLocal}）");

        throw new InvalidOperationException(
            $"未知的附件内容存储提供程序「{provider}」：只支持 "
            + $"{AttachmentEvidenceRules.ProviderIsolatedLocal}（隔离的非生产本地存储）");
    }
}
