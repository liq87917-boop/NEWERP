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
/// ERP-489 销售订单变更申请登记册（<c>api/sales-order-change-requests</c>）入口实时授权的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」<c>sales-order</c> 功能菜单授权与
/// 业务员客户数据范围驱动真实 <see cref="SalesOrderChangeRequestController"/>；不新增 / 不修改任何既有菜单 / 角色 /
/// 用户授权，无匿名 / 管理员降级，也不把导出菜单 <c>sales-order-export</c> 当作模块权限。</item>
/// <item><b>逐入口 + 逐调用形状</b>：台账 / 来源候选 / 指定来源清单 / 详情 / 登记 / 编辑 / 提交 / 取消八条入口在
/// 「空路径 / 已赋值路径 / 完全未绑定请求上下文」三种形状下给出完全一致的判定；登记 / 编辑 / 提交 / 取消不再存在
/// 无授权的服务直调旁路。</item>
/// <item><b>身份拒绝矩阵</b>：缺失 / 零 / 未知 / 已删除账号按未认证拒绝，已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单
/// 按权限不足拒绝；受限业务员越界来源与申请一律同一非披露错误。</item>
/// <item><b>零写入 / 不发号证据</b>：每次拒绝前后对规范业务表（含 <c>SalesOrderChangeRequests</c> /
/// <c>SalesOrderChangeRequestDetails</c> / <c>SalesOrders</c> / <c>SalesOrderDetails</c> / <c>StockOuts</c> /
/// <c>StockMovements</c> / <c>Quotations</c> / <c>FinanceReceipts</c> / <c>SysOperationLogs</c>）做只读快照，
/// 并断言变更申请单号流水不推进。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderChangeRequestEntryAuthorizationSqlServerTests
    : IClassFixture<SalesOrderChangeRequestEntryAuthorizationSqlServerFixture>
{
    private readonly SalesOrderChangeRequestEntryAuthorizationSqlServerFixture _fixture;

    public SalesOrderChangeRequestEntryAuthorizationSqlServerTests(
        SalesOrderChangeRequestEntryAuthorizationSqlServerFixture fixture) => _fixture = fixture;

    private const string BasePath = "/api/sales-order-change-requests";
    private const long MissingSourceOrderId = 9_489_999L;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements", "Quotations", "FinanceReceipts",
        "SalesOrderChangeRequests", "SalesOrderChangeRequestDetails", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderChangeRequestEntryAuthorizationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderChangeRequestController Controller(ErpDbContext db, long? userId, string? path)
    {
        var http = new DefaultHttpContext
        {
            User = userId.HasValue
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
                : new ClaimsPrincipal(new ClaimsIdentity())
        };
        if (path is not null) http.Request.Path = path;

        return new SalesOrderChangeRequestController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static SalesOrderChangeRequestController ContextlessController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static async Task<T> OkDataAsync<T>(Task<IActionResult> action)
    {
        var ok = Assert.IsType<OkObjectResult>(await action);
        return Assert.IsType<ApiResponse<T>>(ok.Value).Data!;
    }

    private static async Task<BusinessException> AssertDeniedAsync(int expectedCode, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static SalesOrderChangeRequestSaveDto Save(long salesOrderId, string reason,
        List<SalesOrderChangeRequestDetailSaveDto>? details = null)
        => new() { SalesOrderId = salesOrderId, Reason = reason, Details = details };

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string name, decimal quantity, decimal unitPrice)
        => new() { ProductId = productId, ProductName = name, Spec = "红", Unit = "PCS", Quantity = quantity, UnitPrice = unitPrice };

    /// <summary>八条入口在给定身份 / 调用形状下的逐入口一致判定。</summary>
    private static async Task AssertAllRoutesDeniedAsync(
        ErpDbContext db, long? userId, string? path, long salesOrderId, long requestId, int expectedCode)
    {
        var ctl = Controller(db, userId, path);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.GetPaged(new SalesOrderChangeRequestQuery()))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode, () => ctl.SourceOptions(null))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode, () => ctl.GetForSource(salesOrderId))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode, () => ctl.GetById(requestId))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode, () => ctl.Create(Save(salesOrderId, "越权登记")))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode, () => ctl.Update(requestId, Save(salesOrderId, "越权编辑")))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode, () => ctl.Submit(requestId))).Code);
        Assert.Equal(expectedCode, (await AssertDeniedAsync(expectedCode,
            () => ctl.Cancel(requestId, new SalesOrderChangeRequestCancelRequest { Reason = "越权取消" }))).Code);
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

    /// <summary>变更申请单号流水：授权先于发号，被拒的登记 / 编辑 / 提交 / 取消决不推进该流水。</summary>
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

    // ==================== 1. 缺 / 零 / 未知 / 已删除身份：任何请求形状一致（含无上下文） ====================

    [Fact]
    public async Task 缺失或零或未知或已删除身份_八条入口任何请求形状一律未认证且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await ChangeRequestSequenceTotalAsync();

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new string?[] { null, BasePath })
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, _fixture.DeletedUserId })
            {
                var expected = identity is null or <= 0
                    ? SalesOrderChangeRequestAuthorizationRules.UnauthorizedText
                    : SalesOrderChangeRequestAuthorizationRules.UserDeletedText;
                Assert.Equal(expected, (await AssertDeniedAsync(ErrorCodes.Unauthorized,
                    () => Controller(db, identity, path).Create(Save(_fixture.OrderBId, "越权登记")))).Message);
                await AssertAllRoutesDeniedAsync(db, identity, path, _fixture.OrderBId, _fixture.OwnRequestId,
                    ErrorCodes.Unauthorized);
            }

        // 完全未绑定请求上下文：同样 fail closed（不得退化为匿名或管理员）。
        var contextless = ContextlessController(db);
        Assert.Equal(SalesOrderChangeRequestAuthorizationRules.UnauthorizedText,
            (await AssertDeniedAsync(ErrorCodes.Unauthorized,
                () => contextless.Create(Save(_fixture.OrderBId, "越权登记")))).Message);
        await AssertAllRoutesDeniedAsync(db, null, null, _fixture.OrderBId, _fixture.OwnRequestId,
            ErrorCodes.Unauthorized);

        Assert.Equal(sequenceBefore, await ChangeRequestSequenceTotalAsync());
        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 2. 禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单：任何请求形状一致 ====================

    [Fact]
    public async Task 禁用或无菜单或仅导出菜单或已撤销菜单_八条入口任何请求形状一律权限不足且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await ChangeRequestSequenceTotalAsync();

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new string?[] { null, BasePath })
        {
            Assert.Equal(SalesOrderChangeRequestAuthorizationRules.UserDisabledText,
                (await AssertDeniedAsync(ErrorCodes.Forbidden,
                    () => Controller(db, _fixture.DisabledUserId, path).Create(Save(_fixture.OrderBId, "越权登记")))).Message);
            await AssertAllRoutesDeniedAsync(db, _fixture.DisabledUserId, path, _fixture.OrderBId,
                _fixture.OwnRequestId, ErrorCodes.Forbidden);

            foreach (var id in new[] { _fixture.MenuLessUserId, _fixture.ExportOnlyUserId, _fixture.RevokedMenuUserId })
            {
                Assert.Equal(SalesOrderChangeRequestAuthorizationRules.MenuDeniedText,
                    (await AssertDeniedAsync(ErrorCodes.Forbidden,
                        () => Controller(db, id, path).Create(Save(_fixture.OrderBId, "越权登记")))).Message);
                await AssertAllRoutesDeniedAsync(db, id, path, _fixture.OrderBId, _fixture.OwnRequestId,
                    ErrorCodes.Forbidden);
            }
        }

        Assert.Equal(sequenceBefore, await ChangeRequestSequenceTotalAsync());
        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 3. 受限业务员：越界来源 / 申请非披露拒绝且零变更 ====================

    [Fact]
    public async Task 受限业务员越界来源与申请_任何请求形状同一非披露错误_零变更且不发号()
    {
        Guard();
        var sequenceBefore = await ChangeRequestSequenceTotalAsync();

        await using var db = _fixture.CreateDbContext();
        foreach (var path in new string?[] { null, BasePath })
        {
            var ctl = Controller(db, _fixture.RestrictedUserId, path);

            // 越界来源 / 越界申请 / 不存在来源：同一非披露错误。
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Create(Save(_fixture.OrderBId, "越权登记")));
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Create(Save(MissingSourceOrderId, "越权登记")));
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.GetForSource(_fixture.OrderBId));
            var foreignDenied = await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.GetById(_fixture.ForeignRequestId));
            Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, foreignDenied.Message);
            Assert.DoesNotContain(_fixture.ForeignRequestNo, foreignDenied.Message, StringComparison.Ordinal);
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Update(_fixture.ForeignRequestId, Save(_fixture.OrderAId, "越权编辑")));
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Submit(_fixture.ForeignRequestId));
            await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.Cancel(_fixture.ForeignRequestId,
                new SalesOrderChangeRequestCancelRequest { Reason = "越权取消" }));

            // 越界来源不可见：来源候选只含本人订单。
            var options = await OkDataAsync<List<SalesOrderChangeRequestSourceOptionDto>>(ctl.SourceOptions(null));
            Assert.Contains(options, o => o.SalesOrderId == _fixture.OrderAId);
            Assert.DoesNotContain(options, o => o.SalesOrderId == _fixture.OrderBId);
        }

        // 拒绝不发号、不改写他人申请。
        Assert.Equal(sequenceBefore, await ChangeRequestSequenceTotalAsync());
        var foreign = await db.SalesOrderChangeRequests.AsNoTracking().SingleAsync(r => r.Id == _fixture.ForeignRequestId);
        Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, foreign.Status);
        Assert.Null(foreign.SubmittedAt);
        Assert.Null(foreign.CancelledAt);

        // 越权调用之后本人来源仍可登记（授权未误伤），且只推进一次流水 / 每个调用形状一次。
        foreach (var path in new string?[] { null, BasePath })
        {
            var ctl = Controller(db, _fixture.RestrictedUserId, path);
            var created = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Create(Save(_fixture.OrderAId, "本人登记")));
            Assert.Equal(_fixture.OrderAId, created.SalesOrderId);
            await OkDataAsync<SalesOrderChangeRequestDto>(ctl.GetById(created.Id));
            await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Cancel(created.Id,
                new SalesOrderChangeRequestCancelRequest { Reason = "本人撤回" }));
        }

        Assert.Equal(sequenceBefore + 2, await ChangeRequestSequenceTotalAsync());
    }

    // ==================== 4. 授权身份：两种请求形状都完成既有生命周期且来源订单不变 ====================

    [Fact]
    public async Task 授权身份_空路径与已赋值路径都完成既有生命周期且来源订单不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var orderBefore = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == _fixture.OrderAId);
        var detailCountBefore = await db.SalesOrderDetails.AsNoTracking()
            .CountAsync(d => d.SalesOrderId == _fixture.OrderAId);

        foreach (var path in new string?[] { null, BasePath })
        {
            var ctl = Controller(db, _fixture.RestrictedUserId, path);

            var created = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Create(Save(_fixture.OrderAId, "客户要求改数量")));
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, created.Status);
            Assert.StartsWith("SOC", created.RequestNo, StringComparison.Ordinal);

            var updated = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Update(created.Id, Save(_fixture.OrderAId, "改数量与交期",
                new List<SalesOrderChangeRequestDetailSaveDto>
                {
                    Detail(101, "A 商品", 12m, 5m),
                    Detail(102, "B 商品", 4m, 25m)
                })));
            Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, updated.Status);

            var submitted = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Submit(created.Id));
            Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, submitted.Status);
            Assert.NotNull(submitted.SubmittedAt);

            var cancelled = await OkDataAsync<SalesOrderChangeRequestDto>(ctl.Cancel(created.Id,
                new SalesOrderChangeRequestCancelRequest { Reason = "客户撤回" }));
            Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);
            Assert.Equal("客户撤回", cancelled.CancelledReason);

            Assert.Equal(created.Id, (await OkDataAsync<SalesOrderChangeRequestDto>(ctl.GetById(created.Id))).Id);
        }

        // 来源订单与拟议边界不被改写：绝不自动套用拟议。
        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == _fixture.OrderAId);
        Assert.Equal(orderBefore.Status, orderAfter.Status);
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(orderBefore.UpdatedAt, orderAfter.UpdatedAt);
        Assert.Equal(detailCountBefore, await db.SalesOrderDetails.AsNoTracking()
            .CountAsync(d => d.SalesOrderId == _fixture.OrderAId));
    }
}

