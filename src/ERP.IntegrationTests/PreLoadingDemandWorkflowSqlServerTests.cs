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
/// ERP-372 预装柜「需求来源」显式链接工作流的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标；复用 ERP-368 专用 localdb Fixture）。
/// <list type="number">
/// <item><b>真实控制器往返</b>：新增带链接 → 编辑改派 → 清除，全程走
/// <see cref="ContainerPreLoadingController"/>（既有「预装柜单」+「销售订单」权限与实时客户范围），
/// 逐步复核**服务端持久化**的精确 <c>SourceSalesOrderDetailId</c>；</item>
/// <item><b>批量原子性</b>：任一链接非法则整批拒绝，库中所有行保持原样；</item>
/// <item><b>两条独立连接竞争</b>：同一单据两行并发指派 → 两行各自生效且无撕裂；同一行并发指派到不同来源 →
/// 只保留一个一致结果（确定性锁序：上游销售订单行升序 → 订柜信息行 → 预装柜单行 <c>UPDLOCK, HOLDLOCK</c>）。</item>
/// </list>
/// <para>安全口径：目标必须为 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PreLoadingDemandWorkflowSqlServerTests
    : IClassFixture<PreLoadingSalesOrderLinkSqlServerFixture>
{
    private readonly PreLoadingSalesOrderLinkSqlServerFixture _fixture;

    public PreLoadingDemandWorkflowSqlServerTests(PreLoadingSalesOrderLinkSqlServerFixture fixture)
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static string Clip(string value, int length = 50)
        => value.Length > length ? value[..length] : value;

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = Clip($"372-C-{Guid.NewGuid():N}", 30), CustomerName = name, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit)
    {
        var product = new BaseProduct
        {
            ProductCode = Clip($"372-P-{Guid.NewGuid():N}", 30), ProductName = name, Spec = "规格A", Unit = unit
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<ContainerBooking> SeedBookingAsync(ErpDbContext db, long customerId)
    {
        var booking = new ContainerBooking
        {
            BookingNo = Clip($"DG-372-{Guid.NewGuid():N}"), BookingDate = DateTime.Today,
            CustomerId = customerId, Status = DocumentStatus.Approved, Remark = "ERP-372_INT"
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = Clip($"SO-372-{Guid.NewGuid():N}"), OrderDate = DateTime.Today,
            CustomerId = customerId, Status = DocumentStatus.Approved, Remark = "ERP-372_INT"
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

    private static async Task<ContainerPreLoading> SeedPreLoadingAsync(ErpDbContext db, long? bookingId,
        DocumentStatus status, params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = Clip($"YZ-372-{Guid.NewGuid():N}"), LoadingDate = DateTime.Today,
            BookingId = bookingId, Status = status, Remark = "ERP-372_INT"
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

    /// <summary>已播种的既有管理员账号（既有系统角色，不新增任何用户授权 / 不提供匿名降级）。</summary>
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

    private static PreLoadingSalesOrderLinkAssignRequest Assign(
        params (long DetailId, long? SourceDetailId)[] links)
        => new()
        {
            Links = links.Select(l => new PreLoadingSalesOrderLinkAssignmentDto
            {
                PreLoadingDetailId = l.DetailId,
                SourceSalesOrderDetailId = l.SourceDetailId,
            }).ToList()
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

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

    private async Task<(bool Success, string Error)> TryAssignAsync(
        long preLoadingId, long userId, PreLoadingSalesOrderLinkAssignRequest request)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewController(db, userId).AssignSalesOrderLinks(preLoadingId, request);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 1. 真实 API 往返：新增带链接 → 编辑改派 → 清除 ====================

    [Fact]
    public async Task Live_create_edit_clear_roundtrip_persists_exact_source_detail_ids()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"需求往返客户-{Tag()}");
        var productId = await SeedProductAsync(db, $"需求往返商品-{Tag()}", "PCS");
        var booking = await SeedBookingAsync(db, customerId);
        var order = await SeedSalesOrderAsync(db, customerId);
        var first = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var second = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var containerNo = Clip($"CTN-372-{Tag()}");

        // 1) 新增即带显式链接：保存成功且服务端持久化精确来源明细 Id
        var create = await NewController(db, adminId).Create(new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = containerNo,
            Details = new List<ContainerPreLoadingDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = $"商品{productId}", Quantity = 3m,
                    Cartons = 1m, Weight = 1m, Volume = 1m, SourceSalesOrderDetailId = first.Id
                }
            }
        });
        Assert.IsType<OkObjectResult>(create);

        var created = await db.ContainerPreLoadings.AsNoTracking()
            .SingleAsync(p => p.ContainerNo == containerNo && !p.IsDeleted);
        var detailId = (await DetailIdsAsync(db, created.Id)).Single();
        Assert.Equal(first.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceSalesOrderDetailId);

        // 只读有界候选：本单权威订柜客户下「已审核、未删除」销售订单明细
        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, adminId).GetSalesOrderCandidates(created.Id, null, 0));
        Assert.Contains(candidates, c => c.SalesOrderDetailId == second.Id);
        Assert.All(candidates, c => Assert.Equal(customerId, c.CustomerId));

        // 2) 编辑改派：精确改到第二条订单明细
        var edited = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, adminId).AssignSalesOrderLinks(created.Id, Assign((detailId, second.Id))));
        Assert.Equal(1, edited.LinkedCount);
        var editedLine = Assert.Single(edited.Items);
        Assert.Equal(second.Id, editedLine.SourceSalesOrderDetailId);
        Assert.True(editedLine.SourceAvailable);
        Assert.Equal(second.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceSalesOrderDetailId);

        // 重新加载服务端持久化结果（GET 只读回显）
        var reloaded = AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, adminId).GetSalesOrderLinks(created.Id));
        Assert.Equal(second.Id, Assert.Single(reloaded).SourceSalesOrderDetailId);

        // 3) 明细整体替换（随行携带同一来源）：链接保留
        var update = await NewController(db, adminId).Update(created.Id, new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = containerNo,
            Details = new List<ContainerPreLoadingDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = $"商品{productId}", Quantity = 4m,
                    Cartons = 1m, Weight = 1m, Volume = 1m, SourceSalesOrderDetailId = second.Id
                }
            }
        });
        Assert.IsType<OkObjectResult>(update);
        Assert.Equal(second.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceSalesOrderDetailId);

        // 4) 主表单独更新（不带明细）：链接仍原样保留，绝不静默清空
        var headOnly = await NewController(db, adminId).Update(created.Id, new ContainerPreLoading
        {
            LoadingDate = DateTime.Today,
            BookingId = booking.Id,
            ContainerNo = containerNo,
            Remark = "主表更新",
            Details = new List<ContainerPreLoadingDetail>()
        });
        Assert.IsType<OkObjectResult>(headOnly);
        Assert.Equal(second.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceSalesOrderDetailId);

        // 5) 清除：显式未链接
        var currentDetailId = (await DetailIdsAsync(db, created.Id)).Single();
        var cleared = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, adminId).AssignSalesOrderLinks(created.Id, Assign((currentDetailId, null))));
        Assert.Equal(1, cleared.ClearedCount);
        Assert.Null((await ReloadAsync(db, created.Id)).Details.Single().SourceSalesOrderDetailId);

        var clearedLine = Assert.Single(AssertOk<List<PreLoadingSalesOrderLinkLineDto>>(
            await NewController(db, adminId).GetSalesOrderLinks(created.Id)));
        Assert.False(clearedLine.SourceAvailable);
        Assert.Equal(PreLoadingSalesOrderLinkRules.UnlinkedEvidenceText, clearedLine.SourceAvailabilityText);
    }

    [Fact]
    public async Task Live_denied_batch_leaves_all_assignments_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"批量拒绝客户-{Tag()}");
        var productA = await SeedProductAsync(db, $"批量拒绝商品A-{Tag()}", "PCS");
        var productB = await SeedProductAsync(db, $"批量拒绝商品B-{Tag()}", "CTN");
        var booking = await SeedBookingAsync(db, customerId);
        var orderA = await SeedSalesOrderAsync(db, customerId);
        var detailA = await AddOrderDetailAsync(db, orderA.Id, productA, 10m);
        var orderB = await SeedSalesOrderAsync(db, customerId);
        var detailB = await AddOrderDetailAsync(db, orderB.Id, productB, 10m, "CTN");
        var pre = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Pending,
            (productA, 5m, null), (productB, 5m, null));
        var ids = await DetailIdsAsync(db, pre.Id);
        Assert.Equal(2, ids.Count);

        // 第二行商品不一致（商品 B 链接到订单明细 A）→ 整批拒绝，一行都不写
        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId)
            .AssignSalesOrderLinks(pre.Id, Assign((ids[0], detailA.Id), (ids[1], detailA.Id))));

        var afterDenied = await ReloadAsync(db, pre.Id);
        Assert.All(afterDenied.Details, d => Assert.Null(d.SourceSalesOrderDetailId));
        Assert.Equal(DocumentStatus.Pending, afterDenied.Status);

        // 合法批次随后仍可成功（证明拒绝批次未污染任何行 / 状态）
        var ok = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, adminId).AssignSalesOrderLinks(pre.Id, Assign((ids[1], detailB.Id))));
        Assert.Equal(1, ok.LinkedCount);

        var afterOk = await ReloadAsync(db, pre.Id);
        Assert.Null(afterOk.Details.Single(d => d.Id == ids[0]).SourceSalesOrderDetailId);
        Assert.Equal(detailB.Id, afterOk.Details.Single(d => d.Id == ids[1]).SourceSalesOrderDetailId);
    }

    // ==================== 2. 两条独立连接竞争 ====================

    [Fact]
    public async Task Race_two_connections_assign_different_rows_on_same_document_are_both_persisted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"并发两行客户-{Tag()}");
        var productId = await SeedProductAsync(db, $"并发两行商品-{Tag()}", "PCS");
        var booking = await SeedBookingAsync(db, customerId);
        var order = await SeedSalesOrderAsync(db, customerId);
        var first = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var second = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Pending,
            (productId, 5m, null), (productId, 5m, null));
        var ids = await DetailIdsAsync(db, pre.Id);

        // 两条独立连接 / 事务同时给同一单据的不同行指派不同来源（同一权威订单行 → 同一把行锁）
        var results = await RaceAsync(
            () => TryAssignAsync(pre.Id, adminId, Assign((ids[0], first.Id))),
            () => TryAssignAsync(pre.Id, adminId, Assign((ids[1], second.Id))));

        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var verify = _fixture.CreateDbContext();
        var stored = await ReloadAsync(verify, pre.Id);
        Assert.Equal(first.Id, stored.Details.Single(d => d.Id == ids[0]).SourceSalesOrderDetailId);
        Assert.Equal(second.Id, stored.Details.Single(d => d.Id == ids[1]).SourceSalesOrderDetailId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Race_two_connections_assign_same_line_to_different_sources_yield_single_consistent_value()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"并发同行客户-{Tag()}");
        var productId = await SeedProductAsync(db, $"并发同行商品-{Tag()}", "PCS");
        var booking = await SeedBookingAsync(db, customerId);
        var order = await SeedSalesOrderAsync(db, customerId);
        var first = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var second = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, booking.Id, DocumentStatus.Pending, (productId, 5m, null));
        var detailId = (await DetailIdsAsync(db, pre.Id)).Single();

        // 两条独立连接同时把**同一行**指派到不同来源：串行化后只保留一个一致结果（绝不撕裂 / 绝不为空）
        var results = await RaceAsync(
            () => TryAssignAsync(pre.Id, adminId, Assign((detailId, first.Id))),
            () => TryAssignAsync(pre.Id, adminId, Assign((detailId, second.Id))));

        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var verify = _fixture.CreateDbContext();
        var stored = await ReloadAsync(verify, pre.Id);
        var value = Assert.Single(stored.Details.Where(d => d.Id == detailId)).SourceSalesOrderDetailId;
        Assert.NotNull(value);
        Assert.Contains(value, new long?[] { first.Id, second.Id });
        Assert.Single(stored.Details);
    }
}




/// <summary>ERP-372 目标库护栏单元级校验（复用 ERP-368 Fixture 的专用目标判定）。</summary>
public sealed class PreLoadingDemandWorkflowTargetGuardTests
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

