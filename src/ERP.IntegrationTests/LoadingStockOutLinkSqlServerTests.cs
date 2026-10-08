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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-366 装柜明细 → 显式已审核销售出库证据链接 与 累计容量护栏的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器</b>：候选证据有界接口、链接指派接口、提交 / 审核全链路走
/// <see cref="ContainerLoadingListController"/>（既有「装柜清单」+「销售出库」权限与实时客户范围）；</item>
/// <item><b>来源语义</b>：来源撤销（出库单取消）、商品 / 客户不匹配、来源明细删除一律 fail closed；</item>
/// <item><b>容量与原子性</b>：重复行 / 重复来源明细按「出库单 + 商品」保守聚合，溢出拒绝且失败后
/// 明细 / 状态 / 历史零改动；审核**不**二次过账库存、**不**改财务；</item>
/// <item><b>两条独立连接竞争</b>：同源两条已链接装柜竞争容量只放行合法总量；同一单据双连接并发审核只成功一次
/// （确定性锁序：上游销售出库行升序 → 装柜清单行 <c>UPDLOCK, HOLDLOCK</c>）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class LoadingStockOutLinkSqlServerTests
    : IClassFixture<LoadingStockOutLinkSqlServerFixture>
{
    private readonly LoadingStockOutLinkSqlServerFixture _fixture;

    public LoadingStockOutLinkSqlServerTests(LoadingStockOutLinkSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(LoadingStockOutLinkSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"LSL-C-{Guid.NewGuid():N}"[..30], CustomerName = name, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit)
    {
        var product = new BaseProduct
        {
            ProductCode = $"LSL-P-{Guid.NewGuid():N}"[..30], ProductName = name, Spec = "规格A", Unit = unit
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<StockOut> SeedStockOutAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no.Length > 50 ? no[..50] : no, StockOutDate = DateTime.Today,
            CustomerId = customerId, Status = status, Remark = "ERP-366_INT"
        };
        db.StockOuts.Add(stockOut);
        await db.SaveChangesAsync();
        return stockOut;
    }

    private static async Task<StockOutDetail> AddStockOutDetailAsync(ErpDbContext db, long stockOutId,
        long productId, decimal quantity, string unit = "PCS")
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId, ProductId = productId, ProductName = $"商品{productId}",
            Unit = unit, Quantity = quantity
        };
        db.StockOutDetails.Add(detail);
        await db.SaveChangesAsync();
        return detail;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(ErpDbContext db, string no,
        long customerId, DocumentStatus status,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no.Length > 50 ? no[..50] : no, LoadingDate = DateTime.Today,
            CustomerId = customerId, Status = status, Remark = "ERP-366_INT"
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceStockOutDetailId = sourceDetailId
            });
        }
        await db.SaveChangesAsync();
        return list;
    }

    // ==================== 真实控制器脚手架 ====================

    private static ContainerLoadingListController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
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

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task<ContainerLoadingList> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking().SingleAsync(l => l.Id == id);

    private static async Task<List<long>> DetailIdsAsync(ErpDbContext db, long loadingListId)
        => await db.ContainerLoadingDetails.AsNoTracking()
            .Where(d => d.LoadingListId == loadingListId && !d.IsDeleted)
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync();

    /// <summary>两个独立连接 / DbContext / 事务在同一护栏后同时发起操作（各自独立连接）。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    private async Task<(bool Success, string Error)> TryAsync(
        Func<ContainerLoadingListController, Task<IActionResult>> action, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 断言脚手架 ====================

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


    // ==================== 1. 幂等结构升级（真实列 + 过滤索引） ====================

    [Fact]
    public async Task Live_schema_upgrade_provides_source_detail_column_and_index()
    {
        Guard();
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('db_owner.ContainerLoadingDetails') AND name = 'SourceStockOutDetailId'"));
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ContainerLoadingDetails_SourceStockOutDetailId' AND object_id = OBJECT_ID('db_owner.ContainerLoadingDetails')"));

        // 幂等：既有库重复执行结构升级不报错、不改变列与索引。
        await using var db = _fixture.CreateDbContext();
        await SchemaUpgrader.EnsureUpgradedAsync(db);
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('db_owner.ContainerLoadingDetails') AND name = 'SourceStockOutDetailId'"));
    }

    // ==================== 2. 候选证据（真实有界接口） ====================

    [Fact]
    public async Task Live_candidates_return_bounded_evidence()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "候选客户");
        var productId = await SeedProductAsync(db, "候选商品", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var detail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 7m);
        var pending = await SeedStockOutAsync(db, $"CK-{keyword}-P", customerId, DocumentStatus.Pending);
        await AddStockOutDetailAsync(db, pending.Id, productId, 7m);
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}", customerId, DocumentStatus.Pending);

        var candidates = AssertOk<List<LoadingStockOutCandidateDto>>(
            await NewController(db, adminId).GetStockOutCandidates(list.Id, keyword, 0));

        var candidate = Assert.Single(candidates);
        Assert.Equal(detail.Id, candidate.StockOutDetailId);
        Assert.Equal(stockOut.Id, candidate.StockOutId);
        Assert.Equal(productId, candidate.ProductId);
        Assert.Equal(customerId, candidate.CustomerId);
        Assert.Equal(7m, candidate.SourceBaseQuantity);
        Assert.Equal(7m, candidate.RemainingBaseQuantity);
    }

    // ==================== 3. 指派 → 提交 → 审核 全链路（真实控制器） ====================

    [Fact]
    public async Task Live_assign_submit_approve_persists_evidence_without_stock_posting()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "链接客户");
        var productId = await SeedProductAsync(db, "链接商品", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}", customerId, DocumentStatus.Pending,
            (productId, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        var ctl = NewController(db, adminId);
        var assign = AssertOk<LoadingStockOutLinkAssignResultDto>(await ctl.AssignStockOutLinks(list.Id,
            new LoadingStockOutLinkAssignRequest
            {
                Links = new List<LoadingStockOutLinkAssignmentDto>
                {
                    new() { LoadingDetailId = detailId, SourceStockOutDetailId = sourceDetail.Id }
                }
            }));
        Assert.Equal(1, assign.LinkedCount);
        Assert.Equal(sourceDetail.Id, assign.Items.Single().SourceStockOutDetailId);
        Assert.Equal(stockOut.Id, assign.Items.Single().StockOutId);

        Assert.IsType<OkObjectResult>(await ctl.Submit(list.Id));

        await using (var approveDb = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await NewController(approveDb, adminId).Approve(list.Id));
        }

        var stored = await ReloadAsync(db, list.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceStockOutDetailId);

        // 审核不得二次过账库存 / 财务：不产生来源为该出库单的库存流水，且来源出库单保持已审核。
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.StockMovements.AsNoTracking()
            .CountAsync(m => m.SourceDocId == stockOut.Id));
        Assert.Equal(DocumentStatus.Approved,
            (await verify.StockOuts.AsNoTracking().SingleAsync(s => s.Id == stockOut.Id)).Status);
    }


    // ==================== 4. 来源语义 fail closed（商品 / 客户 / 来源撤销） ====================

    [Fact]
    public async Task Live_assign_wrong_product_fails_closed_and_preserves()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "错商品客户");
        var productA = await SeedProductAsync(db, "商品A", "PCS");
        var productB = await SeedProductAsync(db, "商品B", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var wrongDetail = await AddStockOutDetailAsync(db, stockOut.Id, productB, 10m);
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}", customerId, DocumentStatus.Pending,
            (productA, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.AssignStockOutLinks(list.Id,
            new LoadingStockOutLinkAssignRequest
            {
                Links = new List<LoadingStockOutLinkAssignmentDto>
                {
                    new() { LoadingDetailId = detailId, SourceStockOutDetailId = wrongDetail.Id }
                }
            }));

        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task Live_assign_wrong_customer_fails_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var ownCustomer = await SeedCustomerAsync(db, "自有客户");
        var otherCustomer = await SeedCustomerAsync(db, "他人客户");
        var productId = await SeedProductAsync(db, "客户商品", "PCS");
        var keyword = Tag();
        var foreignStockOut = await SeedStockOutAsync(db, $"CK-{keyword}-OTHER", otherCustomer, DocumentStatus.Approved);
        var foreignDetail = await AddStockOutDetailAsync(db, foreignStockOut.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}", ownCustomer, DocumentStatus.Pending,
            (productId, 6m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.AssignStockOutLinks(list.Id,
            new LoadingStockOutLinkAssignRequest
            {
                Links = new List<LoadingStockOutLinkAssignmentDto>
                {
                    new() { LoadingDetailId = detailId, SourceStockOutDetailId = foreignDetail.Id }
                }
            }));

        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task Live_approve_after_source_revoked_fails_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "来源撤销客户");
        var productId = await SeedProductAsync(db, "撤销商品", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}", customerId, DocumentStatus.Submitted,
            (productId, 6m, sourceDetail.Id));
        var ctl = NewController(db, adminId);

        // 来源撤销：出库单在装柜审核之前被取消（真实来源单据状态）。
        stockOut.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Approve(list.Id));
        var stored = await ReloadAsync(db, list.Id);
        Assert.Equal(DocumentStatus.Submitted, stored.Status);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceStockOutDetailId);
    }


    // ==================== 5. 容量聚合 / 溢出 / 原子失败 ====================

    [Fact]
    public async Task Live_approve_duplicate_lines_aggregate_then_overflow_fails_atomically()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "重复行客户");
        var productId = await SeedProductAsync(db, "重复行商品", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var firstDetail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 5m);
        var secondDetail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 5m);

        // 同一单据两条同商品行（4 + 4 = 8 ≤ 10，分别链接两条来源明细）先聚合再审核 → 通过。
        var exact = await SeedLoadingListAsync(db, $"ZQ-{keyword}-OK", customerId, DocumentStatus.Submitted,
            (productId, 4m, firstDetail.Id), (productId, 4m, secondDetail.Id));
        Assert.IsType<OkObjectResult>(await NewController(db, adminId).Approve(exact.Id));
        Assert.Equal(DocumentStatus.Approved, (await ReloadAsync(db, exact.Id)).Status);

        // 再装 3 → 累计 11 > 10：拒绝且明细 / 状态 / 历史保持不变（原子失败，无半成品写入）。
        var overflow = await SeedLoadingListAsync(db, $"ZQ-{keyword}-OVER", customerId, DocumentStatus.Submitted,
            (productId, 3m, firstDetail.Id));
        var overflowDetailCount = (await DetailIdsAsync(db, overflow.Id)).Count;

        await using (var overflowDb = _fixture.CreateDbContext())
        {
            await AssertCode(ErrorCodes.RuleConflict,
                () => NewController(overflowDb, adminId).Approve(overflow.Id));
        }

        var stored = await ReloadAsync(db, overflow.Id);
        Assert.Equal(DocumentStatus.Submitted, stored.Status);
        Assert.Equal(3m, stored.Details.Single().Quantity);
        Assert.Equal(firstDetail.Id, stored.Details.Single().SourceStockOutDetailId);
        Assert.Equal(overflowDetailCount, (await DetailIdsAsync(db, overflow.Id)).Count);
    }

    // ==================== 6. 两条独立连接竞争 ====================

    [Fact]
    public async Task Race_two_linked_loadings_competing_for_capacity_admit_only_valid_total()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "竞争容量客户");
        var productId = await SeedProductAsync(db, "竞争容量商品", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var first = await SeedLoadingListAsync(db, $"ZQ-{keyword}-A", customerId, DocumentStatus.Submitted,
            (productId, 6m, sourceDetail.Id));
        var second = await SeedLoadingListAsync(db, $"ZQ-{keyword}-B", customerId, DocumentStatus.Submitted,
            (productId, 5m, sourceDetail.Id));

        // 6 + 5 = 11 > 10：两条独立连接同时审核，最多只允许一条通过。
        var results = await RaceAsync(
            () => TryAsync(ctl => ctl.Approve(first.Id), adminId),
            () => TryAsync(ctl => ctl.Approve(second.Id), adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var approved = await verify.ContainerLoadingLists.AsNoTracking()
            .CountAsync(l => (l.Id == first.Id || l.Id == second.Id) && l.Status == DocumentStatus.Approved);
        Assert.Equal(1, approved);
        var approvedQuantity = await (from l in verify.ContainerLoadingLists.AsNoTracking()
                                      join d in verify.ContainerLoadingDetails.AsNoTracking() on l.Id equals d.LoadingListId
                                      where (l.Id == first.Id || l.Id == second.Id) && l.Status == DocumentStatus.Approved
                                      select d.Quantity).SumAsync();
        Assert.True(approvedQuantity <= 10m, $"已审核链接数量 {approvedQuantity} 超过来源容量 10");
    }

    [Fact]
    public async Task Race_concurrent_approval_of_same_list_yields_single_approval()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "竞争同单客户");
        var productId = await SeedProductAsync(db, "竞争同单商品", "PCS");
        var keyword = Tag();
        var stockOut = await SeedStockOutAsync(db, $"CK-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}", customerId, DocumentStatus.Submitted,
            (productId, 6m, sourceDetail.Id));

        var results = await RaceAsync(
            () => TryAsync(ctl => ctl.Approve(list.Id), adminId),
            () => TryAsync(ctl => ctl.Approve(list.Id), adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerLoadingLists.AsNoTracking().SingleAsync(l => l.Id == list.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(1, await verify.ContainerLoadingDetails.AsNoTracking()
            .CountAsync(d => d.LoadingListId == list.Id && d.SourceStockOutDetailId == sourceDetail.Id));
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-366）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class LoadingStockOutLinkSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_LOADINGSTOCKOUTLINK_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-366] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
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

        Console.WriteLine("[ERP-366] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class LoadingStockOutLinkTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => LoadingStockOutLinkSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => LoadingStockOutLinkSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
