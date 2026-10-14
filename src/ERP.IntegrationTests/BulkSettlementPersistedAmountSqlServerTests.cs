using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
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
/// ERP-515 散货结算单「标量金额存储边界」的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际列精度：<c>FinanceBulkSettlement.TotalAmount / FreightCost</c> 未显式 <c>HasPrecision</c>，
/// 真实 <c>INFORMATION_SCHEMA</c> 列为 <c>decimal(18,2)</c>，与既有 <see cref="FinancePersistedAmountRules"/> 常量同源；
/// 恰好等于上限的金额原样往返，超过上限的连原始 EF 写入都被真实列 / 驱动拒绝；</item>
/// <item>创建 / 修改（手工口径，散货结算单没有来源单据）：取整进位越过上限 / <c>1e16</c> / 极大十进制一律在
/// **单号预约与任何写入之前**受控拒绝（零表头写入、零单号序列消耗），同一受权控制器随后合法创建成功；</item>
/// <item>提交 / 审核：既有行锁内只读复核**已持久化**金额——真实 SQL 写入的零总金额 / 负海运费存量值（列本身允许）
/// 在提交 / 审核被受控拒绝，状态 / 字段 / 单号序列零变化，而读取与取消等撤销语义保持既有口径；</item>
/// <item>合法「创建 → 提交 → 审核」在真实列上原样持久化边界金额与单号序列。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST_ERP515_</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class BulkSettlementPersistedAmountSqlServerTests
    : IClassFixture<BulkSettlementPersistedAmountSqlServerFixture>
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）

    private readonly BulkSettlementPersistedAmountSqlServerFixture _fixture;

    public BulkSettlementPersistedAmountSqlServerTests(
        BulkSettlementPersistedAmountSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(BulkSettlementPersistedAmountSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 脚手架 ====================

    private static FinanceBulkSettlementController NewSettlementController(ErpDbContext db, long? userId)
    {
        var controller = new FinanceBulkSettlementController(db, new DocumentNumberService(db));
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
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static FinanceBulkSettlement NewSettlementRequest(
        long customerId, decimal totalAmount = 1000m, decimal freightCost = 0m)
        => new()
        {
            SettlementDate = DateTime.Today,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "ERP-515_SQL_TEST"
        };

    // ==================== 真实库种子 / 只读快照 ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<FinanceBulkSettlement> SeedSettlementAsync(
        ErpDbContext db, string settlementNo, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var settlement = new FinanceBulkSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = DateTime.Today,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "原始备注",
            Status = status
        };
        db.FinanceBulkSettlements.Add(settlement);
        await db.SaveChangesAsync();
        return settlement;
    }

    /// <summary>
    /// 播种真实登录账号：既有「散货结算单」（<c>bulk-settlement</c>）菜单授权 + 业务员员工映射
    /// （把目标客户分配给它的员工，数据范围恰好覆盖该客户，不新增权限模型）。
    /// </summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, long inScopeCustomerId)
    {
        var code = $"erp515-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            DisplayName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "散货结算金额边界角色",
            RoleCode = $"Erp515Amt-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == FinanceBulkSettlementLifecycleRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static Task<long> SequenceAsync(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.BulkSettlement && !r.IsDeleted)
            .Select(r => r.CurrentSequence).SingleAsync();

    private static Task<int> DocumentCountAsync(ErpDbContext db, long customerId)
        => db.FinanceBulkSettlements.AsNoTracking().CountAsync(s => s.CustomerId == customerId);

    private static Task<FinanceBulkSettlement> ReloadAsync(ErpDbContext db, long id)
        => db.FinanceBulkSettlements.AsNoTracking().SingleAsync(s => s.Id == id);

    private static decimal OverLimit(string scenario) => scenario switch
    {
        "round-up-cross" => RoundUpCrossing,
        "1e16" => OneE16,
        _ => decimal.MaxValue
    };


    // ==================== 1. 真实列精度 + 边界往返 + 越界被真实列 / 驱动拒绝 ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules_and_boundary_amounts_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal("decimal(18,2)",
            ColumnType(typeof(FinanceBulkSettlement), nameof(FinanceBulkSettlement.TotalAmount)));
        Assert.Equal("decimal(18,2)",
            ColumnType(typeof(FinanceBulkSettlement), nameof(FinanceBulkSettlement.FreightCost)));

        // 真实 SQL Server 列（INFORMATION_SCHEMA）：TRACE 到实际存储精度，与规则常量逐字同源。
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinanceBulkSettlements", "TotalAmount"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "FinanceBulkSettlements", "FreightCost"));

        var customer = await SeedCustomerAsync(db, $"INT_E515_P_{Tag()}", "精度边界客户");
        var settlement = await SeedSettlementAsync(db, $"INT_E515_S_{Tag()}", customer.Id,
            MaxAmount, MaxAmount);
        var reloaded = await ReloadAsync(db, settlement.Id);
        Assert.Equal(MaxAmount, reloaded.TotalAmount);
        Assert.Equal(MaxAmount, reloaded.FreightCost);
    }

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Raw_ef_write_of_over_limit_amount_is_rejected_by_the_real_column(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"INT_E515_R_{Tag()}", "越界写入客户");
        var customerId = customer.Id;
        var countBefore = await DocumentCountAsync(db, customerId);

        db.FinanceBulkSettlements.Add(new FinanceBulkSettlement
        {
            SettlementNo = $"INT_E515_OVF_{Tag()}",
            SettlementDate = DateTime.Today,
            CustomerId = customerId,
            TotalAmount = OverLimit(scenario),
            FreightCost = 0m,
            Status = DocumentStatus.Pending
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertProviderRejected(ex);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(countBefore, await DocumentCountAsync(verify, customerId));
    }

    [Fact]
    public async Task Sql_server_cannot_convert_over_limit_amount_to_the_column_type()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        // 真实列精度 decimal(18,2)：SQL Server 对越界标量报「算术溢出」（错误号 8115，与语言无关），
        // 证明应用层单列边界（FinancePersistedAmountRules）与真实存储边界逐分一致。
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(10000000000000000.00 AS decimal(18,2)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, ex.Number);
    }


    // ==================== 2. 创建 / 修改：越界拒绝（零写入 / 零占号）与精确上限 ====================

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Create_rejects_unpersistable_amount_without_numbering_or_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"INT_E515_C_{Tag()}", "创建越界客户");
        var userId = await SeedOperatorAsync(db, customer.Id);
        var controller = NewSettlementController(db, userId);

        var countBefore = await DocumentCountAsync(db, customer.Id);
        var sequenceBefore = await SequenceAsync(db);

        var totalEx = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: OverLimit(scenario))));
        Assert.Contains("DECIMAL(18,2)", totalEx.Message);

        var costEx = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: 100m,
                freightCost: OverLimit(scenario))));
        Assert.Contains("DECIMAL(18,2)", costEx.Message);

        // 被拒请求：零表头写入、零单号序列消耗（同一受权控制器随后合法创建成功并只 +1）。
        Assert.Equal(countBefore, await DocumentCountAsync(db, customer.Id));
        Assert.Equal(sequenceBefore, await SequenceAsync(db));

        var id = CreatedId(await controller.Create(NewSettlementRequest(customer.Id, totalAmount: MaxAmount)));
        var stored = await ReloadAsync(db, id);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(countBefore + 1, await DocumentCountAsync(db, customer.Id));
        Assert.Equal(sequenceBefore + 1, await SequenceAsync(db));
    }

    [Fact]
    public async Task Update_rejects_unpersistable_amount_and_leaves_stored_settlement_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"INT_E515_U_{Tag()}", "修改越界客户");
        var userId = await SeedOperatorAsync(db, customer.Id);
        var controller = NewSettlementController(db, userId);
        var settlement = await SeedSettlementAsync(
            db, $"INT_E515_SU_{Tag()}", customer.Id, 1000m, 10m);
        var before = await ReloadAsync(db, settlement.Id);

        var request = NewSettlementRequest(customer.Id, totalAmount: OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Update(settlement.Id, request));
        Assert.Contains("DECIMAL(18,2)", ex.Message);

        var after = await ReloadAsync(db, settlement.Id);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.FreightCost, after.FreightCost);
        Assert.Equal(before.SettlementNo, after.SettlementNo);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        // 恰好等于上限的修改照常放行。
        Assert.IsType<OkObjectResult>(await controller.Update(settlement.Id,
            NewSettlementRequest(customer.Id, totalAmount: MaxAmount, freightCost: MaxAmount)));
        var boundary = await ReloadAsync(db, settlement.Id);
        Assert.Equal(MaxAmount, boundary.TotalAmount);
        Assert.Equal(MaxAmount, boundary.FreightCost);
    }


    // ==================== 3. 合法边界生命周期与真实 SQL 存量异常值 ====================

    [Fact]
    public async Task Valid_lifecycle_create_submit_approve_persists_boundary_amount()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, $"INT_E515_F_{Tag()}", "合法流转客户");
        var userId = await SeedOperatorAsync(db, customer.Id);
        var controller = NewSettlementController(db, userId);
        var sequenceBefore = await SequenceAsync(db);

        var id = CreatedId(await controller.Create(
            NewSettlementRequest(customer.Id, totalAmount: MaxAmount, freightCost: MaxAmount)));
        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(id));

        var stored = await ReloadAsync(db, id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(MaxAmount, stored.TotalAmount);
        Assert.Equal(MaxAmount, stored.FreightCost);
        Assert.Equal(sequenceBefore + 1, await SequenceAsync(db));
    }

    /// <summary>
    /// 真实 SQL 存量异常值（金额列本身允许零 / 负值，只能由历史导入或直接数据操作产生）：
    /// 提交 / 审核在既有行锁内只读复核即受控拒绝，状态 / 单号序列零变化；读取与取消等撤销语义保持既有口径。
    /// </summary>
    [Fact]
    public async Task Raw_sql_zero_total_is_rejected_at_submit_but_reads_and_cancel_keep_existing_semantics()
    {
        Guard();
        long customerId, settlementId, userId;
        long sequenceBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E515_Z_{Tag()}", "存量零金额客户");
            customerId = customer.Id;
            userId = await SeedOperatorAsync(seed, customer.Id);
            settlementId = (await SeedSettlementAsync(seed, $"INT_E515_SZ_{Tag()}", customer.Id, 1000m)).Id;
            sequenceBefore = await SequenceAsync(seed);
        }

        // 直接 SQL 写入列本身允许的存量异常值（零总金额 / 负海运费）——独立连接，模拟历史导入 / 直接数据操作。
        await using (var raw = _fixture.CreateDbContext())
        {
            await raw.Database.ExecuteSqlRawAsync(
                "UPDATE db_owner.FinanceBulkSettlements SET TotalAmount = 0, FreightCost = -5.5 WHERE Id = {0}",
                settlementId);
            var malformed = await ReloadAsync(raw, settlementId);
            Assert.Equal(0m, malformed.TotalAmount);
            Assert.Equal(-5.5m, malformed.FreightCost);
        }

        // 历史读取照常可读（不因存量异常值 fail closed）。
        await using (var read = _fixture.CreateDbContext())
        {
            var byId = Assert.IsType<OkObjectResult>(
                await NewSettlementController(read, userId).GetById(settlementId));
            Assert.NotNull(byId.Value);
        }

        // 提交：锁内只读复核受控拒绝（零状态 / 零单号序列变化）。
        await using (var submit = _fixture.CreateDbContext())
        {
            var ex = await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
                () => NewSettlementController(submit, userId).Submit(settlementId));
            Assert.Contains("必须大于 0", ex.Message);
        }
        await using (var verify = _fixture.CreateDbContext())
        {
            var afterSubmit = await ReloadAsync(verify, settlementId);
            Assert.Equal(DocumentStatus.Pending, afterSubmit.Status);
            Assert.Equal(0m, afterSubmit.TotalAmount);
            Assert.Equal(-5.5m, afterSubmit.FreightCost);
            Assert.Equal(sequenceBefore, await SequenceAsync(verify));
        }

        // 审核同样拒绝（状态不符先于金额判定，状态零变化）。
        await using (var approve = _fixture.CreateDbContext())
        {
            await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
                () => NewSettlementController(approve, userId).Approve(settlementId));
        }
        await using (var verify = _fixture.CreateDbContext())
            Assert.Equal(DocumentStatus.Pending, (await ReloadAsync(verify, settlementId)).Status);

        // 取消等撤销路径不新增草稿有效性闸门：存量异常金额不阻止既有撤销语义。
        await using (var cancel = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewSettlementController(cancel, userId).Cancel(settlementId));
        await using (var verify = _fixture.CreateDbContext())
        {
            var cancelled = await ReloadAsync(verify, settlementId);
            Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
            Assert.Equal(0m, cancelled.TotalAmount);
            Assert.Equal(sequenceBefore, await SequenceAsync(verify));
        }

        // 取消是既有软状态变更：历史行仍可读（未物理删除），存量异常金额原样保留。
        await using (var verify = _fixture.CreateDbContext())
            Assert.Equal(1, await DocumentCountAsync(verify, customerId));
    }

    /// <summary>
    /// 真实列精度为 <c>decimal(18,2)</c>：SQL Server 自身在写入时按列小数位取整（<c>12.345 → 12.35</c>、
    /// <c>-0.004 → 0.00</c>），因此实际存量金额必然可原样存储；提交 / 审核复核仍按同一口径只读判定
    /// （不取整、不改写存量值）。
    /// </summary>
    [Fact]
    public async Task Sql_column_rounds_fractional_raw_value_and_valid_lifecycle_still_passes()
    {
        Guard();
        long settlementId, userId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var customer = await SeedCustomerAsync(seed, $"INT_E515_D_{Tag()}", "列取整客户");
            userId = await SeedOperatorAsync(seed, customer.Id);
            settlementId = (await SeedSettlementAsync(
                seed, $"INT_E515_SD_{Tag()}", customer.Id, 1000m)).Id;
        }

        await using (var raw = _fixture.CreateDbContext())
        {
            await raw.Database.ExecuteSqlRawAsync(
                "UPDATE db_owner.FinanceBulkSettlements SET TotalAmount = CAST(12.345 AS decimal(18,2)), "
                + "FreightCost = CAST(-0.004 AS decimal(18,2)) WHERE Id = {0}",
                settlementId);
        }
        await using (var verify = _fixture.CreateDbContext())
        {
            var rounded = await ReloadAsync(verify, settlementId);
            Assert.Equal(12.35m, rounded.TotalAmount);
            Assert.Equal(0m, rounded.FreightCost);
        }

        // 存量值按列精度取整后必然可表示：既有正负口径满足，提交 / 审核照常放行（绝不改写存量值）。
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewSettlementController(db, userId).Submit(settlementId));
        await using (var db = _fixture.CreateDbContext())
            Assert.IsType<OkObjectResult>(await NewSettlementController(db, userId).Approve(settlementId));
        await using (var verify = _fixture.CreateDbContext())
        {
            var approved = await ReloadAsync(verify, settlementId);
            Assert.Equal(DocumentStatus.Approved, approved.Status);
            Assert.Equal(12.35m, approved.TotalAmount);
            Assert.Equal(0m, approved.FreightCost);
        }
    }


    // ==================== 4. 列类型 / 提供程序拒绝脚手架 ====================

    private static string ColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_ERP515_MODEL_ONLY;"
                + "Integrated Security=true;TrustServerCertificate=true")
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
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + "
            + "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
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

    /// <summary>
    /// 异常链中必须出现提供程序层面的拒绝：SQL Server 算术溢出（<see cref="SqlException"/>，错误号 8115）、
    /// SqlClient 客户端参数范围校验（<see cref="ArgumentException"/>）或 SqlClient 十进制转换溢出
    /// （<see cref="OverflowException"/>，<c>Conversion overflows.</c>）；绝不能是应用层提前拦截。
    /// </summary>
    private static void AssertProviderRejected(Exception exception)
    {
        var chain = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
            if (current is SqlException or ArgumentException or OverflowException) return;
        }
        Assert.Fail("expected the provider to reject the over-limit scalar amount: " + string.Join(" -> ", chain));
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// ERP-515 专用 localdb 夹具：每次运行创建一个全新 GUID 后缀库（<c>NEWERP_AUTOTEST_ERP515_*</c>），
/// 在任何数据库访问之前校验实例名 / 库名前缀 / 集成安全；发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class BulkSettlementPersistedAmountSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST_ERP515_";

    private const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP515";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-515] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

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

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
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

        Console.WriteLine("[ERP-515] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class BulkSettlementPersistedAmountTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP515_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP515_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => BulkSettlementPersistedAmountSqlServerFixture.AssertDedicatedTarget(connection));
}
