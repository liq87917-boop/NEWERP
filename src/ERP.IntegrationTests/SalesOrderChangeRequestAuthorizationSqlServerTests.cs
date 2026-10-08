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
/// ERP-414 销售订单变更申请登记册（<c>api/sales-order-change-requests</c>）实时授权与来源实时归属的真实 SQL Server
/// 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」<c>sales-order</c> 功能菜单授权与
/// 业务员客户数据范围驱动真实 <see cref="SalesOrderChangeRequestController"/>；不新增 / 不修改任何既有菜单 / 角色 /
/// 用户授权，无匿名 / 管理员降级，也不把导出菜单 <c>sales-order-export</c> 当作模块权限。</item>
/// <item><b>逐入口证明</b>：台账 / 详情 / 指定来源清单 / 来源候选 / 登记 / 编辑 / 提交 / 取消在受限业务员下只作用于
/// 本人客户来源的申请；范围外 / 来源已删除 / 来源缺失 / 不存在的申请返回**同一**非披露错误；
/// 台账在计数 / 分页 / 候选之前按来源订单**实时**归属下推。</item>
/// <item><b>身份拒绝矩阵</b>：无身份 / 已删除账号按未认证拒绝，已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单按权限不足拒绝。</item>
/// <item><b>零写入证据</b>：每次拒绝 / 读取前后对规范业务表（含 <c>SalesOrderChangeRequests</c> /
/// <c>SalesOrderChangeRequestDetails</c> / <c>SalesOrders</c> / <c>SalesOrderDetails</c> / <c>StockOuts</c> /
/// <c>StockMovements</c>（保留库存来源单据审计） / <c>Quotations</c> / <c>FinanceReceipts</c> /
/// <c>SysOperationLogs</c>）做只读快照，全部不变。</item>
/// <item><b>两个独立连接竞态</b>：① 两条连接并发读取同一本人申请 → 结果一致；② 一条合法本人读取与一条他人越权
/// 并发 → 合法读取成功、越权 fail closed，收尾快照证明零写入。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderChangeRequestAuthorizationSqlServerTests
    : IClassFixture<SalesOrderChangeRequestAuthorizationSqlServerFixture>
{
    private readonly SalesOrderChangeRequestAuthorizationSqlServerFixture _fixture;

    public SalesOrderChangeRequestAuthorizationSqlServerTests(
        SalesOrderChangeRequestAuthorizationSqlServerFixture fixture) => _fixture = fixture;

    /// <summary>必然不存在的来源订单 / 申请 Id。</summary>
    private const long MissingSourceOrderId = 9_414_999L;
    private const long MissingRequestId = 9_414_998L;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements", "Quotations", "FinanceReceipts",
        "SalesOrderChangeRequests", "SalesOrderChangeRequestDetails", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderChangeRequestAuthorizationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderChangeRequestController NewController(ErpDbContext db, long? userId,
        string path = "/api/sales-order-change-requests")
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return new SalesOrderChangeRequestController(db, new DocumentNumberService(db))
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
        Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, ex.Message);
    }

    private static async Task AssertDeniedAsync(SalesOrderChangeRequestController controller,
        int code, string text, Func<SalesOrderChangeRequestController, Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(() => action(controller));
        Assert.Equal(code, ex.Code);
        Assert.Equal(text, ex.Message);
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

    /// <summary>
    /// ERP-414 变更申请单号的权威流水：授权先于发号，被拒的登记请求绝不推进该流水。
    /// </summary>
    private async Task<long> ChangeRequestSequenceTotalAsync()
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ISNULL(SUM(CurrentSequence), 0) FROM db_owner.[SysDocumentNumberRules] "
            + "WHERE DocumentType = @type AND IsDeleted = 0";
        cmd.Parameters.AddWithValue("@type", (int)DocumentType.SalesOrderChangeRequest);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string name, decimal quantity, decimal unitPrice)
        => new() { ProductId = productId, ProductName = name, Spec = "红", Unit = "PCS", Quantity = quantity, UnitPrice = unitPrice };

    // ==================== 1. 台账 / 详情 / 指定来源 / 来源候选：范围先于计数与候选 ====================

    [Fact]
    public async Task 受限业务员_台账详情指定来源候选按实时来源归属收敛且非披露()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        // 台账：只有本人来源订单的申请可见（他人来源 / 来源已删除 / 来源缺失都不计入总数）。
        var page = await OkDataAsync<PagedResult<SalesOrderChangeRequestDto>>(
            ctl.GetPaged(new SalesOrderChangeRequestQuery()));
        Assert.Equal(1, page.Total);
        var only = Assert.Single(page.Items);
        Assert.Equal(_fixture.OwnRequestId, only.Id);
        Assert.Equal(_fixture.OwnRequestNo, only.RequestNo);

        // 详情：本人可读；他人 / 来源已删除 / 来源缺失 / 不存在申请返回同一非披露错误。
        Assert.Equal(_fixture.OwnRequestId,
            (await OkDataAsync<SalesOrderChangeRequestDto>(ctl.GetById(_fixture.OwnRequestId))).Id);
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.ForeignRequestId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.DeletedSourceRequestId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(_fixture.MissingSourceRequestId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(MissingRequestId));

        // 指定来源清单：本人放行；他人 / 已删除 / 不存在来源返回同一非披露错误。
        var bySource = await OkDataAsync<List<SalesOrderChangeRequestDto>>(ctl.GetForSource(_fixture.OrderAId));
        Assert.Equal(_fixture.OwnRequestId, Assert.Single(bySource).Id);
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetForSource(_fixture.OrderBId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetForSource(_fixture.DeletedOrderAId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetForSource(MissingSourceOrderId));

        // 来源候选：只返回本人客户、未删除的订单。
        var options = await OkDataAsync<List<SalesOrderChangeRequestSourceOptionDto>>(ctl.SourceOptions(null));
        Assert.Contains(options, o => o.SalesOrderId == _fixture.OrderAId);
        Assert.DoesNotContain(options, o =>
            o.SalesOrderId == _fixture.OrderBId || o.SalesOrderId == _fixture.DeletedOrderAId);
    }

    // ==================== 2. 写入口：授权先于发号 / 明细替换 / 状态变更，且来源与下游不变 ====================

    [Fact]
    public async Task 受限业务员_写入口只作用于本人来源_范围外拒绝且来源不变()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await ChangeRequestSequenceTotalAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        var details = new List<SalesOrderChangeRequestDetailSaveDto>
        {
            Detail(101, "A 商品", 20, 5),
            Detail(102, "B 商品", 4, 25)
        };

        // 编辑本人申请：拟议按权威算法生效（200 = 20×5 + 4×25；定金 30% = 60）。
        var updated = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Update(_fixture.OwnRequestId,
            new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.OrderAId, Reason = "改数量", Details = details }));
        Assert.Equal(200m, updated.ProposedTotalAmount);
        Assert.Equal(60m, updated.ProposedDepositAmount);

        // 登记：他人 / 已删除 / 不存在来源一律非披露拒绝（授权先于发号）。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.OrderBId, Reason = "越权" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.DeletedOrderAId, Reason = "越权" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = MissingSourceOrderId, Reason = "越权" }));
        // 拟议客户字段不授予归属：即便拟议客户填成本人客户，他人来源仍被拒绝。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Create(new SalesOrderChangeRequestSaveDto
        { SalesOrderId = _fixture.OrderBId, CustomerId = _fixture.CustomerAId, Reason = "伪造拟议客户" }));

        // 编辑 / 提交 / 取消他人申请：非披露拒绝。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Update(_fixture.ForeignRequestId,
            new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.OrderBId, Reason = "越权", Details = details }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Submit(_fixture.ForeignRequestId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Cancel(_fixture.ForeignRequestId,
            new SalesOrderChangeRequestCancelRequest { Reason = "越权" }));
        // 编辑本人申请时伪造拟议来源为他人订单：非披露拒绝，明细未被替换。
        await AssertNonDisclosingNotFoundAsync(() => ctl.Update(_fixture.OwnRequestId,
            new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.OrderBId, Reason = "改挂来源", Details = details }));

        // 提交 / 取消本人申请成功（保留证据，不硬删除）。
        var submitted = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Submit(_fixture.OwnRequestId));
        Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, submitted.Status);
        var cancelled = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Cancel(_fixture.OwnRequestId,
            new SalesOrderChangeRequestCancelRequest { Reason = "客户撤回" }));
        Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);

        // 拒绝 / 成功路径都不新增本模块行，也不改写来源与下游记录。
        var after = await SnapshotAsync();
        foreach (var table in SnapshotTables)
        {
            if (table is not ("SalesOrderChangeRequests" or "SalesOrderChangeRequestDetails"))
                Assert.Equal(before[table], after[table]);
        }
        Assert.Equal(before["SalesOrderChangeRequests"], after["SalesOrderChangeRequests"]);

        await using var check = _fixture.CreateDbContext();
        var order = await check.SalesOrders.AsNoTracking().Include(o => o.Details)
            .SingleAsync(o => o.Id == _fixture.OrderAId);
        Assert.Equal(150m, order.TotalAmount);
        Assert.Equal(45m, order.DepositAmount);
        Assert.Equal(2, order.Details.Count(d => !d.IsDeleted));
        Assert.Equal(10m, order.Details.Single(d => d.ProductId == 101).Quantity);

        // 授权先于发号：被拒的登记请求绝不推进变更申请单号流水。
        Assert.Equal(sequenceBefore, await ChangeRequestSequenceTotalAsync());
    }

    // ==================== 3. 身份 / 菜单拒绝矩阵（逐入口 fail closed 且零写入） ====================

    [Fact]
    public async Task 身份与菜单拒绝矩阵_逐入口fail_closed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, SalesOrderChangeRequestAuthorizationRules.UnauthorizedText),
            (_fixture.DeletedUserId, ErrorCodes.Unauthorized, SalesOrderChangeRequestAuthorizationRules.UserDeletedText),
            (_fixture.DisabledUserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.UserDisabledText),
            (_fixture.MenuLessUserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.MenuDeniedText),
            (_fixture.ExportOnlyUserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.MenuDeniedText),
            (_fixture.RevokedMenuUserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.MenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = NewController(db, userId);
            await AssertDeniedAsync(ctl, code, text, c => c.GetPaged(new SalesOrderChangeRequestQuery()));
            await AssertDeniedAsync(ctl, code, text, c => c.GetById(_fixture.OwnRequestId));
            await AssertDeniedAsync(ctl, code, text, c => c.SourceOptions(null));
            await AssertDeniedAsync(ctl, code, text, c => c.GetForSource(_fixture.OrderAId));
            await AssertDeniedAsync(ctl, code, text, c => c.Create(
                new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.OrderAId, Reason = "x" }));
            await AssertDeniedAsync(ctl, code, text, c => c.Update(_fixture.OwnRequestId,
                new SalesOrderChangeRequestSaveDto { SalesOrderId = _fixture.OrderAId, Reason = "x" }));
            await AssertDeniedAsync(ctl, code, text, c => c.Submit(_fixture.OwnRequestId));
            await AssertDeniedAsync(ctl, code, text, c => c.Cancel(_fixture.OwnRequestId,
                new SalesOrderChangeRequestCancelRequest { Reason = "x" }));
        }

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 4. 来源改派 / 特权不受限历史访问 ====================

    [Fact]
    public async Task 来源客户改派后_历史快照客户不再是归属_受限不可见特权可见()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var restricted = NewController(db, _fixture.RestrictedUserId);
        var privileged = NewController(db, _fixture.PrivilegedUserId);

        // 改派前受限账号可读本人来源的申请。
        Assert.Equal(_fixture.OwnRequestId,
            (await OkDataAsync<SalesOrderChangeRequestDto>(restricted.GetById(_fixture.OwnRequestId))).Id);

        var order = await db.SalesOrders.SingleAsync(o => o.Id == _fixture.OrderAId);
        var originalCustomer = order.CustomerId;
        try
        {
            order.CustomerId = _fixture.CustomerBId;
            await db.SaveChangesAsync();

            // 申请上的来源客户快照仍是本人客户，但归属按**实时**来源（他人）判定。
            var snapshot = await db.SalesOrderChangeRequests.AsNoTracking()
                .SingleAsync(r => r.Id == _fixture.OwnRequestId);
            Assert.Equal(originalCustomer, snapshot.SourceCustomerId);

            await AssertNonDisclosingNotFoundAsync(() => restricted.GetById(_fixture.OwnRequestId));
            var page = await OkDataAsync<PagedResult<SalesOrderChangeRequestDto>>(
                restricted.GetPaged(new SalesOrderChangeRequestQuery()));
            Assert.DoesNotContain(page.Items, x => x.Id == _fixture.OwnRequestId);

            // 特权账号：保留既有不受限历史访问。
            Assert.Equal(_fixture.OwnRequestId,
                (await OkDataAsync<SalesOrderChangeRequestDto>(privileged.GetById(_fixture.OwnRequestId))).Id);
        }
        finally
        {
            order.CustomerId = originalCustomer;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 特权账号_保留既有不受限历史访问_可读范围外与来源缺失申请()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        Assert.Equal(_fixture.ForeignRequestId,
            (await OkDataAsync<SalesOrderChangeRequestDto>(ctl.GetById(_fixture.ForeignRequestId))).Id);
        Assert.Equal(_fixture.DeletedSourceRequestId,
            (await OkDataAsync<SalesOrderChangeRequestDto>(ctl.GetById(_fixture.DeletedSourceRequestId))).Id);
        Assert.Equal(_fixture.MissingSourceRequestId,
            (await OkDataAsync<SalesOrderChangeRequestDto>(ctl.GetById(_fixture.MissingSourceRequestId))).Id);

        var options = await OkDataAsync<List<SalesOrderChangeRequestSourceOptionDto>>(ctl.SourceOptions(null));
        Assert.Contains(options, o => o.SalesOrderId == _fixture.OrderBId);
    }

    // ==================== 5. 两个独立连接竞态 ====================

    [Fact]
    public async Task 两个独立连接竞态_同一本人申请读取一致()
    {
        Guard();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> ReadAsync(ErpDbContext db)
        {
            await gate.WaitAsync();
            return Json(await OkDataAsync<SalesOrderChangeRequestDto>(
                NewController(db, _fixture.RestrictedUserId).GetById(_fixture.OwnRequestId)));
        }

        var first = ReadAsync(dbA);
        var second = ReadAsync(dbB);
        gate.Release(2);
        var results = new[] { await first, await second };

        Assert.Equal(results[0], results[1]);
        Assert.Contains(_fixture.OwnRequestNo, results[0], StringComparison.Ordinal);
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
            return Json(await OkDataAsync<SalesOrderChangeRequestDto>(
                NewController(dbA, _fixture.RestrictedUserId).GetById(_fixture.OwnRequestId)));
        }

        async Task<string> DeniedAsync()
        {
            await gate.WaitAsync();
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(dbB, _fixture.RestrictedUserId).GetById(_fixture.ForeignRequestId));
            Assert.Equal(ErrorCodes.NotFound, ex.Code);
            return ex.Message;
        }

        var allowed = AllowedAsync();
        var denied = DeniedAsync();
        gate.Release(2);

        var allowedJson = await allowed;
        Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, await denied);
        Assert.Contains(_fixture.OwnRequestNo, allowedJson, StringComparison.Ordinal);
        AssertUnchanged(before, await SnapshotAsync());
    }

}

