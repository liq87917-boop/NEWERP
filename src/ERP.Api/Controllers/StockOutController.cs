using ERP.Application.Common;
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
        var entity = await GetOrThrowAsync(id, "出库单不存在");
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockOutType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockOut entity)
    {
        // ERP-370：授权与客户范围校验先于单号生成——被拒绝方绝不消耗单据号，也绝不落任何明细。
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        entity.Id = 0;
        entity.StockOutNo = await _noService.GenerateAsync(DocumentType.StockOut);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);
        await StockOutOrderFulfillmentRules.ValidateLinkAsync(Db, entity);
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
        var userId = CurrentStockOutUserId();

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
            entity.Id = 0;
            foreach (var d in entity.Details)
            {
                d.Id = 0;
                d.StockOutId = id;
                d.CreatedAt = DateTime.Now;
            }
            await StockUnitConversion.NormalizeAsync(Db, entity.Details);
            await StockOutOrderFulfillmentRules.ValidateLinkAsync(Db, entity);

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
        var entity = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

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
    private long? CurrentStockOutUserId()
        => ControllerContext.HttpContext is null ? null : CurrentUserId();

    /// <summary>
    /// 提交：与本出库单行锁、可串行化事务同口径，锁内先复核实时授权（身份 / 菜单 / 客户范围），
    /// 再走基类状态流转；被拒绝时状态 / 库存 / 流水保持原样。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockStockOutRowAsync(id);

            var entity = await GetOrThrowAsync(id, "出库单不存在");
            await StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync(Db, CurrentStockOutUserId(), entity.CustomerId);

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
