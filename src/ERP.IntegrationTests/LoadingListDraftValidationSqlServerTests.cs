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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-506 装柜清单草稿写入护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// 用与运行时完全相同的调用路径（真实 <see cref="ContainerLoadingListController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建（手工未链接与显式出运证据链接同口径）对「数量非正或不可表示 / 溢出、箱数 · 毛重 · 体积为负或不可表示、
/// 商品 Id 缺失 / 不存在 / 已删除 / 已停用」一律拒绝，且零单据、零明细、零单号消耗；</item>
/// <item>客户端 <c>IsDeleted=true</c> 不能隐藏坏行（服务端会把每一行恢复为正常行）与表头合计溢出同样被拒绝；</item>
/// <item>修改在明细整体替换之前收敛同样的非法输入，既有单据与明细逐字段不变、不消耗新单号；仅主表更新保留既有明细与证据链接；</item>
/// <item>空草稿头可保存但提交 / 审核要求有效明细；重复商品行按累计数量语义保留；合法链接草稿仍可提交并审核；</item>
/// <item>保存后商品失效 / 已存数值被改写时，提交与审核在锁内实时复核并拒绝，状态不变；</item>
/// <item>历史取消路径不新增闸门（商品失效仍可取消，且不清除证据链接）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，
/// 每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class LoadingListDraftValidationSqlServerTests : IClassFixture<LoadingListDraftValidationSqlServerFixture>
{
    private readonly LoadingListDraftValidationSqlServerFixture _fixture;

    public LoadingListDraftValidationSqlServerTests(LoadingListDraftValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(LoadingListDraftValidationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static ContainerLoadingListController ControllerFor(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
                }
            }
        };

    private static async Task<long> SeedOperatorAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task<long> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"LLDV-{Tag()}", ProductName = "草稿校验商品", Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"LLDV-C-{Tag()}", CustomerName = "草稿校验客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<StockOutDetail> SeedApprovedStockOutDetailAsync(
        ErpDbContext db, long customerId, long productId, decimal capacity)
    {
        var stockOut = new StockOut
        {
            StockOutNo = $"LLDV-XSCK-{Tag()}", StockOutDate = DateTime.Today, CustomerId = customerId,
            Status = DocumentStatus.Approved, Remark = "ERP-506_INT"
        };
        db.StockOuts.Add(stockOut);
        await db.SaveChangesAsync();

        var detail = new StockOutDetail
        {
            StockOutId = stockOut.Id, ProductId = productId, ProductName = "草稿校验商品",
            Unit = "PCS", Quantity = capacity
        };
        db.StockOutDetails.Add(detail);
        await db.SaveChangesAsync();
        return detail;
    }

    private static async Task SetProductFlagsAsync(ErpDbContext db, long productId, string scenario)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        if (scenario == "deleted-product") product.IsDeleted = true;
        if (scenario == "disabled-product") product.Status = 0;
        await db.SaveChangesAsync();
    }

    private static ContainerLoadingList Draft(long customerId, params ContainerLoadingDetail[] lines)
        => new()
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerNo = $"CTN-{Tag()}",
            Remark = "ERP-506_INT",
            Details = lines.ToList()
        };

    /// <summary>仅更新主表：显式提交空明细集合（保留既有明细与证据链接）。</summary>
    private static ContainerLoadingList HeaderOnlyDraft(long customerId)
        => new()
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerNo = $"CTN-{Tag()}",
            Remark = "ERP-506_INT_HEADER_ONLY",
            Details = new List<ContainerLoadingDetail>()
        };

    private static ContainerLoadingDetail Line(long productId, decimal quantity, decimal cartons = 0m,
        decimal weight = 0m, decimal volume = 0m, bool isDeleted = false, long? sourceStockOutDetailId = null)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Quantity = quantity,
            Cartons = cartons,
            Weight = weight,
            Volume = volume,
            IsDeleted = isDeleted,
            SourceStockOutDetailId = sourceStockOutDetailId
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static async Task<ContainerLoadingList> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking().SingleAsync(p => p.Id == id);

    private sealed record Snapshot(long Documents, long Details, long PreLoadings, long Movements, long Expenses,
        long? Sequence);

    private static async Task<Snapshot> SnapshotAsync(ErpDbContext db)
        => new(await db.ContainerLoadingLists.CountAsync(), await db.ContainerLoadingDetails.CountAsync(),
            await db.ContainerPreLoadings.CountAsync(), await db.StockMovements.CountAsync(),
            await db.FinanceExpenses.CountAsync(), await NumberSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存流水、零财务、零单号消耗。</summary>
    private static async Task AssertNoWriteAsync(ErpDbContext db, Snapshot before)
    {
        Assert.Equal(before.Documents, await db.ContainerLoadingLists.CountAsync());
        Assert.Equal(before.Details, await db.ContainerLoadingDetails.CountAsync());
        Assert.Equal(before.PreLoadings, await db.ContainerPreLoadings.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.Expenses, await db.FinanceExpenses.CountAsync());
        Assert.Equal(before.Sequence, await NumberSequenceAsync(db));
    }

    private static async Task<long?> NumberSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.LoadingList)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    private static async Task<long> SeedLoadingListAsync(ErpDbContext db, long customerId, DocumentStatus status,
        params ContainerLoadingDetail[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = $"LLDV-L-{Tag()}",
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerNo = $"CTN-SEED-{Tag()}",
            Status = status,
            Remark = "ERP-506_INT_SEED"
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();

        foreach (var line in lines)
        {
            line.LoadingListId = list.Id;
            db.ContainerLoadingDetails.Add(line);
        }
        await db.SaveChangesAsync();
        return list.Id;
    }

    // ==================== 1. 创建：非法明细（手工未链接与显式出运证据链接同口径） ====================

    [Theory]
    [InlineData("missing-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-cartons", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cartons", ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-weight", ErrorCodes.InvalidParameter)]
    [InlineData("negative-volume", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", ErrorCodes.InvalidParameter)]
    public async Task Create_rejects_invalid_unlinked_lines_before_any_write(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        await SetProductFlagsAsync(db, productId, scenario);

        var line = scenario switch
        {
            "missing-id" => Line(0L, 1m),
            "missing-product" => Line(999_999_999L, 1m),
            "zero-quantity" => Line(productId, 0m),
            "negative-quantity" => Line(productId, -1m),
            "unrepresentable-quantity" => Line(productId, 1.005m),
            "overflow-quantity" => Line(productId, 10_000_000_000_000_000m),
            "negative-cartons" => Line(productId, 1m, cartons: -1m),
            "unrepresentable-cartons" => Line(productId, 1m, cartons: 0.005m),
            "negative-weight" => Line(productId, 1m, weight: -1m),
            "unrepresentable-weight" => Line(productId, 1m, weight: 0.005m),
            "negative-volume" => Line(productId, 1m, volume: -1m),
            "unrepresentable-volume" => Line(productId, 1m, volume: 0.005m),
            _ => Line(productId, 1m)
        };

        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Create(Draft(customer.Id, line)));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);
    }

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-cartons", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.RuleConflict)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_linked_lines_before_any_write(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        var source = await SeedApprovedStockOutDetailAsync(db, customer.Id, productId, capacity: 100m);
        await SetProductFlagsAsync(db, productId, scenario);

        var line = scenario switch
        {
            "zero-quantity" => Line(productId, 0m, sourceStockOutDetailId: source.Id),
            "negative-quantity" => Line(productId, -1m, sourceStockOutDetailId: source.Id),
            "unrepresentable-quantity" => Line(productId, 1.005m, sourceStockOutDetailId: source.Id),
            "overflow-quantity" => Line(productId, 10_000_000_000_000_000m, sourceStockOutDetailId: source.Id),
            "negative-cartons" => Line(productId, 1m, cartons: -1m, sourceStockOutDetailId: source.Id),
            "unrepresentable-volume" => Line(productId, 1m, volume: 0.005m, sourceStockOutDetailId: source.Id),
            _ => Line(productId, 5m, sourceStockOutDetailId: source.Id)
        };

        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Create(Draft(customer.Id, line)));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);
    }

    [Fact]
    public async Task Create_rejects_overflowing_header_total_before_any_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);

        var before = await SnapshotAsync(db);
        // 每行箱数各自可表示，但两行合计 1e16 超出 DECIMAL(18,2)。
        var error = await Assert.ThrowsAsync<BusinessException>(() => ControllerFor(db, userId).Create(Draft(customer.Id,
            Line(productId, 1m, cartons: 5_000_000_000_000_000m),
            Line(productId, 1m, cartons: 5_000_000_000_000_000m))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出存储精度", error.Message);
        await AssertNoWriteAsync(db, before);
    }

    [Fact]
    public async Task Create_rejects_hidden_bad_row_from_client_deleted_flag()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);

        var before = await SnapshotAsync(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() => ControllerFor(db, userId).Create(Draft(customer.Id,
            Line(productId, 1m), Line(productId, 0m, isDeleted: true))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        await AssertNoWriteAsync(db, before);
    }

    // ==================== 2. 受支持的空草稿头、重复商品行与合法链接承诺 ====================

    [Fact]
    public async Task Empty_draft_is_supported_while_repeated_product_rows_accumulate()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);

        var empty = await ControllerFor(db, userId).Create(Draft(customer.Id));
        var emptyId = CreatedId(empty);
        var savedEmpty = await ReloadAsync(db, emptyId);
        Assert.Empty(savedEmpty.Details);
        Assert.Equal(0m, savedEmpty.TotalCartons);
        Assert.Equal(DocumentStatus.Pending, savedEmpty.Status);

        var repeated = await ControllerFor(db, userId).Create(Draft(customer.Id,
            Line(productId, 2m, cartons: 1m, weight: 3m),
            Line(productId, 5m, cartons: 4m, weight: 7m)));
        var repeatedId = CreatedId(repeated);
        var saved = await ReloadAsync(db, repeatedId);
        Assert.Equal(2, saved.Details.Count);
        Assert.Equal(5m, saved.TotalCartons);
        Assert.Equal(10m, saved.TotalWeight);
    }

    [Fact]
    public async Task Valid_linked_draft_can_be_submitted_and_approved_without_side_effects()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        var source = await SeedApprovedStockOutDetailAsync(db, customer.Id, productId, capacity: 100m);

        var created = await ControllerFor(db, userId).Create(Draft(customer.Id,
            Line(productId, 5m, cartons: 1m, weight: 20m, volume: 1m, sourceStockOutDetailId: source.Id)));
        var id = CreatedId(created);

        var saved = await ReloadAsync(db, id);
        Assert.Equal(source.Id, saved.Details.Single().SourceStockOutDetailId);

        Assert.IsType<OkObjectResult>(await ControllerFor(db, userId).Submit(id));
        Assert.Equal(DocumentStatus.Submitted, (await ReloadAsync(db, id)).Status);

        Assert.IsType<OkObjectResult>(await ControllerFor(db, userId).Approve(id));
        Assert.Equal(DocumentStatus.Approved, (await ReloadAsync(db, id)).Status);

        Assert.Equal(0, await db.StockMovements.CountAsync());
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
        Assert.Equal(0, await db.ContainerPreLoadings.CountAsync());
    }

    // ==================== 3. 修改 / 提交 / 审核边界 ====================

    [Fact]
    public async Task Header_only_update_retains_existing_rows_and_explicit_source_link()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        var source = await SeedApprovedStockOutDetailAsync(db, customer.Id, productId, capacity: 100m);

        var created = await ControllerFor(db, userId).Create(Draft(customer.Id,
            Line(productId, 5m, cartons: 1m, sourceStockOutDetailId: source.Id)));
        var id = CreatedId(created);
        var detailId = (await ReloadAsync(db, id)).Details.Single().Id;

        Assert.IsType<OkObjectResult>(await ControllerFor(db, userId).Update(id, HeaderOnlyDraft(customer.Id)));

        var saved = await ReloadAsync(db, id);
        var detail = Assert.Single(saved.Details);
        Assert.Equal(detailId, detail.Id);
        Assert.Equal(source.Id, detail.SourceStockOutDetailId);
        Assert.Equal(5m, detail.Quantity);
        Assert.Equal(1m, saved.TotalCartons);
    }

    [Fact]
    public async Task Update_rejects_invalid_rows_before_replacing_details()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);

        var created = await ControllerFor(db, userId).Create(Draft(customer.Id, Line(productId, 4m, cartons: 1m)));
        var id = CreatedId(created);
        var before = await ReloadAsync(db, id);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Update(id, Draft(customer.Id, Line(productId, 0m))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        var after = await ReloadAsync(db, id);
        Assert.Equal(before.LoadingListNo, after.LoadingListNo);
        Assert.Equal(before.TotalCartons, after.TotalCartons);
        Assert.Equal(before.Details.Single().Quantity, after.Details.Single().Quantity);
    }

    [Fact]
    public async Task Submit_and_approve_reject_empty_draft_without_status_change()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var customer = await SeedCustomerAsync(db);
        var emptyId = await SeedLoadingListAsync(db, customer.Id, DocumentStatus.Pending);
        var submittedId = await SeedLoadingListAsync(db, customer.Id, DocumentStatus.Submitted);

        var submitError = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Submit(emptyId));
        Assert.Equal(ErrorCodes.RuleConflict, submitError.Code);
        Assert.Equal(DocumentStatus.Pending, (await ReloadAsync(db, emptyId)).Status);

        var approveError = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Approve(submittedId));
        Assert.Equal(ErrorCodes.RuleConflict, approveError.Code);
        Assert.Equal(DocumentStatus.Submitted, (await ReloadAsync(db, submittedId)).Status);
    }

    [Fact]
    public async Task Submit_rejects_when_live_master_invalidated_after_save()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        var pendingId = await SeedLoadingListAsync(db, customer.Id, DocumentStatus.Pending,
            Line(productId, 4m, cartons: 1m));

        await SetProductFlagsAsync(db, productId, "deleted-product");

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Submit(pendingId));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Equal(DocumentStatus.Pending, (await ReloadAsync(db, pendingId)).Status);
    }

    [Fact]
    public async Task Approve_rejects_when_stored_quantity_was_rewritten_invalid()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        var submittedId = await SeedLoadingListAsync(db, customer.Id, DocumentStatus.Submitted,
            Line(productId, 4m, cartons: 1m));

        // 模拟历史 / 被并发改写的非法已存数值（绕过控制器直接写入数据库）。
        var detail = await db.ContainerLoadingDetails.SingleAsync(d => d.LoadingListId == submittedId);
        detail.Quantity = 0m;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerFor(db, userId).Approve(submittedId));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        var after = await ReloadAsync(db, submittedId);
        Assert.Equal(DocumentStatus.Submitted, after.Status);
        Assert.Equal(0m, after.Details.Single().Quantity);
    }

    // ==================== 4. 历史取消路径不新增校验闸门 ====================

    [Fact]
    public async Task Cancel_of_approved_document_with_invalidated_product_still_works()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedOperatorAsync(db);
        var productId = await SeedProductAsync(db);
        var customer = await SeedCustomerAsync(db);
        var source = await SeedApprovedStockOutDetailAsync(db, customer.Id, productId, capacity: 100m);
        var approvedId = await SeedLoadingListAsync(db, customer.Id, DocumentStatus.Approved,
            Line(productId, 5m, cartons: 1m, sourceStockOutDetailId: source.Id));

        await SetProductFlagsAsync(db, productId, "deleted-product");

        Assert.IsType<OkObjectResult>(await ControllerFor(db, userId).Cancel(approvedId));

        var after = await ReloadAsync(db, approvedId);
        Assert.Equal(DocumentStatus.Cancelled, after.Status);
        Assert.Equal(source.Id, after.Details.Single().SourceStockOutDetailId);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-506）：全新 GUID 后缀库 → 完整 NEWERP 结构 + 种子数据。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 也绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class LoadingListDraftValidationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_LOADINGLISTDRAFT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-506] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
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

    private async Task ResetToFullDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退，也绝不复用已存在的库。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
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

        Console.WriteLine("[ERP-506] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class LoadingListDraftValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            LoadingListDraftValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
