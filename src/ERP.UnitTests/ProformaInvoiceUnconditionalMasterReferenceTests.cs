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

namespace ERP.UnitTests;

/// <summary>
/// ERP-490：形式发票 PI 与「报价单 → PI」的**实时主数据引用**复核**与调用形状完全无关**的单元测试
/// （纯内存库 <see cref="TestDbFactory"/> + 真实既有授权；不连接 SQL Server、不执行 DDL / 部署脚本、不使用生产数据）。
/// <list type="number">
/// <item><b>唯一规则不变</b>：可选客户 / 可选业务员 / 明细商品沿用 ERP-473 的
/// <see cref="ProformaInvoiceMasterReferenceRules"/> —— 手工快照草稿（显式引用留空）放行，显式失效引用受控拒绝；</item>
/// <item><b>调用形状无关</b>：同一载荷 + 同一真实启用身份下，「空 <c>Request.Path</c>」与「已赋值
/// <c>Request.Path</c>」给出**完全相同**的结论（有界拒绝 / 放行），不存在请求路径 / 请求上下文旁路；</item>
/// <item><b>承诺前复核</b>：新增 / 修改先于单号预约拒绝，提交 / 审核 / 转销售订单 / 报价单转 PI 在既有来源行锁内
/// 复核已持久化引用，草稿后客户被删除 / 商品被停用即原子拒绝，零表头 / 明细 / 订单 / PI / 状态 / 占号变更；</item>
/// <item><b>接线</b>：控制器不再包含 <c>RequiresLiveMasterValidation</c> / <c>RequiresLivePiMasterValidation</c>，
/// 既定写入 / 提交 / 审核 / 转换入口无条件调用同一份实时主数据引用契约。</item>
/// </list>
/// <para>安全口径：不读取 appsettings / .env / 生产凭据，不执行 drop / reset，也不新增任何权限 / 菜单 / 角色。</para>
/// </summary>
public class ProformaInvoiceUnconditionalMasterReferenceTests
{
    private const string PiRoute = "/api/sales/proforma-invoices";
    private const string QuotationRoute = "/api/sales/quotations";

    // ==================== 脚手架：真实请求形状 vs 空请求路径形状 ====================

