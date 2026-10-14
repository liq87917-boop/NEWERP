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
/// ERP-470 报价单商业条款（币种 / 汇率 / 数量 / 单价 / 金额 / 合计）校验的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// <list type="number">
/// <item><b>真实控制器 + 既有授权</b>：以既有系统内置角色（等同既有无保留访问口径）驱动真实
/// <see cref="QuotationController"/> 与 <see cref="InquiryController"/>，不新增任何菜单 / 角色 / 用户授权。</item>
/// <item><b>非法条款零写入零占号</b>：非法数量 / 未定义币种 / 非正汇率 / 软删除明细注入在新增 / 修改时被受控业务错误拒绝，
/// 报价单 / 明细 / PI / 销售订单 / 单号流水全部零变化。</item>
/// <item><b>服务端权威</b>：客户端伪造的行金额 / 合计在落库时被服务端按币种精度重算覆盖，
/// <c>Σ 有效明细金额 == 主表总额</c> 在真实 <c>DECIMAL(18,4)</c> 金额列落库后仍精确成立；JPY 按 0 位小数落库。</item>
/// <item><b>承诺前复核</b>：直接改库模拟非法历史条款后，提交 / 审核 / 转 PI / 转销售订单原子拒绝且状态与下游单据不变；
/// 已审核询价单转报价单按同一契约派生金额且不改写来源状态。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。
/// 本测试只**新增**自己的夹具行，不清理、不删除、不改写任何既有行。</para>
/// </summary>
public sealed class QuotationCommercialValidationSqlServerTests
    : IClassFixture<QuotationCommercialValidationSqlServerFixture>
{
    private readonly QuotationCommercialValidationSqlServerFixture _fixture;

    public QuotationCommercialValidationSqlServerTests(QuotationCommercialValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => QuotationCommercialValidationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static QuotationController QuotationControllerFor(ErpDbContext db, long userId)
        => Controller(new QuotationController(db, new DocumentNumberService(db)), userId);

    private static InquiryController InquiryControllerFor(ErpDbContext db, long userId)
        => Controller(new InquiryController(db, new DocumentNumberService(db)), userId);

    private static T Controller<T>(T controller, long userId) where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed record WriteSnapshot(
        int Quotations, int QuotationDetails, int ProformaInvoices, int SalesOrders, string Numbering);

    private static async Task<WriteSnapshot> SnapshotAsync(ErpDbContext db) => new(
        await db.Quotations.CountAsync(),
        await db.QuotationDetails.CountAsync(),
        await db.ProformaInvoices.CountAsync(),
        await db.SalesOrders.CountAsync(),
        string.Join(",", await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.Quotation)
            .OrderBy(r => r.Id).Select(r => r.CurrentSequence).ToListAsync()));

    private static void AssertUnchanged(WriteSnapshot before, WriteSnapshot after)
    {
        Assert.Equal(before.Quotations, after.Quotations);
        Assert.Equal(before.QuotationDetails, after.QuotationDetails);
        Assert.Equal(before.ProformaInvoices, after.ProformaInvoices);
        Assert.Equal(before.SalesOrders, after.SalesOrders);
        Assert.Equal(before.Numbering, after.Numbering);
    }

    /// <summary>新增 / 修改请求体（金额一律故意伪造，用于验证服务端不复采信）。</summary>
    private Quotation Body(params QuotationDetail[] details) => new()
    {
        QuotationNo = string.Empty,
        QuotationDate = DateTime.Today,
        CustomerId = _fixture.CustomerId,
        CustomerName = "ERP470 客户",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        Status = DocumentStatus.Pending,
        TotalAmount = 999_999m,
        TotalAmountCny = 1m,
        Details = details.ToList()
    };

    private static QuotationDetail Line(decimal quantity, decimal unitPrice, bool deleted = false) => new()
    {
        ProductId = 1, ProductCode = "P-470", ProductName = "ERP470 商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99, IsDeleted = deleted
    };


    // ==================== 1. 新增 / 修改：非法条款零写入零占号 ====================

    [Fact]
    public async Task 新增_非法数量_拒绝且零写入且不消耗单号()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).Create(Body(Line(0m, 10m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 新增_未定义币种与软删除明细注入_拒绝且零写入()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);
        var controller = QuotationControllerFor(db, _fixture.PrivilegedUserId);

        var badCurrency = Body(Line(1m, 10m));
        badCurrency.Currency = (Currency)999;
        var currencyError = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(badCurrency));
        Assert.Equal(QuotationCommercialValidationRules.CurrencyText, currencyError.Message);

        var injected = Body(Line(1m, 10m), Line(1m, 10m, deleted: true));
        var injectionError = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(injected));
        Assert.Equal(QuotationCommercialValidationRules.SoftDeletedDetailText, injectionError.Message);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 新增_客户端伪造合计与行金额_服务端按币种精度派生并精确落库()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var body = Body(Line(2m, 10m), Line(3m, 4m));

        Assert.IsType<OkObjectResult>(await QuotationControllerFor(db, _fixture.PrivilegedUserId).Create(body));

        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.Quotations.AsNoTracking()
            .Where(q => q.CustomerId == _fixture.CustomerId)
            .OrderByDescending(q => q.Id).FirstAsync();
        var lines = await verify.QuotationDetails.AsNoTracking()
            .Where(d => d.QuotationId == saved.Id).ToListAsync();

        Assert.Equal(new[] { 20m, 12m }, lines.OrderBy(d => d.SortNo).Select(d => d.Amount));
        Assert.Equal(32m, saved.TotalAmount);
        Assert.Equal(230.4m, saved.TotalAmountCny);
        Assert.Equal(saved.TotalAmount, lines.Sum(d => d.Amount));
        Assert.All(lines, d => Assert.True(QuotationCommercialValidationRules.IsRepresentable(d.Amount)));
        Assert.StartsWith("QT", saved.QuotationNo);
    }

    [Fact]
    public async Task 新增_JPY报价单_行金额与合计按零位小数落库()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var body = Body(Line(3m, 10.5m), Line(1m, 100.4m));
        body.Currency = Currency.JPY;
        body.ExchangeRate = 0.05m;

        Assert.IsType<OkObjectResult>(await QuotationControllerFor(db, _fixture.PrivilegedUserId).Create(body));

        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.Quotations.AsNoTracking()
            .Where(q => q.CustomerId == _fixture.CustomerId && q.Currency == Currency.JPY)
            .OrderByDescending(q => q.Id).FirstAsync();
        var lines = await verify.QuotationDetails.AsNoTracking()
            .Where(d => d.QuotationId == saved.Id).OrderBy(d => d.SortNo).ToListAsync();

        Assert.Equal(new[] { 32m, 100m }, lines.Select(d => d.Amount));
        Assert.Equal(132m, saved.TotalAmount);
        Assert.Equal(6.60m, saved.TotalAmountCny);
        Assert.Equal(saved.TotalAmount, lines.Sum(d => d.Amount));
    }

    [Fact]
    public async Task 修改_非法明细_原表头明细与合计不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var quotation = await _fixture.SeedQuotationAsync(db, DocumentStatus.Pending, Line(2m, 10m));
        var before = await SnapshotAsync(db);
        var beforeLines = string.Join(";", await db.QuotationDetails.AsNoTracking()
            .Where(d => d.QuotationId == quotation.Id).OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{d.SortNo}|{d.Quantity}|{d.Amount}").ToListAsync());

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).Update(quotation.Id, Body(Line(-3m, 5m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        var after = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id);
        Assert.Equal(quotation.QuotationNo, after.QuotationNo);
        Assert.Equal(20m, after.TotalAmount);
        Assert.Equal(beforeLines, string.Join(";", await verify.QuotationDetails.AsNoTracking()
            .Where(d => d.QuotationId == quotation.Id).OrderBy(d => d.Id)
            .Select(d => $"{d.Id}|{d.SortNo}|{d.Quantity}|{d.Amount}").ToListAsync()));
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    // ==================== 2. 提交 / 审核：承诺前复核已持久化条款 ====================

    [Fact]
    public async Task 提交_已持久化数量非法_原子拒绝且状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var quotation = await _fixture.SeedQuotationAsync(db, DocumentStatus.Pending, Line(0m, 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).Submit(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        var after = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(0m, after.TotalAmount);
        Assert.Equal(1, await verify.QuotationDetails.CountAsync(d => d.QuotationId == quotation.Id));
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var quotation = await _fixture.SeedQuotationAsync(db, DocumentStatus.Submitted, Line(1m, 10m));
        // 直接改库模拟非法历史条款（绕过写入契约），验证承诺前复核。
        var stored = await db.Quotations.SingleAsync(q => q.Id == quotation.Id);
        stored.Currency = (Currency)777;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).Approve(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.CurrencyText, ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id)).Status);
    }


    // ==================== 3. 转 PI / 转销售订单：非法历史条款不得成为实时履约 ====================

    [Fact]
    public async Task 转PI_已持久化条款非法_拒绝且零PI零占号()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        // ERP-533：0.001 已是声明列（DECIMAL(18,4)）可逐字存储的合法数量，改用负数模拟非法历史条款。
        var quotation = await _fixture.SeedQuotationAsync(db, DocumentStatus.Approved, Line(-1m, 10m));
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id)).Status);
    }

    [Fact]
    public async Task 转销售订单_已持久化单价为负_拒绝且零订单零占号()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var quotation = await _fixture.SeedQuotationAsync(db, DocumentStatus.Approved, Line(1m, -5m));
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationControllerFor(db, _fixture.PrivilegedUserId).ToSalesOrder(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id)).Status);
    }

    [Fact]
    public async Task 合法报价单_审核与转PI仍正常流转()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var quotation = await _fixture.SeedQuotationAsync(db, DocumentStatus.Submitted, Line(2m, 10m));
        var controller = QuotationControllerFor(db, _fixture.PrivilegedUserId);

        Assert.IsType<OkObjectResult>(await controller.Approve(quotation.Id));
        Assert.IsType<OkObjectResult>(await controller.ToProformaInvoice(quotation.Id));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Completed,
            (await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotation.Id)).Status);
        var pi = await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(p => p.QuotationId == quotation.Id);
        Assert.Equal(20m, pi.TotalAmount);
        Assert.Equal(144m, pi.TotalAmountCny);          // 20 × 7.2
    }

    // ==================== 4. 询价 → 报价：同一契约派生金额 ====================

    [Fact]
    public async Task 转换_已审核询价单_按币种精度派生金额且不改写来源快照()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var inquiry = await _fixture.SeedInquiryAsync(db, Currency.JPY, 0.05m, 3m, 10.5m);

        Assert.IsType<OkObjectResult>(await InquiryControllerFor(db, _fixture.PrivilegedUserId)
            .ToQuotation(inquiry.Id));

        await using var verify = _fixture.CreateDbContext();
        var quotation = await verify.Quotations.AsNoTracking().SingleAsync(q => q.InquiryId == inquiry.Id);
        var line = await verify.QuotationDetails.AsNoTracking()
            .SingleAsync(d => d.QuotationId == quotation.Id);

        Assert.Equal(Currency.JPY, quotation.Currency);
        Assert.Equal(32m, line.Amount);                       // 31.5 → JPY 0 位小数 0.5 进位
        Assert.Equal(32m, quotation.TotalAmount);
        Assert.Equal(1.60m, quotation.TotalAmountCny);

        // 来源询价单只按既有语义转为「已完成」；汇率与来源行金额（= 1）一律不被改写。
        var source = await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiry.Id);
        Assert.Equal(DocumentStatus.Completed, source.Status);
        Assert.Equal(0.05m, source.ExchangeRate);
        Assert.Equal(1m, (await verify.InquiryDetails.AsNoTracking()
            .SingleAsync(d => d.InquiryId == inquiry.Id)).Amount);
    }

    [Fact]
    public async Task 转换_询价单条款非法_拒绝且零写入且来源状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var inquiry = await _fixture.SeedInquiryAsync(db, Currency.USD, 7.2m, 0m, 100m);
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryControllerFor(db, _fixture.PrivilegedUserId).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved,
            (await verify.Inquiries.AsNoTracking().SingleAsync(i => i.Id == inquiry.Id)).Status);
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

}

