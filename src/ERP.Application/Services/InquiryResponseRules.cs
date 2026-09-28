using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 询价报价响应时间（ERP-104）的纯规则（无数据库依赖，便于逐条单测）：
/// 首张有效报价日期与间隔日历天的口径、缺失 / 异常状态的分类、状态文案与级别，以及只读 / 边界 / 免责声明。
/// <para>审计口径：只按报价单上持久化的 <c>InquiryId</c> 显式链接（绝不按单号 / 文本 / 金额 / 相似度推断链接），
/// 只取版本链根单（<c>RootQuotationId == null</c> 的初始版本，版本不重复计入）；软删除行一律排除；
/// 缺失链接 / 缺失日期 / 报价日期早于询价日期（负间隔）分别作为独立证据标注，绝不推断为已报价。</para>
/// </summary>
public static class InquiryResponseRules
{
    // ==================== 0. 状态与级别常量 ====================

    /// <summary>已报价（有首张有效报价日期且不早于询价日期）</summary>
    public const string StateQuoted = "quoted";

    /// <summary>未报价（无有效报价单链接，或链接存在但报价日期缺失 / 无效）</summary>
    public const string StateUnquoted = "unquoted";

    /// <summary>链接异常（报价日期早于询价日期，间隔为负）</summary>
    public const string StateInconsistentLink = "inconsistent-link";

    /// <summary>成功级别（正常）</summary>
    public const string LevelSuccess = "success";

    /// <summary>中性级别</summary>
    public const string LevelNeutral = "neutral";

    /// <summary>警告级别（异常）</summary>
    public const string LevelWarning = "warning";

    /// <summary>早于该年份的日期视为「缺失 / 无效」（<c>DateTime</c> 未赋值的默认年份为 1）</summary>
    public const int MinDateYear = 1900;

    // ==================== 1. 文案 ====================

    /// <summary>未知文案（缺失且无法判定的取值一律显示未知，不推断）</summary>
    public const string UnknownText = "未知";

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读视图：不写任何表，不改写询价单 / 报价单 / 客户主数据，不生成、不修改、不删除任何记录，也不回填任何历史留痕";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "证据来源是已持久化的询价单与报价单：仅按报价单上持久化的 InquiryId 显式链接（绝不按单号 / 文本 / 金额 / 相似度推断链接），"
        + "并只取报价单版本链根单（初始版本，版本不重复计入）；软删除行一律排除";

    /// <summary>免责文案（哪些结论不能从响应时间推出）</summary>
    public const string DisclaimerText =
        "响应时间只是询价日期与首张有效报价日期之间的日历天算术证据：不代表报价是否成交、不代表承诺交期或履约、"
        + "不是 SLA 或绩效结论，也不是客户对账 / 结算 / 应收应付结论";

    // ==================== 2. 纯计算与分类 ====================

    /// <summary>日期是否有效（非缺失：年份不小于 <see cref="MinDateYear"/>）</summary>
    public static bool IsValidDate(DateTime date) => date.Year >= MinDateYear;

    /// <summary>计算询价日期到报价日期的日历天（报价日期早于询价日期时为负数）</summary>
    public static int ElapsedDays(DateTime inquiryDate, DateTime quotationDate)
        => (quotationDate.Date - inquiryDate.Date).Days;

    /// <summary>单据状态中文文案</summary>
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

    /// <summary>响应状态中文文案</summary>
    public static string StateText(string state) => state switch
    {
        StateQuoted => "已报价",
        StateUnquoted => "未报价",
        StateInconsistentLink => "链接异常",
        _ => UnknownText
    };

    /// <summary>响应状态级别（success / warning / neutral，供前端上色）</summary>
    public static string StateLevel(string state) => state switch
    {
        StateQuoted => LevelSuccess,
        StateInconsistentLink => LevelWarning,
        _ => LevelNeutral
    };

    /// <summary>响应证据文案（缺失链接 / 缺失日期 / 负间隔分别标注，绝不推断）</summary>
    public static string EvidenceText(
        string state, bool hasLink, bool missingDate, DateTime? firstQuotationDate, DateTime inquiryDate)
    {
        return state switch
        {
            StateQuoted when firstQuotationDate.HasValue =>
                $"首张有效报价日期 {firstQuotationDate.Value:yyyy-MM-dd}，间隔 {(firstQuotationDate.Value.Date - inquiryDate.Date).Days} 天",
            StateInconsistentLink when firstQuotationDate.HasValue =>
                $"报价日期 {firstQuotationDate.Value:yyyy-MM-dd} 早于询价日期 {inquiryDate:yyyy-MM-dd}"
                + $"（间隔为负 {(firstQuotationDate.Value.Date - inquiryDate.Date).Days} 天）",
            StateUnquoted when !hasLink => "无有效报价单链接（未报价）",
            StateUnquoted when missingDate => "存在报价单链接但报价日期缺失 / 无效",
            _ => "未报价"
        };
    }

    /// <summary>客户 Id 筛选校验（显式正整数 Id；空值 = 不过滤）</summary>
    public static long? NormalizeCustomerIdFilter(long? customerId)
    {
        if (customerId is null) return null;
        if (customerId <= 0)
            throw BusinessException.InvalidParameter("客户 Id 必须为正整数（按客户筛选时不接受 0 或负数）");
        return customerId;
    }

    /// <summary>
    /// 构建单行（纯函数）：按已加载的「显式链接 + 根单（初始版本）」报价单日期判定首张有效报价与响应状态。
    /// <para>首张有效报价日期 = 显式链接且日期有效（非缺失）的根单报价日期中的最早者；间隔为负时判为链接异常。</para>
    /// </summary>
    public static InquiryResponseRowDto BuildRow(
        long inquiryId, string inquiryNo, DateTime inquiryDate, long customerId, string customerName,
        long? salesmanId, DocumentStatus status,
        IReadOnlyList<(string QuotationNo, DateTime QuotationDate)> linkedRootQuotations)
    {
        var valid = linkedRootQuotations
            .Where(q => IsValidDate(q.QuotationDate))
            .OrderBy(q => q.QuotationDate)
            .ToList();

        var hasLink = linkedRootQuotations.Count > 0;
        var missingDate = hasLink && valid.Count == 0;

        DateTime? firstQuotationDate = null;
        var quotationNo = string.Empty;
        int? elapsedDays = null;
        string state;

        if (valid.Count > 0)
        {
            var first = valid[0];
            firstQuotationDate = first.QuotationDate;
            quotationNo = first.QuotationNo;
            var days = ElapsedDays(inquiryDate, first.QuotationDate);
            elapsedDays = days;
            state = days < 0 ? StateInconsistentLink : StateQuoted;
        }
        else
        {
            state = StateUnquoted;
        }

        return new InquiryResponseRowDto(
            inquiryId, inquiryNo, inquiryDate, customerId, customerName, salesmanId, status,
            DocumentStatusText(status),
            state == StateQuoted,
            state,
            StateText(state),
            StateLevel(state),
            firstQuotationDate,
            quotationNo,
            elapsedDays,
            EvidenceText(state, hasLink, missingDate, firstQuotationDate, inquiryDate));
    }
}
