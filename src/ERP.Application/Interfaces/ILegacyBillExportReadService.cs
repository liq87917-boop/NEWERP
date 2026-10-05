using ERP.Application.DTOs;

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
}
