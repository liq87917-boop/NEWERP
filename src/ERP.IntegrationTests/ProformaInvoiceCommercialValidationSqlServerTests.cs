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
/// ERP-471 形式发票 PI 商业条款（币种 / 汇率 / 数量 / 单价 / 金额 / 合计 / 定金）校验的
/// **真实 SQL Server** 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// <list type="number">
/// <item><b>真实控制器 + 既有授权</b>：以既有系统内置角色（等同既有无保留访问口径）驱动真实
/// <see cref="ProformaInvoiceController"/> 与 <see cref="QuotationController"/>，不新增任何菜单 / 角色 / 用户授权。</item>
/// <item><b>非法条款零写入零占号</b>：非法数量 / 未定义币种 / 非正汇率 / 负定金 / 软删除明细注入在新增 / 修改时被
/// 受控业务错误拒绝，PI / 明细 / 销售订单 / 单号流水全部零变化。</item>
/// <item><b>服务端权威</b>：客户端伪造的行金额 / 合计 / 定金在落库时被服务端按币种精度重算覆盖，
/// <c>Σ 有效明细金额 == 主表总额</c> 在真实 <c>DECIMAL(18,2)</c> 落库后仍精确成立；JPY 按 0 位小数落库。</item>
/// <item><b>承诺前复核</b>：直接改库模拟非法历史条款后，提交 / 审核 / 转销售订单原子拒绝且状态与下游单据不变；
/// 报价单转 PI 不传播非法来源定金比例（来源状态与单号预约不变）。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。
/// 本测试只**新增**自己的夹具行，不清理、不删除、不改写任何既有行。</para>
/// </summary>
public sealed class ProformaInvoiceCommercialValidationSqlServerTests
    : IClassFixture<ProformaInvoiceCommercialValidationSqlServerFixture>
{
    private readonly ProformaInvoiceCommercialValidationSqlServerFixture _fixture;

    public ProformaInvoiceCommercialValidationSqlServerTests(
        ProformaInvoiceCommercialValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => ProformaInvoiceCommercialValidationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static ProformaInvoiceController PiControllerFor(ErpDbContext db, long userId)
        => Controller(new ProformaInvoiceController(db, new DocumentNumberService(db)), userId);

    private static QuotationController QuotationControllerFor(ErpDbContext db, long userId)
        => Controller(new QuotationController(db, new DocumentNumberService(db)), userId);

    private static T Controller<T>(T controller, long userId) where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed record WriteSnapshot(
        int ProformaInvoices, int ProformaInvoiceDetails, int SalesOrders, string Numbering);

    private static async Task<WriteSnapshot> SnapshotAsync(ErpDbContext db) => new(
        await db.ProformaInvoices.CountAsync(),
        await db.ProformaInvoiceDetails.CountAsync(),
        await db.SalesOrders.CountAsync(),
        string.Join(",", await db.SysDocumentNumberRules.AsNoTracking()
            .OrderBy(r => r.Id).Select(r => r.CurrentSequence).ToListAsync()));

    private static void AssertUnchanged(WriteSnapshot before, WriteSnapshot after)
    {
        Assert.Equal(before.ProformaInvoices, after.ProformaInvoices);
        Assert.Equal(before.ProformaInvoiceDetails, after.ProformaInvoiceDetails);
        Assert.Equal(before.SalesOrders, after.SalesOrders);
        Assert.Equal(before.Numbering, after.Numbering);
    }

    /// <summary>新增 / 修改请求体（金额一律故意伪造，用于验证服务端不复采信）。</summary>
    private ProformaInvoice Body(params ProformaInvoiceDetail[] details) => new()
    {
        PiDate = DateTime.Today,
        CustomerId = _fixture.CustomerId,
        CustomerName = "ERP471 客户",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        DepositRatio = 30m,
        TotalAmount = 999_999m,
        TotalAmountCny = 1m,
        Details = details.ToList()
    };

    private static ProformaInvoiceDetail Line(decimal quantity, decimal unitPrice, bool deleted = false) => new()
    {
        ProductId = 1, ProductCode = "P-471", ProductName = "ERP471 商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99, IsDeleted = deleted
    };

    // ==================== 1. 新增 / 修改：非法条款零写入零占号 ====================

    [Fact]
    public async Task 新增_非法条款_拒绝且零写入且不消耗单号()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);
        var controller = PiControllerFor(db, _fixture.PrivilegedUserId);

        var badCurrency = Body(Line(1m, 10m));
        badCurrency.Currency = (Currency)999;
        Assert.Equal(ProformaInvoiceCommercialValidationRules.CurrencyText,
            (await Assert.ThrowsAsync<BusinessException>(() => controller.Create(badCurrency))).Message);

        var badRate = Body(Line(1m, 10m));
        badRate.ExchangeRate = 0m;   // 旧实现会静默按 1 处理
        Assert.Equal(ProformaInvoiceCommercialValidationRules.ExchangeRateText,
            (await Assert.ThrowsAsync<BusinessException>(() => controller.Create(badRate))).Message);

        var badQuantity = Body(Line(0m, 10m));
        Assert.Contains("数量必须大于 0",
            (await Assert.ThrowsAsync<BusinessException>(() => controller.Create(badQuantity))).Message);

        var negativeDeposit = Body(Line(10m, 100m));
        negativeDeposit.DepositAmount = -1m;
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositNegativeText,
            (await Assert.ThrowsAsync<BusinessException>(() => controller.Create(negativeDeposit))).Message);

        var injected = Body(Line(1m, 10m), Line(1m, 10m, deleted: true));
        Assert.Equal(ProformaInvoiceCommercialValidationRules.SoftDeletedDetailText,
            (await Assert.ThrowsAsync<BusinessException>(() => controller.Create(injected))).Message);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 新增_客户端伪造合计与行金额_服务端按币种精度派生并精确落库()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var body = Body(Line(2m, 10m), Line(3m, 4m));

        Assert.IsType<OkObjectResult>(await PiControllerFor(db, _fixture.PrivilegedUserId).Create(body));

        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.ProformaInvoices.AsNoTracking()
            .Where(p => p.CustomerId == _fixture.CustomerId)
            .OrderByDescending(p => p.Id).FirstAsync();
        var lines = await verify.ProformaInvoiceDetails.AsNoTracking()
            .Where(d => d.PiId == saved.Id).OrderBy(d => d.SortNo).ToListAsync();

        Assert.Equal(new[] { 20m, 12m }, lines.Select(d => d.Amount));
        Assert.Equal(32m, saved.TotalAmount);
        Assert.Equal(230.4m, saved.TotalAmountCny);
        Assert.Equal(9.6m, saved.DepositAmount);                    // 32 × 30%
        Assert.Equal(saved.TotalAmount, lines.Sum(d => d.Amount));  // 落库后仍精确一致
        Assert.StartsWith("PI", saved.PiNo);
    }

    [Fact]
    public async Task 新增_JPY_行金额合计与自动定金按零位小数落库()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var body = Body(Line(3m, 10.5m), Line(1m, 100.4m));
        body.Currency = Currency.JPY;
        body.ExchangeRate = 0.05m;

        Assert.IsType<OkObjectResult>(await PiControllerFor(db, _fixture.PrivilegedUserId).Create(body));

        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.ProformaInvoices.AsNoTracking()
            .Where(p => p.CustomerId == _fixture.CustomerId && p.Currency == Currency.JPY)
            .OrderByDescending(p => p.Id).FirstAsync();
        var lines = await verify.ProformaInvoiceDetails.AsNoTracking()
            .Where(d => d.PiId == saved.Id).OrderBy(d => d.SortNo).ToListAsync();

        Assert.Equal(new[] { 32m, 100m }, lines.Select(d => d.Amount));   // 31.5 → 32；100.4 → 100
        Assert.Equal(132m, saved.TotalAmount);
        Assert.Equal(6.60m, saved.TotalAmountCny);
        Assert.Equal(40m, saved.DepositAmount);                          // 132 × 30% = 39.6 → 0 位 0.5 进位
        Assert.Equal(saved.TotalAmount, lines.Sum(d => d.Amount));
    }

    [Fact]
    public async Task 新增_显式正定金保留_显式零定金按比例自动计算()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = PiControllerFor(db, _fixture.PrivilegedUserId);

        var explicitBody = Body(Line(10m, 100m));
        explicitBody.DepositAmount = 250m;   // 显式正定金（<= 总额 1000）保留
        Assert.IsType<OkObjectResult>(await ctl.Create(explicitBody));

        var autoBody = Body(Line(10m, 100m));   // 显式 0 = 自动 300
        Assert.IsType<OkObjectResult>(await ctl.Create(autoBody));

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.ProformaInvoices.AsNoTracking()
            .Where(p => p.CustomerId == _fixture.CustomerId)
            .OrderByDescending(p => p.Id).Take(2).ToListAsync();

        Assert.Equal(250m, rows[1].DepositAmount);
        Assert.Equal(300m, rows[0].DepositAmount);
        Assert.All(rows, r => Assert.Equal(1000m, r.TotalAmount));
    }

    // ==================== 2. 修改 / 提交 / 审核 / 转换：承诺前复核 ====================

    [Fact]
    public async Task 修改_非法明细_原表头明细与合计及单号不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var pi = await _fixture.SeedPiAsync(db, DocumentStatus.Pending, 7.2m, Line(10m, 100m));
        var before = await SnapshotAsync(db);
        var beforeLines = string.Join(";", await db.ProformaInvoiceDetails.AsNoTracking()
            .Where(d => d.PiId == pi.Id).OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{d.SortNo}|{d.Quantity}|{d.Amount}").ToListAsync());

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PiControllerFor(db, _fixture.PrivilegedUserId).Update(pi.Id, Body(Line(-3m, 5m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        var after = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == pi.Id);
        Assert.Equal(pi.PiNo, after.PiNo);
        Assert.Equal(1000m, after.TotalAmount);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(beforeLines, string.Join(";", await verify.ProformaInvoiceDetails.AsNoTracking()
            .Where(d => d.PiId == pi.Id).OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{d.SortNo}|{d.Quantity}|{d.Amount}").ToListAsync()));
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 提交_已持久化汇率非法_原子拒绝且状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var pi = await _fixture.SeedPiAsync(db, DocumentStatus.Pending, 0m, Line(10m, 100m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PiControllerFor(db, _fixture.PrivilegedUserId).Submit(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending,
            (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == pi.Id)).Status);
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var pi = await _fixture.SeedPiAsync(db, DocumentStatus.Submitted, 7.2m, Line(1m, 10m));
        var stored = await db.ProformaInvoices.SingleAsync(p => p.Id == pi.Id);
        stored.Currency = (Currency)777;   // 直接改库模拟非法历史条款（绕过写入契约）
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PiControllerFor(db, _fixture.PrivilegedUserId).Approve(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.CurrencyText, ex.Message);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == pi.Id)).Status);
    }

    [Fact]
    public async Task 转销售订单_已持久化数量非法_原子拒绝且零订单零占号()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var pi = await _fixture.SeedPiAsync(db, DocumentStatus.Approved, 7.2m, Line(0m, 100m));
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PiControllerFor(db, _fixture.PrivilegedUserId).ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == pi.Id)).Status);
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 合法PI_提交审核转销售订单_订单与来源一致且PI置已完成()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var pi = await _fixture.SeedPiAsync(db, DocumentStatus.Pending, 7.2m, Line(10m, 100m));
        var ctl = PiControllerFor(db, _fixture.PrivilegedUserId);

        Assert.IsType<OkObjectResult>(await ctl.Submit(pi.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(pi.Id));
        Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(pi.Id));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Completed,
            (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == pi.Id)).Status);
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.SourcePiId == pi.Id);
        Assert.Equal(Currency.USD, order.Currency);
        Assert.Equal(7.2m, order.ExchangeRate);
        Assert.Equal(1000m, order.TotalAmount);
        Assert.Equal(300m, order.DepositAmount);
        Assert.Equal(pi.PiNo, order.SourcePiNo);
    }

    // ==================== 3. 报价转 PI：非法来源商业值不传播 ====================

    [Fact]
    public async Task 转PI_来源客户定金比例越界_拒绝且零PI且来源状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var badCustomer = await _fixture.SeedCustomerAsync(db, 150m);
        var quotation = await _fixture.SeedQuotationAsync(db, badCustomer.Id, DocumentStatus.Approved,
            new QuotationDetail
            {
                SortNo = 1, ProductId = 1, ProductName = "ERP471 报价商品", Unit = "PCS",
                Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositRatioText, ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id)).Status);
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 转PI_JPY报价单_PI按零位小数派生金额与自动定金()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await _fixture.SeedCustomerAsync(db, 30m);
        var quotation = await _fixture.SeedQuotationAsync(db, customer.Id, DocumentStatus.Approved,
            Currency.JPY, 0.05m,
            new QuotationDetail
            {
                SortNo = 1, ProductId = 1, ProductName = "ERP471 报价商品", Unit = "PCS",
                Quantity = 3m, UnitPrice = 10.5m, Amount = 32m
            });

        Assert.IsType<OkObjectResult>(await QuotationControllerFor(db, _fixture.PrivilegedUserId)
            .ToProformaInvoice(quotation.Id));

        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(p => p.QuotationId == quotation.Id);
        var line = await verify.ProformaInvoiceDetails.AsNoTracking().SingleAsync(d => d.PiId == pi.Id);
        Assert.Equal(Currency.JPY, pi.Currency);
        Assert.Equal(32m, pi.TotalAmount);        // 31.5 → JPY 0 位 0.5 进位
        Assert.Equal(1.60m, pi.TotalAmountCny);
        Assert.Equal(10m, pi.DepositAmount);      // 32 × 30% = 9.6 → 0 位 0.5 进位
        Assert.Equal(32m, line.Amount);
    }
}

