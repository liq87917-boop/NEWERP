using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 装柜出运引用登记（ERP-362）的**真实 SQL Server 两条独立连接竞争**集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item><b>create/create</b>：两个独立连接并发登记**同一源记录**，经源记录行锁（<c>UPDLOCK, HOLDLOCK</c>）+
/// 可序列化事务串行化后恰好一方成功，库中只留一条有效引用（另一方明确重复）；</item>
/// <item><b>update/void</b>：两个独立连接并发「修订 vs 作废」同一引用行，经引用行锁串行化后
/// 作废证据绝不被改写，失败方的失败信息明确（已作废 → 规则冲突）；</item>
/// <item><b>repeated revision</b>：两个独立连接并发修订同一引用，串行化后修订号单调、留痕逐版保留前值、无丢失无重复。</item>
/// </list>
/// <para>全部通过**真实控制器**（<c>ContainerShipmentReferenceController</c>）与真实身份（既有登录账号，
/// 不新增任何用户授权）驱动；目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；
/// 绝不 drop / reset / 复用任何数据库，连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ShipmentReferenceConcurrencySqlServerTests
    : IClassFixture<ShipmentReferenceConcurrencySqlServerFixture>
{
    private readonly ShipmentReferenceConcurrencySqlServerFixture _fixture;

    public ShipmentReferenceConcurrencySqlServerTests(ShipmentReferenceConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ShipmentReferenceConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 两条独立连接：create / create 竞争同一源记录 ====================

    [Fact]
    public async Task Real_two_connections_racing_create_for_one_source_record_only_one_reference()
    {
        Guard();
        long userId;
        long bookingId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            bookingId = (await SeedBookingAsync(seeding, $"DG-CSR-CREATE-{Guid.NewGuid():N}")).Id;
        }

        var race = await RaceAsync(
            () => TryCreateAsync(bookingId, userId, "FCL"),
            () => TryCreateAsync(bookingId, userId, "LCL"));

        Assert.Equal(1, race.Count(r => r.Ok));
        var failure = Assert.Single(race.Where(r => !r.Ok)).Error;
        var conflict = Assert.IsType<BusinessException>(failure);
        Assert.Equal(ErrorCodes.Duplicate, conflict.Code);
        Assert.Contains("已有有效出运引用", conflict.Message);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.ContainerShipmentReferences.AsNoTracking()
            .Where(r => !r.IsDeleted
                        && r.SourceType == ContainerShipmentReferenceRules.SourceTypeBooking
                        && r.SourceId == bookingId)
            .ToListAsync();
        var stored = Assert.Single(rows);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, stored.Status);
        Assert.Equal(1, stored.RevisionNo);
        Assert.Equal(0, await verify.ContainerShipmentReferenceRevisions.AsNoTracking()
            .CountAsync(r => r.ContainerShipmentReferenceId == stored.Id));
    }

    // ==================== 2. 两条独立连接：update / void 竞争同一引用 ====================

    [Fact]
    public async Task Real_two_connections_racing_update_and_void_never_rewrite_voided_evidence()
    {
        Guard();
        long userId;
        long bookingId;
        long referenceId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            bookingId = (await SeedBookingAsync(seeding, $"DG-CSR-UV-{Guid.NewGuid():N}")).Id;
            var created = AssertOk<ContainerShipmentReferenceDto>(await ControllerFor(seeding, userId).Create(
                NewDto(ContainerShipmentReferenceRules.SourceTypeBooking, bookingId, "LCL")));
            referenceId = created.Id;
        }

        var race = await RaceAsync(
            () => TryUpdateAsync(referenceId, bookingId, userId, "并发修订", "CARRIER-RACE"),
            () => TryVoidAsync(referenceId, userId, "并发作废"));

        // 首次作废必须成功；作废证据（原始值 / 作废原因）绝不被任何并发修订改写
        Assert.True(race[1].Ok, "首次作废应当成功");

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerShipmentReferences.AsNoTracking().SingleAsync(o => o.Id == referenceId);
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided, stored.Status);
        Assert.Equal("并发作废", stored.VoidReason);
        Assert.NotNull(stored.VoidedAt);

        var revisions = await verify.ContainerShipmentReferenceRevisions.AsNoTracking()
            .Where(r => r.ContainerShipmentReferenceId == referenceId)
            .ToListAsync();

        if (race[0].Ok)
        {
            // 修订先赢：V2 已提交，随后的作废只置作废状态，不回退修订值
            Assert.Equal(2, stored.RevisionNo);
            Assert.Equal("CARRIER-RACE", stored.CarrierName);
            Assert.Equal("FCL", stored.ShipmentMode);
            var revision = Assert.Single(revisions);
            Assert.Equal(1, revision.RevisionNo);
            Assert.Equal("LCL", revision.ShipmentMode);      // 修订前原值保留
            Assert.Equal("并发修订", revision.Reason);
        }
        else
        {
            // 作废先赢：后到的修订在锁内看到已作废状态 → 明确规则冲突，留痕与新值都不落库
            var conflict = Assert.IsType<BusinessException>(race[0].Error);
            Assert.Equal(ErrorCodes.RuleConflict, conflict.Code);
            Assert.Contains("已作废", conflict.Message);
            Assert.Equal(1, stored.RevisionNo);
            Assert.Equal("LCL", stored.ShipmentMode);
            Assert.Empty(revisions);
        }
    }

    // ==================== 3. 两条独立连接：重复修订保持单调修订序列 ====================

    [Fact]
    public async Task Real_two_connections_racing_repeated_revision_keeps_monotonic_revision_sequence()
    {
        Guard();
        long userId;
        long bookingId;
        long referenceId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            bookingId = (await SeedBookingAsync(seeding, $"DG-CSR-REV-{Guid.NewGuid():N}")).Id;
            var created = AssertOk<ContainerShipmentReferenceDto>(await ControllerFor(seeding, userId).Create(
                new ContainerShipmentReferenceSaveDto
                {
                    SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                    SourceId = bookingId,
                    ShipmentMode = "LCL",
                    CarrierName = "CARRIER-ORIGINAL"
                }));
            referenceId = created.Id;
        }

        var race = await RaceAsync(
            () => TryUpdateAsync(referenceId, bookingId, userId, "并发修订-A", "CARRIER-A"),
            () => TryUpdateAsync(referenceId, bookingId, userId, "并发修订-B", "CARRIER-B"));

        Assert.True(race[0].Ok && race[1].Ok, "同一引用的两次并发修订应在行锁内串行化后全部成功");

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerShipmentReferences.AsNoTracking().SingleAsync(o => o.Id == referenceId);
        Assert.Equal(3, stored.RevisionNo);                       // 单调递增，无丢失
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, stored.Status);

        var revisions = await verify.ContainerShipmentReferenceRevisions.AsNoTracking()
            .Where(r => r.ContainerShipmentReferenceId == referenceId)
            .OrderBy(r => r.RevisionNo)
            .ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(new[] { 1, 2 }, revisions.Select(r => r.RevisionNo).ToArray());
        Assert.Equal("CARRIER-ORIGINAL", revisions[0].CarrierName);   // V1 修订前原值
        Assert.Equal("LCL", revisions[0].ShipmentMode);
        Assert.Contains(revisions[1].CarrierName, new[] { "CARRIER-A", "CARRIER-B" });  // V2 前值 = 先到者
        Assert.Equal(
            new[] { "并发修订-A", "并发修订-B" },
            revisions.Select(r => r.Reason).OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    // ==================== 4. 脚手架：真实控制器 / 真实身份 / 两条独立连接 ====================

    private static ContainerShipmentReferenceController ControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new ContainerShipmentReferenceController(db);
        var claims = userId.HasValue
            ? new[] { new System.Security.Claims.Claim(
                System.Security.Claims.ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<System.Security.Claims.Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(claims, "IntegrationTest"))
            }
        };
        return controller;
    }

    /// <summary>已有种子数据里的既有超级管理员账号（不新增任何用户授权）。</summary>
    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task<ContainerBooking> SeedBookingAsync(ErpDbContext db, string bookingNo)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = DateTime.Today,
            CustomerId = 1,
            ContainerType = ContainerType.GP40,
            Status = DocumentStatus.Pending
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static ContainerShipmentReferenceSaveDto NewDto(string sourceType, long sourceId, string mode)
        => new() { SourceType = sourceType, SourceId = sourceId, ShipmentMode = mode };

    /// <summary>一条独立连接 = 一个独立 <c>ErpDbContext</c>（真实 SQL Server 上的独立连接）。</summary>
    private async Task<(bool Ok, Exception? Error)> TryCreateAsync(long bookingId, long userId, string mode)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ControllerFor(db, userId).Create(
                NewDto(ContainerShipmentReferenceRules.SourceTypeBooking, bookingId, mode));
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<(bool Ok, Exception? Error)> TryUpdateAsync(
        long referenceId, long bookingId, long userId, string reason, string carrier)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ControllerFor(db, userId).Update(referenceId, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = bookingId,
                Reason = reason,
                ShipmentMode = "FCL",
                CarrierName = carrier
            });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<(bool Ok, Exception? Error)> TryVoidAsync(long referenceId, long userId, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ControllerFor(db, userId).Void(
                referenceId, new ContainerShipmentReferenceVoidRequest { Reason = reason });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    /// <summary>让两个动作尽量同时起跑，并在**两条独立连接**上并行执行。</summary>
    private static async Task<(bool Ok, Exception? Error)[]> RaceAsync(
        params Func<Task<(bool Ok, Exception? Error)>>[] actions)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions
            .Select(action => Task.Run(async () =>
            {
                await gate.Task;
                return await action();
            }))
            .ToArray();
        gate.SetResult(true);
        return await Task.WhenAll(tasks);
    }

    // ==================== 断言脚手架 ====================

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-362）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 竞争测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ShipmentReferenceConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SHIPMENTREFCONCURRENCY_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine(
            $"[ERP-362] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-362] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ShipmentReferenceConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ShipmentReferenceConcurrencySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ShipmentReferenceConcurrencySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
