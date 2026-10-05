using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 旧打印快照统一读取接缝（ERP-334 Stage 2）：把注册的基础资料打印族（7 族）与销售单据打印族
/// （报价单 / 形式发票 PI）归一化为同一个有界旧打印快照，供旧报表来源统一接缝与 parity 证据源消费。
/// <para>实现方必须复用既有 <c>BaseDataControllers</c> / <c>BaseDataIoController</c> 与
/// <c>QuotationController.GetPrint</c> / <c>ProformaInvoiceController.GetPrint</c> 的读取语义，
/// 绝不调用通用数据集适配器充当旧侧、绝不执行任意 SQL、绝不新增实体 / 权限 / 环境变量；每次读取都按
/// 当前账号重新校验菜单与客户业务员数据范围（fail closed），并遵守有界分页与调用方取消令牌。</para>
/// </summary>
public interface ILegacyPrintSnapshotReadService
{
    /// <summary>读取指定打印快照来源的一页有界旧打印快照。</summary>
    Task<LegacyPrintSnapshotResult> ReadAsync(
        LegacyPrintSnapshotRequest request, CancellationToken cancellationToken = default);
}
