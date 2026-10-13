using ERP.Api.Controllers;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.UnitTests;

internal static class StockOutTestAuthorization
{
    public static StockOutController Create(ErpDbContext db)
    {
        var user = new SysUser { UserName = "stockout-test-admin", DisplayName = "Test", PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled };
        var role = new SysRole { RoleCode = "SuperAdmin", RoleName = "Test Admin" };
        db.SysUsers.Add(user); db.SysRoles.Add(role); db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id }); db.SaveChanges();
        return ForUser(db, user.Id);
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
