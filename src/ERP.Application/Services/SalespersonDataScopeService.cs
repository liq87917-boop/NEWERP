using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 业务员数据范围（ERP-097）：一次解析得到当前账号的数据可见范围。
/// <para>特权账号（超级管理员 / 系统内置角色 / 显式配置的特权角色）<c>AllowedCustomerIds</c> 为 <c>null</c>（不过滤）；</para>
/// <para>受限制的业务员账号 <c>AllowedCustomerIds</c> 为其被分配客户（<c>BaseCustomer.EmpId == 本人</c>）的 Id 集合
/// （未映射到任何业务员时为空集合，即「看不到任何客户」，fail closed）。</para>
/// </summary>
public sealed class SalespersonDataScope
{
    /// <summary>是否特权账号（超级管理员 / 系统内置角色 / 显式特权角色）：特权账号不受数据范围限制</summary>
    public bool IsPrivileged { get; init; }

    /// <summary>解析出的业务员（员工）Id；未映射到业务员时为 null</summary>
    public long? SalesmanId { get; init; }

    /// <summary>允许访问的客户 Id 集合；null 表示不限制（特权账号），非 null 表示限制在该集合内</summary>
    public HashSet<long>? AllowedCustomerIds { get; init; }

    /// <summary>某个客户 Id 是否在当前范围内（特权账号恒为 true；受限制账号仅允许已分配客户）</summary>
    public bool AllowsCustomer(long? customerId)
        => AllowedCustomerIds is null
           || (customerId.HasValue && AllowedCustomerIds.Contains(customerId.Value));
}

/// <summary>
/// 业务员数据范围策略（ERP-097，**唯一权威口径**）：客户、询价单、报价单、PI、销售订单、销售出库（shipment）
/// 与客户销项发票（invoice）的查询共享同一份显式数据范围策略。
/// <list type="number">
/// <item><b>特权判定</b>：账号拥有「超级管理员」（<c>SuperAdmin</c>）角色、任一系统内置角色（<c>IsSystem == true</c>），
/// 或任一出现在系统参数 <see cref="PrivilegedRolesParameterKey"/>（逗号分隔角色编码）中的角色时，视为特权账号，
/// 保留既有全部访问（不过滤）；</item>
/// <item><b>业务员映射</b>：受限制账号按其登录账号 <c>SysUser.UserName</c> 精确匹配员工编码
/// <c>BaseEmployee.EmployeeCode</c> 且该员工 <c>IsSalesman == true</c>（大小写不敏感、去首尾空白）；</item>
/// <item><b>可见客户</b>：受限制业务员仅能看到 <c>BaseCustomer.EmpId == 本人员工 Id</c> 的客户，
/// 以及这些客户相关的销售单据；未映射到业务员时可见客户集合为空（fail closed，不泄露任何数据）；</item>
/// <item><b>每次请求重新解析</b>：授权 / 员工 / 客户分配变更后立即收敛，绝不缓存。</item>
/// </list>
/// <para>边界：本策略只读现有表（<c>SysUsers</c> / <c>SysRoles</c> / <c>SysUserRoles</c> / <c>SysParameters</c> /
/// <c>BaseEmployees</c> / <c>BaseCustomers</c>），<strong>不新增任何表或列</strong>、不改写任何记录，也不引入新的权限模型。</para>
/// </summary>
public static class SalespersonDataScopeService
{
    /// <summary>显式特权角色参数键（逗号分隔角色编码；不配置时默认为仅超级管理员）</summary>
    public const string PrivilegedRolesParameterKey = "DataScopePrivilegedRoles";

    /// <summary>超级管理员角色编码（与 <c>SeedData.AdminRoleCode</c> 同名，避免应用层反向依赖基础设施层）</summary>
    public const string SuperAdminRoleCode = "SuperAdmin";

