using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-361 收货计划实时授权 / 主数据 / 生命周期护栏单元测试。
/// <para>覆盖：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除<b>每一个</b>路由的身份（缺失 / 已删除 / 禁用）与
/// 既有「收货计划」菜单授权 fail closed；供应商计划没有权威业务员归属列，未映射业务员的受限账号不降级为全局可见；
/// 新增 / 修改 / 状态变更前供应商（必填）与目的港（可选，必须是启用港口字典项）真实可用、柜型为已知枚举、文本有界；
/// 提交 / 审核要求总件数为正数；修改先校验完整拟议内容，被拒绝的编辑不改动库中收货计划；历史读取在主数据停用 / 删除时
/// 照常可读并给出显式不可用证据；订柜单号只作 legacy 文本、不做任何链接推断。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API。</para>
/// </summary>
public class ReceivingPlanLifecycleTests
{
    private const long SupplierOk = 964101L;
    private const long SupplierOff = 964102L;
    private const long SupplierDeleted = 964103L;
    private const long PortOk = 964201L;
    private const long PortOff = 964202L;
    private const long PortDeleted = 964203L;
    private const long PortWrongType = 964204L;

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 每个路由_无身份_一律未认证拒绝且不落任何变更()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var plan = SeedPlan(db, "RP-ANON", SupplierOk);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(plan.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewPlan(SupplierOk)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(plan.Id, NewPlan(SupplierOk)));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Submit(plan.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Approve(plan.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Cancel(plan.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(plan.Id));

        Assert.Single(db.ContainerReceivingPlans);
        var stored = db.ContainerReceivingPlans.Single();
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var plan = SeedPlan(db, "RP-STATUS", SupplierOk);
        var disabled = SeedUser(db, UserStatus.Disabled);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Delete(plan.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(plan.Id));

        Assert.False(db.ContainerReceivingPlans.Single().IsDeleted);
    }

    [Fact]
    public async Task 非特权_无既有收货计划菜单_拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        var plan = SeedPlan(db, "RP-NOMENU", SupplierOk);
        var noMenuUser = SeedMenuUser(db);   // 普通角色 + 未映射员工 + 未授予 receiving-plan 菜单

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, noMenuUser).GetPaged(new PageQuery(), null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, noMenuUser).GetById(plan.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, noMenuUser).Create(NewPlan(SupplierOk)));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, noMenuUser).Submit(plan.Id));
        Assert.False(db.ContainerReceivingPlans.Single(b => b.Id == plan.Id).IsDeleted);
    }

    [Fact]
    public async Task 非特权_有菜单但未映射业务员_fail_closed不降级为全局可见()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedPlan(db, "RP-UNMAPPED", SupplierOk);
        var user = SeedMenuUser(db, grantReceivingPlanMenu: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user).GetPaged(new PageQuery(), null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射为业务员", ex.Message);
    }

    [Fact]
    public async Task 非特权_业务员_菜单被撤销后下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedPlan(db, "RP-REVOKE", SupplierOk);
        var (userId, _, roleId) = SeedRestrictedOperator(db);

        // 先授予既有「收货计划」菜单：放行
        var menuId = GrantReceivingPlanMenu(db, roleId);
        AssertOk<PagedResult<ContainerReceivingPlan>>(
            await NewController(db, userId).GetPaged(new PageQuery(), null));

        // 撤销授权（软删除角色菜单）：下一次请求立即收敛为拒绝
        var grant = db.SysRoleMenus.Single(rm => rm.RoleId == roleId && rm.MenuId == menuId);
        grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetPaged(new PageQuery(), null));
    }

    // ==================== 2. 新增：供应商 / 目的港 / 柜型 / 有界文本 ====================

    [Fact]
    public async Task 新增_供应商必须为正整数且真实可用_否则拒绝且不占单据号不落库()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedSupplier(db, SupplierDeleted, "已删除供应商", deleted: true);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(0)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(999999L)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(SupplierDeleted)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewPlan(SupplierOff)));

        // 失败发生在单据号生成与落库之前：不占流水、不落任何数据
        Assert.Empty(db.ContainerReceivingPlans);

        SeedSupplier(db, SupplierOk, "可用供应商");
        AssertOkObject(await ctl.Create(NewPlan(SupplierOk)));
        var stored = db.ContainerReceivingPlans.Single();
        Assert.False(string.IsNullOrWhiteSpace(stored.PlanNo));
        Assert.Equal(SupplierOk, stored.SupplierId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 新增_可选目的港_必须是启用港口字典项_留空放行()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI", status: 1);
        SeedOtherInfo(db, PortOff, "Port", "NINGBO", status: 0);
        SeedOtherInfo(db, PortDeleted, "Port", "DALIAN", status: 1, deleted: true);
        SeedOtherInfo(db, PortWrongType, "Currency", "USD", status: 1);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(SupplierOk, portId: 0)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(SupplierOk, portId: 999999L)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewPlan(SupplierOk, portId: PortDeleted)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewPlan(SupplierOk, portId: PortOff)));
        // 非港口类型的字典项 Id 不被采信
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(SupplierOk, portId: PortWrongType)));
        Assert.Empty(db.ContainerReceivingPlans);

        AssertOkObject(await ctl.Create(NewPlan(SupplierOk, portId: PortOk)));
        AssertOkObject(await ctl.Create(NewPlan(SupplierOk)));
        var stored = db.ContainerReceivingPlans.OrderBy(p => p.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(PortOk, stored[0].PortId);
        Assert.Null(stored[1].PortId);
    }

    [Fact]
    public async Task 新增_柜型必须是已知枚举_否则拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPlan(SupplierOk, containerType: (ContainerType)999)));

        Assert.Empty(db.ContainerReceivingPlans);
    }

    [Fact]
    public async Task 新增_文本字段超长_拒绝且不落库_合法文本去首尾空白()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPlan(SupplierOk, bookingNo: new string('B', 51))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPlan(SupplierOk, destination: new string('D', 201))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPlan(SupplierOk, remark: new string('R', 501))));
        Assert.Empty(db.ContainerReceivingPlans);

        AssertOkObject(await ctl.Create(NewPlan(SupplierOk,
            bookingNo: "  DG-LEGACY-1  ", containerNo: "  CONT-1  ",
            destination: "  Shanghai  ", remark: "  note  ")));

        var stored = db.ContainerReceivingPlans.Single();
        Assert.Equal("DG-LEGACY-1", stored.BookingNo);
        Assert.Equal("CONT-1", stored.ContainerNo);
        Assert.Equal("Shanghai", stored.Destination);
        Assert.Equal("note", stored.Remark);
    }

    [Fact]
    public async Task 新增_订柜单号只是legacy文本_绝不按单号推断订柜链接()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        // 预置一条同号订柜信息：收货计划仍只把 BookingNo 当文本，不做任何链接校验 / 写入
        db.ContainerBookings.Add(new ContainerBooking
        {
            BookingNo = "DG-SHARED", BookingDate = DateTime.Today, CustomerId = 1,
            Status = DocumentStatus.Approved
        });
        db.SaveChanges();
        var ctl = NewController(db, privileged);

        AssertOkObject(await ctl.Create(NewPlan(SupplierOk, bookingNo: "DG-SHARED")));
        // 没有任何订柜链接被推断：订柜记录保持原样，收货计划也不回写
        Assert.Equal("DG-SHARED", db.ContainerReceivingPlans.Single().BookingNo);
        Assert.Single(db.ContainerBookings);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single().Status);
    }

    // ==================== 3. 提交 / 审核：总件数必须为正 ====================

    [Fact]
    public async Task 提交_总件数为零或负数_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        var zero = SeedPlan(db, "RP-ZERO", SupplierOk, DocumentStatus.Pending, totalQuantity: 0m);
        var negative = SeedPlan(db, "RP-NEG", SupplierOk, DocumentStatus.Pending, totalQuantity: -5m);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Submit(zero.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Submit(negative.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == zero.Id).Status);
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == negative.Id).Status);
    }

    [Fact]
    public async Task 审核_总件数为零_拒绝且状态不变_正数可通过()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        var zero = SeedPlan(db, "RP-APP-ZERO", SupplierOk, DocumentStatus.Submitted, totalQuantity: 0m);
        var ok = SeedPlan(db, "RP-APP-OK", SupplierOk, DocumentStatus.Submitted, totalQuantity: 12m);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Approve(zero.Id));
        AssertOkObject(await ctl.Approve(ok.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Submitted, db.ContainerReceivingPlans.Single(p => p.Id == zero.Id).Status);
        Assert.Equal(DocumentStatus.Approved, db.ContainerReceivingPlans.Single(p => p.Id == ok.Id).Status);
    }

    [Fact]
    public async Task 提交审核_完整流程_行锁与状态机_正数数量()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI", status: 1);
        var plan = SeedPlan(db, "RP-FLOW", SupplierOk, DocumentStatus.Pending,
            totalQuantity: 3m, portId: PortOk);
        var ctl = NewController(db, privileged);

        AssertOkObject(await ctl.Submit(plan.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Submitted, db.ContainerReceivingPlans.Single(p => p.Id == plan.Id).Status);

        AssertOkObject(await ctl.Approve(plan.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Approved, db.ContainerReceivingPlans.Single(p => p.Id == plan.Id).Status);

        // 重复审核：状态已不是 Submitted，fail closed
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Approve(plan.Id));

        // 审核后仍可按既有取消流程取消
        AssertOkObject(await ctl.Cancel(plan.Id));
        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerReceivingPlans.Single(p => p.Id == plan.Id).Status);
    }

    // ==================== 4. 修改：完整校验先于赋值（失败不留痕） ====================

    [Fact]
    public async Task 修改_供应商已停用_完整校验失败_原单与状态完全不变()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI", status: 1);
        var plan = SeedPlan(db, "RP-EDIT", SupplierOk, DocumentStatus.Pending,
            totalQuantity: 10m, portId: PortOk, destination: "OLD-DEST", remark: "OLD");
        var ctl = NewController(db, privileged);

        // 拟议内容同时改动供应商 / 数量 / 目的港 / 柜型：完整校验先于赋值 ⇒ 整单拒绝且不改动库中实体
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Update(plan.Id, NewPlan(
            SupplierOff, portId: null, containerType: ContainerType.HQ45, totalQuantity: 999m,
            destination: "NEW-DEST", remark: "NEW")));

        db.ChangeTracker.Clear();
        var stored = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal(SupplierOk, stored.SupplierId);
        Assert.Equal(10m, stored.TotalQuantity);
        Assert.Equal(PortOk, stored.PortId);
        Assert.Equal(ContainerType.GP40, stored.ContainerType);
        Assert.Equal("OLD-DEST", stored.Destination);
        Assert.Equal("OLD", stored.Remark);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 修改_目的港过期或柜型非法或文本超长_原单不变()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI", status: 1);
        SeedOtherInfo(db, PortOff, "Port", "NINGBO", status: 0);
        var plan = SeedPlan(db, "RP-EDIT2", SupplierOk, DocumentStatus.Pending,
            totalQuantity: 7m, portId: PortOk, destination: "OLD-DEST");
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Update(plan.Id,
            NewPlan(SupplierOk, portId: PortOff, totalQuantity: 7m)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(plan.Id,
            NewPlan(SupplierOk, portId: PortOk, containerType: (ContainerType)777)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(plan.Id,
            NewPlan(SupplierOk, portId: PortOk, destination: new string('X', 201))));

        db.ChangeTracker.Clear();
        var stored = db.ContainerReceivingPlans.Single(p => p.Id == plan.Id);
        Assert.Equal(PortOk, stored.PortId);
        Assert.Equal(ContainerType.GP40, stored.ContainerType);
        Assert.Equal("OLD-DEST", stored.Destination);
        Assert.Equal(7m, stored.TotalQuantity);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 修改_合法内容_保存规范化值且非待提交拒绝修改()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedOtherInfo(db, PortOk, "Port", "SHANGHAI", status: 1);
        var pending = SeedPlan(db, "RP-EDIT-OK", SupplierOk, DocumentStatus.Pending, totalQuantity: 7m);
        var submitted = SeedPlan(db, "RP-EDIT-LOCK", SupplierOk, DocumentStatus.Submitted, totalQuantity: 7m);
        var ctl = NewController(db, privileged);

        AssertOkObject(await ctl.Update(pending.Id, NewPlan(SupplierOk,
            portId: PortOk, totalQuantity: 21m, bookingNo: "  DG-X  ", destination: "  NEW-DEST  ")));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Update(submitted.Id, NewPlan(SupplierOk, totalQuantity: 1m)));

        db.ChangeTracker.Clear();
        var stored = db.ContainerReceivingPlans.Single(p => p.Id == pending.Id);
        Assert.Equal(PortOk, stored.PortId);
        Assert.Equal(21m, stored.TotalQuantity);
        Assert.Equal("DG-X", stored.BookingNo);
        Assert.Equal("NEW-DEST", stored.Destination);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(7m, db.ContainerReceivingPlans.Single(p => p.Id == submitted.Id).TotalQuantity);
    }

    // ==================== 5. 状态变更 / 取消 / 删除：主数据与状态机 ====================

    [Fact]
    public async Task 状态变更_供应商或目的港已停用_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedOtherInfo(db, PortOff, "Port", "NINGBO", status: 0);
        var badSupplier = SeedPlan(db, "RP-T-SUP", SupplierOff, DocumentStatus.Pending, totalQuantity: 5m);
        var badPort = SeedPlan(db, "RP-T-PORT", SupplierOk, DocumentStatus.Submitted,
            totalQuantity: 5m, portId: PortOff);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Submit(badSupplier.Id));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Approve(badPort.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == badSupplier.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.ContainerReceivingPlans.Single(p => p.Id == badPort.Id).Status);
    }

    [Fact]
    public async Task 取消_任意状态可用_但主数据停用时拒绝()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        var ok = SeedPlan(db, "RP-CANCEL", SupplierOk, DocumentStatus.Submitted, totalQuantity: 1m);
        var blocked = SeedPlan(db, "RP-CANCEL-BAD", SupplierOff, DocumentStatus.Pending, totalQuantity: 1m);
        var ctl = NewController(db, privileged);

        AssertOkObject(await ctl.Cancel(ok.Id));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Cancel(blocked.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerReceivingPlans.Single(p => p.Id == ok.Id).Status);
        Assert.Equal(DocumentStatus.Pending, db.ContainerReceivingPlans.Single(p => p.Id == blocked.Id).Status);
    }

    [Fact]
    public async Task 删除_仅待提交可删_非待提交拒绝且不软删除()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOk, "可用供应商");
        var pending = SeedPlan(db, "RP-DEL-OK", SupplierOk, DocumentStatus.Pending, totalQuantity: 1m);
        var submitted = SeedPlan(db, "RP-DEL-LOCK", SupplierOk, DocumentStatus.Submitted, totalQuantity: 1m);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Delete(submitted.Id));
        AssertOkObject(await ctl.Delete(pending.Id));

        db.ChangeTracker.Clear();
        Assert.True(db.ContainerReceivingPlans.Single(p => p.Id == pending.Id).IsDeleted);
        Assert.False(db.ContainerReceivingPlans.Single(p => p.Id == submitted.Id).IsDeleted);
    }

    [Fact]
    public async Task 历史读取_主数据不可用_照常可读并给出显式证据()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedOtherInfo(db, PortOff, "Port", "NINGBO", status: 0);
        var plan = SeedPlan(db, "RP-EVIDENCE", SupplierOff, DocumentStatus.Pending,
            totalQuantity: 1m, portId: PortOff);
        var ctl = NewController(db, privileged);

        var page = AssertOkResponse<PagedResult<ContainerReceivingPlan>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        Assert.Single(page.Data!.Items);
        Assert.Contains(ReceivingPlanLifecycleRules.UnavailableEvidencePrefix, page.Message);
        Assert.Contains("已停用", page.Message);

        var detail = AssertOkResponse<ContainerReceivingPlan>(await ctl.GetById(plan.Id));
        Assert.Equal(plan.Id, detail.Data!.Id);
        Assert.Contains(ReceivingPlanLifecycleRules.UnavailableEvidencePrefix, detail.Message);
        // 历史读取绝不回填 / 改写主数据引用
        Assert.Equal(SupplierOff, detail.Data!.SupplierId);
        Assert.Equal(PortOff, detail.Data!.PortId);
    }

    // ==================== 6. 源码契约：纯只读规则 / 全部路由先授权 ====================

    [Fact]
    public void 规则_纯只读判定_不落库不新增权限_菜单与legacy文本口径()
    {
        var rules = ReadSource("ERP.Application/Services/ReceivingPlanLifecycleRules.cs");

        Assert.Contains("AsNoTracking", rules);
        Assert.DoesNotContain("SaveChanges", rules);              // 纯判定，不落库
        Assert.DoesNotContain("ContainerReceivingPlans.Add", rules);
        Assert.DoesNotContain(".Remove(", rules);
        Assert.DoesNotContain(".Update(", rules);
        Assert.DoesNotContain("HttpClient", rules);               // 不调用外部系统
        Assert.DoesNotContain("SysRoleMenus.Add", rules);         // 不新增用户授权 / 权限模型
        Assert.DoesNotContain("db.ContainerBookings", rules);     // 绝不反查订柜信息推断链接

        Assert.Equal("receiving-plan", ReceivingPlanLifecycleRules.RequiredMenuCode);
        Assert.Equal("Port", ReceivingPlanLifecycleRules.PortInfoType);
        Assert.Contains("未映射业务员", ReceivingPlanLifecycleRules.RuleText);
        Assert.Contains("绝不降级为全局 / 管理员可见", ReceivingPlanLifecycleRules.RuleText);
        Assert.Contains("legacy 文本", ReceivingPlanLifecycleRules.BoundaryText);
        Assert.Contains("不新增任何表 / 列 / 菜单 / 权限", ReceivingPlanLifecycleRules.BoundaryText);
    }

    [Fact]
    public void 控制器_全部路由先授权_列表先计数_状态变更共用行锁()
    {
        var controller = ReadSource("ERP.Api/Controllers/ContainerControllers.cs");

        // list / detail / create / update / submit / approve / cancel / delete（submit+approve 共用一次授权）
        Assert.True(Count(controller, "ReceivingPlanLifecycleRules.EnsureAuthorizedAsync") >= 7);
        Assert.Contains("ReceivingPlanLifecycleRules.ValidateProposedAsync", controller);
        Assert.Contains("ReceivingPlanLifecycleRules.ApplyValidated", controller);
        Assert.Contains("ReceivingPlanLifecycleRules.EnsureTransitionAllowedAsync", controller);
        Assert.Contains("ContainerReceivingPlans WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("[HttpPost(\"{id:long}/submit\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/approve\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/cancel\")]", controller);
        Assert.Contains("[HttpDelete(\"{id:long}\")]", controller);

        // 列表：授权严格先于计数 / 分页
        var auth = controller.IndexOf("ReceivingPlanLifecycleRules.EnsureAuthorizedAsync", StringComparison.Ordinal);
        var count = controller.IndexOf("var total = await source.CountAsync();", auth, StringComparison.Ordinal);
        Assert.True(auth >= 0 && count > auth);

        // 新增：完整校验严格先于单据号生成
        var create = controller.IndexOf(
            "public async Task<IActionResult> Create([FromBody] ContainerReceivingPlan entity)", StringComparison.Ordinal);
        var validate = controller.IndexOf("ReceivingPlanLifecycleRules.ValidateProposedAsync", create, StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.ReceivingPlan)", create, StringComparison.Ordinal);
        Assert.True(validate > create && generate > validate);

        // 修改：校验严格先于复制到被跟踪实体
        var update = controller.IndexOf(
            "public async Task<IActionResult> Update(long id, [FromBody] ContainerReceivingPlan entity)", StringComparison.Ordinal);
        var updateValidate = controller.IndexOf("ReceivingPlanLifecycleRules.ValidateProposedAsync", update, StringComparison.Ordinal);
        var apply = controller.IndexOf("ReceivingPlanLifecycleRules.ApplyValidated(existing, validated)", update, StringComparison.Ordinal);
        Assert.True(updateValidate > update && apply > updateValidate);

        // 授权仅继承基类 [Authorize]，不新增控制器级权限特性
        var type = typeof(ContainerReceivingPlanController);
        Assert.NotNull(type.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    // ==================== 脚手架与种子数据 ====================

    private static ContainerReceivingPlanController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerReceivingPlanController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerReceivingPlan NewPlan(
        long supplierId = SupplierOk, long? portId = null, ContainerType containerType = ContainerType.GP40,
        decimal totalQuantity = 1m, string bookingNo = "", string containerNo = "",
        string destination = "", string remark = "ERP-361_TEST")
        => new()
        {
            PlanDate = DateTime.Today,
            SupplierId = supplierId,
            PortId = portId,
            ContainerType = containerType,
            TotalQuantity = totalQuantity,
            BookingNo = bookingNo,
            ContainerNo = containerNo,
            Destination = destination,
            Remark = remark
        };

    private static void SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id, SupplierCode = $"RP-S-{id}", SupplierName = name, Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static void SeedOtherInfo(ErpDbContext db, long id, string infoType, string code,
        int status = 1, bool deleted = false)
    {
        db.BaseOtherInfos.Add(new BaseOtherInfo
        {
            Id = id, InfoType = infoType, InfoCode = code, InfoName = $"{infoType}-{code}",
            Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static ContainerReceivingPlan SeedPlan(ErpDbContext db, string no, long supplierId,
        DocumentStatus status = DocumentStatus.Pending, decimal totalQuantity = 1m, long? portId = null,
        string destination = "", string remark = "ERP-361_SEED")
    {
        var plan = new ContainerReceivingPlan
        {
            PlanNo = no, PlanDate = DateTime.Today, SupplierId = supplierId, Status = status,
            TotalQuantity = totalQuantity, PortId = portId, Destination = destination, Remark = remark
        };
        db.ContainerReceivingPlans.Add(plan);
        db.SaveChanges();
        return plan;
    }

    private static long SeedUser(ErpDbContext db, UserStatus status, bool deleted = false)
    {
        var user = new SysUser
        {
            UserName = $"rp-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "测试账号", Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>普通角色账号（未映射业务员）：可选授予既有「收货计划」菜单。</summary>
    private static long SeedMenuUser(ErpDbContext db, bool grantReceivingPlanMenu = false)
    {
        var user = new SysUser
        {
            UserName = $"rp-menu-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "菜单账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        if (!grantReceivingPlanMenu) return user.Id;

        var role = new SysRole { RoleName = "菜单角色", RoleCode = $"RpMenu-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        GrantReceivingPlanMenu(db, role.Id);
        return user.Id;
    }

    /// <summary>受限制业务员账号：员工映射（IsSalesman）+ 普通角色（菜单由调用方授予 / 撤销）。</summary>
    private static (long UserId, long EmployeeId, long RoleId) SeedRestrictedOperator(ErpDbContext db)
    {
        var code = $"rp-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "收货计划操作员", RoleCode = $"RpOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        return (user.Id, employee.Id, role.Id);
    }

    /// <summary>授予既有「收货计划」菜单（复用 SeedData 口径的菜单编码，不新增权限模型），返回菜单 Id。</summary>
    private static long GrantReceivingPlanMenu(ErpDbContext db, long roleId)
    {
        var menuId = db.SysMenus
            .Where(m => m.MenuCode == ReceivingPlanLifecycleRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id).FirstOrDefault();
        if (menuId == 0)
        {
            var menu = new SysMenu
            {
                MenuCode = ReceivingPlanLifecycleRules.RequiredMenuCode,
                MenuName = ReceivingPlanLifecycleRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            menuId = menu.Id;
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
        return menuId;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<T> AssertOkResponse<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var resp = AssertOkResponse<T>(result);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static void AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));

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