/// <summary>
/// ERP-414 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <para>只以新播种的既有「销售订单」菜单授权与业务员客户数据范围驱动真实控制器，不新增 / 不修改任何既有权限模型。</para>
/// </summary>
public sealed class SalesOrderChangeRequestAuthorizationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOCREQAUTH";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
    public long RevokedMenuUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }
    public long OrderAId { get; private set; }
    public long OrderBId { get; private set; }
    public long DeletedOrderAId { get; private set; }
    public long OwnRequestId { get; private set; }
    public long ForeignRequestId { get; private set; }
    public long DeletedSourceRequestId { get; private set; }
    public long MissingSourceRequestId { get; private set; }
    public string OwnRequestNo { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-414] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-414] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 订单 / 申请夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP414 特权角色", RoleCode = $"ERP414-P-{Guid.NewGuid():N}", IsSystem = true
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
            EmployeeCode = restricted.UserName, EmployeeName = "ERP414 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP414 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP414 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        await SeedOrdersAsync(db, customerA.Id, customerB.Id);
        await SeedRequestsAsync(db);

        // 3) 拒绝侧账号：无菜单 / 仅导出菜单 / 已撤销菜单 / 已禁用 / 已删除。
        MenuLessUserId = await SeedDeniedUserAsync(db, Array.Empty<string>(), UserStatus.Enabled);
        ExportOnlyUserId = await SeedDeniedUserAsync(db, new[] { "sales-order-export" }, UserStatus.Enabled);
        RevokedMenuUserId = await SeedRevokedMenuUserAsync(db);
        DisabledUserId = await SeedDeniedUserAsync(db, new[] { "sales-order" }, UserStatus.Disabled);
        DeletedUserId = await SeedDeniedUserAsync(db, new[] { "sales-order" }, UserStatus.Enabled, deleted: true);

        Console.WriteLine("[ERP-414] 既有授权 + 订单 / 申请夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp414-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP414 隔离账号", Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP414-{Guid.NewGuid():N}", RoleCode = $"ERP414-{Guid.NewGuid():N}", IsSystem = false
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

    /// <summary>播种本人订单 A（2 行明细，总额 150）、他人订单 B 与已删除订单 D（来源 A 客户）。</summary>
    private async Task SeedOrdersAsync(ErpDbContext db, long customerAId, long customerBId)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var orderA = new SalesOrder
        {
            OrderNo = $"SO-414-A-{tag}", OrderDate = DateTime.Today, CustomerId = customerAId,
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m, TotalAmount = 150m,
            DepositAmount = 45m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(orderA);
        await db.SaveChangesAsync();
        OrderAId = orderA.Id;

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = orderA.Id, ProductId = 101, ProductName = "A 商品", Spec = "红", Unit = "PCS",
            Quantity = 10m, UnitPrice = 5m, Amount = 50m
        });
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = orderA.Id, ProductId = 102, ProductName = "B 商品", Spec = "蓝", Unit = "PCS",
            Quantity = 4m, UnitPrice = 25m, Amount = 100m
        });
        await db.SaveChangesAsync();

        var orderB = new SalesOrder
        {
            OrderNo = $"SO-414-B-{tag}", OrderDate = DateTime.Today, CustomerId = customerBId,
            Currency = Currency.USD, ExchangeRate = 7.2m, TotalAmount = 200m, Status = DocumentStatus.Approved
        };
        var deleted = new SalesOrder
        {
            OrderNo = $"SO-414-D-{tag}", OrderDate = DateTime.Today, CustomerId = customerAId,
            Currency = Currency.USD, ExchangeRate = 7.2m, TotalAmount = 300m,
            Status = DocumentStatus.Approved, IsDeleted = true
        };
        db.SalesOrders.AddRange(orderB, deleted);
        await db.SaveChangesAsync();
        OrderBId = orderB.Id;
        DeletedOrderAId = deleted.Id;
    }

    /// <summary>
    /// 播种四张申请：本人来源（唯一可见）、他人来源、来源已删除、来源缺失。
    /// 后三张的**来源客户快照与拟议客户都写成受限账号可见的本人客户**，用于证明二者都不是授权依据。
    /// </summary>
    private async Task SeedRequestsAsync(ErpDbContext db)
    {
        var own = NewRequest($"SOC-{Guid.NewGuid():N}", OrderAId, CustomerAId, CustomerAId);
        db.SalesOrderChangeRequests.Add(own);
        await db.SaveChangesAsync();
        OwnRequestId = own.Id;
        OwnRequestNo = own.RequestNo;

        var foreign = NewRequest($"SOC-{Guid.NewGuid():N}", OrderBId, CustomerAId, CustomerAId);
        var deletedSource = NewRequest($"SOC-{Guid.NewGuid():N}", DeletedOrderAId, CustomerAId, CustomerAId);
        var missingSource = NewRequest($"SOC-{Guid.NewGuid():N}", 9_414_999L, CustomerAId, CustomerAId);
        db.SalesOrderChangeRequests.AddRange(foreign, deletedSource, missingSource);
        await db.SaveChangesAsync();
        ForeignRequestId = foreign.Id;
        DeletedSourceRequestId = deletedSource.Id;
        MissingSourceRequestId = missingSource.Id;
    }

    private static SalesOrderChangeRequest NewRequest(
        string requestNo, long salesOrderId, long snapshotCustomerId, long proposedCustomerId)
        => new()
        {
            RequestNo = requestNo,
            SalesOrderId = salesOrderId,
            SalesOrderNo = "SO-SNAP",
            SourceStatus = (int)DocumentStatus.Approved,
            SourceUpdatedAt = DateTime.Now,
            SourceDetailSignature = "2|101|10|5|102|4|25",
            SourceSnapshotMarker = "快照",
            Reason = "原始原因",
            SourceOrderDate = DateTime.Today,
            SourceCustomerId = snapshotCustomerId,
            SourceCurrency = Currency.USD,
            SourceExchangeRate = 7.2m,
            SourceTotalAmount = 150m,
            SourceDepositRatio = 30m,
            SourceDepositAmount = 45m,
            ProposedCustomerId = proposedCustomerId,
            ProposedOrderDate = DateTime.Today,
            ProposedCurrency = Currency.USD,
            ProposedExchangeRate = 7.2m,
            ProposedTotalAmount = 150m,
            ProposedDepositRatio = 30m,
            ProposedDepositAmount = 45m,
            Status = SalesOrderChangeRequestRules.StatusDraft,
            CreatedAt = DateTime.Now
        };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderChangeRequestAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderChangeRequestAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

