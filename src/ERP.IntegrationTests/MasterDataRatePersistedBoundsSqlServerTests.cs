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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-531 供应商税率 / 返点比例与客户信用额度 / 定金比例 / 佣金比例「实际存储精度」（<c>DECIMAL(18,4)</c>）
/// 的真实 SQL Server 集成测试（专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> + 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认 5 个数值列均为
/// <c>decimal(18,4)</c>；</item>
/// <item>合法的 4 位小数（含恰好上限）经真实受权控制器新增 / 修改后**原样**往返（以 <c>CONVERT(varchar, ...)</c>
/// 逐位断言，绝不收敛为 2 位）；</item>
/// <item>第 5 位小数 / 量级溢出在写入之前受控拒绝，零 <c>BaseSuppliers</c> / <c>BaseCustomers</c> 行；</item>
/// <item>「半进位取整后恰好落到列上限」的过小数位值仍被拒绝（绝不采信取整结果），而恰好上限本身放行；</item>
/// <item>被拒的修改不改变权威行的任何数值字段与审计时间戳；</item>
/// <item>绕过应用层护栏的原始 EF 写入会被真实列静默取整，超容量值被 SQL Server 以算术溢出（8115）拒绝 ——
/// 这正是护栏必须提前受控拒绝的原因。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class MasterDataRatePersistedBoundsSqlServerTests
    : IClassFixture<MasterDataRatePersistedBoundsSqlServerFixture>
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = SupplierAuthorizationRules.MaxRateMagnitude;

    /// <summary>供应商全部持久化数值列（与实体 / 建表脚本逐一对应）。</summary>
    private static readonly string[] SupplierColumns =
    {
        nameof(BaseSupplier.TaxRate),
        nameof(BaseSupplier.RebateRatio),
    };

    /// <summary>客户全部持久化数值列（与实体 / 建表脚本逐一对应）。</summary>
    private static readonly string[] CustomerColumns =
    {
        nameof(BaseCustomer.CreditLimit),
        nameof(BaseCustomer.DepositRatio),
        nameof(BaseCustomer.CommissionRatio),
    };

    private readonly MasterDataRatePersistedBoundsSqlServerFixture _fixture;

    public MasterDataRatePersistedBoundsSqlServerTests(MasterDataRatePersistedBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
        => MasterDataRatePersistedBoundsSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    private static async Task AssertCodeAsync(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>供应商工厂（两个数值列留 0，由用例按需赋值）。</summary>
    private static BaseSupplier Supplier(string code) => new()
    {
        SupplierCode = code,
        SupplierName = "供应商精度集成测试供应商",
        Status = SupplierAuthorizationRules.EnabledStatus
    };

    /// <summary>客户工厂（三个数值列留 0，由用例按需赋值）。</summary>
    private static BaseCustomer Customer(string code) => new()
    {
        CustomerCode = code,
        CustomerName = "客户精度集成测试客户",
        Status = CustomerAuthorizationRules.EnabledStatus,
        CreditStatus = "正常"
    };

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_is_18_4_for_every_master_rate_column()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(4, SupplierAuthorizationRules.PersistedRateScale);
        Assert.Equal(4, CustomerAuthorizationRules.PersistedDecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        var supplierType = db.Model.FindEntityType(typeof(BaseSupplier));
        Assert.NotNull(supplierType);
        foreach (var column in SupplierColumns)
        {
            var mapped = supplierType!.FindProperty(column);
            Assert.NotNull(mapped);
            Assert.Equal(18, mapped!.GetPrecision());
            Assert.Equal(SupplierAuthorizationRules.PersistedRateScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());

            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseSuppliers", column));
        }

        var customerType = db.Model.FindEntityType(typeof(BaseCustomer));
        Assert.NotNull(customerType);
        foreach (var column in CustomerColumns)
        {
            var mapped = customerType!.FindProperty(column);
            Assert.NotNull(mapped);
            Assert.Equal(18, mapped!.GetPrecision());
            Assert.Equal(CustomerAuthorizationRules.PersistedDecimalScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());

            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseCustomers", column));
        }
    }

    // ==================== 2. 合法 4 位小数（含恰好上限）原样往返（真实列） ====================

    [Fact]
    public async Task Four_decimal_values_round_trip_verbatim_through_every_real_column()
    {
        Guard();
        var supplierCode = $"MR531-S-4DP-{Tag()}";
        var customerCode = $"MR531-C-4DP-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var supplier = Supplier(supplierCode);
            supplier.TaxRate = 13.0555m;
            supplier.RebateRatio = 1.2345m;
            Data<BaseSupplier>(await _fixture.NewSupplierController(db).Create(supplier));

            var customer = Customer(customerCode);
            customer.CreditLimit = 123456.7890m;
            customer.DepositRatio = 10.5000m;
            customer.CommissionRatio = 0.1250m;
            Data<BaseCustomer>(await _fixture.NewCustomerController(db).Create(customer));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var supplier = await db.BaseSuppliers.AsNoTracking().SingleAsync(s => s.SupplierCode == supplierCode);
            Assert.Equal(13.0555m, supplier.TaxRate);
            Assert.Equal(1.2345m, supplier.RebateRatio);
            Assert.Equal(new[] { "13.0555", "1.2345" }, await SqlSupplierRowTextAsync(db, supplierCode));

            var customer = await db.BaseCustomers.AsNoTracking().SingleAsync(c => c.CustomerCode == customerCode);
            Assert.Equal(123456.7890m, customer.CreditLimit);
            Assert.Equal(10.5000m, customer.DepositRatio);
            Assert.Equal(0.1250m, customer.CommissionRatio);
            Assert.Equal(new[] { "123456.7890", "10.5000", "0.1250" }, await SqlCustomerRowTextAsync(db, customerCode));
        }
    }

    [Fact]
    public async Task Exact_four_decimal_maxima_round_trip_verbatim_through_every_real_column()
    {
        Guard();
        var supplierCode = $"MR531-S-MAX-{Tag()}";
        var customerCode = $"MR531-C-MAX-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var supplier = Supplier(supplierCode);
            supplier.TaxRate = Max4;
            supplier.RebateRatio = -Max4;
            Data<BaseSupplier>(await _fixture.NewSupplierController(db).Create(supplier));

            var customer = Customer(customerCode);
            customer.CreditLimit = Max4;
            customer.DepositRatio = Max4;
            customer.CommissionRatio = -Max4;
            Data<BaseCustomer>(await _fixture.NewCustomerController(db).Create(customer));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(new[] { "99999999999999.9999", "-99999999999999.9999" },
                await SqlSupplierRowTextAsync(db, supplierCode));
            Assert.Equal(new[] { "99999999999999.9999", "99999999999999.9999", "-99999999999999.9999" },
                await SqlCustomerRowTextAsync(db, customerCode));
        }
    }

    // ==================== 3. 过小数位 / 量级溢出：写入之前受控拒绝（零行） ====================

    [Fact]
    public async Task Over_scale_and_overflowing_values_are_rejected_with_zero_writes()
    {
        Guard();
        var tag = Tag();
        var supplierBefore = await CountAsync(_fixture, "BaseSuppliers");
        var customerBefore = await CountAsync(_fixture, "BaseCustomers");

        await using (var db = _fixture.CreateDbContext())
        {
            var supplierController = _fixture.NewSupplierController(db);
            var customerController = _fixture.NewCustomerController(db);

            var supplierFifth = Supplier($"MR531-S-5DP-{tag}");
            supplierFifth.TaxRate = 1.23455m;                  // 第 5 位小数
            var supplierFifthEx = await Assert.ThrowsAsync<BusinessException>(
                () => supplierController.Create(supplierFifth));
            Assert.Equal(ErrorCodes.InvalidParameter, supplierFifthEx.Code);
            Assert.Contains(nameof(BaseSupplier.TaxRate), supplierFifthEx.Message);
            Assert.Contains("DECIMAL(18,4)", supplierFifthEx.Message);

            var supplierOverflow = Supplier($"MR531-S-OVF-{tag}");
            supplierOverflow.RebateRatio = 100_000_000_000_000m;   // 量级越界（1e14）
            var supplierOverflowEx = await Assert.ThrowsAsync<BusinessException>(
                () => supplierController.Create(supplierOverflow));
            Assert.Equal(ErrorCodes.InvalidParameter, supplierOverflowEx.Code);
            Assert.Contains(nameof(BaseSupplier.RebateRatio), supplierOverflowEx.Message);

            var customerFifth = Customer($"MR531-C-5DP-{tag}");
            customerFifth.CreditLimit = 100.00005m;            // 第 5 位小数
            var customerFifthEx = await Assert.ThrowsAsync<BusinessException>(
                () => customerController.Create(customerFifth));
            Assert.Equal(ErrorCodes.InvalidParameter, customerFifthEx.Code);
            Assert.Contains(nameof(BaseCustomer.CreditLimit), customerFifthEx.Message);

            var customerOverflow = Customer($"MR531-C-OVF-{tag}");
            customerOverflow.CommissionRatio = -100_000_000_000_000m;  // 量级越界（1e14）
            var customerOverflowEx = await Assert.ThrowsAsync<BusinessException>(
                () => customerController.Create(customerOverflow));
            Assert.Equal(ErrorCodes.InvalidParameter, customerOverflowEx.Code);
            Assert.Contains(nameof(BaseCustomer.CommissionRatio), customerOverflowEx.Message);
        }

        Assert.Equal(supplierBefore, await CountAsync(_fixture, "BaseSuppliers"));
        Assert.Equal(customerBefore, await CountAsync(_fixture, "BaseCustomers"));

        // 同一受权控制器随后合法创建成功（拒绝来自护栏而不是授权），且各只新增一行。
        await using (var db = _fixture.CreateDbContext())
        {
            var supplierOk = Supplier($"MR531-S-OK-{tag}");
            supplierOk.TaxRate = 13.0555m;
            supplierOk.RebateRatio = 1.2345m;
            Data<BaseSupplier>(await _fixture.NewSupplierController(db).Create(supplierOk));

            var customerOk = Customer($"MR531-C-OK-{tag}");
            customerOk.CreditLimit = 0.0001m;
            customerOk.DepositRatio = 10.5000m;
            customerOk.CommissionRatio = -0.1250m;
            Data<BaseCustomer>(await _fixture.NewCustomerController(db).Create(customerOk));
        }

        Assert.Equal(supplierBefore + 1, await CountAsync(_fixture, "BaseSuppliers"));
        Assert.Equal(customerBefore + 1, await CountAsync(_fixture, "BaseCustomers"));
    }

    [Fact]
    public async Task Half_up_rounding_onto_the_limit_is_rejected_while_the_exact_limit_is_accepted()
    {
        Guard();
        var supplierCode = $"MR531-S-LIMT-{Tag()}";
        var customerCode = $"MR531-C-LIMT-{Tag()}";
        var supplierBefore = await CountAsync(_fixture, "BaseSuppliers");
        var customerBefore = await CountAsync(_fixture, "BaseCustomers");

        await using (var db = _fixture.CreateDbContext())
        {
            var supplierController = _fixture.NewSupplierController(db);
            var customerController = _fixture.NewCustomerController(db);

            // 半进位取整后恰好落到 DECIMAL(18,4) 上限（…99985 → …9999）仍被拒绝：
            // 护栏判定的是原值，绝不采信「取整之后才可存储」。
            var supplierOntoLimit = Supplier($"MR531-S-HALF1-{Tag()}");
            supplierOntoLimit.TaxRate = 99_999_999_999_999.99985m;
            var supplierOntoEx = await Assert.ThrowsAsync<BusinessException>(
                () => supplierController.Create(supplierOntoLimit));
            Assert.Equal(ErrorCodes.InvalidParameter, supplierOntoEx.Code);
            Assert.Contains(nameof(BaseSupplier.TaxRate), supplierOntoEx.Message);

            // 半进位取整后越过上限（…99995 → 100_000_000_000_000.0000）同样拒绝。
            var supplierPastLimit = Supplier($"MR531-S-HALF2-{Tag()}");
            supplierPastLimit.RebateRatio = 99_999_999_999_999.99995m;
            var supplierPastEx = await Assert.ThrowsAsync<BusinessException>(
                () => supplierController.Create(supplierPastLimit));
            Assert.Equal(ErrorCodes.InvalidParameter, supplierPastEx.Code);

            var customerOntoLimit = Customer($"MR531-C-HALF1-{Tag()}");
            customerOntoLimit.CreditLimit = 99_999_999_999_999.99985m;
            var customerOntoEx = await Assert.ThrowsAsync<BusinessException>(
                () => customerController.Create(customerOntoLimit));
            Assert.Equal(ErrorCodes.InvalidParameter, customerOntoEx.Code);
            Assert.Contains(nameof(BaseCustomer.CreditLimit), customerOntoEx.Message);

            Assert.Equal(supplierBefore, await CountAsync(_fixture, "BaseSuppliers"));
            Assert.Equal(customerBefore, await CountAsync(_fixture, "BaseCustomers"));

            // 恰好上限本身放行。
            var supplierAtLimit = Supplier(supplierCode);
            supplierAtLimit.TaxRate = Max4;
            Data<BaseSupplier>(await supplierController.Create(supplierAtLimit));

            var customerAtLimit = Customer(customerCode);
            customerAtLimit.CreditLimit = Max4;
            Data<BaseCustomer>(await customerController.Create(customerAtLimit));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(new[] { "99999999999999.9999", "0.0000" },
                await SqlSupplierRowTextAsync(db, supplierCode));
            Assert.Equal(new[] { "99999999999999.9999", "0.0000", "0.0000" },
                await SqlCustomerRowTextAsync(db, customerCode));
        }

        Assert.Equal(supplierBefore + 1, await CountAsync(_fixture, "BaseSuppliers"));
        Assert.Equal(customerBefore + 1, await CountAsync(_fixture, "BaseCustomers"));
    }

    // ==================== 4. 被拒修改不改变权威行（数值字段 / 审计时间戳） ====================

    [Fact]
    public async Task Rejected_update_leaves_the_authoritative_rows_unchanged()
    {
        Guard();
        var tag = Tag();
        var supplierCode = $"MR531-S-UPD-{tag}";
        var customerCode = $"MR531-C-UPD-{tag}";
        long supplierId;
        long customerId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var supplier = Supplier(supplierCode);
            supplier.TaxRate = 13.0555m;
            supplier.RebateRatio = 1.2345m;
            supplierId = Data<BaseSupplier>(await _fixture.NewSupplierController(seed).Create(supplier)).Id;

            var customer = Customer(customerCode);
            customer.CreditLimit = 123456.7890m;
            customer.DepositRatio = 10.5000m;
            customer.CommissionRatio = 0.1250m;
            customerId = Data<BaseCustomer>(await _fixture.NewCustomerController(seed).Create(customer)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var beforeSupplier = await db.BaseSuppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);
            var beforeCustomer = await db.BaseCustomers.AsNoTracking().SingleAsync(c => c.Id == customerId);

            var supplierController = _fixture.NewSupplierController(db);
            var customerController = _fixture.NewCustomerController(db);

            var supplierOverScale = await db.BaseSuppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);
            supplierOverScale.TaxRate = 1.23455m;                     // 第 5 位小数
            var rejectedSupplierFifth = await Assert.ThrowsAsync<BusinessException>(
                () => supplierController.Update(supplierId, supplierOverScale));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedSupplierFifth.Code);
            Assert.Contains(nameof(BaseSupplier.TaxRate), rejectedSupplierFifth.Message);

            var supplierOverflow = await db.BaseSuppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);
            supplierOverflow.RebateRatio = Max4 + 0.0001m;            // 量级越界
            var rejectedSupplierOverflow = await Assert.ThrowsAsync<BusinessException>(
                () => supplierController.Update(supplierId, supplierOverflow));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedSupplierOverflow.Code);
            Assert.Contains(nameof(BaseSupplier.RebateRatio), rejectedSupplierOverflow.Message);

            var customerOverScale = await db.BaseCustomers.AsNoTracking().SingleAsync(c => c.Id == customerId);
            customerOverScale.CreditLimit = 100.00005m;               // 第 5 位小数
            var rejectedCustomerFifth = await Assert.ThrowsAsync<BusinessException>(
                () => customerController.Update(customerId, customerOverScale));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedCustomerFifth.Code);
            Assert.Contains(nameof(BaseCustomer.CreditLimit), rejectedCustomerFifth.Message);

            var customerOverflow = await db.BaseCustomers.AsNoTracking().SingleAsync(c => c.Id == customerId);
            customerOverflow.CommissionRatio = Max4 + 0.0001m;        // 量级越界
            var rejectedCustomerOverflow = await Assert.ThrowsAsync<BusinessException>(
                () => customerController.Update(customerId, customerOverflow));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedCustomerOverflow.Code);
            Assert.Contains(nameof(BaseCustomer.CommissionRatio), rejectedCustomerOverflow.Message);

            var afterSupplier = await db.BaseSuppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);
            Assert.Equal(beforeSupplier.TaxRate, afterSupplier.TaxRate);
            Assert.Equal(beforeSupplier.RebateRatio, afterSupplier.RebateRatio);
            Assert.Equal(beforeSupplier.SupplierName, afterSupplier.SupplierName);
            Assert.Equal(beforeSupplier.UpdatedAt, afterSupplier.UpdatedAt);
            Assert.False(afterSupplier.IsDeleted);

            var afterCustomer = await db.BaseCustomers.AsNoTracking().SingleAsync(c => c.Id == customerId);
            Assert.Equal(beforeCustomer.CreditLimit, afterCustomer.CreditLimit);
            Assert.Equal(beforeCustomer.DepositRatio, afterCustomer.DepositRatio);
            Assert.Equal(beforeCustomer.CommissionRatio, afterCustomer.CommissionRatio);
            Assert.Equal(beforeCustomer.CustomerName, afterCustomer.CustomerName);
            Assert.Equal(beforeCustomer.UpdatedAt, afterCustomer.UpdatedAt);
            Assert.False(afterCustomer.IsDeleted);
        }
    }

    // ==================== 5. 真实列行为：绕过护栏时静默取整 / 算术溢出（8115） ====================

    [Fact]
    public async Task Raw_writes_bypassing_the_guard_are_rounded_or_rejected_by_the_real_columns()
    {
        Guard();

        // 绕过应用层护栏的原始 EF 写入：真实四位小数列对第 5 位小数静默取整（护栏必须提前拒绝的原因）。
        var supplierCode = $"MR531-S-RAW-{Tag()}";
        var customerCode = $"MR531-C-RAW-{Tag()}";
        await using (var db = _fixture.CreateDbContext())
        {
            var rawSupplier = Supplier(supplierCode);
            rawSupplier.TaxRate = 1.23455m;
            db.BaseSuppliers.Add(rawSupplier);

            var rawCustomer = Customer(customerCode);
            rawCustomer.CreditLimit = 100.00005m;
            db.BaseCustomers.Add(rawCustomer);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var storedSupplier = await db.BaseSuppliers.AsNoTracking()
                .SingleAsync(s => s.SupplierCode == supplierCode);
            Assert.NotEqual(1.23455m, storedSupplier.TaxRate);
            Assert.Equal(1.2346m, storedSupplier.TaxRate);          // 1.23455 → 1.2346：原值并未原样存储

            var storedCustomer = await db.BaseCustomers.AsNoTracking()
                .SingleAsync(c => c.CustomerCode == customerCode);
            Assert.NotEqual(100.00005m, storedCustomer.CreditLimit);
            Assert.Equal(100.0001m, storedCustomer.CreditLimit);    // 100.00005 → 100.0001：原值并未原样存储
        }

        // 超出列容量的值被 SQL Server 以算术溢出（8115）拒绝 —— 应用层护栏把它提前成受控错误。
        var overflow = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(_fixture.ConnectionString,
            "SELECT CAST(@v AS decimal(18,4))", 100_000_000_000_000m));
        Assert.Equal(8115, overflow.Number);

        // 恰好上限本身可被真实列原样保留（护栏放行的边界与列容量一致）。
        var limit = await ScalarAsync(_fixture.ConnectionString,
            "SELECT CONVERT(varchar(50), CAST(@v AS decimal(18,4)))", Max4);
        Assert.Equal("99999999999999.9999", limit);
    }

    // ==================== 只读 SQL 探针辅助（绝不写业务行） ====================

    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>按真实列顺序读取一个供应商行全部数值列的 <c>CONVERT(varchar, ...)</c> 文本（逐位原样证据）。</summary>
    private static async Task<string[]> SqlSupplierRowTextAsync(ErpDbContext db, string supplierCode)
    {
        var select = string.Join(", ", SupplierColumns.Select(c => $"CONVERT(varchar(50), [{c}])"));
        return await RowTextAsync(db,
            $"SELECT {select} FROM db_owner.BaseSuppliers WHERE SupplierCode = @code",
            supplierCode, SupplierColumns.Length);
    }

    /// <summary>按真实列顺序读取一个客户行全部数值列的 <c>CONVERT(varchar, ...)</c> 文本（逐位原样证据）。</summary>
    private static async Task<string[]> SqlCustomerRowTextAsync(ErpDbContext db, string customerCode)
    {
        var select = string.Join(", ", CustomerColumns.Select(c => $"CONVERT(varchar(50), [{c}])"));
        return await RowTextAsync(db,
            $"SELECT {select} FROM db_owner.BaseCustomers WHERE CustomerCode = @code",
            customerCode, CustomerColumns.Length);
    }

    private static async Task<string[]> RowTextAsync(
        ErpDbContext db, string sql, string code, int columnCount)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@code", code);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new string[columnCount];
        for (var i = 0; i < result.Length; i++) result[i] = reader.GetString(i);
        return result;
    }

    private static async Task<long> CountAsync(MasterDataRatePersistedBoundsSqlServerFixture fixture, string table)
    {
        await using var db = fixture.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM db_owner.{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>只读标量探针：绝不写入任何行（用于真实 SQL Server 自身的取整 / 溢出证据）。</summary>
    private static async Task<string> ScalarAsync(string connectionString, string sql, decimal value)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@v", value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// ERP-531 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 再播种一个同时具备既有「供应商资料」与「客户资料」菜单授权的真实启用身份，供真实
/// <see cref="SupplierController"/> / <see cref="CustomerController"/> 使用。
/// <para>任何库访问 / 建库之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> /
/// 集成安全），发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class MasterDataRatePersistedBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_MASTERDATARATEPREC_{Guid.NewGuid():N}";

    private long _authorizedUserId;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-531] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await InitialiseFreshDatabaseAsync();
        await SeedAuthorizedUserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>绑定已播种真实启用身份（同时具备既有 supplier / customer 菜单）的供应商控制器。</summary>
    public SupplierController NewSupplierController(ErpDbContext db)
        => new(new GenericService<BaseSupplier>(db), db)
        {
            ControllerContext = ControllerContextFor("/api/base/suppliers")
        };

    /// <summary>绑定已播种真实启用身份（同时具备既有 supplier / customer 菜单）的客户控制器。</summary>
    public CustomerController NewCustomerController(ErpDbContext db)
        => new(new GenericService<BaseCustomer>(db), db)
        {
            ControllerContext = ControllerContextFor("/api/base/customers")
        };

    private ControllerContext ControllerContextFor(string path)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, _authorizedUserId.ToString()) }, "Test"));
        http.Request.Path = path;
        return new ControllerContext { HttpContext = http };
    }

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task InitialiseFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-531] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 supplier / customer 菜单）。");
    }

    /// <summary>播种同时具备既有「供应商资料」与「客户资料」菜单授权的真实启用身份（不新增任何权限 / 菜单模型）。</summary>
    private async Task SeedAuthorizedUserAsync()
    {
        await using var db = CreateDbContext();
        var code = $"mr531-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP531 主数据数值精度测试账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        await GrantMenuAsync(db, role.Id, SupplierAuthorizationRules.RequiredMenuCode,
            SupplierAuthorizationRules.RequiredMenuText);
        await GrantMenuAsync(db, role.Id, CustomerAuthorizationRules.RequiredMenuCode,
            CustomerAuthorizationRules.RequiredMenuText);

        _authorizedUserId = user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单，不新增菜单模型）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode, string menuText)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuText, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        await db.SaveChangesAsync();
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class MasterDataRatePersistedBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => MasterDataRatePersistedBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}
