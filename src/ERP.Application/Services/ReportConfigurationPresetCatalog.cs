using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 报表预设模板的编译期种子（ERP-296 Stage 2）：有限、不可变；每条预设键控到一条迁移登记册条目，
/// 把已迁移旧报表重新表达为受控数据集上的可校验 <see cref="ReportConfigurationDefinition"/>。
/// <para>预设只作为「数据驱动的定义」存在，绝不新增权限 / 存储 / 每报表控制器或导出器。</para>
/// </summary>
/// <summary>单节预设共享参数的编译期元数据（ERP-314 Stage 2）：键 / 类型 / 中文标签 / 是否必填。</summary>
internal sealed record ReportConfigurationPresetParameterSeed(string Key, string Type, string Label, bool Required);

/// <summary>单节预设共享参数的编译期绑定（字段键 + 有限操作符）。</summary>
internal sealed record ReportConfigurationPresetParameterBindingSeed(string FieldKey, string Operator);

internal sealed record ReportConfigurationPresetSeed(
    string PresetKey,
    string LegacyKey,
    string Name,
    string DatasetKey,
    ReportConfigurationDefinition Definition,
    IReadOnlyList<ReportConfigurationPresetParameterSeed>? Parameters = null,
    IReadOnlyDictionary<string, ReportConfigurationPresetParameterBindingSeed>? ParameterBindings = null);

