using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-490：形式发票 PI 与「报价单 → PI」的实时主数据引用复核**与调用形状完全无关**的**真实 SQL Server** 集成测试
/// （复用 <see cref="ProformaInvoiceMasterReferenceSqlServerFixture"/> 的 GUID 独占 <c>NEWERP_AUTOTEST</c> 目标：
/// <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>，
/// 每次运行全新 GUID 库、发现同名库立即拒绝、绝不 drop / reset / 复用）。
/// <list type="number">
/// <item><b>调用形状无关</b>：同一载荷 + 同一真实启用身份下，「空 <c>Request.Path</c>」与「已赋值
/// <c>Request.Path</c>」在真实控制器上给出**完全相同**的受控结论，且被拒绝的请求零写入 / 零占号；</item>
/// <item><b>允许的草稿</b>：合法实时客户 + 手工商品快照行（显式商品引用留空的空路径调用）照常落库；</item>
/// <item><b>承诺前复核</b>：提交 / 审核在 PI 来源行锁内复核已持久化引用，草稿后客户被删除 / 商品被停用即原子拒绝，
/// 状态与全部字轨不变；转销售订单 / 报价单转 PI 在来源行锁内拒绝失效引用且零订单 / 零 PI / 零占号 / 来源状态不变；</item>
/// <item><b>合法来源草稿</b>：报价单 → PI 在合法实时引用下照常生成并完成来源报价单。</item>
/// </list>
/// <para>只读取计数与只读快照，不删除 / 不清理任何既有行，不新增菜单 / 角色 / 用户授权；构建完成不等于阶段验收，
/// 只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ProformaInvoiceUnconditionalMasterReferenceSqlServerTests
    : IClassFixture<ProformaInvoiceMasterReferenceSqlServerFixture>
{
    private readonly ProformaInvoiceMasterReferenceSqlServerFixture _fixture;

    public ProformaInvoiceUnconditionalMasterReferenceSqlServerTests(
        ProformaInvoiceMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "ProformaInvoices", "ProformaInvoiceDetails", "SalesOrders", "SalesOrderDetails",
        "Quotations", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => ProformaInvoiceMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    /// <summary>
    /// 真实控制器：<paramref name="path"/> 为 <c>null</c> 时身份存在但 <c>Request.Path</c> 无值（空请求路径形状）。
    /// ERP-490 后两种形状必须给出完全相同的实时主数据引用结论。
    /// </summary>
    private static ProformaInvoiceController NewPiController(ErpDbContext db, long? userId, string? path)
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        return new ProformaInvoiceController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static QuotationController NewQuotationController(ErpDbContext db, long? userId, string? path)
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
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
            CustomerName = "ERP490 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SalesmanId = salesmanId,
            Details = lines.ToList()
        };

    private static ProformaInvoiceDetail Line(long? productId, string? code = null, string? name = null)
        => new()
        {
            ProductId = productId,
            ProductCode = code ?? (productId is > 0 ? $"CODE-{productId}" : string.Empty),
            ProductName = name ?? (productId is > 0 ? "ERP490 商品" : string.Empty),
            Unit = "PCS",
            Quantity = 10m,
            UnitPrice = 100m,
            Amount = 1000m
        };

    // ==================== 1. 调用形状无关：真实控制器上结论一致且零副作用 ====================

    [Fact]
    public async Task 新增_相同载荷与身份_空路径与真实路径_受控拒绝与零写入零占号一致()
    {
        Guard();
        var before = await SnapshotAsync();
        var beforeSequence = await NumberSequenceTotalAsync();
        await using var db = _fixture.CreateDbContext();

        var httpPath = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId, "/api/sales/proforma-invoices")
                .Create(NewPiBody(9_490_999L, null, Line(null, code: "MANUAL-490", name: "手工商品"))));
        var nullPath = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId, null)
                .Create(NewPiBody(9_490_999L, null, Line(null, code: "MANUAL-490", name: "手工商品"))));

        Assert.Equal(ErrorCodes.NotFound, nullPath.Code);
        Assert.Equal(httpPath.Code, nullPath.Code);
        Assert.Equal(httpPath.Message, nullPath.Message);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());
    }

    [Fact]
    public async Task 新增_空请求路径_合法实时客户与手工快照行_真实SQL放行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var body = NewPiBody(_fixture.CustomerId, _fixture.LiveEmployeeId,
            Line(null, code: "MANUAL-490", name: "手工商品"));
        body.Remark = "ERP490-NULLPATH-OK";

        var result = await NewPiController(db, _fixture.PrivilegedUserId, null).Create(body);

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var saved = await verify.ProformaInvoices.Include(o => o.Details)
            .SingleAsync(o => o.Remark == "ERP490-NULLPATH-OK");
        Assert.Equal(_fixture.CustomerId, saved.CustomerId);
        Assert.Null(saved.Details.Single().ProductId);
        Assert.Equal("MANUAL-490", saved.Details.Single().ProductCode);
    }

    [Fact]
    public async Task 提交_空请求路径_草稿后客户已失效_锁内复核拒绝且状态与占号不变()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.DeletedCustomerId, DocumentStatus.Pending,
            danglingProductId: 9_490_777L);
        var beforeSequence = await NumberSequenceTotalAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId, null).Submit(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());
    }

    // ==================== 2. 调用形状无关：审核 / 转销售订单（锁内复核） ====================

    [Fact]
    public async Task 审核_空请求路径_草稿后商品已停用_锁内复核拒绝且状态不变()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.CustomerId, DocumentStatus.Pending,
            productId: _fixture.DisabledProductId);
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId, null).Approve(pi.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);
    }

    [Fact]
    public async Task 转销售订单_空请求路径_草稿后客户已失效_拒绝零订单零占号且来源状态不变()
    {
        Guard();
        var pi = await _fixture.SeedPiAsync(_fixture.DeletedCustomerId, DocumentStatus.Approved,
            danglingProductId: 9_490_777L);
        var beforeSnapshot = await SnapshotAsync();
        var beforeSequence = await NumberSequenceTotalAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewPiController(db, _fixture.PrivilegedUserId, null).ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var after = await SnapshotAsync();
        Assert.Equal(beforeSnapshot["SalesOrders"], after["SalesOrders"]);
        Assert.Equal(beforeSnapshot["SalesOrderDetails"], after["SalesOrderDetails"]);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved, (await verify.ProformaInvoices.AsNoTracking()
            .SingleAsync(o => o.Id == pi.Id)).Status);
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());
    }

    // ==================== 3. 调用形状无关：报价单 → PI ====================

    [Fact]
    public async Task 报价单转PI_空请求路径_草稿后客户已失效_拒绝零PI零占号且来源状态不变()
    {
        Guard();
        var quotation = await _fixture.SeedQuotationAsync(_fixture.DeletedCustomerId,
            _fixture.ProductId, DocumentStatus.Approved);
        var beforeSnapshot = await SnapshotAsync();
        var beforeSequence = await NumberSequenceTotalAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewQuotationController(db, _fixture.PrivilegedUserId, null).ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var after = await SnapshotAsync();
        Assert.Equal(beforeSnapshot["ProformaInvoices"], after["ProformaInvoices"]);
        Assert.Equal(beforeSnapshot["ProformaInvoiceDetails"], after["ProformaInvoiceDetails"]);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Approved, (await verify.Quotations.AsNoTracking()
            .SingleAsync(o => o.Id == quotation.Id)).Status);
        Assert.Equal(beforeSequence, await NumberSequenceTotalAsync());
    }

    [Fact]
    public async Task 报价单转PI_空请求路径_合法实时引用_生成草稿并完成来源报价单()
    {
        Guard();
        var quotation = await _fixture.SeedQuotationAsync(_fixture.CustomerId,
            _fixture.ProductId, DocumentStatus.Approved);
        await using var db = _fixture.CreateDbContext();

        var result = await NewQuotationController(db, _fixture.PrivilegedUserId, null)
            .ToProformaInvoice(quotation.Id);

        Assert.IsType<OkObjectResult>(result);
        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.Include(o => o.Details)
            .SingleAsync(o => o.QuotationId == quotation.Id);
        Assert.Equal(_fixture.CustomerId, pi.CustomerId);
        Assert.Equal(_fixture.ProductId, pi.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed, (await verify.Quotations.AsNoTracking()
            .SingleAsync(o => o.Id == quotation.Id)).Status);
    }
}
