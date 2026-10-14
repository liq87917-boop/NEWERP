using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-513 收货计划总件数（<see cref="ContainerReceivingPlan.TotalQuantity"/>）实际列精度存储边界的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：用与运行时完全相同的调用路径（真实控制器 + 真实身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际列精度：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 均为 <c>decimal(18,2)</c>，恰好等于上下限的数量原样往返，
/// 越界原始 EF 写入被真实列 / 驱动拒绝；SQL Server 自身亦无法把越界标量转换为该列类型（错误号 8115）；</item>
/// <item>新增 / 修改：受权账号提交过精度 / 溢出数量在**单号预约与任何写入之前**受控拒绝（零单号消耗、零写入），
/// 可表示的 <c>0</c> / 负数草稿照常落库并被服务端权威覆盖；</item>
/// <item>承诺：合法零 / 负草稿禁止提交 / 审核且状态 / 数量不变，可存储正数生命周期数量原样保存；</item>
/// <item>历史读取 / 取消：**不新增**任何草稿有效性闸门。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ReceivingPlanNumericBoundsSqlServerTests
    : IClassFixture<ReceivingPlanNumericBoundsSqlServerFixture>
{
    private const decimal MaxQuantity = ReceivingPlanLifecycleRules.MaxQuantity;   // 9_999_999_999_999_999.99
    private const decimal MinQuantity = ReceivingPlanLifecycleRules.MinQuantity;   // -9_999_999_999_999_999.99
    private const decimal OneE16 = 10_000_000_000_000_000m;                        // 1e16（超出 DECIMAL(18,2) 上限）
    private const decimal OverPrecision = 12.345m;                                 // 过精度（3 位小数）

    private readonly ReceivingPlanNumericBoundsSqlServerFixture _fixture;

    public ReceivingPlanNumericBoundsSqlServerTests(ReceivingPlanNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReceivingPlanNumericBoundsSqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

        Assert.Equal(18, ReceivingPlanLifecycleRules.QuantityPrecision);
        Assert.Equal(2, ReceivingPlanLifecycleRules.QuantityScale);
        Assert.Equal(9_999_999_999_999_999.99m, ReceivingPlanLifecycleRules.MaxQuantity);
        Assert.Equal(-9_999_999_999_999_999.99m, ReceivingPlanLifecycleRules.MinQuantity);

        // EF 模型口径：TotalQuantity 未显式声明精度 → 提供程序默认 decimal(18,2)。
        var property = db.Model.FindEntityType(typeof(ContainerReceivingPlan))!
            .FindProperty(nameof(ContainerReceivingPlan.TotalQuantity))!;
        Assert.Equal("decimal(18,2)", property.GetColumnType());

        // 真实 SQL Server 列类型（INFORMATION_SCHEMA）与规则常量逐字同源。
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "ContainerReceivingPlans", "TotalQuantity"));

        var supplierId = await SeedSupplierAsync(db, "总件数精度边界供应商");
        var maxPlan = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: MaxQuantity);
        var minPlan = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: MinQuantity);
        var zeroPlan = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 0m);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(MaxQuantity, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == maxPlan.Id)).TotalQuantity);
            Assert.Equal(MinQuantity, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == minPlan.Id)).TotalQuantity);
            Assert.Equal(0m, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == zeroPlan.Id)).TotalQuantity);
        }

        // 超过上限的原始 EF 写入被真实列 / 驱动拒绝（绝非应用层提前拦截）。
        await using (var raw = _fixture.CreateDbContext())
        {
            var over = NewPlan(supplierId, totalQuantity: OneE16);
            over.PlanNo = $"RP-RAW-{Tag()}";
            raw.ContainerReceivingPlans.Add(over);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => raw.SaveChangesAsync());
            AssertProviderRejected(ex);
        }
    }

    [Fact]
    public async Task Sql_server_itself_cannot_convert_over_limit_quantity_to_the_column_type()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        // 真实列精度 decimal(18,2)：SQL Server 对越界标量报「算术溢出」（错误号 8115，与语言无关）。
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(10000000000000000.00 AS decimal(18,2)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, ex.Number);
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
        Assert.Fail("expected the provider to reject the over-limit quantity: " + string.Join(" -> ", chain));
    }

    // ==================== 2. 新增 / 修改：受控拒绝但零写入零占号，草稿零 / 负数照常 ====================

    [Theory]
    [InlineData("over-precision")]
    [InlineData("huge-positive")]
    [InlineData("huge-negative")]
    [InlineData("decimal-max")]
    public async Task Create_rejects_unpersistable_quantity_before_numbering_without_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var supplierId = await SeedSupplierAsync(db, "总件数拒绝供应商");
        var controller = NewController(db, adminId);

        var before = await db.ContainerReceivingPlans.AsNoTracking()
            .CountAsync(p => p.SupplierId == supplierId);
        var sequenceBefore = await ReceivingPlanSequenceAsync(db);

        var quantity = scenario switch
        {
            "over-precision" => OverPrecision,
            "huge-positive" => OneE16,
            "huge-negative" => -OneE16,
            _ => decimal.MaxValue
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(NewPlan(supplierId, totalQuantity: quantity)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message, StringComparison.Ordinal);

        // 拒绝严格先于单号预约与写入。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(before, await verify.ContainerReceivingPlans.CountAsync(p => p.SupplierId == supplierId));
            Assert.Equal(sequenceBefore, await ReceivingPlanSequenceAsync(verify));
        }

        // 同一受权控制器随后合法创建成功（证明拒绝原因是数量存储边界而非授权）。
        AssertOkObject(await controller.Create(NewPlan(supplierId, totalQuantity: 8m)));
        await using (var verify = _fixture.CreateDbContext())
        {
            var created = await verify.ContainerReceivingPlans.AsNoTracking()
                .Where(p => p.SupplierId == supplierId).OrderByDescending(p => p.Id).FirstAsync();
            Assert.Equal(8m, created.TotalQuantity);
            Assert.False(string.IsNullOrWhiteSpace(created.PlanNo));
            Assert.True(await ReceivingPlanSequenceAsync(verify) > sequenceBefore);
        }
    }

    [Fact]
    public async Task Draft_create_and_update_preserve_representable_zero_and_negative_quantity()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var supplierId = await SeedSupplierAsync(db, "草稿零负数供应商");
        var controller = NewController(db, adminId);

        AssertOkObject(await controller.Create(NewPlan(supplierId, totalQuantity: 0m)));
        AssertOkObject(await controller.Create(NewPlan(supplierId, totalQuantity: -7.25m)));

        long draftId;
        await using (var verify = _fixture.CreateDbContext())
        {
            var rows = await verify.ContainerReceivingPlans.AsNoTracking()
                .Where(p => p.SupplierId == supplierId).OrderBy(p => p.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(0m, rows[0].TotalQuantity);
            Assert.Equal(-7.25m, rows[1].TotalQuantity);
            Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.PlanNo)));
            draftId = rows[0].Id;
        }

        // 过精度修改：受控拒绝且库中草稿完全不变。
        await AssertCode(ErrorCodes.InvalidParameter, () => controller.Update(draftId,
            NewPlan(supplierId, totalQuantity: OverPrecision, destination: "NEW-DEST")));
        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == draftId);
            Assert.Equal(0m, stored.TotalQuantity);
            Assert.NotEqual("NEW-DEST", stored.Destination);
        }

        // 可表示的负数草稿照常可写（既有草稿口径不变）。
        AssertOkObject(await controller.Update(draftId,
            NewPlan(supplierId, totalQuantity: -9.99m, destination: "NEG-DEST")));
        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == draftId);
            Assert.Equal(-9.99m, stored.TotalQuantity);
            Assert.Equal("NEG-DEST", stored.Destination);
        }
    }

    // ==================== 3. 承诺（提交 / 审核）与正数生命周期 ====================

    [Fact]
    public async Task Legal_zero_or_negative_draft_is_denied_commitment_and_state_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var supplierId = await SeedSupplierAsync(db, "承诺拒绝供应商");
        var zero = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 0m);
        var negative = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: -5m);
        var zeroSubmitted = await SeedPlanAsync(db, supplierId, DocumentStatus.Submitted, totalQuantity: 0m);
        var controller = NewController(db, adminId);

        await AssertCode(ErrorCodes.InvalidParameter, () => controller.Submit(zero.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => controller.Submit(negative.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => controller.Approve(zeroSubmitted.Id));

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Pending, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == zero.Id)).Status);
            Assert.Equal(DocumentStatus.Pending, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == negative.Id)).Status);
            Assert.Equal(DocumentStatus.Submitted, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == zeroSubmitted.Id)).Status);
            Assert.Equal(-5m, (await verify.ContainerReceivingPlans.AsNoTracking()
                .SingleAsync(p => p.Id == negative.Id)).TotalQuantity);
        }
    }

    [Fact]
    public async Task Positive_lifecycle_preserves_exact_stored_quantity()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var supplierId = await SeedSupplierAsync(db, "正数生命周期供应商");
        var portId = await SeedOtherInfoAsync(db, "Port", "SHANGHAI");
        var controller = NewController(db, adminId);

        AssertOkObject(await controller.Create(NewPlan(supplierId, portId: portId, totalQuantity: 12345.67m)));

        long planId;
        await using (var verify = _fixture.CreateDbContext())
        {
            planId = await verify.ContainerReceivingPlans.AsNoTracking()
                .Where(p => p.SupplierId == supplierId).Select(p => p.Id).SingleAsync();
        }

        AssertOkObject(await controller.Submit(planId));
        AssertOkObject(await controller.Approve(planId));

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == planId);
            Assert.Equal(DocumentStatus.Approved, stored.Status);
            Assert.Equal(12345.67m, stored.TotalQuantity);
        }
    }

    // ==================== 4. 历史读取 / 取消：不新增草稿有效性闸门 ====================

    [Fact]
    public async Task Historical_read_and_cancel_keep_existing_gates()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var supplierId = await SeedSupplierAsync(db, "历史取消供应商");
        var zero = await SeedPlanAsync(db, supplierId, DocumentStatus.Submitted, totalQuantity: 0m);
        var controller = NewController(db, adminId);

        var page = AssertOk<PagedResult<ContainerReceivingPlan>>(
            await controller.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));
        Assert.Contains(page.Items, p => p.Id == zero.Id);
        Assert.Equal(0m, AssertOk<ContainerReceivingPlan>(await controller.GetById(zero.Id)).TotalQuantity);

        // 取消沿用既有状态机（不要求正数、不新增数量闸门），数量保持原样。
        AssertOkObject(await controller.Cancel(zero.Id));
        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == zero.Id);
            Assert.Equal(DocumentStatus.Cancelled, stored.Status);
            Assert.Equal(0m, stored.TotalQuantity);
        }
    }

    // ==================== 5. 既有实时身份 / 菜单授权不变 ====================

    [Fact]
    public async Task Live_restricted_operator_with_menu_rejects_unpersistable_quantity_without_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, _) = await SeedRestrictedOperatorAsync(db, withReceivingPlanMenu: true);
        var supplierId = await SeedSupplierAsync(db, "受限操作员供应商");
        var controller = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(NewPlan(supplierId, totalQuantity: OverPrecision)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.ContainerReceivingPlans.CountAsync(p => p.SupplierId == supplierId));
        }

        AssertOkObject(await controller.Create(NewPlan(supplierId, totalQuantity: 5m)));
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(1, await verify.ContainerReceivingPlans.CountAsync(p => p.SupplierId == supplierId));
        }
    }

    // ==================== 真实 SQL 夹具种子与脚手架 ====================

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

    private static ContainerReceivingPlanController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerReceivingPlanController(db, new DocumentNumberService(db));
        SetUser(ctl, userId);
        return ctl;
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

    private static ContainerReceivingPlan NewPlan(long supplierId, long? portId = null,
        decimal totalQuantity = 1m, string destination = "", string remark = "ERP-513_INT")
        => new()
        {
            PlanDate = DateTime.Today,
            SupplierId = supplierId,
            PortId = portId,
            TotalQuantity = totalQuantity,
            Destination = destination,
            Remark = remark
        };

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name, int status = 1)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"RP513-S-{Tag()}",
            SupplierName = name,
            Status = status
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<long> SeedOtherInfoAsync(ErpDbContext db, string infoType, string code, int status = 1)
    {
        var other = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = $"{code}-{Tag()}",
            InfoName = $"{infoType}-{code}",
            Status = status
        };
        db.BaseOtherInfos.Add(other);
        await db.SaveChangesAsync();
        return other.Id;
    }

    private static async Task<ContainerReceivingPlan> SeedPlanAsync(ErpDbContext db, long supplierId,
        DocumentStatus status, decimal totalQuantity = 1m, long? portId = null, string destination = "")
    {
        var plan = new ContainerReceivingPlan
        {
            PlanNo = $"RP-Q-{Tag()}",
            PlanDate = DateTime.Today,
            SupplierId = supplierId,
            PortId = portId,
            TotalQuantity = totalQuantity,
            Destination = destination,
            Status = status,
            Remark = "ERP-513_INT"
        };
        db.ContainerReceivingPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    }

    /// <summary>播种真实受限操作员（员工映射 + 既有「收货计划」菜单授权），保持既有 ERP-361 身份口径不变。</summary>
    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, bool withReceivingPlanMenu)
    {
        var code = $"rpq-op-{Tag()}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = "收货计划操作员", RoleCode = $"RpqOp-{Tag()}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withReceivingPlanMenu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == ReceivingPlanLifecycleRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        return (user.Id, employee.Id);
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    /// <summary>收货计划（JH）单号流水当前值：证明被拒请求零单号消耗。</summary>
    private static Task<long> ReceivingPlanSequenceAsync(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.ReceivingPlan && !r.IsDeleted)
            .Select(r => r.CurrentSequence)
            .FirstAsync();

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static void AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-513）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class ReceivingPlanNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RECEIVINGPLANQTY_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-513] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-513] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ReceivingPlanNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ReceivingPlanNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ReceivingPlanNumericBoundsSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
