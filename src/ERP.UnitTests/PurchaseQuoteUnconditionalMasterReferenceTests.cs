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
/// ERP-491：供应商比价（<c>api/purchase/quotes</c>）写入与采购承诺的**实时主数据引用**复核
/// **与调用形状完全无关**的单元测试（纯内存库 <see cref="TestDbFactory"/> + 真实既有授权，
/// 不连接 SQL Server、不执行 DDL / 部署脚本、不使用生产数据）。
/// <list type="number">
/// <item><b>唯一规则不变</b>：沿用 ERP-476 的 <see cref="PurchaseQuoteMasterReferenceRules"/> ——
/// 可空草稿（显式引用留空）照常放行且不臆造主数据；显式零 / 负 / 悬空 / 已删除 / 已停用引用受控拒绝；</item>
/// <item><b>调用形状无关</b>：同一载荷 + 同一真实启用身份下，「空 <c>Request.Path</c>」与「已赋值
/// <c>Request.Path</c>」给出**完全相同**的结论与零写入，不再存在依据 <c>Request.Path</c> 的方法级豁免；</item>
/// <item><b>承诺前复核</b>：新增 / 修改（含选择承诺）先于发号与写入拒绝；带入预填 / 单行转采购订单 /
/// 批次转采购订单在既有来源行锁内复核来源实时引用，采购承诺前归属客户被删除即原子拒绝，
/// 零表头 / 明细 / 来源 / 状态 / 占号变更；批次内任一组失效即**整批**拒绝；</item>
/// <item><b>无请求上下文</b>：完全未绑定 <c>HttpContext</c> 的进程内直调由既有实时授权先行 fail closed
/// （未认证），同样零写入零发号 —— 绝不把「无请求管线」当作主数据 / 授权豁免。</item>
/// </list>
/// <para>安全口径：不读取 appsettings / .env / 生产凭据，不执行 drop / reset，也不新增任何权限 / 菜单 / 角色。</para>
/// </summary>
public class PurchaseQuoteUnconditionalMasterReferenceTests
{
    private const string QuoteRoute = "/api/purchase/quotes";

    // ==================== 脚手架：真实请求形状 / 空请求路径形状 / 无请求上下文 ====================

    /// <summary>
    /// 真实控制器：<paramref name="path"/> 为 <c>null</c> 时身份存在但 <c>Request.Path</c> 无值
    /// （空请求路径形状）；ERP-491 后两种形状必须给出完全相同的实时主数据引用结论。
    /// </summary>
    private static PurchaseQuoteController NewController(ErpDbContext db, long userId, string? path)
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>完全未绑定 <c>HttpContext</c> 的进程内直调（无请求上下文形状）。</summary>
    private static PurchaseQuoteController NoContextController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

    // ==================== 种子 ====================

