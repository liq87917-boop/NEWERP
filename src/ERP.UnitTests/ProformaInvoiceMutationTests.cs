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
/// PI 生命周期与「PI → 销售订单」转换的确定性来源行锁 / 原子事务 / 锁内权威复核护栏单元测试（ERP-399，内存库）。
/// 覆盖：锁与事务契约（PI 来源行锁、目标订单共用的来源订单行锁、Id 升序确定性加锁、原子事务口径）；
/// 各生命周转变更的锁内守卫（修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除）；
/// 已完成且存在下游销售订单链接的 PI 一律冻结（保留显式历史，绝不做反向冲销），无链接时保留既有允许的流转；
/// 转换资格（重复生成 / 未审核 / 已作废 / 无明细）与转换结果权威一致性（数量 / 币种 / 汇率 / 合计 / 定金 / SourcePiId）；
/// 预填保持只读（不落库、不占号）。
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与真实既有身份（<see cref="TestAuth"/>），
/// 不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收；跨连接竞态由
/// <c>ProformaInvoiceMutationSqlServerTests</c> 覆盖。</para>
/// </summary>
public class ProformaInvoiceMutationTests
{
    // ==================== 脚手架 ====================

    private static ProformaInvoiceController NewController(ErpDbContext db)
    {
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static ProformaInvoice SeedPi(ErpDbContext db, string no, DocumentStatus status,
        bool withDetails = true, decimal amount = 1000m)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "客户 A",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            TotalAmount = amount,
            TotalAmountCny = amount * 7.2m,
            DepositAmount = amount * 0.3m,
            Status = status
        };
        if (withDetails)
        {
            pi.Details.Add(new ProformaInvoiceDetail
            {
                SortNo = 1, ProductCode = "P001", ProductName = "饰品 A", Unit = "PCS",
                Quantity = 10m, UnitPrice = amount / 10m, Amount = amount
            });
        }
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    /// <summary>播种一张「已链接来源 PI」的销售订单（只按持久化 SourcePiId 绑定，绝不按自由文本推断）。</summary>
    private static SalesOrder SeedLinkedOrder(ErpDbContext db, ProformaInvoice pi)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-LINK-{pi.Id}",
            OrderDate = DateTime.Today,
            CustomerId = pi.CustomerId ?? 0,
            Currency = pi.Currency,
            ExchangeRate = pi.ExchangeRate,
            TotalAmount = pi.TotalAmount,
            DepositRatio = pi.DepositRatio,
            DepositAmount = pi.DepositAmount,
            Status = DocumentStatus.Pending,
            SourcePiId = pi.Id,
            SourcePiNo = pi.PiNo
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static async Task<BusinessException> Denied(Func<Task<IActionResult>> action)
        => await Assert.ThrowsAsync<BusinessException>(action);

    // ==================== 1. 锁 / 事务契约 ====================

    [Fact]
    public void 锁语句与事务口径_与既有父单证行锁同源且与销售订单行锁身份一致()
    {
        Assert.Contains("db_owner.ProformaInvoices", ProformaInvoiceMutationRules.PiRowLockSql);
        Assert.Contains("UPDLOCK", ProformaInvoiceMutationRules.PiRowLockSql);
        Assert.Contains("HOLDLOCK", ProformaInvoiceMutationRules.PiRowLockSql);

        // 目标订单（来源销售订单）行锁与销售订单取消 / 出库审核 / 单证生成共用同一常量（同一把锁）。
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            ProformaInvoiceMutationRules.SalesOrderRowLockSql);
        Assert.Contains("db_owner.SalesOrders", ProformaInvoiceMutationRules.SalesOrderRowLockSql);

