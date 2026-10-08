using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜清单衔接已审核预装柜单并防止累计超装（ERP-348）。
/// <para>只复用既有 <see cref="ContainerLoadingList.PreLoadingId"/> 显式链接：未链接（null）的历史单据不做任何校验，行为保持不变。</para>
/// <para>显式链接构成「权威来源」：预装柜单必须存在、未删除、已审核，装柜清单每条明细必须指定有效商品、
/// 商品必须存在于来源明细中，数量必须为正数，箱数 / 毛重 / 体积不得为负；任一不满足即在保存 / 审核前
/// fail closed 拒绝，绝不因来源语义不明确而跳过数量护栏。</para>
/// <para>已取消 / 已驳回 / 待提交 / 已提交的装柜清单不计入已审核数量，取消后自动释放剩余额度；重复商品行按商品聚合，
/// 来源授权数量也按商品聚合（不臆造、不猜行）；跨商品不合计。</para>
/// <para>本类只做纯内存判定与有界查询，不落库、不改单据、不写库存 / 财务、不开启事务；同源并发审核的串行化由调用方
/// （<c>ContainerLoadingListController.Approve</c> / <c>ContainerPreLoadingController.Cancel</c>）
/// 在同一可串行化事务内对预装柜单行加 UPDLOCK/HOLDLOCK 完成。</para>
/// </summary>
public static class ContainerLoadingFulfillmentRules
{
    /// <summary>数量比较容差（与装柜数量保留 4 位小数同口径，吸收舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>装柜清单操作所需既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "loading-list";

    /// <summary>装柜清单操作所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "装柜清单";

    /// <summary>取消预装柜单所需既有菜单编码（与 SeedData / SchemaUpgrader 同源）</summary>
    public const string PreLoadingRequiredMenuCode = "pre-loading";

    /// <summary>取消预装柜单所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string PreLoadingRequiredMenuText = "预装柜单";

    /// <summary>履约口径说明（界面 / 文档同源）</summary>
    public const string RuleText =
        "装柜清单关联预装柜单后，仅当链接权威（预装柜单已审核）时，审核时累计「以该预装柜单为来源、未删除、已审核」的装柜数量" +
        "（装柜明细已按商品基础单位口径）加上本单数量不得超过预装柜单授权数量（按商品逐行比对，重复行先聚合）；" +
        "未关联的装柜清单不做累计校验；显式链接无效（预装柜单不存在 / 已删除 / 未审核 / 商品缺失或不在来源 / 数量非正 / 箱数·毛重·体积为负）" +
        "在保存或审核前直接拒绝履约，绝不猜测重复来源行、绝不把箱数当件数、绝不臆造客户 / 销售归属。";

    /// <summary>
    /// 校验创建 / 更新 / 提交时的显式来源链接与当前账号身份 / 菜单 / 客户数据范围。
    /// 未链接（null）保持历史行为（仍校验身份 / 客户范围）；显式链接必须为正整数且构成权威来源，
    /// 否则 fail closed 拒绝保存，避免把无效来源链接落到待提交 / 待审核单据上。
    /// </summary>
    public static async Task ValidateLinkAsync(
        IErpDbContext db, ContainerLoadingList entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        await EnsureAuthorizedAsync(db, entity, userId, ct);

        if (entity.PreLoadingId is null) return;
        if (entity.PreLoadingId is not > 0)
            throw BusinessException.InvalidParameter("预装柜单 Id 必须为正整数");

        var link = await ResolveAuthoritativeSourceOrThrowAsync(db, entity, ct);
        ValidateDetailLines(entity, link);
    }

    /// <summary>
    /// 校验审核时的来源链接与累计数量上限（调用方必须已在本单锁内重新加载本单，且已持有预装柜单行更新锁）。
    /// <para>未链接（null）直接返回；显式链接无效时 fail closed 抛业务异常，且不落库、不写库存 / 财务、不改单据状态。</para>
    /// </summary>
    public static async Task ValidateApprovalAsync(
        IErpDbContext db, ContainerLoadingList entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        await EnsureAuthorizedAsync(db, entity, userId, ct);

        if (entity.PreLoadingId is null) return;

        var link = await ResolveAuthoritativeSourceOrThrowAsync(db, entity, ct);
        ValidateDetailLines(entity, link);
        await EnforceCumulativeAsync(db, entity, link, ct);
    }

