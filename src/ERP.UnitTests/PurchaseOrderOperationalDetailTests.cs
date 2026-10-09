using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-430 采购订单规范运营读取（详情 / 打印 / JSON 运营导出）只返回**未删除**明细行单元测试（内存库 + 真实 HTTP 身份）。
/// <para>覆盖：详情 / 打印 / JSON 导出三者对有效明细行一致；被软删除的明细行绝不进入运营快照；
/// 表头历史存储金额与来源 / 审计快照原样保留、读取侧不重算也不改写被软删除的行；
/// 已删除父单在详情 / 打印被非披露拒绝且不出现在导出；受限账号越界客户 fail closed 且导出只含范围内单据；
/// 缺失 / 畸形 / 禁用 / 撤销菜单身份在运营读取上 fail closed 且零副作用。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收；真实身份通过既有角色 / 菜单 /
/// 员工 / 客户种子构造，不新增任何用户授权。</para>
/// </summary>
public class PurchaseOrderOperationalDetailTests
{
    private const long ProductA = 956201L;
    private const long ProductDeleted = 956801L;
    private const long CustomerA = 956301L;
    private const long CustomerB = 956302L;

    // ==================== 1. 详情 / 打印 / 导出有效明细一致 ====================

    [Fact]
    public async Task Detail_excludes_deleted_lines_and_preserves_historical_header_amount()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var order = SeedOrder(db, "PO-OPD-1", DocumentStatus.Pending, CustomerA, deleted: false,
            headerTotal: 3000m, liveLines: 2, deletedLines: 2);
        var ctl = ForUser(db, userId);

        var detail = DataOf<PurchaseOrder>(await ctl.GetById(order.Id));

        Assert.Equal(2, detail.Details.Count);
        Assert.All(detail.Details, d => Assert.False(d.IsDeleted));
        Assert.Equal(new[] { ProductA, ProductA + 1 },
            detail.Details.Select(d => d.ProductId).OrderBy(id => id).ToArray());
        // 历史存储表头金额原样保留，读取侧绝不重算（有效明细合计仅 200 也不回落）。
        Assert.Equal(3000m, detail.TotalAmount);

