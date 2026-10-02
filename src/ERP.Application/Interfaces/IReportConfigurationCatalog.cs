using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的目录聚合接缝：汇总全部已注册数据集适配器，
/// 只暴露当前账号已授权数据集的有限字段 / 能力 / 粒度 / 币种口径。
/// <para>每次调用都按当前账号重新校验（权限在 catalog / save / load / preview 各入口重检），
/// 已保存定义本身绝不授予权限；无身份时 fail closed。</para>
/// </summary>
public interface IReportConfigurationCatalog
{
    /// <summary>返回当前账号可访问的全部数据集目录（仅授权数据集，按数据集键确定性排序）。</summary>
    Task<ReportConfigurationCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>返回指定数据集（未知键或未授权返回 null；无身份抛未认证异常）。</summary>
    Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
        string datasetKey, long? userId, CancellationToken cancellationToken = default);
}
