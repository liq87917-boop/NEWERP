using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 单证中心（<see cref="TradeDocument"/>，<c>api/trade/documents</c>）实时授权、权威客户范围与写入护栏（ERP-394）。
/// <list type="number">
/// <item><b>实时授权</b>：列表 / 计数 / 详情 / 打印 / 导出 / 明细行读取 / 新增 / 修改 / 删除 / 批量删除，
/// 以及由销售订单 / 装柜清单「带入预填 + 直接生成」的入口，在读取任何计数、生成单证编号或写入任何数据
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 禁用按权限不足拒绝）、既有「单证中心」（<c>doc-center</c>）菜单授权（非特权账号必须显式具备，未映射业务员
/// fail closed）与权威客户数据范围（复用 ERP-097 <see cref="SalespersonDataScopeService"/>）。</item>
/// <item><b>权威归属</b>：单证只按<b>持久化 <see cref="TradeDocument.CustomerId"/></b> 判定，绝不按
/// <see cref="TradeDocument.SalesOrderNo"/> / <see cref="TradeDocument.RefNo"/> / <see cref="TradeDocument.CustomerName"/>
/// 等自由文本推断；受限账号不能读取 / 改写缺失权威归属（<c>CustomerId</c> 为空）的「无主」单证，真正不受限
/// （特权）的授权账号保留既有历史访问。</item>
/// <item><b>列表 / 计数下推</b>：范围在 <c>Count</c> 与分页之前下推到 SQL（<see cref="ScopeFilter"/>），
/// 绝不「先查全量再内存过滤」，导出 / 明细 / 打印同口径，绝不泄露范围外单证或其计数 / 明细 / 打印内容。</item>
/// <item><b>写入校验</b>：改写任何字段之前，受限账号的拟议客户必须<b>存在且落在实时范围内</b>；已存单证在修改 / 删除 / 批量删除
/// 之前复核归属；混合允许 / 越界批次<b>整体拒绝</b>、不做部分删除。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（控制器 / <c>TradeDocumentRequestAuthorizationFilter</c>）解析后以
/// <see cref="SalespersonDataScope"/> 传入；<c>null</c> 仅表示「未经过 MVC 授权管线的进程内调用」，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 权限模型，不伪造任何授权，也不改写任何
/// 来源单据、库存来源审计、明细行快照与历史留痕。</para>
/// </summary>
public static class TradeDocumentAuthorizationRules
{
    /// <summary>单证中心模块复用的既有菜单编码（与 <c>SchemaUpgrader</c> / <c>SeedData</c> 同源）</summary>
    public const string RequiredMenuCode = "doc-center";

    /// <summary>单证中心模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "单证中心";

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问单证中心";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问单证中心";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问单证中心（fail closed）";

    /// <summary>缺少既有「单证中心」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「单证中心」（doc-center）模块授权：拒绝访问单证台账 / 明细 / 打印 / 导出 / 生成"
        + "（fail closed，不返回 / 不修改任何单证数据）";

    /// <summary>受限账号未映射业务员的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（单证操作员），不能访问单证中心（fail closed，不泄露任何范围外单证）";

    /// <summary>受限账号访问缺失权威归属（CustomerId 为空）单证的拒绝文案</summary>
    public const string UnlinkedDocumentText =
        "该单证没有可判定的权威客户归属（持久化 CustomerId 缺失）：受限账号拒绝访问（fail closed，不泄露无主单证）";

    /// <summary>单证客户越范围的拒绝文案</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该单证的归属客户：拒绝操作（fail closed，不泄露范围外单证）";

    /// <summary>来源单据缺失权威归属 / 越界时生成单证的拒绝文案（fail closed，不通过共享来源泄露）</summary>
    public const string SourceOutOfScopeText =
        "来源单据的归属客户缺失或不在当前账号客户数据范围内：拒绝生成单证"
        + "（fail closed，不泄露范围外客户，也不产生任何单证与明细行）";

    /// <summary>客户不存在 / 已删除 / 已停用的拒绝文案</summary>
    public const string CustomerUnavailableText = "归属客户不存在、已删除或已停用，不能作为单证客户";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "单证中心授权与数据范围护栏：列表 / 计数 / 详情 / 打印 / 导出 / 明细行读取 / 新增 / 修改 / 删除 / 批量删除，"
        + "以及由销售订单 / 装柜清单带入预填与直接生成，每一路由都会重新校验当前身份（缺失 / 已删除 / 禁用一律 fail closed）、"
        + "既有「单证中心」（doc-center）菜单授权与 SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；"
        + "单证只按持久化 CustomerId 判定权威归属（绝不按单号 / 柜号 / 客户名等自由文本推断），"
        + "受限账号不得访问无主单证，特权账号保留历史访问；列表 / 导出在计数与分页之前把范围下推到数据库；"
        + "写入前校验拟议客户落在实时范围内且真实启用；混合允许 / 越界批次整体拒绝、不做部分删除；"
        + "生成目标必须归属到来源单据的权威客户并在范围内，绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外单证。";

    /// <summary>边界文案（不改变单证商业口径、不改写来源单据 / 库存来源审计）</summary>
    public const string BoundaryText =
        "本护栏只保护单证中心的授权、范围与写入边界：不改写单证身份 / 单证编号 / 状态 / 明细行快照 / 审计字段，"
        + "不改写销售订单、装柜清单、库存与库存流水、发票、退税、费用或财务数据，不删除历史证据，"
        + "也不新增任何表 / 列 / 菜单 / 权限；库存来源单据审计与既有明细行计算 / 状态冻结口径保持不变。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「单证中心」菜单授权三重实时校验（fail closed），返回本次请求的权威客户数据范围：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 非特权账号缺少既有 <c>doc-center</c> 菜单授权或未映射业务员按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛；特权账号保留既有全部访问。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    // ==================== 2. 范围下推（计数 / 分页之前） ====================

