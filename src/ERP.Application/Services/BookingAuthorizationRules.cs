using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 订柜信息实时授权、客户数据范围与主数据可用性护栏（ERP-360）。
/// <para>订柜信息（<see cref="ContainerBooking"/>）是装柜链路的**上游权威来源**（ERP-353 / ERP-040），
/// 因此 <b>列表 / 详情 / 出运时间线 / 报关行选项 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除</b> 每一个路由
/// 都必须先重新解析：<b>实时身份</b>（缺失 / 非法按未认证拒绝）→ <b>账号状态</b>（不存在 / 已删除按未认证，
/// 禁用按权限不足）→ <b>既有「订柜信息」（booking）菜单授权</b>（非特权账号必须显式具备）→
/// <b>权威客户数据范围</b>（复用 ERP-097 <see cref="SalespersonDataScopeService"/>，未映射业务员的受限账号
/// fail closed，绝不降级为全局 / 管理员可见）。</para>
/// <para><b>数据库侧范围先于计数与分页</b>：列表通过 <see cref="ApplyScope"/> 在 <c>Count</c> 之前把范围下推到
/// SQL（受限制账号只统计 / 只返回本人客户），绝不先查全量再内存过滤。</para>
/// <para><b>实时主数据校验</b>：新增 / 修改 / 状态变更（提交 / 审核 / 取消 / 删除）之前，客户必须真实可用
/// （存在、未删除、已启用），可选供应商填写时也必须真实可用；<b>历史读取不受影响</b>，主数据后来停用 / 删除时
/// 照实返回并给出显式不可用证据（<see cref="DescribeUnavailableMasterDataAsync"/>），绝不回填、绝不改写、
/// 绝不按自由文本猜测归属。</para>
/// <para><b>状态变更串行化</b>：提交 / 审核 / 取消 / 删除共用 ERP-353 的订柜行锁
/// （<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务，由调用方（<c>ContainerBookingController</c>）
/// 负责开事务、执行行锁、提交 / 回滚。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改单据、不改库存 / 财务 / 单证，不新增任何表 / 列 / 菜单 /
/// 权限模型，也不把空身份当作管理员。</para>
/// </summary>
public static class BookingAuthorizationRules
{
    /// <summary>订柜信息模块所需既有菜单编码（与 <see cref="PreLoadingBookingLinkRules.BookingRequiredMenuCode"/> 同源）</summary>
    public const string RequiredMenuCode = PreLoadingBookingLinkRules.BookingRequiredMenuCode;

    /// <summary>订柜信息模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = PreLoadingBookingLinkRules.BookingRequiredMenuText;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（订柜信息操作员），不能访问订柜信息（fail closed，不泄露任何范围外单据）";

    /// <summary>越客户范围的拒绝文案（fail closed，不泄露范围外单据）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该订柜信息的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>历史读取时主数据不可用的显式证据前缀（接口文案 / 文档同源）</summary>
    public const string UnavailableEvidencePrefix =
        "历史订柜信息来源主数据不可用（只读照常返回，不可用于新增 / 修改 / 状态变更）：";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "订柜信息（列表 / 详情 / 出运时间线 / 报关行选项 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除）在读取任何计数、" +
        "来源字段或生成单据号之前，都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）、" +
        "既有「订柜信息」（booking）菜单授权与权威客户数据范围（复用 ERP-097）；受限制账号只能读写本人客户的订柜信息，" +
        "数据库侧范围先于计数与分页；未映射业务员的受限账号 fail closed，绝不降级为全局 / 管理员可见。";

    /// <summary>主数据校验与边界文案（不新增权限 / 表列 / 外部调用）</summary>
    public const string BoundaryText =
        "新增 / 修改 / 提交 / 审核 / 取消 / 删除之前，客户必须真实可用（存在、未删除、已启用）、填写供应商时供应商同样必须可用；" +
        "历史订柜信息照常可读并给出显式不可用证据，绝不回填 / 改写历史字段，也不按单号等自由文本猜测归属。" +
        "本护栏不新增任何表 / 列 / 菜单 / 权限，不写库存 / 财务 / 单证，不调用船公司 / 海关 / 货代等外部系统；" +
        "状态变更与既有订柜取消共用同一把订柜行锁（ERP-353），保留预装柜 / 装柜清单的取消护栏与原始审计。";