    /// <summary>真实 HTTP 写入请求形状（身份 + 已赋值 <c>Request.Path</c>）。</summary>
    private static ProformaInvoiceController HttpPiController(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(userId, PiRoute) }
        };

    /// <summary>空请求路径形状（身份存在但 <c>Request.Path</c> 无值）——ERP-490 后与真实请求同一口径。</summary>
    private static ProformaInvoiceController NullPathPiController(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(userId, null) }
        };

    /// <summary>真实 HTTP 写入请求形状（报价单控制器）。</summary>
    private static QuotationController HttpQuotationController(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(userId, QuotationRoute) }
        };

    /// <summary>空请求路径形状（报价单控制器）。</summary>
    private static QuotationController NullPathQuotationController(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(userId, null) }
        };

    private static DefaultHttpContext HttpContextFor(long? userId, string? path)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        if (path is not null) http.Request.Path = path;
        return http;
    }

    // ==================== 种子与工厂 ====================

    private static void SeedNumberRule(ErpDbContext db, DocumentType type, long current)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = type,
            RuleCode = $"R-{type}-{Guid.NewGuid():N}",
            RuleName = $"{type} 单号",
            Prefix = type == DocumentType.ProformaInvoice ? "PI" : "SO",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = current,
            IsDeleted = false
        });
        db.SaveChanges();
    }

    private static long SequenceOf(ErpDbContext db, DocumentType type)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == type).OrderBy(r => r.Id)
            .Select(r => r.CurrentSequence).First();

    private static BaseCustomer SeedCustomer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-490-{Guid.NewGuid():N}",
            CustomerName = "ERP490 客户",
            Status = status,
            CreditStatus = "正常",
            DepositRatio = 30m,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-490-{Guid.NewGuid():N}",
            ProductName = "ERP490 商品",
            Unit = "PCS",
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static ProformaInvoiceDetail Line(long? productId, string? code = null, string? name = null,
        decimal quantity = 10m, decimal unitPrice = 100m)
        => new()
        {
            ProductId = productId,
            ProductCode = code ?? (productId is > 0 ? $"CODE-{productId}" : string.Empty),
            ProductName = name ?? (productId is > 0 ? "ERP490 商品" : string.Empty),
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = quantity * unitPrice
        };

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

    private static ProformaInvoice SeedPi(ErpDbContext db, string no, DocumentStatus status, long? customerId,
        long? salesmanId = null, params ProformaInvoiceDetail[] lines)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "ERP490 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SalesmanId = salesmanId,
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
        pi.TotalAmountCny = pi.TotalAmount * 7.2m;
        pi.DepositAmount = pi.TotalAmount * 0.3m;
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, DocumentStatus status,
        long? customerId, long? productId)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "ERP490 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            Status = status,
            Details = new List<QuotationDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = productId, ProductCode = "QT-490", ProductName = "报价商品",
                    Unit = "PCS", Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        quotation.TotalAmount = 1000m;
        quotation.TotalAmountCny = 7200m;
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static async Task<BusinessException> DeniedAsync(Func<Task<IActionResult>> action)
        => await Assert.ThrowsAsync<BusinessException>(action);

    /// <summary>读取仓库内源文件（从测试输出目录向上定位解决方案根，与既有接线护栏测试同源）。</summary>
    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    // ==================== 1. 唯一规则：手工 / 来源草稿放行，显式失效拒绝 ====================

    [Fact]
    public async Task 规则_手工快照草稿_可选引用留空_放行且不臆造主数据()
    {
        using var db = TestDbFactory.Create();
        var pi = NewPiBody(null, null, Line(null, code: "MANUAL-490", name: "手工商品"));

        await ProformaInvoiceMasterReferenceRules.EnsureMasterReferencesAsync(db, pi);

        Assert.Null(pi.CustomerId);
        Assert.Null(pi.SalesmanId);
        Assert.Null(pi.Details.Single().ProductId);
        Assert.Empty(db.BaseCustomers);
    }

    [Fact]
    public async Task 规则_显式失效客户_受控拒绝且不写入任何主数据()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureMasterReferencesAsync(db,
                NewPiBody(999_490L, null, Line(null, code: "MANUAL-490"))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("客户", ex.Message);
        Assert.Empty(db.BaseCustomers);
    }

    // ==================== 2. 调用形状无关：新增 ====================

    [Fact]
    public async Task 新增_同一载荷与身份_空路径与真实路径_拒绝结论与零变更完全一致()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var user = TestAuth.SeedPrivilegedUser(db);
        var body = NewPiBody(999_490L, null, Line(null, code: "MANUAL-490"));

        var httpPath = await DeniedAsync(() => HttpPiController(db, user).Create(body));
        var nullPath = await DeniedAsync(() => NullPathPiController(db, user).Create(body));

        Assert.Equal(ErrorCodes.NotFound, nullPath.Code);
        Assert.Equal(httpPath.Code, nullPath.Code);
        Assert.Equal(httpPath.Message, nullPath.Message);
        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.ProformaInvoiceDetails);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 新增_空路径_合法实时客户与手工快照行_照常放行并消耗一次单号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var ctl = NullPathPiController(db, TestAuth.SeedPrivilegedUser(db));

        var result = await ctl.Create(NewPiBody(customer.Id, null,
            Line(null, code: "MANUAL-490", name: "手工商品")));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.ProformaInvoices.Include(o => o.Details).Single();
        Assert.Equal(customer.Id, saved.CustomerId);
        Assert.Null(saved.Details.Single().ProductId);
        Assert.Equal("MANUAL-490", saved.Details.Single().ProductCode);
        Assert.Equal(8L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    // ==================== 3. 调用形状无关：修改 / 提交 / 审核（锁内复核） ====================

    [Fact]
    public async Task 修改_空路径_显式失效客户_拒绝且原表头明细与单号不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-490-EDIT", DocumentStatus.Pending, customer.Id, null,
            Line(null, code: "MANUAL-490", name: "手工商品"));
        var detailId = pi.Details.Single().Id;
        var ctl = NullPathPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(pi.Id,
            NewPiBody(999_490L, null, Line(null, code: "MANUAL-491", name: "手工商品 2"))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var saved = db.ProformaInvoices.AsNoTracking().Single();
        Assert.Equal(customer.Id, saved.CustomerId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(detailId, db.ProformaInvoiceDetails.AsNoTracking().Single().Id);
        Assert.Equal("MANUAL-490", db.ProformaInvoiceDetails.AsNoTracking().Single().ProductCode);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 提交_空路径_草稿后客户被删除_锁内复核拒绝且状态与占号不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-490-SUBMIT", DocumentStatus.Pending, customer.Id, null,
            Line(null, code: "MANUAL-490", name: "手工商品"));
        var ctl = NullPathPiController(db, TestAuth.SeedPrivilegedUser(db));
        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 审核_空路径_草稿后商品被停用_锁内复核拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-490-APPROVE", DocumentStatus.Pending, customer.Id, null, Line(product.Id));
        var ctl = NullPathPiController(db, TestAuth.SeedPrivilegedUser(db));
        product.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(pi.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    // ==================== 4. 调用形状无关：转销售订单 / 报价单转 PI ====================

    [Fact]
    public async Task 转销售订单_空路径_草稿后客户被删除_拒绝零订单零占号且来源状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-490-TOSO", DocumentStatus.Approved, customer.Id, null,
            Line(null, code: "MANUAL-490", name: "手工商品"));
        var ctl = NullPathPiController(db, TestAuth.SeedPrivilegedUser(db));
        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SalesOrderDetails);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(3L, SequenceOf(db, DocumentType.SalesOrder));
    }

    [Fact]
    public async Task 报价单转PI_空路径_草稿后客户被删除_拒绝零PI零占号且来源状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var quotation = SeedQuotation(db, "QT-490-TOPI", DocumentStatus.Approved, customer.Id, product.Id);
        var ctl = NullPathQuotationController(db, TestAuth.SeedPrivilegedUser(db));
        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 报价单转PI_空路径_合法实时引用_生成草稿并完成来源报价单()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var quotation = SeedQuotation(db, "QT-490-TOPI-OK", DocumentStatus.Approved, customer.Id, product.Id);
        var ctl = NullPathQuotationController(db, TestAuth.SeedPrivilegedUser(db));

        var result = await ctl.ToProformaInvoice(quotation.Id);

        Assert.IsType<OkObjectResult>(result);
        var pi = db.ProformaInvoices.Include(o => o.Details).Single();
        Assert.Equal(customer.Id, pi.CustomerId);
        Assert.Equal(quotation.Id, pi.QuotationId);
        Assert.Equal(product.Id, pi.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single().Status);
        Assert.Equal(8L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    // ==================== 5. 接线：控制器不再依赖请求形状 ====================

    [Fact]
    public void 接线_实时主数据复核无条件执行_不再含请求路径豁免()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "ProformaInvoiceController.cs");
        Assert.DoesNotContain("RequiresLiveMasterValidation", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Request.Path.HasValue", controller, StringComparison.Ordinal);
        Assert.Contains("ProformaInvoiceMasterReferenceRules.EnsureMasterReferencesAsync(Db, pi)", controller,
            StringComparison.Ordinal);
        Assert.Contains("await SalesOrderConversion.EnsureSourceMasterReferencesAsync(Db, pi);", controller,
            StringComparison.Ordinal);

        var quotation = ReadSource("src", "ERP.Api", "Controllers", "QuotationController.cs");
        Assert.DoesNotContain("RequiresLivePiMasterValidation", quotation, StringComparison.Ordinal);
        Assert.DoesNotContain("Request.Path.HasValue", quotation, StringComparison.Ordinal);
        Assert.Contains("ProformaInvoiceMasterReferenceRules.EnsureMasterReferencesAsync(Db, pi)", quotation,
            StringComparison.Ordinal);
    }
}
