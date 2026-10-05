using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 旧单据导出族目录（ERP-308 Stage 2）：16 个 BillProc Bills 条目的服务端唯一受控清单。
/// <para>每条都精确承载表名 / 日期字段 / 菜单授权 / 有序导出列（与 <c>BillProcController.Bills</c> 与
/// <c>BillProcController.Export.ExportColumns</c> 完全一致），绝不来自客户端、绝不落到数据库全量元数据发现。</para>
/// <para>sales-order / purchase-order / inquiry 三个族还须同时具备专用导出菜单（sales-order-export /
/// purchase-order-export / inquiry-export），否则 fail closed 隐藏。</para>
/// </summary>
public static class LegacyBillExportCatalog
{
    /// <summary>16 个旧单据导出族（顺序稳定，与 BillProcController.Bills 一致）。</summary>
    public static readonly IReadOnlyList<LegacyBillExportFamilyDefinition> Families = BuildFamilies();

    private static readonly Dictionary<string, LegacyBillExportFamilyDefinition> ByFamilyKey =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, LegacyBillExportFamilyDefinition> ByDatasetKey =
        new(StringComparer.OrdinalIgnoreCase);

    static LegacyBillExportCatalog()
    {
        foreach (var family in Families)
        {
            ByFamilyKey[family.FamilyKey] = family;
            ByDatasetKey[family.DatasetKey] = family;
        }
    }

    /// <summary>
    /// 16 个旧单据导出族的迁移登记册条目（ERP-308 Stage 2）：派生自 <see cref="Families"/>（同一受控目录、顺序稳定），
    /// 以显式族键 <c>export:bill-proc:{familyKey}</c> 取代单一聚合键，使聚合无法再掩盖遗漏族；
    /// 每族一一对应受控数据集（<c>bill-export:{familyKey}</c>）与既有菜单授权（销售订单 / 采购订单 / 询价单族还须同时具备专用导出菜单）。
    /// </summary>
    public static readonly IReadOnlyList<ReportMigrationRegistryEntryDefinition> RegistryEntries = BuildRegistryEntries();

    private static IReadOnlyList<ReportMigrationRegistryEntryDefinition> BuildRegistryEntries()
        => Families
            .Select(f => new ReportMigrationRegistryEntryDefinition(
                "export:bill-proc:" + f.FamilyKey,
                f.Title + " Excel 导出",
                ReportMigrationRegistryCategories.Export,
                f.DatasetKey,
                f.RequiredMenuCodes,
                f.RequiredMenuText,
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
                true,
                false))
            .ToList();

