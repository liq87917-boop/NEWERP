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
/// ERP-369 销售订单取消护栏（已审核预装柜需求计划证据）+ 上游销售订单行锁共享的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>拒绝</b>：未删除、已审核且明细显式链接本单明细的预装柜单存在时，真实控制器取消被拒绝，
/// 订单 / 明细 / 金额 / 下游库存与历史证据零改动；</item>
/// <item><b>释放</b>：显式取消预装柜单后护栏释放且链接与数量作为历史原样保留，来源取消随即成功；</item>
/// <item><b>不阻断</b>：未链接（null 历史遗留）/ 待提交 / 已提交 / 已删除 / 已取消 / 无关订单链接都不阻断；</item>
/// <item><b>真实授权</b>：受限业务员（既有登录名 = 员工编码 + 客户归属 + 显式「销售订单」菜单）可取消，
/// 菜单被回收 / 无身份时在任何状态变更之前 fail closed，绝无匿名 / 管理员兜底；</item>
/// <item><b>两条独立连接竞争</b>：真实控制器「预装柜审核 vs 来源取消」与「预装柜取消 vs 来源取消」
/// 经共享的上游销售订单行锁（<c>UPDLOCK, HOLDLOCK</c>，确定性锁序）串行化后结果一致。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SalesOrderPreLoadingCancellationSqlServerTests
    : IClassFixture<SalesOrderPreLoadingCancellationSqlServerFixture>
{
    private readonly SalesOrderPreLoadingCancellationSqlServerFixture _fixture;

    public SalesOrderPreLoadingCancellationSqlServerTests(SalesOrderPreLoadingCancellationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesOrderPreLoadingCancellationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"SPC-C-{Guid.NewGuid():N}"[..30],
            CustomerName = name,
            EmpId = empId,
            Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit = "PCS")
    {
        var product = new BaseProduct
        {
            ProductCode = $"SPC-P-{Guid.NewGuid():N}"[..30],
            ProductName = name,
            Spec = "规格A",
            Unit = unit
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
            BookingNo = no.Length > 50 ? no[..50] : no,
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            Status = status,
            Remark = "ERP-369_INT"
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
            OrderNo = no.Length > 50 ? no[..50] : no,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = status,
            Remark = "ERP-369_INT"
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
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m
        };
        db.SalesOrderDetails.Add(detail);
        await db.SaveChangesAsync();

        // 维持订单总额与明细一致（失败取消必须原样保留该金额）。
        var order = await db.SalesOrders.SingleAsync(o => o.Id == salesOrderId);
        order.TotalAmount = await db.SalesOrderDetails
            .Where(d => d.SalesOrderId == salesOrderId).SumAsync(d => d.Amount);
        await db.SaveChangesAsync();
        return detail;
    }

    private static async Task<ContainerPreLoading> SeedPreLoadingAsync(ErpDbContext db, string no,
        long? bookingId, DocumentStatus status, bool deleted,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no.Length > 50 ? no[..50] : no,
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            Status = status,
            IsDeleted = deleted,
            Remark = "ERP-369_INT"
        };
        db.ContainerPreLoadings.Add(pre);
        await db.SaveChangesAsync();

        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Quantity = quantity,
                SourceSalesOrderDetailId = sourceDetailId
            });
        }
        await db.SaveChangesAsync();
        return pre;
    }

    private static async Task<PurchaseOrder> SeedPurchaseOrderAsync(ErpDbContext db, string no,
        long supplierId, long owningSalesOrderId, DocumentStatus status)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no.Length > 50 ? no[..50] : no,
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = no,
            Status = status,
            Remark = "ERP-369_INT"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    // ==================== 真实控制器脚手架 ====================

    private static SalesOrderController NewSalesOrderController(ErpDbContext db, long? userId)
    {
        var ctl = new SalesOrderController(db, new DocumentNumberService(db));
        SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerPreLoadingController NewPreLoadingController(ErpDbContext db, long? userId)
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

    private static async Task<SalesOrder> ReloadOrderAsync(ErpDbContext db, long id)
        => await db.SalesOrders.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<ContainerPreLoading> ReloadPreLoadingAsync(ErpDbContext db, long id)
        => await db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

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

    private async Task<(bool Success, string Error)> TryCancelOrderAsync(long orderId, long? userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Cancel(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryApprovePreLoadingAsync(long preLoadingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewPreLoadingController(db, userId).Approve(preLoadingId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelPreLoadingAsync(long preLoadingId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewPreLoadingController(db, userId).Cancel(preLoadingId);
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

    private const long SupplierA = 943001L;

    // ==================== 1. 拒绝：已审核链接阻断来源取消 ====================

    [Fact]
    public async Task Live_cancel_refused_while_effective_approved_preloading_link_exists()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "证据阻断客户");
        var productId = await SeedProductAsync(db, "证据阻断商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var totalBefore = order.TotalAmount;
        var linked = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Approved, false,
            (productId, 6m, sourceDetail.Id));
        var purchase = await SeedPurchaseOrderAsync(db, $"PO-{keyword}", SupplierA, order.Id,
            DocumentStatus.Cancelled);

        await using (var cancelDb = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => NewSalesOrderController(cancelDb, adminId).Cancel(order.Id));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("预装柜", ex.Message);
            Assert.Contains($"YZ-{keyword}", ex.Message);
            Assert.Contains(SalesOrderCancellationRules.PreLoadingReversalRequirementText, ex.Message);
        }

        // 失败拒绝：订单 / 明细 / 金额 / 采购 / 库存 / 预装柜历史证据全部零改动。
        await using var verify = _fixture.CreateDbContext();
        var stored = await ReloadOrderAsync(verify, order.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Single(stored.Details);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().Id);
        Assert.Equal(totalBefore, stored.TotalAmount);
        Assert.Equal(DocumentStatus.Cancelled, (await verify.PurchaseOrders.AsNoTracking()
            .SingleAsync(p => p.Id == purchase.Id)).Status);
        Assert.Equal(0, await verify.StockMovements.AsNoTracking().CountAsync(m => m.SourceDocId == order.Id));

        var history = await ReloadPreLoadingAsync(verify, linked.Id);
        Assert.Equal(DocumentStatus.Approved, history.Status);
        Assert.False(history.IsDeleted);
        Assert.Equal(6m, history.Details.Single().Quantity);
        Assert.Equal(sourceDetail.Id, history.Details.Single().SourceSalesOrderDetailId);
    }

    // ==================== 2. 释放：显式取消预装柜单保留历史并放行 ====================

    [Fact]
    public async Task Live_explicit_preloading_cancellation_releases_guard_and_keeps_history()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "释放客户");
        var productId = await SeedProductAsync(db, "释放商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var linked = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Approved, false,
            (productId, 8m, sourceDetail.Id));

        // 同一场景的正 / 反两面：取消预装柜单之前来源取消被拒绝。
        var denied = await TryCancelOrderAsync(order.Id, adminId);
        Assert.False(denied.Success);
        Assert.Contains("预装柜", denied.Error);

        // 真实控制器取消预装柜单：只改状态，显式链接与数量作为历史原样保留。
        var released = await TryCancelPreLoadingAsync(linked.Id, adminId);
        Assert.True(released.Success, released.Error);

        await using (var verify = _fixture.CreateDbContext())
        {
            var history = await ReloadPreLoadingAsync(verify, linked.Id);
            Assert.Equal(DocumentStatus.Cancelled, history.Status);
            Assert.False(history.IsDeleted);
            Assert.Equal(8m, history.Details.Single().Quantity);
            Assert.Equal(sourceDetail.Id, history.Details.Single().SourceSalesOrderDetailId);
        }

        // 护栏释放 → 来源销售订单取消成功，订单明细原样保留。
        var result = await TryCancelOrderAsync(order.Id, adminId);
        Assert.True(result.Success, result.Error);

        await using var final = _fixture.CreateDbContext();
        var stored = await ReloadOrderAsync(final, order.Id);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        Assert.Single(stored.Details);
        Assert.Equal(sourceDetail.Id, stored.Details.Single().Id);
    }

    // ==================== 3. 不阻断：空链接 / 未审核 / 已删除 / 已取消 / 无关链接 ====================

    [Fact]
    public async Task Live_null_pending_submitted_deleted_cancelled_and_unrelated_links_do_not_block()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "不阻断客户");
        var productId = await SeedProductAsync(db, "不阻断商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var other = await SeedSalesOrderAsync(db, $"SO-{keyword}-X", customerId, DocumentStatus.Approved);
        var otherDetail = await AddOrderDetailAsync(db, other.Id, productId, 10m);

        await SeedPreLoadingAsync(db, $"YZ-{keyword}-NULL", booking.Id, DocumentStatus.Approved, false,
            (productId, 5m, null));
        await SeedPreLoadingAsync(db, $"YZ-{keyword}-PEND", booking.Id, DocumentStatus.Pending, false,
            (productId, 5m, sourceDetail.Id));
        await SeedPreLoadingAsync(db, $"YZ-{keyword}-SUB", booking.Id, DocumentStatus.Submitted, false,
            (productId, 5m, sourceDetail.Id));
        await SeedPreLoadingAsync(db, $"YZ-{keyword}-DEL", booking.Id, DocumentStatus.Approved, true,
            (productId, 5m, sourceDetail.Id));
        await SeedPreLoadingAsync(db, $"YZ-{keyword}-CX", booking.Id, DocumentStatus.Cancelled, false,
            (productId, 5m, sourceDetail.Id));
        var unrelated = await SeedPreLoadingAsync(db, $"YZ-{keyword}-OTHER", booking.Id,
            DocumentStatus.Approved, false, (productId, 5m, otherDetail.Id));

        var result = await TryCancelOrderAsync(order.Id, adminId);
        Assert.True(result.Success, result.Error);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Cancelled, (await ReloadOrderAsync(verify, order.Id)).Status);
        // 无关订单与其已审核链接完全不受影响（绝不按单号 / 相似度猜测来源）。
        Assert.Equal(DocumentStatus.Approved, (await ReloadOrderAsync(verify, other.Id)).Status);
        var unrelatedStored = await ReloadPreLoadingAsync(verify, unrelated.Id);
        Assert.Equal(DocumentStatus.Approved, unrelatedStored.Status);
        Assert.Equal(otherDetail.Id, unrelatedStored.Details.Single().SourceSalesOrderDetailId);
    }

    // ==================== 4. 真实授权：既有权限可取消，回收 / 无身份 fail closed ====================

    [Fact]
    public async Task Live_limited_user_with_existing_menu_and_scope_denied_then_released()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId, _) = await SeedLimitedOperatorAsync(db, withMenu: true);
        var customerId = await SeedCustomerAsync(db, "受限业务员客户", employeeId);
        var productId = await SeedProductAsync(db, "受限商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var linked = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Approved, false,
            (productId, 4m, sourceDetail.Id));

        // 真实受限账号（既有菜单 + 客户数据范围）：护栏对其同样生效，绝不降级为管理员放行。
        var denied = await TryCancelOrderAsync(order.Id, userId);
        Assert.False(denied.Success);
        Assert.Contains("预装柜", denied.Error);

        // 同一受限账号用既有权限取消预装柜单 → 护栏释放 → 来源取消成功。
        var released = await TryCancelPreLoadingAsync(linked.Id, userId);
        Assert.True(released.Success, released.Error);

        var allowed = await TryCancelOrderAsync(order.Id, userId);
        Assert.True(allowed.Success, allowed.Error);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Cancelled, (await ReloadOrderAsync(verify, order.Id)).Status);
        var history = await ReloadPreLoadingAsync(verify, linked.Id);
        Assert.Equal(DocumentStatus.Cancelled, history.Status);
        Assert.Equal(sourceDetail.Id, history.Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Live_revoked_menu_and_missing_identity_fail_closed_before_any_change()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId, roleId) = await SeedLimitedOperatorAsync(db, withMenu: true);
        var customerId = await SeedCustomerAsync(db, "回收授权客户", employeeId);
        var productId = await SeedProductAsync(db, "回收授权商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var linked = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Approved, false,
            (productId, 4m, sourceDetail.Id));

        // 只回收本测试自己播种的角色菜单授权（不触碰其它角色 / 系统角色）。
        foreach (var roleMenu in await db.SysRoleMenus
                     .Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync())
        {
            roleMenu.IsDeleted = true;
        }
        await db.SaveChangesAsync();

        var revoked = await TryCancelOrderAsync(order.Id, userId);
        Assert.False(revoked.Success);
        Assert.Contains("模块授权", revoked.Error);

        var anonymous = await TryCancelOrderAsync(order.Id, null);
        Assert.False(anonymous.Success);
        Assert.Contains("登录", anonymous.Error);

        // 任何状态变更都未发生：订单 / 明细 / 预装柜历史证据零改动。
        await using var verify = _fixture.CreateDbContext();
        var stored = await ReloadOrderAsync(verify, order.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Single(stored.Details);
        var history = await ReloadPreLoadingAsync(verify, linked.Id);
        Assert.Equal(DocumentStatus.Approved, history.Status);
        Assert.Equal(sourceDetail.Id, history.Details.Single().SourceSalesOrderDetailId);
    }

    // ==================== 5. 两条独立连接竞争（真实控制器 + 共享上游订单行锁） ====================

    [Fact]
    public async Task Race_controller_preloading_approval_versus_source_cancellation_is_consistent()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "竞争审核客户");
        var productId = await SeedProductAsync(db, "竞争审核商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Submitted, false,
            (productId, 6m, sourceDetail.Id));

        // 两条独立连接同时发起：预装柜审核（真实控制器）vs 来源销售订单取消。
        var results = await RaceAsync(
            () => TryApprovePreLoadingAsync(pre.Id, adminId),
            () => TryCancelOrderAsync(order.Id, adminId));

        await using var verify = _fixture.CreateDbContext();
        var orderStatus = (await ReloadOrderAsync(verify, order.Id)).Status;
        var stored = await ReloadPreLoadingAsync(verify, pre.Id);

        // 共享同一把上游行锁：只能成功其一，且结果自洽（不可能出现「已审核链接 + 来源已取消」）。
        Assert.Equal(1, results.Count(r => r.Success));
        if (orderStatus == DocumentStatus.Cancelled)
            Assert.NotEqual(DocumentStatus.Approved, stored.Status);
        else
            Assert.Equal(DocumentStatus.Approved, stored.Status);

        // 无论哪一方获胜：需求证据链接与数量都原样保留，且没有任何库存流水。
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceSalesOrderDetailId);
        Assert.Equal(6m, stored.Details.Single().Quantity);
        Assert.Equal(0, await verify.StockMovements.AsNoTracking().CountAsync(m => m.SourceDocId == order.Id));
    }

    [Fact]
    public async Task Race_preloading_cancellation_versus_source_cancellation_is_consistent()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "竞争取消客户");
        var productId = await SeedProductAsync(db, "竞争取消商品");
        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}", customerId);
        var order = await SeedSalesOrderAsync(db, $"SO-{keyword}", customerId, DocumentStatus.Approved);
        var sourceDetail = await AddOrderDetailAsync(db, order.Id, productId, 10m);
        var pre = await SeedPreLoadingAsync(db, $"YZ-{keyword}", booking.Id, DocumentStatus.Approved, false,
            (productId, 6m, sourceDetail.Id));

        // 两条独立连接同时发起：预装柜取消（释放护栏）vs 来源销售订单取消（护栏判定）。
        var results = await RaceAsync(
            () => TryCancelPreLoadingAsync(pre.Id, adminId),
            () => TryCancelOrderAsync(order.Id, adminId));

        var preLoadingCancel = results[0];
        var orderCancel = results[1];

        await using var verify = _fixture.CreateDbContext();
        var orderStatus = (await ReloadOrderAsync(verify, order.Id)).Status;
        var stored = await ReloadPreLoadingAsync(verify, pre.Id);

        // 预装柜取消自身必然成功（无下游装柜清单），且来源取消与订单状态严格一致：
        // 先取消预装柜 → 来源取消放行；先取消来源 → 被护栏拒绝并回滚。二者不可能同时「消失」证据。
        Assert.True(preLoadingCancel.Success, preLoadingCancel.Error);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        Assert.Equal(orderCancel.Success, orderStatus == DocumentStatus.Cancelled);
        Assert.False(orderCancel.Success && orderStatus != DocumentStatus.Cancelled);

        // 历史证据原样保留：显式来源链接与数量在取消后仍可读。
        Assert.Equal(sourceDetail.Id, stored.Details.Single().SourceSalesOrderDetailId);
        Assert.Equal(6m, stored.Details.Single().Quantity);
        Assert.False(stored.IsDeleted);
    }

    /// <summary>
    /// 播种受限操作员（既有权限口径：登录名 = 员工编码 + 显式「销售订单」菜单授权），
    /// 返回用户 / 员工 / 角色 Id；调用方用员工 Id 绑定客户数据范围。
    /// </summary>
    private static async Task<(long UserId, long EmployeeId, long RoleId)> SeedLimitedOperatorAsync(
        ErpDbContext db, bool withMenu)
    {
        var code = $"ERP369-OP-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "预装柜操作员",
            RoleCode = $"ERP369-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu)
        {
            // 复用既有已播种菜单（MenuCode 唯一索引），只新增角色菜单授权绑定。
            await GrantMenuAsync(db, role.Id, SalesOrderCancellationRules.RequiredMenuCode,
                SalesOrderCancellationRules.RequiredMenuText);
            await GrantMenuAsync(db, role.Id, PreLoadingSalesOrderLinkRules.RequiredMenuCode,
                PreLoadingSalesOrderLinkRules.RequiredMenuText);
        }

        return (user.Id, employee.Id, role.Id);
    }

    /// <summary>按既有菜单编码授权（已存在则复用，绝不重复插入菜单主数据）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menuId = await db.SysMenus
            .Where(m => m.MenuCode == menuCode)
            .Select(m => m.Id)
            .FirstOrDefaultAsync();

        if (menuId == 0)
        {
            var menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
            menuId = menu.Id;
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-369）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，发现同名库已存在立即拒绝；连接串只来自进程环境变量或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SalesOrderPreLoadingCancellationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SALESORDERPRELOADINGCANCEL_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-369] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-369] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderPreLoadingCancellationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderPreLoadingCancellationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => SalesOrderPreLoadingCancellationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

