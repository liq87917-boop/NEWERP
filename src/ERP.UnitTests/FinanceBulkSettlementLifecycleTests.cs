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
/// 散货结算单生命周期护栏（ERP-387）单元测试：覆盖实时启用身份 / 散货结算单（bulk-settlement）菜单授权 /
/// 客户数据范围、金额（正总金额 / 非负海运费，按既有存储精度取整，不做币种换算 / 合计公式）、
/// 真实启用客户校验（存在 / 未删除 / 启用）、被拒创建不消耗单号、被拒编辑 / 流转不改写原始字段（含审计时间戳）、
/// 提交 / 审核的持久化金额与范围复核、取消幂等 / 软删除仅待提交，以及规则与控制器源码契约。
/// 全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// <para>真实 SQL 的身份 / 金额护栏与两个独立连接竞态见
/// <c>ERP.IntegrationTests/FinanceBulkSettlementLifecycleSqlServerTests.cs</c>。</para>
/// </summary>
public class FinanceBulkSettlementLifecycleTests
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
            return Task.FromResult($"SJ{DateTime.Now:yyyyMMdd}{++_seq:D4}");
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

    private static FinanceBulkSettlement SeedSettlement(
        ErpDbContext db, string settlementNo, long customerId,
        decimal totalAmount = 1000m, decimal freightCost = 0m,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var settlement = new FinanceBulkSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 26, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceBulkSettlements.Add(settlement);
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
    /// 并显式授予既有「散货结算单」（bulk-settlement）菜单。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true,
        bool withSettlementMenu = true)
    {
        var role = new SysRole
        {
            RoleName = "散货结算操作角色",
            RoleCode = $"BulkSettlementOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"bulk-settlement-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "散货结算操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withSettlementMenu)
            GrantMenu(db, role.Id, FinanceBulkSettlementLifecycleRules.RequiredMenuCode,
                FinanceBulkSettlementLifecycleRules.RequiredMenuText, "/finance/bulk-settlement");

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

    private static FinanceBulkSettlement StoredSettlement(ErpDbContext db, long settlementId)
        => db.FinanceBulkSettlements.AsNoTracking().Single(s => s.Id == settlementId);

    private static void AssertSettlementUnchanged(ErpDbContext db, FinanceBulkSettlement expected)
    {
        var stored = StoredSettlement(db, expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.TotalAmount, stored.TotalAmount);
        Assert.Equal(expected.FreightCost, stored.FreightCost);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static FinanceBulkSettlementController BuildController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static FinanceBulkSettlement NewRequest(
        long customerId, decimal totalAmount = 1000m, decimal freightCost = 0m)
        => new()
        {
            SettlementDate = new DateTime(2026, 9, 27),
            CustomerId = customerId,
            TotalAmount = totalAmount,
            FreightCost = freightCost,
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
        var settlement = SeedSettlement(db, "SJ-1", customer.Id);
        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(NewRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Submit(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Approve(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Cancel(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Delete(settlement.Id));

        Assert.Equal(0, no.Calls);
        Assert.Single(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 账号停用_全部路由拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "SJ-1", customer.Id);
        DisableUser(db, userId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(settlement.Id, NewRequest(customer.Id, totalAmount: 200m)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(settlement.Id));

        Assert.Equal(0, no.Calls);
        AssertSettlementUnchanged(db, settlement);
    }

    [Fact]
    public async Task 撤销菜单授权_全部路由拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, _) = SeedOperator(db, privileged: false);
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "SJ-1", customer.Id);
        RevokeSettlementMenu(db, roleId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(settlement.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(settlement.Id));

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
        var settlement = SeedSettlement(db, "SJ-1", customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceBulkSettlement>>>(result.Value);
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
        var own = SeedSettlement(db, "SJ-OWN", ownCustomer.Id);
        var foreign = SeedSettlement(db, "SJ-FOREIGN", foreignCustomer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceBulkSettlement>>>(result.Value);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(own.Id, Assert.Single(payload.Data.Items).Id);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(foreign.Id));
        AssertSettlementUnchanged(db, foreign);

        // 自己客户：详情与状态流转可用
        Assert.IsType<OkObjectResult>(await controller.GetById(own.Id));
        Assert.IsType<OkObjectResult>(await controller.Submit(own.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredSettlement(db, own.Id).Status);
    }


    // ==================== 2. 创建（金额 / 客户资格，先校验后单号） ====================

    [Fact]
    public async Task 创建_非法金额拒绝_不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: 0m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: -10m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: 0.004m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: 100m, freightCost: -1m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(NewRequest(customer.Id, totalAmount: 100m, freightCost: -0.005m)));

        // 悬空客户（特权账号不受范围限制）：不存在，仍在单号前拒绝
        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(NewRequest(99999999L)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_越界或未知或已删除客户拒绝_不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-FOR", "范围外客户", empId: 6666L);
        var deleted = SeedCustomer(db, "C-DEL", "已删除客户", empId: employee.Id, deleted: true);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 范围外 / 已删除（受限账号可见集合不含已删除）/ 悬空 Id：一律 fail closed（未知归属），不消耗单号
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewRequest(foreign.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewRequest(deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewRequest(99999999L)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());

        // 范围内客户：创建成功
        Assert.IsType<OkObjectResult>(await controller.Create(NewRequest(own.Id)));
        Assert.Equal(1, no.Calls);
    }

    [Fact]
    public async Task 创建_真实启用客户校验_停用规则冲突_删除或悬空不存在()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var disabled = SeedCustomer(db, "C-DIS", "停用客户", status: 0);
        var deleted = SeedCustomer(db, "C-DEL", "已删除客户", deleted: true);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(NewRequest(disabled.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(NewRequest(deleted.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(NewRequest(99999999L)));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceBulkSettlements.AsNoTracking().ToList());
    }

    [Fact]
    public async Task 创建_正常_生成单号且按既有精度取整落库()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);
        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.Create(
            NewRequest(own.Id, totalAmount: 123.456m, freightCost: 10.555m)));
        Assert.NotNull(result.Value);
        Assert.Equal(1, no.Calls);

        var stored = db.FinanceBulkSettlements.AsNoTracking().Single();
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(own.Id, stored.CustomerId);
        Assert.Equal(123.46m, stored.TotalAmount);
        Assert.Equal(10.56m, stored.FreightCost);
        Assert.False(string.IsNullOrWhiteSpace(stored.SettlementNo));
    }


    // ==================== 3. 修改 / 提交 / 审核 / 取消 / 删除 ====================

    [Fact]
    public async Task 修改_仅待提交可改_且按既有精度取整()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedSettlement(db, "SJ-1", customer.Id, 1000m);
        var approved = SeedSettlement(db, "SJ-APP", customer.Id, 1000m, status: DocumentStatus.Approved);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(approved.Id, NewRequest(customer.Id, totalAmount: 200m)));
        AssertSettlementUnchanged(db, approved);

        Assert.IsType<OkObjectResult>(await controller.Update(pending.Id,
            NewRequest(customer.Id, totalAmount: 123.456m, freightCost: 10.555m)));
        var stored = StoredSettlement(db, pending.Id);
        Assert.Equal(123.46m, stored.TotalAmount);
        Assert.Equal(10.56m, stored.FreightCost);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 修改_越界或停用客户拒绝_被拒编辑不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-FOR", "范围外客户", empId: 6666L);
        var disabled = SeedCustomer(db, "C-DIS", "停用客户", empId: employee.Id, status: 0);
        var pending = SeedSettlement(db, "SJ-1", own.Id, 1000m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Update(pending.Id, NewRequest(foreign.Id, totalAmount: 300m)));
        AssertSettlementUnchanged(db, pending);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(pending.Id, NewRequest(disabled.Id, totalAmount: 300m)));
        AssertSettlementUnchanged(db, pending);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Update(pending.Id, NewRequest(own.Id, totalAmount: 0m)));
        AssertSettlementUnchanged(db, pending);
    }

    [Fact]
    public async Task 提交审核_正常流转_保留商业字段()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var settlement = SeedSettlement(db, "SJ-1", customer.Id, 1000m, 100m);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(settlement.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredSettlement(db, settlement.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(settlement.Id));
        var stored = StoredSettlement(db, settlement.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(100m, stored.FreightCost);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 提交审核_状态不符或持久化金额非法拒绝_且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var pending = SeedSettlement(db, "SJ-1", customer.Id, 1000m);
        // 历史持久化金额非法（0）：提交不得放行（锁内复核，不写库）
        var legacyZero = SeedSettlement(db, "SJ-ZERO", customer.Id, 0m);

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
        var pending = SeedSettlement(db, "SJ-1", customer.Id);
        var approved = SeedSettlement(db, "SJ-APP", customer.Id, status: DocumentStatus.Approved);
        var deletable = SeedSettlement(db, "SJ-DEL", customer.Id);

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
        Assert.Single(db.FinanceBulkSettlements.AsNoTracking().Where(s => s.Id == deletable.Id));

        // 已删除的结算单不再可见
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(deletable.Id));
    }


    // ==================== 4. 规则 / 控制器源码契约 ====================

    [Fact]
    public void 规则契约_金额精度_客户范围与文案()
    {
        Assert.Equal("bulk-settlement", FinanceBulkSettlementLifecycleRules.RequiredMenuCode);
        Assert.Equal("散货结算单", FinanceBulkSettlementLifecycleRules.RequiredMenuText);
        Assert.Equal(2, FinanceBulkSettlementLifecycleRules.AmountDecimals);

        // 0.5 进位（既有存储精度 2 位）
        Assert.Equal(1.01m, FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(1.005m));
        Assert.Equal(0.02m, FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(0.015m));
        Assert.Equal(0m, FinanceBulkSettlementLifecycleRules.NormalizeCost(-0.001m, "海运费"));

        // 取整后非正 / 负费用拒绝
        Assert.Throws<BusinessException>(() => FinanceBulkSettlementLifecycleRules.NormalizeTotalAmount(0.004m));
        Assert.Throws<BusinessException>(() => FinanceBulkSettlementLifecycleRules.NormalizeCost(-0.5m, "海运费"));

        // 客户范围：无权威归属 / 越界一律 fail closed，特权账号放行
        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false,
            AllowedCustomerIds = new HashSet<long> { 1L }
        };
        Assert.Throws<BusinessException>(() =>
            FinanceBulkSettlementLifecycleRules.EnsureCustomerInScope(restricted, 2L));
        Assert.Throws<BusinessException>(() =>
            FinanceBulkSettlementLifecycleRules.EnsureCustomerInScope(restricted, 0L));
        FinanceBulkSettlementLifecycleRules.EnsureCustomerInScope(restricted, 1L);

        // 锁序 / 边界 / 口径文案同源非空
        Assert.False(string.IsNullOrWhiteSpace(FinanceBulkSettlementLifecycleRules.RuleText));
        Assert.False(string.IsNullOrWhiteSpace(FinanceBulkSettlementLifecycleRules.BoundaryText));
        Assert.Contains("散货结算单", FinanceBulkSettlementLifecycleRules.RuleText);
        Assert.Contains("散货结算单", FinanceBulkSettlementLifecycleRules.LockOrderText);
        Assert.Contains("库存", FinanceBulkSettlementLifecycleRules.BoundaryText);
    }

    [Fact]
    public void 控制器源码契约_先授权后单号且写路由先取行锁()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceSettlementControllers.cs"));

        // 实时授权（身份 / 菜单）在计数、单号生成与写入之前
        var menuCheck = controller.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal);
        Assert.True(menuCheck >= 0);
        Assert.True(menuCheck < controller.IndexOf(
            "GenerateAsync(DocumentType.BulkSettlement)", StringComparison.Ordinal));
        Assert.True(menuCheck < controller.IndexOf(
            "Db.FinanceBulkSettlements.Add", StringComparison.Ordinal));

        // 客户可用性校验先于单号预留与写入
        Assert.True(controller.IndexOf(
                "EnsureCustomerAvailableAsync", StringComparison.Ordinal)
            < controller.IndexOf(
                "GenerateAsync(DocumentType.BulkSettlement)", StringComparison.Ordinal));

        // 写路由：散货结算单行锁 + 锁内范围复核
        Assert.Contains("LockSettlementRowAsync", controller);
        Assert.Contains("EnsureStoredSettlementScopeAllowedAsync", controller);
        Assert.Contains("EnsureCustomerInScope", controller);

        // 散货结算单没有来源单据：绝不引入装柜清单行锁调用
        Assert.DoesNotContain("LockLoadingListRowsAsync(Db", controller[
            controller.IndexOf("class FinanceBulkSettlementController", StringComparison.Ordinal)..]);
    }
}
