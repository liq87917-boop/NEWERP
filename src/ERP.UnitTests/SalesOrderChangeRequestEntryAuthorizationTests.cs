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
/// ERP-489 销售订单变更申请登记册（<c>api/sales-order-change-requests</c>）入口实时授权单元测试。
/// <para>覆盖台账 / 来源候选 / 指定来源清单 / 详情 / 登记 / 编辑 / 提交 / 取消<strong>全部八条数据 / 状态入口</strong>在
/// 「已赋值路径 / 空路径（未设置 <c>Request.Path</c>）/ 完全未绑定 <c>HttpContext</c>」三种调用形状下的一致判定：
/// 空路径与已赋值路径口径完全一致，登记 / 编辑 / 提交 / 取消不再存在无授权的服务直调旁路；缺失 / 非法 / 零 / 未知 /
/// 已删除身份一律未认证（2000），禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单一律权限不足（2002），受限业务员越界来源与申请
/// 一律受控非披露拒绝，且全部先于任何计数 / 候选 / 快照读取与写入；被拒调用不发号、零申请 / 拟议 / 来源 / 审计变更。
/// 授权身份在两种路径形状下都完成既有草稿 → 编辑 → 提交 → 取消生命周期，来源订单与拟议快照边界保持不变。</para>
/// <para>全部使用隔离内存测试数据（<see cref="TestDbFactory"/>），只播种既有菜单 / 角色授权与真实启用身份，
/// 绝不新增任何生产菜单 / 权限 / 用户授权，也不提供任何请求路径 / 环境变量 / 测试专用开关旁路。
/// 真实隔离 SQL 用例见 <c>ERP.IntegrationTests/SalesOrderChangeRequestEntryAuthorizationSqlServerTests.cs</c>。</para>
/// </summary>
public class SalesOrderChangeRequestEntryAuthorizationTests
{
    private const string BasePath = "/api/sales-order-change-requests";
    private const long MissingSourceOrderId = 9_489_999L;

    // ==================== 0. 测试脚手架（隔离测试数据，绝不新增生产授权） ====================

