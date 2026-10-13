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
/// ERP-473 形式发票 PI「实时主数据引用」护栏的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「形式发票 PI」/「报价单」/
/// 「销售订单」功能菜单 + 业务员客户数据范围驱动真实 <see cref="ProformaInvoiceController"/> /
/// <see cref="QuotationController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>受控错误与零写入</b>：缺失 / 已删除 / 已停用客户，缺失 / 已删除 / 已停用商品，已离职业务员，
/// 均在单号预约与任何赋值之前被受控业务错误拒绝；<c>ProformaInvoices</c> / <c>ProformaInvoiceDetails</c> /
/// <c>SalesOrders</c> / <c>SalesOrderDetails</c> / <c>Quotations</c> / <c>SysOperationLogs</c> 只读快照
/// 与全部字轨序列之和均不变。</item>
/// <item><b>草稿后被失效</b>：提交 / 审核在既有 PI 来源行锁内重查引用，客户被删除 / 商品被停用即原子拒绝，
/// 状态与原始证据不变；转销售订单在 PI 行锁内拒绝失效引用且零订单 / 零占号 / 来源状态不变。</item>
/// <item><b>报价单转 PI</b>：报价单行锁内复核生成草稿的实时引用，失效即零 PI / 零占号 / 来源报价单状态不变；
/// 合法引用照常生成并保留手工商品快照文本。</item>
/// <item><b>历史读取只读</b>：引用失效的历史 PI 仍可读、不被回填或静默改派。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ProformaInvoiceMasterReferenceSqlServerTests
    : IClassFixture<ProformaInvoiceMasterReferenceSqlServerFixture>
{
    private readonly ProformaInvoiceMasterReferenceSqlServerFixture _fixture;

    public ProformaInvoiceMasterReferenceSqlServerTests(ProformaInvoiceMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "ProformaInvoices", "ProformaInvoiceDetails", "SalesOrders", "SalesOrderDetails",
        "Quotations", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => ProformaInvoiceMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static ProformaInvoiceController NewPiController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/sales/proforma-invoices";   // 真实 HTTP 写入请求形状：护栏执行
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        return new ProformaInvoiceController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static QuotationController NewQuotationController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/sales/quotations";
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        return new QuotationController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ProformaInvoices"] = await db.ProformaInvoices.CountAsync(),
            ["ProformaInvoiceDetails"] = await db.ProformaInvoiceDetails.CountAsync(),
            ["SalesOrders"] = await db.SalesOrders.CountAsync(),
            ["SalesOrderDetails"] = await db.SalesOrderDetails.CountAsync(),
            ["Quotations"] = await db.Quotations.CountAsync(),
            ["SysOperationLogs"] = await db.SysOperationLogs.CountAsync(),
        };
    }

    /// <summary>全部字轨当前序列之和：单号预约必然改变它，因此可判定「被拒绝的请求零占号」。</summary>
    private async Task<long> NumberSequenceTotalAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.SysDocumentNumberRules.SumAsync(r => r.CurrentSequence);
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in SnapshotTables)
            Assert.Equal(before[table], after[table]);
    }

    private static ProformaInvoice NewPiBody(long? customerId, long? salesmanId,
        params ProformaInvoiceDetail[] lines)
        => new()
        {
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "ERP473 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SalesmanId = salesmanId,
            Details = lines.ToList()
        };

    private static ProformaInvoiceDetail Line(long? productId, decimal quantity = 10m, decimal unitPrice = 100m,
        string? code = null, string? name = null)
        => new()
        {
            ProductId = productId,
            ProductCode = code ?? (productId is > 0 ? $"CODE-{productId}" : string.Empty),
            ProductName = name ?? (productId is > 0 ? "ERP473 商品" : string.Empty),
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = quantity * unitPrice
        };

    // ==================== 1. 新增：失效引用受控拒绝且零写入零占号 ====================

    [Fact]
    public async Task 新增_客户缺失或已删除_真实SQL受控拒绝且零写入零占号()
    {
        Guard();
        var before = await SnapshotAsync();
        var beforeSequence = await NumberSequenceTotalAsync();
        await using var db = _fixture.CreateDbContext();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(9_473_999L, null, Line(_fixture.ProductId))));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("客户", missing.Message);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.DeletedCustomerId, null, Line(_fixture.ProductId))));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());
    }

    [Fact]
    public async Task 新增_客户已停用_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.DisabledCustomerId, null, Line(_fixture.ProductId))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_商品缺失或已删除_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.CustomerId, null,
                    Line(9_473_888L, code: string.Empty, name: string.Empty))));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("商品", missing.Message);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.CustomerId, null, Line(_fixture.DeletedProductId))));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_商品已停用_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.CustomerId, null, Line(_fixture.DisabledProductId))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_业务员已离职或非业务员_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var left = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.CustomerId, _fixture.DisabledEmployeeId, Line(_fixture.ProductId))));
        Assert.Equal(ErrorCodes.RuleConflict, left.Code);
        Assert.Contains("业务员", left.Message);

        var notSalesman = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId)
                .Create(NewPiBody(_fixture.CustomerId, _fixture.NonSalesmanEmployeeId,
                    Line(_fixture.ProductId))));
        Assert.Equal(ErrorCodes.RuleConflict, notSalesman.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 2. 新增：允许的引用与手工快照 ====================

    [Fact]
    public async Task 新增_合法引用_真实SQL放行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var result = await NewPiController(db, _fixture.PrivilegedUserId)
            .Create(NewPiBody(_fixture.CustomerId, _fixture.LiveEmployeeId, Line(_fixture.ProductId)));

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.ProformaInvoices.Include(o => o.Details)
            .FirstAsync(o => o.SalesmanId == _fixture.LiveEmployeeId);
        Assert.Equal(_fixture.CustomerId, saved.CustomerId);
        Assert.Equal(_fixture.ProductId, saved.Details.Single().ProductId);
    }

    [Fact]
    public async Task 新增_留空客户与手工商品快照行_真实SQL放行并保留快照文本()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var body = NewPiBody(null, null, Line(null, code: "MANUAL-SQL-1", name: "手工商品 SQL"),
            Line(9_473_777L, code: "LEGACY-SQL-1", name: "历史悬空商品"));
        body.Remark = $"MANUAL-{Guid.NewGuid():N}";
        var result = await NewPiController(db, _fixture.PrivilegedUserId).Create(body);

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.ProformaInvoices.Include(o => o.Details)
            .FirstAsync(o => o.CustomerId == null && o.Remark == body.Remark);
        var details = saved.Details.OrderBy(d => d.SortNo).ToList();
        Assert.Equal(2, details.Count);
        Assert.Null(details[0].ProductId);
        Assert.Equal("MANUAL-SQL-1", details[0].ProductCode);
        Assert.Equal(9_473_777L, details[1].ProductId);
        Assert.Equal("历史悬空商品", details[1].ProductName);
    }

    // ==================== 3. 授权前置与撤销授权（非披露） ====================

    [Fact]
    public async Task 新增_越界客户_受限账号_受控权限错误且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.RestrictedUserId)
                .Create(NewPiBody(_fixture.OutOfScopeCustomerId, null, Line(_fixture.ProductId))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 撤销PI菜单后_受限账号提交失效引用_仍返回受控权限错误且非披露()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.NoMenuUserId)
                .Create(NewPiBody(9_473_999L, null, Line(_fixture.ProductId))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        // 非披露：不得因缺少授权而泄露主数据引用复核结论。
        Assert.DoesNotContain("不存在或已删除", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 4. 提交 / 审核：草稿后被失效（PI 来源行锁内重查） ====================

    [Fact]
    public async Task 提交_草稿后客户被删除_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.CustomerId, DocumentStatus.Pending,
            productId: _fixture.ProductId);
        var before = await SnapshotAsync();
        await using (var db = _fixture.CreateDbContext())
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == _fixture.CustomerId);
            customer.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(ctlDb, _fixture.PrivilegedUserId).Submit(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);
        AssertSnapshotUnchanged(before, await SnapshotAsync());

        // 复原夹具（仅恢复本用例改动的行，不删除 / 不清理任何既有行）。
        await using var restore = _fixture.CreateDbContext();
        var row = await restore.BaseCustomers.SingleAsync(c => c.Id == _fixture.CustomerId);
        row.IsDeleted = false;
        await restore.SaveChangesAsync();
    }

    [Fact]
    public async Task 审核_草稿后商品被停用_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.CustomerId, DocumentStatus.Submitted,
            productId: _fixture.ProductId);
        await using (var db = _fixture.CreateDbContext())
        {
            var product = await db.BaseProducts.SingleAsync(p => p.Id == _fixture.ProductId);
            product.Status = 0;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(ctlDb, _fixture.PrivilegedUserId).Approve(pi.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);

        await using var restore = _fixture.CreateDbContext();
        var row = await restore.BaseProducts.SingleAsync(p => p.Id == _fixture.ProductId);
        row.Status = 1;
        await restore.SaveChangesAsync();
    }

    [Fact]
    public async Task 合法PI_提交审核转销售订单全流程_真实SQL()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.CustomerId, DocumentStatus.Pending,
            salesmanId: _fixture.LiveEmployeeId, productId: _fixture.ProductId);

        await using (var ctlDb = _fixture.CreateDbContext())
        {
            var ctl = NewPiController(ctlDb, _fixture.PrivilegedUserId);
            Assert.IsType<OkObjectResult>(await ctl.Submit(pi.Id));
            Assert.IsType<OkObjectResult>(await ctl.Approve(pi.Id));
        }

        await using (var ctlDb = _fixture.CreateDbContext())
        {
            var ok = Assert.IsType<OkObjectResult>(
                await NewPiController(ctlDb, _fixture.PrivilegedUserId).ToSalesOrder(pi.Id));
            var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value).Data!;
            Assert.StartsWith("SO", result.OrderNo);
        }

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Completed, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.SourcePiId == pi.Id);
        Assert.Equal(_fixture.CustomerId, order.CustomerId);
    }

    // ==================== 5. 转销售订单：失效引用零订单零占号 ====================

    [Fact]
    public async Task 转销售订单_客户已删除_真实SQL原子拒绝且零订单零占号()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.CustomerId, DocumentStatus.Approved,
            productId: _fixture.ProductId);
        var beforeSequence = await NumberSequenceTotalAsync();
        await using (var db = _fixture.CreateDbContext())
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == _fixture.CustomerId);
            customer.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(ctlDb, _fixture.PrivilegedUserId).ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourcePiId == pi.Id));
        Assert.Equal(DocumentStatus.Approved, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());

        await using var restore = _fixture.CreateDbContext();
        var row = await restore.BaseCustomers.SingleAsync(c => c.Id == _fixture.CustomerId);
        row.IsDeleted = false;
        await restore.SaveChangesAsync();
    }

    // ==================== 6. 报价单 → PI：草稿引用在行锁内复核 ====================

    [Fact]
    public async Task 报价单转PI_草稿商品已停用_真实SQL原子拒绝零PI零占号且来源状态不变()
    {
        Guard();
        var quotation = await _fixture.SeedQuotationAsync(_fixture.CustomerId, _fixture.ProductId,
            DocumentStatus.Approved);
        var before = await SnapshotAsync();
        var beforeSequence = await NumberSequenceTotalAsync();
        await using (var db = _fixture.CreateDbContext())
        {
            var product = await db.BaseProducts.SingleAsync(p => p.Id == _fixture.ProductId);
            product.Status = 0;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewQuotationController(ctlDb, _fixture.PrivilegedUserId).ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.False(await verify.ProformaInvoices.AnyAsync(o => o.QuotationId == quotation.Id));
        Assert.Equal(DocumentStatus.Approved, (await verify.Quotations.AsNoTracking()
            .SingleAsync(o => o.Id == quotation.Id)).Status);
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());
        AssertSnapshotUnchanged(before, await SnapshotAsync());

        await using var restore = _fixture.CreateDbContext();
        var row = await restore.BaseProducts.SingleAsync(p => p.Id == _fixture.ProductId);
        row.Status = 1;
        await restore.SaveChangesAsync();
    }

    [Fact]
    public async Task 报价单转PI_合法引用_真实SQL生成PI()
    {
        Guard();
        var quotation = await _fixture.SeedQuotationAsync(_fixture.CustomerId, _fixture.ProductId,
            DocumentStatus.Approved);

        await using var ctlDb = _fixture.CreateDbContext();
        var result = await NewQuotationController(ctlDb, _fixture.PrivilegedUserId)
            .ToProformaInvoice(quotation.Id);

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.QuotationId == quotation.Id);
        Assert.StartsWith("PI", pi.PiNo);
        Assert.Equal(_fixture.CustomerId, pi.CustomerId);
    }

    // ==================== 7. 历史读取：引用失效照原样可读 ====================

    [Fact]
    public async Task 历史读取_引用失效的历史PI_真实SQL只读且不被回填()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.DeletedCustomerId, DocumentStatus.Approved,
            salesmanId: _fixture.DisabledEmployeeId, danglingProductId: 9_473_666L);

        await using var ctlDb = _fixture.CreateDbContext();
        var ok = Assert.IsType<OkObjectResult>(
            await NewPiController(ctlDb, _fixture.PrivilegedUserId).GetById(pi.Id));
        var returned = Assert.IsType<ApiResponse<ProformaInvoice>>(ok.Value).Data!;

        Assert.Equal(_fixture.DeletedCustomerId, returned.CustomerId);
        Assert.Equal(_fixture.DisabledEmployeeId, returned.SalesmanId);
        var detail = returned.Details.Single();
        Assert.Equal(9_473_666L, detail.ProductId);
        Assert.Equal("LEGACY-SQL-9", detail.ProductCode);
        Assert.Equal("历史手工商品", detail.ProductName);
    }
}

