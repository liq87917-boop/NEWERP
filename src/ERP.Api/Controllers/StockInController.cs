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
/// 采购入库控制器（审核通过后增加库存）
/// <para>ERP-025：审核 / 取消统一经 <see cref="IInventoryService"/> 记账，库存数量与库存流水
/// （<see cref="StockMovement"/>）同步落地，跨单据可追溯；本次改造前已审核的历史单据没有流水，
/// 取消时退化为按明细基础单位原路冲回，保证既有库存台账不被改写。</para>
/// <para>ERP-033：审核时按入库单持久化的采购订单链接（<see cref="StockIn.PurchaseOrderId"/>）解析
/// 基础单位成本并带入库存流水，口径见 <see cref="PurchaseStockInCostSource"/>；语义不明确时保持
/// ERP-025 的移动加权平均兜底，不臆造成本单价、不改写历史流水数量与金额。</para>
/// <para>ERP-375：新增只读、有界的采购订单来源候选 / 详情端点（既有「采购入库」+「采购订单」菜单 + ERP-371 权威客户范围），
/// 供业务表单显式选择来源并回填权威供应商与可收货商品行；候选选择绝不臆造成本，也绝不改动 ERP-033 成本口径与
/// ERP-342 收货容量口径。表单「未选择来源」（数字 0 或声明式可空引用提交 <c>null</c>）归一为 <c>null</c>，
/// 保持历史无来源入库单语义。</para>
/// <para>ERP-477：既有「采购入库」菜单改为打开<b>规范 EF 主子表模块</b>（<c>api/stock-ins</c>，规范 <c>Id</c> 口径），
/// 不再映射到已由 ERP-404 门禁整体停用（fail closed）的旧存储过程族；旧族 <c>Oid</c> 读侧 / 导出保持只读可用，
/// 规范 <c>Id</c> 与旧 <c>Oid</c> 之间<b>不存在</b>任何映射，也绝不启用任何旧写适配器。
/// 新增 / 修改在实时授权、供应商 / 仓库可用性与来源归属校验（含基础单位折算与 ERP-342 累计容量）之后、
/// <b>单号预约与任何写入之前</b>完成全部校验：主键 / 入库单号 / 状态 / 删除态 / 时间戳与明细身份一律由服务端权威覆盖，
/// 被拒绝的请求零表头 · 明细 · 来源 · 状态变化且不消耗单据号（ERP-352 口径）。</para>
/// <para>ERP-352：每一条读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 流水入口都<b>先</b>执行统一实时闸门
/// （<see cref="StockInAuthorizationRules.EnsureMenuAuthorizedAsync"/>：身份 / 账号状态 / 业务员映射 / 既有「采购入库」菜单），
/// 闸门<b>先于任何入库单 / 来源 / 流水字节读取与任何行锁</b>；已存单据的可见性再按来源采购订单归属客户复核，
/// 范围外 / 已删除 / 归属未知与「不存在」返回<b>同一</b>非披露错误（<see cref="StockInAuthorizationRules.NotFoundText"/>），
/// 分页列表 / 详情 / 流水 / 生命周期完全同口径。缺失 / 禁用 / 未映射 / 无菜单调用方对「已存在」与「不存在」的 Id
/// 得到同一受控错误，无从探测单据存在性；不存在「无请求上下文 / 空路径 / 匿名进程内调用」的免授权回退，
/// 也不读取 <c>Request.Path</c> / 环境变量 / 测试开关。审核 / 取消仍在可串行化事务与行锁内复核实时授权与来源归属，
/// 被拒绝方绝不改动单据 / 明细 / 状态 / 库存 / 流水，也绝不消耗单据号。</para>
/// <para>ERP-504：创建 / 修改在单位折算与来源链接校验之后、<b>单号预约与任何写入之前</b>追加统一草稿护栏
/// （<see cref="StockInAuthorizationRules.EnsureDraftValidAsync"/>：逐行正数量 / 实际存储精度 <c>DECIMAL(18,2)</c> /
/// 非负毛重体积 / 与 <c>Calculate</c> 同源的表头合计 + 批量在用商品），校验覆盖<b>全部提交行</b>——
/// 服务端在持久化时把每一行都强制为「正常」行，客户端 <c>IsDeleted</c> 绝不可能隐藏坏行后被重新启用；
/// 提交 / 审核作为承诺边界在既有来源 / 单据锁内复核当前主数据与已存数值（
/// <see cref="StockInAuthorizationRules.EnsureCommitReadyAsync"/>），空草稿头仍可保存但不可提交 / 审核。
/// 明细没有客户端价格 / 成本字段：成本口径仍由 ERP-033 权威来源解析，绝不臆造来源、绝不改写已审核采购价；
/// 历史取消 / 冲销路径（含无流水历史单据的原路冲回）不新增任何校验闸门。被拒绝的请求零单号消耗、
/// 零表头 · 明细 · 来源 · 状态 · 库存 · 流水变化。</para>
/// <para>ERP-522：创建 / 修改在单位折算<b>同一次批量商品读取</b>内追加共享单位资格闸门
/// （<see cref="StockUnitConversion.IsUnitEligible"/>）：显式提供的非空明细单位（去首尾空白、大小写不敏感）必须等于商品当前
/// 基础单位或合法装箱单位，其他任意单位在单号预约与任何表头 / 明细写入之前受控拒绝；空单位保持既有回退，
/// 装箱单位按当前正数 <c>UnitsPerPackage</c> 恰好折算一次。提交 / 审核作为承诺边界在既有来源 / 单据锁内复核
/// <b>仍然生效</b>明细的单位资格（<see cref="StockUnitConversion.EnsureUnitsEligibleAsync"/>），主数据被改写 /
/// 越界即受控拒绝并整体回滚；绝不臆造来源、绝不猜测任意单位换算、绝不改写历史单位。</para>
/// </summary>
[Route("api/stock-ins")]
public class StockInController : DocumentControllerBase<StockIn>
{
    /// <summary>
    /// ERP-352：当前入库操作员 Id —— 只来自已认证请求主体（<see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>）。
    /// 缺少请求上下文 / 缺少身份声明 / 非数字（含 0 / 负数）一律<b>安全解析为 <c>null</c></b>，
    /// 由 <see cref="StockInAuthorizationRules.EnsureMenuAuthorizedAsync"/> 先返回受控未认证（2000）。
    /// <para>这里<b>不是</b>免授权或测试兼容边界：不存在「无请求上下文 / 空路径 / 匿名进程内调用」豁免，
    /// 也不读取 <c>Request.Path</c>、环境变量、数据库提供程序或任何测试开关。</para>
    /// </summary>
    private long? CurrentInboundUserId()
        => ControllerContext?.HttpContext is null ? null : CurrentUserId();

