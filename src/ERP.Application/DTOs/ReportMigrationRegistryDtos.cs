using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>
/// 报表迁移登记册（ERP-295 Stage 2）的 parity 状态：派生自注册的数据集适配器目录、预设目录与逐条声明的兼容性清单。
/// <para>取值语义单调递进：pending（无受控数据集）→ dataset-ready（受控数据集适配器就绪）→
/// preset-ready（受控数据集 + 预设模板均就绪，但旧语义尚未完全对齐）→ parity-passed（适配器 + 预设 + 兼容性声明全部就绪且与旧语义匹配）。</para>
/// </summary>
public enum ReportMigrationParityStatus
{
    /// <summary>未迁移：尚无受控数据集适配器。</summary>
    Pending = 0,

    /// <summary>受控数据集适配器已注册（且当前账号已获其菜单授权）。</summary>
    DatasetReady = 1,

    /// <summary>受控数据集适配器 + 预设模板均已就绪，但旧语义（币种 / 单位 / 权限 / 兼容性）尚未完全匹配。</summary>
    PresetReady = 2,

    /// <summary>适配器 + 预设 + Excel/PDF 兼容性声明全部就绪，且与旧语义（币种 / 单位 / 权限 / 兼容性）匹配。</summary>
    ParityPassed = 3,
}

/// <summary>parity 状态的对外线上取值（与文档 / 界面同源；与枚举一一对应）。</summary>
public static class ReportMigrationParityStatusText
{
    public const string Pending = "pending";
    public const string DatasetReady = "dataset-ready";
    public const string PresetReady = "preset-ready";
    public const string ParityPassed = "parity-passed";

    /// <summary>把枚举映射为稳定的对外线上取值。</summary>
    public static string Of(ReportMigrationParityStatus status) => status switch
    {
        ReportMigrationParityStatus.DatasetReady => DatasetReady,
        ReportMigrationParityStatus.PresetReady => PresetReady,
        ReportMigrationParityStatus.ParityPassed => ParityPassed,
        _ => Pending,
    };
}

/// <summary>报表迁移登记册的旧报表分类（有限、稳定；与旧路由族一一对应）。</summary>
public static class ReportMigrationRegistryCategories
{
    /// <summary>固定报表（ReportController 的固定 GET 端点）。</summary>
    public const string FixedReport = "fixed-report";

    /// <summary>18 个动态报表（Dynamic*ReportController 各自承载的专用路由）。</summary>
    public const string DynamicReport = "dynamic-report";

    /// <summary>导出（BillProcController.Export、ProductExportFieldCompleteness）。</summary>
    public const string Export = "export";

    /// <summary>报告包（CustomerReportPacket）。</summary>
    public const string Packet = "packet";

    /// <summary>单证打印 / 导出（TradeDocument）。</summary>
    public const string Document = "document";

    /// <summary>财务报表（资产负债表 / 利润表 / 现金流量表 + 应收账龄 / 采购成本 / 退税汇总 / 库存预警）。</summary>
    public const string FinancialStatement = "financial-statement";

    /// <summary>打印模板（SysPrintTemplate 受控绑定；ERP-312）。</summary>
    public const string PrintTemplate = "print-template";
}

/// <summary>
/// 报表迁移登记册的编译期清单条目（静态、无运行时派生）：一条旧报表路由的稳定迁移元数据。
/// <para><see cref="RequiredMenuCodes"/> 为该旧报表重检授权所需的既有菜单编码（一个或多个，全部具备才算授权；空 = 未声明菜单，fail closed 隐藏）；</para>
/// <para><see cref="DatasetKey"/> 为该条目最终迁入的受控数据集键（当前未注册适配器时 parity 仍为 pending）。</para>
/// </summary>
public sealed record ReportMigrationRegistryEntryDefinition(
    string LegacyKey,
    string Title,
    string Category,
    string DatasetKey,
    IReadOnlyList<string> RequiredMenuCodes,
    string RequiredMenuText,
    string CurrencyUnitSemantics,
    bool ExcelCompatible,
    bool PdfCompatible);

/// <summary>报表迁移登记册的运行时派生条目：编译期清单 + 当前账号授权重检（fail closed）+ 派生 parity 状态。</summary>
public sealed record ReportMigrationRegistryEntryDto(
    string LegacyKey,
    string Title,
    string Category,
    string DatasetKey,
    IReadOnlyList<string> RequiredMenuCodes,
    string RequiredMenuText,
    string CurrencyUnitSemantics,
    bool ExcelCompatible,
    bool PdfCompatible,
    string ParityStatus);

