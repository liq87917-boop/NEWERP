using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 报价单「行号 / 金额 / 合计 / 有效期」的唯一权威整理逻辑（ERP-035 从
/// <c>QuotationController</c> 抽到应用层，新增 / 修改 / **创建新版本** 三条路径共用同一份实现）：
/// 行号按数组顺序重排为 1..n、金额按「数量 × 单价」重算、合计与折人民币由服务端复核，
/// 客户端提交的行号 / 金额 / 合计一律不被信任。
/// <para>抽出的目的：创建版本时必须复用「既有权威报价逻辑」复算合计，
/// 不允许在版本复制路径上另写一套算法（口径漂移会导致版本间金额不可比）。</para>
/// </summary>
public static class QuotationLineRules
{
    /// <summary>行号 / 金额 / 合计 / 有效期统一整理（后端复核，防止前端篡改合计）</summary>
    public static void Normalize(Quotation e)
    {
        decimal total = 0;
        var line = 0;
        foreach (var d in e.Details)
        {
            d.Id = 0;
            d.QuotationId = e.Id;
            d.QuotationNo = e.QuotationNo;
            d.SortNo = ++line;
            d.Amount = Math.Round(d.Quantity * d.UnitPrice, 2);
            d.CreatedAt = DateTime.Now;
            total += d.Amount;
        }
        e.TotalAmount = Math.Round(total, 2);
        var rate = e.ExchangeRate == 0 ? 1 : e.ExchangeRate;
        e.TotalAmountCny = Math.Round(e.TotalAmount * rate, 2);
        if (e.QuotationDate == default) e.QuotationDate = DateTime.Today;
        e.ValidUntil ??= e.QuotationDate.Date.AddDays(30);
    }
}
