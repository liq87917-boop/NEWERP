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
/// ERP-364 装柜清单实时授权 / 权威客户数据范围（含一柜多客户参与方与显式上游共享出运）护栏单元测试。
/// <para>覆盖：列表 / 详情 / 出运时间线 / 费用分摊证据 / 参与方读取 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除<b>每一个</b>路由的
/// 身份（缺失 / 已删除 / 禁用）、既有「装柜清单」菜单授权与未映射业务员的 fail closed；受限业务员一柜多客户必须对
/// 每一个有效参与方客户有权限、显式上游订柜客户越范围拒绝、无权威归属 fail closed、列表在计数前按数据库侧范围过滤；
/// 被拒绝的参与方维护 / 修改 / 状态变更不改动库中参与方、字段、明细、状态与审计；特权账号保留历史访问；
/// 规则为纯只读判定、控制器全部路由先授权并共用锁与事务。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class LoadingListAuthorizationTests
{
    private const long CustomerOwn = 964001L;
    private const long CustomerOther = 964002L;
    private const long ProductA = 964101L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerLoadingListController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name,
        long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = name, EmpId = empId, Status = status, IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, long customerId, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId,
            Status = DocumentStatus.Approved, IsDeleted = deleted, BillOfLadingNo = "BL-" + no
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, long? bookingId,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no, LoadingDate = DateTime.Today, BookingId = bookingId,
            ContainerNo = "CTN-" + no, SealNo = "SEAL-" + no, Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        return pre;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Pending, long? preLoadingId = null)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, ContainerNo = "TCLU-" + no,
            CustomerId = customerId, Status = status, PreLoadingId = preLoadingId
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingListParticipant SeedParticipant(ErpDbContext db, long loadingListId,
        long customerId, int status = 1, bool primary = false, bool deleted = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId, CustomerId = customerId, CustomerCode = $"C-{customerId}",
            CustomerName = "参与方", Status = status, IsPrimary = primary, IsDeleted = deleted
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    private static ContainerLoadingDetail SeedDetail(ErpDbContext db, long loadingListId, decimal quantity = 10m)
    {
        var detail = new ContainerLoadingDetail
        {
            LoadingListId = loadingListId, ProductId = ProductA, ProductName = "商品A",
            Quantity = quantity, Cartons = 1m, Weight = 1m, Volume = 1m
        };
        db.ContainerLoadingDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static void SeedProduct(ErpDbContext db, long id, string name)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = name, Spec = "规格A", Unit = "PCS"
        });
        db.SaveChanges();
    }

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

    /// <summary>播种受限制的装柜清单操作员（业务员映射 + 既有菜单授权），返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"loading-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "装柜清单操作员", RoleCode = $"LoadingOp-{Guid.NewGuid():N}", IsSystem = false };
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
                MenuName = menuCode == LoadingListAuthorizationRules.RequiredMenuCode
                    ? LoadingListAuthorizationRules.RequiredMenuText
                    : menuCode,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static ContainerLoadingList Reload(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return db.ContainerLoadingLists.AsNoTracking().Include(o => o.Details).Single(o => o.Id == id);
    }

    private static ContainerLoadingParticipantSaveDto Save(long customerId, bool? primary = null, int? status = null)
        => new() { CustomerId = customerId, IsPrimary = primary, Status = status };

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));

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


    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 每个路由_无身份_一律未认证拒绝且不落任何变更()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var booking = SeedBooking(db, "B-ANON", CustomerOwn);
        var pre = SeedPreLoading(db, "YZ-ANON", booking.Id);
        var list = SeedLoadingList(db, "ZQ-ANON", CustomerOwn, DocumentStatus.Pending, pre.Id);
        var participant = SeedParticipant(db, list.Id, CustomerOwn, primary: true);
        SeedProduct(db, ProductA, "商品A");
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetShipmentTimeline(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetExpenseAllocationEvidence(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetParticipants(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.CreateParticipant(list.Id, Save(CustomerOwn)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.UpdateParticipant(list.Id, participant.Id, Save(CustomerOwn)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.SetPrimaryParticipant(list.Id, participant.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.DisableParticipant(list.Id, participant.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.EnableParticipant(list.Id, participant.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.DeleteParticipant(list.Id, participant.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewLoading(CustomerOwn, pre.Id)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(list.Id, NewLoading(CustomerOwn, pre.Id)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Submit(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Approve(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Cancel(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(list.Id));

        Assert.Equal(1, db.ContainerLoadingListParticipants.Count());
        Assert.Equal(1, db.ContainerLoadingListParticipants.Single().Status);   // 参与方未被停用 / 删除
        Assert.False(db.ContainerLoadingListParticipants.Single().IsDeleted);
        Assert.Equal(DocumentStatus.Pending, Reload(db, list.Id).Status);
        Assert.False(Reload(db, list.Id).IsDeleted);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var list = SeedLoadingList(db, "ZQ-STATUS", CustomerOwn);

        var disabled = SeedMenuUser(db, LoadingListAuthorizationRules.RequiredMenuCode);
        disabled.Status = UserStatus.Disabled;
        db.SaveChanges();

        var deleted = SeedMenuUser(db, LoadingListAuthorizationRules.RequiredMenuCode);
        deleted.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled.Id).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled.Id).Delete(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted.Id).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted.Id).Delete(list.Id));

        Assert.False(Reload(db, list.Id).IsDeleted);
        Assert.Equal(DocumentStatus.Pending, Reload(db, list.Id).Status);
    }

    [Fact]
    public async Task 非特权_无既有装柜清单菜单_拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var list = SeedLoadingList(db, "ZQ-NOMENU", CustomerOwn);
        var user = SeedMenuUser(db, "booking");   // 只有订柜菜单，没有装柜清单菜单

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user.Id).GetById(list.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
    }

    [Fact]
    public async Task 未映射业务员的受限账号_不降级为全局可见()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var list = SeedLoadingList(db, "ZQ-UNMAPPED", CustomerOwn);
        var user = SeedMenuUser(db, LoadingListAuthorizationRules.RequiredMenuCode);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user.Id).GetById(list.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射为业务员", ex.Message);
    }

    private static ContainerLoadingList NewLoading(long customerId, long? preLoadingId, decimal quantity = 5m)
        => new()
        {
            LoadingDate = DateTime.Today,
            PreLoadingId = preLoadingId,
            CustomerId = customerId,
            Remark = "ERP-364_TEST",
            Details = new List<ContainerLoadingDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Quantity = quantity,
                    Cartons = 1m, Weight = 1m, Volume = 1m
                }
            }
        };


    // ==================== 2. 受限业务员：权威客户范围（参与方 + 上游共享出运） ====================

    [Fact]
    public async Task 受限业务员_列表只统计本人客户且范围先于计数()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");

        var own = SeedLoadingList(db, "ZQ-OWN", CustomerOwn);
        var foreign = SeedLoadingList(db, "ZQ-OTHER", CustomerOther);
        var ownMulti = SeedLoadingList(db, "ZQ-MULTI", CustomerOther);
        SeedParticipant(db, ownMulti.Id, CustomerOwn, primary: true);   // 参与方含本人客户 → 可见
        var foreignMulti = SeedLoadingList(db, "ZQ-FOREIGN-MULTI", CustomerOwn);
        SeedParticipant(db, foreignMulti.Id, CustomerOther);            // 含他人参与方 → 不可见

        var page = AssertOk<PagedResult<ContainerLoadingList>>(
            await NewController(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));

        Assert.Equal(2, page.Total);   // 计数发生在数据库侧范围过滤之后
        Assert.Equal(new[] { ownMulti.Id, own.Id }.OrderBy(x => x), page.Items.Select(x => x.Id).OrderBy(x => x));
        Assert.DoesNotContain(page.Items, x => x.Id == foreign.Id || x.Id == foreignMulti.Id);
    }

    [Fact]
    public async Task 受限业务员_多客户单据_缺少任一有效参与方客户即拒绝读取()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");

        var shared = SeedLoadingList(db, "ZQ-SHARED", CustomerOwn);
        SeedParticipant(db, shared.Id, CustomerOwn, primary: true);
        SeedParticipant(db, shared.Id, CustomerOther);                  // 共享柜含他人客户

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, userId).GetById(shared.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("参与方客户", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetShipmentTimeline(shared.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).Submit(shared.Id));
    }

    [Fact]
    public async Task 受限业务员_显式上游共享出运客户越范围_拒绝读取()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        var foreignBooking = SeedBooking(db, "B-UPSTREAM", CustomerOther);
        var pre = SeedPreLoading(db, "YZ-UPSTREAM", foreignBooking.Id);
        var list = SeedLoadingList(db, "ZQ-UPSTREAM", CustomerOwn, DocumentStatus.Pending, pre.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, userId).GetById(list.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("上游", ex.Message);
    }

    [Fact]
    public async Task 受限业务员_无有效参与方且兼容客户字段缺失_无权威归属拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        var list = SeedLoadingList(db, "ZQ-OWNERLESS", 0L);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, userId).GetById(list.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("权威客户归属", ex.Message);
    }

    [Fact]
    public async Task 特权账号_保留历史访问_含无参与方与共享柜与无主单据()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerOther, "他人客户");
        var legacy = SeedLoadingList(db, "ZQ-LEGACY", CustomerOther);
        var ownerless = SeedLoadingList(db, "ZQ-OWNERLESS-OK", 0L);
        var shared = SeedLoadingList(db, "ZQ-SHARED-OK", 0L);
        SeedParticipant(db, shared.Id, CustomerOther);

        var ctl = NewController(db, privileged);
        AssertOk<ContainerLoadingList>(await ctl.GetById(legacy.Id));
        AssertOk<ContainerLoadingList>(await ctl.GetById(ownerless.Id));
        AssertOk<ContainerLoadingList>(await ctl.GetById(shared.Id));
        Assert.Equal(3, AssertOk<PagedResult<ContainerLoadingList>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null)).Total);
    }


    // ==================== 3. 被拒绝的参与方维护 / 修改 / 状态变更不改动库中数据 ====================

    [Fact]
    public async Task 受限业务员_新增或改派范围外参与方被拒绝_库中参与方不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        var list = SeedLoadingList(db, "ZQ-MAINTAIN", CustomerOwn);
        var own = SeedParticipant(db, list.Id, CustomerOwn, primary: true);
        var ctl = NewController(db, userId);

        // 已存储范围合法（仅本人参与方），但拟议新增他人客户参与方 → 拒绝。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.CreateParticipant(list.Id, Save(CustomerOther)));
        // 拟议把现有参与方改派为他人客户 → 拒绝。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.UpdateParticipant(list.Id, own.Id, Save(CustomerOther)));

        Assert.Equal(1, db.ContainerLoadingListParticipants.Count());
        Assert.Equal(CustomerOwn, db.ContainerLoadingListParticipants.Single().CustomerId);
        Assert.True(db.ContainerLoadingListParticipants.Single().IsPrimary);
    }

    [Fact]
    public async Task 受限业务员_被拒绝的拟议改派_不改动字段明细与审计()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        SeedProduct(db, ProductA, "商品A");
        var list = SeedLoadingList(db, "ZQ-EDIT", CustomerOwn);
        SeedDetail(db, list.Id, 7m);
        var before = Reload(db, list.Id);

        // 已存储范围合法（本人客户、无上游），但拟议兼容客户字段改派他人客户 → 写入任何字段 / 替换明细之前拒绝。
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId)
            .Update(list.Id, NewLoading(CustomerOther, null, 99m)));

        var after = Reload(db, list.Id);
        Assert.Equal(before.CustomerId, after.CustomerId);
        Assert.Equal(before.PreLoadingId, after.PreLoadingId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);            // 审计时间戳未被改写
        Assert.False(after.IsDeleted);
        Assert.Equal(before.Details.Single().Quantity, after.Details.Single().Quantity);
    }

    [Fact]
    public async Task 受限业务员_被拒绝的状态变更与删除_不改动状态与审计()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        var foreignBooking = SeedBooking(db, "B-FOREIGN-STATUS", CustomerOther);
        var pre = SeedPreLoading(db, "YZ-FOREIGN-STATUS", foreignBooking.Id);
        var pending = SeedLoadingList(db, "ZQ-STATUS-P", CustomerOwn, DocumentStatus.Pending, pre.Id);
        var submitted = SeedLoadingList(db, "ZQ-STATUS-S", CustomerOwn, DocumentStatus.Submitted, pre.Id);
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Submit(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Cancel(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Approve(submitted.Id));

        Assert.Equal(DocumentStatus.Pending, Reload(db, pending.Id).Status);
        Assert.False(Reload(db, pending.Id).IsDeleted);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, submitted.Id).Status);
    }

    [Fact]
    public async Task 受限业务员_本人客户内的参与方维护_正常成功()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, LoadingListAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "本人客户", employeeId);
        var other = SeedCustomer(db, CustomerOther, "他人客户", employeeId);   // 同属本人业务员 → 可见
        var list = SeedLoadingList(db, "ZQ-ALLOWED", CustomerOwn);
        var ctl = NewController(db, userId);

        var created = AssertOk<ContainerLoadingParticipantDto>(
            await ctl.CreateParticipant(list.Id, Save(CustomerOther, primary: true)));
        Assert.Equal(other.Id, created.CustomerId);
        Assert.Equal(CustomerOther, Reload(db, list.Id).CustomerId);          // 兼容客户字段已同步
    }

    // ==================== 4. 规则与控制器源码契约 ====================

    [Fact]
    public void 规则_纯只读判定_不落库不改单据不新增授权()
    {
        var rules = ReadSource("ERP.Application/Services/LoadingListAuthorizationRules.cs");

        Assert.DoesNotContain("SaveChanges", rules);          // 纯判定：绝不落库
        Assert.DoesNotContain(".Add(", rules);                // 不新增任何单据 / 授权
        Assert.DoesNotContain(".Remove(", rules);
        Assert.DoesNotContain(".Update(", rules);
        Assert.DoesNotContain("HttpClient", rules);           // 不调用外部系统
        Assert.DoesNotContain("SysRoleMenus", rules);         // 不新增用户授权
        Assert.DoesNotContain("SysMenus", rules);             // 不新增菜单
        Assert.DoesNotContain("LoadingListNo =", rules);      // 绝不推导 / 改写单号
        Assert.DoesNotContain("ContainerNo =", rules);        // 绝不推导 / 改写柜号

        Assert.Equal("loading-list", LoadingListAuthorizationRules.RequiredMenuCode);
        Assert.Equal(ContainerLoadingFulfillmentRules.RequiredMenuCode, LoadingListAuthorizationRules.RequiredMenuCode);
        Assert.Contains("每一个有效参与方客户", LoadingListAuthorizationRules.RuleText);
        Assert.Contains("显式上游", LoadingListAuthorizationRules.RuleText);
        Assert.Contains("无权威归属 fail closed", LoadingListAuthorizationRules.RuleText);
        Assert.Contains("绝不按单号等自由文本猜测归属", LoadingListAuthorizationRules.RuleText);
        Assert.Contains("特权账号保留历史访问", LoadingListAuthorizationRules.RuleText);
        Assert.Contains("不新增任何表 / 列 / 菜单 / 权限", LoadingListAuthorizationRules.BoundaryText);
        Assert.Contains("ERP-348", LoadingListAuthorizationRules.BoundaryText);
        Assert.Contains("ERP-363", LoadingListAuthorizationRules.BoundaryText);
    }


    [Fact]
    public void 控制器_全部读取路由先授权_列表先范围后计数_写路由共用锁与事务()
    {
        var controller = ReadSource("ERP.Api/Controllers/ContainerLoadingListController.cs");

        Assert.True(Count(controller, "LoadingListAuthorizationRules.EnsureAuthorizedAsync") >= 17);
        Assert.True(Count(controller, "LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync") >= 6);
        Assert.True(Count(controller, "LoadingListAuthorizationRules.EnsureProposedScopeAllowedAsync") >= 2);
        Assert.True(Count(controller, "EnsureStoredLoadingListScopeAsync") >= 7);
        Assert.Contains("LoadingListAuthorizationRules.ApplyScope", controller);
        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.True(Count(controller, "AcquireLoadingListWriteLocksAsync") >= 6);
        Assert.Contains("ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("ContainerLoadingFulfillmentRules.ValidateLinkAsync", controller);
        Assert.Contains("ContainerLoadingFulfillmentRules.ValidateApprovalAsync", controller);
        Assert.Contains("[HttpGet(\"{id:long}/participants\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/participants/{participantId:long}/primary\")]", controller);
        Assert.Contains("[HttpDelete(\"{id:long}\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/submit\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/approve\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/cancel\")]", controller);

        // 列表：授权（含范围解析）严格先于计数 / 分页
        var auth = controller.IndexOf("LoadingListAuthorizationRules.EnsureAuthorizedAsync", StringComparison.Ordinal);
        Assert.True(auth >= 0);
        var count = controller.IndexOf("var total = await source.CountAsync();", auth, StringComparison.Ordinal);
        Assert.True(count > auth);

        // 新增：拟议范围校验严格先于单据号生成
        var proposed = controller.IndexOf("LoadingListAuthorizationRules.EnsureProposedScopeAllowedAsync", StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.LoadingList)", StringComparison.Ordinal);
        Assert.True(proposed >= 0 && generate > proposed);
    }

    [Fact]
    public void Controller_授权仅继承基类_无权限扩展()
    {
        var type = typeof(ContainerLoadingListController);
        Assert.NotNull(type.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

}
