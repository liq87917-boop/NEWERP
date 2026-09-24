using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using System.Security.Cryptography;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单变更申请的纯规则（ERP-047，无数据库依赖，便于逐条单测）：
/// 状态机（草稿 → 已提交 → 已取消）、有界文本校验、来源快照签名与快照标记、
/// 来源变化提示文案、对照文案与边界声明。
/// <para>边界：本规则只做**校验与文案**，不写库、不访问数据库、不审核、不套用变更，
/// 也不改写来源销售订单与任何下游记录。</para>
/// </summary>
public static class SalesOrderChangeRequestRules
{
    // ==================== 0. 状态与文案 ====================

    /// <summary>状态：草稿（可编辑；尚未提交）</summary>
    public const int StatusDraft = 0;

    /// <summary>状态：已提交（拟议快照冻结、不可编辑；**不**代表已批准或已套用）</summary>
    public const int StatusSubmitted = 1;

    /// <summary>状态：已取消（保留原始与拟议证据，必须记录原因）</summary>
    public const int StatusCancelled = 2;

    /// <summary>支持的状态（超出范围一律拒绝）</summary>
    public static readonly int[] SupportedStatuses = { StatusDraft, StatusSubmitted, StatusCancelled };

    /// <summary>状态文案（未知状态照实返回而不是猜测）</summary>
    public static string StatusText(int status) => status switch
    {
        StatusDraft => "草稿",
        StatusSubmitted => "已提交（仅登记，未批准、未套用）",
        StatusCancelled => "已取消",
        _ => $"未知状态({status})"
    };

    /// <summary>来源订单的单据状态文案（只读标注；未知取值返回枚举名）</summary>
    public static string DocumentStatusText(int status)
        => Enum.IsDefined(typeof(DocumentStatus), status)
            ? DocumentStatusText((DocumentStatus)status)
            : $"未知状态({status})";

    /// <summary>来源订单的单据状态文案（只读标注）</summary>
    public static string DocumentStatusText(DocumentStatus status) => status switch
    {
        DocumentStatus.Pending => "待提交",
        DocumentStatus.Submitted => "已提交",
        DocumentStatus.Approved => "已审核",
        DocumentStatus.Rejected => "已驳回",
        DocumentStatus.Completed => "已完成",
        DocumentStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    /// <summary>币种文案（界面对照用；未知取值返回枚举名）</summary>
    public static string CurrencyText(Currency currency) => currency switch
    {
        Currency.CNY => "CNY 人民币",
        Currency.USD => "USD 美元",
        Currency.EUR => "EUR 欧元",
        Currency.HKD => "HKD 港币",
        Currency.GBP => "GBP 英镑",
        Currency.JPY => "JPY 日元",
        _ => currency.ToString()
    };

    // ==================== 1. 有界上限 ====================

    /// <summary>变更原因长度上限</summary>
    public const int MaxReasonLength = 500;

    /// <summary>取消原因长度上限</summary>
    public const int MaxCancelReasonLength = 500;

    /// <summary>拟议明细行数上限（有界：单张申请一次最多登记这么多行）</summary>
    public const int MaxDetailLines = 200;

    /// <summary>来源快照标记长度上限</summary>
    public const int MaxSnapshotMarkerLength = 300;

    /// <summary>来源明细签名长度上限</summary>
    public const int MaxDetailSignatureLength = 500;

    /// <summary>备注 / 验货 / 包装 / 唛头类文本上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>付款条件上限</summary>
    public const int MaxPaymentTermsLength = 200;

    /// <summary>运输方式上限</summary>
    public const int MaxShippingMethodLength = 100;

    /// <summary>合同号 / 客户 PO 号 / 价格条款上限</summary>
    public const int MaxShortCodeLength = 50;

    /// <summary>目的港文本上限</summary>
    public const int MaxPortTextLength = 100;

    /// <summary>收货人 / 通知人上限</summary>
    public const int MaxPartyLength = 300;

    /// <summary>出口方式 / 业务性质上限</summary>
    public const int MaxModeLength = 20;

    /// <summary>商品名称 / 规格上限</summary>
    public const int MaxProductTextLength = 200;

    /// <summary>单位上限</summary>
    public const int MaxUnitLength = 20;

    // ==================== 2. 状态机 ====================

    /// <summary>仅草稿可编辑（已提交申请冻结拟议快照，已取消申请只读）</summary>
    public static void EnsureDraftEditable(int status)
    {
        if (status == StatusDraft) return;
        throw BusinessException.RuleConflict(status == StatusSubmitted
            ? "变更申请已提交：拟议快照已冻结，不能再编辑（只能查看或取消）"
            : "变更申请已取消：只能查看历史，不能再编辑");
    }

    /// <summary>仅草稿可提交（重复提交、已取消一律拒绝；本模块不提供「重新提交」）</summary>
    public static void EnsureSubmittable(int status)
    {
        if (status == StatusDraft) return;
        throw BusinessException.RuleConflict(status == StatusSubmitted
            ? "变更申请已提交，不能重复提交"
            : "变更申请已取消，不能再提交");
    }

    /// <summary>草稿与已提交都可取消（已取消不能重复取消）</summary>
    public static void EnsureCancellable(int status)
    {
        if (status is StatusDraft or StatusSubmitted) return;
        throw BusinessException.RuleConflict("变更申请已取消，不能重复取消");
    }

    /// <summary>该状态是否可编辑（只读标注，供 DTO 输出）</summary>
    public static bool IsEditable(int status) => status == StatusDraft;

    // ==================== 3. 文本校验（有界；超长一律拒绝，不静默截断） ====================

    /// <summary>变更原因校验（必填、有界、拒绝控制字符）</summary>
    public static string NormalizeReason(string? reason)
        => NormalizeText(reason, MaxReasonLength, "变更原因", required: true);

    /// <summary>取消原因校验（必填、有界、拒绝控制字符）</summary>
    public static string NormalizeCancelReason(string? reason)
        => NormalizeText(reason, MaxCancelReasonLength, "取消原因", required: true);

    /// <summary>可选文本校验（去首尾空白、拒绝控制字符与超长取值）</summary>
    public static string NormalizeOptionalText(string? value, int maxLength, string fieldName)
        => NormalizeText(value, maxLength, fieldName, required: false);

    /// <summary>
    /// 通用文本校验：去首尾空白；必填字段为空一律拒绝；超长一律拒绝（不静默截断，避免把非法值写进库）；
    /// 除制表 / 换行 / 回车外的控制字符一律拒绝。
    /// </summary>
    private static string NormalizeText(string? value, int maxLength, string fieldName, bool required)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            if (required) throw BusinessException.InvalidParameter($"{fieldName}不能为空");
            return string.Empty;
        }

