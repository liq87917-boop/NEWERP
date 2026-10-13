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
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-475 询价单「实时主数据引用」护栏的**真实 SQL Server** 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「询价单」功能菜单 + 业务员客户数据范围
/// 驱动真实 <see cref="InquiryController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>受控错误与零写入</b>：缺失 / 已删除 / 已停用客户，缺失（无自带文本）/ 已删除 / 已停用 / 零 Id 商品，
/// 缺失 / 已离职 / 非业务员业务员，均在单号预约与任何赋值之前被受控业务错误拒绝；
/// <c>Inquiries</c> / <c>InquiryDetails</c> / <c>Quotations</c> / <c>QuotationDetails</c> 与单号流水全部不变。</item>
/// <item><b>手工快照行</b>：悬空商品 Id + 自带受界商品文本照常保存、提交、审核，且悬空 Id 与文本原样保留
/// （不回填 / 不改派）。</item>
/// <item><b>锁内重查</b>：草稿保存后客户被删除 / 商品被停用时提交 / 审核原子拒绝，状态与原始证据不变；
/// 询价转报价失败绝不消耗报价单号、绝不落库、绝不改写来源询价单状态。</item>
/// <item><b>历史读取只读</b>：引用失效主数据的历史询价单仍可读、不被回填。</item>
/// <item><b>商品批量查询</b>：多条明细引用多个 / 重复商品时，商品资料只发生**一次**查询（命令拦截计数）。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有询价单来源审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class InquiryMasterReferenceSqlServerTests
    : IClassFixture<InquiryMasterReferenceSqlServerFixture>
{
    private readonly InquiryMasterReferenceSqlServerFixture _fixture;

    public InquiryMasterReferenceSqlServerTests(InquiryMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => InquiryMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static InquiryController NewController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/inquiries";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new InquiryController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Inquiries"] = await db.Inquiries.CountAsync(),
            ["InquiryDetails"] = await db.InquiryDetails.CountAsync(),
            ["Quotations"] = await db.Quotations.CountAsync(),
            ["QuotationDetails"] = await db.QuotationDetails.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in before.Keys)
            Assert.Equal(before[table], after[table]);
    }

    /// <summary>手工商品快照行：悬空商品 Id + 自带受界商品文本。</summary>
    private static InquiryDetail ManualDetail(string name)
        => new() { ProductId = 9_475_002L, ProductName = name, Spec = "标准", Unit = "PCS", Quantity = 2m, UnitPrice = 5m };

    private static InquiryDetail LinkedDetail(long productId)
        => new() { ProductId = productId, ProductName = "集成商品", Spec = "标准", Unit = "PCS", Quantity = 2m, UnitPrice = 5m };

    private static Inquiry NewInquiryBody(long customerId, params InquiryDetail[] details)
        => new()
        {
            InquiryDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            ValidDays = 30,
            Details = details.ToList()
        };

    private static Inquiry NewInquiryBodyWithSalesman(long customerId, long salesmanId,
        params InquiryDetail[] details)
    {
        var body = NewInquiryBody(customerId, details);
        body.SalesmanId = salesmanId;
        return body;
    }

    private static long StoredInquiryId(ErpDbContext db)
        => db.Inquiries.AsNoTracking().OrderByDescending(i => i.Id).Select(i => i.Id).First();

    // ==================== 1. 新增：失效引用受控拒绝、零写入、零占号 ====================

    [Fact]
    public async Task 新增_失效引用_真实SQL受控拒绝且零写入零占号()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.InquirySequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var missingCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.MissingCustomerId, ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.NotFound, missingCustomer.Code);

        var deletedCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.DeletedCustomerId, ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.NotFound, deletedCustomer.Code);

        var disabledCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.DisabledCustomerId, ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledCustomer.Code);

        var zeroCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(0L, ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.InvalidParameter, zeroCustomer.Code);

        var deletedProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.CustomerId, LinkedDetail(_fixture.DeletedProductId))));
        Assert.Equal(ErrorCodes.NotFound, deletedProduct.Code);

        var disabledProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.CustomerId, LinkedDetail(_fixture.DisabledProductId))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledProduct.Code);

        var zeroProduct = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.CustomerId,
                new InquiryDetail { ProductId = 0L, ProductName = "零 Id 行", Quantity = 2m, UnitPrice = 5m })));
        Assert.Equal(ErrorCodes.InvalidParameter, zeroProduct.Code);

        var missingProductNoText = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBody(_fixture.CustomerId,
                new InquiryDetail { ProductId = _fixture.MissingProductId, Quantity = 2m, UnitPrice = 5m })));
        Assert.Equal(ErrorCodes.NotFound, missingProductNoText.Code);

        var missingSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBodyWithSalesman(_fixture.CustomerId, _fixture.MissingSalesmanId,
                ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.NotFound, missingSalesman.Code);

        var disabledSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBodyWithSalesman(_fixture.CustomerId, _fixture.DisabledEmployeeId,
                ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledSalesman.Code);

        var notSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewInquiryBodyWithSalesman(_fixture.CustomerId, _fixture.NotSalesmanEmployeeId,
                ManualDetail("手工商品"))));
        Assert.Equal(ErrorCodes.RuleConflict, notSalesman.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.InquirySequenceAsync());   // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task 新增_合法主数据_真实SQL落库且消耗恰好一次单号()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.InquirySequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var result = await ctl.Create(NewInquiryBodyWithSalesman(_fixture.CustomerId,
            _fixture.EmployeeId, LinkedDetail(_fixture.ProductId)));
        Assert.IsType<OkObjectResult>(result);

        var stored = await db.Inquiries.AsNoTracking().Include(i => i.Details)
            .SingleAsync(i => i.Id == StoredInquiryId(db));
        Assert.StartsWith("INQ", stored.InquiryNo);
        Assert.Equal(_fixture.CustomerId, stored.CustomerId);
        Assert.Equal(_fixture.EmployeeId, stored.SalesmanId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(_fixture.ProductId, stored.Details.Single().ProductId);
        Assert.Equal(10m, stored.Details.Single().Amount);

        var after = await SnapshotAsync();
        Assert.Equal(before["Inquiries"] + 1, after["Inquiries"]);
        Assert.Equal(before["InquiryDetails"] + 1, after["InquiryDetails"]);
        Assert.Equal(sequenceBefore + 1, await _fixture.InquirySequenceAsync());
    }

    [Fact]
    public async Task 手工快照行_真实SQL保存提交审核且文本原样保留()
    {
        Guard();
        var sequenceBefore = await _fixture.InquirySequenceAsync();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewInquiryBody(_fixture.CustomerId,
            ManualDetail("手工快照商品"))));
        var inquiryId = StoredInquiryId(db);
        Assert.IsType<OkObjectResult>(await ctl.Submit(inquiryId));
        Assert.IsType<OkObjectResult>(await ctl.Approve(inquiryId));

        var stored = await db.Inquiries.AsNoTracking().Include(i => i.Details)
            .SingleAsync(i => i.Id == inquiryId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        var line = stored.Details.Single();
        Assert.Equal(9_475_002L, line.ProductId);                 // 悬空 Id 原样保留（不回填 / 不改派）
        Assert.Equal("手工快照商品", line.ProductName);
        Assert.Equal("标准", line.Spec);
        Assert.Equal("PCS", line.Unit);
        Assert.Equal(sequenceBefore + 1, await _fixture.InquirySequenceAsync());
    }

    [Fact]
    public async Task 提交审核_草稿后主数据失效_真实SQL原子拒绝且状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        // 场景 1：草稿保存后客户被软删除 → 提交拒绝，状态与单号不变。
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        Assert.IsType<OkObjectResult>(await ctl.Create(NewInquiryBody(customerId, LinkedDetail(productId))));
        var inquiryId = StoredInquiryId(db);
        var consumed = await _fixture.InquirySequenceAsync();

        await _fixture.SoftDeleteCustomerAsync(customerId);
        var submitDeleted = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(inquiryId));
        Assert.Equal(ErrorCodes.NotFound, submitDeleted.Code);
        Assert.Equal(DocumentStatus.Pending, (await db.Inquiries.AsNoTracking()
            .SingleAsync(i => i.Id == inquiryId)).Status);
        Assert.Equal(consumed, await _fixture.InquirySequenceAsync());

        // 场景 2：草稿保存后商品被停用 → 提交拒绝（锁内权威重读 + 实时主数据引用）。
        var (customerId2, productId2) = await _fixture.SeedTransientMastersAsync();
        Assert.IsType<OkObjectResult>(await ctl.Create(NewInquiryBody(customerId2, LinkedDetail(productId2))));
        var inquiryId2 = StoredInquiryId(db);
        await _fixture.DisableProductAsync(productId2);
        var submitDisabled = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(inquiryId2));
        Assert.Equal(ErrorCodes.RuleConflict, submitDisabled.Code);
        Assert.Equal(DocumentStatus.Pending, (await db.Inquiries.AsNoTracking()
            .SingleAsync(i => i.Id == inquiryId2)).Status);

        // 场景 3：已提交后商品被停用 → 审核拒绝，状态停在已提交。
        var (customerId3, productId3) = await _fixture.SeedTransientMastersAsync();
        Assert.IsType<OkObjectResult>(await ctl.Create(NewInquiryBody(customerId3, LinkedDetail(productId3))));
        var inquiryId3 = StoredInquiryId(db);
        Assert.IsType<OkObjectResult>(await ctl.Submit(inquiryId3));
        await _fixture.DisableProductAsync(productId3);
        var approveDisabled = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(inquiryId3));
        Assert.Equal(ErrorCodes.RuleConflict, approveDisabled.Code);
        Assert.Equal(DocumentStatus.Submitted, (await db.Inquiries.AsNoTracking()
            .SingleAsync(i => i.Id == inquiryId3)).Status);
    }

    // ==================== 2. 询价转报价：来源失效拒绝、合法转换正向 ====================

    [Fact]
    public async Task 转报价_来源失效_真实SQL在占号与改写来源之前受控拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        // 场景 1：已审核来源的客户被软删除。
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        var inquiryId = await _fixture.SeedInquiryAsync(customerId, productId, _fixture.EmployeeId);
        await _fixture.SoftDeleteCustomerAsync(customerId);

        // 场景 2：已审核来源的商品被停用。
        var (customerId2, productId2) = await _fixture.SeedTransientMastersAsync();
        var inquiryId2 = await _fixture.SeedInquiryAsync(customerId2, productId2, _fixture.EmployeeId);
        await _fixture.DisableProductAsync(productId2);

        // 夹具播种完成后取快照：两次被拒绝的转换必须零写入。
        var before = await SnapshotAsync();
        var quotationSequenceBefore = await _fixture.QuotationSequenceAsync();

        var direct = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToQuotation(inquiryId));
        Assert.Equal(ErrorCodes.NotFound, direct.Code);

        var prefill = await Assert.ThrowsAsync<BusinessException>(() => ctl.QuotationPrefill(inquiryId));
        Assert.Equal(ErrorCodes.NotFound, prefill.Code);

        Assert.Equal(DocumentStatus.Approved, (await db.Inquiries.AsNoTracking()
            .SingleAsync(i => i.Id == inquiryId)).Status);

        var productEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToQuotation(inquiryId2));
        Assert.Equal(ErrorCodes.RuleConflict, productEx.Code);
        Assert.Equal(DocumentStatus.Approved, (await db.Inquiries.AsNoTracking()
            .SingleAsync(i => i.Id == inquiryId2)).Status);

        AssertSnapshotUnchanged(before, await SnapshotAsync());          // 零报价单 / 零报价明细
        Assert.Equal(quotationSequenceBefore, await _fixture.QuotationSequenceAsync());
    }

    [Fact]
    public async Task 转报价_合法来源_真实SQL正向生成并把来源置为完成()
    {
        Guard();
        var before = await SnapshotAsync();
        var quotationSequenceBefore = await _fixture.QuotationSequenceAsync();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        var inquiryId = await _fixture.SeedInquiryAsync(customerId, productId, _fixture.EmployeeId);
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var result = await ctl.ToQuotation(inquiryId);

        Assert.IsType<OkObjectResult>(result);
        var quotation = await db.Quotations.AsNoTracking().Include(q => q.Details)
            .SingleAsync(q => q.InquiryId == inquiryId);
        Assert.Equal(customerId, quotation.CustomerId);
        Assert.Equal(productId, quotation.Details.Single().ProductId);
        Assert.Equal(_fixture.EmployeeName, quotation.SalesmanName);
        Assert.Equal(DocumentStatus.Completed, (await db.Inquiries.AsNoTracking()
            .SingleAsync(i => i.Id == inquiryId)).Status);

        var after = await SnapshotAsync();
        Assert.Equal(before["Quotations"] + 1, after["Quotations"]);
        Assert.Equal(before["QuotationDetails"] + 1, after["QuotationDetails"]);
        Assert.Equal(quotationSequenceBefore + 1, await _fixture.QuotationSequenceAsync());
    }

    [Fact]
    public async Task 读取_引用失效的历史询价单_真实SQL原样只读且不回填()
    {
        Guard();
        var (customerId, productId) = await _fixture.SeedTransientMastersAsync();
        var inquiryId = await _fixture.SeedInquiryAsync(customerId, productId, _fixture.EmployeeId);
        await _fixture.SoftDeleteCustomerAsync(customerId);
        await _fixture.SoftDeleteProductAsync(productId);

        await using var db = _fixture.CreateDbContext();
        var result = await NewController(db, _fixture.PrivilegedUserId).GetById(inquiryId);

        var response = Assert.IsType<ApiResponse<Inquiry>>(Assert.IsType<OkObjectResult>(result).Value);
        var data = response.Data!;
        Assert.Equal(customerId, data.CustomerId);
        Assert.Equal(productId, data.Details.Single().ProductId);
        Assert.Equal("ERP475 询价商品", data.Details.Single().ProductName);   // 文本原样，无自动回填 / 改派
    }

    [Fact]
    public async Task 新增_多行多商品_真实SQL商品资料仅一次批量查询()
    {
        Guard();
        var counter = new ProductQueryCounter();
        await using var db = _fixture.CreateDbContext(counter);
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var result = await ctl.Create(NewInquiryBody(_fixture.CustomerId,
            LinkedDetail(_fixture.ProductId),
            LinkedDetail(_fixture.ProductId),
            LinkedDetail(_fixture.ProductId),
            ManualDetail("手工快照行")));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, counter.BaseProductQueries);      // 去重集合一次批量查询，绝不逐行查库
    }

    [Fact]
    public async Task 授权先于主数据_无菜单账号_真实SQL非披露拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        var sequenceBefore = await _fixture.InquirySequenceAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.NoMenuUserId)
                .Create(NewInquiryBody(_fixture.MissingCustomerId, ManualDetail("手工商品"))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(InquiryAuthorizationRules.MenuDeniedText, ex.Message);   // 非披露：不泄露引用有效性
        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(sequenceBefore, await _fixture.InquirySequenceAsync());
    }

    /// <summary>统计 <c>BaseProducts</c> 查询次数的命令拦截器（证明商品资料一次有界批量查询）。</summary>
    private sealed class ProductQueryCounter : DbCommandInterceptor
    {
        public int BaseProductQueries { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.Contains("BaseProducts", StringComparison.OrdinalIgnoreCase))
                BaseProductQueries++;
        }
    }
}

