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
/// ERP-389 客诉单来源销售订单候选 / 已存储来源 + 表单显式选择的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，复用 <see cref="FinanceComplaintLifecycleSqlServerFixture"/>）。
/// <list type="number">
/// <item><b>真实控制器</b>：既有「客诉单」+「销售订单」菜单与既有业务员数据范围驱动真实
/// <see cref="FinanceComplaintController"/>，验证候选只含<b>精确客户</b>名下「未删除」订单、已取消显式标记不可选、
/// 范围外 / 越权一律 fail closed；</item>
/// <item><b>伪造来源</b>：候选选择不等于授权——最终保存按 ERP-388 生命周期规则复核精确来源，
/// 伪造 / 已取消 / 跨客户来源一律拒绝且不消耗单号、不落半成品；</item>
/// <item><b>历史只读</b>：来源事后取消 / 删除后，客诉单链接原样保留并显式标注，绝不静默清除 / 重绑定；</item>
/// <item><b>两个独立连接竞态</b>：并发「来源销售订单取消 vs 新客诉链接该来源」与
/// 并发「同一待提交客诉单链接 vs 断开来源」在兼容锁序下收敛为唯一一致结果。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ComplaintSalesOrderSourceSqlServerTests
    : IClassFixture<FinanceComplaintLifecycleSqlServerFixture>
{
    private readonly FinanceComplaintLifecycleSqlServerFixture _fixture;

    public ComplaintSalesOrderSourceSqlServerTests(FinanceComplaintLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceComplaintLifecycleSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static FinanceComplaintController NewComplaintController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceComplaintController(db, new DocumentNumberService(db));
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

    private static ComplaintSalesOrderCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<ComplaintSalesOrderCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static FinanceComplaint Request(long customerId, long? salesOrderId, string description = "并发链接")
        => new()
        {
            ComplaintDate = DateTime.Today.AddDays(-1),
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = description,
            ResponsibleDept = "质检部",
            HandleResult = "处理结果",
            Remark = "备注"
        };

    // ==================== 1. 真实授权：既有「客诉单」+「销售订单」菜单，无匿名 / 管理员兜底 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("no-complaint")]
    [InlineData("no-sales-order")]
    [InlineData("disabled")]
    public async Task Candidates_deny_identities_without_existing_permissions(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "候选权限客户");
        var userId = await SeedOperatorAsync(db, customerId, scenario);
        await SeedOrderAsync(db, $"SO-PERM-{Tag()}", customerId);

        var ctl = NewComplaintController(db, scenario == "missing" ? null : userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderCandidates(customerId, null, 0, 0));
        Assert.Equal(scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 候选：精确客户 / 状态 / 范围（真实 SQL） ====================

    [Fact]
    public async Task Candidates_return_exact_customer_scope_and_eligible_status()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "候选客户A");
        var customerB = await SeedCustomerAsync(db, "候选客户B");
        var userId = await SeedOperatorAsync(db, customerA, "ok");

        var eligible = await SeedOrderAsync(db, $"SO-ELIG-{Tag()}", customerA, DocumentStatus.Approved);
        var cancelled = await SeedOrderAsync(db, $"SO-CAN-{Tag()}", customerA, DocumentStatus.Cancelled);
        var deleted = await SeedOrderAsync(db, $"SO-DEL-{Tag()}", customerA, DocumentStatus.Approved);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();
        await SeedOrderAsync(db, $"SO-FOREIGN-{Tag()}", customerB, DocumentStatus.Approved);

        var ctl = NewComplaintController(db, userId);
        var page = Candidates(await ctl.GetSalesOrderCandidates(customerA, null, 0, 0));

        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, i => i.SalesOrderId == eligible.Id && i.Eligible);
        Assert.Contains(page.Items, i => i.SalesOrderId == cancelled.Id && !i.Eligible
            && i.IneligibleReason.Contains("已取消"));
        Assert.DoesNotContain(page.Items, i => i.SalesOrderId == deleted.Id);
        Assert.All(page.Items, i => Assert.Equal(customerA, i.CustomerId));

        // 越范围 / 非法客户一律 fail closed（不泄露范围外客户订单）
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderCandidates(customerB, null, 0, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSalesOrderCandidates(0, null, 0, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 3. 伪造 / 取消 / 跨客户来源：最终保存必须复核（候选不等于授权） ====================

    [Fact]
    public async Task Create_rejects_forged_cancelled_and_foreign_source_without_side_effects()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "伪造来源客户A");
        var customerB = await SeedCustomerAsync(db, "伪造来源客户B");
        var userId = await SeedOperatorAsync(db, customerA, "ok");
        var cancelled = await SeedOrderAsync(db, $"SO-CAN-{Tag()}", customerA, DocumentStatus.Cancelled);
        var foreign = await SeedOrderAsync(db, $"SO-FOR-{Tag()}", customerB, DocumentStatus.Approved);

        var ctl = NewComplaintController(db, userId);
        var complaintsBefore = await db.FinanceComplaints.CountAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, 987654321L)));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, cancelled.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA, foreign.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Equal(complaintsBefore, await db.FinanceComplaints.CountAsync());
    }

    // ==================== 4. 已存储来源：取消 / 删除后仍只读可读、显式标注 ====================

    [Fact]
    public async Task Stored_source_preserved_when_source_cancelled_then_deleted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "历史来源客户");
        var userId = await SeedOperatorAsync(db, customerA, "ok");
        var order = await SeedOrderAsync(db, $"SO-HIST-{Tag()}", customerA, DocumentStatus.Approved);
        var complaint = await SeedComplaintAsync(db, $"KS-HIST-{Tag()}", customerA, order.Id);

        var ctl = NewComplaintController(db, userId);
        var live = ViewOf(await ctl.GetStoredSalesOrderSource(complaint.Id));
        Assert.True(live.Linked);
        Assert.True(live.EligibleForNewLink);

        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var cancelled = ViewOf(await ctl.GetStoredSalesOrderSource(complaint.Id));
        Assert.Equal(order.Id, cancelled.SalesOrderId);
        Assert.False(cancelled.EligibleForNewLink);
        Assert.False(cancelled.Unavailable);
        Assert.Contains("已取消", cancelled.Annotation);

        var gone = await SeedOrderAsync(db, $"SO-GONE-{Tag()}", customerA, DocumentStatus.Approved);
        var c2 = await SeedComplaintAsync(db, $"KS-GONE-{Tag()}", customerA, gone.Id);
        gone.IsDeleted = true;
        await db.SaveChangesAsync();

        var unavailable = ViewOf(await ctl.GetStoredSalesOrderSource(c2.Id));
        Assert.True(unavailable.Linked);
        Assert.True(unavailable.Unavailable);
        Assert.Equal(gone.Id, unavailable.SalesOrderId);
        Assert.Contains("不可用", unavailable.Annotation);

        // 历史链接原样保留（绝不静默清除 / 重绑定）
        Assert.Equal(order.Id, await db.FinanceComplaints.Where(c => c.Id == complaint.Id)
            .Select(c => c.SalesOrderId).SingleAsync());
        Assert.Equal(gone.Id, await db.FinanceComplaints.Where(c => c.Id == c2.Id)
            .Select(c => c.SalesOrderId).SingleAsync());
    }

    // ==================== 5. 两个独立连接竞态 ====================

    [Fact]
    public async Task Race_source_cancellation_vs_complaint_link_converges()
    {
        Guard();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            customerId = await SeedCustomerAsync(seed, "并发取消客户");
            userId = await SeedOperatorAsync(seed, customerId, "ok");
            orderId = (await SeedOrderAsync(seed, $"SO-RACE1-{Tag()}", customerId)).Id;
        }

        var results = await RaceAsync(
            () => TryCancelOrderAsync(userId, orderId),
            () => TryActionAsync(userId, ctl => ctl.Create(Request(customerId, orderId))));

        var cancel = results[0];
        var link = results[1];

        // 取消绝不因客诉存在而被拒绝（客诉不是履约 / 收款证据）
        Assert.True(cancel.Success, cancel.Error);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Cancelled,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status);
        var linked = await verify.FinanceComplaints.AsNoTracking()
            .Where(c => c.SalesOrderId == orderId).ToListAsync();

        if (link.Success)
        {
            // 链接在取消前成功：历史链接保留（来源已取消，只读可读）
            Assert.Single(linked);
            Assert.Equal(customerId, linked[0].CustomerId);
        }
        else
        {
            // 取消先提交：新链接被显式拒绝，且绝不留下半成品
            Assert.Empty(linked);
            Assert.Contains("已取消", link.Error);
        }
    }

    [Fact]
    public async Task Race_two_connections_link_vs_unlink_converges()
    {
        Guard();
        long userId, customerId, orderId, complaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            customerId = await SeedCustomerAsync(seed, "并发链接客户");
            userId = await SeedOperatorAsync(seed, customerId, "ok");
            orderId = (await SeedOrderAsync(seed, $"SO-RACE2-{Tag()}", customerId)).Id;
            complaintId = (await SeedComplaintAsync(seed, $"KS-RACE2-{Tag()}", customerId, null)).Id;
        }

        // 两个独立连接并发「链接来源」与「断开来源」同一待提交客诉单
        var results = await RaceAsync(
            () => TryActionAsync(userId, ctl => ctl.Update(complaintId, Request(customerId, orderId))),
            () => TryActionAsync(userId, ctl => ctl.Update(complaintId, Request(customerId, 0))));

        // 行锁串行化：至少一方成功；失败方必须是显式并发冲突，绝不静默写坏数据
        Assert.True(results[0].Success || results[1].Success, results[0].Error + "|" + results[1].Error);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.FinanceComplaints.AsNoTracking().SingleAsync(c => c.Id == complaintId);

        // 最终状态唯一一致：要么链接到来源订单，要么显式未关联；客户 / 状态与审计不被破坏
        Assert.Equal(customerId, stored.CustomerId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.True(stored.SalesOrderId is null || stored.SalesOrderId == orderId,
            "最终来源链接必须是链接或显式未关联之一的唯一一致结果");
    }

    // ==================== 6. 结果解析 / 双连接 / 种子 ====================

    private static ComplaintSalesOrderSourceViewDto ViewOf(IActionResult result)
        => Assert.IsType<ApiResponse<ComplaintSalesOrderSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private async Task<(bool Success, string Error)> TryActionAsync(
        long? userId, Func<FinanceComplaintController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewComplaintController(db, userId));
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

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-COS-{Tag()}", CustomerName = name, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<SalesOrder> SeedOrderAsync(
        ErpDbContext db, string orderNo, long customerId, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-3),
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = 5000m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<FinanceComplaint> SeedComplaintAsync(
        ErpDbContext db, string complaintNo, long customerId, long? salesOrderId,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var complaint = new FinanceComplaint
        {
            ComplaintNo = complaintNo,
            ComplaintDate = DateTime.Today.AddDays(-1),
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = "历史描述",
            ResponsibleDept = "质检部",
            HandleResult = "历史处理结果",
            Remark = "历史备注",
            Status = status
        };
        db.FinanceComplaints.Add(complaint);
        await db.SaveChangesAsync();
        return complaint;
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

    /// <summary>
    /// 播种一个真实登录账号：既有「客诉单」/「销售订单」菜单授权 + 业务员员工映射（把范围内客户分配给它的员工，
    /// 数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, long inScopeCustomerId, string scenario)
    {
        var code = $"cos-op-{Tag()}";
        var role = new SysRole { RoleCode = $"COS-OP-{Tag()}", RoleName = "客诉来源操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "客诉来源操作员",
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

        if (scenario is not ("no-complaint" or "missing"))
        {
            var menu = await EnsureMenuAsync(db, FinanceComplaintLifecycleRules.RequiredMenuCode,
                FinanceComplaintLifecycleRules.RequiredMenuText, "/finance/complaints", 70);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        if (scenario is not ("no-sales-order" or "missing"))
        {
            var menu = await EnsureMenuAsync(db, FinanceComplaintLifecycleRules.SourceRequiredMenuCode,
                FinanceComplaintLifecycleRules.SourceRequiredMenuText, "/sales/orders", 40);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ComplaintSalesOrderSourceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            FinanceComplaintLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}





