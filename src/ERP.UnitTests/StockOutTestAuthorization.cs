using ERP.Api.Controllers;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.UnitTests;

internal static class StockOutTestAuthorization
{
    /// <summary>
    /// ERP-502：既有装箱换算回归夹具（<c>StockUnitConversionTests</c>）直接复用本工厂并以 <c>WarehouseId = 3</c> 建单；
    /// 草稿护栏要求仓库真实存在，因此本工厂播种一个与 <c>StockInLegacyTestFixture</c> 同口径的在用仓库（仅供测试脚手架，
    /// 绝不新增任何用户授权）。
    /// </summary>
    public const long LegacyWarehouseId = 3L;

    public static StockOutController Create(ErpDbContext db)
    {
        var user = new SysUser { UserName = "stockout-test-admin", DisplayName = "Test", PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled };
        var role = new SysRole { RoleCode = "SuperAdmin", RoleName = "Test Admin" };
        db.SysUsers.Add(user); db.SysRoles.Add(role); db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id }); db.SaveChanges();
        EnsureLegacyWarehouse(db);
        return ForUser(db, user.Id);
    }

    /// <summary>ERP-502：播种既有装箱换算测试使用的默认在用仓库（幂等）。</summary>
    private static void EnsureLegacyWarehouse(ErpDbContext db)
        => SeedWarehouse(db, LegacyWarehouseId, "ERP-052测试仓");

    /// <summary>播种真实在用的仓库主数据（存在、未删除、启用；幂等）。</summary>
    public static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name = "测试仓A")
    {
        var existing = db.BaseWarehouses.FirstOrDefault(w => w.Id == id);
        if (existing is not null)
        {
            // 播种后即脱离跟踪：允许调用方随后以同一主键自行播种 / 改写（避免同键重复跟踪）。
            db.Entry(existing).State = EntityState.Detached;
            return existing;
        }

        var warehouse = new BaseWarehouse
        {
            Id = id, WarehouseCode = $"W-{id}", WarehouseName = name, Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        db.Entry(warehouse).State = EntityState.Detached;
        return warehouse;
    }

    /// <summary>播种真实在用的商品主数据（存在、未删除、启用；幂等）。</summary>
    public static BaseProduct SeedProduct(ErpDbContext db, long id, string unit = "PCS",
        string packageUnit = "", int unitsPerPackage = 0, string name = "测试商品")
    {
        var existing = db.BaseProducts.FirstOrDefault(p => p.Id == id);
        if (existing is not null)
        {
            db.Entry(existing).State = EntityState.Detached;
            return existing;
        }

        var product = new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = name, Spec = "规格A",
            Unit = unit, PackageUnit = packageUnit, UnitsPerPackage = unitsPerPackage, Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        db.Entry(product).State = EntityState.Detached;
        return product;
    }

    /// <summary>播种销售出库单号规则（幂等；供「被拒绝的请求绝不消耗单据号」断言使用）。</summary>
    public static SysDocumentNumberRule SeedStockOutNumberRule(ErpDbContext db, long currentSequence = 0)
    {
        var existing = db.SysDocumentNumberRules
            .FirstOrDefault(r => r.DocumentType == DocumentType.StockOut);
        if (existing is not null)
            return existing;

        var rule = new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockOut,
            RuleCode = "CK-TEST",
            RuleName = "销售出库单号",
            Prefix = "CK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = currentSequence
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    public static StockOutController ForUser(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };

    // ==================== ERP-478：规范入口测试脚手架（真实既有菜单 / 身份 / 客户范围） ====================

    /// <summary>播种既有「销售出库」菜单（幂等），供角色 → 菜单授权复用（绝不新增权限模型）。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == StockOutAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (existing is not null)
            return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = StockOutAuthorizationRules.RequiredMenuCode,
            MenuName = StockOutAuthorizationRules.RequiredMenuText,
            Path = "/logistics/stock-out",
            Icon = "PackageMinus",
            SortOrder = 41,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种既有「销售订单」菜单（幂等，来源候选所需）。</summary>
    public static SysMenu SeedSalesOrderMenu(ErpDbContext db)
    {
        var code = StockOutAuthorizationRules.SourceRequiredMenuCode;
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
        if (existing is not null)
            return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = StockOutAuthorizationRules.SourceRequiredMenuText,
            Path = "/order/sales",
            Icon = "Clipboard",
            SortOrder = 39,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种真实可用客户（存在、未删除、已启用）。</summary>
    public static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name = "测试客户")
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"CUS-{id}",
            CustomerName = name,
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        db.Entry(customer).State = EntityState.Detached;
        return customer;
    }

    /// <summary>播种一个特权出库操作员（SuperAdmin 角色 + 既有「销售出库」菜单）并返回其用户 Id。</summary>
    public static long SeedPrivilegedOutboundOperator(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = "SuperAdmin", RoleName = "测试超级管理员", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"out-auth-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "出库测试管理员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// 播种一个受限制的出库操作员（业务员映射 + 既有「销售出库」菜单，无特权角色），
    /// 并分配一个归属客户；返回（用户 Id, 归属客户 Id）。用于客户数据范围 fail closed 场景。
    /// </summary>
    public static (long UserId, long CustomerId) SeedRestrictedOutboundOperator(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var employee = new BaseEmployee
        {
            EmployeeCode = $"out-restr-{Guid.NewGuid():N}",
            EmployeeName = "出库业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "出库业务员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"StockOutRe-{Guid.NewGuid():N}", RoleName = "出库业务员角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "归属客户",
            EmpId = employee.Id,
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return (user.Id, customer.Id);
    }

    /// <summary>
    /// 播种一个受限制的出库操作员（业务员映射 + 仅有既有「销售订单」菜单，<b>缺少</b>「销售出库」菜单），
    /// 并分配一个归属客户；返回（用户 Id, 归属客户 Id），用于「缺菜单授权 fail closed 且零写入」场景。
    /// </summary>
    public static (long UserId, long CustomerId) SeedOperatorWithoutStockOutMenu(ErpDbContext db)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"out-op-{Guid.NewGuid():N}",
            EmployeeName = "出库操作员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "出库操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"StockOutOp-{Guid.NewGuid():N}", RoleName = "出库操作员角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var salesOrderMenu = SeedSalesOrderMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = salesOrderMenu.Id });
        db.SaveChanges();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "归属客户",
            EmpId = employee.Id,
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return (user.Id, customer.Id);
    }
}
