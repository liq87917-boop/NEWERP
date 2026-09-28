using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 库位 + 批次库存基础（ERP-096）的纯规则（无数据库依赖，便于逐条单测）：
/// 批次 / 库位编码的归一化与长度校验、移动数量的正数校验、调拨仓库与守恒校验。
/// <para>长度口径与既有实体 <c>BatchNo</c> / <c>WarehouseName</c> 等 NVARCHAR(50/100) 一致，
/// 本规则只做校验与归一化，不落库、不改写任何库存或单据。</para>
/// </summary>
public static class InventoryLocationLotRules
{
    /// <summary>批次 / 库位编码最大长度（与既有明细 <c>BatchNo</c> 的 MaxLength(50) 一致）</summary>
    public const int IdentityMaxLength = 50;

    /// <summary>数量对账容差（4 位小数口径，吸收浮点 / 取整误差）</summary>
    public const decimal ReconciliationTolerance = 0.0001m;

    /// <summary>空批次桶：无法唯一归属批次的数量统一计入空串批次</summary>
    public const string NoLot = "";

    /// <summary>归一化批次号（去首尾空白；空值 / 空白返回空串；超长拒绝）。批次为可选身份。</summary>
    public static string NormalizeLot(string? lotNo)
    {
        var value = lotNo?.Trim() ?? string.Empty;
        if (value.Length > IdentityMaxLength)
            throw BusinessException.InvalidParameter($"批次号长度不能超过 {IdentityMaxLength} 个字符");
        return value;
    }

    /// <summary>归一化库位编码（去首尾空白；空值 / 空白返回空串；超长拒绝）。库位为可选细分。</summary>
    public static string NormalizeLocationCode(string? locationCode)
    {
        var value = locationCode?.Trim() ?? string.Empty;
        if (value.Length > IdentityMaxLength)
            throw BusinessException.InvalidParameter($"库位编码长度不能超过 {IdentityMaxLength} 个字符");
        return value;
    }

    /// <summary>移动数量必须为正数（基础单位）。</summary>
    public static void EnsurePositiveQuantity(decimal quantity, string productName)
    {
        if (quantity <= 0)
            throw BusinessException.InvalidParameter($"商品 [{productName}] 的数量必须大于 0");
    }

    /// <summary>仓库 Id 必须为正整数。</summary>
    public static long RequireWarehouse(long warehouseId, string role)
    {
        if (warehouseId <= 0)
            throw BusinessException.InvalidParameter($"{role}仓库 Id 必须为正整数");
        return warehouseId;
    }

    /// <summary>调拨的调出仓与调入仓必须不同。</summary>
    public static void EnsureDistinctWarehouses(long fromWarehouseId, long toWarehouseId)
    {
        if (fromWarehouseId == toWarehouseId)
            throw BusinessException.InvalidParameter("调拨的调出仓与调入仓不能相同");
    }

    /// <summary>
    /// 调拨守恒校验（纯函数）：调出数量 = 调入数量、调出成本 = 调入成本（同一非负成本单价），
    /// 且调出仓现存量足以覆盖调拨数量（拒绝负库存）。返回结果而非抛异常，便于逐条断言。
    /// </summary>
    public static TransferConservationResult EvaluateTransfer(long fromWarehouseId, long toWarehouseId,
        decimal quantity, decimal unitCost, decimal sourceOnHand)
    {
        var problems = new List<string>();

        if (fromWarehouseId <= 0 || toWarehouseId <= 0)
            problems.Add("调出仓 / 调入仓 Id 必须为正整数");
        else if (fromWarehouseId == toWarehouseId)
            problems.Add("调出仓与调入仓不能相同");

        var quantityConserved = quantity > 0;
        if (!quantityConserved)
            problems.Add("调拨数量必须大于 0");

        var costConserved = unitCost >= 0;
        if (!costConserved)
            problems.Add("调拨成本单价不能为负数");

        var sourceSufficient = sourceOnHand >= quantity;
        if (!sourceSufficient)
            problems.Add($"调出仓现存量 {sourceOnHand} 不足以调拨 {quantity}，不允许出现负库存");

        var valid = problems.Count == 0 && sourceSufficient;
        return new TransferConservationResult(
            valid,
            quantityConserved,
            costConserved,
            sourceSufficient,
            sourceOnHand,
            quantity,
            valid
                ? "调拨数量与成本守恒，调出仓库存充足"
                : string.Join("；", problems));
    }
}