/// <summary>
/// ERP-489 入口实时授权的隔离真实 SQL Server 夹具（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且 <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量 <c>ERP_ConnectionStrings__Default</c> 或专用
/// localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>只以新播种的既有「销售订单」菜单授权与业务员客户数据范围驱动真实控制器，不新增 / 不修改任何既有权限模型。</para>
/// </summary>
public sealed class SalesOrderChangeRequestEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOCREQENTRY";

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
    public long OwnRequestId { get; private set; }
    public long ForeignRequestId { get; private set; }
    public string ForeignRequestNo { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-489] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-489] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 订单 / 申请夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 0) 变更申请单号规则（与生产 SeedData 同源口径的专用字轨；隔离库里显式播种，便于证明被拒调用不发号）。
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesOrderChangeRequest,
            RuleCode = $"ERP489-SOC-{Guid.NewGuid():N}",
            RuleName = "ERP489 变更申请单号",
            Prefix = "SOC", DateFormat = "yyyyMMdd", SerialLength = 4, CurrentSequence = 0
        });
        await db.SaveChangesAsync();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP489 特权角色", RoleCode = $"ERP489-P-{Guid.NewGuid():N}", IsSystem = true
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
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = await AddRoleAsync(db, "sales-order") });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP489 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP489 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP489 隐藏客户",
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

        Console.WriteLine("[ERP-489] 既有授权 + 订单 / 申请夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp489-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP489 隔离账号", Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP489-{Guid.NewGuid():N}", RoleCode = $"ERP489-{Guid.NewGuid():N}", IsSystem = false
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
        foreach (var grant in db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>播种本人订单 A（2 行明细，总额 150）与他人订单 B。</summary>
    private async Task SeedOrdersAsync(ErpDbContext db, long customerAId, long customerBId)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var orderA = new SalesOrder
        {
            OrderNo = $"SO-489-A-{tag}", OrderDate = DateTime.Today, CustomerId = customerAId,
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
            OrderNo = $"SO-489-B-{tag}", OrderDate = DateTime.Today, CustomerId = customerBId,
            Currency = Currency.USD, ExchangeRate = 7.2m, TotalAmount = 200m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(orderB);
        await db.SaveChangesAsync();
        OrderBId = orderB.Id;
    }

    /// <summary>
    /// 播种两张草稿申请：本人来源 OrderA（唯一对受限账号可见）与他人来源 OrderB（越权样本）。
    /// 他人申请的来源客户快照与拟议客户都写成受限账号可见的本人客户，用于证明二者都不是授权依据。
    /// </summary>
    private async Task SeedRequestsAsync(ErpDbContext db)
    {
        var own = NewRequest($"SOC-{Guid.NewGuid():N}", OrderAId, CustomerAId, CustomerAId);
        var foreign = NewRequest($"SOC-{Guid.NewGuid():N}", OrderBId, CustomerAId, CustomerAId);
        db.SalesOrderChangeRequests.AddRange(own, foreign);
        await db.SaveChangesAsync();
        OwnRequestId = own.Id;
        ForeignRequestId = foreign.Id;
        ForeignRequestNo = foreign.RequestNo;
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
public sealed class SalesOrderChangeRequestEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderChangeRequestEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