internal static class ReportConfigurationPresetManifest
{
    /// <summary>有限、不可变的预设清单（顺序稳定；与迁移登记册条目一一对应）。</summary>
    public static readonly IReadOnlyList<ReportConfigurationPresetSeed> Presets = new[]
    {
        new ReportConfigurationPresetSeed(
            "sales-order",
            "dynamic:sales-order",
            "销售订单（迁移预设）",
            ReportConfigurationConstants.DatasetSalesOrder,
            SalesOrderPresetDefinition(),
            new ReportConfigurationPresetParameterSeed[]
            {
                new(ReportConfigurationPresetConstants.ParameterCustomer,
                    ReportConfigurationPresetConstants.ParameterTypeNumber, "客户", Required: false),
                new(ReportConfigurationPresetConstants.ParameterDate,
                    ReportConfigurationPresetConstants.ParameterTypeDate, "日期区间", Required: false),
                new(ReportConfigurationPresetConstants.ParameterStatus,
                    ReportConfigurationPresetConstants.ParameterTypeText, "状态", Required: false),
            },
            new Dictionary<string, ReportConfigurationPresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
            {
                [ReportConfigurationPresetConstants.ParameterCustomer] = new("customerId", ReportConfigurationConstants.OperatorEq),
                [ReportConfigurationPresetConstants.ParameterDate] = new("orderDate", ReportConfigurationConstants.OperatorBetween),
                [ReportConfigurationPresetConstants.ParameterStatus] = new("status", ReportConfigurationConstants.OperatorEq),
            }),
        new ReportConfigurationPresetSeed(
            "receivable",
            "dynamic:receivable",
            "客户应收账款证据（迁移预设）",
            ReportConfigurationConstants.DatasetReceivable,
            ReceivablePresetDefinition(),
            new ReportConfigurationPresetParameterSeed[]
            {
                new(ReportConfigurationPresetConstants.ParameterCustomer,
                    ReportConfigurationPresetConstants.ParameterTypeNumber, "客户", Required: false),
                new(ReportConfigurationPresetConstants.ParameterDate,
                    ReportConfigurationPresetConstants.ParameterTypeDate, "日期区间", Required: false),
                new(ReportConfigurationPresetConstants.ParameterStatus,
                    ReportConfigurationPresetConstants.ParameterTypeText, "分配状态", Required: false),
            },
            new Dictionary<string, ReportConfigurationPresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
            {
                [ReportConfigurationPresetConstants.ParameterCustomer] = new("customerId", ReportConfigurationConstants.OperatorEq),
                [ReportConfigurationPresetConstants.ParameterDate] = new("invoiceDate", ReportConfigurationConstants.OperatorBetween),
                [ReportConfigurationPresetConstants.ParameterStatus] = new("allocationState", ReportConfigurationConstants.OperatorEq),
            }),
        new ReportConfigurationPresetSeed(
            "inventory-movement",
            "report:inventory-movement",
            "库存移动与呆滞报表（迁移预设）",
            ReportConfigurationConstants.DatasetInventoryMovement,
            InventoryMovementPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "inventory-aging",
            "report:inventory-aging",
            "库存库龄与成本估值报表（迁移预设）",
            ReportConfigurationConstants.DatasetInventoryAging,
            InventoryAgingPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "stock-alert",
            "report:stock-alert",
            "库存预警表（迁移预设）",
            ReportConfigurationConstants.DatasetStockAlert,
            StockAlertPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "inventory-movement-dynamic",
            "dynamic:inventory-movement",
            "动态库存移动报表（迁移预设）",
            ReportConfigurationConstants.DatasetInventoryMovement,
            InventoryMovementPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "inventory-aging-dynamic",
            "dynamic:inventory-aging",
            "动态库存库龄报表（迁移预设）",
            ReportConfigurationConstants.DatasetInventoryAging,
            InventoryAgingPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "product-sales-ranking",
            "report:product-sales-ranking",
            "商品销量排名榜（迁移预设）",
            ReportConfigurationConstants.DatasetProductSalesRanking,
            ProductSalesRankingPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "product-sales-ranking-dynamic",
            "dynamic:product-sales-ranking",
            "动态商品销量排名报表（迁移预设）",
            ReportConfigurationConstants.DatasetProductSalesRanking,
            ProductSalesRankingPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "order-profit",
            "report:order-profit",
            "订单利润暂估表（迁移预设）",
            ReportConfigurationConstants.DatasetOrderProfit,
            OrderProfitPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "order-profit-dynamic",
            "dynamic:order-profit",
            "动态订单利润暂估报表（迁移预设）",
            ReportConfigurationConstants.DatasetOrderProfit,
            OrderProfitPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "sales-commission",
            "report:sales-commission",
            "业务员提成表（迁移预设）",
            ReportConfigurationConstants.DatasetSalesCommission,
            SalesCommissionPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "sales-commission-dynamic",
            "dynamic:sales-commission",
            "动态业务员提成证据报表（迁移预设）",
            ReportConfigurationConstants.DatasetSalesCommission,
            SalesCommissionPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "container-stats",
            "report:container-stats",
            "柜量与装柜利用率统计（迁移预设）",
            ReportConfigurationConstants.DatasetContainerStats,
            ContainerStatsPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "container-stats-dynamic",
            "dynamic:container-stats",
            "动态柜量与装柜利用率证据报表（迁移预设）",
            ReportConfigurationConstants.DatasetContainerStats,
            ContainerStatsPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "customer-shipment",
            "report:customer-shipment",
            "客户出货量统计表（迁移预设）",
            ReportConfigurationConstants.DatasetCustomerShipment,
            CustomerShipmentPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "customer-shipment-dynamic",
            "dynamic:customer-shipment",
            "动态客户出货量统计报表（迁移预设）",
            ReportConfigurationConstants.DatasetCustomerShipment,
            CustomerShipmentPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "shipment-finance",
            "dynamic:shipment-finance",
            "动态销售订单出货/财务进度报表（迁移预设）",
            ReportConfigurationConstants.DatasetShipmentFinance,
            ShipmentFinancePresetDefinition()),
        new ReportConfigurationPresetSeed(
            "follow-up-due",
            "report:follow-up-due",
            "跟进提醒（迁移预设）",
            ReportConfigurationConstants.DatasetFollowUpDue,
            FollowUpDuePresetDefinition()),
        new ReportConfigurationPresetSeed(
            "follow-up-due-dynamic",
            "dynamic:follow-up-due",
            "动态跟进提醒报表（迁移预设）",
            ReportConfigurationConstants.DatasetFollowUpDue,
            FollowUpDuePresetDefinition()),
        new ReportConfigurationPresetSeed(
            "quotation-conversion",
            "report:quotation-conversion",
            "报价成交率分析（迁移预设）",
            ReportConfigurationConstants.DatasetQuotationConversion,
            QuotationConversionPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "quotation-conversion-dynamic",
            "dynamic:quotation-conversion",
            "动态报价成交率报表（迁移预设）",
            ReportConfigurationConstants.DatasetQuotationConversion,
            QuotationConversionPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "salesman-output",
            "report:salesman-output",
            "业务员产值报表（迁移预设）",
            ReportConfigurationConstants.DatasetSalesmanOutput,
            SalesmanOutputPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "salesman-output-dynamic",
            "dynamic:salesman-output",
            "动态业务员产值证据报表（迁移预设）",
            ReportConfigurationConstants.DatasetSalesmanOutput,
            SalesmanOutputPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "agency-service-fee-monthly",
            "dynamic:agency-service-fee-monthly",
            "动态代理服务费月度汇总报表（迁移预设）",
            ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly,
            AgencyServiceFeeMonthlyPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "receipt-reconciliation",
            "dynamic:receipt-reconciliation",
            "动态客户订单与收款核对报表（迁移预设）",
            ReportConfigurationConstants.DatasetReceiptReconciliation,
            ReceiptReconciliationPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "purchase-order",
            "dynamic:purchase-order",
            "采购订单（迁移预设）",
            ReportConfigurationConstants.DatasetPurchaseOrder,
            PurchaseOrderPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "supplier-aging",
            "dynamic:supplier-aging",
            "供应商对账与账龄（迁移预设）",
            ReportConfigurationConstants.DatasetSupplierAging,
            SupplierAgingPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "supplier-exposure",
            "dynamic:supplier-exposure",
            "供应商采购敞口（迁移预设）",
            ReportConfigurationConstants.DatasetSupplierExposure,
            SupplierExposurePresetDefinition()),
        new ReportConfigurationPresetSeed(
            "balance-sheet",
            "report:balance-sheet",
            "资产负债表（迁移预设）",
            ReportConfigurationConstants.DatasetBalanceSheet,
            BalanceSheetPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "income-statement",
            "report:income-statement",
            "利润表（迁移预设）",
            ReportConfigurationConstants.DatasetIncomeStatement,
            IncomeStatementPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "cash-flow",
            "report:cash-flow",
            "现金流量表（迁移预设）",
            ReportConfigurationConstants.DatasetCashFlow,
            CashFlowPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "ar-aging",
            "report:ar-aging",
            "应收账龄分析表（迁移预设）",
            ReportConfigurationConstants.DatasetArAging,
            ArAgingPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "purchase-cost",
            "report:purchase-cost",
            "采购成本分析表（迁移预设）",
            ReportConfigurationConstants.DatasetPurchaseCost,
            PurchaseCostPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "tax-refund-summary",
            "report:tax-refund-summary",
            "退税汇总表（迁移预设）",
            ReportConfigurationConstants.DatasetTaxRefundSummary,
            TaxRefundSummaryPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "product-export-field-completeness",
            "export:product-export-field-completeness",
            "出口字段完整度工作台（迁移预设）",
            ReportConfigurationConstants.DatasetProductExportFieldCompleteness,
            ProductExportFieldCompletenessPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "trade-document-print",
            "document:trade-document-print",
            "出口单证打印数据（迁移预设）",
            ReportConfigurationConstants.DatasetTradeDocument,
            TradeDocumentPrintPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "trade-document-export-excel",
            "document:trade-document-export-excel",
            "出口单证台账导出（迁移预设）",
            ReportConfigurationConstants.DatasetTradeDocument,
            TradeDocumentExportExcelPresetDefinition()),

        // ERP-308：16 个 BillProc 导出族，每族一个预设（复用受控族目录的字段顺序）。
        new ReportConfigurationPresetSeed("bill-export:sales-order", "export:bill-proc:sales-order",
            "销售订单导出（迁移预设）", LegacyBillExportCatalog.Resolve("sales-order").DatasetKey, BillExportPresetDefinition("sales-order")),
        new ReportConfigurationPresetSeed("bill-export:purchase-order", "export:bill-proc:purchase-order",
            "采购订单导出（迁移预设）", LegacyBillExportCatalog.Resolve("purchase-order").DatasetKey, BillExportPresetDefinition("purchase-order")),
        new ReportConfigurationPresetSeed("bill-export:inquiry", "export:bill-proc:inquiry",
            "询价单导出（迁移预设）", LegacyBillExportCatalog.Resolve("inquiry").DatasetKey, BillExportPresetDefinition("inquiry")),
        new ReportConfigurationPresetSeed("bill-export:stock-in", "export:bill-proc:stock-in",
            "采购入库导出（迁移预设）", LegacyBillExportCatalog.Resolve("stock-in").DatasetKey, BillExportPresetDefinition("stock-in")),
        new ReportConfigurationPresetSeed("bill-export:stock-out", "export:bill-proc:stock-out",
            "销售出库导出（迁移预设）", LegacyBillExportCatalog.Resolve("stock-out").DatasetKey, BillExportPresetDefinition("stock-out")),
        new ReportConfigurationPresetSeed("bill-export:receipt", "export:bill-proc:receipt",
            "收款单导出（迁移预设）", LegacyBillExportCatalog.Resolve("receipt").DatasetKey, BillExportPresetDefinition("receipt")),
        new ReportConfigurationPresetSeed("bill-export:payment", "export:bill-proc:payment",
            "付款单导出（迁移预设）", LegacyBillExportCatalog.Resolve("payment").DatasetKey, BillExportPresetDefinition("payment")),
        new ReportConfigurationPresetSeed("bill-export:deposit-apply", "export:bill-proc:deposit-apply",
            "定金申请单导出（迁移预设）", LegacyBillExportCatalog.Resolve("deposit-apply").DatasetKey, BillExportPresetDefinition("deposit-apply")),
        new ReportConfigurationPresetSeed("bill-export:payment-apply", "export:bill-proc:payment-apply",
            "货款申请单导出（迁移预设）", LegacyBillExportCatalog.Resolve("payment-apply").DatasetKey, BillExportPresetDefinition("payment-apply")),
        new ReportConfigurationPresetSeed("bill-export:container-settlement", "export:bill-proc:container-settlement",
            "装柜结算单导出（迁移预设）", LegacyBillExportCatalog.Resolve("container-settlement").DatasetKey, BillExportPresetDefinition("container-settlement")),
        new ReportConfigurationPresetSeed("bill-export:bulk-settlement", "export:bill-proc:bulk-settlement",
            "散货结算单导出（迁移预设）", LegacyBillExportCatalog.Resolve("bulk-settlement").DatasetKey, BillExportPresetDefinition("bulk-settlement")),
        new ReportConfigurationPresetSeed("bill-export:complaint", "export:bill-proc:complaint",
            "客诉单导出（迁移预设）", LegacyBillExportCatalog.Resolve("complaint").DatasetKey, BillExportPresetDefinition("complaint")),
        new ReportConfigurationPresetSeed("bill-export:receiving-plan", "export:bill-proc:receiving-plan",
            "收货计划导出（迁移预设）", LegacyBillExportCatalog.Resolve("receiving-plan").DatasetKey, BillExportPresetDefinition("receiving-plan")),
        new ReportConfigurationPresetSeed("bill-export:booking", "export:bill-proc:booking",
            "订柜信息导出（迁移预设）", LegacyBillExportCatalog.Resolve("booking").DatasetKey, BillExportPresetDefinition("booking")),
        new ReportConfigurationPresetSeed("bill-export:pre-loading", "export:bill-proc:pre-loading",
            "预装柜单导出（迁移预设）", LegacyBillExportCatalog.Resolve("pre-loading").DatasetKey, BillExportPresetDefinition("pre-loading")),
        new ReportConfigurationPresetSeed("bill-export:loading-list", "export:bill-proc:loading-list",
            "装柜清单导出（迁移预设）", LegacyBillExportCatalog.Resolve("loading-list").DatasetKey, BillExportPresetDefinition("loading-list")),

        // ERP-327：16 个旧单据导出族的打印模板预设（复用受控族目录的字段顺序与 BillExportPresetDefinition），
        // 键到对应的 print-template:{familyKey} 登记册条目，使数据集 + 菜单均就绪时到达 preset-ready。
        new ReportConfigurationPresetSeed("print-template:sales-order", "print-template:sales-order",
            "销售订单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("sales-order").DatasetKey, BillExportPresetDefinition("sales-order")),
        new ReportConfigurationPresetSeed("print-template:purchase-order", "print-template:purchase-order",
            "采购订单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("purchase-order").DatasetKey, BillExportPresetDefinition("purchase-order")),
        new ReportConfigurationPresetSeed("print-template:inquiry", "print-template:inquiry",
            "询价单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("inquiry").DatasetKey, BillExportPresetDefinition("inquiry")),
        new ReportConfigurationPresetSeed("print-template:stock-in", "print-template:stock-in",
            "采购入库打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("stock-in").DatasetKey, BillExportPresetDefinition("stock-in")),
        new ReportConfigurationPresetSeed("print-template:stock-out", "print-template:stock-out",
            "销售出库打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("stock-out").DatasetKey, BillExportPresetDefinition("stock-out")),
        new ReportConfigurationPresetSeed("print-template:receipt", "print-template:receipt",
            "收款单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("receipt").DatasetKey, BillExportPresetDefinition("receipt")),
        new ReportConfigurationPresetSeed("print-template:payment", "print-template:payment",
            "付款单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("payment").DatasetKey, BillExportPresetDefinition("payment")),
        new ReportConfigurationPresetSeed("print-template:deposit-apply", "print-template:deposit-apply",
            "定金申请单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("deposit-apply").DatasetKey, BillExportPresetDefinition("deposit-apply")),
        new ReportConfigurationPresetSeed("print-template:payment-apply", "print-template:payment-apply",
            "货款申请单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("payment-apply").DatasetKey, BillExportPresetDefinition("payment-apply")),
        new ReportConfigurationPresetSeed("print-template:container-settlement", "print-template:container-settlement",
            "装柜结算单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("container-settlement").DatasetKey, BillExportPresetDefinition("container-settlement")),
        new ReportConfigurationPresetSeed("print-template:bulk-settlement", "print-template:bulk-settlement",
            "散货结算单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("bulk-settlement").DatasetKey, BillExportPresetDefinition("bulk-settlement")),
        new ReportConfigurationPresetSeed("print-template:complaint", "print-template:complaint",
            "客诉单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("complaint").DatasetKey, BillExportPresetDefinition("complaint")),
        new ReportConfigurationPresetSeed("print-template:receiving-plan", "print-template:receiving-plan",
            "收货计划打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("receiving-plan").DatasetKey, BillExportPresetDefinition("receiving-plan")),
        new ReportConfigurationPresetSeed("print-template:booking", "print-template:booking",
            "订柜信息打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("booking").DatasetKey, BillExportPresetDefinition("booking")),
        new ReportConfigurationPresetSeed("print-template:pre-loading", "print-template:pre-loading",
            "预装柜单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("pre-loading").DatasetKey, BillExportPresetDefinition("pre-loading")),
        new ReportConfigurationPresetSeed("print-template:loading-list", "print-template:loading-list",
            "装柜清单打印模板（迁移预设）", LegacyBillExportCatalog.Resolve("loading-list").DatasetKey, BillExportPresetDefinition("loading-list")),
        new ReportConfigurationPresetSeed("master:customer", "print-template:customer",
            "客户资料（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey, MasterDataPresetDefinition("customer")),
        new ReportConfigurationPresetSeed("master:supplier", "print-template:supplier",
            "供应商资料（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("supplier").DatasetKey, MasterDataPresetDefinition("supplier")),
        new ReportConfigurationPresetSeed("master:employee", "print-template:employee",
            "员工资料（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("employee").DatasetKey, MasterDataPresetDefinition("employee")),
        new ReportConfigurationPresetSeed("master:expense-account", "print-template:expense-account",
            "费用科目（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("expense-account").DatasetKey, MasterDataPresetDefinition("expense-account")),
        new ReportConfigurationPresetSeed("master:warehouse", "print-template:warehouse",
            "仓库资料（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("warehouse").DatasetKey, MasterDataPresetDefinition("warehouse")),
        new ReportConfigurationPresetSeed("master:product", "print-template:product",
            "商品资料（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("product").DatasetKey, MasterDataPresetDefinition("product")),
        new ReportConfigurationPresetSeed("master:other-info", "print-template:other-info",
            "其他资料（迁移预设）", ReportConfigurationMasterDataCatalog.Resolve("other-info").DatasetKey, MasterDataPresetDefinition("other-info")),

        // ERP-318：销售单据打印族（报价单 / 形式发票 PI），复用既有销售单据菜单授权与客户业务员数据范围。
        new ReportConfigurationPresetSeed(
            "sales-document:quotation",
            "print-template:quotation",
            "报价单（迁移预设）",
            ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey,
            SalesDocumentPresetDefinition("quotation"),
            new ReportConfigurationPresetParameterSeed[]
            {
                new(ReportConfigurationPresetConstants.ParameterCustomer,
                    ReportConfigurationPresetConstants.ParameterTypeNumber, "客户", Required: false),
                new(ReportConfigurationPresetConstants.ParameterDate,
                    ReportConfigurationPresetConstants.ParameterTypeDate, "日期区间", Required: false),
                new(ReportConfigurationPresetConstants.ParameterStatus,
                    ReportConfigurationPresetConstants.ParameterTypeText, "状态", Required: false),
            },
            new Dictionary<string, ReportConfigurationPresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
            {
                [ReportConfigurationPresetConstants.ParameterCustomer] = new("customerId", ReportConfigurationConstants.OperatorEq),
                [ReportConfigurationPresetConstants.ParameterDate] = new("docDate", ReportConfigurationConstants.OperatorBetween),
                [ReportConfigurationPresetConstants.ParameterStatus] = new("status", ReportConfigurationConstants.OperatorEq),
            }),
        new ReportConfigurationPresetSeed(
            "sales-document:proforma-invoice",
            "print-template:proforma-invoice",
            "形式发票 PI（迁移预设）",
            ReportConfigurationSalesDocumentCatalog.Resolve("proforma-invoice").DatasetKey,
            SalesDocumentPresetDefinition("proforma-invoice"),
            new ReportConfigurationPresetParameterSeed[]
            {
                new(ReportConfigurationPresetConstants.ParameterCustomer,
                    ReportConfigurationPresetConstants.ParameterTypeNumber, "客户", Required: false),
                new(ReportConfigurationPresetConstants.ParameterDate,
                    ReportConfigurationPresetConstants.ParameterTypeDate, "日期区间", Required: false),
                new(ReportConfigurationPresetConstants.ParameterStatus,
                    ReportConfigurationPresetConstants.ParameterTypeText, "状态", Required: false),
            },
            new Dictionary<string, ReportConfigurationPresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase)
            {
                [ReportConfigurationPresetConstants.ParameterCustomer] = new("customerId", ReportConfigurationConstants.OperatorEq),
                [ReportConfigurationPresetConstants.ParameterDate] = new("docDate", ReportConfigurationConstants.OperatorBetween),
                [ReportConfigurationPresetConstants.ParameterStatus] = new("status", ReportConfigurationConstants.OperatorEq),
            }),
        new ReportConfigurationPresetSeed(
            "print-template:doc-center",
            "print-template:doc-center",
            "出口单证打印模板（迁移预设）",
            ReportConfigurationConstants.DatasetTradeDocument,
            TradeDocumentPrintPresetDefinition()),
    };

    private static ReportConfigurationDefinition SalesOrderPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
        Fields = new List<string> { "orderNo", "orderDate", "customerId", "currency", "totalAmount", "status" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ReceivablePresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetReceivable,
        Fields = new List<string> { "invoiceNumber", "invoiceDate", "customerName", "currency", "grossAmount", "remainingAmount", "statusText" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition InventoryMovementPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetInventoryMovement,
        Fields = new List<string>
        {
            "warehouseName", "productCode", "productName", "spec", "unit",
            "currentQuantity", "inboundQuantity", "outboundQuantity", "netQuantity",
            "lastMovementDate", "inactivityDays", "classification",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition InventoryAgingPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetInventoryAging,
        Fields = new List<string>
        {
            "warehouseName", "productCode", "productName", "spec", "unit",
            "currentQuantity", "knownAgedQuantity", "unknownAgeQuantity",
            "authoritativeAmount", "agedAmount", "unknownAgeAmount", "evidenceStatus", "costStatus",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition StockAlertPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetStockAlert,
        Fields = new List<string>
        {
            "productName", "spec", "unit", "warehouseName",
            "quantity", "minStock", "maxStock", "diff", "alertLevel",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ProductSalesRankingPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetProductSalesRanking,
        Fields = new List<string>
        {
            "rank", "productCode", "productName", "spec", "unit", "totalQuantity",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition OrderProfitPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetOrderProfit,
        Fields = new List<string>
        {
            "orderNo", "orderDate", "customerName", "currency", "salesAmount",
            "costAmount", "profit", "profitRate", "currentPriceEstimate",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition SalesCommissionPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSalesCommission,
        Fields = new List<string>
        {
            "salesmanName", "currency", "orderCount", "salesAmount",
            "commissionRate", "commissionAmount", "sourceLabel",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ContainerStatsPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetContainerStats,
        Fields = new List<string>
        {
            "loadingDate", "containerNo", "loadingListCount", "authorizedCustomerCount",
            "totalCartons", "totalWeight", "totalVolume", "utilizationType", "reasons",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition CustomerShipmentPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetCustomerShipment,
        Fields = new List<string>
        {
            "customerId", "customerName", "currency", "currencyLabel", "orderCount",
            "totalAmount", "currencyEvidence", "amountLabel", "unitGroups", "totalQuantity",
            "quantityCompletenessReason", "quantityLabel",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ShipmentFinancePresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetShipmentFinance,
        Fields = new List<string>
        {
            "orderId", "orderNo", "orderDate", "status", "customerId", "customerName", "currency",
            "orderAmount", "recordedDepositAmount", "orderedQuantity", "shippedQuantity",
            "pendingShipmentQuantity", "outstandingQuantity", "shipmentStatus", "hasApprovedShipment",
            "shipmentDocumentCount", "approvedShipmentCount", "financeLinkStatus", "financeLinkReason",
            "linkedAmount", "uncoveredAmount", "submittedAmount", "otherCurrencyRecordCount",
            "unapprovedRecordCount", "unattributedRecordCount", "overReceived", "note",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition AgencyServiceFeeMonthlyPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly,
        Fields = new List<string>
        {
            "statementYear", "statementMonthText", "customerId", "customerCode", "customerName",
            "currency", "registeredCount", "registeredTotalAmount", "draftCount", "draftTotalAmount",
            "voidedCount", "voidedTotalAmount", "statementCount",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ReceiptReconciliationPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetReceiptReconciliation,
        Fields = new List<string>
        {
            "orderId", "orderNo", "orderDate", "status", "customerId", "customerName", "currency",
            "orderAmount", "recordedDepositAmount", "orderedQuantity", "shippedQuantity",
            "pendingShipmentQuantity", "outstandingQuantity", "shipmentStatus", "hasApprovedShipment",
            "receiptCoverageStatus", "linkedReceiptAmount", "pendingReceiptAmount", "uncoveredAmount",
            "receiptAllocationStatus", "recordedReceiptAllocationAmount", "invoiceEvidenceStatus",
            "recordedInvoicedAmount", "overReceived", "note",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition FollowUpDuePresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetFollowUpDue,
        Fields = new List<string>
        {
            "id", "followNo", "followDate", "customerId", "customerName", "followType", "contactPerson",
            "salesmanId", "salesmanName", "subject", "content", "result", "nextFollowDate", "dueDays", "dueStatus", "remark",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition QuotationConversionPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetQuotationConversion,
        Fields = new List<string>
        {
            "salesmanName", "currency", "quotationCount", "convertedCount", "conversionRate",
            "expiredCount", "cancelledCount", "totalAmount", "convertedAmount", "avgConvertedAmount",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition SalesmanOutputPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSalesmanOutput,
        Fields = new List<string>
        {
            "salesmanId", "salesmanName", "currency", "currencyLabel", "orderCount", "totalAmount",
            "totalProfit", "amountLabel", "currencyEvidence", "profitEvidence", "salesmanIdentityEvidence", "sourceEvidence",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition PurchaseOrderPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetPurchaseOrder,
        Fields = new List<string>
        {
            "orderNo", "orderDate", "supplierId", "currency", "totalAmount", "contractNo", "status", "remark",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition SupplierAgingPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSupplierAging,
        Fields = new List<string>
        {
            "invoiceNumber", "invoiceDate", "supplierName", "currency", "grossAmount",
            "dueDate", "agingBucket", "overdueDays", "remainingAmount", "note",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition SupplierExposurePresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSupplierExposure,
        Fields = new List<string>
        {
            "orderNo", "orderDate", "supplierName", "currency", "orderedAmount",
            "linkStatus", "settledAmount", "outstandingAmount", "note",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition BalanceSheetPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetBalanceSheet,
        Fields = new List<string> { "lineName", "amount" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition IncomeStatementPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetIncomeStatement,
        Fields = new List<string> { "lineName", "amount" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition CashFlowPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetCashFlow,
        Fields = new List<string> { "lineName", "amount" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ArAgingPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetArAging,
        Fields = new List<string>
        {
            "customerName", "orderNo", "currency", "orderAmount",
            "receivedAmount", "balance", "agingDays", "bucket", "status",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition PurchaseCostPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetPurchaseCost,
        Fields = new List<string>
        {
            "supplierName", "supplierType", "orderCount", "totalAmount", "avgAmount", "lastOrderDate",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition TaxRefundSummaryPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetTaxRefundSummary,
        Fields = new List<string>
        {
            "refundPeriod", "recordCount", "declaredCount", "refundedCount",
            "exportAmount", "refundableAmount", "refundedAmount", "unrefundedAmount",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ProductExportFieldCompletenessPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetProductExportFieldCompleteness,
        Fields = new List<string>
        {
            "productId", "productCode", "productName", "spec", "unit",
            "completeness", "gapCount", "fieldCount",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition TradeDocumentPrintPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetTradeDocument,
        Fields = new List<string>
        {
            "docNo", "docType", "status", "issueDate", "customerName", "salesOrderNo", "refNo", "declareNo",
            "amount", "currency", "departurePort", "destinationPort", "issuedBy", "copies", "fileNote", "remark",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition TradeDocumentExportExcelPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetTradeDocument,
        Fields = new List<string>
        {
            "docNo", "docType", "issueDate", "salesOrderNo", "refNo", "declareNo", "customerName",
            "amount", "currency", "departurePort", "destinationPort", "issuedBy", "copies", "status", "fileNote", "remark",
        },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition BillExportPresetDefinition(string familyKey) => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = LegacyBillExportCatalog.Resolve(familyKey).DatasetKey,
        Fields = LegacyBillExportCatalog.Resolve(familyKey).Columns.Select(c => c.Key).ToList(),
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition MasterDataPresetDefinition(string familyKey) => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationMasterDataCatalog.Resolve(familyKey).DatasetKey,
        Fields = ReportConfigurationMasterDataCatalog.Resolve(familyKey).Columns.Select(c => c.Key).ToList(),
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition SalesDocumentPresetDefinition(string familyKey) => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationSalesDocumentCatalog.Resolve(familyKey).DatasetKey,
        Fields = ReportConfigurationSalesDocumentCatalog.Resolve(familyKey).Columns
            .Where(c => c.Grain != ReportSalesDocumentGrain.Detail)
            .Select(c => c.Key)
            .ToList(),
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };
}

/// <summary>
/// 迁移登记册预设接缝（ERP-296 Stage 2）：只回答「某旧报表是否已有预设模板」，供 <see cref="ReportMigrationRegistry"/>
/// 派生 parity 使用。无状态、只读，绝不依赖预设编排或物化结果（避免循环依赖）。
/// </summary>
public sealed class ReportMigrationPresetCatalog : IReportMigrationPresetCatalog
{
    /// <inheritdoc />
    public Task<bool> HasPresetAsync(string legacyKey, long? userId, CancellationToken cancellationToken = default)
        => Task.FromResult(
            ReportConfigurationPresetManifest.Presets.Any(p =>
                string.Equals(p.LegacyKey, legacyKey, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// 报表预设模板编排（ERP-296 Stage 2）实现：只读列出 + 私有物化。
/// <list type="number">
/// <item><b>只读列出</b>：按迁移登记册逐条重检原始菜单授权与派生 parity，并按当前账号重新校验数据集授权，
/// 未授权 / 未 ready（pending）的预设被隐藏，绝不通过模板授予权限。</item>
/// <item><b>物化</b>：先确认预设 ready（至少 dataset-ready），再对当前授权数据集重新校验定义（绝不信任预设载荷），
/// 最后经既有 <see cref="ReportConfigurationService.CreateAsync"/> 落为当前用户私有草稿。</item>
/// </list>
/// </summary>
public sealed class ReportConfigurationPresetCatalog : IReportConfigurationPresetCatalog
{
    private readonly IReportConfigurationCatalog _catalog;
    private readonly IReportMigrationRegistry _registry;
    private readonly IReportConfigurationService _service;

    public ReportConfigurationPresetCatalog(
        IReportConfigurationCatalog catalog,
        IReportMigrationRegistry registry,
        IReportConfigurationService service)
    {
        _catalog = catalog;
        _registry = registry;
        _service = service;
    }

    /// <inheritdoc />
    public async Task<List<ReportConfigurationPresetDto>> ListPresetsAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);

        var registry = await _registry.GetRegistryAsync(userId, cancellationToken);
        var byLegacy = registry.Entries.ToDictionary(e => e.LegacyKey, StringComparer.OrdinalIgnoreCase);

        var result = new List<ReportConfigurationPresetDto>();
        foreach (var preset in ReportConfigurationPresetManifest.Presets)
        {
            var dto = await TryBuildAsync(preset, byLegacy, userId!.Value, cancellationToken);
            if (dto is not null)
                result.Add(dto);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationPresetDto?> GetPresetAsync(
        string presetKey, long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        if (string.IsNullOrWhiteSpace(presetKey))
            return null;

        var preset = ReportConfigurationPresetManifest.Presets.FirstOrDefault(p =>
            string.Equals(p.PresetKey, presetKey.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return null;

        var registry = await _registry.GetRegistryAsync(userId, cancellationToken);
        var byLegacy = registry.Entries.ToDictionary(e => e.LegacyKey, StringComparer.OrdinalIgnoreCase);

        return await TryBuildAsync(preset, byLegacy, userId!.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDto> MaterializeAsync(
        string presetKey, long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);

        var preset = await RequireReadyPresetAsync(presetKey, userId!.Value, cancellationToken);
        return await MaterializeCoreAsync(preset, new ReportConfigurationPresetMaterializeRequest(), userId.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDto> MaterializeAsync(
        string presetKey,
        ReportConfigurationPresetMaterializeRequest request,
        long? userId,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        ArgumentNullException.ThrowIfNull(request);

        var preset = await RequireReadyPresetAsync(presetKey, userId!.Value, cancellationToken);
        ValidateDeclaredParameters(preset, request);

        return await MaterializeCoreAsync(preset, request, userId.Value, cancellationToken);
    }

    /// <summary>
    /// 物化核心：绝不信任预设载荷，先对当前授权数据集重新校验定义与参数绑定（fail closed），
    /// 再经既有 <see cref="IReportConfigurationService.CreateAsync"/> 落为当前用户私有草稿。
    /// <para>模板定义不可变：这里总是新建一份定义并替换 <c>Filters</c>，绝不改写共享预设清单。</para>
    /// </summary>
    private async Task<ReportConfigurationDto> MaterializeCoreAsync(
        ReportConfigurationPresetSeed preset,
        ReportConfigurationPresetMaterializeRequest request,
        long userId,
        CancellationToken cancellationToken)
    {
        var dataset = await _catalog.GetDatasetAsync(preset.DatasetKey, userId, cancellationToken)
            ?? throw BusinessException.InvalidParameter($"数据集 {preset.DatasetKey} 不存在或未授权");

        foreach (var (parameterKey, binding) in preset.ParameterBindings ?? new Dictionary<string, ReportConfigurationPresetParameterBindingSeed>())
            ValidateBinding(parameterKey, binding, dataset);

        var definition = BuildDefinition(preset, BuildFilters(preset, request));
        ReportConfigurationRules.Validate(definition, dataset);

        var saveDto = new ReportConfigurationSaveDto
        {
            Name = preset.Name,
            Definition = definition,
        };

        return await _service.CreateAsync(userId, saveDto, cancellationToken);
    }

    private static void ValidateDeclaredParameters(
        ReportConfigurationPresetSeed preset,
        ReportConfigurationPresetMaterializeRequest request)
    {
        var declared = (preset.Parameters ?? Array.Empty<ReportConfigurationPresetParameterSeed>())
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (request.CustomerId.HasValue
            && !declared.Contains(ReportConfigurationPresetConstants.ParameterCustomer))
            throw BusinessException.InvalidParameter("该预设不支持客户参数");

        if ((request.StartDate.HasValue || request.EndDate.HasValue)
            && !declared.Contains(ReportConfigurationPresetConstants.ParameterDate))
            throw BusinessException.InvalidParameter("该预设不支持日期参数");

        if (!string.IsNullOrWhiteSpace(request.Status)
            && !declared.Contains(ReportConfigurationPresetConstants.ParameterStatus))
            throw BusinessException.InvalidParameter("该预设不支持状态参数");

        if (request.CustomerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 必须为正整数");

        if (request.StartDate.HasValue && request.EndDate.HasValue
            && request.StartDate.Value.Date > request.EndDate.Value.Date)
            throw BusinessException.InvalidParameter("开始日期不能晚于结束日期");

        if (request.Status is { Length: > ReportConfigurationPresetConstants.MaxStatusLength })
            throw BusinessException.InvalidParameter(
                $"状态参数最长 {ReportConfigurationPresetConstants.MaxStatusLength} 个字符");

        foreach (var parameter in (preset.Parameters ?? Array.Empty<ReportConfigurationPresetParameterSeed>()).Where(p => p.Required))
        {
            if (string.Equals(parameter.Key, ReportConfigurationPresetConstants.ParameterCustomer, StringComparison.OrdinalIgnoreCase)
                && request.CustomerId is null)
                throw BusinessException.InvalidParameter($"参数 {parameter.Label} 为必填");
        }
    }

    private static void ValidateBinding(
        string parameterKey,
        ReportConfigurationPresetParameterBindingSeed binding,
        ReportConfigurationDatasetDto dataset)
    {
        var field = dataset.Fields.FirstOrDefault(f =>
            string.Equals(f.Key, binding.FieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null)
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 不在数据集 {dataset.DatasetKey} 的字段目录中");
        if (field.Hidden)
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 已隐藏");
        if (!field.Filterable)
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 不可筛选");
        if (field.FilterOperators is null
            || !field.FilterOperators.Contains(binding.Operator, StringComparer.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 绑定的字段 {binding.FieldKey} 不支持操作符 {binding.Operator}");
        if (!TypeMatches(parameterKey, field.Type))
            throw BusinessException.InvalidParameter(
                $"预设参数 {parameterKey} 的类型与字段 {binding.FieldKey} 类型（{field.Type}）不匹配");
    }

    private static bool TypeMatches(string parameterKey, string fieldType)
    {
        return parameterKey switch
        {
            ReportConfigurationPresetConstants.ParameterCustomer =>
                string.Equals(fieldType, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase),
            ReportConfigurationPresetConstants.ParameterDate =>
                string.Equals(fieldType, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase),
            ReportConfigurationPresetConstants.ParameterStatus =>
                string.Equals(fieldType, ReportConfigurationConstants.TypeText, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fieldType, ReportConfigurationConstants.TypeEnum, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }


    private static ReportConfigurationDefinition BuildDefinition(
        ReportConfigurationPresetSeed preset,
        IReadOnlyList<ReportConfigurationFilter> filters)
    {
        var template = preset.Definition;
        return new ReportConfigurationDefinition
        {
            SchemaVersion = template.SchemaVersion,
            DatasetKey = template.DatasetKey,
            Fields = template.Fields.ToList(),
            Filters = filters.ToList(),
            Grouping = template.Grouping.ToList(),
            Aggregates = template.Aggregates.ToList(),
            ComputedColumns = template.ComputedColumns.ToList(),
            Capabilities = template.Capabilities.ToList(),
            Presentation = template.Presentation,
            Relations = template.Relations.ToList(),
            Pivot = template.Pivot,
            Coverage = template.Coverage,
        };
    }

    private static List<ReportConfigurationFilter> BuildFilters(
        ReportConfigurationPresetSeed preset,
        ReportConfigurationPresetMaterializeRequest request)
    {
        var bindings = preset.ParameterBindings ?? new Dictionary<string, ReportConfigurationPresetParameterBindingSeed>(StringComparer.OrdinalIgnoreCase);
        var filters = new List<ReportConfigurationFilter>();

        if (bindings.TryGetValue(ReportConfigurationPresetConstants.ParameterCustomer, out var customerBinding)
            && request.CustomerId.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = customerBinding.FieldKey,
                Operator = customerBinding.Operator,
                Value = request.CustomerId.Value,
            });
        }

        if (bindings.TryGetValue(ReportConfigurationPresetConstants.ParameterDate, out var dateBinding))
            AddDateFilters(filters, dateBinding, request.StartDate, request.EndDate);

        if (bindings.TryGetValue(ReportConfigurationPresetConstants.ParameterStatus, out var statusBinding)
            && !string.IsNullOrWhiteSpace(request.Status))
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = statusBinding.FieldKey,
                Operator = statusBinding.Operator,
                Value = request.Status.Trim(),
            });
        }

        return filters;
    }

    private static void AddDateFilters(
        List<ReportConfigurationFilter> filters,
        ReportConfigurationPresetParameterBindingSeed binding,
        DateTime? start,
        DateTime? end)
    {
        if (start.HasValue && end.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = binding.FieldKey,
                Operator = ReportConfigurationConstants.OperatorBetween,
                Value = start.Value,
                Value2 = end.Value,
            });
        }
        else if (start.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = binding.FieldKey,
                Operator = ReportConfigurationConstants.OperatorGte,
                Value = start.Value,
            });
        }
        else if (end.HasValue)
        {
            filters.Add(new ReportConfigurationFilter
            {
                FieldKey = binding.FieldKey,
                Operator = ReportConfigurationConstants.OperatorLte,
                Value = end.Value,
            });
        }
    }


    private async Task<ReportConfigurationPresetDto?> TryBuildAsync(
        ReportConfigurationPresetSeed preset,
        IReadOnlyDictionary<string, ReportMigrationRegistryEntryDto> byLegacy,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!byLegacy.TryGetValue(preset.LegacyKey, out var entry))
            return null; // 原始菜单未授权，或非完整迁移登记册条目

        if (string.Equals(entry.ParityStatus, ReportMigrationParityStatusText.Pending, StringComparison.Ordinal))
            return null; // 未 ready：受控数据集适配器缺失 / 未授权

        var dataset = await _catalog.GetDatasetAsync(preset.DatasetKey, userId, cancellationToken);
        if (dataset is null)
            return null; // 数据集授权已失效（fail closed 双重校验）

        return new ReportConfigurationPresetDto
        {
            PresetKey = preset.PresetKey,
            LegacyKey = preset.LegacyKey,
            Name = preset.Name,
            DatasetKey = preset.DatasetKey,
            DatasetLabel = dataset.Label,
            ParityStatus = entry.ParityStatus,
            Parameters = (preset.Parameters ?? Array.Empty<ReportConfigurationPresetParameterSeed>())
                .Select(p => new ReportConfigurationPresetParameterDto
                {
                    Key = p.Key,
                    Type = p.Type,
                    Label = p.Label,
                    Required = p.Required,
                })
                .ToList(),
        };
    }

    private async Task<ReportConfigurationPresetSeed> RequireReadyPresetAsync(
        string presetKey, long userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presetKey))
            throw BusinessException.NotFound("预设模板不存在");

        var preset = ReportConfigurationPresetManifest.Presets.FirstOrDefault(p =>
            string.Equals(p.PresetKey, presetKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.NotFound("预设模板不存在");

        var registry = await _registry.GetRegistryAsync(userId, cancellationToken);
        var entry = registry.Entries.FirstOrDefault(e =>
            string.Equals(e.LegacyKey, preset.LegacyKey, StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.NotFound("预设模板不存在或无权访问");

        if (string.Equals(entry.ParityStatus, ReportMigrationParityStatusText.Pending, StringComparison.Ordinal))
            throw BusinessException.RuleConflict("预设模板尚未就绪，暂不能物化");

        return preset;
    }

    private static void EnsureAuthenticated(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问报表预设模板", ErrorCodes.Unauthorized);
    }
}

