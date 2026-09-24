using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 商品 / SKU 货源关系服务（ERP-038）。职责：
/// <list type="number">
/// <item><b>维护</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/> / <see cref="SetStatusAsync"/> /
/// <see cref="SetPreferredAsync"/> / <see cref="DeleteAsync"/>）：只写货源关系子表 <c>BaseProductSuppliers</c>，
/// 服务端统一推导作用域键、规范化文本与数值，并校验商品 / 规格 / 供应商引用的存在性、归属与启用状态；</item>
/// <item><b>读取</b>（<see cref="ListByProductAsync"/> / <see cref="ListBySupplierAsync"/>）：商品侧与供应商侧
/// 都返回**有界**列表，默认含停用关系（历史可读）并显式标注可用性；</item>
/// <item><b>可选用口径</b>（<see cref="LoadSelectableAsync"/>）：只返回启用中的关系，因此停用 / 已删除关系
/// 不会被当成启用货源；</item>
/// <item><b>列表 / 详情标注</b>（<see cref="AnnotateProductsAsync"/> / <see cref="AnnotateSuppliersAsync"/>）：
/// 为主数据补写启用关系数与关系总数。</item>
/// </list>
/// <para>边界（重要）：除 <c>BaseProductSuppliers</c> 自身外不写任何数据 ——
/// <strong>不</strong>自动选择供应商、<strong>不</strong>定价 / 审批价、<strong>不</strong>生成或改写
/// 采购报价 <c>PurchaseQuotes</c>、采购订单 <c>PurchaseOrders</c> / <c>PurchaseOrderDetails</c>、
/// <strong>不</strong>改写库存 <c>Stocks</c> 成本与库存流水 <c>StockMovements</c>，
/// 也不改写任何历史单据：货源关系只是人工比价与下单前的指引。</para>
/// </summary>
public static class ProductSupplierService
{
    /// <summary>并发 / 唯一索引兜底时的对外文案（范围 + 供应商重复或首选重复）</summary>
    public const string DuplicateConflictMessage = "该货源关系正在被其他请求写入（商品 / 规格 + 供应商重复或首选冲突），请重试";