/// <summary>
/// ERP-471 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ProformaInvoiceCommercialValidationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_PICV";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }

    public long CustomerId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-471] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-471] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 特权账号：既有系统内置角色（等同既有无保留访问口径，不新增菜单 / 角色授权）。
        var role = new SysRole
        {
            RoleName = "ERP471 特权角色", RoleCode = $"ERP471-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"erp471-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP471 隔离账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = user.Id;

        var customer = await SeedCustomerAsync(db, 30m);
        CustomerId = customer.Id;

        Console.WriteLine("[ERP-471] 既有授权 + 客户夹具就绪（受控只读，不新增权限模型）。");
    }

    /// <summary>客户夹具（每个用例可创建自己的客户，绝不改写共享客户行）。</summary>
    public async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, decimal depositRatio)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-471-{Guid.NewGuid():N}", CustomerName = "ERP471 客户",
            Status = 1, CreditStatus = "正常", DepositRatio = depositRatio
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    public async Task<Quotation> SeedQuotationAsync(ErpDbContext db, long customerId, DocumentStatus status,
        params QuotationDetail[] lines)
        => await SeedQuotationAsync(db, customerId, status, Currency.USD, 7.2m, lines);

    /// <summary>直接落库的报价单夹具（用于「报价转 PI 不传播非法来源商业值」）。</summary>
    public async Task<Quotation> SeedQuotationAsync(ErpDbContext db, long customerId, DocumentStatus status,
        Currency currency, decimal exchangeRate, params QuotationDetail[] lines)
    {
        var quotation = new Quotation
        {
            QuotationNo = $"QT-{Guid.NewGuid():N}"[..18],
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "ERP471 客户",
            Currency = currency,
            ExchangeRate = exchangeRate,
            Status = status,
            Details = lines.ToList()
        };
        var line = 0;
        foreach (var detail in quotation.Details)
        {
            detail.SortNo = ++line;
            detail.Amount = detail.Quantity * detail.UnitPrice;
        }
        quotation.TotalAmount = quotation.Details.Sum(d => d.Amount);
        quotation.TotalAmountCny = quotation.TotalAmount * exchangeRate;
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();
        return quotation;
    }

    /// <summary>直接落库的 PI 夹具（不经写入契约，允许构造非法历史条款用于承诺前复核）。</summary>
    public async Task<ProformaInvoice> SeedPiAsync(ErpDbContext db, DocumentStatus status,
        decimal exchangeRate, params ProformaInvoiceDetail[] lines)
    {
        var pi = new ProformaInvoice
        {
            PiNo = $"PI-{Guid.NewGuid():N}"[..18],
            PiDate = DateTime.Today,
            CustomerId = CustomerId,
            CustomerName = "ERP471 客户",
            Currency = Currency.USD,
            ExchangeRate = exchangeRate,
            DepositRatio = 30m,
            DepositAmount = 0m,
            Status = status,
            Details = lines.ToList()
        };
        var line = 0;
        foreach (var detail in pi.Details)
        {
            detail.SortNo = ++line;
            detail.Amount = detail.Quantity * detail.UnitPrice;
        }
        pi.TotalAmount = pi.Details.Sum(d => d.Amount);
        pi.TotalAmountCny = pi.TotalAmount * exchangeRate;
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ProformaInvoiceCommercialValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ProformaInvoiceCommercialValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
