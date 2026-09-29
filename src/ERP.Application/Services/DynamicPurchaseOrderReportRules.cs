using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态采购订单报表（ERP-125）的纯规则：字段白名单、字段 / 供应商 / 状态 / 币种 / 日期区间 / 页大小校验、
/// 行映射与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>purchase-order</c> 采购订单菜单，见 <c>SeedData.Menus</c>）；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// <para>财务证据边界：本报表只回显采购订单 / 供应商的原始持久化字段，不计算应付余额、不做结算；
/// 因此「结算进度」（SettlementProgress）字段刻意不纳入白名单，金额按订单原币呈现、不做跨币种换算或汇总。</para>
/// </summary>
public static class DynamicPurchaseOrderReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用采购订单模块菜单；与 SeedData.Menus 同源）</summary>
    public const string RequiredMenuCode = "purchase-order";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购订单";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 100;

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读采购订单报表预览：仅按选定白名单字段与有界筛选读取当前账号可见的采购订单，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限采购订单持久化字段白名单；筛选仅限供应商 / 订单日期 / 状态 / 币种；结果限定在未删除采购订单内；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读快照：供应商与订单引用仅为原始持久化证据，不构成应付余额或结算结论；金额按订单原币呈现、不做跨币种换算或汇总";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(string Key, string Label, string DataType, bool Filterable, Func<PurchaseOrder, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("id", "订单Id", "number", false, o => o.Id),
        new("orderNo", "采购单号", "text", false, o => o.OrderNo),
        new("orderDate", "订单日期", "date", true, o => o.OrderDate),
        new("supplierId", "供应商Id", "number", true, o => o.SupplierId),
        new("buyerId", "采购员Id", "number", false, o => o.BuyerId),
        new("currency", "币种", "enum", true, o => o.Currency.ToString()),
        new("exchangeRate", "汇率", "number", false, o => o.ExchangeRate),
        new("totalAmount", "订单总额", "number", false, o => o.TotalAmount),
        new("paymentTerms", "付款条件", "text", false, o => o.PaymentTerms),
        new("deliveryDate", "交货日期", "date", false, o => o.DeliveryDate),
        new("portId", "起运港Id", "number", false, o => o.PortId),
        new("owningCustomerId", "归属客户Id", "number", false, o => o.OwningCustomerId),
        new("owningCustomerName", "归属客户名称", "text", false, o => o.OwningCustomerName),
        new("owningSalesOrderId", "归属销售订单Id", "number", false, o => o.OwningSalesOrderId),
        new("owningSalesOrderNo", "归属销售订单号", "text", false, o => o.OwningSalesOrderNo),
        new("advanceOnBehalf", "代垫货款", "boolean", false, o => o.AdvanceOnBehalf),
        new("supplierConfirmedDate", "供应商确认交期", "date", false, o => o.SupplierConfirmedDate),
        new("taxRate", "税率%", "number", false, o => o.TaxRate),
        new("taxIncluded", "含税单价", "boolean", false, o => o.TaxIncluded),
        new("arrivalProgress", "到货进度", "text", false, o => o.ArrivalProgress),
        new("qcStatus", "验货状态", "text", false, o => o.QcStatus),
        new("contractNo", "采购合同号", "text", false, o => o.ContractNo),
        new("status", "状态", "enum", true, o => o.Status.ToString()),
        new("remark", "备注", "text", false, o => o.Remark),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        new(StringComparer.OrdinalIgnoreCase);

    static DynamicPurchaseOrderReportRules()
    {
        foreach (var field in Fields)
            FieldByKey[field.Key] = field;
    }

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static IReadOnlyList<string> AllFieldKeys { get; } = Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限白名单字段目录（按目录顺序）</summary>
    public static List<DynamicPurchaseOrderReportFieldDto> GetCatalog() => Fields.Select(ToDto).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicPurchaseOrderReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicPurchaseOrderReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def) ? ToDto(def) : null;

    private static DynamicPurchaseOrderReportFieldDto ToDto(FieldDef def)
        => new(def.Key, def.Label, def.DataType, def.Filterable);

    // ==================== 4. 校验与规范化 ====================

    /// <summary>
    /// 规范化选定字段（fail closed）：未知字段显式拒绝；空 / 留空 = 返回全部白名单字段（目录顺序）；
    /// 去重并保持请求顺序。
    /// </summary>
    public static IReadOnlyList<string> NormalizeFields(IEnumerable<string>? fields)
    {
        var requested = (fields ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();
        if (requested.Count == 0)
            return AllFieldKeys;

        var ordered = new List<string>();
        foreach (var key in requested)
        {
            if (!FieldByKey.TryGetValue(key, out var def))
                throw BusinessException.InvalidParameter($"未知字段: {key}");
            if (!ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                ordered.Add(def.Key);
        }
        return ordered;
    }

    /// <summary>规范化状态筛选（空 = 不过滤；未知取值显式拒绝）</summary>
    public static DocumentStatus? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;
        var normalized = status.Trim();
        foreach (var name in Enum.GetNames<DocumentStatus>())
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<DocumentStatus>(name, ignoreCase: true);
        }
        throw BusinessException.InvalidParameter(
            $"无效的状态筛选值: {status}（可选：Pending / Submitted / Approved / Rejected / Completed / Cancelled）");
    }

    /// <summary>规范化币种筛选（空 = 不过滤；未知取值显式拒绝）</summary>
    public static Currency? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;
        var normalized = currency.Trim();
        foreach (var name in Enum.GetNames<Currency>())
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<Currency>(name, ignoreCase: true);
        }
        throw BusinessException.InvalidParameter(
            $"无效的币种筛选值: {currency}（可选：CNY / USD / EUR / HKD / GBP / JPY）");
    }

    /// <summary>校验订单日期区间（开始晚于结束 = 无效）</summary>
    public static void ValidateDateRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && start.Value.Date > end.Value.Date)
            throw BusinessException.InvalidParameter("开始日期不能晚于结束日期");
    }

    /// <summary>校验每页条数（1 ~ 上限，超出直接拒绝）</summary>
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1~{MaxPageSize} 之间");
    }

    // ==================== 5. 行映射 ====================

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    public static object? Select(PurchaseOrder order, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(order)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>把一张采购订单映射为「选定字段 → 值」的只读行（仅含选定字段）</summary>
    public static Dictionary<string, object?> BuildRow(PurchaseOrder order, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(order, key);
        return row;
    }
}
