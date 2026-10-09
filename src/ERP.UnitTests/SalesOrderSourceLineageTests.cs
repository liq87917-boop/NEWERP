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
/// ERP-401 销售订单普通表单保存（新增 / 修改 / 提交 / 审核）来源血缘护栏单元测试：
/// 权威解析（来源号规范化 / PI 报价单祖先 / 冲突来源对 / 异客户 / 已删除 / 无法解析的历史值）、
/// 实时授权与客户范围、既有转换资格与唯一目标、历史来源不被静默清除与显式改绑的下游冻结、
/// 确定性锁语句与事务口径，以及控制器「先解析血缘、后预约单号与写入」的源码契约。
/// </summary>
public class SalesOrderSourceLineageTests
{
    // ==================== 1. 锁语句 / 事务口径 ====================

    [Fact]
    public void 锁语句与确定性锁序_与ERP399_400转换共用同一把来源行锁()
    {
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql,
            SalesOrderSourceLineageRules.QuotationRowLockSql);
        Assert.Equal(ProformaInvoiceMutationRules.PiRowLockSql,
            SalesOrderSourceLineageRules.ProformaInvoiceRowLockSql);
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            SalesOrderSourceLineageRules.SalesOrderRowLockSql);

        Assert.Contains("db_owner.Quotations", SalesOrderSourceLineageRules.QuotationRowLockSql);
        Assert.Contains("db_owner.ProformaInvoices", SalesOrderSourceLineageRules.ProformaInvoiceRowLockSql);
        Assert.Contains("db_owner.SalesOrders", SalesOrderSourceLineageRules.SalesOrderRowLockSql);
        Assert.Contains("报价单来源行锁", SalesOrderSourceLineageRules.LockOrderText);
        Assert.Contains("PI 来源行锁", SalesOrderSourceLineageRules.LockOrderText);
        Assert.Contains("销售订单目标行锁", SalesOrderSourceLineageRules.LockOrderText);
        Assert.Contains("绝不反向获取", SalesOrderSourceLineageRules.LockOrderText);

        // 与转换模块的锁序口径同源（ERP-399 / ERP-400 的确定性锁序）。
        Assert.Equal(SalesOrderSourceLineageRules.LockOrderText, SalesOrderConversion.SourceLockOrderText);
        Assert.Contains("PI 来源行锁", ProformaInvoiceMutationRules.ManualLinkLockOrderText);
        Assert.Contains("报价单来源行锁", QuotationMutationRules.ManualLinkLockOrderText);
    }

    [Fact]
    public async Task 事务与行锁_内存库等价无操作不阻断既有语义()
    {
        using var db = TestDbFactory.Create();
        Assert.False(SalesOrderSourceLineageRules.IsRelationalProvider(db));
        Assert.True(await SalesOrderSourceLineageRules.LockSourcesAsync(db, null, null));
        await using var transaction = await SalesOrderSourceLineageRules.BeginWriteTransactionAsync(db);
        Assert.Null(transaction);
    }

    // ==================== 2. 纯归一化 / 来源合并 ====================

    [Fact]
    public void 来源合并_请求未给出Id时保留历史来源_绝不静默清除()
    {
        var change = SalesOrderSourceLineageRules.ResolveChange(11L, 22L, null, null);

        Assert.Equal(11L, change.QuotationId);
        Assert.Equal(22L, change.PiId);
        Assert.True(change.IsClearingAttempt);
        Assert.False(change.IsExplicitChange);
        Assert.True(change.HasSource);
    }

    [Fact]
    public void 来源合并_显式给出不同来源判定为改绑_相同则保持未改动()
    {
        var rebind = SalesOrderSourceLineageRules.ResolveChange(11L, 22L, 33L, 44L);
        Assert.True(rebind.IsExplicitChange);
        Assert.Equal(33L, rebind.QuotationId);
        Assert.Equal(44L, rebind.PiId);

        var unchanged = SalesOrderSourceLineageRules.ResolveChange(11L, 22L, 11L, 22L);
        Assert.False(unchanged.IsExplicitChange);
        Assert.Equal(11L, unchanged.QuotationId);
        Assert.Equal(22L, unchanged.PiId);

        var fresh = SalesOrderSourceLineageRules.ResolveChange(null, null, 5L, null);
        Assert.True(fresh.IsExplicitChange);
        Assert.Equal(5L, fresh.QuotationId);

        var none = SalesOrderSourceLineageRules.ResolveChange(null, null, 0L, -1L);
        Assert.False(none.HasSource);
        Assert.False(none.IsExplicitChange);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 来源单号规范化_空白归一为空串(string? value)
        => Assert.Equal(string.Empty, SalesOrderSourceLineageRules.NormalizeNo(value, 50));

    [Fact]
    public void 来源单号规范化_去首尾空白并按列宽截断()
    {
        Assert.Equal("QT-1", SalesOrderSourceLineageRules.NormalizeNo("  QT-1 ", 50));
        Assert.Equal("AB", SalesOrderSourceLineageRules.NormalizeNo("ABCD", 2));
        Assert.Null(SalesOrderSourceLineageRules.NormalizeId(0));
        Assert.Null(SalesOrderSourceLineageRules.NormalizeId(-3));
        Assert.Equal(7L, SalesOrderSourceLineageRules.NormalizeId(7L));
    }

    // ==================== 3. 权威解析 ====================

    [Fact]
    public async Task 解析_未携带来源_显式未链接且保持有效()
    {
        using var db = TestDbFactory.Create();
        var lineage = await SalesOrderSourceLineageRules.ResolveAsync(db,
            SalesOrderSourceLineageRules.ResolveChange(null, null, null, null), 1L);

        Assert.True(lineage.IsUnlinked);
        Assert.False(lineage.IsUnresolvedLegacy);
        Assert.Null(lineage.PiId);
        Assert.Null(lineage.QuotationId);
        Assert.Equal(string.Empty, lineage.PiNo);
    }

    [Fact]
    public async Task 解析_已审核PI_规范化来源号并保留报价单祖先()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-LIN-1", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-LIN-1", customer.Id, DocumentStatus.Approved, quotation.Id, "QT-LIN-1");

        var change = SalesOrderSourceLineageRules.ResolveChange(null, null, null, pi.Id);
        var lineage = await SalesOrderSourceLineageRules.ResolveAsync(db, change, customer.Id);

        Assert.False(lineage.IsUnresolvedLegacy);
        Assert.Equal(pi.Id, lineage.PiId);
        Assert.Equal("PI-LIN-1", lineage.PiNo);
        Assert.Equal(quotation.Id, lineage.QuotationId);
        Assert.Equal("QT-LIN-1", lineage.QuotationNo);
        Assert.Equal(customer.Id, lineage.SourceCustomerId);
        Assert.NotNull(lineage.ProformaInvoice);
    }

    [Fact]
    public async Task 解析_冲突来源对_原子拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-CONF-1", customer.Id, DocumentStatus.Approved);
        var other = SeedQuotation(db, "QT-CONF-2", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-CONF-1", customer.Id, DocumentStatus.Approved, quotation.Id, "QT-CONF-1");

        var change = SalesOrderSourceLineageRules.ResolveChange(null, null, other.Id, pi.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderSourceLineageRules.ResolveAsync(db, change, customer.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("来源冲突", ex.Message);
    }

    [Fact]
    public async Task 解析_异客户来源_跨客户链接拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var pi = SeedPi(db, "PI-FOREIGN", foreign.Id, DocumentStatus.Approved, null, string.Empty);

        var change = SalesOrderSourceLineageRules.ResolveChange(null, null, null, pi.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderSourceLineageRules.ResolveAsync(db, change, own.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("跨客户", ex.Message);
    }

    [Fact]
    public async Task 解析_已删除来源_无效来源拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-DEL", customer.Id, DocumentStatus.Approved, null, string.Empty);
        pi.IsDeleted = true;
        db.SaveChanges();

        var change = SalesOrderSourceLineageRules.ResolveChange(null, null, null, pi.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderSourceLineageRules.ResolveAsync(db, change, customer.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已删除", ex.Message);
    }

    [Fact]
    public async Task 解析_显式来源Id完全无法解析_按显式历史值原样保留()
    {
        using var db = TestDbFactory.Create();
        var change = SalesOrderSourceLineageRules.ResolveChange(null, null, 777L, 888L);
        var lineage = await SalesOrderSourceLineageRules.ResolveAsync(db, change, 999999L);

        Assert.True(lineage.IsUnresolvedLegacy);
        Assert.False(lineage.IsUnlinked);
        Assert.Equal(777L, lineage.QuotationId);
        Assert.Equal(888L, lineage.PiId);
        Assert.Null(lineage.ProformaInvoice);
    }

    // ==================== 4. 唯一目标 ====================

    [Fact]
    public async Task 唯一目标_同一PI唯一化并可排除本单()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-UNIQ", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, pi.Id, pi.PiNo, null, null);

        Assert.NotNull(await SalesOrderSourceLineageRules.FindOtherTargetAsync(db, null, pi.Id, null));
        Assert.Null(await SalesOrderSourceLineageRules.FindOtherTargetAsync(db, null, pi.Id, order.Id));
    }

    [Fact]
    public async Task 唯一目标_PI来源订单不误判为报价单重复目标()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-UNIQ", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-UNIQ-2", customer.Id, DocumentStatus.Approved, quotation.Id, "QT-UNIQ");
        SeedOrder(db, customer.Id, pi.Id, pi.PiNo, quotation.Id, "QT-UNIQ");

        // PI 来源订单同时留痕报价单祖先：不能因此判定报价单「已被直接链接」。
        Assert.Null(await SalesOrderSourceLineageRules.FindOtherTargetAsync(db, quotation.Id, null, null));
        // 带上 PI 时只按 PI 唯一化（命中的是同一张 PI 目标，而不是把祖先报价单当成第二个目标）。
        var byPi = await SalesOrderSourceLineageRules.FindOtherTargetAsync(db, quotation.Id, pi.Id, null);
        Assert.NotNull(byPi);
        Assert.Equal(pi.Id, byPi!.SourcePiId);
    }

    // ==================== 5. 新增（普通表单保存） ====================

    [Fact]
    public async Task 新增_显式链接已审核PI_以权威来源号落库且不伪造来源状态()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-NEW-1", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-NEW-1", customer.Id, DocumentStatus.Approved, quotation.Id, "QT-NEW-1");
        var ctl = PrivilegedController(db);

        var body = NewOrderBody(customer.Id, quotation.Id, pi.Id,
            quotationNo: "伪造报价单号", piNo: "伪造PI号");
        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var order = db.SalesOrders.Single();
        Assert.True(order.Id > 0);
        Assert.StartsWith("SO", order.OrderNo);
        Assert.Equal(DocumentStatus.Pending, order.Status);
        // 权威来源 Id 与**规范化**单号（不采信提交的自由文本单号）。
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal("PI-NEW-1", order.SourcePiNo);
        Assert.Equal(quotation.Id, order.SourceQuotationId);
        Assert.Equal("QT-NEW-1", order.SourceQuotationNo);
        // 服务端金额口径不变：10 × 3 = 30，定金 30% = 9。
        Assert.Equal(30m, order.TotalAmount);
        Assert.Equal(9m, order.DepositAmount);
        // 来源 PI 状态不被伪造 / 改写（普通保存不是直接转换）。
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task 新增_手工未链接订单_完全有效()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var ctl = PrivilegedController(db);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(customer.Id, null, null)));

        var order = db.SalesOrders.Single();
        Assert.Null(order.SourcePiId);
        Assert.Null(order.SourceQuotationId);
        Assert.Equal(string.Empty, order.SourcePiNo);
        Assert.Equal(30m, order.TotalAmount);
    }

    [Fact]
    public async Task 新增_重复来源目标_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-DUP", customer.Id, DocumentStatus.Approved, null, string.Empty);
        SeedOrder(db, customer.Id, pi.Id, pi.PiNo, null, null);
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复链接", ex.Message);
        Assert.Single(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_未审核来源_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-PENDING", customer.Id, DocumentStatus.Pending, null, string.Empty);
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_异客户来源_拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var pi = SeedPi(db, "PI-FOREIGN-NEW", foreign.Id, DocumentStatus.Approved, null, string.Empty);
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Create(NewOrderBody(own.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("跨客户", ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_显式来源Id无法解析_按显式历史值原样保留()
    {
        using var db = TestDbFactory.Create();
        // ERP-423：显式历史来源无法解析不构成实时链接，但订单本身仍要求既有合法客户 / 商品主数据。
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = 999999L, CustomerCode = "C-MR-999999", CustomerName = "ERP423 历史客户", Status = 1, DepositRatio = 30m
        });
        db.SaveChanges();
        SeedProducts(db, 5L);
        var ctl = PrivilegedController(db);

        var body = NewOrderBody(999999L, 777L, 888L, quotationNo: "QT-HISTORY-1", piNo: "PI-HISTORY-1");
        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var order = db.SalesOrders.Single();
        Assert.Equal(777L, order.SourceQuotationId);
        Assert.Equal("QT-HISTORY-1", order.SourceQuotationNo);
        Assert.Equal(888L, order.SourcePiId);
        Assert.Equal("PI-HISTORY-1", order.SourcePiNo);
    }

    // ==================== 6. 修改（历史来源不被静默清除 / 显式改绑） ====================

    [Fact]
    public async Task 修改_请求未给出来源Id_保留历史来源绝不静默清除()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-UPD-1", customer.Id, DocumentStatus.Approved);
        var pi = SeedPi(db, "PI-UPD-1", customer.Id, DocumentStatus.Approved, quotation.Id, "QT-UPD-1");
        var order = SeedOrder(db, customer.Id, pi.Id, pi.PiNo, quotation.Id, "QT-UPD-1");
        var ctl = PrivilegedController(db);

        // 请求体不带任何来源 Id（清空尝试）：绝不静默清除历史来源。
        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id, NewOrderBody(customer.Id, null, null)));

        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(pi.Id, saved.SourcePiId);
        Assert.Equal("PI-UPD-1", saved.SourcePiNo);
        Assert.Equal(quotation.Id, saved.SourceQuotationId);
        Assert.Equal("QT-UPD-1", saved.SourceQuotationNo);
    }

    [Fact]
    public async Task 修改_显式改绑到另一可解析来源_完整复核并改写为权威来源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedPi(db, "PI-RB-1", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var second = SeedPi(db, "PI-RB-2", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, first.Id, first.PiNo, null, null);
        var ctl = PrivilegedController(db);

        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id,
            NewOrderBody(customer.Id, null, second.Id, piNo: "伪造PI号")));

        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(second.Id, saved.SourcePiId);
        Assert.Equal("PI-RB-2", saved.SourcePiNo);
        Assert.Null(saved.SourceQuotationId);
        Assert.Equal(string.Empty, saved.SourceQuotationNo);
    }

    [Fact]
    public async Task 修改_改绑到无法解析来源_拒绝且保留历史来源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-RB-3", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, pi.Id, pi.PiNo, null, null);
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Update(order.Id, NewOrderBody(customer.Id, null, 99999L)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("无法解析", ex.Message);

        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(pi.Id, saved.SourcePiId);
        Assert.Equal("PI-RB-3", saved.SourcePiNo);
    }

    [Fact]
    public async Task 修改_下游已有变更申请_改绑冻结()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedPi(db, "PI-FRZ-1", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var second = SeedPi(db, "PI-FRZ-2", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, first.Id, first.PiNo, null, null);
        db.SalesOrderChangeRequests.Add(new SalesOrderChangeRequest
        {
            SalesOrderId = order.Id,
            SalesOrderNo = order.OrderNo
        });
        db.SaveChanges();
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Update(order.Id, NewOrderBody(customer.Id, null, second.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("冻结", ex.Message);
        Assert.Equal(first.Id, db.SalesOrders.Single(o => o.Id == order.Id).SourcePiId);
    }

    [Fact]
    public async Task 修改_改绑到已被其它订单占用的来源_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var first = SeedPi(db, "PI-RB-4", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var second = SeedPi(db, "PI-RB-5", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, first.Id, first.PiNo, null, null);
        SeedOrder(db, customer.Id, second.Id, second.PiNo, null, null);
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Update(order.Id, NewOrderBody(customer.Id, null, second.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复链接", ex.Message);
        Assert.Equal(first.Id, db.SalesOrders.Single(o => o.Id == order.Id).SourcePiId);
    }

    // ==================== 7. 提交 / 审核 ====================

    [Fact]
    public async Task 提交审核_未链接订单_既有流转口径不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedOrder(db, customer.Id, null, null, null, null);
        var ctl = PrivilegedController(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 提交审核_已链接订单_持久化来源重查通过后正常流转()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-WF-1", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, pi.Id, pi.PiNo, null, null);
        var ctl = PrivilegedController(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 提交_来源已删除_冻结拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-WF-2", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var order = SeedOrder(db, customer.Id, pi.Id, pi.PiNo, null, null);
        pi.IsDeleted = true;
        db.SaveChanges();
        var ctl = PrivilegedController(db);

        var ex = await Denied(() => ctl.Submit(order.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    // ==================== 8. 实时授权（既有权限，不新增授权） ====================

    [Fact]
    public async Task 新增_受限账号无销售订单菜单_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-AUTH-1", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var userId = SeedRestrictedUser(db, customer.Id, withSalesOrderMenu: false);
        var ctl = NewController(db, userId);

        var ex = await Denied(() => ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("销售订单", ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_受限账号越客户范围_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-AUTH-2", customer.Id, DocumentStatus.Approved, null, string.Empty);
        // 具备菜单但未分配该客户（范围外）。
        var userId = SeedRestrictedUser(db, null, withSalesOrderMenu: true);
        var ctl = NewController(db, userId);

        var ex = await Denied(() => ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_受限账号具备菜单且在范围内_放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-AUTH-3", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var userId = SeedRestrictedUser(db, customer.Id, withSalesOrderMenu: true);
        var ctl = NewController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(pi.Id, db.SalesOrders.Single().SourcePiId);
    }

    [Fact]
    public async Task 新增_禁用账号_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-AUTH-4", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var userId = SeedRestrictedUser(db, customer.Id, withSalesOrderMenu: true, enabled: false);
        var ctl = NewController(db, userId);

        var ex = await Denied(() => ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, "PI-AUTH-5", customer.Id, DocumentStatus.Approved, null, string.Empty);
        var ctl = NewController(db, null);

        var ex = await Denied(() => ctl.Create(NewOrderBody(customer.Id, null, pi.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    // ==================== 9. 控制器源码契约 ====================

    [Fact]
    public void 控制器_新增与修改先解析血缘再预约单号与写入()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ERP.Api", "Controllers",
            "SalesOrderController.cs"));

        var createIndex = source.IndexOf("public async Task<IActionResult> Create(", StringComparison.Ordinal);
        var generateIndex = source.IndexOf("_noService.GenerateAsync(DocumentType.SalesOrder)",
            createIndex, StringComparison.Ordinal);
        var resolveIndex = source.IndexOf("SalesOrderSourceLineageRules.ResolveAsync",
            createIndex, StringComparison.Ordinal);

        Assert.True(createIndex > 0);
        Assert.True(generateIndex > createIndex);
        Assert.True(resolveIndex > createIndex && resolveIndex < generateIndex);

        Assert.Contains("SalesOrderSourceLineageRules.LockSourcesAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.LockSalesOrderRowAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.EnsureWriteAuthorizedAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.EnsureNewLinkEligibleAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.EnsureRebindNotFrozenAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.EnsurePersistedSourceIntactAsync", source);
        Assert.Contains("SalesOrderSourceLineageRules.RebindUnknownSourceText", source);
        Assert.Contains("RunLineageGuardedStatusChangeAsync", source);
        // ERP-421：无论手工 / 历史 / 已解析来源，都进入同一原子事务 + 确定性行锁 + 锁内权威重读协议。
        Assert.Contains("if (change.HasSource)", source);
        Assert.Contains("SalesOrderMutationRules.BeginMutationTransactionAsync", source);
        Assert.Contains("SalesOrderMutationRules.ResolveLiveSourceLockScopeAsync", source);
        Assert.Contains("SalesOrderMutationRules.TryResolveLiveSourceLockScopeAsync", source);
        Assert.Contains("SalesOrderMutationRules.PersistedSourceUnchanged", source);
        // 来源字段不再照抄提交文本。
        Assert.DoesNotContain("existing.SourcePiId = entity.SourcePiId", source);
        Assert.DoesNotContain("existing.SourceQuotationId = entity.SourceQuotationId", source);
    }

    // ==================== 工厂与种子数据 ====================

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static SalesOrderController NewController(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static SalesOrderController PrivilegedController(ErpDbContext db)
        => NewController(db, TestAuth.SeedPrivilegedUser(db));

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
        // ERP-423：规范销售订单写入要求实时商品主数据；一并播种本用例使用的既有合法商品。
        SeedProducts(db, 5L, 21L);
        return customer;
    }

    /// <summary>ERP-423：播种既有合法商品（单位留空 = 不产生单位口径判定）供规范销售订单写入使用；只补缺失行。</summary>
    private static void SeedProducts(ErpDbContext db, params long[] ids)
    {
        foreach (var id in ids)
        {
            if (!db.BaseProducts.Any(p => p.Id == id))
                db.BaseProducts.Add(new BaseProduct
                {
                    Id = id, ProductCode = $"P-MR-{id}", ProductName = $"ERP423 商品 {id}", Status = 1
                });
        }

        db.SaveChanges();
    }

    /// <summary>受限（非特权）业务员账号：既有「销售订单」菜单可选 + 仅分配指定客户的数据范围。</summary>
    private static long SeedRestrictedUser(ErpDbContext db, long? assignedCustomerId, bool withSalesOrderMenu,
        bool enabled = true)
    {
        var role = new SysRole { RoleCode = $"SL-{Guid.NewGuid():N}", RoleName = "销售订单操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"sl-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销售订单操作员",
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "销售订单操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        if (assignedCustomerId is long customerId)
        {
            var customer = db.BaseCustomers.Single(c => c.Id == customerId);
            customer.EmpId = employee.Id;
        }

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (withSalesOrderMenu)
        {
            var menu = new SysMenu
            {
                MenuName = SalesOrderSourceLineageRules.SalesOrderMenuText,
                MenuCode = SalesOrderSourceLineageRules.SalesOrderMenuCode,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return user.Id;
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "血缘测试客户",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            SalesmanId = 66L,
            Status = status,
            Details = new List<QuotationDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 11, ProductCode = "P-1", ProductName = "商品 A", Spec = "大",
                    Unit = "PCS", Quantity = 100m, UnitPrice = 2.5m, Amount = 250m
                }
            }
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static ProformaInvoice SeedPi(ErpDbContext db, string no, long? customerId, DocumentStatus status,
        long? quotationId, string quotationNo)
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
            PortOfDestination = "HAMBURG",
            PaymentTerms = "T/T 30% deposit",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = 1000m,
            DepositRatio = 30m,
            SalesmanId = 66L,
            Status = status,
            Details = new List<ProformaInvoiceDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 21, ProductCode = "PX-1", ProductName = "PI 商品", Spec = "标准",
                    Unit = "PCS", Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, long customerId, long? piId, string? piNo,
        long? quotationId, string? quotationNo)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-{Guid.NewGuid():N}"[..18],
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Status = DocumentStatus.Pending,
            SourcePiId = piId,
            SourcePiNo = piNo ?? string.Empty,
            SourceQuotationId = quotationId,
            SourceQuotationNo = quotationNo ?? string.Empty,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 21, ProductName = "PI 商品", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrder NewOrderBody(long customerId, long? quotationId, long? piId,
        string quotationNo = "", string piNo = "")
        => new()
        {
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            SourceQuotationId = quotationId,
            SourceQuotationNo = quotationNo,
            SourcePiId = piId,
            SourcePiNo = piNo,
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = 5, ProductName = "手填商品", Unit = "PCS", Quantity = 10m, UnitPrice = 3m }
            }
        };

    private static async Task<BusinessException> Denied(Func<Task<IActionResult>> action)
        => await Assert.ThrowsAsync<BusinessException>(action);
}
