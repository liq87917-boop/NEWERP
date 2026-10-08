using System.Reflection;
using ERP.Api.Controllers;
using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-404 旧单据（<c>api/v2/bills</c>）有限服务端变更策略单元测试（内存库 + 真实既有身份 / 菜单授权）。
/// <list type="bullet">
/// <item>有限目录：恰好覆盖 <c>BillProcController.Bills</c> 全部 16 族；导出菜单绝不当模块权限；今日无任何已验证适配器；</item>
/// <item>写前门禁：实时身份（缺失 / 已删除 / 已禁用）+ 既有功能菜单（导出菜单不顶替）+ 保留命令字段（<c>@Action</c> / <c>@Oid</c>）拒绝；</item>
/// <item>fail closed：无权威「旧 Oid ↔ 规范 Id」映射时，一律稳定业务错误 + 有限既有规范路由指引，绝不执行 <c>sp_Biz_*</c>；</item>
/// <item>控制器零副作用：Save / 全部状态流转 / Excel 导入被拒时业务表、明细、单号、操作日志、钉钉通知全部零变更。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与真实既有身份；跨连接竞态与真实 SQL 由
/// <c>LegacyBillMutationSqlServerTests</c> 覆盖。</para>
/// </summary>
public class LegacyBillMutationTests
{
    // ==================== 脚手架 ====================

    private const string UnusedConnectionString =
        "Server=(localdb)\\NEWERP_UnitTests_Gate;Database=UNUSED;Integrated Security=true;Connect Timeout=1";