    /// <summary>
    /// 单证中心作用域谓词：<c>null</c>（进程内调用）或特权账号返回恒真；受限账号只保留<b>已登记归属客户且该客户在范围内</b>
    /// 的单证，缺失权威归属或越界一律排除，绝不「先查全量再内存过滤」。
    /// </summary>
    public static Expression<Func<TradeDocument, bool>> ScopeFilter(SalespersonDataScope? scope)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return _ => true;
        if (scope.AllowedCustomerIds.Count == 0) return _ => false;

        var allowed = scope.AllowedCustomerIds.ToList();
        return document => document.CustomerId.HasValue && allowed.Contains(document.CustomerId.Value);
    }

    /// <summary>按权威归属客户把单证范围下推到查询（<see cref="ScopeFilter"/> 的便捷重载）。</summary>
    public static IQueryable<TradeDocument> ApplyScope(IQueryable<TradeDocument> source, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(ScopeFilter(scope));
    }

    // ==================== 3. 已存 / 拟议归属校验 ====================

    /// <summary>
    /// 受限账号按持久化客户 Id 的硬范围边界：<c>null</c>（进程内）/ 特权账号放行；
    /// 受限账号的缺失权威归属（<c>null</c> / <c>&lt;= 0</c>）与越界一律 fail closed。
    /// </summary>
    public static void EnsureCustomerInScope(SalespersonDataScope? scope, long? customerId)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (customerId is null or <= 0)
            throw new BusinessException(UnlinkedDocumentText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>已存单证的范围校验（读取 / 详情 / 打印 / 导出 / 修改 / 删除之前调用；特权账号保留历史访问）。</summary>
    public static void EnsureStoredScopeAllowed(SalespersonDataScope? scope, TradeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EnsureCustomerInScope(scope, document.CustomerId);
    }

    /// <summary>
    /// 锁内重新加载父单证并复核实时授权口径（ERP-395）：在取得父单证行锁（UPDLOCK, HOLDLOCK）之后调用，
    /// 按主键重新读取<b>持久化</b>状态（存在 / 未删除），再复核权威客户范围；不存在 / 已删除按「数据不存在」
    /// 拒绝，受限账号无主 / 越界 fail closed。返回权威在库单证，供调用方在锁内继续校验状态与明细行规则。
    /// </summary>
    public static async Task<TradeDocument> LoadLockedDocumentAsync(
        IErpDbContext db, SalespersonDataScope? scope, long documentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (documentId <= 0) throw BusinessException.InvalidParameter("请指定单证");

        var document = await db.TradeDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId && !d.IsDeleted, ct)
            ?? throw BusinessException.NotFound(
                "单证不存在或已删除：无法维护单证或明细行（并发删除已先行提交，fail closed）");

        EnsureStoredScopeAllowed(scope, document);
        return document;
    }

    /// <summary>
    /// 来源单据（销售订单 / 装柜清单）的权威客户范围校验（带入预填 / 直接生成之前调用）：
    /// 受限账号的来源缺失权威归属或越界一律 fail closed，绝不通过共享来源泄露范围外客户。
    /// </summary>
    public static void EnsureSourceScopeAllowed(SalespersonDataScope? scope, long? sourceCustomerId)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (sourceCustomerId is null or <= 0)
            throw new BusinessException(SourceOutOfScopeText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(sourceCustomerId.Value))
            throw new BusinessException(SourceOutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 按 Id 精确解析权威客户：必须真实存在、未删除且启用，否则一律拒绝（绝不按自由文本猜测客户）。
    /// </summary>
    public static async Task<BaseCustomer> EnsureCustomerAvailableAsync(
        IErpDbContext db, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter(CustomerUnavailableText);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        if (customer is null || customer.Status != ContainerLoadingParticipantRules.ActiveStatus)
            throw BusinessException.InvalidParameter($"{CustomerUnavailableText}（客户 Id={customerId}）");
        return customer;
    }

    /// <summary>
    /// 拟议单证（新增 / 修改）的完整范围校验（改写任何字段之前调用）：受限账号的拟议客户必须在实时范围内；
    /// 已登记客户必须是真实启用客户。<c>null</c> 范围（进程内调用）保持既有内部口径、不做范围限制。
    /// </summary>
    public static async Task EnsureProposedScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, TradeDocument proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(proposed);

        EnsureCustomerInScope(scope, proposed.CustomerId);
        if (scope is not null && proposed.CustomerId is > 0)
            await EnsureCustomerAvailableAsync(db, proposed.CustomerId.Value, ct);
    }

    // ==================== 4. 批量删除的归属投影 ====================

    /// <summary>
    /// 最小归属投影：按 Id 精确读取未删除单证的归属字段，供控制器在不装载全量单证的前提下复核范围
    /// （批量删除逐行复核，混合允许 / 越界批次整体拒绝）。
    /// </summary>
    public static async Task<List<TradeDocument>> LoadOwnershipAsync(
        IErpDbContext db, IEnumerable<long> documentIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(documentIds);

        var ids = documentIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new List<TradeDocument>();

        return await db.TradeDocuments.AsNoTracking()
            .Where(d => ids.Contains(d.Id) && !d.IsDeleted)
            .Select(d => new TradeDocument { Id = d.Id, CustomerId = d.CustomerId })
            .ToListAsync(ct);
    }
}
