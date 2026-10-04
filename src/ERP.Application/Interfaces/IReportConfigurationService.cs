using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 私有报表配置平台服务（ERP-260 Stage 1）：可复用的应用服务，负责「保存、列表、加载、重命名、
/// 复制、更新、软删除」用户私有报表定义，以及「发布 / 恢复 / 修订列表」的不可变版本链。
/// <para>安全口径：所有方法只接受服务端认证的 <paramref name="ownerUserId"/>（客户端不得提交所有者）；
/// 无身份 fail closed；跨所有者访问 fail closed（一律按不存在处理）；每次保存 / 加载 / 发布 / 恢复 /
/// 复制都按当前账号重新校验数据集既有菜单授权，已保存定义本身绝不授予权限。</para>
/// </summary>
public interface IReportConfigurationService
{
    /// <summary>保存（新增）一条私有报表配置草稿：校验当前数据集授权与有界定义后落库。</summary>
    Task<ReportConfigurationDto> CreateAsync(long ownerUserId, ReportConfigurationSaveDto dto,
        CancellationToken cancellationToken = default);

    /// <summary>更新既有草稿定义 / 名称（需匹配 <paramref name="expectedVersion"/>，陈旧写入拒绝）。</summary>
    Task<ReportConfigurationDto> UpdateAsync(long ownerUserId, long id, int expectedVersion,
        ReportConfigurationSaveDto dto, CancellationToken cancellationToken = default);

    /// <summary>重命名私有报表配置（需匹配 <paramref name="expectedVersion"/>）。</summary>
    Task<ReportConfigurationDto> RenameAsync(long ownerUserId, long id, int expectedVersion, string name,
        CancellationToken cancellationToken = default);

    /// <summary>复制一条私有报表配置为新的草稿（同数据集、同定义；重新校验当前数据集授权）。</summary>
    Task<ReportConfigurationDto> CopyAsync(long ownerUserId, long id, CancellationToken cancellationToken = default);

    /// <summary>加载单条私有报表配置详情（重新校验当前数据集授权）。</summary>
    Task<ReportConfigurationDto> GetAsync(long ownerUserId, long id, CancellationToken cancellationToken = default);

    /// <summary>列表当前用户的全部未删除私有报表配置（owner-only）。</summary>
    Task<List<ReportConfigurationSummaryDto>> ListAsync(long ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>有界 keyset 分页列表当前用户的未删除私有报表配置（owner-only；默认 25、最大 100）。</summary>
    Task<ReportConfigurationPage<ReportConfigurationSummaryDto>> ListPageAsync(
        long ownerUserId, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>软删除私有报表配置及其全部发布修订（需匹配 <paramref name="expectedVersion"/>）。</summary>
    Task DeleteAsync(long ownerUserId, long id, int expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>发布：把当前定义固定为一条新修订快照并置为已发布（需匹配 <paramref name="expectedVersion"/>）。</summary>
    Task<ReportConfigurationDto> PublishAsync(long ownerUserId, long id, int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>恢复：把指定历史发布版本的定义再次发布成一条新修订（需匹配 <paramref name="expectedVersion"/>）。</summary>
    Task<ReportConfigurationDto> RestoreAsync(long ownerUserId, long id, int expectedVersion, int versionNumber,
        CancellationToken cancellationToken = default);

    /// <summary>列出单条私有报表配置的全部发布修订（重新校验当前数据集授权）。</summary>
    Task<List<ReportConfigurationRevisionDto>> ListRevisionsAsync(long ownerUserId, long id,
        CancellationToken cancellationToken = default);

    /// <summary>有界 keyset 分页列出单条私有报表配置的发布修订（owner-only；默认 25、最大 100）。</summary>
    Task<ReportConfigurationPage<ReportConfigurationRevisionDto>> ListRevisionsPageAsync(
        long ownerUserId, long id, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default);
}
