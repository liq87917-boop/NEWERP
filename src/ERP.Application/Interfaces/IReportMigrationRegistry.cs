using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 报表迁移登记册（ERP-295 Stage 2）接缝：把编译期旧报表清单按当前账号做菜单授权重检（fail closed），
/// 并从注册的数据集适配器目录 + 预设目录 + 逐条兼容性清单派生 parity 状态。
/// <para>只读、无写库；每次调用都重新校验（权限撤销后立即收敛）；已保存定义 / 预设本身绝不授予权限。</para>
/// </summary>
public interface IReportMigrationRegistry
{
    /// <summary>返回当前账号已授权的迁移登记册（未授权 / 未声明菜单的条目被隐藏；无身份抛未认证）。</summary>
    Task<ReportMigrationRegistryDto> GetRegistryAsync(long? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 旧路由退役门（被 preset 编排与旧路由门控消费）：只有当清单内<strong>每一条</strong>旧报表都达到
    /// parity-passed 时才返回 true；无身份 / 任一未达标一律 false（fail closed，旧路由绝不提前移除）。
    /// </summary>
    Task<bool> CanRetireLegacyRoutesAsync(long? userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 报表预设目录接缝（ERP-295 Stage 2）：为迁移登记册提供「某旧报表是否已有预设模板」的运行时查询。
/// <para>Stage 2 阶段预设尚未落地，因此默认实现为空目录（恒 false），只作为 parity 派生的前置接缝，不新增任何权限或存储。</para>
/// </summary>
public interface IReportMigrationPresetCatalog
{
    /// <summary>旧报表（按 <c>LegacyKey</c>）是否已有预设模板。</summary>
    Task<bool> HasPresetAsync(string legacyKey, long? userId, CancellationToken cancellationToken = default);
}
