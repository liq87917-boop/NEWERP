using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的数据集适配器接缝：每个既有数据集提供一个适配器，
/// 负责把「有限、静态的字段白名单 + 能力 / 粒度 / 币种口径」暴露为统一 <see cref="ReportConfigurationDatasetDto"/>。
/// <para>适配器按 <see cref="DatasetKey"/> 注册并由目录聚合，新数据集只增加适配器、不改控制器或目录实现；
/// 每次调用都按当前账号重新校验既有菜单授权与数据范围，绝不把静态目录扩大到数据库全量元数据发现。</para>
/// </summary>
public interface IReportConfigurationDatasetProvider
{
    /// <summary>唯一数据集键（与 <see cref="ReportConfigurationConstants"/> 中的既有数据集键一致）。</summary>
    string DatasetKey { get; }

    /// <summary>
    /// 返回当前账号已授权的数据集目录；无对应菜单授权时返回 null（该数据集不暴露），
    /// 无身份时抛出未认证异常（fail closed）。
    /// </summary>
    Task<ReportConfigurationDatasetDto?> GetDatasetAsync(long? userId, CancellationToken cancellationToken = default);
}