        Assert.Contains("PI 来源行锁", ProformaInvoiceMutationRules.LockOrderText);
        Assert.Contains("Id 升序", ProformaInvoiceMutationRules.LockOrderText);
        Assert.Contains("不新增", ProformaInvoiceMutationRules.BoundaryText);
    }

    [Fact]
    public void 加锁键_去重_仅正整数_按Id升序()
    {
        var merged = ProformaInvoiceMutationRules.MergeLockIds(new long[] { 5, 0, 3, -1, 5, 1 });
        Assert.Equal(new long[] { 1, 3, 5 }, merged);
        Assert.Empty(ProformaInvoiceMutationRules.MergeLockIds(null));
    }

    [Fact]
    public async Task 内存库_事务与行锁等价无操作_转换资格仍由锁内权威重读判定()
    {
        using var db = TestDbFactory.Create();
        Assert.False(ProformaInvoiceMutationRules.IsRelationalProvider(db));
        Assert.Null(await ProformaInvoiceMutationRules.BeginMutationTransactionAsync(db));

        var pi = SeedPi(db, "PI-LOCK-1", DocumentStatus.Approved);
        Assert.True(await ProformaInvoiceMutationRules.LockPiRowAsync(db, pi.Id));
        Assert.False(await ProformaInvoiceMutationRules.LockPiRowAsync(db, 0));
    }

    [Fact]
    public async Task 下游链接判定_只按持久化SourcePiId且排除已删除订单()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-LINK-1", DocumentStatus.Approved);
        Assert.False(await ProformaInvoiceMutationRules.HasDownstreamLinkAsync(db, pi.Id));

        var order = SeedLinkedOrder(db, pi);
        Assert.True(await ProformaInvoiceMutationRules.HasDownstreamLinkAsync(db, pi.Id));
        Assert.Equal(order.Id, (await ProformaInvoiceMutationRules.FindDownstreamOrderAsync(db, pi.Id))!.Id);

        order.IsDeleted = true;
        db.SaveChanges();
        Assert.False(await ProformaInvoiceMutationRules.HasDownstreamLinkAsync(db, pi.Id));
    }

    // ==================== 2. 生命周期守卫（纯规则） ====================

    [Fact]
    public void 修改资格_仅待提交与已提交可改()
    {
        ProformaInvoiceMutationRules.EnsureEditAllowed(DocumentStatus.Pending);
        ProformaInvoiceMutationRules.EnsureEditAllowed(DocumentStatus.Submitted);
        foreach (var status in new[]
                 { DocumentStatus.Approved, DocumentStatus.Cancelled, DocumentStatus.Completed })
            Assert.Equal(ErrorCodes.RuleConflict,
                Assert.Throws<BusinessException>(() => ProformaInvoiceMutationRules.EnsureEditAllowed(status)).Code);
    }

    [Fact]
    public void 提交资格_仅待提交可提交()
    {
        ProformaInvoiceMutationRules.EnsureSubmitAllowed(DocumentStatus.Pending);
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureSubmitAllowed(DocumentStatus.Submitted)).Code);
    }

    [Fact]
    public void 审核资格_无有效明细或状态不符时拒绝()
    {
        ProformaInvoiceMutationRules.EnsureApproveAllowed(DocumentStatus.Pending, 1);
        ProformaInvoiceMutationRules.EnsureApproveAllowed(DocumentStatus.Submitted, 2);

        var noDetails = Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureApproveAllowed(DocumentStatus.Pending, 0));
        Assert.Equal(ProformaInvoiceMutationRules.NoActiveDetailsApproveText, noDetails.Message);

        Assert.Contains("已作废", Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureApproveAllowed(DocumentStatus.Cancelled, 1)).Message);
        Assert.Contains("已转销售订单", Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureApproveAllowed(DocumentStatus.Completed, 1)).Message);
        Assert.Contains("已审核", Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureApproveAllowed(DocumentStatus.Approved, 1)).Message);
    }

    [Fact]
    public void 销审资格_仅已审核可销审()
    {
        ProformaInvoiceMutationRules.EnsureUnauditAllowed(DocumentStatus.Approved);
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureUnauditAllowed(DocumentStatus.Pending)).Code);
    }

    [Fact]
    public void 作废资格_已作废与已转销售订单拒绝_其余保留既有口径()
    {
        ProformaInvoiceMutationRules.EnsureVoidAllowed(DocumentStatus.Pending);
        ProformaInvoiceMutationRules.EnsureVoidAllowed(DocumentStatus.Submitted);
        ProformaInvoiceMutationRules.EnsureVoidAllowed(DocumentStatus.Approved);

        Assert.Contains("已作废", Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureVoidAllowed(DocumentStatus.Cancelled)).Message);
        Assert.Contains("已转销售订单", Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureVoidAllowed(DocumentStatus.Completed)).Message);
    }

    [Fact]
    public void 取消资格_保留既有口径_仅由下游链接守卫冻结()
    {
        foreach (var status in Enum.GetValues<DocumentStatus>())
            ProformaInvoiceMutationRules.EnsureCancelAllowed(status);
    }

    [Fact]
    public void 删除资格_仅待提交可删()
    {
        ProformaInvoiceMutationRules.EnsureDeleteAllowed(DocumentStatus.Pending);
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureDeleteAllowed(DocumentStatus.Submitted)).Code);
    }

    [Fact]
    public void 批量删除资格_任一行非待提交即整体拒绝()
    {
        ProformaInvoiceMutationRules.EnsureBatchDeleteAllowed(new[]
            { DocumentStatus.Pending, DocumentStatus.Pending });
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureBatchDeleteAllowed(new[]
                { DocumentStatus.Pending, DocumentStatus.Approved })).Code);
    }

    [Fact]
    public void 下游链接守卫_有链接即冻结_无链接保留既有流转()
    {
        ProformaInvoiceMutationRules.EnsureNoDownstreamLink(false);
        var ex = Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsureNoDownstreamLink(true));
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText, ex.Message);
        Assert.Contains("反向冲销", ex.Message);
    }

    [Fact]
    public void 乐观令牌复核_不一致即拒绝_任一侧为空不判定()
    {
        var a = new byte[] { 1, 2, 3 };
        ProformaInvoiceMutationRules.EnsurePersistedVersionCurrent(a, new byte[] { 1, 2, 3 });
        ProformaInvoiceMutationRules.EnsurePersistedVersionCurrent(null, a);
        ProformaInvoiceMutationRules.EnsurePersistedVersionCurrent(a, null);

        var ex = Assert.Throws<BusinessException>(
            () => ProformaInvoiceMutationRules.EnsurePersistedVersionCurrent(a, new byte[] { 1, 2, 4 }));
        Assert.Equal(ProformaInvoiceMutationRules.StaleRowVersionText, ex.Message);
    }

    // ==================== 3. 转换资格与转换结果权威一致性 ====================

    private static ProformaInvoice ConversionPi(DocumentStatus status)
        => new() { Id = 9, PiNo = "PI-CONV-9", Currency = Currency.EUR, ExchangeRate = 7.9m, Status = status };

    [Fact]
    public void 转换资格_已审核且有明细且无既有订单放行()
        => ProformaInvoiceMutationRules.EnsureConversionEligible(
            ConversionPi(DocumentStatus.Approved), null, 1);

    [Fact]
    public void 转换资格_未审核与已作废与已完成拒绝()
    {
        Assert.Contains("未审核", Assert.Throws<BusinessException>(() =>
            ProformaInvoiceMutationRules.EnsureConversionEligible(
                ConversionPi(DocumentStatus.Pending), null, 1)).Message);
        Assert.Contains("已作废", Assert.Throws<BusinessException>(() =>
            ProformaInvoiceMutationRules.EnsureConversionEligible(
                ConversionPi(DocumentStatus.Cancelled), null, 1)).Message);
        Assert.Contains("已完成", Assert.Throws<BusinessException>(() =>
            ProformaInvoiceMutationRules.EnsureConversionEligible(
                ConversionPi(DocumentStatus.Completed), null, 1)).Message);
    }

    [Fact]
    public void 转换资格_无有效明细拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceMutationRules.EnsureConversionEligible(
                ConversionPi(DocumentStatus.Approved), null, 0));
        Assert.Equal(ProformaInvoiceMutationRules.NoActiveDetailsConversionText, ex.Message);
    }

    [Fact]
    public void 转换资格_重复生成拒绝并提示既有单号()
    {
        var existing = new SalesOrder { OrderNo = "SO-EXIST-1" };
        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceMutationRules.EnsureConversionEligible(
                ConversionPi(DocumentStatus.Approved), existing, 1));
        Assert.Contains("SO-EXIST-1", ex.Message);
        Assert.Contains("不能重复生成", ex.Message);
    }

    [Fact]
    public void 转换结果一致性_数量币种汇率合计定金与显式SourcePiId逐项复核()
    {
        var pi = ConversionPi(DocumentStatus.Approved);
        pi.Details.Add(new ProformaInvoiceDetail
        {
            SortNo = 1, Quantity = 10m, UnitPrice = 100m, Amount = 1000m
        });

        SalesOrder Draft() => new()
        {
            SourcePiId = pi.Id,
            SourcePiNo = pi.PiNo,
            Currency = Currency.EUR,
            ExchangeRate = 7.9m,
            TotalAmount = 1000m,
            DepositRatio = 30m,
            DepositAmount = 300m,
            Details = new List<SalesOrderDetail> { new() { Quantity = 10m, UnitPrice = 100m } }
        };

        SalesOrderConversion.EnsureDraftMatchesSource(pi, Draft());

        var wrongSource = Draft();
        wrongSource.SourcePiId = 99;
        Assert.Equal(SalesOrderConversion.DraftMismatchText,
            Assert.Throws<BusinessException>(() =>
                SalesOrderConversion.EnsureDraftMatchesSource(pi, wrongSource)).Message);

        var wrongCurrency = Draft();
        wrongCurrency.Currency = Currency.USD;
        Assert.Throws<BusinessException>(() => SalesOrderConversion.EnsureDraftMatchesSource(pi, wrongCurrency));

        var wrongRate = Draft();
        wrongRate.ExchangeRate = 1m;
        Assert.Throws<BusinessException>(() => SalesOrderConversion.EnsureDraftMatchesSource(pi, wrongRate));

        var wrongQuantity = Draft();
        wrongQuantity.Details[0].Quantity = 9m;
        Assert.Throws<BusinessException>(() => SalesOrderConversion.EnsureDraftMatchesSource(pi, wrongQuantity));

        var wrongAmount = Draft();
        wrongAmount.TotalAmount = 999m;
        Assert.Throws<BusinessException>(() => SalesOrderConversion.EnsureDraftMatchesSource(pi, wrongAmount));

        var wrongDeposit = Draft();
        wrongDeposit.DepositAmount = 1m;
        Assert.Throws<BusinessException>(() => SalesOrderConversion.EnsureDraftMatchesSource(pi, wrongDeposit));

        // 已软删除的来源明细不参与权威合计（与转换映射同口径）：来源有效明细为空即判定不一致。
        pi.Details[0].IsDeleted = true;
        Assert.Throws<BusinessException>(() =>
            SalesOrderConversion.EnsureDraftMatchesSource(pi, Draft()));
    }

    // ==================== 4. 控制器锁内护栏（内存库，真实既有身份） ====================

    [Fact]
    public async Task 已链接PI_取消被冻结_来源状态与目标订单均未改变()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-FREEZE-CANCEL", DocumentStatus.Completed);
        var order = SeedLinkedOrder(db, pi);

        var ex = await Denied(() => NewController(db).Cancel(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText, ex.Message);

        Assert.Equal(DocumentStatus.Completed, db.ProformaInvoices.Single().Status);
        Assert.Equal(order.Id, db.SalesOrders.Single().Id);
        Assert.Equal(DocumentStatus.Pending, db.SalesOrders.Single().Status);
    }

    [Fact]
    public async Task 已链接PI_作废被冻结()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-FREEZE-VOID", DocumentStatus.Approved);
        SeedLinkedOrder(db, pi);

        var ex = await Denied(() => NewController(db).Void(pi.Id));
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText, ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task 已链接PI_销审与编辑与提交与审核被冻结()
    {
        using var db = TestDbFactory.Create();
        var approved = SeedPi(db, "PI-FREEZE-UNAUDIT", DocumentStatus.Approved);
        SeedLinkedOrder(db, approved);
        var submitted = SeedPi(db, "PI-FREEZE-EDIT", DocumentStatus.Submitted);
        SeedLinkedOrder(db, submitted);
        var pending = SeedPi(db, "PI-FREEZE-SUBMIT", DocumentStatus.Pending);
        SeedLinkedOrder(db, pending);
        var ctl = NewController(db);

        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText,
            (await Denied(() => ctl.Unaudit(approved.Id))).Message);
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText,
            (await Denied(() => ctl.Update(submitted.Id, new ProformaInvoice { CustomerId = 1 }))).Message);
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText,
            (await Denied(() => ctl.Submit(pending.Id))).Message);
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText,
            (await Denied(() => ctl.Approve(pending.Id))).Message);

        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single(p => p.Id == approved.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.ProformaInvoices.Single(p => p.Id == submitted.Id).Status);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.Single(p => p.Id == pending.Id).Status);
    }

    [Fact]
    public async Task 已链接PI_删除与批量删除被整体冻结_零部分删除()
    {
        using var db = TestDbFactory.Create();
        var pending = SeedPi(db, "PI-FREEZE-DEL", DocumentStatus.Pending);
        SeedLinkedOrder(db, pending);
        var other = SeedPi(db, "PI-FREEZE-BATCH", DocumentStatus.Pending);
        var ctl = NewController(db);

        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText,
            (await Denied(() => ctl.Delete(pending.Id))).Message);

        var ex = await Denied(() => ctl.BatchDelete(new List<long> { other.Id, pending.Id }));
        Assert.Equal(ProformaInvoiceMutationRules.DownstreamLinkedText, ex.Message);
        Assert.All(db.ProformaInvoices.ToList(), p => Assert.False(p.IsDeleted));
    }

    [Fact]
    public async Task 无下游链接_既有允许流转全部保留()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var submit = SeedPi(db, "PI-KEEP-SUBMIT", DocumentStatus.Pending);
        Assert.IsType<OkObjectResult>(await ctl.Submit(submit.Id));
        Assert.Equal(DocumentStatus.Submitted, db.ProformaInvoices.Single(p => p.Id == submit.Id).Status);

        var approve = SeedPi(db, "PI-KEEP-APPROVE", DocumentStatus.Pending);
        Assert.IsType<OkObjectResult>(await ctl.Approve(approve.Id));
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single(p => p.Id == approve.Id).Status);

        var unaudit = SeedPi(db, "PI-KEEP-UNAUDIT", DocumentStatus.Approved);
        Assert.IsType<OkObjectResult>(await ctl.Unaudit(unaudit.Id));
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.Single(p => p.Id == unaudit.Id).Status);

        var cancel = SeedPi(db, "PI-KEEP-CANCEL", DocumentStatus.Approved);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(cancel.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.ProformaInvoices.Single(p => p.Id == cancel.Id).Status);

        var voided = SeedPi(db, "PI-KEEP-VOID", DocumentStatus.Approved);
        Assert.IsType<OkObjectResult>(await ctl.Void(voided.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.ProformaInvoices.Single(p => p.Id == voided.Id).Status);

        var deleted = SeedPi(db, "PI-KEEP-DELETE", DocumentStatus.Pending);
        Assert.IsType<OkObjectResult>(await ctl.Delete(deleted.Id));
        Assert.True(db.ProformaInvoices.Single(p => p.Id == deleted.Id).IsDeleted);

        var batchA = SeedPi(db, "PI-KEEP-BATCH-A", DocumentStatus.Pending);
        var batchB = SeedPi(db, "PI-KEEP-BATCH-B", DocumentStatus.Pending);
        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { batchA.Id, batchB.Id, batchA.Id, 0 }));
        Assert.True(db.ProformaInvoices.Single(p => p.Id == batchA.Id).IsDeleted);
        Assert.True(db.ProformaInvoices.Single(p => p.Id == batchB.Id).IsDeleted);
    }

    [Fact]
    public async Task 编辑_锁内权威重读后整体替换明细_金额与定金按服务端口径重算()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-EDIT-1", DocumentStatus.Pending);

        var body = new ProformaInvoice
        {
            PiDate = DateTime.Today, CustomerId = 1, CustomerName = "客户 A",
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            Details = new List<ProformaInvoiceDetail>
            {
                new() { ProductCode = "P1", ProductName = "新行", Quantity = 20m, UnitPrice = 5m }
            }
        };
        Assert.IsType<OkObjectResult>(await NewController(db).Update(pi.Id, body));

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(100m, saved.TotalAmount);
        Assert.Equal(30m, saved.DepositAmount);
        var details = db.ProformaInvoiceDetails.Where(d => d.PiId == pi.Id).ToList();
        Assert.Single(details);
        Assert.Equal(100m, details[0].Amount);
    }

    [Fact]
    public async Task 转销售订单_只生成一张完整订单_金额币种定金与显式SourcePiId一致且PI置已完成()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-TO-ORDER-1", DocumentStatus.Approved);

        var ok = Assert.IsType<OkObjectResult>(await NewController(db).ToSalesOrder(pi.Id));
        var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value).Data!;
        Assert.StartsWith("SO", result.OrderNo);
        Assert.Equal(pi.PiNo, result.SourceNo);

        var order = db.SalesOrders.Single();
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal(pi.PiNo, order.SourcePiNo);
        Assert.Equal(pi.Currency, order.Currency);
        Assert.Equal(pi.ExchangeRate, order.ExchangeRate);
        Assert.Equal(pi.TotalAmount, order.TotalAmount);
        Assert.Equal(pi.DepositAmount, order.DepositAmount);
        Assert.Equal(DocumentStatus.Completed, db.ProformaInvoices.Single().Status);

        // 重复转换：锁内既有来源订单守卫生效，同一 PI 至多一张完整订单（不产生财务 / 库存过账副作用）。
        var ex = await Denied(() => NewController(db).ToSalesOrder(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已生成销售订单", ex.Message);
        Assert.Single(db.SalesOrders);
        Assert.Empty(db.StockOuts);
    }

    [Fact]
    public async Task 转销售订单_已预先存在来源订单_拒绝且不新增订单不改来源状态()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-TO-ORDER-2", DocumentStatus.Approved);
        SeedLinkedOrder(db, pi);

        var ex = await Denied(() => NewController(db).ToSalesOrder(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复生成", ex.Message);
        Assert.Single(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task 带入预填保持只读_不落库不占号不改来源状态_最终保存以销售订单为权威()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-PREFILL-RO", DocumentStatus.Approved);

        var ok = Assert.IsType<OkObjectResult>(await NewController(db).OrderPrefill(pi.Id));
        var prefill = Assert.IsType<ApiResponse<SalesOrderPrefillResult>>(ok.Value).Data!;

        Assert.Equal(string.Empty, prefill.Order.OrderNo);
        Assert.Equal(pi.Id, prefill.Order.SourcePiId);
        Assert.Equal(pi.Currency, prefill.Order.Currency);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SysDocumentNumberRules);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public void 控制器接线_生命周期路由与转换都在PI来源行锁与原子事务内()
    {
        var source = ReadSource("src", "ERP.Api", "Controllers", "ProformaInvoiceController.cs");

        Assert.Contains("ProformaInvoiceMutationRules.BeginMutationTransactionAsync(Db)", source);
        Assert.Contains("ProformaInvoiceMutationRules.LockPiRowAsync", source);
        Assert.Contains("ProformaInvoiceMutationRules.LockPiRowsAsync", source);
        Assert.Contains("ProformaInvoiceMutationRules.EnsureNoDownstreamLink", source);
        Assert.Contains("SalesOrderConversion.EnsureDraftMatchesSource(pi, order)", source);
        Assert.Contains("ProformaInvoiceMutationRules.DiscardTrackedChanges(Db)", source);
    }

    /// <summary>读取仓库内源文件（从测试输出目录向上定位解决方案根，与既有并发护栏测试同源）。</summary>
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
