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
/// 装柜出运里程碑证据（ERP-058 / ERP-365）的**真实 SQL Server 两条独立连接竞争**集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item><b>create/create</b>：两个独立连接并发登记同一父出运引用 + 事件类型 + 事件时间，经
/// **父引用行锁 + 源记录行锁**（<c>UPDLOCK, HOLDLOCK</c>）+ 可序列化事务串行化后恰好一方成功，
/// 库中只留一条有效证据（另一方明确重复）；</item>
/// <item><b>parent void vs create</b>：两个独立连接并发「父出运引用作废」与「里程碑登记」，
/// 经同一把父引用行锁串行化后只产生一致结果（登记先赢 → 证据存在且父引用随后作废；
/// 作废先赢 → 登记被明确拒绝且一条证据都不落库）；</item>
/// <item><b>void/void</b>：两个独立连接并发作废同一里程碑，经里程碑行锁串行化后恰好一方成功，
/// 原始事件 / 时间 / 来源 / 记录人与首个作废原因绝不被改写。</item>
/// </list>
/// <para>全部通过**真实控制器**（<c>ContainerShipmentMilestoneController</c> /
/// <c>ContainerShipmentReferenceController</c>）与真实身份（既有种子登录账号，不新增任何用户授权）驱动；
/// 目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ShipmentMilestoneConcurrencySqlServerTests
    : IClassFixture<ShipmentMilestoneConcurrencySqlServerFixture>
{
    private readonly ShipmentMilestoneConcurrencySqlServerFixture _fixture;

    public ShipmentMilestoneConcurrencySqlServerTests(ShipmentMilestoneConcurrencySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ShipmentMilestoneConcurrencySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 两条独立连接：create / create 竞争同一父记录 + 类型 + 时间 ====================

    [Fact]
    public async Task Real_two_connections_racing_milestone_for_one_parent_key_only_one_effective_event()
    {
        Guard();
        long userId;
        long referenceId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            referenceId = await SeedActiveReferenceAsync(seeding, userId, $"DG-CSM-RACE-{Guid.NewGuid():N}");
        }

        var race = await RaceAsync(
            () => TryCreateMilestoneAsync(referenceId, userId, "并发登记甲"),
            () => TryCreateMilestoneAsync(referenceId, userId, "并发登记乙"));

        Assert.Equal(1, race.Count(r => r.Ok));
        var failure = Assert.Single(race.Where(r => !r.Ok)).Error;
        var conflict = Assert.IsType<BusinessException>(failure);
        Assert.Equal(ErrorCodes.Duplicate, conflict.Code);
        Assert.Contains("不会静默合并", conflict.Message);

        await using var verify = _fixture.CreateDbContext();
        var rows = await verify.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted && m.ContainerShipmentReferenceId == referenceId)
            .ToListAsync();
        var stored = Assert.Single(rows);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, stored.Status);
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeActualDeparture, stored.EventType);
        Assert.Equal(userId, stored.CreatedBy);
        Assert.Contains(stored.SourceDescription, new[] { "并发登记甲", "并发登记乙" });
    }

    // ==================== 2. 两条独立连接：父引用作废 vs 里程碑登记 ====================

    [Fact]
    public async Task Real_two_connections_racing_parent_void_and_registration_only_consistent_result()
    {
        Guard();
        long userId;
        long referenceId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            referenceId = await SeedActiveReferenceAsync(seeding, userId, $"DG-CSM-PVOID-{Guid.NewGuid():N}");
        }

        var race = await RaceAsync(
            () => TryCreateMilestoneAsync(referenceId, userId, "作废竞争登记"),
            () => TryVoidParentAsync(referenceId, userId, "父引用并发作废"));

        // 作废本身必须成功（父引用初始为已登记）
        Assert.True(race[1].Ok, "父出运引用作废应当成功");

        await using var verify = _fixture.CreateDbContext();
        var reference = await verify.ContainerShipmentReferences.AsNoTracking()
            .SingleAsync(r => r.Id == referenceId);
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided, reference.Status);
        Assert.Equal("父引用并发作废", reference.VoidReason);
        Assert.NotNull(reference.VoidedAt);

        var milestones = await verify.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted && m.ContainerShipmentReferenceId == referenceId)
            .ToListAsync();

        if (race[0].Ok)
        {
            // 登记先赢：证据已提交，父引用随后作废 —— 历史证据保留可读，登记时间不晚于作废时间
            var single = Assert.Single(milestones);
            Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, single.Status);
            Assert.True(single.RecordedAt <= reference.VoidedAt,
                "登记先赢时里程碑必须早于父引用作废（串行化保证）");
        }
        else
        {
            // 作废先赢：后到登记在锁内看到父引用已作废 → 明确拒绝，且一条证据都不落库
            var conflict = Assert.IsType<BusinessException>(race[0].Error);
            Assert.Equal(ErrorCodes.RuleConflict, conflict.Code);
            Assert.Contains("已作废", conflict.Message);
            Assert.Empty(milestones);
        }

        // 绝不改写上游源记录 / 相邻业务数据
        Assert.Equal(DocumentStatus.Pending, (await verify.ContainerBookings.AsNoTracking()
            .SingleAsync(b => b.Id == reference.SourceId)).Status);
    }

    // ==================== 3. 两条独立连接：void / void 重复作废 ====================

    [Fact]
    public async Task Real_two_connections_racing_repeated_void_keeps_original_evidence_and_single_reason()
    {
        Guard();
        long userId;
        long referenceId;
        long milestoneId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            referenceId = await SeedActiveReferenceAsync(seeding, userId, $"DG-CSM-VOID-{Guid.NewGuid():N}");
            var created = AssertOk<ContainerShipmentMilestoneDto>(await ControllerFor(seeding, userId)
                .Create(NewMilestoneDto(referenceId, "原始来源说明", "原始记录人")));
            milestoneId = created.Id;
        }

        var race = await RaceAsync(
            () => TryVoidMilestoneAsync(milestoneId, userId, "并发作废甲"),
            () => TryVoidMilestoneAsync(milestoneId, userId, "并发作废乙"));

        Assert.Equal(1, race.Count(r => r.Ok));
        var failure = Assert.IsType<BusinessException>(Assert.Single(race.Where(r => !r.Ok)).Error);
        Assert.Equal(ErrorCodes.RuleConflict, failure.Code);
        Assert.Contains("已作废", failure.Message);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerShipmentMilestones.AsNoTracking().SingleAsync(m => m.Id == milestoneId);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusVoided, stored.Status);
        Assert.NotNull(stored.VoidedAt);
        Assert.Contains(stored.VoidReason, new[] { "并发作废甲", "并发作废乙" });

        // 原始事件 / 时间 / 来源 / 记录人逐字段保留（作废不是删除，也不是改写）
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeActualDeparture, stored.EventType);
        Assert.Equal("原始来源说明", stored.SourceDescription);
        Assert.Equal("原始记录人", stored.RecordedBy);
        Assert.Equal(userId, stored.CreatedBy);

        // 作废后同一父 + 类型 + 时间可重新登记一条新的有效证据（已作废行不占额度）
        await using var again = _fixture.CreateDbContext();
        var reRecorded = AssertOk<ContainerShipmentMilestoneDto>(await ControllerFor(again, userId)
            .Create(NewMilestoneDto(referenceId, "重新登记", "重新记录人")));
        Assert.NotEqual(milestoneId, reRecorded.Id);
    }

    // ==================== 4. 身份缺失 / 父引用源记录取消 ====================

    [Fact]
    public async Task Missing_identity_cannot_read_or_register_milestone_and_leaves_no_evidence()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        long referenceId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            var userId = await ResolveSeededAdminIdAsync(seeding);
            referenceId = await SeedActiveReferenceAsync(seeding, userId, $"DG-CSM-DENIED-{Guid.NewGuid():N}");
        }

        var controller = ControllerFor(db, null);
        await Assert.ThrowsAsync<BusinessException>(() => controller.GetPaged(new ContainerShipmentMilestoneQuery()));
        await Assert.ThrowsAsync<BusinessException>(() => controller.ParentCandidates(null));
        await Assert.ThrowsAsync<BusinessException>(() => controller.GetById(1));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Create(NewMilestoneDto(referenceId)));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Void(
            1, new ContainerShipmentMilestoneVoidRequest { Reason = "匿名作废" }));

        Assert.Equal(0, await db.ContainerShipmentMilestones.CountAsync(m =>
            m.ContainerShipmentReferenceId == referenceId));
        var sourceId = (await db.ContainerShipmentReferences.AsNoTracking()
            .SingleAsync(r => r.Id == referenceId)).SourceId;
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == sourceId)).Status);
    }

    [Fact]
    public async Task Cancelled_parent_source_preserves_readable_history_but_rejects_new_milestone()
    {
        Guard();
        long userId;
        long referenceId;
        long bookingId;
        long milestoneId;
        await using (var seeding = _fixture.CreateDbContext())
        {
            userId = await ResolveSeededAdminIdAsync(seeding);
            var booking = await SeedBookingAsync(seeding, $"DG-CSM-CANCEL-{Guid.NewGuid():N}");
            bookingId = booking.Id;
            referenceId = await SeedActiveReferenceAsync(seeding, userId, booking.BookingNo!, booking.Id);
            milestoneId = AssertOk<ContainerShipmentMilestoneDto>(await ControllerFor(seeding, userId)
                .Create(NewMilestoneDto(referenceId))).Id;
        }

        // 源记录（订柜信息）被取消：历史里程碑照常可读，但不得新增证据
        await using (var mutating = _fixture.CreateDbContext())
        {
            var booking = await mutating.ContainerBookings.SingleAsync(b => b.Id == bookingId);
            booking.Status = DocumentStatus.Cancelled;
            await mutating.SaveChangesAsync();
        }

        await using var verify = _fixture.CreateDbContext();
        var controller = ControllerFor(verify, userId);
        var history = AssertOk<ContainerShipmentMilestoneDetailDto>(await controller.GetById(milestoneId));
        Assert.Equal(milestoneId, history.Milestone.Id);

        var conflict = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            NewMilestoneDto(referenceId, eventAt: DateTime.Now.AddMinutes(-5))));
        Assert.Equal(ErrorCodes.RuleConflict, conflict.Code);
        Assert.Contains("源记录已取消", conflict.Message);

        Assert.Equal(1, await verify.ContainerShipmentMilestones.CountAsync(m =>
            m.ContainerShipmentReferenceId == referenceId));
        Assert.Equal(DocumentStatus.Cancelled,
            (await verify.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
    }

    // ==================== 5. 脚手架：真实控制器 / 真实身份 / 两条独立连接 ====================

    private static ContainerShipmentMilestoneController ControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new ContainerShipmentMilestoneController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(Claims(userId), "IntegrationTest"))
            }
        };
        return controller;
    }

    private static ContainerShipmentReferenceController ReferenceControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new ContainerShipmentReferenceController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(Claims(userId), "IntegrationTest"))
            }
        };
        return controller;
    }

    private static System.Security.Claims.Claim[] Claims(long? userId)
        => userId.HasValue
            ? new[]
            {
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, userId.Value.ToString())
            }
            : Array.Empty<System.Security.Claims.Claim>();

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

    /// <summary>经真实 ERP-362 控制器登记一条有效出运引用（父引用），返回其 Id。</summary>
    private static async Task<long> SeedActiveReferenceAsync(
        ErpDbContext db, long userId, string bookingNo, long? bookingId = null)
    {
        var sourceId = bookingId ?? (await SeedBookingAsync(db, bookingNo)).Id;
        var created = AssertOk<ContainerShipmentReferenceDto>(await ReferenceControllerFor(db, userId).Create(
            new ContainerShipmentReferenceSaveDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = sourceId,
                ShipmentMode = "FCL"
            }));
        return created.Id;
    }

    private static ContainerShipmentMilestoneSaveDto NewMilestoneDto(
        long referenceId, string sourceDescription = "并发来源说明", string recordedBy = "并发记录人",
        DateTime? eventAt = null)
        => new()
        {
            ContainerShipmentReferenceId = referenceId,
            EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            EventAt = eventAt ?? new DateTime(2026, 9, 12, 10, 30, 0),
            SourceDescription = sourceDescription,
            RecordedBy = recordedBy
        };

    /// <summary>一条独立连接 = 一个独立 <c>ErpDbContext</c>（真实 SQL Server 上的独立连接）。</summary>
    private async Task<(bool Ok, Exception? Error)> TryCreateMilestoneAsync(
        long referenceId, long userId, string sourceDescription)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ControllerFor(db, userId).Create(NewMilestoneDto(referenceId, sourceDescription));
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<(bool Ok, Exception? Error)> TryVoidMilestoneAsync(
        long milestoneId, long userId, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ControllerFor(db, userId).Void(
                milestoneId, new ContainerShipmentMilestoneVoidRequest { Reason = reason });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    private async Task<(bool Ok, Exception? Error)> TryVoidParentAsync(long referenceId, long userId, string reason)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await ReferenceControllerFor(db, userId).Void(
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
/// 专用 localdb 目标 Fixture（ERP-365）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server
/// 里程碑竞争测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值，绝不读取
/// appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ShipmentMilestoneConcurrencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SHIPMENTMILESTONECONCURRENCY_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine(
            $"[ERP-365] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-365] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ShipmentMilestoneConcurrencyTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ShipmentMilestoneConcurrencySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ShipmentMilestoneConcurrencySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
