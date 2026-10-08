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
/// ERP-373 装柜清单「出运证据」显式链接工作流的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标；复用 ERP-366 专用 localdb Fixture）。
/// <list type="number">
/// <item><b>真实控制器往返</b>：新增 → 指派 → 改派 → 主表更新 / 明细替换 → 清除，全程走
/// <see cref="ContainerLoadingListController"/>（既有「装柜清单」+「销售出库」权限与实时客户范围），
/// 逐步复核**服务端持久化**的精确 <c>SourceStockOutDetailId</c>；</item>
/// <item><b>来源不可用</b>：来源出库单取消后原链接**原样保留**并显式标注「来源不可用」，绝不静默清除；</item>
/// <item><b>批量原子性</b>：任一链接非法则整批拒绝，库中所有行与状态保持原样；</item>
/// <item><b>两条独立连接竞争</b>：同一单据两行并发指派 → 两行各自生效且无撕裂；同一行并发指派到不同来源 →
/// 只保留一个一致结果（确定性锁序：上游销售出库行升序 → 装柜清单行 <c>UPDLOCK, HOLDLOCK</c>）。</item>
/// </list>
/// <para>安全口径：目标必须为 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class LoadingOutboundWorkflowSqlServerTests
    : IClassFixture<LoadingStockOutLinkSqlServerFixture>
{
    private readonly LoadingStockOutLinkSqlServerFixture _fixture;

    public LoadingOutboundWorkflowSqlServerTests(LoadingStockOutLinkSqlServerFixture fixture)
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static string Clip(string value, int length = 50)
        => value.Length > length ? value[..length] : value;

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = Clip($"373-C-{Guid.NewGuid():N}", 30), CustomerName = name, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit)
    {
        var product = new BaseProduct
        {
            ProductCode = Clip($"373-P-{Guid.NewGuid():N}", 30), ProductName = name, Spec = "规格A", Unit = unit
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<SalesOrder> SeedSalesOrderAsync(ErpDbContext db, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = Clip($"SO-373-{Guid.NewGuid():N}"), OrderDate = DateTime.Today,
            CustomerId = customerId, Status = DocumentStatus.Approved, Remark = "ERP-373_INT"
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<StockOut> SeedStockOutAsync(ErpDbContext db, long customerId, long? salesOrderId)
    {
        var stockOut = new StockOut
        {
            StockOutNo = Clip($"CK-373-{Guid.NewGuid():N}"), StockOutDate = DateTime.Today,
            CustomerId = customerId, Status = DocumentStatus.Approved, SalesOrderId = salesOrderId,
            Remark = "ERP-373_INT"
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

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(ErpDbContext db, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = Clip($"ZQ-373-{Guid.NewGuid():N}"), LoadingDate = DateTime.Today,
            CustomerId = customerId, Status = status, Remark = "ERP-373_INT"
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

    /// <summary>已播种的既有管理员账号（既有系统角色，不新增任何用户授权 / 不提供匿名降级）。</summary>
    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task<ContainerLoadingList> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerLoadingLists.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<List<long>> DetailIdsAsync(ErpDbContext db, long loadingListId)
        => await db.ContainerLoadingDetails.AsNoTracking()
            .Where(d => d.LoadingListId == loadingListId && !d.IsDeleted)
            .OrderBy(d => d.Id).Select(d => d.Id).ToListAsync();

    private static LoadingStockOutLinkAssignRequest Assign(params (long DetailId, long? SourceDetailId)[] links)
        => new()
        {
            Links = links.Select(l => new LoadingStockOutLinkAssignmentDto
            {
                LoadingDetailId = l.DetailId,
                SourceStockOutDetailId = l.SourceDetailId,
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
        long loadingListId, long userId, LoadingStockOutLinkAssignRequest request)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewController(db, userId).AssignStockOutLinks(loadingListId, request);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 1. 真实 API 往返：保存草稿 → 指派 → 改派 → 保留 → 清除 ====================

    [Fact]
    public async Task Live_saved_draft_link_reassign_preserve_and_clear_roundtrip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"出运往返客户-{Tag()}");
        var productId = await SeedProductAsync(db, $"出运往返商品-{Tag()}", "PCS");
        var order = await SeedSalesOrderAsync(db, customerId);
        var stockOut = await SeedStockOutAsync(db, customerId, order.Id);
        var first = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var second = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var containerNo = Clip($"CTN-373-{Tag()}");

        // 1) 先保存草稿（无链接）—— 未保存单据不得登记链接
        var create = await NewController(db, adminId).Create(new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerNo = containerNo,
            Details = new List<ContainerLoadingDetail>
            {
                new() { ProductId = productId, ProductName = $"商品{productId}", Quantity = 3m,
                    Cartons = 1m, Weight = 1m, Volume = 1m }
            }
        });
        Assert.IsType<OkObjectResult>(create);

        var created = await db.ContainerLoadingLists.AsNoTracking()
            .SingleAsync(l => l.ContainerNo == containerNo && !l.IsDeleted);
        var detailId = (await DetailIdsAsync(db, created.Id)).Single();
        Assert.Null((await ReloadAsync(db, created.Id)).Details.Single().SourceStockOutDetailId);

        // 只读有界候选：本单权威客户下「已审核、未删除」销售出库明细
        var candidates = AssertOk<List<LoadingStockOutCandidateDto>>(
            await NewController(db, adminId).GetStockOutCandidates(created.Id, null, 0));
        Assert.Contains(candidates, c => c.StockOutDetailId == first.Id);
        Assert.Contains(candidates, c => c.StockOutDetailId == second.Id);
        Assert.All(candidates, c => Assert.Equal(customerId, c.CustomerId));

        // 2) 显式链接第一条来源明细
        var linked = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, adminId).AssignStockOutLinks(created.Id, Assign((detailId, first.Id))));
        Assert.Equal(1, linked.LinkedCount);
        Assert.Equal(first.Id, Assert.Single(linked.Items).SourceStockOutDetailId);

        // 3) 改派到第二条来源明细（精确）
        var reassigned = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, adminId).AssignStockOutLinks(created.Id, Assign((detailId, second.Id))));
        Assert.Equal(1, reassigned.LinkedCount);
        var reassignedLine = Assert.Single(reassigned.Items);
        Assert.Equal(second.Id, reassignedLine.SourceStockOutDetailId);
        Assert.True(reassignedLine.SourceAvailable);
        Assert.Equal(second.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceStockOutDetailId);

        // 重新加载服务端持久化结果（GET 只读回显）
        var reloaded = AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, adminId).GetStockOutLinks(created.Id));
        var reloadedLine = Assert.Single(reloaded);
        Assert.Equal(second.Id, reloadedLine.SourceStockOutDetailId);
        Assert.Equal(LoadingStockOutLinkRules.LinkedEvidenceText, reloadedLine.SourceAvailabilityText);

        // 4) 主表单独更新（不带明细）：链接仍原样保留，绝不静默清空
        var headOnly = await NewController(db, adminId).Update(created.Id, new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerNo = containerNo,
            Remark = "主表更新",
            Details = new List<ContainerLoadingDetail>()
        });
        Assert.IsType<OkObjectResult>(headOnly);
        Assert.Equal(second.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceStockOutDetailId);

        // 5) 明细整体替换（随行携带同一来源）：链接保留
        var update = await NewController(db, adminId).Update(created.Id, new ContainerLoadingList
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerNo = containerNo,
            Details = new List<ContainerLoadingDetail>
            {
                new() { ProductId = productId, ProductName = $"商品{productId}", Quantity = 4m,
                    Cartons = 1m, Weight = 1m, Volume = 1m, SourceStockOutDetailId = second.Id }
            }
        });
        Assert.IsType<OkObjectResult>(update);
        Assert.Equal(second.Id, (await ReloadAsync(db, created.Id)).Details.Single().SourceStockOutDetailId);

        // 6) 清除：显式未链接
        var currentDetailId = (await DetailIdsAsync(db, created.Id)).Single();
        var cleared = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, adminId).AssignStockOutLinks(created.Id, Assign((currentDetailId, null))));
        Assert.Equal(1, cleared.ClearedCount);
        Assert.Null((await ReloadAsync(db, created.Id)).Details.Single().SourceStockOutDetailId);

        var clearedLine = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, adminId).GetStockOutLinks(created.Id)));
        Assert.False(clearedLine.SourceAvailable);
        Assert.Equal(LoadingStockOutLinkRules.UnlinkedEvidenceText, clearedLine.SourceAvailabilityText);
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
        var order = await SeedSalesOrderAsync(db, customerId);
        var stockOut = await SeedStockOutAsync(db, customerId, order.Id);
        var sourceA = await AddStockOutDetailAsync(db, stockOut.Id, productA, 10m);
        var sourceB = await AddStockOutDetailAsync(db, stockOut.Id, productB, 10m, "CTN");
        var list = await SeedLoadingListAsync(db, customerId, DocumentStatus.Pending,
            (productA, 5m, null), (productB, 5m, null));
        var ids = await DetailIdsAsync(db, list.Id);
        Assert.Equal(2, ids.Count);

        // 第二行商品不一致（商品 B 链接到商品 A 的来源明细）→ 整批拒绝，一行都不写
        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId)
            .AssignStockOutLinks(list.Id, Assign((ids[0], sourceA.Id), (ids[1], sourceA.Id))));

        var afterDenied = await ReloadAsync(db, list.Id);
        Assert.All(afterDenied.Details, d => Assert.Null(d.SourceStockOutDetailId));
        Assert.Equal(DocumentStatus.Pending, afterDenied.Status);

        // 合法批次随后仍可成功（证明拒绝批次未污染任何行 / 状态）
        var ok = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, adminId).AssignStockOutLinks(list.Id, Assign((ids[1], sourceB.Id))));
        Assert.Equal(1, ok.LinkedCount);

        var afterOk = await ReloadAsync(db, list.Id);
        Assert.Null(afterOk.Details.Single(d => d.Id == ids[0]).SourceStockOutDetailId);
        Assert.Equal(sourceB.Id, afterOk.Details.Single(d => d.Id == ids[1]).SourceStockOutDetailId);
    }

    [Fact]
    public async Task Live_cancelled_source_keeps_evidence_and_is_shown_unavailable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, $"来源取消客户-{Tag()}");
        var productId = await SeedProductAsync(db, $"来源取消商品-{Tag()}", "PCS");
        var stockOut = await SeedStockOutAsync(db, customerId, salesOrderId: null);
        var other = await SeedStockOutAsync(db, customerId, salesOrderId: null);
        var source = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var otherSource = await AddStockOutDetailAsync(db, other.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, customerId, DocumentStatus.Pending, (productId, 6m, source.Id));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        // 来源出库单取消 / 撤销审核：历史装柜证据原样保留，只显式标注「来源不可用」
        stockOut.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var line = Assert.Single(AssertOk<List<LoadingStockOutLinkLineDto>>(
            await NewController(db, adminId).GetStockOutLinks(list.Id)));
        Assert.Equal(source.Id, line.SourceStockOutDetailId);
        Assert.False(line.SourceAvailable);
        Assert.Equal(LoadingStockOutLinkRules.UnavailableEvidenceText, line.SourceAvailabilityText);
        Assert.Contains("绝不静默清除", line.SourceAvailabilityText);
        Assert.Equal(source.Id, (await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);

        // 清除既有链接（显式未链接）后重新链接已取消来源：拒绝，且保持显式未链接
        var cleared = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, adminId).AssignStockOutLinks(list.Id, Assign((detailId, null))));
        Assert.Equal(1, cleared.ClearedCount);
        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);

        await AssertCode(ErrorCodes.RuleConflict, () => NewController(db, adminId)
            .AssignStockOutLinks(list.Id, Assign((detailId, source.Id))));
        Assert.Null((await ReloadAsync(db, list.Id)).Details.Single().SourceStockOutDetailId);

        // 改派到另一条合法来源成功（证明历史证据可在显式选择下改派，而不是被静默清除）
        var reassigned = AssertOk<LoadingStockOutLinkAssignResultDto>(
            await NewController(db, adminId).AssignStockOutLinks(list.Id, Assign((detailId, otherSource.Id))));
        Assert.Equal(otherSource.Id, Assert.Single(reassigned.Items).SourceStockOutDetailId);
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
        var order = await SeedSalesOrderAsync(db, customerId);
        var stockOut = await SeedStockOutAsync(db, customerId, order.Id);
        var first = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var second = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, customerId, DocumentStatus.Pending,
            (productId, 5m, null), (productId, 5m, null));
        var ids = await DetailIdsAsync(db, list.Id);

        // 两条独立连接 / 事务同时给同一单据的不同行指派不同来源（同一上游出库单行 → 同一把行锁）
        var results = await RaceAsync(
            () => TryAssignAsync(list.Id, adminId, Assign((ids[0], first.Id))),
            () => TryAssignAsync(list.Id, adminId, Assign((ids[1], second.Id))));

        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var verify = _fixture.CreateDbContext();
        var stored = await ReloadAsync(verify, list.Id);
        Assert.Equal(first.Id, stored.Details.Single(d => d.Id == ids[0]).SourceStockOutDetailId);
        Assert.Equal(second.Id, stored.Details.Single(d => d.Id == ids[1]).SourceStockOutDetailId);
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
        var order = await SeedSalesOrderAsync(db, customerId);
        var stockOut = await SeedStockOutAsync(db, customerId, order.Id);
        var first = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var second = await AddStockOutDetailAsync(db, stockOut.Id, productId, 10m);
        var list = await SeedLoadingListAsync(db, customerId, DocumentStatus.Pending, (productId, 5m, null));
        var detailId = (await DetailIdsAsync(db, list.Id)).Single();

        // 两条独立连接同时把**同一行**指派到不同来源：串行化后只保留一个一致结果（绝不撕裂 / 绝不为空）
        var results = await RaceAsync(
            () => TryAssignAsync(list.Id, adminId, Assign((detailId, first.Id))),
            () => TryAssignAsync(list.Id, adminId, Assign((detailId, second.Id))));

        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var verify = _fixture.CreateDbContext();
        var stored = await ReloadAsync(verify, list.Id);
        var value = Assert.Single(stored.Details.Where(d => d.Id == detailId)).SourceStockOutDetailId;
        Assert.NotNull(value);
        Assert.Contains(value, new long?[] { first.Id, second.Id });
        Assert.Single(stored.Details);
    }
}

/// <summary>ERP-373 目标库护栏单元级校验（复用 ERP-366 Fixture 的专用目标判定）。</summary>
public sealed class LoadingOutboundWorkflowTargetGuardTests
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
