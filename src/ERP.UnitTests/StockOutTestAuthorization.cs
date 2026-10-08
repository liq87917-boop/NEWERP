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
}