        // 被软删除的明细行仍完整留在库中（读取绝不物理删除 / 绝不改写）。
        var stored = db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == order.Id).ToList();
        Assert.Equal(4, stored.Count);
        Assert.Equal(2, stored.Count(d => d.IsDeleted));
        Assert.All(stored.Where(d => d.IsDeleted), d => Assert.Equal(81m, d.Amount));
    }

    [Fact]
    public async Task Detail_print_and_json_export_agree_on_live_lines()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var order = SeedOrder(db, "PO-OPD-2", DocumentStatus.Approved, CustomerA, deleted: false,
            headerTotal: 5000m, liveLines: 3, deletedLines: 2);
        var ctl = ForUser(db, userId);

        var detail = DataOf<PurchaseOrder>(await ctl.GetById(order.Id));
        var print = DataOf<PurchaseOrder>(await ctl.GetPrint(order.Id));
        var exported = DataOf<List<PurchaseOrder>>(await ctl.Export(null, null)).Single(o => o.Id == order.Id);

        var expected = detail.Details.Select(d => d.Id).OrderBy(id => id).ToArray();
        Assert.Equal(3, expected.Length);
        Assert.Equal(expected, print.Details.Select(d => d.Id).OrderBy(id => id).ToArray());
        Assert.Equal(expected, exported.Details.Select(d => d.Id).OrderBy(id => id).ToArray());
        Assert.All(print.Details, d => Assert.False(d.IsDeleted));
        Assert.All(exported.Details, d => Assert.False(d.IsDeleted));
        Assert.Equal(5000m, print.TotalAmount);
        Assert.Equal(5000m, exported.TotalAmount);
    }

    // ==================== 2. 已删除父单非披露拒绝 / 导出排除 ====================

    [Fact]
    public async Task Deleted_parent_is_denied_on_detail_and_print_and_excluded_from_export()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var live = SeedOrder(db, "PO-OPD-LIVE", DocumentStatus.Pending, CustomerA, deleted: false,
            headerTotal: 1000m, liveLines: 1, deletedLines: 1);
        var deleted = SeedOrder(db, "PO-OPD-DEL", DocumentStatus.Pending, CustomerA, deleted: true,
            headerTotal: 1000m, liveLines: 1, deletedLines: 1);
        var ctl = ForUser(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.GetById(live.Id));
        await AssertDenied(ErrorCodes.NotFound, () => ctl.GetById(deleted.Id));
        await AssertDenied(ErrorCodes.NotFound, () => ctl.GetPrint(deleted.Id));

        var exported = DataOf<List<PurchaseOrder>>(await ctl.Export(null, null));
        Assert.Equal(live.Id, Assert.Single(exported).Id);
        Assert.DoesNotContain(exported, o => o.Id == deleted.Id);
    }

    // ==================== 3. 读取零副作用（软删除行 / 表头快照不被改写） ====================

    [Fact]
    public async Task Operational_reads_do_not_mutate_tombstoned_rows_or_header_snapshot()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var order = SeedOrder(db, "PO-OPD-SNAP", DocumentStatus.Submitted, CustomerA, deleted: false,
            headerTotal: 4242m, liveLines: 1, deletedLines: 2);
        var ctl = ForUser(db, userId);

        var headerBefore = SnapshotHeader(db, order.Id);
        var detailsBefore = SnapshotDetails(db, order.Id);

        Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Export(null, null));

        Assert.Equal(headerBefore, SnapshotHeader(db, order.Id));
        Assert.Equal(detailsBefore, SnapshotDetails(db, order.Id));
        Assert.Equal(3, SnapshotDetails(db, order.Id).Count);
        Assert.Equal(2, db.PurchaseOrderDetails.AsNoTracking()
            .Count(d => d.PurchaseOrderId == order.Id && d.IsDeleted));
    }

    // ==================== 4. 受限账号范围与越界拒绝 ====================

    [Fact]
    public async Task Restricted_account_is_scoped_and_foreign_order_is_refused_without_leak()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var (userId, _) = SeedSalesman(db, role, CustomerA);
        var own = SeedOrder(db, "PO-OPD-OWN", DocumentStatus.Pending, CustomerA, deleted: false,
            headerTotal: 1000m, liveLines: 2, deletedLines: 1);
        var foreign = SeedOrder(db, "PO-OPD-FOREIGN", DocumentStatus.Pending, CustomerB, deleted: false,
            headerTotal: 1000m, liveLines: 1, deletedLines: 1);
        var ctl = ForUser(db, userId);

        var ownDetail = DataOf<PurchaseOrder>(await ctl.GetById(own.Id));
        Assert.Equal(2, ownDetail.Details.Count);
        Assert.All(ownDetail.Details, d => Assert.False(d.IsDeleted));

        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetPrint(foreign.Id));

        var exported = DataOf<List<PurchaseOrder>>(await ctl.Export(null, null));
        Assert.Equal(own.Id, Assert.Single(exported).Id);
        Assert.DoesNotContain(exported, o => o.Id == foreign.Id);
        Assert.Equal(2, exported[0].Details.Count);
    }

    // ==================== 5. 身份 fail closed ====================

    [Fact]
    public async Task Missing_and_malformed_identity_are_refused_on_operational_reads()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-OPD-AUTH", DocumentStatus.Pending, CustomerA, deleted: false,
            headerTotal: 1000m, liveLines: 1, deletedLines: 1);

        var anonymous = ForUser(db, null);
        await AssertDenied(ErrorCodes.Unauthorized, () => anonymous.GetById(order.Id));
        await AssertDenied(ErrorCodes.Unauthorized, () => anonymous.GetPrint(order.Id));
        await AssertDenied(ErrorCodes.Unauthorized, () => anonymous.Export(null, null));

        var malformed = ForClaim(db, "not-a-numeric-id");
        await AssertDenied(ErrorCodes.Unauthorized, () => malformed.GetById(order.Id));
        await AssertDenied(ErrorCodes.Unauthorized, () => malformed.GetPrint(order.Id));
        await AssertDenied(ErrorCodes.Unauthorized, () => malformed.Export(null, null));

        // 被拒绝的调用零副作用：软删除行与表头快照原样。
        Assert.Equal(2, db.PurchaseOrderDetails.AsNoTracking()
            .Count(d => d.PurchaseOrderId == order.Id));
    }

    [Fact]
    public async Task Disabled_account_and_revoked_menu_are_refused_on_operational_reads()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var disabledUserId = SeedUser(db, role, UserStatus.Disabled);
        var order = SeedOrder(db, "PO-OPD-DIS", DocumentStatus.Pending, CustomerA, deleted: false,
            headerTotal: 1000m, liveLines: 1, deletedLines: 1);

        var disabled = ForUser(db, disabledUserId);
        await AssertDenied(ErrorCodes.Forbidden, () => disabled.GetById(order.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => disabled.GetPrint(order.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => disabled.Export(null, null));

        // 撤销既有菜单授权后，运营读取在下一请求立即收敛（绝不缓存）。
        var (salesmanId, _) = SeedSalesman(db, role, CustomerA);
        var salesman = ForUser(db, salesmanId);
        Assert.IsType<OkObjectResult>(await salesman.GetById(order.Id));

        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == role.Id && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        await AssertDenied(ErrorCodes.Forbidden, () => salesman.GetById(order.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => salesman.GetPrint(order.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => salesman.Export(null, null));
    }

    // ==================== 6. 源码契约（单一 filtered include，无内存明细过滤） ====================

    [Fact]
    public void Operational_reads_share_one_sql_side_filtered_include_and_never_filter_in_memory()
    {
        var source = ReadSource("src/ERP.Api/Controllers/PurchaseOrderController.cs");

        // 明细未删除过滤在查询（SQL 生成）阶段，先于物化。
        Assert.Contains("Include(o => o.Details.Where(d => !d.IsDeleted))", source);
        // 详情 / 打印 / 导出共用同一运营读取口径，保证三者一致。
        Assert.Contains("OperationalReadQuery()", source);
        // 打印不再依赖「物化后再内存过滤」。
        Assert.DoesNotContain("entity.Details = entity.Details.Where(d => !d.IsDeleted)", source);
    }

    // ==================== 脚手架 ====================

    private static T DataOf<T>(IActionResult result)
        => Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static async Task AssertDenied(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
    }

    /// <summary>构造绑定到真实 HTTP 请求管线的控制器（<c>Request.Path</c> 已赋值），使实时授权按真实请求口径生效。</summary>
    private static PurchaseOrderController ForUser(ErpDbContext db, long? userId)
        => Build(db, httpBound: true, userId.HasValue ? userId.Value.ToString() : null);

    /// <summary>构造带畸形身份声明（非数字）的真实请求控制器，模拟无法解析的登录身份。</summary>
    private static PurchaseOrderController ForClaim(ErpDbContext db, string claimValue, bool httpBound = true)
        => Build(db, httpBound, claimValue);

    private static PurchaseOrderController Build(ErpDbContext db, bool httpBound, string? claimValue)
    {
        var claims = claimValue is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, claimValue) };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (httpBound) http.Request.Path = "/api/purchase-orders";
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static (decimal TotalAmount, DocumentStatus Status, string Remark, DateTime? UpdatedAt) SnapshotHeader(
        ErpDbContext db, long id)
    {
        var order = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == id);
        return (order.TotalAmount, order.Status, order.Remark, order.UpdatedAt);
    }

    private static List<(long Id, bool IsDeleted, decimal Quantity, decimal UnitPrice, decimal Amount, DateTime? UpdatedAt)>
        SnapshotDetails(ErpDbContext db, long id)
        => db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == id)
            .OrderBy(d => d.Id)
            .Select(d => new ValueTuple<long, bool, decimal, decimal, decimal, DateTime?>(
                d.Id, d.IsDeleted, d.Quantity, d.UnitPrice, d.Amount, d.UpdatedAt))
            .ToList();

    /// <summary>播种一张含「有效 + 已删除」明细的采购订单，表头金额为历史存储值（与有效明细合计无关）。</summary>
    private static PurchaseOrder SeedOrder(ErpDbContext db, string no, DocumentStatus status,
        long? owningCustomerId, bool deleted, decimal headerTotal, int liveLines, int deletedLines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = new DateTime(2026, 3, 1),
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            OwningCustomerName = owningCustomerId is null ? string.Empty : $"客户{owningCustomerId}",
            Status = status,
            TotalAmount = headerTotal,
            Remark = "ERP430 运营快照",
            IsDeleted = deleted,
            CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
            UpdatedAt = new DateTime(2026, 3, 2, 9, 30, 0)
        };
        for (var i = 0; i < liveLines; i++)
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = ProductA + i,
                ProductName = $"有效商品{i}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = 1m,
                UnitPrice = 100m,
                Amount = 100m,
                CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
                UpdatedAt = new DateTime(2026, 3, 2, 9, 30, 0)
            });
        for (var i = 0; i < deletedLines; i++)
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = ProductDeleted - i,
                ProductName = $"已删除商品{i}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = 9m,
                UnitPrice = 9m,
                Amount = 81m,
                IsDeleted = true,
                CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
                UpdatedAt = new DateTime(2026, 3, 2, 9, 30, 0)
            });
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code, string name)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static SysRole SeedRole(ErpDbContext db, params SysMenu[] menus)
    {
        var role = new SysRole { RoleCode = $"PoOpd-{Guid.NewGuid():N}", RoleName = "采购运营明细测试角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        foreach (var menu in menus)
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return role;
    }

    private static long SeedUser(ErpDbContext db, SysRole role, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"po-opd-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购运营明细测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有采购订单菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var menu = SeedMenu(db, PurchaseOrderAuthorizationRules.RequiredMenuCode, PurchaseOrderAuthorizationRules.RequiredMenuText);
        var role = new SysRole { RoleCode = $"PoOpdPriv-{Guid.NewGuid():N}", RoleName = "采购运营明细特权角色", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种受限业务员（员工编码映射 + 指定角色 / 菜单 + 本人客户），返回（用户 Id，员工 Id）。</summary>
    private static (long UserId, long EmployeeId) SeedSalesman(ErpDbContext db, SysRole? role, params long[] ownedCustomerIds)
    {
        var code = $"po-opd-sales-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购运营明细业务员",
            Status = UserStatus.Enabled
        };
        db.BaseEmployees.Add(employee);
        db.SysUsers.Add(user);
        db.SaveChanges();

        if (role is not null)
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            db.SaveChanges();
        }

        foreach (var customerId in ownedCustomerIds)
        {
            var existing = db.BaseCustomers.FirstOrDefault(c => c.Id == customerId);
            if (existing is null)
                db.BaseCustomers.Add(new BaseCustomer
                {
                    Id = customerId,
                    CustomerCode = $"C-{customerId}",
                    CustomerName = $"客户{customerId}",
                    EmpId = employee.Id,
                    Status = 1
                });
            else
                existing.EmpId = employee.Id;
            db.SaveChanges();
        }

        return (user.Id, employee.Id);
    }
}





