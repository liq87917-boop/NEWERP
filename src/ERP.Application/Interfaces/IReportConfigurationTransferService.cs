using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-276 Stage 1）可移植报表定义传输服务：把「自有草稿 / 自有发布修订 /
/// 被共享的固定发布快照」导出为有界版本化 JSON 信封，或把该信封导入为当前用户新的私有草稿。
/// <para>安全口径：用户身份只来自服务端认证；导出 / 导入都按当前账号重新校验数据集既有菜单授权与数据范围
/// （fail closed）；导出只走服务端详情解析（绝不信任客户端行 / 身份 / 范围 / 缓存）；被共享来源撤销后导出失败；
/// 导入绝不改写 Id / 绝不覆盖既有配置 / 绝不发布 / 绝不导入所有权、共享或发布状态。</para>
/// </summary>
public interface IReportConfigurationTransferService
{
    /// <summary>导出有界版本化定义信封（只含格式 / schema / 名称 / 定义；不含行、身份、授权、历史、SQL 连接或附件）。</summary>
    Task<ReportConfigurationTransferEnvelopeDto> ExportAsync(
        long userId, ReportConfigurationTransferExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>严格校验并导入定义信封为当前用户新的私有草稿（version1 + 正常审计，绝不覆盖 / 发布 / 扩权）。</summary>
    Task<ReportConfigurationDto> ImportAsync(
        long userId, ReportConfigurationTransferImportRequest request,
        CancellationToken cancellationToken = default);
}