        if (text.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldName}长度不能超过 {maxLength} 个字符");

        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
                throw BusinessException.InvalidParameter($"{fieldName}不能包含控制字符");
        }

        return text;
    }

    // ==================== 4. 来源快照签名与标记 ====================

    /// <summary>
    /// 来源订单明细的**确定性签名**：行数 + 明细合计 + 规范行串的 SHA-256（取前 32 位十六进制）。
    /// 相同明细必得相同签名；行数 / 商品 / 规格 / 单位 / 数量 / 单价任一变化都会改变签名 ——
    /// 用于「来源是否已变化」检测，不保存明细内容，也不参与来源订单自身的计算。
    /// </summary>
    public static string BuildDetailSignature(IEnumerable<SalesOrderDetail>? details)
    {
        var lines = (details ?? Enumerable.Empty<SalesOrderDetail>())
            .Where(d => !d.IsDeleted)
            .OrderBy(d => d.Id)
            .ToList();

        var sum = lines.Sum(d => d.Quantity * d.UnitPrice);
        var canonical = string.Join(';', lines.Select(d =>
            $"{d.ProductId}|{d.ProductName}|{d.Spec}|{d.Unit}|{d.Quantity:0.########}|{d.UnitPrice:0.########}"));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var signature = $"{lines.Count}|{sum:0.########}|{hash[..32]}";
        return signature.Length <= MaxDetailSignatureLength ? signature : signature[..MaxDetailSignatureLength];
    }

    /// <summary>
    /// 来源快照标记（服务端权威写入、确定性文本）：来源单号 / 状态 / 最后更新时间 / 明细摘要，
    /// 便于人工核对「这条申请登记的到底是哪一刻的哪个订单」。
    /// </summary>
    public static string BuildSnapshotMarker(
        string? orderNo, int sourceStatus, DateTime sourceUpdatedAt, int detailLineCount, decimal totalAmount)
    {
        var marker = $"来源 {Safe(orderNo)} · 状态 {DocumentStatusText(sourceStatus)} · 更新 {sourceUpdatedAt:yyyy-MM-dd HH:mm:ss}"
            + $" · 明细 {detailLineCount} 行 · 总额 {totalAmount:0.########}";
        return marker.Length <= MaxSnapshotMarkerLength ? marker : marker[..MaxSnapshotMarkerLength];
    }

    // ==================== 5. 来源可用性与来源变化提示（只读标注） ====================

    /// <summary>来源订单可用性文案（不存在 / 已删除时照实说明，历史申请照常可读）</summary>
    public static string SourceAvailabilityText(bool available)
        => available ? "来源销售订单可用" : "来源销售订单已不存在或已删除（申请快照仍可读，不能据此套用变更）";

    /// <summary>
    /// 来源变化提示文案：只**提示**「来源在快照之后已变化」并说明系统不会自动刷新拟议值；
    /// 来源未变化时也照实说明登记时的依据仍然成立。
    /// </summary>
    public static string SourceChangedText(bool changed, bool available)
    {
        if (!available) return "来源销售订单已不存在：无法核对来源是否变化（申请快照保持登记当时口径）";
        return changed
            ? "来源销售订单在登记快照之后已发生变化：本申请不覆盖、不合并、不静默刷新拟议值，请人工核对后决定是否新建申请"
            : "来源销售订单自登记快照以来未发生变化（按登记依据的确定性签名核对）";
    }

    /// <summary>来源变化差异依据文案（逐项列出不一致的对比项；无差异时说明比对口径）</summary>
    public static string SourceChangeDetailText(IReadOnlyList<string> changedFields)
        => changedFields.Count == 0
            ? "比对项：状态 / 最后更新时间 / 订单总额 / 明细签名 —— 全部与登记快照一致"
            : "比对项不一致：" + string.Join("、", changedFields);

    /// <summary>变更摘要文案（差异规模；不表示任何审批结果）</summary>
    public static string ChangeSummaryText(int headerChangedCount, int changedLines, int addedLines, int removedLines)
    {
        if (headerChangedCount == 0 && changedLines == 0 && addedLines == 0 && removedLines == 0)
            return "拟议值与来源快照完全一致（可作为「原样留痕」申请，但未做任何修改）";

        var parts = new List<string>();
        if (headerChangedCount > 0) parts.Add($"主表 {headerChangedCount} 项不同");
        if (changedLines > 0) parts.Add($"明细 {changedLines} 行修改");
        if (addedLines > 0) parts.Add($"明细新增 {addedLines} 行");
        if (removedLines > 0) parts.Add($"明细移除 {removedLines} 行");
        return "拟议与来源差异：" + string.Join("、", parts) + "（仅为拟议，未批准、未套用）";
    }

    /// <summary>明细对照文案（新增 / 移除 / 已修改 / 未修改）</summary>
    public static string DetailComparisonText(bool hasSourceLine, bool removed, bool changed)
    {
        if (!hasSourceLine) return "拟议新增行（来源没有对应行）";
        if (removed) return "拟议移除来源行（来源快照保留）";
        return changed ? "拟议已修改（与来源行不同）" : "与来源行一致（未修改）";
    }

    // ==================== 6. 边界与口径声明（代码与界面同源） ====================

    /// <summary>审批边界声明：本模块没有审批阈值、审批人、生效时间，也不套用任何变更</summary>
    public const string ApprovalBoundaryText =
        "变更申请只有「草稿 / 已提交 / 已取消」三种状态：系统不定义审批阈值、不记录审批人、不发号生效、"
        + "不套用变更；任何变更的批准与执行都要在系统之外由人工决定，并另行走既有销售订单流程。";

    /// <summary>模块边界声明</summary>
    public const string BoundaryText =
        "本登记册只写入自己的两张表：创建 / 编辑 / 提交 / 取消申请都不改写来源销售订单的主表与明细，"
        + "也不改写报价单、销售出库、装柜与出运、收款与发票、佣金、库存与库存成本、单证中心与财务记录；"
        + "不执行任何生产库 DDL、回填或数据迁移。";

    /// <summary>快照口径声明</summary>
    public const string SnapshotPolicyText =
        "登记时冻结来源销售订单的主表值与明细行快照（含状态、最后更新时间与明细确定性签名）；"
        + "来源订单之后的变化只会被显式提示，不会被自动写入本申请。";

    /// <summary>金额口径声明</summary>
    public const string AmountPolicyText =
        "拟议明细金额 = 数量 × 单价、拟议总额 = Σ 明细金额、拟议定金金额 = 总额 × 定金比例%（0~100），"
        + "全部由服务端按销售订单唯一权威算法重算（SalesOrderAmountRules），不接受客户端合计。";

    /// <summary>提交口径声明</summary>
    public const string SubmitPolicyText =
        "提交后拟议快照冻结、申请不可编辑（只能查看或取消，取消必须填写原因）；"
        + "申请没有「已批准 / 已套用」状态，也不提供硬删除。";

    /// <summary>内部工具：空值安全文本</summary>
    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "(未登记单号)" : value;
}
