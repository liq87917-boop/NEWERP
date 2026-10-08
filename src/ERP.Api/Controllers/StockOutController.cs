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
        var scope = await ResolveScopeAsync();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
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
        if (!(await ResolveScopeAsync()).AllowsCustomer(entity.CustomerId))
            throw BusinessException.NotFound("出库单不存在");
        return Ok(ApiResponse<StockOut>.Success(entity));
    }

    /// <summary>该单据的库存流水（ERP-025，含红字冲销流水，按发生顺序）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        var entity = await GetOrThrowAsync(id, "出库单不存在");
        await EnsureCustomerScopeAsync(entity.CustomerId);
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockOutType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockOut entity)
    {
        await EnsureCustomerScopeAsync(entity.CustomerId);
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

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockOut entity)
    {
        var existing = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        await EnsureCustomerScopeAsync(existing.CustomerId);
        await EnsureCustomerScopeAsync(entity.CustomerId);
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.StockOutDate = entity.StockOutDate;
        existing.SalesOrderId = entity.SalesOrderId;
        existing.CustomerId = entity.CustomerId;
        existing.WarehouseId = entity.WarehouseId;
        existing.Remark = entity.Remark;

        Db.StockOutDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.StockOutId = id;
            d.CreatedAt = DateTime.Now;
        }
        existing.Details = entity.Details;
        await StockUnitConversion.NormalizeAsync(Db, existing.Details);
        Calculate(existing);
        await StockOutOrderFulfillmentRules.ValidateLinkAsync(Db, existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "出库单更新成功"));
    }

    /// <summary>审核（按基础单位校验库存、扣减并写入库存流水，恰好一次）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.StockOuts.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("出库单不存在");
        await EnsureCustomerScopeAsync(entity.CustomerId);
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);

        // ERP-343：把「累计已审核出库 ≤ 来源订单授权数量」的判定与库存写入放进同一个可串行化事务，
        // 并对来源订单行加 UPDLOCK/HOLDLOCK 串行化同单并发审核；任一步失败整体回滚，库存、流水、状态都不变。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
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
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var probe = await Db.StockOuts.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.CustomerId })
            .FirstOrDefaultAsync() ?? throw BusinessException.NotFound("出库单不存在");
        await EnsureCustomerScopeAsync(probe.CustomerId);

        // 确定性锁序：来源出库单行（与销售退货审核 / 销审同一把锁）→ 退货单行（只读判定）。任一步失败整体回滚。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockSourceShipmentRowAsync(id);

            var entity = await Db.StockOuts.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("出库单不存在");
            await EnsureCustomerScopeAsync(entity.CustomerId);

            var status = GetStatus(entity);
            if (status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("出库单已取消");

            // 在锁内、在任何库存冲销 / 状态变更之前判定退货引用：被拒绝时不留任何半成品写入。
            await ReturnSourceCancellationRules.EnsureNoEffectiveApprovedSalesReturnAsync(Db, id);

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
    private Task LockSourceShipmentRowAsync(long stockOutId)
    {
        if (!ReturnSourceCancellationRules.IsRelationalProvider(Db)) return Task.CompletedTask;
        return Db.Database
            .SqlQueryRaw<long>(ReturnSourceCancellationRules.LockStockOutRowSql, stockOutId)
            .ToListAsync();
    }

    /// <summary>
    /// 冲销已审核出库：优先按库存流水生成红字流水（ERP-025，与 ERP-009 四类单据同一口径，
    /// 重复冲销由流水的 <c>IsReversed</c> 标记兜住）；本次改造前审核的历史单据没有流水，
    /// 退化为按明细基础单位原路恢复，避免库存台账漂移。
    /// </summary>
    private async Task EnsureCustomerScopeAsync(long customerId)
    {
        if (!(await ResolveScopeAsync()).AllowsCustomer(customerId))
            throw BusinessException.NotFound("出库单不存在");
    }

    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var entity = await GetOrThrowAsync(id, "出库单不存在");
        await EnsureCustomerScopeAsync(entity.CustomerId);
        return await base.Submit(id);
    }

    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var entity = await GetOrThrowAsync(id, "出库单不存在");
        await EnsureCustomerScopeAsync(entity.CustomerId);
        return await base.Delete(id);
    }

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
