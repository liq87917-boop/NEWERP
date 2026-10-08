using System.Text.Json;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-411 旧销售订单专用路由（<c>api/v2/sales-orders</c>）查询 / 翻页导航的读侧授权与行范围单元测试
/// （内存库 + 真实既有身份 / 菜单授权 + 范围语义一致的内存读实现）。
/// <list type="bullet">
/// <item><b>复用 ERP-405</b>：专用读路径复用同一族 <c>sales-order</c>、同一既有「销售订单」功能菜单口径与
/// 同一受控只读服务 <see cref="ILegacyBillReadService"/>（导出菜单 <c>sales-order-export</c> 绝不当作模块权限）；</item>
/// <item><b>范围先于读取</b>：计数 / 分页 / 导航只在调用方数据范围内完成，受限账号看不到越权客户的计数与行；</item>
/// <item><b>prev / next 锚点</b>：越权 / 不存在锚点与「范围内没有更多」返回同一结果，绝不泄露锚点存在性；</item>
/// <item><b>专用投影不变</b>：列表含定金、导航不含定金，金额 / <c>null</c> 原值原样保留；</item>
/// <item><b>专用 vs 通用读侧一致</b>：同一授权记录下 <c>api/v2/sales-orders</c> 与 <c>api/v2/bills</c> 返回同一计数与行；</item>
/// <item><b>零写入</b>：拒绝路径不写操作日志 / 钉钉通知，也不产生任何待写入变更。</item>
/// </list>
/// <para>真实 SQL、越权锚点与两个独立连接竞态由 <c>SalesOrderProcReadSqlServerTests</c> 覆盖。</para>
/// </summary>
public class SalesOrderProcReadTests
{
    private const long VisibleOid1 = 101;
    private const long VisibleOid2 = 102;
    private const long VisibleOid3 = 103;
    private const long HiddenOid = 202;

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

    private static SalesOrderProcController NewController(ErpDbContext db, long? userId, ILegacyBillReadService reads)
    {
        var controller = new SalesOrderProcController(StoredProcedures(), db, reads);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static BillProcController NewGenericController(ErpDbContext db, long? userId, ILegacyBillReadService reads)
    {
        var controller = new BillProcController(StoredProcedures(), db,
            new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance), reads);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static JsonElement Payload(ApiResponse<object> envelope)
        => JsonDocument.Parse(JsonSerializer.Serialize(envelope.Data)).RootElement.Clone();

    private static (int Total, List<long> Oids, List<string?> BillNos) PageOf(ApiResponse<object> envelope)
    {
        var root = Payload(envelope);
        var oids = new List<long>();
        var billNos = new List<string?>();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            oids.Add(item.GetProperty("Oid").GetInt64());
            billNos.Add(item.GetProperty("BillNo").ValueKind == JsonValueKind.Null
                ? null
                : item.GetProperty("BillNo").GetString());
        }

        return (root.GetProperty("total").GetInt32(), oids, billNos);
    }

    private static string Serialize(object? value) => JsonSerializer.Serialize(value);

