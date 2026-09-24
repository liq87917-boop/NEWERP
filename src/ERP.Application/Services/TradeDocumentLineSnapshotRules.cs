using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 单证生成时的**来源明细 → 明细行快照**映射规则（ERP-052，纯规则 + 有界只读装载，便于逐条单测）。
/// <para>定位：把销售订单明细 / 装柜清单明细在**生成单证当时**复制成 ERP-051 的行快照
/// （<see cref="TradeDocumentItem"/>），使商业发票与装箱单可逐行核对与追溯。</para>
/// <para>口径（重要，全部 fail-closed，宁可拒绝也不臆造）：</para>
/// <list type="bullet">
/// <item><b>只取权威来源值</b>：商品名称 / 规格 / 数量 / 单位 / 单价一律取来源单据明细自身的值；
/// 商品编码与英文名称来源明细没有该列，只在引用商品资料（<c>ProductId &gt; 0</c>）时取商品资料的**权威快照**，
/// 否则留空；装柜清单明细没有计量单位列，未引用商品资料时单位留空（**不按文本猜测**）；</item>
/// <item><b>箱数 / 净重 / 毛重</b>：只有来源单据确有该列时才写入 —— 装柜清单明细提供「箱数（仅整数箱数）」与
/// 「毛重」，**没有**净重列（净重一律留空）；销售订单明细不提供任何箱数 / 重量证据，一律留空。
/// 未登记保持 <c>null</c>，绝不写成 0、绝不按商品资料或自由文本推断；</item>
/// <item><b>来源以 0 表示「未登记」</b>：装柜清单明细的箱数 / 毛重是非空列并以 0 为默认值，
/// 因此 <c>&lt;= 0</c> 按「未登记」处理（留空），并在生成摘要里照实说明；</item>
/// <item><b>不静默丢弃来源明细</b>：来源明细数量非正 / 超精度、单价为负、或既无商品引用又无商品名称 / 编码时，
/// 生成与预填都**拒绝**（<see cref="BusinessException"/>），要求先修正来源单据；
/// 来源明细行数超过 <see cref="TradeDocumentItemRules.MaxLinesPerDocument"/> 同样拒绝（不截断写入）；</item>
/// <item><b>文本按纯文本有界写入</b>：去首尾空白、折叠换行 / 制表符为空格、剔除控制字符与
/// HTML / 脚本标记字符（<c>&lt;</c> / <c>&gt;</c>）、按列长截断，因此安全文本永远不会被当成标记或公式执行；</item>
/// <item><b>行金额服务端计算</b>：数量 × 单价并按币种精度取整（装箱单行恒为 0），不接受任何来源金额；</item>
/// <item><b>只读来源</b>：本类只读销售订单明细 / 装柜清单明细 / 商品资料，不改写它们，
/// 也不触碰库存、发票、退税、费用与财务记录。</item>
/// </list>
/// </summary>
public static class TradeDocumentLineSnapshotRules
{
    /// <summary>来源单据明细的**有界**单次读取上限（与单证明细行上限一致：超出即拒绝，不截断写入）</summary>
    public const int MaxSourceDetails = TradeDocumentItemRules.MaxLinesPerDocument;

    /// <summary>来源销售订单（摘要 / 证据文案用）</summary>
    public const string SalesOrderSourceLabel = "销售订单";

    /// <summary>来源装柜清单（摘要 / 证据文案用）</summary>
    public const string LoadingListSourceLabel = "装柜清单";

    /// <summary>重量在小数上与目标列的精度上限（与 TradeDocumentItems 列精度一致）</summary>
    public const int MaxWeightDecimals = TradeDocumentItemRules.MaxAmountDecimals;

    // ==================== 1. 文本与数值清洗（纯函数） ====================

