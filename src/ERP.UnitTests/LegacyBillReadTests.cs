using System.Reflection;
using ERP.Api.Controllers;
using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-405 旧单据（<c>api/v2/bills</c>）读侧（查询 / 翻页导航 / 详情 / 默认值）授权与行范围单元测试
/// （内存库 + 真实既有身份 / 菜单授权 + 真实受控只读服务 + 范围语义一致的内存读实现）。
/// <list type="bullet">
/// <item>有限目录：读侧恰好覆盖 <c>BillProcController.Bills</c> 全部 16 族；表头列派生自 ERP-308 授权导出白名单；
/// 行归属按族显式登记（客户归属列必须命中授权白名单，无归属族显式 fail closed）；</item>
/// <item>范围先于读取：特权不加约束、受限按客户归属列参数化、空集合恒假、无归属族 / 超大范围 fail closed；
/// 非法分页与偏移溢出在打开查询之前拒绝；</item>
/// <item>读侧授权：缺失 / 已删除身份按未认证拒绝，已禁用按权限不足拒绝，无功能菜单拒绝，
/// 仅导出菜单（<c>*-export</c>）不顶替功能菜单；受限账号解析出实时客户范围；</item>
/// <item>控制器：受限账号分页 / 导航 / 详情只含授权客户；越权锚点与越权 Oid 不泄露；默认值同样先授权；全部零写入。</item>
/// </list>
/// <para>跨连接竞态与真实 SQL 由 <c>LegacyBillReadSqlServerTests</c> 覆盖。</para>
/// </summary>
public class LegacyBillReadTests
{
    private const string UnusedConnectionString =
        "Server=(localdb)\\NEWERP_UnitTests_Gate;Database=UNUSED;Integrated Security=true;Connect Timeout=1";

    // ==================== 脚手架 ====================

