using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 单证明细行快照服务（ERP-051）。职责：
/// <list type="number">
/// <item><b>清单读取</b>（<see cref="ListAsync"/>）：一次查询取回该单证的明细行（按行序），
/// 再一次批量取回被引用商品资料的可用性标注 —— 不做逐行查库；同时派生行金额合计与
/// 「与单证表头金额是否一致」的**提示**（绝不回写表头）；</item>
/// <item><b>新增行</b>（<see cref="CreateAsync"/>）：单证必须存在、未删除、类型在允许范围内（商业发票 / 装箱单）、
/// 状态处于准备状态；文本 / 数量 / 精度 / 币种 / 箱数与重量全部服务端校验；行金额服务端计算；行序由服务端追加，
/// 显式指定时不得重复；</item>
/// <item><b>修改行</b>（<see cref="UpdateAsync"/>）：同上校验；商品引用未变化时**保留**已登记的商品快照文本
/// （绝不静默从商品资料刷新），只有显式改指商品资料时才重新取权威快照；</item>
/// <item><b>删除行</b>（<see cref="DeleteAsync"/>）：只在准备状态允许的**显式删除**（软删除，保留审计字段），
/// 已提交客户 / 已使用的单证明细既不能修改也不能删除。</item>
/// </list>
/// <para>边界（重要）：本服务只读写 <c>TradeDocumentItems</c> 一张表；<strong>不</strong>改写单证台账表头
/// （含金额 / 状态 / 份数 / 备注）、<strong>不</strong>改写商品资料、销售订单、采购订单、装柜清单、
/// 库存与库存流水、发票与发票关联、退税、费用或财务记录，也<strong>不</strong>做任何价格推断与汇率换算。</para>
/// </summary>
public static class TradeDocumentItemService
{
    // ==================== 1. 清单读取（有界；一次查行 + 一次批量查商品） ====================

    /// <summary>
    /// 单证明细行清单（只读）：按行序返回有界行清单、行金额合计、可维护性与
    /// 「行合计 vs 单证表头金额」提示；单证不存在 / 已删除时返回 404 业务错误。
    /// </summary>
    public static async Task<TradeDocumentItemListDto> ListAsync(
        IErpDbContext db, long documentId, int take = TradeDocumentItemRules.MaxLinesPerDocument)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (documentId <= 0) throw BusinessException.InvalidParameter("请指定要查看明细行的单证");

        var document = await LoadDocumentAsync(db, documentId);
        var limit = NormalizeTake(take);

        // 一次查询取回有界明细行（多取一行用于判断是否被截断），再按行序升序展示
        var rows = await db.TradeDocumentItems.AsNoTracking()
            .Where(i => !i.IsDeleted && i.TradeDocumentId == document.Id)
            .OrderBy(i => i.LineNo).ThenBy(i => i.Id)
            .Take(limit + 1)
            .ToListAsync();

        var truncated = rows.Count > limit;
        if (truncated) rows = rows.Take(limit).ToList();

        // 一次批量装载被引用商品（避免逐行查询）；商品停用 / 删除只做只读标注
        var products = await LoadProductsAsync(db, rows);

        var items = rows.Select(row => Map(row, document, products)).ToList();
        var currency = TradeDocumentItemRules.NormalizeCurrency(document.Currency);
        var lineTotal = items.Sum(i => i.LineAmount);

        var packageLines = rows.Count(r => r.PackageCount is not null);
        var weightLines = rows.Count(r => r.NetWeight is not null || r.GrossWeight is not null);

        var mismatchText = truncated
            ? $"明细行超过单次读取上限 {limit} 行：以下行合计只是前 {items.Count} 行的部分合计，"
              + "不判定差异（请缩小范围后逐页核对，系统不会据此改写单证金额）"
            : TradeDocumentItemRules.AmountMismatchText(document.Amount, lineTotal, currency, items.Count);

        var docTypeSupported = TradeDocumentItemRules.IsSupportedDocType(document.DocType);

