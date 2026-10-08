using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-410 旧销售订单专用路由（<c>api/v2/sales-orders</c>）有限变更策略单元测试（内存库 + 真实既有身份 / 菜单授权）。
/// <list type="bullet">
/// <item><b>复用 ERP-404 唯一策略</b>：五个写动作（save / delete / audit / void / restore）在任何 <c>sp_Biz_SalesOrder</c>
/// 调用之前复用 <see cref="LegacyBillMutationRules"/> 族 <c>sales-order</c>，不新增第二套策略或业务实现；</item>
/// <item><b>实时身份 / 既有菜单 / fail closed</b>：缺失 / 已删除 → <c>2000</c>，已禁用 / 无销售订单菜单 / 仅导出菜单 / 外来模块 → <c>2002</c>，
/// 撤销菜单后下一次请求立即收敛；具备功能菜单与特权账号仍因无已验证适配器 fail closed（<c>1004</c> + 规范路由 + <c>sp_Biz_</c>）；</item>
/// <item><b>零副作用</b>：被拒后业务订单 / 明细 / 库存 / 库存流水 / 单号流水 / 操作日志 / 钉钉通知全部零变更；</item>
/// <item><b>规范工作流不受影响</b>：规范销售订单（<c>api/sales-orders</c>）创建 / 审核 / 取消流转回归保持可用。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与真实既有身份；真实 SQL 与两个独立连接竞态由
/// <c>SalesOrderProcMutationSqlServerTests</c> 覆盖。</para>
/// </summary>
public class SalesOrderProcMutationTests
{
    /// <summary>绑定不出现在任何真实目标上的连接串；任何 <c>sp_Biz_*</c> 调用都会立刻失败（用于证明门禁先于存储过程）。</summary>
    private const string UnusedConnectionString =
        "Server=(localdb)\\NEWERP_UnitTests_Gate;Database=UNUSED;Integrated Security=true;Connect Timeout=1";

    /// <summary>伪造的旧库主键（绝不允许被推断为规范 Id）。</summary>
    private const long ForgedOid = 987654321L;

    // ==================== 脚手架 ====================

