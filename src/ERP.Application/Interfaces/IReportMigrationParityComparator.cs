using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 迁移 parity 四维比对接缝（ERP-329）：把有界旧路由结果快照与有界通用预览快照做纯只读比对，
/// 得出数据粒度 / 币种单位 / 权限三维结论，并借由调用方传入的输出语义比对结论合成完整四维证据。
/// <para>绝不触碰数据库、绝不扩权、绝不新增实体或 API；比较是确定性的，不一致明细稳定排序。</para>
/// </summary>
public interface IReportMigrationParityComparator
{
    /// <summary>
    /// 比较有界旧路由结果快照与有界通用预览快照。
    /// <para><paramref name="outputSemanticsMatched"/> 为独立导出比对接缝的结论（本比较器不负责导出语义，
    /// 仅透传合成 <see cref="ReportMigrationParityEvidenceDto"/>）；其余三维由本比较器计算。</para>
    /// </summary>
    ReportMigrationParityComparisonResultDto Compare(
        ReportMigrationParitySnapshotDto legacy,
        ReportMigrationParitySnapshotDto generic,
        bool outputSemanticsMatched);
}