    /// <summary>
    /// 来源文本 → 快照文本（纯文本、有界）：去首尾空白；把换行 / 制表符折叠为单个空格；
    /// 剔除控制字符与 HTML / 脚本标记字符（<c>&lt;</c> / <c>&gt;</c>）；按目标列长截断。
    /// <para>截断与剔除只影响文本载体，不改变来源事实（数量 / 单价 / 单位等数值一律照实保留），
    /// 也保证界面、Excel 与打印输出的是纯文本而不是可执行标记。</para>
    /// </summary>
    public static string SanitizeText(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maxLength));
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (builder.Length >= maxLength) break;

            if (ch is '\r' or '\n' or '\t')
            {
                if (!lastWasSpace && builder.Length > 0) { builder.Append(' '); lastWasSpace = true; }
                continue;
            }

            if (char.IsControl(ch) || ch is '<' or '>') continue;

            lastWasSpace = false;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>商品编码的快照文本（列长 50）</summary>
    public static string SanitizeProductCode(string? value)
        => SanitizeText(value, TradeDocumentItemRules.MaxProductCodeLength);

    /// <summary>商品中英文名称 / 规格的快照文本（列长 200）</summary>
    public static string SanitizeProductText(string? value)
        => SanitizeText(value, TradeDocumentItemRules.MaxProductTextLength);

    /// <summary>单位的快照文本（列长 20）</summary>
    public static string SanitizeUnit(string? value)
        => SanitizeText(value, TradeDocumentItemRules.MaxUnitLength);

    /// <summary>行备注的快照文本（列长 500）</summary>
    public static string SanitizeRemark(string? value)
        => SanitizeText(value, TradeDocumentItemRules.MaxRemarkLength);

    /// <summary>数量：必须大于 0、最多 4 位小数；否则返回 null（由调用方 fail-closed 拒绝生成）</summary>
    public static decimal? NormalizeQuantity(decimal quantity)
    {
        if (quantity <= 0m) return null;
        if (decimal.Round(quantity, TradeDocumentItemRules.MaxAmountDecimals) != quantity) return null;
        return quantity;
    }

    /// <summary>单价：不能为负、最多 4 位小数；否则返回 null（由调用方 fail-closed 拒绝生成）</summary>
    public static decimal? NormalizeUnitPrice(decimal unitPrice)
    {
        if (unitPrice < 0m) return null;
        if (decimal.Round(unitPrice, TradeDocumentItemRules.MaxAmountDecimals) != unitPrice) return null;
        return unitPrice;
    }

    /// <summary>
    /// 装柜清单明细的箱数 → 明细行箱数：<c>&lt;= 0</c>（来源以 0 表示未登记）或非整数箱数一律返回 null
    /// （留空而不是四舍五入或写成 0）；超过有界上限同样返回 null。
    /// </summary>
    public static int? PackageCountOf(decimal? sourceCartons)
    {
        if (sourceCartons is null) return null;
        var value = sourceCartons.Value;
        if (value <= 0m) return null;                                     // 来源 0 / 负数 = 未登记
        if (decimal.Remainder(value, 1m) != 0m) return null;              // 非整数箱数：不四舍五入
        if (value > TradeDocumentItemRules.MaxPackageCount) return null;  // 越界：不写入异常值
        return decimal.ToInt32(value);
    }

    /// <summary>来源明细的箱数是否因「非整数 / 越界」而未能写入箱数列（用于摘要照实说明）</summary>
    public static bool IsNonIntegralPackageCount(decimal? sourceCartons)
        => sourceCartons is > 0m
           && (decimal.Remainder(sourceCartons.Value, 1m) != 0m
               || sourceCartons.Value > TradeDocumentItemRules.MaxPackageCount);

    /// <summary>
    /// 装柜清单明细的毛重 → 明细行毛重：<c>&lt;= 0</c>（来源以 0 表示未登记）返回 null（留空而不是 0）；
    /// 仅在超过列精度（4 位小数）时收敛到 4 位，不改变量级。
    /// </summary>
    public static decimal? GrossWeightOf(decimal? sourceWeight)
    {
        if (sourceWeight is null) return null;
        var value = sourceWeight.Value;
        return value <= 0m ? null : decimal.Round(value, MaxWeightDecimals);
    }

    // ==================== 2. 来源明细的有界只读装载 ====================

    /// <summary>
    /// 装载销售订单明细（有界：最多 <see cref="MaxSourceDetails"/> + 1 行，用于判定是否超出上限）；
    /// 只读、按 Id 升序（与订单明细录入顺序一致），不按自由文本匹配任何记录。
    /// </summary>
    public static async Task<List<SalesOrderDetail>> LoadSalesOrderDetailsAsync(
        IErpDbContext db, long salesOrderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId <= 0) return new List<SalesOrderDetail>();

        return await db.SalesOrderDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && d.SalesOrderId == salesOrderId)
            .OrderBy(d => d.Id)
            .Take(MaxSourceDetails + 1)
            .ToListAsync(ct);
    }

    /// <summary>装载装柜清单明细（有界：最多 <see cref="MaxSourceDetails"/> + 1 行）；只读、按 Id 升序。</summary>
    public static async Task<List<ContainerLoadingDetail>> LoadLoadingListDetailsAsync(
        IErpDbContext db, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (loadingListId <= 0) return new List<ContainerLoadingDetail>();

        return await db.ContainerLoadingDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && d.LoadingListId == loadingListId)
            .OrderBy(d => d.Id)
            .Take(MaxSourceDetails + 1)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 按商品 Id 批量装载商品资料（**一次**查询，避免逐行查库）：用于写入商品编码 / 英文名称 / 规格 / 单位的
    /// 权威快照；商品被停用 / 删除时返回缺失（快照留空，不阻断生成，也不从文本推断）。
    /// </summary>
    public static async Task<Dictionary<long, BaseProduct>> LoadProductsAsync(
        IErpDbContext db, IEnumerable<long> productIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ids = (productIds ?? Array.Empty<long>()).Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, BaseProduct>();

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);
        return products.ToDictionary(p => p.Id);
    }

    /// <summary>销售订单明细引用的商品 Id（用于批量装载商品资料）</summary>
    public static List<long> ProductIdsOf(IEnumerable<SalesOrderDetail>? details)
        => (details ?? Enumerable.Empty<SalesOrderDetail>()).Select(d => d.ProductId).Where(id => id > 0)
            .Distinct().ToList();

    /// <summary>装柜清单明细引用的商品 Id（用于批量装载商品资料）</summary>
    public static List<long> ProductIdsOf(IEnumerable<ContainerLoadingDetail>? details)
        => (details ?? Enumerable.Empty<ContainerLoadingDetail>()).Select(d => d.ProductId).Where(id => id > 0)
            .Distinct().ToList();

    /// <summary>来源明细是否超出有界读取上限（超出时生成 / 预填都必须明确说明，不得截断写入）</summary>
    public static bool IsOverBound(int loadedCount) => loadedCount > MaxSourceDetails;

    /// <summary>单证类型是否含价格口径（只有商业发票含单价与行金额）</summary>
    public static bool IsPricingDocType(string? docType)
        => string.Equals((docType ?? string.Empty).Trim(), TradeDocumentItemRules.CommercialInvoiceDocType,
            StringComparison.Ordinal);

    // ==================== 3. 内部工具（校验与文案） ====================

    /// <summary>来源明细引用的商品资料快照（未引用 / 已被删除时全部为空串，不臆造任何值）</summary>
    private static (string ProductCode, string ProductNameCn, string ProductNameEn, string Spec, string Unit)
        SnapshotTextOf(long productId, IReadOnlyDictionary<long, BaseProduct>? products)
    {
        if (productId <= 0 || products is null || !products.TryGetValue(productId, out var product)
            || product is null || product.IsDeleted)
            return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        return (SanitizeProductCode(product.ProductCode),
            SanitizeProductText(product.ProductName),
            SanitizeProductText(product.EnglishName),
            SanitizeProductText(product.Spec),
            SanitizeUnit(product.Unit));
    }

    /// <summary>优先取来源明细自身的值（来源证据优先），为空时回退商品资料快照，最后按列长清洗</summary>
    private static string Pick(string? primary, string fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();

    /// <summary>
    /// 币种：商业发票必须是系统支持币种（定价口径，不做汇率换算），装箱单只做规范化；
    /// 空值按单证台账 / 系统默认口径处理。
    /// </summary>
    private static string ResolveCurrency(string? documentCurrency, bool pricing)
        => pricing
            ? TradeDocumentItemRules.NormalizePricingCurrency(documentCurrency)
            : TradeDocumentItemRules.NormalizeCurrency(documentCurrency);

    /// <summary>来源明细行数有界校验：超出上限一律拒绝（不静默截断，也不写入半套明细）</summary>
    private static void EnsureWithinBound(int sourceCount, string sourceLabel, string? sourceNo)
    {
        if (!IsOverBound(sourceCount)) return;
        throw BusinessException.RuleConflict(
            $"{sourceLabel}「{SanitizeText(sourceNo, 50)}」的明细行超过 {MaxSourceDetails} 行："
            + "系统不为超长来源生成单证明细（不截断写入、不静默丢弃来源明细行），"
            + "请拆分来源单据后分别生成单证");
    }

    /// <summary>来源明细数值非法：拒绝生成并要求先修正来源单据（避免生成缺行的发票 / 装箱单）</summary>
    private static BusinessException FailInvalidNumber(
        string sourceLabel, string? sourceNo, int lineNo, string fieldName, decimal value)
        => BusinessException.RuleConflict(
            $"{sourceLabel}「{SanitizeText(sourceNo, 50)}」第 {lineNo} 行{fieldName}={value} 不合法"
            + "（数量必须大于 0 且最多 4 位小数、单价不能为负）：请先修正来源明细后再生成单证，"
            + "系统不静默丢弃来源明细行、也不臆造数值");

    /// <summary>来源明细没有可核对的商品身份：拒绝生成（与 ERP-051「空行无从核对」同一口径）</summary>
    private static BusinessException FailMissingIdentity(string sourceLabel, string? sourceNo, int lineNo)
        => BusinessException.RuleConflict(
            $"{sourceLabel}「{SanitizeText(sourceNo, 50)}」第 {lineNo} 行没有商品引用、商品编码与商品名称："
            + "空行无从核对，请先补齐来源明细的商品信息后再生成单证");

    /// <summary>生成摘要文案（行数与口径），界面与预填预览共用</summary>
    private static string SummaryTextOf(string sourceLabel, string? docType, int lineCount, bool pricing)
    {
        var type = (docType ?? string.Empty).Trim();
        var scope = pricing
            ? "含单价与行金额（行金额由服务端按币种精度计算）"
            : $"不含价格口径（单价与行金额恒为 0，只登记数量{ (string.Equals(type, TradeDocumentItemRules.PackingListDocType, StringComparison.Ordinal) ? "、箱数与重量" : string.Empty) }）";
        return lineCount == 0
            ? $"由{sourceLabel}生成的「{type}」没有可带入的明细行（来源明细为空）"
            : $"已由{sourceLabel}带入 {lineCount} 行明细行快照：{scope}";
    }

    /// <summary>证据说明文案（哪些值来源未提供 / 按什么口径留空），照实陈述、不给任何推断值</summary>
    private static string EvidenceTextOf(string sourceLabel, string? docType, IReadOnlyList<TradeDocumentItem> lines)
    {
        var type = (docType ?? string.Empty).Trim();
        var parts = new List<string>
        {
            $"来源：{sourceLabel}明细（{lines.Count} 行，按来源明细顺序写入行序）"
        };

        if (string.Equals(type, TradeDocumentItemRules.PackingListDocType, StringComparison.Ordinal))
        {
            var packagingLines = lines.Count;
            var missingPackage = lines.Count(l => l.PackageCount is null);
            var missingNet = lines.Count(l => l.NetWeight is null);
            var missingGross = lines.Count(l => l.GrossWeight is null);
            parts.Add($"装箱单行 {packagingLines} 行：箱数未登记 {missingPackage} 行、净重未登记 {missingNet} 行、"
                      + $"毛重未登记 {missingGross} 行 —— 未登记一律留空（不写成 0、不按商品资料或自由文本推断）");
        }
        else if (string.Equals(type, TradeDocumentItemRules.CommercialInvoiceDocType, StringComparison.Ordinal))
        {
            parts.Add("商业发票行：商品 / 规格 / 数量 / 单位 / 单价取自来源明细，行金额由服务端按币种精度计算");
        }

        return string.Join(" ｜ ", parts);
    }

    // ==================== 4. 由销售订单明细构造明细行快照 ====================

    /// <summary>
    /// 由销售订单明细构造商业发票 / 装箱单的明细行快照（未落库，由生成接口在**同一事务**内写库）。
    /// <para>商业发票行：商品 / 规格 / 数量 / 单位取订单明细（快照），单价取订单明细单价，行金额服务端计算；
    /// 装箱单行：同样的商品 / 数量 / 单位，不含价格口径（单价与金额恒为 0），箱数与重量一律留空
    /// （销售订单明细不提供箱数 / 净重 / 毛重证据）。</para>
    /// <para>单证类型不支持明细行（非商业发票 / 装箱单）时返回空行集合，并说明类型不支持。</para>
    /// </summary>
    public static TradeDocumentLineSnapshotResult BuildFromSalesOrder(
        SalesOrder order, IReadOnlyList<SalesOrderDetail> details,
        IReadOnlyDictionary<long, BaseProduct> products, string? docType, string? documentCurrency)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!TradeDocumentItemRules.IsSupportedDocType(docType))
            return TradeDocumentLineSnapshotResult.NotSupported(docType, SalesOrderSourceLabel);

        var pricing = IsPricingDocType(docType);
        var currency = ResolveCurrency(documentCurrency, pricing);
        var source = details ?? Array.Empty<SalesOrderDetail>();
        EnsureWithinBound(source.Count, SalesOrderSourceLabel, order.OrderNo);

        var lines = new List<TradeDocumentItem>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var detail = source[index];
            var snapshot = SnapshotTextOf(detail.ProductId, products);

            var nameCn = SanitizeProductText(Pick(detail.ProductName, snapshot.ProductNameCn));
            if (detail.ProductId <= 0 && nameCn.Length == 0 && snapshot.ProductCode.Length == 0)
                throw FailMissingIdentity(SalesOrderSourceLabel, order.OrderNo, index + 1);

            var quantity = NormalizeQuantity(detail.Quantity)
                ?? throw FailInvalidNumber(SalesOrderSourceLabel, order.OrderNo, index + 1,
                    "数量", detail.Quantity);
            var unitPrice = pricing
                ? NormalizeUnitPrice(detail.UnitPrice)
                  ?? throw FailInvalidNumber(SalesOrderSourceLabel, order.OrderNo, index + 1,
                      "单价", detail.UnitPrice)
                : 0m;

            lines.Add(new TradeDocumentItem
            {
                LineNo = index + 1,
                ProductId = detail.ProductId > 0 ? detail.ProductId : 0,
                ProductCode = snapshot.ProductCode,
                ProductNameCn = nameCn,
                ProductNameEn = snapshot.ProductNameEn,
                Spec = SanitizeProductText(Pick(detail.Spec, snapshot.Spec)),
                Quantity = quantity,
                Unit = SanitizeUnit(Pick(detail.Unit, snapshot.Unit)),
                UnitPrice = unitPrice,
                LineAmount = TradeDocumentItemRules.ComputeLineAmount(quantity, unitPrice, currency),
                PackageCount = null,          // 销售订单明细没有箱数列：留空（不推断）
                NetWeight = null,             // 同上：净重留空
                GrossWeight = null,           // 同上：毛重留空
                Currency = currency,
                Remark = SanitizeRemark(detail.Remark),
                CreatedAt = DateTime.Now,
            });
        }

        return new TradeDocumentLineSnapshotResult
        {
            Lines = lines,
            DocType = (docType ?? string.Empty).Trim(),
            SourceLabel = SalesOrderSourceLabel,
            SourceNo = (order.OrderNo ?? string.Empty).Trim(),
            SourceDetailCount = source.Count,
            PricingApplied = pricing,
            SummaryText = SummaryTextOf(SalesOrderSourceLabel, docType, lines.Count, pricing),
            EvidenceText = EvidenceTextOf(SalesOrderSourceLabel, docType, lines),
        };
    }

    // ==================== 5. 由装柜清单明细构造明细行快照 ====================

    /// <summary>
    /// 由装柜清单明细构造装箱单明细行快照（未落库）：数量取清单明细数量，箱数取清单明细箱数
    /// （仅整数箱数；来源 0 视为未登记、非整数箱数不四舍五入 → 一律留空），毛重取清单明细毛重
    /// （来源 0 视为未登记 → 留空）；**净重来源没有该列，一律留空**；
    /// 装柜清单明细没有单价列、装箱单也不含价格口径，因此单价与行金额恒为 0（不推测价格）。
    /// </summary>
    public static TradeDocumentLineSnapshotResult BuildFromLoadingList(
        ContainerLoadingList list, IReadOnlyList<ContainerLoadingDetail> details,
        IReadOnlyDictionary<long, BaseProduct> products, string? docType, string? documentCurrency)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!TradeDocumentItemRules.IsSupportedDocType(docType))
            return TradeDocumentLineSnapshotResult.NotSupported(docType, LoadingListSourceLabel);

        var pricing = IsPricingDocType(docType);
        var currency = ResolveCurrency(documentCurrency, pricing);
        var source = details ?? Array.Empty<ContainerLoadingDetail>();
        EnsureWithinBound(source.Count, LoadingListSourceLabel, list.LoadingListNo);

        var nonIntegralPackages = 0;
        var unrecordedWeights = 0;
        var lines = new List<TradeDocumentItem>(source.Count);

        for (var index = 0; index < source.Count; index++)
        {
            var detail = source[index];
            var snapshot = SnapshotTextOf(detail.ProductId, products);

            var nameCn = SanitizeProductText(Pick(detail.ProductName, snapshot.ProductNameCn));
            if (detail.ProductId <= 0 && nameCn.Length == 0 && snapshot.ProductCode.Length == 0)
                throw FailMissingIdentity(LoadingListSourceLabel, list.LoadingListNo, index + 1);

            var quantity = NormalizeQuantity(detail.Quantity)
                ?? throw FailInvalidNumber(LoadingListSourceLabel, list.LoadingListNo, index + 1,
                    "数量", detail.Quantity);

            var packageCount = PackageCountOf(detail.Cartons);
            if (IsNonIntegralPackageCount(detail.Cartons)) nonIntegralPackages++;
            if (detail.Weight <= 0m) unrecordedWeights++;

            lines.Add(new TradeDocumentItem
            {
                LineNo = index + 1,
                ProductId = detail.ProductId > 0 ? detail.ProductId : 0,
                ProductCode = snapshot.ProductCode,
                ProductNameCn = nameCn,
                ProductNameEn = snapshot.ProductNameEn,
                Spec = snapshot.Spec,                  // 清单明细无规格列：仅引用商品资料时有值
                Quantity = quantity,
                Unit = snapshot.Unit,                  // 清单明细无单位列：仅引用商品资料时有值
                UnitPrice = 0m,                        // 清单明细无单价列：不推测价格
                LineAmount = 0m,                       // 装箱单不含价格口径
                PackageCount = packageCount,
                NetWeight = null,                      // 清单明细没有净重列：留空（不推断）
                GrossWeight = GrossWeightOf(detail.Weight),
                Currency = currency,
                Remark = SanitizeRemark(detail.Remark),
                CreatedAt = DateTime.Now,
            });
        }

        return new TradeDocumentLineSnapshotResult
        {
            Lines = lines,
            DocType = (docType ?? string.Empty).Trim(),
            SourceLabel = LoadingListSourceLabel,
            SourceNo = (list.LoadingListNo ?? string.Empty).Trim(),
            SourceDetailCount = source.Count,
            PricingApplied = pricing,
            NonIntegralPackageCountLines = nonIntegralPackages,
            UnrecordedWeightLines = unrecordedWeights,
            SummaryText = SummaryTextOf(LoadingListSourceLabel, docType, lines.Count, pricing),
            EvidenceText = EvidenceTextOf(LoadingListSourceLabel, docType, lines),
        };
    }
}

