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
/// ERP-521 代理服务费「对账单共享分摊额度 + 对账单作废 ↔ 收款分摊证据」并发序列化真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST_ERP521_*</c> 目标）。
/// <list type="number">
/// <item><b>真两条独立连接竞态</b>（各自独立 DbContext / 连接 / 事务，用门闩对齐起跑，绝不使用时间睡眠）：
/// ① 两张收款单争抢同一对账单的同一份剩余未分摊额度（恰好一个赢家、额度守恒、输家零写入）；
/// ② 分摊登记 vs 对账单作废（恰好一个赢家，绝不出现「已作废对账单仍被有效分摊证据指向」）；
/// ③ 分摊作废 vs 所属对账单作废（串行成一个顺序，分摊作废证据保留）；
/// ④ 同一张收款单被两个对账单并发分摊（共享收款单唯一额度，恰好一个赢家）。</item>
/// <item><b>确定性拒绝</b>：未授权 / 越范围（权威客户归属收敛）与跨币种的分摊 / 作废一律受控拒绝且零变更；
/// 跨证据族（ERP-053 销售订单收款引用）已占用额度时，代理分摊不得突破收款单唯一额度。</item>
/// <item><b>显式作废释放流程</b>：先走既有作废服务显式作废分摊证据，对账单才可按既有规则作废；
/// 作废证据的原始金额 / 双方快照 / 审计在真实列上原样保留。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class AgencyStatementCollectionConcurrencySqlServerTests
    : IClassFixture<AgencyStatementCollectionConcurrencySqlServerFixture>
{
    private readonly AgencyStatementCollectionConcurrencySqlServerFixture _fixture;

    public AgencyStatementCollectionConcurrencySqlServerTests(
        AgencyStatementCollectionConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AgencyStatementCollectionConcurrencySqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private readonly record struct ActionOutcome(bool Success, int? Code, string Type, string Error);

    // ==================== 独立连接脚手架 ====================

    private static T WithUser<T>(T controller, long? userId, string? displayName) where T : ControllerBase
    {
        var claims = new List<Claim>();
        if (userId.HasValue) claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        if (!string.IsNullOrEmpty(displayName)) claims.Add(new Claim(ClaimTypes.Name, displayName));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return controller;
    }

    /// <summary>一条独立连接完成一次真实收款分摊动作（含实时身份 / 菜单 / 客户范围解析）。</summary>
    private async Task<ActionOutcome> TryAllocationAsync(
        long? userId, string? displayName,
        Func<AgencyServiceFeeCollectionAllocationController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(WithUser(
                new AgencyServiceFeeCollectionAllocationController(db), userId, displayName));
            return new ActionOutcome(result is OkObjectResult, ErrorCodes.Success, string.Empty, string.Empty);
        }
        catch (BusinessException ex)
        {
            return new ActionOutcome(false, ex.Code, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            return new ActionOutcome(false, null, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>一条独立连接完成一次真实对账单动作（含实时身份 / 菜单 / 客户范围解析）。</summary>
    private async Task<ActionOutcome> TryStatementAsync(
        long? userId, string? displayName,
        Func<AgencyServiceFeeStatementController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(WithUser(
                new AgencyServiceFeeStatementController(db), userId, displayName));
            return new ActionOutcome(result is OkObjectResult, ErrorCodes.Success, string.Empty, string.Empty);
        }
        catch (BusinessException ex)
        {
            return new ActionOutcome(false, ex.Code, ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            return new ActionOutcome(false, null, ex.GetType().Name, ex.Message);
        }
    }

    private Task<ActionOutcome> TryAllocateAsync(
        long? userId, string? displayName, long statementId, long receiptId, decimal amount)
        => TryAllocationAsync(userId, displayName, ctl => ctl.Create(new AgencyServiceFeeCollectionAllocationSaveDto
        {
            StatementId = statementId,
            ReceiptId = receiptId,
            AllocatedAmount = amount,
            Remark = "ERP-521 集成测试"
        }));

    private Task<ActionOutcome> TryVoidAllocationAsync(
        long? userId, string? displayName, long allocationId, string reason)
        => TryAllocationAsync(userId, displayName, ctl => ctl.Void(
            allocationId, new AgencyServiceFeeCollectionAllocationVoidRequest { Reason = reason }));

    private Task<ActionOutcome> TryVoidStatementAsync(
        long? userId, string? displayName, long statementId, string reason)
        => TryStatementAsync(userId, displayName, ctl => ctl.Void(
            statementId, new AgencyServiceFeeStatementVoidRequest { Reason = reason }));

    /// <summary>
    /// 双连接竞态的确定性门闩：两条独立连接在自己的线程上等待同一屏障后同时起跑，
    /// 绝不使用时间睡眠来制造交错。
    /// </summary>
    private static (Task Barrier, Action Release) NewBarrier()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (gate.Task, () => gate.TrySetResult(true));
    }

    // ==================== 种子 / 观测脚手架 ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = code,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<long> SeedReceiptAsync(
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency = Currency.USD)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt.Id;
    }

    private static async Task<long> SeedStatementAsync(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount)
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            Currency = "USD",
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = 1L,
            AgreementNo = "AG-521",
            AgreementCurrency = "USD",
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = AgencyServiceFeeStatementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 9, 21),
            RecordedBy = "集成测试"
        };
        db.AgencyServiceFeeStatements.Add(statement);
        await db.SaveChangesAsync();
        return statement.Id;
    }

    /// <summary>
    /// 播种一条 ERP-053「收款单 → 销售订单」**有效**分摊（另一证据族），用于验证同一张收款单的唯一同币种
    /// 分摊额度（ERP-350）被两套证据共同占用、代理分摊不得突破该额度。
    /// </summary>
    private static async Task SeedOrderFamilyAllocationAsync(
        ErpDbContext db, long receiptId, long customerId, decimal amount)
    {
        var receipt = await db.FinanceReceipts.AsNoTracking().SingleAsync(r => r.Id == receiptId);
        var order = new SalesOrder
        {
            OrderNo = $"SO-521-{Guid.NewGuid():N}"[..24],
            OrderDate = new DateTime(2026, 8, 1),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receiptId,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = string.Empty,
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = receipt.Currency.ToString(),
            CustomerId = customerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = receipt.Currency.ToString(),
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 播种具备既有「客户资料」（customer）菜单、且数据范围不受限（系统内置角色）的真实操作账号。
    /// 绝不新增任何菜单 / 角色授权定义，只复用既有菜单口径。
    /// </summary>
    private async Task<long> SeedPrivilegedOperatorAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var code = $"int-e521-op-{Guid.NewGuid():N}";
        var role = new SysRole { RoleCode = $"INT_E521_{Guid.NewGuid():N}", RoleName = "ERP-521 特权角色", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => !m.IsDeleted && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP-521 集成账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// 播种受限业务员账号：既有「客户资料」菜单 + 客户归属映射（数据范围只覆盖指定客户）。
    /// </summary>
    private async Task<long> SeedScopedOperatorAsync(long inScopeCustomerId)
    {
        await using var db = _fixture.CreateDbContext();
        var code = $"int-e521-scoped-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleCode = $"INT_E521_S_{Guid.NewGuid():N}", RoleName = "ERP-521 业务员角色", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => !m.IsDeleted && m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP-521 业务员账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();
        return user.Id;
    }

    // ==================== 观测脚手架 ====================

    private async Task<AgencyServiceFeeStatement> ReloadStatementAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeStatements.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<AgencyServiceFeeCollectionAllocation> ReloadAllocationAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeCollectionAllocations.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<List<AgencyServiceFeeCollectionAllocation>> ActiveAllocationsAsync(long statementId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.StatementId == statementId
                        && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .ToListAsync();
    }

    private async Task<decimal> ActiveAllocationSumAsync(long statementId)
        => (await ActiveAllocationsAsync(statementId)).Sum(a => a.AllocatedAmount);

    private async Task<int> CountAllocationsAsync(long statementId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .CountAsync(a => a.StatementId == statementId);
    }

    private async Task<int> ActiveCountForReceiptAsync(long receiptId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .CountAsync(a => !a.IsDeleted && a.ReceiptId == receiptId
                             && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive);
    }

    // ==================== 1. 真两条独立连接竞态 ====================

    /// <summary>
    /// 竞态①：<b>两张不同收款单争抢同一对账单的同一份剩余未分摊额度</b>（两条独立连接 + 门闩对齐起跑）。
    /// 对账单合计 100、两张收款单各 100；两个连接同时对同一对账单各分摊 60 —— 恰好一个赢家，
    /// 另一个在共享对账单行锁内读到剩余 40 而受控拒绝（零写入、无重复有效对），合计守恒不超过 100；
    /// 随后剩余 40 仍可被另一张收款单正常使用。
    /// </summary>
    [Fact]
    public async Task Race_two_receipts_for_one_remaining_statement_capacity_has_exactly_one_winner()
    {
        Guard();
        var operatorId = await SeedPrivilegedOperatorAsync();
        long customerId, statementId, receiptA, receiptB;
        await using (var db = _fixture.CreateDbContext())
        {
            customerId = (await SeedCustomerAsync(db, $"C-521-R1-{Tag()}")).Id;
            statementId = await SeedStatementAsync(db, $"ASFS-521-R1-{Tag()}", customerId, 100m);
            receiptA = await SeedReceiptAsync(db, $"RC-521-R1A-{Tag()}", customerId, 100m);
            receiptB = await SeedReceiptAsync(db, $"RC-521-R1B-{Tag()}", customerId, 100m);
        }

        var (barrier, release) = NewBarrier();
        var first = Task.Run(async () =>
        {
            await barrier;
            return await TryAllocateAsync(operatorId, "并发登记人", statementId, receiptA, 60m);
        });
        var second = Task.Run(async () =>
        {
            await barrier;
            return await TryAllocateAsync(operatorId, "并发登记人", statementId, receiptB, 60m);
        });
        release();

        var outcomes = new[] { await first, await second };
        Assert.Equal(1, outcomes.Count(o => o.Success));
        var loser = outcomes.Single(o => !o.Success);
        Assert.Equal(ErrorCodes.RuleConflict, loser.Code);
        Assert.Contains("未分摊额", loser.Error);

        var active = await ActiveAllocationsAsync(statementId);
        var winner = Assert.Single(active);
        Assert.Equal(60m, winner.AllocatedAmount);
        Assert.Equal(60m, await ActiveAllocationSumAsync(statementId));
        Assert.Equal(1, await CountAllocationsAsync(statementId));   // 输家零部分写入

        // 剩余额度仍可被另一张收款单使用：共享额度守恒、无重复有效对、上限不被突破。
        var remainingReceipt = winner.ReceiptId == receiptA ? receiptB : receiptA;
        var followUp = await TryAllocateAsync(operatorId, "登记人", statementId, remainingReceipt, 40m);
        Assert.True(followUp.Success, followUp.Error);
        Assert.Equal(100m, await ActiveAllocationSumAsync(statementId));
        Assert.Equal(2, (await ActiveAllocationsAsync(statementId)).Count);
        Assert.Equal(100m, (await ReloadStatementAsync(statementId)).TotalAmount);
    }

    /// <summary>
    /// 竞态②：<b>分摊登记 vs 对账单作废</b>（两条独立连接 + 门闩对齐起跑）。分摊登记与对账单作废共用同一把
    /// 对账单行锁，因此只有两种一致结果：分摊先提交 → 对账单作废被受控拒绝（仍已登记、分摊有效）；
    /// 对账单作废先提交 → 分摊登记被受控拒绝（对账单已作废、零分摊行）。绝不存在「已作废对账单仍被有效分摊
    /// 证据指向」，也绝无部分写入 / 半截证据。
    /// </summary>
    [Fact]
    public async Task Race_allocation_create_versus_statement_void_is_serialized_without_active_allocation_on_voided_statement()
    {
        Guard();
        var operatorId = await SeedPrivilegedOperatorAsync();
        long customerId, statementId, receiptId;
        await using (var db = _fixture.CreateDbContext())
        {
            customerId = (await SeedCustomerAsync(db, $"C-521-R2-{Tag()}")).Id;
            statementId = await SeedStatementAsync(db, $"ASFS-521-R2-{Tag()}", customerId, 100m);
            receiptId = await SeedReceiptAsync(db, $"RC-521-R2-{Tag()}", customerId, 100m);
        }

        var (barrier, release) = NewBarrier();
        var allocate = Task.Run(async () =>
        {
            await barrier;
            return await TryAllocateAsync(operatorId, "并发登记人", statementId, receiptId, 60m);
        });
        var voidStatement = Task.Run(async () =>
        {
            await barrier;
            return await TryVoidStatementAsync(operatorId, "并发作废人", statementId, "对账口径更正");
        });
        release();

        var allocation = await allocate;
        var statementVoid = await voidStatement;

        Assert.NotEqual(allocation.Success, statementVoid.Success);   // 恰好一个赢家

        var header = await ReloadStatementAsync(statementId);
        var active = await ActiveAllocationsAsync(statementId);
        if (allocation.Success)
        {
            Assert.False(statementVoid.Success);
            Assert.Equal(ErrorCodes.RuleConflict, statementVoid.Code);
            Assert.Contains("有效收款分摊证据", statementVoid.Error);
            Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
            var row = Assert.Single(active);
            Assert.Equal(60m, row.AllocatedAmount);
        }
        else
        {
            Assert.Equal(ErrorCodes.RuleConflict, allocation.Code);
            Assert.Contains("已作废", allocation.Error);
            Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, header.Status);
            Assert.Empty(active);                              // 绝不出现指向已作废对账单的有效分摊证据
            Assert.Equal(0, await CountAllocationsAsync(statementId));
        }
    }

    /// <summary>
    /// 竞态③：<b>分摊证据作废 vs 所属对账单作废</b>（两条独立连接 + 门闩对齐起跑）。两者共用同一把对账单行锁：
    /// 分摊作废先提交 → 释放额度后对账单也能按既有规则作废；对账单先提交 → 因仍存在有效分摊证据被受控拒绝，
    /// 随后分摊作废成功。分摊作废证据的原始金额 / 双方快照 / 作废审计在真实列上原样保留。
    /// </summary>
    [Fact]
    public async Task Race_allocation_void_versus_parent_statement_void_is_serialized_and_keeps_void_evidence()
    {
        Guard();
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-521-R3-{Tag()}";
        long statementId, receiptId;
        await using (var db = _fixture.CreateDbContext())
        {
            var customerId = (await SeedCustomerAsync(db, $"C-521-R3-{Tag()}")).Id;
            statementId = await SeedStatementAsync(db, statementNo, customerId, 100m);
            receiptId = await SeedReceiptAsync(db, $"RC-521-R3-{Tag()}", customerId, 100m);
        }

        var seeded = await TryAllocateAsync(operatorId, "登记人", statementId, receiptId, 60m);
        Assert.True(seeded.Success, seeded.Error);
        var allocationId = Assert.Single(await ActiveAllocationsAsync(statementId)).Id;

        var (barrier, release) = NewBarrier();
        var voidAllocationTask = Task.Run(async () =>
        {
            await barrier;
            return await TryVoidAllocationAsync(operatorId, "并发作废人", allocationId, "分摊录错");
        });
        var voidStatementTask = Task.Run(async () =>
        {
            await barrier;
            return await TryVoidStatementAsync(operatorId, "并发作废人", statementId, "对账口径更正");
        });
        release();

        var allocationVoid = await voidAllocationTask;
        var statementVoid = await voidStatementTask;

        // 有另一条连接按同一把对账单行锁串行：作废有效分摊证据自身总是成功。
        Assert.True(allocationVoid.Success, allocationVoid.Error);

        var allocationRow = await ReloadAllocationAsync(allocationId);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusVoided, allocationRow.Status);
        Assert.Equal(60m, allocationRow.AllocatedAmount);          // 原始金额保留
        Assert.Equal(statementNo, allocationRow.StatementNo);      // 两侧快照保留
        Assert.Equal("分摊录错", allocationRow.VoidReason);
        Assert.NotNull(allocationRow.VoidedAt);

        var header = await ReloadStatementAsync(statementId);
        if (statementVoid.Success)
        {
            Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, header.Status);
        }
        else
        {
            Assert.Equal(ErrorCodes.RuleConflict, statementVoid.Code);
            Assert.Contains("有效收款分摊证据", statementVoid.Error);
            Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
        }

        Assert.Empty(await ActiveAllocationsAsync(statementId));   // 作废后不再占用额度
    }

    /// <summary>
    /// 竞态④：<b>同一张收款单被两个对账单并发分摊</b>（两条独立连接 + 门闩对齐起跑）。收款单金额 100 是唯一、
    /// 同币种的分摊额度：两个对账单各要 60 时，收款单行锁内恰好一个赢家，另一个受控拒绝（零写入）；
    /// 收款单有效分摊合计守恒为 60，且不产生任何越额度 / 重复有效证据。
    /// </summary>
    [Fact]
    public async Task Race_two_statements_for_one_receipt_shared_budget_has_exactly_one_winner()
    {
        Guard();
        var operatorId = await SeedPrivilegedOperatorAsync();
        long customerId, statementA, statementB, receiptId;
        await using (var db = _fixture.CreateDbContext())
        {
            customerId = (await SeedCustomerAsync(db, $"C-521-R4-{Tag()}")).Id;
            statementA = await SeedStatementAsync(db, $"ASFS-521-R4A-{Tag()}", customerId, 200m);
            statementB = await SeedStatementAsync(db, $"ASFS-521-R4B-{Tag()}", customerId, 200m);
            receiptId = await SeedReceiptAsync(db, $"RC-521-R4-{Tag()}", customerId, 100m);
        }

        var (barrier, release) = NewBarrier();
        var first = Task.Run(async () =>
        {
            await barrier;
            return await TryAllocateAsync(operatorId, "并发登记人", statementA, receiptId, 60m);
        });
        var second = Task.Run(async () =>
        {
            await barrier;
            return await TryAllocateAsync(operatorId, "并发登记人", statementB, receiptId, 60m);
        });
        release();

        var outcomes = new[] { await first, await second };
        Assert.Equal(1, outcomes.Count(o => o.Success));
        var loser = outcomes.Single(o => !o.Success);
        Assert.Equal(ErrorCodes.RuleConflict, loser.Code);
        Assert.False(loser.Type.Contains("SqlException", StringComparison.Ordinal));
        Assert.DoesNotContain("SqlException", loser.Error);
        Assert.DoesNotContain("UNIQUE", loser.Error);
        // 输家在收款单行锁内被受控拒绝：可能是「共享额度不足」，也可能是既有收款单行锁的「阻塞后并发冲突」；
        // 两者都是可读业务冲突（绝不泄露数据库内部细节），且额度守恒、无越额度证据。
        Assert.True(
            loser.Error.Contains("可分摊余额", StringComparison.Ordinal)
            || loser.Error.Contains("正在被并发修改", StringComparison.Ordinal),
            loser.Error);

        Assert.Equal(1, await ActiveCountForReceiptAsync(receiptId));
        var winnerStatement = outcomes[0].Success ? statementA : statementB;
        Assert.Equal(60m, await ActiveAllocationSumAsync(winnerStatement));
        Assert.Equal(0m, await ActiveAllocationSumAsync(outcomes[0].Success ? statementB : statementA));
    }

    // ==================== 2. 共享收款单额度（跨证据族）与拒绝路径 ====================

    /// <summary>
    /// 同一张收款单的唯一同币种分摊额度跨证据族保留（ERP-350）：已有的 ERP-053「收款单 → 销售订单」有效分摊
    /// 已占用 60，则 ERP-071 代理分摊最多只能再占用 40；超额的 60 被受控拒绝且零写入，40 可正常登记。
    /// </summary>
    [Fact]
    public async Task Existing_order_family_allocation_keeps_receipt_unique_budget_for_agency_family()
    {
        Guard();
        var operatorId = await SeedPrivilegedOperatorAsync();
        long statementId, receiptId;
        await using (var db = _fixture.CreateDbContext())
        {
            var customerId = (await SeedCustomerAsync(db, $"C-521-B1-{Tag()}")).Id;
            statementId = await SeedStatementAsync(db, $"ASFS-521-B1-{Tag()}", customerId, 200m);
            receiptId = await SeedReceiptAsync(db, $"RC-521-B1-{Tag()}", customerId, 100m);
            await SeedOrderFamilyAllocationAsync(db, receiptId, customerId, 60m);
        }

        var denied = await TryAllocateAsync(operatorId, "登记人", statementId, receiptId, 60m);
        Assert.False(denied.Success);
        Assert.Equal(ErrorCodes.RuleConflict, denied.Code);
        Assert.Contains("可分摊余额", denied.Error);
        Assert.Empty(await ActiveAllocationsAsync(statementId));   // 零写入

        var allowed = await TryAllocateAsync(operatorId, "登记人", statementId, receiptId, 40m);
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal(40m, await ActiveAllocationSumAsync(statementId));
    }

    /// <summary>
    /// 拒绝路径（真实身份 / 菜单 / 权威客户范围 + 币种口径）：未认证账号 → 未授权；越范围客户的收款单 →
    /// 同一条不披露存在性的错误；跨币种 → 受控冲突。任何一条失败的请求都不落分摊行、不改写对账单 / 收款单。
    /// </summary>
    [Fact]
    public async Task Denied_authority_ownership_and_currency_allocate_nothing()
    {
        Guard();
        var statementNo = $"ASFS-521-B2-{Tag()}";
        long statementId, ownerReceiptId, eurReceiptId;
        long scopedOperatorId;
        await using (var db = _fixture.CreateDbContext())
        {
            var owner = await SeedCustomerAsync(db, $"C-521-B2-{Tag()}");
            var foreign = await SeedCustomerAsync(db, $"C-521-B3-{Tag()}");
            statementId = await SeedStatementAsync(db, statementNo, owner.Id, 100m);
            ownerReceiptId = await SeedReceiptAsync(db, $"RC-521-B2-{Tag()}", owner.Id, 100m);
            eurReceiptId = await SeedReceiptAsync(db, $"RC-521-B2E-{Tag()}", owner.Id, 100m, Currency.EUR);
            scopedOperatorId = await SeedScopedOperatorAsync(owner.Id);

            // 越范围客户的收款单（受限业务员只覆盖 owner 客户）。
            var foreignReceiptId = await SeedReceiptAsync(db, $"RC-521-B3-{Tag()}", foreign.Id, 100m);
            var foreignDenied = await TryAllocateAsync(
                scopedOperatorId, "越范围登记人", statementId, foreignReceiptId, 10m);
            Assert.Equal(ErrorCodes.NotFound, foreignDenied.Code);
            Assert.Equal(AgencyServiceFeeReconciliationRules.RegisterNotFoundText, foreignDenied.Error);
        }

        // 未认证账号：在任何读取 / 写入之前 fail closed。
        var anonymous = await TryAllocateAsync(null, null, statementId, ownerReceiptId, 10m);
        Assert.Equal(ErrorCodes.Unauthorized, anonymous.Code);

        // 跨币种：受控冲突（不做汇率换算、不跨币种合并）。
        var crossCurrency = await TryAllocateAsync(scopedOperatorId, "跨币种登记人", statementId, eurReceiptId, 10m);
        Assert.Equal(ErrorCodes.RuleConflict, crossCurrency.Code);
        Assert.Contains("币种", crossCurrency.Error);

        Assert.Empty(await ActiveAllocationsAsync(statementId));
        Assert.Equal(0, await CountAllocationsAsync(statementId));
        var header = await ReloadStatementAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded, header.Status);
        Assert.Equal(100m, header.TotalAmount);
    }

    /// <summary>
    /// 显式作废释放流程（真实 SQL）：对账单存在有效分摊证据时不能作废；先走既有分摊作废服务释放额度后，
    /// 对账单才可按既有规则作废；作废证据的原始金额 / 双方快照 / 审计全部保留，不物理删除。
    /// </summary>
    [Fact]
    public async Task Explicit_allocation_void_releases_statement_and_allows_statement_void_with_evidence_retained()
    {
        Guard();
        var operatorId = await SeedPrivilegedOperatorAsync();
        var statementNo = $"ASFS-521-B4-{Tag()}";
        long statementId, receiptId;
        await using (var db = _fixture.CreateDbContext())
        {
            var customerId = (await SeedCustomerAsync(db, $"C-521-B4-{Tag()}")).Id;
            statementId = await SeedStatementAsync(db, statementNo, customerId, 100m);
            receiptId = await SeedReceiptAsync(db, $"RC-521-B4-{Tag()}", customerId, 100m);
        }

        var allocated = await TryAllocateAsync(operatorId, "登记人", statementId, receiptId, 60m);
        Assert.True(allocated.Success, allocated.Error);
        var allocationId = Assert.Single(await ActiveAllocationsAsync(statementId)).Id;

        var blocked = await TryVoidStatementAsync(operatorId, "作废人", statementId, "对账口径更正");
        Assert.False(blocked.Success);
        Assert.Equal(ErrorCodes.RuleConflict, blocked.Code);
        Assert.Contains("有效收款分摊证据", blocked.Error);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusRecorded,
            (await ReloadStatementAsync(statementId)).Status);

        var released = await TryVoidAllocationAsync(operatorId, "作废人", allocationId, "分摊录错");
        Assert.True(released.Success, released.Error);

        var voided = await TryVoidStatementAsync(operatorId, "作废人", statementId, "对账口径更正");
        Assert.True(voided.Success, voided.Error);
        var header = await ReloadStatementAsync(statementId);
        Assert.Equal(AgencyServiceFeeStatementRules.StatusVoided, header.Status);
        Assert.Equal("对账口径更正", header.VoidReason);

        var row = await ReloadAllocationAsync(allocationId);
        Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusVoided, row.Status);
        Assert.Equal(60m, row.AllocatedAmount);
        Assert.Equal(statementNo, row.StatementNo);
        Assert.NotEqual(0, row.ReceiptId);
        Assert.Equal("分摊录错", row.VoidReason);
    }
}

/// <summary>
/// ERP-521 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>
/// （默认 <c>NEWERP_AUTOTEST_ERP521_STATEMENTCOLLECTIONCONCURRENCY_&lt;guid&gt;</c>），发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class AgencyStatementCollectionConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP521_STATEMENTCOLLECTIONCONCURRENCY";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-521] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
            // 绝不销毁已存在的 Fixture 库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-521] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class AgencyStatementCollectionConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            AgencyStatementCollectionConcurrencySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => AgencyStatementCollectionConcurrencySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