/// <summary>
/// ERP-473 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ProformaInvoiceMasterReferenceSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_PIMASTERREF";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long NoMenuUserId { get; private set; }
    public long CustomerId { get; private set; }
    public long DeletedCustomerId { get; private set; }
    public long DisabledCustomerId { get; private set; }
    public long OutOfScopeCustomerId { get; private set; }
    public long ProductId { get; private set; }
    public long DeletedProductId { get; private set; }
    public long DisabledProductId { get; private set; }
    public long LiveEmployeeId { get; private set; }
    public long DisabledEmployeeId { get; private set; }
    public long NonSalesmanEmployeeId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-473] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-473] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 主数据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，豁免菜单授权）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP473 特权角色", RoleCode = $"ERP473-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        var piMenu = await EnsureMenuAsync(db, ProformaInvoiceAuthorizationRules.RequiredMenuCode,
            ProformaInvoiceAuthorizationRules.RequiredMenuText);
        var quotationMenu = await EnsureMenuAsync(db, QuotationAuthorizationRules.RequiredMenuCode,
            QuotationAuthorizationRules.RequiredMenuText);
        var salesOrderMenu = await EnsureMenuAsync(db, ProformaInvoiceAuthorizationRules.SalesOrderMenuCode,
            ProformaInvoiceAuthorizationRules.SalesOrderMenuText);

        // 2) 受限业务员：既有「形式发票 PI」+「报价单」+「销售订单」菜单 + 客户数据范围
        //    （登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRole = new SysRole
        {
            RoleName = $"ERP473-R-{Guid.NewGuid():N}", RoleCode = $"ERP473-R-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(restrictedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRole.Id });
        db.SysRoleMenus.AddRange(
            new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = piMenu.Id },
            new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = quotationMenu.Id },
            new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = salesOrderMenu.Id });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var restrictedEmployee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP473 受限业务员",
            IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(restrictedEmployee);
        await db.SaveChangesAsync();

        // 3) 撤销 PI 菜单的受限账号：非系统角色且不授予任何菜单授权。
        var noMenu = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(noMenu);
        await db.SaveChangesAsync();
        var noMenuRole = new SysRole
        {
            RoleName = $"ERP473-N-{Guid.NewGuid():N}", RoleCode = $"ERP473-N-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(noMenuRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = noMenu.Id, RoleId = noMenuRole.Id });
        await db.SaveChangesAsync();
        NoMenuUserId = noMenu.Id;

        // 4) 客户：启用 / 已删除 / 已停用 / 受限账号越界。
        var customer = NewCustomer("ERP473 客户");
        var deletedCustomer = NewCustomer("ERP473 已删除客户", deleted: true);
        var disabledCustomer = NewCustomer("ERP473 已停用客户", status: 0);
        var visibleCustomer = NewCustomer("ERP473 可见客户", empId: restrictedEmployee.Id);
        var outOfScopeCustomer = NewCustomer("ERP473 越界客户");
        db.BaseCustomers.AddRange(customer, deletedCustomer, disabledCustomer, visibleCustomer, outOfScopeCustomer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;
        DeletedCustomerId = deletedCustomer.Id;
        DisabledCustomerId = disabledCustomer.Id;
        OutOfScopeCustomerId = outOfScopeCustomer.Id;

        // 5) 员工：在职业务员 / 已离职 / 非业务员。
        var liveEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-473-{Guid.NewGuid():N}", EmployeeName = "ERP473 在职业务员",
            IsSalesman = true, Status = 1
        };
        var disabledEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-473S-{Guid.NewGuid():N}", EmployeeName = "ERP473 离职业务员",
            IsSalesman = true, Status = 0
        };
        var nonSalesman = new BaseEmployee
        {
            EmployeeCode = $"E-473N-{Guid.NewGuid():N}", EmployeeName = "ERP473 非业务员",
            IsSalesman = false, Status = 1
        };
        db.BaseEmployees.AddRange(liveEmployee, disabledEmployee, nonSalesman);
        await db.SaveChangesAsync();
        LiveEmployeeId = liveEmployee.Id;
        DisabledEmployeeId = disabledEmployee.Id;
        NonSalesmanEmployeeId = nonSalesman.Id;

        // 6) 商品：启用 / 已删除 / 已停用。
        var product = new BaseProduct
        {
            ProductCode = $"P-473-{Guid.NewGuid():N}", ProductName = "ERP473 商品", Unit = "PCS", Status = 1
        };
        var deletedProduct = new BaseProduct
        {
            ProductCode = $"P-473D-{Guid.NewGuid():N}", ProductName = "ERP473 已删除商品",
            Unit = "PCS", Status = 1, IsDeleted = true
        };
        var disabledProduct = new BaseProduct
        {
            ProductCode = $"P-473S-{Guid.NewGuid():N}", ProductName = "ERP473 已停用商品",
            Unit = "PCS", Status = 0
        };
        db.BaseProducts.AddRange(product, deletedProduct, disabledProduct);
        await db.SaveChangesAsync();
        ProductId = product.Id;
        DeletedProductId = deletedProduct.Id;
        DisabledProductId = disabledProduct.Id;

        Console.WriteLine("[ERP-473] 既有授权 + 主数据夹具就绪（受控只读，不新增权限模型）。");
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string menuCode, string menuText)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu { MenuName = menuText, MenuCode = menuCode, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    private static BaseCustomer NewCustomer(string name, int status = 1, bool deleted = false, long? empId = null)
        => new()
        {
            CustomerCode = $"C-473-{Guid.NewGuid():N}",
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            DepositRatio = 30m,
            IsDeleted = deleted,
            EmpId = empId
        };

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp473-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP473 隔离账号", Status = status
    };

    /// <summary>
    /// 播种一张**规范 PI**：<paramref name="productId"/> 为实时商品链接；
    /// <paramref name="danglingProductId"/> 用于构造「悬空 Id + 自带受界商品文本」的历史手工快照行。
    /// </summary>
    public async Task<ProformaInvoice> SeedPiAsync(long? customerId, DocumentStatus status,
        long? salesmanId = null, long? productId = null, long? danglingProductId = null)
    {
        await using var db = CreateDbContext();
        var pi = new ProformaInvoice
        {
            PiNo = $"PI-473-{Guid.NewGuid():N}"[..20],
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "ERP473 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SalesmanId = salesmanId,
            Status = status
        };
        if (productId is long linked)
            pi.Details.Add(new ProformaInvoiceDetail
            {
                SortNo = 1, ProductId = linked, ProductCode = $"CODE-{linked}", ProductName = "ERP473 商品",
                Unit = "PCS", Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
        if (danglingProductId is long dangling)
            pi.Details.Add(new ProformaInvoiceDetail
            {
                SortNo = pi.Details.Count + 1, ProductId = dangling, ProductCode = "LEGACY-SQL-9",
                ProductName = "历史手工商品", Unit = "PCS", Quantity = 1m, UnitPrice = 100m, Amount = 100m
            });
        pi.TotalAmount = pi.Details.Sum(d => d.Amount);
        pi.TotalAmountCny = pi.TotalAmount * 7.2m;
        pi.DepositAmount = pi.TotalAmount * 0.3m;
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }

    /// <summary>播种一张**已审核报价单**（一行明细 10 × 100），供「报价单 → PI」转换场景使用。</summary>
    public async Task<Quotation> SeedQuotationAsync(long? customerId, long? productId, DocumentStatus status)
    {
        await using var db = CreateDbContext();
        var quotation = new Quotation
        {
            QuotationNo = $"QT-473-{Guid.NewGuid():N}"[..20],
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "ERP473 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            Status = status,
            Details = new List<QuotationDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = productId, ProductCode = "QT-473", ProductName = "报价商品",
                    Unit = "PCS", Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        quotation.TotalAmount = 1000m;
        quotation.TotalAmountCny = 7200m;
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();
        return quotation;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ProformaInvoiceMasterReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ProformaInvoiceMasterReferenceSqlServerFixture.AssertDedicatedTarget(connection));
}
