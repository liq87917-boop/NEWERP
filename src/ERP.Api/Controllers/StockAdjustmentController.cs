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
/// 库存盘点/调整控制器（ERP-009，EF 主子表；ERP-355 过账 / 冲销原子化）
/// 业务规则：
/// 1) 草稿 → 提交 → 审核；<b>仅已提交</b>可审核，<b>仅已审核</b>可销审，重复审核/销审一律拒绝；
/// 2) 审核按「实盘 - 账面」差异调整库存（盘盈增加、盘亏减少），每条差异写一笔可审计库存流水；
/// 3) 销审按流水逐笔生成红字流水并还原库存；差异入库已被后续业务占用时拒绝销审（不出现负库存）；
/// 4) 已审核单据不能取消、不能删除，必须先销审；
/// 5) 审核要求<b>已验证的账面数量基线</b>（账面数量必须等于权威当前库存）；基线过期一律拒绝，
///    要求操作员在待提交状态重新盘点 / 修改，绝不用客户端账面差额静默过账出虚构差异；
///    审核 / 销审 / 取消 / 提交 / 删除 / 修改共用同一把盘点单行锁（UPDLOCK/HOLDLOCK），
///    受影响库存行按确定性顺序（仓库 Id 升序 → 商品 Id 升序）加锁，全部写入包在同一可串行化事务内。
/// </summary>
[Route("api/inventory/stock-adjustments")]
public class StockAdjustmentController : DocumentControllerBase<StockAdjustment>
{
    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockAdjustmentController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
        : base(db)
    {
        _noService = noService;
        _inventory = inventory;
    }

