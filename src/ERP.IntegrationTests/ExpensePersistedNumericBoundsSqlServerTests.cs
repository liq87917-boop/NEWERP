using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-512 / ERP-538 费用单 / 装柜费用分摊「标量声明列精度与派生金额存储边界」的真实 SQL Server 集成测试
/// （NEWERP_AUTOTEST 护栏）：用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际列精度：<c>FinanceExpenses</c> 的金额 / 折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额列为
/// <c>decimal(18,4)</c>、汇率列为 <c>decimal(18,6)</c>（ERP-538 EF 显式声明，与权威建表脚本同源），
/// ERP-042 批次汇率 <c>decimal(18,6)</c>、分摊行比例 / 基数 <c>decimal(18,4)</c>、批次表头来源金额 / 分摊合计
/// <c>decimal(18,2)</c>，与 <see cref="FinancePersistedAmountRules"/> 常量同源；恰好等于上限的金额原样往返，
/// 超过上限的金额连原始 EF 写入都被真实列 / 驱动拒绝；</item>
/// <item>费用 CRUD：受权账号提交「第 5 位小数 / 第 7 位小数 / 取整进位越过上限 / 1e16 / 负数」一律在
/// **任何表头写入之前**被受控拒绝（同一受权控制器随后合法创建成功，证明拒绝原因是标量精度而非授权）；</item>
/// <item>传统分摊：折人民币乘法越界与分摊总额过精度 / 越界被拒绝且零写入、零单号消耗，合法请求照常落库；</item>
/// <item>ERP-042 批次：真实列内可存储但折人民币派生越界的来源费用与越界基准值都被拒绝（零批次 / 零分摊行 / 零费用行），
/// 合法「来源 → 分摊生成」流转仍在同一事务内落批次 + 逐行留痕 + 费用单行且行合计恒等于来源金额。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ExpensePersistedNumericBoundsSqlServerTests
    : IClassFixture<ExpensePersistedNumericBoundsSqlServerFixture>
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale4;  // 99_999_999_999_999.9999（金额类声明列上限）
    private const decimal MaxRate = FinancePersistedAmountRules.MaxDecimal18Scale6;    // 999_999_999_999.999999（汇率声明列上限）
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;                // 取整进位越过 DECIMAL(18,2) 上限（对 18,4 同样越界）
    private const decimal OneE16 = 10_000_000_000_000_000m;                            // 1e16（超出 DECIMAL(18,4) 上限）
    private const decimal OneE15 = 1_000_000_000_000_000m;                             // 1e15（超出 DECIMAL(18,4) 上限）

    private readonly ExpensePersistedNumericBoundsSqlServerFixture _fixture;

    public ExpensePersistedNumericBoundsSqlServerTests(ExpensePersistedNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ExpensePersistedNumericBoundsSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 1. 实际列精度 + 边界值原样往返 + 真实列拒绝越界 ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules_and_boundary_values_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal(4, FinancePersistedAmountRules.DecimalScale4);
        Assert.Equal(6, FinancePersistedAmountRules.DecimalScale6);

        // ERP-538 EF 模型口径：FinanceExpense 金额类列显式 decimal(18,4)、汇率列显式 decimal(18,6)。
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.Amount)));
        Assert.Equal("decimal(18,6)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.ExchangeRate)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.AmountCny)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.TaxRate)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.TaxAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.AllocationRatio)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpense), nameof(FinanceExpense.AllocatedAmount)));
        // ERP-042 批次 / 分摊行：显式列精度（批次表结构不变）。
        Assert.Equal("decimal(18,6)", ColumnType(typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.ExchangeRate)));
        Assert.Equal("decimal(18,2)", ColumnType(typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.SourceAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.Ratio)));
        Assert.Equal("decimal(18,4)", ColumnType(typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.BasisValue)));
        Assert.Equal("decimal(18,2)", ColumnType(typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.AllocatedAmountCny)));

        // 真实 SQL Server 列类型（INFORMATION_SCHEMA）与规则常量逐字同源。
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", "Amount"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "FinanceExpenses", "ExchangeRate"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", "AmountCny"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", "TaxRate"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", "TaxAmount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", "AllocationRatio"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", "AllocatedAmount"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "FinanceExpenseAllocationBatches", "ExchangeRate"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenseAllocationLines", "Ratio"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinanceExpenseAllocationLines", "AllocatedAmountCny"));

        // 恰好等于上限的金额原样往返（分毫不变）。
        var customer = await SeedCustomerAsync(db, "费用精度边界客户");
        var expense = await SeedExpenseAsync(db, $"MAX-{Tag()}", customer.Id, MaxAmount);
        await using (var verify = _fixture.CreateDbContext())
        {
            var reloaded = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == expense.Id);
            Assert.Equal(MaxAmount, reloaded.Amount);
            Assert.Equal(MaxAmount, reloaded.AmountCny);
        }

        // 超过上限的原始 EF 写入被真实列 / 驱动拒绝（绝非应用层提前拦截）。
        await using (var raw = _fixture.CreateDbContext())
        {
            var over = NewExpense(customer.Id, $"RAW-{Tag()}");
            over.Amount = OneE16;
            over.AmountCny = OneE16;
            over.TaxAmount = OneE16;
            raw.FinanceExpenses.Add(over);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
            AssertProviderRejected(ex);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(1, await verify.FinanceExpenses.CountAsync(x => x.CustomerId == customer.Id));
        }
    }

    [Fact]
    public async Task Sql_server_itself_cannot_convert_over_limit_scalar_amount_to_the_column_type()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        // 真实列精度：SQL Server 对越界标量报「算术溢出」（错误号 8115，与语言无关）——18,2 / 18,4 / 18,6 各自验证。
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(10000000000000000.00 AS decimal(18,2)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, ex.Number);

        // DECIMAL(18,4) / DECIMAL(18,6) 同样由 SQL Server 自身拒绝越界标量。
        var scale4 = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(100000000000000.0000 AS decimal(18,4)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, scale4.Number);
        var scale6 = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(1000000000000.000000 AS decimal(18,6)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, scale6.Number);
    }

    /// <summary>
    /// 异常链中必须出现提供程序层面的拒绝：SQL Server 算术溢出（<see cref="SqlException"/>）或
    /// SqlClient 客户端参数范围校验（<see cref="ArgumentException"/>）；绝不能是应用层提前拦截。
    /// </summary>
    private static void AssertProviderRejected(Exception exception)
    {
        var chain = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
            if (current is SqlException or ArgumentException) return;
        }
        Assert.Fail("expected the provider to reject the over-limit scalar amount: " + string.Join(" -> ", chain));
    }

    // ==================== 2. 费用 CRUD：越界标量拒绝且零写入 ====================

    [Theory]
    [InlineData("amount-round-up-cross")]
    [InlineData("amount-over-scale")]
    [InlineData("amount-magnitude")]
    [InlineData("amount-negative")]
    [InlineData("rate-over-scale")]
    [InlineData("rate-magnitude")]
    [InlineData("amountcny-magnitude")]
    [InlineData("taxrate-negative")]
    [InlineData("taxamount-magnitude")]
    [InlineData("allocationratio-over-scale")]
    [InlineData("allocationratio-magnitude")]
    [InlineData("allocatedamount-magnitude")]
    public async Task Create_expense_rejects_unpersistable_scalars_without_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "费用精度写入客户");
        var userId = await SeedUserAsync(db, ExpenseAuthorizationRules.RequiredMenuCode, "费用精度操作员");
        var controller = NewExpenseController(db, userId);

        var entity = NewExpense(customer.Id, $"BAD-{Tag()}");
        switch (scenario)
        {
            case "amount-round-up-cross":
                entity.Amount = RoundUpCrossing;         // 取整进位越过上限：本身不可逐位原样存储
                break;
            case "amount-over-scale":
                entity.Amount = 1.00005m;                // 第 5 位小数
                break;
            case "amount-magnitude":
                entity.Amount = OneE16;
                break;
            case "amount-negative":
                entity.Amount = -1m;
                break;
            case "rate-over-scale":
                entity.ExchangeRate = 1.0000005m;        // 第 7 位小数
                break;
            case "rate-magnitude":
                entity.ExchangeRate = OneE16;
                break;
            case "amountcny-magnitude":
                entity.AmountCny = OneE16;
                break;
            case "taxrate-negative":
                entity.TaxRate = -1m;
                break;
            case "taxamount-magnitude":
                entity.TaxAmount = OneE16;
                break;
            case "allocationratio-over-scale":
                entity.AllocationRatio = 1.00005m;       // 第 5 位小数
                break;
            case "allocationratio-magnitude":
                entity.AllocationRatio = OneE16;
                break;
            default:
                entity.AllocatedAmount = OneE16;
                break;
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        if (!scenario.EndsWith("-negative", StringComparison.Ordinal))
            Assert.Contains("DECIMAL(18,", ex.Message, StringComparison.Ordinal);

        // 被拒请求零写入。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.FinanceExpenses.CountAsync(x => x.CustomerId == customer.Id));
        }

        // 同一受权控制器随后合法创建成功（证明拒绝原因是标量边界而非授权）。
        var created = AssertOk<FinanceExpense>(await controller.Create(NewExpense(customer.Id, $"OK-{Tag()}")));
        Assert.Equal(100m, created.Amount);

        // 声明列上限 + 4 位小数 / 6 位小数 + 零值可选字段同样照常落库（真实列逐位原样存储）。
        var maxEx = NewExpense(customer.Id, $"MAX-{Tag()}");
        maxEx.Amount = MaxAmount;
        maxEx.AmountCny = MaxAmount;
        maxEx.ExchangeRate = MaxRate;
        maxEx.TaxRate = 1.2345m;
        maxEx.AllocationRatio = 33.3333m;
        var maxCreated = AssertOk<FinanceExpense>(await controller.Create(maxEx));
        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == maxCreated.Id);
            Assert.Equal(MaxAmount, stored.Amount);
            Assert.Equal(MaxAmount, stored.AmountCny);
            Assert.Equal(MaxRate, stored.ExchangeRate);
            Assert.Equal(1.2345m, stored.TaxRate);
            Assert.Equal(33.3333m, stored.AllocationRatio);
            Assert.Equal(0m, stored.TaxAmount);
            Assert.Equal(0m, stored.AllocatedAmount);
        }
    }

    // ==================== 3. 传统分摊：派生金额越界拒绝且零写入 / 零占号 ====================

    [Fact]
    public async Task Legacy_allocate_apply_rejects_unpersistable_derived_amounts_without_number_or_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "传统分摊精度客户");
        var userId = await SeedUserAsync(db, ExpenseAuthorizationRules.RequiredMenuCode, "传统分摊精度操作员");
        var controller = NewExpenseController(db, userId);

        // 折人民币 = 原币金额 × 汇率：真实列内各自可存储的极大值相乘仍会溢出折人民币声明列 → 受控拒绝。
        var overflow = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.AllocateApply(AllocateRequest(customer.Id, 1_000_000_000_000m, "USD", MaxRate)));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Contains("折人民币金额", overflow.Message, StringComparison.Ordinal);

        // 分摊总额自身超出声明列精度（1e15 / 第 5 位小数）：同样在单号生成之前拒绝。
        var bigTotal = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.AllocateApply(AllocateRequest(customer.Id, OneE16, "CNY", 1m)));
        Assert.Equal(ErrorCodes.InvalidParameter, bigTotal.Code);
        Assert.Contains("DECIMAL(18,4)", bigTotal.Message, StringComparison.Ordinal);

        var overScaleTotal = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.AllocateApply(AllocateRequest(customer.Id, 1.00005m, "CNY", 1m)));
        Assert.Equal(ErrorCodes.InvalidParameter, overScaleTotal.Code);
        Assert.Contains("DECIMAL(18,4)", overScaleTotal.Message, StringComparison.Ordinal);

        // 被拒请求零写入。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.FinanceExpenses.CountAsync(x => x.CustomerId == customer.Id));
        }

        // 合法请求照常生成：折人民币 = 分摊金额 × 汇率，且单号恰为既有最大序号 + 1（拒绝未占号）。
        var prefix = $"EXP-{DateTime.Today:yyyyMMdd}-";
        var maxNoBefore = await db.FinanceExpenses.AsNoTracking()
            .Where(x => x.ExpenseNo.StartsWith(prefix))
            .OrderByDescending(x => x.ExpenseNo)
            .Select(x => x.ExpenseNo)
            .FirstOrDefaultAsync();
        var expectedSeq = 1;
        if (maxNoBefore is not null && int.TryParse(maxNoBefore[prefix.Length..], out var parsed))
            expectedSeq = parsed + 1;

        Assert.IsType<OkObjectResult>(await controller.AllocateApply(
            AllocateRequest(customer.Id, 100m, "USD", 7.2m)));
        await using (var verify = _fixture.CreateDbContext())
        {
            var row = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.CustomerId == customer.Id);
            Assert.Equal(100m, row.Amount);
            Assert.Equal(100m, row.AllocatedAmount);
            Assert.Equal(720m, row.AmountCny);
            Assert.Equal($"{prefix}{expectedSeq:000}", row.ExpenseNo);
        }
    }

    // ==================== 4. ERP-042 批次：派生值越界拒绝且零批次零分摊行，合法生成原子落库 ====================

    [Fact]
    public async Task Batch_generate_rejects_invalidated_source_and_basis_without_partial_rows()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerA = await SeedCustomerAsync(db, "装柜精度客户A");
        var customerB = await SeedCustomerAsync(db, "装柜精度客户B");
        var userId = await SeedUserAsync(db, ExpenseAuthorizationRules.RequiredMenuCode, "装柜精度操作员");
        var controller = NewExpenseController(db, userId);
        var containerNo = $"TCLU-{Tag()}";
        var list = await SeedLoadingListAsync(db, containerNo, customerA.Id);
        var participantA = await SeedParticipantAsync(db, list.Id, customerA, primary: true);
        var participantB = await SeedParticipantAsync(db, list.Id, customerB);

        // ① 来源费用在真实列内可存储（恰好上限），但折人民币派生越界：预览与生成都在写入之前受控拒绝。
        var overflowSource = await SeedSourceExpenseAsync(
            db, $"EXP-{Tag()}", containerNo, 1_000_000_000_000m, "USD", MaxRate);
        var previewEx = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationPreview(
            BatchRequest(overflowSource.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));
        Assert.Equal(ErrorCodes.InvalidParameter, previewEx.Code);
        Assert.Contains("折人民币金额", previewEx.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationGenerate(
            BatchRequest(overflowSource.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));

        // ② 基准值超出 DECIMAL(18,4)：在任何 Db.Add 之前受控拒绝。
        var okSource = await SeedSourceExpenseAsync(db, $"EXP-{Tag()}", containerNo, 1000m, "USD", 7.2m);
        var basisEx = await Assert.ThrowsAsync<BusinessException>(() => controller.AllocationGenerate(
            BatchRequest(okSource.Id, list.Id, (participantA.Id, OneE15), (participantB.Id, 1m))));
        Assert.Equal(ErrorCodes.InvalidParameter, basisEx.Code);
        Assert.Contains("DECIMAL(18,4)", basisEx.Message, StringComparison.Ordinal);

        // 被拒请求零批次 / 零分摊行 / 零生成费用行（此刻仅 2 条来源费用行）。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.FinanceExpenseAllocationBatches.CountAsync(x => x.LoadingListId == list.Id));
            Assert.Equal(0, await verify.FinanceExpenseAllocationLines.CountAsync(x => x.LoadingListId == list.Id));
            Assert.Equal(2, await verify.FinanceExpenses.CountAsync(x => x.RefNo == containerNo));
        }

        // ③ 合法生成：批次 + 逐行留痕 + 生成费用单在同一事务落库，行合计恒等于来源金额、折人民币按汇率换算。
        var result = AssertOk<ContainerExpenseAllocationGenerateResultDto>(await controller.AllocationGenerate(
            BatchRequest(okSource.Id, list.Id, (participantA.Id, 30m), (participantB.Id, 70m))));
        Assert.Equal(1000m, result.AllocatedTotal);
        Assert.Equal(2, result.LineCount);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(1, await verify.FinanceExpenseAllocationBatches.CountAsync(x => x.LoadingListId == list.Id));
            var lines = await verify.FinanceExpenseAllocationLines.AsNoTracking()
                .Where(x => x.LoadingListId == list.Id)
                .OrderBy(x => x.AllocatedAmount)
                .ToListAsync();
            Assert.Equal(2, lines.Count);
            Assert.Equal(1000m, lines.Sum(l => l.AllocatedAmount));
            Assert.Equal(300m, lines[0].AllocatedAmount);
            Assert.Equal(2160m, lines[0].AllocatedAmountCny);
            Assert.Equal(700m, lines[1].AllocatedAmount);
            Assert.Equal(5040m, lines[1].AllocatedAmountCny);
        }
    }

    // ==================== 5. 真实 SQL 夹具种子与脚手架 ====================

    private static string ColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP512_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>），证明实际存储精度与规则常量同源。</summary>
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static ExpenseBillController NewExpenseController(ErpDbContext db, long? userId)
    {
        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            ActionDescriptor = new ControllerActionDescriptor()
        };
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static FinanceExpense NewExpense(long? customerId, string expenseNo)
        => new()
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = 100m,
            Currency = "CNY",
            ExchangeRate = 1m,
            AmountCny = 100m,
            TaxRate = 0m,
            TaxAmount = 0m,
            AllocationRatio = 0m,
            AllocatedAmount = 0m,
            CustomerId = customerId,
            RefType = "客户",
            RefNo = expenseNo,
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };

    private static ExpenseAllocateRequest AllocateRequest(
        long? customerId, decimal total, string currency, decimal rate)
        => new()
        {
            RefType = "拼柜",
            RefNo = $"TCLU-LEGACY-{Tag()}",
            ExpenseType = "报关费",
            TotalAmount = total,
            Currency = currency,
            ExchangeRate = rate,
            AllocationBase = "按体积",
            ExpenseDate = DateTime.Today,
            Details = new List<ExpenseAllocateDetail>
            {
                new() { CustomerId = customerId, CustomerName = "传统分摊客户", Volume = 1m }
            }
        };

    private static ContainerExpenseAllocationRequest BatchRequest(
        long sourceExpenseId, long loadingListId, params (long ParticipantId, decimal BasisValue)[] lines)
        => new()
        {
            SourceExpenseId = sourceExpenseId,
            LoadingListId = loadingListId,
            AllocationMethod = ContainerExpenseAllocationRules.MethodByVolume,
            Lines = lines.Select(l => new ContainerExpenseAllocationBasisDto
            {
                ParticipantId = l.ParticipantId,
                BasisValue = l.BasisValue
            }).ToList()
        };

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Tag()}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>播种真实授权账号：既有「费用单」（expense-bill）菜单授权 + 启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string menuCode, string displayName)
    {
        var role = new SysRole { RoleCode = $"EXPAMT-{Tag()}", RoleName = displayName, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"expamt-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = displayName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted)
            ?? throw new InvalidOperationException($"seed menu {menuCode} is missing");
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<FinanceExpense> SeedExpenseAsync(
        ErpDbContext db, string expenseNo, long? customerId, decimal amount)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = "CNY",
            ExchangeRate = 1m,
            AmountCny = amount,
            CustomerId = customerId,
            RefType = "客户",
            RefNo = expenseNo,
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static async Task<FinanceExpense> SeedSourceExpenseAsync(
        ErpDbContext db, string expenseNo, string containerNo, decimal amount, string currency, decimal exchangeRate)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            AmountCny = amount,
            RefType = "拼柜",
            RefNo = containerNo,
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        await db.SaveChangesAsync();
        return expense;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string containerNo, long customerId)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = $"ZG-{Tag()}",
            LoadingDate = DateTime.Today,
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = 10m,
            TotalWeight = 500m,
            TotalVolume = 3.5m
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    private static async Task<ContainerLoadingListParticipant> SeedParticipantAsync(
        ErpDbContext db, long loadingListId, BaseCustomer customer, bool primary = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = 1
        };
        db.ContainerLoadingListParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-512）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class ExpensePersistedNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_EXPENSEAMOUNT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-512] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
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

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 破坏性重置前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-512] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ExpensePersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ExpensePersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}