    /// <summary>播种一位**受限制**操作员（非系统内置角色 → 非特权）并按需授予既有功能菜单。</summary>
    private static (SysUser User, SysRole Role) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"sopr-op-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "旧销售订单受限读操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "受限读角色", RoleCode = $"SR-{Guid.NewGuid():N}", IsSystem = false };
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
            UserName = $"sopr-{Guid.NewGuid():N}",
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
            => Task.FromResult<LegacyBillReadDetail?>(null);

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

            return Task.FromResult(pick.BillNo is null
                ? new LegacyBillNavigateResult { Found = false, Row = null }
                : new LegacyBillNavigateResult { Found = true, Row = Row(pick) });
        }
    }

    // ==================== 1. 读侧门禁先于读取 ====================

    [Fact]
    public async Task 控制器_未授权身份不读取任何行()
    {
        await using var db = TestDbFactory.Create();
        var reads = new RecordingReadService((VisibleOid1, 1, "SO-1"));

        var page = Envelope(await NewController(db, null, reads).GetPaged(new PageQuery(), null));
        Assert.Equal(ErrorCodes.Unauthorized, page.Code);

        var navigation = Envelope(await NewController(db, null, reads).Navigate(VisibleOid1, "next"));
        Assert.Equal(ErrorCodes.Unauthorized, navigation.Code);

        Assert.Equal(0, reads.PageCalls);
        Assert.Equal(0, reads.NavigateCalls);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 控制器_受限账号分页只含授权客户行且保持专用投影()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, SalesOrderProcController.LegacyFamilyKey);
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new RecordingReadService(
            (VisibleOid1, visible.Id, "SO-VISIBLE"),
            (HiddenOid, hidden.Id, "SO-HIDDEN"));
        var controller = NewController(db, user.Id, reads);

        var page = Envelope(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));
        Assert.Equal(ErrorCodes.Success, page.Code);

        var payload = Serialize(page.Data);
        Assert.Contains("SO-VISIBLE", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("SO-HIDDEN", payload, StringComparison.Ordinal);
        Assert.Equal(1, PageOf(page).Total);

        // 专用列表投影字段集保持不变（缺失的授权列按 null 保留键，绝不臆造值）。
        Assert.Contains("\"DepositAmount\":null", payload, StringComparison.Ordinal);
        Assert.Contains("\"TotalAmount\":null", payload, StringComparison.Ordinal);
        Assert.Contains("\"OrderDate\":null", payload, StringComparison.Ordinal);

        Assert.NotNull(reads.LastScope);
        Assert.False(reads.LastScope!.IsPrivileged);
        Assert.Equal(new[] { visible.Id }, reads.LastScope.AllowedCustomerIds!.ToArray());
    }

    // ==================== 2. 翻页导航与锚点 ====================

    [Fact]
    public async Task 控制器_翻页导航各方向与越权锚点不泄露()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, SalesOrderProcController.LegacyFamilyKey);
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new RecordingReadService(
            (VisibleOid1, visible.Id, "SO-1"),
            (VisibleOid2, visible.Id, "SO-2"),
            (VisibleOid3, visible.Id, "SO-3"),
            (HiddenOid, hidden.Id, "SO-HIDDEN"));
        var controller = NewController(db, user.Id, reads);

        Assert.Equal(VisibleOid1, Payload(Envelope(await controller.Navigate(VisibleOid2, "first"))).GetProperty("Oid").GetInt64());
        Assert.Equal(VisibleOid3, Payload(Envelope(await controller.Navigate(VisibleOid2, "last"))).GetProperty("Oid").GetInt64());
        Assert.Equal(VisibleOid1, Payload(Envelope(await controller.Navigate(VisibleOid2, "prev"))).GetProperty("Oid").GetInt64());
        Assert.Equal(VisibleOid3, Payload(Envelope(await controller.Navigate(VisibleOid2, "next"))).GetProperty("Oid").GetInt64());

        // 范围内没有更多（含首 / 末边界）。
        Assert.Equal(ErrorCodes.NotFound, Envelope(await controller.Navigate(VisibleOid1, "prev")).Code);
        Assert.Equal(ErrorCodes.NotFound, Envelope(await controller.Navigate(VisibleOid3, "next")).Code);

        // 越权锚点与不存在锚点返回同一结果（不泄露锚点存在性）。
        var foreign = Envelope(await controller.Navigate(HiddenOid, "next"));
        Assert.Equal(ErrorCodes.NotFound, foreign.Code);
        Assert.Null(foreign.Data);

        var absent = Envelope(await controller.Navigate(999999, "next"));
        Assert.Equal(ErrorCodes.NotFound, absent.Code);
        Assert.Null(absent.Data);

        // 导航投影不含定金（与列表投影的既有差异保持一致）。
        var navigated = Serialize(Payload(Envelope(await controller.Navigate(VisibleOid2, "next"))));
        Assert.DoesNotContain("DepositAmount", navigated, StringComparison.Ordinal);
    }

    // ==================== 3. 拒绝矩阵（实时身份 + 既有功能菜单） ====================

    [Fact]
    public async Task 控制器_已删除已禁用无菜单仅导出菜单与撤销菜单均被拒绝()
    {
        await using var db = TestDbFactory.Create();
        var reads = new RecordingReadService((VisibleOid1, 1, "SO-1"));

        var deleted = SeedUser(db, UserStatus.Enabled);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, deleted.Id, reads).GetPaged(new PageQuery(), null)).Code);

        var disabled = SeedUser(db, UserStatus.Disabled);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, disabled.Id, reads).GetPaged(new PageQuery(), null)).Code);

        var (noMenu, _) = SeedRestrictedOperator(db);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, noMenu.Id, reads).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, noMenu.Id, reads).Navigate(VisibleOid1, "first")).Code);

        // 仅导出菜单绝不顶替既有功能菜单。
        var (exportOnly, _) = SeedRestrictedOperator(db, "sales-order-export");
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, exportOnly.Id, reads).GetPaged(new PageQuery(), null)).Code);

        // 撤销功能菜单后下一次请求立即收敛。
        var (revoked, revokedRole) = SeedRestrictedOperator(db, SalesOrderProcController.LegacyFamilyKey);
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == revokedRole.Id));
        await db.SaveChangesAsync();
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, revoked.Id, reads).GetPaged(new PageQuery(), null)).Code);

        Assert.Equal(0, reads.PageCalls);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 4. 特权账号沿用既有全量口径 ====================

    [Fact]
    public async Task 控制器_特权账号沿用既有全量口径()
    {
        await using var db = TestDbFactory.Create();
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        var reads = new RecordingReadService(
            (VisibleOid1, 1, "SO-VISIBLE"),
            (HiddenOid, 2, "SO-HIDDEN"));

        var page = Envelope(await NewController(db, privilegedId, reads)
            .GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));

        Assert.Equal(ErrorCodes.Success, page.Code);
        var payload = Serialize(page.Data);
        Assert.Contains("SO-VISIBLE", payload, StringComparison.Ordinal);
        Assert.Contains("SO-HIDDEN", payload, StringComparison.Ordinal);
        Assert.True(reads.LastScope!.IsPrivileged);
        Assert.Null(reads.LastScope.AllowedCustomerIds);
    }

    // ==================== 5. 专用 vs 通用旧读侧一致（同一授权记录） ====================

    [Fact]
    public async Task 控制器_专用与通用旧读侧对同一授权记录一致()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, SalesOrderProcController.LegacyFamilyKey);
        var (visible, hidden) = SeedScopedSalesman(db, user.Id);
        var reads = new RecordingReadService(
            (VisibleOid1, visible.Id, "SO-1"),
            (VisibleOid2, visible.Id, "SO-2"),
            (VisibleOid3, visible.Id, "SO-3"),
            (HiddenOid, hidden.Id, "SO-HIDDEN"));

        var dedicatedPage = Envelope(await NewController(db, user.Id, reads)
            .GetPaged(new PageQuery { PageSize = 50 }, null));
        var genericPage = Envelope(await NewGenericController(db, user.Id, reads)
            .GetPaged(SalesOrderProcController.LegacyFamilyKey, new PageQuery { PageSize = 50 }, null));

        Assert.Equal(ErrorCodes.Success, dedicatedPage.Code);
        Assert.Equal(ErrorCodes.Success, genericPage.Code);

        var dedicated = PageOf(dedicatedPage);
        var generic = PageOf(genericPage);
        Assert.Equal(generic.Total, dedicated.Total);
        Assert.Equal(generic.Oids.OrderBy(o => o).ToArray(), dedicated.Oids.OrderBy(o => o).ToArray());
        Assert.Equal(
            generic.BillNos.Where(b => b is not null).OrderBy(b => b).ToArray(),
            dedicated.BillNos.Where(b => b is not null).OrderBy(b => b).ToArray());
    }

    // ==================== 6. 畸形 / 溢出输入（真实受控服务，先于连接拒绝） ====================

    [Fact]
    public async Task 控制器_非法分页与非法方向在打开查询之前被拒绝且零写入()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, SalesOrderProcController.LegacyFamilyKey);
        var controller = NewController(db, user.Id, RealReadService());

        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 100000 }, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetPaged(new PageQuery { Page = int.MaxValue, PageSize = 200 }, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Navigate(VisibleOid2, "first;drop table db_owner.SalesOrder")).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Navigate(0, "next")).Code);

        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 控制器_缺表缺列映射为environmentBlocked且绝不回退规范表()
    {
        await using var db = TestDbFactory.Create();
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        var controller = NewController(db, privilegedId, RealReadService());

        var page = Envelope(await controller.GetPaged(new PageQuery(), null));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, page.Code);
        Assert.Contains("environment-blocked", page.Message, StringComparison.Ordinal);
        Assert.Null(page.Data);
    }
}
