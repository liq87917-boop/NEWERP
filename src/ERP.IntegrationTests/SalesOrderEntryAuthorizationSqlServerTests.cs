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
/// ERP-465 规范销售订单（<c>api/sales-orders</c>）读侧入口实时授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「销售订单」（<c>sales-order</c>）菜单授权与既有
/// <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实 <see cref="SalesOrderController"/>。</item>
/// <item><b>与请求形状无关</b>：<strong>空 <c>Request.Path</c></strong> 与<strong>已赋值 <c>Request.Path</c></strong>
/// 必须给出完全一致的判定；缺失 / 零 / 未知 / 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，且都在任何订单 /
/// 明细 / 执行证据 / 时间线字节读取之前。</item>
/// <item><b>保留既有业务语义与零写入拒绝</b>：既有菜单授权身份照常读取列表 / 详情 / 时间线 / 财务核对 / 进度 / 退货影响 /
/// 收款与发票证据；范围外订单返回同一非披露错误；被拒绝的请求零写入、零授权扩张。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderEntryAuthorizationSqlServerTests
    : IClassFixture<SalesOrderEntryAuthorizationSqlServerFixture>
{
    private readonly SalesOrderEntryAuthorizationSqlServerFixture _fixture;

    public SalesOrderEntryAuthorizationSqlServerTests(SalesOrderEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const string RoutePath = "/api/sales-orders";
    private const long UnknownUserId = 9_465_999_999L;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesOrderEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    /// <summary>绑定真实登录身份的真实控制器；<paramref name="requestPath"/> 为 null 表示空路径请求。</summary>
    private static SalesOrderController NewController(ErpDbContext db, long? userId, string? requestPath)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new SalesOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>用一条独立连接执行控制器动作，返回受控错误码（成功返回 null）。</summary>
    private async Task<int?> DeniedCodeAsync(
        long? userId, string? requestPath, Func<SalesOrderController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await action(NewController(db, userId, requestPath));
            return null;
        }
        catch (BusinessException ex)
        {
            return ex.Code;
        }
    }

    /// <summary>用一条独立连接执行控制器动作，断言返回 Ok。</summary>
    private async Task AssertOkAsync(
        long? userId, string? requestPath, Func<SalesOrderController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        Assert.IsType<OkObjectResult>(await action(NewController(db, userId, requestPath)));
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db)
    {
        var code = $"INT_SO465_CUS_{Guid.NewGuid():N}";
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<SalesOrder> SeedOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = $"INT_SO465_{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.1m,
            TotalAmount = 1000m,
            Status = DocumentStatus.Pending
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, long inScopeCustomerId, bool menu = true,
        UserStatus status = UserStatus.Enabled, bool privileged = false)
    {
        var code = $"int-so465-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = code,
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "销售订单入口集成角色",
            RoleCode = $"So465Op-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (menu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == SalesOrderExecutionAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();
        return (user.Id, role.Id);
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task SoftDeleteUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>读侧每一条公开入口：先授权、后读取；拒绝时返回同一受控错误码。</summary>
    private async Task AssertAllReadEntriesDeniedAsync(
        long? userId, string? requestPath, long orderId, int code)
    {
        var ids = orderId.ToString();
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetById(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Timeline(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.FinanceReconciliation(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Progress(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.ReturnImpact(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.ReceiptEvidence(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.ReceiptEvidenceSummaries(new SalesOrderReceiptEvidenceQuery { Ids = ids })));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.InvoiceEvidence(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.InvoiceEvidenceSummaries(new SalesOrderInvoiceEvidenceQuery { Ids = ids })));
    }

    // ==================== 1. 缺失 / 零 / 未知 / 已删除身份：任何入口、任何请求形状一律未认证 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized(string? requestPath)
    {
        Guard();
        long orderId, deletedUserId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed);
            var order = await SeedOrderAsync(seed, customer.Id);
            var deleted = await SeedOperatorAsync(seed, customer.Id);
            await SoftDeleteUserAsync(seed, deleted.UserId);
            orderId = order.Id;
            deletedUserId = deleted.UserId;
        }

        await AssertAllReadEntriesDeniedAsync(null, requestPath, orderId, ErrorCodes.Unauthorized);
        await AssertAllReadEntriesDeniedAsync(0L, requestPath, orderId, ErrorCodes.Unauthorized);
        await AssertAllReadEntriesDeniedAsync(UnknownUserId, requestPath, orderId, ErrorCodes.Unauthorized);
        await AssertAllReadEntriesDeniedAsync(deletedUserId, requestPath, orderId, ErrorCodes.Unauthorized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Disabled_identity_is_forbidden_on_both_request_shapes(string? requestPath)
    {
        Guard();
        long orderId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed);
            var order = await SeedOrderAsync(seed, customer.Id);
            var disabled = await SeedOperatorAsync(seed, customer.Id, status: UserStatus.Disabled);
            orderId = order.Id;
            userId = disabled.UserId;
        }

        await AssertAllReadEntriesDeniedAsync(userId, requestPath, orderId, ErrorCodes.Forbidden);
    }

    // ==================== 2. 缺少 / 撤销既有菜单：仅证据入口权限不足 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_menu_denies_evidence_but_not_list_or_detail(string? requestPath)
    {
        Guard();
        long orderId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed);
            var order = await SeedOrderAsync(seed, customer.Id);
            var owner = await SeedOperatorAsync(seed, customer.Id, menu: false);
            orderId = order.Id;
            userId = owner.UserId;
        }

        await AssertOkAsync(userId, requestPath, c => c.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertOkAsync(userId, requestPath, c => c.GetById(orderId));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, requestPath, c => c.Timeline(orderId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, requestPath, c => c.Progress(orderId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, requestPath, c => c.ReceiptEvidence(orderId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, requestPath, c => c.InvoiceEvidence(orderId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Revoked_menu_denies_evidence_but_not_list_or_detail(string? requestPath)
    {
        Guard();
        long orderId, userId, roleId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed);
            var order = await SeedOrderAsync(seed, customer.Id);
            var owner = await SeedOperatorAsync(seed, customer.Id);
            await RevokeMenuAsync(seed, owner.RoleId);
            orderId = order.Id;
            userId = owner.UserId;
            roleId = owner.RoleId;
        }
        Assert.True(roleId > 0);

        await AssertOkAsync(userId, requestPath, c => c.GetById(orderId));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, requestPath, c => c.Progress(orderId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, requestPath, c => c.InvoiceEvidence(orderId)));
    }

    // ==================== 3. 允许身份：既有读侧生命周期与非披露 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Permitted_identity_reads_own_order_evidence_on_both_request_shapes(string? requestPath)
    {
        Guard();
        long ownOrderId, foreignOrderId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var ownCustomer = await SeedCustomerAsync(seed);
            var foreignCustomer = await SeedCustomerAsync(seed);
            var ownOrder = await SeedOrderAsync(seed, ownCustomer.Id);
            var foreignOrder = await SeedOrderAsync(seed, foreignCustomer.Id);
            var owner = await SeedOperatorAsync(seed, ownCustomer.Id);
            ownOrderId = ownOrder.Id;
            foreignOrderId = foreignOrder.Id;
            userId = owner.UserId;
        }

        await AssertOkAsync(userId, requestPath, c => c.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertOkAsync(userId, requestPath, c => c.GetById(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.Timeline(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.FinanceReconciliation(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.Progress(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.ReturnImpact(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.ReceiptEvidence(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.InvoiceEvidence(ownOrderId));
        await AssertOkAsync(userId, requestPath, c => c.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = ownOrderId.ToString() }));

        // 非披露：范围外订单与批量混入不可访问订单返回同一受控错误。
        Assert.Equal(ErrorCodes.NotFound, await DeniedCodeAsync(userId, requestPath, c => c.Progress(foreignOrderId)));
        Assert.Equal(ErrorCodes.NotFound, await DeniedCodeAsync(userId, requestPath, c => c.GetById(foreignOrderId)));
        Assert.Equal(ErrorCodes.NotFound, await DeniedCodeAsync(userId, requestPath, c => c.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{ownOrderId},{foreignOrderId}" })));
    }

    // ==================== 4. 被拒绝的调用零写入、零授权扩张 ====================

    [Fact]
    public async Task Denied_calls_persist_zero_rows_and_grant_nothing()
    {
        Guard();
        long foreignOrderId, ownOrderId, userId, roleId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var ownCustomer = await SeedCustomerAsync(seed);
            var foreignCustomer = await SeedCustomerAsync(seed);
            var ownOrder = await SeedOrderAsync(seed, ownCustomer.Id);
            var foreignOrder = await SeedOrderAsync(seed, foreignCustomer.Id);
            var owner = await SeedOperatorAsync(seed, ownCustomer.Id);
            foreignOrderId = foreignOrder.Id;
            ownOrderId = ownOrder.Id;
            userId = owner.UserId;
            roleId = owner.RoleId;
        }

        long grantsBefore, usersBefore, rolesBefore, ordersBefore;
        await using (var before = _fixture.CreateDbContext())
        {
            grantsBefore = await before.SysRoleMenus.CountAsync(g => !g.IsDeleted);
            usersBefore = await before.SysUsers.CountAsync();
            rolesBefore = await before.SysRoles.CountAsync();
            ordersBefore = await before.SalesOrders.CountAsync();
        }

        // 范围外订单：非披露拒绝。
        Assert.Equal(ErrorCodes.NotFound, await DeniedCodeAsync(userId, null, c => c.Progress(foreignOrderId)));
        Assert.Equal(ErrorCodes.NotFound, await DeniedCodeAsync(userId, RoutePath, c => c.GetById(foreignOrderId)));

        // 撤销既有菜单：证据入口权限不足，且不扩张任何用户授权。
        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, roleId);
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, null, c => c.Progress(ownOrderId)));
        Assert.Equal(ErrorCodes.Forbidden, await DeniedCodeAsync(userId, RoutePath, c => c.InvoiceEvidence(ownOrderId)));

        await using (var after = _fixture.CreateDbContext())
        {
            Assert.Equal(grantsBefore, await after.SysRoleMenus.CountAsync(g => !g.IsDeleted));
            Assert.Equal(usersBefore, await after.SysUsers.CountAsync());
            Assert.Equal(rolesBefore, await after.SysRoles.CountAsync());
            Assert.Equal(ordersBefore, await after.SalesOrders.CountAsync());
            // 列表 / 详情仍可用（未新增菜单要求），证明拒绝路径未改写任何业务 / 授权记录。
            Assert.IsType<OkObjectResult>(await NewController(after, userId, null).GetById(ownOrderId));
        }
    }
}

/// <summary>
/// ERP-465 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesOrderEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SALESORDERENTRYAUTHORIZATION";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-465] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-465] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