    /// <summary>
    /// 身份 / 账号状态 / 既有「订柜信息」菜单授权 / 权威数据范围四重校验（fail closed），返回本次请求的数据范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号无既有 booking 菜单授权 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号未映射为业务员 → <see cref="ErrorCodes.Forbidden"/>（fail closed，不降级为全局可见）。</item>
    /// </list>
    /// 每次请求重新解析，授权 / 菜单 / 员工映射变更后下一次请求立即收敛；特权账号继承既有全部访问（与 ERP-097 / ERP-353 同源），
    /// 但绝不因为身份缺失而降级为管理员。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再访问{RequiredMenuText}", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException($"登录账号不存在或已删除，禁止访问{RequiredMenuText}", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException($"登录账号已禁用，禁止访问{RequiredMenuText}（fail closed）", ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 / ERP-353 同源）；普通账号必须显式具备订柜信息菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问{RequiredMenuText}" +
                    "（fail closed，不返回 / 不修改任何订柜数据）",
                    ErrorCodes.Forbidden);
            }

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>
    /// 把已解析的数据范围应用到订柜信息查询：特权账号不过滤；受限制账号在 <c>Count</c> / 分页之前
    /// 把客户范围下推到数据库（只统计 / 只返回本人客户的订柜信息）。
    /// </summary>
    public static IQueryable<ContainerBooking> ApplyScope(
        IQueryable<ContainerBooking> source, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        return SalespersonDataScopeService.FilterByCustomer(source, scope, b => b.CustomerId);
    }

    /// <summary>单个订柜信息的客户数据范围守卫：范围外一律拒绝（fail closed，不泄露范围外单据）。</summary>
    public static void EnsureScopeAllowsBooking(SalespersonDataScope scope, ContainerBooking booking)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(booking);
        EnsureScopeAllowsCustomer(scope, booking.CustomerId);
    }

    /// <summary>指定客户必须以正文为准落在当前账号范围内（特权账号恒放行；其余 fail closed 拒绝）。</summary>
    public static void EnsureScopeAllowsCustomer(SalespersonDataScope scope, long customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowsCustomer(customerId))
            return;

        throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 新增 / 修改 / 状态变更前的完整写入门槛：客户 / 供应商实时主数据可用性 + 客户数据范围
    /// （含「修改不能把订柜信息移入 / 移出当前账号的客户范围」）。
    /// </summary>
    /// <param name="storedCustomerId">修改时库中原单的客户 Id（新增 / 状态变更传 <c>null</c>）：原客户也必须在范围内。</param>
    public static async Task EnsureWriteAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerBooking entity, long? storedCustomerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entity);

        EnsureScopeAllowsCustomer(scope, entity.CustomerId);
        if (storedCustomerId.HasValue)
            EnsureScopeAllowsCustomer(scope, storedCustomerId.Value);

        await EnsureCustomerAvailableAsync(db, entity.CustomerId, ct);
        await EnsureSupplierAvailableAsync(db, entity.SupplierId, ct);
    }

    /// <summary>
    /// 新增 / 修改 / 状态变更前的完整校验（授权 + 数据范围 + 客户 / 供应商实时主数据），返回本次请求的数据范围。
    /// 全部判定发生在单据号生成、来源读取与任何写入之前。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureWriteAuthorizedAsync(
        IErpDbContext db, long? userId, ContainerBooking entity, long? storedCustomerId,
        CancellationToken ct = default)
    {
        var scope = await EnsureAuthorizedAsync(db, userId, ct);
        await EnsureWriteAllowedAsync(db, scope, entity, storedCustomerId, ct);
        return scope;
    }

    /// <summary>客户必须真实可用（存在、未删除、已启用）；<c>CustomerId</c> 必须为正整数。</summary>
    public static async Task EnsureCustomerAvailableAsync(
        IErpDbContext db, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter("订柜信息必须指定客户（CustomerId 必须为正整数）");

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound(
                $"客户（Id={customerId}）不存在或已删除，不能用于订柜信息的新增 / 修改 / 状态变更（历史订柜信息仍可读取）");
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用，不能用于订柜信息的新增 / 修改 / 状态变更（历史订柜信息仍可读取）");
    }

    /// <summary>供应商为可选项：未指定（<c>null</c> / <c>&lt;= 0</c>）跳过；填写时必须真实可用（存在、未删除、已启用）。</summary>
    public static async Task EnsureSupplierAvailableAsync(
        IErpDbContext db, long? supplierId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (supplierId is not > 0) return;

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == supplierId.Value, ct);
        if (supplier is null || supplier.IsDeleted)
            throw BusinessException.NotFound(
                $"供应商（Id={supplierId.Value}）不存在或已删除，不能用于订柜信息的新增 / 修改 / 状态变更（历史订柜信息仍可读取）");
        if (supplier.Status != 1)
            throw BusinessException.RuleConflict(
                $"供应商「{supplier.SupplierName}」已停用，不能用于订柜信息的新增 / 修改 / 状态变更（历史订柜信息仍可读取）");
    }

    /// <summary>
    /// 历史读取的显式不可用证据（只读，不写库）：逐条列出客户 / 供应商已停用或已删除的订柜信息，
    /// 说明「历史照常可读、但不能用于新增 / 修改 / 状态变更」；全部可用时返回空串。
    /// </summary>
    public static async Task<string> DescribeUnavailableMasterDataAsync(
        IErpDbContext db, IEnumerable<ContainerBooking>? bookings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var list = bookings?.Where(b => b is not null).ToList() ?? new List<ContainerBooking>();
        if (list.Count == 0) return string.Empty;

        var customerIds = list.Where(b => b.CustomerId > 0).Select(b => b.CustomerId).Distinct().ToList();
        var supplierIds = list.Where(b => b.SupplierId is > 0).Select(b => b.SupplierId!.Value).Distinct().ToList();

        var customers = customerIds.Count == 0
            ? new List<CustomerSnapshot>()
            : await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .Select(c => new CustomerSnapshot
                {
                    Id = c.Id, Name = c.CustomerName, Status = c.Status, IsDeleted = c.IsDeleted
                })
                .ToListAsync(ct);
        var customerMap = customers.ToDictionary(c => c.Id);

        var suppliers = supplierIds.Count == 0
            ? new List<SupplierSnapshot>()
            : await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .Select(s => new SupplierSnapshot
                {
                    Id = s.Id, Name = s.SupplierName, Status = s.Status, IsDeleted = s.IsDeleted
                })
                .ToListAsync(ct);
        var supplierMap = suppliers.ToDictionary(s => s.Id);

        var evidence = new List<string>();
        foreach (var booking in list)
        {
            if (booking.CustomerId > 0)
            {
                if (!customerMap.TryGetValue(booking.CustomerId, out var customer))
                    evidence.Add($"订柜信息 [{booking.BookingNo}] 的客户（Id={booking.CustomerId}）已不存在或已删除");
                else if (customer.IsDeleted || customer.Status != 1)
                    evidence.Add($"订柜信息 [{booking.BookingNo}] 的客户「{customer.Name}」{UnavailableReason(customer.IsDeleted)}");
            }

            if (booking.SupplierId is > 0)
            {
                if (!supplierMap.TryGetValue(booking.SupplierId!.Value, out var supplier))
                    evidence.Add($"订柜信息 [{booking.BookingNo}] 的供应商（Id={booking.SupplierId}）已不存在或已删除");
                else if (supplier.IsDeleted || supplier.Status != 1)
                    evidence.Add($"订柜信息 [{booking.BookingNo}] 的供应商「{supplier.Name}」{UnavailableReason(supplier.IsDeleted)}");
            }
        }

        return evidence.Count == 0 ? string.Empty : string.Join("；", evidence);
    }

    private static string UnavailableReason(bool isDeleted) => isDeleted ? "已删除" : "已停用";

    private sealed class CustomerSnapshot
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Status { get; init; }
        public bool IsDeleted { get; init; }
    }

    private sealed class SupplierSnapshot
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Status { get; init; }
        public bool IsDeleted { get; init; }
    }
}