    private static void SeedNumberRule(ErpDbContext db, long current)
        => db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseOrder,
            RuleCode = $"PO-491-{Guid.NewGuid():N}",
            RuleName = "ERP491 采购订单号",
            Prefix = "PO",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = current
        });

    private static long PurchaseOrderSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.PurchaseOrder && !r.IsDeleted)
            .Sum(r => r.CurrentSequence);

    private static BaseSupplier SeedSupplier(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-491-{Guid.NewGuid():N}", SupplierName = "ERP491 供应商",
            Status = status, IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        return supplier;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-491-{Guid.NewGuid():N}", ProductName = "ERP491 商品", Unit = "PCS",
            Status = status, IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        return product;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-491-{Guid.NewGuid():N}", CustomerName = "ERP491 客户", Status = status,
            CreditStatus = "正常", DepositRatio = 30m, IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        return customer;
    }

    private static async Task SoftDeleteCustomerAsync(ErpDbContext db, long customerId)
    {
        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    // ==================== 比价行构造 ====================

    private static PurchaseQuote NewDraft(string quoteNo, long? customerId = null, long? supplierId = null,
        long? productId = null) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 10, 1),
        ProductId = productId,
        ProductName = "ERP491 商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 10m,
        SupplierId = supplierId,
        SupplierName = supplierId is > 0 ? $"ERP491 供应商 {supplierId}" : string.Empty,
        SupplierType = "档口",
        QuotePrice = 3m,
        TotalAmount = 30m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = PurchaseQuoteMutationRules.PendingStatus,
        CustomerId = customerId,
        CustomerName = "ERP491 客户快照",
        Remark = "ERP491_TEST"
    };

    /// <summary>已选中 + 已批准的比价行（可承诺转采购订单的来源；审批决定 append-only）。</summary>
    private static PurchaseQuote SeedConvertibleQuote(ErpDbContext db, string quoteNo, long customerId,
        long supplierId, long productId)
    {
        var quote = NewDraft(quoteNo, customerId, supplierId, productId);
        quote.Status = PurchaseQuoteConversion.SelectedStatus;
        quote.IsSelected = true;
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();

        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId,
            SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = 1L,
            DecidedByName = "审批人",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
        });
        db.SaveChanges();
        return quote;
    }

    // ==================== 1. 草稿写入：可空语义保留 + 显式引用复核（与调用形状无关） ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 草稿写入_显式引用全部留空_空路径与真实路径一致放行且不臆造主数据(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var draft = NewDraft("PQ-491-DRAFT-NULL");

        var ok = Assert.IsType<OkObjectResult>(await NewController(db, userId, path).Create(draft));
        var created = Assert.IsType<ApiResponse<PurchaseQuote>>(ok.Value).Data!;

        Assert.True(created.Id > 0);
        Assert.Null(created.SupplierId);
        Assert.Null(created.ProductId);
        Assert.Null(created.CustomerId);
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, created.Status);
        // 既有可空草稿语义：不要求主数据链接，也绝不臆造 / 回填任何主数据行。
        Assert.Empty(db.BaseSuppliers);
        Assert.Empty(db.BaseProducts);
        Assert.Empty(db.BaseCustomers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 草稿写入_显式零引用_空路径与真实路径同样参数拒绝且零写入(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var draft = NewDraft("PQ-491-DRAFT-ZERO", supplierId: 0L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId, path).Create(draft));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseQuoteMasterReferenceRules.SupplierRequiredText, ex.Message);
        Assert.Empty(db.PurchaseQuotes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 草稿写入_悬空归属客户_空路径与真实路径同样不存在拒绝且零写入(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var draft = NewDraft("PQ-491-DRAFT-DANGLING", customerId: 9_491_001L);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId, path).Create(draft));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        Assert.Empty(db.PurchaseQuotes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 草稿写入_合法实时主数据_空路径与真实路径一致放行并原样落库(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        db.SaveChanges();

        var draft = NewDraft("PQ-491-DRAFT-LIVE", customer.Id, supplier.Id, product.Id);

        var ok = Assert.IsType<OkObjectResult>(await NewController(db, userId, path).Create(draft));
        var created = Assert.IsType<ApiResponse<PurchaseQuote>>(ok.Value).Data!;

        Assert.Equal(supplier.Id, created.SupplierId);
        Assert.Equal(product.Id, created.ProductId);
        Assert.Equal(customer.Id, created.CustomerId);
        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(supplier.Id, stored.SupplierId);
        Assert.Equal(customer.Id, stored.CustomerId);
    }


    // ==================== 2. 选择承诺：主数据失效同样先于写入拒绝 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 选择承诺_归属客户已删除_空路径与真实路径同样拒绝且原行不变(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        db.SaveChanges();

        var seeded = NewDraft("PQ-491-SELECT", customer.Id, supplier.Id, product.Id);
        db.PurchaseQuotes.Add(seeded);
        db.SaveChanges();

        await SoftDeleteCustomerAsync(db, customer.Id);   // 选择承诺前归属客户被删除

        var proposed = NewDraft("PQ-491-SELECT", customer.Id, supplier.Id, product.Id);
        proposed.IsSelected = true;
        proposed.Status = PurchaseQuoteMutationRules.SelectedStatus;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId, path)
            .Update(seeded.Id, proposed));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, stored.Status);   // 绝不进入已选中承诺
        Assert.False(stored.IsSelected);
    }

    // ==================== 3. 采购承诺：带入预填 / 单行 / 批次 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 带入预填_来源商品引用缺失_空路径与真实路径同样拒绝且零发号(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var supplier = SeedSupplier(db);
        var customer = SeedCustomer(db);
        SeedNumberRule(db, 7);
        db.SaveChanges();

        // 手工来源行（ProductId 留空）：草稿写入合法，采购承诺必须显式引用商品 —— 绝不静默零占位。
        var quote = NewDraft("PQ-491-PREFILL", customer.Id, supplier.Id);
        quote.Status = PurchaseQuoteConversion.SelectedStatus;
        quote.IsSelected = true;
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId, path)
            .OrderPrefill(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(7L, PurchaseOrderSequence(db));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 单行转采购订单_承诺前归属客户失效_空路径与真实路径同样零写入零发号(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        SeedNumberRule(db, 7);
        db.SaveChanges();

        var quote = SeedConvertibleQuote(db, "PQ-491-CONV", customer.Id, supplier.Id, product.Id);
        await SoftDeleteCustomerAsync(db, customer.Id);   // 选中 / 批准后归属客户被撤回

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId, path)
            .ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        Assert.Equal(7L, PurchaseOrderSequence(db));                        // 绝不消耗采购单号
        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
    }


    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 单行转采购订单_合法实时主数据_空路径与真实路径一致正向落库并留痕(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        SeedNumberRule(db, 7);
        db.SaveChanges();

        var quote = SeedConvertibleQuote(db, "PQ-491-CONV-OK", customer.Id, supplier.Id, product.Id);

        var ok = Assert.IsType<OkObjectResult>(await NewController(db, userId, path).ToPurchaseOrder(quote.Id));

        var data = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Data!;
        var order = await db.PurchaseOrders.Include(o => o.Details).SingleAsync(o => o.Id == data.Id);
        Assert.Equal(supplier.Id, order.SupplierId);
        Assert.Equal(customer.Id, order.OwningCustomerId);
        Assert.Equal(product.Id, order.Details.Single().ProductId);
        Assert.Equal(8L, PurchaseOrderSequence(db));                        // 合法承诺消耗一次单号
        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, source.Status);
        Assert.Equal(order.OrderNo, source.RefOrderNo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuoteRoute)]
    public async Task 批次转采购订单_任一组来源客户已失效_空路径与真实路径同样整批拒绝(string? path)
    {
        using var db = TestDbFactory.Create();
        var userId = PurchaseQuoteTestAuthority.SeedAuthorizedUser(db);
        var supplierA = SeedSupplier(db);
        var supplierB = SeedSupplier(db);
        var product = SeedProduct(db);
        var customerA = SeedCustomer(db);
        var customerB = SeedCustomer(db);
        SeedNumberRule(db, 5);
        db.SaveChanges();

        SeedConvertibleQuote(db, "PQ-491-BATCH", customerA.Id, supplierA.Id, product.Id);
        SeedConvertibleQuote(db, "PQ-491-BATCH", customerB.Id, supplierB.Id, product.Id);
        await SoftDeleteCustomerAsync(db, customerB.Id);   // 仅一组失效

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId, path)
            .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = "PQ-491-BATCH" }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        // 一整批受控拒绝：绝不生成部分采购订单 / 部分批次组，绝不消耗采购单号，绝不改写任何来源状态。
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        Assert.Equal(5L, PurchaseOrderSequence(db));
        var stored = db.PurchaseQuotes.AsNoTracking().OrderBy(q => q.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, q => Assert.Equal(PurchaseQuoteConversion.SelectedStatus, q.Status));
        Assert.All(stored, q => Assert.Equal(string.Empty, q.RefOrderNo ?? string.Empty));
    }


    // ==================== 4. 无请求上下文：授权先行 fail closed ====================

    [Fact]
    public async Task 无HttpContext的进程内直调_逐入口授权fail_closed且零写入零发号()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var customer = SeedCustomer(db);
        SeedNumberRule(db, 3);
        db.SaveChanges();
        var quote = SeedConvertibleQuote(db, "PQ-491-NOCONTEXT", customer.Id, supplier.Id, product.Id);
        var ctl = NoContextController(db);

        var create = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewDraft("PQ-491-NOCONTEXT-NEW", customer.Id, supplier.Id, product.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, create.Code);
        var prefill = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(quote.Id));
        Assert.Equal(ErrorCodes.Unauthorized, prefill.Code);
        var toOrder = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));
        Assert.Equal(ErrorCodes.Unauthorized, toOrder.Code);
        var batch = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = "PQ-491-NOCONTEXT" }));
        Assert.Equal(ErrorCodes.Unauthorized, batch.Code);

        // 绝不把「无请求管线」当作授权 / 主数据豁免：零写入、零发号、来源状态不变。
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(3L, PurchaseOrderSequence(db));
        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, stored.Status);
        Assert.Equal(string.Empty, stored.RefOrderNo ?? string.Empty);
    }

    // ==================== 5. 源码契约 ====================

    [Fact]
    public void 源码契约_主数据复核与调用形状无关_无路径豁免且入口无条件接入()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteController.cs");
        Assert.DoesNotContain("RequiresLiveMasterValidation", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Request.Path.HasValue", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteMasterReferenceRules.EnsureComparisonReferencesAsync", controller);
        Assert.Contains("PurchaseQuoteMasterReferenceRules.IsSelectionCommitment", controller);
        // 承诺复核唯一入口：写入承诺级（1）+ 带入预填（1）+ 单行转单（1）+ 批次转单（1）全部无条件调用。
        Assert.Equal(4, CountOccurrences(controller,
            "await PurchaseQuoteConversion.EnsureSourceMasterReferencesAsync("));

        var conversion = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteConversion.cs");
        Assert.Contains("PurchaseQuoteMasterReferenceRules.EnsureCommitmentReferencesAsync", conversion);
        Assert.DoesNotContain("RequiresLiveMasterValidation", conversion, StringComparison.Ordinal);

        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseQuoteMasterReferenceRules.cs");
        Assert.DoesNotContain("SaveChangesAsync", rules, StringComparison.Ordinal);   // 只读判定，不落库

        var doc = ReadSource("docs", "purchase-quote-unconditional-master-validation.md");
        Assert.Contains("PurchaseQuoteMasterReferenceRules", doc);
        Assert.Contains(@"(localdb)\NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
        Assert.Contains("ProductId = 0", doc);
    }


    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray()));
    }
}

