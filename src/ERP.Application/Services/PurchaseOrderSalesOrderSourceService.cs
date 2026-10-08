using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购订单来源销售订单候选 / 已存储来源解析（ERP-393，只读、有界、分页）。
/// <para><b>权威口径复用</b>：候选与已存储来源的可选性都<b>复用</b> <see cref="PurchaseSalesOrderLinkRules"/>
/// 的资格判定（<see cref="PurchaseSalesOrderLinkRules.IsEligibleNewSource"/>：已审核、未删除、未取消），
/// 最终保存仍由 <see cref="PurchaseSalesOrderLinkRules.ApplyLinkAsync"/> 在锁内复核来源、权威客户与商品 / 单位兼容性。
/// 候选<b>不强制币种一致</b>，也不改写采购订单的原始商业币种 / 单价 / 金额。</para>
/// <para><b>范围先于计数</b>：候选先按 <see cref="SalespersonDataScopeService"/> 权威客户数据范围下推到数据库，
/// 再做可选客户过滤与关键字匹配，最后归一化页码 / 每页条数后再计数与分页，绝不「先查全量再内存过滤」，也绝不无界拉取。</para>
/// <para><b>不泄露</b>：已存储来源若已删除 / 已取消 / 未审核 / <b>不在当前账号客户数据范围之内</b>，
/// 一律返回不可用标注且<b>不填充</b>客户 / 订单字段（<c>PurchaseOrderSalesOrderSourceViewDto</c> 的
/// <c>OrderNo</c> / <c>CustomerId</c> / <c>CustomerName</c> 等保持为空），绝不披露范围外（foreign）来源字段。</para>
/// <para>本类只做<b>有界只读投影</b>：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限或用户授权。</para>
/// </summary>
public static class PurchaseOrderSalesOrderSourceService
{
    /// <summary>候选查询默认每页条数（有界）</summary>
    public const int DefaultPageSize = 20;

    /// <summary>候选查询每页条数上限（有界，绝不无界拉取）</summary>
    public const int MaxPageSize = 100;

    /// <summary>候选查询页码上限（有界，防止越界深分页）</summary>
    public const int MaxPage = 10000;

    /// <summary>关键字长度上限（超长截断，避免无界匹配）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>未关联来源的只读标注（历史未关联语义原样保留）</summary>
    public const string UnlinkedAnnotationText =
        "当前未关联来源销售订单（历史未关联语义原样保留，绝不回填）";

    /// <summary>来源不可用（已删除 / 已取消 / 未审核 / 范围外）的只读标注（<b>不含</b>任何客户端可披露的来源字段）</summary>
    public const string UnavailableAnnotationText =
        "已存储来源销售订单不可用（已删除 / 已取消 / 未审核 / 不在当前账号客户数据范围内），原链接原样保留";

    /// <summary>归属客户档案不存在 / 已删除时的候选不可选原因文案</summary>
    public const string CustomerUnavailableReasonText =
        "来源销售订单的归属客户不存在或已删除：不能作为采购备货来源";

    /// <summary>候选口径文案（接口 / 文档同源）</summary>
    public const string CandidateRuleText =
        "采购订单来源销售订单候选只返回当前账号客户数据范围之内、「已审核、未删除、未取消」的销售订单"
        + "（复用采购订单显式链接规则的可选性口径；归属客户档案不存在 / 已删除显式标记不可选；"
        + "不要求币种一致、不做汇率换算，候选币种仅供展示）；"
        + "客户数据范围与可选客户过滤先于计数 / 分页下推到数据库，关键字 / 页码 / 每页条数先归一化再计数；"
        + "只返回有界 DTO 字段，绝不返回范围外订单，也不接受按猜测 Id 直取；"
        + "候选选择不等于授权，最终保存仍按采购订单显式链接规则复核精确来源"
        + "（不新增表 / 列 / 菜单 / 权限或用户授权）。";

    /// <summary>已存储来源展示口径文案（接口 / 文档同源）</summary>
    public const string StoredSourceRuleText =
        "已存储来源按采购订单权威归属客户做实时身份 / 菜单 / 客户数据范围复核后显式标注"
        + "（未关联 / 已关联 / 来源已取消 / 来源不可用）；历史已取消 / 不可用来源原样保留、只读可读，"
        + "绝不静默清除或重绑定；来源不可用时绝不泄露范围外（foreign）来源的客户 / 订单字段。";

