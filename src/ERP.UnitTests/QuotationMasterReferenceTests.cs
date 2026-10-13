using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-472 报价单「实时主数据引用」护栏单元测试（纯内存 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一规则</b>：可选客户（填写必须存在 / 未删除 / 启用；留空跳过）、可选业务员（填写必须存在 / 未删除 /
/// 在职 / 业务员；历史悬空 Id 保留手工快照）、明细商品（留空 = 手工快照行；填写必须为正整数，商品资料一次有界批量
/// 查询；已删除 / 已停用拒绝；无对应商品行时自带受界商品文本的手工快照行保留、纯目录引用按缺失引用拒绝）；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与任何写入之前拒绝失效引用且零写入 / 零占号；提交 / 审核在报价单
/// 来源行锁内重查引用，草稿保存后被删除 / 停用即原子拒绝、状态不变；创建版本不把失效引用复制成新草稿；
/// 询价转报价在来源行锁内拒绝失效引用且不改写来源状态 / 不占号；历史读取 / 打印照原样可读、不被回填；</item>
/// <item><b>授权前置与非披露</b>：写入授权先于主数据复核，缺菜单的受限账号即便提交失效引用也返回同一受控权限错误。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class QuotationMasterReferenceTests
{
    // ==================== 1. 可选客户 ====================

    [Fact]
    public async Task 客户_未填写_保留未指定客户语义()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await QuotationMasterReferenceRules.EnsureCustomerAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task 客户_非正整数_按参数错误拒绝(long customerId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureCustomerAvailableAsync(db, customerId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationMasterReferenceRules.CustomerRequiredText, ex.Message);
    }

    [Fact]
    public async Task 客户_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedCustomer(db, deleted: true);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureCustomerAvailableAsync(db, 424242L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureCustomerAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);
    }

    [Fact]
    public async Task 客户_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var disabled = SeedCustomer(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureCustomerAvailableAsync(db, disabled.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 客户_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);

        var resolved = await QuotationMasterReferenceRules.EnsureCustomerAvailableAsync(db, customer.Id);

        Assert.NotNull(resolved);
        Assert.Equal(customer.Id, resolved!.Id);
        Assert.Equal(customer.CustomerName, resolved.CustomerName);
    }

    // ==================== 2. 可选业务员 ====================

    [Fact]
    public async Task 业务员_未填写_跳过校验()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 业务员_非正整数_按参数错误拒绝(long salesmanId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, salesmanId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationMasterReferenceRules.SalesmanRequiredText, ex.Message);
    }

    [Fact]
    public async Task 业务员_历史悬空Id_保留手工快照不拒绝()
    {
        using var db = TestDbFactory.Create();

        // 既有历史报价单可能引用已被清理的员工行：悬空 Id 按既有手工快照保留（不拒绝、不回填、不改派）。
        Assert.Null(await QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, 424242L));
    }

    [Fact]
    public async Task 业务员_已删除已停用或非业务员_按受控错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedEmployee(db, deleted: true);
        var disabled = SeedEmployee(db, status: 0);
        var notSalesman = SeedEmployee(db, isSalesman: false);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, disabled.Id));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);
        Assert.Contains("已离职", disabledEx.Message);

        var notSalesmanEx = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, notSalesman.Id));
        Assert.Equal(ErrorCodes.RuleConflict, notSalesmanEx.Code);
        Assert.Contains("业务员", notSalesmanEx.Message);
    }

    [Fact]
    public async Task 业务员_合法_返回在职业务员()
    {
        using var db = TestDbFactory.Create();
        var salesman = SeedEmployee(db);

        var resolved = await QuotationMasterReferenceRules.EnsureSalesmanAvailableAsync(db, salesman.Id);

        Assert.NotNull(resolved);
        Assert.Equal(salesman.Id, resolved!.Id);
        Assert.True(resolved.IsSalesman);
    }

    // ==================== 3. 明细商品 ====================

    [Fact]
    public async Task 明细_未填写商品Id_手工快照行原样支持()
    {
        using var db = TestDbFactory.Create();

        await QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
        {
            ManualLine("FREE-1", "自由文本商品"),
            new() { ProductId = null, ProductCode = string.Empty, ProductName = string.Empty, Quantity = 1m }
        });
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public async Task 明细_商品Id非正整数_按参数错误拒绝(long productId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
            {
                new() { ProductId = productId, Quantity = 1m }
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationMasterReferenceRules.ProductRequiredText, ex.Message);
    }

    [Fact]
    public async Task 明细_商品缺失且无自带文本_按缺失引用拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
            {
                new() { ProductId = 424242L, Quantity = 1m }
            }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 明细_商品缺失但自带受界快照文本_按手工快照行保留()
    {
        using var db = TestDbFactory.Create();

        // 报价单界面本就不回写商品 Id：历史 / 手工行的悬空 Id 与自带文本按手工快照行保留（不回填、不改派）。
        await QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
        {
            ManualLine("P-LEGACY", "历史手工商品"),
            new() { ProductId = 424242L, ProductCode = string.Empty, ProductName = "仅名称快照", Quantity = 1m }
        });
    }

    [Fact]
    public async Task 明细_商品已删除或已停用_按受控错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedProduct(db, deleted: true);
        var disabled = SeedProduct(db, status: 0);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
            {
                ManualLine("P-D", "已删除商品", deleted.Id)
            }));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
            {
                ManualLine("P-S", "已停用商品", disabled.Id)
            }));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);
        Assert.Contains("已停用", disabledEx.Message);
    }

    [Fact]
    public async Task 明细_软删除行不参与校验()
    {
        using var db = TestDbFactory.Create();

        await QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
        {
            new() { ProductId = 424242L, Quantity = 1m, IsDeleted = true }
        });
    }

    [Fact]
    public async Task 明细_多行多商品_批量解析后全部通过()
    {
        using var db = TestDbFactory.Create();
        var first = SeedProduct(db);
        var second = SeedProduct(db);

        await QuotationMasterReferenceRules.EnsureDetailProductsAsync(db, new List<QuotationDetail>
        {
            ManualLine("P-1", "商品一", first.Id),
            ManualLine("P-2", "商品二", second.Id),
            ManualLine("P-1B", "商品一（同商品重复行）", first.Id),
            ManualLine("FREE-1", "手工行")
        });
    }

    [Fact]
    public void 规则_商品资料一次有界批量查询_绝不逐行查库()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "QuotationMasterReferenceRules.cs"));

        // 商品只出现一次查询入口：去重集合 + 批量 ToDictionary（明细循环内没有任何 await 查询）。
        Assert.Equal(1, source.Split("db.BaseProducts", StringSplitOptions.None).Length - 1);
        Assert.Contains("var productIds = lines.Where(d => d.ProductId is > 0)", source);
        Assert.Contains(".Where(p => productIds.Contains(p.Id))", source);
        Assert.Contains(".ToDictionaryAsync(p => p.Id, ct)", source);

        var batchIndex = source.IndexOf(".ToDictionaryAsync(p => p.Id, ct)", StringComparison.Ordinal);
        var loopStart = source.IndexOf("foreach (var line in lines)", batchIndex, StringComparison.Ordinal);
        var loopEnd = source.IndexOf("public static bool HasManualProductSnapshot", StringComparison.Ordinal);
        Assert.True(loopStart > 0 && loopEnd > loopStart);
        var loop = source[loopStart..loopEnd].Replace("\r\n", "\n");
        Assert.DoesNotContain("await", loop);
    }

    // ==================== 4. 新增 / 修改：零写入、零占号 ====================

    [Fact]
    public async Task 新增_客户缺失_在单号预约与任何写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db, current: 7);
        var entity = Draft(Line("P-1", "商品 A", productId: null, quantity: 2m, unitPrice: 5m));
        entity.CustomerId = 424242L;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Create(entity));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.Quotations);
        Assert.Empty(db.QuotationDetails);
        Assert.Equal(7L, QuotationSequence(db));            // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task 新增_商品已停用_在单号预约与任何写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db, current: 7);
        var disabled = SeedProduct(db, status: 0);
        var entity = Draft(Line("P-S", "已停用商品", disabled.Id, 2m, 5m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Create(entity));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.Quotations);
        Assert.Empty(db.QuotationDetails);
        Assert.Equal(7L, QuotationSequence(db));
    }

    [Fact]
    public async Task 新增_业务员已离职_在单号预约与任何写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var disabled = SeedEmployee(db, status: 0);
        var entity = Draft(Line("P-1", "商品 A", productId: null, quantity: 1m, unitPrice: 1m));
        entity.SalesmanId = disabled.Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Create(entity));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Quotations);
    }

    [Fact]
    public async Task 新增_未指定引用与手工快照行_保持既有口径可用()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db, current: 7);
        var entity = Draft(Line("FREE-1", "自由文本商品", productId: null, quantity: 2m, unitPrice: 5m));

        Assert.IsType<OkObjectResult>(await NewController(db).Create(entity));

        var saved = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Null(saved.CustomerId);
        Assert.Null(saved.SalesmanId);
        Assert.Null(saved.Details.Single().ProductId);
        Assert.Equal(8L, QuotationSequence(db));            // 合法请求正常消耗单号
    }

    [Fact]
    public async Task 新增_商品悬空Id但自带受界文本_按手工快照行保存()
    {
        using var db = TestDbFactory.Create();
        var entity = Draft(Line("P-LEGACY", "历史手工商品", productId: 424242L, quantity: 3m, unitPrice: 4m));

        Assert.IsType<OkObjectResult>(await NewController(db).Create(entity));

        var saved = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(424242L, saved.Details.Single().ProductId);       // 原样保留，绝不回填 / 改派
        Assert.Equal("历史手工商品", saved.Details.Single().ProductName);
        Assert.Equal(12m, saved.TotalAmount);
    }

    [Fact]
    public async Task 修改_失效客户_原表头明细单号与合计全部不变()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-UPD", DocumentStatus.Pending,
            Line("P-1", "原商品", productId: null, quantity: 2m, unitPrice: 5m));
        var update = Draft(Line("P-2", "新商品", productId: null, quantity: 9m, unitPrice: 9m));
        update.CustomerId = 424242L;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).Update(quotation.Id, update));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var after = db.Quotations.AsNoTracking().Include(q => q.Details).Single();
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(quotation.QuotationNo, after.QuotationNo);
        Assert.Equal(quotation.TotalAmount, after.TotalAmount);
        Assert.Equal("原商品", after.Details.Single().ProductName);
    }

    // ==================== 5. 提交 / 审核：锁内重查引用 ====================

    [Fact]
    public async Task 提交_草稿保存后客户被删除_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-SUB", DocumentStatus.Pending,
            Line("P-1", "商品 A", productId: null, quantity: 2m, unitPrice: 5m), customer.Id);
        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(quotation.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.Quotations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 审核_草稿保存后商品被停用或业务员离职_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var salesman = SeedEmployee(db);
        var quotation = SeedQuotation(db, "QT-APP", DocumentStatus.Submitted,
            Line("P-1", "商品 A", product.Id, 2m, 5m), customerId: null, salesmanId: salesman.Id);
        product.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Approve(quotation.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.Quotations.AsNoTracking().Single().Status);

        // 商品恢复启用后业务员被停用：同样原子拒绝，状态仍不变。
        // （上一步的失败事务已回滚并清空变更跟踪器 → 这里重新读取权威行后再改动。）
        db.BaseProducts.Single(p => p.Id == product.Id).Status = 1;
        db.BaseEmployees.Single(e => e.Id == salesman.Id).Status = 0;
        db.SaveChanges();
        var salesmanEx = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Approve(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, salesmanEx.Code);
        Assert.Contains("已离职", salesmanEx.Message);
        Assert.Equal(DocumentStatus.Submitted, db.Quotations.AsNoTracking().Single().Status);
    }

    // ==================== 6. 创建版本：不把失效引用复制成新草稿 ====================

    [Fact]
    public async Task 创建版本_源客户已删除_拒绝且不新增版本不改源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var source = SeedQuotation(db, "QT-REV", DocumentStatus.Approved,
            Line("P-1", "商品 A", productId: null, quantity: 2m, unitPrice: 5m), customer.Id);
        customer.IsDeleted = true;
        db.SaveChanges();
        var before = db.Quotations.Count();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).CreateRevision(source.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(before, db.Quotations.Count());
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
    }

    // ==================== 7. 询价转报价：来源行锁内复核引用 ====================

    [Fact]
    public async Task 转报价_客户已停用_拒绝且来源状态单号与写入全部不变()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db, current: 7);
        var customer = SeedCustomer(db, status: 0);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, customer.Id, product.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => InquiryController(db).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
        Assert.Equal(7L, QuotationSequence(db));
    }

    [Fact]
    public async Task 转报价_商品已停用_拒绝且来源状态与写入不变()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db, current: 7);
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, status: 0);
        var inquiry = SeedInquiry(db, customer.Id, product.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => InquiryController(db).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
        Assert.Equal(7L, QuotationSequence(db));
    }

    [Fact]
    public async Task 转报价_合法引用_正常生成报价单并置来源已完成()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, customer.Id, product.Id);

        Assert.IsType<OkObjectResult>(await InquiryController(db).ToQuotation(inquiry.Id));

        var quotation = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(customer.Id, quotation.CustomerId);
        Assert.Equal(product.Id, quotation.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed, db.Inquiries.AsNoTracking().Single().Status);
    }

    // ==================== 8. 历史读取与授权前置 ====================

    [Fact]
    public async Task 历史报价单_引用失效_读取打印照原样且不被回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var quotation = SeedQuotation(db, "QT-LEGACY", DocumentStatus.Approved,
            Line("P-1", "商品 A", product.Id, 2m, 5m), customer.Id);
        customer.IsDeleted = true;
        product.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        var read = GetData<Quotation>(await ctl.GetById(quotation.Id));
        Assert.Equal(customer.Id, read.CustomerId);
        Assert.Equal(product.Id, read.Details.Single().ProductId);

        var print = GetData<Quotation>(await ctl.GetPrint(quotation.Id));
        Assert.Equal(product.Id, print.Details.Single().ProductId);
        Assert.Equal(10m, print.TotalAmount);
    }

    [Fact]
    public async Task 新增_受限账号缺报价单菜单_即便引用失效也返回同一权限错误且零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedRestrictedUserWithoutMenu(db);
        var entity = Draft(Line("P-1", "商品 A", productId: null, quantity: 1m, unitPrice: 1m));
        entity.CustomerId = 424242L;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(entity));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(QuotationAuthorizationRules.MenuDeniedText, ex.Message);
        Assert.Empty(db.Quotations);
    }

    // ==================== 工厂与种子数据 ====================

    private static QuotationController NewController(ErpDbContext db)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static QuotationController NewController(ErpDbContext db, long? userId)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static InquiryController InquiryController(ErpDbContext db)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static T GetData<T>(IActionResult action)
    {
        var response = Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(action).Value);
        return response.Data!;
    }

    private static void SeedQuotationNumberRule(ErpDbContext db, long current)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.Quotation,
            RuleCode = $"R-QT-{Guid.NewGuid():N}",
            RuleName = "报价单号",
            Prefix = "QT",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = current,
            IsDeleted = false
        });
        db.SaveChanges();
    }

    private static long QuotationSequence(ErpDbContext db) => db.SysDocumentNumberRules
        .AsNoTracking().Single(r => r.DocumentType == DocumentType.Quotation).CurrentSequence;

    private static Quotation Draft(params QuotationDetail[] details) => new()
    {
        QuotationDate = new DateTime(2026, 9, 1),
        CustomerName = "义乌客户（主数据引用测试）",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        Status = DocumentStatus.Pending,
        Details = details.ToList()
    };

    private static QuotationDetail Line(string code, string name, long? productId, decimal quantity,
        decimal unitPrice) => new()
    {
        ProductId = productId,
        ProductCode = code,
        ProductName = name,
        Spec = "标准",
        Unit = "PCS",
        Quantity = quantity,
        UnitPrice = unitPrice,
        Amount = quantity * unitPrice
    };

    private static QuotationDetail ManualLine(string code, string name, long? productId = null)
        => Line(code, name, productId, 1m, 1m);

    private static Quotation SeedQuotation(ErpDbContext db, string no, DocumentStatus status,
        QuotationDetail line, long? customerId = null, long? salesmanId = null)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "义乌客户（主数据引用测试）",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            SalesmanId = salesmanId,
            SalesmanName = salesmanId.HasValue ? "业务员 A" : string.Empty,
            Status = status,
            Details = new List<QuotationDetail> { line }
        };
        line.SortNo = 1;
        quotation.TotalAmount = line.Amount;
        quotation.TotalAmountCny = line.Amount * quotation.ExchangeRate;
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static Inquiry SeedInquiry(ErpDbContext db, long customerId, long productId)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = $"INQ-472-{Guid.NewGuid():N}"[..18],
            InquiryDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            ValidDays = 30,
            Status = DocumentStatus.Approved,
            Details = new List<InquiryDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "询价商品", Spec = "标准", Unit = "PCS",
                    Quantity = 2m, UnitPrice = 5m, Amount = 10m
                }
            }
        };
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-472-{Guid.NewGuid():N}",
            CustomerName = "ERP472 客户",
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
            EmployeeCode = $"E-472-{Guid.NewGuid():N}",
            EmployeeName = "ERP472 业务员",
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
            ProductCode = $"P-472-{Guid.NewGuid():N}",
            ProductName = "ERP472 商品",
            Unit = "PCS",
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    /// <summary>受限（非特权）账号：非系统内置角色且未授予任何菜单授权 → 既有菜单授权 fail closed。</summary>
    private static long SeedRestrictedUserWithoutMenu(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"erp472-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP472 受限账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleCode = $"R-472-{Guid.NewGuid():N}",
            RoleName = "ERP472 受限角色",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }
            .Concat(segments).ToArray()));
}
