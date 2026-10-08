using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-390 定金申请单来源销售订单候选 / 已存储来源 + 表单显式选择的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="FinanceDepositApplyLifecycleSqlServerFixture"/>）。
/// <list type="number">
/// <item><b>真实控制器</b>：既有「定金申请单」+「销售订单」菜单与既有业务员数据范围驱动真实
/// <see cref="FinanceDepositApplyController"/>，验证候选只含<b>精确客户</b>名下「未删除」订单、已取消 / 币种不一致
/// 显式标记不可选、范围外 / 越权一律 fail closed；</item>
/// <item><b>伪造来源</b>：候选选择不等于授权——最终保存按生命周期规则复核精确来源，
/// 伪造 / 已取消 / 跨客户来源一律拒绝且不消耗单号、不落半成品；</item>
/// <item><b>历史只读</b>：来源事后取消 / 删除后，申请单链接原样保留并显式标注，绝不静默清除 / 重绑定；</item>
/// <item><b>两个独立连接竞态</b>：并发「来源销售订单取消 vs 新申请链接该来源」与
/// 并发「同一待提交申请单链接 vs 断开来源」在兼容锁序下收敛为唯一一致结果。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class DepositApplySalesOrderSourceSqlServerTests
    : IClassFixture<FinanceDepositApplyLifecycleSqlServerFixture>
{
    private readonly FinanceDepositApplyLifecycleSqlServerFixture _fixture;

    public DepositApplySalesOrderSourceSqlServerTests(FinanceDepositApplyLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceDepositApplyLifecycleSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static FinanceDepositApplyController NewApplyController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceDepositApplyController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static FinanceApplySalesOrderCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<FinanceApplySalesOrderCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static FinanceApplySalesOrderSourceViewDto ViewOf(IActionResult result)
        => Assert.IsType<ApiResponse<FinanceApplySalesOrderSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static FinanceDepositApply Request(long customerId, decimal amount, long? salesOrderId,
        Currency currency = Currency.USD, decimal exchangeRate = 1m)
        => new()
        {
            ApplyDate = DateTime.Today.AddDays(-1),
            CustomerId = customerId,
            Amount = amount,
            SalesOrderId = salesOrderId,
            Currency = currency,
            ExchangeRate = exchangeRate,
            BankAccount = "RACE-BANK",
            Payee = "并发收款方",
            Reason = "并发事由",
            Remark = "并发备注"
        };

    private async Task<(bool Success, string Error)> TryApplyActionAsync(
        long? userId, Func<FinanceDepositApplyController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewApplyController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelOrderAsync(long? userId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var controller = new SalesOrderController(db, new DocumentNumberService(db));
            SetUser(controller, userId);
            var result = await controller.Cancel(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接在同一起点同时发起动作（各自独立 DbContext / 连接 / 事务）。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    // ==================== 1. 真实授权：既有「定金申请单」+「销售订单」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-deposit")]
    [InlineData("no-sales-order")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "候选权限客户");
        var userId = await SeedOperatorAsync(db, customerId, scenario);
        await SeedOrderAsync(db, $"SO-PERM-{Tag()}", customerId);

        var ctl = NewApplyController(db, scenario == "missing" ? null : userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderCandidates(customerId, "USD", null, 0, 0));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 候选：精确客户 / 状态 / 币种 / 范围（真实 SQL） ====================

    [Fact]
    public async Task Candidates_return_exact_customer_scope_state_and_currency()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "候选客户A");
        var customerB = await SeedCustomerAsync(db, "候选客户B");
        var userId = await SeedOperatorAsync(db, customerA, "ok");

        var eligible = await SeedOrderAsync(db, $"SO-ELIG-{Tag()}", customerA, Currency.USD);
        var cancelled = await SeedOrderAsync(db, $"SO-CAN-{Tag()}", customerA, Currency.USD, DocumentStatus.Cancelled);
        var mismatch = await SeedOrderAsync(db, $"SO-MIS-{Tag()}", customerA, Currency.CNY);
        var deleted = await SeedOrderAsync(db, $"SO-DEL-{Tag()}", customerA, Currency.USD);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();
        await SeedOrderAsync(db, $"SO-FOREIGN-{Tag()}", customerB, Currency.USD);

        var ctl = NewApplyController(db, userId);
        var page = Candidates(await ctl.GetSalesOrderCandidates(customerA, "USD", null, 0, 0));

        Assert.Equal(3, page.Total);
        Assert.Contains(page.Items, i => i.SalesOrderId == eligible.Id && i.Eligible && i.Currency == "USD");
        Assert.Contains(page.Items, i => i.SalesOrderId == cancelled.Id && !i.Eligible
            && i.IneligibleReason.Contains("已取消"));
        Assert.Contains(page.Items, i => i.SalesOrderId == mismatch.Id && !i.Eligible
            && i.IneligibleReason.Contains("币种") && i.Currency == "CNY");
        Assert.DoesNotContain(page.Items, i => i.SalesOrderId == deleted.Id);
        Assert.All(page.Items, i => Assert.Equal(customerA, i.CustomerId));

        // 越范围 / 非法客户一律 fail closed（不泄露范围外客户订单）
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderCandidates(customerB, null, null, 0, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderCandidates(0, null, null, 0, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 3. 伪造 / 取消 / 跨客户 / 币种不一致来源：最终保存必须复核 ====================

    [Fact]
    public async Task Create_rejects_forged_cancelled_foreign_and_currency_mismatch_source_without_side_effects()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "伪造来源客户A");
        var customerB = await SeedCustomerAsync(db, "伪造来源客户B");
        var userId = await SeedOperatorAsync(db, customerA, "ok");
        var cancelled = await SeedOrderAsync(db, $"SO-CAN-{Tag()}", customerA, Currency.USD, DocumentStatus.Cancelled);
        var foreign = await SeedOrderAsync(db, $"SO-FOR-{Tag()}", customerB, Currency.USD);
        var mismatch = await SeedOrderAsync(db, $"SO-MIS-{Tag()}", customerA, Currency.CNY);

        var ctl = NewApplyController(db, userId);
        var appliesBefore = await db.FinanceDepositApplies.CountAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, 100m, 987654321L)));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, 100m, cancelled.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, 100m, foreign.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, 100m, mismatch.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Equal(appliesBefore, await db.FinanceDepositApplies.CountAsync());
    }

    // ==================== 4. 已存储来源：取消 / 删除后仍只读可读、显式标注 ====================

    [Fact]
    public async Task Stored_source_preserved_when_source_cancelled_then_deleted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "历史来源客户");
        var userId = await SeedOperatorAsync(db, customerA, "ok");
        var order = await SeedOrderAsync(db, $"SO-HIST-{Tag()}", customerA, Currency.USD);
        var apply = await SeedApplyAsync(db, $"DJ-HIST-{Tag()}", customerA, 1000m, order.Id);

        var ctl = NewApplyController(db, userId);
        var live = ViewOf(await ctl.GetStoredSalesOrderSource(apply.Id));
        Assert.True(live.Linked);
        Assert.True(live.EligibleForNewLink);

        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var cancelled = ViewOf(await ctl.GetStoredSalesOrderSource(apply.Id));
        Assert.Equal(order.Id, cancelled.SalesOrderId);
        Assert.False(cancelled.EligibleForNewLink);
        Assert.False(cancelled.Unavailable);
        Assert.Contains("已取消", cancelled.Annotation);

        var gone = await SeedOrderAsync(db, $"SO-GONE-{Tag()}", customerA, Currency.USD);
        var a2 = await SeedApplyAsync(db, $"DJ-GONE-{Tag()}", customerA, 1000m, gone.Id);
        gone.IsDeleted = true;
        await db.SaveChangesAsync();

        var unavailable = ViewOf(await ctl.GetStoredSalesOrderSource(a2.Id));
        Assert.True(unavailable.Linked);
        Assert.True(unavailable.Unavailable);
        Assert.Equal(gone.Id, unavailable.SalesOrderId);
        Assert.Contains("不可用", unavailable.Annotation);

        // 历史链接原样保留（绝不静默清除 / 重绑定）
        Assert.Equal(order.Id, await db.FinanceDepositApplies.Where(a => a.Id == apply.Id)
            .Select(a => a.SalesOrderId).SingleAsync());
        Assert.Equal(gone.Id, await db.FinanceDepositApplies.Where(a => a.Id == a2.Id)
            .Select(a => a.SalesOrderId).SingleAsync());

        // 显式断开：留空（0）→ 服务端归一化为 null（历史未关联语义）；
        // 需用一张来源仍有效且处于「待提交」的申请单（已取消来源会被 ERP-381 持久化护栏拒绝修改）
        var liveOrder = await SeedOrderAsync(db, $"SO-UNLINK-{Tag()}", customerA, Currency.USD);
        var pending = await SeedApplyAsync(db, $"DJ-UNLINK-{Tag()}", customerA, 1000m, liveOrder.Id,
            DocumentStatus.Pending);
        Assert.IsType<OkObjectResult>(await ctl.Update(pending.Id, Request(customerA, 1000m, 0)));
        var unlinked = ViewOf(await ctl.GetStoredSalesOrderSource(pending.Id));
        Assert.False(unlinked.Linked);
        Assert.Null(await db.FinanceDepositApplies.Where(a => a.Id == pending.Id)
            .Select(a => a.SalesOrderId).SingleAsync());
    }

    // ==================== 5. 两个独立连接竞态（来源销售订单行锁 + 申请单行锁串行化） ====================

    [Fact]
    public async Task Race_source_order_cancel_versus_new_link_converges()
    {
        Guard();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            customerId = await SeedCustomerAsync(seed, "并发取消客户");
            userId = await SeedOperatorAsync(seed, customerId, "ok");
            orderId = (await SeedOrderAsync(seed, $"SO-RACE1-{Tag()}", customerId)).Id;
        }

        // 取消订单与新建「链接该订单」的申请单都先取同一把来源销售订单行锁，串行化后唯一一致
        var results = await RaceAsync(
            () => TryCancelOrderAsync(userId, orderId),
            () => TryApplyActionAsync(userId, ctl => ctl.Create(Request(customerId, 100m, orderId))));

        var cancel = results[0];
        var link = results[1];

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var linked = await verify.FinanceDepositApplies.AsNoTracking()
            .Where(a => a.SalesOrderId == orderId).ToListAsync();

        Assert.True(cancel.Success || link.Success, cancel.Error + "|" + link.Error);
        if (cancel.Success)
        {
            // 取消先赢：订单已取消；新链接被显式拒绝，绝不留下半成品链接
            Assert.Equal(DocumentStatus.Cancelled, order.Status);
            Assert.False(link.Success, link.Error);
            Assert.Empty(linked);
        }
        else
        {
            // 链接先赢：存在未删除且未取消的定金申请单以本单为显式来源 → 取消被证据护栏拒绝
            Assert.Contains("定金申请单", cancel.Error);
            Assert.Equal(DocumentStatus.Approved, order.Status);
            Assert.True(link.Success, link.Error);
            Assert.Single(linked);
            Assert.Equal(customerId, linked[0].CustomerId);
        }
    }

    [Fact]
    public async Task Race_two_connections_link_versus_unlink_converges()
    {
        Guard();
        long userId, customerId, orderId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            customerId = await SeedCustomerAsync(seed, "并发链接客户");
            userId = await SeedOperatorAsync(seed, customerId, "ok");
            orderId = (await SeedOrderAsync(seed, $"SO-RACE2-{Tag()}", customerId)).Id;
            applyId = (await SeedApplyAsync(seed, $"DJ-RACE2-{Tag()}", customerId, 1000m, null,
                DocumentStatus.Pending)).Id;
        }

        // 两个独立连接并发「链接来源」与「断开来源」同一待提交申请单
        var results = await RaceAsync(
            () => TryApplyActionAsync(userId, ctl => ctl.Update(applyId, Request(customerId, 1000m, orderId))),
            () => TryApplyActionAsync(userId, ctl => ctl.Update(applyId, Request(customerId, 1000m, 0))));

        // 行锁串行化：至少一方成功；失败方必须是显式并发冲突，绝不静默写坏数据
        Assert.True(results[0].Success || results[1].Success, results[0].Error + "|" + results[1].Error);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.FinanceDepositApplies.AsNoTracking().SingleAsync(a => a.Id == applyId);

        // 最终状态唯一一致：要么链接到来源订单，要么显式未关联；客户 / 状态 / 金额不被破坏
        Assert.Equal(customerId, stored.CustomerId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(1000m, stored.Amount);
        Assert.True(stored.SalesOrderId is null || stored.SalesOrderId == orderId,
            "最终来源链接必须是链接或显式未关联之一的唯一一致结果");
    }

    // ==================== 6. 种子（既有菜单 / 业务员数据范围，不新增权限模型） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-FAS-{Tag()}", CustomerName = name, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<SalesOrder> SeedOrderAsync(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today.AddDays(-3), CustomerId = customerId,
            Currency = currency, TotalAmount = 5000m, Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<FinanceDepositApply> SeedApplyAsync(
        ErpDbContext db, string applyNo, long customerId, decimal amount, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Approved, Currency currency = Currency.USD)
    {
        var apply = new FinanceDepositApply
        {
            ApplyNo = applyNo, ApplyDate = DateTime.Today.AddDays(-3), SalesOrderId = salesOrderId,
            CustomerId = customerId, Amount = amount, Currency = currency, ExchangeRate = 1m,
            BankAccount = "ORIGINAL-BANK", Payee = "原始收款方", Reason = "原始事由",
            Remark = "原始备注", Status = status
        };
        db.FinanceDepositApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    /// <summary>
    /// 播种一个真实登录账号：既有「定金申请单」（与可选「销售订单」）菜单授权 + 业务员员工映射（把范围内客户分配给
    /// 它的员工，数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, long inScopeCustomerId, string scenario)
    {
        var code = $"fas-op-{Tag()}";
        var role = new SysRole { RoleCode = $"FAS-OP-{Tag()}", RoleName = "定金来源操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "定金来源操作员",
            Status = scenario == "disabled" ? UserStatus.Disabled : UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (scenario is not ("no-deposit" or "missing"))
        {
            var menu = await EnsureMenuAsync(db, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
                FinanceDepositApplyLifecycleRules.RequiredMenuText, "/finance/deposit-applies", 70);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        if (scenario is not ("no-sales-order" or "missing"))
        {
            var menu = await EnsureMenuAsync(db, FinanceDepositApplyLifecycleRules.SourceRequiredMenuCode,
                FinanceDepositApplyLifecycleRules.SourceRequiredMenuText, "/sales/orders", 40);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code, string name,
        string path, int sortOrder)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            SortOrder = sortOrder, MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class DepositApplySalesOrderSourceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            FinanceDepositApplyLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}