    /// <summary>按族键解析（未知 / 空白 / 畸形一律拒绝；在打开任何查询之前调用）。</summary>
    public static LegacyBillExportFamilyDefinition Resolve(string? familyKey)
    {
        if (string.IsNullOrWhiteSpace(familyKey))
            throw BusinessException.InvalidParameter("旧单据导出族标识不能为空");

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            throw BusinessException.InvalidParameter($"旧单据导出族标识非法：{key}");

        if (!ByFamilyKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的旧单据导出族标识：{key}");

        return family;
    }

    /// <summary>按族键解析（失败返回 false，不抛异常）。</summary>
    public static bool TryResolve(string? familyKey, out LegacyBillExportFamilyDefinition family)
    {
        family = null!;
        if (string.IsNullOrWhiteSpace(familyKey))
            return false;

        var key = familyKey.Trim();
        if (key.Length > 64 || ContainsUnsafeIdentifier(key))
            return false;

        return ByFamilyKey.TryGetValue(key, out family!);
    }

    /// <summary>按数据集键解析（未知 / 空白 / 畸形一律拒绝）。</summary>
    public static LegacyBillExportFamilyDefinition ResolveByDatasetKey(string? datasetKey)
    {
        if (string.IsNullOrWhiteSpace(datasetKey))
            throw BusinessException.InvalidParameter("旧单据导出数据集键不能为空");

        var key = datasetKey.Trim();
        if (!ByDatasetKey.TryGetValue(key, out var family))
            throw BusinessException.InvalidParameter($"未知的旧单据导出数据集键：{key}");

        return family;
    }

    /// <summary>
    /// 解析并校验请求列键：全部必须命中族白名单（大小写不敏感），去重并保持请求顺序；空 = 族默认全列。
    /// <para>任何未知 / 空白 / 畸形列键在打开查询前拒绝（fail closed），绝不作为 SQL 标识符拼接。</para>
    /// </summary>
    public static IReadOnlyList<LegacyBillExportColumn> ResolveColumns(
        LegacyBillExportFamilyDefinition family, IReadOnlyList<string>? requested)
    {
        ArgumentNullException.ThrowIfNull(family);

        var byKey = family.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var keys = (requested ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
            return family.Columns;

        var result = new List<LegacyBillExportColumn>(keys.Count);
        foreach (var key in keys)
        {
            if (key.Length > 64 || ContainsUnsafeIdentifier(key) || !byKey.TryGetValue(key, out var column))
                throw BusinessException.InvalidParameter($"旧单据导出族 {family.FamilyKey} 不支持的字段：{key}");

            result.Add(column);
        }

        return result;
    }

    private static bool ContainsUnsafeIdentifier(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':'));

    private static string Dataset(string familyKey)
        => ReportConfigurationConstants.DatasetBillExportPrefix + familyKey;

    private static LegacyBillExportColumn C(string key, string title, char type)
        => new(key, title, type switch
        {
            'd' => ReportConfigurationConstants.TypeDate,
            'n' => ReportConfigurationConstants.TypeNumber,
            _ => ReportConfigurationConstants.TypeText,
        });

    private static LegacyBillExportFamilyDefinition Family(
        string familyKey, string table, string dateField, string title,
        string[] menus, string menuText, params (string Key, string Title, char Type)[] columns)
        => new(
            familyKey,
            Dataset(familyKey),
            table,
            dateField,
            title,
            menus,
            menuText,
            columns.Select(c => C(c.Key, c.Title, c.Type)).ToList());

    private static List<LegacyBillExportFamilyDefinition> BuildFamilies() => new()
    {
        Family("sales-order", "SalesOrder", "OrderDate", "销售订单",
            new[] { "sales-order", "sales-order-export" }, "销售订单 + 销售订单导出",
            ("BillNo", "单据号", 't'), ("OrderDate", "订单日期", 'd'), ("CustId", "客户Id", 'n'),
            ("EmpId", "业务员Id", 'n'), ("Currency", "币种", 'n'), ("ExchangeRate", "汇率", 'n'),
            ("TotalAmount", "总金额", 'n'), ("DepositAmount", "定金", 'n'), ("DepositRatio", "定金比例%", 'n'),
            ("DeliveryDate", "交货日期", 'd'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("purchase-order", "PurchaseOrder", "OrderDate", "采购订单",
            new[] { "purchase-order", "purchase-order-export" }, "采购订单 + 采购订单导出",
            ("BillNo", "单据号", 't'), ("OrderDate", "订单日期", 'd'), ("SupplierId", "供应商Id", 'n'),
            ("EmpId", "采购员Id", 'n'), ("Currency", "币种", 'n'), ("ExchangeRate", "汇率", 'n'),
            ("TotalAmount", "总金额", 'n'), ("PaymentTerms", "付款条件", 't'), ("DeliveryDate", "交货日期", 'd'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("inquiry", "Inquiry", "InquiryDate", "询价单",
            new[] { "inquiry", "inquiry-export" }, "询价单 + 询价单导出",
            ("BillNo", "单据号", 't'), ("InquiryDate", "询价日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("ContactPerson", "联系人", 't'), ("ContactPhone", "联系电话", 't'), ("EmpId", "业务员Id", 'n'),
            ("Currency", "币种", 'n'), ("ExchangeRate", "汇率", 'n'), ("ValidDays", "有效期", 'n'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("stock-in", "StockIn", "StockInDate", "采购入库",
            new[] { "stock-in" }, "采购入库",
            ("BillNo", "单据号", 't'), ("StockInDate", "入库日期", 'd'), ("SupplierId", "供应商Id", 'n'),
            ("WarehouseId", "仓库Id", 'n'), ("PurchaseOrderId", "采购订单Id", 'n'),
            ("TotalQuantity", "总数量", 'n'), ("TotalWeight", "总毛重", 'n'), ("TotalVolume", "总体积", 'n'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("stock-out", "StockOut", "StockOutDate", "销售出库",
            new[] { "stock-out" }, "销售出库",
            ("BillNo", "单据号", 't'), ("StockOutDate", "出库日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("WarehouseId", "仓库Id", 'n'), ("SalesOrderId", "销售订单Id", 'n'),
            ("TotalQuantity", "总数量", 'n'), ("TotalWeight", "总毛重", 'n'), ("TotalVolume", "总体积", 'n'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("receipt", "FinanceReceipt", "ReceiptDate", "收款单",
            new[] { "receipt" }, "收款单",
            ("BillNo", "单据号", 't'), ("ReceiptDate", "收款日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("Amount", "金额", 'n'), ("Currency", "币种", 'n'), ("PaymentMethod", "收款方式", 't'),
            ("BankAccount", "银行账户", 't'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("payment", "FinancePayment", "PaymentDate", "付款单",
            new[] { "payment" }, "付款单",
            ("BillNo", "单据号", 't'), ("PaymentDate", "付款日期", 'd'), ("SupplierId", "供应商Id", 'n'),
            ("PaymentApplyId", "申请单Id", 'n'), ("Amount", "金额", 'n'), ("Currency", "币种", 'n'),
            ("PaymentMethod", "付款方式", 't'), ("BankAccount", "银行账户", 't'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("deposit-apply", "FinanceDepositApply", "ApplyDate", "定金申请单",
            new[] { "deposit-apply" }, "定金申请单",
            ("BillNo", "单据号", 't'), ("ApplyDate", "申请日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("SalesOrderId", "销售订单Id", 'n'), ("Amount", "金额", 'n'), ("Currency", "币种", 'n'),
            ("ExchangeRate", "汇率", 'n'), ("BankAccount", "银行账户", 't'), ("Payee", "收款方", 't'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("payment-apply", "FinancePaymentApply", "ApplyDate", "货款申请单",
            new[] { "payment-apply" }, "货款申请单",
            ("BillNo", "单据号", 't'), ("ApplyDate", "申请日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("SalesOrderId", "销售订单Id", 'n'), ("Amount", "金额", 'n'), ("Currency", "币种", 'n'),
            ("ExchangeRate", "汇率", 'n'), ("BankAccount", "银行账户", 't'), ("Payee", "收款方", 't'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("container-settlement", "FinanceContainerSettlement", "SettlementDate", "装柜结算单",
            new[] { "container-settlement" }, "装柜结算单",
            ("BillNo", "单据号", 't'), ("SettlementDate", "结算日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("LoadingListId", "装柜清单Id", 'n'), ("TotalAmount", "结算总金额", 'n'),
            ("FreightCost", "海运费", 'n'), ("OtherCost", "其他费用", 'n'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("bulk-settlement", "FinanceBulkSettlement", "SettlementDate", "散货结算单",
            new[] { "bulk-settlement" }, "散货结算单",
            ("BillNo", "单据号", 't'), ("SettlementDate", "结算日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("TotalAmount", "结算总金额", 'n'), ("FreightCost", "海运费", 'n'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("complaint", "FinanceComplaint", "ComplaintDate", "客诉单",
            new[] { "complaint" }, "客诉单",
            ("BillNo", "单据号", 't'), ("ComplaintDate", "客诉日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("SalesOrderId", "销售订单Id", 'n'), ("ComplaintType", "客诉类型", 't'),
            ("ResponsibleDept", "责任部门", 't'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("receiving-plan", "ContainerReceivingPlan", "PlanDate", "收货计划",
            new[] { "receiving-plan" }, "收货计划",
            ("BillNo", "单据号", 't'), ("PlanDate", "计划日期", 'd'), ("SupplierId", "供应商Id", 'n'),
            ("BookingNo", "订柜单号", 't'), ("ContainerType", "柜型", 't'), ("ContainerNo", "柜号", 't'),
            ("ExpectedArrivalDate", "预计到货", 'd'), ("Destination", "目的地", 't'),
            ("TotalQuantity", "总件数", 'n'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("booking", "ContainerBooking", "BookingDate", "订柜信息",
            new[] { "booking" }, "订柜信息",
            ("BillNo", "单据号", 't'), ("BookingDate", "订柜日期", 'd'), ("CustomerId", "客户Id", 'n'),
            ("SupplierId", "供应商Id", 'n'), ("ContainerType", "柜型", 't'), ("ShippingCompany", "船公司", 't'),
            ("VoyageNo", "航次", 't'), ("SailingDate", "开船日期", 'd'), ("DeparturePort", "起运港", 't'),
            ("DestinationPort", "目的港", 't'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("pre-loading", "ContainerPreLoading", "LoadingDate", "预装柜单",
            new[] { "pre-loading" }, "预装柜单",
            ("BillNo", "单据号", 't'), ("LoadingDate", "装柜日期", 'd'), ("BookingId", "订柜Id", 'n'),
            ("ContainerNo", "柜号", 't'), ("SealNo", "封条号", 't'), ("TotalCartons", "总箱数", 'n'),
            ("TotalWeight", "总毛重", 'n'), ("TotalVolume", "总体积", 'n'), ("Status", "状态", 'n'), ("Remark", "备注", 't')),
        Family("loading-list", "ContainerLoadingList", "LoadingDate", "装柜清单",
            new[] { "loading-list" }, "装柜清单",
            ("BillNo", "单据号", 't'), ("LoadingDate", "装柜日期", 'd'), ("PreLoadingId", "预装柜单Id", 'n'),
            ("ContainerNo", "柜号", 't'), ("CustomerId", "客户Id", 'n'), ("ShippingMark", "唛头", 't'),
            ("TotalCartons", "总箱数", 'n'), ("TotalWeight", "总毛重", 'n'), ("TotalVolume", "总体积", 'n'),
            ("Status", "状态", 'n'), ("Remark", "备注", 't')),
    };
}
