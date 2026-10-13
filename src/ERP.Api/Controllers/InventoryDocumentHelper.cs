using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 库存单据（盘点/调拨/销售退货/采购退货）共用助手（ERP-009）：
/// 统一「仓库名称解析」「商品信息补齐」「库存移动上下文构造」三件事，
/// 避免四份控制器各写一遍，也保证落库的冗余字段口径一致。
/// <para>ERP-501 边界说明：本助手的仓库名称与商品信息解析只产出**展示快照**（缺失 / 已删除 / 停用一律回退为空串或
/// 调用方文本），**绝不构成主数据在用的证明**。单据的「在用主数据」校验由各模块护栏
/// （如 <c>StockTransferPostingRules.EnsureLiveMasterDataAsync</c>）在单号预约与任何写入之前独立完成。</para>
/// </summary>
/// <remarks>声明为 public 以便单元测试直接断言来源单据类型常量（与 BillProcController.BillMeta 同一做法）。</remarks>
public static class InventoryDocumentHelper
{
    /// <summary>来源单据类型：库存盘点/调整单</summary>
    public const string StockAdjustmentType = "StockAdjustment";

    /// <summary>来源单据类型：仓库调拨单</summary>
    public const string StockTransferType = "StockTransfer";

    /// <summary>来源单据类型：销售退货单</summary>
    public const string SalesReturnType = "SalesReturn";

    /// <summary>来源单据类型：采购退货单</summary>
    public const string PurchaseReturnType = "PurchaseReturn";

    /// <summary>来源单据类型：销售出库单（退货成本回取）</summary>
    public const string StockOutType = "StockOut";

    /// <summary>来源单据类型：采购入库单（退货成本回取）</summary>
    public const string StockInType = "StockIn";

    /// <summary>
    /// 按仓库 Id 取仓库名称（仓库被删除或不存在时返回空串，不阻断单据保存）
    /// <para>ERP-501：返回空串只是**展示兜底**，绝不代表该仓库不存在；单据写入前的在用主数据校验
    /// 必须独立查询主数据，不得以「仓名非空」作为引用有效的证据。</para>
    /// </summary>
    public static async Task<string> WarehouseNameAsync(IErpDbContext db, long warehouseId,
        CancellationToken cancellationToken = default)
    {
        if (warehouseId <= 0) return string.Empty;
        return await db.BaseWarehouses.AsNoTracking()
            .Where(w => w.Id == warehouseId && !w.IsDeleted)
            .Select(w => w.WarehouseName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    /// <summary>
    /// 按商品 Id 补齐编码/名称/规格/单位：前端只填了商品名称时，
    /// 单据行与库存流水也能显示完整商品信息（历史手输数据同样兜底）。
    /// <para>ERP-501：商品缺失 / 已删除时回退为调用方文本只是**展示兜底**，绝不代表该商品存在；
    /// 单据写入前的在用主数据校验必须独立查询主数据，不得以前端文本作为引用有效的证据。</para>
    /// </summary>
    public static async Task<(string Code, string Name, string Spec, string Unit)> ResolveProductAsync(
        IErpDbContext db, long? productId, string fallbackName, string fallbackSpec, string fallbackUnit,
        CancellationToken cancellationToken = default)
    {
        if (productId is null or <= 0)
            return (string.Empty, fallbackName, fallbackSpec, fallbackUnit);

        var product = await db.BaseProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId.Value && !p.IsDeleted, cancellationToken);
        if (product is null) return (string.Empty, fallbackName, fallbackSpec, fallbackUnit);

        return (
            product.ProductCode,
            string.IsNullOrWhiteSpace(fallbackName) ? product.ProductName : fallbackName,
            string.IsNullOrWhiteSpace(fallbackSpec) ? product.Spec : fallbackSpec,
            string.IsNullOrWhiteSpace(fallbackUnit) ? product.Unit : fallbackUnit);
    }

    /// <summary>构造库存移动上下文（仓库名称在内部解析，保证流水里始终有可读的仓库名）</summary>
    public static async Task<InventoryMovementContext> BuildContextAsync(IErpDbContext db, string sourceDocType,
        long sourceDocId, string sourceDocNo, InventoryMovementType movementType, long warehouseId,
        long? productId, string productCode, string productName, string spec, string unit,
        DateTime movementDate, string remark, CancellationToken cancellationToken = default)
        => new()
        {
            SourceDocType = sourceDocType,
            SourceDocId = sourceDocId,
            SourceDocNo = sourceDocNo,
            MovementType = movementType,
            WarehouseId = warehouseId,
            WarehouseName = await WarehouseNameAsync(db, warehouseId, cancellationToken),
            ProductId = productId,
            ProductCode = productCode,
            ProductName = productName,
            Spec = spec,
            Unit = unit,
            MovementDate = movementDate,
            Remark = remark
        };
}
