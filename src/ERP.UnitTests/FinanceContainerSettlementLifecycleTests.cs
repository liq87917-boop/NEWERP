using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// 装柜结算单生命周期护栏（ERP-384）单元测试：覆盖实时启用身份 / 装柜结算单（container-settlement）菜单授权 /
/// 客户数据范围、金额（正总金额 / 非负费用，按既有存储精度取整，不做币种换算 / 合计公式）、
/// 可空来源装柜清单的精确资格（悬空 / 已删除 / 已取消 / 跨客户 / 多参与方非参与方一律拒绝，空来源保留历史语义）、
/// 被拒创建不消耗单号、被拒编辑 / 流转不改写原始字段（含审计时间戳）、提交 / 审核的持久化金额与范围复核、
/// 装柜清单取消被未取消结算单阻断及显式取消释放、费用分摊证据的混合客户范围护栏与规则契约。
/// 全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// <para>真实 SQL 的身份 / 来源护栏与两个独立连接竞态见
/// <c>ERP.IntegrationTests/FinanceContainerSettlementLifecycleSqlServerTests.cs</c>。</para>
/// </summary>
public class FinanceContainerSettlementLifecycleTests
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
            return Task.FromResult($"ZGS{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = new DateTime(2026, 9, 25),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingListParticipant SeedParticipant(
        ErpDbContext db, long loadingListId, BaseCustomer customer, bool primary = false, int status = 1)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = status
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    private static FinanceContainerSettlement SeedSettlement(
        ErpDbContext db, string settlementNo, long? loadingListId, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m, decimal otherCost = 0m,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 25),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 26, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceContainerSettlements.Add(settlement);
        db.SaveChanges();
        return settlement;
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string code, string name, string path)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>
    /// 播种一个操作员：<paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户，
    /// 且无需菜单授权）；为 <c>false</c> 时按既有业务员数据范围口径（<c>BaseEmployee.EmployeeCode</c> 映射）限制客户范围，
    /// 并显式授予既有「装柜结算单」（container-settlement）菜单。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true,
        bool withSettlementMenu = true)
    {
        var role = new SysRole
        {
            RoleName = "装柜结算操作角色",
            RoleCode = $"SettlementOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"settlement-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "装柜结算操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withSettlementMenu)
            GrantMenu(db, role.Id, FinanceContainerSettlementLifecycleRules.RequiredMenuCode,
                FinanceContainerSettlementLifecycleRules.RequiredMenuText, "/finance/container-settlement");

        return (user.Id, role.Id, userName);
    }

    private static void MapOperatorToEmployee(ErpDbContext db, string userName)
    {
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = userName,
            IsSalesman = true,
            Status = 1
        });
        db.SaveChanges();
    }

    private static void RevokeSettlementMenu(ErpDbContext db, long roleId)
    {
        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableUser(ErpDbContext db, long userId)
    {
        db.SysUsers.Single(u => u.Id == userId).Status = UserStatus.Disabled;
        db.SaveChanges();
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static FinanceContainerSettlement StoredSettlement(ErpDbContext db, long settlementId)
        => db.FinanceContainerSettlements.AsNoTracking().Single(s => s.Id == settlementId);

    private static void AssertSettlementUnchanged(ErpDbContext db, FinanceContainerSettlement expected)
    {
        var stored = StoredSettlement(db, expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.LoadingListId, stored.LoadingListId);
        Assert.Equal(expected.TotalAmount, stored.TotalAmount);
        Assert.Equal(expected.FreightCost, stored.FreightCost);
        Assert.Equal(expected.OtherCost, stored.OtherCost);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static FinanceContainerSettlementController BuildController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static ContainerLoadingListController BuildLoadingListController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static FinanceContainerSettlement NewSettlementRequest(
        long customerId, long? loadingListId = null, decimal totalAmount = 1000m,
        decimal freightCost = 0m, decimal otherCost = 0m)
        => new()
        {
            SettlementDate = new DateTime(2026, 9, 27),
            LoadingListId = loadingListId,
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            OtherCost = otherCost,
            Remark = "请求备注"
        };

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));


    // ==================== 1. 实时身份 / 菜单授权 / 客户数据范围 ====================

    [Fact]
    public async Task 未认证_列表详情创建全部拒绝_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "ZGS-1", null, customer.Id);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(NewSettlementRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => controller.GetExpenseAllocationEvidence(settlement.Id));

        Assert.Equal(0, no.Calls);
        Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 账号停用_全部路由拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "ZGS-1", null, customer.Id);
        DisableUser(db, userId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewSettlementRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(settlement.Id, NewSettlementRequest(customer.Id, totalAmount: 200m)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.GetExpenseAllocationEvidence(settlement.Id));

        Assert.Equal(0, no.Calls);
        AssertSettlementUnchanged(db, settlement);
    }

    [Fact]
    public async Task 撤销菜单授权_全部路由拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, _) = SeedOperator(db, privileged: false);
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "ZGS-1", null, customer.Id);
        RevokeSettlementMenu(db, roleId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewSettlementRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.GetExpenseAllocationEvidence(settlement.Id));

        Assert.Equal(0, no.Calls);
        AssertSettlementUnchanged(db, settlement);
    }

    [Fact]
    public async Task 未映射业务员的受限账号_fail_closed不降级为全局可见()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db, privileged: false);
        // 不映射员工：受限账号未映射业务员 → 可见客户为空（既有权衡，绝不降级为全局可见）
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "ZGS-1", null, customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceContainerSettlement>>>(result.Value);
        Assert.Equal(0, payload.Data!.Total);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(settlement.Id));
    }

    [Fact]
    public async Task 受限制业务员_列表仅返回自己客户_且范围外详情与状态路由拒绝不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 9999L);
        var own = SeedSettlement(db, "ZGS-OWN", null, ownCustomer.Id);
        var foreign = SeedSettlement(db, "ZGS-FOREIGN", null, foreignCustomer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceContainerSettlement>>>(result.Value);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(own.Id, Assert.Single(payload.Data.Items).Id);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(foreign.Id));
        AssertSettlementUnchanged(db, foreign);
    }


    // ==================== 2. 创建：金额（既有存储精度）/ 客户 / 来源清单 ====================

    [Fact]
    public async Task 创建_拒绝非正总金额与负费用_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: 0m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: -100m)));
        // 按既有存储精度（2 位）取整后为 0 → 拒绝
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, totalAmount: 0.004m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, freightCost: -0.5m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewSettlementRequest(customer.Id, otherCost: -1m)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_金额按既有精度取整_费用允许为零且不施加合计公式()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 100.456 → 100.46，10.555 → 10.56，0 → 0；总金额与费用之和不做任何约束（无币种字段 / 无合计公式）
        Assert.IsType<OkObjectResult>(await controller.Create(NewSettlementRequest(
            customer.Id, totalAmount: 100.456m, freightCost: 10.555m, otherCost: 0m)));

        var stored = Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList());
        Assert.Equal(100.46m, stored.TotalAmount);
        Assert.Equal(10.56m, stored.FreightCost);
        Assert.Equal(0m, stored.OtherCost);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.SettlementNo));
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 创建_不可用客户与越界客户_拒绝且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var deleted = SeedCustomer(db, "C-DEL", "已删除客户", deleted: true);
        var disabled = SeedCustomer(db, "C-DIS", "停用客户", status: 0);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 特权账号可见全部客户：不存在 / 已删除 → NotFound，停用 → RuleConflict
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(NewSettlementRequest(deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(NewSettlementRequest(disabled.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(NewSettlementRequest(999_999L)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());

        // 受限账号：范围外客户一致按「权限不足」fail closed（不泄露存在性），本人客户可创建
        var (limitedId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);
        var foreign = SeedCustomer(db, "C-FOR", "范围外客户", empId: 8888L);
        var mine = SeedCustomer(db, "C-MINE", "本人客户", empId: employee.Id);
        TestAuth.SetUser(controller, limitedId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewSettlementRequest(foreign.Id)));
        Assert.Equal(0, no.Calls);

        Assert.IsType<OkObjectResult>(await controller.Create(NewSettlementRequest(mine.Id)));
        Assert.Equal(1, no.Calls);
    }


    // ==================== 3. 创建：来源装柜清单精确资格（ERP-364 口径） ====================

    [Fact]
    public async Task 创建_来源清单_悬空已删除已取消拒绝_空来源放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var deleted = SeedLoadingList(db, "ZG-DEL", "TCLU-DEL", customer.Id, deleted: true);
        var cancelled = SeedLoadingList(db, "ZG-CAN", "TCLU-CAN", customer.Id, DocumentStatus.Cancelled);
        var eligible = SeedLoadingList(db, "ZG-OK", "TCLU-OK", customer.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewSettlementRequest(customer.Id, loadingListId: 999_999L)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewSettlementRequest(customer.Id, loadingListId: deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewSettlementRequest(customer.Id, loadingListId: cancelled.Id)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());

        // 空来源（历史未关联来源）与有效来源都放行
        Assert.IsType<OkObjectResult>(await controller.Create(NewSettlementRequest(customer.Id)));
        Assert.IsType<OkObjectResult>(await controller.Create(
            NewSettlementRequest(customer.Id, loadingListId: eligible.Id)));
        Assert.Equal(2, no.Calls);
        Assert.Equal(2, db.FinanceContainerSettlements.AsNoTracking().Count());
    }

    [Fact]
    public async Task 创建_来源清单_结算客户须为清单权威客户()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var single = SeedCustomer(db, "C-SINGLE", "单客户");
        var other = SeedCustomer(db, "C-OTHER", "他客户");
        var list = SeedLoadingList(db, "ZG-1", "TCLU-1", single.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 单客户清单：结算客户与清单客户不一致 → 拒绝
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewSettlementRequest(other.Id, loadingListId: list.Id)));
        Assert.Equal(0, no.Calls);

        // 多参与方清单：非参与方客户 → 拒绝；参与方之一 → 放行
        var multi = SeedLoadingList(db, "ZG-2", "TCLU-2", single.Id);
        SeedParticipant(db, multi.Id, single, primary: true);
        SeedParticipant(db, multi.Id, other);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(NewSettlementRequest(other.Id, loadingListId: list.Id)));
        Assert.Equal(0, no.Calls);

        Assert.IsType<OkObjectResult>(await controller.Create(
            NewSettlementRequest(other.Id, loadingListId: multi.Id)));
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 创建_来源清单_参与方越范围的受限账号拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-FOR", "范围外参与方", empId: 7777L);
        var shared = SeedLoadingList(db, "ZG-SHARED", "TCLU-SHARED", own.Id);
        SeedParticipant(db, shared.Id, own, primary: true);
        SeedParticipant(db, shared.Id, foreign);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 共享柜中其他客户不在范围内：以「本人客户」结算也必须拒绝（不泄露他人客户）
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Create(NewSettlementRequest(own.Id, loadingListId: shared.Id)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceContainerSettlements.AsNoTracking().ToList());

        // 单参与方（本人）清单可创建
        var solo = SeedLoadingList(db, "ZG-SOLO", "TCLU-SOLO", own.Id);
        SeedParticipant(db, solo.Id, own, primary: true);
        Assert.IsType<OkObjectResult>(await controller.Create(
            NewSettlementRequest(own.Id, loadingListId: solo.Id)));
        Assert.Equal(1, no.Calls);
    }


    // ==================== 4. 修改 / 提交 / 审核 / 取消 / 删除 ====================

    [Fact]
    public async Task 修改_仅待提交可改_并按精度取整_保留来源()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG-1", "TCLU-1", customer.Id);
        var pending = SeedSettlement(db, "ZGS-1", null, customer.Id, 1000m);
        var approved = SeedSettlement(db, "ZGS-APP", list.Id, customer.Id, 1000m,
            status: DocumentStatus.Approved);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(approved.Id, NewSettlementRequest(customer.Id, totalAmount: 200m)));
        AssertSettlementUnchanged(db, approved);

        Assert.IsType<OkObjectResult>(await controller.Update(pending.Id,
            NewSettlementRequest(customer.Id, loadingListId: list.Id, totalAmount: 123.456m,
                freightCost: 10.555m, otherCost: 1.005m)));
        var stored = StoredSettlement(db, pending.Id);
        Assert.Equal(123.46m, stored.TotalAmount);
        Assert.Equal(10.56m, stored.FreightCost);
        Assert.Equal(1.01m, stored.OtherCost);
        Assert.Equal(list.Id, stored.LoadingListId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 修改_越界客户与无效来源拒绝_被拒编辑不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-FOR", "范围外客户", empId: 6666L);
        var foreignList = SeedLoadingList(db, "ZG-FOR", "TCLU-FOR", foreign.Id);
        var cancelledList = SeedLoadingList(db, "ZG-CAN", "TCLU-CAN", own.Id, DocumentStatus.Cancelled);
        var pending = SeedSettlement(db, "ZGS-1", null, own.Id, 1000m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(pending.Id, NewSettlementRequest(foreign.Id, totalAmount: 300m)));
        AssertSettlementUnchanged(db, pending);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(pending.Id,
                NewSettlementRequest(own.Id, loadingListId: foreignList.Id, totalAmount: 300m)));
        AssertSettlementUnchanged(db, pending);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(pending.Id,
                NewSettlementRequest(own.Id, loadingListId: cancelledList.Id, totalAmount: 300m)));
        AssertSettlementUnchanged(db, pending);
    }

    [Fact]
    public async Task 提交审核_正常流转_保留商业字段与来源()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG-1", "TCLU-1", customer.Id);
        var settlement = SeedSettlement(db, "ZGS-1", list.Id, customer.Id, 1000m, 100m, 50m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(settlement.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredSettlement(db, settlement.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(settlement.Id));
        var stored = StoredSettlement(db, settlement.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(100m, stored.FreightCost);
        Assert.Equal(50m, stored.OtherCost);
        Assert.Equal(list.Id, stored.LoadingListId);
    }

    [Fact]
    public async Task 提交审核_状态不符或持久化金额非法拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedSettlement(db, "ZGS-1", null, customer.Id, 1000m);
        // 历史持久化金额非法（0）：提交不得放行（锁内复核，不写库）
        var legacyZero = SeedSettlement(db, "ZGS-ZERO", null, customer.Id, 0m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(pending.Id));
        AssertSettlementUnchanged(db, pending);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Submit(legacyZero.Id));
        AssertSettlementUnchanged(db, legacyZero);
    }

    [Fact]
    public async Task 取消_幂等_删除仅待提交_且不物理删除()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedSettlement(db, "ZGS-1", null, customer.Id);
        var approved = SeedSettlement(db, "ZGS-APP", null, customer.Id, status: DocumentStatus.Approved);
        var deletable = SeedSettlement(db, "ZGS-DEL", null, customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Cancel(pending.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredSettlement(db, pending.Id).Status);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(pending.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredSettlement(db, pending.Id).Status);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(approved.Id));
        AssertSettlementUnchanged(db, approved);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(pending.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredSettlement(db, pending.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Delete(deletable.Id));
        Assert.True(StoredSettlement(db, deletable.Id).IsDeleted);

        // 已删除的结算单不再可见
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(deletable.Id));
        Assert.Single(db.FinanceContainerSettlements.AsNoTracking().Where(s => !s.IsDeleted && s.Id == pending.Id));
    }


    // ==================== 5. 装柜清单取消与结算单占用协同 ====================

    [Fact]
    public async Task 装柜清单取消_存在未取消结算单拒绝_取消结算单后释放()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var list = SeedLoadingList(db, "ZG-1", "TCLU-1", customer.Id);
        var settlement = SeedSettlement(db, "ZGS-1", list.Id, customer.Id, status: DocumentStatus.Submitted);

        var loadingController = BuildLoadingListController(db);
        TestAuth.SetUser(loadingController, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => loadingController.Cancel(list.Id));
        Assert.Equal(DocumentStatus.Pending,
            db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == list.Id).Status);

        // 显式取消结算单释放护栏（历史与审计原样保留，不物理删除）
        var settlementController = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(settlementController, userId);
        Assert.IsType<OkObjectResult>(await settlementController.Cancel(settlement.Id));

        Assert.IsType<OkObjectResult>(await loadingController.Cancel(list.Id));
        Assert.Equal(DocumentStatus.Cancelled,
            db.ContainerLoadingLists.AsNoTracking().Single(l => l.Id == list.Id).Status);
        Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 装柜清单取消_已删除或已取消结算单不阻断()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var deletedList = SeedLoadingList(db, "ZG-DEL", "TCLU-DEL", customer.Id);
        SeedSettlement(db, "ZGS-DEL", deletedList.Id, customer.Id, deleted: true);
        var cancelledList = SeedLoadingList(db, "ZG-CAN", "TCLU-CAN", customer.Id);
        SeedSettlement(db, "ZGS-CAN", cancelledList.Id, customer.Id, status: DocumentStatus.Cancelled);

        var loadingController = BuildLoadingListController(db);
        TestAuth.SetUser(loadingController, userId);

        Assert.IsType<OkObjectResult>(await loadingController.Cancel(deletedList.Id));
        Assert.IsType<OkObjectResult>(await loadingController.Cancel(cancelledList.Id));
    }


    // ==================== 6. 费用分摊证据的混合客户范围护栏 ====================

    private static FinanceExpense SeedSourceExpense(ErpDbContext db, string expenseNo, string containerNo, decimal amount)
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = new DateTime(2026, 9, 25),
            ExpenseType = "报关费",
            Amount = amount,
            Currency = "CNY",
            ExchangeRate = 1m,
            AmountCny = amount,
            RefType = "拼柜",
            RefNo = containerNo,
            AllocationBase = "不分摊",
            AllocationRatio = 0m,
            AllocatedAmount = 0m,
            AllocationBatchNo = string.Empty,
            PaymentStatus = "未付"
        };
        db.FinanceExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    private static FinanceExpenseAllocationLine SeedAllocationLine(
        ErpDbContext db, ContainerLoadingList list, BaseCustomer customer)
    {
        var source = SeedSourceExpense(db, $"EXP-{Guid.NewGuid():N}"[..20], list.ContainerNo, 100m);
        var participant = SeedParticipant(db, list.Id, customer, status: ContainerLoadingParticipantRules.DisabledStatus);
        var batch = new FinanceExpenseAllocationBatch
        {
            BatchNo = $"EAB-{Guid.NewGuid():N}"[..20],
            SourceExpenseId = source.Id,
            SourceExpenseNo = source.ExpenseNo,
            LoadingListId = list.Id,
            LoadingListNo = list.LoadingListNo,
            ContainerNo = list.ContainerNo,
            AllocationMethod = "按体积",
            BasisKind = "按体积",
            Currency = "CNY",
            ExchangeRate = 1m,
            SourceAmount = 100m,
            AllocatedTotal = 100m,
            LineCount = 0,
            Status = 1
        };
        db.FinanceExpenseAllocationBatches.Add(batch);
        db.SaveChanges();

        var line = new FinanceExpenseAllocationLine
        {
            BatchId = batch.Id,
            BatchNo = batch.BatchNo,
            SourceExpenseId = source.Id,
            SourceExpenseNo = source.ExpenseNo,
            LoadingListId = list.Id,
            LoadingListNo = list.LoadingListNo,
            ContainerNo = list.ContainerNo,
            ParticipantId = participant.Id,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            ParticipantPrimary = false,
            AllocationMethod = "按体积",
            BasisKind = "按体积",
            BasisSource = "用户确认请求值",
            BasisValue = 1m,
            Ratio = 1m,
            AllocatedAmount = 100m,
            AllocatedAmountCny = 100m,
            Currency = "CNY",
            ExpenseNo = source.ExpenseNo,
            SortOrder = 0
        };
        db.FinanceExpenseAllocationLines.Add(line);
        batch.LineCount = 1;
        db.SaveChanges();
        return line;
    }

    [Fact]
    public async Task 证据_混合客户分摊行含范围外客户时拒绝_范围内放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-FOR", "范围外客户", empId: 5555L);

        // 单客户清单（无有效参与方）：清单范围复核通过，但分摊行引用了范围外客户 → 证据读取拒绝
        var list = SeedLoadingList(db, "ZG-MIX", "TCLU-MIX", own.Id);
        SeedAllocationLine(db, list, foreign);
        var settlement = SeedSettlement(db, "ZGS-MIX", list.Id, own.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var ex = await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.GetExpenseAllocationEvidence(settlement.Id));
        Assert.Contains("费用分摊证据", ex.Message);

        // 范围内客户的分摊行：证据可读
        var cleanList = SeedLoadingList(db, "ZG-CLEAN", "TCLU-CLEAN", own.Id);
        SeedAllocationLine(db, cleanList, own);
        var cleanSettlement = SeedSettlement(db, "ZGS-CLEAN", cleanList.Id, own.Id);

        Assert.IsType<OkObjectResult>(await controller.GetExpenseAllocationEvidence(cleanSettlement.Id));
        Assert.Single(db.FinanceContainerSettlements.AsNoTracking().ToList(), s => s.Id == cleanSettlement.Id);
    }


    // ==================== 7. 规则 / 控制器源码契约 ====================

    [Fact]
    public void 规则契约_金额精度_锁Id合并与文案()
    {
        Assert.Equal("container-settlement", FinanceContainerSettlementLifecycleRules.RequiredMenuCode);
        Assert.Equal("装柜结算单", FinanceContainerSettlementLifecycleRules.RequiredMenuText);
        Assert.Equal(2, FinanceContainerSettlementLifecycleRules.AmountDecimals);

        // 0.5 进位（既有存储精度 2 位）
        Assert.Equal(1.01m, FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(1.005m));
        Assert.Equal(0.02m, FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(0.015m));
        Assert.Equal(0m, FinanceContainerSettlementLifecycleRules.NormalizeCost(-0.001m, "海运费"));

        // 取整后非正 / 负费用拒绝
        Assert.Throws<BusinessException>(() => FinanceContainerSettlementLifecycleRules.NormalizeTotalAmount(0.004m));
        Assert.Throws<BusinessException>(() => FinanceContainerSettlementLifecycleRules.NormalizeCost(-0.5m, "海运费"));

        // 锁 Id：去重、剔除非正、按 Id 升序
        var ids = FinanceContainerSettlementLifecycleRules.MergeLoadingListLockIds(5, null, 3, 5, 0, -1);
        Assert.Equal(new long[] { 3, 5 }, ids);

        // 锁序 / 边界 / 口径文案同源非空
        Assert.False(string.IsNullOrWhiteSpace(FinanceContainerSettlementLifecycleRules.RuleText));
        Assert.False(string.IsNullOrWhiteSpace(FinanceContainerSettlementLifecycleRules.BoundaryText));
        Assert.Contains("装柜清单行", FinanceContainerSettlementLifecycleRules.LockOrderText);
        Assert.Contains("装柜结算单", FinanceContainerSettlementLifecycleRules.RuleText);
        Assert.Contains("库存", FinanceContainerSettlementLifecycleRules.BoundaryText);
    }

    [Fact]
    public void 控制器源码契约_先授权后写入且写路由先取来源清单行锁()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceSettlementControllers.cs"));
        var loadingController = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "ContainerLoadingListController.cs"));

        // 实时授权（身份 / 菜单）在计数、单号生成、证据读取与写入之前
        var menuCheck = controller.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal);
        Assert.True(menuCheck >= 0);
        Assert.True(menuCheck < controller.IndexOf("GenerateAsync(DocumentType.ContainerSettlement)", StringComparison.Ordinal));
        Assert.True(menuCheck < controller.IndexOf("GetForSettlementAsync", StringComparison.Ordinal));
        Assert.True(menuCheck < controller.IndexOf("Db.FinanceContainerSettlements.Add", StringComparison.Ordinal));

        // 写路由：来源装柜清单行锁先于结算单行锁
        Assert.Contains("LockLoadingListRowsAsync", controller);
        Assert.True(controller.IndexOf("LockLoadingListRowsAsync", StringComparison.Ordinal)
                    < controller.IndexOf("LockSettlementRowAsync", StringComparison.Ordinal));
        Assert.Contains("MergeLoadingListLockIds", controller);
        Assert.Contains("EnsureStoredSettlementScopeAllowedAsync", controller);
        Assert.Contains("EnsureEvidenceCustomersInScopeAsync", controller);

        // 装柜清单取消协同：未取消结算单阻断取消
        Assert.Contains("EnsureNoActiveSettlementForLoadingListAsync", loadingController);
    }
}
