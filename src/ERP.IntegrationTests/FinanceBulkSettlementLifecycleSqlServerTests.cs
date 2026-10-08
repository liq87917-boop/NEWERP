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
/// ERP-387 散货结算单生命周期护栏 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器</b>：以既有「散货结算单」（<c>bulk-settlement</c>）菜单授权 + 既有业务员数据范围口径驱动真实
/// <see cref="FinanceBulkSettlementController"/>，验证实时启用身份、客户范围（own / foreign / revoked / disabled）、
/// 金额（正总金额 / 非负海运费，既有存储精度）、真实启用客户校验，以及被拒编辑不改写（含审计时间戳）；</item>
/// <item><b>软删除语义</b>：仅待提交可删，删除只置软删除标记、不物理删除；</item>
/// <item><b>两个独立连接竞态</b>：并发「修改 vs 提交」「审核 vs 取消」经同一把散货结算单行锁串行化后给出唯一一致结果，
/// 绝不出现半成品写入或丢失更新，也不产生任何资金记账 / 收款 / 库存流水。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class FinanceBulkSettlementLifecycleSqlServerTests
    : IClassFixture<FinanceBulkSettlementLifecycleSqlServerFixture>
{
    private readonly FinanceBulkSettlementLifecycleSqlServerFixture _fixture;

    public FinanceBulkSettlementLifecycleSqlServerTests(
        FinanceBulkSettlementLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceBulkSettlementLifecycleSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static FinanceBulkSettlementController NewBulkController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceBulkSettlementController(db, new DocumentNumberService(db));
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
        long? userId, Func<FinanceBulkSettlementController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewBulkController(db, userId));
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

    private static async Task<FinanceBulkSettlement> SeedSettlementAsync(
        ErpDbContext db, string settlementNo, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var settlement = new FinanceBulkSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = DateTime.Today.AddDays(-2),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "原始备注",
            Status = status
        };
        db.FinanceBulkSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }

    /// <summary>
    /// 播种一个真实登录账号：既有「散货结算单」菜单授权 + 业务员员工映射（把范围内客户分配给它的员工，
    /// 数据范围恰好覆盖该客户，不新增权限模型）；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// </summary>
    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool withSettlementMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp387-op-{Guid.NewGuid():N}";
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
            RoleName = "散货结算操作角色",
            RoleCode = $"Erp387Op-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withSettlementMenu)
            await GrantMenuAsync(db, role.Id, FinanceBulkSettlementLifecycleRules.RequiredMenuCode);

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

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        foreach (var grant in await db.SysRoleMenus
                     .Where(g => g.RoleId == roleId && g.MenuId == menuId && !g.IsDeleted)
                     .ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    // ==================== 只读复核 / 非目标表计数 ====================

    private async Task<FinanceBulkSettlement> ReloadSettlementAsync(long settlementId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinanceBulkSettlements.AsNoTracking().SingleAsync(s => s.Id == settlementId);
    }

    /// <summary>既有资金 / 库存记账表条数合计：本护栏绝不产生新的收款 / 付款 / 费用 / 装柜结算 / 库存流水。</summary>
    private async Task<int> PostingRowCountAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.FinancePayments.AsNoTracking().CountAsync()
            + await db.FinanceReceipts.AsNoTracking().CountAsync()
            + await db.FinanceExpenses.AsNoTracking().CountAsync()
            + await db.FinanceContainerSettlements.AsNoTracking().CountAsync()
            + await db.StockMovements.AsNoTracking().CountAsync();
    }


    // ==================== 1. 真实身份 / 菜单 / 数据范围（own / foreign / revoked / disabled） ====================

    [Fact]
    public async Task Controller_OwnScopeAllowed_AndForeignRevokedDisabledDenied()
    {
        Guard();
        var tag = Tag();
        long ownerId, foreignId, revokedId, revokedRoleId, disabledId;
        long ownSettlementId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var ownerCustomer = await SeedCustomerAsync(seed, $"INT_E387_OWN_{tag}", "本人客户");
            ownerId = (await SeedOperatorAsync(seed, ownerCustomer.Id)).UserId;
            ownSettlementId = (await SeedSettlementAsync(seed, $"INT_E387_SOWN_{tag}", ownerCustomer.Id)).Id;

            // 范围外操作员：其客户范围不含 ownerCustomer（结算单属于 ownerCustomer）
            var foreignCustomer = await SeedCustomerAsync(seed, $"INT_E387_FOR_{tag}", "范围外客户");
            foreignId = (await SeedOperatorAsync(seed, foreignCustomer.Id)).UserId;

            var revokedCustomer = await SeedCustomerAsync(seed, $"INT_E387_REV_{tag}", "撤销菜单客户");
            (revokedId, revokedRoleId) = await SeedOperatorAsync(seed, revokedCustomer.Id);
            await RevokeMenuAsync(seed, revokedRoleId, FinanceBulkSettlementLifecycleRules.RequiredMenuCode);

            var disabledCustomer = await SeedCustomerAsync(seed, $"INT_E387_DIS_{tag}", "禁用账号客户");
            disabledId = (await SeedOperatorAsync(seed, disabledCustomer.Id)).UserId;
            await DisableUserAsync(seed, disabledId);
        }

        var before = await ReloadSettlementAsync(ownSettlementId);
        var postingsBefore = await PostingRowCountAsync();

        // 本人客户：详情可用
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewBulkController(db, ownerId).GetById(ownSettlementId));

        // 范围外客户：详情 / 提交 / 取消拒绝（fail closed）
        // Each simulated HTTP request owns its DbContext. A rolled-back row lock must
        // not leave a tracked RowVersion from an earlier denied request in the next one.
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewBulkController(db, foreignId).GetById(ownSettlementId));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewBulkController(db, foreignId).Submit(ownSettlementId));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewBulkController(db, foreignId).Cancel(ownSettlementId));

        // 撤销菜单授权：拒绝
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewBulkController(db, revokedId).GetById(ownSettlementId));

        // 禁用账号：拒绝
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
                () => NewBulkController(db, disabledId).GetById(ownSettlementId));

        // 被拒请求绝不改写原始结算单（含审计时间戳）与非目标记账表
        var after = await ReloadSettlementAsync(ownSettlementId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.CustomerId, after.CustomerId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(after.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }


    // ==================== 2. 金额 / 客户资格（invalid / disabled / foreign） ====================

    [Fact]
    public async Task Controller_InvalidAmountsAndDisabledCustomer_Denied_NoWrite()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, disabledCustomerId, foreignCustomerId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E387_CINV_{tag}", "校验客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;

            disabledCustomerId = (await SeedCustomerAsync(
                seed, $"INT_E387_CDIS_{tag}", "停用客户", status: 0, empId: customer.EmpId)).Id;
            foreignCustomerId = (await SeedCustomerAsync(
                seed, $"INT_E387_CFOR_{tag}", "范围外客户")).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = NewBulkController(db, userId);

            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(
                new FinanceBulkSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 0m }));
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(
                new FinanceBulkSettlement { CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 100m, FreightCost = -1m }));
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(
                new FinanceBulkSettlement { CustomerId = disabledCustomerId, SettlementDate = DateTime.Today, TotalAmount = 100m }));
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(
                new FinanceBulkSettlement { CustomerId = foreignCustomerId, SettlementDate = DateTime.Today, TotalAmount = 100m }));
            // Unknown customer is outside this restricted operator's scope: deny before existence lookup.
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(
                new FinanceBulkSettlement { CustomerId = 99999999L, SettlementDate = DateTime.Today, TotalAmount = 100m }));
        }

        await using (var db = _fixture.CreateDbContext())
            Assert.Equal(0, await db.FinanceBulkSettlements.AsNoTracking().CountAsync(s => s.CustomerId == customerId));
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    // ==================== 3. 被拒编辑不改写 + 软删除语义 ====================

    [Fact]
    public async Task Controller_DeniedEditUnchanged_AndDeleteIsSoftDeleteOnly()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, disabledCustomerId, foreignCustomerId;
        long settlementId, deletableId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E387_CEDT_{tag}", "编辑客户");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E387_SEDT_{tag}", customer.Id, 1000m, 50m)).Id;
            deletableId = (await SeedSettlementAsync(
                seed, $"INT_E387_SDEL_{tag}", customer.Id, 800m)).Id;

            disabledCustomerId = (await SeedCustomerAsync(
                seed, $"INT_E387_EDIS_{tag}", "停用客户", status: 0, empId: customer.EmpId)).Id;
            foreignCustomerId = (await SeedCustomerAsync(
                seed, $"INT_E387_EFOR_{tag}", "范围外客户")).Id;
        }
        var before = await ReloadSettlementAsync(settlementId);
        var postingsBefore = await PostingRowCountAsync();

        // 非法金额 / 停用客户 / 范围外客户：编辑拒绝且不改写
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => NewBulkController(db, userId).Update(
                settlementId, new FinanceBulkSettlement
                {
                    CustomerId = customerId, SettlementDate = DateTime.Today, TotalAmount = 0m
                }));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => NewBulkController(db, userId).Update(
                settlementId, new FinanceBulkSettlement
                {
                    CustomerId = disabledCustomerId, SettlementDate = DateTime.Today, TotalAmount = 300m
                }));
        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => NewBulkController(db, userId).Update(
                settlementId, new FinanceBulkSettlement
                {
                    CustomerId = foreignCustomerId, SettlementDate = DateTime.Today, TotalAmount = 300m
                }));

        var denied = await ReloadSettlementAsync(settlementId);
        Assert.Equal(before.Status, denied.Status);
        Assert.Equal(before.TotalAmount, denied.TotalAmount);
        Assert.Equal(before.FreightCost, denied.FreightCost);
        Assert.Equal(before.CustomerId, denied.CustomerId);
        Assert.Equal(before.UpdatedAt, denied.UpdatedAt);

        // 删除只置软删除标记：物理行保留、审计与商业字段不变
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewBulkController(db, userId).Delete(deletableId));

        await using (var db = _fixture.CreateDbContext())
        {
            var physical = await db.FinanceBulkSettlements.AsNoTracking()
                .SingleAsync(s => s.Id == deletableId);
            Assert.True(physical.IsDeleted);
            Assert.Equal(800m, physical.TotalAmount);
            Assert.Equal(customerId, physical.CustomerId);
        }

        await using (var db = _fixture.CreateDbContext())
            await AssertBusinessCodeAsync(ErrorCodes.NotFound,
                () => NewBulkController(db, userId).GetById(deletableId));

        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }


    // ==================== 4. 两个独立连接竞态（散货结算单行锁串行化） ====================

    [Fact]
    public async Task Race_Edit_Versus_Submit_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, settlementId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E387_RC1_{tag}", "并发客户1");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E387_RS1_{tag}", customer.Id, 1000m)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TryActionAsync(userId, ctl => ctl.Update(settlementId, new FinanceBulkSettlement
            {
                CustomerId = customerId,
                SettlementDate = DateTime.Today,
                TotalAmount = 300m
            })),
            () => TryActionAsync(userId, ctl => ctl.Submit(settlementId)));

        var edit = results[0];
        var submit = results[1];

        // 同一行竞争可能由 RowVersion / 行锁拒绝一个请求；至少一个成功，失败方不得覆盖赢家。
        Assert.True(edit.Success || submit.Success, $"edit={edit.Error}; submit={submit.Error}");
        var applied = await ReloadSettlementAsync(settlementId);
        Assert.Equal(submit.Success ? DocumentStatus.Submitted : DocumentStatus.Pending, applied.Status);
        Assert.Equal(edit.Success ? 300m : 1000m, applied.TotalAmount);
        Assert.Equal(customerId, applied.CustomerId);
        Assert.False(applied.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }

    [Fact]
    public async Task Race_Approve_Versus_Cancel_SerializedConsistentResult()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, settlementId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E387_RC2_{tag}", "并发客户2");
            customerId = customer.Id;
            userId = (await SeedOperatorAsync(seed, customer.Id)).UserId;
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E387_RS2_{tag}", customer.Id, 1000m,
                status: DocumentStatus.Submitted)).Id;
        }
        var postingsBefore = await PostingRowCountAsync();

        var results = await RaceAsync(
            () => TryActionAsync(userId, ctl => ctl.Approve(settlementId)),
            () => TryActionAsync(userId, ctl => ctl.Cancel(settlementId)));

        var approve = results[0];
        var cancel = results[1];

        // 串行化后绝无「已取消又被审核」或丢失更新：最终状态必须对应成功的操作
        Assert.True(approve.Success || cancel.Success, $"approve={approve.Error}; cancel={cancel.Error}");
        if (!approve.Success)
            Assert.True(approve.Error.Contains("当前状态不允许") || approve.Error.Contains("正在被并发修改"), approve.Error);
        if (!cancel.Success)
            Assert.Contains("正在被并发修改", cancel.Error);

        var applied = await ReloadSettlementAsync(settlementId);
        Assert.Equal(cancel.Success ? DocumentStatus.Cancelled : DocumentStatus.Approved, applied.Status);
        Assert.Equal(1000m, applied.TotalAmount);
        Assert.Equal(customerId, applied.CustomerId);
        Assert.False(applied.IsDeleted);
        Assert.Equal(postingsBefore, await PostingRowCountAsync());
    }
}

/// <summary>
/// ERP-387 专用 localdb 夹具：每次运行创建一个全新 GUID 库 + 完整 NEWERP 结构 + 种子数据；
/// 任何库访问之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> / 集成安全），
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。
/// </summary>
public sealed class FinanceBulkSettlementLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP387";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-387] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-387] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceBulkSettlementLifecycleTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => FinanceBulkSettlementLifecycleSqlServerFixture.AssertDedicatedTarget(connection));
}