/// <summary>
/// ERP-470 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class QuotationCommercialValidationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_QTCV";

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

        Console.WriteLine($"[ERP-470] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-470] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }


    // ==================== 既有授权（不新增权限模型）+ 客户夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 特权账号：既有系统内置角色（等同既有无保留访问口径，不新增菜单 / 角色授权）。
        var role = new SysRole
        {
            RoleName = "ERP470 特权角色", RoleCode = $"ERP470-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"erp470-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP470 隔离账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = user.Id;

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-470-{Guid.NewGuid():N}", CustomerName = "ERP470 客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;

        Console.WriteLine("[ERP-470] 既有授权 + 客户夹具就绪（受控只读，不新增权限模型）。");
    }

    /// <summary>直接落库的报价单夹具（不经写入契约，允许构造非法历史条款用于承诺前复核）。</summary>
    public async Task<Quotation> SeedQuotationAsync(ErpDbContext db, DocumentStatus status,
        params QuotationDetail[] lines)
    {
        var quotation = new Quotation
        {
            QuotationNo = $"QT-{Guid.NewGuid():N}"[..18],
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = CustomerId,
            CustomerName = "ERP470 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
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
        quotation.TotalAmountCny = quotation.TotalAmount * quotation.ExchangeRate;
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();
        return quotation;
    }

    /// <summary>已审核询价单夹具（来源行金额故意写错，用于验证转换路径不采信来源金额）。</summary>
    public async Task<Inquiry> SeedInquiryAsync(ErpDbContext db, Currency currency, decimal exchangeRate,
        decimal quantity, decimal unitPrice)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = $"INQ-{Guid.NewGuid():N}"[..18],
            InquiryDate = DateTime.Today,
            CustomerId = CustomerId,
            ContactPerson = "ERP470 对接人",
            ContactPhone = "13800000000",
            Currency = currency,
            ExchangeRate = exchangeRate,
            ValidDays = 30,
            Status = DocumentStatus.Approved,
            Details = new List<InquiryDetail>
            {
                new()
                {
                    ProductId = 1, ProductName = "ERP470 询价商品", Spec = "标准", Unit = "PCS",
                    Quantity = quantity, UnitPrice = unitPrice, Amount = 1m
                }
            }
        };
        db.Inquiries.Add(inquiry);
        await db.SaveChangesAsync();
        return inquiry;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class QuotationCommercialValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            QuotationCommercialValidationSqlServerFixture.AssertDedicatedTarget(connection));
}

