using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 客户对账证据导出（ERP-086）纯规则：证据类白名单、导出格式、有界上限，
/// 以及与接口、界面、文档同源的边界 / 口径文案。
/// <para>关键口径：本导出复用 ERP-074 客户应收账款对账工作台（<see cref="CustomerReceivableReconciliationService"/>）
/// 的同一派生引擎与筛选口径，把四类证据分别标注：①ERP-055 发票含税总额证据、②ERP-073 有效收款分摊证据、
/// ③ERP-075 收款分摊上下文、④ERP-076 发票 → 出货链接证据；算术剩余证据 = 发票含税总额 − 有效分摊合计，
/// 各类证据绝不互相抵减、绝不断言已结清 / 已审计 / 已确认的余额；不同币种分别成行、绝无跨币种总额。</para>
/// <para>边界：本规则只做校验与文案，不写库、不开票、不记账、不核销、不收款或付款、不催收或联系客户，
/// 也不改写发票证据、收款分摊证据、收款单、客户主数据、销售订单、装柜清单、单证、库存、费用、退税与结算记录。</para>
/// </summary>
public static class CustomerReconciliationStatementRules
{
    // ==================== 1. 证据类（分别标注，绝不合并） ====================

    /// <summary>ERP-055 发票含税总额证据</summary>
    public const string ClassInvoice = "invoice";

    /// <summary>ERP-073 有效收款分摊证据（收款单 → 发票）</summary>
    public const string ClassReceiptAllocation = "receipt_allocation";

    /// <summary>ERP-075 收款分摊上下文（分摊行 / 已作废历史 / 分摊状态）</summary>
    public const string ClassReceiptContext = "receipt_context";

    /// <summary>ERP-076 发票 → 出货链接证据</summary>
    public const string ClassShipmentLink = "shipment_link";

    /// <summary>全部证据类（默认）</summary>
    public const string ClassAll = "all";

    /// <summary>支持的证据类筛选取值</summary>
    public static readonly string[] SupportedEvidenceClasses =
    {
        ClassInvoice, ClassReceiptAllocation, ClassReceiptContext, ClassShipmentLink, ClassAll,
    };

    /// <summary>证据类中文文案（接口、界面与文档同源）</summary>
    public static string EvidenceClassText(string evidenceClass) => evidenceClass switch
    {
        ClassInvoice => "发票含税总额证据（ERP-055）",
        ClassReceiptAllocation => "有效收款分摊证据（ERP-073）",
        ClassReceiptContext => "收款分摊上下文（ERP-075）",
        ClassShipmentLink => "发票→出货链接证据（ERP-076）",
        ClassAll => "全部证据类",
        _ => "未知证据类",
    };

    // ==================== 2. 出货链接证据状态（无权威持久化链接册时 fail closed 为「未知」） ====================

    /// <summary>出货链接证据未知（仓库没有权威的持久化「发票 → 出货」链接模型）</summary>
    public const string ShipmentLinkUnknown = "unknown";

    // ==================== 3. 导出格式 ====================

    /// <summary>CSV 导出</summary>
    public const string FormatCsv = "csv";

    /// <summary>HTML 导出</summary>
    public const string FormatHtml = "html";

    /// <summary>支持的导出格式</summary>
    public static readonly string[] SupportedFormats = { FormatCsv, FormatHtml };

    // ==================== 4. 有界上限 ====================

    /// <summary>默认导出行数上限</summary>
    public const int DefaultMaxRows = 2000;

    /// <summary>导出行数硬上限（有界：单次导出最多返回这么多发票证据行）</summary>
    public const int MaxExportRows = 5000;

    // ==================== 5. 筛选归一化（非法取值直接拒绝，不静默忽略） ====================

    /// <summary>归一化证据类筛选（null / 空 = all；非法取值直接拒绝）</summary>
    public static string NormalizeEvidenceClass(string? evidenceClass)
    {
        if (string.IsNullOrWhiteSpace(evidenceClass)) return ClassAll;
        var value = evidenceClass.Trim();
        return SupportedEvidenceClasses.Contains(value, StringComparer.Ordinal)
            ? value
            : throw BusinessException.InvalidParameter(
                $"非法的证据类筛选取值：{evidenceClass}（仅支持 invoice / receipt_allocation / receipt_context / shipment_link / all）");
    }

    /// <summary>归一化导出格式（null / 空 = csv；非法取值直接拒绝）</summary>
    public static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return FormatCsv;
        var value = format.Trim().ToLowerInvariant();
        return SupportedFormats.Contains(value, StringComparer.Ordinal)
            ? value
            : throw BusinessException.InvalidParameter(
                $"非法的导出格式：{format}（仅支持 csv / html）");
    }

    // ==================== 6. 边界 / 口径文案（接口、界面与文档同源） ====================

    /// <summary>模块边界声明：操作性证据核对，不是总账 / 对账单 / 收入确认 / 税务 / 催收 / 结算</summary>
    public const string BoundaryText =
        "本导出是仓库操作性证据核对：呈现 ERP-055 客户销项发票证据、ERP-073 收款分摊证据、"
        + "ERP-075 收款分摊上下文与 ERP-076 发票→出货链接证据，各类证据分别标注、绝不互相抵减；"
        + "它不是总账或应收账款余额、不是经审计的客户对账单、不是收入确认、不是税务申报、"
        + "不是付款通知或催收函，也不是结算确认或核销结果；全程只读，不改变任何来源记录。";

    /// <summary>出货链接证据不可用说明：无权威持久化链接册，一律按「未知」呈现，绝不修复 / 改派 / 推断</summary>
    public const string ShipmentLinkUnavailableText =
        "发票→出货链接证据：仓库当前没有权威的持久化「发票→出货」链接模型（ERP-076 未建立持久化链接册），"
        + "因此本证据类一律按「未知」呈现；缺失 / 无效链接保持可见为未知，绝不修复、不改派、不推断。";

    /// <summary>币种隔离说明：不同币种分别成行，绝不合并、换算或改派</summary>
    public const string CurrencySeparationText =
        "金额按币种分别成行，不同币种绝不合并、换算或改派（仅当来源证据自身携带权威持久化换算时才按该换算呈现）。";
}
