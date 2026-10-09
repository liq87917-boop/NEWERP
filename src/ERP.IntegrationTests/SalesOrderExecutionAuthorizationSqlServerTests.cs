using System.Text.Json;
using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-413 规范销售订单（<c>api/sales-orders</c>）执行证据入口实时授权的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」<c>sales-order</c> 功能菜单授权与
/// 业务员客户数据范围驱动真实 <see cref="SalesOrderController"/>；不新增 / 不修改任何既有菜单 / 角色 / 用户授权，
/// 无匿名 / 管理员降级，也不把导出菜单 <c>sales-order-export</c> 当作模块权限。</item>
/// <item><b>逐入口证明</b>：时间线 / 财务核对 / 进度 / 退货影响 / 收款引用证据 / 销项发票证据（含列表批量汇总）
/// 在受限业务员下本人订单可读且与直接派生结果一致；他人订单 / 已删除订单 / 不存在订单返回**同一**非披露错误；
/// 批量显式 Id 混入任何不可访问订单**整批拒绝**（无部分行 / 计数）。</item>
/// <item><b>身份拒绝矩阵</b>：无身份 / 已删除账号按未认证拒绝，已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单按权限不足拒绝。</item>
/// <item><b>零写入证据</b>：每次拒绝 / 读取前后对规范业务表（<c>SalesOrders</c> / <c>SalesOrderDetails</c> /
/// <c>StockOuts</c> / <c>StockOutDetails</c> / <c>StockMovements</c>（保留库存来源单据审计） /
/// <c>CustomerReceiptAllocations</c> / <c>CustomerSalesInvoiceEvidences</c> / <c>CustomerSalesInvoiceAllocations</c> /
/// <c>FinanceDepositApplies</c> / <c>SysOperationLogs</c>）做只读快照，全部不变。</item>
/// <item><b>两个独立连接竞态</b>：① 两条连接并发读取同一本人订单证据 → 结果一致；② 一条合法本人读取与一条他人越权
/// 并发 → 合法读取成功、越权 fail closed，收尾快照证明零写入。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderExecutionAuthorizationSqlServerTests
    : IClassFixture<SalesOrderExecutionAuthorizationSqlServerFixture>
{
    private readonly SalesOrderExecutionAuthorizationSqlServerFixture _fixture;

    public SalesOrderExecutionAuthorizationSqlServerTests(SalesOrderExecutionAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>必然不存在的订单 Id（批量显式 Id 混入不存在订单用）。</summary>
    private const long MissingOrderId = 9_413_000L;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockOutDetails", "StockMovements",
        "CustomerReceiptAllocations", "CustomerSalesInvoiceEvidences", "CustomerSalesInvoiceAllocations",
        "FinanceDepositApplies", "SysOperationLogs",
    };

    /// <summary>六个单张执行证据入口（路由标签 → 控制器动作）。</summary>
    private static readonly (string Label, Func<SalesOrderController, long, Task<IActionResult>> Call)[] SingleEntries =
    {
        ("timeline", (c, id) => c.Timeline(id)),
        ("finance-reconciliation", (c, id) => c.FinanceReconciliation(id)),
        ("progress", (c, id) => c.Progress(id)),
        ("return-impact", (c, id) => c.ReturnImpact(id)),
        ("receipt-evidence", (c, id) => c.ReceiptEvidence(id)),
        ("invoice-evidence", (c, id) => c.InvoiceEvidence(id)),
    };

    /// <summary>ERP-432 三个运营核对读取路由（路由标签 → 控制器动作；默认查询无客户筛选）。</summary>
    private static readonly (string Label, Func<SalesOrderController, Task<IActionResult>> Call)[] ReportEntries =
    {
        ("delivery-exceptions", c => c.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery())),
        ("shipment-finance-report", c => c.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery())),
        ("receipt-reconciliation-report",
            c => c.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery())),
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderExecutionAuthorizationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderController NewController(ErpDbContext db, long? userId, string path = "/api/sales-orders")
    {
        var http = new DefaultHttpContext { };
        http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return new SalesOrderController(db, new DocumentNumberService(db))
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
        Assert.Equal(SalesOrderExecutionAuthorizationRules.NotFoundText, ex.Message);
    }

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

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    // ==================== 1. 受限业务员：本人订单可读且与直接派生一致（部分出货 / 退货 / 收款 / 发票） ====================

    [Fact]
    public async Task 受限业务员_本人订单_每个执行证据入口可读且与直接派生一致()
    {
        Guard();
        var id = _fixture.OrderAId;
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        Assert.Equal(Json(await OrderExecutionTimeline.ForSalesOrderAsync(db, id)),
            Json(await OkDataAsync<List<OrderTimelineEvent>>(ctl.Timeline(id))));
        Assert.Equal(Json(await OrderFinanceReconciliation.ForSalesOrderAsync(db, id)),
            Json(await OkDataAsync<OrderFinanceReconciliationView>(ctl.FinanceReconciliation(id))));
        Assert.Equal(Json(await SalesOrderReturnImpact.ForOrderAsync(db, id)),
            Json(await OkDataAsync<SalesOrderReturnImpactView>(ctl.ReturnImpact(id))));
        Assert.Equal(Json(await SalesOrderReceiptEvidence.ForOrderAsync(db, id)),
            Json(await OkDataAsync<SalesOrderReceiptEvidenceDetail>(ctl.ReceiptEvidence(id))));
        Assert.Equal(Json(await SalesOrderInvoiceEvidence.ForOrderAsync(db, id)),
            Json(await OkDataAsync<SalesOrderInvoiceEvidenceDetail>(ctl.InvoiceEvidence(id))));

        // 部分出货 / 部分退货的既有语义不变。
        var progress = await OkDataAsync<SalesOrderProgressView>(ctl.Progress(id));
        Assert.Equal(Json(await SalesOrderProgress.ForSalesOrderAsync(db, id)), Json(progress));
        Assert.Equal(4m, progress.Shipment.ShippedQuantity);
        Assert.Equal(6m, progress.Shipment.OutstandingQuantity);
        Assert.Equal(SalesOrderProgress.ShipmentPartial, progress.Shipment.ShipmentStatus);
        Assert.Equal(3m, (await OkDataAsync<SalesOrderReturnImpactView>(ctl.ReturnImpact(id))).NetShippedTotal);
    }

    // ==================== 2. 受限业务员：他人 / 已删除 / 不存在订单逐入口统一非披露错误 ====================

    [Fact]
    public async Task 受限业务员_他人已删除不存在订单_每个入口返回同一非披露错误()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        // 本人订单放行（对照）。
        Assert.NotNull(await OkDataAsync<SalesOrderProgressView>(ctl.Progress(_fixture.OrderAId)));

        foreach (var (label, call) in SingleEntries)
        {
            Assert.True(call is not null, label);
            await AssertNonDisclosingNotFoundAsync(() => call(ctl, _fixture.OrderBId));       // 他人
            await AssertNonDisclosingNotFoundAsync(() => call(ctl, _fixture.DeletedOrderAId)); // 已删除
            await AssertNonDisclosingNotFoundAsync(() => call(ctl, MissingOrderId));           // 不存在
        }

        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.OrderBId));
        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 3. 批量显式 Id：整批拒绝（无部分行 / 计数） ====================

    [Fact]
    public async Task 批量显式Id_仅本人放行_混入他人或不存在整批拒绝()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        var ownReceipt = await OkDataAsync<SalesOrderReceiptEvidenceBatch>(ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = _fixture.OrderAId.ToString() }));
        Assert.Equal(1, ownReceipt.ItemCount);
        Assert.Equal(_fixture.OrderAId, ownReceipt.Items[0].SalesOrderId);

        var ownInvoice = await OkDataAsync<SalesOrderInvoiceEvidenceBatch>(ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = _fixture.OrderAId.ToString() }));
        Assert.Equal(1, ownInvoice.ItemCount);

        var empty = await OkDataAsync<SalesOrderReceiptEvidenceBatch>(ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery()));
        Assert.Equal(0, empty.RequestedCount);
        Assert.Empty(empty.Items);

        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = $"{_fixture.OrderAId},{_fixture.OrderBId}" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = $"{_fixture.OrderAId},{MissingOrderId}" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{_fixture.OrderAId},{_fixture.OrderBId}" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{_fixture.OrderAId},{MissingOrderId}" }));

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
            (null, ErrorCodes.Unauthorized, SalesOrderExecutionAuthorizationRules.UnauthorizedText),
            (_fixture.DeletedUserId, ErrorCodes.Unauthorized, SalesOrderExecutionAuthorizationRules.UserDeletedText),
            (_fixture.DisabledUserId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.UserDisabledText),
            (_fixture.MenuLessUserId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.MenuDeniedText),
            (_fixture.ExportOnlyUserId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.MenuDeniedText),
            (_fixture.RevokedMenuUserId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.MenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = NewController(db, userId);
            foreach (var (label, call) in SingleEntries)
            {
                var ex = await Assert.ThrowsAsync<BusinessException>(() => call(ctl, _fixture.OrderAId));
                Assert.True(ex.Code == code, $"{label} 期望 {code} 实际 {ex.Code}");
                Assert.Equal(text, ex.Message);
            }

            foreach (var (label, call) in ReportEntries)
            {
                var reportEx = await Assert.ThrowsAsync<BusinessException>(() => call(ctl));
                Assert.True(reportEx.Code == code, $"{label} 期望 {code} 实际 {reportEx.Code}");
                Assert.Equal(text, reportEx.Message);
            }

            await Assert.ThrowsAsync<BusinessException>(() => ctl.ReceiptEvidenceSummaries(
                new SalesOrderReceiptEvidenceQuery { Ids = _fixture.OrderAId.ToString() }));
        }

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 5. 特权账号保留既有全量口径 ====================

    [Fact]
    public async Task 特权账号_可读他人订单证据且保留既有口径()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        foreach (var (_, call) in SingleEntries)
        {
            var ok = Assert.IsType<OkObjectResult>(await call(ctl, _fixture.OrderBId));
            Assert.NotNull(ok.Value);
        }

        Assert.Equal(_fixture.OrderBId, (await OkDataAsync<SalesOrder>(ctl.GetById(_fixture.OrderBId))).Id);
    }

    // ==================== 6. 两个独立连接竞态 ====================

    [Fact]
    public async Task 两个独立连接竞态_同一本人订单证据读取一致()
    {
        Guard();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> ReadAsync(ErpDbContext db)
        {
            await gate.WaitAsync();
            var progress = await OkDataAsync<SalesOrderProgressView>(
                NewController(db, _fixture.RestrictedUserId).Progress(_fixture.OrderAId));
            return Json(progress);
        }

        var first = ReadAsync(dbA);
        var second = ReadAsync(dbB);
        gate.Release(2);
        var results = new[] { await first, await second };

        Assert.Equal(results[0], results[1]);
        Assert.Contains(_fixture.OrderANo, results[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 两个独立连接竞态_合法读取与他人越权并发_拒绝侧fail_closed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> AllowedAsync()
        {
            await gate.WaitAsync();
            return Json(await OkDataAsync<SalesOrderReceiptEvidenceDetail>(
                NewController(dbA, _fixture.RestrictedUserId).ReceiptEvidence(_fixture.OrderAId)));
        }

        async Task<string> DeniedAsync()
        {
            await gate.WaitAsync();
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(dbB, _fixture.RestrictedUserId).ReceiptEvidence(_fixture.OrderBId));
            Assert.Equal(ErrorCodes.NotFound, ex.Code);
            return ex.Message;
        }

        var allowed = AllowedAsync();
        var denied = DeniedAsync();
        gate.Release(2);

        var allowedJson = await allowed;
        Assert.Equal(SalesOrderExecutionAuthorizationRules.NotFoundText, await denied);
        Assert.Contains(_fixture.OrderANo, allowedJson, StringComparison.Ordinal);
        AssertUnchanged(before, await SnapshotAsync());
    }
    // ==================== 7. ERP-432 运营核对读取路由：实时授权 + 范围下推 ====================

    [Fact]
    public async Task 受限业务员_三个运营核对读取路由_只返回范围内客户数据()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        var delivery = await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()));
        Assert.Equal(1, delivery.Total);
        Assert.Equal(_fixture.CustomerAId, Assert.Single(delivery.Items).CustomerId);

        var shipment = await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()));
        Assert.Equal(1, shipment.Total);
        Assert.Equal(_fixture.CustomerAId, Assert.Single(shipment.Groups).CustomerId);

        var receipt = await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()));
        Assert.Equal(1, receipt.Total);
        Assert.Equal(_fixture.CustomerAId, Assert.Single(receipt.Groups).CustomerId);
    }

    [Fact]
    public async Task 受限业务员_筛选范围外客户_三个运营核对读取路由返回空且无计数()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);
        var foreign = _fixture.CustomerBId;

        var delivery = await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery { CustomerId = foreign }));
        Assert.Equal(0, delivery.Total);
        Assert.Empty(delivery.Items);

        var shipment = await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery { CustomerId = foreign }));
        Assert.Equal(0, shipment.Total);
        Assert.Empty(shipment.Groups);

        var receipt = await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery { CustomerId = foreign }));
        Assert.Equal(0, receipt.Total);
        Assert.Empty(receipt.Groups);
    }

    [Fact]
    public async Task 特权账号_三个运营核对读取路由保留既有全量口径()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        Assert.Equal(2, (await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()))).Total);
        Assert.Equal(2, (await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()))).Total);
        Assert.Equal(2, (await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()))).Total);
    }
}

