using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Globalization;
using System.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-533 报价单汇率 / 数量 / 单价 / 行金额的**持久化精度**（声明列 <c>DECIMAL(18,6)</c> 与
/// <c>DECIMAL(18,4)</c>）边界单元测试（内存库 + 真实启用身份 + 真实控制器）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：<c>Quotation.ExchangeRate</c> 显式 <c>decimal(18,6)</c>，
/// <c>TotalAmount</c> / <c>TotalAmountCny</c> 与 <c>QuotationDetail.Quantity</c> / <c>UnitPrice</c> /
/// <c>Amount</c> 显式 <c>decimal(18,4)</c>（仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>标量可表示性纯判定：恰好 6 位小数汇率 / 4 位小数数量 · 单价与量级边界放行；第 7 位小数汇率、
/// 第 5 位小数数量 · 单价、量级越界一律拒绝（绝不静默取整）；</item>
/// <item>契约边界：六位小数汇率上限、四位小数数量 · 单价上限原样接受；服务端仍按币种精度派生行金额 / 合计 /
/// 折人民币（取整口径不变）；半进位取整越过金额列上界受控拒绝；</item>
/// <item>真实控制器新增 / 修改：超精度 / 越界请求零写入、零占号、权威行逐字段不变；提交 / 转 PI 对已持久化
/// 非法条款原子拒绝且状态 / 下游 / 单号预约不变；</item>
/// <item>源码契约：EF 映射、规则常量与「护栏先于占号与任何写入」的接线逐字同源。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返」由
/// <c>ERP.IntegrationTests/QuotationPersistedNumericBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class QuotationPersistedNumericBoundsTests
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private const decimal MaxAmount = QuotationCommercialValidationRules.MaxAmount;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（与规则常量同源）。</summary>
    private const decimal MaxRate = QuotationCommercialValidationRules.MaxExchangeRate;

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP533_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(18, QuotationCommercialValidationRules.DecimalPrecision);
        Assert.Equal(6, QuotationCommercialValidationRules.ExchangeRateScale);
        Assert.Equal(4, QuotationCommercialValidationRules.AmountScale);
        Assert.Equal(999_999_999_999.999999m, MaxRate);
        Assert.Equal(99_999_999_999_999.9999m, MaxAmount);

        AssertColumn(db.Model, typeof(Quotation), nameof(Quotation.ExchangeRate), 6);
        AssertColumn(db.Model, typeof(Quotation), nameof(Quotation.TotalAmount), 4);
        AssertColumn(db.Model, typeof(Quotation), nameof(Quotation.TotalAmountCny), 4);
        AssertColumn(db.Model, typeof(QuotationDetail), nameof(QuotationDetail.Quantity), 4);
        AssertColumn(db.Model, typeof(QuotationDetail), nameof(QuotationDetail.UnitPrice), 4);
        AssertColumn(db.Model, typeof(QuotationDetail), nameof(QuotationDetail.Amount), 4);
    }

    /// <summary>
    /// 模型映射必须与 SchemaUpgrader 第 20 段的权威建表脚本逐字一致；若退回 EF 默认 <c>decimal(18,2)</c>，
    /// 合法的六位小数汇率与四位小数数量 / 单价会在写入前被静默取整。
    /// </summary>
    private static void AssertColumn(IModel model, Type entityType, string propertyName, int scale)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.Equal(18, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
        Assert.Equal($"decimal(18,{scale})", property.GetColumnType());
    }

    // ==================== 2. 可表示性纯判定 ====================

    [Theory]
    [InlineData("0")]
    [InlineData("0.0001")]
    [InlineData("1.2345")]
    [InlineData("-1.2345")]
    [InlineData("99999999999999.9999")]     // 恰好 4 位小数上界
    [InlineData("-99999999999999.9999")]
    public void 可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(QuotationCommercialValidationRules.IsRepresentable(Raw(raw)));

    [Theory]
    [InlineData("1.00005")]                 // 第 5 位小数
    [InlineData("0.00001")]
    [InlineData("100000000000000")]         // 1e14 量级越界
    [InlineData("100000000000000.0000")]
    public void 不可表示_过小数位或越界(string raw)
        => Assert.False(QuotationCommercialValidationRules.IsRepresentable(Raw(raw)));

    [Theory]
    [InlineData("0.000001")]
    [InlineData("7.123456")]
    [InlineData("999999999999.999999")]     // 恰好 6 位小数上界
    [InlineData("-999999999999.999999")]
    public void 汇率可表示_六位小数与量级边界内放行(string raw)
        => Assert.True(QuotationCommercialValidationRules.IsExchangeRateRepresentable(Raw(raw)));

    [Theory]
    [InlineData("7.1234567")]               // 第 7 位小数
    [InlineData("0.0000001")]
    [InlineData("1000000000000")]           // 1e12 量级越界（上界 999999999999.999999）
    public void 汇率不可表示_过小数位或越界(string raw)
        => Assert.False(QuotationCommercialValidationRules.IsExchangeRateRepresentable(Raw(raw)));

    // ==================== 3. 契约边界（标量上限与派生金额） ====================

    [Fact]
    public void 契约_六位小数汇率与四位小数单价_原样接受且金额按币种精度派生()
    {
        // 任务描述的两个具体例子：六位小数汇率 7.123456、四位小数单价 0.0125。
        var quotation = Draft(Line(2m, 0.0125m));
        quotation.ExchangeRate = 7.123456m;
        QuotationCommercialValidationRules.NormalizeIncomingDraft(quotation);

        Assert.Equal(7.123456m, quotation.ExchangeRate);
        Assert.Equal(0.0125m, quotation.Details.Single().UnitPrice);
        Assert.Equal(0.03m, quotation.Details.Single().Amount);        // 2 × 0.0125 = 0.025 → USD 2 位 0.5 进位
        Assert.Equal(0.03m, quotation.TotalAmount);
        Assert.Equal(0.21m, quotation.TotalAmountCny);                 // 0.03 × 7.123456 = 0.2137 → CNY 2 位
    }

    [Fact]
    public void 契约_六位小数汇率上限_原样接受且折人民币落在有界金额列内()
    {
        var quotation = Draft(Line(1m, 1m));
        quotation.ExchangeRate = MaxRate;
        QuotationCommercialValidationRules.NormalizeIncomingDraft(quotation);

        Assert.Equal(MaxRate, quotation.ExchangeRate);
        Assert.Equal(1_000_000_000_000.00m, quotation.TotalAmountCny);  // 1 × 999999999999.999999 → CNY 2 位
    }

    [Fact]
    public void 契约_四位小数数量与单价上限_原样接受且派生金额落在有界金额列内()
    {
        var quantityMax = Draft(Line(MaxAmount, 0.0001m));
        quantityMax.ExchangeRate = 1m;
        QuotationCommercialValidationRules.NormalizeIncomingDraft(quantityMax);

        Assert.Equal(MaxAmount, quantityMax.Details.Single().Quantity);
        // 99999999999999.9999 × 0.0001 = 9999999999.99999999 → USD 2 位 0.5 进位
        Assert.Equal(10_000_000_000.00m, quantityMax.Details.Single().Amount);
        Assert.Equal(10_000_000_000.00m, quantityMax.TotalAmount);
        Assert.Equal(10_000_000_000.00m, quantityMax.TotalAmountCny);

        var priceMax = Draft(Line(0.0001m, MaxAmount));
        priceMax.ExchangeRate = 1m;
        QuotationCommercialValidationRules.NormalizeIncomingDraft(priceMax);

        Assert.Equal(MaxAmount, priceMax.Details.Single().UnitPrice);
        Assert.Equal(10_000_000_000.00m, priceMax.TotalAmount);
    }

    [Fact]
    public void 契约_半进位取整越过金额列上界_受控拒绝()
    {
        // 1 × 99999999999999.995 取整到 USD 2 位（0.5 进位）= 100000000000000.00，越过 DECIMAL(18,4) 上界。
        var quotation = Draft(Line(1m, 99_999_999_999_999.995m));
        quotation.ExchangeRate = 1m;

        var ex = Assert.Throws<BusinessException>(() =>
            QuotationCommercialValidationRules.ValidateIncomingDraft(quotation));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("金额", ex.Message, StringComparison.Ordinal);
    }

    // ==================== 4. 控制器：新增 / 修改 / 承诺（零写入、零占号、原证据不变） ====================

    [Theory]
    [InlineData("7.1234567", "1", "1")]              // 第 7 位小数汇率
    [InlineData("1000000000000", "1", "1")]          // 汇率量级越界
    [InlineData("0", "1", "1")]                      // 零汇率
    [InlineData("-7.2", "1", "1")]                   // 负汇率
    [InlineData("1", "0.00005", "1")]                // 第 5 位小数数量
    [InlineData("1", "100000000000000", "1")]        // 数量量级越界
    [InlineData("1", "-1", "1")]                     // 负数量
    [InlineData("1", "1", "1.00005")]                // 第 5 位小数单价
    [InlineData("1", "1", "100000000000000")]        // 单价量级越界
    [InlineData("1", "1", "-0.01")]                  // 负单价
    public async Task 新增_超精度或越界_拒绝且零写入零占号(string rate, string quantity, string unitPrice)
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db);
        var entity = Draft(Line(Raw(quantity), Raw(unitPrice)));
        entity.ExchangeRate = Raw(rate);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Create(entity));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.Quotations);
        Assert.Empty(db.QuotationDetails);
        Assert.Equal(7L, db.SysDocumentNumberRules
            .Single(r => r.DocumentType == DocumentType.Quotation).CurrentSequence);   // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task 修改_超精度条款_拒绝且原表头与明细不变()
    {
        using var db = TestDbFactory.Create();
        var original = Seed(db, "QT-NUM-KEEP", DocumentStatus.Pending, Line(2m, 10m));
        var oldDetailId = db.QuotationDetails.Single().Id;

        var update = Draft(Line(1.00005m, 10m));                 // 第 5 位小数数量
        update.ExchangeRate = 7.1234567m;                        // 第 7 位小数汇率
        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Update(original.Id, update));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(7.2m, after.ExchangeRate);
        Assert.Equal(20m, after.TotalAmount);
        Assert.Equal(144m, after.TotalAmountCny);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        var line = Assert.Single(after.Details);
        Assert.Equal(oldDetailId, line.Id);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(10m, line.UnitPrice);
    }

    [Fact]
    public async Task 提交_已持久化第七位小数汇率_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-NUM-SUBMIT", DocumentStatus.Pending, Line(2m, 10m));
        // 绕过写入契约直接改库，模拟非法历史条款（内存库保留原值，承诺前复核必须拒绝）。
        var stored = db.Quotations.Single(q => q.Id == quotation.Id);
        stored.ExchangeRate = 7.1234567m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(QuotationCommercialValidationRules.ExchangeRateText, ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.Quotations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转PI_已持久化第五位小数数量_原子拒绝且零PI零占号()
    {
        using var db = TestDbFactory.Create();
        SeedQuotationNumberRule(db);
        var quotation = Seed(db, "QT-NUM-PI", DocumentStatus.Approved, Line(2m, 10m));
        db.QuotationDetails.Single().Quantity = 0.00005m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToProformaInvoice(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message, StringComparison.Ordinal);
        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.ProformaInvoiceDetails);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
        Assert.Equal(7L, db.SysDocumentNumberRules
            .Single(r => r.DocumentType == DocumentType.Quotation).CurrentSequence);
    }

    // ==================== 5. 源码契约（EF 映射 / 规则常量 / 接线先于占号与写入） ====================

    [Fact]
    public void 源码契约_EF映射与护栏接线与声明同源()
    {
        var config = ReadSource("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");
        Assert.Contains("modelBuilder.Entity<Quotation>().Property(x => x.ExchangeRate).HasPrecision(18, 6)", config);
        Assert.Contains("modelBuilder.Entity<Quotation>().Property(x => x.TotalAmount).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<Quotation>().Property(x => x.TotalAmountCny).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<QuotationDetail>().Property(x => x.Quantity).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<QuotationDetail>().Property(x => x.UnitPrice).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<QuotationDetail>().Property(x => x.Amount).HasPrecision(18, 4)", config);

        var contract = ReadSource("src", "ERP.Application", "Services", "QuotationCommercialValidationRules.cs");
        Assert.Contains("ExchangeRateScale = 6", contract);
        Assert.Contains("AmountScale = 4", contract);
        Assert.Contains("IsExchangeRateRepresentable", contract);

        // 新增 / 修改仍在单号预约、字段改写与任何写入之前先按契约校验（被拒绝的请求零写入、零占号）。
        var controller = ReadSource("src", "ERP.Api", "Controllers", "QuotationController.cs");
        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart);
        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("QuotationCommercialValidationRules.ValidateIncomingDraft(entity)", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));
        var update = controller[updateStart..];
        Assert.True(update.IndexOf("QuotationCommercialValidationRules.ValidateIncomingDraft(entity)", StringComparison.Ordinal)
                    < update.IndexOf("QuotationLineRules.Normalize(entity)", StringComparison.Ordinal));
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
        QuotationDate = new DateTime(2026, 10, 1),
        CustomerName = "ERP533 客户",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        Status = DocumentStatus.Pending,
        Details = lines.ToList()
    };

    private static QuotationDetail Line(decimal quantity, decimal unitPrice) => new()
    {
        ProductId = 1, ProductCode = "P-533", ProductName = "持久化精度商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99, IsDeleted = false
    };

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));

    private static decimal Raw(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