    /// <summary>
    /// 实时授权（fail closed）：校验当前身份 / 账号状态 / 既有「库存查询」菜单授权。
    /// 复用既有权限模型，不新增用户授权，也不把空身份当作管理员。
    /// </summary>
    private Task AuthorizeAsync(CancellationToken ct = default)
        => StockAdjustmentPostingRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId(), ct);

    /// <summary>
    /// 对盘点单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：把同单并发的「审核 / 销审 / 取消 / 提交 / 删除 / 修改」
    /// 串行化在同一事务内。返回 <c>false</c> 表示单据行不存在（按业务「不存在」拒绝）。
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由后续读取判定）。</para>
    /// </summary>
    private async Task<bool> LockAdjustmentRowAsync(long adjustmentId)
    {
        if (!StockAdjustmentPostingRules.IsRelationalProvider(Db)) return true;

        var ids = await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.StockAdjustments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", adjustmentId)
            .ToListAsync();
        return ids.Count > 0;
    }

    /// <summary>
    /// 对盘点仓库受影响库存行按**确定性顺序**（仓库 Id 升序 → 商品 Id 升序，顺序由既有
    /// <see cref="InventoryService.OrderStockIdentities"/> 给出）加 <c>UPDLOCK/HOLDLOCK</c>：
    /// 多明细 / 多单据共用同一顺序，不会形成环形等待而死锁。
    /// <para>内存库等非关系型提供程序无行锁语义，等价无操作。</para>
    /// </summary>
    private async Task LockAdjustmentStocksAsync(long warehouseId, IEnumerable<StockAdjustmentDetail> details)
    {
        if (!StockAdjustmentPostingRules.IsRelationalProvider(Db)) return;

        var keys = InventoryService.OrderStockIdentities(
            StockAdjustmentPostingRules.StockKeys(warehouseId, details));
        foreach (var key in keys)
        {
            await Db.Database
                .SqlQueryRaw<long>(
                    "SELECT Id FROM db_owner.Stocks WITH (UPDLOCK, HOLDLOCK) " +
                    "WHERE WarehouseId = {0} AND ProductId = {1}",
                    key.WarehouseId, key.ProductId)
                .ToListAsync();
        }
    }

    /// <summary>
    /// 事务回滚后丢弃变更跟踪器中的半成品变更：关系型后端由事务回滚保证，内存库等无事务提供程序
    /// 也能回到失败前状态（库存 / 流水 / 状态全部不变），绝不残留部分写入。
    /// </summary>
    private void DiscardTrackedChanges()
    {
        if (Db is DbContext context) context.ChangeTracker.Clear();
    }

    /// <summary>分页查询（keyword 匹配单号 / 仓库名 / 备注）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] long? warehouseId, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        await AuthorizeAsync();
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (warehouseId.HasValue) source = source.Where(o => o.WarehouseId == warehouseId.Value);
        if (start.HasValue) source = source.Where(o => o.AdjustmentDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.AdjustmentDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.AdjustmentNo.Contains(kw) || o.WarehouseName.Contains(kw)
                                       || o.Remark.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockAdjustment>>.Success(new PagedResult<StockAdjustment>
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
            ?? throw BusinessException.NotFound("盘点单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<StockAdjustment>.Success(entity));
    }

    /// <summary>该单据的库存流水（含红字冲销流水，按发生顺序）</summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        await AuthorizeAsync();
        await GetOrThrowAsync(id, "盘点单不存在");
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockAdjustmentType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    /// <summary>创建（单号由字轨生成；差异数量与差异金额由后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockAdjustment entity)
    {
        // 授权先于单号生成：被拒绝方绝不消耗单据号
        await AuthorizeAsync();
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.AdjustmentNo))
            entity.AdjustmentNo = await _noService.GenerateAsync(DocumentType.StockAdjustment);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        StockAdjustmentPostingRules.EnsureLineShape(entity.Details);
        await StockAdjustmentPostingRules.EnsureLiveMasterDataAsync(Db, entity.WarehouseId, entity.Details);
        entity.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
        ResetDetailIds(entity);
        Normalize(entity);
        Db.StockAdjustments.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.AdjustmentNo }, "盘点单创建成功"));
    }

    /// <summary>修改（仅待提交可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockAdjustment entity)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockAdjustmentRowAsync(id))
                throw BusinessException.NotFound("盘点单不存在");
            await AuthorizeAsync();

            var existing = await Db.StockAdjustments.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("盘点单不存在");
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改（已审核请先销审）");

            StockAdjustmentPostingRules.EnsureLineShape(entity.Details);
            await StockAdjustmentPostingRules.EnsureLiveMasterDataAsync(Db, entity.WarehouseId, entity.Details);

            existing.AdjustmentDate = entity.AdjustmentDate;
            existing.WarehouseId = entity.WarehouseId;
            existing.WarehouseName = await ResolveWarehouseNameAsync(entity.WarehouseId, entity.WarehouseName);
            existing.AdjustType = entity.AdjustType;
            existing.Remark = entity.Remark;

            Db.StockAdjustmentDetails.RemoveRange(existing.Details);
            entity.AdjustmentNo = existing.AdjustmentNo;
            entity.Id = id;
            ResetDetailIds(entity);
            Normalize(entity);
            existing.Details = entity.Details;
            existing.TotalDiffQuantity = entity.TotalDiffQuantity;
            existing.TotalDiffAmount = entity.TotalDiffAmount;
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

        return Ok(ApiResponse<object>.Success(null, "盘点单更新成功"));
    }

    /// <summary>
    /// 审核：先取盘点单行锁（UPDLOCK/HOLDLOCK）+ 可串行化事务，再在锁内重读权威状态、校验明细与在用主数据、
    /// 按确定性顺序锁定受影响库存行，核验账面基线（必须等于权威当前库存），随后把全部差异调整、
    /// 库存流水与单据状态包在同一事务内恰好一次提交；任一步失败整体回滚。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockAdjustmentRowAsync(id))
                throw BusinessException.NotFound("盘点单不存在");

            await AuthorizeAsync();

            var entity = await Db.StockAdjustments.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("盘点单不存在");
            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("仅已提交的盘点单可审核");

            var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
            if (details.Count == 0)
                throw BusinessException.RuleConflict("盘点单无明细，不能审核");

            // 幂等护栏：已存在有效流水说明库存已调整过，禁止重复审核（防止重复改库存）
            if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockAdjustmentType, entity.Id) > 0)
                throw BusinessException.RuleConflict("该盘点单已产生库存流水，不能重复审核");

            // 明细形状（非负数量 / 成本、商品不重复）与在用主数据（仓库 / 商品）校验：先于任何库存写入
            StockAdjustmentPostingRules.EnsureLineShape(details);
            await StockAdjustmentPostingRules.EnsureLiveMasterDataAsync(Db, entity.WarehouseId, details);

            // 确定性顺序锁定受影响库存行，再核验账面基线（账面数量必须等于权威当前库存）
            await LockAdjustmentStocksAsync(entity.WarehouseId, details);
            foreach (var d in details)
            {
                var current = await _inventory.GetCurrentQuantityAsync(entity.WarehouseId, d.ProductId!.Value);
                StockAdjustmentPostingRules.EnsureFreshBookQuantity(d.ProductName, d.BookQuantity, current);
            }

            Normalize(entity);
            foreach (var d in details.Where(d => d.DiffQuantity != 0))
            {
                var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
                d.ProductCode = product.Code;
                d.ProductName = product.Name;
                d.Spec = product.Spec;
                d.Unit = product.Unit;

                var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                    InventoryDocumentHelper.StockAdjustmentType, entity.Id, entity.AdjustmentNo,
                    InventoryMovementType.Adjustment, entity.WarehouseId, d.ProductId, d.ProductCode, d.ProductName,
                    d.Spec, d.Unit, entity.AdjustmentDate,
                    $"盘点差异：账面 {d.BookQuantity}，实盘 {d.ActualQuantity}");

                if (d.DiffQuantity > 0)
                    await _inventory.IncreaseAsync(context, d.DiffQuantity, d.UnitCost);
                else
                    await _inventory.DecreaseAsync(context, -d.DiffQuantity, d.UnitCost);
            }

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

        return Ok(ApiResponse<object>.Success(null, "盘点单已审核，库存已按差异调整"));
    }

    /// <summary>
    /// 销审：与审核共用同一把盘点单行锁，先按确定性顺序锁定受影响库存行，再按既有红字流水冲销库存，
    /// 冲销失败（货物已被后续业务占用）时整体回滚——余额 / 流水 / 状态全部不变，绝不留下半截冲销。
    /// </summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockAdjustmentRowAsync(id))
                throw BusinessException.NotFound("盘点单不存在");
            await AuthorizeAsync();

            var entity = await Db.StockAdjustments.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("盘点单不存在");
            if (GetStatus(entity) != DocumentStatus.Approved)
                throw BusinessException.RuleConflict("仅已审核的盘点单可销审");

            var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
            await LockAdjustmentStocksAsync(entity.WarehouseId, details);

            await _inventory.ReverseAsync(InventoryDocumentHelper.StockAdjustmentType, entity.Id, "盘点单销审冲销");
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

        return Ok(ApiResponse<object>.Success(null, "已销审，库存已冲销还原"));
    }

    /// <summary>取消（已审核需先销审；与审核 / 销审共用同一把单据行锁）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockAdjustmentRowAsync(id))
                throw BusinessException.NotFound("盘点单不存在");
            await AuthorizeAsync();

            var entity = await Db.StockAdjustments
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("盘点单不存在");
            if (GetStatus(entity) == DocumentStatus.Approved)
                throw BusinessException.RuleConflict("已审核的盘点单不能取消，请先销审");

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

    /// <summary>提交（仅待提交可提交；与审核 / 销审 / 取消共用同一把单据行锁）</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockAdjustmentRowAsync(id))
                throw BusinessException.NotFound("盘点单不存在");
            await AuthorizeAsync();

            var entity = await Db.StockAdjustments
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("盘点单不存在");
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

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

    /// <summary>删除（软删除，仅待提交可删；与审核 / 销审 / 取消 / 提交共用同一把单据行锁）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await LockAdjustmentRowAsync(id))
                throw BusinessException.NotFound("盘点单不存在");
            await AuthorizeAsync();

            var entity = await Db.StockAdjustments
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("盘点单不存在");
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

    /// <summary>行号 / 差异数量 / 差异金额 / 合计统一整理（后端复核，防止前端篡改）</summary>
    /// <remarks>只做「计算」，不动明细主键：审核时明细是已跟踪的实体，重置主键会触发 EF 的 key 修改异常。</remarks>
    private static void Normalize(StockAdjustment entity)
    {
        if (entity.AdjustmentDate == default) entity.AdjustmentDate = DateTime.Today;
        var line = 0;
        decimal totalQuantity = 0, totalAmount = 0;
        foreach (var d in entity.Details)
        {
            d.StockAdjustmentId = entity.Id;
            d.AdjustmentNo = entity.AdjustmentNo;
            d.SortNo = ++line;
            d.DiffQuantity = d.ActualQuantity - d.BookQuantity;
            d.DiffAmount = InventoryService.RoundAmount(d.DiffQuantity * d.UnitCost);
            totalQuantity += d.DiffQuantity;
            totalAmount += d.DiffAmount;
        }
        entity.TotalDiffQuantity = totalQuantity;
        entity.TotalDiffAmount = InventoryService.RoundAmount(totalAmount);
    }

    /// <summary>明细整体替换时强制作为新行插入（请求体可能带回历史明细 Id，直接保存会被 EF 当成已存在行）</summary>
    private static void ResetDetailIds(StockAdjustment entity)
    {
        foreach (var d in entity.Details) d.Id = 0;
    }
}