    /// <summary>
    /// 校验装柜清单当前账号身份 / 账号状态 / 菜单 / 权威客户数据范围（不校验来源链接数量语义）。
    /// 供创建 / 修改 / 提交 / 审核与取消等路由复用（ERP-364 起统一委托
    /// <see cref="LoadingListAuthorizationRules"/>：实时身份 + 账号状态 + 既有装柜清单菜单 + 参与方 / 上游客户范围）。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(
        IErpDbContext db, ContainerLoadingList entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var scope = await LoadingListAuthorizationRules.EnsureAuthorizedAsync(db, userId, ct);
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, entity, ct);
    }

    /// <summary>
    /// 校验预装柜单能否取消（不写库）。调用方必须在同一可串行化事务内持有该预装柜单行更新锁后再调用，
    /// 以保证「判定」与「状态变更」原子，且与同源装柜清单审核串行化。
    /// <para>存在「以本单为来源、未删除、已审核」的装柜清单时拒绝；已取消 / 已驳回 / 待提交 / 已提交 / 已删除的
    /// 装柜清单不算有效引用，不阻断取消。</para>
    /// </summary>
    public static async Task ValidateSourceCancellationAsync(
        IErpDbContext db, ContainerPreLoading preLoading, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(preLoading);

        await EnsurePreLoadingAuthorizedAsync(db, userId, ct);

        var hasApprovedLoading = await db.ContainerLoadingLists.AsNoTracking()
            .AnyAsync(l => !l.IsDeleted && l.PreLoadingId == preLoading.Id
                           && l.Status == DocumentStatus.Approved, ct);
        if (hasApprovedLoading)
        {
            throw BusinessException.RuleConflict(
                "存在已审核且未取消的装柜清单引用本预装柜单：请先取消装柜清单，再取消预装柜单");
        }
    }

    // ==================== 私有助手 ====================

    /// <summary>身份 / 菜单校验（预装柜单模块；来源无客户字段，不适用客户范围）。</summary>
    private static async Task EnsurePreLoadingAuthorizedAsync(IErpDbContext db, long? userId, CancellationToken ct)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再取消预装柜单", ErrorCodes.Unauthorized);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(PreLoadingRequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{PreLoadingRequiredMenuText}」（{PreLoadingRequiredMenuCode}）模块授权：" +
                    "拒绝取消预装柜单（fail closed，不执行任何状态变更）",
                    ErrorCodes.Forbidden);
            }
        }
    }

    private static async Task<SourceContext> ResolveAuthoritativeSourceOrThrowAsync(
        IErpDbContext db, ContainerLoadingList entity, CancellationToken ct)
    {
        var source = await db.ContainerPreLoadings.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.PreLoadingId!.Value && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict("预装柜单不存在或已删除");
        if (source.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("预装柜单未审核，不能作为装柜依据");

        var productIds = entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0)
            .Select(d => d.ProductId).Distinct().ToList();
        var products = productIds.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        // 来源授权数量按商品聚合（重复来源行先求和，不臆造、不猜行）。
        var sourceQuantities = source.Details.Where(d => !d.IsDeleted && d.ProductId > 0)
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Quantity));

        return new SourceContext(source, products, sourceQuantities);
    }

    /// <summary>
    /// 逐行校验装柜明细：商品必须有效、必须在来源中、数量必须为正数、箱数 / 毛重 / 体积不得为负。
    /// 数量按商品基础单位口径（<c>Quantity</c>），绝不把箱数（<c>Cartons</c>）当成件数参与上限判定。
    /// </summary>
    private static void ValidateDetailLines(ContainerLoadingList entity, SourceContext link)
    {
        foreach (var detail in entity.Details.Where(d => !d.IsDeleted))
        {
            if (detail.ProductId <= 0)
                throw BusinessException.RuleConflict("装柜明细必须指定商品");
            if (!link.Products.TryGetValue(detail.ProductId, out var product))
                throw BusinessException.RuleConflict($"商品 [{detail.ProductName}] 不存在或已删除");
            if (!link.SourceQuantities.ContainsKey(detail.ProductId))
                throw BusinessException.RuleConflict($"预装柜单无商品 [{product.ProductName}] 的明细行");

            if (detail.Quantity <= 0)
                throw BusinessException.InvalidParameter($"商品 [{product.ProductName}] 的装柜数量必须为正数");
            if (detail.Cartons < 0)
                throw BusinessException.InvalidParameter($"商品 [{product.ProductName}] 的箱数不能为负数");
            if (detail.Weight < 0)
                throw BusinessException.InvalidParameter($"商品 [{product.ProductName}] 的毛重不能为负数");
            if (detail.Volume < 0)
                throw BusinessException.InvalidParameter($"商品 [{product.ProductName}] 的体积不能为负数");
        }
    }

    private static async Task EnforceCumulativeAsync(
        IErpDbContext db, ContainerLoadingList entity, SourceContext link, CancellationToken ct)
    {
        var sourceId = link.Source.Id;

        // 已审核装柜数量：以本预装柜单为来源、未删除、已审核、且非本单的装柜明细数量按商品汇总。
        // 本单由调用方在拿到预装柜单锁后进入本方法，因此并发同源审核会被串行化，后到者能看到先到者已提交的数量。
        var approvedRows = await (
                from l in db.ContainerLoadingLists
                join d in db.ContainerLoadingDetails on l.Id equals d.LoadingListId
                where l.PreLoadingId == sourceId
                      && !l.IsDeleted
                      && l.Status == DocumentStatus.Approved
                      && l.Id != entity.Id
                      && !d.IsDeleted
                      && d.ProductId > 0
                select new { d.ProductId, d.Quantity })
            .ToListAsync(ct);

        var already = approvedRows.GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

        foreach (var group in entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0 && d.Quantity > 0)
                     .GroupBy(d => d.ProductId))
        {
            var productId = group.Key;
            var product = link.Products[productId];
            var authorized = link.SourceQuantities[productId];

            var loaded = already.TryGetValue(productId, out var value) ? value : 0m;
            var thisQuantity = group.Sum(d => d.Quantity);
            var total = loaded + thisQuantity;

            if (total > authorized + QuantityTolerance)
            {
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 累计装柜数量 {total} 超过预装柜单授权数量 {authorized}" +
                    $"（已审核 {loaded} + 本次 {thisQuantity}）");
            }
        }
    }

    private sealed class SourceContext
    {
        public SourceContext(
            ContainerPreLoading source,
            IReadOnlyDictionary<long, BaseProduct> products,
            IReadOnlyDictionary<long, decimal> sourceQuantities)
        {
            Source = source;
            Products = products;
            SourceQuantities = sourceQuantities;
        }

        public ContainerPreLoading Source { get; }
        public IReadOnlyDictionary<long, BaseProduct> Products { get; }
        public IReadOnlyDictionary<long, decimal> SourceQuantities { get; }
    }
}

