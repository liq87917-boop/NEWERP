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
/// ERP-381 定金申请单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「定金申请单」（<c>deposit-apply</c>）菜单授权 + 既有业务员数据范围口径驱动真实
/// <see cref="FinanceDepositApplyController"/>，验证实时启用身份、权威客户范围、币种 / 金额 / 汇率与来源销售订单资格、
/// 被拒编辑不改写（含审计时间戳），以及与 <see cref="SalesOrderCancellationRules"/> 的协同（存在未删除且未取消的
/// 定金申请单即拒绝取消来源销售订单）；</item>
/// <item><b>两个独立连接竞态</b>：并发「编辑 vs 提交」「审核 vs 取消」「审核 vs 来源销售订单取消」经同一把
/// 来源销售订单行锁 + 定金申请单行锁（UPDLOCK/HOLDLOCK 语义）串行化后给出唯一一致结果：绝不出现陈旧来源、
/// 半成品写入或丢失更新，也不产生任何资金记账 / 结算 / 库存流水。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class FinanceDepositApplyLifecycleSqlServerTests
    : IClassFixture<FinanceDepositApplyLifecycleSqlServerFixture>
{
    private readonly FinanceDepositApplyLifecycleSqlServerFixture _fixture;

    public FinanceDepositApplyLifecycleSqlServerTests(FinanceDepositApplyLifecycleSqlServerFixture fixture)
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

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinanceDepositApplyController NewApplyController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceDepositApplyController(db, new DocumentNumberService(db));
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

    private async Task<(bool Success, string Error)> TryCancelSourceOrderAsync(long userId, long orderId)
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
    // ==================== 真实身份 / 既有菜单授权 / 数据范围种子 ====================

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

    private static async Task<SalesOrder> SeedSalesOrderAsync(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = 10000m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<FinanceDepositApply> SeedApplyAsync(
        ErpDbContext db, string applyNo, long customerId, decimal amount,
        Currency currency = Currency.CNY, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Approved, decimal exchangeRate = 1m)
    {
        var apply = new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            BankAccount = "ORIGINAL-BANK",
            Payee = "原始收款方",
            Reason = "原始事由",
            Remark = "原始备注",
            Status = status
        };
        db.FinanceDepositApplies.Add(apply);
        await db.SaveChangesAsync();
        return apply;
    }

    private static FinanceDepositApply NewApplyRequest(
        long customerId, decimal amount, long? salesOrderId = null, Currency currency = Currency.CNY,
        decimal exchangeRate = 1m)
        => new()
        {
            ApplyDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            SalesOrderId = salesOrderId,
            ExchangeRate = exchangeRate,
            BankAccount = "REQ-BANK",
            Payee = "请求收款方",
            Reason = "请求事由",
            Remark = "请求备注"
        };


    /// <summary>
    /// 播种一个真实登录账号：既有「定金申请单」（与可选「销售订单」）菜单授权 + 业务员员工映射
    /// （把范围内客户分配给它的员工，数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于
    /// fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withApplyMenu = true, bool withSalesOrderMenu = false,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp381-op-{Guid.NewGuid():N}";
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
            RoleName = "定金申请操作角色",
            RoleCode = $"Erp381Op-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withApplyMenu)
            await GrantMenuAsync(db, role.Id, FinanceDepositApplyLifecycleRules.RequiredMenuCode);
        if (withSalesOrderMenu)
            await GrantMenuAsync(db, role.Id, SalesOrderCancellationRules.RequiredMenuCode);

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return (user.Id, role.Id);
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

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    /// <summary>只读复核：用一条独立连接读取申请单权威行（不受被测事务 / 跟踪状态影响）。</summary>
    private async Task<FinanceDepositApply> ReloadApplyAsync(long applyId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceDepositApplies.AsNoTracking().SingleAsync(a => a.Id == applyId);
    }

    private async Task<DocumentStatus> ReloadOrderStatusAsync(long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => o.Status).SingleAsync();
    }

    /// <summary>既有记账 / 结算表条数合计：本护栏绝不产生任何新的资金记账 / 结算行。</summary>
    private async Task<int> PostingRowCountAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenses.AsNoTracking().CountAsync()
            + await db.FinanceContainerSettlements.AsNoTracking().CountAsync()
            + await db.FinanceBulkSettlements.AsNoTracking().CountAsync();
    }


    // ==================== 1. 真实身份 / 菜单 / 数据范围（own / foreign / revoked / disabled） ====================

    [Fact]
    public async Task Controller_OwnScopeAllowed_AndForeignScopeRevokedDisabledDenied()
    {
        Guard();
        var tag = Tag();
        long ownerId, foreignOperatorId, disabledOperatorId, revokedOperatorId, revokedRoleId;
        long ownerApplyId, foreignApplyId, disabledApplyId, revokedApplyId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var ownerCustomer = await SeedCustomerAsync(seed, $"INT_E381_OWN_{tag}", "本人客户");
            ownerId = (await SeedOperatorAsync(seed, ownerCustomer.Id)).UserId;
            ownerApplyId = (await SeedApplyAsync(seed, $"INT_E381_AOWN_{tag}", ownerCustomer.Id, 1000m)).Id;

            // 范围外操作员：其客户范围不含 ownerCustomer（申请单属于 ownerCustomer）
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E381_FOR_{tag}", "范围外客户");
            foreignOperatorId = (await SeedOperatorAsync(seed, foreignCustomer.Id)).UserId;
            foreignApplyId = (await SeedApplyAsync(seed, $"INT_E381_AFOR_{tag}", ownerCustomer.Id, 1000m)).Id;

            var disabledCustomer = await SeedCustomerAsync(seed, $"INT_E381_DIS_{tag}", "停用账号客户");
            disabledOperatorId = (await SeedOperatorAsync(seed, disabledCustomer.Id)).UserId;
            disabledApplyId = (await SeedApplyAsync(seed, $"INT_E381_ADIS_{tag}", disabledCustomer.Id, 1000m)).Id;
            await DisableUserAsync(seed, disabledOperatorId);

            var revokedCustomer = await SeedCustomerAsync(seed, $"INT_E381_REV_{tag}", "撤销授权客户");
            (revokedOperatorId, revokedRoleId) = await SeedOperatorAsync(seed, revokedCustomer.Id);
            revokedApplyId = (await SeedApplyAsync(seed, $"INT_E381_AREV_{tag}", revokedCustomer.Id, 1000m)).Id;
            await RevokeMenuAsync(seed, revokedRoleId);
        }

        var disabledBefore = await ReloadApplyAsync(disabledApplyId);
        var revokedBefore = await ReloadApplyAsync(revokedApplyId);

        await using (var db = _fixture.CreateDbContext())
        {
            // own：范围内客户可读、可流转
            Assert.IsType<OkObjectResult>(await NewApplyController(db, ownerId).GetById(ownerApplyId));
            Assert.IsType<OkObjectResult>(await NewApplyController(db, ownerId).Cancel(ownerApplyId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewApplyController(db, foreignOperatorId).GetById(foreignApplyId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewApplyController(db, disabledOperatorId).GetById(disabledApplyId));
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewApplyController(db, revokedOperatorId).GetById(revokedApplyId));
        }

        var disabled = await ReloadApplyAsync(disabledApplyId);
        var revoked = await ReloadApplyAsync(revokedApplyId);
        Assert.Equal(DocumentStatus.Approved, disabled.Status);
        Assert.Equal(disabledBefore.UpdatedAt, disabled.UpdatedAt);
        Assert.Equal(DocumentStatus.Approved, revoked.Status);
        Assert.Equal(revokedBefore.UpdatedAt, revoked.UpdatedAt);
    }


    [Fact]
    public async Task Controller_InvalidCurrencyAmountExchangeRateAndSource_Denied_NoWrite()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, cancelledOrderId, foreignOrderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E381_CINV_{tag}", "校验客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            cancelledOrderId = (await SeedSalesOrderAsync(
                seed, $"INT_E381_SOC_{tag}", customer.Id, status: DocumentStatus.Cancelled)).Id;
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E381_CFOR_{tag}", "他客户");
            foreignOrderId = (await SeedSalesOrderAsync(seed, $"INT_E381_SOF_{tag}", foreignCustomer.Id)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewApplyController(db, userId);

            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(new FinanceDepositApply
            {
                CustomerId = customerId,
                Amount = 100m,
                Currency = (Currency)77,
                ExchangeRate = 1m,
                ApplyDate = DateTime.Today
            }));
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => controller.Create(NewApplyRequest(customerId, 0.004m)));
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => controller.Create(NewApplyRequest(customerId, 100m, exchangeRate: 0m)));
            await AssertBusinessCodeAsync(ErrorCodes.NotFound,
                () => controller.Create(NewApplyRequest(customerId, 100m, salesOrderId: 99999999L)));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
                () => controller.Create(NewApplyRequest(customerId, 100m, salesOrderId: cancelledOrderId)));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
                () => controller.Create(NewApplyRequest(customerId, 100m, salesOrderId: foreignOrderId)));
        }

        await using (var db = _fixture.CreateDbContext())
            Assert.Equal(0, await db.FinanceDepositApplies.AsNoTracking().CountAsync(a => a.CustomerId == customerId));

        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Controller_DeniedEditsUnchanged_AndLinkedOrderCancelRefusedUntilApplyReleased()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E381_CLNK_{tag}", "联动客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id, withSalesOrderMenu: true)).UserId;
            orderId = (await SeedSalesOrderAsync(seed, $"INT_E381_SOL_{tag}", customer.Id)).Id;
            applyId = (await SeedApplyAsync(
                seed, $"INT_E381_ALNK_{tag}", customer.Id, 1000m,
                salesOrderId: orderId, status: DocumentStatus.Pending)).Id;
        }
        var before = await ReloadApplyAsync(applyId);
        var postingsBefore = await PostingRowCountAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewApplyController(db, userId);
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => controller.Update(applyId, NewApplyRequest(customerId, 0m)));
        }
        // A rolled-back request owns its context; the next HTTP request has fresh tracking state.
        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewApplyController(db, userId);
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => controller.Update(applyId,
                    NewApplyRequest(customerId, 300m, salesOrderId: orderId, exchangeRate: 0m)));
        }

        // 被拒编辑绝不改写原始字段（含审计时间戳）、状态与删除标记
        var denied = await ReloadApplyAsync(applyId);
        Assert.Equal(before.Status, denied.Status);
        Assert.Equal(before.Amount, denied.Amount);
        Assert.Equal(before.CustomerId, denied.CustomerId);
        Assert.Equal(before.SalesOrderId, denied.SalesOrderId);
        Assert.Equal(before.Currency, denied.Currency);
        Assert.Equal(before.ExchangeRate, denied.ExchangeRate);
        Assert.Equal(before.BankAccount, denied.BankAccount);
        Assert.Equal(before.UpdatedAt, denied.UpdatedAt);
        Assert.False(denied.IsDeleted);

        // 来源销售订单取消：存在未删除且未取消的定金申请单 → 拒绝（订单保持原状态）
        var refused = await TryCancelSourceOrderAsync(userId, orderId);
        Assert.False(refused.Success);
        Assert.Contains("定金申请单", refused.Error);
        Assert.Equal(DocumentStatus.Approved, await ReloadOrderStatusAsync(orderId));

        // 显式取消定金申请单释放护栏（历史与审计保留，不物理删除）
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewApplyController(db, userId).Cancel(applyId));

        var released = await TryCancelSourceOrderAsync(userId, orderId);
        Assert.True(released.Success, released.Error);
        Assert.Equal(DocumentStatus.Cancelled, await ReloadOrderStatusAsync(orderId));

        var retained = await ReloadApplyAsync(applyId);
        Assert.Equal(DocumentStatus.Cancelled, retained.Status);
        Assert.False(retained.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }


    // ==================== 2. 两个独立连接竞态（定金申请单行锁 + 来源销售订单行锁串行化） ====================

    [Fact]
    public async Task Race_Edit_Versus_Submit_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E381_RC1_{tag}", "并发客户1");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            // 仅待提交状态可修改（既有冻结规则）
            applyId = (await SeedApplyAsync(
                seed, $"INT_E381_RA1_{tag}", customer.Id, 1000m, status: DocumentStatus.Pending)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TryApplyActionAsync(userId, ctl => ctl.Update(applyId, NewApplyRequest(customerId, 300m))),
            () => TryApplyActionAsync(userId, ctl => ctl.Submit(applyId)));

        var edit = results[0];
        var submit = results[1];

        // 同一行竞争可能由 RowVersion 拒绝一个请求；至少一个成功，失败方不得覆盖赢家。
        // 编辑只可能在提交之前落库（成功 → 金额 300），否则在锁内重读状态被拒（绝不丢失更新 / 半成品写入）
        Assert.True(edit.Success || submit.Success, $"edit={edit.Error}; submit={submit.Error}");
        if (!submit.Success) Assert.Contains("并发修改", submit.Error);

        var applied = await ReloadApplyAsync(applyId);
        Assert.Equal(submit.Success ? DocumentStatus.Submitted : DocumentStatus.Pending, applied.Status);
        if (edit.Success)
        {
            Assert.Equal(300m, applied.Amount);
            Assert.Equal(submit.Success ? DocumentStatus.Submitted : DocumentStatus.Pending, applied.Status);
        }
        else
        {
            Assert.Equal(1000m, applied.Amount);
            Assert.True(edit.Error.Contains("当前状态不允许") || edit.Error.Contains("并发修改"), edit.Error);
        }

        Assert.False(applied.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Race_Approve_Versus_Cancel_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E381_RC2_{tag}", "并发客户2");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            applyId = (await SeedApplyAsync(
                seed, $"INT_E381_RA2_{tag}", customer.Id, 1000m, status: DocumentStatus.Submitted)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TryApplyActionAsync(userId, ctl => ctl.Approve(applyId)),
            () => TryApplyActionAsync(userId, ctl => ctl.Cancel(applyId)));

        var approve = results[0];
        var cancel = results[1];

        // 两条连接竞争同一行，允许明确的 RowVersion 冲突；最终状态必须对应成功的操作，
        // 否则在锁内重读状态被拒（绝不出现「审核后又并行取消」的陈旧状态或丢失更新）
        Assert.True(approve.Success || cancel.Success, $"approve={approve.Error} cancel={cancel.Error}");
        if (!cancel.Success) Assert.Contains("并发修改", cancel.Error);
        if (!approve.Success)
            Assert.True(approve.Error.Contains("当前状态不允许") || approve.Error.Contains("并发修改"), approve.Error);

        var applied = await ReloadApplyAsync(applyId);
        Assert.Equal(cancel.Success ? DocumentStatus.Cancelled : DocumentStatus.Approved, applied.Status);
        Assert.Equal(1000m, applied.Amount);
        Assert.Equal(customerId, applied.CustomerId);
        Assert.False(applied.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Race_Approve_Versus_SourceOrderCancel_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId, applyId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E381_RC3_{tag}", "并发客户3");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id, withSalesOrderMenu: true)).UserId;
            orderId = (await SeedSalesOrderAsync(seed, $"INT_E381_SO3_{tag}", customer.Id)).Id;
            applyId = (await SeedApplyAsync(
                seed, $"INT_E381_RA3_{tag}", customer.Id, 1000m,
                salesOrderId: orderId, status: DocumentStatus.Submitted)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        // 审核先取来源销售订单行锁、再取申请单行锁；来源取消取同一把订单行锁后再读取定金证据
        var results = await RaceAsync(
            () => TryApplyActionAsync(userId, ctl => ctl.Approve(applyId)),
            () => TryCancelSourceOrderAsync(userId, orderId));

        var approve = results[0];
        var sourceCancel = results[1];

        // 只要仍存在未删除且未取消的定金申请单以本单为显式来源，来源取消就绝不可能赢：
        // 审核先赢 → 取消在锁内看到有效来源证据被拒；取消先赢 → 也在锁内被证据护栏拒绝后回滚（订单保持已审核），审核随后成功。
        Assert.True(approve.Success, approve.Error);
        Assert.False(sourceCancel.Success);
        Assert.Contains("定金申请单", sourceCancel.Error);
        Assert.Equal(DocumentStatus.Approved, await ReloadOrderStatusAsync(orderId));

        var applied = await ReloadApplyAsync(applyId);
        Assert.Equal(DocumentStatus.Approved, applied.Status);
        Assert.Equal(orderId, applied.SalesOrderId);
        Assert.Equal(1000m, applied.Amount);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }


}

/// <summary>
/// ERP-381 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class FinanceDepositApplyLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP381";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-381] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-381] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceDepositApplyLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => FinanceDepositApplyLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}

