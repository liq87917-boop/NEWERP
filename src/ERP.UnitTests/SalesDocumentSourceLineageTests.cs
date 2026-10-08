using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-403 报价单 / 形式发票 PI 普通表单保存（新增 / 修改 / 提交 / 审核）上游来源血缘护栏单元测试：
/// 权威解析（来源号规范化 / 异客户 / 已删除 / 无法解析的历史值）、实时授权与客户范围（来源 + 目标双菜单）、
/// 既有转换资格与唯一目标、历史来源不被静默清除与显式改绑、PI 修改沿用持久化链接，
/// 确定性锁语句与事务口径，以及控制器「先解析血缘、后预约单号与写入」的源码契约。
/// </summary>
public class SalesDocumentSourceLineageTests
{
    // ==================== 1. 锁语句 / 事务口径 ====================

    [Fact]
    public void 锁语句与确定性锁序_与ERP402_400转换共用同一把来源行锁()
    {
        Assert.Equal(InquiryMutationRules.InquiryRowLockSql, SalesDocumentSourceLineageRules.InquiryRowLockSql);
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql, SalesDocumentSourceLineageRules.QuotationRowLockSql);
        Assert.Equal(ProformaInvoiceMutationRules.PiRowLockSql,
            SalesDocumentSourceLineageRules.ProformaInvoiceRowLockSql);

        Assert.Contains("db_owner.Inquiries", SalesDocumentSourceLineageRules.InquiryRowLockSql);
        Assert.Contains("db_owner.Quotations", SalesDocumentSourceLineageRules.QuotationRowLockSql);
        Assert.Contains("来源行锁", SalesDocumentSourceLineageRules.LockOrderText);
        Assert.Contains("绝不反向获取", SalesDocumentSourceLineageRules.LockOrderText);

