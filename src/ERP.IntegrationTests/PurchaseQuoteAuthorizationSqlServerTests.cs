using System.Text.Json;
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
/// ERP-416 供应商比价（<c>api/purchase/quotes</c>）与比价审批（<c>api/purchase/quote-decisions</c>）
/// 实时授权、持久化归属与转单护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「供应商比价」<c>purchase-quote</c> /
/// 「采购订单」<c>purchase-order</c> 功能菜单授权与业务员客户数据范围驱动真实控制器；
/// 不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>逐入口证明</b>：受限业务员下本人比价行可读 / 可改 / 可转单；范围外 / 空归属 / 已删除 / 不存在返回
/// **同一**非披露错误；台账在计数 / 分页之前按持久化 <c>CustomerId</c> 下推；派生只读按范围收敛。</item>
/// <item><b>批次整批</b>：混合批次（本人行 + 他人行）的计划 / 转单 / 审批状态 / 显式行清单都整批非披露拒绝且零写入。</item>
/// <item><b>身份 / 菜单拒绝矩阵</b>：无身份 / 已删除账号 → 未认证；已禁用 / 无菜单 → 权限不足（逐入口断言）。</item>
/// <item><b>零写入证据</b>：每次拒绝 / 读取前后对规范业务表（含 <c>PurchaseQuotes</c> / <c>PurchaseQuoteDecisions</c> /
/// <c>PurchaseOrders</c> / <c>PurchaseOrderDetails</c> / <c>StockMovements</c>（保留库存来源单据审计） /
/// <c>SysOperationLogs</c>）做只读快照，全部不变。</item>
/// <item><b>两个独立连接竞态</b>：① 两条连接并发读取同一本人比价行 → 结果一致；② 一条合法本人读取与一条他人越权
/// 并发 → 合法读取成功、越权 fail closed，收尾快照证明零写入。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseQuoteAuthorizationSqlServerTests
    : IClassFixture<PurchaseQuoteAuthorizationSqlServerFixture>
{
    private readonly PurchaseQuoteAuthorizationSqlServerFixture _fixture;

    public PurchaseQuoteAuthorizationSqlServerTests(PurchaseQuoteAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const long MissingQuoteId = 9_416_999L;

    private static readonly string[] SnapshotTables =
    {
        "PurchaseQuotes", "PurchaseQuoteDecisions", "PurchaseOrders", "PurchaseOrderDetails",
        "StockMovements", "SysOperationLogs",
    };

    private void Guard() => PurchaseQuoteAuthorizationSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    private static PurchaseQuoteController QuoteController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase/quotes";
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static PurchaseQuoteDecisionController DecisionController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase/quote-decisions";
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return new PurchaseQuoteDecisionController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static async Task<T> OkDataAsync<T>(Task<IActionResult> action)
    {
        var ok = Assert.IsType<OkObjectResult>(await action);
        return Assert.IsType<ApiResponse<T>>(ok.Value).Data!;
    }

    private static async Task AssertNonDisclosingNotFoundAsync(Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(PurchaseQuoteAuthorizationRules.NotFoundText, ex.Message);
    }

    private static async Task AssertDeniedAsync(int code, string text, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(text, ex.Message);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static PurchaseQuote NewQuote(string quoteNo, long? customerId) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 9, 1),
        ProductId = 310L,
        ProductName = "ERP416 SQL 新商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 10m,
        SupplierId = 88L,
        SupplierName = "ERP416 SQL 档口",
        SupplierType = "档口",
        QuotePrice = 3m,
        TotalAmount = 30m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 5,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = "待比较",
        CustomerId = customerId,
        CustomerName = "伪造客户名",
        Remark = "ERP416_SQL_NEW"
    };

    private async Task<Dictionary<string, long>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in SnapshotTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM db_owner.[{table}]";
            snapshot[table] = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        return snapshot;
    }

    private static void AssertUnchanged(Dictionary<string, long> before, Dictionary<string, long> after)
    {
        foreach (var (table, count) in before)
        {
            Assert.True(after.TryGetValue(table, out var current), $"快照缺少 {table}");
            Assert.Equal(count, current);
        }
    }

    // ==================== 1. 读取 / 写入入口：范围先于计数与分页，被拒零写入 ====================

    [Fact]
    public async Task 受限业务员_读取与写入入口按持久化归属收敛且拒绝非披露()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = QuoteController(db, _fixture.OperatorUserId);

        // 台账：只有本人客户 3 行可见（本人 / 混合批次本人行 / 纯本人批次行），他人 / 空归属 / 已删除都不计入。
        var page = await OkDataAsync<PagedResult<PurchaseQuote>>(ctl.GetPaged(new PageQuery { PageSize = 100 }));
        Assert.Equal(3, page.Total);
        Assert.Contains(page.Items, q => q.Id == _fixture.OwnQuoteId);
        Assert.DoesNotContain(page.Items, q => q.Id == _fixture.ForeignQuoteId || q.Id == _fixture.NullOwnerQuoteId);

        // 详情：本人可读；他人 / 空归属 / 已删除 / 不存在返回同一非披露错误。
        Assert.Equal(_fixture.OwnQuoteId, (await OkDataAsync<PurchaseQuote>(ctl.GetById(_fixture.OwnQuoteId))).Id);
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.ForeignQuoteId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.NullOwnerQuoteId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.DeletedQuoteId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(MissingQuoteId));

        // 新增：范围外 / 空归属归属客户被拒。
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.ProposedCustomerDeniedText,
            () => ctl.Create(NewQuote($"PQ-SQL-{Guid.NewGuid():N}", _fixture.CustomerBId)));
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.ProposedCustomerDeniedText,
            () => ctl.Create(NewQuote($"PQ-SQL-{Guid.NewGuid():N}", null)));

        // 删除 / 批量删除混入范围外：非披露拒绝且零删除。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Delete(_fixture.ForeignQuoteId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.BatchDelete(
            new List<long> { _fixture.OwnQuoteId, _fixture.ForeignQuoteId }));

        // 派生只读：按范围收敛，范围外单行打开非披露拒绝。
        var history = await OkDataAsync<PurchaseQuotePriceHistoryView>(ctl.PriceHistory(
            new PurchaseQuotePriceHistoryQuery { ProductId = 310L, PageSize = 200 }));
        Assert.Equal(3, history.TotalCount);
        await AssertNonDisclosingNotFoundAsync(() => ctl.QuotePriceHistory(_fixture.ForeignQuoteId));
        var funnel = await OkDataAsync<PurchaseQuoteConversionFunnelView>(ctl.ConversionFunnel(
            new PurchaseQuoteConversionFunnelQuery { PageSize = 200 }));
        Assert.Equal(3, funnel.TotalLineCount);

        AssertUnchanged(before, await SnapshotAsync());
        Assert.False((await _fixture.CreateDbContext().PurchaseQuotes.AsNoTracking()
            .SingleAsync(q => q.Id == _fixture.OwnQuoteId)).IsDeleted);
    }

    // ==================== 2. 转单与批次：目的地授权 + 整批判定 ====================

    [Fact]
    public async Task 受限业务员_混合批次整批拒绝_纯本人批次转单成功()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = QuoteController(db, _fixture.OperatorUserId);

        await AssertNonDisclosingNotFoundAsync(() => ctl.BatchOrderPlan(_fixture.BatchNo, null));
        await AssertNonDisclosingNotFoundAsync(() => ctl.BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = _fixture.BatchNo }));

        // 本人来源带入预填（不落库）。
        var prefill = await OkDataAsync<PurchaseOrderPrefillResult>(ctl.OrderPrefill(_fixture.OwnQuoteId));
        Assert.Equal(_fixture.OwnQuoteId, prefill.SourceId);

        // 纯本人批次：转单成功并留痕。
        var converted = await OkDataAsync<PurchaseQuoteBatchConversionResult>(ctl.BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = _fixture.OwnBatchNo }));
        Assert.Equal(1, converted.OrderCount);

        await using var check = _fixture.CreateDbContext();
        var ownBatchLine = await check.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == _fixture.OwnBatchLineId);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, ownBatchLine.Status);

        var after = await SnapshotAsync();
        Assert.Equal(before["PurchaseOrders"] + 1, after["PurchaseOrders"]);
        Assert.Equal(before["PurchaseQuoteDecisions"], after["PurchaseQuoteDecisions"]);
    }

    // ==================== 3. 审批：批次状态与记录决定 ====================

    [Fact]
    public async Task 受限业务员_审批批次与记录决定按归属授权()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = DecisionController(db, _fixture.OperatorUserId);

        await AssertNonDisclosingNotFoundAsync(() => ctl.BatchStatus(_fixture.BatchNo));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = _fixture.ForeignQuoteId, Decision = PurchaseQuoteApproval.Approved, DecidedByName = "伪造决定人"
        }));

        var batch = await OkDataAsync<PurchaseQuoteDecisionBatch>(ctl.BatchStatus(_fixture.OwnBatchNo));
        Assert.True(batch.LineCount >= 1);

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 4. 身份 / 菜单拒绝矩阵 ====================

    [Fact]
    public async Task 身份与菜单拒绝矩阵_逐入口fail_closed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UnauthorizedText),
            (_fixture.DeletedUserId, ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UserDeletedText),
            (_fixture.DisabledUserId, ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.UserDisabledText),
            (_fixture.MenuLessUserId, ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.MenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = QuoteController(db, userId);
            var decisions = DecisionController(db, userId);
            await AssertDeniedAsync(code, text, () => ctl.GetPaged(new PageQuery()));
            await AssertDeniedAsync(code, text, () => ctl.GetById(_fixture.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.Create(NewQuote("PQ-SQL-X", _fixture.CustomerAId)));
            await AssertDeniedAsync(code, text, () => ctl.Delete(_fixture.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.OrderPrefill(_fixture.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.ToPurchaseOrder(_fixture.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.BatchOrderPlan(_fixture.BatchNo, null));
            await AssertDeniedAsync(code, text, () => ctl.PriceHistory(
                new PurchaseQuotePriceHistoryQuery { ProductId = 310L }));
            await AssertDeniedAsync(code, text, () => ctl.ConversionFunnel(new PurchaseQuoteConversionFunnelQuery()));
            await AssertDeniedAsync(code, text, () => decisions.BatchStatus(_fixture.BatchNo));
            await AssertDeniedAsync(code, text, () => decisions.Decide(new PurchaseQuoteDecisionRequest
            {
                QuoteId = _fixture.OwnQuoteId, Decision = PurchaseQuoteApproval.Approved
            }));
        }

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 5. 特权账号 ====================

    [Fact]
    public async Task 特权账号_保留既有不受限口径()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = QuoteController(db, _fixture.PrivilegedUserId);

        var page = await OkDataAsync<PagedResult<PurchaseQuote>>(ctl.GetPaged(new PageQuery { PageSize = 100 }));
        Assert.True(page.Total >= 4);  // 本人 / 他人 / 空归属 / 混合批次两行
        Assert.Equal(_fixture.ForeignQuoteId, (await OkDataAsync<PurchaseQuote>(ctl.GetById(_fixture.ForeignQuoteId))).Id);
        Assert.Equal(_fixture.NullOwnerQuoteId, (await OkDataAsync<PurchaseQuote>(ctl.GetById(_fixture.NullOwnerQuoteId))).Id);
    }

    // ==================== 6. 两个独立连接竞态 ====================

    [Fact]
    public async Task 两个独立连接竞态_同一本人比价行读取一致()
    {
        Guard();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> ReadAsync(ErpDbContext db)
        {
            await gate.WaitAsync();
            return Json(await OkDataAsync<PurchaseQuote>(QuoteController(db, _fixture.OperatorUserId).GetById(_fixture.OwnQuoteId)));
        }

        var first = ReadAsync(dbA);
        var second = ReadAsync(dbB);
        gate.Release(2);
        var results = new[] { await first, await second };

        Assert.Equal(results[0], results[1]);
        Assert.Contains(_fixture.OwnQuoteNo, results[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 两个独立连接竞态_合法读取与越权并发_拒绝侧fail_closed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> AllowedAsync()
        {
            await gate.WaitAsync();
            return Json(await OkDataAsync<PurchaseQuote>(QuoteController(dbA, _fixture.OperatorUserId).GetById(_fixture.OwnQuoteId)));
        }

        async Task<string> DeniedAsync()
        {
            await gate.WaitAsync();
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => QuoteController(dbB, _fixture.OperatorUserId).GetById(_fixture.ForeignQuoteId));
            Assert.Equal(ErrorCodes.NotFound, ex.Code);
            return ex.Message;
        }

        var allowed = AllowedAsync();
        var denied = DeniedAsync();
        gate.Release(2);

        var allowedJson = await allowed;
        Assert.Equal(PurchaseQuoteAuthorizationRules.NotFoundText, await denied);
        Assert.Contains(_fixture.OwnQuoteNo, allowedJson, StringComparison.Ordinal);
        AssertUnchanged(before, await SnapshotAsync());
    }
}

/// <summary>
/// ERP-416 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <para>只以新播种的既有「供应商比价」/「采购订单」菜单授权与业务员客户数据范围驱动真实控制器，
/// 不新增 / 不修改任何既有权限模型。</para>
/// </summary>
public sealed class PurchaseQuoteAuthorizationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_PQSOURCEAUTH";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long OperatorUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }
    public long OwnQuoteId { get; private set; }
    public long ForeignQuoteId { get; private set; }
    public long NullOwnerQuoteId { get; private set; }
    public long DeletedQuoteId { get; private set; }
    public string OwnQuoteNo { get; private set; } = string.Empty;
    public string BatchNo { get; private set; } = string.Empty;
    public string OwnBatchNo { get; private set; } = string.Empty;
    public long OwnBatchLineId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-416] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
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

        Console.WriteLine("[ERP-416] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 比价夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP416 特权角色", RoleCode = $"ERP416-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        await GrantMenusAsync(db, privilegedRole.Id,
            PurchaseQuoteAuthorizationRules.RequiredMenuCode, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「供应商比价」+「采购订单」菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = restricted.Id, RoleId = await AddRoleAsync(db,
                PurchaseQuoteAuthorizationRules.RequiredMenuCode, PurchaseQuoteAuthorizationRules.DestinationMenuCode)
        });
        await db.SaveChangesAsync();
        OperatorUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP416 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP416 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP416 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        var own = await AddQuoteAsync(db, $"PQ-OWN-{Tag()}", customerA.Id, "ERP416 可见客户");
        OwnQuoteId = own.Id;
        OwnQuoteNo = own.QuoteNo;
        ForeignQuoteId = (await AddQuoteAsync(db, $"PQ-FGN-{Tag()}", customerB.Id, "ERP416 隐藏客户")).Id;
        // 空归属：CustomerName 刻意写成受限账号可见客户（不是授权依据）。
        NullOwnerQuoteId = (await AddQuoteAsync(db, $"PQ-NUL-{Tag()}", null, "ERP416 可见客户")).Id;
        DeletedQuoteId = (await AddQuoteAsync(db, $"PQ-DEL-{Tag()}", customerA.Id, "ERP416 可见客户",
            deleted: true)).Id;

        BatchNo = $"PQ-BATCH-{Tag()}";
        await AddQuoteAsync(db, BatchNo, customerA.Id, "ERP416 可见客户");
        await AddQuoteAsync(db, BatchNo, customerB.Id, "ERP416 隐藏客户");

        OwnBatchNo = $"PQ-OWNBATCH-{Tag()}";
        OwnBatchLineId = (await AddQuoteAsync(db, OwnBatchNo, customerA.Id, "ERP416 可见客户")).Id;

        // 3) 拒绝侧账号：无菜单 / 已禁用 / 已删除。
        MenuLessUserId = await SeedDeniedUserAsync(db, Array.Empty<string>(), UserStatus.Enabled);
        DisabledUserId = await SeedDeniedUserAsync(db, new[] { PurchaseQuoteAuthorizationRules.RequiredMenuCode },
            UserStatus.Disabled);
        DeletedUserId = await SeedDeniedUserAsync(db, new[] { PurchaseQuoteAuthorizationRules.RequiredMenuCode },
            UserStatus.Enabled, deleted: true);

        Console.WriteLine("[ERP-416] 既有授权 + 比价夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp416-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP416 隔离账号", Status = status
    };

    /// <summary>短随机片段（比价号 / 批次号用；保证 <c>APV-{QuoteNo}-#{Id}</c> 落在 DecisionRef 长度上限内）。</summary>
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP416-{Guid.NewGuid():N}", RoleCode = $"ERP416-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        await GrantMenusAsync(db, role.Id, menuCodes);
        return role.Id;
    }

    private static async Task GrantMenusAsync(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null) continue;
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<long> SeedDeniedUserAsync(ErpDbContext db, string[] menuCodes, UserStatus status,
        bool deleted = false)
    {
        var user = NewUser(status);
        user.IsDeleted = deleted;
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = await AddRoleAsync(db, menuCodes) });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<PurchaseQuote> AddQuoteAsync(ErpDbContext db, string quoteNo, long? customerId,
        string customerName, bool deleted = false)
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = new DateTime(2026, 9, 1),
            ProductId = 310L,
            ProductName = "ERP416 SQL 商品",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 100m,
            SupplierId = 88L,
            SupplierName = "ERP416 SQL 档口",
            SupplierType = "档口",
            QuotePrice = 2m,
            TotalAmount = 200m,
            Currency = "USD",
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = true,
            Status = PurchaseQuoteConversion.SelectedStatus,
            CustomerId = customerId,
            CustomerName = customerName,
            Remark = "ERP416_SQL_TEST",
            IsDeleted = deleted
        };
        db.PurchaseQuotes.Add(quote);
        await db.SaveChangesAsync();

        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId,
            SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = 1L,
            DecidedByName = "审批人",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
        });
        await db.SaveChangesAsync();
        return quote;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseQuoteAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseQuoteAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
