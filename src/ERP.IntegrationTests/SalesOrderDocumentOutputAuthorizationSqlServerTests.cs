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
/// ERP-424 规范销售订单（<c>api/sales-orders</c>）**文档输出入口**（打印 / JSON 单据导出 / Excel 导出）
/// 实时授权与权威客户范围的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」功能菜单 + 既有「销售订单导出」
/// 导出菜单（与既有受控导出族目录逐字一致）与业务员客户数据范围驱动真实 <see cref="SalesOrderController"/>；
/// 不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>逐入口证明</b>：受限业务员下本人订单打印保留未删除明细；JSON 导出只返回范围内父订单；
/// Excel 导出**解码真实 xlsx 工作簿**核对越界订单号 / 金额绝不出现。</item>
/// <item><b>非披露错误</b>：他人 / 已删除 / 不存在订单打印返回同一受控「销售订单不存在」。</item>
/// <item><b>身份拒绝矩阵</b>：无身份 / 已删除账号按未认证拒绝，已禁用 / 无菜单 / 仅导出菜单 / 仅功能菜单 /
/// 已撤销菜单按权限不足拒绝（三个入口逐一断言）。</item>
/// <item><b>零写入证据</b>：每次拒绝 / 读取前后对 <c>SalesOrders</c> / <c>SalesOrderDetails</c> /
/// <c>StockOuts</c> / <c>StockMovements</c>（保留库存来源单据审计） / <c>FinanceReceipts</c> /
/// <c>SysOperationLogs</c> 做只读快照，全部不变。</item>
/// <item><b>两个独立连接竞态</b>：① 两条连接并发打印同一本人订单 → 结果一致；② 一条合法打印与一条他人越权并发 →
/// 合法打印成功、越权 fail closed，收尾快照证明零写入。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才构成验收证据。</para>
/// </summary>
public sealed class SalesOrderDocumentOutputAuthorizationSqlServerTests
    : IClassFixture<SalesOrderDocumentOutputAuthorizationSqlServerFixture>
{
    private readonly SalesOrderDocumentOutputAuthorizationSqlServerFixture _fixture;

    public SalesOrderDocumentOutputAuthorizationSqlServerTests(
        SalesOrderDocumentOutputAuthorizationSqlServerFixture fixture) => _fixture = fixture;

    /// <summary>必然不存在的订单 Id。</summary>
    private const long MissingOrderId = 9_424_000L;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements", "FinanceReceipts", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderDocumentOutputAuthorizationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderController NewController(ErpDbContext db, long? userId,
        string path = "/api/sales-orders")
    {
        var http = new DefaultHttpContext();
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
        Assert.Equal(SalesOrderDocumentOutputAuthorizationRules.NotFoundText, ex.Message);
    }

    private static async Task AssertDeniedAsync(int code, string text, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
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

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>读取 xlsx 首个工作表指定中文列头的所有取值（导出内容断言用）。</summary>
    private static List<string> ReadExcelColumn(byte[] bytes, string title)
    {
        using var stream = new MemoryStream(bytes);
        using var workbook = new NPOI.XSSF.UserModel.XSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);
        var header = sheet.GetRow(0);
        var index = -1;
        for (var c = 0; c < header.LastCellNum; c++)
        {
            if (string.Equals(header.GetCell(c)?.ToString(), title, StringComparison.Ordinal)) { index = c; break; }
        }
        Assert.True(index >= 0, $"导出缺少列「{title}」");

        var values = new List<string>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
        {
            var value = sheet.GetRow(r)?.GetCell(index)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values;
    }
    // ==================== 1. 受限业务员：本人订单可读，JSON / Excel 仅含范围内订单 ====================

    [Fact]
    public async Task 受限业务员_本人订单_打印保留明细且JSON与Excel仅含本人()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        var print = await OkDataAsync<SalesOrder>(ctl.GetPrint(_fixture.OrderAId));
        Assert.Equal(_fixture.OrderAId, print.Id);
        Assert.Equal(_fixture.OwnCustomerId, print.CustomerId);
        Assert.Equal(_fixture.OwnTotal, print.TotalAmount);
        Assert.Equal("PCS", Assert.Single(print.Details).Unit);
        Assert.DoesNotContain(print.Details, d => d.IsDeleted);

        var exported = await OkDataAsync<List<SalesOrder>>(ctl.Export(null, null));
        var only = Assert.Single(exported);
        Assert.Equal(_fixture.OrderAId, only.Id);
        Assert.Equal(_fixture.OwnCustomerId, only.CustomerId);

        var file = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        var orderNos = ReadExcelColumn(file.FileContents, "订单号");
        Assert.Contains(_fixture.OrderANo, orderNos);
        Assert.DoesNotContain(_fixture.OrderBNo, orderNos);
        Assert.DoesNotContain(_fixture.UnlinkedOrderNo, orderNos);

        var amounts = ReadExcelColumn(file.FileContents, "订单总额");
        Assert.Single(amounts);
        Assert.Equal(_fixture.OwnTotal.ToString("0.00"), amounts[0]);
        Assert.DoesNotContain(_fixture.ForeignTotal.ToString("0.00"), amounts);
        Assert.DoesNotContain(_fixture.UnlinkedTotal.ToString("0.00"), amounts);

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 2. 他人 / 已删除 / 不存在订单：同一非披露错误且导出不含 ====================

    [Fact]
    public async Task 受限业务员_他人已删除不存在订单_打印同一非披露错误且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.RestrictedUserId);

        Assert.NotNull(await OkDataAsync<SalesOrder>(ctl.GetPrint(_fixture.OrderAId)));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetPrint(_fixture.OrderBId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetPrint(_fixture.DeletedOrderAId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetPrint(MissingOrderId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetPrint(0));

        var exported = await OkDataAsync<List<SalesOrder>>(ctl.Export(null, null));
        Assert.Equal(new[] { _fixture.OrderAId }, exported.Select(o => o.Id).ToArray());

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 3. 身份 / 菜单拒绝矩阵：三个输出入口 fail closed 且零写入 ====================

    [Fact]
    public async Task 身份与菜单拒绝矩阵_三个输出入口fail_closed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText),
            (_fixture.DeletedUserId, ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UserDeletedText),
            (_fixture.DisabledUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.UserDisabledText),
            (_fixture.NoMenuUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText),
            (_fixture.ExportOnlyUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText),
            (_fixture.FunctionalOnlyUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.ExportMenuDeniedText),
            (_fixture.RevokedMenuUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = NewController(db, userId);
            await AssertDeniedAsync(code, text, () => ctl.GetPrint(_fixture.OrderAId));
            await AssertDeniedAsync(code, text, () => ctl.Export(null, null));
            await AssertDeniedAsync(code, text, () => ctl.ExportExcel(null, null, null, null));
        }

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 4. 特权账号：保留既有全量口径 ====================

    [Fact]
    public async Task 特权账号_保留既有全量口径_可读他人订单且导出含之()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        Assert.Equal(_fixture.OrderBId, (await OkDataAsync<SalesOrder>(ctl.GetPrint(_fixture.OrderBId))).Id);
        var exported = await OkDataAsync<List<SalesOrder>>(ctl.Export(null, null));
        Assert.Contains(exported, o => o.Id == _fixture.OrderBId);

        var file = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));
        Assert.Contains(_fixture.OrderBNo, ReadExcelColumn(file.FileContents, "订单号"));
    }

    // ==================== 5. 两个独立连接竞态 ====================

    [Fact]
    public async Task 两个独立连接竞态_同一本人打印结果一致()
    {
        Guard();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> PrintAsync(ErpDbContext db)
        {
            await gate.WaitAsync();
            return Json(await OkDataAsync<SalesOrder>(
                NewController(db, _fixture.RestrictedUserId).GetPrint(_fixture.OrderAId)));
        }

        var first = PrintAsync(dbA);
        var second = PrintAsync(dbB);
        gate.Release(2);
        var results = new[] { await first, await second };

        Assert.Equal(results[0], results[1]);
        Assert.Contains(_fixture.OrderANo, results[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 两个独立连接竞态_合法打印与他人越权并发_拒绝侧fail_closed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        using var gate = new SemaphoreSlim(0, 2);

        async Task<string> AllowedAsync()
        {
            await gate.WaitAsync();
            return Json(await OkDataAsync<SalesOrder>(
                NewController(dbA, _fixture.RestrictedUserId).GetPrint(_fixture.OrderAId)));
        }

        async Task<string> DeniedAsync()
        {
            await gate.WaitAsync();
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(dbB, _fixture.RestrictedUserId).GetPrint(_fixture.OrderBId));
            Assert.Equal(ErrorCodes.NotFound, ex.Code);
            return ex.Message;
        }

        var allowed = AllowedAsync();
        var denied = DeniedAsync();
        gate.Release(2);

        var allowedJson = await allowed;
        Assert.Equal(SalesOrderDocumentOutputAuthorizationRules.NotFoundText, await denied);
        Assert.Contains(_fixture.OrderANo, allowedJson, StringComparison.Ordinal);
        AssertUnchanged(before, await SnapshotAsync());
    }

}