/// <summary>
/// 来源明细 → 明细行快照的构造结果（未落库）：行集合 + 摘要 + 证据说明；
/// 由生成接口在**同一事务**内把 <see cref="Lines"/> 与父单证一起落库，由预填接口转成只读预览。
/// </summary>
public sealed class TradeDocumentLineSnapshotResult
{
    /// <summary>明细行快照（行序已按来源明细顺序写入；所属单证 Id 由生成接口在落库时补写）</summary>
    public List<TradeDocumentItem> Lines { get; init; } = new();

    /// <summary>单证类型</summary>
    public string DocType { get; init; } = string.Empty;

    /// <summary>来源单据类型文案（销售订单 / 装柜清单）</summary>
    public string SourceLabel { get; init; } = string.Empty;

    /// <summary>来源单据号（销售订单号 / 装柜清单号）</summary>
    public string SourceNo { get; init; } = string.Empty;

    /// <summary>来源明细行数（0 = 来源没有明细）</summary>
    public int SourceDetailCount { get; init; }

    /// <summary>本单证类型是否含价格口径（商业发票 = true）</summary>
    public bool PricingApplied { get; init; }

    /// <summary>本单证类型是否支持明细行（非商业发票 / 装箱单 = false，此时行集合为空）</summary>
    public bool Supported { get; init; } = true;

    /// <summary>未能写入箱数列的来源行数（非整数箱数 / 越界；照实说明，不四舍五入）</summary>
    public int NonIntegralPackageCountLines { get; init; }

    /// <summary>来源未登记毛重（来源值为 0 / 负数）的行数</summary>
    public int UnrecordedWeightLines { get; init; }

    /// <summary>摘要文案（行数与口径）</summary>
    public string SummaryText { get; init; } = string.Empty;

    /// <summary>证据说明文案（哪些值来源未提供、按什么口径留空）</summary>
    public string EvidenceText { get; init; } = string.Empty;

    /// <summary>单证类型不支持明细行时的结果（行集合为空，并说明原因）</summary>
    public static TradeDocumentLineSnapshotResult NotSupported(string? docType, string sourceLabel)
    {
        var type = (docType ?? string.Empty).Trim();
        return new TradeDocumentLineSnapshotResult
        {
            DocType = type,
            SourceLabel = sourceLabel,
            Supported = false,
            SummaryText = $"「{type}」不支持商品明细行：只有 {string.Join(" / ", TradeDocumentItemRules.AllowedDocTypes)} "
                          + "可带入明细行（未带入任何行）",
            EvidenceText = string.Empty,
        };
    }
}
