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
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-371 采购订单实时授权与数据范围护栏 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item>无身份 / 禁用 / 无既有「采购订单」菜单授权：经**真实控制器**在写入前 fail closed，单据 / 明细不变；</item>
/// <item>既有种子管理员（<c>SeedData</c> 已授予全部菜单）合法放行；受限操作员自有客户放行、他人客户与无归属备货拒绝；</item>
/// <item>撤销菜单立即收敛、导出按范围收敛、失败改单不改动总额 / 明细；</item>
/// <item><b>两条独立连接竞争</b>：并发「提交 vs 删除」，以及「来源销售订单失效（持锁取消）vs 采购审核」，
/// 经「归属销售订单 → 采购订单」行锁（<c>UPDLOCK, HOLDLOCK</c>）+ 可串行化事务串行化后只出现一种一致结果，
/// 来源已失效时审核被拒。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PurchaseOrderAuthorizationSqlServerTests
    : IClassFixture<PurchaseOrderAuthorizationSqlServerFixture>
{
    private readonly PurchaseOrderAuthorizationSqlServerFixture _fixture;

    private const long ProductA = 956201L;
    private const long CustomerA = 956301L;
    private const long CustomerB = 956302L;

    public PurchaseOrderAuthorizationSqlServerTests(PurchaseOrderAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseOrderAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("no-menu")]
    public async Task Live_identity_menu_and_status_denials_leave_order_unchanged(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        long? userId = null;
        if (scenario == "disabled")
            userId = await SeedUserAsync(db, $"po-denied-disabled-{Guid.NewGuid():N}", UserStatus.Disabled, withMenu: true);
        else if (scenario == "no-menu")
            userId = await SeedUserAsync(db, $"po-denied-no-menu-{Guid.NewGuid():N}", UserStatus.Enabled, withMenu: false);

        var order = await SeedOrderAsync(db, $"INT_TEST_POAUTH_DENY_{Guid.NewGuid():N}", CustomerA, DocumentStatus.Pending);
        var ctl = ControllerFor(db, userId);
        var expected = scenario == "missing" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden;

        await AssertDenied(expected, () => ctl.GetPaged(new PageQuery { PageSize = 10 }, null));
        await AssertDenied(expected, () => ctl.GetById(order.Id));
        await AssertDenied(expected, () => ctl.GetPrint(order.Id));
        await AssertDenied(expected, () => ctl.Export(null, null));
        await AssertDenied(expected, () => ctl.ExportExcel(null, null, null, null));
        await AssertDenied(expected, () => ctl.Create(NewOrder(CustomerA)));
        await AssertDenied(expected, () => ctl.Update(order.Id, NewOrder(CustomerA)));
        await AssertDenied(expected, () => ctl.Submit(order.Id));
        await AssertDenied(expected, () => ctl.Approve(order.Id));
        await AssertDenied(expected, () => ctl.Cancel(order.Id));
        await AssertDenied(expected, () => ctl.Delete(order.Id));

        var stored = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 既有种子管理员合法放行 ====================

    [Fact]
    public async Task Seeded_admin_with_all_menus_can_read_export_and_create_stock_procurement()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var order = await SeedOrderAsync(db, $"INT_TEST_POAUTH_ADMIN_{Guid.NewGuid():N}", CustomerA, DocumentStatus.Pending);
        var ctl = ControllerFor(db, adminId);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 10 }, null));
        Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Export(null, null));
        Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));

        // 既有特权账号的无归属备货采购保持可用（procurement-for-stock 未被收紧）。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(null)));
    }

    // ==================== 3. 受限操作员：自有 / 他人 / 无归属 ====================

    [Fact]
    public async Task Restricted_operator_scopes_own_foreign_and_legacy_unowned_access()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedRestrictedOperatorAsync(db, CustomerA);
        var own = await SeedOrderAsync(db, $"INT_TEST_POAUTH_OWN_{Guid.NewGuid():N}", CustomerA, DocumentStatus.Pending);
        var foreign = await SeedOrderAsync(db, $"INT_TEST_POAUTH_FOREIGN_{Guid.NewGuid():N}", CustomerB, DocumentStatus.Pending);
        var unowned = await SeedOrderAsync(db, $"INT_TEST_POAUTH_UNOWNED_{Guid.NewGuid():N}", null, DocumentStatus.Pending);
        var ctl = ControllerFor(db, operatorId);

        // 列表：只出现自有客户单据（无归属备货与范围外单据绝不出现）。
        var page = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 200 }, null));
        var items = Assert.IsType<ApiResponse<PagedResult<PurchaseOrder>>>(page.Value).Data!.Items;
        Assert.Contains(items, o => o.Id == own.Id);
        Assert.DoesNotContain(items, o => o.Id == foreign.Id || o.Id == unowned.Id);
        Assert.All(items, o => Assert.Equal(CustomerA, o.OwningCustomerId));

        // 详情 / 打印：自有放行，范围外与无归属 fail closed。
        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));
        Assert.IsType<OkObjectResult>(await ctl.GetPrint(own.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(unowned.Id));

        // 导出：范围收敛（导出结果不含范围外 / 无归属单据）。
        var export = Assert.IsType<OkObjectResult>(await ctl.Export(null, null));
        var exported = Assert.IsType<ApiResponse<List<PurchaseOrder>>>(export.Value).Data!;
        Assert.Contains(exported, o => o.Id == own.Id);
        Assert.DoesNotContain(exported, o => o.Id == foreign.Id || o.Id == unowned.Id);

        // 创建 / 修改：无归属备货与越界客户拒绝；自有客户放行。
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Create(NewOrder(null)));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Create(NewOrder(CustomerB)));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Update(foreign.Id, NewOrder(CustomerB)));
        await AssertDenied(ErrorCodes.Forbidden, () => ctl.Update(own.Id, NewOrder(CustomerB)));
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(CustomerA)));

        Assert.Equal(DocumentStatus.Pending,
            (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == foreign.Id)).Status);
    }

    [Fact]
    public async Task Revoked_menu_authorization_converges_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedRestrictedOperatorAsync(db, CustomerA);
        var order = await SeedOrderAsync(db, $"INT_TEST_POAUTH_REVOKE_{Guid.NewGuid():N}", CustomerA, DocumentStatus.Pending);
        var ctl = ControllerFor(db, operatorId);

        Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id));

        // 撤销既有菜单授权：下一次请求立即收敛（绝不缓存）。
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == operatorId && !ur.IsDeleted).Select(ur => ur.RoleId).ToListAsync();
        foreach (var grant in await db.SysRoleMenus
                     .Where(g => roleIds.Contains(g.RoleId) && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertDenied(ErrorCodes.Forbidden, () => ctl.GetById(order.Id));
        Assert.Equal(DocumentStatus.Pending,
            (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    // ==================== 4. 失败改单的原子性 ====================

    [Fact]
    public async Task Failed_linked_edit_preserves_totals_details_and_status()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"INT_TEST_POAUTH_CUST_{Guid.NewGuid():N}");
        var order = await SeedOrderAsync(db, $"INT_TEST_POAUTH_EDIT_{Guid.NewGuid():N}", customerId, DocumentStatus.Pending);

        // 已存单据链接一个不存在的来源销售订单：更新在锁内校验失败（fail closed），原单据保持不变。
        var update = NewOrder(customerId);
        update.OwningSalesOrderId = 987654321L;
        var ctl = ControllerFor(db, adminId);
        await AssertDenied(ErrorCodes.RuleConflict, () => ctl.Update(order.Id, update));

        var stored = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Single(stored.Details);
        Assert.Null(stored.OwningSalesOrderId);
    }

    // ==================== 5. 两条独立连接竞争 ====================

    [Fact]
    public async Task Two_connection_race_submit_vs_delete_yields_one_consistent_result()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var order = await SeedOrderAsync(db, $"INT_TEST_POAUTH_RACE1_{Guid.NewGuid():N}", null, DocumentStatus.Pending);

        var race = await RaceAsync(
            () => TrySubmitAsync(order.Id, adminId),
            () => TryDeleteAsync(order.Id, adminId));

        // 同一把采购订单行锁 + 可串行化事务：只能成功其一，且最终状态自相一致。
        Assert.Equal(1, race.Count(r => r.Ok));
        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        if (race[0].Ok)
        {
            Assert.Equal(DocumentStatus.Submitted, stored.Status);
            Assert.False(stored.IsDeleted);
            Assert.IsType<BusinessException>(race[1].Error);
        }
        else
        {
            Assert.True(stored.IsDeleted);
            Assert.Equal(DocumentStatus.Pending, stored.Status);
            Assert.IsType<BusinessException>(race[0].Error);
        }
    }

    [Fact]
    public async Task Two_connection_race_invalidated_source_vs_approve_refuses_approval()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"INT_TEST_POAUTH_RACE2C_{Guid.NewGuid():N}");
        var salesOrderId = await SeedApprovedSalesOrderAsync(db, $"INT_TEST_POAUTH_RACE2S_{Guid.NewGuid():N}", customerId);
        var order = await SeedLinkedOrderAsync(db, $"INT_TEST_POAUTH_RACE2P_{Guid.NewGuid():N}",
            customerId, salesOrderId, DocumentStatus.Submitted);

        // 连接 1：持来源销售订单行锁并取消来源；连接 2：同单采购审核（先锁来源、再锁采购）。
        var lockAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidate = Task.Run(() => TryInvalidateSalesOrderAsync(salesOrderId, lockAcquired));
        await lockAcquired.Task;

        var approve = await TryApproveAsync(order.Id, adminId);
        var invalidateResult = await invalidate;

        Assert.True(invalidateResult.Ok, invalidateResult.Error?.ToString());
        Assert.False(approve.Ok);
        var error = Assert.IsType<BusinessException>(approve.Error);
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("已取消", error.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(DocumentStatus.Cancelled,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == salesOrderId)).Status);
    }

    // ==================== 脚手架：真实控制器 / 真实身份 / 两条独立连接 ====================

    private readonly record struct RaceResult(bool Ok, IActionResult? Result, Exception? Error);

    /// <summary>让两个动作尽量同时起跑，并在<b>两条独立连接</b>上并行执行。</summary>
    private static async Task<RaceResult[]> RaceAsync(params Func<Task<RaceResult>>[] actions)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions
            .Select(action => Task.Run(async () =>
            {
                await gate.Task;
                return await action();
            }))
            .ToArray();
        gate.SetResult(true);
        return await Task.WhenAll(tasks);
    }

    private async Task<RaceResult> TrySubmitAsync(long orderId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Submit(orderId));

    private async Task<RaceResult> TryDeleteAsync(long orderId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Delete(orderId));

    private async Task<RaceResult> TryApproveAsync(long orderId, long userId)
        => await RunAsync(db => ControllerFor(db, userId).Approve(orderId));

    private async Task<RaceResult> RunAsync(Func<ErpDbContext, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            return new RaceResult(true, await action(db), null);
        }
        catch (Exception ex)
        {
            return new RaceResult(false, null, ex);
        }
    }

    /// <summary>在独立连接 + 可串行化事务内持来源销售订单行锁并取消来源（模拟来源失效竞争者）。</summary>
    private async Task<RaceResult> TryInvalidateSalesOrderAsync(long salesOrderId, TaskCompletionSource<bool> lockAcquired)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", salesOrderId)
                .ToListAsync();
            var order = await db.SalesOrders.SingleAsync(o => o.Id == salesOrderId);
            order.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();

            lockAcquired.TrySetResult(true);
            // 持锁等待，确保审核侧在同一把来源行锁上竞争后再提交。
            await Task.Delay(200);

            await transaction.CommitAsync();
            return new RaceResult(true, null, null);
        }
        catch (Exception ex)
        {
            lockAcquired.TrySetResult(false);
            try { await transaction.RollbackAsync(); } catch { /* 事务已结束 */ }
            return new RaceResult(false, null, ex);
        }
    }

    private static PurchaseOrderController ControllerFor(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "IntegrationTest")) };
        http.Request.Path = "/api/purchase-orders";
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task AssertDenied(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
    }

    // ==================== 种子助手（EF 生成身份键；仅既有授权口径，不新增用户授权） ====================

    /// <summary>既有种子数据里的超级管理员账号（已被授予全部菜单，不新增任何用户授权）。</summary>
    private static async Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking().Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

    /// <summary>按既有菜单授权创建账号：非系统角色 + 可选既有 purchase-order 菜单。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string name, UserStatus status, bool withMenu)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var user = new SysUser
        {
            UserName = $"{name}-{suffix}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = name,
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleCode = $"PoAuth-{suffix}", RoleName = "采购授权受限角色" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
            await GrantPurchaseOrderMenuAsync(db, role.Id);

        return user.Id;
    }

    private static async Task GrantPurchaseOrderMenuAsync(ErpDbContext db, long roleId)
    {
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode)
            .Select(m => m.Id).FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    /// <summary>播种受限采购操作员（员工编码映射 + 既有菜单 + 本人客户）。</summary>
    private static async Task<long> SeedRestrictedOperatorAsync(ErpDbContext db, long ownedCustomerId)
    {
        var code = $"po-sql-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var userId = await SeedUserAsync(db, code, UserStatus.Enabled, withMenu: true);

        var customer = await db.BaseCustomers.SingleOrDefaultAsync(c => c.Id == ownedCustomerId);
        if (customer is null)
            db.BaseCustomers.Add(new BaseCustomer
            {
                Id = ownedCustomerId, CustomerCode = $"SQLC-{ownedCustomerId}",
                CustomerName = $"SQL 客户{ownedCustomerId}", EmpId = employee.Id, Status = 1
            });
        else
            customer.EmpId = employee.Id;
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string code)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = code, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedApprovedSalesOrderAsync(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task<PurchaseOrder> SeedOrderAsync(ErpDbContext db, string no,
        long? owningCustomerId, DocumentStatus status)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            Status = status,
            TotalAmount = 1000m
        };
        order.Details.Add(new PurchaseOrderDetail
        {
            ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
            Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<PurchaseOrder> SeedLinkedOrderAsync(ErpDbContext db, string no,
        long customerId, long salesOrderId, DocumentStatus status)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = customerId,
            OwningSalesOrderId = salesOrderId,
            Status = status,
            TotalAmount = 200m
        };
        order.Details.Add(new PurchaseOrderDetail
        {
            ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
            Quantity = 2m, UnitPrice = 100m, Amount = 200m
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static PurchaseOrder NewOrder(long? owningCustomerId)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            Details = new List<PurchaseOrderDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS", Quantity = 1m, UnitPrice = 100m }
            }
        };

}

/// <summary>
/// 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 供 ERP-371 采购订单授权 / 两条独立连接竞争集成测试复用；发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取生产设置。
/// </summary>
public sealed class PurchaseOrderAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_POAUTH";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-371] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
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

        Console.WriteLine("[ERP-371] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

public sealed class PurchaseOrderAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PurchaseOrderAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

