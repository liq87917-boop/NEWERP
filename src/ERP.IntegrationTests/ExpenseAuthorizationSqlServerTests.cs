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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-385 费用单 / 分摊批次 / 分摊证据实时授权与权威客户范围 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 / 服务</b>：以既有「费用单」（<c>expense-bill</c>）菜单授权、既有业务员数据范围与
/// 真实 <see cref="ExpenseBillController"/> / <see cref="ContainerExpenseAllocationEvidenceController"/> 验证
/// 本人 / 他人 / 混源 / 无主 / 撤销授权 / 禁用账号场景，以及被拒绝的编辑 / 删除 / 批量删除 / 作废绝不改写原行、状态与审计；</item>
/// <item><b>两个独立连接竞态</b>：①同一来源费用 + 装柜清单的并发分摊生成（唯一索引兜底）只落一条有效批次且无部分行；
/// ②两个独立连接并发执行范围外修改与删除，二者都被 fail closed 且原行 / 审计不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ExpenseAuthorizationSqlServerTests : IClassFixture<ExpenseAuthorizationSqlServerFixture>
{
    private readonly ExpenseAuthorizationSqlServerFixture _fixture;

    public ExpenseAuthorizationSqlServerTests(ExpenseAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ExpenseAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static ExpenseBillController NewExpenseController(
        ErpDbContext db, SalespersonDataScope? scope, long? userId)
    {
        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        if (scope is not null)
            httpContext.Items[ExpenseRequestAuthorizationFilter.ScopeItemKey] = scope;

        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
        };
        return controller;
    }

    private static SalespersonDataScope RestrictedScope(long salesmanId, params long[] allowedCustomerIds)
        => new()
        {
            IsPrivileged = false,
            SalesmanId = salesmanId,
            AllowedCustomerIds = allowedCustomerIds.ToHashSet()
        };

    private static SalespersonDataScope PrivilegedScope()
        => new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

    private static async Task<(bool Success, string Error)> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两个独立连接/上下文并发执行（同一栅栏同时放行）。</summary>
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

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1)
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

    private static async Task<BaseEmployee> SeedEmployeeAsync(ErpDbContext db, string tag)
    {
        var code = $"INT_E385_EMP_{tag}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    /// <summary>播种受限业务员：登录账号 = 员工编码 + 既有「费用单」菜单授权（复用已部署菜单，不新增权限）。</summary>
    private static async Task<SysUser> SeedOperatorAsync(ErpDbContext db, BaseEmployee employee)
    {
        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = employee.EmployeeCode,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E385_ROLE_{employee.EmployeeCode}",
            RoleCode = $"INT_E385_{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == ExpenseAuthorizationRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<FinanceExpense> SeedExpenseAsync(
        ErpDbContext db, string expenseNo, long? customerId, decimal amount = 100m,
        string refType = "客户", string? refNo = null)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = "CNY",
            ExchangeRate = 1m,
            AmountCny = amount,
            RefType = refType,
            RefNo = refNo ?? expenseNo,
            CustomerId = customerId,
            CustomerName = customerId.HasValue ? $"客户{customerId}" : string.Empty,
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string listNo, string containerNo, long customerId)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = DateTime.Today,
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = 10m,
            TotalWeight = 500m,
            TotalVolume = 3.5m
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

    // ==================== 1. 本人 / 他人 / 混源 / 无主 与拒绝不改写 ====================

    [Fact]
    public async Task Own_foreign_mixed_and_unknown_scope_deny_without_mutating_rows()
    {
        Guard();
        var tag = Tag();
        long ownCustomerId, foreignExpenseId, ownExpenseId;
        decimal foreignAmountBefore;
        DateTime? foreignUpdatedBefore;
        long operatorUserId, mixedBatchId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            operatorUserId = (await SeedOperatorAsync(seed, employee)).Id;

            var own = await SeedCustomerAsync(seed, $"INT_E385_OWN_{tag}", "本人客户", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E385_OTH_{tag}", "他人客户");
            ownCustomerId = own.Id;

            ownExpenseId = (await SeedExpenseAsync(seed, $"EXP-E385-OWN-{tag}", own.Id, 120m)).Id;
            var foreignRow = await SeedExpenseAsync(seed, $"EXP-E385-OTH-{tag}", foreign.Id, 250m);
            foreignExpenseId = foreignRow.Id;
            foreignAmountBefore = foreignRow.Amount;
            foreignUpdatedBefore = foreignRow.UpdatedAt;
            await SeedExpenseAsync(seed, $"EXP-E385-NONE-{tag}", null, 90m);

            // 同一柜：本人 + 他人参与方；用真实服务生成混源批次（逐行含他人客户）
            var list = await SeedLoadingListAsync(seed, $"INT_E385_L_{tag}", $"TCLU-E385-{tag}", own.Id);
            var ownParticipant = await SeedParticipantAsync(seed, list.Id, own, primary: true);
            var foreignParticipant = await SeedParticipantAsync(seed, list.Id, foreign);
            var source = await SeedExpenseAsync(seed, $"EXP-E385-SRC-{tag}", own.Id, 1000m, "拼柜", list.ContainerNo);

            var generated = await ContainerExpenseAllocationService.GenerateAsync(
                seed,
                new ContainerExpenseAllocationRequest
                {
                    SourceExpenseId = source.Id,
                    LoadingListId = list.Id,
                    AllocationMethod = "按体积",
                    Lines = new List<ContainerExpenseAllocationBasisDto>
                    {
                        new() { ParticipantId = ownParticipant.Id, BasisValue = 6m },
                        new() { ParticipantId = foreignParticipant.Id, BasisValue = 4m }
                    }
                },
                PrivilegedScope());
            mixedBatchId = generated.BatchId;
        }

        var scope = RestrictedScope(operatorUserId, ownCustomerId);

        await using (var own = _fixture.CreateDbContext())
        {
            // 列表：在计数与分页之前按本人客户过滤（排除他人行与无主行）
            var page = await NewExpenseController(own, scope, operatorUserId).GetPaged(new PageQuery());
            var ok = Assert.IsType<OkObjectResult>(page);
            var data = Assert.IsType<ApiResponse<PagedResult<FinanceExpense>>>(ok.Value).Data!;
            Assert.Contains(data.Items, x => x.Id == ownExpenseId);
            Assert.DoesNotContain(data.Items, x => x.Id == foreignExpenseId);
            Assert.DoesNotContain(data.Items, x => x.CustomerId is null);

            // 详情：他人与无主一律拒绝
            await Assert.ThrowsAsync<BusinessException>(() =>
                NewExpenseController(own, scope, operatorUserId).GetById(foreignExpenseId));

            // 修改 / 删除 / 批量删除：一律拒绝且不写库
            await Assert.ThrowsAsync<BusinessException>(() =>
                NewExpenseController(own, scope, operatorUserId).Update(foreignExpenseId, new FinanceExpense
                {
                    Id = foreignExpenseId,
                    ExpenseNo = $"EXP-E385-TRY-{tag}",
                    ExpenseDate = DateTime.Today,
                    ExpenseType = "报关费",
                    Amount = 999m,
                    Currency = "CNY",
                    ExchangeRate = 1m,
                    CustomerId = ownCustomerId,
                    RefType = "客户",
                    RefNo = $"EXP-E385-TRY-{tag}",
                    AllocationBase = "不分摊"
                }));
            await Assert.ThrowsAsync<BusinessException>(() =>
                NewExpenseController(own, scope, operatorUserId).Delete(foreignExpenseId));
            await Assert.ThrowsAsync<BusinessException>(() =>
                NewExpenseController(own, scope, operatorUserId)
                    .BatchDelete(new List<long> { ownExpenseId, foreignExpenseId }));

            // 批次台账 / 详情 / 作废：混源批次对受限账号不可见、不可作废
            var ledger = await ContainerExpenseAllocationService.ListBatchesAsync(
                own, new ContainerExpenseAllocationBatchQuery(), scope);
            Assert.DoesNotContain(ledger.Items, b => b.Id == mixedBatchId);
            await Assert.ThrowsAsync<BusinessException>(() =>
                ContainerExpenseAllocationService.GetBatchAsync(own, mixedBatchId, scope));
            await Assert.ThrowsAsync<BusinessException>(() =>
                ContainerExpenseAllocationService.VoidAsync(own, mixedBatchId, "越界作废", scope));
        }

        // 复核：被拒绝的操作绝不改写原行 / 状态 / 审计
        await using (var verify = _fixture.CreateDbContext())
        {
            var foreignAfter = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == foreignExpenseId);
            Assert.False(foreignAfter.IsDeleted);
            Assert.Equal(foreignAmountBefore, foreignAfter.Amount);
            Assert.Equal(foreignUpdatedBefore, foreignAfter.UpdatedAt);

            var ownAfter = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == ownExpenseId);
            Assert.False(ownAfter.IsDeleted);

            var batch = await verify.FinanceExpenseAllocationBatches.AsNoTracking()
                .SingleAsync(x => x.Id == mixedBatchId);
            Assert.Equal(ContainerExpenseAllocationRules.BatchActive, batch.Status);
            Assert.Null(batch.VoidedAt);
            Assert.Equal(string.Empty, batch.VoidReason);

            // 特权账号仍可读取该混源批次（保留历史访问）
            var privilegedLedger = await ContainerExpenseAllocationService.ListBatchesAsync(
                verify, new ContainerExpenseAllocationBatchQuery(), PrivilegedScope());
            Assert.Contains(privilegedLedger.Items, b => b.Id == mixedBatchId);
        }
    }

    // ==================== 2. 撤销授权 / 禁用 / 删除账号 fail closed ====================

    [Fact]
    public async Task Revoked_menu_disabled_and_deleted_accounts_fail_closed()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, menuId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            userId = (await SeedOperatorAsync(seed, employee)).Id;
            roleId = await seed.SysUserRoles.AsNoTracking()
                .Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).FirstAsync();
            menuId = await seed.SysMenus.AsNoTracking()
                .Where(m => !m.IsDeleted && m.MenuCode == ExpenseAuthorizationRules.RequiredMenuCode)
                .Select(m => m.Id).FirstAsync();

            // 授权存活时实时解析可见
            var scope = await ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(seed, userId);
            Assert.False(scope.IsPrivileged);

            // 撤销既有菜单授权 → 下一次请求立即收敛（无缓存）
            var grant = await seed.SysRoleMenus.FirstAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId);
            grant.IsDeleted = true;
            await seed.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var revoked = await Assert.ThrowsAsync<BusinessException>(() =>
                ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId));
            Assert.Equal(ErrorCodes.Forbidden, revoked.Code);

            // 恢复授权并禁用账号 → 权限不足
            var grant = await db.SysRoleMenus.FirstAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId);
            grant.IsDeleted = false;
            var user = await db.SysUsers.FirstAsync(u => u.Id == userId);
            user.Status = UserStatus.Disabled;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var disabled = await Assert.ThrowsAsync<BusinessException>(() =>
                ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId));
            Assert.Equal(ErrorCodes.Forbidden, disabled.Code);

            var user = await db.SysUsers.FirstAsync(u => u.Id == userId);
            user.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
                ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId));
            Assert.Equal(ErrorCodes.Unauthorized, deleted.Code);
        }
    }

    // ==================== 3. 两个独立连接竞态 ====================

    private async Task<(bool Success, string Error)> GenerateOnceAsync(
        ContainerExpenseAllocationRequest request, SalespersonDataScope scope)
        => await TryAsync(async () =>
        {
            await using var db = _fixture.CreateDbContext();
            await ContainerExpenseAllocationService.GenerateAsync(db, request, scope);
        });

    [Fact]
    public async Task Race_concurrent_generate_for_same_source_keeps_single_active_batch()
    {
        Guard();
        var tag = Tag();
        long loadingListId, sourceId, participantId, ownCustomerId, operatorUserId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            operatorUserId = (await SeedOperatorAsync(seed, employee)).Id;
            var own = await SeedCustomerAsync(seed, $"INT_E385_RC1_{tag}", "并发客户1", employee.Id);
            ownCustomerId = own.Id;
            var list = await SeedLoadingListAsync(seed, $"INT_E385_RC1L_{tag}", $"TCLU-E385-RC1-{tag}", own.Id);
            var participant = await SeedParticipantAsync(seed, list.Id, own, primary: true);
            var source = await SeedExpenseAsync(
                seed, $"EXP-E385-RC1-{tag}", own.Id, 1000m, "拼柜", list.ContainerNo);
            loadingListId = list.Id;
            sourceId = source.Id;
            participantId = participant.Id;
        }

        var request = new ContainerExpenseAllocationRequest
        {
            SourceExpenseId = sourceId,
            LoadingListId = loadingListId,
            AllocationMethod = "按体积",
            Lines = new List<ContainerExpenseAllocationBasisDto>
            {
                new() { ParticipantId = participantId, BasisValue = 1m }
            }
        };
        var scope = RestrictedScope(operatorUserId, ownCustomerId);

        // 两个独立连接（各自 DbContext）在同一栅栏后并发生成同一来源费用 + 装柜清单的分摊批次
        var results = await RaceAsync(
            () => GenerateOnceAsync(request, scope),
            () => GenerateOnceAsync(request, scope));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var db = _fixture.CreateDbContext();
        var batch = Assert.Single(await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .Where(b => b.SourceExpenseId == sourceId && b.LoadingListId == loadingListId)
            .ToListAsync());
        Assert.Equal(ContainerExpenseAllocationRules.BatchActive, batch.Status);

        // 无部分行：批次逐行留痕与生成的费用单行各自只有一条，且金额合计等于来源金额
        var lines = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(l => l.BatchId == batch.Id).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(batch.SourceAmount, lines.Sum(l => l.AllocatedAmount));
        Assert.Equal(1, await db.FinanceExpenses.AsNoTracking()
            .CountAsync(e => !e.IsDeleted && e.AllocationBatchNo == batch.BatchNo));
    }

    [Fact]
    public async Task Race_concurrent_foreign_update_and_delete_fail_closed_without_mutation()
    {
        Guard();
        var tag = Tag();
        long foreignExpenseId, ownCustomerId, operatorUserId;
        decimal amountBefore;
        DateTime? updatedBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            operatorUserId = (await SeedOperatorAsync(seed, employee)).Id;
            var own = await SeedCustomerAsync(seed, $"INT_E385_RC2_{tag}", "本人客户2", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E385_RC2F_{tag}", "他人客户2");
            ownCustomerId = own.Id;
            var row = await SeedExpenseAsync(seed, $"EXP-E385-RC2-{tag}", foreign.Id, 250m);
            foreignExpenseId = row.Id;
            amountBefore = row.Amount;
            updatedBefore = row.UpdatedAt;
        }

        var scope = RestrictedScope(operatorUserId, ownCustomerId);

        // 两个独立连接并发执行范围外「修改」与「删除」：二者都必须 fail closed
        var results = await RaceAsync(
            () => TryAsync(async () =>
            {
                await using var db = _fixture.CreateDbContext();
                await NewExpenseController(db, scope, operatorUserId).Update(foreignExpenseId, new FinanceExpense
                {
                    Id = foreignExpenseId,
                    ExpenseNo = $"EXP-E385-RC2-TRY-{tag}",
                    ExpenseDate = DateTime.Today,
                    ExpenseType = "报关费",
                    Amount = 999m,
                    Currency = "CNY",
                    ExchangeRate = 1m,
                    CustomerId = ownCustomerId,
                    RefType = "客户",
                    RefNo = $"EXP-E385-RC2-TRY-{tag}",
                    AllocationBase = "不分摊"
                });
            }),
            () => TryAsync(async () =>
            {
                await using var db = _fixture.CreateDbContext();
                await NewExpenseController(db, scope, operatorUserId).Delete(foreignExpenseId);
            }));

        Assert.All(results, r => Assert.False(r.Success, r.Error));

        await using var verify = _fixture.CreateDbContext();
        var after = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == foreignExpenseId);
        Assert.False(after.IsDeleted);
        Assert.Equal(amountBefore, after.Amount);
        Assert.Equal(updatedBefore, after.UpdatedAt);
    }
}

/// <summary>
/// ERP-385 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class ExpenseAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP385";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-385] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-385] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ExpenseAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ExpenseAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
