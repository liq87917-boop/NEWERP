using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 费用单控制器（阶段 2 新增）
/// 维护：出口杂费（报关费 / 拖车费 / THC / 文件费 / 港杂费 / 仓储费 / 快递费 / 查验费…）
/// 支持：按整柜 / 拼柜 / 散货 / 订单 / 客户 归属，并按 体积 / 重量 / 金额 / 箱数 / 手工 分摊
/// 另提供：拼柜费用分摊（预览 + 生成），用于"一柜多客户，费用按体积或重量分摊"
/// <para><b>实时授权（ERP-385 / ERP-496）</b>：本控制器<b>每一个路由动作</b>在任何计数、行、金额、分摊基数
/// 读取或写入之前，都先经 <see cref="ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync"/> 解析<b>当前实时</b>
/// 启用身份、账号状态、既有「费用单」（<c>expense-bill</c>）菜单授权与 ERP-097 权威客户数据范围，
/// 并把解析出的<b>非空</b>范围下推到服务 / 数据库。</para>
/// <para><b>与请求形状无关（ERP-496）</b>：判定不依赖 <c>Request.Path</c>、请求形状或是否绑定 <c>HttpContext</c> ——
/// 原 HTTP 入口动作过滤器（<c>ExpenseRequestAuthorizationFilter</c>）与它的 <c>HttpContext.Items</c> 范围通道已
/// <b>彻底移除</b>：空路径 / 已赋值路径 / 未绑定任何请求上下文的纯进程内直调对同一身份给出完全相同的判定；
/// 缺失 / 为零 / 未知 / 已删除身份一律受控未认证，禁用账号 / 缺少或被撤销既有菜单一律权限不足；
/// 绝无匿名 / 管理员 / 环境 / 测试专用开关回退。契约见
/// <see cref="ExpenseAuthorizationRules.PathIndependenceText"/>。</para>
/// <para><b>标量实际列精度（ERP-512）</b>：费用 CRUD 的全部客户端可持久化标量（金额 / 汇率 / 折人民币金额 /
/// 税率 / 税额 / 分摊比例 / 分摊金额）在既有取整之后必须能在实际列精度（<c>DECIMAL(18,2)</c>）内表示；
/// 传统分摊与 ERP-042 批次的**派生**金额 / 比例 / 折人民币金额在单号生成与任何 <c>Db.Add</c> 之前预计算并校验，
/// 越界 / 乘法溢出即整批受控拒绝（零单号消耗、零已跟踪行），绝不静默截断 / 取整，也不改写任何历史。</para>
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
    public async Task<IActionResult> AllocatePreview([FromBody] ExpenseAllocateRequest req)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于任何读取（与请求路径 / 是否绑定 HttpContext 无关）。
        var scope = await EnsureExpenseAuthorizedAsync();

        // ERP-385：只读预览也必须先复核明细客户的权威范围（受限账号 fail closed，不泄露范围外客户）。
        await EnsureLegacyAllocateRequestValidAsync(req, scope, write: false);
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
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于任何校验、重复检查、单号生成与写入。
        var scope = await EnsureExpenseAuthorizedAsync();

        if (req.Details is null || req.Details.Count == 0)
            throw BusinessException.InvalidParameter("请至少填写一行分摊明细");

        // ERP-385：在重复检查、单号生成与任何写入之前完成范围 / 客户 / 币种 / 金额 / 汇率校验。
        await EnsureLegacyAllocateRequestValidAsync(req, scope, write: true);

        // ERP-386：传统分摊的「重复检查 + 单号保留 + 生成写入」必须在**同一原子事务**内完成，
        // 并在「模块级单号键行」（既有 expense-bill 菜单行）行锁下串行化 —— 锁键只取既有权威行，
        // 绝不把自由文本 RefNo / 柜号当作权威来源身份或锁键。
        await using var transaction = await ExpenseAllocationConcurrencyRules.BeginTransactionIfRelationalAsync(_db);
        await ExpenseAllocationConcurrencyRules.LockModuleNumberKeyAsync(_db);

        // 锁内复核实时授权 / 客户可用性 / 金额口径：陈旧权限或被停用客户一律拒绝，且不占用任何单号。
        // ERP-496：此处重新解析实时身份 / 菜单 / 权威范围（无缓存），锁内复核与请求形状完全无关。
        var lockedScope = await EnsureExpenseAuthorizedAsync();
        await EnsureLegacyAllocateRequestValidAsync(req, lockedScope, write: true);

        var expenseDate = (req.ExpenseDate ?? DateTime.Today).Date;

        // ERP-512：派生金额（分摊金额 / 分摊比例 / 折人民币金额）在**单号生成与任何 Db.Add 之前**全部预计算
        // 并校验到实际列精度（DECIMAL(18,2)）—— Calculate 是预览与生成共用的收敛边界。
        // 任一越界 / 乘法溢出即整批受控拒绝：绝不占用单号、绝不留下部分已跟踪行、绝不改写任何既有数据。
        var allocated = Calculate(req);
        var rate = req.ExchangeRate <= 0 ? 1m : req.ExchangeRate;
        var plannedAmountsCny = allocated
            .Select(item => ExpenseAuthorizationRules.DeriveCnyAmount(item.AllocatedAmount, req.Currency, rate))
            .ToList();

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
        for (var i = 0; i < allocated.Count; i++)
        {
            var item = allocated[i];
            _db.FinanceExpenses.Add(new FinanceExpense
            {
                ExpenseNo = $"{prefix}{seq:000}",
                ExpenseDate = expenseDate,
                ExpenseType = req.ExpenseType,
                Amount = item.AllocatedAmount,
                Currency = req.Currency,
                ExchangeRate = rate,
                AmountCny = plannedAmountsCny[i],
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

        // ERP-386：整套费用单 + 单号保留在同一事务内提交；任一步失败整体回滚（不留部分行、不占用单号）。
        if (transaction is not null) await transaction.CommitAsync();

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
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何计数与分页（数据库侧范围下推）。
        var scope = await EnsureExpenseAuthorizedAsync();
        var result = await Service.GetPagedAsync(query, ExpenseAuthorizationRules.ExpenseScopeFilter(scope));
        await ContainerExpenseAllocationService.AnnotateLineageAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<FinanceExpense>>.Success(result));
    }

    /// <summary>费用单全量（下拉用；按当前账号范围在数据库侧过滤后返回，只读）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何读取。
        var scope = await EnsureExpenseAuthorizedAsync();
        var result = await Service.GetAllAsync(ExpenseAuthorizationRules.ExpenseScopeFilter(scope));
        await ContainerExpenseAllocationService.AnnotateLineageAsync(_db, result);
        return Ok(ApiResponse<List<FinanceExpense>>.Success(result));
    }

    /// <summary>费用单详情：读取后复核持久化 CustomerId 的权威归属（受限账号 fail closed）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于任何行读取。
        var scope = await EnsureExpenseAuthorizedAsync();
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
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于任何字段校验与写入。
        var scope = await EnsureExpenseAuthorizedAsync();
        entity.Id = 0;
        await ExpenseAuthorizationRules.EnsureProposedExpenseValidAsync(_db, scope, entity);
        ClearClientSuppliedAllocationLineage(entity);
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<FinanceExpense>.Success(result, "新增成功"));
    }

    /// <summary>
    /// 修改费用单：先复核「已存储」行的权威归属，再校验「拟议」客户范围与金额 / 币种 / 汇率，
    /// 最后从已存储行恢复分摊批次留痕（批次号 / 来源费用）以保证批次生成行的审计不可被客户端改写或清空。
    /// <para>ERP-386：整段改动在同一原子事务内，并先取来源费用单行锁（与分摊生成同序），
    /// 因此「来源费用商业修改 vs 分摊批次生成」串行化；有效批次来源费用的权威分摊基数不得被改写。</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] FinanceExpense entity)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于行锁与任何行读取。
        var scope = await EnsureExpenseAuthorizedAsync();

        await using var transaction = await ExpenseAllocationConcurrencyRules.BeginTransactionIfRelationalAsync(_db);
        await ExpenseAllocationConcurrencyRules.LockSourceExpenseRowsAsync(_db, new[] { id });

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

        // ERP-386：有效分摊批次的来源费用，其权威分摊基数（金额 / 币种 / 汇率 / 归属客户 / 柜级身份）
        // 在批次有效期内保持不变；否则已生成的批次与费用单快照会变成陈旧证据（请先显式作废批次）。
        if (await ExpenseAllocationConcurrencyRules.HasEffectiveBatchForSourceAsync(_db, id))
            ExpenseAllocationConcurrencyRules.EnsureSourceAllocationBasisUnchanged(existing, entity);

        var result = await Service.UpdateAsync(entity);
        if (transaction is not null) await transaction.CommitAsync();
        return Ok(ApiResponse<FinanceExpense>.Success(result, "更新成功"));
    }

    /// <summary>
    /// 删除费用单（软删除）：删除之前复核持久化 CustomerId 的权威归属（越界 / 无主 fail closed）。
    /// <para>ERP-386：在同一原子事务内先取来源费用单行锁；有效分摊批次的来源费用在批次有效期内拒绝删除
    /// （先显式作废批次释放），因此不会出现「来源已删除但批次仍有效」的陈旧证据。</para>
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于行锁与任何行读取。
        var scope = await EnsureExpenseAuthorizedAsync();

        await using var transaction = await ExpenseAllocationConcurrencyRules.BeginTransactionIfRelationalAsync(_db);
        await ExpenseAllocationConcurrencyRules.LockSourceExpenseRowsAsync(_db, new[] { id });

        var entity = await _db.FinanceExpenses.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw BusinessException.NotFound("费用单不存在或已删除");
        ExpenseAuthorizationRules.EnsureStoredExpenseScopeAllowed(scope, entity);
        await ExpenseAllocationConcurrencyRules.EnsureSourceDeletableAsync(_db, new[] { id });

        var result = await base.Delete(id);
        if (transaction is not null) await transaction.CommitAsync();
        return result;
    }

    /// <summary>
    /// 批量删除费用单（软删除）：任一行越界 / 无主 / 为有效分摊批次来源费用即整体拒绝，
    /// 绝不删除任何范围外行或有效批次来源行；全部在同一原子事务内按 Id 升序加行锁完成。
    /// </summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单先于行锁与任何行读取。
        var scope = await EnsureExpenseAuthorizedAsync();
        var lockIds = ExpenseAllocationConcurrencyRules.MergeRowLockIds(ids);

        await using var transaction = await ExpenseAllocationConcurrencyRules.BeginTransactionIfRelationalAsync(_db);
        await ExpenseAllocationConcurrencyRules.LockSourceExpenseRowsAsync(_db, lockIds);

        var rows = lockIds.Count == 0
            ? new List<FinanceExpense>()
            : await _db.FinanceExpenses.AsNoTracking()
                .Where(x => lockIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync();
        foreach (var row in rows)
            ExpenseAuthorizationRules.EnsureStoredExpenseScopeAllowed(scope, row);

        // ERP-386：整批删除前先复核来源证据（任一行是有效批次来源费用即整批拒绝，不删除任何行）。
        await ExpenseAllocationConcurrencyRules.EnsureSourceDeletableAsync(_db, rows.Select(r => r.Id));

        var result = await base.BatchDelete(ids ?? new List<long>());
        if (transaction is not null) await transaction.CommitAsync();
        return result;
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
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何参与方 / 来源 / 批次读取。
        var scope = await EnsureExpenseAuthorizedAsync();
        var context = await ContainerExpenseAllocationService.GetContextAsync(
            _db, loadingListId, scope);
        return Ok(ApiResponse<ContainerExpenseAllocationContextDto>.Success(context, "已返回分摊上下文（只读）"));
    }

    /// <summary>
    /// 分摊预览（**只读，不写库**）：返回参与方、方法、基数种类与来源、基数值、比例与分摊金额，
    /// 以及余差归属与边界声明。预览与生成共用同一计算口径，预览所示即生成结果。
    /// </summary>
    [HttpPost("allocation-preview")]
    public async Task<IActionResult> AllocationPreview([FromBody] ContainerExpenseAllocationRequest request)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何计算。
        var scope = await EnsureExpenseAuthorizedAsync();
        var preview = await ContainerExpenseAllocationService.PreviewAsync(
            _db, request, scope);
        return Ok(ApiResponse<ContainerExpenseAllocationPreviewDto>.Success(preview, "分摊预览完成（未写库）"));
    }

    /// <summary>
    /// 生成分摊批次（**事务性**）：一次请求内写入批次 + 逐行留痕 + 既有费用单行；
    /// 同一「来源费用 + 装柜清单」已有有效批次时拒绝重复生成（不区分方法），失败不留部分行。
    /// </summary>
    [HttpPost("allocation-generate")]
    public async Task<IActionResult> AllocationGenerate([FromBody] ContainerExpenseAllocationRequest request)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何行锁与写入。
        var scope = await EnsureExpenseAuthorizedAsync();
        var result = await ContainerExpenseAllocationService.GenerateAsync(
            _db, request, scope);
        return Ok(ApiResponse<ContainerExpenseAllocationGenerateResultDto>.Success(
            result, $"已生成分摊批次 {result.BatchNo}（{result.LineCount} 条费用单）"));
    }

    /// <summary>分摊批次台账（分页，可按来源费用 / 装柜清单 / 状态 / 关键字过滤；含已作废历史）</summary>
    [HttpGet("allocation-batches")]
    public async Task<IActionResult> GetAllocationBatches([FromQuery] ContainerExpenseAllocationBatchQuery query)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何计数与分页。
        var scope = await EnsureExpenseAuthorizedAsync();
        var result = await ContainerExpenseAllocationService.ListBatchesAsync(
            _db, query, scope);
        return Ok(ApiResponse<PagedResult<ContainerExpenseAllocationBatchDto>>.Success(result));
    }

    /// <summary>单个分摊批次台账（含逐行留痕，只读）</summary>
    [HttpGet("allocation-batches/{batchId:long}")]
    public async Task<IActionResult> GetAllocationBatch(long batchId)
    {
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何行读取。
        var scope = await EnsureExpenseAuthorizedAsync();
        var batch = await ContainerExpenseAllocationService.GetBatchAsync(
            _db, batchId, scope);
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
        // ERP-496：实时身份 / 账号状态 / 既有「费用单」菜单 + 权威范围先于任何状态改写。
        var scope = await EnsureExpenseAuthorizedAsync();
        var batch = await ContainerExpenseAllocationService.VoidAsync(
            _db, batchId, request?.Reason, scope);
        return Ok(ApiResponse<ContainerExpenseAllocationBatchDto>.Success(
            batch, $"分摊批次 {batch.BatchNo} 已作废（历史与逐行留痕保留）"));
    }

    /// <summary>
    /// 本控制器全部路由的实时授权（ERP-496，<b>无条件执行</b>）：身份只来自已认证请求主体
    /// （<c>ClaimTypes.NameIdentifier</c>），每次调用都重新查询实时启用身份 / 账号状态 / 既有「费用单」
    /// （<c>expense-bill</c>）菜单授权与 ERP-097 权威客户数据范围，返回<b>非空</b>范围供同一动作复用。
    /// <para>缺少请求上下文 / 缺少身份声明 / 非数字 / 非正数一律安全解析为 <c>null</c> 并由规则返回受控未认证，
    /// 与 <c>Request.Path</c> 是否赋值、控制器是否绑定 <c>HttpContext</c>、是否经过 MVC 过滤器管线完全无关；
    /// 绝无匿名 / 管理员 / 环境 / 测试专用开关回退。契约见
    /// <see cref="ExpenseAuthorizationRules.PathIndependenceText"/>。</para>
    /// </summary>
    private Task<SalespersonDataScope> EnsureExpenseAuthorizedAsync()
        => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());

    /// <summary>
    /// 当前登录账号 Id（身份只来自已认证请求主体）：缺少请求上下文 / 缺少身份声明 / 非数字 / 非正数一律
    /// 安全解析为 <c>null</c>，由 <see cref="ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync"/> 返回受控未认证，
    /// 绝不猜测身份、绝不回退为管理员或任何测试专用放行。
    /// </summary>
    private long? CurrentUserId()
        => long.TryParse(
            ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            && id > 0
            ? id
            : null;

    /// <summary>
    /// 传统「拼柜分摊」请求（预览 / 生成共用）的授权与写入校验：在任何重复检查、单号生成与写入之前调用。
    /// 明细客户必须逐个落在当前账号权威范围内（受限账号 fail closed，不泄露范围外客户）；写入路径还必须指向
    /// 真实启用客户，且币种受支持、总额按既有存储精度取整后为正、汇率取整后为正。
    /// <para>ERP-496：权威范围由调用方（动作内 <c>EnsureExpenseAuthorizedAsync</c>）实时解析后传入，<b>必填且非空</b>。</para>
    /// </summary>
    private async Task EnsureLegacyAllocateRequestValidAsync(
        ExpenseAllocateRequest req, SalespersonDataScope scope, bool write)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(scope);

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

    /// <summary>
    /// 分摊计算核心（预览与生成**共用**的派生边界）：按指定基数计算各客户权重、比例与分摊金额（末行补齐四舍五入差额）。
    /// <para>ERP-512：分摊总额与每一行的派生标量（比例 / 分摊金额）在这里按实际列精度 <c>DECIMAL(18,2)</c> 收敛 ——
    /// 越界、极大权重导致的乘法 / 求和溢出一律受控拒绝，绝不静默截断 / 取整，也不以 <see cref="OverflowException"/> 崩溃。
    /// 预览与生成因此共享完全一致的边界（预览不会展示生成阶段必然被拒绝的结果）。</para>
    /// </summary>
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
        decimal sum;
        try
        {
            sum = weights.Sum();
        }
        catch (OverflowException)
        {
            throw BusinessException.InvalidParameter(
                "分摊权重（体积 / 重量 / 箱数 / 金额）合计超出十进制可表示范围：请检查明细取值（绝不静默截断或取整）");
        }

        // 分摊总额同样先收敛到实际列精度（生成的行金额 / 折人民币金额都从它派生）。
        ExpenseAuthorizationRules.EnsureScalarRepresentable(
            req.TotalAmount, ExpenseAuthorizationRules.AmountOverflowLabel);

        // 权重全部为 0 时退化为"平均分摊"，避免除零并给出可用结果
        var items = new List<ExpenseAllocateResultItem>();
        decimal assigned = 0;
        for (var i = 0; i < details.Count; i++)
        {
            var d = details[i];
            var ratio = sum > 0 ? Math.Round(weights[i] / sum * 100, 4) : Math.Round(100m / details.Count, 4);
            var amount = i == details.Count - 1
                ? Math.Round(req.TotalAmount - assigned, 2)                       // 末行补齐差额
                : Math.Round(
                    FinancePersistedAmountRules.SafeMultiply(
                        req.TotalAmount, ratio / 100m, ExpenseAuthorizationRules.AllocatedAmountOverflowLabel),
                    2);
            assigned += amount;
            ExpenseAuthorizationRules.EnsureAllocateResultRepresentable(ratio, amount);

            // 折人民币派生（只校验、不写库）：与生成阶段共用同一收敛边界 ——
            // 预览因此不会展示生成阶段必然被拒绝（越界 / 乘法溢出）的结果。
            ExpenseAuthorizationRules.DeriveCnyAmount(amount, req.Currency, req.ExchangeRate);

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

