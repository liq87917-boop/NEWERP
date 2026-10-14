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
/// ERP-470 报价单商业条款（币种 / 汇率 / 数量 / 单价 / 金额 / 合计）校验与整理的单元测试
/// （纯内存库 + 直接实例化控制器，不连接 SQL Server、不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一权威契约</b>（<see cref="QuotationCommercialValidationRules"/>）：币种必须已定义、汇率 &gt; 0
/// 且在汇率列实际精度 <c>DECIMAL(18,6)</c> 内可表示、数量 &gt; 0、单价 ≥ 0 且均在数量 / 单价列实际精度
/// <c>DECIMAL(18,4)</c> 内可表示；软删除明细行注入拒绝；金额与合计一律由服务端按
/// <see cref="CurrencyAmountRules"/> 的币种精度（JPY 等 0 位、其余 2 位、0.5 进位）从有效明细派生；</item>
/// <item><b>EF 精度契约</b>：用 SQL Server 提供程序**仅构建模型**（不打开连接）断言报价单商业列的实际存储精度
/// 与规则常量同源；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与任何字段改写之前拒绝非法条款（零写入、零占号、原证据不变）；
/// 客户端伪造行金额 / 合计被服务端重算覆盖；提交 / 审核 / 转 PI / 转销售订单在既有行锁内复核已持久化条款，
/// 非法即原子拒绝且状态 / 下游单据 / 单号预约均不变；显式支持的空草稿仍可保存。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class QuotationCommercialValidationTests
{
    // ==================== 1. 唯一权威契约（纯规则） ====================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.5)]
    [InlineData(0.00005)]                 // 正数量但第 5 位小数超出 DECIMAL(18,4)
    [InlineData(1.00005)]
    [InlineData(100_000_000_000_000)]     // 超出 DECIMAL(18,4) 上界（合法 4 位小数之上限为 99_999_999_999_999.9999）
    public void 条款校验_数量非正或超出持久化精度_拒绝(decimal quantity)
    {
        var quotation = Draft(Line(quantity, 10m));

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(quotation));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(1.00005)]                 // 负单价之外：第 5 位小数超出 DECIMAL(18,4)
    [InlineData(100_000_000_000_000)]     // 超出 DECIMAL(18,4) 上界
    public void 条款校验_单价为负或超出持久化精度_拒绝(decimal unitPrice)
    {
        var quotation = Draft(Line(1m, unitPrice));

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(quotation));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负", ex.Message);
    }

    [Fact]
    public void 条款校验_币种未定义_拒绝()
    {
        var quotation = Draft(Line(1m, 1m));
        quotation.Currency = (Currency)999;

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(quotation));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.CurrencyText, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7.2)]
    [InlineData(7.1234567)]           // 第 7 位小数超出 DECIMAL(18,6)：落库会被静默取整
    [InlineData(1_000_000_000_000)]   // 超出 DECIMAL(18,6) 上界（MaxExchangeRate = 999_999_999_999.999999）
    public void 条款校验_汇率非正或不可表示_拒绝(decimal exchangeRate)
    {
        var quotation = Draft(Line(1m, 1m));
        quotation.ExchangeRate = exchangeRate;

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(quotation));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.ExchangeRateText, ex.Message);
    }

    [Fact]
    public void 条款校验_软删除明细行注入_拒绝()
    {
        var injected = Draft(Line(1m, 1m), Line(2m, 2m, deleted: true));

        var draft = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(injected));
        Assert.Equal(ErrorCodes.InvalidParameter, draft.Code);
        Assert.Equal(QuotationCommercialValidationRules.SoftDeletedDetailText, draft.Message);

        // 已持久化记录的条款复核按有效明细进行：历史软删除行本身不构成拒绝原因。
        QuotationCommercialValidationRules.ValidatePersisted(injected);
    }

    [Fact]
    public void 条款校验_空草稿可保存_提交与转换要求有效明细()
    {
        var empty = Draft(Array.Empty<QuotationDetail>());

        QuotationCommercialValidationRules.ValidateIncomingDraft(empty);

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateOperational(empty));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.EmptyOperationalDetailsText, ex.Message);
    }

    [Fact]
    public void 条款整理_JPY按零位小数取整_USD按两位小数取整_折人民币按CNY精度()
    {
        Assert.Equal(0, QuotationCommercialValidationRules.AmountDecimals(Currency.JPY));
        Assert.Equal(CurrencyAmountRules.PrecisionOf("JPY"), QuotationCommercialValidationRules.AmountDecimals(Currency.JPY));
        Assert.Equal(2, QuotationCommercialValidationRules.AmountDecimals(Currency.USD));

        // JPY：3 × 10.5 = 31.5 → 0 位小数 0.5 进位 = 32；1 × 100.4 = 100.4 → 100；合计 132
        var jpy = Draft(Line(3m, 10.5m), Line(1m, 100.4m));
        jpy.Currency = Currency.JPY;
        jpy.ExchangeRate = 0.05m;
        QuotationLineRules.Normalize(jpy);

        Assert.Equal(new[] { 32m, 100m }, jpy.Details.Select(d => d.Amount));
        Assert.Equal(132m, jpy.TotalAmount);
        Assert.Equal(6.60m, jpy.TotalAmountCny);            // 132 × 0.05，折人民币列按 CNY 的 2 位小数
        Assert.All(jpy.Details, d => Assert.Equal(d.Amount, decimal.Round(d.Amount, 0)));

        // USD：2.25 × 1.10 = 2.475 → 2 位小数 0.5 进位 = 2.48（绝不使用银行家舍入）
        var usd = Draft(Line(2.25m, 1.10m));
        QuotationLineRules.Normalize(usd);

        Assert.Equal(2.48m, usd.Details.Single().Amount);
        Assert.Equal(2.48m, usd.TotalAmount);
        Assert.Equal(17.86m, usd.TotalAmountCny);           // 2.48 × 7.2 = 17.856 → CNY 2 位小数 0.5 进位
    }

    [Fact]
    public void 条款整理_客户端伪造行金额合计与行号_一律被服务端派生覆盖()
    {
        var quotation = Draft(Line(2m, 10m), Line(3m, 4m));
        quotation.Id = 999;
        quotation.TotalAmount = 999_999m;
        quotation.TotalAmountCny = 1m;
        quotation.Details[0].Amount = 888m;
        quotation.Details[0].SortNo = 99;

        QuotationLineRules.Normalize(quotation);

        Assert.Equal(new[] { 1, 2 }, quotation.Details.Select(d => d.SortNo));
        Assert.Equal(new[] { 20m, 12m }, quotation.Details.Select(d => d.Amount));
        Assert.Equal(32m, quotation.TotalAmount);
        Assert.Equal(230.4m, quotation.TotalAmountCny);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void 条款校验_金额超出持久化精度或乘法溢出_受控拒绝(int scenario)
    {
        // ERP-533：数量 / 单价列实际为 DECIMAL(18,4)，但派生的行金额仍必须落在 4 位小数金额列容量内。
        var quotation = Draft(scenario == 0
            ? Line(QuotationCommercialValidationRules.MaxAmount, 10m)                  // 行金额越界
            : Line(QuotationCommercialValidationRules.MaxAmount,                        // 乘积远超单列容量
                QuotationCommercialValidationRules.MaxAmount));

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(quotation));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.True(ex.Message.Contains("金额", StringComparison.Ordinal)
                    || ex.Message.Contains("DECIMAL(18,4)", StringComparison.Ordinal));
    }

    [Fact]
    public void 条款校验_金额列上界边界_通过_超出边界拒绝()
    {
        // 金额 / 合计列为 DECIMAL(18,4)：可取整到列容量内的金额照常放行，
        // 半进位取整越过上界、本身越界或折人民币放大越界一律受控拒绝（绝不静默截断）。
        const decimal amountCeiling = 99_999_999_999_999.99m;                       // 2 位小数可取整到的最大金额
        var boundary = Draft(Line(1m, amountCeiling));
        boundary.ExchangeRate = 1m;                                                 // 折人民币与总额同源，避免汇率放大越界
        QuotationCommercialValidationRules.NormalizeIncomingDraft(boundary);

        Assert.Equal(amountCeiling, boundary.TotalAmount);
        Assert.Equal(amountCeiling, boundary.TotalAmountCny);
        Assert.True(QuotationCommercialValidationRules.IsRepresentable(
            QuotationCommercialValidationRules.MaxAmount));                         // 4 位小数上界本身可表示
        Assert.False(QuotationCommercialValidationRules.IsRepresentable(
            QuotationCommercialValidationRules.MaxAmount + 0.0001m));               // 越过 DECIMAL(18,4) 上界
        Assert.False(QuotationCommercialValidationRules.IsRepresentable(decimal.MinValue));
        Assert.False(QuotationCommercialValidationRules.IsLineAmountRepresentable(
            QuotationCommercialValidationRules.MaxAmount, QuotationCommercialValidationRules.MaxAmount));

        // 半进位取整后恰好越过上界同样受控拒绝。
        var roundingOver = Draft(Line(1m, 99_999_999_999_999.995m));
        roundingOver.ExchangeRate = 1m;
        var roundingError = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(roundingOver));
        Assert.Equal(ErrorCodes.InvalidParameter, roundingError.Code);

        // 折人民币按汇率放大后越界同样受控拒绝。
        var inflated = Draft(Line(1m, amountCeiling));
        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(inflated));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.AmountOverflowText, ex.Message);
    }

    [Fact]
    public void 契约文案_与唯一权威口径同源()
    {
        Assert.Contains("QuotationCommercialValidationRules", QuotationCommercialValidationRules.ContractText);
        Assert.Contains("CurrencyAmountRules", QuotationCommercialValidationRules.ContractText);
        Assert.Contains("绝不采信客户端", QuotationCommercialValidationRules.ContractText);
    }

    // ==================== 2. EF 实际精度契约（只构建模型，不打开连接） ====================

    [Fact]
    public void EF实际精度_报价单数量金额与汇率列_与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        var amountType = $"decimal({QuotationCommercialValidationRules.DecimalPrecision},"
                         + $"{QuotationCommercialValidationRules.AmountScale})";
        var rateType = $"decimal({QuotationCommercialValidationRules.DecimalPrecision},"
                       + $"{QuotationCommercialValidationRules.ExchangeRateScale})";

        Assert.Equal(amountType, ColumnType(model, typeof(QuotationDetail), nameof(QuotationDetail.Quantity)));
        Assert.Equal(amountType, ColumnType(model, typeof(QuotationDetail), nameof(QuotationDetail.UnitPrice)));
        Assert.Equal(amountType, ColumnType(model, typeof(QuotationDetail), nameof(QuotationDetail.Amount)));
        Assert.Equal(amountType, ColumnType(model, typeof(Quotation), nameof(Quotation.TotalAmount)));
        Assert.Equal(amountType, ColumnType(model, typeof(Quotation), nameof(Quotation.TotalAmountCny)));
        Assert.Equal(rateType, ColumnType(model, typeof(Quotation), nameof(Quotation.ExchangeRate)));
    }

    // ==================== 3. 控制器：新增 / 修改（零写入、零占号） ====================

    [Fact]
    public async Task 新增_非法条款_零写入零占号_单号流水不变()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db);
        var controller = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Draft(Line(0m, 10m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Empty(db.Quotations);
        Assert.Empty(db.QuotationDetails);
        Assert.Equal(7L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.Quotation)
            .CurrentSequence);                                    // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task 新增_未定义币种与软删除明细注入_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var controller = NewController(db);

        var badCurrency = Draft(Line(1m, 1m));
        badCurrency.Currency = (Currency)999;
        var currencyError = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(badCurrency));
        Assert.Equal(QuotationCommercialValidationRules.CurrencyText, currencyError.Message);

        var injected = Draft(Line(1m, 1m), Line(1m, 1m, deleted: true));
        var injectionError = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(injected));
        Assert.Equal(QuotationCommercialValidationRules.SoftDeletedDetailText, injectionError.Message);

        Assert.Empty(db.Quotations);
        Assert.Empty(db.QuotationDetails);
    }

    [Fact]
    public async Task 新增_空草稿_作为待提交草稿保存且不生成明细()
    {
        using var db = TestDbFactory.Create();
        var empty = Draft(Array.Empty<QuotationDetail>());

        Assert.IsType<OkObjectResult>(await NewController(db).Create(empty));

        var saved = db.Quotations.Single();
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.StartsWith("QT", saved.QuotationNo);
        Assert.Equal(0m, saved.TotalAmount);
        Assert.Empty(db.QuotationDetails);
    }

    [Fact]
    public async Task 新增_客户端伪造合计与行金额_服务端按币种精度派生并忽略客户端值()
    {
        using var db = TestDbFactory.Create();
        var entity = Draft(Line(2m, 10m), Line(3m, 4m));
        entity.TotalAmount = 999_999m;
        entity.TotalAmountCny = 1m;
        entity.Details[0].Amount = 888m;
        entity.Details[0].SortNo = 99;

        Assert.IsType<OkObjectResult>(await NewController(db).Create(entity));

        var saved = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(new[] { 1, 2 }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.SortNo));
        Assert.Equal(new[] { 20m, 12m }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.Amount));
        Assert.Equal(32m, saved.TotalAmount);
        Assert.Equal(230.4m, saved.TotalAmountCny);           // 32 × 7.2
    }

    [Fact]
    public async Task 新增_JPY报价单_行金额与合计按零位小数落库_折人民币两位()
    {
        using var db = TestDbFactory.Create();
        var entity = Draft(Line(3m, 10.5m), Line(1m, 100.4m));
        entity.Currency = Currency.JPY;
        entity.ExchangeRate = 0.05m;

        Assert.IsType<OkObjectResult>(await NewController(db).Create(entity));

        var saved = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(new[] { 32m, 100m }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.Amount));
        Assert.Equal(132m, saved.TotalAmount);
        Assert.Equal(6.60m, saved.TotalAmountCny);
        Assert.Equal(Currency.JPY, saved.Currency);
    }

    [Fact]
    public async Task 修改_非法明细_原表头明细合计与单号全部不变()
    {
        using var db = TestDbFactory.Create();
        var original = Seed(db, "QT-KEEP", DocumentStatus.Pending, Line(2m, 10m));
        var oldDetailId = db.QuotationDetails.Single().Id;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).Update(original.Id, Draft(Line(-3m, 5m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal("QT-KEEP", after.QuotationNo);
        Assert.Equal("义乌客户（商业条款测试）", after.CustomerName);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(20m, after.TotalAmount);
        Assert.Equal(144m, after.TotalAmountCny);
        var line = Assert.Single(after.Details);
        Assert.Equal(oldDetailId, line.Id);
        Assert.Equal(2m, line.Quantity);
    }

    // ==================== 4. 提交 / 审核 / 转换（下游承诺复核已持久化条款） ====================

    [Fact]
    public async Task 提交_已持久化数量非法_原子拒绝且状态与证据不变()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-BAD-QTY", DocumentStatus.Pending, Line(0m, 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        var after = db.Quotations.AsNoTracking().Single();
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(0m, after.TotalAmount);
        Assert.Equal(1, db.QuotationDetails.Count());
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-BAD-CUR", DocumentStatus.Submitted, Line(1m, 10m));
        var stored = db.Quotations.Single(q => q.Id == quotation.Id);
        stored.Currency = (Currency)777;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Approve(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.CurrencyText, ex.Message);
        Assert.Equal(DocumentStatus.Submitted,
            db.Quotations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转PI_已持久化条款非法_拒绝且零PI零占号()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db);
        var quotation = Seed(db, "QT-PI-BAD", DocumentStatus.Approved, Line(-1m, 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.ProformaInvoiceDetails);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
        Assert.Equal(7L, db.SysDocumentNumberRules.Single(r => r.DocumentType == DocumentType.Quotation)
            .CurrentSequence);
    }

    [Fact]
    public async Task 转销售订单_已持久化单价为负_拒绝且零订单零占号()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-SO-BAD", DocumentStatus.Approved, Line(1m, -5m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db).ToSalesOrder(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负", ex.Message);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SalesOrderDetails);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 合法报价单_审核与转PI仍正常流转()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-GOOD", DocumentStatus.Pending, Line(2m, 10m));
        var controller = NewController(db);

        Assert.IsType<OkObjectResult>(await controller.Approve(quotation.Id));
        Assert.IsType<OkObjectResult>(await controller.ToProformaInvoice(quotation.Id));

        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single().Status);
        var pi = db.ProformaInvoices.Include(p => p.Details).Single();
        Assert.Equal(20m, pi.TotalAmount);
        Assert.Equal(2m, pi.Details.Single().Quantity);
    }

    // ==================== 工厂与种子数据 ====================

    private static QuotationController NewController(ErpDbContext db)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static void SeedQuotationNumberRule(ErpDbContext db, long current = 7)
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

    /// <summary>直接落库的报价单种子（不经契约，允许构造非法历史条款用于承诺前复核测试）。</summary>
    private static Quotation Seed(ErpDbContext db, string no, DocumentStatus status,
        params QuotationDetail[] lines)
    {
        var quotation = Draft(lines);
        quotation.QuotationNo = no;
        quotation.Status = status;
        quotation.Details = lines.ToList();
        var line = 0;
        foreach (var detail in quotation.Details)
        {
            detail.SortNo = ++line;
            detail.Amount = detail.Quantity * detail.UnitPrice;
        }
        quotation.TotalAmount = quotation.Details.Sum(d => d.Amount);
        quotation.TotalAmountCny = quotation.TotalAmount * quotation.ExchangeRate;
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static Quotation Draft(params QuotationDetail[] lines) => new()
    {
        QuotationNo = string.Empty,
        QuotationDate = new DateTime(2026, 9, 1),
        CustomerName = "义乌客户（商业条款测试）",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        Status = DocumentStatus.Pending,
        Details = lines.ToList()
    };

    private static QuotationDetail Line(decimal quantity, decimal unitPrice, bool deleted = false) => new()
    {
        ProductId = 1, ProductCode = "P-470", ProductName = "商业条款商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99, IsDeleted = deleted
    };

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
}


