using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
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
/// ERP-388 客诉单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「客诉单」（<c>complaint</c>）与「销售订单」（<c>sales-order</c>）菜单授权 +
/// 既有业务员数据范围口径驱动真实 <see cref="FinanceComplaintController"/>，验证实时启用身份、菜单撤销 / 禁用、
/// 客户范围（own / foreign / unmapped）、可空来源销售订单（wrong-customer / deleted / cancelled 一律拒绝，
/// 拒绝即回滚无半成品）与文本长度契约；</item>
/// <item><b>来源状态</b>：来源销售订单被取消后，客诉历史与其显式链接保持只读可读、绝不静默重绑定；</item>
/// <item><b>两个独立连接竞态</b>：并发「修改 vs 提交」经客诉单行锁串行化后给出唯一一致结果；
/// 并发「来源销售订单取消 vs 新客诉链接」在「先来源订单行、后客诉单行」的兼容锁序下收敛为一致可用来源结果，
/// 且取消绝不因客诉存在而被拒绝。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class FinanceComplaintLifecycleSqlServerTests
    : IClassFixture<FinanceComplaintLifecycleSqlServerFixture>
{
    private readonly FinanceComplaintLifecycleSqlServerFixture _fixture;

    public FinanceComplaintLifecycleSqlServerTests(FinanceComplaintLifecycleSqlServerFixture fixture)
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

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinanceComplaintController NewComplaintController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceComplaintController(db, new DocumentNumberService(db));
        SetUser(controller, userId);
        return controller;
    }

    private static SalesOrderController NewSalesOrderController(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
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
            var result = await NewSalesOrderController(db, userId).Cancel(orderId);
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(
        ErpDbContext db, string code, string name, int status = 1, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<SalesOrder> SeedOrderAsync(
        ErpDbContext db, string orderNo, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
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
        ErpDbContext db, string complaintNo, long customerId, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var complaint = new FinanceComplaint
        {
            ComplaintNo = complaintNo,
            ComplaintDate = DateTime.Today.AddDays(-1),
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = "原始描述",
            ResponsibleDept = "质检部",
            HandleResult = "原始处理结果",
            Remark = "原始备注",
            Status = status
        };
        db.FinanceComplaints.Add(complaint);
        await db.SaveChangesAsync();
        return complaint;
    }

    /// <summary>
    /// 播种一个真实登录账号：既有「客诉单」/「销售订单」菜单授权 + 业务员员工映射（把范围内客户分配给它的员工，
    /// 数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId, long EmployeeId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withComplaintMenu = true,
        bool withSalesOrderMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp388-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            DisplayName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "客诉操作角色",
            RoleCode = $"Erp388Op-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withComplaintMenu)
            await GrantMenuAsync(db, role.Id, FinanceComplaintLifecycleRules.RequiredMenuCode);
        if (withSalesOrderMenu)
            await GrantMenuAsync(db, role.Id, FinanceComplaintLifecycleRules.SourceRequiredMenuCode);

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return (user.Id, role.Id, employee.Id);
    }

    /// <summary>授予既有种子菜单（不新增菜单 / 权限模型）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private async Task<FinanceComplaint?> ReloadComplaintAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceComplaints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    private async Task<SalesOrder?> ReloadOrderAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
    }

    private async Task<int> ComplaintCountForOrderAsync(long salesOrderId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceComplaints.AsNoTracking()
            .CountAsync(c => c.SalesOrderId == salesOrderId && !c.IsDeleted);
    }

    /// <summary>以独立请求 DbContext 调用真实控制器并断言业务拒绝（fail closed）。</summary>
    private async Task<BusinessException> ActDeniedAsync(
        long? userId, Func<FinanceComplaintController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        return await Assert.ThrowsAsync<BusinessException>(
            () => action(NewComplaintController(db, userId)));
    }

    private static FinanceComplaint NewRequest(
        long customerId, long? salesOrderId = null, string? description = null)
        => new()
        {
            ComplaintDate = DateTime.Today,
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = description ?? "新客诉描述",
            ResponsibleDept = "质检部",
            HandleResult = "待处理",
            Remark = "新备注"
        };

    // ==================== 1. 实时身份 / 菜单授权 / 客户数据范围（own / foreign / unmapped / revoked / disabled） ====================

    [Fact]
    public async Task ActualController_OwnCustomer_菜单齐全_创建与状态流转可用()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_OWN_{tag}", "本人客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            orderId = (await SeedOrderAsync(seed, $"SO-E388-OWN-{tag}", customer.Id)).Id;
        }

        var create = await TryActionAsync(userId, ctl => ctl.Create(NewRequest(customerId, orderId)));
        Assert.True(create.Success, create.Error);

        long complaintId;
        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.FinanceComplaints.AsNoTracking()
                .FirstAsync(c => c.SalesOrderId == orderId && !c.IsDeleted);
            complaintId = stored.Id;
            Assert.Equal(customerId, stored.CustomerId);
            Assert.Equal(DocumentStatus.Pending, stored.Status);
            Assert.StartsWith("KS", stored.ComplaintNo);

            var controller = NewComplaintController(verify, userId);
            Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
            Assert.IsType<OkObjectResult>(await controller.GetById(stored.Id));
            Assert.IsType<OkObjectResult>(await controller.Submit(stored.Id));
            Assert.IsType<OkObjectResult>(await controller.Approve(stored.Id));
        }

        Assert.Equal(DocumentStatus.Approved, (await ReloadComplaintAsync(complaintId))!.Status);
    }

    [Fact]
    public async Task ActualController_ForeignCustomer_越界拒绝且不改写()
    {
        Guard();
        var tag = Tag();
        long userId, foreignCustomerId, foreignComplaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var own = await SeedCustomerAsync(seed, $"INT_E388_FOWN_{tag}", "本人客户");
            var foreign = await SeedCustomerAsync(seed, $"INT_E388_FFOR_{tag}", "范围外客户");
            userId = (await SeedOperatorAsync(seed, own.Id)).UserId;
            foreignCustomerId = foreign.Id;
            foreignComplaintId = (await SeedComplaintAsync(
                seed, $"KS-E388-FOR-{tag}", foreign.Id)).Id;
        }

        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.GetById(foreignComplaintId))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.Submit(foreignComplaintId))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.Delete(foreignComplaintId))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.Create(NewRequest(foreignCustomerId)))).Code);

        var foreign2 = await ReloadComplaintAsync(foreignComplaintId);
        Assert.Equal(DocumentStatus.Pending, foreign2!.Status);
        Assert.False(foreign2.IsDeleted);
        Assert.Equal(foreignCustomerId, foreign2.CustomerId);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(
                await NewComplaintController(verify, userId).GetPaged(new PageQuery(), null));
            // 仅保留播种的 1 条范围外客诉：被拒创建未新增任何客诉（无半成品）
            Assert.Equal(1, await verify.FinanceComplaints.AsNoTracking()
                .CountAsync(c => c.CustomerId == foreignCustomerId));
            Assert.Equal(0, await verify.FinanceComplaints.AsNoTracking()
                .CountAsync(c => c.CustomerId == foreignCustomerId && c.SalesOrderId != null));
        }
    }

    [Fact]
    public async Task ActualController_UnmappedUser_不可见任何客诉数据()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, complaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_UNM_{tag}", "未映射客户");
            customerId = customer.Id;
            complaintId = (await SeedComplaintAsync(seed, $"KS-E388-UNM-{tag}", customer.Id)).Id;

            // 播种一个有「客诉单」菜单但**未映射业务员**的受限账号（可见客户集合为空，fail closed）
            var code = $"erp388-unmapped-{Guid.NewGuid():N}";
            var user = new SysUser
            {
                UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt",
                Status = UserStatus.Enabled
            };
            seed.SysUsers.Add(user);
            await seed.SaveChangesAsync();
            var role = new SysRole
            {
                RoleName = "客诉未映射角色", RoleCode = $"Erp388Unm-{Guid.NewGuid():N}", IsSystem = false
            };
            seed.SysRoles.Add(role);
            await seed.SaveChangesAsync();
            seed.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await seed.SaveChangesAsync();
            await GrantMenuAsync(seed, role.Id, FinanceComplaintLifecycleRules.RequiredMenuCode);
            userId = user.Id;
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var result = Assert.IsType<OkObjectResult>(
                await NewComplaintController(verify, userId).GetPaged(new PageQuery(), null));
            var payload = Assert.IsType<ApiResponse<PagedResult<FinanceComplaint>>>(result.Value);
            Assert.Equal(0, payload.Data!.Total);
        }
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.GetById(complaintId))).Code);
        Assert.Equal(customerId, (await ReloadComplaintAsync(complaintId))!.CustomerId);
    }

    [Fact]
    public async Task ActualController_菜单撤销_全部路由拒绝且不产生客诉()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId, complaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_REV_{tag}", "撤销菜单客户");
            customerId = customer.Id;
            var op = await SeedOperatorAsync(seed, customer.Id);
            userId = op.UserId;
            orderId = (await SeedOrderAsync(seed, $"SO-E388-REV-{tag}", customer.Id)).Id;
            complaintId = (await SeedComplaintAsync(seed, $"KS-E388-REV-{tag}", customer.Id)).Id;
            await RevokeMenusAsync(seed, op.RoleId);
        }

        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.GetPaged(new PageQuery(), null))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.GetById(complaintId))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.Create(NewRequest(customerId, orderId)))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.Cancel(complaintId))).Code);

        Assert.Equal(0, await ComplaintCountForOrderAsync(orderId));
        var stored = await ReloadComplaintAsync(complaintId);
        Assert.Equal(DocumentStatus.Pending, stored!.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task ActualController_账号禁用_拒绝访问()
    {
        Guard();
        var tag = Tag();
        long userId, complaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_DIS_{tag}", "禁用账号客户");
            userId = (await SeedOperatorAsync(seed, customer.Id, status: UserStatus.Disabled)).UserId;
            complaintId = (await SeedComplaintAsync(seed, $"KS-E388-DIS-{tag}", customer.Id)).Id;
        }

        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.GetPaged(new PageQuery(), null))).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.GetById(complaintId))).Code);
        Assert.Equal(DocumentStatus.Pending, (await ReloadComplaintAsync(complaintId))!.Status);
    }

    // ==================== 2. 可空来源销售订单（wrong-customer / deleted / cancelled + 回滚） ====================

    [Fact]
    public async Task Link_WrongCustomer_Deleted_Cancelled_全部拒绝且无半成品()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, otherCustomerId, wrongCustomerOrderId, deletedOrderId, cancelledOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_LNK_{tag}", "链接客户");
            var other = await SeedCustomerAsync(seed, $"INT_E388_OTH_{tag}", "他客户");
            customerId = customer.Id;
            otherCustomerId = other.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;

            wrongCustomerOrderId = (await SeedOrderAsync(
                seed, $"SO-E388-WRONG-{tag}", other.Id)).Id;
            deletedOrderId = (await SeedOrderAsync(seed, $"SO-E388-DEL-{tag}", customer.Id)).Id;
            cancelledOrderId = (await SeedOrderAsync(
                seed, $"SO-E388-CAN-{tag}", customer.Id, DocumentStatus.Cancelled)).Id;

            var deleted = await seed.SalesOrders.SingleAsync(o => o.Id == deletedOrderId);
            deleted.IsDeleted = true;
            await seed.SaveChangesAsync();
        }

        // 他客户来源 → 规则冲突
        Assert.Equal(ErrorCodes.RuleConflict,
            (await ActDeniedAsync(userId, ctl => ctl.Create(NewRequest(customerId, wrongCustomerOrderId)))).Code);
        // 已删除来源 → 不存在
        Assert.Equal(ErrorCodes.NotFound,
            (await ActDeniedAsync(userId, ctl => ctl.Create(NewRequest(customerId, deletedOrderId)))).Code);
        // 已取消来源 → 规则冲突
        var cancelledError = await ActDeniedAsync(
            userId, ctl => ctl.Create(NewRequest(customerId, cancelledOrderId)));
        Assert.Equal(ErrorCodes.RuleConflict, cancelledError.Code);
        Assert.Contains("已取消", cancelledError.Message);

        // 任一被拒创建都不得留下半成品
        Assert.Equal(0, await ComplaintCountForOrderAsync(wrongCustomerOrderId));
        Assert.Equal(0, await ComplaintCountForOrderAsync(deletedOrderId));
        Assert.Equal(0, await ComplaintCountForOrderAsync(cancelledOrderId));
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.FinanceComplaints.AsNoTracking()
                .CountAsync(c => c.CustomerId == customerId));
        }
        Assert.True(otherCustomerId > 0);
    }

    [Fact]
    public async Task Link_仅客诉菜单_有来源拒绝但未关联可创建()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_MENU_{tag}", "仅客诉菜单客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id, withSalesOrderMenu: false)).UserId;
            orderId = (await SeedOrderAsync(seed, $"SO-E388-MENU-{tag}", customer.Id)).Id;
        }

        // 提供来源 → 缺「销售订单」菜单授权 → 权限不足，且不落库
        Assert.Equal(ErrorCodes.Forbidden,
            (await ActDeniedAsync(userId, ctl => ctl.Create(NewRequest(customerId, orderId)))).Code);
        Assert.Equal(0, await ComplaintCountForOrderAsync(orderId));

        // 未关联来源 → 不要求来源菜单，保留历史未关联语义
        var unlinked = await TryActionAsync(userId, ctl => ctl.Create(NewRequest(customerId)));
        Assert.True(unlinked.Success, unlinked.Error);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.FinanceComplaints.AsNoTracking()
            .SingleAsync(c => c.CustomerId == customerId && c.SalesOrderId == null);
        Assert.Null(stored.SalesOrderId);
    }

    [Fact]
    public async Task CancelledSource_客诉历史保持可读且不阻断来源取消()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId, complaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_HIS_{tag}", "历史链接客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            orderId = (await SeedOrderAsync(seed, $"SO-E388-HIS-{tag}", customer.Id)).Id;
            complaintId = (await SeedComplaintAsync(
                seed, $"KS-E388-HIS-{tag}", customer.Id, orderId)).Id;
        }

        // 来源销售订单取消绝不因客诉存在而被拒绝
        var cancel = await TryCancelOrderAsync(userId, orderId);
        Assert.True(cancel.Success, cancel.Error);
        Assert.Equal(DocumentStatus.Cancelled, (await ReloadOrderAsync(orderId))!.Status);

        // 客诉历史与显式链接原样保留、仍可读，并带显式「来源已取消 / 只读」状态
        await using var verify = _fixture.CreateDbContext();
        var result = Assert.IsType<OkObjectResult>(
            await NewComplaintController(verify, userId).GetById(complaintId));
        var payload = Assert.IsType<ApiResponse<FinanceComplaint>>(result.Value);
        Assert.Equal(orderId, payload.Data!.SalesOrderId);
        Assert.Contains("已取消", payload.Message);
        Assert.Contains("只读", payload.Message);
        Assert.True(customerId > 0);
    }

    // ==================== 3. 两个独立连接竞态 ====================

    [Fact]
    public async Task Race_Edit_Versus_Submit_串行化一致结果()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, complaintId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_RS1_{tag}", "并发客户1");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            complaintId = (await SeedComplaintAsync(seed, $"KS-E388-RS1-{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryActionAsync(userId,
                ctl => ctl.Update(complaintId, NewRequest(customerId, null, "并发修改"))),
            () => TryActionAsync(userId, ctl => ctl.Submit(complaintId)));

        var edit = results[0];
        var submit = results[1];

        // 同一客诉单行竞争在行锁下串行化：至少一个成功，失败方不得覆盖赢家。
        Assert.True(edit.Success || submit.Success, $"edit={edit.Error}; submit={submit.Error}");
        var applied = await ReloadComplaintAsync(complaintId);
        Assert.Equal(submit.Success ? DocumentStatus.Submitted : DocumentStatus.Pending, applied!.Status);
        Assert.Equal(edit.Success ? "并发修改" : "原始描述", applied.Description);
        Assert.Equal(customerId, applied.CustomerId);
        Assert.False(applied.IsDeleted);
    }

    [Fact]
    public async Task Race_来源取消_Versus_新链接_一致可用来源结果()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E388_RS2_{tag}", "并发来源客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            orderId = (await SeedOrderAsync(seed, $"SO-E388-RS2-{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryCancelOrderAsync(userId, orderId),
            () => TryActionAsync(userId, ctl => ctl.Create(NewRequest(customerId, orderId, "并发链接"))));

        var cancel = results[0];
        var link = results[1];

        // 取消绝不因客诉存在而被拒绝（客诉不是履约 / 收款证据）
        Assert.True(cancel.Success, cancel.Error);

        // 来源最终为已取消：新链接要么在取消前成功（历史链接只读保留），要么在取消后被显式拒绝。
        Assert.Equal(DocumentStatus.Cancelled, (await ReloadOrderAsync(orderId))!.Status);
        var linkedCount = await ComplaintCountForOrderAsync(orderId);
        if (link.Success)
        {
            Assert.Equal(1, linkedCount);
            await using var verify = _fixture.CreateDbContext();
            var stored = await verify.FinanceComplaints.AsNoTracking().SingleAsync(c => c.SalesOrderId == orderId);
            Assert.Equal(customerId, stored.CustomerId);
            Assert.Equal("并发链接", stored.Description);
        }
        else
        {
            // 被拒链接绝不留下半成品，且拒绝必须是「来源已取消」这类显式状态
            Assert.Equal(0, linkedCount);
            Assert.Contains("已取消", link.Error);
        }
    }
}

/// <summary>
/// ERP-388 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class FinanceComplaintLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP388";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-388] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-388] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceComplaintLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => FinanceComplaintLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}
