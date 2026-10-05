using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 报表预设模板编排（ERP-296 Stage 2）接缝：把有限、不可变的预设定义（由迁移登记册条目键控）重新表达为
/// 可校验的 <see cref="ReportConfigurationDefinition"/>，只读列出，并可物化为当前用户私有副本。
/// <para>安全口径：每次调用都重新校验数据集既有菜单授权与迁移登记册 readiness（fail closed）；预设本身绝不授予权限；
/// 物化绝不信任预设载荷，先对当前授权数据集重新校验定义，再经既有 <see cref="IReportConfigurationService.CreateAsync"/>
/// 落为私有草稿。依赖方向：编排消费 <see cref="IReportMigrationRegistry"/> 与
/// <see cref="IReportConfigurationCatalog"/>，绝不反向依赖自己的物化结果，避免循环依赖。</para>
/// </summary>
public interface IReportConfigurationPresetCatalog
{
    /// <summary>只读列出当前账号已授权且 ready（至少 dataset-ready）的预设模板（未授权 / 未 ready 隐藏）。</summary>
    Task<List<ReportConfigurationPresetDto>> ListPresetsAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>按 <paramref name="presetKey"/> 取得单条已授权且 ready 的预设；未知 / 未授权 / 未 ready 返回 null（fail closed）。</summary>
    Task<ReportConfigurationPresetDto?> GetPresetAsync(string presetKey, long? userId, CancellationToken cancellationToken = default);

    /// <summary>把指定预设物化为当前用户私有副本（重新校验当前授权数据集与定义，绝不信任预设载荷）。</summary>
    Task<ReportConfigurationDto> MaterializeAsync(string presetKey, long? userId, CancellationToken cancellationToken = default);
}
