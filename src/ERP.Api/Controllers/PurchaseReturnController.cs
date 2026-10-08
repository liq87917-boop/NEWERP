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
/// 采购退货控制器（ERP-009，EF 主子表；ERP-358 显式来源与可退容量护栏）
/// 业务规则：
/// 1) 可关联来源采购入库单（<c>SourceStockInId</c> / <c>No</c>）做追溯，也允许无来源直接退货；
///    显式链接必须构成权威来源（入库单存在、未删除、已审核、供应商一致、入库仓库与退货出库仓库一致、
///    来源单号快照一致、商品 / 单位可折算为基础单位），否则在创建 / 修改 / 提交 / 审核时 fail closed 拒绝，
///    且不消耗单据号、不做任何写入；未链接（null）的历史退货保持显式「无来源」，绝不按单号文本猜测链接；
/// 2) 审核后货物退出仓库：库存减少，出库成本优先取明细成本 → 来源入库流水成本 → 当前加权平均成本，
///    实际采用的成本会回填到明细并持久化到库存流水（估价可追溯）；
/// 3) 审核时按商品逐行比较「已审核退货（重复商品行按基础单位合计） + 本次退货 ≤ 已审核入库数量」，
///    单位不一致 / 数量为负等损坏证据一律拒绝，绝不静默钳制；销审即释放可退容量；
/// 4) 仅已提交可审核、仅已审核可销审；销审按流水冲销（红字流水），重复动作一律拒绝；
/// 5) 全部端点重新校验实时身份 / 账号状态 / 既有「采购退货」菜单授权；创建 / 修改 / 提交 / 审核额外校验
///    来源入库单链接的上游采购订单归属客户数据范围（仅适用时）；
///    修改 / 提交 / 审核 / 销审 / 取消 / 删除共用「来源入库单行 → 退货单行」确定性锁序 + 可串行化事务。
/// </summary>
[Route("api/inventory/purchase-returns")]
public class PurchaseReturnController : DocumentControllerBase<PurchaseReturn>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public PurchaseReturnController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>
    /// 实时授权（fail closed）：校验当前身份 / 账号状态 / 既有「采购退货」菜单授权。
    /// 复用既有权限模型，不新增用户授权，也不把空身份当作管理员。
    /// </summary>
    private Task AuthorizeAsync(CancellationToken ct = default)
        => PurchaseReturnSourceRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId(), ct);

    /// <summary>
    /// 上游客户数据范围（ERP-097 唯一权威口径，仅适用时）：来源采购入库单链接的采购订单若存在归属客户，
    /// 该归属客户必须落在当前账号可见范围内，否则按「不存在」拒绝（不泄露归属）。
    /// </summary>
    private Task EnsureUpstreamScopeAsync(StockIn? receipt)
        => PurchaseReturnSourceRules.EnsureUpstreamCustomerScopeAsync(Db, CurrentUserId(), receipt);

    /// <summary>
    /// 对退货单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：把同单并发的「修改 / 提交 / 审核 / 销审 / 取消 / 删除」
    /// 串行化在同一事务内。返回 <c>false</c> 表示单据行不存在（按业务「不存在」拒绝）。
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由后续读取判定）。</para>
    /// </summary>
    private async Task<bool> LockReturnRowAsync(long returnId)
    {
        if (!PurchaseReturnSourceRules.IsRelationalProvider(Db)) return true;

        var ids = await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.PurchaseReturns WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", returnId)
            .ToListAsync();
        return ids.Count > 0;
    }

    /// <summary>
    /// 对来源采购入库单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：把<b>同一来源入库单</b>上的并发退货审核 / 销审
    /// 串行化在同一事务内，后到者能看到先到者已提交的累计退货数量。
    /// <para>调用顺序固定为「来源入库单行 → 退货单行」，与确定性锁序一致；未链接（null）或非关系型提供程序跳过。</para>
    /// </summary>
    private async Task LockSourceReceiptAsync(long? sourceStockInId)
    {
        if (sourceStockInId is not > 0) return;
        if (!PurchaseReturnSourceRules.IsRelationalProvider(Db)) return;

        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.StockIns WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                sourceStockInId.Value)
            .ToListAsync();
    }

    /// <summary>
    /// 事务回滚后丢弃变更跟踪器中的半成品变更：关系型后端由事务回滚保证，内存库等无事务提供程序
    /// 也能回到失败前状态（库存 / 流水 / 状态全部不变），绝不残留部分写入。
    /// </summary>
    private void DiscardTrackedChanges()
    {
        if (Db is DbContext context) context.ChangeTracker.Clear();
    }

    /// <summary>分页查询（keyword 匹配单号 / 供应商 / 来源入库单号 / 退货原因）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] long? warehouseId, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        await AuthorizeAsync();
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (warehouseId.HasValue) source = source.Where(o => o.WarehouseId == warehouseId.Value);
        if (start.HasValue) source = source.Where(o => o.ReturnDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.ReturnDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.ReturnNo.Contains(kw) || o.SupplierName.Contains(kw)
                                       || o.SourceStockInNo.Contains(kw) || o.ReturnReason.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<PurchaseReturn>>.Success(new PagedResult<PurchaseReturn>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        }));
    }

    /// <summary>查询详情（含明细）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await AuthorizeAsync();
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购退货单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<PurchaseReturn>.Success(entity));
    }

    /// <summary>该单据的库存流水（含红字冲销流水）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await AuthorizeAsync();
        await GetOrThrowAsync(id, "采购退货单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.PurchaseReturnType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建（单号由字轨生成；金额与合计由后端复核；来源链接校验先于单号生成）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PurchaseReturn entity)
    {
        // 授权 + 来源链接 + 上游客户范围校验先于单号生成：被拒绝方绝不消耗单据号、绝不写库。
        await AuthorizeAsync();
        entity.Id = 0;
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        entity.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
        ResetDetailIds(entity);
        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Normalize(entity);
        var link = await PurchaseReturnSourceRules.ValidateLinkAsync(Db, entity);
        await EnsureUpstreamScopeAsync(link?.Receipt);

        if (string.IsNullOrWhiteSpace(entity.ReturnNo))
            entity.ReturnNo = await _noService.GenerateAsync(DocumentType.PurchaseReturn);
        // 显式链接回填权威来源单号；未关联（null）保留显式文本，绝不按单号文本猜测链接。
        entity.SourceStockInNo = link is null ? entity.SourceStockInNo : link.Receipt.StockInNo;
        Normalize(entity);   // 单号生成后回填明细冗余单号
        Db.PurchaseReturns.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ReturnNo }, "采购退货单创建成功"));
    }

    /// <summary>修改（仅待提交可改；明细整体替换；与提交 / 审核共用同一把退货单行锁）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] PurchaseReturn entity)
    {
        await AuthorizeAsync();
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockReturnRowAsync(id))
                throw BusinessException.NotFound("采购退货单不存在");
            await AuthorizeAsync();

            var existing = await Db.PurchaseReturns.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购退货单不存在");
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改（已审核请先销审）");

            entity.ReturnNo = existing.ReturnNo;
            entity.Id = id;
            ResetDetailIds(entity);
            await StockUnitConversion.NormalizeAsync(Db, entity.Details);
            Normalize(entity);
            var link = await PurchaseReturnSourceRules.ValidateLinkAsync(Db, entity);
            await EnsureUpstreamScopeAsync(link?.Receipt);

            existing.ReturnDate = entity.ReturnDate;
            existing.SupplierId = entity.SupplierId;
            existing.SupplierName = entity.SupplierName;
            existing.WarehouseId = entity.WarehouseId;
            existing.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
            existing.SourceStockInId = entity.SourceStockInId;
            existing.SourceStockInNo = link is null ? entity.SourceStockInNo : link.Receipt.StockInNo;
            existing.ReturnReason = entity.ReturnReason;
            existing.Remark = entity.Remark;

            Db.PurchaseReturnDetails.RemoveRange(existing.Details);
            existing.Details = entity.Details;
            existing.TotalQuantity = entity.TotalQuantity;
            existing.TotalAmount = entity.TotalAmount;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            DiscardTrackedChanges();
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "采购退货单更新成功"));
    }

    /// <summary>
    /// 审核：先取来源采购入库单行锁与退货单行锁（UPDLOCK/HOLDLOCK）+ 可串行化事务，
    /// 再在锁内重读权威状态、校验来源链接与「累计退货 ≤ 已审核入库数量」，随后把退货出库、库存流水、
    /// 成本与单据状态包在同一事务内恰好一次提交；任一步失败整体回滚，库存 / 流水 / 状态全部不变。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        await AuthorizeAsync();
        // 事务前无锁预读仅为取得上游来源 Id（避免在可串行化事务内持有额外共享范围锁）；
        // 随后在锁内重读并复核，来源在窗口内变化时补锁新的来源单。
        var probe = await Db.PurchaseReturns.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.SourceStockInId })
            .FirstOrDefaultAsync() ?? throw BusinessException.NotFound("采购退货单不存在");
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockSourceReceiptAsync(probe.SourceStockInId);
            if (!await LockReturnRowAsync(id))
                throw BusinessException.NotFound("采购退货单不存在");
            await AuthorizeAsync();

            var entity = await Db.PurchaseReturns.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购退货单不存在");
            if (entity.SourceStockInId != probe.SourceStockInId)
                await LockSourceReceiptAsync(entity.SourceStockInId);
            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("仅已提交的采购退货单可审核");
            if (entity.WarehouseId <= 0)
                throw BusinessException.InvalidParameter("退货出库仓库必须填写");

            await StockUnitConversion.NormalizeAsync(Db, entity.Details);
            Normalize(entity);
            var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
            if (details.Count == 0)
                throw BusinessException.RuleConflict("采购退货单无明细，不能审核");
            if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.PurchaseReturnType, entity.Id) > 0)
                throw BusinessException.RuleConflict("该采购退货单已产生库存流水，不能重复审核");

            // 来源权威性 + 累计可退容量：在锁内校验（并发退货已串行化），失败即回滚且不写任何库存。
            var link = await PurchaseReturnSourceRules.ValidateApprovalAsync(Db, entity);
            await EnsureUpstreamScopeAsync(link?.Receipt);
            entity.SourceStockInNo = link is null ? entity.SourceStockInNo : link.Receipt.StockInNo;

            foreach (var d in details)
            {
                if (d.Quantity <= 0)
                    throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的退货数量必须大于 0");

                var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
                d.ProductCode = product.Code;
                d.ProductName = product.Name;
                d.Spec = product.Spec;
                d.Unit = product.Unit;

                // 成本口径：明细指定成本 → 来源采购入库流水成本 → 当前加权平均成本（由服务兜底）
                var sourceCost = d.UnitCost > 0
                    ? 0m
                    : await _inventory.ResolveSourceCostAsync(InventoryDocumentHelper.StockInType,
                        entity.SourceStockInId ?? 0, d.ProductId);
                var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                    InventoryDocumentHelper.PurchaseReturnType, entity.Id, entity.ReturnNo,
                    InventoryMovementType.PurchaseReturn, entity.WarehouseId, d.ProductId, d.ProductCode, d.ProductName,
                    d.Spec, d.Unit, entity.ReturnDate,
                    string.IsNullOrWhiteSpace(entity.SourceStockInNo)
                        ? "采购退货出库"
                        : $"采购退货出库（来源入库单 {entity.SourceStockInNo}）");

                var movement = await _inventory.DecreaseAsync(context, d.Quantity,
                    d.UnitCost > 0 ? d.UnitCost : sourceCost);
                d.UnitCost = movement.UnitCost;      // 回填实际出库成本，便于对账与复核
            }

            entity.TotalQuantity = details.Sum(d => d.Quantity);
            entity.TotalAmount = InventoryService.RoundAmount(details.Sum(d => d.Amount));
            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            DiscardTrackedChanges();
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "采购退货单已审核，退货已出库"));
    }

    /// <summary>
    /// 销审：与审核共用同一把「来源入库单行 → 退货单行」锁，按既有红字流水冲销退货出库；
    /// 销审即释放该单据占用的可退容量（仅已审核退货计入累计）。冲销失败时整体回滚，余额 / 流水 / 状态全部不变。
    /// </summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        await AuthorizeAsync();
        // 事务前无锁预读仅为取得上游来源 Id；随后在锁内重读并复核。
        var probe = await Db.PurchaseReturns.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.SourceStockInId })
            .FirstOrDefaultAsync() ?? throw BusinessException.NotFound("采购退货单不存在");
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockSourceReceiptAsync(probe.SourceStockInId);
            if (!await LockReturnRowAsync(id))
                throw BusinessException.NotFound("采购退货单不存在");
            await AuthorizeAsync();

            var entity = await Db.PurchaseReturns.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购退货单不存在");
            if (entity.SourceStockInId != probe.SourceStockInId)
                await LockSourceReceiptAsync(entity.SourceStockInId);
            if (GetStatus(entity) != DocumentStatus.Approved)
                throw BusinessException.RuleConflict("仅已审核的采购退货单可销审");

            await _inventory.ReverseAsync(InventoryDocumentHelper.PurchaseReturnType, entity.Id, "采购退货单销审冲销");
            SetStatus(entity, DocumentStatus.Pending);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            DiscardTrackedChanges();
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "已销审，退货出库已冲销"));
    }

    /// <summary>取消（已审核需先销审；与审核 / 销审共用同一把退货单行锁）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await AuthorizeAsync();
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockReturnRowAsync(id))
                throw BusinessException.NotFound("采购退货单不存在");
            await AuthorizeAsync();

            var entity = await GetOrThrowAsync(id, "采购退货单不存在");
            if (GetStatus(entity) == DocumentStatus.Approved)
                throw BusinessException.RuleConflict("已审核的采购退货单不能取消，请先销审");

            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            DiscardTrackedChanges();
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "已取消"));
    }

    /// <summary>提交（仅待提交可提交；重新校验来源链接；与修改 / 审核共用同一把退货单行锁）</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await AuthorizeAsync();
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockReturnRowAsync(id))
                throw BusinessException.NotFound("采购退货单不存在");
            await AuthorizeAsync();

            var entity = await Db.PurchaseReturns.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购退货单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            // 提交前重新解析权威来源：来源失效 / 快照冲突时拒绝，不消耗任何库存或单据号。
            await StockUnitConversion.NormalizeAsync(Db, entity.Details);
            Normalize(entity);
            var link = await PurchaseReturnSourceRules.ValidateLinkAsync(Db, entity);
            await EnsureUpstreamScopeAsync(link?.Receipt);
            entity.SourceStockInNo = link is null ? entity.SourceStockInNo : link.Receipt.StockInNo;

            SetStatus(entity, DocumentStatus.Submitted);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            DiscardTrackedChanges();
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "提交成功"));
    }

    /// <summary>删除（软删除，仅待提交可删；与修改 / 提交 / 审核共用同一把退货单行锁）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await AuthorizeAsync();
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockReturnRowAsync(id))
                throw BusinessException.NotFound("采购退货单不存在");
            await AuthorizeAsync();

            var entity = await GetOrThrowAsync(id, "采购退货单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");

            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            DiscardTrackedChanges();
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    private async Task<string> ResolveWarehouseNameAsync(long warehouseId, string fallback)
        => string.IsNullOrWhiteSpace(fallback)
            ? await InventoryDocumentHelper.WarehouseNameAsync(Db, warehouseId)
            : fallback;

    /// <summary>行号 / 金额 / 合计统一整理（后端复核，防止前端篡改合计）</summary>
    /// <remarks>只做「计算」，不动明细主键：审核时明细是已跟踪的实体，重置主键会触发 EF 的 key 修改异常。</remarks>
    private static void Normalize(PurchaseReturn entity)
    {
        if (entity.ReturnDate == default) entity.ReturnDate = DateTime.Today;
        var line = 0;
        decimal totalQuantity = 0, totalAmount = 0;
        foreach (var d in entity.Details)
        {
            d.PurchaseReturnId = entity.Id;
            d.ReturnNo = entity.ReturnNo;
            d.SortNo = ++line;
            d.Amount = InventoryService.RoundAmount(d.Quantity * d.UnitPrice);
            totalQuantity += d.Quantity;
            totalAmount += d.Amount;
        }
        entity.TotalQuantity = totalQuantity;
        entity.TotalAmount = InventoryService.RoundAmount(totalAmount);
    }

    /// <summary>明细整体替换时强制作为新行插入（请求体可能带回历史明细 Id，直接保存会被 EF 当成已存在行）</summary>
    private static void ResetDetailIds(PurchaseReturn entity)
    {
        foreach (var d in entity.Details) d.Id = 0;
    }
}

