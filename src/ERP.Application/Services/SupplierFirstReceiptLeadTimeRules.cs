using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>入库证据内存投影（批量派生：一次取全本页入库单，供首收派生与异常标注使用，不落库）</summary>
public sealed record SupplierFirstReceiptReceiptRow(
    long PurchaseOrderId,
    string StockInNo,
    DateTime StockInDate,
    bool IsDeleted,
    DocumentStatus Status,
    long SupplierId);

/// <summary>首收交期派生结果（纯函数返回，供服务装配成行）</summary>
public sealed record SupplierFirstReceiptLeadTimeDerivation(
    string? FirstReceiptNo,
    DateTime? FirstReceiptDate,
    int? ElapsedDays,
    string LeadTimeStatus,
    bool IsAnomalous,
    List<string> Anomalies,
    string Note);

/// <summary>
/// 供应商首收交期纯规则（ERP-109，无数据库依赖，便于逐条单测）：
/// 口径常量、异常证据文案与「首张有效已审核入库」的派生算法（首收日期 / 间隔天数 / 负间隔异常）。
/// <para>边界：本规则只做**只读派生**，不写库、不改写采购订单 / 入库单 / 库存与库存成本，也不执行迁移 / 生产 SQL / 部署。</para>
/// </summary>
public static class SupplierFirstReceiptLeadTimeRules
{
    // ==================== 0. 首收状态常量 ====================

    /// <summary>首收状态：已派生首收日期（首张有效已审核入库，间隔 &gt;= 0）</summary>
    public const string LeadTimeReceived = "received";

    /// <summary>首收状态：负间隔（首张有效已审核入库早于订单日期，间隔为负，异常，不钳制为 0）</summary>
    public const string LeadTimeNegativeInterval = "negative_interval";

    /// <summary>首收状态：不可用（缺首张有效已审核入库证据，或证据超过单次派生上限），间隔未知</summary>
    public const string LeadTimeUnavailable = "unavailable";

    // ==================== 1. 异常证据文案 ====================

    /// <summary>异常证据：存在未审核入库单（不计入首收）</summary>
    public const string AnomalyUnapprovedReceipt = "存在未审核入库单（不计入首收）";

    /// <summary>异常证据：存在已删除入库单（不计入首收）</summary>
    public const string AnomalyDeletedReceipt = "存在已删除入库单（不计入首收）";

    /// <summary>异常证据：存在供应商不一致入库单（不计入首收）</summary>
    public const string AnomalyWrongSupplierReceipt = "存在供应商不一致入库单（不计入首收）";

    /// <summary>异常证据：首张有效入库早于订单日期（负间隔）</summary>
    public const string AnomalyPreOrderReceipt = "首张有效入库早于订单日期（负间隔）";

    /// <summary>异常证据：入库证据超过单次派生上限，首收日期未知</summary>
    public const string AnomalyReceiptOverCeiling = "入库证据超过单次派生上限，首收日期未知";

    // ==================== 2. 有界上限 ====================

    /// <summary>本页关联入库单的单次派生上限（超过即整页首收日期按未知，绝不报出不可靠的首收日期）</summary>
    public const int ReceiptEvidenceCeiling = 2000;

    // ==================== 3. 口径 / 范围 / 边界文案（接口、界面与文档同源） ====================

    /// <summary>首收口径说明（界面与文档同源）</summary>
    public const string RuleText =
        "本报表按采购订单的显式 PurchaseOrderId 链接统计「订单日期到首张有效已审核入库」的间隔天数（只读派生）；"
        + "首收日期 = 最早一张「以本单为来源、未删除、已审核、供应商与本单一致」的入库日期；"
        + "间隔天数 = 首收日期 − 订单日期（可为负，负间隔按异常标注、不钳制为 0）；"
        + "缺少有效入库 / 全部未审核 / 全部已删除 / 全部供应商不一致时为未知（unavailable），绝不当作 0 或正常；"
        + "这不是完整交付完成度，也不是准时率评分，不改写采购订单 / 入库单 / 库存与库存成本。";

    /// <summary>范围说明：合计与计数只统计本次返回页的订单（分页有界）</summary>
    public const string ScopeNoteText =
        "以下汇总与计数只统计本次返回页的已审核采购订单；total 为符合筛选条件的未删除已审核采购订单总数；"
        + "分页按供应商 + 订单日期 + 单据 Id 稳定排序。";

    /// <summary>边界说明：不是交付完成度 / 准时率评分，也不是供应商绩效考核</summary>
    public const string BoundaryText =
        "本报表不是完整交付完成度，也不是准时率 / 供应商绩效考核：只统计首张有效入库与订单日期的间隔天数，"
        + "不统计收齐与否、不按交期承诺打分、不推断缺失或不一致的日期，也不据此改写任何已登记进度。";

    // ==================== 4. 首收派生（纯函数，便于单测） ====================

    /// <summary>
    /// 派生首收交期（纯函数）。首收只取「未删除、已审核、供应商与本单一致」的最早入库；
    /// 未审核 / 已删除 / 供应商不一致的入库仅作为异常证据列出、不计入首收；
    /// 首收早于订单日期时为负间隔（异常，间隔保留负值，不钳制为 0）。
    /// </summary>
    public static SupplierFirstReceiptLeadTimeDerivation Derive(
        DateTime orderDate, long supplierId, IReadOnlyList<SupplierFirstReceiptReceiptRow> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var anomalies = new List<string>();

        if (receipts.Any(r => r.IsDeleted))
            anomalies.Add(AnomalyDeletedReceipt);
        if (receipts.Any(r => !r.IsDeleted && r.Status != DocumentStatus.Approved))
            anomalies.Add(AnomalyUnapprovedReceipt);
        if (receipts.Any(r => !r.IsDeleted && r.SupplierId != supplierId))
            anomalies.Add(AnomalyWrongSupplierReceipt);

        var first = receipts
            .Where(r => !r.IsDeleted && r.Status == DocumentStatus.Approved && r.SupplierId == supplierId)
            .OrderBy(r => r.StockInDate)
            .ThenBy(r => r.StockInNo, StringComparer.Ordinal)
            .FirstOrDefault();

        if (first is null)
        {
            var note = anomalies.Count > 0
                ? "无有效已审核入库证据（存在异常入库记录，不计入首收），首收日期未知"
                : "无已审核、未删除且供应商一致的入库记录，首收日期未知";
            return new SupplierFirstReceiptLeadTimeDerivation(
                null, null, null, LeadTimeUnavailable, anomalies.Count > 0, anomalies, note);
        }

        var elapsed = (first.StockInDate.Date - orderDate.Date).Days;
        if (elapsed < 0) anomalies.Add(AnomalyPreOrderReceipt);

        var status = elapsed < 0 ? LeadTimeNegativeInterval : LeadTimeReceived;
        var anomalous = elapsed < 0 || anomalies.Count > 0;
        var noteText = elapsed < 0
            ? "首张有效入库早于订单日期，间隔为负（不钳制为 0）"
            : anomalies.Count > 0
                ? "首收日期已派生（存在其它异常入库证据，不计入首收）"
                : "首收日期已派生";

        return new SupplierFirstReceiptLeadTimeDerivation(
            first.StockInNo, first.StockInDate.Date, elapsed, status, anomalous, anomalies, noteText);
    }
}