    private static StoredProcedureService StoredProcedures()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = UnusedConnectionString
            })
            .Build();
        return new StoredProcedureService(configuration);
    }

    private static LegacyBillReadService RealReadService() => new(StoredProcedures());

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static BillProcController NewController(ErpDbContext db, long? userId, ILegacyBillReadService? reads = null)
    {
        var controller = reads is null
            ? new BillProcController(StoredProcedures(), db,
                new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance))
            : new BillProcController(StoredProcedures(), db,
                new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance), reads);

        var http = new DefaultHttpContext();
        if (userId.HasValue)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static Dictionary<string, BillMeta> BillsCatalog()
    {
        var field = typeof(BillProcController).GetField("Bills", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Dictionary<string, BillMeta>)field!.GetValue(null)!;
    }

    /// <summary>播种一位**受限制**操作员（非系统内置角色 → 非特权）并按需授予既有功能菜单。</summary>
    private static (SysUser User, SysRole Role) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"legacy-read-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "受限读操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "受限读角色", RoleCode = $"RR-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return (user, role);
    }

    private static void GrantMenus(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    /// <summary>播种受限制业务员映射：登录账号名 = 员工编码（<c>IsSalesman</c>），可见 / 隐藏两位客户分别挂在名下或空置。</summary>
    private static (BaseCustomer Visible, BaseCustomer Hidden) SeedScopedSalesman(ErpDbContext db, long userId)
    {
        var userName = db.SysUsers.AsNoTracking().First(u => u.Id == userId).UserName;
        var employee = new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = "受限读业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var visible = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "可见客户",
            EmpId = employee.Id,
            Status = 1,
            CreditStatus = "正常"
        };
        var hidden = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "隐藏客户",
            EmpId = null,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(visible, hidden);
        db.SaveChanges();
        return (visible, hidden);
    }

    private static SysUser SeedUser(ErpDbContext db, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"legacy-read-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }


    // ==================== 内存读实现（与真实服务共用同一范围语义） ====================

    /// <summary>
    /// 与真实 <see cref="LegacyBillReadService"/> 共用 <c>ResolveScopeClause</c> 的范围语义：特权看到全部行，
    /// 受限账号只看到授权客户行，无权威归属族 / 空集合 fail closed；并记录调用与收到的范围。
    /// </summary>
    private sealed class RecordingReadService : ILegacyBillReadService
    {
        private readonly List<(long Oid, long CustId, string BillNo)> _rows;

        public RecordingReadService(params (long Oid, long CustId, string BillNo)[] rows) => _rows = rows.ToList();

        public int PageCalls { get; private set; }
        public int DetailCalls { get; private set; }
        public int NavigateCalls { get; private set; }
        public SalespersonDataScope? LastScope { get; private set; }

        private List<(long Oid, long CustId, string BillNo)> Scoped(string familyKey, SalespersonDataScope scope)
        {
            var family = LegacyBillReadCatalog.Resolve(familyKey);
            var clause = LegacyBillReadService.ResolveScopeClause(family, scope);
            if (scope.AllowedCustomerIds is null)
                return _rows.OrderBy(r => r.Oid).ToList();
            if (clause.Sql == "1=0")
                return new List<(long, long, string)>();

            var allowed = scope.AllowedCustomerIds;
            return _rows.Where(r => allowed.Contains(r.CustId)).OrderBy(r => r.Oid).ToList();
        }

        private static Dictionary<string, object?> Row((long Oid, long CustId, string BillNo) row)
            => new(StringComparer.Ordinal) { ["Oid"] = row.Oid, ["BillNo"] = row.BillNo, ["CustId"] = row.CustId };

        public Task<LegacyBillReadPage> ReadPageAsync(
            string familyKey, LegacyBillReadQuery query, SalespersonDataScope scope,
            CancellationToken cancellationToken = default)
        {
            PageCalls++;
            LastScope = scope;
            var family = LegacyBillReadCatalog.Resolve(familyKey);
            var scoped = Scoped(familyKey, scope).OrderByDescending(r => r.Oid).ToList();
            return Task.FromResult(new LegacyBillReadPage
            {
                Columns = family.HeaderColumns,
                Items = scoped.Select(Row).ToList(),
                Total = scoped.Count,
                Page = query.Page,
                PageSize = query.PageSize,
                TotalPages = scoped.Count == 0 ? 0 : (int)Math.Ceiling(scoped.Count / (double)query.PageSize),
            });
        }

        public Task<LegacyBillReadDetail?> ReadDetailAsync(
            string familyKey, long oid, SalespersonDataScope scope,
            CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            LastScope = scope;
            var hit = Scoped(familyKey, scope).FirstOrDefault(r => r.Oid == oid);
            if (hit.BillNo is null)
                return Task.FromResult<LegacyBillReadDetail?>(null);

            return Task.FromResult<LegacyBillReadDetail?>(new LegacyBillReadDetail
            {
                Main = Row(hit),
                Details = new List<Dictionary<string, object?>>
                {
                    new(StringComparer.Ordinal) { ["Oid"] = hit.Oid, ["ProductName"] = "明细行" }
                },
            });
        }

        public Task<LegacyBillNavigateResult> NavigateAsync(
            string familyKey, long oid, string? direction, SalespersonDataScope scope,
            CancellationToken cancellationToken = default)
        {
            NavigateCalls++;
            LastScope = scope;

            var normalized = (direction ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized is not ("first" or "last" or "prev" or "next"))
                throw BusinessException.InvalidParameter("方向参数无效");

            var scoped = Scoped(familyKey, scope);
            if (normalized is "prev" or "next" && !scoped.Any(r => r.Oid == oid))
                return Task.FromResult(new LegacyBillNavigateResult { Found = false, Row = null });

            var pick = normalized switch
            {
                "first" => scoped.FirstOrDefault(),
                "last" => scoped.LastOrDefault(),
                "prev" => scoped.LastOrDefault(r => r.Oid < oid),
                _ => scoped.FirstOrDefault(r => r.Oid > oid),
            };

            // 元组未命中时 BillNo 为 null；范围内没有更多与不可访问锚点返回同一结果（不泄露）。
            return Task.FromResult(pick.BillNo is null
                ? new LegacyBillNavigateResult { Found = false, Row = null }
                : new LegacyBillNavigateResult { Found = true, Row = Row(pick) });
        }
    }


    // ==================== 1. 读侧有限目录与显式行归属 ====================

    private static readonly SalespersonDataScope PrivilegedScope =
        new() { IsPrivileged = true, AllowedCustomerIds = null };

    private static SalespersonDataScope RestrictedScope(params long[] allowed)
        => new() { IsPrivileged = false, SalesmanId = 1, AllowedCustomerIds = allowed.ToHashSet() };

    private static LegacyBillReadQuery ReadQuery(int page = 1, int pageSize = 20)
        => new() { Page = page, PageSize = pageSize };

    [Fact]
    public void 读侧目录恰好覆盖全部16族且表头列派生自授权导出白名单()
    {
        var catalog = BillsCatalog();
        Assert.Equal(16, LegacyBillReadCatalog.Families.Count);
        Assert.Equal(catalog.Count, LegacyBillReadCatalog.Families.Count);
        Assert.Equal(
            catalog.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            LegacyBillReadCatalog.Families.Select(f => f.FamilyKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var family in LegacyBillReadCatalog.Families)
        {
            var meta = catalog[family.FamilyKey];
            var export = LegacyBillExportCatalog.Resolve(family.FamilyKey);

            Assert.Equal(meta.Table, family.TableName);
            Assert.Equal(meta.Table, export.TableName);
            Assert.Equal(family.FamilyKey, family.ModuleMenuCode);
            Assert.DoesNotContain("export", family.ModuleMenuCode, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(LegacyBillReadCatalog.OidColumn, family.HeaderColumns[0]);
            Assert.Equal(
                new[] { LegacyBillReadCatalog.OidColumn }.Concat(export.Columns.Select(c => c.Key)).ToArray(),
                family.HeaderColumns.ToArray());

            Assert.True(LegacyBillReadCatalog.TryResolve(family.FamilyKey, out var resolved));
            Assert.Equal(family.FamilyKey, resolved.FamilyKey);
        }
    }

    [Fact]
    public void 读侧行归属显式登记且客户归属列必须命中授权白名单()
    {
        foreach (var family in LegacyBillReadCatalog.Families)
        {
            if (family.OwnershipKind == LegacyBillOwnershipKind.Customer)
            {
                Assert.False(string.IsNullOrWhiteSpace(family.OwnershipColumn));
                Assert.Contains(family.OwnershipColumn!, family.HeaderColumns, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Null(family.OwnershipColumn);
            }
        }

        Assert.Equal("CustId", LegacyBillReadCatalog.Resolve("sales-order").OwnershipColumn);
        Assert.Equal("CustomerId", LegacyBillReadCatalog.Resolve("inquiry").OwnershipColumn);

        // 供应商 / 业务员 / 无归属族显式 fail closed（受限账号不可读）。
        foreach (var familyKey in new[] { "purchase-order", "stock-in", "payment", "receiving-plan", "pre-loading" })
            Assert.Equal(LegacyBillOwnershipKind.None, LegacyBillReadCatalog.Resolve(familyKey).OwnershipKind);

        // 仅登记的族才有副表明细（其余 = 有意兼容省略）。
        Assert.Equal("SalesOrderDetail", LegacyBillReadCatalog.Resolve("sales-order").Detail!.TableName);
        Assert.Null(LegacyBillReadCatalog.Resolve("receipt").Detail);
    }

    [Fact]
    public void 未知或畸形读侧族标识在访问数据之前被拒绝()
    {
        foreach (var invalid in new[] { null, "", "   ", "unknown", "sales order", "a;drop table", new string('x', 65) })
        {
            Assert.Throws<BusinessException>(() => LegacyBillReadCatalog.Resolve(invalid));
            Assert.False(LegacyBillReadCatalog.TryResolve(invalid, out _));
        }

        // 大小写不敏感命中受控目录，但返回的族键仍是服务端规范常量（绝不回显调用方输入）。
        Assert.Equal("sales-order", LegacyBillReadCatalog.Resolve("SALES-ORDER").FamilyKey, ignoreCase: true);
        Assert.True(LegacyBillReadCatalog.TryResolve(" Sales-Order ", out var trimmed));
        Assert.Equal("sales-order", trimmed.FamilyKey);
    }

    // ==================== 2. 范围子句（纯函数，先于任何查询） ====================

    [Fact]
    public void 范围子句_特权不加约束_受限按客户列参数化_空集合恒假()
    {
        var salesOrder = LegacyBillReadCatalog.Resolve("sales-order");

        var privileged = LegacyBillReadService.ResolveScopeClause(salesOrder, PrivilegedScope);
        Assert.Equal("1=1", privileged.Sql);
        Assert.Empty(privileged.Parameters);

        var restricted = LegacyBillReadService.ResolveScopeClause(salesOrder, RestrictedScope(7, 3));
        Assert.Equal("[CustId] IN (@scope0, @scope1)", restricted.Sql);
        Assert.Equal(new object?[] { 3L, 7L }, restricted.Parameters.Select(p => p.Value).ToArray());

        Assert.Equal("1=0", LegacyBillReadService.ResolveScopeClause(salesOrder, RestrictedScope()).Sql);
    }

    [Fact]
    public void 受限账号在无权威客户归属族上failclosed而特权照常()
    {
        foreach (var familyKey in new[] { "purchase-order", "stock-in", "payment", "receiving-plan", "pre-loading" })
        {
            var family = LegacyBillReadCatalog.Resolve(familyKey);

            var denied = Assert.Throws<BusinessException>(
                () => LegacyBillReadService.ResolveScopeClause(family, RestrictedScope(1)));
            Assert.Equal(ErrorCodes.Forbidden, denied.Code);
            Assert.Contains(familyKey, denied.Message, StringComparison.Ordinal);

            // 特权账号在无归属族上仍可读（既有全部访问口径）。
            Assert.Equal("1=1", LegacyBillReadService.ResolveScopeClause(family, PrivilegedScope).Sql);
        }
    }

    [Fact]
    public void 受限账号客户范围超过受控上限failclosed()
    {
        var family = LegacyBillReadCatalog.Resolve("sales-order");
        var huge = Enumerable.Range(1, LegacyBillReadService.MaxScopeCustomers + 1).Select(i => (long)i).ToArray();

        var denied = Assert.Throws<BusinessException>(
            () => LegacyBillReadService.ResolveScopeClause(family, RestrictedScope(huge)));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);
    }


    // ==================== 3. 真实受控只读服务：边界裁决先于查询，缺结构 environment-blocked ====================

    [Fact]
    public async Task 非法分页与偏移溢出在打开查询之前被拒绝()
    {
        var service = RealReadService();

        foreach (var pageSize in new[] { 0, -1, LegacyBillReadService.MaxPageSize + 1, 100000 })
        {
            var denied = await Assert.ThrowsAsync<BusinessException>(
                () => service.ReadPageAsync("sales-order", ReadQuery(1, pageSize), PrivilegedScope));
            Assert.Equal(ErrorCodes.InvalidParameter, denied.Code);
        }

        var zeroPage = await Assert.ThrowsAsync<BusinessException>(
            () => service.ReadPageAsync("sales-order", ReadQuery(0, 20), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, zeroPage.Code);

        var overflow = await Assert.ThrowsAsync<BusinessException>(() => service.ReadPageAsync(
            "sales-order", ReadQuery(int.MaxValue, LegacyBillReadService.MaxPageSize), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Contains("偏移", overflow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知族_非法方向与非法Oid在打开查询之前被拒绝()
    {
        var service = RealReadService();

        var unknown = await Assert.ThrowsAsync<BusinessException>(
            () => service.ReadPageAsync("no-such-bill", ReadQuery(), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, unknown.Code);

        foreach (var direction in new[] { "drop", null, " ", "first;drop" })
        {
            var denied = await Assert.ThrowsAsync<BusinessException>(
                () => service.NavigateAsync("sales-order", 1, direction, PrivilegedScope));
            Assert.Equal(ErrorCodes.InvalidParameter, denied.Code);
        }

        var badOid = await Assert.ThrowsAsync<BusinessException>(
            () => service.ReadDetailAsync("sales-order", 0, PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, badOid.Code);

        // prev / next 需要合法锚点；first / last 不需要锚点（调用方通常传 0），因此会前进到查询阶段。
        var relativeWithoutAnchor = await Assert.ThrowsAsync<BusinessException>(
            () => service.NavigateAsync("sales-order", 0, "next", PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, relativeWithoutAnchor.Code);

        var firstWithoutAnchor = await Assert.ThrowsAsync<BusinessException>(
            () => service.NavigateAsync("sales-order", 0, "first", PrivilegedScope));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, firstWithoutAnchor.Code);

        // 受限账号在无权威归属族上先于任何查询 fail closed。
        var deniedFamily = await Assert.ThrowsAsync<BusinessException>(
            () => service.ReadPageAsync("purchase-order", ReadQuery(), RestrictedScope(1)));
        Assert.Equal(ErrorCodes.Forbidden, deniedFamily.Code);
    }

    [Fact]
    public async Task 真实服务在旧结构不可达时environmentBlocked且不回退规范表()
    {
        var service = RealReadService();

        var page = await Assert.ThrowsAsync<BusinessException>(
            () => service.ReadPageAsync("sales-order", ReadQuery(), PrivilegedScope));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, page.Code);
        Assert.Contains("environment-blocked", page.Message, StringComparison.Ordinal);
    }

    // ==================== 4. 读侧授权（实时身份 + 既有功能菜单，导出菜单不顶替） ====================

    [Fact]
    public async Task 读侧授权_缺失已删除已禁用无菜单仅导出菜单均被拒绝()
    {
        await using var db = TestDbFactory.Create();

        var missing = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, null, "sales-order"));
        Assert.Equal(ErrorCodes.Unauthorized, missing.Code);

        var invalid = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, -1, "sales-order"));
        Assert.Equal(ErrorCodes.Unauthorized, invalid.Code);

        var deleted = SeedUser(db, UserStatus.Enabled);
        deleted.IsDeleted = true;
        db.SaveChanges();
        var deletedEx = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, deleted.Id, "sales-order"));
        Assert.Equal(ErrorCodes.Unauthorized, deletedEx.Code);

        var disabled = SeedUser(db, UserStatus.Disabled);
        var disabledEx = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, disabled.Id, "sales-order"));
        Assert.Equal(ErrorCodes.Forbidden, disabledEx.Code);

        var (noMenu, _) = SeedRestrictedOperator(db);
        var noMenuEx = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, noMenu.Id, "sales-order"));
        Assert.Equal(ErrorCodes.Forbidden, noMenuEx.Code);

        // 仅导出菜单绝不顶替功能菜单（普通读取也不要求导出菜单）。
        var (exportOnly, _) = SeedRestrictedOperator(db, "sales-order-export");
        var exportEx = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, exportOnly.Id, "sales-order"));
        Assert.Equal(ErrorCodes.Forbidden, exportEx.Code);

        // 未知 / 畸形族标识先于身份解析拒绝。
        var unknown = await Assert.ThrowsAsync<BusinessException>(
            () => LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, exportOnly.Id, "no-such-bill"));
        Assert.Equal(ErrorCodes.InvalidParameter, unknown.Code);

        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    [Fact]
    public async Task 读侧授权_受限账号解析实时客户范围且特权照常()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);

        var scope = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, user.Id, "sales-order");
        Assert.False(scope.IsPrivileged);
        Assert.NotNull(scope.AllowedCustomerIds);
        Assert.Contains(visible.Id, scope.AllowedCustomerIds!);
        Assert.DoesNotContain(hidden.Id, scope.AllowedCustomerIds!);

        // 特权账号（系统内置角色）沿用既有全部访问口径，但仍须通过实时身份校验。
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        var privileged = await LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(db, privilegedId, "sales-order");
        Assert.True(privileged.IsPrivileged);
        Assert.Null(privileged.AllowedCustomerIds);
    }


    // ==================== 5. 控制器：范围约束、越权不泄露与零写入 ====================

    private const long VisibleOid = 101;
    private const long HiddenOid = 202;

    private static string Serialize(object? value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public async Task 控制器_未授权身份不读取任何行()
    {
        await using var db = TestDbFactory.Create();
        var reads = new RecordingReadService((VisibleOid, 1, "SO-1"));

        var page = Envelope(await NewController(db, null, reads).GetPaged("sales-order", new PageQuery(), null));
        Assert.Equal(ErrorCodes.Unauthorized, page.Code);

        var detail = Envelope(await NewController(db, null, reads).GetDetail("sales-order", VisibleOid));
        Assert.Equal(ErrorCodes.Unauthorized, detail.Code);

        var navigation = Envelope(await NewController(db, null, reads).Navigate("sales-order", VisibleOid, "next"));
        Assert.Equal(ErrorCodes.Unauthorized, navigation.Code);

        Assert.Equal(0, reads.PageCalls);
        Assert.Equal(0, reads.DetailCalls);
        Assert.Equal(0, reads.NavigateCalls);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    [Fact]
    public async Task 控制器_受限账号分页与详情只含授权客户行()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new RecordingReadService(
            (VisibleOid, visible.Id, "SO-VISIBLE"),
            (HiddenOid, hidden.Id, "SO-HIDDEN"));
        var controller = NewController(db, user.Id, reads);

        var page = Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 50 }, null));
        Assert.Equal(ErrorCodes.Success, page.Code);
        var payload = Serialize(page.Data);
        Assert.Contains("SO-VISIBLE", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("SO-HIDDEN", payload, StringComparison.Ordinal);

        Assert.NotNull(reads.LastScope);
        Assert.False(reads.LastScope!.IsPrivileged);
        Assert.Equal(new[] { visible.Id }, reads.LastScope.AllowedCustomerIds!.ToArray());

        var allowed = Envelope(await controller.GetDetail("sales-order", VisibleOid));
        Assert.Equal(ErrorCodes.Success, allowed.Code);

        var denied = Envelope(await controller.GetDetail("sales-order", HiddenOid));
        Assert.Equal(ErrorCodes.NotFound, denied.Code);
        Assert.Null(denied.Data);

        Assert.Empty(db.SysOperationLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 控制器_不可访问锚点导航与无归属族均不泄露()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order", "purchase-order");
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new RecordingReadService(
            (VisibleOid, visible.Id, "SO-VISIBLE"),
            (HiddenOid, hidden.Id, "SO-HIDDEN"));
        var controller = NewController(db, user.Id, reads);

        // 越权锚点与「范围内没有更多」返回同一结果（NotFound），不泄露锚点存在性。
        var deniedAnchor = Envelope(await controller.Navigate("sales-order", HiddenOid, "next"));
        Assert.Equal(ErrorCodes.NotFound, deniedAnchor.Code);
        Assert.Null(deniedAnchor.Data);

        var withinScope = Envelope(await controller.Navigate("sales-order", VisibleOid, "first"));
        Assert.Equal(ErrorCodes.Success, withinScope.Code);
        Assert.Contains("SO-VISIBLE", Serialize(withinScope.Data), StringComparison.Ordinal);

        // 无权威客户归属族：受限账号在读取任何行之前 fail closed。
        var unresolved = Envelope(await controller.GetPaged("purchase-order", new PageQuery(), null));
        Assert.Equal(ErrorCodes.Forbidden, unresolved.Code);
        Assert.Contains("归属", unresolved.Message, StringComparison.Ordinal);
        Assert.Null(unresolved.Data);
    }

    [Fact]
    public async Task 控制器_默认值与查询共用读侧门禁且拒绝时零写入()
    {
        await using var db = TestDbFactory.Create();
        var reads = new RecordingReadService((VisibleOid, 1, "SO-VISIBLE"));

        var anonymous = Envelope(await NewController(db, null, reads).GetDefaults("sales-order"));
        Assert.Equal(ErrorCodes.Unauthorized, anonymous.Code);

        var (noMenu, _) = SeedRestrictedOperator(db);
        var deniedByMenu = Envelope(await NewController(db, noMenu.Id, reads).GetDefaults("sales-order"));
        Assert.Equal(ErrorCodes.Forbidden, deniedByMenu.Code);

        var unknownType = Envelope(await NewController(db, noMenu.Id, reads).GetDefaults("no-such-bill"));
        Assert.Equal(ErrorCodes.InvalidParameter, unknownType.Code);

        Assert.Equal(0, reads.PageCalls);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 控制器_特权账号读侧沿用既有全量口径()
    {
        await using var db = TestDbFactory.Create();
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        var reads = new RecordingReadService(
            (VisibleOid, 1, "SO-VISIBLE"),
            (HiddenOid, 2, "SO-HIDDEN"));

        var page = Envelope(await NewController(db, privilegedId, reads)
            .GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 50 }, null));

        Assert.Equal(ErrorCodes.Success, page.Code);
        var payload = Serialize(page.Data);
        Assert.Contains("SO-VISIBLE", payload, StringComparison.Ordinal);
        Assert.Contains("SO-HIDDEN", payload, StringComparison.Ordinal);
        Assert.True(reads.LastScope!.IsPrivileged);
    }

}
