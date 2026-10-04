using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-265 Stage 1）只读共享授权服务：所有者以「被授权人用户 Id + 固定发布修订版本号」
/// 授予 / 撤销单个不可变已发布快照的只读访问；被授权人只读取固定快照、复制为自有草稿，
/// 绝不暴露草稿 / 其它修订 / 历史或所有者编辑。
/// <para>安全口径：所有者 / 被授权人都只来自服务端认证身份（客户端不得提交）；无身份 fail closed；
/// 授权 / 撤销 / 列出 / 复制都按被授权人当前数据集授权与数据范围重新校验，已保存定义本身绝不授予权限。</para>
/// </summary>
public interface IReportConfigurationSharingService
{
    /// <summary>列出某条私有报表配置的全部有效只读授权（owner-only）。</summary>
    Task<List<ReportConfigurationGrantDto>> ListGrantsAsync(
        long ownerUserId, long configurationId, CancellationToken cancellationToken = default);

    /// <summary>有界 keyset 分页列出某条私有报表配置的有效只读授权（owner-only；默认 25、最大 100）。</summary>
    Task<ReportConfigurationPage<ReportConfigurationGrantDto>> ListGrantsPageAsync(
        long ownerUserId, long configurationId, int? limit = null, string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>授予 / 变更某个固定发布修订的只读访问（owner-only；变更 pin 需回传预期版本）。</summary>
    Task<ReportConfigurationGrantDto> GrantAsync(
        long ownerUserId, long configurationId, ReportConfigurationGrantRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>撤销某个被授权人的只读访问（owner-only；需回传预期版本，防止陈旧撤销）。</summary>
    Task RevokeAsync(
        long ownerUserId, long configurationId, long recipientUserId, int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>列出当前用户被共享的只读发布快照（recipient-only；只暴露固定快照）。</summary>
    Task<List<ReportConfigurationSharedSummaryDto>> ListSharedAsync(
        long recipientUserId, CancellationToken cancellationToken = default);

    /// <summary>有界 keyset 分页列出当前用户被共享的只读发布快照（recipient-only；默认 25、最大 100）。</summary>
    Task<ReportConfigurationPage<ReportConfigurationSharedSummaryDto>> ListSharedPageAsync(
        long recipientUserId, int? limit = null, string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>加载当前用户被共享的只读发布快照详情（recipient-only）。</summary>
    Task<ReportConfigurationSharedDetailDto> GetSharedAsync(
        long recipientUserId, long configurationId, CancellationToken cancellationToken = default);

    /// <summary>复制被共享的只读发布快照为当前用户自有草稿（新鲜校验，绝不改写原配置）。</summary>
    Task<ReportConfigurationDto> CopySharedAsync(
        long recipientUserId, long configurationId, CancellationToken cancellationToken = default);
}
