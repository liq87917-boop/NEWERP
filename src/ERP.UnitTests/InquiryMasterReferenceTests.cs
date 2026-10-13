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
/// ERP-475 询价单「实时主数据引用」护栏单元测试（纯内存库 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一规则</b>：必填客户（正整数的实时引用：存在 / 未删除 / 启用）、可选业务员（留空跳过；
/// 填写必须存在 / 未删除 / 在职 / 业务员）与明细商品（正整数；商品资料一次有界批量查询；已删除 / 已停用拒绝；
/// 无对应商品行时自带受界商品文本的手工快照行保留、纯目录引用按缺失引用拒绝；软删除行不参与）；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与任何写入之前拒绝失效引用且零写入 / 零占号；
/// 提交 / 审核在询价单来源行锁内复核实时引用，草稿保存后客户被删除 / 商品被停用即原子拒绝、状态不变；
/// 带入预填 / 转报价单在来源行锁内拒绝失效来源且不消耗报价单号、不落库、不改写来源状态；
/// 失效引用绝不触发自动改派或文本回填；</item>
/// <item><b>授权前置与非披露</b>：既有身份 / 菜单 / 客户范围授权先于主数据复核，缺菜单的受限账号即便提交
/// 失效引用也返回同一受控权限错误。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class InquiryMasterReferenceTests
{
    // ==================== 1. 必填客户 ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task 客户_非正整数_按参数错误拒绝(long customerId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureCustomerAvailableAsync(db, customerId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryMasterReferenceRules.CustomerRequiredText, ex.Message);
    }

    [Fact]
    public async Task 客户_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedCustomer(db, deleted: true);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureCustomerAvailableAsync(db, 424242L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureCustomerAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);
    }

    [Fact]
    public async Task 客户_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var disabled = SeedCustomer(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureCustomerAvailableAsync(db, disabled.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 客户_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);

        var resolved = await InquiryMasterReferenceRules.EnsureCustomerAvailableAsync(db, customer.Id);

        Assert.Equal(customer.Id, resolved.Id);
        Assert.Equal(customer.CustomerName, resolved.CustomerName);
    }

    // ==================== 2. 可选业务员 ====================

    [Fact]
    public async Task 业务员_未填写_保持未指定语义()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-9L)]
    public async Task 业务员_非正整数_按参数错误拒绝(long salesmanId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, salesmanId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryMasterReferenceRules.SalesmanRequiredText, ex.Message);
    }

    [Fact]
    public async Task 业务员_不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedEmployee(db, deleted: true);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, 424242L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);
    }

    [Fact]
    public async Task 业务员_已离职_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var left = SeedEmployee(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, left.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已离职", ex.Message);
    }

    [Fact]
    public async Task 业务员_非业务员员工_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db, isSalesman: false);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, employee.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("未被标记为业务员", ex.Message);
    }

    [Fact]
    public async Task 业务员_合法_返回实时员工()
    {
        using var db = TestDbFactory.Create();
        var employee = SeedEmployee(db);

        var resolved = await InquiryMasterReferenceRules.EnsureSalesmanAvailableAsync(db, employee.Id);

        Assert.NotNull(resolved);
        Assert.Equal(employee.Id, resolved!.Id);
        Assert.Equal(employee.EmployeeName, resolved.EmployeeName);
    }

    // ==================== 3. 明细商品（含手工快照行） ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public async Task 明细商品_非正整数_按参数错误拒绝(long productId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<InquiryDetail> { Line(productId) }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(InquiryMasterReferenceRules.ProductRequiredText, ex.Message);
    }

    [Fact]
    public async Task 明细商品_已删除或已停用_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedProduct(db, deleted: true);
        var disabled = SeedProduct(db, status: 0);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<InquiryDetail> { Line(deleted.Id, "已删除商品") }));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);
        Assert.Contains("不存在或已删除", deletedEx.Message);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<InquiryDetail> { Line(disabled.Id, "已停用商品") }));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);
        Assert.Contains("已停用", disabledEx.Message);
    }

    [Fact]
    public async Task 明细商品_无对应行但自带受界文本_按手工快照行保留()
    {
        using var db = TestDbFactory.Create();

        // 手工商品快照行：主数据行不存在，但自带受界商品文本（名称 / 规格 / 单位任一非空）——
        // 既有界面语义（明细以自带文本为准）保持不变，绝不回填、绝不改派。
        await InquiryMasterReferenceRules.EnsureDetailProductsAsync(db, new List<InquiryDetail>
        {
            new() { ProductId = 424242L, ProductName = "手工商品" },
            new() { ProductId = 424243L, Spec = "手工规格" },
            new() { ProductId = 424244L, Unit = "SET" },
        });
    }

    [Fact]
    public async Task 明细商品_无对应行且无自带文本_按缺失引用拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            InquiryMasterReferenceRules.EnsureDetailProductsAsync(db,
                new List<InquiryDetail> { new() { ProductId = 424242L, Quantity = 1m, UnitPrice = 1m } }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 明细_软删除行不参与校验且不注入()
    {
        using var db = TestDbFactory.Create();

        // 软删除行（含零 / 悬空商品 Id）不参与主数据复核：既有软删除语义保持，写入端由 ERP-474 拒绝注入。
        await InquiryMasterReferenceRules.EnsureDetailProductsAsync(db, new List<InquiryDetail>
        {
            new() { ProductId = 0, Quantity = 1m, UnitPrice = 1m, IsDeleted = true },
            new() { ProductId = 424242L, Quantity = 1m, UnitPrice = 1m, IsDeleted = true },
        });
    }

    [Fact]
    public async Task 明细商品_多行多商品_一次有界批量查询后全部通过()
    {
        using var db = TestDbFactory.Create();
        var first = SeedProduct(db);
        var second = SeedProduct(db);

        await InquiryMasterReferenceRules.EnsureDetailProductsAsync(db, new List<InquiryDetail>
        {
            Line(first.Id, "商品一"),
            Line(second.Id, "商品二"),
            Line(first.Id, "商品一（重复引用行）"),
            new() { ProductId = 424242L, ProductName = "手工快照行" },
        });
    }

    [Fact]
    public void 规则_商品资料一次有界批量查询_绝不逐行查库()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Application", "Services",
            "InquiryMasterReferenceRules.cs"));

        // 商品只出现一次查询入口：去重集合 + 批量 ToDictionary（明细循环内没有任何 await 查询）。
        Assert.Equal(1, source.Split("db.BaseProducts", StringSplitOptions.None).Length - 1);
        Assert.Contains("var productIds = lines.Select(d => d.ProductId).Distinct().ToList();", source);
        Assert.Contains(".Where(p => productIds.Contains(p.Id))", source);
        Assert.Contains(".ToDictionaryAsync(p => p.Id, ct)", source);

        var batchIndex = source.IndexOf(".ToDictionaryAsync(p => p.Id, ct)", StringComparison.Ordinal);
        var loopStart = source.IndexOf("foreach (var line in lines)", batchIndex, StringComparison.Ordinal);
        var loopEnd = source.IndexOf("public static bool HasManualProductSnapshot", StringComparison.Ordinal);
        Assert.True(loopStart > 0 && loopEnd > loopStart);
        var loop = source[loopStart..loopEnd].Replace("\r\n", "\n");
        Assert.DoesNotContain("await", loop);
    }

    [Fact]
    public void 规则与边界文案_口径同源且不新增权限与回填()
    {
        Assert.Contains("实时主数据引用", InquiryMasterReferenceRules.RuleText);
        Assert.Contains("一次有界批量查询", InquiryMasterReferenceRules.RuleText);
        Assert.Contains("手工快照行", InquiryMasterReferenceRules.RuleText);
        Assert.Contains("不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权", InquiryMasterReferenceRules.BoundaryText);
        Assert.Contains("不被自动回填或修正", InquiryMasterReferenceRules.BoundaryText);
        Assert.Contains("既有询价单实时身份 / 菜单 / 客户数据范围授权仍然决定性", InquiryMasterReferenceRules.BoundaryText);
    }

    [Fact]
    public void 控制器接线_主数据复核先于单号与写入()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "InquiryController.cs");
        // 新增 / 修改 / 提交 / 审核 四处在写入 / 状态改写之前复核实时主数据引用。
        Assert.Equal(4, controller.Split("InquiryMasterReferenceRules.EnsureMasterReferencesAsync(Db, entity)",
            StringSplitOptions.None).Length - 1);

        var conversion = ReadSource("src", "ERP.Api", "Controllers", "InquiryQuotationConversion.cs");
        Assert.Contains("InquiryMasterReferenceRules", conversion);
        Assert.Contains("EnsureDetailProductsAsync(db, inquiry.Details)", conversion);
        Assert.Contains("EnsureCustomerAvailableAsync(db, inquiry.CustomerId)", conversion);
    }

    // ==================== 4. 新增 / 修改：零写入、零占号 ====================

    [Fact]
    public async Task 新增_失效客户_在单号预约与任何写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var rule = SeedNumberRule(db, DocumentType.Inquiry, "INQ", 7);
        var deleted = SeedCustomer(db, deleted: true);
        var disabled = SeedCustomer(db, status: 0);
        var ctl = NewController(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(424242L, details: Line(1L))));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("不存在或已删除", missing.Message);

        var softDeleted = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(deleted.Id, details: Line(1L))));
        Assert.Equal(ErrorCodes.NotFound, softDeleted.Code);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(disabled.Id, details: Line(1L))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);
        Assert.Contains("已停用", disabledEx.Message);

        var zero = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(0L, details: Line(1L))));
        Assert.Equal(ErrorCodes.InvalidParameter, zero.Code);

        Assert.Empty(db.Inquiries);
        Assert.Empty(db.InquiryDetails);
        Assert.Equal(7L, Sequence(db, rule.Id));            // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task 新增_失效业务员_在单号预约与任何写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var rule = SeedNumberRule(db, DocumentType.Inquiry, "INQ", 7);
        var customer = SeedCustomer(db);
        var left = SeedEmployee(db, status: 0);
        var notSalesman = SeedEmployee(db, isSalesman: false);
        var ctl = NewController(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, salesmanId: 424242L, details: Line(1L))));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var leftEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, salesmanId: left.Id, details: Line(1L))));
        Assert.Equal(ErrorCodes.RuleConflict, leftEx.Code);

        var notSalesmanEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, salesmanId: notSalesman.Id, details: Line(1L))));
        Assert.Equal(ErrorCodes.RuleConflict, notSalesmanEx.Code);

        var zero = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, salesmanId: 0L, details: Line(1L))));
        Assert.Equal(ErrorCodes.InvalidParameter, zero.Code);

        Assert.Empty(db.Inquiries);
        Assert.Equal(7L, Sequence(db, rule.Id));
    }

    [Fact]
    public async Task 新增_失效商品_在单号预约与任何写入之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var rule = SeedNumberRule(db, DocumentType.Inquiry, "INQ", 7);
        var customer = SeedCustomer(db);
        var deleted = SeedProduct(db, deleted: true);
        var disabled = SeedProduct(db, status: 0);
        var ctl = NewController(db);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, details: Line(deleted.Id, "已删除商品"))));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, details: Line(disabled.Id, "已停用商品"))));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);

        var zero = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id, details: Line(0L, "零商品 Id"))));
        Assert.Equal(ErrorCodes.InvalidParameter, zero.Code);

        var missingWithoutText = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(customer.Id,
                details: new InquiryDetail { ProductId = 424242L, Quantity = 1m, UnitPrice = 1m })));
        Assert.Equal(ErrorCodes.NotFound, missingWithoutText.Code);

        Assert.Empty(db.Inquiries);
        Assert.Empty(db.InquiryDetails);
        Assert.Equal(7L, Sequence(db, rule.Id));
    }

    [Fact]
    public async Task 修改_草稿后客户被停用_拒绝且原表头明细与单号不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, "INQ-EDIT-CUST", customer.Id, DocumentStatus.Pending, product.Id);
        var detailId = db.InquiryDetails.Single().Id;

        // 草稿保存后客户被停用（主数据失效）。
        customer.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Update(inquiry.Id,
            Draft(customer.Id, details: Line(product.Id))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);

        var stored = db.Inquiries.AsNoTracking().Include(i => i.Details).Single();
        Assert.Equal("INQ-EDIT-CUST", stored.InquiryNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(customer.Id, stored.CustomerId);
        var line = Assert.Single(stored.Details);
        Assert.Equal(detailId, line.Id);                      // 原明细未被替换 / 未被改写
        Assert.Equal(2m, line.Quantity);
    }

    [Fact]
    public async Task 修改_拟议商品已删除_拒绝且原证据不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var deleted = SeedProduct(db, deleted: true);
        var inquiry = SeedInquiry(db, "INQ-EDIT-PROD", customer.Id, DocumentStatus.Pending, product.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Update(inquiry.Id,
            Draft(customer.Id, details: Line(deleted.Id, "已删除商品"))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var stored = db.Inquiries.AsNoTracking().Include(i => i.Details).Single();
        Assert.Equal(product.Id, stored.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 修改_拟议客户不存在_拒绝且原归属不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-EDIT-MISS", customer.Id, DocumentStatus.Pending);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Update(inquiry.Id,
            Draft(424242L, details: Line(424242L, "手工商品"))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(customer.Id, db.Inquiries.AsNoTracking().Single().CustomerId);
    }

    // ==================== 5. 提交 / 审核 / 转报价单：锁内复核实时引用 ====================

    [Fact]
    public async Task 提交_草稿后客户被删除_原子拒绝且状态与单号不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var rule = SeedNumberRule(db, DocumentType.Inquiry, "INQ", 7);
        var ctl = NewController(db);
        Assert.IsType<OkObjectResult>(await ctl.Create(Draft(customer.Id, details: Line(product.Id))));
        var inquiryId = db.Inquiries.Single().Id;
        var consumed = Sequence(db, rule.Id);

        // 草稿保存后客户被软删除（主数据失效）。
        customer.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(inquiryId));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var stored = db.Inquiries.AsNoTracking().Include(i => i.Details).Single();
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Single(stored.Details);
        Assert.Equal(consumed, Sequence(db, rule.Id));      // 被拒绝的提交不再消耗单号
    }

    [Fact]
    public async Task 审核_草稿后商品被停用_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, "INQ-APPROVE-FAIL", customer.Id, DocumentStatus.Pending, product.Id);
        var ctl = NewController(db);
        Assert.IsType<OkObjectResult>(await ctl.Submit(inquiry.Id));

        // 提交后被停用（主数据失效）：审核拒绝，状态停在已提交。
        product.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(inquiry.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转报价_来源客户已删除_在占号与改写来源之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, "INQ-CVT-CUST", customer.Id, DocumentStatus.Approved, product.Id);
        var rule = SeedNumberRule(db, DocumentType.Quotation, "QT", 7);
        var ctl = NewController(db);

        customer.IsDeleted = true;
        db.SaveChanges();

        var direct = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToQuotation(inquiry.Id));
        Assert.Equal(ErrorCodes.NotFound, direct.Code);

        var prefill = await Assert.ThrowsAsync<BusinessException>(() => ctl.QuotationPrefill(inquiry.Id));
        Assert.Equal(ErrorCodes.NotFound, prefill.Code);

        Assert.Empty(db.Quotations);
        Assert.Empty(db.QuotationDetails);
        Assert.Equal(7L, Sequence(db, rule.Id));            // 被拒绝的转换绝不消耗报价单号
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转报价_来源业务员已离职_在占号与改写来源之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var salesman = SeedEmployee(db);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, "INQ-CVT-SALES", customer.Id, DocumentStatus.Approved, product.Id,
            salesman.Id);
        var rule = SeedNumberRule(db, DocumentType.Quotation, "QT", 7);

        salesman.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已离职", ex.Message);
        Assert.Empty(db.Quotations);
        Assert.Equal(7L, Sequence(db, rule.Id));
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转报价_合法来源与手工快照明细_正向生成报价单且不改写来源文本()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var salesman = SeedEmployee(db);
        // 手工商品快照行：悬空商品 Id + 自带受界文本（既有语义，绝不被拒绝 / 回填 / 改派）。
        var inquiry = SeedInquiry(db, "INQ-CVT-OK", customer.Id, DocumentStatus.Approved,
            productId: 424242L, salesmanId: salesman.Id);
        SeedNumberRule(db, DocumentType.Quotation, "QT", 7);

        var result = await NewController(db).ToQuotation(inquiry.Id);

        Assert.IsType<OkObjectResult>(result);
        var quotation = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(customer.Id, quotation.CustomerId);
        Assert.Equal(customer.CustomerName, quotation.CustomerName);
        Assert.Equal(salesman.Id, quotation.SalesmanId);
        Assert.Equal(salesman.EmployeeName, quotation.SalesmanName);
        var line = quotation.Details.Single();
        Assert.Equal(424242L, line.ProductId);
        Assert.Equal("询价商品", line.ProductName);
        Assert.Equal(DocumentStatus.Completed, db.Inquiries.AsNoTracking().Single().Status);

        // 来源文本保持原样：来源明细一行不改（不回填 / 不改派）。
        var sourceLine = db.InquiryDetails.AsNoTracking().Single();
        Assert.Equal(424242L, sourceLine.ProductId);
        Assert.Equal("询价商品", sourceLine.ProductName);
    }

    [Fact]
    public async Task 授权先于主数据_缺菜单账号提交失效引用仍返回同一权限错误()
    {
        using var db = TestDbFactory.Create();
        var rule = SeedNumberRule(db, DocumentType.Inquiry, "INQ", 7);
        var userId = SeedRestrictedUserWithoutMenu(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(Draft(424242L, details: Line(0L))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(InquiryAuthorizationRules.MenuDeniedText, ex.Message);   // 非披露：不泄露引用有效性
        Assert.Empty(db.Inquiries);
        Assert.Equal(7L, Sequence(db, rule.Id));
    }

    [Fact]
    public async Task 提交_当前客户范围被撤销_受控拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, customer) = SeedRestrictedOperator(db,
            InquiryAuthorizationRules.RequiredMenuCode);
        var inquiry = SeedInquiry(db, "INQ-SCOPE-REVOKED", customer.Id, DocumentStatus.Pending);

        // 既有实时范围被撤销（客户改派其他业务员）：即便主数据仍启用，提交也按范围拒绝且零改写。
        var stored = db.BaseCustomers.Single(c => c.Id == customer.Id);
        stored.EmpId = employee.Id + 1_000L;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, user.Id)
            .Submit(inquiry.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 读取_引用失效的历史询价单_原样只读且不回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db);
        var inquiry = SeedInquiry(db, "INQ-HISTORY", customer.Id, DocumentStatus.Completed, product.Id);

        // 主数据事后失效（客户 / 商品被软删除）：历史读取完全只读、不被自动修正。
        customer.IsDeleted = true;
        product.IsDeleted = true;
        db.SaveChanges();

        var result = await NewController(db).GetById(inquiry.Id);

        var response = Assert.IsType<ApiResponse<Inquiry>>(Assert.IsType<OkObjectResult>(result).Value);
        var data = response.Data!;
        Assert.Equal(customer.Id, data.CustomerId);
        Assert.Equal("询价商品", data.Details.Single().ProductName);
        Assert.Equal(product.Id, data.Details.Single().ProductId);
        Assert.Equal(DocumentStatus.Completed, data.Status);
    }

    // ==================== 6. 脚手架 ====================

    private static InquiryController NewController(ErpDbContext db)
        => NewController(db, TestAuth.SeedPrivilegedUser(db));

    private static InquiryController NewController(ErpDbContext db, long? userId)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static Inquiry Draft(long customerId, long? salesmanId = null, params InquiryDetail[] details) => new()
    {
        InquiryDate = DateTime.Today,
        CustomerId = customerId,
        SalesmanId = salesmanId,
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        ValidDays = 30,
        Status = DocumentStatus.Pending,
        Details = details.ToList()
    };

    /// <summary>询价明细行（默认自带受界商品文本 → 无对应商品行时按手工快照行保留）。</summary>
    private static InquiryDetail Line(long productId, string productName = "询价商品") => new()
    {
        ProductId = productId, ProductName = productName, Spec = "标准", Unit = "PCS",
        Quantity = 2m, UnitPrice = 5m, Amount = 10m
    };

    private static Inquiry SeedInquiry(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Pending, long productId = 1L, long? salesmanId = null)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no, InquiryDate = DateTime.Today, CustomerId = customerId,
            ContactPerson = "C", ContactPhone = "138", SalesmanId = salesmanId,
            Currency = Currency.USD, ExchangeRate = 7.2m, ValidDays = 30, Status = status,
            Details = new List<InquiryDetail> { Line(productId) }
        };
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    private static SysDocumentNumberRule SeedNumberRule(ErpDbContext db, DocumentType type,
        string prefix, long current)
    {
        var rule = new SysDocumentNumberRule
        {
            DocumentType = type, RuleCode = $"{prefix}-{Guid.NewGuid():N}", RuleName = $"{prefix} 单号",
            Prefix = prefix, DateFormat = "yyyyMMdd", SerialLength = 4, Separator = string.Empty,
            CurrentSequence = current, IsDeleted = false
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private static long Sequence(ErpDbContext db, long ruleId)
        => db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == ruleId).CurrentSequence;

    private static BaseCustomer SeedCustomer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-475-{Guid.NewGuid():N}", CustomerName = "ERP475 客户", Status = status,
            CreditStatus = "正常", DepositRatio = 30m, IsDeleted = deleted
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
            EmployeeCode = $"E-475-{Guid.NewGuid():N}", EmployeeName = "ERP475 业务员",
            IsSalesman = isSalesman, Status = status, IsDeleted = deleted
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-475-{Guid.NewGuid():N}", ProductName = "ERP475 商品", Unit = "PCS",
            Status = status, IsDeleted = deleted
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
            UserName = $"erp475-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP475 受限账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleCode = $"R-475-{Guid.NewGuid():N}", RoleName = "ERP475 受限角色", IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// 受限业务员操作员（既有身份 / 菜单 / 客户范围口径）：登录账号 = 员工编码（ERP-097 权威映射）、
    /// 客户归属该员工、按需授予既有功能菜单授权。
    /// </summary>
    private static (SysUser User, BaseEmployee Employee, BaseCustomer Customer) SeedRestrictedOperator(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"inq475-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{code}", CustomerName = "本人客户", Status = 1, CreditStatus = "正常",
            EmpId = employee.Id
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = code, RoleCode = $"R-{code}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return (user, employee, customer);
    }

    /// <summary>读取仓库内源文件（从测试输出目录向上定位，与既有护栏测试同源）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }
            .Concat(segments).ToArray()));

    /// <summary>读取仓库内源文件（按解决方案根定位，避免依赖输出目录层级）。</summary>
    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }
}






