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
/// ERP-473 形式发票 PI「实时主数据引用」护栏单元测试（纯内存 + 真实既有授权，直接实例化控制器，
/// 不连接 SQL Server、不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一规则</b>：可选客户（填写必须存在 / 未删除 / 启用；留空跳过）、可选业务员（填写必须存在 / 未删除 /
/// 在职 / 业务员；历史悬空 Id 保留手工快照）、明细商品（留空 = 手工快照行；填写必须为正整数，商品资料一次有界批量
/// 查询；已删除 / 已停用拒绝；无对应商品行时自带受界商品文本的手工快照行保留、纯目录引用按缺失引用拒绝）；</item>
/// <item><b>控制器行为（HTTP 请求管线）</b>：新增 / 修改在单号预约与任何写入之前拒绝失效引用且零写入 / 零占号；
/// 提交 / 审核在 PI 来源行锁内重查引用，草稿保存后被删除 / 停用即原子拒绝、状态不变；转销售订单在 PI 行锁内拒绝
/// 失效引用且零订单 / 零占号 / 来源状态不变；报价单转 PI 在报价单行锁内拒绝失效草稿引用且零 PI / 零占号 /
/// 来源报价单状态不变；历史读取照原样可读、不被回填；</item>
/// <item><b>授权前置与非披露</b>：写入授权先于主数据复核，缺菜单的受限账号即便提交失效引用也返回同一受控权限错误；</item>
/// <item><b>调用形状无关（ERP-490）</b>：实时主数据引用复核不再依据请求路径 / 请求上下文是否存在 ——
/// 进程内直调与真实 HTTP 写入请求同一口径，失效引用一律受控拒绝且零写入 / 零占号，
/// 不存在任何请求形状 / 环境 / 测试旁路。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class ProformaInvoiceMasterReferenceTests
{
    private const string PiRoute = "/api/sales/proforma-invoices";
    private const string QuotationRoute = "/api/sales/quotations";

    private const long CustomerA = 957301L;
    private const long CustomerB = 957302L;
    private const long EmployeeA = 957201L;
    private const long ProductA = 957401L;
    private const long ProductB = 957402L;

    // ==================== 脚手架 ====================

    /// <summary>绑定真实 HTTP 请求路径（MVC 形状）的真实 PI 控制器：护栏按真实写入请求执行。</summary>
    private static ProformaInvoiceController HttpPiController(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(userId, PiRoute) }
        };

    /// <summary>绑定真实 HTTP 请求路径（MVC 形状）的真实报价单控制器。</summary>
    private static QuotationController HttpQuotationController(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContextFor(userId, QuotationRoute) }
        };

    /// <summary>进程内直调控制器（无 HTTP 请求路径）：ERP-490 后与真实写入请求**同一口径**执行实时主数据复核。</summary>
    private static ProformaInvoiceController InProcessController(ErpDbContext db, long userId)
    {
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static DefaultHttpContext HttpContextFor(long? userId, string path)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = path;
        return http;
    }

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
            CustomerCode = $"C-473-{Guid.NewGuid():N}",
            CustomerName = "ERP473 客户",
            Status = status,
            CreditStatus = "正常",
            DepositRatio = 30m,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, int status = 1, bool deleted = false,
        bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"E-473-{Guid.NewGuid():N}",
            EmployeeName = "ERP473 业务员",
            IsSalesman = isSalesman,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-473-{Guid.NewGuid():N}",
            ProductName = "ERP473 商品",
            Unit = "PCS",
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static ProformaInvoiceDetail Line(long? productId, decimal quantity = 10m, decimal unitPrice = 100m,
        string? code = null, string? name = null, bool deleted = false)
        => new()
        {
            ProductId = productId,
            ProductCode = code ?? (productId is > 0 ? $"CODE-{productId}" : string.Empty),
            ProductName = name ?? (productId is > 0 ? "护栏商品" : string.Empty),
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = quantity * unitPrice,
            IsDeleted = deleted
        };

    /// <summary>直接落库的 PI 种子（不经写入契约，便于构造「草稿后被失效」的引用）。</summary>
    private static ProformaInvoice SeedPi(ErpDbContext db, string no, DocumentStatus status, long? customerId,
        long? salesmanId = null, params ProformaInvoiceDetail[] lines)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "ERP473 客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SalesmanId = salesmanId,
            QuotationId = 77L,
            QuotationNo = "QT-UPSTREAM-473",
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

    /// <summary>受限（非特权）账号：非系统内置角色、未授予任何菜单授权、未映射业务员 → 既有授权 fail closed。</summary>
    private static long SeedRestrictedUserWithoutMenu(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"erp473-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP473 受限账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleCode = $"R-473-{Guid.NewGuid():N}",
            RoleName = "ERP473 受限角色",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    // ==================== 1. 唯一规则：可选客户 ====================

    [Fact]
    public async Task 客户_未填写_保留未指定客户语义()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await ProformaInvoiceMasterReferenceRules.EnsureCustomerAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task 客户_非正整数_按参数错误拒绝(long customerId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureCustomerAvailableAsync(db, customerId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceMasterReferenceRules.CustomerRequiredText, ex.Message);
    }

    [Fact]
    public async Task 客户_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedCustomer(db, deleted: true);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureCustomerAvailableAsync(db, 999_473L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureCustomerAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);
    }

    [Fact]
    public async Task 客户_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var disabled = SeedCustomer(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureCustomerAvailableAsync(db, disabled.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 客户_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);

        var resolved = await ProformaInvoiceMasterReferenceRules.EnsureCustomerAvailableAsync(db, customer.Id);

        Assert.NotNull(resolved);
        Assert.Equal(customer.Id, resolved!.Id);
        Assert.Equal(customer.CustomerName, resolved.CustomerName);
    }

    // ==================== 2. 唯一规则：可选业务员 ====================

    [Fact]
    public async Task 业务员_未填写_跳过校验()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-2L)]
    public async Task 业务员_非正整数_按参数错误拒绝(long salesmanId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, salesmanId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceMasterReferenceRules.SalesmanRequiredText, ex.Message);
    }

    [Fact]
    public async Task 业务员_无对应员工行_保留既有手工快照不拒绝不改派()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, 999_473L));
    }

    [Fact]
    public async Task 业务员_已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db, deleted: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, employee.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 业务员_已离职或非业务员_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var left = SeedEmployee(db, status: 0);
        var notSalesman = SeedEmployee(db, isSalesman: false);

        var leftEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, left.Id));
        Assert.Equal(ErrorCodes.RuleConflict, leftEx.Code);
        Assert.Contains("已离职", leftEx.Message);

        var notSalesmanEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, notSalesman.Id));
        Assert.Equal(ErrorCodes.RuleConflict, notSalesmanEx.Code);
        Assert.Contains("未被标记为业务员", notSalesmanEx.Message);
    }

    [Fact]
    public async Task 业务员_合法在职业务员_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db);

        var resolved = await ProformaInvoiceMasterReferenceRules.EnsureSalesmanAvailableAsync(db, employee.Id);

        Assert.NotNull(resolved);
        Assert.Equal(employee.Id, resolved!.Id);
    }

    // ==================== 3. 唯一规则：明细商品 ====================

    [Fact]
    public async Task 明细_空集合_跳过校验()
    {
        using var db = TestDbFactory.Create();

        await ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db, null);
        await ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
            new List<ProformaInvoiceDetail>());
    }

    [Fact]
    public async Task 明细_留空或软删除行_按手工快照行跳过校验()
    {
        using var db = TestDbFactory.Create();

        // null = 手工商品快照行（既有受界文本语义）；软删除行不是写入行，二者都不产生引用判定。
        await ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
            new List<ProformaInvoiceDetail> { Line(null), Line(null, deleted: true) });

        Assert.True(ProformaInvoiceMasterReferenceRules.HasManualProductSnapshot(
            new ProformaInvoiceDetail { ProductCode = "P-1" }));
        Assert.True(ProformaInvoiceMasterReferenceRules.HasManualProductSnapshot(
            new ProformaInvoiceDetail { ProductName = "手工商品" }));
        Assert.False(ProformaInvoiceMasterReferenceRules.HasManualProductSnapshot(
            new ProformaInvoiceDetail { ProductCode = " ", ProductName = string.Empty }));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 明细_商品Id非正整数_按参数错误拒绝(long productId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<ProformaInvoiceDetail> { Line(productId) }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceMasterReferenceRules.ProductRequiredText, ex.Message);
    }

    [Fact]
    public async Task 明细_商品不存在_无自带文本按缺失引用拒绝_自带文本按手工快照保留()
    {
        using var db = TestDbFactory.Create();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<ProformaInvoiceDetail> { Line(999_473L, code: string.Empty, name: string.Empty) }));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        // 悬空 Id + 自带受界商品文本 = 自足的手工快照行（历史证据保留，绝不回填 / 改派）。
        await ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
            new List<ProformaInvoiceDetail> { Line(999_473L, code: "LEGACY-1", name: "历史手工商品") });
    }

    [Fact]
    public async Task 明细_商品已删除或已停用_分别按不存在与规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedProduct(db, deleted: true);
        var disabled = SeedProduct(db, status: 0);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<ProformaInvoiceDetail> { Line(deleted.Id) }));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<ProformaInvoiceDetail> { Line(disabled.Id) }));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);
        Assert.Contains("已停用", disabledEx.Message);
    }

    [Fact]
    public async Task 明细_合法商品_放行且软删除行不参与判定()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);

        await ProformaInvoiceMasterReferenceRules.EnsureDetailProductsAsync(db,
            new List<ProformaInvoiceDetail> { Line(product.Id), Line(999_473L, deleted: true) });
    }

    [Fact]
    public void 商品解析_一次有界批量查询契约_绝不逐行查库()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "ProformaInvoiceMasterReferenceRules.cs"));

        Assert.Contains(".Where(p => productIds.Contains(p.Id))", source);
        Assert.Contains(".ToDictionaryAsync(p => p.Id, ct)", source);
        // 批量投影之后不再有任何商品主数据查询（判定循环绝不逐行查库）。
        var queryStart = source.IndexOf("var products = await db.BaseProducts", StringComparison.Ordinal);
        Assert.True(queryStart >= 0);
        var afterQuery = source[(queryStart + "var products = await db.BaseProducts".Length)..];
        Assert.DoesNotContain("BaseProducts", afterQuery);
    }

    // ==================== 4. 唯一规则：完整复核顺序 ====================

    [Fact]
    public async Task 完整复核_客户优先_任一项失效即整体拒绝且不改写任何引用()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var pi = new ProformaInvoice
        {
            CustomerId = 999_473L,
            Details = new List<ProformaInvoiceDetail> { Line(product.Id) }
        };

        var master = await Assert.ThrowsAsync<BusinessException>(() =>
            ProformaInvoiceMasterReferenceRules.EnsureMasterReferencesAsync(db, pi));
        Assert.Equal(ErrorCodes.NotFound, master.Code);
        Assert.Contains("客户", master.Message);

        // 判定是纯只读投影：主数据行与 PI 内存对象都不被回填 / 改派。
        Assert.Equal($"CODE-{product.Id}", pi.Details[0].ProductCode);
        Assert.Equal(product.Id, pi.Details[0].ProductId);
        Assert.Equal(999_473L, pi.CustomerId);
        Assert.True(await db.BaseProducts.AnyAsync(p => p.Id == product.Id && p.Status == 1));
    }

    [Fact]
    public async Task 完整复核_合法可空引用与手工快照行_整体放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var employee = SeedEmployee(db);
        var product = SeedProduct(db);
        var pi = new ProformaInvoice
        {
            CustomerId = customer.Id,
            SalesmanId = employee.Id,
            Details = new List<ProformaInvoiceDetail> { Line(product.Id), Line(null), Line(999_473L, code: "LEGACY-2") }
        };

        await ProformaInvoiceMasterReferenceRules.EnsureMasterReferencesAsync(db, pi);
    }

    // ==================== 5. 控制器：新增（HTTP 请求管线） ====================

    [Fact]
    public async Task 新增_客户缺失_受控拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(999_473L, null, Line(ProductA))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("客户", ex.Message);
        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.ProformaInvoiceDetails);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 新增_客户已停用_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var disabled = SeedCustomer(db, status: 0);
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(disabled.Id, null, Line(ProductA))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task 新增_商品已停用_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var disabled = SeedProduct(db, status: 0);
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(customer.Id, null, Line(disabled.Id))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("商品", ex.Message);
        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task 新增_业务员已离职_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var left = SeedEmployee(db, status: 0);
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(customer.Id, left.Id, Line(product.Id))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("业务员", ex.Message);
        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task 新增_合法引用与手工快照行_正常创建且保留快照文本()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var employee = SeedEmployee(db);
        var product = SeedProduct(db);
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var result = await ctl.Create(NewPiBody(customer.Id, employee.Id,
            Line(product.Id), Line(null, code: "MANUAL-1", name: "手工商品")));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.ProformaInvoices.Include(o => o.Details).Single();
        Assert.StartsWith("PI", saved.PiNo);
        Assert.Equal(customer.Id, saved.CustomerId);
        Assert.Equal(employee.Id, saved.SalesmanId);
        var details = saved.Details.OrderBy(d => d.SortNo).ToList();
        Assert.Equal(2, details.Count);
        Assert.Equal(product.Id, details[0].ProductId);
        Assert.Null(details[1].ProductId);
        Assert.Equal("MANUAL-1", details[1].ProductCode);
        Assert.Equal("手工商品", details[1].ProductName);
    }

    // ==================== 6. 控制器：修改（PI 来源行锁内） ====================

    [Fact]
    public async Task 修改_客户缺失_原子拒绝且表头明细与状态零变更()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-UPD", DocumentStatus.Pending, customer.Id, null, Line(product.Id));
        var detailId = db.ProformaInvoiceDetails.AsNoTracking().Single().Id;
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(pi.Id, NewPiBody(999_473L, null, Line(product.Id, quantity: 99m))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var saved = db.ProformaInvoices.AsNoTracking().Single();
        Assert.Equal("PI-473-UPD", saved.PiNo);
        Assert.Equal(customer.Id, saved.CustomerId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(1000m, saved.TotalAmount);
        var detail = db.ProformaInvoiceDetails.AsNoTracking().Single();
        Assert.Equal(detailId, detail.Id);
        Assert.Equal(10m, detail.Quantity);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));   // 修改不消耗单号
    }

    [Fact]
    public async Task 修改_合法引用_正常改写()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-UPD-OK", DocumentStatus.Pending, customer.Id, null, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var result = await ctl.Update(pi.Id, NewPiBody(customer.Id, null, Line(product.Id, quantity: 20m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.ProformaInvoices.AsNoTracking().Single();
        Assert.Equal(2000m, saved.TotalAmount);
        Assert.Equal(20m, db.ProformaInvoiceDetails.AsNoTracking().Single().Quantity);
    }

    // ==================== 7. 控制器：提交 / 审核（PI 来源行锁内重查） ====================

    [Fact]
    public async Task 提交_草稿后客户被删除_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-SUBMIT", DocumentStatus.Pending, customer.Id, null, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));
        customer.IsDeleted = true;                 // 草稿保存后被删除
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(pi.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 审核_草稿后商品被停用_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-APPROVE", DocumentStatus.Submitted, customer.Id, null, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));
        product.Status = 0;                        // 草稿保存后被停用
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(pi.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 提交_草稿后业务员离职_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var employee = SeedEmployee(db);
        var pi = SeedPi(db, "PI-473-SUBMIT-EMP", DocumentStatus.Pending, customer.Id, employee.Id,
            Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));
        employee.Status = 0;                       // 草稿保存后离职
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(pi.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 提交审核_合法引用_正常流转()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var employee = SeedEmployee(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-FLOW", DocumentStatus.Pending, customer.Id, employee.Id, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        Assert.IsType<OkObjectResult>(await ctl.Submit(pi.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(pi.Id));
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 提交_历史悬空业务员Id_保留手工快照不被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-LEGACY-EMP", DocumentStatus.Pending, customer.Id, 999_473L,
            Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        Assert.IsType<OkObjectResult>(await ctl.Submit(pi.Id));
        Assert.Equal(999_473L, db.ProformaInvoices.AsNoTracking().Single().SalesmanId);
    }

    // ==================== 8. 控制器：转销售订单（PI 来源行锁内） ====================

    [Fact]
    public async Task 转销售订单_客户已删除_原子拒绝且零订单零占号状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-ORDER-BAD", DocumentStatus.Approved, customer.Id, null, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));
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
    public async Task 转销售订单_商品已停用_原子拒绝且零订单零占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-ORDER-BAD2", DocumentStatus.Approved, customer.Id, null, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));
        product.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesOrders);
        Assert.Equal(3L, SequenceOf(db, DocumentType.SalesOrder));
    }

    [Fact]
    public async Task 转销售订单_合法引用_生成订单并完成来源PI()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var pi = SeedPi(db, "PI-473-ORDER-OK", DocumentStatus.Approved, customer.Id, null, Line(product.Id));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(pi.Id));
        var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value).Data!;

        Assert.StartsWith("SO", result.OrderNo);
        var order = db.SalesOrders.Include(o => o.Details).Single();
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal(customer.Id, order.CustomerId);
        Assert.Equal(product.Id, order.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(4L, SequenceOf(db, DocumentType.SalesOrder));
    }

    [Fact]
    public async Task 转销售订单守卫_转换前复核_与PI写入共用同一份实时主数据契约()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var pi = new ProformaInvoice { CustomerId = 999_473L, Details = { Line(product.Id) } };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderConversion.EnsureSourceMasterReferencesAsync(db, pi));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("客户", ex.Message);
    }

    // ==================== 9. 控制器：报价单 → PI（报价单来源行锁内） ====================

    [Fact]
    public async Task 报价单转PI_草稿客户已删除_原子拒绝零PI零占号且来源状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var quotation = SeedQuotation(db, "QT-473-TO-PI", DocumentStatus.Approved, customer.Id, product.Id);
        var ctl = HttpQuotationController(db, TestAuth.SeedPrivilegedUser(db));
        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 报价单转PI_草稿商品已停用_原子拒绝零PI零占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var quotation = SeedQuotation(db, "QT-473-TO-PI2", DocumentStatus.Approved, customer.Id, product.Id);
        var ctl = HttpQuotationController(db, TestAuth.SeedPrivilegedUser(db));
        product.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 报价单转PI_合法引用_生成PI并完成来源报价单()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var quotation = SeedQuotation(db, "QT-473-TO-PI-OK", DocumentStatus.Approved, customer.Id, product.Id);
        var ctl = HttpQuotationController(db, TestAuth.SeedPrivilegedUser(db));

        var result = await ctl.ToProformaInvoice(quotation.Id);

        Assert.IsType<OkObjectResult>(result);
        var pi = db.ProformaInvoices.Include(o => o.Details).Single();
        Assert.StartsWith("PI", pi.PiNo);
        Assert.Equal(customer.Id, pi.CustomerId);
        Assert.Equal(product.Id, pi.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single().Status);
    }

    // ==================== 10. 授权前置 / 进程内直调边界 / 历史读取 ====================

    [Fact]
    public async Task 授权前置_缺菜单受限账号提交失效引用_返回受控权限错误且非披露()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = HttpPiController(db, SeedRestrictedUserWithoutMenu(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(999_473L, null, Line(ProductA))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        // 非披露：不得因缺少授权而泄露主数据引用复核结论（不存在的客户）。
        Assert.DoesNotContain("不存在或已删除", ex.Message);
        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 进程内直调_无请求路径_失效引用同样受控拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = InProcessController(db, TestAuth.SeedPrivilegedUser(db));

        // ERP-490：不再有「无请求路径免实时主数据复核」旁路 —— 进程内直调与真实写入请求同一口径，
        // 失效客户在单号预约与任何写入之前受控拒绝（零写入、零占号、零来源 / 状态改动）。
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(999_473L, null, Line(null, code: "LEGACY-MANUAL"))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 进程内直调_无请求路径_真实启用身份与实时主数据_照常放行()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var customer = SeedCustomer(db);
        var ctl = InProcessController(db, TestAuth.SeedPrivilegedUser(db));

        // ERP-490：复核与调用形状无关 —— 进程内直调携带**真实启用身份**并引用**实时主数据行**时照常放行。
        var result = await ctl.Create(NewPiBody(customer.Id, null, Line(null, code: "LEGACY-MANUAL")));

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(db.ProformaInvoices);
    }

    [Fact]
    public async Task 历史读取_引用失效的历史PI_照原样可读且不被回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, deleted: true);
        var pi = SeedPi(db, "PI-473-HISTORY", DocumentStatus.Approved, customer.Id, 999_473L,
            Line(999_473L, code: "LEGACY-9", name: "历史手工商品"));
        var ctl = HttpPiController(db, TestAuth.SeedPrivilegedUser(db));

        var ok = Assert.IsType<OkObjectResult>(await ctl.GetById(pi.Id));
        var returned = Assert.IsType<ApiResponse<ProformaInvoice>>(ok.Value).Data!;

        Assert.Equal(customer.Id, returned.CustomerId);
        Assert.Equal(999_473L, returned.SalesmanId);
        Assert.Equal(999_473L, returned.Details.Single().ProductId);
        Assert.Equal("LEGACY-9", returned.Details.Single().ProductCode);
        Assert.Equal("历史手工商品", returned.Details.Single().ProductName);
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, DocumentStatus status, long? customerId,
        long? productId)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
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
        db.SaveChanges();
        return quotation;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }
            .Concat(segments).ToArray()));
}
