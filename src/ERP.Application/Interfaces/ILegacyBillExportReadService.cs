using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;

namespace ERP.Application.Interfaces;

/// <summary>
/// 旧单据导出族的受控只读读取接缝（ERP-308 Stage 2）：在打开查询前拒绝未知 / 畸形族标识与未知列，
/// 只使用受控常量标识符（表名 / 列名）与参数化值，绝不做 SELECT * / 模型 SQL / 写入存储过程。
/// <para>实现方负责行 / 时间 / 页码 / 日期范围的有界约束与缺表缺列的显式 environment-blocked 失败。</para>
/// </summary>
public interface ILegacyBillExportReadService
{
    /// <summary>读取指定旧单据导出族的一页原值数据（只读、有界）。</summary>
    Task<LegacyBillExportPage> ReadPageAsync(LegacyBillExportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取指定旧单据导出族的完整匹配事实快照（ERP-319 Stage 2）：同一只读 Serializable 事务内、
    /// 稳定按 Oid 降序、有界（≤ <c>MaxSnapshotFacts</c> 条、≤ <c>MaxSnapshotBytes</c> 字节），
    /// 超限 / 缺表缺列 / 取消 / 不支持隔离级别显式失败，绝不静默截断或重试。
    /// <para>列顺序 / null / 原币 / 原单位原样保留；默认实现为 environment-blocked，旧实现方保持编译。</para>
    /// </summary>
    Task<IReportConfigurationReadSnapshot> ReadSnapshotAsync(
        LegacyBillExportQuery query,
        string correlationId,
        string scopeFingerprint,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        ReportConfigurationEvidenceContextDto evidence,
        CancellationToken cancellationToken = default)
        => Task.FromException<IReportConfigurationReadSnapshot>(
            new BusinessException(
                "旧单据导出族不支持有界一致只读快照（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported));
}
