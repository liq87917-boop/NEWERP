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
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-518 / ERP-535 单证明细行与表头金额的实际存储精度边界真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// 用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>真实 <c>INFORMATION_SCHEMA</c> 列精度与规则常量同源（明细 18,4 / 行金额 18,2 / 表头金额 18,4），
/// 边界值与 4 位（明细标量 / 表头金额）与 2 位（行金额）小数原样往返；</item>
/// <item>绕过应用层护栏的原始 EF 越界写入被真实列 / 驱动拒绝（算术溢出 8115）；SQL Server 自身的乘法溢出同样为 8115；</item>
/// <item>明细行新增 / 单证表头新增对越界值受控拒绝且**零写入**（随后同一受权控制器合法创建成功）；</item>
/// <item>来源生成对不可表示的来源明细 / 表头金额**整批拒绝**且零单证 / 零明细，来源单据不被改写；
/// 合法边界来源照常生成并保留显式来源链接。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class TradeDocumentPersistedNumericBoundsSqlServerTests
    : IClassFixture<TradeDocumentPersistedNumericBoundsSqlServerFixture>
{
    private static readonly decimal Max4 = FinancePersistedAmountRules.MaxDecimal18Scale4;
    private static readonly decimal Max2 = FinancePersistedAmountRules.MaxDecimal18Scale2;

    private readonly TradeDocumentPersistedNumericBoundsSqlServerFixture _fixture;

    public TradeDocumentPersistedNumericBoundsSqlServerTests(
        TradeDocumentPersistedNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(TradeDocumentPersistedNumericBoundsSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static TradeDocumentController NewItemController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        var controller = new TradeDocumentController(new GenericService<TradeDocument>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static SalesOrderController NewSalesOrderController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static TradeDocumentItemSaveDto Item(
        decimal quantity = 2m, decimal unitPrice = 5m)
        => new()
        {
            ProductCode = "P-INT-518", ProductNameCn = "毛巾", Unit = "箱",
            Quantity = quantity, UnitPrice = unitPrice,
        };

    private static TradeDocument NewHeader(string docNo, decimal amount, long? customerId,
        string docType = "商业发票")
        => new()
        {
            DocNo = docNo, DocType = docType, Status = "待制作", Amount = amount,
            Currency = "USD", IssueDate = DateTime.Today, Copies = 3, CustomerId = customerId,
            CustomerName = customerId is > 0 ? "集成客户（ERP-518）" : string.Empty,
        };

    // ==================== 种子（既有菜单 / 既有主数据，一律自增主键） ====================

    private static async Task<(long UserId, long EmployeeId)> SeedOperatorAsync(ErpDbContext db)
    {
        var code = $"INT_E518_EMP_{Tag()}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E518_ROLE_{code}", RoleCode = $"INT_E518_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted
            && m.MenuCode == TradeDocumentAuthorizationRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return (user.Id, employee.Id);
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, long empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"INT_E518_C_{Tag()}", CustomerName = "集成客户（ERP-518）", Status = 1,
            CreditStatus = "正常", Currency = "USD", EmpId = empId, DestinationPort = "ROTTERDAM",
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"INT_E518_P_{Tag()}", ProductName = "毛巾", Spec = "70x140", Unit = "箱",
            Status = 1,
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedDocumentAsync(ErpDbContext db, long customerId,
        string docType = "商业发票", decimal amount = 0m)
    {
        var document = new TradeDocument
        {
            DocNo = $"INT_E518_D_{Tag()}", DocType = docType, Status = "待制作", Amount = amount,
            Currency = "USD", IssueDate = DateTime.Today, CustomerId = customerId,
            CustomerName = "集成客户（ERP-518）", Copies = 3,
        };
        db.TradeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }

    private static async Task<long> SeedOrderAsync(ErpDbContext db, long customerId, long productId,
        decimal totalAmount, params (decimal Quantity, decimal UnitPrice)[] details)
    {
        var order = new SalesOrder
        {
            OrderNo = $"INT_E518_SO_{Tag()}", OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = totalAmount, Status = DocumentStatus.Approved,
            DestinationPort = "HAMBURG",
            Details = details.Select(line => new SalesOrderDetail
            {
                ProductId = productId, ProductName = "毛巾", Spec = "70x140", Unit = "箱",
                Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                Amount = line.Quantity * line.UnitPrice,
            }).ToList(),
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>），证明实际存储精度与规则常量同源。</summary>
    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<object?> ExecuteScalarAsync(ErpDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }


    // ==================== 1. 真实列精度 + 边界值原样往返 ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules_and_boundary_values_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "TradeDocumentItems", "Quantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "TradeDocumentItems", "UnitPrice"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "TradeDocumentItems", "NetWeight"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "TradeDocumentItems", "GrossWeight"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "TradeDocumentItems", "LineAmount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "TradeDocuments", "Amount"));

        Assert.Equal(18, TradeDocumentItemRules.ItemScalarPrecision);
        Assert.Equal(4, TradeDocumentItemRules.ItemScalarScale);
        Assert.Equal(2, TradeDocumentItemRules.LineAmountScale);
        Assert.Equal(18, TradeDocumentItemRules.HeaderAmountPrecision);
        Assert.Equal(4, TradeDocumentItemRules.HeaderAmountScale);
        Assert.Equal(FinancePersistedAmountRules.DecimalScale4, TradeDocumentItemRules.HeaderAmountScale);

        var (userId, employeeId) = await SeedOperatorAsync(db);
        var customerId = await SeedCustomerAsync(db, employeeId);
        var documentId = await SeedDocumentAsync(db, customerId);
        var controller = NewItemController(db, userId);

        // 数量 = 18,4 上界、单价 100 → 行金额恰好 = 18,2 上界：原样落库（无截断）。
        var ok = Assert.IsType<OkObjectResult>(await controller.CreateItem(documentId, Item(Max4, 100m)));
        var dto = Assert.IsType<ApiResponse<TradeDocumentItemDto>>(ok.Value).Data!;
        Assert.Equal(Max4, dto.Quantity);
        Assert.Equal(100m, dto.UnitPrice);
        Assert.Equal(Max2, dto.LineAmount);

        var stored = await db.TradeDocumentItems.AsNoTracking().SingleAsync(i => i.Id == dto.Id);
        Assert.Equal(Max4, stored.Quantity);
        Assert.Equal(Max2, stored.LineAmount);

        // 单证表头金额（宣告列 DECIMAL(18,4)，ERP-535）：精确 4 位小数上限与 3 / 4 位小数经真实列原样读写。
        var boundaryHeader = Assert.IsType<OkObjectResult>(
            await controller.Create(NewHeader($"INT_E535_H_{Tag()}", Max4, customerId)));
        var createdBoundary = Assert.IsType<ApiResponse<TradeDocument>>(boundaryHeader.Value).Data!;
        Assert.Equal(Max4, createdBoundary.Amount);

        var decimalHeader = Assert.IsType<OkObjectResult>(
            await controller.Create(NewHeader($"INT_E535_H_{Tag()}", 1234.5678m, customerId)));
        var createdDecimal = Assert.IsType<ApiResponse<TradeDocument>>(decimalHeader.Value).Data!;
        Assert.Equal(1234.5678m, createdDecimal.Amount);

        db.ChangeTracker.Clear();
        Assert.Equal(Max4, (await db.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == createdBoundary.Id)).Amount);
        Assert.Equal(1234.5678m, (await db.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == createdDecimal.Id)).Amount);
    }

    // ==================== 2. 原始 EF / SQL Server 自身的越界拒绝（算术溢出 8115） ====================

    [Fact]
    public async Task Raw_ef_overflow_and_sql_arithmetic_overflow_are_rejected()
    {
        Guard();
        long documentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employeeId) = await SeedOperatorAsync(seed);
            var customerId = await SeedCustomerAsync(seed, employeeId);
            documentId = await SeedDocumentAsync(seed, customerId);
        }

        // 绕过应用层护栏的原始 EF 写入：真实列（DECIMAL(18,4)）拒绝越界值（算术溢出 8115）。
        await using (var raw = _fixture.CreateDbContext())
        {
            raw.TradeDocumentItems.Add(new TradeDocumentItem
            {
                TradeDocumentId = documentId, LineNo = 900_000, ProductCode = "RAW",
                ProductNameCn = "原始越界", Quantity = 100_000_000_000_000m, Unit = "箱",
                UnitPrice = 1m, LineAmount = 1m, Currency = "USD",
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
            var baseException = ex.GetBaseException();
            // 绕过应用层护栏的越界值必须被真实列 / 驱动 / 服务端拒绝（真实环境为算术溢出 8115 或驱动层参数越界）。
            Assert.True(baseException is SqlException || baseException is ArgumentException,
                $"原始 EF 越界写入应被拒绝，但底层异常为：{baseException.GetType().FullName}: {baseException.Message}");
            if (baseException is SqlException sqlException) Assert.Equal(8115, sqlException.Number);

            raw.ChangeTracker.Clear();
            Assert.Equal(0, await raw.TradeDocumentItems.AsNoTracking()
                .CountAsync(i => i.TradeDocumentId == documentId && i.LineNo == 900_000));
        }

        await using var db = _fixture.CreateDbContext();

        // SQL Server 自身：合法 (18,4) 乘积再落入 (18,2) 列溢出（与规则判定同向）。
        var overflow = await Assert.ThrowsAsync<SqlException>(() => ExecuteScalarAsync(db,
            "SELECT CAST(CAST(99999999999999.9999 AS decimal(18,4)) * CAST(101 AS decimal(18,4)) AS decimal(18,2))"));
        Assert.Equal(8115, overflow.Number);

        // 边界乘积（恰好等于 18,2 上界）在 SQL Server 上照常可表示。
        var boundary = await ExecuteScalarAsync(db,
            "SELECT CAST(CAST(99999999999999.9999 AS decimal(18,4)) * CAST(100 AS decimal(18,4)) AS decimal(18,2))");
        Assert.Equal(Max2, Assert.IsType<decimal>(boundary));
    }


    // ==================== 3. 明细行 / 表头金额越界：零写入，随后合法创建成功 ====================

    [Fact]
    public async Task Item_and_header_bounds_reject_with_zero_write_then_legal_succeeds()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedOperatorAsync(db);
        var customerId = await SeedCustomerAsync(db, employeeId);
        var documentId = await SeedDocumentAsync(db, customerId);
        var controller = NewItemController(db, userId);

        foreach (var bad in new[]
        {
            Item(Max4 + 1m, 1m),        // 数量越界（18,4）
            Item(1m, Max4 + 1m),        // 单价越界（18,4）
            Item(Max4, 101m),           // 行金额越界（18,2）
            Item(decimal.MaxValue, 2m), // CLR 乘法溢出
        })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => controller.CreateItem(documentId, bad));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => i.TradeDocumentId == documentId && !i.IsDeleted));

        var ok = Assert.IsType<OkObjectResult>(await controller.CreateItem(documentId, Item(Max4, 100m)));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocumentItemDto>>(ok.Value).Code);

        // 单证表头金额按宣告列 DECIMAL(18,4) 判定（ERP-535）：量级越界 / 第 5 位小数 /
        // 「半进位取整后恰好落到上限」一律受控拒绝且零单证写入。
        foreach (var (badAmount, docNo) in new[]
        {
            (Max2 + 1m, $"INT_E535_H_{Tag()}"),        // 超过宣告列 DECIMAL(18,4) 上界
            (Max4 + 1m, $"INT_E535_H_{Tag()}"),        // 精确宣告列上界的下一整数
            (1.00001m, $"INT_E535_H_{Tag()}"),         // 第 5 位小数
            (Max4 - 0.00005m, $"INT_E535_H_{Tag()}"),  // 半进位取整后恰好 = 上限（仍拒绝）
        })
        {
            var headerEx = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Create(NewHeader(docNo, badAmount, customerId)));
            Assert.Equal(ErrorCodes.InvalidParameter, headerEx.Code);
            db.ChangeTracker.Clear();
            Assert.Equal(0, await db.TradeDocuments.AsNoTracking()
                .CountAsync(d => d.DocNo == docNo && !d.IsDeleted));
        }

        // 合法边界金额（宣告列精确 4 位小数上限）放行。
        var headerOk = Assert.IsType<OkObjectResult>(
            await controller.Create(NewHeader($"INT_E535_H_{Tag()}", Max4, customerId)));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocument>>(headerOk.Value).Code);
    }


    // ==================== 4. 来源生成：整批拒绝与合法边界落库 ====================

    [Fact]
    public async Task Generation_rejects_unrepresentable_source_with_zero_documents()
    {
        Guard();
        long userId, orderId;
        string orderNo;
        int documentsBefore, itemsBefore;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employeeId) = await SeedOperatorAsync(seed);
            userId = user;
            var customerId = await SeedCustomerAsync(seed, employeeId);
            var productId = await SeedProductAsync(seed);
            orderId = await SeedOrderAsync(seed, customerId, productId, totalAmount: 200m,
                details: new[] { (1m, 1m), (Max4 + 1m, 10m) });
            orderNo = (await seed.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).OrderNo;
            documentsBefore = await seed.TradeDocuments.AsNoTracking().CountAsync(d => !d.IsDeleted);
            itemsBefore = await seed.TradeDocumentItems.AsNoTracking().CountAsync(i => !i.IsDeleted);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => NewSalesOrderController(db, userId).GenerateTradeDocuments(orderId, null));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        }

        // 整批原子拒绝：单证 / 明细总数不变，且该来源零单证；来源单据与明细未被改写。
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(documentsBefore, await verify.TradeDocuments.AsNoTracking()
            .CountAsync(d => !d.IsDeleted));
        Assert.Equal(itemsBefore, await verify.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => !i.IsDeleted));
        Assert.Equal(0, await verify.TradeDocuments.AsNoTracking()
            .CountAsync(d => !d.IsDeleted && d.SalesOrderNo == orderNo));
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(200m, order.TotalAmount);
        Assert.Equal(2, await verify.SalesOrderDetails.AsNoTracking()
            .CountAsync(d => d.SalesOrderId == orderId));
    }

    [Fact]
    public async Task Generation_persists_legal_boundary_with_source_link()
    {
        Guard();
        long userId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employeeId) = await SeedOperatorAsync(seed);
            userId = user;
            var customerId = await SeedCustomerAsync(seed, employeeId);
            var productId = await SeedProductAsync(seed);
            orderId = await SeedOrderAsync(seed, customerId, productId, totalAmount: 7.5m,
                details: new[] { (3m, 2.5m) });
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ok = Assert.IsType<OkObjectResult>(
                await NewSalesOrderController(db, userId).GenerateTradeDocuments(orderId, null));
            var result = Assert.IsType<ApiResponse<TradeDocGenerateResult>>(ok.Value).Data!;
            Assert.Equal(2, result.TotalLineCount);
        }

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);

        var ci = await verify.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.DocType == "商业发票" && d.SalesOrderNo == order.OrderNo);
        Assert.Equal(7.5m, ci.Amount);
        var ciLine = await verify.TradeDocumentItems.AsNoTracking()
            .SingleAsync(i => i.TradeDocumentId == ci.Id);
        Assert.Equal(3m, ciLine.Quantity);
        Assert.Equal(7.5m, ciLine.LineAmount);

        var pl = await verify.TradeDocuments.AsNoTracking()
            .SingleAsync(d => d.DocType == "装箱单" && d.SalesOrderNo == order.OrderNo);
        var plLine = await verify.TradeDocumentItems.AsNoTracking()
            .SingleAsync(i => i.TradeDocumentId == pl.Id);
        Assert.Equal(0m, plLine.UnitPrice);
        Assert.Equal(0m, plLine.LineAmount);
        Assert.Null(plLine.GrossWeight);
    }

}

/// <summary>
/// ERP-518 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class TradeDocumentPersistedNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_TRADEDOCNUMERIC_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-518] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-518] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>目标库护栏 fail-closed 覆盖：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class TradeDocumentPersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => TradeDocumentPersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}

