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
/// ERP-368 预装柜明细 → 显式已审核销售订单需求计划证据链接 与 累计容量护栏的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器</b>：候选证据有界接口、链接指派接口、提交 / 审核全链路走
/// <see cref="ContainerPreLoadingController"/>（既有「预装柜单」+「销售订单」权限与实时客户范围）；</item>
/// <item><b>来源语义</b>：未审核 / 已取消订单、商品 / 客户不匹配、来源明细删除一律 fail closed；</item>
/// <item><b>容量与原子性</b>：重复行按「来源销售订单明细」逐条聚合，溢出拒绝且失败后明细 / 状态 / 历史零改动；
/// 审核**不**锁库、**不**过账库存、**不**改财务、**不**生成出运单；</item>
/// <item><b>两条独立连接竞争</b>：同源两条已链接预装柜竞争容量只放行合法总量；同一单据双连接并发审核只成功一次
/// （确定性锁序：上游销售订单行升序 → 订柜信息行 → 预装柜单行 <c>UPDLOCK, HOLDLOCK</c>）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PreLoadingSalesOrderLinkSqlServerTests
    : IClassFixture<PreLoadingSalesOrderLinkSqlServerFixture>
{
    private readonly PreLoadingSalesOrderLinkSqlServerFixture _fixture;

    public PreLoadingSalesOrderLinkSqlServerTests(PreLoadingSalesOrderLinkSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PreLoadingSalesOrderLinkSqlServerFixture.DatabasePrefix, target.InitialCatalog,
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
            CustomerCode = $"LSP-C-{Guid.NewGuid():N}"[..30], CustomerName = name, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit)
    {
        var product = new BaseProduct
        {
            ProductCode = $"LSP-P-{Guid.NewGuid():N}"[..30], ProductName = name, Spec = "规格A", Unit = unit
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<ContainerBooking> SeedBookingAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no.Length > 50 ? no[..50] : no, BookingDate = DateTime.Today,
            CustomerId = customerId, Status = status, Remark = "ERP-368_INT"
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status)
    {
        var order = new SalesOrder
        {
            OrderNo = no.Length > 50 ? no[..50] : no, OrderDate = DateTime.Today,
            CustomerId = customerId, Status = status, Remark = "ERP-368_INT"
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<SalesOrderDetail> AddOrderDetailAsync(ErpDbContext db, long salesOrderId,
        long productId, decimal quantity, string unit = "PCS")
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId, ProductId = productId, ProductName = $"商品{productId}",
            Unit = unit, Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m
        };
        db.SalesOrderDetails.Add(detail);
        await db.SaveChangesAsync();
        return detail;
    }

    private static async Task<ContainerPreLoading> SeedPreLoadingAsync(ErpDbContext db, string no,
        long? bookingId, DocumentStatus status,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no.Length > 50 ? no[..50] : no, LoadingDate = DateTime.Today,
            BookingId = bookingId, Status = status, Remark = "ERP-368_INT"
        };
        db.ContainerPreLoadings.Add(pre);
        await db.SaveChangesAsync();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceSalesOrderDetailId = sourceDetailId
            });
        }
        await db.SaveChangesAsync();
        return pre;
    }

    // ==================== 真实控制器脚手架 ====================

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
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

    private static async Task<ContainerPreLoading> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<List<long>> DetailIdsAsync(ErpDbContext db, long preLoadingId)
        => await db.ContainerPreLoadingDetails.AsNoTracking()
            .Where(d => d.PreLoadingId == preLoadingId && !d.IsDeleted)
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

    private async Task<(bool Success, string Error)> TryApproveAsync(long preLoadingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewController(db, userId).Approve(preLoadingId);
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
    public async Task Live_schema_upgrade_provides_source_sales_order_detail_column_and_index()
    {
        Guard();
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('db_owner.ContainerPreLoadingDetails') AND name = 'SourceSalesOrderDetailId'"));
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_ContainerPreLoadingDetails_SourceSalesOrderDetailId' AND object_id = OBJECT_ID('db_owner.ContainerPreLoadingDetails')"));

        // 幂等：既有库重复执行结构升级不报错、不改变列与索引。
        await using var db = _fixture.CreateDbContext();
        await SchemaUpgrader.EnsureUpgradedAsync(db);
        Assert.Equal(1, await ScalarAsync(
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('db_owner.ContainerPreLoadingDetails') AND name = 'SourceSalesOrderDetailId'"));

        // 本任务之外的历史明细保持 NULL（显式未链接），绝不回填：
        // 任何带链接的明细，其父预装柜单都必须是本任务测试创建的（Remark = ERP-368_INT）。
        Assert.Equal(0, await db.ContainerPreLoadingDetails.AsNoTracking()
            .CountAsync(d => d.SourceSalesOrderDetailId != null
                && !db.ContainerPreLoadings.Any(p => p.Id == d.PreLoadingId && p.Remark == "ERP-368_INT")));
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
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var detail = await AddOrderDetailAsync(db, order.Id, productId, 7m);
        var pending = await SeedSalesOrderAsync(db, $"SO-{keyword}-P", customerId, DocumentStatus.Pending);
        await AddOrderDetailAsync(db, pending.Id, productId, 7m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Pending);

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, adminId).GetSalesOrderCandidates(pre.Id, keyword, 0));

        var candidate = Assert.Single(candidates);
        Assert.Equal(detail.Id, candidate.SalesOrderDetailId);
        Assert.Equal(order.Id, candidate.SalesOrderId);
        Assert.Equal(productId, candidate.ProductId);
        Assert.Equal(customerId, candidate.CustomerId);
        Assert.Equal(7m, candidate.OrderBaseQuantity);
        Assert.Equal(7m, candidate.RemainingBaseQuantity);
    }

    // ==================== 3. 指派 → 提交 → 审核 全链路（真实控制器） ====================

    [Fact]
    public async Task Live_assign_submit_approve_persists_evidence_without_reservation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "链接客户");
        var productId = await SeedProductAsync(db, "链接商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Pending,
            (productId, 6m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        var ctl = NewController(db, adminId);
        var assign = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(await ctl.AssignSalesOrderLinks(pre.Id,
            new PreLoadingSalesOrderLinkAssignRequest
            {
                Links = new List<PreLoadingSalesOrderLinkAssignmentDto>
                {
                    new() { PreLoadingDetailId = detailId, SourceSalesOrderDetailId = sourceDetail.Id }
                }
            }));
        Assert.Equal(1, assign.LinkedCount);
        Assert.Equal(sourceDetail.Id, assign.Items.Single().SourceSalesOrderDetailId);
        Assert.Equal(order.Id, assign.Items.Single().SalesOrderId);
        Assert.Equal($"SO-{keyword}", assign.Items.Single().OrderNo);

        Assert.IsType<OkObjectResult>(await ctl.Submit(pre.Id));

        await using (var approveDb = _fixture.CreateDbContext())
        {
            Assert.IsType<OkObjectResult>(await NewController(approveDb, adminId).Approve(pre.Id));
        }

        var stored = await ReloadAsync(db, pre.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceSalesOrderDetailId);

        // 审核不得锁库 / 过账库存 / 生成出运：来源订单保持已审核且无库存流水。
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.StockMovements.AsNoTracking()
            .CountAsync(m => m.SourceDocId == order.Id));
        Assert.Equal(DocumentStatus.Approved,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    // ==================== 4. 来源语义 fail closed（订单状态 / 商品 / 客户） ====================

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Cancelled)]
    public async Task Live_assign_non_approved_order_fails_closed_and_preserves(DocumentStatus status)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "非审核订单客户");
        var productId = await SeedProductAsync(db, "非审核订单商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, status);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Pending,
            (productId, 6m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId).AssignSalesOrderLinks(pre.Id,
            new PreLoadingSalesOrderLinkAssignRequest
            {
                Links = new List<PreLoadingSalesOrderLinkAssignmentDto>
                {
                    new() { PreLoadingDetailId = detailId, SourceSalesOrderDetailId = sourceDetail.Id }
                }
            }));

        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

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
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var wrongDetail = await AddOrderDetailAsync(db, order.Id, productB, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Pending,
            (productA, 6m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId).AssignSalesOrderLinks(pre.Id,
            new PreLoadingSalesOrderLinkAssignRequest
            {
                Links = new List<PreLoadingSalesOrderLinkAssignmentDto>
                {
                    new() { PreLoadingDetailId = detailId, SourceSalesOrderDetailId = wrongDetail.Id }
                }
            }));

        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Live_assign_wrong_customer_fails_closed_and_preserves()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var ownCustomer = await SeedCustomerAsync(db, "订柜自有客户");
        var otherCustomer = await SeedCustomerAsync(db, "订单他人客户");
        var productId = await SeedProductAsync(db, "客户商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", ownCustomer);
        var foreignOrder = await SeedSalesOrderAsync(db, $"SO-{keyword}-OTHER", otherCustomer,
            DocumentStatus.Approved);
        var foreignDetail = await AddOrderDetailAsync(db, foreignOrder.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Pending,
            (productId, 6m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId).AssignSalesOrderLinks(pre.Id,
            new PreLoadingSalesOrderLinkAssignRequest
            {
                Links = new List<PreLoadingSalesOrderLinkAssignmentDto>
                {
                    new() { PreLoadingDetailId = detailId, SourceSalesOrderDetailId = foreignDetail.Id }
                }
            }));

        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Live_approve_after_source_order_cancelled_fails_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "来源撤销客户");
        var productId = await SeedProductAsync(db, "撤销商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Submitted,
            (productId, 6m, sourceDetail.Id));

        // 来源撤销：销售订单在预装柜审核之前被取消（真实来源单据状态）。
        order.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId).Approve(pre.Id));
        var stored = await ReloadAsync(db, pre.Id);
        Assert.Equal(DocumentStatus.Submitted, stored.Status);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceSalesOrderDetailId);
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
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);

        // 同一单据两条同来源明细行（4 + 4 = 8 ≤ 10）先聚合再审核 → 通过。
        var exact = await SeedPreLoadingAsync(db, $"YZ-{keyword}-OK", booking.Id, DocumentStatus.Submitted,
            (productId, 4m, sourceDetail.Id), (productId, 4m, sourceDetail.Id));
        Assert.IsType<OkObjectResult>(await NewController(db, adminId).Approve(exact.Id));
        Assert.Equal(DocumentStatus.Approved, (await ReloadAsync(db, exact.Id)).Status);

        // 再装 3 → 累计 11 > 10：拒绝且明细 / 状态 / 历史保持不变（原子失败，无半成品写入）。
        var overflow = await SeedPreLoadingAsync(db, $"YZ-{keyword}-OVER", booking.Id, DocumentStatus.Submitted,
            (productId, 3m, sourceDetail.Id));
        var overflowDetailCount = (await DetailIdsAsync(db, overflow.Id)).Count;

        await using (var overflowDb = _fixture.CreateDbContext())
        {
            await AssertCode(ErrorCodes.RuleConflict,
                () => NewController(overflowDb, adminId).Approve(overflow.Id));
        }

        var stored = await ReloadAsync(db, overflow.Id);
        Assert.Equal(DocumentStatus.Submitted, stored.Status);
        Assert.Equal(3m, stored.Details.Single().Quantity);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceSalesOrderDetailId);
        Assert.Equal(overflowDetailCount, (await DetailIdsAsync(db, overflow.Id)).Count);
    }

    [Fact]
    public async Task Live_cancelled_preloading_releases_capacity_but_keeps_history()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "取消释放客户");
        var productId = await SeedProductAsync(db, "取消释放商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);

        // 已取消预装柜的历史链接与数量原样保留，但不占用规划容量。
        var cancelled = await SeedPreLoadingAsync(db, $"YZ-{keyword}-CX", booking.Id, DocumentStatus.Cancelled,
            (productId, 8m, sourceDetail.Id));
        var target = await SeedPreLoadingAsync(db, $"YZ-{keyword}-OK", booking.Id, DocumentStatus.Submitted,
            (productId, 9m, sourceDetail.Id));

        Assert.IsType<OkObjectResult>(await NewController(db, adminId).Approve(target.Id));

        var history = await ReloadAsync(db, cancelled.Id);
        Assert.Equal(DocumentStatus.Cancelled, history.Status);
        Assert.Equal(8m, history.Details.Single().Quantity);
        Assert.Equal(sourceDetail.Id, history.Details.Single().SourceSalesOrderDetailId);
    }

    // ==================== 6. 两条独立连接竞争 ====================

    [Fact]
    public async Task Race_two_linked_preloadings_competing_for_capacity_admit_only_valid_total()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "竞争容量客户");
        var productId = await SeedProductAsync(db, "竞争容量商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var first = await SeedPreLoadingAsync(db, $"YZ-{keyword}-A", booking.Id, DocumentStatus.Submitted,
            (productId, 6m, sourceDetail.Id));
        var second = await SeedPreLoadingAsync(db, $"YZ-{keyword}-B", booking.Id, DocumentStatus.Submitted,
            (productId, 5m, sourceDetail.Id));

        // 6 + 5 = 11 > 10：两条独立连接同时审核，最多只允许一条通过。
        var results = await RaceAsync(
            () => TryApproveAsync(first.Id, adminId),
            () => TryApproveAsync(second.Id, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var approved = await verify.ContainerPreLoadings.AsNoTracking()
            .CountAsync(p => (p.Id == first.Id || p.Id == second.Id) && p.Status == DocumentStatus.Approved);
        Assert.Equal(1, approved);
        var approvedQuantity = await (from p in verify.ContainerPreLoadings.AsNoTracking()
                                      join d in verify.ContainerPreLoadingDetails.AsNoTracking()
                                          on p.Id equals d.PreLoadingId
                                      where (p.Id == first.Id || p.Id == second.Id)
                                            && p.Status == DocumentStatus.Approved
                                      select d.Quantity).SumAsync();
        Assert.True(approvedQuantity <= 10m, $"已审核链接数量 {approvedQuantity} 超过来源容量 10");
    }

    [Fact]
    public async Task Race_concurrent_approval_of_same_preloading_yields_single_approval()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "竞争同单客户");
        var productId = await SeedProductAsync(db, "竞争同单商品", "PCS");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Submitted,
            (productId, 6m, sourceDetail.Id));

        var results = await RaceAsync(
            () => TryApproveAsync(pre.Id, adminId),
            () => TryApproveAsync(pre.Id, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == pre.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(1, await verify.ContainerPreLoadingDetails.AsNoTracking()
            .CountAsync(d => d.PreLoadingId == pre.Id && d.SourceSalesOrderDetailId == sourceDetail.Id));
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-368）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class PreLoadingSalesOrderLinkSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PRELOADINGSALESORDERLINK_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-368] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-368] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PreLoadingSalesOrderLinkTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PreLoadingSalesOrderLinkSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => PreLoadingSalesOrderLinkSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
