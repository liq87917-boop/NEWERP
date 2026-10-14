using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-534 形式发票 PI 汇率 / 数量 / 单价 / 行金额 / 合计 / 定金比例 · 定金金额的**持久化精度**
/// （声明列 <c>DECIMAL(18,6)</c> 与 <c>DECIMAL(18,4)</c>）真实 SQL Server 集成测试
/// （专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> + 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认
/// <c>ProformaInvoices.ExchangeRate</c> 为 <c>decimal(18,6)</c>，<c>TotalAmount</c> / <c>TotalAmountCny</c> /
/// <c>DepositRatio</c> / <c>DepositAmount</c> 与 <c>ProformaInvoiceDetails.Quantity</c> / <c>UnitPrice</c> /
/// <c>Amount</c> 为 <c>decimal(18,4)</c>；</item>
/// <item>合法的六位小数汇率（7.123456）与四位小数数量 / 单价（4.0000 / 1.2345）经真实控制器创建后
/// **原样往返**（以 <c>CONVERT(varchar, ...)</c> 断言逐位保留，绝不收敛为 2 位）；</item>
/// <item>恰好落在声明列上限（汇率 <c>999999999999.999999</c>、数量 / 单价 <c>99999999999999.9999</c> /
/// 定金比例 <c>30.1250</c>）同样原样往返；第 7 位小数汇率、第 5 位小数数量 · 单价 · 比例与量级越界在写入
/// 之前受控拒绝；</item>
/// <item>被拒的新增零 PI、零明细、零单号消耗，被拒的修改不改变权威行任何商业字段。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security</c>；夹具每次运行创建全新 GUID 库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ProformaInvoicePersistedNumericBoundsSqlServerTests
    : IClassFixture<ProformaInvoiceCommercialValidationSqlServerFixture>
{
    private readonly ProformaInvoiceCommercialValidationSqlServerFixture _fixture;

    public ProformaInvoicePersistedNumericBoundsSqlServerTests(
        ProformaInvoiceCommercialValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => ProformaInvoiceCommercialValidationSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    /// <summary>每个用例的唯一标记（夹具库在同一测试类内共享，绝不按主表全量 Single 取刚建行）。</summary>
    private static string Marker() => $"ERP534-{Guid.NewGuid():N}";

    private static ProformaInvoiceController PiControllerFor(ErpDbContext db, long userId)
    {
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>新增请求体（金额故意伪造，用于验证服务端仍按币种精度派生）。</summary>
    private ProformaInvoice Body(decimal exchangeRate, params ProformaInvoiceDetail[] details) => new()
    {
        PiNo = string.Empty,
        PiDate = DateTime.Today,
        CustomerId = _fixture.CustomerId,
        CustomerName = "ERP534 客户",
        Currency = Currency.USD,
        ExchangeRate = exchangeRate,
        Status = DocumentStatus.Pending,
        TotalAmount = 999_999m,
        TotalAmountCny = 1m,
        DepositRatio = 30m,
        Details = details.ToList()
    };

    private static ProformaInvoiceDetail Line(decimal quantity, decimal unitPrice) => new()
    {
        ProductId = 1, ProductCode = "P-534", ProductName = "ERP534 持久化精度商品", Unit = "PCS",
        Quantity = quantity, UnitPrice = unitPrice, Amount = 999m, SortNo = 99
    };

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task 实际列精度_EF模型与真实INFORMATION_SCHEMA双向一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,6)", ModelColumn(db, typeof(ProformaInvoice),
            nameof(ProformaInvoice.ExchangeRate)));
        foreach (var property in new[]
                 {
                     nameof(ProformaInvoice.TotalAmount), nameof(ProformaInvoice.TotalAmountCny),
                     nameof(ProformaInvoice.DepositRatio), nameof(ProformaInvoice.DepositAmount)
                 })
            Assert.Equal("decimal(18,4)", ModelColumn(db, typeof(ProformaInvoice), property));
        foreach (var property in new[]
                 {
                     nameof(ProformaInvoiceDetail.Quantity), nameof(ProformaInvoiceDetail.UnitPrice),
                     nameof(ProformaInvoiceDetail.Amount)
                 })
            Assert.Equal("decimal(18,4)", ModelColumn(db, typeof(ProformaInvoiceDetail), property));

        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "ProformaInvoices", "ExchangeRate"));
        foreach (var column in new[] { "TotalAmount", "TotalAmountCny", "DepositRatio", "DepositAmount" })
            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "ProformaInvoices", column));
        foreach (var column in new[] { "Quantity", "UnitPrice", "Amount" })
            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "ProformaInvoiceDetails", column));
    }

    private static string ModelColumn(ErpDbContext db, Type entityType, string propertyName)
        => db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    // ==================== 2. 声明列上原样往返 ====================

    [Fact]
    public async Task 六位小数汇率与四位小数数量单价_真实控制器创建后逐位原样往返()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = PiControllerFor(db, _fixture.PrivilegedUserId);

        var marker = Marker();
        var body = Body(7.123456m, Line(4.0000m, 1.2345m));
        body.CustomerName = marker;
        Assert.IsType<OkObjectResult>(await controller.Create(body));
        var id = db.ProformaInvoices.Single(x => x.CustomerName == marker).Id;

        var (rate, total, ratio, _) = await SqlPiTextAsync(db, id);
        Assert.Equal("7.123456", rate);          // 六位小数汇率逐位保留
        Assert.Equal("4.9400", total);           // 4.0000 × 1.2345 = 4.9380 → USD 2 位 0.5 进位 4.94
        Assert.Equal("30.0000", ratio);

        var (quantity, unitPrice, amount) = await SqlDetailTextAsync(db, id);
        Assert.Equal("4.0000", quantity);        // 四位小数数量逐位保留
        Assert.Equal("1.2345", unitPrice);       // 四位小数单价逐位保留
        Assert.Equal("4.9400", amount);
    }

    [Fact]
    public async Task 六位小数汇率上限_真实列原样往返()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = PiControllerFor(db, _fixture.PrivilegedUserId);

        var marker = Marker();
        var body = Body(ProformaInvoiceCommercialValidationRules.MaxExchangeRate, Line(1m, 1m));
        body.CustomerName = marker;
        Assert.IsType<OkObjectResult>(await controller.Create(body));
        var id = db.ProformaInvoices.Single(x => x.CustomerName == marker).Id;

        var (rate, total, _, _) = await SqlPiTextAsync(db, id);
        Assert.Equal("999999999999.999999", rate);
        Assert.Equal("1.0000", total);
    }

    [Fact]
    public async Task 四位小数数量单价上限与四位小数定金比例_真实列原样往返()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = PiControllerFor(db, _fixture.PrivilegedUserId);

        var marker = Marker();
        var body = Body(1m, Line(ProformaInvoiceCommercialValidationRules.MaxAmount, 0.0001m));
        body.CustomerName = marker;
        body.DepositRatio = 30.1250m;            // 四位小数定金比例
        Assert.IsType<OkObjectResult>(await controller.Create(body));
        var id = db.ProformaInvoices.Single(x => x.CustomerName == marker).Id;

        var (_, total, ratio, deposit) = await SqlPiTextAsync(db, id);
        Assert.Equal("10000000000.0000", total);   // 99999999999999.9999 × 0.0001 → USD 2 位
        Assert.Equal("30.1250", ratio);
        Assert.Equal("3012500000.0000", deposit);  // 10000000000.00 × 30.125%

        var (quantity, unitPrice, amount) = await SqlDetailTextAsync(db, id);
        Assert.Equal("99999999999999.9999", quantity);
        Assert.Equal("0.0001", unitPrice);
        Assert.Equal("10000000000.0000", amount);
    }

    // ==================== 3. 超精度 / 越界在写入之前受控拒绝 ====================

    [Theory]
    [InlineData("rate-seventh")]
    [InlineData("quantity-fifth")]
    [InlineData("price-fifth")]
    [InlineData("ratio-fifth")]
    [InlineData("rate-magnitude")]
    public async Task 超精度或越界新增_写入前受控拒绝且零写入零占号(string caseName)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = PiControllerFor(db, _fixture.PrivilegedUserId);

        var body = Body(7.2m, Line(10m, 100m));
        switch (caseName)
        {
            case "rate-seventh": body.ExchangeRate = 7.1234567m; break;
            case "quantity-fifth": body.Details[0].Quantity = 1.00005m; break;
            case "price-fifth": body.Details[0].UnitPrice = 1.00005m; break;
            case "ratio-fifth": body.DepositRatio = 30.00005m; break;
            case "rate-magnitude": body.ExchangeRate = 1_000_000_000_000m; break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null);
        }

        var before = await SnapshotAsync(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await SnapshotAsync(db);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task 超精度修改_受控拒绝且权威行逐字段不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var pi = await _fixture.SeedPiAsync(db, DocumentStatus.Pending, 7.2m, Line(2m, 10m));
        var beforePi = await SqlPiTextAsync(db, pi.Id);
        var beforeDetail = await SqlDetailTextAsync(db, pi.Id);

        var update = Body(7.1234567m, Line(2m, 10m));   // 第 7 位小数汇率
        var controller = PiControllerFor(db, _fixture.PrivilegedUserId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(pi.Id, update));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(beforePi, await SqlPiTextAsync(db, pi.Id));
        Assert.Equal(beforeDetail, await SqlDetailTextAsync(db, pi.Id));
    }


    // ==================== 夹具与 SQL 读取辅助 ====================

    private sealed record WriteSnapshot(int ProformaInvoices, int ProformaInvoiceDetails, int SalesOrders, string Numbering);

    private static async Task<WriteSnapshot> SnapshotAsync(ErpDbContext db) => new(
        await db.ProformaInvoices.CountAsync(),
        await db.ProformaInvoiceDetails.CountAsync(),
        await db.SalesOrders.CountAsync(),
        string.Join(",", await db.SysDocumentNumberRules.AsNoTracking()
            .OrderBy(r => r.Id).Select(r => r.CurrentSequence).ToListAsync()));

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

    private static async Task<(string Rate, string Total, string Ratio, string Deposit)> SqlPiTextAsync(
        ErpDbContext db, long id)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(varchar(50), ExchangeRate), CONVERT(varchar(50), TotalAmount), " +
            "CONVERT(varchar(50), DepositRatio), CONVERT(varchar(50), DepositAmount) " +
            "FROM db_owner.ProformaInvoices WHERE Id = @id";
        AddParameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private static async Task<(string Quantity, string UnitPrice, string Amount)> SqlDetailTextAsync(
        ErpDbContext db, long id)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(varchar(50), Quantity), CONVERT(varchar(50), UnitPrice), " +
            "CONVERT(varchar(50), Amount) FROM db_owner.ProformaInvoiceDetails " +
            "WHERE PiId = @id ORDER BY SortNo";
        AddParameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ProformaInvoicePersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ProformaInvoiceCommercialValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
