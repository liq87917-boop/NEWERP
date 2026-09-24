using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 商品规格变体服务（ERP-037）。职责：
/// <list type="number">
/// <item><b>维护</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/> / <see cref="SetStatusAsync"/> /
/// <see cref="DeleteAsync"/>）：只写商品资料自己的规格子表，服务端统一规范化编码与「颜色 + 尺码」组合键，
/// 并通过 <see cref="ProductVariantRules"/> 拒绝重复编码与重复的启用颜色/尺码组合；</item>
/// <item><b>读取</b>（<see cref="ListAsync"/>）：返回该商品的全部（含停用）规格，按上限收敛为有界视图；
/// 停用规格照常可读并显式标注「不可再被新选中」；</item>
/// <item><b>可选用口径</b>（<see cref="LoadSelectableAsync"/>）：只返回启用中的规格，因此停用 / 已删除的
/// 规格无法被新选用（历史引用仍可读）；</item>
/// <item><b>列表 / 详情标注</b>（<see cref="AnnotateAsync"/>）：为商品补写启用规格数与规格总数。</item>
/// </list>
/// <para>边界（重要）：除 <c>BaseProductVariants</c> 自身外不写任何数据 —— 不改动询价 / 报价 / PI /
/// 销售订单 / 采购订单的行，也不写库存 <c>Stocks</c> 与库存流水 <c>StockMovements</c>：
/// 规格是主数据细分，既不拆分已有库存，也不重算成本。</para>
/// </summary>
public static class ProductVariantService
{
    /// <summary>并发 / 唯一索引兜底时的对外文案（编码或颜色+尺码组合被并发写入占用）</summary>
    public const string DuplicateConflictMessage = "该规格正在被其他请求写入（编码或颜色+尺码组合重复），请重试";