    /// <summary>
    /// 解析当前账号的数据范围。userId 缺失或非正整数时按未认证拒绝（fail closed）。
    /// </summary>
    public static async Task<SalespersonDataScope> ResolveAsync(IErpDbContext db, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问数据", ErrorCodes.Unauthorized);

        var roles = await db.SysRoles.AsNoTracking()
            .Where(r => !r.IsDeleted && db.SysUserRoles.Any(ur => !ur.IsDeleted && ur.UserId == userId.Value && ur.RoleId == r.Id))
            .ToListAsync();

        var privilegedCodes = await LoadPrivilegedRoleCodesAsync(db);
        if (roles.Any(r => r.IsSystem || privilegedCodes.Contains(r.RoleCode.Trim())))
            return new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };

        var userName = await db.SysUsers.AsNoTracking()
            .Where(u => u.Id == userId.Value && !u.IsDeleted)
            .Select(u => u.UserName)
            .FirstOrDefaultAsync() ?? string.Empty;
        var normalized = userName.Trim();

        var employee = string.IsNullOrEmpty(normalized)
            ? null
            : await db.BaseEmployees.AsNoTracking()
                .FirstOrDefaultAsync(e => !e.IsDeleted && e.IsSalesman && e.EmployeeCode == normalized);

        if (employee is null)
            return new SalespersonDataScope { IsPrivileged = false, SalesmanId = null, AllowedCustomerIds = new HashSet<long>() };

        var allowed = await db.BaseCustomers.AsNoTracking()
            .Where(c => !c.IsDeleted && c.EmpId == employee.Id)
            .Select(c => c.Id)
            .ToListAsync();

        return new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = employee.Id,
            AllowedCustomerIds = allowed.ToHashSet()
        };
    }

    /// <summary>加载显式特权角色编码（系统参数 <see cref="PrivilegedRolesParameterKey"/>，逗号分隔；默认恒含超级管理员）</summary>
    private static async Task<HashSet<string>> LoadPrivilegedRoleCodesAsync(IErpDbContext db)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SuperAdminRoleCode };
        var raw = await db.SysParameters.AsNoTracking()
            .Where(p => !p.IsDeleted && p.ParamKey == PrivilegedRolesParameterKey)
            .Select(p => p.ParamValue)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return set;

        foreach (var code in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(code))
                set.Add(code);
        }
        return set;
    }

    /// <summary>按客户 Id（<see cref="long"/>）过滤查询；特权账号（<c>AllowedCustomerIds == null</c>）不过滤。</summary>
    public static IQueryable<T> FilterByCustomer<T>(
        IQueryable<T> source, SalespersonDataScope scope, Expression<Func<T, long>> customerId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(customerId);
        if (scope.AllowedCustomerIds is null)
            return source;

        var allowed = scope.AllowedCustomerIds.ToList();
        var contains = typeof(List<long>).GetMethod(nameof(List<long>.Contains), new[] { typeof(long) })!;
        var param = customerId.Parameters[0];
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.Call(Expression.Constant(allowed), contains, customerId.Body), param);
        return source.Where(predicate);
    }

    /// <summary>按客户 Id（<see cref="long?"/>，可为空的报价单 / PI 客户）过滤查询；特权账号不过滤。</summary>
    public static IQueryable<T> FilterByCustomer<T>(
        IQueryable<T> source, SalespersonDataScope scope, Expression<Func<T, long?>> customerId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(customerId);
        if (scope.AllowedCustomerIds is null)
            return source;

        var allowed = scope.AllowedCustomerIds.ToList();
        var contains = typeof(List<long>).GetMethod(nameof(List<long>.Contains), new[] { typeof(long) })!;
        var param = customerId.Parameters[0];
        var hasValue = Expression.Property(customerId.Body, nameof(Nullable<long>.HasValue));
        var value = Expression.Property(customerId.Body, nameof(Nullable<long>.Value));
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.AndAlso(hasValue, Expression.Call(Expression.Constant(allowed), contains, value)), param);
        return source.Where(predicate);
    }
}