/// <summary>报表迁移登记册（只读）：仅当前账号已授权条目 + 全量旧路由是否可退役的全局门。</summary>
public sealed record ReportMigrationRegistryDto(
    int SchemaVersion,
    List<ReportMigrationRegistryEntryDto> Entries,
    bool LegacyRoutesRetirable);

/// <summary>
/// 迁移 parity 比对证据（ERP-296 Stage 2）：一条旧报表是否已有真实的「旧路由 vs 通用平台」夹具比对证据。
/// <para>四个维度必须全部具备才算完整（<see cref="Complete"/>）；仅目录存在 / 兼容性声明 / 预设存在绝不构成证据。</para>
/// </summary>
public sealed record ReportMigrationParityEvidenceDto(
    bool DataGrainMatched,
    bool CurrencyUnitMatched,
    bool PermissionsMatched,
    bool OutputSemanticsMatched)
{
    /// <summary>四个维度是否全部具备（任一缺失即不完整，fail closed）。</summary>
    public bool Complete =>
        DataGrainMatched && CurrencyUnitMatched && PermissionsMatched && OutputSemanticsMatched;
}

/// <summary>
/// 报表迁移登记册的编译期清单（ERP-295 Stage 2）：完整枚举所有旧报表条目，作为运行时 parity 派生的唯一输入。
/// <para>这不是审计用的静态 markdown，而是被 <c>ReportMigrationRegistry</c> 在运行时逐条派生 parity 并被旧路由门控消费的编译期清单。</para>
/// </summary>
public static class ReportMigrationRegistryManifest
{
    /// <summary>完整旧报表清单（固定报表 + 18 个动态报表 + 导出 + 报告包 + 单证 + 财务报表）。</summary>
    public static readonly IReadOnlyList<ReportMigrationRegistryEntryDefinition> Entries =
        new ReportMigrationRegistryEntryDefinition[]
        {
            // ==================== 固定报表（ReportController） ====================
            new("report:product-sales-ranking", "商品销量排名榜", ReportMigrationRegistryCategories.FixedReport,
                "product-sales-ranking", new[] { "product-sales-ranking" }, "商品销量排名榜",
                "发货数量按基础单位独立（绝不跨单位合计）；金额为数量 × 商品当前售价的估算（币种未知，仅估算）", false, false),
            new("report:order-profit", "订单利润暂估表", ReportMigrationRegistryCategories.FixedReport,
                "order-profit", new[] { "order-profit" }, "订单利润暂估表",
                "销售额为订单原币金额、绝不跨币种合计；成本 / 利润 / 利润率为未知（null，绝不回落为 0）；当前价估算为独立口径（币种未知，仅估算）", false, false),
            new("report:customer-shipment", "客户出货量统计表", ReportMigrationRegistryCategories.FixedReport,
                "customer-shipment", new[] { "customer-shipment" }, "客户出货量统计表",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并", false, false),
            new("report:salesman-output", "业务员产值报表", ReportMigrationRegistryCategories.FixedReport,
                "salesman-output", new[] { "salesman-output" }, "业务员产值报表",
                "金额按业务员 × 原币独立小计，绝不跨币种合计；未知 / 无效币种金额与未知利润为未知（null，绝不回落为 0）", false, false),
            new("report:sales-commission", "业务员提成表", ReportMigrationRegistryCategories.FixedReport,
                "sales-commission", new[] { "sales-commission" }, "业务员提成表",
                "金额按业务员桶 × 原币独立小计，绝不跨币种合计；未知 / 无效币种金额与未知利润 / 利润率 / 提成额为未知（null，绝不回落为 0）", false, false),
            new("report:container-stats", "柜量与装柜利用率统计", ReportMigrationRegistryCategories.FixedReport,
                "container-stats", new[] { "container-stats" }, "柜量与装柜利用率统计",
                "箱数/毛重/体积按原始单位；装载率与柜型未知；不跨币种换算", false, false),
            new("report:follow-up-due", "跟进提醒", ReportMigrationRegistryCategories.FixedReport,
                "follow-up-due", new[] { "follow-up-due" }, "跟进提醒",
                "无金额/币种；日期为自然日；到期天数与到期状态由下次跟进日期与 as-of 日期派生", false, false),
            new("report:quotation-conversion", "报价成交率分析", ReportMigrationRegistryCategories.FixedReport,
                "quotation-conversion", new[] { "quotation" }, "报价单",
                "金额均为报价单原币；按业务员 × 原币分列；绝不跨币种换算或合并", false, false),
            new("report:inventory-movement", "库存移动与呆滞报表", ReportMigrationRegistryCategories.FixedReport,
                "inventory-movement", new[] { "stock-query" }, "库存查询",
                "数量按基础单位；成本按移动加权平均；不跨币种合并", false, false),
            new("report:inventory-aging", "库存库龄与成本估值报表", ReportMigrationRegistryCategories.FixedReport,
                "inventory-aging", new[] { "stock-query" }, "库存查询",
                "数量按基础单位；成本/金额按移动加权平均；不跨币种合并", false, false),

            // ==================== 财务报表 ====================
            new("report:balance-sheet", "资产负债表", ReportMigrationRegistryCategories.FinancialStatement,
                "balance-sheet", new[] { "balance-sheet" }, "资产负债表",
                "金额按单据金额直接汇总（资产/负债/权益）；不跨币种换算", false, false),
            new("report:income-statement", "利润表", ReportMigrationRegistryCategories.FinancialStatement,
                "income-statement", new[] { "income-statement" }, "利润表",
                "金额按单据金额直接汇总（收入-成本-费用）；不跨币种换算", false, false),
            new("report:cash-flow", "现金流量表", ReportMigrationRegistryCategories.FinancialStatement,
                "cash-flow", new[] { "cash-flow" }, "现金流量表",
                "金额按单据金额直接汇总（流入-流出）；不跨币种换算", false, false),
            new("report:ar-aging", "应收账龄分析表", ReportMigrationRegistryCategories.FinancialStatement,
                "ar-aging", new[] { "ar-aging" }, "应收账龄分析表",
                "金额按原币呈现；账龄按自然日；不跨币种换算或合并", false, false),
            new("report:purchase-cost", "采购成本分析表", ReportMigrationRegistryCategories.FinancialStatement,
                "purchase-cost", new[] { "purchase-cost" }, "采购成本分析表",
                "金额按原币呈现；按供应商聚合；不跨币种换算或合并", false, false),
            new("report:tax-refund-summary", "退税汇总表", ReportMigrationRegistryCategories.FinancialStatement,
                "tax-refund-summary", new[] { "tax-refund-summary" }, "退税汇总表",
                "金额按原币呈现；按退税期间聚合；不跨币种换算或合并", false, false),
            new("report:stock-alert", "库存预警表", ReportMigrationRegistryCategories.FinancialStatement,
                "stock-alert", new[] { "stock-alert" }, "库存预警表",
                "数量按基础单位；无金额/币种", false, false),


            // ==================== 18 个动态报表 ====================
            new("dynamic:agency-service-fee-monthly", "动态代理服务费月度汇总报表", ReportMigrationRegistryCategories.DynamicReport,
                "agency-service-fee-monthly", new[] { "customer" }, "客户资料",
                "金额按原币呈现；按对账月份/客户分组；不跨币种换算或合并", true, true),
            new("dynamic:container-stats", "动态柜量与装柜利用率证据报表", ReportMigrationRegistryCategories.DynamicReport,
                "container-stats", new[] { "container-stats" }, "柜量与装柜利用率统计",
                "箱数/毛重/体积按原始单位；装载率与柜型未知；不跨币种换算", true, true),
            new("dynamic:customer-shipment", "动态客户出货量统计报表", ReportMigrationRegistryCategories.DynamicReport,
                "customer-shipment", new[] { "customer-shipment" }, "客户出货量统计表",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并", true, true),
            new("dynamic:follow-up-due", "动态跟进提醒报表", ReportMigrationRegistryCategories.DynamicReport,
                "follow-up-due", new[] { "follow-up-due" }, "跟进提醒",
                "无金额/币种；日期为自然日；到期天数与到期状态由下次跟进日期与 as-of 日期派生", true, true),
            new("dynamic:inventory-aging", "动态库存库龄报表", ReportMigrationRegistryCategories.DynamicReport,
                "inventory-aging", new[] { "stock-query" }, "库存查询",
                "数量按基础单位；成本/金额按移动加权平均；不跨币种合并", true, true),
            new("dynamic:inventory-movement", "动态库存移动报表", ReportMigrationRegistryCategories.DynamicReport,
                "inventory-movement", new[] { "stock-query" }, "库存查询",
                "数量按基础单位；成本按移动加权平均；不跨币种合并", true, true),
            new("dynamic:order-profit", "动态订单利润暂估报表", ReportMigrationRegistryCategories.DynamicReport,
                "order-profit", new[] { "order-profit" }, "订单利润暂估表",
                "销售额为订单原币金额、绝不跨币种合计；成本 / 利润 / 利润率为未知（null，绝不回落为 0）；当前价估算为独立口径（币种未知，仅估算）", true, true),
            new("dynamic:product-sales-ranking", "动态商品销量排名报表", ReportMigrationRegistryCategories.DynamicReport,
                "product-sales-ranking", new[] { "product-sales-ranking" }, "商品销量排名榜",
                "发货数量按基础单位独立（绝不跨单位合计）；金额为数量 × 商品当前售价的估算（币种未知，仅估算）", true, true),
            new("dynamic:purchase-order", "动态采购订单报表", ReportMigrationRegistryCategories.DynamicReport,
                "purchase-order", new[] { "purchase-order" }, "采购订单",
                "金额按订单原币呈现，不跨币种换算或合并", true, true),
            new("dynamic:quotation-conversion", "动态报价成交率报表", ReportMigrationRegistryCategories.DynamicReport,
                "quotation-conversion", new[] { "quotation" }, "报价单",
                "金额均为报价单原币；按业务员 × 原币分列；绝不跨币种换算或合并", true, true),
            new("dynamic:receipt-reconciliation", "动态客户订单与收款核对报表", ReportMigrationRegistryCategories.DynamicReport,
                "receipt-reconciliation", new[] { "sales-order" }, "销售订单",
                "金额按原币呈现；订单与收款核对；不跨币种换算或合并", true, true),
            new("dynamic:receivable", "动态客户应收账款证据报表", ReportMigrationRegistryCategories.DynamicReport,
                "receivable", new[] { "customer" }, "客户资料",
                "金额按证据原币呈现，不跨币种换算或合并；算术剩余证据不主张权威余额", true, true),
            new("dynamic:sales-commission", "动态业务员提成证据报表", ReportMigrationRegistryCategories.DynamicReport,
                "sales-commission", new[] { "sales-commission" }, "业务员提成表",
                "金额按业务员桶 × 原币独立小计，绝不跨币种合计；未知 / 无效币种金额与未知利润 / 利润率 / 提成额为未知（null，绝不回落为 0）", true, true),
            new("dynamic:salesman-output", "动态业务员产值证据报表", ReportMigrationRegistryCategories.DynamicReport,
                "salesman-output", new[] { "salesman-output" }, "业务员产值报表",
                "金额按业务员 × 原币独立小计，绝不跨币种合计；未知 / 无效币种金额与未知利润为未知（null，绝不回落为 0）", true, true),
            new("dynamic:sales-order", "动态销售订单报表", ReportMigrationRegistryCategories.DynamicReport,
                "sales-order", new[] { "sales-order" }, "销售订单",
                "金额按订单原币呈现，不跨币种换算或合并", true, true),
            new("dynamic:shipment-finance", "动态销售订单出货/财务进度报表", ReportMigrationRegistryCategories.DynamicReport,
                "shipment-finance", new[] { "sales-order" }, "销售订单",
                "数量按基础单位；金额按原币呈现；不跨币种换算或合并", true, true),
            new("dynamic:supplier-aging", "动态供应商对账与账龄报表", ReportMigrationRegistryCategories.DynamicReport,
                "supplier-aging", new[] { "purchase-order" }, "采购订单",
                "金额按原币呈现；账龄按自然日；不跨币种换算或合并", true, true),
            new("dynamic:supplier-exposure", "动态供应商采购敞口报表", ReportMigrationRegistryCategories.DynamicReport,
                "supplier-exposure", new[] { "purchase-order" }, "采购订单",
                "金额按原币呈现；不跨币种换算或合并", true, true),


            // ==================== 导出 ====================
            new("export:product-export-field-completeness", "出口字段完整度工作台", ReportMigrationRegistryCategories.Export,
                "product-export-field-completeness", new[] { "product" }, "商品资料",
                "只读字段完整度（无金额/币种）", false, false),

            // ==================== 报告包 ====================
            new("packet:customer-report-packet", "客户报告包预览与导出", ReportMigrationRegistryCategories.Packet,
                "customer-report-packet", new[] { "sales-order", "customer" }, "销售订单 + 客户资料",
                "两个分区各自金额按原币呈现；不跨币种换算或合并", true, true),

            // ==================== 单证（TradeDocument 打印 / 导出） ====================
            new("document:trade-document-print", "出口单证打印数据", ReportMigrationRegistryCategories.Document,
                "trade-document", new[] { "doc-center" }, "单证中心",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并", false, true),
            new("document:trade-document-export-excel", "出口单证台账导出", ReportMigrationRegistryCategories.Document,
                "trade-document", new[] { "doc-center" }, "单证中心",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并", true, false),
        };
}