    /// <summary>
    /// 校验商品存在（未删除），返回商品实体；规格必须挂在既有商品下。
    /// </summary>
    public static async Task<BaseProduct> EnsureProductAsync(IErpDbContext db, long productId)
    {
        if (productId <= 0)
            throw BusinessException.InvalidParameter("商品 Id 不合法");
        return await db.BaseProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted)
            ?? throw BusinessException.NotFound($"商品（Id={productId}）不存在或已删除，不能维护规格");
    }

    /// <summary>
    /// 读取某商品的规格列表（<b>只读，不写库</b>）：默认包含停用规格（历史可读），
    /// <paramref name="activeOnly"/> 为真时只返回启用中的规格（可选用口径）；
    /// 结果按上限 <see cref="ProductVariantRules.MaxVariantsPerProduct"/> 收敛，保证视图有界。
    /// </summary>
    public static async Task<List<ProductVariantDto>> ListAsync(
        IErpDbContext db, long productId, bool activeOnly = false, int take = ProductVariantRules.MaxVariantsPerProduct)
    {
        await EnsureProductAsync(db, productId);

        var bound = take <= 0 ? ProductVariantRules.MaxVariantsPerProduct
            : Math.Min(take, ProductVariantRules.MaxVariantsPerProduct);

        var query = db.BaseProductVariants.AsNoTracking()
            .Where(v => v.ProductId == productId && !v.IsDeleted);
        if (activeOnly)
            query = query.Where(v => v.Status == ProductVariantRules.ActiveStatus);

        var variants = await query
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Id)
            .Take(bound)
            .ToListAsync();

        return variants.Select(ToDto).ToList();
    }

    /// <summary>
    /// 读取「可选用」的规格（<b>只读</b>）：只返回启用中的规格。
    /// 与写入校验口径（<see cref="ProductVariantRules.IsSelectable"/>）一致，
    /// 因此停用 / 已删除的规格不会出现在该视图中，无法被新选用。
    /// <para>历史引用不依赖本方法：规格明细视图（<see cref="ListAsync"/>）仍照常返回停用规格并标注。</para>
    /// </summary>
    public static Task<List<ProductVariantDto>> LoadSelectableAsync(IErpDbContext db, long productId) =>
        ListAsync(db, productId, activeOnly: true);

    /// <summary>
    /// 读取标注（<b>不写库</b>）：为商品列表 / 详情补写启用规格数与规格总数。
    /// 一次查询解析全部商品的规格计数，避免逐行查询。
    /// </summary>
    public static async Task AnnotateAsync(IErpDbContext db, IEnumerable<BaseProduct> products)
    {
        var list = products as IList<BaseProduct> ?? products.ToList();
        if (list.Count == 0) return;

        var ids = list.Select(p => p.Id).Distinct().ToList();
        var rows = await db.BaseProductVariants.AsNoTracking()
            .Where(v => !v.IsDeleted && ids.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.Status })
            .ToListAsync();

        var counted = rows
            .GroupBy(v => v.ProductId)
            .ToDictionary(g => g.Key, g => new
            {
                Total = g.Count(),
                Active = g.Count(v => v.Status == ProductVariantRules.ActiveStatus)
            });

        foreach (var product in list)
        {
            var found = counted.TryGetValue(product.Id, out var counts);
            product.VariantCount = found ? counts!.Active : 0;
            product.VariantTotalCount = found ? counts!.Total : 0;
        }
    }

    /// <summary>新增规格（仅写 <c>BaseProductVariants</c>）</summary>
    public static async Task<ProductVariantDto> CreateAsync(IErpDbContext db, long productId, ProductVariantSaveDto dto)
    {
        await EnsureProductAsync(db, productId);
        if (dto is null)
            throw BusinessException.InvalidParameter("规格数据不能为空");

        var normalized = ProductVariantRules.Normalize(dto.VariantCode, dto.Color, dto.Size, dto.Remark);
        var status = NormalizeStatus(dto.Status);
        var siblings = await LoadSiblingsAsync(db, productId);

        ProductVariantRules.EnsureWithinBound(siblings.Count);
        ProductVariantRules.EnsureCodeUnique(siblings, normalized.VariantCode);
        if (status == ProductVariantRules.ActiveStatus)
            ProductVariantRules.EnsureColorSizeUnique(siblings, normalized.ColorSizeKey);

        var variant = new BaseProductVariant
        {
            ProductId = productId,
            VariantCode = normalized.VariantCode,
            Color = normalized.Color,
            Size = normalized.Size,
            ColorSizeKey = normalized.ColorSizeKey,
            Status = status,
            SortOrder = dto.SortOrder ?? 0,
            Remark = normalized.Remark
        };

        db.BaseProductVariants.Add(variant);
        await SaveAsync(db);
        return ToDto(variant);
    }

    /// <summary>
    /// 修改规格（仅写 <c>BaseProductVariants</c>）：编码 / 颜色 / 尺码重新规范化并重新做唯一性判定；
    /// 停用规格可以修改内容（仍是停用），启用规格则必须满足「启用组合唯一」。
    /// </summary>
    public static async Task<ProductVariantDto> UpdateAsync(
        IErpDbContext db, long productId, long variantId, ProductVariantSaveDto dto)
    {
        var variant = await FindAsync(db, productId, variantId);
        if (dto is null)
            throw BusinessException.InvalidParameter("规格数据不能为空");

        var normalized = ProductVariantRules.Normalize(dto.VariantCode, dto.Color, dto.Size, dto.Remark);
        var status = dto.Status.HasValue ? NormalizeStatus(dto.Status) : variant.Status;
        var siblings = await LoadSiblingsAsync(db, productId);

        ProductVariantRules.EnsureCodeUnique(siblings, normalized.VariantCode, variant.Id);
        if (status == ProductVariantRules.ActiveStatus)
            ProductVariantRules.EnsureColorSizeUnique(siblings, normalized.ColorSizeKey, variant.Id);

        variant.VariantCode = normalized.VariantCode;
        variant.Color = normalized.Color;
        variant.Size = normalized.Size;
        variant.ColorSizeKey = normalized.ColorSizeKey;
        variant.Status = status;
        variant.SortOrder = dto.SortOrder ?? variant.SortOrder;
        variant.Remark = normalized.Remark;

        await SaveAsync(db);
        return ToDto(variant);
    }

    /// <summary>停用规格：历史仍可读（列表照常返回并标注），但不能再被新选用</summary>
    public static Task<ProductVariantDto> DisableAsync(IErpDbContext db, long productId, long variantId) =>
        SetStatusAsync(db, productId, variantId, ProductVariantRules.DisabledStatus);

    /// <summary>重新启用规格：必须仍然满足「启用中颜色 + 尺码组合唯一」，否则拒绝</summary>
    public static Task<ProductVariantDto> EnableAsync(IErpDbContext db, long productId, long variantId) =>
        SetStatusAsync(db, productId, variantId, ProductVariantRules.ActiveStatus);

    /// <summary>
    /// 设置规格状态（仅写 <c>BaseProductVariants</c>）：
    /// 启用时重新做「启用组合唯一」判定，因此不会出现两条同时启用且颜色 / 尺码相同的规格。
    /// </summary>
    public static async Task<ProductVariantDto> SetStatusAsync(
        IErpDbContext db, long productId, long variantId, int status)
    {
        var variant = await FindAsync(db, productId, variantId);
        var normalizedStatus = NormalizeStatus(status);

        if (normalizedStatus == ProductVariantRules.ActiveStatus)
        {
            var siblings = await LoadSiblingsAsync(db, productId);
            ProductVariantRules.EnsureColorSizeUnique(
                siblings, ProductVariantRules.BuildColorSizeKey(variant.Color, variant.Size), variant.Id);
        }

        variant.Status = normalizedStatus;
        await SaveAsync(db);
        return ToDto(variant);
    }

    /// <summary>
    /// 删除规格（<b>只做本表软删除</b>，保留行以便历史引用可读）：
    /// 不物理删除、不改写任何单据行、不动库存与库存流水。
    /// </summary>
    public static async Task DeleteAsync(IErpDbContext db, long productId, long variantId)
    {
        var variant = await FindAsync(db, productId, variantId);
        variant.IsDeleted = true;
        await SaveAsync(db);
    }

    /// <summary>按「商品 + 规格」定位未删除规格；不属于该商品时按不存在处理</summary>
    private static async Task<BaseProductVariant> FindAsync(IErpDbContext db, long productId, long variantId)
    {
        await EnsureProductAsync(db, productId);
        return await db.BaseProductVariants
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId && !v.IsDeleted)
            ?? throw BusinessException.NotFound($"规格（Id={variantId}）在该商品下不存在或已删除");
    }

    /// <summary>该商品下未删除的全部规格（含停用），用于唯一性判定</summary>
    private static async Task<List<BaseProductVariant>> LoadSiblingsAsync(IErpDbContext db, long productId) =>
        await db.BaseProductVariants.AsNoTracking()
            .Where(v => v.ProductId == productId && !v.IsDeleted)
            .ToListAsync();

    /// <summary>状态口径：只接受 0（停用）与 1（启用），其余按参数错误拒绝</summary>
    private static int NormalizeStatus(int? status) => status switch
    {
        null => ProductVariantRules.ActiveStatus,
        ProductVariantRules.ActiveStatus => ProductVariantRules.ActiveStatus,
        ProductVariantRules.DisabledStatus => ProductVariantRules.DisabledStatus,
        _ => throw BusinessException.InvalidParameter("规格状态只能是 1（启用）或 0（停用）")
    };

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

    /// <summary>实体 → 读取 DTO（含服务端计算的展示名与可选用标注）</summary>
    private static ProductVariantDto ToDto(BaseProductVariant variant) => new(
        variant.Id,
        variant.ProductId,
        variant.VariantCode,
        variant.Color,
        variant.Size,
        ProductVariantRules.DisplayName(variant.VariantCode, variant.Color, variant.Size),
        variant.Status,
        ProductVariantRules.IsSelectable(variant),
        variant.Status == ProductVariantRules.ActiveStatus ? "启用" : "停用",
        variant.ColorSizeKey,
        variant.SortOrder,
        variant.Remark,
        variant.CreatedAt,
        variant.UpdatedAt);
}