        // 与「询价单 → 报价单」直接转换共用同一把来源行锁（串行化口径同源）。
        Assert.Equal(SalesDocumentSourceLineageRules.LockOrderText,
            InquiryQuotationConversion.SourceLineageLockOrderText);
        // 来源 / 目标菜单编码与既有授权模块同源。
        Assert.Equal(QuotationAuthorizationRules.RequiredMenuCode, SalesDocumentSourceLineageRules.QuotationMenuCode);
        Assert.Equal(InquiryAuthorizationRules.RequiredMenuCode, SalesDocumentSourceLineageRules.InquiryMenuCode);
        Assert.Equal(ProformaInvoiceAuthorizationRules.RequiredMenuCode,
            SalesDocumentSourceLineageRules.ProformaInvoiceMenuCode);
    }

    [Fact]
    public async Task 事务与行锁_内存库等价无操作不阻断既有语义()
    {
        using var db = TestDbFactory.Create();
        Assert.False(SalesDocumentSourceLineageRules.IsRelationalProvider(db));
        Assert.True(await SalesDocumentSourceLineageRules.LockSourceRowAsync(
            db, SalesDocumentSourceKind.Inquiry, null));
        await using var transaction = await SalesDocumentSourceLineageRules.BeginWriteTransactionAsync(db);
        Assert.Null(transaction);
    }

    // ==================== 2. 纯归一化 / 来源合并 ====================

    [Fact]
    public void 来源合并_请求未给出Id时保留历史来源_绝不静默清除()
    {
        var change = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, 11L, null);

        Assert.Equal(11L, change.SourceId);
        Assert.True(change.IsClearingAttempt);
        Assert.False(change.IsExplicitChange);
        Assert.True(change.HasSource);
    }

    [Fact]
    public void 来源合并_显式给出不同来源判定为改绑_相同则保持未改动()
    {
        var rebind = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Quotation, 11L, 33L);
        Assert.True(rebind.IsExplicitChange);
        Assert.Equal(33L, rebind.SourceId);

        var unchanged = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Quotation, 11L, 11L);
        Assert.False(unchanged.IsExplicitChange);
        Assert.Equal(11L, unchanged.SourceId);

        var fresh = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, null, 5L);
        Assert.True(fresh.IsExplicitChange);
        Assert.Equal(5L, fresh.SourceId);

        var none = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, null, 0L);
        Assert.False(none.HasSource);
        Assert.False(none.IsExplicitChange);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 来源单号规范化_空白归一为空串(string? value)
        => Assert.Equal(string.Empty, SalesDocumentSourceLineageRules.NormalizeNo(value, 50));

    [Fact]
    public void 来源单号规范化_去首尾空白并按列宽截断()
    {
        Assert.Equal("INQ-1", SalesDocumentSourceLineageRules.NormalizeNo("  INQ-1 ", 50));
        Assert.Equal("AB", SalesDocumentSourceLineageRules.NormalizeNo("ABCD", 2));
        Assert.Null(SalesDocumentSourceLineageRules.NormalizeId(0));
        Assert.Null(SalesDocumentSourceLineageRules.NormalizeId(-3));
        Assert.Equal(7L, SalesDocumentSourceLineageRules.NormalizeId(7L));
    }

    // ==================== 3. 权威解析 ====================

    [Fact]
    public async Task 解析_未携带来源_显式未链接且保持有效()
    {
        using var db = TestDbFactory.Create();
        var lineage = await SalesDocumentSourceLineageRules.ResolveAsync(db,
            SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, null, null), 1L);

        Assert.True(lineage.IsUnlinked);
        Assert.False(lineage.IsUnresolvedLegacy);
        Assert.Null(lineage.SourceId);
        Assert.Equal(string.Empty, lineage.SourceNo);
    }

    [Fact]
    public async Task 解析_询价单来源_规范化来源号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "  INQ-LIN-1 ", customer.Id, DocumentStatus.Approved);

        var change = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, null, inquiry.Id);
        var lineage = await SalesDocumentSourceLineageRules.ResolveAsync(db, change, customer.Id);

        Assert.False(lineage.IsUnresolvedLegacy);
        Assert.Equal(inquiry.Id, lineage.SourceId);
        Assert.Equal("INQ-LIN-1", lineage.SourceNo);
        Assert.Equal(customer.Id, lineage.SourceCustomerId);
        Assert.NotNull(lineage.Inquiry);
    }

    [Fact]
    public async Task 解析_报价单来源_规范化来源号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "  QT-LIN-1 ", customer.Id, DocumentStatus.Approved);

        var change = SalesDocumentSourceLineageRules.ResolveChange(
            SalesDocumentSourceKind.Quotation, null, quotation.Id);
        var lineage = await SalesDocumentSourceLineageRules.ResolveAsync(db, change, customer.Id);

        Assert.Equal(quotation.Id, lineage.SourceId);
        Assert.Equal("QT-LIN-1", lineage.SourceNo);
        Assert.NotNull(lineage.Quotation);
    }

    [Fact]
    public async Task 解析_已删除来源_无效来源拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-DEL", customer.Id, DocumentStatus.Approved);
        inquiry.IsDeleted = true;
        db.SaveChanges();

        var change = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, null, inquiry.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesDocumentSourceLineageRules.ResolveAsync(db, change, customer.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已删除", ex.Message);
    }

    [Fact]
    public async Task 解析_异客户来源_跨客户链接拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-FOREIGN", foreign.Id, DocumentStatus.Approved);

        var change = SalesDocumentSourceLineageRules.ResolveChange(
            SalesDocumentSourceKind.Quotation, null, quotation.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesDocumentSourceLineageRules.ResolveAsync(db, change, own.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("跨客户", ex.Message);
    }

    [Fact]
    public async Task 解析_显式来源Id完全无法解析_按显式历史值原样保留()
    {
        using var db = TestDbFactory.Create();
        var change = SalesDocumentSourceLineageRules.ResolveChange(SalesDocumentSourceKind.Inquiry, null, 777L);
        var lineage = await SalesDocumentSourceLineageRules.ResolveAsync(db, change, 999999L);

        Assert.True(lineage.IsUnresolvedLegacy);
        Assert.False(lineage.IsUnlinked);
        Assert.Equal(777L, lineage.SourceId);
        Assert.Null(lineage.Inquiry);
    }

    // ==================== 4. 唯一目标 ====================

    [Fact]
    public async Task 唯一目标_同一询价单唯一化并排除本单与版本链()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-UNIQ", customer.Id, DocumentStatus.Approved);
        var quotation = SeedQuotation(db, "QT-UNIQ", customer.Id, DocumentStatus.Approved, inquiry.Id, "INQ-UNIQ");

        Assert.NotNull(await SalesDocumentSourceLineageRules.FindOtherTargetAsync(
            db, SalesDocumentSourceKind.Inquiry, inquiry.Id, null));
        Assert.Null(await SalesDocumentSourceLineageRules.FindOtherTargetAsync(
            db, SalesDocumentSourceKind.Inquiry, inquiry.Id, quotation.Id));

        // 版本链（RootQuotationId 指向本单）：合法共享同一来源，不算重复目标。
        SeedQuotation(db, "QT-UNIQ-R2", customer.Id, DocumentStatus.Pending, inquiry.Id, "INQ-UNIQ",
            rootQuotationId: quotation.Id);
        Assert.Null(await SalesDocumentSourceLineageRules.FindOtherTargetAsync(
            db, SalesDocumentSourceKind.Inquiry, inquiry.Id, quotation.Id));
    }

    [Fact]
    public async Task 唯一目标_同一报价单PI唯一化并可排除本单()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-UNIQ-PI", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-UNIQ", customer.Id, DocumentStatus.Approved, quotation.Id, "QT-UNIQ-PI");

        Assert.NotNull(await SalesDocumentSourceLineageRules.FindOtherTargetAsync(
            db, SalesDocumentSourceKind.Quotation, quotation.Id, null));
        Assert.Null(await SalesDocumentSourceLineageRules.FindOtherTargetAsync(
            db, SalesDocumentSourceKind.Quotation, quotation.Id, pi.Id));
    }

    // ==================== 5. 报价单新增 / 修改（来源询价单） ====================

    [Fact]
    public async Task 报价单新增_显式链接已审核询价单_以权威来源号落库且不改写来源状态()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-NEW-1", customer.Id, DocumentStatus.Approved);
        var ctl = PrivilegedQuotationController(db);

        // 提交文本故意伪造来源号：服务端一律按来源行规范化，绝不采信文本。
        var body = NewQuotationBody(customer.Id, inquiry.Id, inquiryNo: "FORGED-INQ-999");
        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var saved = db.Quotations.Single();
        Assert.Equal(inquiry.Id, saved.InquiryId);
        Assert.Equal("INQ-NEW-1", saved.InquiryNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(1000m, saved.TotalAmount);            // 服务端按 10 × 100 重算

        // 来源询价单状态与商业口径零改写（普通保存不是直接转换）。
        var source = db.Inquiries.Single(i => i.Id == inquiry.Id);
        Assert.Equal(DocumentStatus.Approved, source.Status);
    }

    [Fact]
    public async Task 报价单新增_未链接手工单据_完全有效()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var ctl = PrivilegedQuotationController(db);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewQuotationBody(customer.Id, null)));

        var saved = db.Quotations.Single();
        Assert.Null(saved.InquiryId);
        Assert.Equal(string.Empty, saved.InquiryNo);
    }

    [Fact]
    public async Task 报价单新增_来源未审核_原子拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-PEND", customer.Id, DocumentStatus.Pending);
        var ctl = PrivilegedQuotationController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(customer.Id, inquiry.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Quotations);
    }

    [Fact]
    public async Task 报价单新增_同一询价单已有实时报价单_重复目标拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-DUP", customer.Id, DocumentStatus.Approved);
        SeedQuotation(db, "QT-DUP", customer.Id, DocumentStatus.Pending, inquiry.Id, "INQ-DUP");
        var ctl = PrivilegedQuotationController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(customer.Id, inquiry.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复", ex.Message);
    }

    [Fact]
    public async Task 报价单修改_请求未给出来源Id_保留历史来源绝不静默清除()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-UPD-1", customer.Id, DocumentStatus.Approved);
        var quotation = SeedQuotation(db, "QT-UPD-1", customer.Id, DocumentStatus.Pending, inquiry.Id, "INQ-UPD-1");
        var ctl = PrivilegedQuotationController(db);

        Assert.IsType<OkObjectResult>(await ctl.Update(quotation.Id, NewQuotationBody(customer.Id, null)));

        var saved = db.Quotations.Single();
        Assert.Equal(inquiry.Id, saved.InquiryId);
        Assert.Equal("INQ-UPD-1", saved.InquiryNo);
    }

    [Fact]
    public async Task 报价单修改_显式改绑到另一可解析来源_改写为权威来源号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedInquiry(db, "INQ-RB-1", customer.Id, DocumentStatus.Approved);
        var second = SeedInquiry(db, "INQ-RB-2", customer.Id, DocumentStatus.Approved);
        var quotation = SeedQuotation(db, "QT-RB-1", customer.Id, DocumentStatus.Pending, first.Id, "INQ-RB-1");
        var ctl = PrivilegedQuotationController(db);

        Assert.IsType<OkObjectResult>(await ctl.Update(quotation.Id,
            NewQuotationBody(customer.Id, second.Id, inquiryNo: "伪造单号")));

        var saved = db.Quotations.Single();
        Assert.Equal(second.Id, saved.InquiryId);
        Assert.Equal("INQ-RB-2", saved.InquiryNo);
    }

    [Fact]
    public async Task 报价单修改_改绑到无法解析来源_拒绝且保留历史来源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-RB-3", customer.Id, DocumentStatus.Approved);
        var quotation = SeedQuotation(db, "QT-RB-3", customer.Id, DocumentStatus.Pending, inquiry.Id, "INQ-RB-3");
        var ctl = PrivilegedQuotationController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(quotation.Id, NewQuotationBody(customer.Id, 99999L)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("无法解析", ex.Message);

        var saved = db.Quotations.Single();
        Assert.Equal(inquiry.Id, saved.InquiryId);
        Assert.Equal("INQ-RB-3", saved.InquiryNo);
    }

    // ==================== 6. 报价单提交 / 审核 ====================

    [Fact]
    public async Task 报价单提交审核_未链接_既有流转口径不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-WF-1", customer.Id, DocumentStatus.Pending);
        var ctl = PrivilegedQuotationController(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(quotation.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(quotation.Id));
        Assert.Equal(DocumentStatus.Approved, db.Quotations.Single().Status);
    }

    [Fact]
    public async Task 报价单提交_已链接来源_持久化来源重查通过后正常流转()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-WF-1", customer.Id, DocumentStatus.Approved);
        var quotation = SeedQuotation(db, "QT-WF-2", customer.Id, DocumentStatus.Pending, inquiry.Id, "INQ-WF-1");
        var ctl = PrivilegedQuotationController(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(quotation.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(quotation.Id));
        Assert.Equal(DocumentStatus.Approved, db.Quotations.Single().Status);
    }

    [Fact]
    public async Task 报价单提交_来源已删除_冻结拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-WF-2", customer.Id, DocumentStatus.Approved);
        var quotation = SeedQuotation(db, "QT-WF-3", customer.Id, DocumentStatus.Pending, inquiry.Id, "INQ-WF-2");
        inquiry.IsDeleted = true;
        db.SaveChanges();
        var ctl = PrivilegedQuotationController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.Quotations.Single().Status);
    }

    // ==================== 7. 形式发票 PI 新增 / 修改 / 提交（来源报价单） ====================

    [Fact]
    public async Task PI新增_显式链接已审核报价单_以权威来源号落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-PI-1", customer.Id, DocumentStatus.Approved);
        var ctl = PrivilegedPiController(db);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPiBody(customer.Id, quotation.Id, "FORGED-QT")));

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(quotation.Id, saved.QuotationId);
        Assert.Equal("QT-PI-1", saved.QuotationNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);

        // 来源报价单状态零改写（普通保存不是「报价单 → PI」直接转换）。
        Assert.Equal(DocumentStatus.Approved, db.Quotations.Single().Status);
    }

    [Fact]
    public async Task PI新增_未链接手工单据_完全有效()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var ctl = PrivilegedPiController(db);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPiBody(customer.Id, null)));

        var saved = db.ProformaInvoices.Single();
        Assert.Null(saved.QuotationId);
        Assert.Equal(string.Empty, saved.QuotationNo);
    }

    [Fact]
    public async Task PI新增_来源报价单未审核_原子拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-PI-2", customer.Id, DocumentStatus.Pending);
        var ctl = PrivilegedPiController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(customer.Id, quotation.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task PI新增_同一报价单已有实时PI_重复目标拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-PI-3", customer.Id, DocumentStatus.Approved);
        SeedPi(db, "PI-DUP", customer.Id, DocumentStatus.Pending, quotation.Id, "QT-PI-3");
        var ctl = PrivilegedPiController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(customer.Id, quotation.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复", ex.Message);
    }

    [Fact]
    public async Task PI修改_沿用持久化来源_调用方改绑被忽略()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedQuotation(db, "QT-PI-4", customer.Id, DocumentStatus.Approved);
        var second = SeedQuotation(db, "QT-PI-5", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-UPD-1", customer.Id, DocumentStatus.Pending, first.Id, "QT-PI-4");
        var ctl = PrivilegedPiController(db);

        // 请求体伪造另一来源 Id：PI 修改沿用持久化链接，绝不改绑。
        Assert.IsType<OkObjectResult>(await ctl.Update(pi.Id, NewPiBody(customer.Id, second.Id, "QT-PI-5")));

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(first.Id, saved.QuotationId);
        Assert.Equal("QT-PI-4", saved.QuotationNo);
    }

    [Fact]
    public async Task PI提交_来源已删除_冻结拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-PI-6", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-WF-1", customer.Id, DocumentStatus.Pending, quotation.Id, "QT-PI-6");
        quotation.IsDeleted = true;
        db.SaveChanges();
        var ctl = PrivilegedPiController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.Single().Status);
    }

    // ==================== 8. 实时授权（既有权限，不新增授权） ====================

    [Fact]
    public async Task 报价单新增_受限账号缺询价单菜单_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-AUTH-1", customer.Id, DocumentStatus.Approved);
        // 只授予报价单菜单，未授予来源询价单菜单。
        var userId = SeedRestrictedUser(db, customer.Id, QuotationAuthorizationRules.RequiredMenuCode);
        var ctl = QuotationControllerFor(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(customer.Id, inquiry.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("询价单", ex.Message);
        Assert.Empty(db.Quotations);
    }

    [Fact]
    public async Task 报价单新增_受限账号越客户范围_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-AUTH-2", customer.Id, DocumentStatus.Approved);
        // 双菜单齐备但未分配该客户（范围外）。
        var userId = SeedRestrictedUser(db, null, QuotationAuthorizationRules.RequiredMenuCode,
            SalesDocumentSourceLineageRules.InquiryMenuCode);
        var ctl = QuotationControllerFor(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(customer.Id, inquiry.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.Quotations);
    }

    [Fact]
    public async Task 报价单新增_受限账号双菜单且在范围内_放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-AUTH-3", customer.Id, DocumentStatus.Approved);
        var userId = SeedRestrictedUser(db, customer.Id, QuotationAuthorizationRules.RequiredMenuCode,
            SalesDocumentSourceLineageRules.InquiryMenuCode);
        var ctl = QuotationControllerFor(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewQuotationBody(customer.Id, inquiry.Id)));
        Assert.Equal(inquiry.Id, db.Quotations.Single().InquiryId);
    }

    [Fact]
    public async Task PI新增_受限账号缺报价单菜单_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-AUTH-PI", customer.Id, DocumentStatus.Approved);
        // 只授予 PI 菜单，未授予来源报价单菜单。
        var userId = SeedRestrictedUser(db, customer.Id, ProformaInvoiceAuthorizationRules.RequiredMenuCode);
        var ctl = ProformaInvoiceControllerFor(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPiBody(customer.Id, quotation.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("报价单", ex.Message);
        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task 报价单新增_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-AUTH-N", customer.Id, DocumentStatus.Approved);
        var ctl = QuotationControllerFor(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewQuotationBody(customer.Id, inquiry.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.Quotations);
    }

    // ==================== 9. 控制器源码契约 ====================

    [Fact]
    public void 控制器_报价单新增先解析血缘再预约单号_来源字段不再照抄提交文本()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ERP.Api", "Controllers",
            "QuotationController.cs"));

        var createIndex = source.IndexOf("public async Task<IActionResult> Create(", StringComparison.Ordinal);
        var generateIndex = source.IndexOf("_noService.GenerateAsync(DocumentType.Quotation)",
            createIndex, StringComparison.Ordinal);
        var resolveIndex = source.IndexOf("SalesDocumentSourceLineageRules.ResolveAsync",
            createIndex, StringComparison.Ordinal);

        Assert.True(createIndex > 0);
        Assert.True(generateIndex > createIndex);
        Assert.True(resolveIndex > createIndex && resolveIndex < generateIndex);

        Assert.Contains("SalesDocumentSourceLineageRules.LockSourceRowAsync", source);
        Assert.Contains("SalesDocumentSourceLineageRules.EnsureWriteAuthorizedAsync", source);
        Assert.Contains("SalesDocumentSourceLineageRules.EnsureNewLinkEligibleAsync", source);
        Assert.Contains("SalesDocumentSourceLineageRules.EnsurePersistedSourceIntactAsync", source);
        Assert.Contains("SalesDocumentSourceLineageRules.RebindUnknownSourceText", source);
        Assert.Contains("if (change.HasSource)", source);
        // 来源字段不再照抄提交文本。
        Assert.DoesNotContain("existing.InquiryId = entity.InquiryId", source);
        Assert.DoesNotContain("existing.InquiryNo = entity.InquiryNo", source);
    }

    [Fact]
    public void 控制器_PI新增先解析血缘_修改与提交审核接入持久化来源重查()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ERP.Api", "Controllers",
            "ProformaInvoiceController.cs"));

        var createIndex = source.IndexOf("public async Task<IActionResult> Create(", StringComparison.Ordinal);
        var generateIndex = source.IndexOf("_noService.GenerateAsync(DocumentType.ProformaInvoice)",
            createIndex, StringComparison.Ordinal);
        var resolveIndex = source.IndexOf("SalesDocumentSourceLineageRules.ResolveAsync",
            createIndex, StringComparison.Ordinal);

        Assert.True(createIndex > 0);
        Assert.True(generateIndex > createIndex);
        Assert.True(resolveIndex > createIndex && resolveIndex < generateIndex);

        Assert.Contains("SalesDocumentSourceLineageRules.LockSourceRowAsync", source);
        Assert.Contains("SalesDocumentSourceLineageRules.EnsureNewLinkEligibleAsync", source);
        // 修改沿用持久化链接 + 提交 / 审核持久化来源重查。
        Assert.Contains("SalesDocumentSourceLineageRules.EnsurePersistedSourceIntactAsync", source);
        Assert.Contains("entity.QuotationId = existing.QuotationId", source);
    }

    // ==================== 工厂与种子数据 ====================

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static QuotationController QuotationControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static QuotationController PrivilegedQuotationController(ErpDbContext db)
        => QuotationControllerFor(db, TestAuth.SeedPrivilegedUser(db));

    private static ProformaInvoiceController ProformaInvoiceControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static ProformaInvoiceController PrivilegedPiController(ErpDbContext db)
        => ProformaInvoiceControllerFor(db, TestAuth.SeedPrivilegedUser(db));

    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "血缘测试客户",
            DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Inquiry SeedInquiry(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no,
            InquiryDate = DateTime.Today,
            CustomerId = customerId,
            ContactPerson = "Mr. Smith",
            ContactPhone = "138-0000-0000",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            ValidDays = 30,
            Status = status,
            Details = new List<InquiryDetail>
            {
                new()
                {
                    ProductId = 11, ProductName = "询价商品", Spec = "标准", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, long customerId, DocumentStatus status,
        long? inquiryId = null, string inquiryNo = "", long? rootQuotationId = null)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "血缘测试客户",
            InquiryId = inquiryId,
            InquiryNo = inquiryNo,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            SalesmanId = 66L,
            Status = status,
            RootQuotationId = rootQuotationId,
            RevisionNumber = rootQuotationId is null ? 1 : 2,
            Details = new List<QuotationDetail>
            {
                new()
                {
                    SortNo = 1, ProductCode = "Q-1", ProductName = "报价商品", Spec = "大", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        quotation.TotalAmount = 1000m;
        quotation.TotalAmountCny = 7200m;
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static ProformaInvoice SeedPi(ErpDbContext db, string no, long? customerId, DocumentStatus status,
        long? quotationId = null, string quotationNo = "")
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            QuotationId = quotationId,
            QuotationNo = quotationNo,
            CustomerId = customerId,
            CustomerName = "血缘测试客户",
            TradeTerms = "CIF",
            PaymentTerms = "T/T 30% deposit",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = 1000m,
            DepositRatio = 30m,
            DepositAmount = 300m,
            SalesmanId = 66L,
            Status = status,
            Details = new List<ProformaInvoiceDetail>
            {
                new()
                {
                    SortNo = 1, ProductCode = "P-1", ProductName = "PI 商品", Spec = "标准", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static Quotation NewQuotationBody(long customerId, long? inquiryId, string inquiryNo = "")
        => new()
        {
            QuotationDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "血缘测试客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            InquiryId = inquiryId,
            InquiryNo = inquiryNo,
            Details = new List<QuotationDetail>
            {
                new() { ProductCode = "N-1", ProductName = "手填商品", Unit = "PCS", Quantity = 10m, UnitPrice = 100m }
            }
        };

    private static ProformaInvoice NewPiBody(long customerId, long? quotationId, string quotationNo = "")
        => new()
        {
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "血缘测试客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            QuotationId = quotationId,
            QuotationNo = quotationNo,
            Details = new List<ProformaInvoiceDetail>
            {
                new() { ProductCode = "N-2", ProductName = "手填商品", Unit = "PCS", Quantity = 10m, UnitPrice = 100m }
            }
        };

    /// <summary>受限（非特权）业务员账号：登录账号 = 员工编码（ERP-097 权威映射）+ 既有菜单授权 + 客户数据范围。</summary>
    private static long SeedRestrictedUser(ErpDbContext db, long? assignedCustomerId, params string[] menuCodes)
    {
        var role = new SysRole { RoleCode = $"SL-{Guid.NewGuid():N}", RoleName = "单据操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "单据操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "单据操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        if (assignedCustomerId is long customerId)
            db.BaseCustomers.Single(c => c.Id == customerId).EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        foreach (var code in menuCodes)
        {
            var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }

        db.SaveChanges();
        return user.Id;
    }
}
