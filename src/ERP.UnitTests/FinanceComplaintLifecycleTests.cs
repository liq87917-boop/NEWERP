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
/// 客诉单生命周期护栏（ERP-388）单元测试：覆盖实时启用身份 / 客诉单（complaint）菜单授权 /
/// 客户数据范围、文本长度契约（客诉类型 100 / 客诉描述 1000 / 责任部门 100 / 处理结果 1000 / 备注 500）、
/// 真实启用客户校验、可空来源销售订单（未删除 / 未取消 / 同客户 + 销售订单菜单授权）、历史未关联来源语义、
/// 已取消来源历史链接只读可读、被拒创建不消耗单号、被拒编辑 / 流转不改写原始字段（含审计时间戳）、
/// 取消幂等 / 软删除仅待提交，以及「客诉单不阻断销售订单取消」与规则 / 控制器源码契约。
/// 全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// <para>真实 SQL 的身份 / 范围 / 来源链接护栏与两个独立连接竞态见
/// <c>ERP.IntegrationTests/FinanceComplaintLifecycleSqlServerTests.cs</c>。</para>
/// </summary>
public class FinanceComplaintLifecycleTests
{
    // ==================== 0. 测试脚手架 ====================

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗客诉单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            return Task.FromResult($"KS{DateTime.Now:yyyyMMdd}{++_seq:D4}");
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

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = 5000m,
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 21, 8, 0, 0),
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceComplaint SeedComplaint(
        ErpDbContext db, string complaintNo, long customerId, long? salesOrderId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var complaint = new FinanceComplaint
        {
            ComplaintNo = complaintNo,
            ComplaintDate = new DateTime(2026, 9, 22),
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = "原始描述",
            ResponsibleDept = "质检部",
            HandleResult = "原始处理结果",
            Remark = "原始备注",
            Status = status,
            UpdatedAt = new DateTime(2026, 9, 23, 8, 0, 0),
            IsDeleted = deleted
        };
        db.FinanceComplaints.Add(complaint);
        db.SaveChanges();
        return complaint;
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
    /// 播种一个操作员：<paramref name="privileged"/> 为 <c>true</c> 时复用既有「系统内置角色」口径（可见全部客户）；
    /// 为 <c>false</c> 时按既有业务员数据范围口径（<c>BaseEmployee.EmployeeCode</c> 映射）限制客户范围。
    /// 默认显式授予既有「客诉单」（complaint）菜单；可选授予既有「销售订单」（sales-order）菜单。
    /// </summary>
    private static (long UserId, long RoleId, string UserName) SeedOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool privileged = true,
        bool withComplaintMenu = true, bool withSalesOrderMenu = false)
    {
        var role = new SysRole
        {
            RoleName = "客诉操作角色",
            RoleCode = $"ComplaintOp-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"complaint-op-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "客诉操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withComplaintMenu)
            GrantMenu(db, role.Id, FinanceComplaintLifecycleRules.RequiredMenuCode,
                FinanceComplaintLifecycleRules.RequiredMenuText, "/finance/complaint");
        if (withSalesOrderMenu)
            GrantMenu(db, role.Id, FinanceComplaintLifecycleRules.SourceRequiredMenuCode,
                FinanceComplaintLifecycleRules.SourceRequiredMenuText, "/order/sales");

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

    private static void RevokeMenus(ErpDbContext db, long roleId)
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

    private static FinanceComplaint StoredComplaint(ErpDbContext db, long complaintId)
        => db.FinanceComplaints.AsNoTracking().Single(c => c.Id == complaintId);

    private static void AssertComplaintUnchanged(ErpDbContext db, FinanceComplaint expected)
    {
        var stored = StoredComplaint(db, expected.Id);
        Assert.Equal(expected.Status, stored.Status);
        Assert.Equal(expected.CustomerId, stored.CustomerId);
        Assert.Equal(expected.SalesOrderId, stored.SalesOrderId);
        Assert.Equal(expected.ComplaintType, stored.ComplaintType);
        Assert.Equal(expected.Description, stored.Description);
        Assert.Equal(expected.ResponsibleDept, stored.ResponsibleDept);
        Assert.Equal(expected.HandleResult, stored.HandleResult);
        Assert.Equal(expected.Remark, stored.Remark);
        Assert.Equal(expected.IsDeleted, stored.IsDeleted);
        Assert.Equal(expected.UpdatedAt, stored.UpdatedAt);
    }

    private static FinanceComplaintController BuildController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static FinanceComplaint NewRequest(
        long customerId, long? salesOrderId = null, string? description = null)
        => new()
        {
            ComplaintDate = new DateTime(2026, 9, 28),
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = description ?? "新客诉描述",
            ResponsibleDept = "质检部",
            HandleResult = "待处理",
            Remark = "新备注"
        };

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 规则契约 / 实时身份 / 菜单授权 / 客户数据范围 ====================

    [Fact]
    public void 规则契约_菜单编码_长度契约与文案()
    {
        Assert.Equal("complaint", FinanceComplaintLifecycleRules.RequiredMenuCode);
        Assert.Equal("客诉单", FinanceComplaintLifecycleRules.RequiredMenuText);
        Assert.Equal("sales-order", FinanceComplaintLifecycleRules.SourceRequiredMenuCode);
        Assert.Equal("销售订单", FinanceComplaintLifecycleRules.SourceRequiredMenuText);

        Assert.Equal(50, FinanceComplaintLifecycleRules.ComplaintNoMaxLength);
        Assert.Equal(100, FinanceComplaintLifecycleRules.ComplaintTypeMaxLength);
        Assert.Equal(1000, FinanceComplaintLifecycleRules.DescriptionMaxLength);
        Assert.Equal(100, FinanceComplaintLifecycleRules.ResponsibleDeptMaxLength);
        Assert.Equal(1000, FinanceComplaintLifecycleRules.HandleResultMaxLength);
        Assert.Equal(500, FinanceComplaintLifecycleRules.RemarkMaxLength);

        // 长度契约：边界内放行、超长按参数错误拒绝
        Assert.Equal(new string('x', 1000),
            FinanceComplaintLifecycleRules.EnsureTextWithinLength(new string('x', 1000), 1000, "客诉描述"));
        Assert.Throws<BusinessException>(() =>
            FinanceComplaintLifecycleRules.EnsureTextWithinLength(new string('x', 1001), 1000, "客诉描述"));

        // 客户范围：无权威归属 / 越界一律 fail closed，特权账号放行
        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false,
            AllowedCustomerIds = new HashSet<long> { 1L }
        };
        Assert.Throws<BusinessException>(() =>
            FinanceComplaintLifecycleRules.EnsureCustomerInScope(restricted, 2L));
        FinanceComplaintLifecycleRules.EnsureCustomerInScope(restricted, 1L);

        // 锁序 / 口径 / 边界文案同源非空
        Assert.Contains("客诉单", FinanceComplaintLifecycleRules.RuleText);
        Assert.Contains("销售订单行", FinanceComplaintLifecycleRules.LockOrderText);
        Assert.Contains("客诉单行", FinanceComplaintLifecycleRules.LockOrderText);
        Assert.Contains("不产生销售数量", FinanceComplaintLifecycleRules.BoundaryText);
        Assert.Contains("不构成销售订单取消的阻断证据", SalesOrderCancellationRules.ComplaintNonBlockingText);
        Assert.Contains("客诉", SalesOrderCancellationRules.ComplaintNonBlockingText);

        // 多个来源订单按 Id 升序（确定性锁序），只保留正整数
        Assert.Equal(new long[] { 3, 7, 9 },
            FinanceComplaintLifecycleRules.MergeSourceOrderLockIds(null, 9L, 3L, 7L, 3L, 0L));
        Assert.Empty(FinanceComplaintLifecycleRules.MergeSourceOrderLockIds(null, null));
    }

    [Fact]
    public async Task 未认证_全部路由拒绝_且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(NewRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Submit(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Approve(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Cancel(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Delete(complaint.Id));

        Assert.Equal(0, no.Calls);
        AssertComplaintUnchanged(db, complaint);
    }

    [Fact]
    public async Task 无客诉单菜单授权_拒绝访问且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, roleId, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);
        RevokeMenus(db, roleId);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(NewRequest(customer.Id)));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(complaint.Id));

        Assert.Equal(0, no.Calls);
        AssertComplaintUnchanged(db, complaint);
    }

    [Fact]
    public async Task 账号禁用_拒绝访问()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);
        DisableUser(db, userId);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new PageQuery(), null));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(complaint.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(complaint.Id));
        AssertComplaintUnchanged(db, complaint);
    }

    [Fact]
    public async Task 未映射业务员的受限账号_fail_closed不降级为全局可见()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db, privileged: false);
        // 不映射员工：受限账号未映射业务员 → 可见客户为空（既有权衡，绝不降级为全局可见）
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceComplaint>>>(result.Value);
        Assert.Equal(0, payload.Data!.Total);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(complaint.Id));
    }

    [Fact]
    public async Task 受限制业务员_列表仅返回自己客户_且范围外路由拒绝不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(db, privileged: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);

        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "范围外客户", empId: 9999L);
        var own = SeedComplaint(db, "KS-OWN", ownCustomer.Id);
        var foreign = SeedComplaint(db, "KS-FOREIGN", foreignCustomer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetPaged(new PageQuery(), null));
        var payload = Assert.IsType<ApiResponse<PagedResult<FinanceComplaint>>>(result.Value);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal(own.Id, Assert.Single(payload.Data.Items).Id);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Cancel(foreign.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Delete(foreign.Id));
        AssertComplaintUnchanged(db, foreign);

        // 自己客户：详情与状态流转可用
        Assert.IsType<OkObjectResult>(await controller.GetById(own.Id));
        Assert.IsType<OkObjectResult>(await controller.Submit(own.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredComplaint(db, own.Id).Status);
    }

    // ==================== 2. 创建（文本 / 客户 / 来源资格，先校验后单号） ====================

    [Fact]
    public async Task 创建_有效客户与有效来源订单_校验后生成单号并落库()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-1", customer.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(
            await controller.Create(NewRequest(customer.Id, order.Id)));

        Assert.Equal(1, no.Calls);
        var stored = db.FinanceComplaints.AsNoTracking().Single();
        Assert.True(stored.Id > 0);
        Assert.StartsWith("KS", stored.ComplaintNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(order.Id, stored.SalesOrderId);
        Assert.Equal("质量", stored.ComplaintType);
    }

    [Fact]
    public async Task 创建_停用客户_规则冲突且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "停用客户", status: 0);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(NewRequest(customer.Id)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceComplaints.AsNoTracking());
    }

    [Fact]
    public async Task 创建_已删除客户_不存在且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "已删除客户", deleted: true);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(NewRequest(customer.Id)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceComplaints.AsNoTracking());
    }

    [Fact]
    public async Task 创建_文本超长_参数错误且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var tooLongDescription = NewRequest(customer.Id, description: new string('x', 1001));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(tooLongDescription));

        var tooLongType = NewRequest(customer.Id);
        tooLongType.ComplaintType = new string('y', 101);
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(tooLongType));

        var tooLongResult = NewRequest(customer.Id);
        tooLongResult.HandleResult = new string('z', 1001);
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(tooLongResult));

        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceComplaints.AsNoTracking());
    }

    [Fact]
    public async Task 创建_已取消来源订单_新建链接被拒且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var cancelled = SeedOrder(db, "SO-CANCEL", customer.Id, DocumentStatus.Cancelled);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var ex = await AssertBusinessCodeAsync(
            ErrorCodes.RuleConflict, () => controller.Create(NewRequest(customer.Id, cancelled.Id)));
        Assert.Contains("已取消", ex.Message);
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceComplaints.AsNoTracking());
    }

    [Fact]
    public async Task 创建_已删除来源订单_不存在且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var deleted = SeedOrder(db, "SO-DEL", customer.Id, deleted: true);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(
            ErrorCodes.NotFound, () => controller.Create(NewRequest(customer.Id, deleted.Id)));
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceComplaints.AsNoTracking());
    }

    [Fact]
    public async Task 创建_他客户来源订单_规则冲突且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var otherCustomer = SeedCustomer(db, "C002", "客户B");
        var foreignOrder = SeedOrder(db, "SO-FOREIGN", otherCustomer.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        var ex = await AssertBusinessCodeAsync(
            ErrorCodes.RuleConflict, () => controller.Create(NewRequest(customer.Id, foreignOrder.Id)));
        Assert.Contains("客户", ex.Message);
        Assert.Equal(0, no.Calls);
        Assert.Empty(db.FinanceComplaints.AsNoTracking());
    }

    [Fact]
    public async Task 创建_未关联来源_保留历史未关联语义且不要求销售订单菜单()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Create(NewRequest(customer.Id)));
        Assert.Equal(1, no.Calls);
        Assert.Null(db.FinanceComplaints.AsNoTracking().Single().SalesOrderId);
    }

    [Fact]
    public async Task 创建_受限账号无销售订单菜单_链接被拒但未关联可创建()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, userName) = SeedOperator(
            db, privileged: false, withSalesOrderMenu: false);
        MapOperatorToEmployee(db, userName);
        var employee = db.BaseEmployees.Single(e => e.EmployeeCode == userName);
        var customer = SeedCustomer(db, "C001", "本人客户", empId: employee.Id);
        var order = SeedOrder(db, "SO-1", customer.Id);

        var no = new FakeDocumentNumberService();
        var controller = BuildController(db, no);
        TestAuth.SetUser(controller, userId);

        // 有来源 → 缺「销售订单」菜单授权 → fail closed，且不消耗单号
        await AssertBusinessCodeAsync(
            ErrorCodes.Forbidden, () => controller.Create(NewRequest(customer.Id, order.Id)));
        Assert.Equal(0, no.Calls);

        // 未关联来源 → 不要求来源菜单，保留历史未关联语义
        Assert.IsType<OkObjectResult>(await controller.Create(NewRequest(customer.Id)));
        Assert.Equal(1, no.Calls);
        Assert.Null(db.FinanceComplaints.AsNoTracking().Single().SalesOrderId);
    }

    // ==================== 3. 修改 / 提交 / 审核 / 取消 / 删除 ====================

    [Fact]
    public async Task 修改_仅待提交可改_非待提交拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id, status: DocumentStatus.Submitted);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(
            ErrorCodes.RuleConflict, () => controller.Update(complaint.Id, NewRequest(customer.Id)));
        AssertComplaintUnchanged(db, complaint);
    }

    [Fact]
    public async Task 修改_待提交_更新字段并持久化新来源()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order1 = SeedOrder(db, "SO-1", customer.Id);
        var order2 = SeedOrder(db, "SO-2", customer.Id);
        var complaint = SeedComplaint(db, "KS-1", customer.Id, order1.Id);
        var seededUpdatedAt = complaint.UpdatedAt;

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var request = NewRequest(customer.Id, order2.Id, "更新后的描述");
        Assert.IsType<OkObjectResult>(await controller.Update(complaint.Id, request));

        var stored = StoredComplaint(db, complaint.Id);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(order2.Id, stored.SalesOrderId);
        Assert.Equal("更新后的描述", stored.Description);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.True(stored.UpdatedAt > seededUpdatedAt);
    }

    [Fact]
    public async Task 修改_变更来源到已取消订单_拒绝且原字段不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order1 = SeedOrder(db, "SO-1", customer.Id);
        var cancelled = SeedOrder(db, "SO-CANCEL", customer.Id, DocumentStatus.Cancelled);
        var complaint = SeedComplaint(db, "KS-1", customer.Id, order1.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var ex = await AssertBusinessCodeAsync(
            ErrorCodes.RuleConflict,
            () => controller.Update(complaint.Id, NewRequest(customer.Id, cancelled.Id)));
        Assert.Contains("已取消", ex.Message);
        AssertComplaintUnchanged(db, complaint);
    }

    [Fact]
    public async Task 修改_文本超长_参数错误且不改写()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(
            ErrorCodes.InvalidParameter,
            () => controller.Update(complaint.Id, NewRequest(customer.Id, description: new string('x', 1001))));
        AssertComplaintUnchanged(db, complaint);
    }

    [Fact]
    public async Task 提交与审核_状态流转_锁内复核持久化字段()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-1", customer.Id);
        var complaint = SeedComplaint(db, "KS-1", customer.Id, order.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Submit(complaint.Id));
        Assert.Equal(DocumentStatus.Submitted, StoredComplaint(db, complaint.Id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(complaint.Id));
        Assert.Equal(DocumentStatus.Approved, StoredComplaint(db, complaint.Id).Status);
    }

    [Fact]
    public async Task 提交_持久化文本超长_参数错误且状态不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);
        // 直接写入超长持久化文本（模拟历史脏数据），提交时锁内复核应 fail closed
        complaint.Description = new string('x', 1001);
        db.SaveChanges();

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Submit(complaint.Id));
        Assert.Equal(DocumentStatus.Pending, StoredComplaint(db, complaint.Id).Status);
    }

    [Fact]
    public async Task 取消_置为已取消_重复取消拒绝_来源订单与原字段不受影响()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-1", customer.Id);
        var complaint = SeedComplaint(db, "KS-1", customer.Id, order.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        Assert.IsType<OkObjectResult>(await controller.Cancel(complaint.Id));

        var stored = StoredComplaint(db, complaint.Id);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(order.Id, stored.SalesOrderId);
        Assert.Equal(complaint.Description, stored.Description);
        Assert.Equal(complaint.ComplaintType, stored.ComplaintType);

        // 取消客诉绝不改写来源销售订单，也不产生任何库存 / 财务影响
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(complaint.Id));
    }

    [Fact]
    public async Task 删除_仅待提交可删_软删除且不再可见()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var deletable = SeedComplaint(db, "KS-DEL", customer.Id);
        var submitted = SeedComplaint(db, "KS-SUB", customer.Id, status: DocumentStatus.Submitted);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        // 非待提交不可删
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(submitted.Id));
        Assert.False(StoredComplaint(db, submitted.Id).IsDeleted);

        Assert.IsType<OkObjectResult>(await controller.Delete(deletable.Id));
        Assert.True(StoredComplaint(db, deletable.Id).IsDeleted);

        // 已删除的客诉单不再可见
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(deletable.Id));
    }

    [Fact]
    public async Task 详情_来源订单已取消_仍可读且带显式状态()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-1", customer.Id);
        var complaint = SeedComplaint(db, "KS-1", customer.Id, order.Id);

        // 来源订单事后被取消（客诉历史不得因此失效）
        order.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetById(complaint.Id));
        var payload = Assert.IsType<ApiResponse<FinanceComplaint>>(result.Value);
        Assert.Equal(order.Id, payload.Data!.SalesOrderId);
        Assert.Contains("已取消", payload.Message);
        Assert.Contains("只读", payload.Message);
    }

    [Fact]
    public async Task 详情_未关联来源_显式未关联文案()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var complaint = SeedComplaint(db, "KS-1", customer.Id);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var result = Assert.IsType<OkObjectResult>(await controller.GetById(complaint.Id));
        var payload = Assert.IsType<ApiResponse<FinanceComplaint>>(result.Value);
        Assert.Null(payload.Data!.SalesOrderId);
        Assert.Equal(FinanceComplaintLifecycleRules.UnlinkedEvidenceText, payload.Message);
    }

    // ==================== 4. 客诉不阻断销售订单取消 + 控制器源码契约 ====================

    [Fact]
    public async Task 销售订单取消_不因客诉存在而拒绝_且客诉历史保持可读()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-1", customer.Id);
        var complaint = SeedComplaint(db, "KS-1", customer.Id, order.Id);

        // 取消护栏绝不因存在客诉单而拒绝（客诉不是履约 / 收款证据）
        await SalesOrderCancellationRules.ValidateCancellationAsync(db, order, userId);

        // 与 SalesOrderController.Cancel 同一命令逻辑：只改订单状态
        order.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        // 客诉历史与来源链接原样保留、仍可读，并带显式「来源已取消」状态
        var stored = StoredComplaint(db, complaint.Id);
        Assert.Equal(order.Id, stored.SalesOrderId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);

        var controller = BuildController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);
        var result = Assert.IsType<OkObjectResult>(await controller.GetById(complaint.Id));
        var payload = Assert.IsType<ApiResponse<FinanceComplaint>>(result.Value);
        Assert.Contains("已取消", payload.Message);

        // 已记录的来源不得被静默重绑定：来源 Id 保持原样
        Assert.Equal(order.Id, payload.Data!.SalesOrderId);
    }

    [Fact]
    public void 控制器源码契约_先授权后单号_来源订单锁先于客诉单锁()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceReceiptComplaintControllers.cs"));
        var complaints = controller[controller.IndexOf(
            "class FinanceComplaintController", StringComparison.Ordinal)..];

        // 实时授权（身份 / 菜单 / 客户范围）在计数、单号生成与写入之前
        var menuCheck = complaints.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal);
        Assert.True(menuCheck >= 0);
        Assert.True(menuCheck < complaints.IndexOf(
            "GenerateAsync(DocumentType.Complaint)", StringComparison.Ordinal));
        Assert.True(menuCheck < complaints.IndexOf(
            "Db.FinanceComplaints.Add", StringComparison.Ordinal));

        // 客户可用性 / 来源资格校验先于单号预留与写入
        Assert.True(complaints.IndexOf(
                "EnsureCustomerAvailable", StringComparison.Ordinal)
            < complaints.IndexOf(
                "GenerateAsync(DocumentType.Complaint)", StringComparison.Ordinal));

        // 写路由：来源销售订单行锁 + 客诉单行锁 + 锁内范围复核
        Assert.Contains("LockSourceOrderRowsAsync", complaints);
        Assert.Contains("MergeSourceOrderLockIds", complaints);
        Assert.Contains("LockSalesOrderRowSql", complaints);
        Assert.Contains("LockComplaintRowAsync", complaints);
        Assert.Contains("EnsureStoredComplaintScopeAllowed", complaints);
        Assert.Contains("EnsurePersistedComplaintConsistentAsync", complaints);
        Assert.Contains("ValidateAndAuthorizeLinkAsync", complaints);
        Assert.Contains("DescribeStoredSourceAsync", complaints);

        // 修改：先来源销售订单行锁、后客诉单行锁（ERP-388 锁序，绝不反向）
        var updateSourceLock = complaints.IndexOf(
            "LockSourceOrderRowsAsync(storedSourceOrderId, entity.SalesOrderId)", StringComparison.Ordinal);
        var updateComplaintLock = complaints.IndexOf(
            "LockComplaintRowAsync(Db, id)", StringComparison.Ordinal);
        Assert.True(updateSourceLock >= 0);
        Assert.True(updateComplaintLock >= 0);
        Assert.True(updateSourceLock < updateComplaintLock);
    }
}
