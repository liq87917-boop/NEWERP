using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-273 Stage 1）的可选快照能力契约：数据集适配器显式实现本接口，
/// 声明其支持请求作用域的有界一致只读快照（关系型运行时使用既有 <see cref="IErpDbContext.Database"/>
/// 的只读 Serializable 事务，绝不启用 SnapshotIsolation / 数据库配置）。
/// <para>未实现本接口的旧适配器 / 测试替身保持编译（沿用 <see cref="IReportConfigurationDatasetProvider"/>
/// 的默认不支持实现），有限既有请求维持既有行为。</para>
/// </summary>
public interface IReportConfigurationSnapshotDatasetProvider : IReportConfigurationDatasetProvider
{
}