        return new TradeDocumentItemListDto(
            document.Id,
            (document.DocNo ?? string.Empty).Trim(),
            (document.DocType ?? string.Empty).Trim(),
            (document.Status ?? string.Empty).Trim(),
            docTypeSupported,
            TradeDocumentItemRules.DocTypeSupportText(docTypeSupported, document.DocType),
            docTypeSupported && TradeDocumentItemRules.IsEditableStatus(document.Status),
            TradeDocumentItemRules.EditabilityText(document),
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            document.Amount,
            TradeDocumentItemRules.IsHeaderAmountRecorded(document.Amount),
            items.Count,
            truncated,
            limit,
            lineTotal,
            !truncated && TradeDocumentItemRules.HasAmountMismatch(document.Amount, lineTotal, currency),
            mismatchText,
            packageLines,
            packageLines == 0 ? null : rows.Where(r => r.PackageCount is not null).Sum(r => r.PackageCount!.Value),
            weightLines,
            weightLines == 0 ? null : rows.Where(r => r.NetWeight is not null).Sum(r => r.NetWeight!.Value),
            weightLines == 0 ? null : rows.Where(r => r.GrossWeight is not null).Sum(r => r.GrossWeight!.Value),
            TradeDocumentItemRules.RuleText,
            TradeDocumentItemRules.BoundaryText,
            items);
    }

    // ==================== 2. 新增行（快照写入；行金额服务端计算） ====================

    /// <summary>
    /// 新增一条明细行快照：单证必须存在 / 未删除、类型允许（商业发票 / 装箱单）、状态处于准备状态；
    /// 数量 / 单价 / 精度 / 币种 / 箱数与重量全部服务端校验；行金额服务端计算（客户端金额不被信任）；
    /// 行序留空由服务端追加，显式指定时同一单证内不得重复。
    /// </summary>
    public static async Task<TradeDocumentItemDto> CreateAsync(
        IErpDbContext db, long documentId, TradeDocumentItemSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (documentId <= 0) throw BusinessException.InvalidParameter("请指定要新增明细行的单证");

        var document = await LoadDocumentAsync(db, documentId);
        var pricingAllowed = EnsureMutable(document, out var currency);

        var existing = await db.TradeDocumentItems.AsNoTracking()
            .Where(i => !i.IsDeleted && i.TradeDocumentId == document.Id)
            .Select(i => i.LineNo)
            .ToListAsync();

        if (existing.Count >= TradeDocumentItemRules.MaxLinesPerDocument)
            throw BusinessException.RuleConflict(
                $"单证「{Safe(document.DocNo)}」的明细行已达上限 {TradeDocumentItemRules.MaxLinesPerDocument} 行："
                + "如需新增请先删除不需要的行（删除只在准备状态允许）");

        var requested = TradeDocumentItemRules.NormalizeLineOrder(dto.LineOrder);
        var lineNo = requested ?? (existing.Count == 0 ? 1 : existing.Max(no => no) + 1);
        if (lineNo > TradeDocumentItemRules.MaxLineOrder)
            throw BusinessException.RuleConflict(
                $"单证「{Safe(document.DocNo)}」的行序已达上限 {TradeDocumentItemRules.MaxLineOrder}：不能再追加明细行");
        if (existing.Contains(lineNo))
            throw BusinessException.Duplicate(
                $"单证「{Safe(document.DocNo)}」已存在行序 {lineNo} 的明细行：请改用其他行序或留空由系统追加");

        var values = await BuildValuesAsync(db, dto, existingItem: null, pricingAllowed, currency);

        var entity = new TradeDocumentItem
        {
            TradeDocumentId = document.Id,
            LineNo = lineNo,
            ProductId = values.ProductId,
            ProductCode = values.ProductCode,
            ProductNameCn = values.ProductNameCn,
            ProductNameEn = values.ProductNameEn,
            Spec = values.Spec,
            Quantity = values.Quantity,
            Unit = values.Unit,
            UnitPrice = values.UnitPrice,
            LineAmount = values.LineAmount,
            PackageCount = values.PackageCount,
            NetWeight = values.NetWeight,
            GrossWeight = values.GrossWeight,
            Currency = currency,
            Remark = values.Remark
        };

        db.TradeDocumentItems.Add(entity);
        await db.SaveChangesAsync();

        return Map(entity, document, await LoadProductsAsync(db, new List<TradeDocumentItem> { entity }));
    }

    // ==================== 3. 修改行（准备状态；商品引用未变化时不刷新历史快照） ====================

    /// <summary>
    /// 修改一条明细行：与新增同一套校验；**商品引用未变化时保留已登记的商品快照文本**
    /// （绝不静默从商品资料刷新）；只有显式改指商品资料时才重新取商品资料的权威快照；
    /// 行金额一律服务端重算；已提交客户 / 已使用的单证明细一律拒绝修改。
    /// </summary>
    public static async Task<TradeDocumentItemDto> UpdateAsync(
        IErpDbContext db, long itemId, TradeDocumentItemSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var item = await db.TradeDocumentItems
            .FirstOrDefaultAsync(i => i.Id == itemId && !i.IsDeleted)
            ?? throw BusinessException.NotFound("明细行不存在或已删除：不能修改（历史行请通过清单查看）");

        var document = await LoadDocumentAsync(db, item.TradeDocumentId);
        var pricingAllowed = EnsureMutable(document, out var currency);

        var requested = TradeDocumentItemRules.NormalizeLineOrder(dto.LineOrder);
        if (requested.HasValue && requested.Value != item.LineNo)
        {
            var duplicate = await db.TradeDocumentItems.AsNoTracking()
                .AnyAsync(i => !i.IsDeleted && i.TradeDocumentId == item.TradeDocumentId
                               && i.Id != item.Id && i.LineNo == requested.Value);
            if (duplicate)
                throw BusinessException.Duplicate(
                    $"单证「{Safe(document.DocNo)}」已存在行序 {requested.Value} 的明细行：请改用其他行序");
        }

        var values = await BuildValuesAsync(db, dto, item, pricingAllowed, currency);

        item.LineNo = requested ?? item.LineNo;
        item.ProductId = values.ProductId;
        item.ProductCode = values.ProductCode;
        item.ProductNameCn = values.ProductNameCn;
        item.ProductNameEn = values.ProductNameEn;
        item.Spec = values.Spec;
        item.Quantity = values.Quantity;
        item.Unit = values.Unit;
        item.UnitPrice = values.UnitPrice;
        item.LineAmount = values.LineAmount;
        item.PackageCount = values.PackageCount;
        item.NetWeight = values.NetWeight;
        item.GrossWeight = values.GrossWeight;
        item.Currency = currency;
        item.Remark = values.Remark;

        await db.SaveChangesAsync();

        return Map(item, document, await LoadProductsAsync(db, new List<TradeDocumentItem> { item }));
    }

    // ==================== 4. 删除行（仅准备状态；显式删除，保留审计字段） ====================

    /// <summary>
    /// 显式删除一条明细行（软删除，审计字段保留）：只在准备状态允许；已提交客户 / 已使用以及
    /// 不支持明细行的单证类型一律拒绝 —— 已冻结的单证快照既不能修改也不能删除，也不提供静默替换。
    /// </summary>
    public static async Task<TradeDocumentItemDto> DeleteAsync(IErpDbContext db, long itemId)
    {
        ArgumentNullException.ThrowIfNull(db);

        var item = await db.TradeDocumentItems
            .FirstOrDefaultAsync(i => i.Id == itemId && !i.IsDeleted)
            ?? throw BusinessException.NotFound("明细行不存在或已删除：不能重复删除");

        var document = await LoadDocumentAsync(db, item.TradeDocumentId);
        EnsureMutable(document, out _);

        item.IsDeleted = true;
        await db.SaveChangesAsync();

        return Map(item, document, await LoadProductsAsync(db, new List<TradeDocumentItem> { item }));
    }

    // ==================== 5. 行值构建（服务端权威快照与计算） ====================

    /// <summary>
    /// 构建一行明细的服务端权威值：
    /// 1) 数值与文本全部经规则校验（数量 &gt; 0、单价与重量精度、毛重 ≥ 净重、箱数有界、文本有界且为纯文本）；
    /// 2) 商品引用：引用商品资料时（新增，或修改时**显式改指**）由服务端取商品资料的编码 / 中英文名称 /
    ///    规格 / 单位作为快照；修改时商品引用未变化则**保留已登记快照**（绝不静默刷新历史数据）；
    ///    未引用商品资料时使用人工录入文本，并要求商品编码或中文名称至少填写一项；
    /// 3) 行金额一律服务端计算（数量 × 单价，按币种精度取整），客户端金额不被信任。
    /// </summary>
    private static async Task<ItemValues> BuildValuesAsync(
        IErpDbContext db, TradeDocumentItemSaveDto dto, TradeDocumentItem? existingItem,
        bool pricingAllowed, string currency)
    {
        var quantity = TradeDocumentItemRules.NormalizeQuantity(dto.Quantity);
        var unitPrice = TradeDocumentItemRules.NormalizeUnitPrice(dto.UnitPrice, pricingAllowed);
        var packageCount = TradeDocumentItemRules.NormalizePackageCount(dto.PackageCount);
        var netWeight = TradeDocumentItemRules.NormalizeWeight(dto.NetWeight, "净重");
        var grossWeight = TradeDocumentItemRules.NormalizeWeight(dto.GrossWeight, "毛重");
        TradeDocumentItemRules.EnsureGrossNotBelowNet(netWeight, grossWeight);
        var remark = TradeDocumentItemRules.NormalizeOptionalText(
            dto.Remark, TradeDocumentItemRules.MaxRemarkLength, "行备注");

        var productId = dto.ProductId > 0 ? dto.ProductId : 0;
        var referenceUnchanged = existingItem is not null && existingItem.ProductId == productId;

        string productCode, productNameCn, productNameEn, spec, unit;

        if (productId > 0 && referenceUnchanged)
        {
            // 商品引用未变化：保留已登记的快照文本（不因商品资料被改动而静默刷新历史行）
            productCode = (existingItem!.ProductCode ?? string.Empty).Trim();
            productNameCn = (existingItem.ProductNameCn ?? string.Empty).Trim();
            productNameEn = (existingItem.ProductNameEn ?? string.Empty).Trim();
            spec = (existingItem.Spec ?? string.Empty).Trim();
            unit = (existingItem.Unit ?? string.Empty).Trim();
        }
        else if (productId > 0)
        {
            var product = await db.BaseProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted)
                ?? throw BusinessException.NotFound(
                    $"商品资料（Id={productId}）不存在或已删除，不能作为明细行的商品引用");

            productCode = (product.ProductCode ?? string.Empty).Trim();
            productNameCn = (product.ProductName ?? string.Empty).Trim();
            productNameEn = (product.EnglishName ?? string.Empty).Trim();
            spec = (product.Spec ?? string.Empty).Trim();
            unit = (product.Unit ?? string.Empty).Trim();
        }
        else
        {
            productCode = TradeDocumentItemRules.NormalizeOptionalText(
                dto.ProductCode, TradeDocumentItemRules.MaxProductCodeLength, "商品编码");
            productNameCn = TradeDocumentItemRules.NormalizeOptionalText(
                dto.ProductNameCn, TradeDocumentItemRules.MaxProductTextLength, "商品中文名称");
            productNameEn = TradeDocumentItemRules.NormalizeOptionalText(
                dto.ProductNameEn, TradeDocumentItemRules.MaxProductTextLength, "商品英文名称");
            spec = TradeDocumentItemRules.NormalizeOptionalText(
                dto.Spec, TradeDocumentItemRules.MaxProductTextLength, "规格型号");
            unit = TradeDocumentItemRules.NormalizeOptionalText(
                dto.Unit, TradeDocumentItemRules.MaxUnitLength, "单位");
            TradeDocumentItemRules.EnsureProductIdentity(productCode, productNameCn, productId);
        }

        return new ItemValues(
            productId, productCode, productNameCn, productNameEn, spec, quantity, unit, unitPrice,
            TradeDocumentItemRules.ComputeLineAmount(quantity, unitPrice, currency),
            packageCount, netWeight, grossWeight, remark);
    }

    /// <summary>
    /// 单证可变性前置校验（新增 / 修改 / 删除共用）：类型必须在白名单内、状态必须处于准备状态；
    /// 返回该类型是否允许价格口径（只有商业发票允许），并输出行应使用的币种（定价行做币种严格校验）。
    /// </summary>
    private static bool EnsureMutable(TradeDocument document, out string currency)
    {
        TradeDocumentItemRules.EnsureSupportedDocType(document.DocType);
        TradeDocumentItemRules.EnsureParentEditable(document);

        var pricingAllowed = IsPricingDocType(document.DocType);
        currency = pricingAllowed
            ? TradeDocumentItemRules.NormalizePricingCurrency(document.Currency)
            : TradeDocumentItemRules.NormalizeCurrency(document.Currency);
        return pricingAllowed;
    }

    /// <summary>单证类型是否含价格口径（商业发票 = 含单价与金额；装箱单 = 只含数量 / 箱数 / 重量）</summary>
    private static bool IsPricingDocType(string? docType)
        => string.Equals((docType ?? string.Empty).Trim(),
            TradeDocumentItemRules.CommercialInvoiceDocType, StringComparison.Ordinal);

    /// <summary>装载单证（不存在 / 已删除一律 404；只读查询，不做任何写入）</summary>
    private static async Task<TradeDocument> LoadDocumentAsync(IErpDbContext db, long documentId)
        => await db.TradeDocuments.AsNoTracking()
               .FirstOrDefaultAsync(d => d.Id == documentId && !d.IsDeleted)
           ?? throw BusinessException.NotFound("单证不存在或已删除：无法维护或查看明细行");

    /// <summary>批量装载被引用商品资料（一次查询；不做逐行查库，停用 / 删除只作只读标注）</summary>
    private static async Task<Dictionary<long, BaseProduct>> LoadProductsAsync(
        IErpDbContext db, IReadOnlyList<TradeDocumentItem> rows)
    {
        var ids = rows.Where(r => r.ProductId > 0).Select(r => r.ProductId).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, BaseProduct>();

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync();
        return products.ToDictionary(p => p.Id);
    }

    /// <summary>读取条数规范化（有界：非正或超过上限一律按上限处理）</summary>
    private static int NormalizeTake(int take)
        => take <= 0 || take > TradeDocumentItemRules.MaxLinesPerDocument
            ? TradeDocumentItemRules.MaxLinesPerDocument
            : take;

    /// <summary>行实体 → DTO（含商品引用只读可用性标注；纯映射，不写库）</summary>
    private static TradeDocumentItemDto Map(
        TradeDocumentItem row, TradeDocument document, Dictionary<long, BaseProduct> products)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = TradeDocumentItemRules.NormalizeCurrency(row.Currency);
        products.TryGetValue(row.ProductId, out var product);
        var productAvailable = row.ProductId <= 0 || (product is not null && !product.IsDeleted);

        return new TradeDocumentItemDto(
            row.Id,
            row.TradeDocumentId,
            (document.DocNo ?? string.Empty).Trim(),
            (document.DocType ?? string.Empty).Trim(),
            row.LineNo,
            row.ProductId,
            row.ProductCode ?? string.Empty,
            row.ProductNameCn ?? string.Empty,
            row.ProductNameEn ?? string.Empty,
            row.Spec ?? string.Empty,
            row.Quantity,
            row.Unit ?? string.Empty,
            row.UnitPrice,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            row.LineAmount,
            row.PackageCount,
            row.NetWeight,
            row.GrossWeight,
            row.Remark ?? string.Empty,
            productAvailable,
            TradeDocumentItemRules.ProductAvailabilityText(row.ProductId, product),
            row.CreatedAt,
            row.UpdatedAt);
    }

    /// <summary>有界回显（错误提示中不整段抛出超长 / 不安全原值）</summary>
    private static string Safe(string? value, int max = 60)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>一行明细的服务端权威值（快照文本 + 数值 + 服务端计算的金额；不落库的中间载体）</summary>
    private sealed record ItemValues(
        long ProductId,
        string ProductCode,
        string ProductNameCn,
        string ProductNameEn,
        string Spec,
        decimal Quantity,
        string Unit,
        decimal UnitPrice,
        decimal LineAmount,
        int? PackageCount,
        decimal? NetWeight,
        decimal? GrossWeight,
        string Remark);
}
