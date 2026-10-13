using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售出库控制器（审核通过后扣减库存）
/// <para>ERP-025：审核 / 取消统一经 <see cref="IInventoryService"/> 记账，库存数量、库存金额与库存流水
/// （<see cref="StockMovement"/>）同步落地，跨单据可追溯；本次改造前已审核的历史单据没有流水，
/// 取消时退化为按明细基础单位原路恢复，保证既有库存台账不被改写。</para>
/// <para>ERP-370：读取（列表 / 详情 / 流水）/ 创建 / 修改 / 提交 / 审核 / 取消 / 删除每一路由都先做实时授权
/// （既有登录身份 + 账号启用状态 + 既有「销售出库」菜单 + <see cref="SalespersonDataScopeService"/> 客户数据范围），
/// 列表在计数 / 分页之前把客户范围下推到数据库；修改 / 提交 / 删除 / 审核与取消共用<b>同一把</b>本出库单行锁
/// （<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务，使已审核库存 / 流水不会被并发改单穿透。数量护栏仍为 ERP-343，
/// 取消护栏仍为 ERP-359 / ERP-367。</para>
/// <para>ERP-376：新增只读、有界的销售订单来源候选 / 详情端点（既有「销售出库」+「销售订单」菜单 + ERP-097 权威客户范围），
/// 供业务表单显式选择来源并回填权威客户与可发货商品行（ERP-343 剩余可发数量口径）；候选选择绝不臆造价格，
/// 也绝不改动 ERP-343 发货容量口径、ERP-359 退货护栏与 ERP-367 装柜取消护栏。表单「未选择来源」（数字 0）
/// 归一为 <c>null</c>，保持历史无来源出库单语义（重开时可见为未链接）。</para>
/// <para>ERP-478：既有「销售出库」菜单改为打开<b>规范 EF 主子表模块</b>（<c>api/stock-outs</c>，规范 <c>Id</c> 口径），
/// 不再映射到已由 ERP-404 门禁整体停用（fail closed）的旧存储过程族；旧族 <c>Oid</c> 读侧 / 导出保持只读可用，
/// 规范 <c>Id</c> 与旧 <c>Oid</c> 之间<b>不存在</b>任何映射，也绝不启用任何旧写适配器。
/// 新增 / 修改在实时授权与客户范围、来源归属（含基础单位折算与 ERP-343 累计容量）校验之后、<b>单号预约与任何写入之前</b>
/// 完成全部校验：主键 / 出库单号 / 状态 / 删除态 / 时间戳与明细身份一律由服务端权威覆盖，
/// 被拒绝的请求零表头 · 明细 · 来源 · 状态变化且不消耗单据号（ERP-370 口径）。</para>
/// <para>ERP-502：新增 / 修改在单位折算之后、单号预约与任何写入之前再执行共享草稿护栏
/// （<see cref="StockOutAuthorizationRules.EnsureDraftValidAsync"/>）：仓库 Id 必须为正数且仓库真实在用（存在 / 未删除 / 启用），
/// 每一行有效明细的数量必须为正且与毛重 / 体积 / 表头合计一并落在实际存储精度 <c>DECIMAL(18,2)</c> 内（含溢出），
/// 商品必须存在 / 未删除 / 启用；空草稿头仍可保存，提交 / 审核要求至少一行有效明细，重复商品行按累计数量语义保留。
/// 该护栏是<b>纯判定 + 有界只读查询</b>，不新增表 / 列 / 索引 / 菜单 / 权限，也不改动既有行锁、可串行化事务、
/// ERP-343 累计容量口径与 <see cref="StockMovement"/> 流水写入。</para>
/// </summary>
[Route("api/stock-outs")]
public class StockOutController : DocumentControllerBase<StockOut>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockOutController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        // ERP-370：身份 / 账号状态 / 既有「销售出库」菜单 / 权威客户范围先于任何计数与分页（数据库侧范围下推）。
        var source = await StockOutAuthorizationRules.ApplyScopeAsync(
            Db, Set.AsNoTracking().Where(o => !o.IsDeleted), CurrentStockOutUserId());
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.StockOutNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockOut>>.Success(
            new PagedResult<StockOut> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        // ERP-370（2026-10-13 验收恢复）：身份 / 账号状态 / 既有「销售出库」菜单闸门先于任何单据读取 fail closed ——
        // 未知 Id 与存在但越权 Id 对缺失 / 禁用 / 已删除 / 无菜单账号给出完全相同的受控未认证 / 权限不足错误。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        // ERP-370：详情与列表同一口径（身份 / 菜单 / 客户范围），范围外按不存在拒绝（不泄露 Id / 单号）。
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        return Ok(ApiResponse<StockOut>.Success(entity));
    }

    /// <summary>该单据的库存流水（ERP-025，含红字冲销流水，按发生顺序）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        // ERP-370（2026-10-13 验收恢复）：统一实时闸门先于任何出库单 / 流水读取，与详情完全同口径。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        var entity = await GetOrThrowAsync(id, "出库单不存在");
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockOutType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    // ==================== ERP-376：来源候选 / 详情（只读、有界） ====================

    /// <summary>
    /// 可发货来源候选（只读、有界）：返回当前账号客户数据范围内、客户匹配且关键字命中的
    /// 「已审核、未删除」销售订单可发货商品行，按「来源销售订单 + 商品」聚合给出剩余可发数量（ERP-343 口径）。
    /// <para>授权口径：既有「销售出库」菜单 + 既有「销售订单」菜单（实时校验，撤销后立即收敛）+
    /// ERP-097 权威客户范围（在计数 / 取数<b>之前</b>下推到数据库）；不新增用户授权，
    /// 也不提供匿名 / 管理员降级；重复 / 歧义明细、单位未知、已发货满额的候选显式标记不可用，绝不猜容量。</para>
    /// </summary>
    [HttpGet("source-candidates")]
    public async Task<IActionResult> GetSourceCandidates([FromQuery] long? customerId,
        [FromQuery] string? keyword, [FromQuery] int take = 0)
    {
        var scope = await StockOutAuthorizationRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        var scoped = ApplySalesOrderScope(scope);

        var candidates = (await StockOutOrderFulfillmentRules.QuerySourceCandidatesAsync(
            Db, scoped, customerId, keyword, take)).ToList();
        return Ok(ApiResponse<List<StockOutSourceCandidateDto>>.Success(
            candidates, "已返回可发货的已审核销售订单来源（只读：重复 / 歧义明细、单位未知、已发货满额标记为不可用，绝不猜容量）"));
    }

    /// <summary>
    /// 来源详情（只读、有界）：返回一张权威来源销售订单的表头 + 逐商品剩余可发行，供业务表单在显式选择后
    /// 回填权威来源 Id / 单号 / 客户与可发货商品行。范围外 / 不存在 / 已删除按「不存在」拒绝，
    /// 未审核 / 已取消 / 已驳回按冲突拒绝；任一失败都不改写已保存的来源链接。
    /// </summary>
    [HttpGet("source-candidates/{salesOrderId:long}")]
    public async Task<IActionResult> GetSourceCandidateDetail(long salesOrderId)
    {
        var scope = await StockOutAuthorizationRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        var scoped = ApplySalesOrderScope(scope);

        var detail = await StockOutOrderFulfillmentRules.ResolveSourceDetailAsync(Db, scoped, salesOrderId);
        return Ok(ApiResponse<StockOutSourceDetailDto>.Success(
            detail, "已返回来源销售订单的可发货详情（只读：不可用行带原因，绝不猜价格）"));
    }

    /// <summary>候选 / 详情的权威客户范围下推（计数 / 取数之前）：复用 ERP-097 唯一权威口径，绝不内存过滤。</summary>
    private IQueryable<SalesOrder> ApplySalesOrderScope(SalespersonDataScope scope)
        => SalespersonDataScopeService.FilterByCustomer(Db.SalesOrders.AsNoTracking(), scope, o => o.CustomerId);

    /// <summary>
    /// 创建（ERP-478：省略 / 空串 / 伪造出库单号照常进入本动作，单号与身份由服务端权威覆盖；
    /// 全部授权 · 来源 · 单位折算 · 累计容量校验都在<b>单号预约与任何写入之前</b>完成，
    /// 被拒绝的请求零写入、零占号）。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockOut entity)
    {
        // ERP-370（2026-10-13 验收恢复）：统一实时身份 / 菜单闸门先于任何请求字节处理与写入（缺失 / 禁用 / 无菜单立即受控拒绝）。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        // ERP-376 / ERP-478：业务表单「未选择来源」以数字 0 表示（数字字段留空 → 0），
        // 或按 ERP-478 的声明式可空引用（nullable）直接提交 null；两者都归一为 null，
        // 保持历史「无来源」出库单语义（null），绝不把 0 当成无效显式链接拒绝。
        NormalizeSalesOrderLink(entity);
        entity.Details ??= new List<StockOutDetail>();

        // ERP-370：授权与客户范围校验先于单号生成——被拒绝方绝不消耗单据号，也绝不落任何明细。
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);
        await StockOutOrderFulfillmentRules.ValidateLinkAsync(Db, entity);
        // ERP-502：草稿护栏（形状 + 实际存储精度 + 在用仓库 / 商品）在单位折算与来源校验之后、
        // 单号预约与任何写入之前执行；被拒绝的请求零单号消耗、零单据 / 明细 / 库存 / 流水变化。
        await StockOutAuthorizationRules.EnsureDraftValidAsync(Db, entity.WarehouseId, entity.Details);

        // ERP-478：主键 / 单号 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖（客户端提交值不参与持久化），
        // 明细身份（主键 / 归属单据 / 删除态）同口径重置，绝不采信客户端注入的明细 Id / IsDeleted / 表头身份。
        var now = DateTime.Now;
        entity.Id = 0;
        ApplyServerOwnedDetailIdentity(entity.Details, 0, now);
        entity.StockOutNo = await _noService.GenerateAsync(DocumentType.StockOut);
        entity.Status = DocumentStatus.Pending;
        entity.IsDeleted = false;
        entity.CreatedAt = now;
        entity.UpdatedAt = null;
        Db.StockOuts.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.StockOutNo }, "出库单创建成功"));
    }

    /// <summary>
    /// 更新（仅待提交）：与来源销售订单 / 本出库单共用确定性行锁（<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务，
    /// 使「已审核出库 / 库存台账」不会被并发改单穿透。
    /// <para>ERP-370：事务内先复核实时授权（身份 / 菜单 / 客户范围），并同时校验「已存单据客户」与「请求客户」；
    /// 再把「请求内容」的来源链接与明细折算全部校验通过后才改写已存单据 —— 任一步失败整体回滚，
    /// 单据 / 明细 / 状态 / 库存 / 流水保持原样。</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockOut entity)
    {
        // ERP-376 / ERP-478：与创建同一口径——表单「未选择来源」（0 或 null）归一为 null，保持历史无来源语义；
        // 失败的修改在事务内整体回滚（单据 / 明细 / 库存 / 流水保持原样）。
        NormalizeSalesOrderLink(entity);
        entity.Details ??= new List<StockOutDetail>();

        var userId = CurrentStockOutUserId();

        // ERP-370（2026-10-13 验收恢复）：统一实时身份 / 既有「销售出库」菜单闸门先于行锁与任何单据读取 ——
        // 未知 Id 与存在但越权 Id 对缺失 / 禁用 / 已删除 / 无菜单账号给出完全相同的受控拒绝。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, userId);

        // 确定性锁序：来源出库单行（与销售退货审核 / 销审 / 取消 / 装柜审核同一把锁）→ 只读判定。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockStockOutRowAsync(id);

            var existing = await Db.StockOuts.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("出库单不存在");

            // 授权先于任何赋值：已存单据客户越界或请求客户越界都拒绝，原单据保持不变。
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, userId, existing.CustomerId);
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, userId, entity.CustomerId);

            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            // 先把「请求内容」规范化并校验来源链接（ERP-343），失败时已存单据 / 明细 / 库存 / 流水保持原样。
            // ERP-478：明细身份（主键 / 归属单据 / 删除态 / 时间戳）一律由服务端重置；
            // 单据号 / 状态 / 删除态由服务端保持（客户端提交值不参与持久化）。
            entity.Id = 0;
            ApplyServerOwnedDetailIdentity(entity.Details, id, DateTime.Now);
            await StockUnitConversion.NormalizeAsync(Db, entity.Details);
            await StockOutOrderFulfillmentRules.ValidateLinkAsync(Db, entity);
            // ERP-502：与创建同一草稿护栏，在明细整体替换 / 任何写入之前收敛非法仓库、非法数量与失效商品，
            // 被拒绝方不消耗新单号、不改动既有单据与明细逐字段。
            await StockOutAuthorizationRules.EnsureDraftValidAsync(Db, entity.WarehouseId, entity.Details);

            existing.StockOutDate = entity.StockOutDate;
            existing.SalesOrderId = entity.SalesOrderId;
            existing.CustomerId = entity.CustomerId;
            existing.WarehouseId = entity.WarehouseId;
            existing.Remark = entity.Remark;

            Db.StockOutDetails.RemoveRange(existing.Details);
            existing.Details = entity.Details;
            Calculate(existing);
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "出库单更新成功"));
    }

    /// <summary>
    /// 审核（按基础单位校验库存、扣减并写入库存流水，恰好一次）。
    /// <para>ERP-343：把「累计已审核出库 ≤ 来源订单授权数量」的判定与库存写入放进同一个可串行化事务，
    /// 并对来源订单行加 UPDLOCK/HOLDLOCK 串行化同单并发审核；任一步失败整体回滚，库存、流水、状态都不变。</para>
    /// <para>ERP-370：同一事务内先取本出库单行锁（与取消 / 装柜审核 / 销售退货审核同一把锁），
    /// 并在锁内复核实时授权（身份 / 菜单 / 客户范围）——读取之后被撤销授权立即收敛，且改单无法穿透审核。</para>
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        // ERP-370（2026-10-13 验收恢复）：身份 / 菜单闸门先于任何单据读取与行锁；锁内再复核实时客户范围。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        var entity = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
        // ERP-502：审核要求至少一行有效明细（空草稿头可保存但不可提交 / 审核）。
        StockOutAuthorizationRules.EnsureHasEffectiveLine(entity.Details, "审核");

        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            // 确定性锁序：本出库单行 → 来源销售订单行（两者都不与其反序共存，避免死锁）。
            await LockStockOutRowAsync(entity.Id);

            // 锁内复核实时授权：授权在读取之后被撤销（撤菜单 / 禁用 / 改派客户）立即收敛。
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);

            await AcquireOrderApprovalLockAsync(entity.SalesOrderId);

            // 幂等护栏放进锁内：同单并发审核时，后到者在拿到锁后能看到先到者已产生的流水，从而被拒绝。
            if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockOutType, entity.Id) > 0)
                throw BusinessException.RuleConflict("该出库单已产生库存流水，不能重复审核");

            await StockOutOrderFulfillmentRules.ValidateApprovalAsync(Db, entity);

            foreach (var d in entity.Details.Where(d => !d.IsDeleted))
            {
                // 零数量明细（旧版页面允许留空行）不产生库存变动；负数属于数据错误，直接拒绝
                if (d.Quantity < 0)
                    throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的出库数量不能为负数");
                if (d.Quantity == 0) continue;

                var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
                var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                    InventoryDocumentHelper.StockOutType, entity.Id, entity.StockOutNo,
                    InventoryMovementType.SalesOut, entity.WarehouseId, d.ProductId, product.Code, product.Name,
                    product.Spec, product.Unit, entity.StockOutDate, MovementRemark(entity));

                // 出库成本基准：出库单明细没有成本列 → 由 InventoryService 按当前加权平均成本核减
                // （ERP-009 移动加权平均口径）；库存不足时服务抛业务异常，整单不落半截数据。
                await _inventory.DecreaseAsync(context, d.Quantity, 0m);
            }

            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "审核通过，库存已扣减并写入库存流水"));
    }

    /// <summary>
    /// 对来源销售订单行加更新锁（UPDLOCK, HOLDLOCK），把同单并发审核串行化在同一事务内。
    /// 未链接订单时无需加锁；非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquireOrderApprovalLockAsync(long? salesOrderId)
    {
        if (salesOrderId is not > 0) return;
        if (!Db.Database.IsRelational()) return;

        // 订单行不存在时无需加锁：ValidateApprovalAsync 会对显式链接 fail closed 抛异常，
        // 在取得任何库存写入前拒绝履约；未链接（null）则不加锁。
        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                salesOrderId.Value)
            .ToListAsync();
    }

    /// <summary>
    /// 取消：已审核单据按库存流水冲销（无流水的历史单据按基础单位原路恢复）。
    /// <para>ERP-359：与销售退货审核 / 销审共用<b>同一</b>把来源出库单行锁（<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务，
    /// 锁内先复核实时授权 / 客户范围，再确认没有仍然生效的已审核销售退货单显式引用本出库单（
    /// <see cref="ReturnSourceCancellationRules.EnsureNoEffectiveApprovedSalesReturnAsync"/>）；
    /// 存在时 fail closed 拒绝，单据 / 明细 / 状态 / 库存 / 流水全部保持原样，绝不先冲销再校验。</para>
    /// <para>ERP-367：同一把行锁与事务内追加「装柜物理出运证据」判定 —— 存在「已审核、未删除」装柜清单明细显式链接
    /// （<see cref="ContainerLoadingDetail.SourceStockOutDetailId"/>）到本出库单明细时同样 fail closed 拒绝并给出可执行的
    /// 装柜撤销要求（<see cref="LoadingStockOutLinkRules.EnsureNoEffectiveApprovedLoadingAsync"/>）；
    /// 与装柜审核共用同一把上游出库单行锁，故并发「装柜审核」与「来源取消」被串行化，只出现一种一致结果。</para>
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        // ERP-370（2026-10-13 验收恢复）：身份 / 菜单闸门先于任何探测读取与行锁（未知 Id 同口径受控拒绝）。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());
        var probe = await Db.StockOuts.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.CustomerId })
            .FirstOrDefaultAsync() ?? throw BusinessException.NotFound("出库单不存在");
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), probe.CustomerId);

        // 确定性锁序：来源出库单行（与销售退货审核 / 销审 / 装柜审核同一把锁）→ 退货单行 / 装柜明细行（只读判定）。
        // 任一步失败整体回滚，装柜历史证据与出库单据原样保留。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockSourceShipmentRowAsync(id);

            var entity = await Db.StockOuts.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("出库单不存在");
            // ERP-370：锁内再次复核实时授权（身份 / 菜单 / 客户范围），读取之后被撤销授权立即收敛。
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);

            var status = GetStatus(entity);
            if (status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("出库单已取消");

            // 在锁内、在任何库存冲销 / 状态变更之前判定引用：被拒绝时不留任何半成品写入。
            await ReturnSourceCancellationRules.EnsureNoEffectiveApprovedSalesReturnAsync(Db, id);
            await LoadingStockOutLinkRules.EnsureNoEffectiveApprovedLoadingAsync(Db, id);

            if (status == DocumentStatus.Approved)
                await ReverseStockAsync(entity);
            else if (status is not (DocumentStatus.Pending or DocumentStatus.Submitted))
                throw BusinessException.RuleConflict("当前状态不允许取消");
            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "已取消，库存已按基础单位恢复"));
    }

    /// <summary>
    /// 对来源销售出库单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>，与销售退货审核 / 销审共用
    /// <see cref="ReturnSourceCancellationRules.LockStockOutRowSql"/> 同一锁语句）：
    /// 把「取消来源出库单」与「销售退货审核 / 销审」串行化在同一可串行化事务内；非关系型提供程序跳过。
    /// </summary>
    private Task LockSourceShipmentRowAsync(long stockOutId) => LockStockOutRowAsync(stockOutId);

    /// <summary>
    /// 对本出库单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>，与取消 / 销售退货审核 / 销审 / 装柜审核共用<b>同一</b>把
    /// 来源出库单行锁）：把「审核」「修改 / 状态流转」与「来源取消 / 装柜引用」串行化在同一可串行化事务内，
    /// 已审核库存与流水不会被并发改单穿透；非关系型提供程序（内存库）无法执行表提示，跳过即可。
    /// </summary>
    private Task LockStockOutRowAsync(long stockOutId)
    {
        if (!ReturnSourceCancellationRules.IsRelationalProvider(Db)) return Task.CompletedTask;
        return Db.Database
            .SqlQueryRaw<long>(StockOutAuthorizationRules.LockStockOutRowSql, stockOutId)
            .ToListAsync();
    }

    /// <summary>当前登录用户 Id（缺失 / 非数字时返回 null，由授权规则 fail closed 拒绝，绝不猜测身份）</summary>
    // ERP-370：缺失身份 / 无请求上下文一律安全解析为 null → 受控未认证；绝无内部直调 / 测试兼容豁免或改名旁路。
    private long? CurrentStockOutUserId()
        => ControllerContext.HttpContext is null ? null : CurrentUserId();

    /// <summary>
    /// 提交：与本出库单行锁、可串行化事务同口径，锁内先复核实时授权（身份 / 菜单 / 客户范围），
    /// 再走基类状态流转；被拒绝时状态 / 库存 / 流水保持原样。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        // ERP-370（2026-10-13 验收恢复）：身份 / 菜单闸门先于行锁与任何单据读取。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockStockOutRowAsync(id);

            var entity = await Db.StockOuts.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("出库单不存在");
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
            // ERP-502：提交要求至少一行有效明细（空草稿头可保存但不可提交 / 审核）。
            StockOutAuthorizationRules.EnsureHasEffectiveLine(entity.Details, "提交");

            var result = await base.Submit(id);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 删除（软删除，仅待提交）：与本出库单行锁、可串行化事务同口径，锁内先复核实时授权，
    /// 被拒绝时不删除任何单据 / 明细，已审核库存与流水不受影响。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        // ERP-370（2026-10-13 验收恢复）：身份 / 菜单闸门先于行锁与任何单据读取。
        await StockOutAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentStockOutUserId());

        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockStockOutRowAsync(id);

            var entity = await GetOrThrowAsync(id, "出库单不存在");
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);

            var result = await base.Delete(id);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 冲销已审核出库：优先按库存流水生成红字流水（ERP-025，与 ERP-009 四类单据同一口径，
    /// 重复冲销由流水的 <c>IsReversed</c> 标记兜住）；本次改造前审核的历史单据没有流水，
    /// 退化为按明细基础单位原路恢复，避免库存台账漂移。
    /// </summary>
    private async Task ReverseStockAsync(StockOut entity)
    {
        var reversals = await _inventory.ReverseAsync(InventoryDocumentHelper.StockOutType, entity.Id,
            "销售出库单取消冲销");
        if (reversals.Count == 0)
            await RestoreLegacyStockAsync(entity);
    }

    /// <summary>
    /// ERP-376：把业务表单「未选择来源」的数字 0 归一为 <c>null</c>（历史「无来源」语义，重开时可见为未链接），
    /// 负数等非法值保持原样交由 <see cref="StockOutOrderFulfillmentRules.ValidateLinkAsync"/> fail closed 拒绝。
    /// </summary>
    private static void NormalizeSalesOrderLink(StockOut entity)
    {
        if (entity.SalesOrderId == 0) entity.SalesOrderId = null;
    }

    /// <summary>
    /// ERP-478：明细身份 / 归属单据 / 删除态 / 时间戳一律由服务端权威覆盖：客户端提交的明细主键一律置 0（自增），
    /// 归属单据取自服务端已知的单据 Id（新建为 0，由 EF 关系在保存时回填），删除态强制为「正常」，
    /// 时间戳以服务端时钟为准。绝不采信客户端注入的明细身份 / 删除态，也绝不臆造任何明细行。
    /// </summary>
    private static void ApplyServerOwnedDetailIdentity(IEnumerable<StockOutDetail> details, long stockOutId, DateTime now)
    {
        foreach (var d in details)
        {
            d.Id = 0;
            d.StockOutId = stockOutId;
            d.IsDeleted = false;
            d.CreatedAt = now;
            d.UpdatedAt = null;
        }
    }

    /// <summary>流水备注：带上来源销售订单 Id，便于按流水反查订单执行情况</summary>
    private static string MovementRemark(StockOut entity)
        => entity.SalesOrderId is > 0
            ? $"销售出库单审核出库（销售订单 Id {entity.SalesOrderId}）"
            : "销售出库单审核出库";

    private static void Calculate(StockOut entity)
    {
        entity.TotalQuantity = entity.Details.Sum(d => d.Quantity);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }

    /// <summary>
    /// 历史单据兜底（ERP-023 之前审核、没有库存流水的出库单）：按明细已持久化的基础单位数量原路恢复。
    /// 有流水的单据一律走 <see cref="ReverseStockAsync"/> 的红字冲销，库存金额由冲销流水精确还原。
    /// </summary>
    private async Task RestoreLegacyStockAsync(StockOut entity)
    {
        foreach (var detail in entity.Details)
        {
            var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId
                && s.ProductId == detail.ProductId);
            if (stock is null)
            {
                stock = new Stock
                {
                    WarehouseId = entity.WarehouseId, ProductId = detail.ProductId,
                    CreatedAt = DateTime.Now
                };
                Db.Stocks.Add(stock);
            }
            stock.Quantity += detail.Quantity;
            stock.AvailableQuantity += detail.Quantity;
            stock.UpdatedAt = DateTime.Now;
        }
    }
}