/// <summary>
/// ERP-413 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <para>只以新播种的既有「销售订单」菜单授权与业务员客户数据范围驱动真实控制器，不新增 / 不修改任何既有权限模型。</para>
/// </summary>
public sealed class SalesOrderExecutionAuthorizationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOEXECAUTH";
    private const long ProductId = 9_413_010L;

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
    public long RevokedMenuUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }
    public long OrderAId { get; private set; }
    public long OrderBId { get; private set; }
    public long DeletedOrderAId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }
    public string OrderANo { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-413] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-413] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 订单 / 证据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP413 特权角色", RoleCode = $"ERP413-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();

        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「销售订单」功能菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = restricted.Id, RoleId = await AddRoleAsync(db, "sales-order")
        });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP413 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP413 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP413 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        await SeedOrdersAsync(db, tag, customerA.Id, customerB.Id);

        // 3) 拒绝侧账号：无菜单 / 仅导出菜单 / 已撤销菜单 / 已禁用 / 已删除。
        MenuLessUserId = await SeedDeniedUserAsync(db, Array.Empty<string>(), UserStatus.Enabled);
        ExportOnlyUserId = await SeedDeniedUserAsync(db, new[] { "sales-order-export" }, UserStatus.Enabled);
        RevokedMenuUserId = await SeedRevokedMenuUserAsync(db);
        DisabledUserId = await SeedDeniedUserAsync(db, new[] { "sales-order" }, UserStatus.Disabled);
        DeletedUserId = await SeedDeniedUserAsync(db, new[] { "sales-order" }, UserStatus.Enabled, deleted: true);

        Console.WriteLine("[ERP-413] 既有授权 + 订单证据夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp413-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        DisplayName = "ERP413 隔离账号",
        Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP413-{Guid.NewGuid():N}", RoleCode = $"ERP413-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        foreach (var menuCode in menuCodes)
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

        return role.Id;
    }

    /// <summary>播种本人订单（部分出货 4/10 与部分退货 1）与后续财务证据。</summary>
    private async Task SeedOrdersAsync(ErpDbContext db, string tag, long customerAId, long customerBId)
    {
        var orderA = new SalesOrder
        {
            OrderNo = $"SO-413-A-{tag}", OrderDate = DateTime.Today, CustomerId = customerAId,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 100m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(orderA);
        await db.SaveChangesAsync();
        OrderAId = orderA.Id;
        OrderANo = orderA.OrderNo;

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = orderA.Id, ProductId = ProductId, ProductName = $"商品{ProductId}",
            Unit = "PCS", Quantity = 10m, UnitPrice = 10m, Amount = 100m
        });
        await db.SaveChangesAsync();

        var stockOut = new StockOut
        {
            StockOutNo = $"CK-413-{tag}", StockOutDate = DateTime.Today, SalesOrderId = orderA.Id,
            CustomerId = customerAId, WarehouseId = 1, Status = DocumentStatus.Approved
        };
        db.StockOuts.Add(stockOut);
        await db.SaveChangesAsync();
        db.StockOutDetails.Add(new StockOutDetail
        {
            StockOutId = stockOut.Id, ProductId = ProductId, ProductName = $"商品{ProductId}",
            Unit = "PCS", Quantity = 4m
        });
        await db.SaveChangesAsync();
        stockOut.TotalQuantity = 4m;
        await db.SaveChangesAsync();

        var salesReturn = new SalesReturn
        {
            ReturnNo = $"XTH-413-{tag}", ReturnDate = DateTime.Today, CustomerId = customerAId,
            CustomerName = "ERP413 可见客户", WarehouseId = 1, SourceStockOutId = stockOut.Id,
            TotalQuantity = 1m, Status = DocumentStatus.Approved
        };
        db.SalesReturns.Add(salesReturn);
        await db.SaveChangesAsync();
        db.SalesReturnDetails.Add(new SalesReturnDetail
        {
            SalesReturnId = salesReturn.Id, ReturnNo = salesReturn.ReturnNo, ProductId = ProductId,
            ProductName = $"商品{ProductId}", Unit = "PCS", Quantity = 1m
        });
        await db.SaveChangesAsync();

        await SeedFinanceEvidenceAsync(db, tag, customerAId, customerBId, orderA);
    }

    /// <summary>播种部分收款 350 / 部分开票 350 证据，以及他人订单与已删除订单。</summary>
    private async Task SeedFinanceEvidenceAsync(ErpDbContext db, string tag, long customerAId, long customerBId,
        SalesOrder orderA)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = $"SK-413-{tag}", ReceiptDate = DateTime.Today, CustomerId = customerAId,
            Amount = 600m, Currency = Currency.USD, PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        await db.SaveChangesAsync();
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id, ReceiptNo = receipt.ReceiptNo, ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status, ReceiptStatusText = string.Empty, ReceiptAmount = receipt.Amount,
            SalesOrderId = orderA.Id, OrderNo = orderA.OrderNo, OrderDate = orderA.OrderDate,
            OrderStatus = (int)orderA.Status, OrderCurrency = orderA.Currency.ToString(),
            CustomerId = customerAId, CustomerCode = "C413-SNAP", CustomerName = "客户快照",
            AllocatedAmount = 350m, Currency = "USD",
            Status = CustomerReceiptAllocationRules.StatusActive, AllocatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();

        var invoiceNumber = $"INV-413-{tag}";
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary, InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(invoiceNumber),
            InvoiceDate = DateTime.Today, CustomerId = customerAId, CustomerCode = "C413-SNAP",
            CustomerName = "客户快照", Currency = "USD", NetAmount = 600m, TaxAmount = 0m, GrossAmount = 600m,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded, RecordedAt = DateTime.Now
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        await db.SaveChangesAsync();
        db.CustomerSalesInvoiceAllocations.Add(new CustomerSalesInvoiceAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id, SalesOrderId = orderA.Id, OrderNo = orderA.OrderNo,
            OrderDate = orderA.OrderDate, OrderStatus = (int)orderA.Status,
            OrderCurrency = orderA.Currency.ToString(), CustomerId = customerAId, CustomerCode = "C413-SNAP",
            CustomerName = "客户快照", AllocatedAmount = 350m, Currency = "USD", CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();

        var orderB = new SalesOrder
        {
            OrderNo = $"SO-413-B-{tag}", OrderDate = DateTime.Today, CustomerId = customerBId,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 200m, Status = DocumentStatus.Approved
        };
        var deleted = new SalesOrder
        {
            OrderNo = $"SO-413-D-{tag}", OrderDate = DateTime.Today, CustomerId = customerAId,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 300m,
            Status = DocumentStatus.Approved, IsDeleted = true
        };
        db.SalesOrders.AddRange(orderB, deleted);
        await db.SaveChangesAsync();
        OrderBId = orderB.Id;
        DeletedOrderAId = deleted.Id;
    }

    /// <summary>播种拒绝侧账号（无菜单 / 仅导出菜单 / 已禁用 / 已删除），不新增任何业务客户数据。</summary>
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

    /// <summary>授予后又撤销既有「销售订单」菜单（角色仍在，授权已回收 → 下一次请求立即收敛）。</summary>
    private static async Task<long> SeedRevokedMenuUserAsync(ErpDbContext db)
    {
        var user = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        var roleId = await AddRoleAsync(db, "sales-order");
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync();
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == roleId));
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderExecutionAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderExecutionAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}