    /// <summary>绑定真实 HttpContext 身份（可空 = 无身份）的旧单据控制器；连接串不可用，任何 sp_Biz_* 调用都会立刻失败。</summary>
    private static BillProcController NewController(ErpDbContext db, long? userId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = UnusedConnectionString
            })
            .Build();

        var controller = new BillProcController(
            new StoredProcedureService(configuration),
            db,
            new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance));

        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>播种一位**受限制**操作员（非系统内置角色 → 非特权）并按需授予既有功能菜单。</summary>
    private static (SysUser User, SysRole Role) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = $"legacy-op-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "受限操作员",
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
            UserName = $"legacy-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static Dictionary<string, BillMeta> BillsCatalog()
    {
        var field = typeof(BillProcController).GetField("Bills", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Dictionary<string, BillMeta>)field!.GetValue(null)!;
    }

    private static BillSaveRequest SaveRequest(params (string Key, object? Value)[] fields)
    {
        var request = new BillSaveRequest { Oid = 0 };
        foreach (var (key, value) in fields)
            request.Fields[key] = value;
        return request;
    }

    // ==================== 1. 有限策略目录 ====================

    [Fact]
    public void 变更策略恰好覆盖BillProcController的全部16个旧单据族()
    {
        var catalog = BillsCatalog();
        Assert.Equal(16, LegacyBillMutationRules.Families.Count);
        Assert.Equal(catalog.Count, LegacyBillMutationRules.Families.Count);

        Assert.Equal(
            catalog.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            LegacyBillMutationRules.Families.Select(f => f.FamilyKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var policy in LegacyBillMutationRules.Families)
        {
            var meta = catalog[policy.FamilyKey];
            Assert.Equal(meta.Table, policy.TableName);
            Assert.Equal(meta.Proc, policy.ProcedureName);
            Assert.StartsWith("db_owner.sp_Biz_", policy.ProcedureName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 变更策略绝不把导出菜单当作模块权限且给出有限规范路由()
    {
        foreach (var policy in LegacyBillMutationRules.Families)
        {
            Assert.False(string.IsNullOrWhiteSpace(policy.ModuleMenuCode));
            // 导出类菜单（*-export）绝不作为模块权限。
            Assert.DoesNotContain("export", policy.ModuleMenuCode, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(policy.ModuleMenuText));
            Assert.StartsWith("/api/", policy.CanonicalRoute, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(policy.CanonicalRouteText));
        }
    }

    [Fact]
    public void 变更策略今日没有任何已验证适配器()
    {
        Assert.All(LegacyBillMutationRules.Families, policy => Assert.False(policy.HasValidatedAdapter));
    }

    [Fact]
    public void 未知或畸形族标识在访问数据之前被拒绝()
    {
        foreach (var invalid in new[] { null, "", "   ", "unknown", "sales order", "a;drop table", new string('x', 65) })
        {
            Assert.Throws<BusinessException>(() => LegacyBillMutationRules.Resolve(invalid));
            Assert.False(LegacyBillMutationRules.TryResolve(invalid, out _));
        }
    }

    [Fact]
    public void 操作标识解析大小写不敏感且未知标识被拒绝()
    {
        Assert.Equal(LegacyBillOperation.Save, LegacyBillMutationRules.ParseOperation("save"));
        Assert.Equal(LegacyBillOperation.Delete, LegacyBillMutationRules.ParseOperation("DELETE"));
        Assert.Equal(LegacyBillOperation.UnAudit, LegacyBillMutationRules.ParseOperation(" UnAudit "));
        Assert.Equal(LegacyBillOperation.Import, LegacyBillMutationRules.ParseOperation("import"));
        Assert.Throws<BusinessException>(() => LegacyBillMutationRules.ParseOperation("drop"));
        Assert.Throws<BusinessException>(() => LegacyBillMutationRules.ParseOperation(null));
    }

    // ==================== 2. 保留命令字段 ====================

    [Fact]
    public void 保留命令字段识别大小写不敏感()
    {
        Assert.True(LegacyBillMutationRules.IsReservedCommandField("Action"));
        Assert.True(LegacyBillMutationRules.IsReservedCommandField(" oid "));
        Assert.True(LegacyBillMutationRules.IsReservedCommandField("DETAILSJSON"));
        Assert.False(LegacyBillMutationRules.IsReservedCommandField("BillNoText"));
        Assert.False(LegacyBillMutationRules.IsReservedCommandField(null));
    }

    [Fact]
    public void 保留命令字段注入被拒绝()
    {
        var injected = new Dictionary<string, object?> { ["Action"] = "Audit", ["TotalAmount"] = 1m };
        var ex = Assert.Throws<BusinessException>(() => LegacyBillMutationRules.EnsureNoReservedCommandFields(injected));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 大小写 / 空白变体同样拒绝；无保留字段时放行。
        Assert.Throws<BusinessException>(() =>
            LegacyBillMutationRules.EnsureNoReservedCommandFields(new Dictionary<string, object?> { [" oid "] = 7L }));
        LegacyBillMutationRules.EnsureNoReservedCommandFields(
            new Dictionary<string, object?> { ["TotalAmount"] = 1m });
        LegacyBillMutationRules.EnsureNoReservedCommandFields(null);
    }

    [Fact]
    public void 剥离保留命令字段后仅保留业务字段()
    {
        var stripped = LegacyBillMutationRules.StripReservedCommandFields(new Dictionary<string, object?>
        {
            ["Action"] = "Void", ["Oid"] = 9L, ["Currency"] = 2, ["Remark"] = "x"
        });

        Assert.Equal(2, stripped.Count);
        Assert.True(stripped.ContainsKey("Currency"));
        Assert.True(stripped.ContainsKey("Remark"));
        Assert.False(stripped.ContainsKey("Action"));
        Assert.False(stripped.ContainsKey("Oid"));
        Assert.Empty(LegacyBillMutationRules.StripReservedCommandFields(null));
    }

    // ==================== 3. 写前门禁（身份 / 菜单 / 保留字段 / fail closed） ====================

    [Fact]
    public async Task 缺失身份一律按未认证拒绝()
    {
        await using var db = TestDbFactory.Create();
        foreach (long? userId in new long?[] { null, 0, -3 })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
                db, userId, "sales-order", LegacyBillOperation.Save));
            Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        }

        Assert.Empty(db.SysOperationLogs);
    }

    [Fact]
    public async Task 账号不存在或已删除按未认证拒绝()
    {
        await using var db = TestDbFactory.Create();
        var deleted = SeedUser(db, UserStatus.Enabled);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
            db, deleted.Id, "sales-order", LegacyBillOperation.Save));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 账号已禁用按权限不足拒绝()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
            db, user.Id, "sales-order", LegacyBillOperation.Save));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 受限账号缺少功能菜单按权限不足拒绝()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
            db, user.Id, "stock-in", LegacyBillOperation.Save));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 仅持有导出菜单不得当作模块权限()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order-export");
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
            db, user.Id, "sales-order", LegacyBillOperation.Save));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 受限账号具备功能菜单仍然failclosed并提示规范路由()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
            db, user.Id, "sales-order", LegacyBillOperation.Save));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("/api/sales-orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sp_Biz_", ex.Message, StringComparison.Ordinal);
        Assert.Empty(db.SysOperationLogs);
    }

    [Fact]
    public async Task 特权账号同样failclosed因无已验证适配器()
    {
        await using var db = TestDbFactory.Create();
        var privilegedId = TestAuth.SeedPrivilegedUser(db);
        foreach (var operation in new[] { LegacyBillOperation.Save, LegacyBillOperation.Audit, LegacyBillOperation.Import })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
                db, privilegedId, "stock-in", operation));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        }
    }

    [Fact]
    public async Task 保留字段注入在身份与菜单通过后仍被拒绝()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
            db, user.Id, "sales-order", LegacyBillOperation.Save,
            new Dictionary<string, object?> { ["Action"] = "Audit" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 授权规则直接复用实时身份与既有功能菜单()
    {
        await using var db = TestDbFactory.Create();
        var (user, role) = SeedRestrictedOperator(db, "stock-in");

        var scope = await LegacyBillAuthorizationRules.EnsureModuleAuthorizedAsync(db, user.Id, "stock-in", "采购入库");
        Assert.False(scope.IsPrivileged);

        var denied = await Assert.ThrowsAsync<BusinessException>(() =>
            LegacyBillAuthorizationRules.EnsureModuleAuthorizedAsync(db, user.Id, "stock-out", "销售出库"));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);

        // 撤销功能菜单后立即收敛（不缓存）。
        var grant = db.SysRoleMenus.First(rm => rm.RoleId == role.Id);
        grant.IsDeleted = true;
        await db.SaveChangesAsync();
        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<BusinessException>(() =>
            LegacyBillAuthorizationRules.EnsureModuleAuthorizedAsync(db, user.Id, "stock-in", "采购入库"))).Code);
    }

    // ==================== 4. 控制器：Save / Action / Import 零副作用 ====================

    [Fact]
    public async Task Save_无身份返回未认证且零写入()
    {
        await using var db = TestDbFactory.Create();
        var envelope = Envelope(await NewController(db, null).Save("sales-order", SaveRequest(("CustId", 1))));

        Assert.Equal(ErrorCodes.Unauthorized, envelope.Code);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    [Fact]
    public async Task Save_受限账号缺少菜单返回权限不足且零写入()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db);

        var envelope = Envelope(await NewController(db, user.Id).Save("stock-in", SaveRequest(("SupplierId", 1))));

        Assert.Equal(ErrorCodes.Forbidden, envelope.Code);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.SysOperationLogs);
    }

    [Fact]
    public async Task Save_保留Action与Oid注入被拒绝且零写入()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var request = SaveRequest(("Action", "audit"), ("Oid", 99L), ("Remark", "x"));

        var envelope = Envelope(await NewController(db, user.Id).Save("sales-order", request));

        Assert.Equal(ErrorCodes.InvalidParameter, envelope.Code);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SalesOrderDetails);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    [Fact]
    public async Task Save_已授权账号仍然failclosed且未执行存储过程()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var numberRulesBefore = await db.SysDocumentNumberRules.AsNoTracking().CountAsync();

        var envelope = Envelope(await NewController(db, user.Id)
            .Save("sales-order", SaveRequest(("CustId", 7), ("TotalAmount", 100m))));

        Assert.Equal(ErrorCodes.RuleConflict, envelope.Code);
        Assert.Contains("/api/sales-orders", envelope.Message, StringComparison.Ordinal);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SalesOrderDetails);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        Assert.Equal(numberRulesBefore, await db.SysDocumentNumberRules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Import_被拒绝且零写入()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "stock-in");

        var envelope = Envelope(await NewController(db, user.Id).Import("stock-in", null));

        Assert.Equal(ErrorCodes.RuleConflict, envelope.Code);
        Assert.Contains("/api/stock-ins", envelope.Message, StringComparison.Ordinal);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockInDetails);
        Assert.Empty(db.SysOperationLogs);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("audit")]
    [InlineData("unaudit")]
    [InlineData("void")]
    [InlineData("restore")]
    public async Task Action_全部流转均被拒绝且零状态与零日志(string op)
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "stock-out");

        var envelope = Envelope(await NewController(db, user.Id).Action("stock-out", 12, op));

        Assert.Equal(ErrorCodes.RuleConflict, envelope.Code);
        Assert.Contains("/api/stock-outs", envelope.Message, StringComparison.Ordinal);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    [Fact]
    public async Task Action_无身份返回未认证且零写入()
    {
        await using var db = TestDbFactory.Create();
        var envelope = Envelope(await NewController(db, null).Action("sales-order", 3, "audit"));

        Assert.Equal(ErrorCodes.Unauthorized, envelope.Code);
        Assert.Empty(db.SysOperationLogs);
    }

    [Fact]
    public async Task Action_非法操作仍返回既有无效操作且不进入写入门禁()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "stock-out");

        var envelope = Envelope(await NewController(db, user.Id).Action("stock-out", 12, "drop"));

        Assert.Equal(ErrorCodes.InvalidParameter, envelope.Code);
        Assert.Contains("无效操作", envelope.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知单据类型仍返回既有错误且零写入()
    {
        await using var db = TestDbFactory.Create();
        var (user, _) = SeedRestrictedOperator(db, "sales-order");
        var controller = NewController(db, user.Id);

        var save = Envelope(await controller.Save("no-such-bill", SaveRequest()));
        Assert.Equal(ErrorCodes.InvalidParameter, save.Code);
        Assert.Contains("未知单据类型", save.Message, StringComparison.Ordinal);

        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Action("no-such-bill", 1, "audit")).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Import("no-such-bill", null)).Code);

        Assert.Empty(db.SysOperationLogs);
    }

    [Fact]
    public async Task 状态校验异常时failclosed不把校验失败当成功()
    {
        await using var db = TestDbFactory.Create();
        var controller = NewController(db, TestAuth.SeedPrivilegedUser(db));
        var method = typeof(BillProcController).GetMethod("VerifyActionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // 连接串不可用 → 状态读取抛异常；fail closed 必须返回 false（旧实现返回 true 的错误口径已修复）。
        var task = (Task<bool>)method!.Invoke(controller, new object[] { "SalesOrder", 1L, "audit" })!;
        Assert.False(await task);
    }

    // ==================== 5. 全部 16 族逐族覆盖（Save / 每个流转 / 导入） ====================

    [Fact]
    public async Task 全部16族的Save与全部流转均failclosed且零写入()
    {
        foreach (var policy in LegacyBillMutationRules.Families)
        {
            await using var db = TestDbFactory.Create();
            var (user, _) = SeedRestrictedOperator(db, policy.ModuleMenuCode);

            foreach (var operation in new[]
                     {
                         LegacyBillOperation.Save, LegacyBillOperation.Delete, LegacyBillOperation.Audit,
                         LegacyBillOperation.UnAudit, LegacyBillOperation.Void, LegacyBillOperation.Restore,
                         LegacyBillOperation.Import
                     })
            {
                var ex = await Assert.ThrowsAsync<BusinessException>(() => LegacyBillMutationRules.AuthorizeAsync(
                    db, user.Id, policy.FamilyKey, operation));

                Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
                Assert.Contains(policy.CanonicalRoute, ex.Message, StringComparison.Ordinal);
                Assert.Contains(policy.ProcedureName, ex.Message, StringComparison.Ordinal);
            }

            Assert.Empty(db.SysOperationLogs);
            Assert.Empty(db.SysDingTalkLogs);
        }
    }

    [Fact]
    public async Task 全部16族的Save路由均被拒绝且零写入()
    {
        foreach (var policy in LegacyBillMutationRules.Families)
        {
            await using var db = TestDbFactory.Create();
            var (user, _) = SeedRestrictedOperator(db, policy.ModuleMenuCode);

            var envelope = Envelope(await NewController(db, user.Id)
                .Save(policy.FamilyKey, SaveRequest(("Remark", "x"))));

            Assert.Equal(ErrorCodes.RuleConflict, envelope.Code);
            Assert.Contains(policy.CanonicalRoute, envelope.Message, StringComparison.Ordinal);
            Assert.Empty(db.SysOperationLogs);
            Assert.Empty(db.SysDingTalkLogs);
        }
    }

    [Fact]
    public async Task 全部16族的Excel导入均被拒绝且零写入()
    {
        foreach (var policy in LegacyBillMutationRules.Families)
        {
            await using var db = TestDbFactory.Create();
            var (user, _) = SeedRestrictedOperator(db, policy.ModuleMenuCode);

            var envelope = Envelope(await NewController(db, user.Id).Import(policy.FamilyKey, null));

            Assert.Equal(ErrorCodes.RuleConflict, envelope.Code);
            Assert.Contains(policy.CanonicalRoute, envelope.Message, StringComparison.Ordinal);
            Assert.Empty(db.SysOperationLogs);
            Assert.Empty(db.SysDingTalkLogs);
        }
    }
}
