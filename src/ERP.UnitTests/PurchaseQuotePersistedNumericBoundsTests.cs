using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-528 供应商比价（<c>api/purchase/quotes</c>）数量 / 单价 / 服务端重算总额的**持久化精度**边界单元测试
/// （内存库 + 真实启用身份 + 真实控制器）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：<c>PurchaseQuote.Quantity</c> / <c>QuotePrice</c> / <c>TotalAmount</c>
/// 显式 <c>decimal(18,4)</c>（仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>草稿校验：恰好 4 位小数原样接受、第 5 位小数与越界量受控拒绝、量级边界放行 / 越界拒绝、
/// 服务端重算总额溢出受控拒绝（均沿用既有 <c>InvalidParameter</c> 文案，绝不静默取整）；</item>
/// <item>真实控制器新增 / 修改：越界请求零写入、权威行逐字段（含审计时间戳）不变；
/// 合法的 4 位小数报价原样落库；</item>
/// <item>源码契约：EF 映射与权威列声明逐字同源、护栏先于任何写入。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返」由
/// <c>ERP.IntegrationTests/PurchaseQuotePersistedNumericBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class PurchaseQuotePersistedNumericBoundsTests
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示绝对值（与规则常量同源）。</summary>
    private const decimal Max4 = PurchaseQuoteMutationRules.MaxDecimal18Scale4;

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP528_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(18, PurchaseQuoteMutationRules.DecimalPrecision);
        Assert.Equal(4, PurchaseQuoteMutationRules.DecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        AssertPurchaseQuoteColumn(db.Model, nameof(PurchaseQuote.Quantity));
        AssertPurchaseQuoteColumn(db.Model, nameof(PurchaseQuote.QuotePrice));
        AssertPurchaseQuoteColumn(db.Model, nameof(PurchaseQuote.TotalAmount));
    }

    /// <summary>
    /// 模型映射必须与 SchemaUpgrader 第 15 段的权威建表脚本（<c>DECIMAL(18,4)</c>）逐字一致；
    /// 若退回 EF 默认 <c>decimal(18,2)</c>，合法的 4 位小数报价会在写入前被静默取整。
    /// </summary>
    private static void AssertPurchaseQuoteColumn(IModel model, string propertyName)
    {
        var property = model.FindEntityType(typeof(PurchaseQuote))!.FindProperty(propertyName)!;
        Assert.Equal(PurchaseQuoteMutationRules.DecimalPrecision, property.GetPrecision());
        Assert.Equal(PurchaseQuoteMutationRules.DecimalScale, property.GetScale());
        Assert.Equal("decimal(18,4)", property.GetColumnType());
    }

    // ==================== 2. 可表示性边界（纯函数） ====================

    [Theory]
    [InlineData("0")]
    [InlineData("1.2345")]
    [InlineData("-1.2345")]
    [InlineData("4.0000")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(PurchaseQuoteMutationRules.IsRepresentable(decimal.Parse(raw,
            System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1.00005")]              // 第 5 位小数
    [InlineData("0.00001")]
    [InlineData("100000000000000")]      // 量级越界（1e14）
    [InlineData("100000000000000.0000")] // 量级越界
    public void 不可表示_过小数位或越界(string raw)
        => Assert.False(PurchaseQuoteMutationRules.IsRepresentable(decimal.Parse(raw,
            System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void 逐位原样校验_不可表示受控拒绝_可表示原值返回()
    {
        Assert.Equal(1.2345m, PurchaseQuoteMutationRules.EnsureRepresentable(1.2345m, "x"));

        var ex = Assert.Throws<BusinessException>(
            () => PurchaseQuoteMutationRules.EnsureRepresentable(1.00005m, "自定义文案"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal("自定义文案", ex.Message);
    }

    // ==================== 3. 草稿校验：四位小数 / 第 5 位 / 量级 / 重算溢出 ====================

    [Fact]
    public void 草稿校验_四位小数原样接受_服务端重算总额()
    {
        var quote = NewDraft("PQ-PREC-OK");
        quote.Quantity = 4.0000m;
        quote.QuotePrice = 1.2345m;
        quote.TotalAmount = -1m; // 客户端伪造总额，服务端按 4.0000 × 1.2345 重算

        PurchaseQuoteMutationRules.ValidateDraft(quote);

        Assert.Equal(4.0000m, quote.Quantity);
        Assert.Equal(1.2345m, quote.QuotePrice);
        Assert.Equal(4.9380m, quote.TotalAmount);
    }

    [Fact]
    public void 草稿校验_第5位小数_数量与单价受控拒绝()
    {
        var quote = NewDraft("PQ-PREC-QTY");
        quote.Quantity = 1.00005m;
        var quantity = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(ErrorCodes.InvalidParameter, quantity.Code);
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, quantity.Message);

        quote = NewDraft("PQ-PREC-PRICE");
        quote.QuotePrice = 1.23456m;
        var price = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(ErrorCodes.InvalidParameter, price.Code);
        Assert.Equal(PurchaseQuoteMutationRules.PriceText, price.Message);
    }

    [Fact]
    public void 草稿校验_量级边界_精确上限放行_超出最小单位拒绝()
    {
        var atMax = NewDraft("PQ-MAX-OK");
        atMax.Quantity = Max4;
        atMax.QuotePrice = 1.0000m;
        PurchaseQuoteMutationRules.ValidateDraft(atMax);
        Assert.Equal(Max4, atMax.TotalAmount);              // 1.0000 × 上限 = 上限（原样）

        var overQuantity = NewDraft("PQ-MAX-QTY");
        overQuantity.Quantity = Max4 + 0.0001m;
        overQuantity.QuotePrice = 1.0000m;
        var quantity = Assert.Throws<BusinessException>(
            () => PurchaseQuoteMutationRules.ValidateDraft(overQuantity));
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, quantity.Message);

        var overPrice = NewDraft("PQ-MAX-PRICE");
        overPrice.Quantity = 1.0000m;
        overPrice.QuotePrice = Max4 + 0.0001m;
        var price = Assert.Throws<BusinessException>(
            () => PurchaseQuoteMutationRules.ValidateDraft(overPrice));
        Assert.Equal(PurchaseQuoteMutationRules.PriceText, price.Message);
    }

    [Fact]
    public void 草稿校验_重算总额溢出_受控拒绝()
    {
        var quote = NewDraft("PQ-TOTAL-OVERFLOW");
        quote.Quantity = Max4;                              // 各自在上限内
        quote.QuotePrice = Max4;                            // 但乘积超出 DECIMAL(18,4)

        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteMutationRules.ValidateDraft(quote));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseQuoteMutationRules.TotalText, ex.Message);
    }

    // ==================== 4. 控制器新增 / 修改：拒绝零写入、合法四位小数原样落库 ====================

    [Fact]
    public async Task 新增_第5位小数与越界与重算溢出_拒绝且零比价行零审批行()
    {
        using var db = TestDbFactory.Create();
        var ctl = PurchaseQuoteTestAuthority.NewQuoteController(db);

        var fifthDecimal = NewDraft("PQ-CREATE-5");
        fifthDecimal.Quantity = 1.00005m;
        var precision = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(fifthDecimal));
        Assert.Equal(ErrorCodes.InvalidParameter, precision.Code);

        var overflowing = NewDraft("PQ-CREATE-MAX");
        overflowing.Quantity = 100_000_000_000_000m;
        var overflow = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(overflowing));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);

        var recompute = NewDraft("PQ-CREATE-RECOMPUTE");
        recompute.Quantity = Max4;
        recompute.QuotePrice = Max4;
        var total = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(recompute));
        Assert.Equal(PurchaseQuoteMutationRules.TotalText, total.Message);

        Assert.Empty(db.PurchaseQuotes);
        Assert.Empty(db.PurchaseQuoteDecisions);
    }

    [Fact]
    public async Task 新增_合法四位小数_原样落库_服务端重算总额()
    {
        using var db = TestDbFactory.Create();
        var draft = NewDraft("PQ-CREATE-4DP");
        draft.Quantity = 4.0000m;
        draft.QuotePrice = 1.2345m;
        draft.TotalAmount = 999m; // 伪造总额被覆盖

        var created = AssertOk<PurchaseQuote>(await PurchaseQuoteTestAuthority.NewQuoteController(db).Create(draft));

        Assert.Equal(4.0000m, created.Quantity);
        Assert.Equal(1.2345m, created.QuotePrice);
        Assert.Equal(4.9380m, created.TotalAmount);

        var stored = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(4.0000m, stored.Quantity);
        Assert.Equal(1.2345m, stored.QuotePrice);
        Assert.Equal(4.9380m, stored.TotalAmount);
    }

    [Fact]
    public async Task 修改_越界拒绝_权威行逐字段与审计时间戳不变_随后合法四位小数更新成功()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedPendingQuote(db, "PQ-UPD-BOUND");
        var ctl = PurchaseQuoteTestAuthority.NewQuoteController(db);
        var before = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id);

        var fifthDecimal = Clone(quote);
        fifthDecimal.Quantity = 1.00005m;
        var precision = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(quote.Id, fifthDecimal));
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, precision.Message);

        var overflow = Clone(quote);
        overflow.Quantity = Max4;
        overflow.QuotePrice = Max4;
        var total = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(quote.Id, overflow));
        Assert.Equal(PurchaseQuoteMutationRules.TotalText, total.Message);

        var after = db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id);
        Assert.Equal(before.Quantity, after.Quantity);
        Assert.Equal(before.QuotePrice, after.QuotePrice);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.IsSelected, after.IsSelected);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.RowVersion, after.RowVersion);

        var valid = Clone(quote);
        valid.Quantity = 2.5000m;
        valid.QuotePrice = 0.0001m;
        valid.TotalAmount = -1m;
        var updated = AssertOk<PurchaseQuote>(await ctl.Update(quote.Id, valid));
        Assert.Equal(2.5000m, updated.Quantity);
        Assert.Equal(0.0001m, updated.QuotePrice);
        Assert.Equal(0.0003m, updated.TotalAmount);          // 0.0001 × 2.5000 = 0.00025 → 4 位 AwayFromZero
    }

    // ==================== 5. 源码契约（映射同源 + 护栏先于任何写入） ====================

    [Fact]
    public void 契约_EF映射与权威列声明逐字同源()
    {
        var config = ReadSource("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");
        Assert.Contains("modelBuilder.Entity<PurchaseQuote>().Property(x => x.Quantity).HasPrecision(18, 4);", config);
        Assert.Contains("modelBuilder.Entity<PurchaseQuote>().Property(x => x.QuotePrice).HasPrecision(18, 4);", config);
        Assert.Contains("modelBuilder.Entity<PurchaseQuote>().Property(x => x.TotalAmount).HasPrecision(18, 4);", config);
    }

    [Fact]
    public void 契约_草稿护栏逐位原样拒绝且不落库()
    {
        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseQuoteMutationRules.cs");
        Assert.Contains("EnsureRepresentable", rules);
        Assert.Contains("MaxDecimal18Scale4", rules);
        Assert.Contains("DECIMAL(18,4)", rules);
        Assert.Contains("PersistedNumericText", rules);
        Assert.Contains("decimal.Round(value, DecimalScale) == value", rules);

        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteController.cs");
        var createStart = controller.IndexOf("public override async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public override async Task<IActionResult> Update", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart);
        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("PurchaseQuoteMutationRules.ValidateDraft", StringComparison.Ordinal)
                    < create.IndexOf("Service.CreateAsync", StringComparison.Ordinal));  // 校验先于任何写入
        var update = controller[updateStart..];
        Assert.True(update.IndexOf("PurchaseQuoteMutationRules.ValidateDraft", StringComparison.Ordinal)
                    < update.IndexOf("PurchaseQuoteMutationRules.ApplyEditableFields", StringComparison.Ordinal));
    }

    // ==================== 工厂与种子数据 ====================

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static PurchaseQuote NewDraft(string quoteNo) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 10, 1),
        ProductId = 310L,
        ProductName = "ERP528 商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 10m,
        SupplierId = 88L,
        SupplierName = "ERP528 档口",
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
        Remark = "ERP528_TEST"
    };

    private static PurchaseQuote SeedPendingQuote(ErpDbContext db, string quoteNo)
    {
        var quote = NewDraft(quoteNo);
        quote.UpdatedAt = new DateTime(2026, 10, 1, 8, 30, 0);
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }

    private static PurchaseQuote Clone(PurchaseQuote quote) => new()
    {
        Id = quote.Id,
        QuoteNo = quote.QuoteNo,
        QuoteDate = quote.QuoteDate,
        ProductId = quote.ProductId,
        ProductName = quote.ProductName,
        Spec = quote.Spec,
        Unit = quote.Unit,
        Quantity = quote.Quantity,
        SupplierId = quote.SupplierId,
        SupplierName = quote.SupplierName,
        SupplierType = quote.SupplierType,
        QuotePrice = quote.QuotePrice,
        TotalAmount = quote.TotalAmount,
        Currency = quote.Currency,
        TaxIncluded = quote.TaxIncluded,
        DeliveryDays = quote.DeliveryDays,
        MinOrderQty = quote.MinOrderQty,
        PaymentTerms = quote.PaymentTerms,
        IsSelected = quote.IsSelected,
        Status = quote.Status,
        CustomerId = quote.CustomerId,
        CustomerName = quote.CustomerName,
        RefOrderNo = quote.RefOrderNo,
        Remark = quote.Remark
    };

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));
}
