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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-439 代理服务费协议证据（<c>api/agency-service-fee-agreements</c>）**真实 SQL Server** 实时授权与
/// 权威客户范围集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>每一条路由在读取 / 写入之前都要求实时启用身份 + 既有「客户资料」（customer）功能菜单（缺一即 fail closed）；</item>
/// <item>受限业务员只读 / 只写本人被分配客户的协议，范围在 <c>Count</c> / 分页之前下推；</item>
/// <item>越范围 / 已删除 / 不存在的协议返回同一条不披露存在性的错误，且被拒写入零行；</item>
/// <item>被许可的协议生命周期（草稿 → 登记 → 作废）在同一范围内正常完成。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class AgencyServiceFeeAgreementAuthorizationSqlServerTests
    : IClassFixture<AgencyServiceFeeAgreementAuthorizationSqlServerFixture>
{
    private readonly AgencyServiceFeeAgreementAuthorizationSqlServerFixture _fixture;

    public AgencyServiceFeeAgreementAuthorizationSqlServerTests(
        AgencyServiceFeeAgreementAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AgencyServiceFeeAgreementAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 断言 / 控制器脚手架 ====================

    private static async Task<BusinessException> AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
        return ex;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static AgencyServiceFeeAgreementController NewController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new AgencyServiceFeeAgreementController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"ASFAGR-C-{Guid.NewGuid():N}"[..30], CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedEmployeeAsync(ErpDbContext db, string userName)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private static async Task<long> SeedRestrictedOperatorAsync(ErpDbContext db, string userName, bool grantMenu)
    {
        var user = new SysUser
        {
            UserName = userName, DisplayName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "代理服务费协议证据操作员", RoleCode = $"AsfAgrOp-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        await GrantCustomerMenuIfAsync(db, role.Id, grantMenu);
        return user.Id;
    }

    private static async Task<long> SeedPrivilegedOperatorAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"asf-agr-priv-{Guid.NewGuid():N}", DisplayName = "代理服务费协议证据特权账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "代理服务费协议证据特权角色", RoleCode = $"AsfAgrPriv-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        await GrantCustomerMenuIfAsync(db, role.Id, true);
        return user.Id;
    }

    private static async Task GrantCustomerMenuIfAsync(ErpDbContext db, long roleId, bool grant)
    {
        if (!grant) return;
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedDeletedUserAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"asf-agr-deleted-{Guid.NewGuid():N}", DisplayName = "已删除账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled, IsDeleted = true
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<AgencyServiceFeeAgreement> SeedAgreementAsync(
        ErpDbContext db, long customerId, string agreementNo,
        int status = AgencyServiceFeeAgreementRules.StatusDraft, bool deleted = false)
    {
        var agreement = new AgencyServiceFeeAgreement
        {
            AgreementNo = agreementNo,
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeAgreementNo(agreementNo),
            CustomerId = customerId,
            CustomerCode = "ASFAGR-INT",
            CustomerName = "集成测试客户",
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = "USD",
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = 1.5m,
            FeeBasis = "按出口发票金额",
            Status = status,
            RecordedAt = status == AgencyServiceFeeAgreementRules.StatusRecorded ? new DateTime(2026, 1, 2) : null,
            RecordedBy = status == AgencyServiceFeeAgreementRules.StatusRecorded ? "集成测试" : string.Empty,
            IsDeleted = deleted
        };
        db.AgencyServiceFeeAgreements.Add(agreement);
        await db.SaveChangesAsync();
        return agreement;
    }

    private static AgencyServiceFeeAgreementSaveDto SaveDto(long customerId, string agreementNo)
        => new()
        {
            AgreementNo = agreementNo,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = "USD",
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = 1.5m,
            FeeBasis = "按出口发票金额",
            Remark = "集成测试"
        };

    // ==================== 1. 身份 / 菜单矩阵（每一条路由先于任何读取 / 写入） ====================

    [Fact]
    public async Task Live_routes_reject_missing_disabled_deleted_and_revoked_identities_with_zero_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "ERP-439 认证客户");
        var recorded = await SeedAgreementAsync(
            db, customer, $"ASFAGR-AUTH-R-{Tag()}", AgencyServiceFeeAgreementRules.StatusRecorded);
        var draft = await SeedAgreementAsync(
            db, customer, $"ASFAGR-AUTH-D-{Tag()}", AgencyServiceFeeAgreementRules.StatusDraft);

        var disabledId = await SeedPrivilegedOperatorAsync(db);
        await DisableUserAsync(db, disabledId);
        var deletedId = await SeedDeletedUserAsync(db);
        var revokedId = await SeedPrivilegedOperatorAsync(db);
        await RevokeMenusAsync(db, revokedId);
        var noMenuId = await SeedRestrictedOperatorAsync(db, $"asf439-nomenu-{Guid.NewGuid():N}", grantMenu: false);

        var before = await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync();

        // 缺失 / 非法 / 已删除身份 → 未认证
        foreach (long? userId in new long?[] { null, 0, deletedId })
        {
            var ctl = NewController(db, userId);
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new AgencyServiceFeeAgreementQuery()));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(recorded.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(SaveDto(customer, $"ASFAGR-X-{Tag()}")));
            await AssertCode(ErrorCodes.Unauthorized,
                () => ctl.Update(draft.Id, SaveDto(customer, draft.AgreementNo)));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.Record(draft.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.Void(draft.Id,
                new AgencyServiceFeeAgreementVoidRequest { Reason = "越权作废" }));
        }

        // 禁用 / 撤销菜单 / 无菜单 → 权限不足
        foreach (var userId in new[] { disabledId, revokedId, noMenuId })
        {
            var ctl = NewController(db, userId);
            await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new AgencyServiceFeeAgreementQuery()));
            await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(recorded.Id));
            await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(SaveDto(customer, $"ASFAGR-Y-{Tag()}")));
            await AssertCode(ErrorCodes.Forbidden,
                () => ctl.Update(draft.Id, SaveDto(customer, draft.AgreementNo)));
            await AssertCode(ErrorCodes.Forbidden, () => ctl.Record(draft.Id));
            await AssertCode(ErrorCodes.Forbidden, () => ctl.Void(draft.Id,
                new AgencyServiceFeeAgreementVoidRequest { Reason = "越权作废" }));
        }

        // 零变更：被拒请求既不落新协议，也不改变既有协议状态
        Assert.Equal(before, await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync());
        var storedRecorded = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == recorded.Id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, storedRecorded.Status);
        var storedDraft = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == draft.Id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, storedDraft.Status);
        Assert.Null(storedDraft.RecordedAt);
    }

    // ==================== 2. 客户数据范围收敛 + 被许可生命周期 + 零变更 ====================

    [Fact]
    public async Task Live_scope_isolates_own_foreign_and_deleted_agreements_and_zeroes_denied_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userName = $"asf439-own-{Guid.NewGuid():N}";
        var employeeId = await SeedEmployeeAsync(db, userName);
        var operatorId = await SeedRestrictedOperatorAsync(db, userName, grantMenu: true);

        var ownCustomer = await SeedCustomerAsync(db, "ERP-439 本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "ERP-439 他人客户");
        var ownDraft = await SeedAgreementAsync(
            db, ownCustomer, $"ASFAGR-OWN-D-{Tag()}", AgencyServiceFeeAgreementRules.StatusDraft);
        var foreignDraft = await SeedAgreementAsync(
            db, foreignCustomer, $"ASFAGR-FGN-D-{Tag()}", AgencyServiceFeeAgreementRules.StatusDraft);
        var deletedOwn = await SeedAgreementAsync(
            db, ownCustomer, $"ASFAGR-OWN-DEL-{Tag()}", AgencyServiceFeeAgreementRules.StatusDraft, deleted: true);

        var ctl = NewController(db, operatorId);

        // 台账按客户范围过滤：受限业务员只看到自有客户的未删除协议
        var ledger = AssertOk<PagedResult<AgencyServiceFeeAgreementDto>>(
            await ctl.GetPaged(new AgencyServiceFeeAgreementQuery()));
        Assert.Equal(1, ledger.Total);
        Assert.All(ledger.Items, i => Assert.Equal(ownCustomer, i.CustomerId));

        // 自有范围内：草稿 → 登记 → 作废被许可
        var created = AssertOk<AgencyServiceFeeAgreementDto>(
            await ctl.Create(SaveDto(ownCustomer, $"ASFAGR-OWN2-{Tag()}")));
        Assert.True(created.IsDraft);
        var recordedCreated = AssertOk<AgencyServiceFeeAgreementDto>(await ctl.Record(created.Id));
        Assert.True(recordedCreated.IsRecorded);
        var voidedCreated = AssertOk<AgencyServiceFeeAgreementDto>(await ctl.Void(
            recordedCreated.Id, new AgencyServiceFeeAgreementVoidRequest { Reason = "集成更正" }));
        Assert.True(voidedCreated.IsVoided);

        // 越范围 / 已删除 / 不存在：同一条不披露错误
        var foreign = await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(foreignDraft.Id));
        var deleted = await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(deletedOwn.Id));
        var missing = await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(987_654_321L));
        Assert.Equal(foreign.Message, missing.Message);
        Assert.Equal(deleted.Message, missing.Message);
        Assert.Equal(AgencyServiceFeeAgreementRules.AgreementNotFoundText, foreign.Message);

        await AssertCode(ErrorCodes.NotFound,
            () => ctl.Update(foreignDraft.Id, SaveDto(foreignCustomer, foreignDraft.AgreementNo)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Record(foreignDraft.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Void(foreignDraft.Id,
            new AgencyServiceFeeAgreementVoidRequest { Reason = "越权作废" }));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(SaveDto(foreignCustomer, $"ASFAGR-FGN-N-{Tag()}")));
        await AssertCode(ErrorCodes.NotFound,
            () => ctl.Update(ownDraft.Id, SaveDto(foreignCustomer, ownDraft.AgreementNo)));

        // 零变更：越范围 / 已删除协议仍为草稿且未作废
        var storedForeign = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == foreignDraft.Id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, storedForeign.Status);
        Assert.Null(storedForeign.VoidedAt);
        var storedDeleted = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == deletedOwn.Id);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, storedDeleted.Status);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-439）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class AgencyServiceFeeAgreementAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ASFAGREEAUTH_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-439] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
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

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的 Fixture 库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-439] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class AgencyServiceFeeAgreementAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AgencyServiceFeeAgreementAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => AgencyServiceFeeAgreementAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
