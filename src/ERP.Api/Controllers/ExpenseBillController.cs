using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
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
[ExpenseRequestAuthorizationFilter]
public class ExpenseBillController : BaseCrudController<FinanceExpense>
{
    private readonly IErpDbContext _db;

    public ExpenseBillController(IGenericService<FinanceExpense> service, IErpDbContext db) : base(service)
    {
        _db = db;
    }

    /// <summary>分摊预览：只计算不写库，供界面确认</summary>
    [HttpPost("allocate-preview")]
    public async Task<IActionResult> AllocatePreview([FromBody] ExpenseAllocateRequest req)
    {
        // ERP-385：只读预览也必须先复核明细客户的权威范围（受限账号 fail closed，不泄露范围外客户）。
        await EnsureLegacyAllocateRequestValidAsync(req, write: false);
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

        // ERP-385：在重复检查、单号生成与任何写入之前完成范围 / 客户 / 币种 / 金额 / 汇率校验。
        await EnsureLegacyAllocateRequestValidAsync(req, write: true);

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

    // ==================== ERP-042：分摊批次与来源留痕（多客户装柜） ====================
    // 口径：在既有「拼柜分摊」之上补批次与来源留痕 —— 分摊结果仍是本模块既有费用单行，
    //       只新增批次 / 分摊行两张留痕表；不改写来源费用单、装柜清单与明细、单证、库存与订单，
    //       也不记账 / 不生成凭证 / 收款 / 付款 / 结算单。

    /// <summary>
    /// 费用单列表（覆盖基类）：按当前账号的权威客户范围在**计数与分页之前**下推数据库过滤，
    /// 再为当前页补写分摊留痕分类与批次状态说明（**只读，不写库**）——
    /// 「批次留痕：EAB-…（有效 / 已作废批次）」「历史分摊（无批次留痕）」「未分摊」。
    /// </summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var scope = ExpenseRequestScope;
        var result = await Service.GetPagedAsync(query, ExpenseAuthorizationRules.ExpenseScopeFilter(scope));
        await ContainerExpenseAllocationService.AnnotateLineageAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<FinanceExpense>>.Success(result));
    }

    /// <summary>费用单全量（下拉用；按当前账号范围在数据库侧过滤后返回，只读）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        var scope = ExpenseRequestScope;
        var result = await Service.GetAllAsync(ExpenseAuthorizationRules.ExpenseScopeFilter(scope));
        await ContainerExpenseAllocationService.AnnotateLineageAsync(_db, result);
        return Ok(ApiResponse<List<FinanceExpense>>.Success(result));
    }

    /// <summary>费用单详情：读取后复核持久化 CustomerId 的权威归属（受限账号 fail closed）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var scope = ExpenseRequestScope;
        var entity = await Service.GetByIdAsync(id);
        ExpenseAuthorizationRules.EnsureStoredExpenseScopeAllowed(scope, entity);
        await ContainerExpenseAllocationService.AnnotateLineageAsync(_db, new[] { entity });
        return Ok(ApiResponse<FinanceExpense>.Success(entity));
    }

    /// <summary>
    /// 新增费用单：写入任何字段之前校验拟议客户范围、真实启用客户、受支持币种、取整后为正的金额与汇率；
    /// 分摊批次留痕列（批次号 / 来源费用）由服务端权威写入，客户端提交体中的同名值一律忽略。
    /// </summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] FinanceExpense entity)
    {
        var scope = ExpenseRequestScope;
        entity.Id = 0;
        await ExpenseAuthorizationRules.EnsureProposedExpenseValidAsync(_db, scope, entity);
        ClearClientSuppliedAllocationLineage(entity);
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<FinanceExpense>.Success(result, "新增成功"));
    }

    /// <summary>
    /// 修改费用单：先复核「已存储」行的权威归属，再校验「拟议」客户范围与金额 / 币种 / 汇率，
    /// 最后从已存储行恢复分摊批次留痕（批次号 / 来源费用）以保证批次生成行的审计不可被客户端改写或清空。
    /// </summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] FinanceExpense entity)
    {
        var scope = ExpenseRequestScope;

        var existing = await _db.FinanceExpenses.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw BusinessException.NotFound("费用单不存在或已删除");
        ExpenseAuthorizationRules.EnsureStoredExpenseScopeAllowed(scope, existing);

        entity.Id = id;
        await ExpenseAuthorizationRules.EnsureProposedExpenseValidAsync(_db, scope, entity);

        // 批次生成费用单的审计不可变：留痕列只能由分摊批次服务端写入，编辑不得改写 / 清空。
        entity.AllocationBatchNo = existing.AllocationBatchNo;
        entity.AllocationSourceExpenseId = existing.AllocationSourceExpenseId;
        entity.AllocationSourceExpenseNo = existing.AllocationSourceExpenseNo;

        var result = await Service.UpdateAsync(entity);
        return Ok(ApiResponse<FinanceExpense>.Success(result, "更新成功"));
    }

    /// <summary>删除费用单（软删除）：删除之前复核持久化 CustomerId 的权威归属（越界 / 无主 fail closed）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = ExpenseRequestScope;
        var entity = await _db.FinanceExpenses.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw BusinessException.NotFound("费用单不存在或已删除");
        ExpenseAuthorizationRules.EnsureStoredExpenseScopeAllowed(scope, entity);
        return await base.Delete(id);
    }

    /// <summary>批量删除费用单（软删除）：任一行越界 / 无主即整体拒绝，绝不删除任何范围外行</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        var scope = ExpenseRequestScope;
        var rows = ids is null
            ? new List<FinanceExpense>()
            : await _db.FinanceExpenses.AsNoTracking()
                .Where(x => ids.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync();
        foreach (var row in rows)
            ExpenseAuthorizationRules.EnsureStoredExpenseScopeAllowed(scope, row);
        return await base.BatchDelete(ids ?? new List<long>());
    }

    /// <summary>新增费用单时忽略客户端提交的分摊批次留痕（批次号 / 来源费用只能由分摊批次服务端写入）</summary>
    private static void ClearClientSuppliedAllocationLineage(FinanceExpense entity)
    {
        entity.AllocationBatchNo = string.Empty;
        entity.AllocationSourceExpenseId = null;
        entity.AllocationSourceExpenseNo = string.Empty;
    }

    /// <summary>
    /// 分摊上下文（**只读**）：装柜清单与（含停用的）参与方、持久化装柜总量、
    /// 该柜费用单的可分摊资格与留痕分类、现存有效 / 已作废批次、支持的方法与余差规则。
    /// </summary>
    [HttpGet("allocation-context")]
    public async Task<IActionResult> GetAllocationContext([FromQuery] long loadingListId)
    {
        var context = await ContainerExpenseAllocationService.GetContextAsync(
            _db, loadingListId, ExpenseRequestScope);
        return Ok(ApiResponse<ContainerExpenseAllocationContextDto>.Success(context, "已返回分摊上下文（只读）"));
    }

    /// <summary>
    /// 分摊预览（**只读，不写库**）：返回参与方、方法、基数种类与来源、基数值、比例与分摊金额，
    /// 以及余差归属与边界声明。预览与生成共用同一计算口径，预览所示即生成结果。
    /// </summary>
    [HttpPost("allocation-preview")]
    public async Task<IActionResult> AllocationPreview([FromBody] ContainerExpenseAllocationRequest request)
    {
        var preview = await ContainerExpenseAllocationService.PreviewAsync(
            _db, request, ExpenseRequestScope);
        return Ok(ApiResponse<ContainerExpenseAllocationPreviewDto>.Success(preview, "分摊预览完成（未写库）"));
    }

    /// <summary>
    /// 生成分摊批次（**事务性**）：一次请求内写入批次 + 逐行留痕 + 既有费用单行；
    /// 同一「来源费用 + 装柜清单」已有有效批次时拒绝重复生成（不区分方法），失败不留部分行。
    /// </summary>
    [HttpPost("allocation-generate")]
    public async Task<IActionResult> AllocationGenerate([FromBody] ContainerExpenseAllocationRequest request)
    {
        var result = await ContainerExpenseAllocationService.GenerateAsync(
            _db, request, ExpenseRequestScope);
        return Ok(ApiResponse<ContainerExpenseAllocationGenerateResultDto>.Success(
            result, $"已生成分摊批次 {result.BatchNo}（{result.LineCount} 条费用单）"));
    }

    /// <summary>分摊批次台账（分页，可按来源费用 / 装柜清单 / 状态 / 关键字过滤；含已作废历史）</summary>
    [HttpGet("allocation-batches")]
    public async Task<IActionResult> GetAllocationBatches([FromQuery] ContainerExpenseAllocationBatchQuery query)
    {
        var result = await ContainerExpenseAllocationService.ListBatchesAsync(
            _db, query, ExpenseRequestScope);
        return Ok(ApiResponse<PagedResult<ContainerExpenseAllocationBatchDto>>.Success(result));
    }

    /// <summary>单个分摊批次台账（含逐行留痕，只读）</summary>
    [HttpGet("allocation-batches/{batchId:long}")]
    public async Task<IActionResult> GetAllocationBatch(long batchId)
    {
        var batch = await ContainerExpenseAllocationService.GetBatchAsync(
            _db, batchId, ExpenseRequestScope);
        return Ok(ApiResponse<ContainerExpenseAllocationBatchDto>.Success(batch));
    }

    /// <summary>
    /// 作废分摊批次（更正路径）：只改批次状态并记录作废原因 —— 保留批次与分摊行历史，
    /// 不删除 / 不改写已生成的费用单行与来源费用，也不产生任何收付款 / 记账动作。
    /// </summary>
    [HttpPost("allocation-batches/{batchId:long}/void")]
    public async Task<IActionResult> VoidAllocationBatch(
        long batchId, [FromBody] ContainerExpenseAllocationVoidRequest? request)
    {
        var batch = await ContainerExpenseAllocationService.VoidAsync(
            _db, batchId, request?.Reason, ExpenseRequestScope);
        return Ok(ApiResponse<ContainerExpenseAllocationBatchDto>.Success(
            batch, $"分摊批次 {batch.BatchNo} 已作废（历史与逐行留痕保留）"));
    }

    /// <summary>
    /// 传统「拼柜分摊」请求（预览 / 生成共用）的授权与写入校验：在任何重复检查、单号生成与写入之前调用。
    /// 明细客户必须逐个落在当前账号权威范围内（受限账号 fail closed，不泄露范围外客户）；写入路径还必须指向
    /// 真实启用客户，且币种受支持、总额按既有存储精度取整后为正、汇率取整后为正。
    /// </summary>
    private async Task EnsureLegacyAllocateRequestValidAsync(ExpenseAllocateRequest req, bool write)
    {
        ArgumentNullException.ThrowIfNull(req);

        var scope = ExpenseRequestScope;
        var details = req.Details ?? new List<ExpenseAllocateDetail>();

        ExpenseAuthorizationRules.NormalizeCurrencyStrict(req.Currency);

        if (write)
        {
            ExpenseAuthorizationRules.NormalizeAmount(req.TotalAmount);
            ExpenseAuthorizationRules.NormalizeRate(req.ExchangeRate);

            // 逐行先复核权威归属（受限账号对无归属明细 fail closed），再复核客户真实启用。
            foreach (var detail in details)
                ExpenseAuthorizationRules.EnsureCustomerInScope(scope, detail.CustomerId);

            await ExpenseAuthorizationRules.EnsureCustomersValidAsync(
                _db, scope, details.Select(d => d.CustomerId ?? 0).ToList());
            return;
        }

        foreach (var detail in details)
            ExpenseAuthorizationRules.EnsureCustomerInScope(scope, detail.CustomerId);
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

