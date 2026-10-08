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
/// ERP-378 收款单生命周期串行化 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「收款单」菜单授权 + 既有业务员数据范围口径驱动真实
/// <see cref="FinanceReceiptController"/> 的修改 / 提交 / 审核 / 取消 / 删除，验证有效收款分摊证据
/// 保护、显式作废释放、停用账号与撤销授权后 fail closed 且原始状态 / 字段 / 审计不变；</item>
/// <item><b>两个独立连接竞态</b>：并发「登记分摊证据 vs 取消」「修改 vs 提交」「审核 vs 取消」经同一把
/// 收款单行锁（UPDLOCK/HOLDLOCK）串行化后给出唯一一致的串行化结果，没有孤儿分摊、超额占用或半成品写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ReceiptLifecycleConcurrencySqlServerTests
    : IClassFixture<ReceiptLifecycleConcurrencySqlServerFixture>
{
    private readonly ReceiptLifecycleConcurrencySqlServerFixture _fixture;

    public ReceiptLifecycleConcurrencySqlServerTests(ReceiptLifecycleConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReceiptLifecycleConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinanceReceiptController NewController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceReceiptController(db, new DocumentNumberService(db));
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

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接 / 事务）。</summary>
    private async Task<(bool Success, string Error)> TryControllerAsync(
        long? userId, Func<FinanceReceiptController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用一条独立连接登记收款分摊证据（与控制器生命周期动作竞争同一把收款单行锁）。</summary>
    private async Task<(bool Success, string Error)> TryAllocateAsync(long receiptId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await CustomerReceiptAllocationService.CreateAsync(db, new CustomerReceiptAllocationSaveDto
            {
                ReceiptId = receiptId,
                SalesOrderId = orderId,
                AllocatedAmount = amount
            });
            return (true, string.Empty);
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

    // ==================== 种子助手（EF 生成身份主键） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<FinanceReceipt> SeedReceiptAsync(
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "ORIGINAL-BANK",
            Remark = "原始备注",
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<SalesOrder> SeedOrderAsync(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = currency,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    // ==================== 真实身份 / 既有菜单授权 / 数据范围种子 ====================

    /// <summary>
    /// 播种一个真实登录账号：既有 <c>receipt</c> 菜单授权（<c>SeedData</c> 种子的菜单，不新增权限模型）+
    /// 业务员员工映射（把目标客户分配给它，数据范围恰好覆盖该客户）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedReceiptOperatorAsync(
        ErpDbContext db, long customerId, bool withMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"rlc-op-{Guid.NewGuid():N}";
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
            RoleName = "收款操作角色",
            RoleCode = $"RlcOp-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == CustomerReceiptLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return (user.Id, role.Id);
    }

    private static async Task RevokeReceiptMenuAsync(ErpDbContext db, long roleId)
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

    /// <summary>只读复核：用一条独立连接读取收款单权威状态（不受被测事务 / 跟踪状态影响）。</summary>
    private async Task<FinanceReceipt> ReloadReceiptAsync(long receiptId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceReceipts.AsNoTracking().SingleAsync(r => r.Id == receiptId);
    }

    private async Task<CustomerReceiptAllocation> ReloadAllocationAsync(long allocationId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync(a => a.Id == allocationId);
    }

    private async Task<decimal> ActiveAllocatedAsync(long receiptId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receiptId && !a.IsDeleted
                        && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .SumAsync(a => a.AllocatedAmount);
    }

    private async Task<long> ActiveAllocationCountAsync(long receiptId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.CustomerReceiptAllocations.AsNoTracking()
            .CountAsync(a => a.ReceiptId == receiptId && !a.IsDeleted
                             && a.Status == CustomerReceiptAllocationRules.StatusActive);
    }

    /// <summary>每条「请求」等价一条独立连接 / DbContext：断言业务异常代码（失败已在事务内整体回滚）。</summary>
    private async Task AssertControllerDeniedAsync(
        long? userId, int expectedCode, Func<FinanceReceiptController, Task> action)
    {
        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action(NewController(db, userId)));
        Assert.Equal(expectedCode, ex.Code);
    }

    /// <summary>每条「请求」等价一条独立连接 / DbContext：执行成功路径动作。</summary>
    private async Task RunControllerAsync(long? userId, Func<FinanceReceiptController, Task> action)
    {
        await using var db = _fixture.CreateDbContext();
        await action(NewController(db, userId));
    }

    /// <summary>用独立连接登记一条收款分摊证据并返回其 DTO（用于后续显式作废）。</summary>
    private async Task<CustomerReceiptAllocationDto> CreateAllocationAsync(long receiptId, long orderId, decimal amount)
    {
        await using var db = _fixture.CreateDbContext();
        return await CustomerReceiptAllocationService.CreateAsync(db, new CustomerReceiptAllocationSaveDto
        {
            ReceiptId = receiptId,
            SalesOrderId = orderId,
            AllocatedAmount = amount
        });
    }

    /// <summary>用独立连接走既有显式作废服务释放证据（证据保留，不物理删除）。</summary>
    private async Task VoidAllocationAsync(long allocationId, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        await CustomerReceiptAllocationService.VoidAsync(db, allocationId, reason);
    }

    // ==================== 1. 真实控制器 + 有效证据保护 / 显式作废释放 ====================

    [Fact]
    public async Task Controller_Update_WithActiveEvidence_Refused_ThenExplicitVoidApplies()
    {
        Guard();
        long userId, receiptId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C1", "客户A");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R1", customer.Id, 1000m, Currency.USD)).Id;
            orderId = (await SeedOrderAsync(seed, "INT_RLC378_SO1", customer.Id)).Id;
        }

        var allocation = await CreateAllocationAsync(receiptId, orderId, 300m);

        // 有有效证据：改金额被拒绝，原始金额 / 备注 / 状态 / 证据全部不变
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Update(receiptId, new FinanceReceipt
        {
            CustomerId = allocation.CustomerId,
            Amount = 500m,
            Currency = Currency.USD,
            Remark = "试图改金额"
        }));

        var refused = await ReloadReceiptAsync(receiptId);
        Assert.Equal(1000m, refused.Amount);
        Assert.Equal("原始备注", refused.Remark);
        Assert.Equal("ORIGINAL-BANK", refused.BankAccount);
        Assert.Equal(DocumentStatus.Pending, refused.Status);
        Assert.Equal(300m, await ActiveAllocatedAsync(receiptId));

        // 既有显式作废服务释放限制（保留历史证据，不物理删除）
        await VoidAllocationAsync(allocation.Id, "录错");

        await RunControllerAsync(userId, ctl => ctl.Update(receiptId, new FinanceReceipt
        {
            CustomerId = allocation.CustomerId,
            Amount = 500m,
            Currency = Currency.USD,
            Remark = "作废后修改"
        }));

        var applied = await ReloadReceiptAsync(receiptId);
        Assert.Equal(500m, applied.Amount);
        Assert.Equal("作废后修改", applied.Remark);

        var retained = await ReloadAllocationAsync(allocation.Id);
        Assert.False(retained.IsDeleted);
        Assert.Equal(300m, retained.AllocatedAmount);
        Assert.Equal(CustomerReceiptAllocationRules.StatusVoided, retained.Status);
    }

    [Fact]
    public async Task Controller_Cancel_WithActiveEvidence_Refused_ThenVoidReleases()
    {
        Guard();
        long userId, receiptId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C2", "客户B");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R2", customer.Id, 800m, Currency.USD)).Id;
            orderId = (await SeedOrderAsync(seed, "INT_RLC378_SO2", customer.Id)).Id;
        }

        var allocation = await CreateAllocationAsync(receiptId, orderId, 200m);

        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Cancel(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Delete(receiptId));

        var refused = await ReloadReceiptAsync(receiptId);
        Assert.Equal(DocumentStatus.Pending, refused.Status);
        Assert.False(refused.IsDeleted);
        Assert.Equal(800m, refused.Amount);
        Assert.Equal(1, await ActiveAllocationCountAsync(receiptId));

        await VoidAllocationAsync(allocation.Id, "录错");

        await RunControllerAsync(userId, ctl => ctl.Cancel(receiptId));
        var cancelled = await ReloadReceiptAsync(receiptId);
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(800m, cancelled.Amount);
        Assert.Equal(200m, (await ReloadAllocationAsync(allocation.Id)).AllocatedAmount);
    }

    // ==================== 2. 停用账号 / 撤销授权后的 fail closed（原状态与审计不变） ====================

    [Fact]
    public async Task Controller_DisabledAccount_AllLifecycleActions_Denied_AndRowUnchanged()
    {
        Guard();
        long userId, receiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C3", "客户C");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R3", customer.Id, 600m, Currency.USD,
                DocumentStatus.Submitted)).Id;
            await DisableUserAsync(seed, userId);
        }

        var before = await ReloadReceiptAsync(receiptId);

        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Update(receiptId, new FinanceReceipt
        {
            CustomerId = before.CustomerId,
            Amount = 100m,
            Currency = Currency.USD,
            Remark = "停用后改写"
        }));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Submit(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Approve(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Cancel(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Delete(receiptId));

        var after = await ReloadReceiptAsync(receiptId);
        Assert.Equal(DocumentStatus.Submitted, after.Status);
        Assert.Equal(600m, after.Amount);
        Assert.Equal("原始备注", after.Remark);
        Assert.Equal("ORIGINAL-BANK", after.BankAccount);
        Assert.False(after.IsDeleted);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Controller_RevokedMenu_AllLifecycleActions_Denied_AndRowUnchanged()
    {
        Guard();
        long userId, receiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C4", "客户D");
            var seeded = await SeedReceiptOperatorAsync(seed, customer.Id);
            userId = seeded.UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R4", customer.Id, 700m, Currency.USD)).Id;
            await RevokeReceiptMenuAsync(seed, seeded.RoleId);
        }

        var before = await ReloadReceiptAsync(receiptId);

        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Submit(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Approve(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Cancel(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Delete(receiptId));
        await AssertControllerDeniedAsync(userId, ErrorCodes.Forbidden, ctl => ctl.Update(receiptId, new FinanceReceipt
        {
            CustomerId = before.CustomerId,
            Amount = 700m,
            Currency = Currency.USD
        }));

        var after = await ReloadReceiptAsync(receiptId);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(700m, after.Amount);
        Assert.False(after.IsDeleted);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Controller_Transitions_SubmitApprove_AndLosingApproveKeepsAudit()
    {
        Guard();
        long userId, receiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C5", "客户E");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R5", customer.Id, 900m, Currency.USD)).Id;
        }

        // 状态不允许的操作被拒绝（审核必须在提交之后）
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Approve(receiptId));

        await RunControllerAsync(userId, ctl => ctl.Submit(receiptId));
        Assert.Equal(DocumentStatus.Submitted, (await ReloadReceiptAsync(receiptId)).Status);

        var beforeApprove = await ReloadReceiptAsync(receiptId);
        await RunControllerAsync(userId, ctl => ctl.Approve(receiptId));
        var approved = await ReloadReceiptAsync(receiptId);
        Assert.Equal(DocumentStatus.Approved, approved.Status);
        Assert.Equal(900m, approved.Amount);
        Assert.True(approved.UpdatedAt >= beforeApprove.UpdatedAt);

        // 已审核状态下重复审核被拒绝，状态与审计时间戳不被改写
        await AssertControllerDeniedAsync(userId, ErrorCodes.RuleConflict, ctl => ctl.Approve(receiptId));
        var after = await ReloadReceiptAsync(receiptId);
        Assert.Equal(DocumentStatus.Approved, after.Status);
        Assert.Equal(approved.UpdatedAt, after.UpdatedAt);
    }

    // ==================== 3. 两个独立连接竞态（收款单行锁串行化） ====================

    [Fact]
    public async Task Race_AssignmentVersusCancel_OnlyOneSucceeds_NoOrphanAllocation()
    {
        Guard();
        long userId, receiptId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C6", "客户F");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R6", customer.Id, 1000m, Currency.USD)).Id;
            orderId = (await SeedOrderAsync(seed, "INT_RLC378_SO6", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryAllocateAsync(receiptId, orderId, 400m),
            () => TryControllerAsync(userId, ctl => ctl.Cancel(receiptId)));

        Assert.Equal(1, results.Count(r => r.Success));

        var stored = await ReloadReceiptAsync(receiptId);
        var activeCount = await ActiveAllocationCountAsync(receiptId);
        if (stored.Status == DocumentStatus.Cancelled)
        {
            // 取消赢：不得留下任何孤儿分摊（有效行与占用额度都为 0）
            Assert.Equal(0, activeCount);
            Assert.Equal(0m, await ActiveAllocatedAsync(receiptId));
        }
        else
        {
            // 登记赢：收款单保持待提交，只有一条有效分摊且金额恰好 400
            Assert.Equal(DocumentStatus.Pending, stored.Status);
            Assert.Equal(1, activeCount);
            Assert.Equal(400m, await ActiveAllocatedAsync(receiptId));
        }
    }

    [Fact]
    public async Task Race_EditVersusSubmit_SerializedConsistentResult_NoPartialWrite()
    {
        Guard();
        long userId, receiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C7", "客户G");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R7", customer.Id, 1000m, Currency.USD)).Id;
        }

        var customerId = (await ReloadReceiptAsync(receiptId)).CustomerId;

        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.Update(receiptId, new FinanceReceipt
            {
                CustomerId = customerId,
                Amount = 500m,
                Currency = Currency.USD,
                BankAccount = "EDITED-BANK",
                Remark = "并发修改"
            })),
            () => TryControllerAsync(userId, ctl => ctl.Submit(receiptId)));

        var edit = results[0];
        var submit = results[1];
        Assert.True(edit.Success || submit.Success);

        var stored = await ReloadReceiptAsync(receiptId);

        // 无半成品写入：要么整笔修改生效（金额 / 银行账户 / 备注同时变），要么全部保持原值
        if (edit.Success)
        {
            Assert.Equal(500m, stored.Amount);
            Assert.Equal("EDITED-BANK", stored.BankAccount);
            Assert.Equal("并发修改", stored.Remark);
        }
        else
        {
            Assert.Equal(1000m, stored.Amount);
            Assert.Equal("ORIGINAL-BANK", stored.BankAccount);
            Assert.Equal("原始备注", stored.Remark);
        }

        // 提交结果与最终状态严格一致，且状态不会停在提交 / 审核以外的非法值
        Assert.Equal(submit.Success, stored.Status == DocumentStatus.Submitted);
        Assert.True(stored.Status is DocumentStatus.Pending or DocumentStatus.Submitted);
        Assert.Equal(0, await ActiveAllocationCountAsync(receiptId));
    }

    [Fact]
    public async Task Race_ApproveVersusCancel_SerializedConsistentResult_NoPartialWrite()
    {
        Guard();
        long userId, receiptId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, "INT_RLC378_C8", "客户H");
            userId = (await SeedReceiptOperatorAsync(seed, customer.Id)).UserId;
            receiptId = (await SeedReceiptAsync(seed, "INT_RLC378_R8", customer.Id, 1200m, Currency.USD,
                DocumentStatus.Submitted)).Id;
        }

        var results = await RaceAsync(
            () => TryControllerAsync(userId, ctl => ctl.Approve(receiptId)),
            () => TryControllerAsync(userId, ctl => ctl.Cancel(receiptId)));

        var approve = results[0];
        var cancel = results[1];
        Assert.True(approve.Success || cancel.Success);

        var stored = await ReloadReceiptAsync(receiptId);
        Assert.NotEqual(DocumentStatus.Submitted, stored.Status);

        if (cancel.Success)
        {
            // 取消胜出（终态）：审核要么先成功、要么被取消阻断，绝不会出现半成品
            Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        }
        else
        {
            // 取消被行锁串行化击败：审核必须已生效
            Assert.True(approve.Success);
            Assert.Equal(DocumentStatus.Approved, stored.Status);
        }

        Assert.Equal(1200m, stored.Amount);
        Assert.Equal("ORIGINAL-BANK", stored.BankAccount);
        Assert.Equal(0, await ActiveAllocationCountAsync(receiptId));
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-378）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据。
/// <para>安全口径：任何数据库访问之前先校验实例名 / 库名前缀 / 集成安全；发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class ReceiptLifecycleConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RECEIPTLIFECYCLECONCURRENCY_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-378] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

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
                throw new InvalidOperationException("The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-378] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ReceiptLifecycleConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => ReceiptLifecycleConcurrencySqlServerFixture.AssertDedicatedTarget(connection));
}
