using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-392 装柜结算单来源装柜清单候选 / 已存储来源展示 + 表单显式选择的单元测试（内存库，不连接 SQL Server）。
/// <para>覆盖：按<b>精确客户</b>的有界分页候选（只含未删除、未取消，且该客户确实是有效参与方或历史兼容客户的装柜清单）、
/// 共享柜越范围参与方 / 越范围上游客户整张不返回（范围先于计数）、关键字 / 分页<b>先归一化再计数</b>、
/// 既有「装柜结算单」+「装柜清单」菜单与实时客户数据范围的 fail closed（不新增用户授权、无匿名 / 管理员兜底）、
/// 候选只读投影不写库、最终保存按 <see cref="FinanceContainerSettlementLifecycleRules"/> <b>锁内复核精确来源</b>
/// （伪造 / 已取消 / 跨客户来源被拒绝且不消耗单号）、已存储来源在取消 / 删除后只读可读并显式标注、显式断开链接、
/// 以及控制器 / 服务 / 前端接入源码契约。</para>
/// </summary>
public class ContainerSettlementLoadingSourceTests
{
    // ==================== 0. 测试脚手架 ====================

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗结算单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            return Task.FromResult($"JS{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = status,
            CreditStatus = "正常", EmpId = empId, IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false, long? preLoadingId = null)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo, LoadingDate = new DateTime(2026, 9, 18), ContainerNo = containerNo,
            CustomerId = customerId, Status = status, IsDeleted = deleted, PreLoadingId = preLoadingId
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingListParticipant SeedParticipant(
        ErpDbContext db, ContainerLoadingList list, BaseCustomer customer,
        bool primary = false, int status = 1, bool deleted = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = list.Id, CustomerId = customer.Id, CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName, IsPrimary = primary, Status = status, IsDeleted = deleted
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string bookingNo, long customerId)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo, BookingDate = new DateTime(2026, 9, 10), CustomerId = customerId
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string preLoadingNo, long? bookingId)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = preLoadingNo, LoadingDate = new DateTime(2026, 9, 12), BookingId = bookingId
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        return pre;
    }

    private static FinanceContainerSettlement SeedSettlement(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount = 1000m, DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo, SettlementDate = new DateTime(2026, 9, 20), LoadingListId = loadingListId,
            CustomerId = customerId, TotalAmount = totalAmount, FreightCost = 0m, OtherCost = 0m,
            Remark = "原始备注", Status = status, IsDeleted = deleted
        };
        db.FinanceContainerSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }

    private static SysMenu EnsureMenu(ErpDbContext db, string code, string name, string path, int sortOrder)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            SortOrder = sortOrder, MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个真实业务的非特权账号：既有「装柜结算单」/「装柜清单」菜单授权 + 业务员员工映射
    /// （把范围内客户分配给它的员工），数据范围恰好覆盖该客户；可选禁用账号 / 不授予菜单用于 fail closed 场景。
    /// 不新增权限模型。
    /// </summary>
    private static long SeedOperator(
        ErpDbContext db, long inScopeCustomerId, bool settlementMenu = true, bool loadingListMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var role = new SysRole { RoleCode = $"CSLS-{Tag()}", RoleName = "装柜来源操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"csls-{Tag()}";
        var user = new SysUser
        {
            UserName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "装柜来源操作员", Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "装柜来源操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var customer = db.BaseCustomers.Single(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (settlementMenu)
        {
            var menu = EnsureMenu(db, FinanceContainerSettlementLifecycleRules.RequiredMenuCode,
                FinanceContainerSettlementLifecycleRules.RequiredMenuText, "/finance/container-settlements", 72);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        if (loadingListMenu)
        {
            var menu = EnsureMenu(db, LoadingListAuthorizationRules.RequiredMenuCode,
                LoadingListAuthorizationRules.RequiredMenuText, "/container/loading-lists", 45);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return user.Id;
    }

    private static FinanceContainerSettlementController BuildController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var controller = new FinanceContainerSettlementController(db, noService ?? new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static ContainerSettlementLoadingListCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<ContainerSettlementLoadingListCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static ContainerSettlementLoadingSourceViewDto View(IActionResult result)
        => Assert.IsType<ApiResponse<ContainerSettlementLoadingSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static FinanceContainerSettlement Request(long customerId, long? loadingListId, decimal totalAmount = 800m)
        => new()
        {
            SettlementDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            LoadingListId = loadingListId,
            TotalAmount = totalAmount,
            FreightCost = 0m,
            OtherCost = 0m,
            Remark = "请求备注"
        };

    // ==================== 1. 归一化纯函数（先归一化再计数） ====================

    [Fact]
    public void 归一化_关键字页码每页条数均有界()
    {
        Assert.Equal("CL-1", ContainerSettlementLoadingSourceService.NormalizeKeyword("  CL-1  "));
        Assert.Equal(string.Empty, ContainerSettlementLoadingSourceService.NormalizeKeyword(null));
        Assert.Equal(100, ContainerSettlementLoadingSourceService.NormalizeKeyword(new string('x', 150)).Length);

        Assert.Equal(1, ContainerSettlementLoadingSourceService.NormalizePage(0));
        Assert.Equal(1, ContainerSettlementLoadingSourceService.NormalizePage(-5));
        Assert.Equal(7, ContainerSettlementLoadingSourceService.NormalizePage(7));

        Assert.Equal(ContainerSettlementLoadingSourceService.DefaultPageSize,
            ContainerSettlementLoadingSourceService.NormalizePageSize(0));
        Assert.Equal(ContainerSettlementLoadingSourceService.MaxPageSize,
            ContainerSettlementLoadingSourceService.NormalizePageSize(9999));
        Assert.Equal(15, ContainerSettlementLoadingSourceService.NormalizePageSize(15));
    }

    // ==================== 2. 精确客户候选：参与方 / 历史兼容客户 / 状态 ====================

    [Fact]
    public async Task 候选_只返回精确客户有效参与方或历史兼容客户的未删除未取消清单()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = TestAuth.SeedPrivilegedUser(db);

        var legacy = SeedLoadingList(db, "CL-A-LEGACY", "TCNU-1", customerA.Id);
        var shared = SeedLoadingList(db, "CL-AB-SHARED", "TCNU-2", customerB.Id);
        SeedParticipant(db, shared, customerA, primary: true);
        SeedParticipant(db, shared, customerB);
        var foreign = SeedLoadingList(db, "CL-B-FOREIGN", "TCNU-3", customerB.Id);
        SeedParticipant(db, foreign, customerB, primary: true);
        var cancelled = SeedLoadingList(db, "CL-A-CANCELLED", "TCNU-4", customerA.Id, DocumentStatus.Cancelled);
        var deleted = SeedLoadingList(db, "CL-A-DELETED", "TCNU-5", customerA.Id, DocumentStatus.Pending, deleted: true);

        var page = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, null, 0, 0));

        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, i => i.LoadingListId == legacy.Id);
        Assert.Contains(page.Items, i => i.LoadingListId == shared.Id);
        Assert.DoesNotContain(page.Items, i => i.LoadingListId == foreign.Id);
        Assert.DoesNotContain(page.Items, i => i.LoadingListId == cancelled.Id);
        Assert.DoesNotContain(page.Items, i => i.LoadingListId == deleted.Id);

        var legacyRow = page.Items.Single(i => i.LoadingListId == legacy.Id);
        Assert.True(legacyRow.Eligible);
        Assert.True(legacyRow.LegacySingleCustomer);
        Assert.Equal(0, legacyRow.ActiveParticipantCount);
        Assert.Equal(customerA.Id, legacyRow.CustomerId);
        Assert.Equal("客户A", legacyRow.CustomerName);
        Assert.Equal("TCNU-1", legacyRow.ContainerNo);

        var sharedRow = page.Items.Single(i => i.LoadingListId == shared.Id);
        Assert.False(sharedRow.LegacySingleCustomer);
        Assert.Equal(2, sharedRow.ActiveParticipantCount);
    }

    [Fact]
    public async Task 候选_客户不是有效参与方时历史兼容字段不冒充归属()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = TestAuth.SeedPrivilegedUser(db);

        // 清单有参与方（仅 B），兼容字段却写 A：权威归属是参与方集合，A 不是参与方 → 不返回（绝不按兼容字段冒充）
        var list = SeedLoadingList(db, "CL-B-PARTICIPANT", "TCNU-9", customerA.Id);
        SeedParticipant(db, list, customerB, primary: true);

        var page = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, null, 0, 0));
        Assert.DoesNotContain(page.Items, i => i.LoadingListId == list.Id);
    }

    // ==================== 3. 共享柜越范围参与方 / 越范围上游客户整张不返回（范围先于计数） ====================

    [Fact]
    public async Task 候选_受限账号排除共享柜越范围参与方与越范围上游客户()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);

        var ok = SeedLoadingList(db, "CL-A-OK", "TCNU-1", customerA.Id);

        var sharedOutOfScope = SeedLoadingList(db, "CL-AB-SHARED", "TCNU-2", customerA.Id);
        SeedParticipant(db, sharedOutOfScope, customerA, primary: true);
        SeedParticipant(db, sharedOutOfScope, customerB);

        var booking = SeedBooking(db, "BK-B", customerB.Id);
        var pre = SeedPreLoading(db, "PL-B", booking.Id);
        var upstreamOutOfScope = SeedLoadingList(db, "CL-A-UPSTREAM", "TCNU-3", customerA.Id, preLoadingId: pre.Id);

        var page = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, null, 0, 0));

        Assert.Equal(1, page.Total);
        Assert.Contains(page.Items, i => i.LoadingListId == ok.Id);
        Assert.DoesNotContain(page.Items, i => i.LoadingListId == sharedOutOfScope.Id);
        Assert.DoesNotContain(page.Items, i => i.LoadingListId == upstreamOutOfScope.Id);
    }

    [Fact]
    public async Task 候选_范围内共享柜返回并带出上游归属()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = TestAuth.SeedPrivilegedUser(db);

        var booking = SeedBooking(db, "BK-A", customerA.Id);
        var pre = SeedPreLoading(db, "PL-A", booking.Id);
        var list = SeedLoadingList(db, "CL-A-UPSTREAM", "TCNU-7", customerA.Id, preLoadingId: pre.Id);

        var page = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, null, 0, 0));
        var row = page.Items.Single(i => i.LoadingListId == list.Id);
        Assert.Equal(pre.Id, row.PreLoadingId);
        Assert.Equal(customerA.Id, row.UpstreamBookingCustomerId);
    }

    // ==================== 4. 关键字 / 分页先归一化再计数 ====================

    [Fact]
    public async Task 候选_关键字与分页先归一化再有界返回()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = TestAuth.SeedPrivilegedUser(db);

        for (var i = 1; i <= 5; i++)
            SeedLoadingList(db, $"CL-A-{i:D2}", $"TCNU-{i:D2}", customerA.Id);
        SeedLoadingList(db, "CL-B-OTHER", "TCNU-99", customerA.Id, deleted: true);

        var first = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, "  CL-A-  ", 0, 0));
        Assert.Equal(5, first.Total);
        Assert.Equal(ContainerSettlementLoadingSourceService.DefaultPageSize, first.PageSize);
        Assert.Equal(1, first.Page);
        Assert.Equal(5, first.Items.Count);

        var second = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, null, 2, 2));
        Assert.Equal(5, second.Total);
        Assert.Equal(2, second.Items.Count);

        // 越界分页参数被归一化（页码 0 → 1，每页条数 9999 → 上限）
        var bounded = Candidates(await BuildController(db, userId).GetLoadingListCandidates(customerA.Id, null, -3, 9999));
        Assert.Equal(1, bounded.Page);
        Assert.Equal(ContainerSettlementLoadingSourceService.MaxPageSize, bounded.PageSize);
    }

    // ==================== 5. 实时授权 fail closed（无匿名 / 管理员兜底） ====================

    private static async Task<BusinessException> AssertCodeAsync(int expected, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
        return ex;
    }

    [Fact]
    public async Task 候选_缺失身份与菜单或禁用账号一律fail_closed()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        SeedLoadingList(db, "CL-A", "TCNU-1", customerA.Id);

        var anonymous = BuildController(db, null);
        await AssertCodeAsync(ErrorCodes.Unauthorized,
            () => anonymous.GetLoadingListCandidates(customerA.Id, null, 0, 0));

        var noSettlementMenu = BuildController(db, SeedOperator(db, customerA.Id, settlementMenu: false));
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => noSettlementMenu.GetLoadingListCandidates(customerA.Id, null, 0, 0));

        var noLoadingListMenu = BuildController(db, SeedOperator(db, customerA.Id, loadingListMenu: false));
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => noLoadingListMenu.GetLoadingListCandidates(customerA.Id, null, 0, 0));

        var disabled = BuildController(db, SeedOperator(db, customerA.Id, status: UserStatus.Disabled));
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => disabled.GetLoadingListCandidates(customerA.Id, null, 0, 0));
    }

    [Fact]
    public async Task 候选_缺少精确客户或客户越范围一律fail_closed()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        SeedLoadingList(db, "CL-A", "TCNU-1", customerA.Id);

        var ctl = BuildController(db, SeedOperator(db, customerA.Id));

        await AssertCodeAsync(ErrorCodes.InvalidParameter,
            () => ctl.GetLoadingListCandidates(0, null, 0, 0));
        await AssertCodeAsync(ErrorCodes.InvalidParameter,
            () => ctl.GetLoadingListCandidates(-5, null, 0, 0));
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ctl.GetLoadingListCandidates(customerB.Id, null, 0, 0));
    }

    // ==================== 6. 已存储来源只读展示：显式标注（历史可读） ====================

    [Fact]
    public async Task 已存储来源_未关联已关联取消不可用均显式标注()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = TestAuth.SeedPrivilegedUser(db);

        var listOk = SeedLoadingList(db, "CL-A-OK", "TCNU-1", customerA.Id);
        var listCancelled = SeedLoadingList(db, "CL-A-CANCEL", "TCNU-2", customerA.Id, DocumentStatus.Cancelled);
        var listDeleted = SeedLoadingList(db, "CL-A-GONE", "TCNU-3", customerA.Id, DocumentStatus.Pending, deleted: true);

        var unlinked = SeedSettlement(db, "JS-U", null, customerA.Id);
        var linked = SeedSettlement(db, "JS-L", listOk.Id, customerA.Id);
        var cancelled = SeedSettlement(db, "JS-C", listCancelled.Id, customerA.Id);
        var unavailable = SeedSettlement(db, "JS-D", listDeleted.Id, customerA.Id);
        var missing = SeedSettlement(db, "JS-M", 9_999_999, customerA.Id);

        var ctl = BuildController(db, userId);

        var unlinkedView = View(await ctl.GetLoadingListSource(unlinked.Id));
        Assert.False(unlinkedView.Linked);
        Assert.Contains("未关联", unlinkedView.Annotation);

        var linkedView = View(await ctl.GetLoadingListSource(linked.Id));
        Assert.True(linkedView.Linked);
        Assert.True(linkedView.EligibleForNewLink);
        Assert.False(linkedView.Unavailable);
        Assert.Equal("CL-A-OK", linkedView.LoadingListNo);

        var cancelledView = View(await ctl.GetLoadingListSource(cancelled.Id));
        Assert.True(cancelledView.Linked);
        Assert.True(cancelledView.Cancelled);
        Assert.False(cancelledView.EligibleForNewLink);
        Assert.Contains("已取消", cancelledView.Annotation);

        var unavailableView = View(await ctl.GetLoadingListSource(unavailable.Id));
        Assert.True(unavailableView.Unavailable);
        Assert.False(unavailableView.EligibleForNewLink);
        Assert.Contains("不可用", unavailableView.Annotation);

        var missingView = View(await ctl.GetLoadingListSource(missing.Id));
        Assert.True(missingView.Unavailable);
        Assert.Contains("不可用", missingView.Annotation);
    }

    // ==================== 7. 最终保存仍走 ERP-384 锁内复核（候选选择不等于授权） ====================

    [Fact]
    public async Task 保存_伪造取消或跨客户来源被拒绝且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);
        var noService = new FakeDocumentNumberService();
        var ctl = BuildController(db, userId, noService);

        var foreign = SeedLoadingList(db, "CL-B-FOREIGN", "TCNU-1", customerB.Id);
        var cancelled = SeedLoadingList(db, "CL-A-CANCELLED", "TCNU-2", customerA.Id, DocumentStatus.Cancelled);
        var ok = SeedLoadingList(db, "CL-A-OK", "TCNU-3", customerA.Id);

        // 跨客户来源：越范围 fail closed，且不消耗单号、不落半成品
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Create(Request(customerA.Id, foreign.Id)));
        Assert.Equal(0, noService.Calls);
        Assert.Empty(db.FinanceContainerSettlements.Where(s => s.CustomerId == customerA.Id).ToList());

        // 已取消来源：规则冲突，同样不消耗单号
        await AssertCodeAsync(ErrorCodes.RuleConflict, () => ctl.Create(Request(customerA.Id, cancelled.Id)));
        Assert.Equal(0, noService.Calls);

        // 精确来源可用：真正写入且消耗一次单号
        Assert.IsType<OkObjectResult>(await ctl.Create(Request(customerA.Id, ok.Id)));
        Assert.Equal(1, noService.Calls);

        // 空来源保留历史「未关联来源」语义
        Assert.IsType<OkObjectResult>(await ctl.Create(Request(customerA.Id, null)));
        Assert.Equal(2, noService.Calls);
    }

    [Fact]
    public async Task 修改_来源越范围被拒绝且不改写原链接()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);

        var ok = SeedLoadingList(db, "CL-A-OK", "TCNU-1", customerA.Id);
        var foreign = SeedLoadingList(db, "CL-B-FOREIGN", "TCNU-2", customerB.Id);
        var settlement = SeedSettlement(db, "JS-X", ok.Id, customerA.Id);

        var ctl = BuildController(db, userId);
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ctl.Update(settlement.Id, Request(customerA.Id, foreign.Id)));

        // 被拒更新绝不静默重绑定：原来源链接原样保留
        var stored = db.FinanceContainerSettlements.AsNoTracking().Single(s => s.Id == settlement.Id);
        Assert.Equal(ok.Id, stored.LoadingListId);
    }

    // ==================== 8. 源码契约：控制器 / 服务 / 前端接入 ====================

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NEWERP.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    [Fact]
    public void 源码契约_候选端点先授权后取数_归一化先于计数_前端接入声明式选择器()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceSettlementControllers.cs"));
        var container = controller[controller.IndexOf(
            "public class FinanceContainerSettlementController", StringComparison.Ordinal)..];
        container = container[..container.IndexOf(
            "public class FinanceBulkSettlementController", StringComparison.Ordinal)];

        Assert.Contains("[HttpGet(\"loading-list-candidates\")]", container);
        Assert.Contains("GetLoadingListCandidates", container);
        Assert.Contains("[HttpGet(\"{id:long}/loading-list-source\")]", container);
        Assert.Contains("ContainerSettlementLoadingSourceService.QueryCandidatesAsync", container);
        Assert.Contains("ContainerSettlementLoadingSourceService.DescribeStoredSourceAsync", container);

        // 授权（既有装柜结算单菜单 + 既有装柜清单菜单 + 客户数据范围）先于候选取数
        var menuCheck = container.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal);
        var loadingMenuCheck = container.IndexOf(
            "LoadingListAuthorizationRules.EnsureAuthorizedAsync", StringComparison.Ordinal);
        var scopeCheck = container.IndexOf("EnsureCustomerInScope", StringComparison.Ordinal);
        var query = container.IndexOf("QueryCandidatesAsync", StringComparison.Ordinal);
        Assert.True(menuCheck >= 0 && loadingMenuCheck >= 0 && scopeCheck >= 0 && query >= 0);
        Assert.True(menuCheck < query);
        Assert.True(loadingMenuCheck < query);
        Assert.True(scopeCheck < query);

        // 最终保存仍走 ERP-384 生命周期规则锁内复核精确来源（候选选择不等于授权）
        Assert.Contains("ResolveAndAuthorizeLoadingListAsync", container);

        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "ContainerSettlementLoadingSourceService.cs"));
        var count = service.IndexOf("var total = await source.CountAsync", StringComparison.Ordinal);
        Assert.True(count >= 0);
        Assert.True(service.IndexOf("ApplyAuthoritativeCustomerMembership", StringComparison.Ordinal) < count);
        Assert.True(service.IndexOf("ApplyScope", StringComparison.Ordinal) < count);
        Assert.True(service.IndexOf("NormalizeKeyword", StringComparison.Ordinal) < count);
        Assert.True(service.IndexOf("NormalizePageSize", StringComparison.Ordinal) < count);

        var modules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-finance.js"));
        Assert.Contains("selector: 'container-settlement-loading-source'", modules);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/container-settlement-loading-source.js", index);
    }
}