    /// <summary>补缺失的既有菜单行（与 <c>SeedData.Menus</c> 同源编码 / 文案），绝不改写既有菜单语义。</summary>
    private static SysMenu EnsureMenu(ErpDbContext db, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted && m.MenuCode == menuCode);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = menuCode, MenuName = menuName,
            Path = "/sales-order", MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种启用账号 + 真实角色授权（<paramref name="privileged"/> = 系统内置角色口径）。</summary>
    private static long SeedIdentity(
        ErpDbContext db, bool privileged, string? menuCode = null,
        UserStatus status = UserStatus.Enabled, bool deleted = false)
    {
        var code = $"socr489-{Guid.NewGuid():N}";
        var role = new SysRole { RoleCode = code, RoleName = code, IsSystem = privileged };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (menuCode is not null)
        {
            var menu = EnsureMenu(db, menuCode, menuCode);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user.Id;
    }

    /// <summary>播种受限业务员（登录账号 == 员工编码，ERP-097 权威映射）并返回其账号 / 员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedSalesman(
        ErpDbContext db, bool menu = true, UserStatus status = UserStatus.Enabled, bool deleted = false)
    {
        var code = $"socr489-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = code, RoleName = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (menu)
        {
            var m = EnsureMenu(db, SalesOrderChangeRequestAuthorizationRules.RequiredMenuCode,
                SalesOrderChangeRequestAuthorizationRules.RequiredMenuText);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = m.Id });
            db.SaveChanges();
        }

        return (user.Id, employee.Id);
    }

    /// <summary>软删除指定账号全部「角色 → 菜单」授权行（模拟既有菜单授权被撤销，不改写任何权限模型）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long? empId, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}", CustomerName = name, EmpId = empId, Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, long customerId, string orderNo, bool deleted = false,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = new DateTime(2026, 9, 1), CustomerId = customerId,
            SalesmanId = null, Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            Status = status, IsDeleted = deleted, TotalAmount = 150m, DepositAmount = 45m,
            CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0)
        };
        order.Details = new List<SalesOrderDetail>
        {
            new() { ProductId = 101, ProductName = "A 商品", Spec = "红", Unit = "PCS", Quantity = 10, UnitPrice = 5, Amount = 50 },
            new() { ProductId = 102, ProductName = "B 商品", Spec = "蓝", Unit = "PCS", Quantity = 4, UnitPrice = 25, Amount = 100 }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesOrderChangeRequest,
            RuleCode = $"socr489-{Guid.NewGuid():N}",
            RuleName = "变更申请单号",
            Prefix = "SOC", DateFormat = "yyyyMMdd", SerialLength = 4, CurrentSequence = 0
        });
        db.SaveChanges();
    }

    // ==================== 控制器工厂与断言助手 ====================

    /// <summary>
    /// 绑定真实 HTTP 身份的控制器；<paramref name="path"/> 为 null 时刻意保持空 <c>Request.Path</c>，
    /// 用于证明授权判定与请求形状无关。
    /// </summary>
    private static SalesOrderChangeRequestController Controller(ErpDbContext db, long? userId, string? path)
    {
        var http = new DefaultHttpContext
        {
            User = userId.HasValue
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
                : new ClaimsPrincipal(new ClaimsIdentity())
        };
        if (path is not null) http.Request.Path = path;

        return new SalesOrderChangeRequestController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>完全未绑定请求上下文的纯进程内直调（外部请求无法构造）。</summary>
    private static SalesOrderChangeRequestController ContextlessController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        Assert.NotNull(response.Data);
        return response.Data!;
    }

    private static async Task<BusinessException> AssertDeniedAsync(int expectedCode, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string name, decimal quantity, decimal unitPrice)
        => new() { ProductId = productId, ProductName = name, Spec = "红", Unit = "PCS", Quantity = quantity, UnitPrice = unitPrice };

    private static SalesOrderChangeRequestSaveDto Save(long salesOrderId, string reason,
        List<SalesOrderChangeRequestDetailSaveDto>? details = null)
        => new() { SalesOrderId = salesOrderId, Reason = reason, Details = details };

    /// <summary>逐入口一致判定：八条数据 / 状态入口在给定身份与调用形状下必须给出同一受控错误码。</summary>
    private static async Task AssertAllRoutesDeniedAsync(
        ErpDbContext db, long? userId, string? path, long salesOrderId, long requestId, int expectedCode)
    {
        var ctl = Controller(db, userId, path);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.GetPaged(new SalesOrderChangeRequestQuery()))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.SourceOptions(null))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.GetForSource(salesOrderId))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.GetById(requestId))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.Create(Save(salesOrderId, "越权登记")))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.Update(requestId, Save(salesOrderId, "越权编辑")))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.Submit(requestId))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.Cancel(requestId, new SalesOrderChangeRequestCancelRequest { Reason = "越权取消" }))).Code);
    }

    private static async Task<long> SequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.SalesOrderChangeRequest)
            .SumAsync(r => (long)r.CurrentSequence);

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // ==================== 1. 控制器源码契约：授权门与请求形状无关、无兜底 ====================

    [Fact]
    public void 控制器契约_授权与请求形状无关且不存在无授权服务直调旁路()
    {
        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "SalesOrderChangeRequestController.cs"));

        // 不再读取 Request.Path；无 RequiresLiveAuthorization / 空 scope 回退；无匿名 / 角色白名单 / 环境变量 / 内存库开关兜底。
        Assert.DoesNotContain("Request.Path", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("RequiresLiveAuthorization", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("scope is not null", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("[Authorize(Roles", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("UseInMemoryDatabase", controller, StringComparison.Ordinal);

        // 八条入口都无条件解析实时授权，详情无条件复核申请 / 实时来源归属。
        Assert.Contains("SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync", controller, StringComparison.Ordinal);
        Assert.Contains("SalesOrderChangeRequestAuthorizationRules.EnsureRequestAllowedAsync", controller, StringComparison.Ordinal);
        Assert.True(CountOf(controller, "var scope = await EnsureAuthorizedAsync();") >= 8,
            "台账 / 来源候选 / 指定来源 / 详情 / 登记 / 编辑 / 提交 / 取消都必须先解析实时授权");

        // 口径文案与实现同源（接口 / 文档 / 测试共用）。
        var text = SalesOrderChangeRequestAuthorizationRules.PathIndependenceText;
        Assert.Contains("未认证", text, StringComparison.Ordinal);
        Assert.Contains("权限不足", text, StringComparison.Ordinal);
        Assert.Contains("与调用形状完全无关", text, StringComparison.Ordinal);
        Assert.Contains("无授权的服务直调", text, StringComparison.Ordinal);

        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "SalesOrderChangeRequestAuthorizationRules.cs"));
        Assert.Contains("SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync", rules, StringComparison.Ordinal);
        Assert.Contains("SalespersonDataScopeService", rules, StringComparison.Ordinal);
        Assert.Contains("PathIndependenceText", rules, StringComparison.Ordinal);
    }

    // ==================== 2. 缺 / 零 / 未知 / 已删除身份：任何请求形状一致（含无上下文） ====================

    [Fact]
    public async Task 缺失或零或未知或已删除身份_八条入口任何请求形状一律未认证且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, null, "授权客户");
        var order = SeedOrder(db, customer.Id, "SO-489-AUTH");
        SeedNumberRule(db);

        var deletedId = SeedIdentity(db, privileged: true, deleted: true);

        foreach (var path in new string?[] { null, BasePath, "/api/sales-order-change-requests/other" })
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
            {
                var expected = identity is null or <= 0
                    ? SalesOrderChangeRequestAuthorizationRules.UnauthorizedText
                    : SalesOrderChangeRequestAuthorizationRules.UserDeletedText;
                Assert.Equal(expected, (await AssertDeniedAsync(ErrorCodes.Unauthorized,
                    () => Controller(db, identity, path).Create(Save(order.Id, "越权登记")))).Message);
                await AssertAllRoutesDeniedAsync(db, identity, path, order.Id, 1L, ErrorCodes.Unauthorized);
            }

        // 完全未绑定请求上下文：同样 fail closed（不得退化为匿名或管理员）。
        var contextless = ContextlessController(db);
        Assert.Equal(SalesOrderChangeRequestAuthorizationRules.UnauthorizedText,
            (await AssertDeniedAsync(ErrorCodes.Unauthorized,
                () => contextless.Create(Save(order.Id, "越权登记")))).Message);
        Assert.Equal(ErrorCodes.Unauthorized, (await AssertDeniedAsync(ErrorCodes.Unauthorized,
            () => contextless.GetPaged(new SalesOrderChangeRequestQuery()))).Code);

        // 零写入：不新增 / 不改写任何申请、明细、号码或来源订单。
        Assert.Equal(0, await db.SalesOrderChangeRequests.CountAsync());
        Assert.Equal(0, await db.SalesOrderChangeRequestDetails.CountAsync());
        Assert.Equal(0L, await SequenceAsync(db));
        Assert.Equal(150m, (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).TotalAmount);
    }

    // ==================== 3. 禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单：任何请求形状一致 ====================

    [Fact]
    public async Task 禁用或无菜单或仅导出菜单或已撤销菜单_八条入口任何请求形状一律权限不足且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, null, "授权客户");
        var order = SeedOrder(db, customer.Id, "SO-489-MENU");
        SeedNumberRule(db);

        var disabledId = SeedIdentity(db, privileged: false,
            menuCode: SalesOrderChangeRequestAuthorizationRules.RequiredMenuCode, status: UserStatus.Disabled);
        var menuLessId = SeedIdentity(db, privileged: false);
        var exportOnlyId = SeedIdentity(db, privileged: false, menuCode: "sales-order-export");
        var revokedId = SeedIdentity(db, privileged: false,
            menuCode: SalesOrderChangeRequestAuthorizationRules.RequiredMenuCode);
        RevokeMenus(db, revokedId);

        foreach (var path in new string?[] { null, BasePath })
        {
            Assert.Equal(SalesOrderChangeRequestAuthorizationRules.UserDisabledText,
                (await AssertDeniedAsync(ErrorCodes.Forbidden,
                    () => Controller(db, disabledId, path).Create(Save(order.Id, "越权登记")))).Message);
            await AssertAllRoutesDeniedAsync(db, disabledId, path, order.Id, 1L, ErrorCodes.Forbidden);

            foreach (var menuDeniedId in new[] { menuLessId, exportOnlyId, revokedId })
            {
                Assert.Equal(SalesOrderChangeRequestAuthorizationRules.MenuDeniedText,
                    (await AssertDeniedAsync(ErrorCodes.Forbidden,
                        () => Controller(db, menuDeniedId, path).Create(Save(order.Id, "越权登记")))).Message);
                await AssertAllRoutesDeniedAsync(db, menuDeniedId, path, order.Id, 1L, ErrorCodes.Forbidden);
            }
        }

        Assert.Equal(0, await db.SalesOrderChangeRequests.CountAsync());
        Assert.Equal(0L, await SequenceAsync(db));
    }

    // ==================== 4. 受限业务员：越界来源 / 申请非披露拒绝且零变更 ====================

    [Fact]
    public async Task 受限业务员越界来源与申请_任何请求形状同一非披露错误_零变更且不发号()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedSalesman(db);
        var own = SeedCustomer(db, employeeId, "本人客户");
        var foreign = SeedCustomer(db, null, "他人客户");
        var ownOrder = SeedOrder(db, own.Id, "SO-489-OWN");
        var foreignOrder = SeedOrder(db, foreign.Id, "SO-489-FOREIGN");
        SeedNumberRule(db);
        var before = await SequenceAsync(db);

        // 他人申请由特权账号登记（用于证明受限账号不可见，且越权调用不改写它）。
        var foreignCreated = AssertOk<SalesOrderChangeRequestDto>(
            await Controller(db, SeedIdentity(db, privileged: true), BasePath)
                .Create(Save(foreignOrder.Id, "他人登记")));

        foreach (var path in new string?[] { null, BasePath })
        {
            var ctl = Controller(db, userId, path);

            // 本人来源可用（来源实时归属为本人客户）。
            Assert.Equal(ownOrder.Id,
                AssertOk<SalesOrderChangeRequestDto>(await ctl.Create(Save(ownOrder.Id, "本人登记"))).SalesOrderId);

            // 越界 / 不存在来源一律同一非披露错误（不泄露范围外 Id）。
            var missing = await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Create(Save(foreignOrder.Id, "越权登记")));
            Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, missing.Message);
            Assert.Equal(missing.Message, (await AssertDeniedAsync(ErrorCodes.NotFound,
                () => ctl.Create(Save(MissingSourceOrderId, "越权登记")))).Message);

            // 越界来源不可见：来源候选只含本人订单，指定来源清单越界即拒绝。
            var options = AssertOk<List<SalesOrderChangeRequestSourceOptionDto>>(await ctl.SourceOptions(null));
            Assert.Contains(options, o => o.SalesOrderId == ownOrder.Id);
            Assert.DoesNotContain(options, o => o.SalesOrderId == foreignOrder.Id);
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.GetForSource(foreignOrder.Id));

            // 越界申请详情 / 编辑 / 提交 / 取消一律同一非披露错误，且不泄露他人申请存在性。
            var denied = await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.GetById(foreignCreated.Id));
            Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, denied.Message);
            Assert.DoesNotContain("SO-489-FOREIGN", denied.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(foreignCreated.RequestNo, denied.Message, StringComparison.Ordinal);
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Update(foreignCreated.Id, Save(ownOrder.Id, "越权编辑")));
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Submit(foreignCreated.Id));
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Cancel(foreignCreated.Id,
                new SalesOrderChangeRequestCancelRequest { Reason = "越权取消" }));

            // 被拒调用不改写他人申请状态 / 拟议值。
            var stored = await db.SalesOrderChangeRequests.AsNoTracking().SingleAsync(r => r.Id == foreignCreated.Id);
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, stored.Status);
            Assert.Null(stored.SubmittedAt);
            Assert.Null(stored.CancelledAt);
        }

        // 仅三次许可登记发号（1 次他人 + 两次本人），越权调用一律不发号。
        Assert.Equal(before + 3, await SequenceAsync(db));
        Assert.Equal(3, await db.SalesOrderChangeRequests.AsNoTracking().CountAsync());
        Assert.All(await db.SalesOrderChangeRequests.AsNoTracking().ToListAsync(),
            r => Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, r.Status));
    }

    // ==================== 5. 授权身份：两种请求形状都完成既有生命周期 ====================

    [Fact]
    public async Task 授权身份_空路径与已赋值路径都完成既有草稿编辑提交取消生命周期()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedSalesman(db);
        var own = SeedCustomer(db, employeeId, "本人客户");
        var order = SeedOrder(db, own.Id, "SO-489-LIFE");

        foreach (var path in new string?[] { null, BasePath })
        {
            var ctl = Controller(db, userId, path);

            // 读侧入口在授权后可用。
            AssertOk<PagedResult<SalesOrderChangeRequestDto>>(await ctl.GetPaged(new SalesOrderChangeRequestQuery()));
            AssertOk<List<SalesOrderChangeRequestSourceOptionDto>>(await ctl.SourceOptions(null));

            // 登记草稿 → 编辑（仅草稿可编辑）→ 提交（冻结拟议快照）→ 取消（保留原因与历史）。
            var created = AssertOk<SalesOrderChangeRequestDto>(await ctl.Create(Save(order.Id, "客户要求改数量")));
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, created.Status);
            Assert.True(created.IsDraft);
            Assert.StartsWith("SOC", created.RequestNo, StringComparison.Ordinal);

            var updated = AssertOk<SalesOrderChangeRequestDto>(await ctl.Update(created.Id, Save(order.Id, "改数量与交期",
                new List<SalesOrderChangeRequestDetailSaveDto>
                {
                    Detail(101, "A 商品", 12m, 5m),
                    Detail(102, "B 商品", 4m, 25m)
                })));
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, updated.Status);

            var submitted = AssertOk<SalesOrderChangeRequestDto>(await ctl.Submit(created.Id));
            Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, submitted.Status);
            Assert.NotNull(submitted.SubmittedAt);

            var cancelled = AssertOk<SalesOrderChangeRequestDto>(await ctl.Cancel(created.Id,
                new SalesOrderChangeRequestCancelRequest { Reason = "客户撤回" }));
            Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);
            Assert.Equal("客户撤回", cancelled.CancelledReason);

            // 详情入口对本人申请可用。
            Assert.Equal(created.Id, AssertOk<SalesOrderChangeRequestDto>(await ctl.GetById(created.Id)).Id);
        }

        // 来源订单与拟议对照边界不被改写：来源状态 / 总额 / 明细行数保持不变，绝不自动套用拟议。
        var storedOrder = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, storedOrder.Status);
        Assert.Equal(150m, storedOrder.TotalAmount);
        Assert.Equal(2, storedOrder.Details.Count);

        // 取消保留原始与拟议证据（历史可读），不做硬删除。
        var requests = await db.SalesOrderChangeRequests.AsNoTracking().ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, r.Status));
        Assert.All(requests, r => Assert.Equal("客户撤回", r.CancelledReason));
    }
}
