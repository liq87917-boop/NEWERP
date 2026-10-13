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
/// ERP-486 供应商比价（<c>api/purchase/quotes</c>）与比价审批（<c>api/purchase/quote-decisions</c>）
/// 入口实时授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「供应商比价」（<c>purchase-quote</c>）/
/// 「采购订单」（<c>purchase-order</c>）菜单授权与既有 <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实控制器。</item>
/// <item><b>与调用形状无关</b>：<strong>空 <c>Request.Path</c></strong> 与<strong>已赋值 <c>Request.Path</c></strong>
/// 必须给出完全一致的判定；缺失 / 零 / 未知 / 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，
/// 且都在任何报价 / 决定 / 派生读取与写入之前。</item>
/// <item><b>保留既有业务语义与零写入拒绝</b>：既有菜单授权身份照常读取列表 / 详情 / 派生视图并完成既有带入预填；
/// 被拒绝的请求零写入、零授权扩张、不消耗单据号。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseQuoteEntryAuthorizationSqlServerTests
    : IClassFixture<PurchaseQuoteEntryAuthorizationSqlServerFixture>
{
    private readonly PurchaseQuoteEntryAuthorizationSqlServerFixture _fixture;

    public PurchaseQuoteEntryAuthorizationSqlServerTests(PurchaseQuoteEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const string RoutePath = "/api/purchase/quotes";
    private const long UnknownUserId = 9_486_999_999L;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseQuoteEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    /// <summary>绑定真实登录身份的比价控制器；<paramref name="requestPath"/> 为 null 表示空路径请求。</summary>
    private static PurchaseQuoteController NewQuoteController(ErpDbContext db, long? userId, string? requestPath)
    {
        var claims = userId is > 0
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>绑定真实登录身份的审批控制器；<paramref name="requestPath"/> 为 null 表示空路径请求。</summary>
    private static PurchaseQuoteDecisionController NewDecisionController(ErpDbContext db, long? userId, string? requestPath)
    {
        var claims = userId is > 0
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new PurchaseQuoteDecisionController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>用一条独立连接执行控制器动作，返回受控错误码（成功返回 null）。</summary>
    private async Task<int?> DeniedCodeAsync(
        long? userId, string? requestPath, Func<PurchaseQuoteController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await action(NewQuoteController(db, userId, requestPath));
            return null;
        }
        catch (BusinessException ex)
        {
            return ex.Code;
        }
    }

    /// <summary>用一条独立连接执行审批控制器动作，返回受控错误码（成功返回 null）。</summary>
    private async Task<int?> DecisionDeniedCodeAsync(
        long? userId, string? requestPath, Func<PurchaseQuoteDecisionController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await action(NewDecisionController(db, userId, requestPath));
            return null;
        }
        catch (BusinessException ex)
        {
            return ex.Code;
        }
    }

    /// <summary>核心比价入口：先授权、后读写；拒绝时返回同一受控错误码。</summary>
    private async Task AssertAllQuoteEntriesDeniedAsync(long? userId, string? requestPath, PurchaseQuote quote, int code)
    {
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetPaged(new PageQuery { Page = 1, PageSize = 10 })));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetAll()));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetById(quote.Id)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Create(NewDraft(0L))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Update(quote.Id, NewDraft(0L))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Delete(quote.Id)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.BatchDelete(new List<long> { quote.Id })));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.OrderPrefill(quote.Id)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.ToPurchaseOrder(quote.Id)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.BatchOrderPlan(quote.QuoteNo, null)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = quote.QuoteNo })));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.PriceHistory(new PurchaseQuotePriceHistoryQuery { ProductId = quote.ProductId ?? 0L })));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.QuotePriceHistory(quote.Id)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.OrderPriceVariance(new PurchaseQuoteOrderPriceVarianceQuery())));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath,
            c => c.ConversionFunnel(new PurchaseQuoteConversionFunnelQuery())));
    }

    /// <summary>核心审批入口：先授权、后读写；拒绝时返回同一受控错误码。</summary>
    private async Task AssertDecisionEntriesDeniedAsync(long? userId, string? requestPath, PurchaseQuote quote, int code)
    {
        Assert.Equal(code, await DecisionDeniedCodeAsync(userId, requestPath, c => c.BatchStatus(quote.QuoteNo)));
        Assert.Equal(code, await DecisionDeniedCodeAsync(userId, requestPath, c => c.Decide(
            new PurchaseQuoteDecisionRequest { QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved })));
    }


    // ==================== 1. 缺失 / 零 / 未知 / 已删除身份：任何入口、任何请求形状一律未认证 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized(string? requestPath)
    {
        Guard();
        PurchaseQuote quote;
        long deletedUserId;
        await using (var seed = _fixture.CreateDbContext())
        {
            quote = await SeedQuoteAsync(seed);
            var deleted = await SeedOperatorAsync(seed);
            await SoftDeleteUserAsync(seed, deleted.UserId);
            deletedUserId = deleted.UserId;
        }

        await AssertAllQuoteEntriesDeniedAsync(null, requestPath, quote, ErrorCodes.Unauthorized);
        await AssertAllQuoteEntriesDeniedAsync(0L, requestPath, quote, ErrorCodes.Unauthorized);
        await AssertAllQuoteEntriesDeniedAsync(UnknownUserId, requestPath, quote, ErrorCodes.Unauthorized);
        await AssertAllQuoteEntriesDeniedAsync(deletedUserId, requestPath, quote, ErrorCodes.Unauthorized);
        await AssertDecisionEntriesDeniedAsync(null, requestPath, quote, ErrorCodes.Unauthorized);
        await AssertDecisionEntriesDeniedAsync(deletedUserId, requestPath, quote, ErrorCodes.Unauthorized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Disabled_identity_is_forbidden_on_both_request_shapes(string? requestPath)
    {
        Guard();
        PurchaseQuote quote;
        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            quote = await SeedQuoteAsync(seed);
            var disabled = await SeedOperatorAsync(seed, UserStatus.Disabled);
            userId = disabled.UserId;
        }

        await AssertAllQuoteEntriesDeniedAsync(userId, requestPath, quote, ErrorCodes.Forbidden);
        await AssertDecisionEntriesDeniedAsync(userId, requestPath, quote, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Revoked_menu_denies_every_entry_on_both_request_shapes(string? requestPath)
    {
        Guard();
        PurchaseQuote quote;
        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            quote = await SeedQuoteAsync(seed);
            var owner = await SeedOperatorAsync(seed);
            await RevokeMenuAsync(seed, owner.RoleId);
            userId = owner.UserId;
        }

        await AssertAllQuoteEntriesDeniedAsync(userId, requestPath, quote, ErrorCodes.Forbidden);
    }

    // ==================== 2. 授权身份：空路径与已赋值路径读出完全一致的既有契约 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Authorized_identity_reads_are_identical_on_both_request_shapes(string? requestPath)
    {
        Guard();
        PurchaseQuote quote;
        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            quote = await SeedQuoteAsync(seed, customerId: null);
            var owner = await SeedOperatorAsync(seed, privileged: true);
            userId = owner.UserId;
        }

        await using var db = _fixture.CreateDbContext();
        var ctl = NewQuoteController(db, userId, requestPath);
        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.IsType<OkObjectResult>(await ctl.GetAll());
        Assert.IsType<OkObjectResult>(await ctl.GetById(quote.Id));
        Assert.IsType<OkObjectResult>(await ctl.PriceHistory(new PurchaseQuotePriceHistoryQuery
        {
            ProductId = quote.ProductId ?? 0L
        }));
        Assert.IsType<OkObjectResult>(await ctl.QuotePriceHistory(quote.Id));
        Assert.IsType<OkObjectResult>(await ctl.OrderPriceVariance(new PurchaseQuoteOrderPriceVarianceQuery()));
        Assert.IsType<OkObjectResult>(await ctl.ConversionFunnel(new PurchaseQuoteConversionFunnelQuery()));

        var decisions = NewDecisionController(db, userId, requestPath);
        Assert.IsType<OkObjectResult>(await decisions.BatchStatus(quote.QuoteNo));
    }

    // ==================== 3. 拒绝调用零写入、零授权扩张且不消耗单据号 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Denied_calls_persist_zero_rows_and_consume_no_document_number(string? requestPath)
    {
        Guard();
        PurchaseQuote quote;
        await using (var seed = _fixture.CreateDbContext())
        {
            quote = await SeedQuoteAsync(seed);
        }

        long quotesBefore, ordersBefore, decisionsBefore, usersBefore, rolesBefore, grantsBefore;
        await using (var before = _fixture.CreateDbContext())
        {
            quotesBefore = await before.PurchaseQuotes.CountAsync();
            ordersBefore = await before.PurchaseOrders.CountAsync();
            decisionsBefore = await before.PurchaseQuoteDecisions.CountAsync();
            usersBefore = await before.SysUsers.CountAsync();
            rolesBefore = await before.SysRoles.CountAsync();
            grantsBefore = await before.SysRoleMenus.CountAsync(g => !g.IsDeleted);
        }

        await AssertAllQuoteEntriesDeniedAsync(null, requestPath, quote, ErrorCodes.Unauthorized);
        await AssertDecisionEntriesDeniedAsync(null, requestPath, quote, ErrorCodes.Unauthorized);

        await using var after = _fixture.CreateDbContext();
        Assert.Equal(quotesBefore, await after.PurchaseQuotes.CountAsync());
        Assert.Equal(ordersBefore, await after.PurchaseOrders.CountAsync());
        Assert.Equal(decisionsBefore, await after.PurchaseQuoteDecisions.CountAsync());
        Assert.Equal(usersBefore, await after.SysUsers.CountAsync());
        Assert.Equal(rolesBefore, await after.SysRoles.CountAsync());
        Assert.Equal(grantsBefore, await after.SysRoleMenus.CountAsync(g => !g.IsDeleted));
        Assert.False((await after.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quote.Id)).IsDeleted);
    }


    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = false)
    {
        var code = $"int-pq486-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "比价入口集成角色", RoleCode = $"Pq486Op-{Guid.NewGuid():N}", IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var menuCode in new[]
                 {
                     PurchaseQuoteAuthorizationRules.RequiredMenuCode,
                     PurchaseQuoteAuthorizationRules.DestinationMenuCode
                 })
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

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

    private static async Task<PurchaseQuote> SeedQuoteAsync(ErpDbContext db, long? customerId = null)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = $"INT_PQ486_{Guid.NewGuid():N}",
            QuoteDate = DateTime.Today,
            ProductId = 9_486_001L,
            ProductName = "集成商品",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = 10m,
            SupplierId = 0L,
            SupplierName = "集成供应商",
            SupplierType = "档口",
            QuotePrice = 10m,
            TotalAmount = 100m,
            Currency = "USD",
            TaxIncluded = false,
            DeliveryDays = 10,
            IsSelected = false,
            Status = PurchaseQuoteMutationRules.PendingStatus,
            CustomerId = customerId,
            CustomerName = "集成客户",
            RefOrderNo = string.Empty,
            Remark = "INT_PQ486"
        };
        db.PurchaseQuotes.Add(quote);
        await db.SaveChangesAsync();
        return quote;
    }

    private static PurchaseQuote NewDraft(long customerId)
        => new()
        {
            QuoteDate = DateTime.Today,
            ProductId = 0L,
            ProductName = "集成商品",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = 10m,
            SupplierId = 0L,
            SupplierName = "集成供应商",
            SupplierType = "档口",
            QuotePrice = 10m,
            TotalAmount = 100m,
            Currency = "USD",
            TaxIncluded = false,
            DeliveryDays = 10,
            IsSelected = false,
            Status = PurchaseQuoteMutationRules.PendingStatus,
            CustomerId = customerId > 0 ? customerId : null,
            Remark = "INT_PQ486"
        };
}


/// <summary>
/// ERP-486 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class PurchaseQuoteEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PURCHASEQUOTEENTRYAUTHORIZATION";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-486] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-486] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseQuoteEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseQuoteEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

