using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-471 形式发票 PI 商业条款（币种 / 汇率 / 数量 / 单价 / 金额 / 合计 / 定金比例 · 金额）
/// 校验与整理的单元测试（纯内存库 + 直接实例化控制器，不连接 SQL Server、不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一权威契约</b>（<see cref="ProformaInvoiceCommercialValidationRules"/>）：币种必须已定义、
/// 汇率 &gt; 0（绝不静默按 1）、数量 &gt; 0、单价 ≥ 0 且全部在持久化精度内可表示；软删除明细行注入拒绝；
/// 金额 / 合计 / 折人民币与自动定金一律由服务端按 <see cref="CurrencyAmountRules"/> 的币种精度
/// （JPY 等 0 位、其余 2 位、0.5 进位）从有效明细派生；定金显式 0 = 自动计算、显式负数拒绝、显式正数
/// 必须在币种精度内且不超过权威总额；</item>
/// <item><b>EF 精度契约</b>：用 SQL Server 提供程序**仅构建模型**（不打开连接）断言 PI 商业列的实际存储精度
/// 与规则常量同源；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与任何字段改写之前拒绝非法条款（零写入、零占号、原证据不变）；
/// 客户端伪造行金额 / 合计被服务端重算覆盖；提交 / 审核 / 转销售订单在既有行锁内复核已持久化条款，
/// 非法即原子拒绝且状态 / 下游单据 / 单号预约均不变；显式支持的空草稿仍可保存。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class ProformaInvoiceCommercialValidationTests
{
    // ==================== 1. 唯一权威契约（纯规则） ====================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.5)]
    [InlineData(0.00005)]  // 正数量但第 5 位小数：超出声明列 DECIMAL(18,4)
    [InlineData(1.00005)]  // 第 5 位小数
    public void 条款校验_数量非正或超出持久化精度_拒绝(decimal quantity)
    {
        var pi = Draft(Line(quantity, 10m));

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(1.00005)]  // 第 5 位小数：超出声明列 DECIMAL(18,4)
    public void 条款校验_单价为负或超出持久化精度_拒绝(decimal unitPrice)
    {
        var pi = Draft(Line(1m, unitPrice));

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负", ex.Message);
    }

    [Fact]
    public void 条款校验_币种未定义_拒绝()
    {
        var pi = Draft(Line(1m, 1m));
        pi.Currency = (Currency)999;

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.CurrencyText, ex.Message);
    }

    [Theory]
    [InlineData(0)]       // 旧实现会静默按 1 处理，绝不能传播成错误折人民币金额
    [InlineData(-7.2)]
    [InlineData(7.1234567)]   // 第 7 位小数：超出声明列 DECIMAL(18,6)
    public void 条款校验_汇率非正或不可表示_拒绝(decimal exchangeRate)
    {
        var pi = Draft(Line(1m, 1m));
        pi.ExchangeRate = exchangeRate;

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.ExchangeRateText, ex.Message);
    }

    [Fact]
    public void 条款校验_软删除明细行注入_拒绝()
    {
        var pi = Draft(Line(1m, 1m), Line(2m, 2m, deleted: true));

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.SoftDeletedDetailText, ex.Message);
    }

    [Fact]
    public void 条款校验_空草稿可保存_但承诺路径要求至少一行有效明细()
    {
        var pi = Draft();

        // 显式支持的空草稿：新增 / 修改可保存。
        ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi);

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateOperational(pi));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.EmptyOperationalDetailsText, ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(101)]
    [InlineData(30.00005)]   // 第 5 位小数：超出声明列 DECIMAL(18,4)
    public void 定金校验_比例越界或超精度_拒绝(decimal ratio)
    {
        var pi = Draft(Line(10m, 10m));
        pi.DepositRatio = ratio;

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositRatioText, ex.Message);
    }

    [Fact]
    public void 定金校验_显式负数_拒绝且绝不静默按比例重算()
    {
        var pi = Draft(Line(10m, 10m));   // 总额 100
        pi.DepositRatio = 30m;
        pi.DepositAmount = -1m;           // 旧实现把负数当作「未填写」→ 静默重算为 30

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositNegativeText, ex.Message);
        Assert.Equal(-1m, pi.DepositAmount);   // 校验本身不改写任何字段
    }

    [Fact]
    public void 定金校验_超过权威总额_拒绝()
    {
        var pi = Draft(Line(10m, 10m));   // 总额 100
        pi.DepositRatio = 30m;
        pi.DepositAmount = 100.01m;

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositExceedsTotalText, ex.Message);
    }

    [Fact]
    public void 定金校验_超精度_拒绝()
    {
        var pi = Draft(Line(10m, 10m));
        pi.DepositRatio = 30m;
        pi.DepositAmount = 10.005m;       // USD 2 位精度 → 超精度

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositOverflowText, ex.Message);
    }

    [Fact]
    public void 定金校验_JPY显式定金必须为整数_拒绝小数()
    {
        var pi = Draft(Line(10m, 100m));
        pi.Currency = Currency.JPY;
        pi.ExchangeRate = 0.05m;
        pi.DepositRatio = 30m;
        pi.DepositAmount = 100.5m;        // JPY 0 位小数 → 超精度

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositOverflowText, ex.Message);
    }

    [Fact]
    public void 条款校验_金额乘法溢出_受控拒绝()
    {
        // 两行各自在声明列 DECIMAL(18,4) 上界内可表示，但合计取整后越过上界：受控拒绝（绝不落库为截断值）。
        var pi = Draft(
            Line(99_999_999_999_999.99m, 1m),
            Line(99_999_999_999_999.99m, 1m));

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.AmountOverflowText, ex.Message);
    }

    [Fact]
    public void 条款校验_行金额超出存储精度_受控拒绝()
    {
        // 数量取声明列上界（可表示），单价 2：乘积越过金额列上界 → 受控拒绝。
        var pi = Draft(Line(ProformaInvoiceCommercialValidationRules.MaxAmount, 2m));

        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(pi));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("超出存储精度", ex.Message);
    }

    [Fact]
    public void 整理_显式零定金_按币种精度自动计算()
    {
        var pi = Draft(Line(4m, 25m), Line(2m, 50m));   // 200
        pi.DepositRatio = 30m;
        pi.DepositAmount = 0m;

        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(pi);

        Assert.Equal(200m, pi.TotalAmount);
        Assert.Equal(1440m, pi.TotalAmountCny);         // 200 × 7.2
        Assert.Equal(60m, pi.DepositAmount);            // 200 × 30%
        Assert.Equal(new[] { 100m, 100m }, pi.Details.Select(d => d.Amount));
        Assert.Equal(new[] { 1, 2 }, pi.Details.Select(d => d.SortNo));
    }

    [Fact]
    public void 整理_显式正定金保留_但客户端篡改的行金额与合计被覆盖()
    {
        var pi = Draft(Line(10m, 100m));   // 客户端行金额故意伪造为 999
        pi.DepositRatio = 30m;
        pi.DepositAmount = 350m;           // 显式正定金（<= 总额 1000）保留
        pi.TotalAmount = 999_999m;         // 客户端伪造合计
        pi.TotalAmountCny = 1m;

        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(pi);

        Assert.Equal(1000m, pi.TotalAmount);
        Assert.Equal(7200m, pi.TotalAmountCny);
        Assert.Equal(350m, pi.DepositAmount);
        Assert.Equal(1000m, pi.Details.Single().Amount);
    }

    [Fact]
    public void 整理_JPY_行金额合计与自动定金按零位小数派生()
    {
        var pi = Draft(Line(3m, 10.5m), Line(1m, 100.4m));
        pi.Currency = Currency.JPY;
        pi.ExchangeRate = 0.05m;
        pi.DepositRatio = 30m;

        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(pi);

        Assert.Equal(new[] { 32m, 100m }, pi.Details.Select(d => d.Amount));   // 31.5 → 32；100.4 → 100
        Assert.Equal(132m, pi.TotalAmount);
        Assert.Equal(6.60m, pi.TotalAmountCny);                                // 132 × 0.05
        Assert.Equal(40m, pi.DepositAmount);                                   // 132 × 30% = 39.6 → JPY 0 位 0.5 进位
    }

    [Fact]
    public void 精度契约_与SQLServer模型实际列精度同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODELONLY;"
                          + "Integrated Security=true;TrustServerCertificate=true;")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;   // 仅构建模型，不打开连接

        Assert.Equal("decimal(18,6)", ColumnType(model, typeof(ProformaInvoice),
            nameof(ProformaInvoice.ExchangeRate)));

        foreach (var property in new[]
                 {
                     nameof(ProformaInvoice.TotalAmount), nameof(ProformaInvoice.TotalAmountCny),
                     nameof(ProformaInvoice.DepositRatio), nameof(ProformaInvoice.DepositAmount)
                 })
            Assert.Equal("decimal(18,4)", ColumnType(model, typeof(ProformaInvoice), property));

        foreach (var property in new[]
                 {
                     nameof(ProformaInvoiceDetail.Quantity), nameof(ProformaInvoiceDetail.UnitPrice),
                     nameof(ProformaInvoiceDetail.Amount)
                 })
            Assert.Equal("decimal(18,4)", ColumnType(model, typeof(ProformaInvoiceDetail), property));

        Assert.Equal(18, ProformaInvoiceCommercialValidationRules.DecimalPrecision);
        Assert.Equal(6, ProformaInvoiceCommercialValidationRules.ExchangeRateScale);
        Assert.Equal(4, ProformaInvoiceCommercialValidationRules.AmountScale);
        Assert.True(ProformaInvoiceCommercialValidationRules.IsExchangeRateRepresentable(
            ProformaInvoiceCommercialValidationRules.MaxExchangeRate));
        Assert.False(ProformaInvoiceCommercialValidationRules.IsExchangeRateRepresentable(
            ProformaInvoiceCommercialValidationRules.MaxExchangeRate + 0.000001m));
        Assert.True(ProformaInvoiceCommercialValidationRules.IsRepresentable(
            ProformaInvoiceCommercialValidationRules.MaxAmount));
        Assert.False(ProformaInvoiceCommercialValidationRules.IsRepresentable(
            ProformaInvoiceCommercialValidationRules.MaxAmount + 0.0001m));
        Assert.Equal(0, ProformaInvoiceCommercialValidationRules.AmountDecimals(Currency.JPY));
        Assert.Equal(2, ProformaInvoiceCommercialValidationRules.AmountDecimals(Currency.USD));
    }

    // ==================== 2. 控制器：新增 / 修改零写入零占号（内存库 + 既有身份） ====================

    [Fact]
    public async Task 新增_合法条款_服务端派生金额与自动定金且消耗单号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = NewController(db);

        var body = Body(Line(2m, 10m), Line(3m, 4m));   // 客户端行金额 / 合计一律伪造
        body.TotalAmount = 999_999m;
        body.TotalAmountCny = 1m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(32m, saved.TotalAmount);
        Assert.Equal(230.4m, saved.TotalAmountCny);
        Assert.Equal(9.6m, saved.DepositAmount);                    // 32 × 30%
        Assert.Equal(new[] { 20m, 12m }, db.ProformaInvoiceDetails
            .Where(d => d.PiId == saved.Id).OrderBy(d => d.SortNo).Select(d => d.Amount));
        Assert.StartsWith("PI", saved.PiNo);
        Assert.Equal(8L, SequenceOf(db, DocumentType.ProformaInvoice));   // 单号已消耗一次
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("rate")]
    [InlineData("quantity")]
    [InlineData("price")]
    [InlineData("negativeDeposit")]
    [InlineData("depositExceedsTotal")]
    [InlineData("depositOverprecision")]
    [InlineData("injectedDeletedLine")]
    public async Task 新增_非法条款_受控拒绝且零写入零占号(string caseName)
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = NewController(db);

        var body = Body(Line(10m, 100m));   // 总额 1000
        switch (caseName)
        {
            case "currency": body.Currency = (Currency)999; break;
            case "rate": body.ExchangeRate = 0m; break;
            case "quantity": body.Details[0].Quantity = 0m; break;
            case "price": body.Details[0].UnitPrice = -1m; break;
            case "negativeDeposit": body.DepositAmount = -5m; break;
            case "depositExceedsTotal": body.DepositAmount = 1000.01m; break;
            case "depositOverprecision": body.DepositAmount = 10.005m; break;
            case "injectedDeletedLine": body.Details.Add(Line(1m, 1m, deleted: true)); break;
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.ProformaInvoiceDetails);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 新增_显式负定金_拒绝且绝不静默按比例重算()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = NewController(db);
        var body = Body(Line(10m, 100m));
        body.DepositAmount = -1m;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositNegativeText, ex.Message);
        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 新增_空草稿仍可保存_但提交时要求至少一行有效明细()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = NewController(db);

        Assert.IsType<OkObjectResult>(await ctl.Create(Body()));   // 显式支持的空草稿
        var saved = db.ProformaInvoices.Single();
        Assert.Equal(0m, saved.TotalAmount);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(saved.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.EmptyOperationalDetailsText, ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 修改_非法条款_原子拒绝且原表头明细合计与单号不变()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var pi = SeedPi(db, "PI-EDIT-BAD", DocumentStatus.Pending, lines: new[] { Line(10m, 100m) });
        var originalDetailId = db.ProformaInvoiceDetails.AsNoTracking().Single().Id;
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(pi.Id, Body(Line(-1m, 100m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var saved = db.ProformaInvoices.AsNoTracking().Single();
        Assert.Equal("PI-EDIT-BAD", saved.PiNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(1000m, saved.TotalAmount);
        Assert.Equal(300m, saved.DepositAmount);
        var detail = db.ProformaInvoiceDetails.AsNoTracking().Single();
        Assert.Equal(originalDetailId, detail.Id);
        Assert.Equal(10m, detail.Quantity);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));   // 修改不消耗单号
    }

    [Fact]
    public async Task 提交_已持久化汇率非法_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-SUBMIT-BAD", DocumentStatus.Pending, exchangeRate: 0m,
            lines: new[] { Line(10m, 100m) });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.ExchangeRateText, ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-APPROVE-BAD", DocumentStatus.Submitted,
            currency: (Currency)777, lines: new[] { Line(10m, 100m) });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Approve(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.CurrencyText, ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转销售订单_已持久化数量非法_原子拒绝且零订单零占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var pi = SeedPi(db, "PI-ORDER-BAD", DocumentStatus.Approved, lines: new[] { Line(0m, 100m) });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(3L, SequenceOf(db, DocumentType.SalesOrder));
    }

    [Fact]
    public async Task 带入预填_已持久化条款非法_受控拒绝且不落库不占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var pi = SeedPi(db, "PI-PREFILL-BAD", DocumentStatus.Approved, exchangeRate: -1m,
            lines: new[] { Line(10m, 100m) });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).OrderPrefill(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(3L, SequenceOf(db, DocumentType.SalesOrder));
    }

    [Fact]
    public async Task 合法PI_提交审核转销售订单全流程正常()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var pi = SeedPi(db, "PI-FLOW-OK", DocumentStatus.Pending, lines: new[] { Line(10m, 100m) });
        var ctl = NewController(db);

        Assert.IsType<OkObjectResult>(await ctl.Submit(pi.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(pi.Id));
        Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(pi.Id));

        Assert.Equal(DocumentStatus.Completed, db.ProformaInvoices.AsNoTracking().Single().Status);
        var order = db.SalesOrders.Single();
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal(Currency.USD, order.Currency);
        Assert.Equal(7.2m, order.ExchangeRate);
        Assert.Equal(1000m, order.TotalAmount);
        Assert.Equal(300m, order.DepositAmount);
        Assert.Equal(4L, SequenceOf(db, DocumentType.SalesOrder));   // 单号已消耗一次
    }

    // ==================== 3. 工厂与种子数据 ====================

    /// <summary>
    /// ERP-490：播种既有**启用**客户（Id = 1，与既有夹具占位 <c>CustomerId</c> 对齐）——实时主数据引用复核
    /// 与调用形状无关后，进程内直调夹具同样必须引用真实存在的启用客户行（绝不提供请求形状 / 测试旁路）。
    /// 幂等：已存在即复用，绝不新增第二行、绝不回填 / 改派既有引用。
    /// </summary>
    private static BaseCustomer SeedLiveCustomer(ErpDbContext db, long id = 1L)
    {
        var existing = db.BaseCustomers.FirstOrDefault(c => c.Id == id);
        if (existing is not null) return existing;
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C-PI-490-{id}",
            CustomerName = "PI 商业条款测试客户",
            Status = 1,
            CreditStatus = "正常",
            DepositRatio = 30m,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ProformaInvoiceController NewController(ErpDbContext db)
    {
        SeedLiveCustomer(db);
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
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

    /// <summary>纯规则测试用的 PI 草稿（未落库、未预约单号）。</summary>
    private static ProformaInvoice Draft(params ProformaInvoiceDetail[] lines) => new()
    {
        PiNo = string.Empty,
        PiDate = new DateTime(2026, 9, 1),
        CustomerName = "PI 商业条款测试客户",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        DepositRatio = 30m,
        Status = DocumentStatus.Pending,
        Details = lines.ToList()
    };

    private static ProformaInvoice Body(params ProformaInvoiceDetail[] lines) => new()
    {
        PiDate = DateTime.Today,
        CustomerId = 1,
        CustomerName = "PI 商业条款测试客户",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        DepositRatio = 30m,
        Details = lines.ToList()
    };

    /// <summary>直接落库的 PI 种子（不经写入契约，允许构造非法历史条款用于承诺前复核测试）。</summary>
    private static ProformaInvoice SeedPi(ErpDbContext db, string no, DocumentStatus status,
        Currency currency = Currency.USD, decimal exchangeRate = 7.2m, decimal depositRatio = 30m,
        decimal depositAmount = 300m, params ProformaInvoiceDetail[] lines)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "客户 A",
            Currency = currency,
            ExchangeRate = exchangeRate,
            DepositRatio = depositRatio,
            DepositAmount = depositAmount,
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
        pi.TotalAmountCny = pi.TotalAmount * exchangeRate;
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static ProformaInvoiceDetail Line(decimal quantity, decimal unitPrice, bool deleted = false) => new()
    {
        ProductId = 1, ProductCode = "P-471", ProductName = "PI 商业条款商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99, IsDeleted = deleted
    };

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
}
