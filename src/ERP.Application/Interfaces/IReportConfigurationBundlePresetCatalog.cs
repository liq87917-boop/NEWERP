using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置捆绑预设编排（ERP-310 Stage 2）接缝：把有限、不可变的多节模板（由迁移登记册条目键控）
/// 重新表达为可校验的多节私有定义，并物化为当前用户私有草稿，供既有
/// <see cref="IReportConfigurationBundleService"/> 预览 / 导出。
/// <para>安全口径：每次调用都重新校验预设声明的菜单授权与每一节的受控数据集授权（fail closed）；
/// 物化绝不信任预设载荷，先对当前授权数据集逐节重新校验定义与参数绑定，再经既有
/// <see cref="IReportConfigurationService.CreateAsync"/> 落为私有草稿；任何一节失败即整体回滚
/// 本次新建的私有草稿（绝不删除既有配置、绝不跨用户编辑、绝不扩大权限）。</para>
/// </summary>
public interface IReportConfigurationBundlePresetCatalog
{
    /// <summary>只读列出当前账号菜单已授权的捆绑预设（未授权隐藏；readiness 见 DTO，绝不 presence-only parity-passed）。</summary>
    Task<List<ReportConfigurationBundlePresetDto>> ListPresetsAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>按 <paramref name="presetKey"/> 取得单条已授权捆绑预设；未知 / 未授权返回 null（fail closed）。</summary>
    Task<ReportConfigurationBundlePresetDto?> GetPresetAsync(string presetKey, long? userId, CancellationToken cancellationToken = default);

    /// <summary>把指定捆绑预设按有限参数物化为当前用户私有多节草稿（逐节重新校验授权 / 定义 / 参数绑定，失败整体回滚）。</summary>
    Task<ReportConfigurationBundlePresetMaterializationDto> MaterializeAsync(
        string presetKey,
        ReportConfigurationBundlePresetMaterializeRequest request,
        long? userId,
        CancellationToken cancellationToken = default);
}
