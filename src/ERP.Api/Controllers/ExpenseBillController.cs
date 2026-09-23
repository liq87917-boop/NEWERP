using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 费用单控制器（阶段 2 新增）
/// 维护：出口杂费（报关费 / 拖车费 / THC / 文件费 / 港杂费 / 仓储费 / 快递费 / 查验费…）
/// 支持：按整柜 / 拼柜 / 散货 / 订单 / 客户 归属，并按 体积 / 重量 / 金额 / 箱数 / 手工 分摊
/// 另提供：拼柜费用分摊（预览 + 生成），用于"一柜多客户，费用按体积或重量分摊"
/// </summary>
[ApiController]
[Route("api/finance/expenses")]
[Authorize]
public class ExpenseBillController : BaseCrudController<FinanceExpense>
{
    private readonly IErpDbContext _db;

    public ExpenseBillController(IGenericService<FinanceExpense> service, IErpDbContext db) : base(service)
    {
        _db = db;
    }

    /// <summary>分摊预览：只计算不写库，供界面确认</summary>
    [HttpPost("allocate-preview")]
    public IActionResult AllocatePreview([FromBody] ExpenseAllocateRequest req)
    {
        var items = Calculate(req);
        return Ok(ApiResponse<List<ExpenseAllocateResultItem>>.Success(items, "计算完成"));
    }

    /// <summary>
    /// 分摊并生成费用单：按明细为每个客户生成一条费用单记录
    /// 安全性：同一「柜号 + 费用类型 + 日期」已存在记录时拒绝重复生成，避免误操作重复计费
    /// </summary>
    [HttpPost("allocate-apply")]
    public async Task<IActionResult> AllocateApply([FromBody] ExpenseAllocateRequest req)
    {
        if (req.Details is null || req.Details.Count == 0)
            throw BusinessException.InvalidParameter("请至少填写一行分摊明细");

        var expenseDate = (req.ExpenseDate ?? DateTime.Today).Date;
        var allocated = Calculate(req);

        var exists = await _db.FinanceExpenses.AnyAsync(x => !x.IsDeleted
            && x.RefNo == req.RefNo && x.ExpenseType == req.ExpenseType && x.ExpenseDate.Date == expenseDate);
        if (exists)
            throw BusinessException.RuleConflict($"已存在「{req.RefNo} / {req.ExpenseType} / {expenseDate:yyyy-MM-dd}」的费用单，请勿重复生成（如需调整请先删除原记录）");

        // 单号：EXP-yyyyMMdd-序号
        var prefix = $"EXP-{expenseDate:yyyyMMdd}-";
        var maxNo = await _db.FinanceExpenses
            .Where(x => x.ExpenseNo.StartsWith(prefix))
            .OrderByDescending(x => x.ExpenseNo)
            .Select(x => x.ExpenseNo)
            .FirstOrDefaultAsync();
        var seq = 1;
        if (maxNo is not null && int.TryParse(maxNo[prefix.Length..], out var n)) seq = n + 1;

        var created = 0;
        foreach (var item in allocated)
        {
            _db.FinanceExpenses.Add(new FinanceExpense
            {
                ExpenseNo = $"{prefix}{seq:000}",
                ExpenseDate = expenseDate,
                ExpenseType = req.ExpenseType,
                Amount = item.AllocatedAmount,
                Currency = req.Currency,
                ExchangeRate = req.ExchangeRate <= 0 ? 1 : req.ExchangeRate,
                AmountCny = req.Currency == "CNY" ? item.AllocatedAmount : Math.Round(item.AllocatedAmount * req.ExchangeRate, 2),
                Payee = req.Payee,
                RefType = req.RefType,
                RefNo = req.RefNo,
                CustomerId = item.CustomerId,
                CustomerName = item.CustomerName,
                AllocationBase = req.AllocationBase,
                AllocationRatio = item.Ratio,
                AllocatedAmount = item.AllocatedAmount,
                PaymentStatus = "未付",
                Remark = req.Remark
            });
            seq++;
            created++;
        }
        await _db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Success(new { created, items = allocated }, $"已生成 {created} 条费用单"));
    }

    /// <summary>分摊计算核心：按指定基数计算各客户权重、比例与分摊金额（末行补齐四舍五入差额）</summary>
    private static List<ExpenseAllocateResultItem> Calculate(ExpenseAllocateRequest req)
    {
        var details = req.Details ?? new List<ExpenseAllocateDetail>();
        if (details.Count == 0) return new List<ExpenseAllocateResultItem>();

        decimal WeightOf(ExpenseAllocateDetail d) => req.AllocationBase switch
        {
            "按体积" => d.Volume,
            "按重量" => d.Weight,
            "按箱数" => d.Cartons,
            "按金额" => d.Amount,
            _ => d.Volume           // 默认按体积（手工分摊场景由调用方直接给定权重）
        };

        var weights = details.Select(WeightOf).ToList();
        var sum = weights.Sum();

        // 权重全部为 0 时退化为"平均分摊"，避免除零并给出可用结果
        var items = new List<ExpenseAllocateResultItem>();
        decimal assigned = 0;
        for (var i = 0; i < details.Count; i++)
        {
            var d = details[i];
            var ratio = sum > 0 ? Math.Round(weights[i] / sum * 100, 4) : Math.Round(100m / details.Count, 4);
            var amount = i == details.Count - 1
                ? Math.Round(req.TotalAmount - assigned, 2)                       // 末行补齐差额
                : Math.Round(req.TotalAmount * ratio / 100m, 2);
            assigned += amount;

            items.Add(new ExpenseAllocateResultItem
            {
                CustomerId = d.CustomerId,
                CustomerName = d.CustomerName,
                WeightValue = weights[i],
                Ratio = ratio,
                AllocatedAmount = amount
            });
        }
        return items;
    }
}

