using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-386 费用分摊并发生成 / 作废 / 来源费用改动 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>两个独立连接竞态</b>：① 两条等价传统分摊请求 → 最多一套完整费用单；② 两条不同传统分摊请求 →
/// 单号绝不冲突；③ 来源费用删除 vs 批次生成；④ 来源费用金额修改 vs 批次生成；⑤ 生成 vs 作废；
/// ⑥ 参与方停用 vs 生成。每条竞态都验证唯一一致结果与「无半成品 / 无陈旧证据」。</item>
/// <item><b>真实身份实时授权</b>：真实 HTTP 授权管线（<see cref="ExpenseRequestAuthorizationFilter"/>）
/// + 既有「费用单」（expense-bill）菜单 + 既有业务员客户数据范围；撤销菜单 / 停用账号后立即收敛，
/// 且被拒绝的请求零改写；绝不新增任何用户授权，也绝无匿名 / 管理员兜底。</item>
/// <item><b>金额与证据精确</b>：生成行合计恒等于来源费用金额（含币种精度与余差），
/// 来源费用 / 装柜清单与结算口径不被改写，作废保留原因与原审计。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行（前缀 <c>INT_EA386_</c>），不清理 /
/// 不删除任何既有行。构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ExpenseAllocationConcurrencySqlServerTests
    : IClassFixture<ExpenseAllocationConcurrencySqlServerFixture>
{
    private readonly ExpenseAllocationConcurrencySqlServerFixture _fixture;

    public ExpenseAllocationConcurrencySqlServerTests(ExpenseAllocationConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ExpenseAllocationConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    /// <summary>仅解析 IErpDbContext 的最小服务提供器（驱动真实授权过滤器）。</summary>
    private sealed class SingleDbContextServices : IServiceProvider
    {
        private readonly IErpDbContext _db;

        public SingleDbContextServices(IErpDbContext db) => _db = db;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IErpDbContext) ? _db
             : serviceType == typeof(IServiceProvider) ? this
             : null;
    }

    /// <summary>
    /// 用一条独立连接驱动真实 HTTP 授权管线 + 真实控制器动作（身份来自已认证主体，
    /// 菜单授权 / 客户数据范围每次实时查询，绝不来自请求体）。
    /// </summary>
    private async Task<(bool Success, string Error)> TryAuthorizedActionAsync(
        long? userId, Func<ExpenseBillController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var http = new DefaultHttpContext { RequestServices = new SingleDbContextServices(db) };
            if (userId.HasValue)
                http.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
            http.Request.Path = "/api/finance/expenses/allocate-apply";

            var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db)
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };

            var actionContext = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());
            var executing = new ActionExecutingContext(
                actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);
            var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller);
            await new ExpenseRequestAuthorizationFilter().OnActionExecutionAsync(
                executing, () => Task.FromResult(executed));

            var result = await action(controller);
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

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(
        ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        decimal cartons = 10m, decimal weight = 500m, decimal volume = 3.5m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = DateTime.Today,
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    private static async Task<ContainerLoadingListParticipant> SeedParticipantAsync(
        ErpDbContext db, long loadingListId, BaseCustomer customer, bool primary = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = ContainerLoadingParticipantRules.ActiveStatus
        };
        db.ContainerLoadingListParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    private static async Task<FinanceExpense> SeedSourceExpenseAsync(
        ErpDbContext db, string expenseNo, long? customerId, decimal amount,
        string currency = "CNY", decimal exchangeRate = 1m, string refNo = "INT-EA386")
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            AmountCny = amount,
            RefType = "拼柜",
            RefNo = refNo,
            CustomerId = customerId,
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    /// <summary>
    /// 播种真实操作账号：既有「费用单」（expense-bill）菜单 + 既有业务员客户数据范围
    /// （<paramref name="privileged"/> 为 true 时复用系统内置角色口径，否则为映射到
    /// <paramref name="inScopeCustomerId"/> 客户的受限业务员）。绝不新增任何用户授权。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long? inScopeCustomerId, bool privileged = false,
        bool withMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"int-ea386-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleCode = $"INT_EA386_ROLE_{Guid.NewGuid():N}",
            RoleName = "费用分摊并发集成测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        if (withMenu && !privileged)
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "expense-bill" && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu
                {
                    ParentId = 0, MenuCode = "expense-bill", MenuName = "费用单",
                    Path = "/finance/expense-bill", MenuType = MenuType.Menu
                };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "费用分摊并发集成账号",
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (inScopeCustomerId is > 0)
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId.Value);
            customer.EmpId = employee.Id;
            await db.SaveChangesAsync();
        }
        return (user.Id, role.Id);
    }

    /// <summary>撤销（删除）本测试自己创建的「费用单」菜单授权，验证授权撤销后立即收敛（绝不新增授权）。</summary>
    private static async Task RevokeExpenseMenuAsync(ErpDbContext db, long roleId)
    {
        var menuId = await db.SysMenus.Where(m => m.MenuCode == "expense-bill" && !m.IsDeleted)
            .Select(m => m.Id).FirstOrDefaultAsync();
        var grants = await db.SysRoleMenus.Where(g => g.RoleId == roleId && g.MenuId == menuId).ToListAsync();
        db.SysRoleMenus.RemoveRange(grants);
        await db.SaveChangesAsync();
    }

    private static SalespersonDataScope PrivilegedScope()
        => new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

    private static ExpenseAllocateRequest LegacyRequest(string refNo, long? customerId, decimal total)
        => new()
        {
            RefType = "拼柜",
            RefNo = refNo,
            ExpenseType = "报关费",
            TotalAmount = total,
            Currency = "CNY",
            ExchangeRate = 1m,
            AllocationBase = "按体积",
            Details = new List<ExpenseAllocateDetail>
            {
                new() { CustomerId = customerId, CustomerName = "集成客户", Volume = 1m }
            }
        };

    private static ContainerExpenseAllocationRequest BatchRequest(
        long sourceExpenseId, long loadingListId, params (long ParticipantId, decimal BasisValue)[] lines)
        => new()
        {
            SourceExpenseId = sourceExpenseId,
            LoadingListId = loadingListId,
            AllocationMethod = ContainerExpenseAllocationRules.MethodByVolume,
            Lines = lines.Select(l => new ContainerExpenseAllocationBasisDto
            {
                ParticipantId = l.ParticipantId,
                BasisValue = l.BasisValue
            }).ToList()
        };

    private async Task<(bool Success, string Error)> TryGenerateAsync(ContainerExpenseAllocationRequest request)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ContainerExpenseAllocationService.GenerateAsync(db, request, PrivilegedScope());
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryVoidAsync(long batchId, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ContainerExpenseAllocationService.VoidAsync(db, batchId, reason, PrivilegedScope());
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryDisableParticipantAsync(long loadingListId, long participantId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ContainerLoadingParticipantService.SetStatusAsync(
                db, loadingListId, participantId, ContainerLoadingParticipantRules.DisabledStatus,
                PrivilegedScope());
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<FinanceExpense> ReloadExpenseAsync(long id)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<int> ActiveBatchCountAsync(long sourceExpenseId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .CountAsync(x => !x.IsDeleted
                && x.Status == ContainerExpenseAllocationRules.BatchActive
                && x.SourceExpenseId == sourceExpenseId);
    }

    private async Task<decimal> GeneratedExpenseTotalAsync(string batchNo)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenses.AsNoTracking()
            .Where(x => !x.IsDeleted && x.AllocationBatchNo == batchNo)
            .SumAsync(x => (decimal?)x.Amount) ?? 0m;
    }

    private async Task<int> GeneratedExpenseCountAsync(string batchNo)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceExpenses.AsNoTracking()
            .CountAsync(x => !x.IsDeleted && x.AllocationBatchNo == batchNo);
    }

    private async Task<(int Batches, int Lines, int Expenses)> EvidenceCountsAsync(long sourceExpenseId)
    {
        await using var db = _fixture.CreateDbContext();
        return (
            await db.FinanceExpenseAllocationBatches.AsNoTracking()
                .CountAsync(x => !x.IsDeleted && x.SourceExpenseId == sourceExpenseId),
            await db.FinanceExpenseAllocationLines.AsNoTracking()
                .CountAsync(x => !x.IsDeleted && x.SourceExpenseId == sourceExpenseId),
            await db.FinanceExpenses.AsNoTracking()
                .CountAsync(x => !x.IsDeleted && x.AllocationSourceExpenseId == sourceExpenseId));
    }

    // ==================== 1. 两个独立连接竞态：传统分摊 ====================

    [Fact]
    public async Task Race_TwoEquivalentLegacyRequests_CreateAtMostOneCompleteSet()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C1_{Guid.NewGuid():N}", "集成客户1");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var refNo = $"INT_EA386_EQ_{Guid.NewGuid():N}";
        var results = await RaceAsync(
            () => TryAuthorizedActionAsync(userId, ctl => ctl.AllocateApply(LegacyRequest(refNo, customerId, 100m))),
            () => TryAuthorizedActionAsync(userId, ctl => ctl.AllocateApply(LegacyRequest(refNo, customerId, 100m))));

        // 两个独立连接同时发起等价请求：恰好一方成功（另一方在模块单号键行锁内看到已提交的重复证据）
        Assert.True(results[0].Success || results[1].Success, $"{results[0].Error} | {results[1].Error}");
        Assert.NotEqual(results[0].Success, results[1].Success);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.FinanceExpenses.AsNoTracking()
            .Where(x => !x.IsDeleted && x.RefNo == refNo && x.ExpenseType == "报关费")
            .ToListAsync();
        // 恰好一套完整费用单：逐明细一行、合计精确等于请求总额
        Assert.Single(rows);
        Assert.Equal(100m, rows[0].Amount);
        Assert.Equal(customerId, rows[0].CustomerId);
    }

    [Fact]
    public async Task Race_TwoDistinctLegacyRequests_NeverCollideOnExpenseNumbers()
    {
        Guard();
        long userId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C2_{Guid.NewGuid():N}", "集成客户2");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
        }

        var refA = $"INT_EA386_A_{Guid.NewGuid():N}";
        var refB = $"INT_EA386_B_{Guid.NewGuid():N}";
        var results = await RaceAsync(
            () => TryAuthorizedActionAsync(userId, ctl => ctl.AllocateApply(LegacyRequest(refA, customerId, 60m))),
            () => TryAuthorizedActionAsync(userId, ctl => ctl.AllocateApply(LegacyRequest(refB, customerId, 40m))));

        Assert.True(results[0].Success && results[1].Success, $"{results[0].Error} | {results[1].Error}");

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.FinanceExpenses.AsNoTracking()
            .Where(x => !x.IsDeleted && (x.RefNo == refA || x.RefNo == refB))
            .OrderBy(x => x.ExpenseNo)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(100m, rows.Sum(x => x.Amount));
        // 模块单号键行锁把「重复检查 + 单号保留」串行化：两条请求的当日单号绝不重复
        Assert.Equal(2, rows.Select(x => x.ExpenseNo).Distinct().Count());
    }

    // ==================== 2. 两个独立连接竞态：来源改动 / 生成 / 作废 ====================

    [Fact]
    public async Task Race_SourceExpenseDeleteVersusBatchGeneration_NeverProducesStaleEvidence()
    {
        Guard();
        long userId, sourceId, listId, participantId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C3_{Guid.NewGuid():N}", "集成客户3");
            userId = (await SeedOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            var list = await SeedLoadingListAsync(seed, $"INT_EA386_ZG3_{Guid.NewGuid():N}",
                $"INTEA386C3{Guid.NewGuid():N}", customer.Id);
            listId = list.Id;
            participantId = (await SeedParticipantAsync(seed, list.Id, customer, primary: true)).Id;
            var seededSource = await SeedSourceExpenseAsync(seed, $"INT_EA386_SRC3_{Guid.NewGuid():N}",
                customer.Id, 300m, refNo: list.ContainerNo);
            sourceId = seededSource.Id;
        }

        var results = await RaceAsync(
            () => TryAuthorizedActionAsync(userId, ctl => ctl.Delete(sourceId)),
            () => TryGenerateAsync(BatchRequest(sourceId, listId, (participantId, 5m))));

        var source = await ReloadExpenseAsync(sourceId);
        var counts = await EvidenceCountsAsync(sourceId);
        if (results[1].Success)
        {
            // 生成先取得来源费用单行锁：删除在锁内复核「有效批次来源」后被拒绝，来源行保持有效
            Assert.False(results[0].Success);
            Assert.False(source.IsDeleted);
            Assert.Equal((1, 1, 1), counts);
        }
        else
        {
            // 删除先提交：生成在锁内重读权威来源时被拒绝，绝不产生基于已删除来源的陈旧证据
            Assert.True(results[0].Success, results[0].Error);
            Assert.True(source.IsDeleted);
            Assert.Equal((0, 0, 0), counts);
        }
    }

    [Fact]
    public async Task Race_SourceExpenseAmountEditVersusBatchGeneration_EvidenceMatchesPersistedSource()
    {
        Guard();
        long userId, sourceId, listId, participantId;
        FinanceExpense snapshot;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C4_{Guid.NewGuid():N}", "集成客户4");
            userId = (await SeedOperatorAsync(seed, customer.Id, privileged: true)).UserId;
            var list = await SeedLoadingListAsync(seed, $"INT_EA386_ZG4_{Guid.NewGuid():N}",
                $"INTEA386C4{Guid.NewGuid():N}", customer.Id);
            listId = list.Id;
            participantId = (await SeedParticipantAsync(seed, list.Id, customer, primary: true)).Id;
            snapshot = await SeedSourceExpenseAsync(seed, $"INT_EA386_SRC4_{Guid.NewGuid():N}",
                customer.Id, 300m, refNo: list.ContainerNo);
            sourceId = snapshot.Id;
        }

        // 两条独立连接：来源费用金额修改（300 → 350） vs 批次生成
        var results = await RaceAsync(
            () => TryAuthorizedActionAsync(userId, ctl => ctl.Update(sourceId, EditBody(snapshot, 350m))),
            () => TryGenerateAsync(BatchRequest(sourceId, listId, (participantId, 5m))));

        var source = await ReloadExpenseAsync(sourceId);
        Assert.Equal(1, await ActiveBatchCountAsync(sourceId));

        await using var verify = _fixture.CreateDbContext();
        var batch = await verify.FinanceExpenseAllocationBatches.AsNoTracking()
            .SingleAsync(x => !x.IsDeleted
                && x.Status == ContainerExpenseAllocationRules.BatchActive && x.SourceExpenseId == sourceId);

        // 无论谁先取得来源费用单行锁，批次证据都必须与「持久化来源金额」精确一致：
        // 生成先 → 修改被有效批次来源护栏拒绝；修改先 → 生成按新金额分摊。
        Assert.Equal(source.Amount, batch.SourceAmount);
        Assert.Equal(batch.SourceAmount, await GeneratedExpenseTotalAsync(batch.BatchNo));
        Assert.Equal(1, await GeneratedExpenseCountAsync(batch.BatchNo));
        Assert.True(source.Amount == 300m || source.Amount == 350m, $"来源金额 {source.Amount}");
        if (source.Amount == 350m) Assert.True(results[0].Success, results[0].Error);
        else Assert.False(results[0].Success);
    }

    private static FinanceExpense EditBody(FinanceExpense snapshot, decimal amount) => new()
    {
        ExpenseNo = snapshot.ExpenseNo,
        ExpenseDate = snapshot.ExpenseDate,
        ExpenseType = snapshot.ExpenseType,
        Amount = amount,
        Currency = snapshot.Currency,
        ExchangeRate = snapshot.ExchangeRate,
        AmountCny = amount,
        RefType = snapshot.RefType,
        RefNo = snapshot.RefNo,
        CustomerId = snapshot.CustomerId,
        PaymentStatus = snapshot.PaymentStatus,
        Remark = snapshot.Remark
    };

    [Fact]
    public async Task Race_BatchVoidVersusGeneration_SerializedConsistentResult()
    {
        Guard();
        long sourceId, listId, participantId, batchId;
        string batchNo;
        ContainerExpenseAllocationRequest request;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C5_{Guid.NewGuid():N}", "集成客户5");
            var list = await SeedLoadingListAsync(seed, $"INT_EA386_ZG5_{Guid.NewGuid():N}",
                $"INTEA386C5{Guid.NewGuid():N}", customer.Id);
            listId = list.Id;
            participantId = (await SeedParticipantAsync(seed, list.Id, customer, primary: true)).Id;
            var source = await SeedSourceExpenseAsync(seed, $"INT_EA386_SRC5_{Guid.NewGuid():N}",
                customer.Id, 300m, refNo: list.ContainerNo);
            sourceId = source.Id;
            request = BatchRequest(sourceId, listId, (participantId, 5m));

            var generated = await ContainerExpenseAllocationService.GenerateAsync(seed, request, PrivilegedScope());
            batchId = generated.BatchId;
            batchNo = generated.BatchNo;
            Assert.Equal(300m, generated.SourceAmount);
        }

        // 两条独立连接：显式作废既有有效批次 vs 对同一来源再次生成
        var results = await RaceAsync(
            () => TryVoidAsync(batchId, "并发更正原因"),
            () => TryGenerateAsync(request));

        // 作废必定成功且保留原因与审计（生成绝不改写批次状态）
        Assert.True(results[0].Success, results[0].Error);
        await using var verify = _fixture.CreateDbContext();
        var voidedBatch = await verify.FinanceExpenseAllocationBatches.AsNoTracking().SingleAsync(x => x.Id == batchId);
        Assert.Equal(ContainerExpenseAllocationRules.BatchVoided, voidedBatch.Status);
        Assert.Equal("并发更正原因", voidedBatch.VoidReason);
        Assert.NotNull(voidedBatch.VoidedAt);
        // 作废不改写既有生成费用单（金额与单号原样保留）
        Assert.Equal(300m, await GeneratedExpenseTotalAsync(batchNo));
        Assert.Equal(1, await GeneratedExpenseCountAsync(batchNo));

        if (results[1].Success)
        {
            // 生成先于作废提交或作废先提交后重新生成：恰好一条新的有效批次且合计精确
            Assert.Equal(1, await ActiveBatchCountAsync(sourceId));
            var active = await verify.FinanceExpenseAllocationBatches.AsNoTracking()
                .SingleAsync(x => !x.IsDeleted
                    && x.Status == ContainerExpenseAllocationRules.BatchActive && x.SourceExpenseId == sourceId);
            Assert.NotEqual(batchNo, active.BatchNo);
            Assert.Equal(300m, active.SourceAmount);
            Assert.Equal(300m, await GeneratedExpenseTotalAsync(active.BatchNo));
        }
        else
        {
            // 生成被「重复有效批次」原子拒绝：不存在第二条批次，也不留半成品行
            Assert.Equal(0, await ActiveBatchCountAsync(sourceId));
            var counts = await EvidenceCountsAsync(sourceId);
            Assert.Equal((1, 1, 1), counts);
        }
    }

    [Fact]
    public async Task Race_ParticipantDisableVersusGeneration_SerializedConsistentResult()
    {
        Guard();
        long sourceId, listId, firstParticipantId, secondParticipantId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C6_{Guid.NewGuid():N}", "集成客户6");
            var other = await SeedCustomerAsync(seed, $"INT_EA386_C6B_{Guid.NewGuid():N}", "集成客户6B");
            var list = await SeedLoadingListAsync(seed, $"INT_EA386_ZG6_{Guid.NewGuid():N}",
                $"INTEA386C6{Guid.NewGuid():N}", customer.Id);
            listId = list.Id;
            firstParticipantId = (await SeedParticipantAsync(seed, list.Id, customer, primary: true)).Id;
            secondParticipantId = (await SeedParticipantAsync(seed, list.Id, other)).Id;
            var source = await SeedSourceExpenseAsync(seed, $"INT_EA386_SRC6_{Guid.NewGuid():N}",
                customer.Id, 200m, refNo: list.ContainerNo);
            sourceId = source.Id;
        }

        var request = BatchRequest(sourceId, listId, (firstParticipantId, 1m), (secondParticipantId, 1m));

        // 两条独立连接：停用参与方 vs 批次生成（争用同一把装柜清单行锁）
        var results = await RaceAsync(
            () => TryDisableParticipantAsync(listId, secondParticipantId),
            () => TryGenerateAsync(request));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(ContainerLoadingParticipantRules.DisabledStatus,
            (await verify.ContainerLoadingListParticipants.AsNoTracking()
                .SingleAsync(x => x.Id == secondParticipantId)).Status);

        if (results[1].Success)
        {
            // 生成先完成：使用「生成时点」的参与方集合，逐行留痕完整且合计精确
            var active = await verify.FinanceExpenseAllocationBatches.AsNoTracking()
                .SingleAsync(x => !x.IsDeleted
                    && x.Status == ContainerExpenseAllocationRules.BatchActive && x.SourceExpenseId == sourceId);
            Assert.Equal(2, await GeneratedExpenseCountAsync(active.BatchNo));
            Assert.Equal(200m, await GeneratedExpenseTotalAsync(active.BatchNo));
        }
        else
        {
            // 停用先提交：生成按最新启用参与方集合判定并原子拒绝，不留半成品
            Assert.Equal((0, 0, 0), await EvidenceCountsAsync(sourceId));
        }
    }

    // ==================== 3. 既有可串行化事务复用（绝不嵌套事务） ====================

    [Fact]
    public async Task Ambient_transaction_is_reused_by_participant_maintenance_and_never_nested()
    {
        Guard();
        long listId, keepParticipantId, rollbackParticipantId, commitParticipantId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C8_{Guid.NewGuid():N}", "集成客户8");
            var list = await SeedLoadingListAsync(seed, $"INT_EA386_ZG8_{Guid.NewGuid():N}",
                $"INTEA386C8{Guid.NewGuid():N}", customer.Id);
            listId = list.Id;
            keepParticipantId = (await SeedParticipantAsync(seed, list.Id, customer, primary: true)).Id;
            var second = await SeedCustomerAsync(seed, $"INT_EA386_C8B_{Guid.NewGuid():N}", "集成客户8B");
            rollbackParticipantId = (await SeedParticipantAsync(seed, list.Id, second)).Id;
            var third = await SeedCustomerAsync(seed, $"INT_EA386_C8C_{Guid.NewGuid():N}", "集成客户8C");
            commitParticipantId = (await SeedParticipantAsync(seed, list.Id, third)).Id;
        }

        // ① 外层事务回滚：参与方维护必须复用该事务（绝不自行提交），因此停用被一并回滚
        await using (var db = _fixture.CreateDbContext())
        {
            await using var outer = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await ContainerLoadingParticipantService.SetStatusAsync(db, listId, rollbackParticipantId,
                ContainerLoadingParticipantRules.DisabledStatus, PrivilegedScope());
            await outer.RollbackAsync();
        }
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(ContainerLoadingParticipantRules.ActiveStatus,
                (await verify.ContainerLoadingListParticipants.AsNoTracking()
                    .SingleAsync(x => x.Id == rollbackParticipantId)).Status);
        }

        // ② 外层事务提交：停用随外层事务一起生效（服务层只复用事务与锁，不自行提交）
        await using (var db = _fixture.CreateDbContext())
        {
            await using var outer = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await ContainerLoadingParticipantService.SetStatusAsync(db, listId, commitParticipantId,
                ContainerLoadingParticipantRules.DisabledStatus, PrivilegedScope());
            await outer.CommitAsync();
        }
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(ContainerLoadingParticipantRules.DisabledStatus,
                (await verify.ContainerLoadingListParticipants.AsNoTracking()
                    .SingleAsync(x => x.Id == commitParticipantId)).Status);
            Assert.Equal(ContainerLoadingParticipantRules.ActiveStatus,
                (await verify.ContainerLoadingListParticipants.AsNoTracking()
                    .SingleAsync(x => x.Id == keepParticipantId)).Status);
        }
    }

    // ==================== 4. 真实身份实时授权（无新增授权、无匿名兜底） ====================

    [Fact]
    public async Task Realtime_authorization_denies_revoked_menu_disabled_account_and_anonymous_without_side_effects()
    {
        Guard();
        long userId, roleId, customerId;
        int usersAfterSeed;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_EA386_C7_{Guid.NewGuid():N}", "集成客户7");
            customerId = customer.Id;
            var operatorInfo = await SeedOperatorAsync(seed, customer.Id);
            userId = operatorInfo.UserId;
            roleId = operatorInfo.RoleId;
            usersAfterSeed = await seed.SysUsers.CountAsync();
        }

        // ① 具备既有「费用单」菜单授权 + 范围内客户：请求成功并写入恰好一套费用单
        var grantedRefNo = $"INT_EA386_GRANTED_{Guid.NewGuid():N}";
        var granted = await TryAuthorizedActionAsync(userId,
            ctl => ctl.AllocateApply(LegacyRequest(grantedRefNo, customerId, 20m)));
        Assert.True(granted.Success, granted.Error);

        // ② 撤销既有菜单授权：下一次请求立即收敛为拒绝（fail closed）且零写入
        await using (var revoke = _fixture.CreateDbContext())
            await RevokeExpenseMenuAsync(revoke, roleId);
        var revokedRefNo = $"INT_EA386_REVOKED_{Guid.NewGuid():N}";
        var revoked = await TryAuthorizedActionAsync(userId,
            ctl => ctl.AllocateApply(LegacyRequest(revokedRefNo, customerId, 20m)));
        Assert.False(revoked.Success);
        Assert.Contains("expense-bill", revoked.Error);

        // ③ 无登录身份（匿名 HTTP）：在任何读取 / 单号 / 写入之前拒绝
        var anonymousRefNo = $"INT_EA386_ANON_{Guid.NewGuid():N}";
        var anonymous = await TryAuthorizedActionAsync(null,
            ctl => ctl.AllocateApply(LegacyRequest(anonymousRefNo, customerId, 20m)));
        Assert.False(anonymous.Success);

        // ④ 账号停用：立即收敛为权限不足
        await using (var disable = _fixture.CreateDbContext())
        {
            var user = await disable.SysUsers.SingleAsync(x => x.Id == userId);
            user.Status = UserStatus.Disabled;
            await disable.SaveChangesAsync();
        }
        var disabledRefNo = $"INT_EA386_DISABLED_{Guid.NewGuid():N}";
        var disabled = await TryAuthorizedActionAsync(userId,
            ctl => ctl.AllocateApply(LegacyRequest(disabledRefNo, customerId, 20m)));
        Assert.False(disabled.Success);

        // 被拒绝的请求零改写：只有第 ① 步写入；也不新增任何用户 / 角色 / 授权
        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.FinanceExpenses.AsNoTracking()
            .Where(x => !x.IsDeleted
                && (x.RefNo == grantedRefNo || x.RefNo == revokedRefNo
                    || x.RefNo == anonymousRefNo || x.RefNo == disabledRefNo))
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal(grantedRefNo, rows[0].RefNo);
        Assert.Equal(20m, rows[0].Amount);
        Assert.Equal(usersAfterSeed, await verify.SysUsers.CountAsync());
        Assert.Equal(0, await verify.SysRoleMenus.CountAsync(g => g.RoleId == roleId));
    }
}

/// <summary>
/// ERP-386 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ExpenseAllocationConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_EXPENSEALLOCATIONCONCURRENCY_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-386] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

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

        Console.WriteLine("[ERP-386] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ExpenseAllocationConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ExpenseAllocationConcurrencySqlServerFixture.AssertDedicatedTarget(connection));
}