    /// <summary>关键字归一化：去首尾空白并截断到 <see cref="MaxKeywordLength"/>（先归一化再计数 / 匹配）。</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        return text.Length <= MaxKeywordLength ? text : text[..MaxKeywordLength];
    }

    /// <summary>页码归一化：小于 1 取 1，超过 <see cref="MaxPage"/> 收敛到上限（有界）。</summary>
    public static int NormalizePage(int page)
        => page < 1 ? 1 : (page > MaxPage ? MaxPage : page);

    /// <summary>每页条数归一化：小于 1 取默认值，超过 <see cref="MaxPageSize"/> 收敛到上限（有界）。</summary>
    public static int NormalizePageSize(int pageSize)
        => pageSize < 1 ? DefaultPageSize : (pageSize > MaxPageSize ? MaxPageSize : pageSize);

    /// <summary>
    /// 有界只读候选查询：返回当前账号客户数据范围内、「已审核、未删除、未取消」销售订单的候选页。
    /// <para><paramref name="scope"/> 由调用方在实时身份 / 既有「采购订单」菜单校验后解析（绝不缓存）；
    /// 客户数据范围与可选 <paramref name="customerId"/> 过滤都先于计数 / 分页下推到数据库。</para>
    /// <para>候选资格<b>复用</b> <see cref="PurchaseSalesOrderLinkRules.IsEligibleNewSource"/>，且归属客户档案必须存在、未删除；
    /// <b>不要求币种一致</b>，币种只作展示字段。</para>
    /// </summary>
    public static async Task<PurchaseOrderSalesOrderSourceCandidatePageDto> QueryCandidatesAsync(
        IErpDbContext db, SalespersonDataScope scope, long? customerId, string? keyword,
        int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        // 受限账号未映射为业务员（采购操作员）时与列表口径一致：fail closed，不泄露任何单据。
        PurchaseOrderAuthorizationRules.EnsureScopeUsable(scope);

        // 归一化必须先于任何计数 / 取数。
        var kw = NormalizeKeyword(keyword);
        var normalizedPage = NormalizePage(page);
        var normalizedSize = NormalizePageSize(pageSize);

        // 复用采购订单显式链接规则的可选性：已审核（隐含未取消）、未删除。
        var source = db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == DocumentStatus.Approved);
        // 客户数据范围先于计数 / 分页下推到数据库（受限账号只看到范围内客户订单）。
        source = SalespersonDataScopeService.FilterByCustomer(source, scope, o => o.CustomerId);
        if (customerId is > 0)
            source = source.Where(o => o.CustomerId == customerId.Value);
        if (kw.Length > 0)
        {
            source = source.Where(o => o.OrderNo.Contains(kw)
                || o.CustomerPoNo.Contains(kw)
                || o.ContractNo.Contains(kw));
        }

        var total = await source.CountAsync(ct);
        var orders = await source
            .OrderByDescending(o => o.Id)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .Select(o => new { o.Id, o.OrderNo, o.OrderDate, o.CustomerId, o.Currency, o.Status })
            .ToListAsync(ct);

        var customerNames = await LoadCustomerNamesAsync(
            db, orders.Select(o => o.CustomerId).Distinct().ToList(), ct);

        var items = orders.Select(o =>
        {
            var hasCustomer = customerNames.ContainsKey(o.CustomerId);
            var statusEligible = PurchaseSalesOrderLinkRules.IsEligibleNewSource(o.Status);
            var eligible = hasCustomer && statusEligible;
            return new PurchaseOrderSalesOrderSourceCandidateDto
            {
                SalesOrderId = o.Id,
                OrderNo = o.OrderNo,
                OrderDate = o.OrderDate,
                CustomerId = o.CustomerId,
                CustomerName = customerNames.GetValueOrDefault(o.CustomerId, string.Empty),
                Currency = o.Currency.ToString(),
                Status = o.Status.ToString(),
                Eligible = eligible,
                IneligibleReason = eligible
                    ? string.Empty
                    : (hasCustomer
                        ? PurchaseSalesOrderLinkRules.SourceIneligibleReason(o.Status)
                        : CustomerUnavailableReasonText)
            };
        }).ToList();

        return new PurchaseOrderSalesOrderSourceCandidatePageDto
        {
            Items = items,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedSize
        };
    }

    /// <summary>
    /// 已存储来源（归属销售订单）的只读解析：按采购订单持久化的 <c>OwningSalesOrderId</c> 精确解析，
    /// 返回显式状态文案与（仅当<b>可解析且在当前账号客户范围内</b>时）结构化客户 / 订单字段。
    /// <para>未关联（历史）→ <c>Linked = false</c> + 「未关联…」；来源有效 →
    /// <c>Linked = true</c> + 订单号 / 客户 + 可选性；来源已删除 / 已取消 / 未审核 / 范围外 →
    /// <c>Unavailable = true</c> 且<b>不填充</b>客户 / 订单字段。</para>
    /// <para>本方法绝不写库、绝不重绑定 / 清除链接、绝不因来源失效而抛异常（历史必须可读），
    /// 也绝不按订单号文本 / 金额 / 相似度猜测来源。</para>
    /// </summary>
    public static async Task<PurchaseOrderSalesOrderSourceViewDto> DescribeStoredSourceAsync(
        IErpDbContext db, SalespersonDataScope scope, PurchaseOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(order);

        if (order.OwningSalesOrderId is null or <= 0)
        {
            return new PurchaseOrderSalesOrderSourceViewDto
            {
                Linked = false,
                Unavailable = false,
                EligibleForNewLink = false,
                Annotation = UnlinkedAnnotationText
            };
        }

        var storedId = order.OwningSalesOrderId.Value;
        var source = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == storedId && !o.IsDeleted)
            .Select(o => new { o.Id, o.OrderNo, o.OrderDate, o.CustomerId, o.Currency, o.Status })
            .FirstOrDefaultAsync(ct);

        // 来源已删除 / 无法解析：原链接原样保留，绝不披露不存在来源的任何字段。
        if (source is null)
        {
            return new PurchaseOrderSalesOrderSourceViewDto
            {
                SalesOrderId = storedId,
                Linked = true,
                Unavailable = true,
                EligibleForNewLink = false,
                Annotation = UnavailableAnnotationText
            };
        }

        // 范围外（foreign）来源：绝不披露客户 / 订单字段，只返回不可用标注。
        if (!scope.AllowsCustomer(source.CustomerId))
        {
            return new PurchaseOrderSalesOrderSourceViewDto
            {
                SalesOrderId = storedId,
                Linked = true,
                Unavailable = true,
                EligibleForNewLink = false,
                Annotation = UnavailableAnnotationText
            };
        }

        var customerName = await LoadCustomerNameAsync(db, source.CustomerId, ct);
        var eligible = PurchaseSalesOrderLinkRules.IsEligibleNewSource(source.Status);
        var annotation = eligible
            ? $"已关联销售订单「{source.OrderNo}」"
            : $"已关联销售订单「{source.OrderNo}」：{PurchaseSalesOrderLinkRules.SourceIneligibleReason(source.Status)}，链接只读保留";

        return new PurchaseOrderSalesOrderSourceViewDto
        {
            SalesOrderId = storedId,
            OrderNo = source.OrderNo,
            OrderDate = source.OrderDate,
            CustomerId = source.CustomerId,
            CustomerName = customerName,
            Currency = source.Currency.ToString(),
            Status = source.Status.ToString(),
            Linked = true,
            Unavailable = false,
            EligibleForNewLink = eligible,
            Annotation = annotation
        };
    }

    /// <summary>按客户 Id 批量装载权威客户名称（一次查询，有界；已删除客户不返回，绝不臆造）。</summary>
    private static async Task<Dictionary<long, string>> LoadCustomerNamesAsync(
        IErpDbContext db, List<long> customerIds, CancellationToken ct)
    {
        if (customerIds.Count == 0) return new Dictionary<long, string>();
        return await db.BaseCustomers.AsNoTracking()
            .Where(c => !c.IsDeleted && customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CustomerName })
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName ?? string.Empty, ct);
    }

    /// <summary>按客户 Id 精确装载权威客户名称（不存在 / 已删除时为空串，绝不臆造）。</summary>
    private static async Task<string> LoadCustomerNameAsync(IErpDbContext db, long customerId, CancellationToken ct)
    {
        if (customerId <= 0) return string.Empty;
        return await db.BaseCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && !c.IsDeleted)
            .Select(c => c.CustomerName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;
    }
}