    /// <summary>绑定真实 HttpContext 身份的旧销售订单控制器；连接串不可用，任何 sp_Biz_SalesOrder 调用都会立刻失败。</summary>
    private static SalesOrderProcController NewController(ErpDbContext db, long? userId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = UnusedConnectionString
            })
            .Build();

        var controller = new SalesOrderProcController(new StoredProcedureService(configuration), db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    /// <summary>播种一位**受限制**操作员（非系统内置角色 → 非特权）并按需授予既有功能菜单。</summary>
    private static (SysUser User, SysRole Role) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"sopm-op-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "旧销售订单受限操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "受限角色", RoleCode = $"R-{Guid.NewGuid():N}", IsSystem = false };
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

    private static SysUser SeedUser(ErpDbContext db, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"sopm-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    /// <summary>伪造的旧标识 + 请求头金额载荷（绝不允许被信任 / 转换）。</summary>
    private static SalesOrderSaveRequest ForgedSaveRequest() => new()
    {
        Oid = ForgedOid,
        OrderDate = DateTime.Today,
        CustId = 4321,
        SalesmanId = 8765,
        Currency = 2,
        ExchangeRate = 7.1234m,
        TotalAmount = 1234567.89m,
        DepositRatio = 99m,
        DepositAmount = 1222222.22m,
        PaymentTerms = "FORGED",
        DeliveryDate = DateTime.Today.AddDays(3),
        ShippingMethod = "FORGED",
        PortId = 777,
        Remark = "forged legacy oid + header totals"
    };

    /// <summary>执行一个写动作（动作与 Oid 身份由服务端 / 路由确定）；捕获异常以便给出可读断言。</summary>
    private static async Task<(bool GotEnvelope, int Code, string Message)> AttemptAsync(
        ErpDbContext db, long? userId, string action)
    {
        try
        {
            var controller = NewController(db, userId);
            IActionResult result = action switch
            {
                "save" => await controller.Save(ForgedSaveRequest()),
                "delete" => await controller.Delete(ForgedOid),
                "audit" => await controller.Audit(ForgedOid),
                "void" => await controller.Void(ForgedOid),
                "restore" => await controller.Restore(ForgedOid),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知写动作")
            };
            var envelope = Envelope(result);
            return (true, envelope.Code, envelope.Message);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    // ==================== 零变更只读快照 ====================

    private sealed record WriteSnapshot(
        int SalesOrders,
        int SalesOrderDetails,
        int Stocks,
        int StockMovements,
        int OperationLogs,
        int DingTalkLogs,
        string NumberRules);

    private static async Task<WriteSnapshot> SnapshotAsync(ErpDbContext db) => new(
        await db.SalesOrders.CountAsync(),
        await db.SalesOrderDetails.CountAsync(),
        await db.Stocks.CountAsync(),
        await db.StockMovements.CountAsync(),
        await db.SysOperationLogs.CountAsync(),
        await db.SysDingTalkLogs.CountAsync(),
        string.Join(";", await db.SysDocumentNumberRules.AsNoTracking()
            .OrderBy(r => r.RuleCode)
            .Select(r => r.RuleCode + "=" + r.CurrentSequence)
            .ToListAsync()));

    /// <summary>播种一条基线规范销售订单 + 明细，便于证明被拒请求不会改动既有业务行。</summary>
    private static void SeedBaselineOrder(ErpDbContext db)
    {
        var order = new SalesOrder
        {
            OrderNo = "SO-BASELINE",
            OrderDate = DateTime.Today,
            CustomerId = 1,
            SalesmanId = 1,
            Currency = (Currency)2,
            ExchangeRate = 1m,
            TotalAmount = 100m,
            Status = DocumentStatus.Pending
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = 1,
            ProductName = "P1",
            Quantity = 1m,
            UnitPrice = 100m,
            Amount = 100m
        });
        db.SaveChanges();
    }
    // ==================== 1. 复用 ERP-404 有限策略目录 ====================

    [Fact]
    public void 写入门禁复用ERP404销售订单族且今日无已验证适配器()
    {
        var policy = LegacyBillMutationRules.Resolve(SalesOrderProcController.LegacyFamilyKey);
        Assert.Equal("sales-order", policy.FamilyKey);
        Assert.Equal("SalesOrder", policy.TableName);
        Assert.Equal("db_owner.sp_Biz_SalesOrder", policy.ProcedureName);
        Assert.Equal("sales-order", policy.ModuleMenuCode);
        Assert.Equal("/api/sales-orders", policy.CanonicalRoute);
        Assert.False(policy.HasValidatedAdapter);

        // 专用路由本身（api/v2/sales-orders）独立于 ERP-404 通用路由（api/v2/bills）：必须复用同一族策略入口。
        var route = Attribute.GetCustomAttribute(typeof(SalesOrderProcController), typeof(RouteAttribute));
        Assert.Equal("api/v2/sales-orders", Assert.IsType<RouteAttribute>(route).Template);

        // 有限目录中该族唯一（不新增第二套策略 / 业务实现）。
        Assert.Single(LegacyBillMutationRules.Families, f =>
            string.Equals(f.FamilyKey, SalesOrderProcController.LegacyFamilyKey, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("audit")]
    [InlineData("void")]
    [InlineData("restore")]
    public async Task 全部五个写动作failclosed且带稳定业务错误(string action)
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");

        var result = await AttemptAsync(db, user.Id, action);

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        Assert.Contains("sp_Biz_SalesOrder", result.Message, StringComparison.Ordinal);
        Assert.Contains("/api/sales-orders", result.Message, StringComparison.Ordinal);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    [Fact]
    public async Task 全部五个写动作被拒后业务明细库存单号与日志零变更()
    {
        await using var db = TestDbFactory.Create();
        SeedBaselineOrder(db);
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var before = await SnapshotAsync(db);

        foreach (var action in new[] { "save", "delete", "audit", "void", "restore" })
        {
            var result = await AttemptAsync(db, user.Id, action);
            Assert.True(result.GotEnvelope, $"{action}: {result.Message}");
            Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        }

        Assert.Equal(before, await SnapshotAsync(db));
        // 伪造的旧标识绝不被推断为规范 Id，也未新增任何业务行。
        Assert.Null(await db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == ForgedOid));
        Assert.Equal(1, await db.SalesOrders.CountAsync());
    }

    // ==================== 2. 实时身份 / 既有菜单拒绝矩阵 ====================

    [Fact]
    public async Task 缺失身份按未认证拒绝()
    {
        await using var db = TestDbFactory.Create();
        foreach (long? userId in new long?[] { null, 0, -3 })
        {
            var result = await AttemptAsync(db, userId, "save");
            Assert.True(result.GotEnvelope, result.Message);
            Assert.Equal(ErrorCodes.Unauthorized, result.Code);
        }
    }

    [Fact]
    public async Task 账号不存在或已删除按未认证拒绝()
    {
        await using var db = TestDbFactory.Create();
        var deleted = SeedUser(db, UserStatus.Enabled);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        var result = await AttemptAsync(db, deleted.Id, "audit");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Unauthorized, result.Code);
    }

    [Fact]
    public async Task 账号已禁用按权限不足拒绝()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();

        var result = await AttemptAsync(db, user.Id, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task 缺少销售订单功能菜单按权限不足拒绝()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db);

        var result = await AttemptAsync(db, user.Id, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task 仅持有导出菜单不得当作模块权限()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order-export");

        var result = await AttemptAsync(db, user.Id, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task 持有其他模块菜单的外来账号被拒绝()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "purchase-order");

        var result = await AttemptAsync(db, user.Id, "audit");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task 撤销销售订单菜单后下一次请求立即收敛()
    {
        await using var db = TestDbFactory.Create();
        var (user, role) = SeedRestrictedOperator(db, "sales-order");

        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == role.Id));
        await db.SaveChangesAsync();

        var result = await AttemptAsync(db, user.Id, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task 特权账号同样failclosed因无已验证适配器()
    {
        await using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);

        foreach (var action in new[] { "save", "delete", "audit", "void", "restore" })
        {
            var result = await AttemptAsync(db, privileged, action);
            Assert.True(result.GotEnvelope, result.Message);
            Assert.Equal(ErrorCodes.RuleConflict, result.Code);
            Assert.Contains("/api/sales-orders", result.Message, StringComparison.Ordinal);
        }
    }



    // ==================== 3. 伪造旧标识 / 金额载荷 ====================

    [Fact]
    public async Task 伪造旧标识与金额载荷仍failclosed且零写入()
    {
        await using var db = TestDbFactory.Create();
        SeedBaselineOrder(db);
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var before = await SnapshotAsync(db);

        var result = await AttemptAsync(db, user.Id, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        Assert.Contains("sp_Biz_SalesOrder", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(db));
        // 既有业务行的金额（被拒绝的请求头金额绝不落库）保持不变。
        Assert.Equal(100m, (await db.SalesOrders.AsNoTracking().SingleAsync()).TotalAmount);
    }

    // ==================== 4. 规范销售订单工作流回归 ====================

    [Fact]
    public async Task 规范销售订单创建审核取消流转仍可用()
    {
        await using var db = TestDbFactory.Create();
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

        var order = new SalesOrder
        {
            OrderDate = DateTime.Today,
            CustomerId = 9001,
            SalesmanId = 9002,
            Currency = (Currency)2,
            ExchangeRate = 7.1m,
            DepositRatio = 30m,
            DeliveryDate = DateTime.Today.AddDays(30),
            Remark = "ERP-410 canonical regression",
            Details = new List<SalesOrderDetail>
            {
                new SalesOrderDetail { ProductId = 1, ProductName = "P1", Quantity = 10m, UnitPrice = 100m }
            }
        };

        Assert.IsType<OkObjectResult>(await controller.Create(order));
        var created = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.True(created.Id > 0);
        Assert.Equal(1000m, created.TotalAmount);

        await controller.Submit(created.Id);
        await controller.Approve(created.Id);
        Assert.Equal(DocumentStatus.Approved, (await db.SalesOrders.AsNoTracking().SingleAsync()).Status);

        await controller.Cancel(created.Id);
        Assert.Equal(DocumentStatus.Cancelled, (await db.SalesOrders.AsNoTracking().SingleAsync()).Status);
    }
}

