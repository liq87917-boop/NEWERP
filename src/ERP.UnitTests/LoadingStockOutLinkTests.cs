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
/// ERP-366 装柜明细 → 显式已审核销售出库证据链接 与 累计容量护栏单元测试。
/// <para>覆盖：候选证据有界查询（返回父出库单 / 销售订单 / 商品 / 客户 / 剩余基础单位数量、
/// 排除未审核 / 已删除 / 范围外客户、扣减已生效退货与已链接占用）、链接校验（非正 / 不存在 / 已删除 /
/// 未审核 / 商品不一致 / 单位不兼容 / 客户不在权威范围 / 数量非正）、历史未链接明细保持显式未链接、
/// 审核累计容量（重复行与重复来源按「出库单 + 商品」保守聚合、退货扣减、历史未链接不计入）、
/// 失败保留明细与状态、审核不二次过账库存 / 财务、菜单与客户范围 fail closed。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class LoadingStockOutLinkTests
{
    private const long CustomerA = 966001L;
    private const long CustomerB = 966002L;
    private const long ProductA = 966101L;
    private const long ProductB = 966102L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerLoadingListController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static void SeedProduct(ErpDbContext db, long id, string name, string unit = "PCS",
        string packageUnit = "", int unitsPerPackage = 0)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = name, Spec = "规格A",
            Unit = unit, PackageUnit = packageUnit, UnitsPerPackage = unitsPerPackage
        });
        db.SaveChanges();
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string no, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status,
        long? salesOrderId = null, bool deleted = false)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no, StockOutDate = DateTime.Today, CustomerId = customerId,
            Status = status, SalesOrderId = salesOrderId, IsDeleted = deleted
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail AddStockOutDetail(ErpDbContext db, long stockOutId, long productId,
        decimal quantity, string unit = "PCS", bool deleted = false, string? productName = null)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId, ProductId = productId,
            ProductName = productName ?? $"商品{productId}",
            Unit = unit, Quantity = quantity, IsDeleted = deleted
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, CustomerId = customerId, Status = status,
            Remark = "ERP-366_TEST"
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceStockOutDetailId = sourceDetailId
            });
        }
        db.SaveChanges();
        return list;
    }

    private static SalesReturn SeedSalesReturn(ErpDbContext db, long sourceStockOutId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity, string Unit)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-{Guid.NewGuid():N}", ReturnDate = DateTime.Today, CustomerId = customerId,
            SourceStockOutId = sourceStockOutId, Status = status
        };
        db.SalesReturns.Add(ret);
        db.SaveChanges();
        foreach (var (productId, quantity, unit) in lines)
        {
            db.SalesReturnDetails.Add(new SalesReturnDetail
            {
                SalesReturnId = ret.Id, ReturnNo = ret.ReturnNo, ProductId = productId,
                ProductName = $"商品{productId}", Unit = unit, Quantity = quantity
            });
        }
        db.SaveChanges();
        return ret;
    }

    /// <summary>播种受限（非特权）操作员：业务员映射 + 指定既有菜单授权，返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"loading-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "装柜链接操作员", RoleCode = $"LoadingLink-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuCode = menuCode, MenuName = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return (user.Id, employee.Id);
    }

    private static ContainerLoadingList NewLoading(long customerId,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
        => new()
        {
            LoadingDate = DateTime.Today,
            CustomerId = customerId,
            Details = lines.Select(l => new ContainerLoadingDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Quantity = l.Quantity,
                SourceStockOutDetailId = l.SourceDetailId
            }).ToList()
        };

    private static ContainerLoadingList Reload(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return db.ContainerLoadingLists.AsNoTracking().Include(o => o.Details).Single(o => o.Id == id);
    }

    // ==================== 候选证据查询 ====================

    [Fact]
    public async Task Candidate_returns_explicit_evidence()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var order = SeedSalesOrder(db, "SO-CAND-1", CustomerA);
        var stockOut = SeedStockOut(db, "CK-CAND-1", CustomerA, DocumentStatus.Approved, order.Id);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-CAND-1", CustomerA, DocumentStatus.Pending);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var candidate = Assert.Single(
            await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));

        Assert.Equal(detail.Id, candidate.StockOutDetailId);
        Assert.Equal(stockOut.Id, candidate.StockOutId);
        Assert.Equal("CK-CAND-1", candidate.StockOutNo);
        Assert.Equal(order.Id, candidate.SalesOrderId);
        Assert.Equal("SO-CAND-1", candidate.SalesOrderNo);
        Assert.Equal(ProductA, candidate.ProductId);
        Assert.Equal("商品A", candidate.ProductName);
        Assert.Equal("PCS", candidate.Unit);
        Assert.Equal(CustomerA, candidate.CustomerId);
        Assert.Equal("客户A", candidate.CustomerName);
        Assert.Equal(10m, candidate.SourceBaseQuantity);
        Assert.Equal(0m, candidate.EffectiveReturnedBaseQuantity);
        Assert.Equal(0m, candidate.LinkedLoadingBaseQuantity);
        Assert.Equal(10m, candidate.RemainingBaseQuantity);
    }

    [Fact]
    public async Task Candidate_excludes_unapproved_deleted_and_foreign_customer()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedCustomer(db, CustomerB, "客户B");
        SeedProduct(db, ProductA, "商品A");

        var pending = SeedStockOut(db, "CK-CAND-P", CustomerA, DocumentStatus.Pending);
        AddStockOutDetail(db, pending.Id, ProductA, 10m);
        var deletedHeader = SeedStockOut(db, "CK-CAND-D", CustomerA, DocumentStatus.Approved, null, deleted: true);
        AddStockOutDetail(db, deletedHeader.Id, ProductA, 10m);
        var deletedDetail = SeedStockOut(db, "CK-CAND-DD", CustomerA, DocumentStatus.Approved);
        AddStockOutDetail(db, deletedDetail.Id, ProductA, 10m, deleted: true);
        var foreign = SeedStockOut(db, "CK-CAND-F", CustomerB, DocumentStatus.Approved);
        AddStockOutDetail(db, foreign.Id, ProductA, 10m);

        var list = SeedLoadingList(db, "ZQ-CAND-2", CustomerA, DocumentStatus.Pending);
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        Assert.Empty(await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));
    }

    [Fact]
    public async Task Candidate_remaining_subtracts_returns_and_linked_loadings()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-CAND-3", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        SeedSalesReturn(db, stockOut.Id, CustomerA, DocumentStatus.Approved, (ProductA, 3m, "PCS"));
        SeedLoadingList(db, "ZQ-CAND-3-A", CustomerA, DocumentStatus.Approved, (ProductA, 2m, detail.Id));
        var list = SeedLoadingList(db, "ZQ-CAND-3-B", CustomerA, DocumentStatus.Pending);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var candidate = Assert.Single(
            await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));

        Assert.Equal(10m, candidate.SourceBaseQuantity);
        Assert.Equal(3m, candidate.EffectiveReturnedBaseQuantity);
        Assert.Equal(2m, candidate.LinkedLoadingBaseQuantity);
        Assert.Equal(5m, candidate.RemainingBaseQuantity);
    }

    [Fact]
    public async Task Candidate_keyword_filters_by_number_or_product()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品甲");
        SeedProduct(db, ProductB, "商品乙");
        var first = SeedStockOut(db, "CK-KEY-A", CustomerA, DocumentStatus.Approved);
        AddStockOutDetail(db, first.Id, ProductA, 4m, productName: "商品甲");
        var second = SeedStockOut(db, "CK-KEY-B", CustomerA, DocumentStatus.Approved);
        AddStockOutDetail(db, second.Id, ProductB, 4m, productName: "商品乙");
        var list = SeedLoadingList(db, "ZQ-KEY", CustomerA, DocumentStatus.Pending);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var byNumber = Assert.Single(
            await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, "KEY-B", 0));
        Assert.Equal(ProductB, byNumber.ProductId);

        var byName = Assert.Single(
            await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, "商品甲", 0));
        Assert.Equal(ProductA, byName.ProductId);
    }

    [Fact]
    public async Task Candidate_filters_by_zero_remaining_capacity()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-CAND-4", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 5m);
        SeedSalesReturn(db, stockOut.Id, CustomerA, DocumentStatus.Approved, (ProductA, 5m, "PCS"));
        SeedLoadingList(db, "ZQ-CAND-4-OCC", CustomerA, DocumentStatus.Approved, (ProductA, 5m, detail.Id));
        var list = SeedLoadingList(db, "ZQ-CAND-4", CustomerA, DocumentStatus.Pending);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);

        Assert.Empty(await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));
    }

    [Fact]
    public async Task Candidate_uses_active_participants_for_mixed_customer()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedCustomer(db, CustomerB, "客户B");
        SeedProduct(db, ProductA, "商品A");
        var stockOutB = SeedStockOut(db, "CK-MIX-B", CustomerB, DocumentStatus.Approved);
        var detailB = AddStockOutDetail(db, stockOutB.Id, ProductA, 8m);
        var list = SeedLoadingList(db, "ZQ-MIX", CustomerA, DocumentStatus.Pending);
        db.ContainerLoadingListParticipants.Add(new ContainerLoadingListParticipant
        {
            LoadingListId = list.Id, CustomerId = CustomerB, CustomerCode = "C-B", CustomerName = "客户B",
            IsPrimary = false, Status = 1
        });
        db.SaveChanges();

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var candidate = Assert.Single(
            await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));

        Assert.Equal(detailB.Id, candidate.StockOutDetailId);
        Assert.Equal(CustomerB, candidate.CustomerId);
    }

    [Fact]
    public async Task Candidate_restricted_operator_requires_every_participant_in_scope()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "客户A", employeeId);
        SeedCustomer(db, CustomerB, "客户B");
        SeedProduct(db, ProductA, "商品A");
        var list = SeedLoadingList(db, "ZQ-SCOPE", CustomerA, DocumentStatus.Pending);
        db.ContainerLoadingListParticipants.Add(new ContainerLoadingListParticipant
        {
            LoadingListId = list.Id, CustomerId = CustomerB, CustomerCode = "C-B", CustomerName = "客户B",
            IsPrimary = false, Status = 1
        });
        db.SaveChanges();

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Candidate_restricted_operator_stays_within_assigned_customers()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "客户A", employeeId);
        SeedCustomer(db, CustomerB, "客户B");
        SeedProduct(db, ProductA, "商品A");
        var ownStockOut = SeedStockOut(db, "CK-SCOPE-OWN", CustomerA, DocumentStatus.Approved);
        var ownDetail = AddStockOutDetail(db, ownStockOut.Id, ProductA, 4m);
        var foreignStockOut = SeedStockOut(db, "CK-SCOPE-OTHER", CustomerB, DocumentStatus.Approved);
        AddStockOutDetail(db, foreignStockOut.Id, ProductA, 4m);
        var list = SeedLoadingList(db, "ZQ-SCOPE-OWN", CustomerA, DocumentStatus.Pending);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        var candidate = Assert.Single(
            await LoadingStockOutLinkRules.QueryCandidatesAsync(db, list, scope, null, 0));

        Assert.Equal(ownDetail.Id, candidate.StockOutDetailId);
        Assert.Equal(CustomerA, candidate.CustomerId);
    }

    [Fact]
    public async Task Source_menu_required_for_restricted_operator()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.EnsureSourceMenuAuthorizedAsync(db, userId));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(LoadingStockOutLinkRules.SourceRequiredMenuText, ex.Message);
    }

    [Fact]
    public async Task Source_menu_granted_allows_access()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedRestrictedOperator(db,
            LoadingStockOutLinkRules.RequiredMenuCode, LoadingStockOutLinkRules.SourceRequiredMenuCode);

        await LoadingStockOutLinkRules.EnsureSourceMenuAuthorizedAsync(db, userId);
    }

    // ==================== 链接校验（保存 / 提交前） ====================

    [Fact]
    public async Task ValidateLinks_null_source_is_explicitly_unlinked_without_source_menu()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "客户A", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var entity = NewLoading(CustomerA, (ProductA, 6m, null));

        // 未链接（null）保持历史行为：无需「销售出库」菜单授权，也不做来源语义判定。
        await LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId);
    }

    [Fact]
    public async Task ValidateLinks_non_positive_source_id_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var entity = NewLoading(CustomerA, (ProductA, 6m, 0L));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ValidateLinks_valid_link_accepted()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-VL-OK", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        await LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId);
    }

    [Fact]
    public async Task ValidateLinks_source_not_approved_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-VL-P", CustomerA, DocumentStatus.Pending);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("未审核", ex.Message);
    }

    [Fact]
    public async Task ValidateLinks_source_cancelled_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-VL-C", CustomerA, DocumentStatus.Cancelled);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task ValidateLinks_source_deleted_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-VL-D", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m, deleted: true);
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("删除", ex.Message);
    }

    [Fact]
    public async Task ValidateLinks_source_missing_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var entity = NewLoading(CustomerA, (ProductA, 6m, 987654321L));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public async Task ValidateLinks_wrong_product_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        SeedProduct(db, ProductB, "商品B");
        var stockOut = SeedStockOut(db, "CK-VL-PR", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductB, 10m);
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("商品不一致", ex.Message);
    }

    [Fact]
    public async Task ValidateLinks_unit_incompatible_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A", unit: "PCS");
        var stockOut = SeedStockOut(db, "CK-VL-U", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m, unit: "CTN");
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("基础单位", ex.Message);
    }

    [Fact]
    public async Task ValidateLinks_out_of_membership_customer_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedCustomer(db, CustomerB, "客户B");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-VL-CUST", CustomerB, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var entity = NewLoading(CustomerA, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("权威客户范围", ex.Message);
    }

    [Fact]
    public async Task ValidateLinks_non_positive_quantity_rejected()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-VL-Q", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var entity = NewLoading(CustomerA, (ProductA, 0m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateLinksAsync(db, entity, userId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task DescribeLinks_marks_null_as_explicitly_unlinked()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        SeedProduct(db, ProductB, "商品B");
        var stockOut = SeedStockOut(db, "CK-DESC", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-DESC", CustomerA, DocumentStatus.Pending,
            (ProductA, 4m, detail.Id), (ProductB, 3m, null));

        var lines = await LoadingStockOutLinkRules.DescribeLinksAsync(db, Reload(db, list.Id));

        Assert.Equal(2, lines.Count);
        var linked = Assert.Single(lines.Where(l => l.SourceStockOutDetailId == detail.Id));
        Assert.Equal(stockOut.Id, linked.StockOutId);
        Assert.Equal("CK-DESC", linked.StockOutNo);
        var unlinked = Assert.Single(lines.Where(l => l.SourceStockOutDetailId is null));
        Assert.Null(unlinked.StockOutId);
        Assert.Equal(string.Empty, unlinked.StockOutNo);
    }

    // ==================== 审核累计容量（原子性 / 并发语义） ====================

    [Fact]
    public async Task Approve_capacity_overflow_rejected_preserves_details_and_status()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-APP-1", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        SeedLoadingList(db, "ZQ-APP-1-A", CustomerA, DocumentStatus.Approved, (ProductA, 6m, detail.Id));
        var second = SeedLoadingList(db, "ZQ-APP-1-B", CustomerA, DocumentStatus.Submitted, (ProductA, 5m, detail.Id));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("可装柜容量", ex.Message);
        var reloaded = Reload(db, second.Id);
        Assert.Equal(DocumentStatus.Submitted, reloaded.Status);
        Assert.Equal(detail.Id, reloaded.Details.Single().SourceStockOutDetailId);
        Assert.Equal(5m, reloaded.Details.Single().Quantity);
    }

    [Fact]
    public async Task Approve_unlinked_legacy_quantity_is_not_counted_as_shipped()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-APP-2", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        // 历史未链接（null）明细数量很大，但绝不能占用「已证明出运」容量。
        SeedLoadingList(db, "ZQ-APP-2-LEGACY", CustomerA, DocumentStatus.Approved, (ProductA, 100m, null));
        var second = SeedLoadingList(db, "ZQ-APP-2-B", CustomerA, DocumentStatus.Submitted, (ProductA, 10m, detail.Id));
        var ctl = NewController(db, userId);

        await ctl.Approve(second.Id);

        Assert.Equal(DocumentStatus.Approved, Reload(db, second.Id).Status);
    }

    [Fact]
    public async Task Approve_returns_reduce_capacity()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-APP-3", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        SeedSalesReturn(db, stockOut.Id, CustomerA, DocumentStatus.Approved, (ProductA, 4m, "PCS"));
        var exact = SeedLoadingList(db, "ZQ-APP-3-OK", CustomerA, DocumentStatus.Submitted, (ProductA, 6m, detail.Id));
        var overflow = SeedLoadingList(db, "ZQ-APP-3-NO", CustomerA, DocumentStatus.Submitted, (ProductA, 1m, detail.Id));
        var ctl = NewController(db, userId);

        await ctl.Approve(exact.Id);
        Assert.Equal(DocumentStatus.Approved, Reload(db, exact.Id).Status);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(overflow.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, overflow.Id).Status);
    }

    [Fact]
    public async Task Approve_duplicate_lines_and_source_details_aggregate_per_product()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-APP-4", CustomerA, DocumentStatus.Approved);
        var firstDetail = AddStockOutDetail(db, stockOut.Id, ProductA, 5m);
        var secondDetail = AddStockOutDetail(db, stockOut.Id, ProductA, 5m);
        // 同一装柜清单两条同商品行（4 + 4 = 8 ≤ 10）分别链接到两条来源明细：先聚合再判定。
        var first = SeedLoadingList(db, "ZQ-APP-4-A", CustomerA, DocumentStatus.Submitted,
            (ProductA, 4m, firstDetail.Id), (ProductA, 4m, secondDetail.Id));
        var second = SeedLoadingList(db, "ZQ-APP-4-B", CustomerA, DocumentStatus.Submitted, (ProductA, 3m, firstDetail.Id));
        var ctl = NewController(db, userId);

        await ctl.Approve(first.Id);
        Assert.Equal(DocumentStatus.Approved, Reload(db, first.Id).Status);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, second.Id).Status);
    }

    [Fact]
    public async Task Approve_does_not_post_stock_or_touch_finance()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-APP-5", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-APP-5", CustomerA, DocumentStatus.Submitted, (ProductA, 6m, detail.Id));
        var ctl = NewController(db, userId);

        await ctl.Approve(list.Id);

        Assert.Equal(DocumentStatus.Approved, Reload(db, list.Id).Status);
        // 装柜审核不得二次过账库存，也不得生成任何库存流水 / 财务记录。
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
        Assert.Equal(DocumentStatus.Approved, db.StockOuts.Single().Status);
        Assert.Single(db.StockOutDetails);
    }

    [Fact]
    public async Task ValidateApproval_requires_source_menu_for_restricted_operator()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "客户A", employeeId);
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-APP-MENU", CustomerA, DocumentStatus.Approved);
        var detail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-APP-MENU", CustomerA, DocumentStatus.Submitted, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateApprovalAsync(db, Reload(db, list.Id), userId));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(LoadingStockOutLinkRules.SourceRequiredMenuText, ex.Message);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, list.Id).Status);
    }

    // ==================== 链接指派 API（POST /stock-out-links） ====================

    private static long SingleDetailId(ErpDbContext db, long loadingListId)
        => db.ContainerLoadingDetails.AsNoTracking().Where(d => d.LoadingListId == loadingListId)
            .OrderBy(d => d.Id).First().Id;

    private static LoadingStockOutLinkAssignRequest Assign(long loadingDetailId, long? sourceDetailId)
        => new()
        {
            Links = new List<LoadingStockOutLinkAssignmentDto>
            {
                new() { LoadingDetailId = loadingDetailId, SourceStockOutDetailId = sourceDetailId }
            }
        };

    [Fact]
    public async Task AssignStockOutLinks_assigns_and_describes_evidence()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-ASG-1", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-ASG-1", CustomerA, DocumentStatus.Pending, (ProductA, 6m, null));
        var detailId = SingleDetailId(db, list.Id);
        var ctl = NewController(db, userId);

        var result = await ctl.AssignStockOutLinks(list.Id, Assign(detailId, sourceDetail.Id));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<LoadingStockOutLinkAssignResultDto>>(ok.Value);
        Assert.NotNull(payload.Data);
        Assert.Equal(1, payload.Data!.LinkedCount);
        Assert.Equal(0, payload.Data.ClearedCount);
        var line = Assert.Single(payload.Data.Items);
        Assert.Equal(sourceDetail.Id, line.SourceStockOutDetailId);
        Assert.Equal(stockOut.Id, line.StockOutId);
        Assert.Equal("CK-ASG-1", line.StockOutNo);
        Assert.Equal(sourceDetail.Id, Reload(db, list.Id).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task AssignStockOutLinks_rejected_proposal_preserves_existing_links()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        SeedProduct(db, ProductB, "商品B");
        var goodStockOut = SeedStockOut(db, "CK-ASG-2-GOOD", CustomerA, DocumentStatus.Approved);
        var goodDetail = AddStockOutDetail(db, goodStockOut.Id, ProductA, 10m);
        var badStockOut = SeedStockOut(db, "CK-ASG-2-BAD", CustomerA, DocumentStatus.Approved);
        var badDetail = AddStockOutDetail(db, badStockOut.Id, ProductB, 10m);
        var list = SeedLoadingList(db, "ZQ-ASG-2", CustomerA, DocumentStatus.Pending, (ProductA, 6m, goodDetail.Id));
        var detailId = SingleDetailId(db, list.Id);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.AssignStockOutLinks(list.Id, Assign(detailId, badDetail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(goodDetail.Id, Reload(db, list.Id).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task AssignStockOutLinks_rejects_non_pending_list()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-ASG-3", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-ASG-3", CustomerA, DocumentStatus.Submitted, (ProductA, 6m, null));
        var detailId = SingleDetailId(db, list.Id);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.AssignStockOutLinks(list.Id, Assign(detailId, sourceDetail.Id)));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Null(Reload(db, list.Id).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task AssignStockOutLinks_clears_link()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA, "商品A");
        var stockOut = SeedStockOut(db, "CK-ASG-4", CustomerA, DocumentStatus.Approved);
        var sourceDetail = AddStockOutDetail(db, stockOut.Id, ProductA, 10m);
        var list = SeedLoadingList(db, "ZQ-ASG-4", CustomerA, DocumentStatus.Pending, (ProductA, 6m, sourceDetail.Id));
        var detailId = SingleDetailId(db, list.Id);
        var ctl = NewController(db, userId);

        var result = await ctl.AssignStockOutLinks(list.Id, Assign(detailId, null));

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<LoadingStockOutLinkAssignResultDto>>(ok.Value);
        Assert.Equal(0, payload.Data!.LinkedCount);
        Assert.Equal(1, payload.Data.ClearedCount);
        Assert.Null(Reload(db, list.Id).Details.Single().SourceStockOutDetailId);
    }

    // ==================== 边界与授权 ====================

    [Fact]
    public void Controller_authorization_inherits_base_only()
    {
        var controllerType = typeof(ContainerLoadingListController);
        Assert.NotNull(controllerType.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    [Fact]
    public void RuleText_never_guesses_and_rejects_legacy_unlinked_counting()
    {
        Assert.False(string.IsNullOrWhiteSpace(LoadingStockOutLinkRules.RuleText));
        Assert.Contains("绝不回填", LoadingStockOutLinkRules.RuleText);
        Assert.Contains("未链接", LoadingStockOutLinkRules.RuleText);
        Assert.Contains("保守聚合", LoadingStockOutLinkRules.RuleText);
        Assert.Contains("不改财务", LoadingStockOutLinkRules.RuleText);
    }

    [Fact]
    public void IsBaseUnitCompatible_matches_base_or_empty_only()
    {
        var product = new BaseProduct { Unit = "PCS", PackageUnit = "CTN", UnitsPerPackage = 10 };
        Assert.True(LoadingStockOutLinkRules.IsBaseUnitCompatible(product, "PCS"));
        Assert.True(LoadingStockOutLinkRules.IsBaseUnitCompatible(product, ""));
        Assert.False(LoadingStockOutLinkRules.IsBaseUnitCompatible(product, "CTN"));
        Assert.False(LoadingStockOutLinkRules.IsBaseUnitCompatible(new BaseProduct { Unit = "" }, "PCS"));
    }

    [Fact]
    public void LockOrder_note_declares_stock_out_before_loading()
    {
        Assert.Contains("销售出库", LoadingStockOutLinkRules.LockOrderNote);
        Assert.Contains("装柜清单行", LoadingStockOutLinkRules.LockOrderNote);
        Assert.Contains("StockOuts", LoadingStockOutLinkRules.LockStockOutRowSql);
        Assert.Contains("UPDLOCK", LoadingStockOutLinkRules.LockStockOutRowSql);
    }

}
