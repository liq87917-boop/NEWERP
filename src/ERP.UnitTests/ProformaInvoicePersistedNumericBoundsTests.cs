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
/// ERP-534 形式发票 PI 汇率 / 数量 / 单价 / 行金额 / 合计 / 定金比例 · 定金金额的**持久化精度**
/// （声明列 <c>DECIMAL(18,6)</c> 与 <c>DECIMAL(18,4)</c>）边界单元测试（内存库 + 真实启用身份 + 真实控制器）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：<c>ProformaInvoice.ExchangeRate</c> 显式 <c>decimal(18,6)</c>，
/// <c>TotalAmount</c> / <c>TotalAmountCny</c> / <c>DepositRatio</c> / <c>DepositAmount</c> 与
/// <c>ProformaInvoiceDetail.Quantity</c> / <c>UnitPrice</c> / <c>Amount</c> 显式 <c>decimal(18,4)</c>
/// （仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>标量可表示性纯判定：恰好 6 位小数汇率 / 4 位小数数量 · 单价 · 比例与量级边界放行；第 7 位小数汇率、
/// 第 5 位小数数量 · 单价 · 比例、量级越界一律拒绝（绝不静默取整）；</item>
/// <item>契约边界：六位小数汇率上限、四位小数数量 · 单价上限与四位小数定金比例原样接受；服务端仍按币种精度
/// 派生行金额 / 合计 / 折人民币（取整口径不变）；半进位取整恰好落在可接受上界放行、越过上界受控拒绝；</item>
/// <item>真实控制器新增 / 修改：超精度 / 越界请求零写入、零占号，权威行逐字段不变；提交 / 转销售订单对已持久化
/// 非法条款原子拒绝且状态 / 下游 / 单号预约不变；</item>
/// <item>源码契约：EF 映射、规则常量与「护栏先于占号与任何写入」的接线逐字同源。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返 / 越界被真实列拒绝」由
/// <c>ERP.IntegrationTests/ProformaInvoicePersistedNumericBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public class ProformaInvoicePersistedNumericBoundsTests
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private const decimal MaxAmount = ProformaInvoiceCommercialValidationRules.MaxAmount;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（与规则常量同源）。</summary>
    private const decimal MaxRate = ProformaInvoiceCommercialValidationRules.MaxExchangeRate;

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP534_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(18, ProformaInvoiceCommercialValidationRules.DecimalPrecision);
        Assert.Equal(6, ProformaInvoiceCommercialValidationRules.ExchangeRateScale);
        Assert.Equal(4, ProformaInvoiceCommercialValidationRules.AmountScale);
        Assert.Equal(999_999_999_999.999999m, MaxRate);
        Assert.Equal(99_999_999_999_999.9999m, MaxAmount);

        AssertColumn(db.Model, typeof(ProformaInvoice), nameof(ProformaInvoice.ExchangeRate), 6);
        AssertColumn(db.Model, typeof(ProformaInvoice), nameof(ProformaInvoice.TotalAmount), 4);
        AssertColumn(db.Model, typeof(ProformaInvoice), nameof(ProformaInvoice.TotalAmountCny), 4);
        AssertColumn(db.Model, typeof(ProformaInvoice), nameof(ProformaInvoice.DepositRatio), 4);
        AssertColumn(db.Model, typeof(ProformaInvoice), nameof(ProformaInvoice.DepositAmount), 4);
        AssertColumn(db.Model, typeof(ProformaInvoiceDetail), nameof(ProformaInvoiceDetail.Quantity), 4);
        AssertColumn(db.Model, typeof(ProformaInvoiceDetail), nameof(ProformaInvoiceDetail.UnitPrice), 4);
        AssertColumn(db.Model, typeof(ProformaInvoiceDetail), nameof(ProformaInvoiceDetail.Amount), 4);
    }

    /// <summary>
    /// 模型映射必须与 SchemaUpgrader 第 21 段的权威建表脚本逐字一致；若退回 EF 默认 <c>decimal(18,2)</c>，
    /// 合法的六位小数汇率与四位小数数量 / 单价 / 定金会在写入前被静默取整。
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
    [InlineData("99999999999999.9999")]      // 恰好 4 位小数上界
    [InlineData("-99999999999999.9999")]
    public void 可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(ProformaInvoiceCommercialValidationRules.IsRepresentable(Raw(raw)));

    [Theory]
    [InlineData("1.00005")]                  // 第 5 位小数
    [InlineData("0.00001")]
    [InlineData("100000000000000")]          // 1e14 量级越界
    [InlineData("100000000000000.0000")]
    public void 不可表示_过小数位或越界(string raw)
        => Assert.False(ProformaInvoiceCommercialValidationRules.IsRepresentable(Raw(raw)));

    [Theory]
    [InlineData("0.000001")]
    [InlineData("7.123456")]
    [InlineData("999999999999.999999")]      // 恰好 6 位小数上界
    [InlineData("-999999999999.999999")]
    public void 汇率可表示_六位小数与量级边界内放行(string raw)
        => Assert.True(ProformaInvoiceCommercialValidationRules.IsExchangeRateRepresentable(Raw(raw)));

    [Theory]
    [InlineData("7.1234567")]                // 第 7 位小数
    [InlineData("0.0000001")]
    [InlineData("1000000000000")]            // 1e12 量级越界（上界 999999999999.999999）
    public void 汇率不可表示_过小数位或越界(string raw)
        => Assert.False(ProformaInvoiceCommercialValidationRules.IsExchangeRateRepresentable(Raw(raw)));

    // ==================== 3. 契约边界（标量上限与派生金额） ====================

    [Fact]
    public void 契约_六位小数汇率与四位小数单价_原样接受且金额按币种精度派生()
    {
        // 任务描述的两个具体例子：六位小数汇率 7.123456、四位小数单价 0.0125。
        var pi = Draft(Line(2m, 0.0125m));
        pi.ExchangeRate = 7.123456m;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(pi);

        Assert.Equal(7.123456m, pi.ExchangeRate);
        Assert.Equal(0.0125m, pi.Details.Single().UnitPrice);
        Assert.Equal(0.03m, pi.Details.Single().Amount);        // 2 × 0.0125 = 0.025 → USD 2 位 0.5 进位
        Assert.Equal(0.03m, pi.TotalAmount);
        Assert.Equal(0.21m, pi.TotalAmountCny);                 // 0.03 × 7.123456 = 0.21370368 → CNY 2 位
    }

    [Fact]
    public void 契约_六位小数汇率上限_原样接受且折人民币落在有界金额列内()
    {
        var pi = Draft(Line(1m, 1m));
        pi.ExchangeRate = MaxRate;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(pi);

        Assert.Equal(MaxRate, pi.ExchangeRate);
        Assert.Equal(1_000_000_000_000.00m, pi.TotalAmountCny);  // 1 × 999999999999.999999 → CNY 2 位
    }

    [Fact]
    public void 契约_四位小数数量与单价上限_原样接受且派生金额落在有界金额列内()
    {
        var quantityMax = Draft(Line(MaxAmount, 0.0001m));
        quantityMax.ExchangeRate = 1m;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(quantityMax);

        Assert.Equal(MaxAmount, quantityMax.Details.Single().Quantity);
        // 99999999999999.9999 × 0.0001 = 9999999999.99999999 → USD 2 位 0.5 进位
        Assert.Equal(10_000_000_000.00m, quantityMax.Details.Single().Amount);
        Assert.Equal(10_000_000_000.00m, quantityMax.TotalAmount);
        Assert.Equal(10_000_000_000.00m, quantityMax.TotalAmountCny);

        var priceMax = Draft(Line(0.0001m, MaxAmount));
        priceMax.ExchangeRate = 1m;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(priceMax);

        Assert.Equal(MaxAmount, priceMax.Details.Single().UnitPrice);
        Assert.Equal(10_000_000_000.00m, priceMax.TotalAmount);
    }

    [Fact]
    public void 契约_四位小数定金比例_原样接受且自动定金按币种精度派生()
    {
        var pi = Draft(Line(1m, 10m));
        pi.DepositRatio = 30.1250m;                              // 四位小数定金比例
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(pi);

        Assert.Equal(30.1250m, pi.DepositRatio);
        // 10 × 30.125% = 3.0125 → USD 2 位 0.5 进位（0.0025 未达半数 → 3.01）
        Assert.Equal(3.01m, pi.DepositAmount);

        var ratioMax = Draft(Line(1m, 10m));
        ratioMax.DepositRatio = 100.0000m;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(ratioMax);

        Assert.Equal(100.0000m, ratioMax.DepositRatio);
        Assert.Equal(10m, ratioMax.DepositAmount);               // 显式 0 定金 → 按 100% 自动 = 总额
    }

    [Fact]
    public void 契约_显式定金金额_等于总额放行_超过总额或非币种精度拒绝()
    {
        var atLimit = Draft(Line(1m, 10m));                      // 总额 10
        atLimit.DepositAmount = 10m;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(atLimit);
        Assert.Equal(10m, atLimit.DepositAmount);

        var over = Draft(Line(1m, 10m));
        over.DepositAmount = 10.01m;
        var exceeds = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(over));
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositExceedsTotalText, exceeds.Message);

        var fifthPlace = Draft(Line(1m, 10m));
        fifthPlace.DepositAmount = 3.00005m;                     // 第 5 位小数（USD 非币种精度）
        var overprecision = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(fifthPlace));
        Assert.Equal(ProformaInvoiceCommercialValidationRules.DepositOverflowText, overprecision.Message);
    }

    [Fact]
    public void 契约_半进位取整恰好落在可接受上界放行_越过上界受控拒绝()
    {
        // 1 × 99999999999999.9949 → USD 2 位取整落在 99999999999999.99（≤ DECIMAL(18,4) 上界）→ 放行。
        var landed = Draft(Line(1m, 99_999_999_999_999.9949m));
        landed.ExchangeRate = 1m;
        ProformaInvoiceCommercialValidationRules.NormalizeIncomingDraft(landed);

        Assert.Equal(99_999_999_999_999.99m, landed.TotalAmount);

        // 1 × 99999999999999.995 → 取整进位到 100000000000000.00，越过上界 → 受控拒绝。
        var crossed = Draft(Line(1m, 99_999_999_999_999.995m));
        var ex = Assert.Throws<BusinessException>(() =>
            ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(crossed));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("超出存储精度", ex.Message);
    }


    // ==================== 4. 控制器：新增 / 修改（内存库 + 既有身份） ====================

    [Fact]
    public async Task 新增_六位小数汇率与四位小数单价_原样落库且消耗单号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = NewController(db);

        var body = Body(Line(2m, 0.0125m));
        body.ExchangeRate = 7.123456m;

        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var saved = db.ProformaInvoices.Single();
        Assert.Equal(7.123456m, saved.ExchangeRate);             // 六位小数汇率原样落库
        Assert.Equal(0.0125m, db.ProformaInvoiceDetails.Single().UnitPrice);  // 四位小数单价原样落库
        Assert.Equal(8L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Theory]
    [InlineData("rate-seventh")]
    [InlineData("quantity-fifth")]
    [InlineData("price-fifth")]
    [InlineData("ratio-fifth")]
    [InlineData("rate-magnitude")]
    public async Task 新增_超精度或越界_受控拒绝且零写入零占号(string caseName)
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.ProformaInvoice, 7);
        var ctl = NewController(db);

        var body = Body(Line(10m, 100m));
        switch (caseName)
        {
            case "rate-seventh": body.ExchangeRate = 7.1234567m; break;
            case "quantity-fifth": body.Details[0].Quantity = 1.00005m; break;
            case "price-fifth": body.Details[0].UnitPrice = 1.00005m; break;
            case "ratio-fifth": body.DepositRatio = 30.00005m; break;
            case "rate-magnitude": body.ExchangeRate = 1_000_000_000_000m; break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null);
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.ProformaInvoiceDetails);
        Assert.Equal(7L, SequenceOf(db, DocumentType.ProformaInvoice));
    }

    [Fact]
    public async Task 修改_超精度汇率_受控拒绝且权威行逐字段不变()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-NUM-UPDATE", DocumentStatus.Pending, 7.2m, Line(2m, 10m));
        var originalDetailId = db.ProformaInvoiceDetails.Single().Id;

        var update = Body(Line(2m, 10m));
        update.ExchangeRate = 7.1234567m;                        // 第 7 位小数
        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Update(pi.Id, update));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = db.ProformaInvoices.AsNoTracking().Single();
        Assert.Equal(7.2m, after.ExchangeRate);
        Assert.Equal(20m, after.TotalAmount);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        var line = db.ProformaInvoiceDetails.AsNoTracking().Single();
        Assert.Equal(originalDetailId, line.Id);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(10m, line.UnitPrice);
    }


    // ==================== 5. 运营承诺：已持久化非法条款原子拒绝 ====================

    [Fact]
    public async Task 提交_已持久化第七位小数汇率_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var pi = SeedPi(db, "PI-NUM-SUBMIT", DocumentStatus.Pending, 7.1234567m, Line(2m, 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).Submit(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(ProformaInvoiceCommercialValidationRules.ExchangeRateText, ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转销售订单_已持久化第五位小数数量_原子拒绝且零订单零占号()
    {
        using var db = TestDbFactory.Create();
        SeedNumberRule(db, DocumentType.SalesOrder, 3);
        var pi = SeedPi(db, "PI-NUM-ORDER", DocumentStatus.Approved, 7.2m, Line(2m, 10m));
        db.ProformaInvoiceDetails.Single().Quantity = 0.00005m;   // 第 5 位小数
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToSalesOrder(pi.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message, StringComparison.Ordinal);
        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.AsNoTracking().Single().Status);
        Assert.Equal(3L, SequenceOf(db, DocumentType.SalesOrder));
    }

    // ==================== 6. 源码契约（EF 映射 / 规则常量 / 接线先于占号与写入） ====================

    [Fact]
    public void 源码契约_EF映射与护栏接线与声明同源()
    {
        var config = ReadSource("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs");
        Assert.Contains("modelBuilder.Entity<ProformaInvoice>().Property(x => x.ExchangeRate).HasPrecision(18, 6)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoice>().Property(x => x.TotalAmount).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoice>().Property(x => x.TotalAmountCny).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoice>().Property(x => x.DepositRatio).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoice>().Property(x => x.DepositAmount).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoiceDetail>().Property(x => x.Quantity).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoiceDetail>().Property(x => x.UnitPrice).HasPrecision(18, 4)", config);
        Assert.Contains("modelBuilder.Entity<ProformaInvoiceDetail>().Property(x => x.Amount).HasPrecision(18, 4)", config);

        var contract = ReadSource("src", "ERP.Application", "Services", "ProformaInvoiceCommercialValidationRules.cs");
        Assert.Contains("ExchangeRateScale = 6", contract);
        Assert.Contains("AmountScale = 4", contract);
        Assert.Contains("IsExchangeRateRepresentable", contract);

        // 新增路径仍在 PI 单号预约与任何写入之前先按契约校验（被拒绝的请求零写入、零占号）。
        var controller = ReadSource("src", "ERP.Api", "Controllers", "ProformaInvoiceController.cs");
        var validate = controller.IndexOf(
            "ProformaInvoiceCommercialValidationRules.ValidateIncomingDraft(entity)", StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.ProformaInvoice)", StringComparison.Ordinal);
        Assert.True(validate > 0 && generate > validate, "条款校验必须先于 PI 单号生成");
    }


    // ==================== 工厂与种子数据 ====================

    private static ProformaInvoiceController NewController(ErpDbContext db)
    {
        SeedLiveCustomer(db);
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static void SeedLiveCustomer(ErpDbContext db, long id = 1L)
    {
        if (db.BaseCustomers.Any(c => c.Id == id)) return;
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C-PI-534-{id}",
            CustomerName = "PI 持久化精度客户",
            Status = 1,
            CreditStatus = "正常",
            DepositRatio = 30m,
            IsDeleted = false
        });
        db.SaveChanges();
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
        PiDate = new DateTime(2026, 10, 1),
        CustomerName = "PI 持久化精度客户",
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
        CustomerName = "PI 持久化精度客户",
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        DepositRatio = 30m,
        Details = lines.ToList()
    };

    /// <summary>直接落库的 PI 种子（不经写入契约，允许构造非法历史条款用于承诺前复核测试）。</summary>
    private static ProformaInvoice SeedPi(ErpDbContext db, string no, DocumentStatus status,
        decimal exchangeRate, params ProformaInvoiceDetail[] lines)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "PI 持久化精度客户",
            Currency = Currency.USD,
            ExchangeRate = exchangeRate,
            DepositRatio = 30m,
            DepositAmount = 0m,
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

    private static ProformaInvoiceDetail Line(decimal quantity, decimal unitPrice) => new()
    {
        ProductId = 1, ProductCode = "P-534", ProductName = "PI 持久化精度商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99, IsDeleted = false
    };

    /// <summary>读取仓库内源文件（从测试输出目录向上定位解决方案根，与既有护栏测试同源）。</summary>
    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    private static decimal Raw(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

}