    private readonly IDocumentNumberService _noService;
    private readonly IInventoryService _inventory;

    public StockInController(IErpDbContext db, IDocumentNumberService noService, IInventoryService inventory)
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
        var source = await StockInAuthorizationRules.ApplyScopeAsync(
            Db, Set.AsNoTracking().Where(o => !o.IsDeleted), CurrentInboundUserId());
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.StockInNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<StockIn>>.Success(
            new PagedResult<StockIn> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>
    /// 详情（ERP-352：统一实时闸门<b>先于</b>任何入库单字节读取，再按来源归属客户复核可见性）。
    /// <para>缺失 / 禁用 / 未映射 / 无菜单调用方对「已存在」与「不存在」的 Id 得到<b>相同</b>的受控未认证 /
    /// 权限不足错误；已授权调用方对范围外（或来源已删除 / 归属未知）与不存在的入库单得到<b>相同</b>的非披露错误
    /// （<see cref="StockInAuthorizationRules.NotFoundText"/>），绝不泄露范围外单据标识。</para>
    /// </summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        // ERP-352：先实时身份 / 账号状态 / 业务员映射 / 既有「采购入库」菜单，再读取任何单据字节。
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound(StockInAuthorizationRules.NotFoundText);
        // 与分页列表同一可见性谓词：范围外与不存在共用同一非披露错误。
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, entity);
        return Ok(ApiResponse<StockIn>.Success(entity));
    }

    /// <summary>
    /// 该单据的库存流水（ERP-025，含红字冲销流水，按发生顺序）。
    /// <para>ERP-352：与详情完全同口径 —— 统一实时闸门先于任何入库单 / 流水读取，可见性复核先于流水查询。</para>
    /// </summary>
    [HttpGet("{id:long}/movements")]
    public async Task<IActionResult> GetMovements(long id)
    {
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var entity = await GetOrThrowAsync(id, StockInAuthorizationRules.NotFoundText);
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, entity);
        var movements = await _inventory.ListMovementsAsync(InventoryDocumentHelper.StockInType, id);
        return Ok(ApiResponse<IReadOnlyList<StockMovement>>.Success(movements));
    }

    // ==================== ERP-375：来源候选 / 详情（只读、有界） ====================

    /// <summary>
    /// 可收货来源候选（只读、有界）：返回当前账号客户数据范围内、供应商匹配且关键字命中的
    /// 「已审核、未删除」采购订单可收货商品行，按「来源采购订单 + 商品」聚合给出剩余可收数量（ERP-342 口径）。
    /// <para>授权口径：既有「采购入库」菜单 + 既有「采购订单」菜单（实时校验，撤销后立即收敛）+
    /// ERP-371 权威归属客户范围（在计数 / 取数<b>之前</b>下推到数据库）；不新增用户授权，
    /// 也不提供匿名 / 管理员降级；重复 / 歧义明细、单位未知、已收货满额的候选显式标记不可用，绝不猜容量。</para>
    /// </summary>
    [HttpGet("source-candidates")]
    public async Task<IActionResult> GetSourceCandidates([FromQuery] long? supplierId,
        [FromQuery] string? keyword, [FromQuery] int take = 0)
    {
        await StockInAuthorizationRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var scoped = await ApplyProcurementScopeAsync();

        var candidates = (await StockInOrderFulfillmentRules.QuerySourceCandidatesAsync(
            Db, scoped, supplierId, keyword, take)).ToList();
        return Ok(ApiResponse<List<StockInSourceCandidateDto>>.Success(
            candidates, "已返回可收货的已审核采购订单来源（只读：重复 / 歧义明细、单位未知、已收货满额标记为不可用，绝不猜容量）"));
    }

    /// <summary>
    /// 来源详情（只读、有界）：返回一张权威来源采购订单的表头 + 逐商品剩余可收行，供业务表单在显式选择后
    /// 回填权威来源 Id / 单号 / 供应商与可收货商品行。范围外 / 不存在 / 已删除按「不存在」拒绝，
    /// 未审核 / 已取消 / 已驳回按冲突拒绝；任一失败都不改写已保存的来源链接。
    /// </summary>
    [HttpGet("source-candidates/{purchaseOrderId:long}")]
    public async Task<IActionResult> GetSourceCandidateDetail(long purchaseOrderId)
    {
        await StockInAuthorizationRules.EnsureSourceMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var scoped = await ApplyProcurementScopeAsync();

        var detail = await StockInOrderFulfillmentRules.ResolveSourceDetailAsync(Db, scoped, purchaseOrderId);
        return Ok(ApiResponse<StockInSourceDetailDto>.Success(
            detail, "已返回来源采购订单的可收货详情（只读：不可用行带原因，绝不猜成本）"));
    }

    /// <summary>列表 / 候选的权威客户范围下推（计数 / 取数之前）：复用 ERP-371 唯一权威口径，绝不内存过滤。</summary>
    private async Task<IQueryable<PurchaseOrder>> ApplyProcurementScopeAsync()
        => await PurchaseOrderAuthorizationRules.ApplyScopeAsync(
            Db, Db.PurchaseOrders.AsNoTracking(), CurrentInboundUserId());

    /// <summary>
    /// 创建（ERP-477：省略 / 空串 / 伪造入库单号照常进入本动作，单号与身份由服务端权威覆盖；
    /// ERP-352：统一实时闸门先于任何入库单字节读取，全部授权 · 来源 · 单位折算 · 累计容量校验都在
    /// <b>单号预约与任何写入之前</b>完成，被拒绝的请求零写入、零占号）。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StockIn entity)
    {
        // ERP-375 / ERP-477：业务表单「未选择来源」以数字 0 表示（数字字段留空 → 0），
        // 或按 ERP-477 的声明式可空引用（nullable）直接提交 null；两者都归一为 null，
        // 保持历史「无来源」入库单语义（null），绝不把 0 当成无效显式链接拒绝。
        NormalizePurchaseOrderLink(entity);
        entity.Details ??= new List<StockInDetail>();

        // ERP-352：统一实时闸门（身份 / 账号状态 / 业务员映射 / 既有「采购入库」菜单）先于任何读取与写入；
        // 授权与来源 / 累计容量校验先于单号生成与任何写入：被拒绝方绝不消耗单据号、绝不落库。
        await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        await StockInAuthorizationRules.EnsureAuthorizedAsync(
            Db, CurrentInboundUserId(), entity.SupplierId, entity.WarehouseId, entity.PurchaseOrderId);
        // ERP-522：单位资格闸门与装箱折算复用同一次批量商品读取 —— 显式非空的越界单位在单号预约与
        // 任何表头 / 明细写入之前受控拒绝；空单位保持既有回退，装箱折算恰好一次。
        await StockUnitConversion.NormalizeAsync(Db, entity.Details, ensureUnitEligible: true);
        Calculate(entity);
        await StockInOrderFulfillmentRules.ValidateLinkAsync(Db, entity);
        // ERP-504：草稿护栏（逐行形状 / 实际存储精度 / 表头合计 + 批量在用商品）在单位折算与来源校验之后、
        // 单号预约与任何写入之前执行；请求侧校验覆盖全部提交行 —— 服务端会把每一行都强制为「正常」行
        // (ApplyServerOwnedDetailIdentity)，客户端 IsDeleted 绝不可能隐藏坏行后被重新启用。
        // 被拒绝的请求零单号消耗、零单据 / 明细 / 来源 / 状态变化。
        await StockInAuthorizationRules.EnsureDraftValidAsync(Db, entity.Details);

        // ERP-477：主键 / 单号 / 状态 / 删除态 / 时间戳一律由服务端权威覆盖（客户端提交值不参与持久化），
        // 明细身份（主键 / 归属单据 / 删除态）同口径重置，绝不采信客户端注入的明细 Id / IsDeleted / 表头身份。
        var now = DateTime.Now;
        entity.Id = 0;
        ApplyServerOwnedDetailIdentity(entity.Details, 0, now);
        entity.StockInNo = await _noService.GenerateAsync(DocumentType.StockIn);
        entity.Status = DocumentStatus.Pending;
        entity.IsDeleted = false;
        entity.CreatedAt = now;
        entity.UpdatedAt = null;
        Db.StockIns.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.StockInNo }, "入库单创建成功"));
    }

    /// <summary>
    /// 提交：ERP-352 统一实时闸门先于任何入库单字节读取，再复核已存单据可见性，最后走基类状态流转。
    /// 被拒绝 / 范围外 / 不存在的 Id 得到同一受控错误且零状态变化。
    /// <para>ERP-504：提交是承诺边界之一 —— 空草稿头仍可保存，但提交要求至少一行有效明细，
    /// 且已存明细的形状 / 实际存储精度与当前在用商品必须仍然成立；任一不满足即拒绝且状态保持「待提交」
    /// （零状态变化、零库存 / 流水）。</para>
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var entity = await Set.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound(StockInAuthorizationRules.NotFoundText);
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, entity);
        // ERP-504：承诺边界复核（当前主数据 + 已存数值）；拒绝时零状态变化、零库存 / 流水。
        await StockInAuthorizationRules.EnsureCommitReadyAsync(Db, entity.Details, "提交");
        // ERP-522：承诺边界复核仍然生效的已存明细单位资格（当前主数据实时快照；空单位保持既有回退）。
        await StockUnitConversion.EnsureUnitsEligibleAsync(Db, entity.Details.Where(d => !d.IsDeleted));
        return await base.Submit(id);
    }

    /// <summary>
    /// 删除：ERP-352 统一实时闸门先于任何入库单字节读取，再复核已存单据可见性，最后走基类软删除。
    /// 被拒绝 / 范围外 / 不存在的 Id 得到同一受控错误且零写入。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var entity = await GetOrThrowAsync(id, StockInAuthorizationRules.NotFoundText);
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, entity);
        return await base.Delete(id);
    }

    /// <summary>
    /// 更新（ERP-477：来源归属先校验，单据身份 · 单号 · 状态与明细身份全部由服务端保持权威；
    /// ERP-352：统一实时闸门先于任何入库单字节读取，再校验「已存」与「请求」两侧归属；
    /// ERP-504：请求侧内容（单位折算 → 来源链接 → 草稿护栏）全部通过后才改写已存单据 / 明细；
    /// 仅「待提交」可修改，被拒绝的请求零写入、零状态变化）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] StockIn entity)
    {
        // ERP-375 / ERP-477：与创建同一口径——表单「未选择来源」（0 或 null）归一为 null，保持历史无来源语义。
        NormalizePurchaseOrderLink(entity);
        entity.Details ??= new List<StockInDetail>();

        // ERP-352：统一实时闸门先于任何单据字节读取（缺失 / 禁用 / 未映射 / 无菜单对存在与不存在的 Id 同错）。
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var existing = await Db.StockIns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound(StockInAuthorizationRules.NotFoundText);

        // 校验「已存」与「请求」两侧的归属：既有来源客户不可越界（范围外按「不存在」非披露拒绝），
        // 新来源也必须落在当前账号客户数据范围内；任一不满足都零写入、零状态变化、已存来源保持不变。
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, existing);
        await StockInAuthorizationRules.EnsureAuthorizedAsync(
            Db, CurrentInboundUserId(), entity.SupplierId, entity.WarehouseId, entity.PurchaseOrderId);

        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        // ERP-477：明细身份（主键 / 归属单据 / 删除态 / 时间戳）一律由服务端重置；
        // ERP-504：请求侧内容（单位折算 → 来源链接 → 草稿护栏）先在同一份请求对象上完成全部校验，
        // 只有全部通过才改写已存单据 / 明细 —— 任一失败都不改单据 / 明细 / 状态，也不消耗新单号。
        ApplyServerOwnedDetailIdentity(entity.Details, id, DateTime.Now);
        await StockUnitConversion.NormalizeAsync(Db, entity.Details, ensureUnitEligible: true);
        await StockInOrderFulfillmentRules.ValidateLinkAsync(Db, entity);
        await StockInAuthorizationRules.EnsureDraftValidAsync(Db, entity.Details);

        existing.StockInDate = entity.StockInDate;
        existing.PurchaseOrderId = entity.PurchaseOrderId;
        existing.SupplierId = entity.SupplierId;
        existing.WarehouseId = entity.WarehouseId;
        existing.Remark = entity.Remark;

        Db.StockInDetails.RemoveRange(existing.Details);
        // ERP-477：单据号 / 状态 / 删除态由服务端保持（客户端提交值不参与持久化）。
        existing.Details = entity.Details;
        Calculate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "入库单更新成功"));
    }

    /// <summary>
    /// 审核（按基础单位增加库存并写入库存流水，恰好一次）。
    /// <para>ERP-352：统一实时闸门先于任何入库单字节读取；已存单据可见性（范围外按「不存在」非披露拒绝）先于状态判定；
    /// 库存 / 流水 / 状态写入仍统一放进可串行化事务，锁内再复核实时授权与来源归属。</para>
    /// <para>ERP-504：锁内追加承诺边界复核（至少一行有效明细 + 已存数值形状 / 实际存储精度 / 表头合计 + 当前在用商品），
    /// 空明细与非法数值不得过账；被拒绝时整体回滚，来源剩余容量 / 成本 / 库存 / 流水 / 状态 / 单号全部不变。</para>
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var entity = await Db.StockIns.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound(StockInAuthorizationRules.NotFoundText);
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, entity);
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");

        await StockUnitConversion.NormalizeAsync(Db, entity.Details);
        Calculate(entity);

        // ERP-033：成本来源解析。一次加载商品元数据 + 本单链接的采购订单（含明细），逐行纯内存判定，
        // 不做逐行查库；未链接 / 链接不可用 / 语义不明确时该行成本返回 0（走既有兜底）。
        var costSource = await PurchaseStockInCostSource.LoadAsync(Db, entity.PurchaseOrderId,
            entity.Details.Select(d => (long?)d.ProductId));

        // ERP-342：把「累计已审核入库 ≤ 来源订单授权数量」的判定与库存写入放进同一个可串行化事务，
        // 并对来源订单行加 UPDLOCK/HOLDLOCK 串行化同单并发审核；任一步失败整体回滚，库存、流水、状态都不变。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireOrderApprovalLockAsync(entity.PurchaseOrderId);

            // ERP-352：锁内复核实时授权与来源归属（授权 / 范围 / 来源变更立即收敛）。
            await StockInAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentInboundUserId(), entity);

            // 幂等护栏放进锁内：同单并发审核时，后到者在拿到锁后能看到先到者已产生的流水，从而被拒绝。
            if (await _inventory.CountActiveMovementsAsync(InventoryDocumentHelper.StockInType, entity.Id) > 0)
                throw BusinessException.RuleConflict("该入库单已产生库存流水，不能重复审核");

            // ERP-504：承诺边界复核（空 / 非正 / 不可表示数量、精度与合计、当前在用商品）在既有来源 / 单据锁内完成；
            // 任一不满足即拒绝，单据状态 / 来源数量 / 成本 / 库存 / 流水全部保持原样（整体回滚）。
            await StockInAuthorizationRules.EnsureCommitReadyAsync(Db, entity.Details, "审核");
            // ERP-522：在既有来源 / 单据锁内复核仍然生效明细的单位资格（当前商品快照）；越界即整体回滚。
            await StockUnitConversion.EnsureUnitsEligibleAsync(Db, entity.Details.Where(d => !d.IsDeleted));

            await StockInOrderFulfillmentRules.ValidateApprovalAsync(Db, entity);

            foreach (var d in entity.Details.Where(d => !d.IsDeleted))
            {
                // 零数量明细（旧版页面允许留空行）不产生库存变动；负数属于数据错误，直接拒绝
                if (d.Quantity < 0)
                    throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的入库数量不能为负数");
                if (d.Quantity == 0) continue;

                var cost = costSource.Resolve(d.ProductId);
                var product = await InventoryDocumentHelper.ResolveProductAsync(Db, d.ProductId, d.ProductName, d.Spec, d.Unit);
                var context = await InventoryDocumentHelper.BuildContextAsync(Db,
                    InventoryDocumentHelper.StockInType, entity.Id, entity.StockInNo,
                    InventoryMovementType.PurchaseIn, entity.WarehouseId, d.ProductId, product.Code, product.Name,
                    product.Spec, product.Unit, entity.StockInDate, MovementRemark(entity, cost));

                // 成本基准（ERP-033）：优先取「权威链接的采购订单唯一兼容明细行单价」——包装单位单价按商品
                // UnitsPerPackage 折算为基础单位单价，外币单价仅在订单持久化了非占位汇率时换算；
                // 任一语义不明确（无链接 / 订单未审核 / 订单行缺失、重复、单位不兼容 / 无权威汇率 / 单价不可用）
                // 都返回 0，交由 InventoryService 既有兜底口径计价（当前加权平均成本，首次入库为 0），不臆造成本单价。
                await _inventory.IncreaseAsync(context, d.Quantity, cost.UnitCost);
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

        return Ok(ApiResponse<object>.Success(null, "审核通过，库存已更新并写入库存流水"));
    }

    /// <summary>
    /// 对来源采购订单行加更新锁（UPDLOCK, HOLDLOCK），把同单并发审核串行化在同一事务内。
    /// 未链接订单时无需加锁；非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquireOrderApprovalLockAsync(long? purchaseOrderId)
    {
        if (purchaseOrderId is not > 0) return;
        if (!Db.Database.IsRelational()) return;

        // 订单行不存在时无需加锁：ValidateApprovalAsync 会对显式链接 fail closed 抛异常，
        // 在取得任何库存写入前拒绝履约；未链接（null）则不加锁。
        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                purchaseOrderId.Value)
            .ToListAsync();
    }

    /// <summary>
    /// 取消：已审核单据按库存流水冲销（无流水的历史单据按基础单位原路冲回）。
    /// <para>ERP-352：统一实时闸门与已存单据可见性（范围外按「不存在」非披露拒绝）都先于任何入库单字节读取与行锁。</para>
    /// <para>ERP-359：与采购退货审核 / 销审共用<b>同一</b>把来源入库单行锁（<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务，
    /// 锁内先复核实时授权 / 来源归属，再确认没有仍然生效的已审核采购退货单显式引用本入库单（
    /// <see cref="ReturnSourceCancellationRules.EnsureNoEffectiveApprovedPurchaseReturnAsync"/>）；
    /// 存在时 fail closed 拒绝，单据 / 明细 / 状态 / 库存 / 流水全部保持原样，绝不先冲销再校验。</para>
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        // ERP-352：统一实时闸门 + 可见性硬边界都先于任何入库单字节读取与行锁。
        var scope = await StockInAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentInboundUserId());
        var probe = await Db.StockIns.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.PurchaseOrderId })
            .FirstOrDefaultAsync() ?? throw BusinessException.NotFound(StockInAuthorizationRules.NotFoundText);
        await StockInAuthorizationRules.EnsureReceiptVisibleAsync(Db, scope, probe.PurchaseOrderId);

        // 确定性锁序：来源入库单行（与采购退货审核 / 销审同一把锁）→ 退货单行（只读判定）。任一步失败整体回滚。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await LockSourceReceiptRowAsync(id);

            var entity = await Db.StockIns.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound(StockInAuthorizationRules.NotFoundText);
            await StockInAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentInboundUserId(), entity);

            var status = GetStatus(entity);
            if (status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("入库单已取消");

            // 在锁内、在任何库存冲销 / 状态变更之前判定退货引用：被拒绝时不留任何半成品写入。
            await ReturnSourceCancellationRules.EnsureNoEffectiveApprovedPurchaseReturnAsync(Db, id);

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

        return Ok(ApiResponse<object>.Success(null, "已取消，库存已按基础单位冲回"));
    }

    /// <summary>
    /// 对来源采购入库单行加更新锁（<c>UPDLOCK, HOLDLOCK</c>，与采购退货审核 / 销审共用
    /// <see cref="ReturnSourceCancellationRules.LockStockInRowSql"/> 同一锁语句）：
    /// 把「取消来源入库单」与「采购退货审核 / 销审」串行化在同一可串行化事务内；非关系型提供程序跳过。
    /// </summary>
    private Task LockSourceReceiptRowAsync(long stockInId)
    {
        if (!ReturnSourceCancellationRules.IsRelationalProvider(Db)) return Task.CompletedTask;
        return Db.Database
            .SqlQueryRaw<long>(ReturnSourceCancellationRules.LockStockInRowSql, stockInId)
            .ToListAsync();
    }

    /// <summary>
    /// 冲销已审核入库：优先按库存流水生成红字流水（ERP-025，与 ERP-009 四类单据同一口径，
    /// 重复冲销由流水的 <c>IsReversed</c> 标记兜住）；本次改造前审核的历史单据没有流水，
    /// 退化为按明细基础单位原路冲回，避免库存台账漂移。
    /// </summary>
    private async Task ReverseStockAsync(StockIn entity)
    {
        var reversals = await _inventory.ReverseAsync(InventoryDocumentHelper.StockInType, entity.Id,
            "采购入库单取消冲销");
        if (reversals.Count == 0)
            await ReverseLegacyStockAsync(entity);
    }

    /// <summary>
    /// 流水备注：带上来源采购订单 Id 与成本来源（ERP-033），便于按流水反查采购执行情况与成本依据。
    /// 未链接采购订单时保持 ERP-025 的原文案不变。
    /// </summary>
    private static string MovementRemark(StockIn entity, PurchaseStockInCostResolution cost)
        => entity.PurchaseOrderId is > 0
            ? $"采购入库单审核入库（采购订单 Id {entity.PurchaseOrderId}，成本来源：{cost.RemarkText}）"
            : "采购入库单审核入库";

    /// <summary>
    /// ERP-375：把业务表单「未选择来源」的数字 0 归一为 <c>null</c>（历史「无来源」语义），
    /// 负数等非法值保持原样交由 <see cref="StockInOrderFulfillmentRules.ValidateLinkAsync"/> fail closed 拒绝。
    /// </summary>
    private static void NormalizePurchaseOrderLink(StockIn entity)
    {
        if (entity.PurchaseOrderId == 0) entity.PurchaseOrderId = null;
    }

    /// <summary>
    /// ERP-477：明细身份 / 归属单据 / 删除态 / 时间戳一律由服务端权威覆盖：客户端提交的明细主键一律置 0（自增），
    /// 归属单据取自服务端已知的单据 Id（新建为 0，由 EF 关系在保存时回填），删除态强制为「正常」，
    /// 时间戳以服务端时钟为准。绝不采信客户端注入的明细身份 / 删除态，也绝不臆造任何明细行。
    /// </summary>
    private static void ApplyServerOwnedDetailIdentity(IEnumerable<StockInDetail> details, long stockInId, DateTime now)
    {
        foreach (var d in details)
        {
            d.Id = 0;
            d.StockInId = stockInId;
            d.IsDeleted = false;
            d.CreatedAt = now;
            d.UpdatedAt = null;
        }
    }

    private static void Calculate(StockIn entity)
    {
        entity.TotalQuantity = entity.Details.Sum(d => d.Quantity);
        entity.TotalWeight = entity.Details.Sum(d => d.Weight);
        entity.TotalVolume = entity.Details.Sum(d => d.Volume);
    }

    /// <summary>
    /// 历史单据兜底（ERP-023 之前审核、没有库存流水的入库单）：按明细已持久化的基础单位数量原路冲回。
    /// 有流水的单据一律走 <see cref="ReverseStockAsync"/> 的红字冲销，本方法不会再改动库存金额。
    /// </summary>
    private async Task ReverseLegacyStockAsync(StockIn entity)
    {
        foreach (var d in entity.Details)
        {
            var stock = await Db.Stocks.FirstOrDefaultAsync(s => s.WarehouseId == entity.WarehouseId && s.ProductId == d.ProductId)
                ?? throw BusinessException.RuleConflict($"商品 [{d.ProductName}] 库存记录不存在，不能冲销入库");
            if (stock.AvailableQuantity < d.Quantity)
                throw BusinessException.RuleConflict($"商品 [{d.ProductName}] 已有库存不足，不能冲销入库");
            stock.Quantity -= d.Quantity;
            stock.AvailableQuantity -= d.Quantity;
            stock.UpdatedAt = DateTime.Now;
        }
    }
}
