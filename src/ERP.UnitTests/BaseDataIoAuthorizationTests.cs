using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-440 基础资料导入导出（<c>api/base/io</c>）实时身份 / 既有功能菜单 / 权威客户范围护栏单元测试。
/// <para>覆盖：export / import-template / import 三个路由在读取任何行之前对缺失 / 已删除 / 禁用身份与
/// 无既有功能菜单身份 fail closed；撤销菜单后立即收敛；特权账号保留既有全量导出；受限制业务员客户导出
/// 只返回本人客户，导入客户越界行逐行失败且不新增 / 不改写任何行。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何权限。</para>
/// </summary>
public class BaseDataIoAuthorizationTests
{
    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 三个路由_无身份_一律未认证拒绝且不读取任何基础资料()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, name: "客户A");
        SeedSupplier(db);
        SeedProduct(db);
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Export("customers"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.DownloadTemplate("customers"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Import("customers", CustomerFile(("C-A", "客户A", null))));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Export("suppliers"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Import("products", null));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, name: "客户A");
        var before = Snapshot(db);
        var disabled = SeedRoleUser(db, Array.Empty<string>(), status: UserStatus.Disabled);
        var deleted = SeedRoleUser(db, Array.Empty<string>(), deleted: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Export("customers"));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).DownloadTemplate("customers"));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Import("customers", null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Export("customers"));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Import("customers", null));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 非特权_无既有功能菜单_拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, name: "客户A");
        var before = Snapshot(db);
        var noMenu = SeedRoleUser(db, Array.Empty<string>());
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export("customers"));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.DownloadTemplate("customers"));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Import("customers", CustomerFile(("C-A", "客户A", null))));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 非特权_撤销既有功能菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, name: "客户A");
        var (userId, _) = SeedRestrictedOperator(db, "customer");
        var ctl = NewController(db, userId);

        Assert.IsType<FileContentResult>(await ctl.Export("customers"));

        RevokeMenus(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Export("customers"));
    }

    // ==================== 2. 权威客户范围 ====================

    [Fact]
    public async Task 特权账号_客户导出保留既有全量()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, name: "客户A");
        SeedCustomer(db, name: "客户B");
        var privileged = SeedPrivilegedUser(db);

        var file = Assert.IsType<FileContentResult>(await NewController(db, privileged).Export("customers"));
        Assert.Equal(2, ReadExportedRows(file).Count);
    }

    [Fact]
    public async Task 受限制业务员_客户导出只返回本人客户_供应商仍按菜单全量()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, "customer", "supplier");
        SeedCustomer(db, empId: employeeId, name: "本人客户");
        SeedCustomer(db, name: "他人客户");
        SeedSupplier(db);
        SeedSupplier(db);
        var ctl = NewController(db, userId);

        var customers = ReadExportedRows(Assert.IsType<FileContentResult>(await ctl.Export("customers")));
        Assert.Single(customers);
        Assert.Equal("本人客户", customers[0]["客户名称"]);

        var suppliers = ReadExportedRows(Assert.IsType<FileContentResult>(await ctl.Export("suppliers")));
        Assert.Equal(2, suppliers.Count);
    }

    [Fact]
    public async Task 受限制业务员_导入客户越界行逐行失败_仅本人行成功()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, "customer");
        var foreignEmployee = SeedEmployee(db);
        var before = db.BaseCustomers.Count();
        var ctl = NewController(db, userId);

        var file = CustomerFile(
            ("C-OWN", "本人新客户", employeeId.ToString()),
            ("C-OTHER", "他人客户行", foreignEmployee.ToString()),
            ("C-NONE", "无业务员行", null));

        var resp = AssertOkObject(await ctl.Import("customers", file));
        Assert.NotNull(resp.Data);
        Assert.Contains("成功 1 条", resp.Message);
        Assert.Contains("失败 2 条", resp.Message);

        var stored = db.BaseCustomers.ToList();
        Assert.Equal(before + 1, stored.Count);
        var created = Assert.Single(stored, c => c.CustomerCode == "C-OWN");
        Assert.Equal(employeeId, created.EmpId);
        Assert.DoesNotContain(stored, c => c.CustomerCode is "C-OTHER" or "C-NONE");
    }

    [Fact]
    public async Task 受限制业务员_已登记菜单_导入模板与导出均放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedRestrictedOperator(db, "product");
        SeedProduct(db);
        var ctl = NewController(db, userId);

        Assert.IsType<FileContentResult>(await ctl.DownloadTemplate("products"));
        Assert.Single(ReadExportedRows(Assert.IsType<FileContentResult>(await ctl.Export("products"))));
    }

    [Fact]
    public async Task 不支持的资源_保持既有不支持契约且不触发授权()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, name: "客户A");
        var ctl = NewController(db, userId: null);   // 身份缺失也不影响「不支持资源」既有契约

        var export = AssertOkResponse<object>(await ctl.Export("unknown-resource"));
        Assert.Equal(ErrorCodes.InvalidParameter, export.Code);
        var template = AssertOkResponse<object>(await ctl.DownloadTemplate("unknown-resource"));
        Assert.Equal(ErrorCodes.InvalidParameter, template.Code);
    }

    // ==================== 3. 脚手架 ====================

    private static BaseDataIoController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new BaseDataIoController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return ctl;
    }

    private static Dictionary<string, int> Snapshot(ErpDbContext db)
        => new(StringComparer.Ordinal)
        {
            ["customers"] = db.BaseCustomers.Count(),
            ["suppliers"] = db.BaseSuppliers.Count(),
            ["employees"] = db.BaseEmployees.Count(),
            ["products"] = db.BaseProducts.Count(),
        };

    private static void AssertUnchanged(ErpDbContext db, Dictionary<string, int> before)
        => Assert.Equal(before, Snapshot(db));

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<T> AssertOkResponse<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        return resp;
    }

    private static ApiResponse<object> AssertOkObject(IActionResult result)
        => AssertOkResponse<object>(result);

    private static List<Dictionary<string, string>> ReadExportedRows(FileContentResult file)
        => ExcelImporter.ReadRows(new MemoryStream(file.FileContents));

    /// <summary>用导出器构造一份可被导入器解析的客户 xlsx（表头与控制器客户列定义同源）。</summary>
    private static IFormFile CustomerFile(params (string Code, string Name, string? EmpId)[] rows)
    {
        var columns = new List<(string Key, string Title)>
        {
            ("CustomerCode", "客户编码"), ("CustomerName", "客户名称"), ("EmpId", "业务员Id"),
        };
        var data = rows.Select(r => new Dictionary<string, object?>
        {
            ["CustomerCode"] = r.Code,
            ["CustomerName"] = r.Name,
            ["EmpId"] = r.EmpId,
        }).ToList();
        var bytes = ExcelExporter.ExportRows("客户资料", data, columns);
        return new FormFile(new MemoryStream(bytes), 0, bytes.LongLength, "file", "customers.xlsx")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static long SeedCustomer(ErpDbContext db, long? empId = null, string? name = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"BIO-C-{Guid.NewGuid():N}"[..30],
            CustomerName = name ?? "客户",
            EmpId = empId,
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static long SeedSupplier(ErpDbContext db)
    {
        var supplier = new BaseSupplier { SupplierCode = $"BIO-S-{Guid.NewGuid():N}"[..30], SupplierName = "供应商", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier.Id;
    }

    private static long SeedProduct(ErpDbContext db)
    {
        var product = new BaseProduct { ProductCode = $"BIO-P-{Guid.NewGuid():N}"[..30], ProductName = "商品", Spec = "规格", Unit = "PCS" };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    private static long SeedEmployee(ErpDbContext db)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"bio-emp-{Guid.NewGuid():N}",
            EmployeeName = "业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee.Id;
    }

    /// <summary>特权账号（系统内置角色，继承既有全部访问，与 ERP-097 同源）。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var user = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "超级管理员", RoleCode = "SuperAdmin", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>普通账号（非特权）：可选授予既有菜单编码，默认无任何菜单授权。</summary>
    private static long SeedRoleUser(ErpDbContext db, string[] menuCodes, bool isSystem = false,
        UserStatus status = UserStatus.Enabled, bool deleted = false)
    {
        var user = NewUser(status);
        user.IsDeleted = deleted;
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "基础资料操作员", RoleCode = $"BioRole-{Guid.NewGuid():N}", IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        GrantMenus(db, role.Id, menuCodes);
        return user.Id;
    }

    /// <summary>受限制业务员：登录名 == 员工编码且 IsSalesman，仅授予传入的既有菜单。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"bio-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = NewUser(UserStatus.Enabled);
        user.UserName = code;
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "基础资料业务员", RoleCode = $"BioOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        GrantMenus(db, role.Id, menuCodes);
        return (user.Id, employee.Id);
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"bio-user-{Guid.NewGuid():N}",
        DisplayName = "基础资料账号",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        Status = status
    };

    /// <summary>复用 / 新建既有菜单并授予角色（不新增任何权限模型，菜单编码与 SeedData 同源）。</summary>
    private static void GrantMenus(ErpDbContext db, long roleId, IEnumerable<string> menuCodes)
    {
        foreach (var code in menuCodes)
        {
            var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuCode = code, MenuName = code, MenuType = MenuType.Menu, Path = $"/base/{code}" };
                db.SysMenus.Add(menu);
                db.SaveChanges();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }
}
