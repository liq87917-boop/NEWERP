using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-533 报价单汇率 / 数量 / 单价 / 行金额的**持久化精度**（声明列 <c>DECIMAL(18,6)</c> 与
/// <c>DECIMAL(18,4)</c>）真实 SQL Server 集成测试（专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认
/// <c>Quotations.ExchangeRate</c> 为 <c>decimal(18,6)</c>、<c>TotalAmount</c> / <c>TotalAmountCny</c> 与
/// <c>QuotationDetails.Quantity</c> / <c>UnitPrice</c> / <c>Amount</c> 为 <c>decimal(18,4)</c>；</item>
/// <item>合法的六位小数汇率（7.123456）与四位小数数量 / 单价（4.0000 / 1.2345）经真实控制器创建后
/// **原样往返**（以 <c>CONVERT(varchar, ...)</c> 断言逐位保留，绝不收敛为 2 位）；</item>
/// <item>恰好落在声明列上限（汇率 <c>999999999999.999999</c>、数量 / 单价 <c>99999999999999.9999</c>）
/// 同样原样往返；第 7 位小数汇率、第 5 位小数数量 · 单价与量级越界在写入之前受控拒绝；</item>
/// <item>被拒的新增 / 修改零报价单、零明细、零单号消耗，且权威行逐字段不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security</c>；夹具每次运行创建全新 GUID 库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class QuotationPersistedNumericBoundsSqlServerTests
    : IClassFixture<QuotationCommercialValidationSqlServerFixture>
{
    private readonly QuotationCommercialValidationSqlServerFixture _fixture;

    public QuotationPersistedNumericBoundsSqlServerTests(QuotationCommercialValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => QuotationCommercialValidationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static QuotationController ControllerFor(ErpDbContext db, long userId)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>新增请求体（金额故意伪造，用于验证服务端仍按币种精度派生）。</summary>
    private Quotation Body(decimal exchangeRate, params QuotationDetail[] details) => new()
    {
        QuotationNo = string.Empty,
        QuotationDate = DateTime.Today,
        CustomerId = _fixture.CustomerId,
        CustomerName = "ERP533 客户",
        Currency = Currency.USD,
        ExchangeRate = exchangeRate,
        Status = DocumentStatus.Pending,
        TotalAmount = 999_999m,
        TotalAmountCny = 1m,
        Details = details.ToList()
    };

    private static QuotationDetail Line(decimal quantity, decimal unitPrice) => new()
    {
        ProductId = 1, ProductCode = "P-533", ProductName = "ERP533 持久化精度商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99
    };

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_matches_declared_columns()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,6)", ColumnType(db, typeof(Quotation), nameof(Quotation.ExchangeRate)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(Quotation), nameof(Quotation.TotalAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(Quotation), nameof(Quotation.TotalAmountCny)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(QuotationDetail), nameof(QuotationDetail.Quantity)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(QuotationDetail), nameof(QuotationDetail.UnitPrice)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(QuotationDetail), nameof(QuotationDetail.Amount)));

        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "Quotations", "ExchangeRate"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "Quotations", "TotalAmount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "Quotations", "TotalAmountCny"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "QuotationDetails", "Quantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "QuotationDetails", "UnitPrice"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "QuotationDetails", "Amount"));

        Assert.Equal(18, QuotationCommercialValidationRules.DecimalPrecision);
        Assert.Equal(6, QuotationCommercialValidationRules.ExchangeRateScale);
        Assert.Equal(4, QuotationCommercialValidationRules.AmountScale);
        Assert.Equal(999_999_999_999.999999m, QuotationCommercialValidationRules.MaxExchangeRate);
        Assert.Equal(99_999_999_999_999.9999m, QuotationCommercialValidationRules.MaxAmount);
    }

    // ==================== 2. 合法六位小数汇率与四位小数数量 / 单价原样往返 ====================

    [Fact]
    public async Task Six_decimal_rate_and_four_decimal_line_round_trip_verbatim()
    {
        Guard();
        long quotationId;
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await ControllerFor(db, _fixture.PrivilegedUserId)
                .Create(Body(7.123456m, Line(4m, 1.2345m))));

            quotationId = await LatestQuotationIdAsync(db);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
            Assert.Equal(7.123456m, stored.ExchangeRate);
            Assert.Equal(4.9400m, stored.TotalAmount);              // 4 × 1.2345 = 4.938 → USD 2 位 0.5 进位（服务端派生）
            Assert.Equal(35.19m, stored.TotalAmountCny);            // 4.94 × 7.123456 = 35.1899 → CNY 2 位

            var line = await verify.QuotationDetails.AsNoTracking()
                .SingleAsync(d => d.QuotationId == quotationId);
            Assert.Equal(4.0000m, line.Quantity);
            Assert.Equal(1.2345m, line.UnitPrice);
            Assert.Equal(4.9400m, line.Amount);

            // 真实声明列以 6 / 4 位小数原样保存（绝不收敛为 decimal(18,2) 的两位）。
            var (rate, total, totalCny) = await SqlQuotationTextAsync(verify, quotationId);
            Assert.Equal("7.123456", rate);
            Assert.Equal("4.9400", total);
            Assert.Equal("35.1900", totalCny);

            var (quantity, unitPrice, _) = await SqlDetailTextAsync(verify, quotationId);
            Assert.Equal("4.0000", quantity);
            Assert.Equal("1.2345", unitPrice);
        }
    }

    [Fact]
    public async Task Declared_column_maxima_round_trip_verbatim()
    {
        Guard();
        long rateId;
        long quantityId;
        long priceId;
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await ControllerFor(db, _fixture.PrivilegedUserId)
                .Create(Body(QuotationCommercialValidationRules.MaxExchangeRate, Line(1m, 1m))));
            rateId = await LatestQuotationIdAsync(db);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await ControllerFor(db, _fixture.PrivilegedUserId)
                .Create(Body(1m, Line(QuotationCommercialValidationRules.MaxAmount, 0.0001m))));
            quantityId = await LatestQuotationIdAsync(db);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await ControllerFor(db, _fixture.PrivilegedUserId)
                .Create(Body(1m, Line(0.0001m, QuotationCommercialValidationRules.MaxAmount))));
            priceId = await LatestQuotationIdAsync(db);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var (rate, _, _) = await SqlQuotationTextAsync(verify, rateId);
            Assert.Equal("999999999999.999999", rate);

            var (quantity, _, _) = await SqlDetailTextAsync(verify, quantityId);
            Assert.Equal("99999999999999.9999", quantity);

            var (_, unitPrice, _) = await SqlDetailTextAsync(verify, priceId);
            Assert.Equal("99999999999999.9999", unitPrice);
        }
    }

    // ==================== 3. 超精度 / 越界在写入之前受控拒绝（零行、零占号） ====================

    [Theory]
    [InlineData("7.1234567", "1", "1")]              // 第 7 位小数汇率
    [InlineData("1000000000000", "1", "1")]          // 汇率量级越界
    [InlineData("1", "0.00005", "1")]                // 第 5 位小数数量
    [InlineData("1", "100000000000000", "1")]        // 数量量级越界
    [InlineData("1", "1", "1.00005")]                // 第 5 位小数单价
    [InlineData("1", "1", "100000000000000")]        // 单价量级越界
    public async Task Over_scale_or_magnitude_is_rejected_with_zero_rows(
        string rate, string quantity, string unitPrice)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var beforeQuotations = await CountAsync("Quotations");
        var beforeDetails = await CountAsync("QuotationDetails");
        var beforeSequence = await QuotationSequenceAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, _fixture.PrivilegedUserId)
                .Create(Body(Raw(rate), Line(Raw(quantity), Raw(unitPrice)))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(beforeQuotations, await CountAsync("Quotations"));
        Assert.Equal(beforeDetails, await CountAsync("QuotationDetails"));
        Assert.Equal(beforeSequence, await QuotationSequenceAsync(verify));   // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task Rejected_update_leaves_authoritative_rows_unchanged()
    {
        Guard();
        long quotationId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var quotation = await _fixture.SeedQuotationAsync(seed, DocumentStatus.Pending, Line(2m, 10m));
            quotationId = quotation.Id;
        }

        await using var db = _fixture.CreateDbContext();
        var before = await db.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        var beforeLine = await db.QuotationDetails.AsNoTracking().SingleAsync(d => d.QuotationId == quotationId);

        // 第 7 位小数汇率 + 第 5 位小数数量：写入之前受控拒绝。
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, _fixture.PrivilegedUserId)
                .Update(quotationId, Body(7.1234567m, Line(1.00005m, 10m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        var after = await verify.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId);
        Assert.Equal(before.ExchangeRate, after.ExchangeRate);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.TotalAmountCny, after.TotalAmountCny);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.IsDeleted, after.IsDeleted);

        var afterLine = await verify.QuotationDetails.AsNoTracking()
            .SingleAsync(d => d.QuotationId == quotationId);
        Assert.Equal(beforeLine.Quantity, afterLine.Quantity);
        Assert.Equal(beforeLine.UnitPrice, afterLine.UnitPrice);
        Assert.Equal(beforeLine.Amount, afterLine.Amount);
        Assert.Equal(beforeLine.SortNo, afterLine.SortNo);
    }

    [Fact]
    public async Task Half_up_rounding_to_the_amount_ceiling_is_accepted_and_over_the_ceiling_rejected()
    {
        Guard();
        // 恰好取整到金额列的可取整上限：1 × 99999999999999.99 → 2 位小数上界，原样落库。
        long acceptedId;
        await using (var db = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await ControllerFor(db, _fixture.PrivilegedUserId)
                .Create(Body(1m, Line(1m, 99_999_999_999_999.99m))));
            acceptedId = await LatestQuotationIdAsync(db);
        }

        var beforeQuotations = await CountAsync("Quotations");

        // 1 × 99999999999999.995 按币种精度 0.5 进位 = 100000000000000.00，越过 DECIMAL(18,4) 上界：写入之前拒绝。
        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                ControllerFor(db, _fixture.PrivilegedUserId)
                    .Create(Body(1m, Line(1m, 99_999_999_999_999.995m))));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var (_, total, _) = await SqlQuotationTextAsync(verify, acceptedId);
            Assert.Equal("99999999999999.9900", total);
            Assert.Equal(beforeQuotations, await CountAsync("Quotations"));   // 越界请求零行
        }
    }

    // ==================== 工厂与断言辅助 ====================

    private async Task<long> LatestQuotationIdAsync(ErpDbContext db)
        => await db.Quotations.AsNoTracking()
            .Where(q => q.CustomerId == _fixture.CustomerId)
            .OrderByDescending(q => q.Id).Select(q => q.Id).FirstAsync();

    private static async Task<string> QuotationSequenceAsync(ErpDbContext db)
        => string.Join(",", await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.Quotation)
            .OrderBy(r => r.Id).Select(r => r.CurrentSequence).ToListAsync());

    private async Task<long> CountAsync(string table)
    {
        await using var db = _fixture.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM db_owner.{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string ColumnType(ErpDbContext db, Type entityType, string propertyName)
        => db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string Rate, string Total, string TotalCny)> SqlQuotationTextAsync(
        ErpDbContext db, long quotationId)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(varchar(50), ExchangeRate), CONVERT(varchar(50), TotalAmount), " +
            "CONVERT(varchar(50), TotalAmountCny) FROM db_owner.Quotations WHERE Id = @id";
        AddParameter(command, "@id", quotationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<(string Quantity, string UnitPrice, string Amount)> SqlDetailTextAsync(
        ErpDbContext db, long quotationId)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(varchar(50), Quantity), CONVERT(varchar(50), UnitPrice), " +
            "CONVERT(varchar(50), Amount) FROM db_owner.QuotationDetails " +
            "WHERE QuotationId = @id ORDER BY SortNo";
        AddParameter(command, "@id", quotationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static decimal Raw(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class QuotationPersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            QuotationCommercialValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
