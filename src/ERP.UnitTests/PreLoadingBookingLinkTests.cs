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
/// ERP-353 预装柜单上游「订柜信息」权威链接护栏单元测试。
/// <para>覆盖：已审核订柜放行、未关联历史单据保持行为、非正 / 不存在 / 已删除 / 未审核 / 已取消 / 范围外订柜拒绝、
/// 同一订柜不同柜号的权威柜号链接冲突、同柜号与已取消冲突单据不阻断、改派来源失败时原单与原始明细数量不变、
/// 提交 / 审核前的来源复核、订柜取消与已审核预装柜（及下游已审核装柜清单）的协同拒绝与显式解除、
/// 无身份 / 无菜单 / 越客户范围拒绝、只读跟踪语义保持，以及控制器授权仅继承基类。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class PreLoadingBookingLinkTests
{
    private const long CustomerA = 943001L;
    private const long CustomerB = 943002L;
    private const long ProductA = 943101L;
    private const long ProductB = 943102L;

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerBookingController NewBookingController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerBookingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string code, string name, long? empId = null)
    {
        db.BaseCustomers.Add(new BaseCustomer { Id = id, CustomerCode = code, CustomerName = name, EmpId = empId });
        db.SaveChanges();
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name)
    {
        db.BaseProducts.Add(new BaseProduct { Id = id, ProductCode = code, ProductName = name, Spec = "规格A", Unit = "PCS" });
        db.SaveChanges();
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, DocumentStatus status,
        long customerId = CustomerA, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no,
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted,
            BillOfLadingNo = "BL-" + no
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, long? bookingId,
        DocumentStatus status, string containerNo = "", string sealNo = "",
        params (long ProductId, decimal Quantity)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no, LoadingDate = DateTime.Today, BookingId = bookingId,
            ContainerNo = containerNo, SealNo = sealNo, Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id, ProductId = productId, ProductName = $"商品{productId}", Quantity = quantity
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long preLoadingId,
        DocumentStatus status, long customerId = CustomerA)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, PreLoadingId = preLoadingId, LoadingDate = DateTime.Today,
            CustomerId = customerId, Status = status
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    /// <summary>构造提交给控制器的预装柜单（含明细；客户端只提交柜号 / 封条号等本单运行记录）。</summary>
    private static ContainerPreLoading NewPreLoading(long? bookingId, string containerNo,
        params (long ProductId, decimal Quantity, decimal Cartons, decimal Weight, decimal Volume)[] lines)
        => new()
        {
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            ContainerNo = containerNo,
            SealNo = "SEAL-001",
            Remark = "ERP-353_TEST",
            Details = lines.Select(l => new ContainerPreLoadingDetail
            {
                ProductId = l.ProductId, ProductName = $"商品{l.ProductId}", Quantity = l.Quantity,
                Cartons = l.Cartons, Weight = l.Weight, Volume = l.Volume
            }).ToList()
        };

    private static ContainerPreLoading Reload(ErpDbContext db, long id)
        => db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().Single(p => p.Id == id);

    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>播种普通（非特权）账号并授予指定既有菜单；未映射为业务员时数据范围为空（fail closed）。</summary>
    private static SysUser SeedMenuUser(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole { RoleName = "测试角色", RoleCode = $"Role-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"u-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "测试用户", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return user;
    }

    /// <summary>播种受限制的装柜操作员（业务员映射 + 既有菜单授权），返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"container-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "装柜操作员", RoleCode = $"ContainerOp-{Guid.NewGuid():N}", IsSystem = false };
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
                MenuName = menuCode == PreLoadingBookingLinkRules.RequiredMenuCode
                    ? PreLoadingBookingLinkRules.RequiredMenuText
                    : PreLoadingBookingLinkRules.BookingRequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    // ==================== 创建：权威来源与数据范围 ====================

    [Fact]
    public async Task Create_关联已审核订柜_保存成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-LINK-1", "商品A");
        var booking = SeedBooking(db, "DG-LINK-1", DocumentStatus.Approved);
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewPreLoading(booking.Id, "CTN-1001", (ProductA, 6m, 2m, 30m, 1.5m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.ContainerPreLoadings.Include(o => o.Details).Single();
        Assert.Equal(booking.Id, saved.BookingId);
        Assert.Equal("CTN-1001", saved.ContainerNo);       // 本单运行记录原样保留，不由订柜信息推导
        Assert.Equal("SEAL-001", saved.SealNo);            // 绝不臆造 / 不改写封条号
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(6m, saved.Details.Single().Quantity); // 原始明细数量不被护栏改写
        Assert.Empty(db.ContainerLoadingLists);            // 不连带产生下游单据
        Assert.Empty(db.StockMovements);                   // 不写库存
        Assert.Empty(db.FinanceExpenses);                  // 不写财务
    }

    [Fact]
    public async Task Create_未关联订柜_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-LINK-2", "商品A");
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewPreLoading(null, "CTN-1002", (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.ContainerPreLoadings.Single().BookingId);
    }

    [Fact]
    public async Task Create_非正订柜Id_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-LINK-3", "商品A");
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(0L, "CTN-1003", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ContainerPreLoadings);
    }

    [Fact]
    public async Task Create_订柜不存在或已删除_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-LINK-4", "商品A");
        var deleted = SeedBooking(db, "DG-LINK-DEL", DocumentStatus.Approved, deleted: true);
        var ctl = NewController(db, userId);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(999999L, "CTN-1004", (ProductA, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.RuleConflict, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(deleted.Id, "CTN-1005", (ProductA, 6m, 0m, 0m, 0m))));
        Assert.Equal(ErrorCodes.RuleConflict, removed.Code);
        Assert.Contains("已删除", removed.Message);

        Assert.Empty(db.ContainerPreLoadings);
    }

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Rejected)]
    [InlineData(DocumentStatus.Completed)]
    public async Task Create_订柜未审核_拒绝保存(DocumentStatus status)
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-LINK-5", "商品A");
        var booking = SeedBooking(db, $"DG-LINK-{status}", status);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(booking.Id, "CTN-1006", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("未审核", ex.Message);
        Assert.Empty(db.ContainerPreLoadings);
    }

    [Fact]
    public async Task Create_订柜已取消_拒绝保存()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-LINK-6", "商品A");
        var booking = SeedBooking(db, "DG-LINK-CANCEL", DocumentStatus.Cancelled);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(booking.Id, "CTN-1007", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已取消", ex.Message);
        Assert.Empty(db.ContainerPreLoadings);
    }

    [Fact]
    public async Task Create_范围外订柜_拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedRestrictedOperator(db, PreLoadingBookingLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "C-OUT-1", "范围外客户");   // 未映射到该操作员 → 范围外
        var booking = SeedBooking(db, "DG-LINK-OUT", DocumentStatus.Approved);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(booking.Id, "CTN-1008", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.ContainerPreLoadings);
    }

    [Fact]
    public async Task Create_范围内订柜_放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingBookingLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "C-IN-1", "范围内客户", employeeId);
        SeedProduct(db, ProductA, "P-LINK-7", "商品A");
        var booking = SeedBooking(db, "DG-LINK-IN", DocumentStatus.Approved);
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewPreLoading(booking.Id, "CTN-1009", (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(booking.Id, db.ContainerPreLoadings.Single().BookingId);
    }

    [Fact]
    public async Task Create_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-LINK-ANON", DocumentStatus.Approved);
        var ctl = NewController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(booking.Id, "CTN-1010", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.ContainerPreLoadings);
    }

    [Fact]
    public async Task Create_无菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedMenuUser(db);
        var booking = SeedBooking(db, "DG-LINK-NOMENU", DocumentStatus.Approved);
        var ctl = NewController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(booking.Id, "CTN-1011", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.Empty(db.ContainerPreLoadings);
    }

    // ==================== 权威柜号链接冲突 ====================

    [Fact]
    public async Task Create_同订柜不同柜号_冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-CONFLICT-1", "商品A");
        var booking = SeedBooking(db, "DG-CONFLICT-1", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-CONFLICT-A", booking.Id, DocumentStatus.Approved, "CTN-A");
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPreLoading(booking.Id, "CTN-B", (ProductA, 6m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("权威柜号链接冲突", ex.Message);
        Assert.Equal(1, db.ContainerPreLoadings.Count());
    }

    [Fact]
    public async Task Create_同订柜同柜号_允许()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-CONFLICT-2", "商品A");
        var booking = SeedBooking(db, "DG-CONFLICT-2", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-CONFLICT-B", booking.Id, DocumentStatus.Submitted, "CTN-SAME");
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewPreLoading(booking.Id, " ctn-same ", (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, db.ContainerPreLoadings.Count());
    }

    [Fact]
    public async Task Create_已取消的冲突预装柜_不阻断()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-CONFLICT-3", "商品A");
        var booking = SeedBooking(db, "DG-CONFLICT-3", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-CONFLICT-C", booking.Id, DocumentStatus.Cancelled, "CTN-OLD");
        SeedPreLoading(db, "YZ-CONFLICT-D", booking.Id, DocumentStatus.Rejected, "CTN-OLD-2");
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewPreLoading(booking.Id, "CTN-NEW", (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Create_本单未申报柜号_不做冲突判定()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-CONFLICT-4", "商品A");
        var booking = SeedBooking(db, "DG-CONFLICT-4", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-CONFLICT-E", booking.Id, DocumentStatus.Approved, "CTN-EXIST");
        var ctl = NewController(db, userId);

        var result = await ctl.Create(NewPreLoading(booking.Id, string.Empty, (ProductA, 6m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(string.Empty, db.ContainerPreLoadings.OrderBy(p => p.Id).Last().ContainerNo);
    }

    // ==================== 修改：改派来源时先校验，失败不改动原单 ====================

    [Fact]
    public async Task Update_改派到未审核订柜_拒绝且原单不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-UPD-1", "商品A");
        var approved = SeedBooking(db, "DG-UPD-A", DocumentStatus.Approved);
        var pending = SeedBooking(db, "DG-UPD-B", DocumentStatus.Pending);
        var existing = SeedPreLoading(db, "YZ-UPD-1", approved.Id, DocumentStatus.Pending, "CTN-A", "SEAL-A",
            (ProductA, 5m));
        var before = Reload(db, existing.Id);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(existing.Id,
            NewPreLoading(pending.Id, "CTN-B", (ProductA, 9m, 1m, 1m, 1m))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        var after = Reload(db, existing.Id);
        Assert.Equal(before.BookingId, after.BookingId);            // 来源引用保持原值
        Assert.Equal(before.ContainerNo, after.ContainerNo);        // 柜号快照保持原值
        Assert.Equal(before.SealNo, after.SealNo);                  // 封条号保持原值（绝不臆造 / 清空）
        Assert.Equal(before.Details.Single().Quantity, after.Details.Single().Quantity); // 原始数量保持
        Assert.Equal(before.Details.Single().ProductId, after.Details.Single().ProductId);
    }

    [Fact]
    public async Task Update_改派到范围外订柜_拒绝且原单不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingBookingLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "C-UPD-IN", "范围内客户", employeeId);
        SeedCustomer(db, CustomerB, "C-UPD-OUT", "范围外客户");
        var inScope = SeedBooking(db, "DG-UPD-IN", DocumentStatus.Approved, CustomerA);
        var outScope = SeedBooking(db, "DG-UPD-OUT", DocumentStatus.Approved, CustomerB);
        var existing = SeedPreLoading(db, "YZ-UPD-2", inScope.Id, DocumentStatus.Pending, "CTN-IN", "SEAL-IN",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(existing.Id,
            NewPreLoading(outScope.Id, "CTN-OUT", (ProductA, 5m, 0m, 0m, 0m))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(inScope.Id, Reload(db, existing.Id).BookingId);
    }

    [Fact]
    public async Task Update_改派到已审核订柜_成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-UPD-2", "商品A");
        SeedProduct(db, ProductB, "P-UPD-3", "商品B");
        var first = SeedBooking(db, "DG-UPD-C", DocumentStatus.Approved);
        var second = SeedBooking(db, "DG-UPD-D", DocumentStatus.Approved);
        var existing = SeedPreLoading(db, "YZ-UPD-3", first.Id, DocumentStatus.Pending, "CTN-C", "SEAL-C",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var result = await ctl.Update(existing.Id,
            NewPreLoading(second.Id, "CTN-D", (ProductB, 8m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, existing.Id);
        Assert.Equal(second.Id, saved.BookingId);
        Assert.Equal("CTN-D", saved.ContainerNo);
        Assert.Equal(ProductB, saved.Details.Single().ProductId);
        Assert.Equal(8m, saved.Details.Single().Quantity);
    }

    [Fact]
    public async Task Update_解除订柜关联_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-UPD-4", "商品A");
        var booking = SeedBooking(db, "DG-UPD-E", DocumentStatus.Approved);
        var existing = SeedPreLoading(db, "YZ-UPD-4", booking.Id, DocumentStatus.Pending, "CTN-E", "SEAL-E",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var result = await ctl.Update(existing.Id, NewPreLoading(null, "CTN-E2", (ProductA, 5m, 0m, 0m, 0m)));

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(Reload(db, existing.Id).BookingId);
    }

    // ==================== 提交 / 审核：来源复核 ====================

    [Fact]
    public async Task Submit_订柜未审核_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SUB-1", "商品A");
        var booking = SeedBooking(db, "DG-SUB-A", DocumentStatus.Pending);
        var entity = SeedPreLoading(db, "YZ-SUB-1", booking.Id, DocumentStatus.Pending, "CTN-SUB", "SEAL-SUB",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(entity.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Pending, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Submit_合法_成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SUB-2", "商品A");
        var booking = SeedBooking(db, "DG-SUB-B", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-SUB-2", booking.Id, DocumentStatus.Pending, "CTN-SUB2", "SEAL-SUB2",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var result = await ctl.Submit(entity.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Submit_未关联_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SUB-3", "商品A");
        var entity = SeedPreLoading(db, "YZ-SUB-3", null, DocumentStatus.Pending, "CTN-SUB3", "SEAL-SUB3",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var result = await ctl.Submit(entity.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Submit_无菜单_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var user = SeedMenuUser(db);
        var booking = SeedBooking(db, "DG-SUB-C", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-SUB-4", booking.Id, DocumentStatus.Pending, "CTN-SUB4", "SEAL-SUB4");
        var ctl = NewController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(entity.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Pending, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Approve_合法_成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-APP-1", "商品A");
        var booking = SeedBooking(db, "DG-APP-A", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-APP-1", booking.Id, DocumentStatus.Submitted, "CTN-APP", "SEAL-APP",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var result = await ctl.Approve(entity.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Approved, Reload(db, entity.Id).Status);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
        Assert.Empty(db.StockMovements);      // 审核不写库存
        Assert.Empty(db.FinanceExpenses);     // 审核不写财务
    }

    [Fact]
    public async Task Approve_订柜已取消_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-APP-2", "商品A");
        var booking = SeedBooking(db, "DG-APP-B", DocumentStatus.Cancelled);
        var entity = SeedPreLoading(db, "YZ-APP-2", booking.Id, DocumentStatus.Submitted, "CTN-APP2", "SEAL-APP2",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(entity.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Approve_同订柜冲突柜号_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-APP-3", "商品A");
        var booking = SeedBooking(db, "DG-APP-C", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-APP-3A", booking.Id, DocumentStatus.Approved, "CTN-A", "SEAL-A", (ProductA, 5m));
        var entity = SeedPreLoading(db, "YZ-APP-3B", booking.Id, DocumentStatus.Submitted, "CTN-B", "SEAL-B",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(entity.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("权威柜号链接冲突", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Approve_未关联_保持历史行为()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-APP-4", "商品A");
        var entity = SeedPreLoading(db, "YZ-APP-4", null, DocumentStatus.Submitted, "CTN-APP4", "SEAL-APP4",
            (ProductA, 5m));
        var ctl = NewController(db, userId);

        var result = await ctl.Approve(entity.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Approved, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Approve_无菜单_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var user = SeedMenuUser(db);
        var booking = SeedBooking(db, "DG-APP-D", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-APP-5", booking.Id, DocumentStatus.Submitted, "CTN-APP5", "SEAL-APP5");
        var ctl = NewController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(entity.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task Approve_重复审核_状态门拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        var booking = SeedBooking(db, "DG-APP-E", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-APP-6", booking.Id, DocumentStatus.Approved, "CTN-APP6", "SEAL-APP6");
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(entity.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Approved, Reload(db, entity.Id).Status);
    }

    // ==================== 订柜取消：与已审核预装柜（及下游）协同 ====================

    [Fact]
    public async Task BookingCancel_存在已审核预装柜_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CXL-1", "取消订柜客户");
        var booking = SeedBooking(db, "DG-CXL-1", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-CXL-1", booking.Id, DocumentStatus.Approved, "CTN-CXL");
        var ctl = NewBookingController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(booking.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("预装柜单", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
        Assert.Equal(DocumentStatus.Approved, Reload(db, db.ContainerPreLoadings.Single().Id).Status);
    }

    [Fact]
    public async Task BookingCancel_仅待提交或已取消预装柜_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CXL-2", "取消订柜客户");
        var booking = SeedBooking(db, "DG-CXL-2", DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-CXL-2A", booking.Id, DocumentStatus.Pending, "CTN-CXL2A");
        SeedPreLoading(db, "YZ-CXL-2B", booking.Id, DocumentStatus.Submitted, "CTN-CXL2B");
        SeedPreLoading(db, "YZ-CXL-2C", booking.Id, DocumentStatus.Cancelled, "CTN-CXL2C");
        SeedPreLoading(db, "YZ-CXL-2D", booking.Id, DocumentStatus.Rejected, "CTN-CXL2D");
        var ctl = NewBookingController(db, userId);

        var result = await ctl.Cancel(booking.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_下游已审核装柜清单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CXL-3", "取消订柜客户");
        var booking = SeedBooking(db, "DG-CXL-3", DocumentStatus.Approved);
        // 历史数据不一致场景：预装柜单未审核，但其下游装柜清单已审核 → 仍必须拒绝取消订柜
        var entity = SeedPreLoading(db, "YZ-CXL-3", booking.Id, DocumentStatus.Submitted, "CTN-CXL3");
        SeedLoadingList(db, "ZQ-CXL-3", entity.Id, DocumentStatus.Approved);
        var ctl = NewBookingController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(booking.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("装柜清单", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_已取消装柜清单_不阻断()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CXL-4", "取消订柜客户");
        var booking = SeedBooking(db, "DG-CXL-4", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-CXL-4", booking.Id, DocumentStatus.Submitted, "CTN-CXL4");
        SeedLoadingList(db, "ZQ-CXL-4", entity.Id, DocumentStatus.Cancelled);
        var ctl = NewBookingController(db, userId);

        var result = await ctl.Cancel(booking.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_预装柜显式取消后放行()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CXL-5", "取消订柜客户");
        var booking = SeedBooking(db, "DG-CXL-5", DocumentStatus.Approved);
        var entity = SeedPreLoading(db, "YZ-CXL-5", booking.Id, DocumentStatus.Approved, "CTN-CXL5");
        var bookingCtl = NewBookingController(db, userId);
        var preLoadingCtl = NewController(db, userId);

        var blocked = await Assert.ThrowsAsync<BusinessException>(() => bookingCtl.Cancel(booking.Id));
        Assert.Equal(ErrorCodes.RuleConflict, blocked.Code);

        // 显式取消流程：先取消预装柜单（复用既有 ERP-348 护栏），再取消订柜信息
        var released = await preLoadingCtl.Cancel(entity.Id);
        Assert.IsType<OkObjectResult>(released);
        Assert.Equal(DocumentStatus.Cancelled, Reload(db, entity.Id).Status);

        var result = await bookingCtl.Cancel(booking.Id);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-CXL-ANON", DocumentStatus.Approved);
        var ctl = NewBookingController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(booking.Id));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_无菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedMenuUser(db);
        var booking = SeedBooking(db, "DG-CXL-NOMENU", DocumentStatus.Approved);
        var ctl = NewBookingController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(booking.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_越客户范围_拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedRestrictedOperator(db, PreLoadingBookingLinkRules.BookingRequiredMenuCode);
        SeedCustomer(db, CustomerA, "C-CXL-OUT", "范围外客户");
        var booking = SeedBooking(db, "DG-CXL-SCOPE", DocumentStatus.Approved);
        var ctl = NewBookingController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(booking.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == booking.Id).Status);
    }

    [Fact]
    public async Task BookingCancel_不写库存财务与下游单据()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CXL-6", "取消订柜客户");
        var booking = SeedBooking(db, "DG-CXL-6", DocumentStatus.Approved);
        var ctl = NewBookingController(db, userId);

        await ctl.Cancel(booking.Id);

        Assert.Empty(db.ContainerPreLoadings);
        Assert.Empty(db.ContainerLoadingLists);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.FinanceExpenses);
        Assert.Empty(db.TradeDocuments);
    }

    // ==================== 只读跟踪语义保持（ERP-040 回归） ====================

    [Fact]
    public async Task 只读跟踪_未关联或指向无效订柜_仍返回未关联()
    {
        using var db = TestDbFactory.Create();
        var unlinked = SeedPreLoading(db, "YZ-TRK-1", null, DocumentStatus.Approved, "CTN-TRK1");
        var dangling = SeedPreLoading(db, "YZ-TRK-2", 999999L, DocumentStatus.Pending, "CTN-TRK2");
        var deletedBooking = SeedBooking(db, "DG-TRK-DEL", DocumentStatus.Approved, deleted: true);
        db.SaveChanges();
        var toDeleted = SeedPreLoading(db, "YZ-TRK-3", deletedBooking.Id, DocumentStatus.Pending, "CTN-TRK3");

        var unlinkedTracking = await ContainerShipmentTrackingService.ResolveForPreLoadingAsync(db, unlinked);
        var danglingTracking = await ContainerShipmentTrackingService.ResolveForPreLoadingAsync(db, dangling);
        var deletedTracking = await ContainerShipmentTrackingService.ResolveForPreLoadingAsync(db, toDeleted);

        Assert.False(unlinkedTracking.Linked);
        Assert.False(danglingTracking.Linked);
        Assert.False(deletedTracking.Linked);
        Assert.Null(danglingTracking.BookingId);          // 绝不按柜号等自由文本兜底匹配
        Assert.Equal("未知", danglingTracking.ShipmentModeText);
    }

    [Fact]
    public async Task 只读跟踪_已审核订柜_回显权威值且不改写本单()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-TRK-OK", DocumentStatus.Approved);
        booking.ShipmentMode = "FCL";
        db.SaveChanges();
        var entity = SeedPreLoading(db, "YZ-TRK-4", booking.Id, DocumentStatus.Pending, "CTN-TRK4", "SEAL-TRK4");
        var before = Reload(db, entity.Id);

        var tracking = await ContainerShipmentTrackingService.ResolveForPreLoadingAsync(db, Reload(db, entity.Id));

        Assert.True(tracking.Linked);
        Assert.Equal(booking.Id, tracking.BookingId);
        Assert.Equal("FCL", tracking.ShipmentMode);
        Assert.Equal("BL-DG-TRK-OK", tracking.BillOfLadingNo);
        var after = Reload(db, entity.Id);
        Assert.Equal(before.ContainerNo, after.ContainerNo);     // 只读：不把跟踪值复制到本单
        Assert.Equal(before.SealNo, after.SealNo);
        Assert.Equal(before.Status, after.Status);
    }

    // ==================== 口径与接线契约 ====================

    [Fact]
    public void 规则_只读判定_不写单据且不臆造柜号封条号()
    {
        var rules = ReadSource("ERP.Application/Services/PreLoadingBookingLinkRules.cs");

        Assert.Contains("AsNoTracking", rules);
        Assert.DoesNotContain("SaveChanges", rules);            // 纯判定，不落库
        Assert.DoesNotContain(".Add(", rules);
        Assert.DoesNotContain(".Remove(", rules);
        Assert.DoesNotContain(".Update(", rules);
        Assert.DoesNotContain(".ContainerNo =", rules);         // 绝不由订柜信息推导 / 改写柜号
        Assert.DoesNotContain(".SealNo =", rules);              // 绝不臆造封条号
        Assert.DoesNotContain("ContainerLoadingListParticipant", rules); // 绝不臆造参与方
    }

    [Fact]
    public void 规则_口径文案_明确权威字段与不猜测边界()
    {
        Assert.Contains("绝不臆造封条号", PreLoadingBookingLinkRules.RuleText);
        Assert.Contains("绝不按单号等自由文本猜测来源", PreLoadingBookingLinkRules.RuleText);
        Assert.Contains("未填写订柜信息的历史预装柜单保持既有行为", PreLoadingBookingLinkRules.RuleText);
        Assert.Contains("没有柜号", PreLoadingBookingLinkRules.AuthoritativeFieldsText);
        Assert.Contains("没有参与方字段", PreLoadingBookingLinkRules.AuthoritativeFieldsText);
        Assert.Contains("不写库存", PreLoadingBookingLinkRules.BoundaryText);
        Assert.Contains("不调用船公司", PreLoadingBookingLinkRules.BoundaryText);
        Assert.Equal("pre-loading", PreLoadingBookingLinkRules.RequiredMenuCode);
        Assert.Equal("booking", PreLoadingBookingLinkRules.BookingRequiredMenuCode);
    }

    [Fact]
    public void Controller_授权仅继承基类_无权限扩展()
    {
        foreach (var type in new[] { typeof(ContainerPreLoadingController), typeof(ContainerBookingController) })
        {
            Assert.NotNull(type.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>().FirstOrDefault());
            Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
        }
    }

    [Fact]
    public void Controller_四个写路由都接线链接校验与订柜行锁()
    {
        var preLoading = ReadSource("ERP.Api/Controllers/ContainerPreLoadingController.cs");
        Assert.True(Count(preLoading, "PreLoadingBookingLinkRules.ValidateLinkAsync") >= 3);   // 创建 / 修改 / 提交
        Assert.Contains("PreLoadingBookingLinkRules.ValidateApprovalAsync", preLoading);        // 审核
        Assert.Contains("ContainerBookings WITH (UPDLOCK, HOLDLOCK)", preLoading);              // 与订柜取消同一把行锁

        var booking = ReadSource("ERP.Api/Controllers/ContainerControllers.cs");
        Assert.Contains("PreLoadingBookingLinkRules.ValidateBookingCancellationAsync", booking);
        Assert.Contains("ContainerBookings WITH (UPDLOCK, HOLDLOCK)", booking);
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