/// <summary>
/// ERP-424 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <para>只以新播种的既有「销售订单」+「销售订单导出」菜单授权与业务员客户数据范围驱动真实控制器，
/// 不新增 / 不修改任何既有权限模型。</para>
/// </summary>
public sealed class SalesOrderDocumentOutputAuthorizationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SODOCOUTPUT";
    private const long ProductId = 9_424_010L;

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long NoMenuUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
    public long FunctionalOnlyUserId { get; private set; }
    public long RevokedMenuUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }

    public long OwnCustomerId { get; private set; }
    public long OrderAId { get; private set; }
    public string OrderANo { get; private set; } = string.Empty;
    public decimal OwnTotal { get; private set; }

    public long OrderBId { get; private set; }
    public string OrderBNo { get; private set; } = string.Empty;
    public decimal ForeignTotal { get; private set; }

    public long DeletedOrderAId { get; private set; }

    public long UnlinkedOrderId { get; private set; }
    public string UnlinkedOrderNo { get; private set; } = string.Empty;
    public decimal UnlinkedTotal { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-424] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-424] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 订单夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP424 特权角色", RoleCode = $"ERP424-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();

        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 + 客户数据范围。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = restricted.Id,
            RoleId = await AddRoleAsync(db,
                SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode,
                SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode)
        });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP424 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP424 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP424 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        OwnCustomerId = customerA.Id;


        // 本人订单（含一行已删除明细，用于证明打印只保留未删除明细）。
        var orderA = new SalesOrder
        {
            OrderNo = $"SO-424-A-{tag}", OrderDate = DateTime.Today, CustomerId = customerA.Id,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 1234.56m,
            DepositRatio = 30m, DepositAmount = 370.37m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(orderA);
        await db.SaveChangesAsync();
        OrderAId = orderA.Id;
        OrderANo = orderA.OrderNo;
        OwnTotal = orderA.TotalAmount;

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = orderA.Id, ProductId = ProductId, ProductName = $"商品{ProductId}",
            Unit = "PCS", Quantity = 10m, UnitPrice = 123.456m, Amount = 1234.56m
        });
        var hiddenDetail = new SalesOrderDetail
        {
            SalesOrderId = orderA.Id, ProductId = ProductId + 1, ProductName = "ERP424 已删除明细",
            Unit = "PCS", Quantity = 1m, UnitPrice = 1m, Amount = 1m
        };
        db.SalesOrderDetails.Add(hiddenDetail);
        await db.SaveChangesAsync();
        hiddenDetail.IsDeleted = true;
        await db.SaveChangesAsync();

        // 他人订单（归属他人客户，受限账号范围外）。
        var orderB = new SalesOrder
        {
            OrderNo = $"SO-424-B-{tag}", OrderDate = DateTime.Today, CustomerId = customerB.Id,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 98765.43m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(orderB);
        await db.SaveChangesAsync();
        OrderBId = orderB.Id;
        OrderBNo = orderB.OrderNo;
        ForeignTotal = orderB.TotalAmount;

        // 已删除订单（本人客户，但记录已软删除）与无主订单（CustomerId 缺省）。
        var deleted = new SalesOrder
        {
            OrderNo = $"SO-424-D-{tag}", OrderDate = DateTime.Today, CustomerId = customerA.Id,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 300m,
            Status = DocumentStatus.Approved, IsDeleted = true
        };
        var unlinked = new SalesOrder
        {
            OrderNo = $"SO-424-N-{tag}", OrderDate = DateTime.Today, CustomerId = 0L,
            Currency = Currency.USD, ExchangeRate = 7.1m, TotalAmount = 55555.55m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.AddRange(deleted, unlinked);
        await db.SaveChangesAsync();
        DeletedOrderAId = deleted.Id;
        UnlinkedOrderId = unlinked.Id;
        UnlinkedOrderNo = unlinked.OrderNo;
        UnlinkedTotal = unlinked.TotalAmount;

        // 3) 拒绝侧账号：无菜单 / 仅导出菜单 / 仅功能菜单 / 已撤销菜单 / 已禁用 / 已删除。
        NoMenuUserId = await SeedDeniedUserAsync(db, Array.Empty<string>(), UserStatus.Enabled);
        ExportOnlyUserId = await SeedDeniedUserAsync(db,
            new[] { SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode }, UserStatus.Enabled);
        FunctionalOnlyUserId = await SeedDeniedUserAsync(db,
            new[] { SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode }, UserStatus.Enabled);
        RevokedMenuUserId = await SeedRevokedMenuUserAsync(db);
        DisabledUserId = await SeedDeniedUserAsync(db,
            new[] { SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode }, UserStatus.Disabled);
        DeletedUserId = await SeedDeniedUserAsync(db,
            new[] { SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode }, UserStatus.Enabled, deleted: true);

        Console.WriteLine("[ERP-424] 既有授权 + 订单夹具就绪（受控只读，不新增权限模型）。");
    }


    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp424-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        DisplayName = "ERP424 隔离账号",
        Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP424-{Guid.NewGuid():N}", RoleCode = $"ERP424-{Guid.NewGuid():N}", IsSystem = false
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

    /// <summary>播种拒绝侧账号（无菜单 / 仅导出菜单 / 仅功能菜单 / 已禁用 / 已删除），不新增任何业务客户数据。</summary>
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

    /// <summary>授予后又撤销两个既有菜单（角色仍在，授权已回收 → 下一次请求立即收敛）。</summary>
    private static async Task<long> SeedRevokedMenuUserAsync(ErpDbContext db)
    {
        var user = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        var roleId = await AddRoleAsync(db,
            SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode,
            SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode);
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = roleId });
        await db.SaveChangesAsync();
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == roleId));
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderDocumentOutputAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderDocumentOutputAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

