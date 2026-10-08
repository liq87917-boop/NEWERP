using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// 采购入库测试身份 / 主数据脚手架（ERP-352）：为直接实例化 <see cref="StockInController"/> 的单元测试
/// 注入「特权 / 受限制入库操作员」的**真实 HTTP 身份**，并播种 stock-in 菜单与真实可用供应商 / 仓库。
/// </summary>
public static class StockInTestAuthorization
{
    /// <summary>播种 stock-in 菜单（幂等），供角色 → 菜单授权复用。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == "stock-in" && !m.IsDeleted);
        if (existing is not null)
            return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = "stock-in",
            MenuName = "采购入库",
            Path = "/logistics/stock-in",
            Icon = "PackagePlus",
            SortOrder = 0,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种真实可用供应商（存在、未删除、已启用）。</summary>
    public static BaseSupplier SeedSupplier(ErpDbContext db, long id, string name = "测试供应商")
    {
        var supplier = new BaseSupplier
        {
            Id = id,
            SupplierCode = $"SUP-{id}",
            SupplierName = name,
            Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    /// <summary>播种真实可用仓库（存在、未删除、已启用）。</summary>
    public static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name = "测试仓库")
    {
        var warehouse = new BaseWarehouse
        {
            Id = id,
            WarehouseCode = $"WH-{id}",
            WarehouseName = name,
            Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        return warehouse;
    }

    /// <summary>播种一个特权入库操作员（SuperAdmin 角色 + stock-in 菜单）并返回其用户 Id。</summary>
    public static long SeedPrivilegedInboundOperator(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = "SuperAdmin", RoleName = "测试超级管理员", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"in-auth-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "入库测试管理员",
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

    /// <summary>创建已注入特权身份的 StockInController。</summary>
    public static StockInController Create(ErpDbContext db)
        => ForUser(db, SeedPrivilegedInboundOperator(db));

    /// <summary>把指定登录用户 Id（可空 = 无身份）写入控制器 HttpContext。</summary>
    public static StockInController ForUser(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new StockInController(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    /// <summary>
    /// 播种一个受限制的入库操作员（业务员映射 + stock-in 菜单，无特权角色），
    /// 并分配一个归属客户；返回（用户 Id, 归属客户 Id）。
    /// </summary>
    public static (long UserId, long CustomerId) SeedRestrictedInboundOperator(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var employee = new BaseEmployee
        {
            EmployeeCode = $"op-{Guid.NewGuid():N}",
            EmployeeName = "入库操作员",
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
            DisplayName = "入库操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"StockInOp-{Guid.NewGuid():N}", RoleName = "入库操作员角色" };
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
}
