using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-353 预装柜单衔接待审核订柜信息的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）。
/// <para>直接对 <see cref="PreLoadingBookingLinkRules"/> 与两个控制器的锁 / 事务口径做真实 SQL Server 验证：
/// 权威来源矩阵 fail closed、柜号链接冲突、失败不改动状态、订柜取消与已审核预装柜（及下游已审核装柜清单）
/// 的协同拒绝与显式解除，以及**真实两连接**「订柜取消 vs 预装柜审核」经订柜行 UPDLOCK/HOLDLOCK
/// 串行化后只允许一方成功。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class PreLoadingBookingLinkSqlServerTests
    : IClassFixture<PreLoadingBookingLinkSqlServerFixture>
{
    private readonly PreLoadingBookingLinkSqlServerFixture _fixture;

    public PreLoadingBookingLinkSqlServerTests(PreLoadingBookingLinkSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PreLoadingBookingLinkSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static async Task<long> SeedPrivilegedUserAsync(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "集成特权角色", RoleCode = $"PL-PRIV-{Tag()}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"pl-priv-{Tag()}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "集成特权用户", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>短唯一后缀（既有单据号 / 编码列都存在长度上限，这里保持远低于 50 字符）。</summary>
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<long> SeedProductAsync(ErpDbContext db, string code)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = code, Spec = "规格A", Unit = "PCS" };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"PL-C-{Tag()}", CustomerName = "集成客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ContainerBooking> SeedBookingAsync(ErpDbContext db, long customerId,
        DocumentStatus status, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = $"ITPL-B-{Tag()}",
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task<ContainerPreLoading> SeedPreLoadingAsync(ErpDbContext db, long? bookingId,
        DocumentStatus status, string containerNo, long productId)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = $"ITPL-L-{Tag()}",
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            ContainerNo = containerNo,
            SealNo = "SEAL-INT",
            Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        await db.SaveChangesAsync();

        db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
        {
            PreLoadingId = pre.Id, ProductId = productId, ProductName = "商品A", Quantity = 5m, Cartons = 1m
        });
        await db.SaveChangesAsync();
        return pre;
    }

    private static async Task<ContainerPreLoading> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().SingleAsync(p => p.Id == id);

    private static async Task<ContainerBooking> ReloadBookingAsync(ErpDbContext db, long id)
        => await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == id);

    // ==================== 权威来源矩阵 ====================

    [Fact]
    public async Task Live_approved_booking_is_resolved_and_link_is_accepted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var booking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        var entity = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Pending, "INT-CTN-A", productId);

        var resolved = await PreLoadingBookingLinkRules.ResolveAuthoritativeBookingOrThrowAsync(db, booking.Id);
        Assert.Equal(booking.Id, resolved.Id);
        await PreLoadingBookingLinkRules.ValidateLinkAsync(db, await ReloadAsync(db, entity.Id), userId);

        var tracking = await ContainerShipmentTrackingService
            .ResolveForPreLoadingAsync(db, await ReloadAsync(db, entity.Id));
        Assert.True(tracking.Linked);
        Assert.Equal(booking.Id, tracking.BookingId);
    }

    [Fact]
    public async Task Live_non_positive_booking_id_is_rejected()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var entity = await SeedPreLoadingAsync(db, 0L, DocumentStatus.Pending, "INT-CTN-ZERO", productId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PreLoadingBookingLinkRules.ValidateLinkAsync(db, entity, userId));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Cancelled)]
    [InlineData(DocumentStatus.Rejected)]
    public async Task Live_non_authoritative_booking_is_rejected_and_state_unchanged(DocumentStatus status)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var booking = await SeedBookingAsync(db, customer.Id, status);
        var entity = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Submitted, "INT-CTN-B", productId);

        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await PreLoadingBookingLinkRules
            .ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id), userId));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        Assert.Equal(DocumentStatus.Submitted, (await ReloadAsync(db, entity.Id)).Status);
        Assert.Equal(status, (await ReloadBookingAsync(db, booking.Id)).Status);
    }

    [Fact]
    public async Task Live_deleted_or_missing_booking_is_rejected()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var deleted = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved, deleted: true);
        var toDeleted = await SeedPreLoadingAsync(db, deleted.Id, DocumentStatus.Pending, "INT-CTN-DEL", productId);
        var toMissing = await SeedPreLoadingAsync(db, 987654321L, DocumentStatus.Pending, "INT-CTN-MISS", productId);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            PreLoadingBookingLinkRules.ValidateLinkAsync(db, toDeleted, userId));
        var missingEx = await Assert.ThrowsAsync<BusinessException>(() =>
            PreLoadingBookingLinkRules.ValidateLinkAsync(db, toMissing, userId));

        Assert.Equal(ErrorCodes.RuleConflict, deletedEx.Code);
        Assert.Contains("已删除", deletedEx.Message);
        Assert.Equal(ErrorCodes.RuleConflict, missingEx.Code);
    }

    [Fact]
    public async Task Live_unlinked_legacy_record_keeps_historical_behaviour()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var legacy = await SeedPreLoadingAsync(db, null, DocumentStatus.Pending, "INT-CTN-LEGACY", productId);

        await PreLoadingBookingLinkRules.ValidateLinkAsync(db, legacy, userId);

        var tracking = await ContainerShipmentTrackingService.ResolveForPreLoadingAsync(db, legacy);
        Assert.False(tracking.Linked);
    }

    [Fact]
    public async Task Live_denied_identity_and_menu_leave_state_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var booking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        var entity = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Submitted, "INT-CTN-DENY", productId);

        var anonymous = await Assert.ThrowsAsync<BusinessException>(() =>
            PreLoadingBookingLinkRules.EnsureAuthorizedAsync(db, null));
        Assert.Equal(ErrorCodes.Unauthorized, anonymous.Code);

        var noMenu = await SeedMenuLessUserAsync(db);
        var forbidden = await Assert.ThrowsAsync<BusinessException>(async () =>
            await PreLoadingBookingLinkRules.ValidateApprovalAsync(db, await ReloadAsync(db, entity.Id), noMenu));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Code);

        Assert.Equal(DocumentStatus.Submitted, (await ReloadAsync(db, entity.Id)).Status);
        Assert.Equal(DocumentStatus.Approved, (await ReloadBookingAsync(db, booking.Id)).Status);
        Assert.Empty(await db.StockMovements.ToListAsync());
        Assert.Empty(await db.FinanceExpenses.ToListAsync());
    }

    private static async Task<long> SeedMenuLessUserAsync(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "无菜单角色", RoleCode = $"PL-NOM-{Tag()}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"pl-nom-{Tag()}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "无菜单用户", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    // ==================== 权威柜号链接冲突 ====================

    [Fact]
    public async Task Live_conflicting_container_linkage_is_rejected_but_same_or_terminal_is_allowed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var booking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Approved, "INT-CTN-A", productId);

        var conflicting = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Submitted, "INT-CTN-B", productId);
        var conflictEx = await Assert.ThrowsAsync<BusinessException>(async () => await PreLoadingBookingLinkRules
            .ValidateApprovalAsync(db, await ReloadAsync(db, conflicting.Id), userId));
        Assert.Equal(ErrorCodes.RuleConflict, conflictEx.Code);
        Assert.Contains("权威柜号链接冲突", conflictEx.Message);
        Assert.Equal(DocumentStatus.Submitted, (await ReloadAsync(db, conflicting.Id)).Status);

        // 同柜号（忽略大小写与首尾空白）= 同一柜号主张：允许；用独立订柜信息避免被上面的冲突单据影响
        var sameBooking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        await SeedPreLoadingAsync(db, sameBooking.Id, DocumentStatus.Approved, "INT-CTN-SAME", productId);
        var sameContainer = await SeedPreLoadingAsync(db, sameBooking.Id, DocumentStatus.Submitted,
            " int-ctn-same ", productId);
        await PreLoadingBookingLinkRules.ValidateLinkAsync(db, await ReloadAsync(db, sameContainer.Id), userId);

        // 已取消 / 已驳回的预装柜单不构成冲突
        var terminalBooking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        await SeedPreLoadingAsync(db, terminalBooking.Id, DocumentStatus.Cancelled, "INT-CTN-CANCELLED", productId);
        await SeedPreLoadingAsync(db, terminalBooking.Id, DocumentStatus.Rejected, "INT-CTN-REJECTED", productId);
        var fresh = await SeedPreLoadingAsync(db, terminalBooking.Id, DocumentStatus.Pending, "INT-CTN-FRESH", productId);

        await PreLoadingBookingLinkRules.ValidateLinkAsync(db, await ReloadAsync(db, fresh.Id), userId);
    }

    [Fact]
    public async Task Live_source_change_refused_keeps_original_link_and_details()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var approved = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        var pending = await SeedBookingAsync(db, customer.Id, DocumentStatus.Pending);
        var entity = await SeedPreLoadingAsync(db, approved.Id, DocumentStatus.Pending, "INT-CTN-SRC", productId);
        var before = await ReloadAsync(db, entity.Id);

        var changed = await ReloadAsync(db, entity.Id);
        changed.BookingId = pending.Id;
        changed.ContainerNo = "INT-CTN-CHANGED";

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PreLoadingBookingLinkRules.ValidateLinkAsync(db, changed, userId, changed.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        var after = await ReloadAsync(db, entity.Id);
        Assert.Equal(before.BookingId, after.BookingId);
        Assert.Equal(before.ContainerNo, after.ContainerNo);
        Assert.Equal(before.SealNo, after.SealNo);
        Assert.Equal(before.Details.Single().Quantity, after.Details.Single().Quantity);
        Assert.Equal(DocumentStatus.Pending, after.Status);
    }

    // ==================== 订柜取消协同（真实 SQL） ====================

    [Fact]
    public async Task Live_booking_cancel_blocked_by_approved_preloading_and_released_by_explicit_cancel()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var booking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        var approved = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Approved, "INT-CTN-CXL", productId);

        var blocked = await Assert.ThrowsAsync<BusinessException>(async () => await PreLoadingBookingLinkRules
            .ValidateBookingCancellationAsync(db, await ReloadBookingAsync(db, booking.Id), userId));
        Assert.Equal(ErrorCodes.RuleConflict, blocked.Code);
        Assert.Equal(DocumentStatus.Approved, (await ReloadBookingAsync(db, booking.Id)).Status);
        Assert.Equal(DocumentStatus.Approved, (await ReloadAsync(db, approved.Id)).Status);

        // 显式取消流程解除：先取消预装柜单（既有 ERP-348 护栏），再取消订柜信息
        await ContainerLoadingFulfillmentRules.ValidateSourceCancellationAsync(
            db, await ReloadAsync(db, approved.Id), userId);
        var toCancel = await db.ContainerPreLoadings.SingleAsync(p => p.Id == approved.Id);
        toCancel.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        await PreLoadingBookingLinkRules.ValidateBookingCancellationAsync(
            db, await ReloadBookingAsync(db, booking.Id), userId);

        var cancelBooking = await db.ContainerBookings.SingleAsync(b => b.Id == booking.Id);
        cancelBooking.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        Assert.Equal(DocumentStatus.Cancelled, (await ReloadBookingAsync(db, booking.Id)).Status);
        Assert.Empty(await db.StockMovements.ToListAsync());
        Assert.Empty(await db.FinanceExpenses.ToListAsync());
    }

    [Fact]
    public async Task Live_booking_cancel_blocked_by_downstream_approved_loading_list()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedPrivilegedUserAsync(db);
        var customer = await SeedCustomerAsync(db);
        var productId = await SeedProductAsync(db, $"ITPL-P-{Tag()}");
        var booking = await SeedBookingAsync(db, customer.Id, DocumentStatus.Approved);
        // 历史数据不一致：预装柜单未审核，但下游装柜清单已审核 → 仍必须拒绝取消订柜
        var entity = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Submitted, "INT-CTN-DOWN", productId);
        db.ContainerLoadingLists.Add(new ContainerLoadingList
        {
            LoadingListNo = $"ITPL-LST-{Tag()}",
            PreLoadingId = entity.Id,
            LoadingDate = DateTime.Today,
            CustomerId = customer.Id,
            Status = DocumentStatus.Approved
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await PreLoadingBookingLinkRules
            .ValidateBookingCancellationAsync(db, await ReloadBookingAsync(db, booking.Id), userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("装柜清单", ex.Message);
        Assert.Equal(DocumentStatus.Approved, (await ReloadBookingAsync(db, booking.Id)).Status);
    }

    // ==================== 真实两连接竞争：订柜取消 vs 预装柜审核 ====================

    [Fact]
    public async Task Two_connection_booking_cancel_versus_preloading_approve_yields_exactly_one_winner()
    {
        Guard();
        long bookingId;
        long preLoadingId;
        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            userId = await SeedPrivilegedUserAsync(seed);
            var customer = await SeedCustomerAsync(seed);
            var productId = await SeedProductAsync(seed, $"ITPL-P-{Tag()}");
            var booking = await SeedBookingAsync(seed, customer.Id, DocumentStatus.Approved);
            var entity = await SeedPreLoadingAsync(seed, booking.Id, DocumentStatus.Submitted, "INT-CTN-RACE", productId);
            bookingId = booking.Id;
            preLoadingId = entity.Id;
        }

        var results = await Task.WhenAll(
            TryApproveWithBookingLockAsync(preLoadingId, bookingId, userId),
            TryCancelBookingWithLockAsync(bookingId, userId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var bookingStatus = (await ReloadBookingAsync(verify, bookingId)).Status;
        var preLoadingStatus = (await ReloadAsync(verify, preLoadingId)).Status;

        if (bookingStatus == DocumentStatus.Cancelled)
            Assert.Equal(DocumentStatus.Submitted, preLoadingStatus);   // 订柜先取消 ⇒ 审核被拒
        else
        {
            Assert.Equal(DocumentStatus.Approved, bookingStatus);       // 审核先提交 ⇒ 取消被拒
            Assert.Equal(DocumentStatus.Approved, preLoadingStatus);
        }
    }

    /// <summary>模拟 <c>ContainerPreLoadingController.Approve</c>：订柜行锁 + 可串行化事务 + 锁内复核。</summary>
    private async Task<(bool Success, string Error)> TryApproveWithBookingLockAsync(
        long preLoadingId, long bookingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                bookingId).ToListAsync();

            var entity = await db.ContainerPreLoadings.Include(o => o.Details).SingleAsync(p => p.Id == preLoadingId);
            if (entity.Status != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

            await PreLoadingBookingLinkRules.ValidateApprovalAsync(db, entity, userId);

            entity.Status = DocumentStatus.Approved;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            await TryRollbackAsync(transaction);
            return (false, ex.Message);
        }
    }

    /// <summary>模拟 <c>ContainerBookingController.Cancel</c>：订柜行锁 + 可串行化事务 + 锁内复核。</summary>
    private async Task<(bool Success, string Error)> TryCancelBookingWithLockAsync(long bookingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await db.Database.SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                bookingId).ToListAsync();

            var booking = await db.ContainerBookings.SingleAsync(b => b.Id == bookingId);
            await PreLoadingBookingLinkRules.ValidateBookingCancellationAsync(db, booking, userId);

            booking.Status = DocumentStatus.Cancelled;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            await TryRollbackAsync(transaction);
            return (false, ex.Message);
        }
    }

    /// <summary>回滚并吞掉回滚自身的异常（竞争下可能是序列化失败 / 死锁，连接状态不可靠）。</summary>
    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception)
        {
            // 回滚失败无需上抛：事务已被 SQL Server 中止，连接随之释放。
        }
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-353）：把目标库重置为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class PreLoadingBookingLinkSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PRELOADINGBOOKINGLINK_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-353] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task ResetToFullDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

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

        Console.WriteLine("[ERP-353] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PreLoadingBookingLinkTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PreLoadingBookingLinkSqlServerFixture.AssertDedicatedTarget(connection));
}
