using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 旧报表来源统一接缝（ERP-330）：把任意旧报表（固定报表 / 18 个动态报表 / 旧单据导出族 /
/// 客户报告包 / 单证 / 财务报表 / 打印模板族）归一化为同一个有界旧结果快照，供 parity 比对证据源消费。
/// <para>只读、无写库；每次调用都按当前账号重新校验菜单与行 / 数据范围授权（fail closed），
/// 并遵守既有分页 / 日期范围上限与调用方取消令牌。实现方绝不执行任意 SQL（除既有
/// <see cref="ILegacyBillExportReadService"/> 受控读取）、绝不新增实体 / 权限 / 环境变量。</para>
/// </summary>
public interface ILegacyReportSource
{
    /// <summary>
    /// 读取指定旧报表的一页有界旧结果快照。
    /// <para>空白 / 畸形 / 未知旧报表键在打开任何查询之前以精确参数错误拒绝；未认证直接拒绝；
    /// 菜单 / 数据范围撤销返回 <see cref="LegacyReportSourceStatus.Forbidden"/>；来源存在但既有读取
    /// 服务在当前环境不可用时返回 <see cref="LegacyReportSourceStatus.EnvironmentBlocked"/>。</para>
    /// </summary>
    Task<LegacyReportSourceResult> ReadAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken = default);
}
