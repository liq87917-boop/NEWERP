using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-368 预装柜明细 → 显式已审核销售订单需求计划证据链接 与 累计容量护栏单元测试。
/// <para>覆盖：候选证据有界查询（返回父订单 / 商品 / 客户 / 剩余基础单位数量、排除未审核 / 已取消 / 已删除 /
/// 范围外客户、扣减已链接占用）、链接校验（非正 / 不存在 / 已删除 / 已取消 / 未审核 / 商品不一致 / 单位不兼容 /
/// 客户与权威订柜不一致 / 数量非正）、历史未链接明细保持显式未链接、审核按来源订单明细逐条累计容量
/// （重复行聚合、取消释放容量但保留历史）、失败保留明细与状态、审核不锁库 / 不过账库存 / 财务、
/// 菜单与客户范围 fail closed、需求计划证据边界文案。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class PreLoadingSalesOrderLinkTests
{
    private const long CustomerA = 968001L;
    private const long CustomerB = 968002L;
    private const long ProductA = 968101L;
    private const long ProductB = 968102L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = name, EmpId = empId, Status = 1
        });
        db.SaveChanges();
    }

    private static void SeedProduct(ErpDbContext db, long id, string name, string unit = "PCS")
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = name, Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId,
            Status = status, IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId,
        DocumentStatus status, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId,
            Status = status, IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail AddOrderDetail(ErpDbContext db, long salesOrderId, long productId,
        decimal quantity, string unit = "PCS", bool deleted = false, string? productName = null)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId, ProductId = productId,
            ProductName = productName ?? $"商品{productId}", Unit = unit,
            Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m, IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, long? bookingId,
        DocumentStatus status, params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no, LoadingDate = DateTime.Today, BookingId = bookingId, Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceSalesOrderDetailId = sourceDetailId
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static async Task<ContainerPreLoading> ReloadAsync(ErpDbContext db, long id)
        => await db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().SingleAsync(o => o.Id == id);

    private static async Task<long> SingleDetailIdAsync(ErpDbContext db, long preLoadingId)
        => await db.ContainerPreLoadingDetails.AsNoTracking()
            .Where(d => d.PreLoadingId == preLoadingId && !d.IsDeleted)
            .OrderBy(d => d.Id).Select(d => d.Id).FirstAsync();

    private static PreLoadingSalesOrderLinkAssignRequest Assign(long detailId, long? sourceDetailId)
        => new()
        {
            Links = new List<PreLoadingSalesOrderLinkAssignmentDto>
            {
                new() { PreLoadingDetailId = detailId, SourceSalesOrderDetailId = sourceDetailId }
            }
        };

    /// <summary>播种受限制的装柜操作员（业务员映射 + 既有菜单授权），返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"preloading-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "预装柜操作员", RoleCode = $"PreLoadingOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return (user.Id, employee.Id);
    }

    private static void GrantMenus(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu
            {
                MenuCode = menuCode,
                MenuName = menuCode == PreLoadingSalesOrderLinkRules.RequiredMenuCode
                    ? PreLoadingSalesOrderLinkRules.RequiredMenuText
                    : PreLoadingSalesOrderLinkRules.SourceRequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 候选证据（有界只读） ====================

    [Fact]
    public async Task Candidates_return_bounded_evidence_and_exclude_unapproved()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-1", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-1", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 7m);
        var pendingOrder = SeedSalesOrder(db, "SO-368-1P", CustomerA, DocumentStatus.Pending);
        AddOrderDetail(db, pendingOrder.Id, ProductA, 7m);
        var cancelledOrder = SeedSalesOrder(db, "SO-368-1C", CustomerA, DocumentStatus.Cancelled);
        AddOrderDetail(db, cancelledOrder.Id, ProductA, 7m);
        var pre = SeedPreLoading(db, "YZ-368-1", booking.Id, DocumentStatus.Pending, (ProductA, 3m, null));

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));

        var candidate = Assert.Single(candidates);
        Assert.Equal(detail.Id, candidate.SalesOrderDetailId);
        Assert.Equal(order.Id, candidate.SalesOrderId);
        Assert.Equal("SO-368-1", candidate.OrderNo);
        Assert.Equal(ProductA, candidate.ProductId);
        Assert.Equal("商品A", candidate.ProductName);
        Assert.Equal("PCS", candidate.Unit);
        Assert.Equal(CustomerA, candidate.CustomerId);
        Assert.Equal(7m, candidate.OrderBaseQuantity);
        Assert.Equal(0m, candidate.LinkedPreLoadingBaseQuantity);
        Assert.Equal(7m, candidate.RemainingBaseQuantity);
    }

    [Fact]
    public async Task Candidates_only_expose_authoritative_booking_customer()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "自有客户");
        SeedCustomer(db, CustomerB, "他人客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-2", CustomerA);
        var ownOrder = SeedSalesOrder(db, "SO-368-2A", CustomerA, DocumentStatus.Approved);
        var ownDetail = AddOrderDetail(db, ownOrder.Id, ProductA, 5m);
        var foreignOrder = SeedSalesOrder(db, "SO-368-2B", CustomerB, DocumentStatus.Approved);
        AddOrderDetail(db, foreignOrder.Id, ProductA, 5m);
        var pre = SeedPreLoading(db, "YZ-368-2", booking.Id, DocumentStatus.Pending, (ProductA, 3m, null));

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));

        var candidate = Assert.Single(candidates);
        Assert.Equal(ownDetail.Id, candidate.SalesOrderDetailId);
        Assert.Equal(CustomerA, candidate.CustomerId);
    }

    [Fact]
    public async Task Candidates_deduct_linked_approved_preloading_capacity()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-3", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-3", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);

        // 其它已审核预装柜已显式链接占用 6（历史未链接行不计入）。
        SeedPreLoading(db, "YZ-368-3A", booking.Id, DocumentStatus.Approved, (ProductA, 6m, detail.Id));
        SeedPreLoading(db, "YZ-368-3B", booking.Id, DocumentStatus.Approved, (ProductA, 4m, null));
        var target = SeedPreLoading(db, "YZ-368-3C", booking.Id, DocumentStatus.Pending, (ProductA, 4m, null));

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(target.Id, null, 0));

        var candidate = Assert.Single(candidates);
        Assert.Equal(6m, candidate.LinkedPreLoadingBaseQuantity);
        Assert.Equal(4m, candidate.RemainingBaseQuantity);
    }

    // ==================== 链接指派（先校验后改写） ====================

    [Fact]
    public async Task Assign_persists_and_describes_links()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-4", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-4", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-4", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var result = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, detail.Id)));

        Assert.Equal(1, result.LinkedCount);
        Assert.Equal(0, result.ClearedCount);
        var line = Assert.Single(result.Items);
        Assert.Equal(detail.Id, line.SourceSalesOrderDetailId);
        Assert.Equal(order.Id, line.SalesOrderId);
        Assert.Equal("SO-368-4", line.OrderNo);
        Assert.Equal(detail.Id, (await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_clears_link_and_keeps_history()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-5", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-5", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-5", booking.Id, DocumentStatus.Pending,
            (ProductA, 6m, detail.Id));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var result = AssertOk<PreLoadingSalesOrderLinkAssignResultDto>(
            await NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, null)));

        Assert.Equal(0, result.LinkedCount);
        Assert.Equal(1, result.ClearedCount);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Cancelled)]
    public async Task Assign_rejects_non_approved_order_and_preserves(DocumentStatus status)
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-6", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-6", CustomerA, status);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-6", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, detail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_deleted_order_detail_and_preserves()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-7", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-7", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m, deleted: true);
        var pre = SeedPreLoading(db, "YZ-368-7", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, detail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_wrong_product_and_preserves()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        SeedProduct(db, ProductB, "商品B");
        var booking = SeedBooking(db, "DG-368-8", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-8", CustomerA, DocumentStatus.Approved);
        var wrongDetail = AddOrderDetail(db, order.Id, ProductB, 10m);
        var pre = SeedPreLoading(db, "YZ-368-8", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, wrongDetail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_customer_different_from_booking_and_preserves()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "订柜客户");
        SeedCustomer(db, CustomerB, "订单客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-9", CustomerA);
        var foreignOrder = SeedSalesOrder(db, "SO-368-9", CustomerB, DocumentStatus.Approved);
        var foreignDetail = AddOrderDetail(db, foreignOrder.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-9", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, foreignDetail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_non_positive_quantity_and_preserves()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-10", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-10", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-10", booking.Id, DocumentStatus.Pending, (ProductA, 0m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, detail.Id)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_incompatible_unit_and_preserves()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A", "PCS");
        var booking = SeedBooking(db, "DG-368-11", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-11", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m, unit: "CTN");
        var pre = SeedPreLoading(db, "YZ-368-11", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, detail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_unlinked_preloading_without_authoritative_customer()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var order = SeedSalesOrder(db, "SO-368-12", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-12", null, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, detail.Id)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Assign_rejects_invalid_source_id()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-13", CustomerA);
        var pre = SeedPreLoading(db, "YZ-368-13", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = await SingleDetailIdAsync(db, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).AssignSalesOrderLinks(pre.Id, Assign(detailId, 999999)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null((await ReloadAsync(db, pre.Id)).Details.Single().SourceSalesOrderDetailId);
    }

    // ==================== 审核：按来源订单明细逐条累计容量 ====================

    [Fact]
    public async Task Approve_allows_exact_capacity_and_keeps_evidence()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-14", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-14", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-14", booking.Id, DocumentStatus.Submitted,
            (ProductA, 6m, detail.Id));

        Assert.IsType<OkObjectResult>(await NewController(db, userId).Approve(pre.Id));

        var stored = await ReloadAsync(db, pre.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(detail.Id, stored.Details.Single().SourceSalesOrderDetailId);
        Assert.Empty(db.StockMovements);   // 审核不锁库 / 不过账
        Assert.Empty(db.FinanceExpenses);  // 审核不改财务
        Assert.Empty(db.StockOuts);        // 审核不生成出运 / 单证
    }

    [Fact]
    public async Task Approve_rejects_cumulative_overflow_and_preserves()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-15", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-15", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        SeedPreLoading(db, "YZ-368-15A", booking.Id, DocumentStatus.Approved, (ProductA, 6m, detail.Id));
        var overflow = SeedPreLoading(db, "YZ-368-15B", booking.Id, DocumentStatus.Submitted,
            (ProductA, 5m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Approve(overflow.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        var stored = await ReloadAsync(db, overflow.Id);
        Assert.Equal(DocumentStatus.Submitted, stored.Status);
        Assert.Equal(5m, stored.Details.Single().Quantity);
        Assert.Equal(detail.Id, stored.Details.Single().SourceSalesOrderDetailId);
    }

    [Fact]
    public async Task Approve_aggregates_duplicate_lines_against_same_source_detail()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-16", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-16", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);

        // 同一单据两条同来源明细行（4 + 4 = 8 ≤ 10）先聚合再审核 → 通过。
        var exact = SeedPreLoading(db, "YZ-368-16A", booking.Id, DocumentStatus.Submitted,
            (ProductA, 4m, detail.Id), (ProductA, 4m, detail.Id));
        Assert.IsType<OkObjectResult>(await NewController(db, userId).Approve(exact.Id));
        Assert.Equal(DocumentStatus.Approved, (await ReloadAsync(db, exact.Id)).Status);

        // 再 3 → 累计 11 > 10：拒绝且明细 / 状态 / 历史保持不变。
        var overflow = SeedPreLoading(db, "YZ-368-16B", booking.Id, DocumentStatus.Submitted,
            (ProductA, 3m, detail.Id));
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Approve(overflow.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Submitted, (await ReloadAsync(db, overflow.Id)).Status);
    }

    [Fact]
    public async Task Cancelled_preloading_releases_capacity_but_keeps_history()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-17", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-17", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var cancelled = SeedPreLoading(db, "YZ-368-17A", booking.Id, DocumentStatus.Cancelled,
            (ProductA, 8m, detail.Id));
        var target = SeedPreLoading(db, "YZ-368-17B", booking.Id, DocumentStatus.Submitted,
            (ProductA, 9m, detail.Id));

        Assert.IsType<OkObjectResult>(await NewController(db, userId).Approve(target.Id));

        // 已取消单据的链接与历史数量原样保留，仅因状态不再「已审核」而释放规划容量。
        var history = await ReloadAsync(db, cancelled.Id);
        Assert.Equal(DocumentStatus.Cancelled, history.Status);
        Assert.Equal(8m, history.Details.Single().Quantity);
        Assert.Equal(detail.Id, history.Details.Single().SourceSalesOrderDetailId);
    }

    // ==================== 授权（既有菜单 / 客户数据范围 / 边界文案） ====================

    [Fact]
    public async Task Candidates_require_existing_sales_order_menu_for_restricted_operator()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingSalesOrderLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-18", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-18", CustomerA, DocumentStatus.Approved);
        AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-18", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(PreLoadingSalesOrderLinkRules.SourceRequiredMenuText, ex.Message);
    }

    [Fact]
    public async Task Candidates_allow_restricted_operator_with_both_menus_on_own_customer()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db,
            PreLoadingSalesOrderLinkRules.RequiredMenuCode, PreLoadingSalesOrderLinkRules.SourceRequiredMenuCode);
        SeedCustomer(db, CustomerA, "自有客户", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-19", CustomerA);
        var order = SeedSalesOrder(db, "SO-368-19", CustomerA, DocumentStatus.Approved);
        var detail = AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-19", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));

        var candidates = AssertOk<List<PreLoadingSalesOrderCandidateDto>>(
            await NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));

        var candidate = Assert.Single(candidates);
        Assert.Equal(detail.Id, candidate.SalesOrderDetailId);
        Assert.Equal(CustomerA, candidate.CustomerId);
    }

    [Fact]
    public async Task Candidates_reject_foreign_customer_scope_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedRestrictedOperator(db,
            PreLoadingSalesOrderLinkRules.RequiredMenuCode, PreLoadingSalesOrderLinkRules.SourceRequiredMenuCode);
        SeedCustomer(db, CustomerB, "他人客户");
        SeedProduct(db, ProductA, "商品A");
        var booking = SeedBooking(db, "DG-368-20", CustomerB);
        var order = SeedSalesOrder(db, "SO-368-20", CustomerB, DocumentStatus.Approved);
        AddOrderDetail(db, order.Id, ProductA, 10m);
        var pre = SeedPreLoading(db, "YZ-368-20", booking.Id, DocumentStatus.Pending, (ProductA, 6m, null));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).GetSalesOrderCandidates(pre.Id, null, 0));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public void Controller_authorization_inherits_base_only()
    {
        var controllerType = typeof(ContainerPreLoadingController);
        Assert.NotNull(controllerType.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    [Fact]
    public void RuleText_states_demand_planning_evidence_and_no_guessing()
    {
        Assert.False(string.IsNullOrWhiteSpace(PreLoadingSalesOrderLinkRules.RuleText));
        Assert.Contains("绝不回填", PreLoadingSalesOrderLinkRules.RuleText);
        Assert.Contains("未链接", PreLoadingSalesOrderLinkRules.RuleText);
        Assert.Contains("已取消", PreLoadingSalesOrderLinkRules.RuleText);
        Assert.Contains("不改财务", PreLoadingSalesOrderLinkRules.RuleText);

        Assert.Contains("不是库存预留", PreLoadingSalesOrderLinkRules.DemandPlanningText);
        Assert.Contains("不是出运凭证", PreLoadingSalesOrderLinkRules.DemandPlanningText);
        Assert.Contains("只新增一个可空证据列", PreLoadingSalesOrderLinkRules.BoundaryText);
    }

    [Fact]
    public void IsBaseUnitCompatible_matches_base_or_empty_only()
    {
        var product = new BaseProduct { Unit = "PCS" };
        Assert.True(PreLoadingSalesOrderLinkRules.IsBaseUnitCompatible(product, "PCS"));
        Assert.True(PreLoadingSalesOrderLinkRules.IsBaseUnitCompatible(product, ""));
        Assert.False(PreLoadingSalesOrderLinkRules.IsBaseUnitCompatible(product, "CTN"));
        Assert.False(PreLoadingSalesOrderLinkRules.IsBaseUnitCompatible(new BaseProduct { Unit = "" }, "PCS"));
    }

    [Fact]
    public void LockOrder_note_declares_sales_order_then_booking_then_preloading()
    {
        Assert.Contains("销售订单", PreLoadingSalesOrderLinkRules.LockOrderNote);
        Assert.Contains("订柜信息", PreLoadingSalesOrderLinkRules.LockOrderNote);
        Assert.Contains("预装柜单行", PreLoadingSalesOrderLinkRules.LockOrderNote);
        Assert.Contains("SalesOrders", PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql);
        Assert.Contains("UPDLOCK", PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql);
    }
}