/// <summary>
/// ERP-475 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class InquiryMasterReferenceSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_INQMASTERREF";

    private long _restrictedEmployeeId;

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long NoMenuUserId { get; private set; }
    public long EmployeeId { get; private set; }
    public string EmployeeName { get; private set; } = string.Empty;
    public long CustomerId { get; private set; }
    public long DeletedCustomerId { get; private set; }
    public long DisabledCustomerId { get; private set; }
    public long ProductId { get; private set; }
    public long DeletedProductId { get; private set; }
    public long DisabledProductId { get; private set; }
    public long DeletedEmployeeId { get; private set; }
    public long DisabledEmployeeId { get; private set; }
    public long NotSalesmanEmployeeId { get; private set; }

    /// <summary>故意不存在的客户 Id（缺失引用场景）。</summary>
    public long MissingCustomerId => 9_475_001L;

    /// <summary>故意不存在的商品 Id（缺失引用 / 手工快照快照行场景）。</summary>
    public long MissingProductId => 9_475_002L;

    /// <summary>故意不存在的业务员 Id（缺失引用场景）。</summary>
    public long MissingSalesmanId => 9_475_003L;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-475] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>带命令拦截器的上下文（用于统计商品查询次数）。</summary>
    public ErpDbContext CreateDbContext(DbCommandInterceptor interceptor) => new(BuildOptions(interceptor));

    private DbContextOptions<ErpDbContext> BuildOptions(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return builder.Options;
    }

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

        Console.WriteLine("[ERP-475] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 主数据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP475 特权角色", RoleCode = $"ERP475-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「询价单」+「报价单」功能菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRole = new SysRole
        {
            RoleName = $"ERP475-{Guid.NewGuid():N}", RoleCode = $"ERP475-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(restrictedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRole.Id });
        await db.SaveChangesAsync();
        await GrantMenuAsync(db, restrictedRole.Id, InquiryAuthorizationRules.RequiredMenuCode,
            InquiryAuthorizationRules.RequiredMenuText);
        await GrantMenuAsync(db, restrictedRole.Id, QuotationAuthorizationRules.RequiredMenuCode,
            QuotationAuthorizationRules.RequiredMenuText);
        RestrictedUserId = restricted.Id;

        var restrictedEmployee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP475 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(restrictedEmployee);
        await db.SaveChangesAsync();
        _restrictedEmployeeId = restrictedEmployee.Id;

        // 3) 无菜单账号：既有菜单授权 fail closed（非披露：引用是否有效不影响授权结果）。
        var noMenu = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(noMenu);
        await db.SaveChangesAsync();
        var noMenuRole = new SysRole
        {
            RoleName = $"ERP475-NM-{Guid.NewGuid():N}", RoleCode = $"ERP475-NM-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(noMenuRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = noMenu.Id, RoleId = noMenuRole.Id });
        await db.SaveChangesAsync();
        NoMenuUserId = noMenu.Id;

        await SeedMastersAsync(db);
        await SeedNumberRuleAsync(db, DocumentType.Inquiry, "INQ");
        await SeedNumberRuleAsync(db, DocumentType.Quotation, "QT");
    }

    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuName, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>既有主数据夹具：客户（启用 / 已删除 / 已停用）、员工（在职业务员 / 已删除 / 已停用 / 非业务员）、商品（启用 / 已删除 / 已停用）。</summary>
    private async Task SeedMastersAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-475-{Guid.NewGuid():N}", CustomerName = "ERP475 可见客户",
            EmpId = _restrictedEmployeeId, Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var deletedCustomer = new BaseCustomer
        {
            CustomerCode = $"C-475D-{Guid.NewGuid():N}", CustomerName = "ERP475 已删除客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m, IsDeleted = true
        };
        var disabledCustomer = new BaseCustomer
        {
            CustomerCode = $"C-475S-{Guid.NewGuid():N}", CustomerName = "ERP475 已停用客户",
            Status = 0, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.AddRange(customer, deletedCustomer, disabledCustomer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;
        DeletedCustomerId = deletedCustomer.Id;
        DisabledCustomerId = disabledCustomer.Id;

        var salesman = new BaseEmployee
        {
            EmployeeCode = $"E-475-{Guid.NewGuid():N}", EmployeeName = "ERP475 在职业务员",
            IsSalesman = true, Status = 1
        };
        var deletedEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-475D-{Guid.NewGuid():N}", EmployeeName = "ERP475 已删除业务员",
            IsSalesman = true, Status = 1, IsDeleted = true
        };
        var disabledEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-475S-{Guid.NewGuid():N}", EmployeeName = "ERP475 离职业务员",
            IsSalesman = true, Status = 0
        };
        var notSalesman = new BaseEmployee
        {
            EmployeeCode = $"E-475N-{Guid.NewGuid():N}", EmployeeName = "ERP475 非业务员员工",
            IsSalesman = false, Status = 1
        };
        db.BaseEmployees.AddRange(salesman, deletedEmployee, disabledEmployee, notSalesman);
        await db.SaveChangesAsync();
        EmployeeId = salesman.Id;
        EmployeeName = salesman.EmployeeName;
        DeletedEmployeeId = deletedEmployee.Id;
        DisabledEmployeeId = disabledEmployee.Id;
        NotSalesmanEmployeeId = notSalesman.Id;

        var product = new BaseProduct
        {
            ProductCode = $"P-475-{Guid.NewGuid():N}", ProductName = "ERP475 商品", Unit = "PCS", Status = 1
        };
        var deletedProduct = new BaseProduct
        {
            ProductCode = $"P-475D-{Guid.NewGuid():N}", ProductName = "ERP475 已删除商品", Unit = "PCS",
            Status = 1, IsDeleted = true
        };
        var disabledProduct = new BaseProduct
        {
            ProductCode = $"P-475S-{Guid.NewGuid():N}", ProductName = "ERP475 已停用商品", Unit = "PCS", Status = 0
        };
        db.BaseProducts.AddRange(product, deletedProduct, disabledProduct);
        await db.SaveChangesAsync();
        ProductId = product.Id;
        DeletedProductId = deletedProduct.Id;
        DisabledProductId = disabledProduct.Id;

        Console.WriteLine("[ERP-475] 既有授权 + 主数据夹具就绪（受控只读，不新增权限模型）。");
    }

    /// <summary>
    /// 复用既有 <c>SeedData</c> 单据号规则（专用全新库内统一从 0 起算流水），
    /// 便于如实断言「被拒绝的请求零消耗单号 / 成功恰好 +1」。
    /// </summary>
    private static async Task SeedNumberRuleAsync(ErpDbContext db, DocumentType type, string prefix)
    {
        var rules = await db.SysDocumentNumberRules
            .Where(r => r.DocumentType == type && !r.IsDeleted).ToListAsync();
        if (rules.Count == 0)
        {
            var created = new SysDocumentNumberRule
            {
                DocumentType = type,
                RuleCode = $"{prefix}-{Guid.NewGuid():N}",
                RuleName = $"{prefix} 单号",
                Prefix = prefix,
                DateFormat = "yyyyMMdd",
                SerialLength = 4,
                Separator = string.Empty,
                IsDeleted = false
            };
            db.SysDocumentNumberRules.Add(created);
            rules.Add(created);
        }
        foreach (var rule in rules)
        {
            rule.CurrentSequence = 0;
            rule.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>读取询价单单号流水的当前序号（被拒绝的请求必须保持不变）。</summary>
    public async Task<long> InquirySequenceAsync()
    {
        await using var db = CreateDbContext();
        return await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.Inquiry && !r.IsDeleted)
            .MaxAsync(r => r.CurrentSequence);
    }

    /// <summary>读取报价单单号流水的当前序号（被拒绝的转换必须保持不变）。</summary>
    public async Task<long> QuotationSequenceAsync()
    {
        await using var db = CreateDbContext();
        return await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.Quotation && !r.IsDeleted)
            .MaxAsync(r => r.CurrentSequence);
    }

    /// <summary>
    /// 播种**专用**的既有合法客户 / 商品（避免「提交前被删除 / 停用」模拟污染共享夹具的其他用例）。
    /// </summary>
    public async Task<(long CustomerId, long ProductId)> SeedTransientMastersAsync()
    {
        await using var db = CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-T-{Guid.NewGuid():N}", CustomerName = "ERP475 临时客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var product = new BaseProduct
        {
            ProductCode = $"P-T-{Guid.NewGuid():N}", ProductName = "ERP475 临时商品", Unit = "PCS", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return (customer.Id, product.Id);
    }

    /// <summary>播种一张已审核询价单（引用既有实时客户 / 商品），供询价转报价场景使用。</summary>
    public async Task<long> SeedInquiryAsync(long customerId, long productId, long? salesmanId)
    {
        await using var db = CreateDbContext();
        var inquiry = new Inquiry
        {
            InquiryNo = $"INQ-475-{Guid.NewGuid():N}"[..18],
            InquiryDate = DateTime.Today,
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            ValidDays = 30,
            Status = DocumentStatus.Approved,
            Details = new List<InquiryDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "ERP475 询价商品", Spec = "标准", Unit = "PCS",
                    Quantity = 2m, UnitPrice = 5m, Amount = 10m
                }
            }
        };
        db.Inquiries.Add(inquiry);
        await db.SaveChangesAsync();
        return inquiry.Id;
    }

    /// <summary>软删除既有客户（模拟草稿保存后的主数据失效；只改既有行标记，绝不删除数据）。</summary>
    public async Task SoftDeleteCustomerAsync(long customerId)
    {
        await using var db = CreateDbContext();
        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>停用既有商品（模拟草稿保存后的主数据失效；只改既有行状态，绝不删除数据）。</summary>
    public async Task DisableProductAsync(long productId)
    {
        await using var db = CreateDbContext();
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Status = 0;
        await db.SaveChangesAsync();
    }

    /// <summary>软删除既有商品（模拟历史读取的失效引用；只改既有行标记，绝不删除数据）。</summary>
    public async Task SoftDeleteProductAsync(long productId)
    {
        await using var db = CreateDbContext();
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp475-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP475 隔离账号", Status = status
    };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class InquiryMasterReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => InquiryMasterReferenceSqlServerFixture.AssertDedicatedTarget(connection));
}
