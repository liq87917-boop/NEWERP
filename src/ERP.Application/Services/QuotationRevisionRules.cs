using ERP.Application.Common;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 报价单版本链的纯规则（ERP-035，无数据库依赖，便于逐条单测）：
/// 版本号语义（初始版本 = 1、链内单调递增）、版本单号格式（根单号 + <c>-R版本号</c>）
/// 与单号长度上限校验。
/// </summary>
public static class QuotationRevisionRules
{
    /// <summary>初始版本号（= <see cref="Quotation.InitialRevisionNumber"/>，历史报价单同此口径）</summary>
    public const int InitialRevisionNumber = Quotation.InitialRevisionNumber;

    /// <summary>版本单号分隔符：根单号 + 分隔符 + 版本号（如 QT202609240001-R2）</summary>
    public const string RevisionNoSeparator = "-R";

    /// <summary>报价单号长度上限（与 <see cref="Quotation.QuotationNo"/> 的 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxQuotationNoLength = 50;

    /// <summary>
    /// 有效版本号：历史报价单（新增版本列之前创建）在库中为 0 或未赋值，一律按初始版本 1 处理；
    /// 只用于读取与计算，**不回写数据库**（不破坏历史数据）。
    /// </summary>
    public static int EffectiveRevisionNumber(int persisted) =>
        persisted < InitialRevisionNumber ? InitialRevisionNumber : persisted;

    /// <summary>下一个版本号 = 链内现有最大版本号 + 1（无任何版本时为初始版本 1）</summary>
    public static int NextRevisionNumber(int maxExistingRevision) =>
        EffectiveRevisionNumber(maxExistingRevision) + 1;

    /// <summary>是否为初始版本（无法定前序版本）</summary>
    public static bool IsInitialRevision(long? previousRevisionId) => previousRevisionId is null;

    /// <summary>版本单号：根单号 + <c>-R</c> + 版本号（链内版本号唯一 → 单号唯一）</summary>
    public static string BuildRevisionNo(string rootQuotationNo, int revisionNumber) =>
        $"{rootQuotationNo}{RevisionNoSeparator}{revisionNumber}";

    /// <summary>单号长度校验：超长直接拒绝，不做静默截断（截断会破坏「根单号 + 版本号」可读性）</summary>
    public static void EnsureRevisionNoFits(string quotationNo)
    {
        if (quotationNo.Length > MaxQuotationNoLength)
            throw BusinessException.RuleConflict(
                $"版本单号「{quotationNo}」超过 {MaxQuotationNoLength} 个字符，无法生成版本号，请先整理根单号");
    }
}
