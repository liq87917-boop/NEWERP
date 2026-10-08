using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-348 装柜清单衔接已审核预装柜单并防止累计超装单元测试。
/// <para>覆盖：关联已审核来源成功、未关联历史单据保持行为、非正 / 不存在 / 未审核 / 已删除来源拒绝、
/// 缺失商品 / 商品不在来源 / 数量非正 / 箱数·毛重·体积为负拒绝、重复商品行与重复来源行聚合、
/// 部分 / 满量批次累计、累计超限拒绝且状态不变、取消释放额度、来源取消护栏、
/// 无身份 / 无菜单 / 越客户范围拒绝，以及控制器授权仅继承基类。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class ContainerLoadingFulfillmentTests
{
    private const long CustomerA = 942001L;
    private const long CustomerB = 942002L;
    private const long ProductA = 942101L;
    private const long ProductB = 942102L;

    private static ContainerLoadingListController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string code, string name, long? empId = null)
    {
        db.BaseCustomers.Add(new BaseCustomer { Id = id, CustomerCode = code, CustomerName = name, EmpId = empId });
        db.SaveChanges();
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name, string unit)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, DocumentStatus status,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var pre = new ContainerPreLoading { PreLoadingNo = no, LoadingDate = DateTime.Today, Status = status };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Quantity = quantity
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static ContainerLoadingList SeedLoading(ErpDbContext db, string no, long? preLoadingId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity, decimal Cartons, decimal Weight, decimal Volume)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, PreLoadingId = preLoadingId, LoadingDate = DateTime.Today,
            CustomerId = customerId, Status = status
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        foreach (var (productId, quantity, cartons, weight, volume) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Quantity = quantity,
                Cartons = cartons,
                Weight = weight,
                Volume = volume
            });
        }
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingList NewLoading(long customerId, long? preLoadingId,
        params (long ProductId, decimal Quantity, decimal Cartons, decimal Weight, decimal Volume)[] lines)
        => new()
        {
            LoadingDate = DateTime.Today,
            PreLoadingId = preLoadingId,
            CustomerId = customerId,
            Remark = "ERP-348_TEST",
            Details = lines.Select(l => new ContainerLoadingDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Quantity = l.Quantity,
                Cartons = l.Cartons,
                Weight = l.Weight,
                Volume = l.Volume
            }).ToList()
        };

    // ==================== 链接权威性 ====================

    [Fact]
    public async Task Create_关联已审核来源_保存成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-1", "客户A");
        SeedProduct(db, ProductA, "P-FUL-1", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-FUL-1", DocumentStatus.Approved, (ProductA, 10m));
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewLoading(CustomerA, pre.Id, (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.ContainerLoadingLists.Single();
        Assert.Equal(pre.Id, saved.PreLoadingId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task Create_未关联预装柜单_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-2", "客户A");
        SeedProduct(db, ProductA, "P-FUL-2", "商品A", "PCS");
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewLoading(CustomerA, null, (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.ContainerLoadingLists.Single().PreLoadingId);
    }

    [Fact]
    public async Task Create_非正来源Id_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-3", "客户A");
        SeedProduct(db, ProductA, "P-FUL-3", "商品A", "PCS");
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, 0L, (ProductA, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ContainerLoadingLists);
    }

    [Fact]
    public async Task Create_来源不存在或已删除_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-4", "客户A");
        SeedProduct(db, ProductA, "P-FUL-4", "商品A", "PCS");
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, 999999L, (ProductA, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ContainerLoadingLists);
    }

    [Fact]
    public async Task Create_来源未审核_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-5", "客户A");
        SeedProduct(db, ProductA, "P-FUL-5", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-FUL-5", DocumentStatus.Pending, (ProductA, 10m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, pre.Id, (ProductA, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ContainerLoadingLists);
    }

    [Fact]
    public async Task Create_商品不存在_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-6", "客户A");
        SeedProduct(db, ProductA, "P-FUL-6", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-FUL-6", DocumentStatus.Approved, (ProductA, 10m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, pre.Id, (999999L, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ContainerLoadingLists);
    }

    [Fact]
    public async Task Create_商品不在来源_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-7", "客户A");
        SeedProduct(db, ProductA, "P-FUL-7", "商品A", "PCS");
        SeedProduct(db, ProductB, "P-FUL-7B", "商品B", "PCS");
        var pre = SeedPreLoading(db, "YZ-FUL-7", DocumentStatus.Approved, (ProductA, 10m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, pre.Id, (ProductB, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("无商品", ex.Message);
        Assert.Empty(db.ContainerLoadingLists);
    }

    [Fact]
    public async Task Create_数量非正_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-8", "客户A");
        SeedProduct(db, ProductA, "P-FUL-8", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-FUL-8", DocumentStatus.Approved, (ProductA, 10m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, pre.Id, (ProductA, 0m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ContainerLoadingLists);
    }

    [Fact]
    public async Task Create_箱数或毛重或体积为负_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-FUL-9", "客户A");
        SeedProduct(db, ProductA, "P-FUL-9", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-FUL-9", DocumentStatus.Approved, (ProductA, 10m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewLoading(CustomerA, pre.Id, (ProductA, 6m, -1m, 0m, 0m))));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ContainerLoadingLists);
    }


    // ==================== 累计上限与取消释放 ====================

    [Fact]
    public async Task Approve_部分后补足_满量通过()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CUM-1", "客户A");
        SeedProduct(db, ProductA, "P-CUM-1", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-CUM-1", DocumentStatus.Approved, (ProductA, 10m));
        SeedLoading(db, "ZQ-CUM-A", pre.Id, CustomerA, DocumentStatus.Approved, (ProductA, 6m, 0m, 0m, 0m));
        var second = SeedLoading(db, "ZQ-CUM-B", pre.Id, CustomerA, DocumentStatus.Submitted, (ProductA, 4m, 0m, 0m, 0m));
        var ctl = NewController(db, userId);

        await ctl.Approve(second.Id);

        Assert.Equal(DocumentStatus.Approved, db.ContainerLoadingLists.Single(l => l.Id == second.Id).Status);
    }

    [Fact]
    public async Task Approve_累计超限_拒绝且来源目标状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CUM-2", "客户A");
        SeedProduct(db, ProductA, "P-CUM-2", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-CUM-2", DocumentStatus.Approved, (ProductA, 10m));
        var first = SeedLoading(db, "ZQ-CUM-A2", pre.Id, CustomerA, DocumentStatus.Approved, (ProductA, 6m, 0m, 0m, 0m));
        var second = SeedLoading(db, "ZQ-CUM-B2", pre.Id, CustomerA, DocumentStatus.Submitted, (ProductA, 5m, 0m, 0m, 0m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过预装柜单授权数量", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, db.ContainerLoadingLists.Single(l => l.Id == second.Id).Status);
        Assert.Equal(DocumentStatus.Approved, db.ContainerLoadingLists.Single(l => l.Id == first.Id).Status);
        Assert.Equal(DocumentStatus.Approved, db.ContainerPreLoadings.Single(p => p.Id == pre.Id).Status);
    }

    [Fact]
    public async Task Approve_重复明细行聚合_累计超限拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-DUP-1", "客户A");
        SeedProduct(db, ProductA, "P-DUP-1", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-DUP-1", DocumentStatus.Approved, (ProductA, 10m));
        var list = SeedLoading(db, "ZQ-DUP-1", pre.Id, CustomerA, DocumentStatus.Submitted,
            (ProductA, 6m, 0m, 0m, 0m), (ProductA, 6m, 0m, 0m, 0m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(list.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过预装柜单授权数量 10", ex.Message);
        Assert.Contains("本次 12", ex.Message);
    }

    [Fact]
    public async Task Approve_重复来源行聚合_授权数量正确()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-SRCDUP-1", "客户A");
        SeedProduct(db, ProductA, "P-SRCDUP-1", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-SRCDUP-1", DocumentStatus.Approved, (ProductA, 6m), (ProductA, 4m));
        var list = SeedLoading(db, "ZQ-SRCDUP-1", pre.Id, CustomerA, DocumentStatus.Submitted, (ProductA, 10m, 0m, 0m, 0m));
        var ctl = NewController(db, userId);

        await ctl.Approve(list.Id);

        Assert.Equal(DocumentStatus.Approved, db.ContainerLoadingLists.Single(l => l.Id == list.Id).Status);
    }

    [Fact]
    public async Task Cancel_已审核释放额度_可继续满量()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CANCEL-1", "客户A");
        SeedProduct(db, ProductA, "P-CANCEL-1", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-CANCEL-1", DocumentStatus.Approved, (ProductA, 10m));
        var first = SeedLoading(db, "ZQ-CANCEL-A", pre.Id, CustomerA, DocumentStatus.Approved, (ProductA, 6m, 0m, 0m, 0m));
        var ctl = NewController(db, userId);

        var cancel = await ctl.Cancel(first.Id);
        Assert.IsType<OkObjectResult>(cancel);
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerLoadingLists.Single(l => l.Id == first.Id).Status);

        var second = SeedLoading(db, "ZQ-CANCEL-B", pre.Id, CustomerA, DocumentStatus.Submitted, (ProductA, 10m, 0m, 0m, 0m));
        await ctl.Approve(second.Id);
        Assert.Equal(DocumentStatus.Approved, db.ContainerLoadingLists.Single(l => l.Id == second.Id).Status);
    }

    // ==================== 来源取消护栏 ====================

    [Fact]
    public async Task SourceCancel_存在有效已审核装柜_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SRCCANCEL-1", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-SRCCANCEL-1", DocumentStatus.Approved, (ProductA, 10m));
        SeedLoading(db, "ZQ-SRCCANCEL-A", pre.Id, CustomerA, DocumentStatus.Approved, (ProductA, 6m, 0m, 0m, 0m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ContainerLoadingFulfillmentRules.ValidateSourceCancellationAsync(db, ReloadPreLoading(db, pre.Id), userId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task SourceCancel_无有效已审核装柜_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SRCCANCEL-2", "商品A", "PCS");
        var pre = SeedPreLoading(db, "YZ-SRCCANCEL-2", DocumentStatus.Approved, (ProductA, 10m));
        SeedLoading(db, "ZQ-SRCCANCEL-B", pre.Id, CustomerA, DocumentStatus.Cancelled, (ProductA, 6m, 0m, 0m, 0m));

        await ContainerLoadingFulfillmentRules.ValidateSourceCancellationAsync(db, ReloadPreLoading(db, pre.Id), userId);
    }


    // ==================== 权限与口径 ====================

    [Fact]
    public async Task ValidateLink_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "C-AUTH-1", "客户A");
        var entity = NewLoading(CustomerA, null, (ProductA, 6m, 0m, 0m, 0m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ContainerLoadingFulfillmentRules.ValidateLinkAsync(db, entity, null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ValidateLink_无菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: false);
        SeedCustomer(db, CustomerA, "C-AUTH-2", "客户A");
        var entity = NewLoading(CustomerA, null, (ProductA, 6m, 0m, 0m, 0m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ContainerLoadingFulfillmentRules.ValidateLinkAsync(db, entity, user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
    }

    [Fact]
    public async Task ValidateLink_越客户范围_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: true);
        SeedCustomer(db, CustomerA, "C-AUTH-3", "客户A");
        var entity = NewLoading(CustomerA, null, (ProductA, 6m, 0m, 0m, 0m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ContainerLoadingFulfillmentRules.ValidateLinkAsync(db, entity, user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public void Controller_授权仅继承基类_无权限扩展()
    {
        var controllerType = typeof(ContainerLoadingListController);
        Assert.NotNull(controllerType.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    [Fact]
    public void RuleText_口径说明_明确不猜测()
    {
        Assert.False(string.IsNullOrWhiteSpace(ContainerLoadingFulfillmentRules.RuleText));
        Assert.Contains("绝不猜测", ContainerLoadingFulfillmentRules.RuleText);
        Assert.Contains("绝不把箱数当件数", ContainerLoadingFulfillmentRules.RuleText);
        Assert.Contains("显式链接无效", ContainerLoadingFulfillmentRules.RuleText);
    }

    // ==================== 辅助 ====================

    private static ContainerPreLoading ReloadPreLoading(ErpDbContext db, long id)
        => db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().Single(p => p.Id == id);

    private static SysUser SeedAuthorizedUser(ErpDbContext db, bool isSystem, bool withMenu)
    {
        var role = new SysRole
        {
            RoleName = "测试角色",
            RoleCode = $"Role-{Guid.NewGuid():N}",
            IsSystem = isSystem
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "测试用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withMenu)
        {
            var menu = new SysMenu
            {
                MenuCode = ContainerLoadingFulfillmentRules.RequiredMenuCode,
                MenuName = ContainerLoadingFulfillmentRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user;
    }
}