    /// <summary>
    /// 校验商品存在（未删除），返回商品实体；货源关系必须挂在既有商品下。
    /// <para>读取路径也用本方法，因此历史 / 停用商品的关系列表仍照常可读（不做启用性拦截）。</para>
    /// </summary>
    public static async Task<BaseProduct> EnsureProductAsync(IErpDbContext db, long productId)
    {
        if (productId <= 0)
            throw BusinessException.InvalidParameter("商品 Id 不合法");
        return await db.BaseProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted)
            ?? throw BusinessException.NotFound($"商品（Id={productId}）不存在或已删除，不能维护货源关系");
    }

    /// <summary>
    /// 读取某商品（含其规格）的货源关系（<b>只读，不写库</b>）：默认包含停用关系（历史可读），
    /// <paramref name="activeOnly"/> 为真时只返回启用中的关系（可选用口径）；
    /// 结果按上限 <see cref="ProductSupplierRules.MaxRelationshipsPerProduct"/> 收敛，保证视图有界；
    /// 排序按作用域键（商品级在前）→ 排序号 → Id，便于界面清晰区分商品级与规格级关系。
    /// </summary>
    public static async Task<List<ProductSupplierDto>> ListByProductAsync(
        IErpDbContext db, long productId, bool activeOnly = false,
        int take = ProductSupplierRules.MaxRelationshipsPerProduct)
    {
        await EnsureProductAsync(db, productId);

        var bound = take <= 0 ? ProductSupplierRules.MaxRelationshipsPerProduct
            : Math.Min(take, ProductSupplierRules.MaxRelationshipsPerProduct);

        var query = db.BaseProductSuppliers.AsNoTracking()
            .Where(x => x.ProductId == productId && !x.IsDeleted);
        if (activeOnly)
            query = query.Where(x => x.Status == ProductSupplierRules.ActiveStatus);

        var rows = await query
            .OrderBy(x => x.ScopeKey).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Take(bound)
            .ToListAsync();

        return await MapAsync(db, rows);
    }

    /// <summary>
    /// 读取「可选用」的货源关系（<b>只读</b>）：只返回启用中的关系。
    /// 与写入校验口径（<see cref="ProductSupplierRules.IsSelectable"/>）一致，
    /// 因此停用 / 已删除的关系不会出现在该视图中，无法被当成启用货源参考。
    /// </summary>
    public static Task<List<ProductSupplierDto>> LoadSelectableAsync(IErpDbContext db, long productId) =>
        ListByProductAsync(db, productId, activeOnly: true);

    /// <summary>
    /// 读取某供应商的货源关系（供应商资料侧视图，<b>只读</b>）：默认含停用关系（历史可读），
    /// 按上限 <see cref="ProductSupplierRules.MaxRelationshipsPerSupplier"/> 收敛为有界列表。
    /// </summary>
    public static async Task<List<ProductSupplierDto>> ListBySupplierAsync(
        IErpDbContext db, long supplierId, bool activeOnly = false,
        int take = ProductSupplierRules.MaxRelationshipsPerSupplier)
    {
        if (supplierId <= 0)
            throw BusinessException.InvalidParameter("供应商 Id 不合法");
        _ = await db.BaseSuppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId && !s.IsDeleted)
            ?? throw BusinessException.NotFound($"供应商（Id={supplierId}）不存在或已删除");

        var bound = take <= 0 ? ProductSupplierRules.MaxRelationshipsPerSupplier
            : Math.Min(take, ProductSupplierRules.MaxRelationshipsPerSupplier);

        var query = db.BaseProductSuppliers.AsNoTracking()
            .Where(x => x.SupplierId == supplierId && !x.IsDeleted);
        if (activeOnly)
            query = query.Where(x => x.Status == ProductSupplierRules.ActiveStatus);

        var rows = await query
            .OrderBy(x => x.ProductId).ThenBy(x => x.ScopeKey).ThenBy(x => x.Id)
            .Take(bound)
            .ToListAsync();

        return await MapAsync(db, rows);
    }

    /// <summary>
    /// 读取标注（<b>不写库</b>）：为商品列表 / 详情补写「启用货源关系数」与「货源关系总数（含停用）」。
    /// 一次查询解析全部商品的计数，避免逐行查询；没有任何货源关系的商品两项均为 0（无需任何回填）。
    /// </summary>
    public static async Task AnnotateProductsAsync(IErpDbContext db, IEnumerable<BaseProduct> products)
    {
        var list = products as IList<BaseProduct> ?? products.ToList();
        if (list.Count == 0) return;

        var ids = list.Select(p => p.Id).Distinct().ToList();
        var rows = await db.BaseProductSuppliers.AsNoTracking()
            .Where(x => !x.IsDeleted && ids.Contains(x.ProductId))
            .Select(x => new { x.ProductId, x.Status })
            .ToListAsync();

        var counted = rows
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => new
            {
                Total = g.Count(),
                Active = g.Count(x => x.Status == ProductSupplierRules.ActiveStatus)
            });

        foreach (var product in list)
        {
            var found = counted.TryGetValue(product.Id, out var counts);
            product.SourcingCount = found ? counts!.Active : 0;
            product.SourcingTotalCount = found ? counts!.Total : 0;
        }
    }

    /// <summary>
    /// 读取标注（<b>不写库</b>）：为供应商列表 / 详情补写「启用货源关系数」与「货源关系总数（含停用）」。
    /// 供应商侧的可见入口由此有界呈现，历史关系照常可读。
    /// </summary>
    public static async Task AnnotateSuppliersAsync(IErpDbContext db, IEnumerable<BaseSupplier> suppliers)
    {
        var list = suppliers as IList<BaseSupplier> ?? suppliers.ToList();
        if (list.Count == 0) return;

        var ids = list.Select(s => s.Id).Distinct().ToList();
        var rows = await db.BaseProductSuppliers.AsNoTracking()
            .Where(x => !x.IsDeleted && ids.Contains(x.SupplierId))
            .Select(x => new { x.SupplierId, x.Status })
            .ToListAsync();

        var counted = rows
            .GroupBy(x => x.SupplierId)
            .ToDictionary(g => g.Key, g => new
            {
                Total = g.Count(),
                Active = g.Count(x => x.Status == ProductSupplierRules.ActiveStatus)
            });

        foreach (var supplier in list)
        {
            var found = counted.TryGetValue(supplier.Id, out var counts);
            supplier.SourcingCount = found ? counts!.Active : 0;
            supplier.SourcingTotalCount = found ? counts!.Total : 0;
        }
    }

    /// <summary>
    /// 新增货源关系（仅写 <c>BaseProductSuppliers</c>）：
    /// 商品必须未删除且启用；规格（可选）必须存在、未删除、启用且属于该商品；供应商必须存在、未删除且启用。
    /// 同一「商品 + 规格作用域 + 供应商」不重复；勾选首选时该范围不得已有启用首选（更换首选请走「设为首选」）。
    /// </summary>
    public static async Task<ProductSupplierDto> CreateAsync(
        IErpDbContext db, long productId, ProductSupplierSaveDto dto)
    {
        var product = await EnsureProductAsync(db, productId);
        if (dto is null)
            throw BusinessException.InvalidParameter("货源关系数据不能为空");
        ProductSupplierRules.EnsureProductSelectable(product);

        var variant = await EnsureVariantSelectableAsync(db, productId, dto.VariantId);
        var supplier = await EnsureSupplierSelectableAsync(db, dto.SupplierId);
        var scopeKey = ProductSupplierRules.BuildScopeKey(variant?.Id);
        var status = ProductSupplierRules.NormalizeStatus(dto.Status);

        var siblings = await LoadSiblingsAsync(db, productId);
        ProductSupplierRules.EnsureProductBound(siblings.Count);
        ProductSupplierRules.EnsureSupplierBound(await CountBySupplierAsync(db, supplier.Id));
        ProductSupplierRules.EnsureScopeSupplierUnique(siblings, scopeKey, supplier.Id);

        var payload = ProductSupplierRules.Normalize(
            dto.SupplierItemCode, dto.PurchaseUnit, dto.MinOrderQty, dto.LeadTimeDays, dto.Remark);
        var preferred = dto.IsPreferred ?? false;

        // 首选唯一性只针对「启用中的首选」判定：停用关系即使带首选标记也不占用首选位（数据库唯一索引同口径）
        if (status == ProductSupplierRules.ActiveStatus)
            ProductSupplierRules.EnsurePreferredUnique(siblings, scopeKey, preferred);

        var entity = new BaseProductSupplier
        {
            ProductId = productId,
            VariantId = variant?.Id,
            ScopeKey = scopeKey,
            SupplierId = supplier.Id,
            SupplierItemCode = payload.SupplierItemCode,
            PurchaseUnit = payload.PurchaseUnit,
            MinOrderQty = payload.MinOrderQty,
            LeadTimeDays = payload.LeadTimeDays,
            IsPreferred = preferred,
            Status = status,
            SortOrder = dto.SortOrder ?? 0,
            Remark = payload.Remark
        };

        db.BaseProductSuppliers.Add(entity);
        await SaveAsync(db);
        return await ToDtoAsync(db, entity);
    }

    /// <summary>
    /// 修改货源关系（仅写 <c>BaseProductSuppliers</c>）：字段重新规范化并重新做重复 / 首选判定。
    /// <para>引用口径：<b>新选或更换</b>的规格与供应商必须是可用的主数据（存在、未删除、启用，规格还须属于该商品）；
    /// 引用<b>未变更</b>时允许继续编辑其他字段，即使该规格 / 供应商后来被停用或删除 ——
    /// 历史货源关系必须保持可维护、可读，而不是因为主数据停用就不可编辑。</para>
    /// </summary>
    public static async Task<ProductSupplierDto> UpdateAsync(
        IErpDbContext db, long productId, long id, ProductSupplierSaveDto dto)
    {
        var entity = await FindAsync(db, productId, id);
        if (dto is null)
            throw BusinessException.InvalidParameter("货源关系数据不能为空");

        var requestedVariantId = dto.VariantId.HasValue && dto.VariantId.Value > 0 ? dto.VariantId : null;
        var variantChanged = (entity.VariantId ?? 0) != (requestedVariantId ?? 0);
        var supplierChanged = entity.SupplierId != dto.SupplierId;

        if (variantChanged)
            await EnsureVariantSelectableAsync(db, productId, requestedVariantId);
        if (supplierChanged)
            await EnsureSupplierSelectableAsync(db, dto.SupplierId);

        var scopeKey = ProductSupplierRules.BuildScopeKey(requestedVariantId);
        var status = dto.Status.HasValue ? ProductSupplierRules.NormalizeStatus(dto.Status) : entity.Status;
        var siblings = await LoadSiblingsAsync(db, productId);

        ProductSupplierRules.EnsureScopeSupplierUnique(siblings, scopeKey, entity.SupplierId, entity.Id);

        var payload = ProductSupplierRules.Normalize(
            dto.SupplierItemCode, dto.PurchaseUnit, dto.MinOrderQty, dto.LeadTimeDays, dto.Remark);
        var preferred = dto.IsPreferred ?? entity.IsPreferred;

        if (status == ProductSupplierRules.ActiveStatus)
            ProductSupplierRules.EnsurePreferredUnique(siblings, scopeKey, preferred, entity.Id);

        entity.VariantId = requestedVariantId;
        entity.ScopeKey = scopeKey;
        entity.SupplierItemCode = payload.SupplierItemCode;
        entity.PurchaseUnit = payload.PurchaseUnit;
        entity.MinOrderQty = payload.MinOrderQty;
        entity.LeadTimeDays = payload.LeadTimeDays;
        entity.IsPreferred = preferred;
        entity.Status = status;
        entity.SortOrder = dto.SortOrder ?? entity.SortOrder;
        entity.Remark = payload.Remark;

        await SaveAsync(db);
        return await ToDtoAsync(db, entity);
    }

    /// <summary>停用货源关系：历史仍可读（列表照常返回并标注），但不再作为启用货源；同时释放首选标记</summary>
    public static Task<ProductSupplierDto> DisableAsync(IErpDbContext db, long productId, long id) =>
        SetStatusAsync(db, productId, id, ProductSupplierRules.DisabledStatus);

    /// <summary>重新启用货源关系：商品 / 规格 / 供应商必须仍然可用，且启用后不得出现第二个启用首选</summary>
    public static Task<ProductSupplierDto> EnableAsync(IErpDbContext db, long productId, long id) =>
        SetStatusAsync(db, productId, id, ProductSupplierRules.ActiveStatus);

    /// <summary>
    /// 设置货源关系状态（仅写 <c>BaseProductSuppliers</c>）：
    /// <list type="bullet">
    /// <item>停用：仅改状态并释放首选标记（首选只对启用中的关系有效，重新启用后需显式重新设置）；</item>
    /// <item>启用：重新校验商品 / 规格 / 供应商可用性，并重新做「启用首选唯一」判定，
    /// 因此不会出现两条同时启用且同为首选的关系。</item>
    /// </list>
    /// </summary>
    public static async Task<ProductSupplierDto> SetStatusAsync(
        IErpDbContext db, long productId, long id, int status)
    {
        var entity = await FindAsync(db, productId, id);
        var normalizedStatus = ProductSupplierRules.NormalizeStatus(status);

        if (normalizedStatus == ProductSupplierRules.ActiveStatus)
        {
            var product = await EnsureProductAsync(db, productId);
            ProductSupplierRules.EnsureProductSelectable(product);
            await EnsureVariantSelectableAsync(db, productId, entity.VariantId);
            await EnsureSupplierSelectableAsync(db, entity.SupplierId);

            if (entity.IsPreferred)
            {
                var siblings = await LoadSiblingsAsync(db, productId);
                ProductSupplierRules.EnsurePreferredUnique(
                    siblings, entity.ScopeKey, requirePreferred: true, excludeId: entity.Id);
            }
        }
        else
        {
            entity.IsPreferred = false;
        }

        entity.Status = normalizedStatus;
        await SaveAsync(db);
        return await ToDtoAsync(db, entity);
    }

    /// <summary>
    /// 显式设置 / 取消首选（仅写 <c>BaseProductSuppliers</c>）：
    /// 设为该范围首选时先释放同范围内旧的启用首选（一次批量写回），再置新首选 —— 与列表顺序无关；
    /// 取消首选只影响本行。范围首选唯一性由服务端判定 + 数据库过滤唯一索引双重兜底。
    /// </summary>
    public static async Task<ProductSupplierDto> SetPreferredAsync(
        IErpDbContext db, long productId, long id, bool preferred)
    {
        var entity = await FindAsync(db, productId, id);

        if (!preferred)
        {
            entity.IsPreferred = false;
            await SaveAsync(db);
            return await ToDtoAsync(db, entity);
        }

        if (entity.Status != ProductSupplierRules.ActiveStatus)
            throw BusinessException.InvalidParameter("只有启用中的货源关系可以设为该范围的首选，请先启用该货源关系");

        var siblings = await LoadSiblingsAsync(db, productId);
        var conflicts = siblings
            .Where(x => x.Id != entity.Id && x.ScopeKey == entity.ScopeKey && ProductSupplierRules.IsPreferredActive(x))
            .ToList();

        // 两段式显式切换：先在同一范围内释放旧首选（单次 SaveChanges 批量写回），再置新首选。
        // 任何时刻范围内都不会存在两条启用首选，切换结果不依赖列表顺序，也不依赖「最后提交者胜出」；
        // 并发下由过滤唯一索引 UX_BaseProductSuppliers_ScopePreferred 兜底并转为可读的重复错误。
        if (conflicts.Count > 0)
        {
            foreach (var conflict in conflicts) conflict.IsPreferred = false;
            await SaveAsync(db);
        }

        entity.IsPreferred = true;
        await SaveAsync(db);
        return await ToDtoAsync(db, entity);
    }

    /// <summary>
    /// 删除货源关系（<b>只做本表软删除</b>，保留行以便历史引用可读）：不物理删除、
    /// 不改写任何采购报价 / 采购订单 / 库存与历史单据；删除同时释放首选标记。
    /// </summary>
    public static async Task DeleteAsync(IErpDbContext db, long productId, long id)
    {
        var entity = await FindAsync(db, productId, id);
        entity.IsDeleted = true;
        entity.IsPreferred = false;
        await SaveAsync(db);
    }

    /// <summary>按「商品 + 货源关系」定位未删除关系；不属于该商品时按不存在处理</summary>
    private static async Task<BaseProductSupplier> FindAsync(IErpDbContext db, long productId, long id)
    {
        await EnsureProductAsync(db, productId);
        return await db.BaseProductSuppliers
            .FirstOrDefaultAsync(x => x.Id == id && x.ProductId == productId && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"货源关系（Id={id}）在该商品下不存在或已删除");
    }

    /// <summary>
    /// 该商品下未删除的全部货源关系（含停用），用于重复 / 首选唯一性判定与首选切换。
    /// 使用**跟踪**实体：首选切换需要就地释放旧首选并在一次保存中写回。
    /// </summary>
    private static async Task<List<BaseProductSupplier>> LoadSiblingsAsync(IErpDbContext db, long productId) =>
        await db.BaseProductSuppliers
            .Where(x => x.ProductId == productId && !x.IsDeleted)
            .ToListAsync();

    /// <summary>该供应商已有的未删除货源关系条数（含停用），用于单供应商上限判定</summary>
    private static Task<int> CountBySupplierAsync(IErpDbContext db, long supplierId) =>
        db.BaseProductSuppliers.CountAsync(x => x.SupplierId == supplierId && !x.IsDeleted);

    /// <summary>
    /// 校验归属规格可用于货源关系：存在、未删除、属于该商品且启用。
    /// 不满足时按「不存在 → 404」「归属不符 / 已停用 → 参数校验失败」给出可读错误。
    /// </summary>
    private static async Task<BaseProductVariant?> EnsureVariantSelectableAsync(
        IErpDbContext db, long productId, long? variantId)
    {
        if (!variantId.HasValue || variantId.Value <= 0) return null;

        var variant = await db.BaseProductVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == variantId.Value);
        if (variant is null || variant.IsDeleted)
            throw BusinessException.NotFound($"规格（Id={variantId.Value}）不存在或已删除，不能作为货源关系的归属规格");
        if (variant.ProductId != productId)
            throw BusinessException.InvalidParameter(
                $"规格（Id={variantId.Value}）不属于该商品，不能作为本商品货源关系的归属规格");
        if (variant.Status != ProductVariantRules.ActiveStatus)
            throw BusinessException.InvalidParameter(
                $"规格「{ProductVariantRules.DisplayName(variant.VariantCode, variant.Color, variant.Size)}」已停用，"
                + "不能新指定为货源关系的归属规格（历史货源关系仍可读取）");
        return variant;
    }

    /// <summary>校验供应商可用于货源关系：存在、未删除且启用；否则给出可读错误（404 / 参数校验失败）</summary>
    private static async Task<BaseSupplier> EnsureSupplierSelectableAsync(IErpDbContext db, long supplierId)
    {
        if (supplierId <= 0)
            throw BusinessException.InvalidParameter("供应商 Id 不合法");

        var supplier = await db.BaseSuppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId);
        if (supplier is null || supplier.IsDeleted)
            throw BusinessException.NotFound($"供应商（Id={supplierId}）不存在或已删除，请重新选择");
        if (supplier.Status != ProductSupplierRules.ActiveStatus)
            throw BusinessException.InvalidParameter(
                $"供应商「{supplier.SupplierName}」已停用，不能新建或更换货源关系（历史货源关系仍可读取）");
        return supplier;
    }

    /// <summary>按单条关系映射 DTO（写入响应与读取响应口径一致）</summary>
    private static async Task<ProductSupplierDto> ToDtoAsync(IErpDbContext db, BaseProductSupplier entity) =>
        (await MapAsync(db, new List<BaseProductSupplier> { entity })).Single();

    /// <summary>
    /// 实体 → 读取 DTO（含服务端解析的商品 / 规格 / 供应商信息与可用性标注）。
    /// 商品 / 供应商 / 规格按批次一次查询解析，避免逐行查询；
    /// 缺失的主数据行退化为可辨识的占位文案（而不是静默变空），保证历史关系仍然可读。
    /// </summary>
    private static async Task<List<ProductSupplierDto>> MapAsync(
        IErpDbContext db, IReadOnlyList<BaseProductSupplier> rows)
    {
        var result = new List<ProductSupplierDto>(rows.Count);
        if (rows.Count == 0) return result;

        var productIds = rows.Select(x => x.ProductId).Distinct().ToList();
        var products = await db.BaseProducts.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync();
        var productsById = products.ToDictionary(p => p.Id);

        var supplierIds = rows.Select(x => x.SupplierId).Distinct().ToList();
        var suppliers = await db.BaseSuppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToListAsync();
        var suppliersById = suppliers.ToDictionary(s => s.Id);

        var variantIds = rows.Where(x => (x.VariantId ?? 0) > 0).Select(x => x.VariantId!.Value).Distinct().ToList();
        var variants = variantIds.Count == 0
            ? new List<BaseProductVariant>()
            : await db.BaseProductVariants.AsNoTracking().Where(v => variantIds.Contains(v.Id)).ToListAsync();
        var variantsById = variants.ToDictionary(v => v.Id);

        foreach (var row in rows)
        {
            productsById.TryGetValue(row.ProductId, out var product);
            suppliersById.TryGetValue(row.SupplierId, out var supplier);

            BaseProductVariant? variant = null;
            if ((row.VariantId ?? 0) > 0 && variantsById.TryGetValue(row.VariantId!.Value, out var found))
                variant = found;

            var selectable = ProductSupplierRules.IsSelectable(row);
            var supplierAvailable =
                supplier is not null && !supplier.IsDeleted && supplier.Status == ProductSupplierRules.ActiveStatus;
            var variantAvailable = row.VariantId is null
                || (variant is not null && !variant.IsDeleted
                    && variant.Status == ProductVariantRules.ActiveStatus && variant.ProductId == row.ProductId);

            result.Add(new ProductSupplierDto(
                row.Id,
                row.ProductId,
                product?.ProductCode ?? string.Empty,
                product?.ProductName ?? string.Empty,
                row.VariantId,
                variant?.VariantCode ?? string.Empty,
                variant is null
                    ? string.Empty
                    : ProductVariantRules.DisplayName(variant.VariantCode, variant.Color, variant.Size),
                row.ScopeKey,
                ProductSupplierRules.ScopeText(row.ScopeKey),
                row.SupplierId,
                supplier?.SupplierCode ?? string.Empty,
                supplier?.SupplierName ?? $"供应商#{row.SupplierId}",
                supplierAvailable,
                row.SupplierItemCode,
                row.PurchaseUnit,
                row.MinOrderQty,
                row.LeadTimeDays,
                row.IsPreferred,
                row.Status,
                selectable,
                row.Status == ProductSupplierRules.ActiveStatus ? "启用" : "停用",
                ProductSupplierRules.AvailabilityText(selectable, supplierAvailable, variantAvailable),
                row.Remark,
                row.CreatedAt,
                row.UpdatedAt));
        }

        return result;
    }

    /// <summary>保存并把数据库层唯一索引冲突转换为可读的业务错误（并发写入兜底）</summary>
    private static async Task SaveAsync(IErpDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (LooksLikeUniqueViolation(ex))
        {
            throw BusinessException.Duplicate(DuplicateConflictMessage);
        }
    }

    /// <summary>
    /// 是否为唯一索引 / 唯一约束冲突（SQL Server 2601 重复键 / 2627 违反唯一约束）。
    /// 非唯一性冲突（如结构缺失）原样上抛，不被误报成重复。
    /// </summary>
    private static bool LooksLikeUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("UNIQUE", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
