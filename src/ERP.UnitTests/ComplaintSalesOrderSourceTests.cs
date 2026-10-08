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
/// ERP-389 客诉单来源销售订单候选 / 已存储来源展示 + 表单显式选择的单元测试（内存库，不连接 SQL Server）。
/// <para>覆盖：按<b>精确客户</b>的有界分页候选（只含未删除订单、已取消显式不可选、绝不返回任意客户订单）、
/// 关键字 / 分页<b>先归一化再计数</b>、既有「客诉单」+「销售订单」菜单与实时客户数据范围的 fail closed
/// （不新增用户授权、无匿名 / 管理员兜底）、候选只读投影不写库、最终保存按 ERP-388 生命周期规则
/// <b>复核精确来源</b>（伪造 / 已取消 / 跨客户来源被拒绝且不消耗单号）、已存储来源在取消 / 删除后只读可读并显式标注、
/// 显式断开链接、以及控制器 / 服务 / 前端接入源码契约。</para>
/// </summary>
public class ComplaintSalesOrderSourceTests
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
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false,
        string customerPoNo = "", string contractNo = "")
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = 5000m,
            CustomerPoNo = customerPoNo,
            ContractNo = contractNo,
            Status = status,
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
            IsDeleted = deleted
        };
        db.FinanceComplaints.Add(complaint);
        db.SaveChanges();
        return complaint;
    }

    private static SysMenu EnsureMenu(ErpDbContext db, string code, string name, string path, int sortOrder)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            SortOrder = sortOrder,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个真实业务的非特权账号：既有「客诉单」/「销售订单」菜单授权 + 业务员员工映射（把范围内客户分配给
    /// 它的员工），数据范围恰好覆盖该客户；可选禁用账号 / 不授予菜单用于 fail closed 场景。不新增权限模型。
    /// </summary>
    private static long SeedOperator(
        ErpDbContext db, long inScopeCustomerId, bool complaintMenu = true, bool salesOrderMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var role = new SysRole { RoleCode = $"COS-{Guid.NewGuid():N}", RoleName = "客诉来源操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"cos-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "客诉来源操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "客诉来源操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var customer = db.BaseCustomers.Single(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (complaintMenu)
        {
            var menu = EnsureMenu(db, FinanceComplaintLifecycleRules.RequiredMenuCode,
                FinanceComplaintLifecycleRules.RequiredMenuText, "/finance/complaints", 70);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        if (salesOrderMenu)
        {
            var menu = EnsureMenu(db, FinanceComplaintLifecycleRules.SourceRequiredMenuCode,
                FinanceComplaintLifecycleRules.SourceRequiredMenuText, "/sales/orders", 40);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.SaveChanges();
        return user.Id;
    }

    private static FinanceComplaintController BuildController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var controller = new FinanceComplaintController(db, noService ?? new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static ComplaintSalesOrderCandidatePageDto Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<ComplaintSalesOrderCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static ComplaintSalesOrderSourceViewDto View(IActionResult result)
        => Assert.IsType<ApiResponse<ComplaintSalesOrderSourceViewDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NEWERP.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private static FinanceComplaint Request(long customerId, long? salesOrderId, string description = "原始描述")
        => new()
        {
            ComplaintDate = new DateTime(2026, 9, 22),
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = "质量",
            Description = description,
            ResponsibleDept = "质检部",
            HandleResult = "处理结果",
            Remark = "备注"
        };

    // ==================== 1. 归一化纯函数（先归一化再计数） ====================

    [Fact]
    public void 归一化_关键字页码每页条数均有界()
    {
        Assert.Equal("SO-1", ComplaintSalesOrderSourceService.NormalizeKeyword("  SO-1  "));
        Assert.Equal(string.Empty, ComplaintSalesOrderSourceService.NormalizeKeyword(null));
        Assert.Equal(100, ComplaintSalesOrderSourceService.NormalizeKeyword(new string('x', 150)).Length);

        Assert.Equal(1, ComplaintSalesOrderSourceService.NormalizePage(0));
        Assert.Equal(1, ComplaintSalesOrderSourceService.NormalizePage(-5));
        Assert.Equal(7, ComplaintSalesOrderSourceService.NormalizePage(7));

        Assert.Equal(ComplaintSalesOrderSourceService.DefaultPageSize,
            ComplaintSalesOrderSourceService.NormalizePageSize(0));
        Assert.Equal(ComplaintSalesOrderSourceService.MaxPageSize,
            ComplaintSalesOrderSourceService.NormalizePageSize(9999));
        Assert.Equal(15, ComplaintSalesOrderSourceService.NormalizePageSize(15));
    }

    // ==================== 2. 精确客户候选：范围 / 状态 / 分页（真实控制器 + 内存库） ====================

    [Fact]
    public async Task 候选_只返回精确客户未删除订单_已取消显式不可选()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);

        var eligible = SeedOrder(db, "SO-A-ELIGIBLE", customerA.Id, DocumentStatus.Approved);
        var cancelled = SeedOrder(db, "SO-A-CANCELLED", customerA.Id, DocumentStatus.Cancelled);
        var deleted = SeedOrder(db, "SO-A-DELETED", customerA.Id, DocumentStatus.Approved, deleted: true);
        var foreign = SeedOrder(db, "SO-B-FOREIGN", customerB.Id, DocumentStatus.Approved);

        var ctl = BuildController(db, userId);
        var page = Candidates(await ctl.GetSalesOrderCandidates(customerA.Id, null, 0, 0));

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.DoesNotContain(page.Items, i => i.SalesOrderId == deleted.Id);
        Assert.DoesNotContain(page.Items, i => i.SalesOrderId == foreign.Id);

        var ok = page.Items.Single(i => i.SalesOrderId == eligible.Id);
        Assert.True(ok.Eligible);
        Assert.Equal(string.Empty, ok.IneligibleReason);
        Assert.Equal(customerA.Id, ok.CustomerId);
        Assert.Equal("客户A", ok.CustomerName);

        var no = page.Items.Single(i => i.SalesOrderId == cancelled.Id);
        Assert.False(no.Eligible);
        Assert.Contains("已取消", no.IneligibleReason);
    }

    [Fact]
    public async Task 候选_范围外客户与非法客户_拒绝且不泄露()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);
        SeedOrder(db, "SO-B-1", customerB.Id);

        var ctl = BuildController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GetSalesOrderCandidates(customerB.Id, null, 0, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GetSalesOrderCandidates(0, null, 0, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GetSalesOrderCandidates(null, null, 0, 0));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 候选_关键字与分页先归一化再计数()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = SeedOperator(db, customerA.Id);
        SeedOrder(db, "SO-K-1", customerA.Id);
        SeedOrder(db, "SO-K-2", customerA.Id);
        SeedOrder(db, "SO-K-3", customerA.Id);

        var ctl = BuildController(db, userId);

        // 关键字去首尾空白后再匹配
        var hit = Candidates(await ctl.GetSalesOrderCandidates(customerA.Id, "  SO-K-2  ", 0, 0));
        Assert.Equal(1, hit.Total);
        Assert.Equal("SO-K-2", hit.Items.Single().OrderNo);

        // 页码 / 每页条数越界收敛（有界）
        var bounded = Candidates(await ctl.GetSalesOrderCandidates(customerA.Id, null, 0, 9999));
        Assert.Equal(1, bounded.Page);
        Assert.Equal(ComplaintSalesOrderSourceService.MaxPageSize, bounded.PageSize);
        Assert.Equal(3, bounded.Total);

        // 超长关键字被截断（无命中）→ 计数为 0，绝不无界拉取
        var none = Candidates(await ctl.GetSalesOrderCandidates(customerA.Id, new string('z', 150), 0, 0));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task 候选_缺少既有菜单_或_禁用_或_未认证_fail_closed(
        bool complaintMenu, bool salesOrderMenu, bool disabled)
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = SeedOperator(db, customerA.Id, complaintMenu, salesOrderMenu,
            disabled ? UserStatus.Disabled : UserStatus.Enabled);
        SeedOrder(db, "SO-A-1", customerA.Id);

        // 缺少任一既有菜单 / 账号被禁用 → 权限不足（fail closed）
        var ctl = BuildController(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.GetSalesOrderCandidates(customerA.Id, null, 0, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 无身份（未认证）
        var anonymous = BuildController(db, null);
        ex = await Assert.ThrowsAsync<BusinessException>(
            () => anonymous.GetSalesOrderCandidates(customerA.Id, null, 0, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 候选_是只读投影_不写库()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = SeedOperator(db, customerA.Id);
        SeedOrder(db, "SO-A-1", customerA.Id);

        var ordersBefore = db.SalesOrders.Count();
        var complaintsBefore = db.FinanceComplaints.Count();

        var ctl = BuildController(db, userId);
        var page = Candidates(await ctl.GetSalesOrderCandidates(customerA.Id, null, 0, 0));
        Assert.Equal(1, page.Total);

        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.Equal(complaintsBefore, db.FinanceComplaints.Count());
    }

    // ==================== 3. 最终保存：按 ERP-388 生命周期规则复核精确来源 ====================

    [Fact]
    public async Task 最终保存_伪造或取消或跨客户来源被拒且不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);
        SeedOrder(db, "SO-A-1", customerA.Id);
        var cancelled = SeedOrder(db, "SO-A-CAN", customerA.Id, DocumentStatus.Cancelled);
        var foreign = SeedOrder(db, "SO-B-1", customerB.Id);

        var fake = new FakeDocumentNumberService();
        var ctl = BuildController(db, userId, fake);

        // 伪造（不存在的 Id，绝不出现在候选里）→ 来源不存在
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA.Id, 999999)));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        // 已取消来源 → 冲突（历史只读，不能作为新来源）
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA.Id, cancelled.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 跨客户来源 → 冲突（绝不静默重绑定）
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Request(customerA.Id, foreign.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 授权 / 校验失败绝不消耗单号、绝不落半成品
        Assert.Equal(0, fake.Calls);
        Assert.Empty(db.FinanceComplaints);
    }

    [Fact]
    public async Task 最终保存_合法候选来源创建后重开保留精确链接()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = SeedOperator(db, customerA.Id);
        SeedOrder(db, "SO-A-1", customerA.Id);
        SeedOrder(db, "SO-A-2", customerA.Id, DocumentStatus.Cancelled);

        var ctl = BuildController(db, userId);
        var candidate = Candidates(await ctl.GetSalesOrderCandidates(customerA.Id, null, 0, 0))
            .Items.Single(i => i.Eligible);

        var created = CreatedId(await ctl.Create(Request(customerA.Id, candidate.SalesOrderId)));
        Assert.Equal(candidate.SalesOrderId, db.FinanceComplaints.Single(c => c.Id == created).SalesOrderId);

        var reopened = View(await ctl.GetStoredSalesOrderSource(created));
        Assert.True(reopened.Linked);
        Assert.Equal(candidate.SalesOrderId, reopened.SalesOrderId);
        Assert.Equal("SO-A-1", reopened.OrderNo);
        Assert.Contains("已关联", reopened.Annotation);
    }

    // ==================== 4. 已存储来源只读展示（历史可读，绝不静默清除） ====================

    [Fact]
    public async Task 已存储来源_取消或删除后仍只读可读并显式标注()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = SeedOperator(db, customerA.Id);
        var ctl = BuildController(db, userId);

        // 来源事后取消：链接原样保留、显式「已取消」、不再可作为新来源
        var order = SeedOrder(db, "SO-A-LIVE", customerA.Id);
        var complaint = SeedComplaint(db, "KS-1", customerA.Id, order.Id);
        order.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var view = View(await ctl.GetStoredSalesOrderSource(complaint.Id));
        Assert.Equal(order.Id, view.SalesOrderId);
        Assert.Equal("SO-A-LIVE", view.OrderNo);
        Assert.False(view.EligibleForNewLink);
        Assert.False(view.Unavailable);
        Assert.Contains("已取消", view.Annotation);

        // 来源已删除：原链接原样保留、显式「不可用」
        var gone = SeedOrder(db, "SO-A-GONE", customerA.Id);
        var c2 = SeedComplaint(db, "KS-2", customerA.Id, gone.Id);
        gone.IsDeleted = true;
        db.SaveChanges();

        var v2 = View(await ctl.GetStoredSalesOrderSource(c2.Id));
        Assert.True(v2.Linked);
        Assert.True(v2.Unavailable);
        Assert.Equal(gone.Id, v2.SalesOrderId);
        Assert.Contains("不可用", v2.Annotation);

        // 历史未关联语义原样保留
        var c3 = SeedComplaint(db, "KS-3", customerA.Id, null);
        var v3 = View(await ctl.GetStoredSalesOrderSource(c3.Id));
        Assert.False(v3.Linked);
        Assert.Null(v3.SalesOrderId);
        Assert.Contains("未关联", v3.Annotation);
    }

    [Fact]
    public async Task 显式断开来源_更新为未关联()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var userId = SeedOperator(db, customerA.Id);
        var order = SeedOrder(db, "SO-A-UNLINK", customerA.Id);
        var complaint = SeedComplaint(db, "KS-UNLINK", customerA.Id, order.Id);

        var ctl = BuildController(db, userId);
        // 表单留空（0）→ 服务端归一化为 null（显式断开，保留历史未关联语义）
        await ctl.Update(complaint.Id, Request(customerA.Id, 0));

        Assert.Null(db.FinanceComplaints.AsNoTracking().Single(c => c.Id == complaint.Id).SalesOrderId);
        var view = View(await ctl.GetStoredSalesOrderSource(complaint.Id));
        Assert.False(view.Linked);
    }

    [Fact]
    public async Task 更新_客户变更但保留旧来源_跨客户被拒绝且原链接不变()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var userId = SeedOperator(db, customerA.Id);

        // 把 B 也分配给同一操作员，使「跨客户来源」而非「越范围」成为拒绝原因
        var op = db.SysUsers.Single(u => u.Id == userId);
        var emp = db.BaseEmployees.Single(e => e.EmployeeCode == op.UserName);
        customerB.EmpId = emp.Id;
        db.SaveChanges();

        var order = SeedOrder(db, "SO-A-REBIND", customerA.Id);
        var complaint = SeedComplaint(db, "KS-REBIND", customerA.Id, order.Id);

        var ctl = BuildController(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(complaint.Id, Request(customerB.Id, order.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        // 被拒更新绝不静默重绑定：原客户与来源链接原样保留
        var stored = db.FinanceComplaints.AsNoTracking().Single(c => c.Id == complaint.Id);
        Assert.Equal(customerA.Id, stored.CustomerId);
        Assert.Equal(order.Id, stored.SalesOrderId);
    }

    // ==================== 5. 源码契约：控制器 / 服务 / 前端接入 ====================

    [Fact]
    public void 源码契约_候选端点先授权后取数_归一化先于计数_前端接入声明式选择器()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "FinanceReceiptComplaintControllers.cs"));
        var complaints = controller[controller.IndexOf(
            "class FinanceComplaintController", StringComparison.Ordinal)..];

        Assert.Contains("[HttpGet(\"sales-order-candidates\")]", complaints);
        Assert.Contains("GetSalesOrderCandidates", complaints);
        Assert.Contains("[HttpGet(\"{id:long}/sales-order-source\")]", complaints);
        Assert.Contains("ComplaintSalesOrderSourceService.QueryCandidatesAsync", complaints);
        Assert.Contains("ComplaintSalesOrderSourceService.DescribeStoredSourceAsync", complaints);

        // 授权（既有客诉单菜单 + 既有销售订单菜单 + 客户数据范围）先于候选取数
        var menuCheck = complaints.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal);
        var sourceMenuCheck = complaints.IndexOf("EnsureSourceMenuAuthorizedAsync", StringComparison.Ordinal);
        var scopeCheck = complaints.IndexOf("EnsureCustomerInScope", StringComparison.Ordinal);
        var query = complaints.IndexOf("QueryCandidatesAsync", StringComparison.Ordinal);
        Assert.True(menuCheck >= 0 && sourceMenuCheck >= 0 && scopeCheck >= 0 && query >= 0);
        Assert.True(menuCheck < query);
        Assert.True(sourceMenuCheck < query);
        Assert.True(scopeCheck < query);

        // 最终保存仍走 ERP-388 生命周期规则复核精确来源（候选选择不等于授权）
        Assert.Contains("ValidateAndAuthorizeLinkAsync", complaints);

        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "ComplaintSalesOrderSourceService.cs"));
        // 归一化必须发生在其后真正的计数调用之前（注释里的提及不算，取最后一次 CountAsync 调用点）
        var count = service.LastIndexOf("CountAsync", StringComparison.Ordinal);
        Assert.True(count >= 0);
        Assert.True(service.IndexOf("NormalizeKeyword", StringComparison.Ordinal) < count);
        Assert.True(service.IndexOf("NormalizePage", StringComparison.Ordinal) < count);
        Assert.True(service.IndexOf("NormalizePageSize", StringComparison.Ordinal) < count);
        Assert.Contains("var total = await source.CountAsync", service);

        var modules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-finance.js"));
        Assert.Contains("selector: 'complaint-sales-order-source'", modules);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/complaint-sales-order-source.js", index);
    }
}





