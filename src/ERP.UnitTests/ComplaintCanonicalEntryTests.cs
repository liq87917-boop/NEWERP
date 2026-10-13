using System.ComponentModel.DataAnnotations;
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
/// ERP-483 客诉单规范入口（<c>api/finance/complaints</c>）与「省略客诉单号」手工录入契约单元测试
/// （内存库 <see cref="TestDbFactory"/>，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="FinanceComplaint.ComplaintNo"/> 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），
/// 长度约束保留；客诉单本身没有金额 / 币种字段（记录客诉绝不退款、绝不调库存、绝不取消订单）；</item>
/// <item>新增入口：省略 / 空串 / 伪造客诉单号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳全部由服务端
/// 覆盖（客户端注入值不参与持久化）；</item>
/// <item>typed nullable 来源往返：显式关联来源销售订单 / 留空（<c>null</c> / <c>0</c>）显式断开原样往返，
/// 备注保存 → 重开 → 修改原样往返；</item>
/// <item>拒绝先于编号与持久化：无身份 / 缺菜单 / 停用账号 / 无效 · 已删除 · 已停用客户 / 跨客户 · 已取消 ·
/// 悬空来源 / 缺来源菜单 / 超长文本一律零写入且零占号；</item>
/// <item>生命周期：提交 / 审核 / 取消沿用既有护栏，合法流转绝不改写来源销售订单状态、也绝不产生收款 / 库存记录；</item>
/// <item>接线契约：菜单路由不再进入旧存储过程族（旧族读侧保留、旧写路径 fail closed）、规范模块接线、
/// 校验先于单号预约、交付说明如实记录浏览器验收口径。</item>
/// </list></para>
/// <para>真实隔离 SQL / HTTP 的端到端验收见
/// <c>ERP.IntegrationTests/ComplaintCanonicalEntrySqlServerTests.cs</c>。</para>
/// </summary>
public class ComplaintCanonicalEntryTests
{
    // ==================== 0. 脚手架 ====================

    private sealed class CountingDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的客诉单号次数（用于证明被拒请求绝不消耗客诉单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            Assert.Equal(DocumentType.Complaint, documentType);
            return Task.FromResult($"KS{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static FinanceComplaintController NewController(
        ErpDbContext db, long? userId, IDocumentNumberService? noService = null)
    {
        var ctl = new FinanceComplaintController(db, noService ?? new CountingDocumentNumberService());
        TestAuth.SetUser(ctl, userId);
        return ctl;
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
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
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
    /// 播种一个真实登录账号：特权（系统内置角色，全客户范围）+ 显式授予既有「客诉单」（complaint）菜单，
    /// 可选授予既有「销售订单」（sales-order）菜单；可禁用以覆盖 fail closed 场景。不新增权限模型。
    /// </summary>
    private static long SeedOperator(
        ErpDbContext db, UserStatus status = UserStatus.Enabled,
        bool withComplaintMenu = true, bool withSalesOrderMenu = true)
    {
        var role = new SysRole
        {
            RoleName = "客诉规范入口操作角色",
            RoleCode = $"Complaint483Op-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"complaint483-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "客诉规范入口操作员",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withComplaintMenu)
            GrantMenu(db, role.Id, FinanceComplaintLifecycleRules.RequiredMenuCode,
                FinanceComplaintLifecycleRules.RequiredMenuText, "/finance/complaints");
        if (withSalesOrderMenu)
            GrantMenu(db, role.Id, FinanceComplaintLifecycleRules.SourceRequiredMenuCode,
                FinanceComplaintLifecycleRules.SourceRequiredMenuText, "/sales/orders");

        return user.Id;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(g => roleIds.Contains(g.RoleId) && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static FinanceComplaint Stored(ErpDbContext db, long id)
        => db.FinanceComplaints.AsNoTracking().Single(c => c.Id == id);

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static FinanceComplaint Request(
        long customerId, long? salesOrderId = null, string complaintType = "质量",
        string description = "描述", string responsibleDept = "质检部",
        string handleResult = "", string remark = "ERP-483_TEST")
        => new()
        {
            ComplaintDate = DateTime.Today,
            CustomerId = customerId,
            SalesOrderId = salesOrderId,
            ComplaintType = complaintType,
            Description = description,
            ResponsibleDept = responsibleDept,
            HandleResult = handleResult,
            Remark = remark
        };

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    // ==================== 1. 实体契约：省略客诉单号 ====================

    [Fact]
    public void Entity_客诉单号不再显式必填_长度约束保留()
    {
        var property = typeof(FinanceComplaint).GetProperty(nameof(FinanceComplaint.ComplaintNo))!;

        Assert.Equal(typeof(string), property.PropertyType);
        Assert.False(Attribute.IsDefined(property, typeof(RequiredAttribute), true),
            "FinanceComplaint.ComplaintNo 不得显式必填（否则省略客诉单号会在模型绑定阶段被拒为 HTTP 400）");
        var maxLength = Assert.Single(property
            .GetCustomAttributes(typeof(MaxLengthAttribute), true)
            .Cast<MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
    }

    [Fact]
    public void Entity_客诉单不含金额币种字段_不产生退款或库存语义()
    {
        var names = typeof(FinanceComplaint).GetProperties().Select(p => p.Name).ToArray();

        // 客诉单没有任何金额 / 币种字段：记录客诉绝不等于退款 / 冲销 / 库存调整 / 金额上限判定。
        Assert.DoesNotContain("Amount", names);
        Assert.DoesNotContain("Currency", names);
        Assert.DoesNotContain("TotalAmount", names);
        // 来源销售订单是可空引用（历史「未关联来源」语义），绝不回落为哨兵值。
        Assert.Equal(typeof(long?),
            typeof(FinanceComplaint).GetProperty(nameof(FinanceComplaint.SalesOrderId))!.PropertyType);
    }


    // ==================== 2. 省略 / 伪造客诉单号：服务端权威覆盖 ====================

    [Fact]
    public async Task Create_省略客诉单号_服务端生成权威单号与规范身份()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-483", "客户483");
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, userId, no);

        var request = Request(customer.Id);
        request.Id = 987654;                        // 伪造主键
        request.ComplaintNo = string.Empty;         // 省略 / 空串客诉单号
        request.Status = DocumentStatus.Approved;   // 伪造状态
        request.IsDeleted = true;                   // 伪造删除态
        request.CreatedAt = new DateTime(2000, 1, 1);
        request.UpdatedAt = new DateTime(2000, 1, 1);

        var id = CreatedId(await ctl.Create(request));
        Assert.True(id > 0);

        var stored = Stored(db, id);
        Assert.StartsWith("KS", stored.ComplaintNo, StringComparison.Ordinal);
        Assert.Equal(1, no.Calls);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        // 客户端注入的 2000 年时间戳不参与持久化：服务端权威覆盖 + 审计钩子按服务端时间重写。
        Assert.True(stored.UpdatedAt > new DateTime(2020, 1, 1));
        Assert.True(stored.CreatedAt > new DateTime(2020, 1, 1));
        Assert.Null(stored.SalesOrderId);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal("ERP-483_TEST", stored.Remark);
        Assert.Single(db.FinanceComplaints.Where(c => c.Id == id));
    }

    [Fact]
    public async Task Create_伪造客诉单号_由服务端权威单号覆盖()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-483B", "客户483B");
        var ctl = NewController(db, userId);

        var request = Request(customer.Id, remark: "FORGED_NO");
        request.ComplaintNo = "FORGED-483-NO";

        var id = CreatedId(await ctl.Create(request));
        var stored = Stored(db, id);

        Assert.NotEqual("FORGED-483-NO", stored.ComplaintNo);
        Assert.StartsWith("KS", stored.ComplaintNo, StringComparison.Ordinal);
    }

    // ==================== 3. 可空来源往返：显式关联 / 显式断开 ====================

    [Fact]
    public async Task 来源销售订单_显式关联与显式断开_原样往返且不改写单号状态()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-483C", "客户483C");
        var order = SeedOrder(db, "SO-483-1", customer.Id);
        var ctl = NewController(db, userId);

        // 新建：留空 = 显式未关联（历史语义，绝不回落为哨兵值）
        var id = CreatedId(await ctl.Create(Request(customer.Id, salesOrderId: null)));
        var unlinked = Stored(db, id);
        Assert.Null(unlinked.SalesOrderId);
        var number = unlinked.ComplaintNo;

        // 显式关联权威来源订单
        await ctl.Update(id, Request(customer.Id, salesOrderId: order.Id, remark: "LINKED"));
        var linked = Stored(db, id);
        Assert.Equal(order.Id, linked.SalesOrderId);
        Assert.Equal("LINKED", linked.Remark);
        Assert.Equal(number, linked.ComplaintNo);
        Assert.Equal(DocumentStatus.Pending, linked.Status);
        Assert.NotNull(linked.UpdatedAt);

        // 显式断开（0 与 null 同口径归一为未关联）
        await ctl.Update(id, Request(customer.Id, salesOrderId: 0, remark: "CLEARED"));
        var clearedByZero = Stored(db, id);
        Assert.Null(clearedByZero.SalesOrderId);
        Assert.Equal("CLEARED", clearedByZero.Remark);
        Assert.Equal(number, clearedByZero.ComplaintNo);

        await ctl.Update(id, Request(customer.Id, salesOrderId: null, remark: "UNLINKED"));
        var cleared = Stored(db, id);
        Assert.Null(cleared.SalesOrderId);
        Assert.Equal(number, cleared.ComplaintNo);
        Assert.Equal(DocumentStatus.Pending, cleared.Status);

        // 来源订单本身绝不被客诉单改写
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 来源销售订单_跨客户已取消与悬空来源_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-483D", "客户483D");
        var other = SeedCustomer(db, "C-483E", "客户483E");
        var foreign = SeedOrder(db, "SO-483-FOREIGN", other.Id);
        var cancelled = SeedOrder(db, "SO-483-CANCEL", customer.Id, DocumentStatus.Cancelled);
        var no = new CountingDocumentNumberService();
        var ctl = NewController(db, userId, no);

        var before = db.FinanceComplaints.Count();

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(Request(customer.Id, foreign.Id)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(Request(customer.Id, cancelled.Id)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(Request(customer.Id, 987654321L)));

        Assert.Equal(0, no.Calls);
        Assert.Equal(before, db.FinanceComplaints.Count());
        Assert.Equal(DocumentStatus.Approved,
            db.SalesOrders.AsNoTracking().Single(o => o.Id == foreign.Id).Status);
        Assert.Equal(DocumentStatus.Cancelled,
            db.SalesOrders.AsNoTracking().Single(o => o.Id == cancelled.Id).Status);
    }


    // ==================== 4. 客户数据范围：受限账号（非特权）必须显式具备销售订单菜单 ====================

    /// <summary>
    /// 播种一个<b>受限</b>账号：非系统角色 + 既有「客诉单」菜单（可选「销售订单」菜单）+
    /// 员工编码映射，数据范围恰好覆盖被分配的客户。不新增权限模型。
    /// </summary>
    private static long SeedRestrictedOperator(
        ErpDbContext db, long inScopeCustomerId, bool withComplaintMenu = true, bool withSalesOrderMenu = true)
    {
        var employeeCode = $"complaint483-restricted-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = employeeCode,
            EmployeeName = employeeCode,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employeeCode,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "受限客诉操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "受限客诉操作角色",
            RoleCode = $"Complaint483Restricted-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withComplaintMenu)
            GrantMenu(db, role.Id, FinanceComplaintLifecycleRules.RequiredMenuCode,
                FinanceComplaintLifecycleRules.RequiredMenuText, "/finance/complaints");
        if (withSalesOrderMenu)
            GrantMenu(db, role.Id, FinanceComplaintLifecycleRules.SourceRequiredMenuCode,
                FinanceComplaintLifecycleRules.SourceRequiredMenuText, "/sales/orders");

        var customer = db.BaseCustomers.Single(c => c.Id == inScopeCustomerId);
        customer.EmpId = employee.Id;
        db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task 受限账号_范围外客户与缺来源菜单_拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-483J", "范围内客户");
        var other = SeedCustomer(db, "C-483K", "范围外客户");
        var ownOrder = SeedOrder(db, "SO-483-OWN", own.Id);
        var noSourceMenu = SeedRestrictedOperator(db, own.Id, withSalesOrderMenu: false);
        var restricted = SeedRestrictedOperator(db, own.Id);
        var no = new CountingDocumentNumberService();
        var before = db.FinanceComplaints.Count();

        // 缺来源菜单 + 显式来源 → 权限不足（非特权账号不豁免来源菜单）
        await AssertCode(ErrorCodes.Forbidden, () =>
            NewController(db, noSourceMenu, no).Create(Request(own.Id, ownOrder.Id)));

        // 范围外客户 → 权限不足（不泄露范围外客户归属）
        await AssertCode(ErrorCodes.Forbidden, () =>
            NewController(db, restricted, no).Create(Request(other.Id)));

        // 范围内客户 + 显式来源 → 放行（受限账号在范围内可正常录入）
        var created = await NewController(db, restricted, no).Create(Request(own.Id, ownOrder.Id));
        Assert.True(CreatedId(created) > 0);

        Assert.Equal(1, no.Calls);                    // 仅放行的那次消耗单号
        Assert.Equal(before + 1, db.FinanceComplaints.Count());
    }


    // ==================== 5. 拒绝先于编号与持久化（身份 / 菜单 / 客户 / 文本） ====================

    [Fact]
    public async Task 拒绝先于编号与持久化_身份菜单客户与文本零写入零占号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-483L", "客户483L");
        var disabledCustomer = SeedCustomer(db, "C-483M", "停用客户", status: 0);
        var deletedCustomer = SeedCustomer(db, "C-483N", "已删除客户", deleted: true);

        var operatorUser = SeedOperator(db);
        var disabledUser = SeedOperator(db, status: UserStatus.Disabled);
        var noMenuUser = SeedOperator(db, withComplaintMenu: false);
        var revokedUser = SeedOperator(db);
        RevokeMenus(db, revokedUser);

        var no = new CountingDocumentNumberService();
        var before = db.FinanceComplaints.Count();

        // 无身份 / 停用账号 / 缺菜单 / 撤销菜单 → fail closed
        await AssertCode(ErrorCodes.Unauthorized, () =>
            NewController(db, null, no).Create(Request(customer.Id)));
        await AssertCode(ErrorCodes.Forbidden, () =>
            NewController(db, disabledUser, no).Create(Request(customer.Id)));
        await AssertCode(ErrorCodes.Forbidden, () =>
            NewController(db, noMenuUser, no).Create(Request(customer.Id)));
        await AssertCode(ErrorCodes.Forbidden, () =>
            NewController(db, revokedUser, no).Create(Request(customer.Id)));

        // 无效 / 已删除 / 已停用客户
        await AssertCode(ErrorCodes.NotFound, () =>
            NewController(db, operatorUser, no).Create(Request(0)));
        await AssertCode(ErrorCodes.NotFound, () =>
            NewController(db, operatorUser, no).Create(Request(987654321)));
        await AssertCode(ErrorCodes.NotFound, () =>
            NewController(db, operatorUser, no).Create(Request(deletedCustomer.Id)));
        await AssertCode(ErrorCodes.RuleConflict, () =>
            NewController(db, operatorUser, no).Create(Request(disabledCustomer.Id)));

        // 超长文本（客诉类型 100 / 客诉描述 1000 / 责任部门 100 / 处理结果 1000 / 备注 500）
        await AssertCode(ErrorCodes.InvalidParameter, () =>
            NewController(db, operatorUser, no).Create(Request(customer.Id, complaintType: new string('x', 101))));
        await AssertCode(ErrorCodes.InvalidParameter, () =>
            NewController(db, operatorUser, no).Create(Request(customer.Id, description: new string('x', 1001))));
        await AssertCode(ErrorCodes.InvalidParameter, () =>
            NewController(db, operatorUser, no).Create(Request(customer.Id, responsibleDept: new string('x', 101))));
        await AssertCode(ErrorCodes.InvalidParameter, () =>
            NewController(db, operatorUser, no).Create(Request(customer.Id, handleResult: new string('x', 1001))));
        await AssertCode(ErrorCodes.InvalidParameter, () =>
            NewController(db, operatorUser, no).Create(Request(customer.Id, remark: new string('x', 501))));

        Assert.Equal(0, no.Calls);
        Assert.Equal(before, db.FinanceComplaints.Count());
    }

    // ==================== 6. 生命周期：合法流转零财务 / 零库存副作用 ====================

    [Fact]
    public async Task 生命周期_提交审核取消_合法流转且零财务与零库存副作用()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedOperator(db);
        var customer = SeedCustomer(db, "C-483O", "客户483O");
        var order = SeedOrder(db, "SO-483-LIFE", customer.Id);
        var ctl = NewController(db, userId);

        var id = CreatedId(await ctl.Create(Request(customer.Id, order.Id)));
        var receiptsBefore = db.FinanceReceipts.Count();
        var stockBefore = db.StockMovements.Count();

        Assert.IsType<OkObjectResult>(await ctl.Submit(id));
        Assert.Equal(DocumentStatus.Submitted, Stored(db, id).Status);
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));
        Assert.Equal(DocumentStatus.Approved, Stored(db, id).Status);

        // 已审核属禁止修改状态：拒绝先于任何写入（原始字段不变）
        var before = Stored(db, id);
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Update(id, Request(customer.Id, remark: "MUTATE")));
        var afterReject = Stored(db, id);
        Assert.Equal(before.Remark, afterReject.Remark);
        Assert.Equal(before.ComplaintNo, afterReject.ComplaintNo);
        Assert.Equal(before.Status, afterReject.Status);

        // 客诉只是追溯证据：绝不改写来源订单、绝不产生收款 / 库存记录
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id).Status);
        Assert.Equal(receiptsBefore, db.FinanceReceipts.Count());
        Assert.Equal(stockBefore, db.StockMovements.Count());

        // 已审核仍可取消（既有口径）；重复取消被拒且状态不变，历史来源链接保留
        Assert.IsType<OkObjectResult>(await ctl.Cancel(id));
        Assert.Equal(DocumentStatus.Cancelled, Stored(db, id).Status);
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Cancel(id));
        var cancelled = Stored(db, id);
        Assert.Equal(DocumentStatus.Cancelled, cancelled.Status);
        Assert.Equal(order.Id, cancelled.SalesOrderId);
    }


    // ==================== 7. 接线契约：菜单路由 / 规范模块 / 校验先于单号预约 ====================

    [Fact]
    public void 菜单路由_客诉单走规范模块_旧族路由停用并保留迁移记录()
    {
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        // 停用方式：键与字面量保留为迁移记录，取值求值为假值空串（不参与任何路由判定）。
        Assert.Contains("complaint: 'complaint'", billV2);
        Assert.Contains("complaint: 'complaint' && ''", billV2);
        Assert.DoesNotContain("complaint: 'complaint',", billV2);
        Assert.Contains("ERP-483", billV2);

        // app.js 先判 BILL_CODE_MAP 再判 MODULES：取值停用为假值后菜单必然落到规范模块。
        var app = ReadSource("src", "ERP.Api", "wwwroot", "js", "app.js");
        var billMapCheck = app.IndexOf("else if (BILL_CODE_MAP[code]) renderBillV2", StringComparison.Ordinal);
        var modulesLookup = app.IndexOf("const mod = MODULES[code]", StringComparison.Ordinal);
        Assert.True(billMapCheck >= 0 && modulesLookup > billMapCheck,
            "app.js 先判 BILL_CODE_MAP 再判 MODULES：映射取值停用为假值后菜单必然落到规范客诉单模块");

        // 规范模块：api/finance/complaints（规范 Id）+ 可空来源销售订单 + 备注往返；绝不调用旧存储过程族。
        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-finance.js");
        Assert.Contains("title: '客诉单', api: '/api/finance/complaints'", modules);
        Assert.Contains("type: 'number', nullable: true, selector: 'complaint-sales-order-source'", modules);
        Assert.Contains("key: 'remark'", modules);
        Assert.DoesNotContain("/api/v2/bills", modules);

        // crud.js 规范路径（mod.api）绝不调用旧存储过程族，声明式可空引用留空提交 null。
        var crud = ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js");
        Assert.Contains("f.nullable === true ? null : 0", crud);
        Assert.Contains("await api(`${mod.api}/${id}`, 'PUT', body)", crud);
        Assert.Contains("await api(mod.api, 'POST', body)", crud);
        Assert.DoesNotContain("/api/v2/bills", crud);

        // 既有 ERP-389 选择器：只回填权威来源 Id，支持「显式断开」。
        var selector = ReadSource("src", "ERP.Api", "wwwroot", "js", "complaint-sales-order-source.js");
        Assert.Contains("function cosUnlinkSource()", selector);
        Assert.Contains("function cosFillForm(fill)", selector);
        Assert.Contains("ERP-483", selector);
        Assert.DoesNotContain("/api/v2/bills", selector);

        var index = ReadSource("src", "ERP.Api", "wwwroot", "index.html");
        Assert.Contains("/js/modules-finance.js", index);
        Assert.Contains("/js/complaint-sales-order-source.js", index);

        // 旧族自身（旧库单体表 Oid 读侧）保持只读可用；规范 Id 与旧 Oid 之间不存在任何映射。
        var billConfig = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-config3.js");
        Assert.Contains("complaint: {", billConfig);

        // 旧写路径整体 fail closed（没有任何已验证适配器），规范路由指向 api/finance/complaints。
        var legacyRules = ReadSource("src", "ERP.Application", "Services", "LegacyBillMutationRules.cs");
        Assert.Contains("\"complaint\"", legacyRules);
        Assert.Contains("/api/finance/complaints", legacyRules);
        Assert.Contains("HasValidatedAdapter", legacyRules);
    }

    [Fact]
    public void 控制器契约_创建先授权与客户来源校验后单号_服务端覆盖身份()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "FinanceReceiptComplaintControllers.cs");
        var complaints = controller[controller.IndexOf(
            "class FinanceComplaintController", StringComparison.Ordinal)..];

        Assert.Contains("ERP-483", complaints);
        Assert.Contains("EnsureMenuAuthorizedAsync", complaints);
        Assert.Contains("entity.Id = 0;", complaints);
        Assert.Contains("entity.IsDeleted = false;", complaints);
        Assert.Contains("entity.UpdatedAt = null;", complaints);

        // 仅切片 Create 方法体，证明「授权 / 客户 / 来源校验」严格先于单号预约与写入。
        var createStart = complaints.IndexOf(
            "public async Task<IActionResult> Create([FromBody] FinanceComplaint entity)", StringComparison.Ordinal);
        var createEnd = complaints.IndexOf(
            "public async Task<IActionResult> Update(long id, [FromBody] FinanceComplaint entity)",
            StringComparison.Ordinal);
        Assert.True(createStart >= 0 && createEnd > createStart);
        var create = complaints[createStart..createEnd];

        var menu = create.IndexOf("EnsureAuthorizedAsync", StringComparison.Ordinal);
        var customer = create.IndexOf("EnsureCustomerAvailable", StringComparison.Ordinal);
        var link = create.IndexOf("ValidateAndAuthorizeLinkAsync", StringComparison.Ordinal);
        var generate = create.IndexOf("GenerateAsync(DocumentType.Complaint)", StringComparison.Ordinal);
        var write = create.IndexOf("Db.FinanceComplaints.Add", StringComparison.Ordinal);
        Assert.True(menu >= 0 && customer >= 0 && link >= 0 && generate >= 0 && write >= 0);
        Assert.True(menu < generate && menu < write);
        Assert.True(customer < generate && customer < write);
        Assert.True(link < generate && link < write);

        // ERP-388 锁序：先来源销售订单行，后客诉单行（绝不反向）。
        var updateSourceLock = complaints.IndexOf(
            "LockSourceOrderRowsAsync(storedSourceOrderId, entity.SalesOrderId)", StringComparison.Ordinal);
        var updateComplaintLock = complaints.IndexOf("LockComplaintRowAsync(Db, id)", StringComparison.Ordinal);
        Assert.True(updateSourceLock >= 0 && updateComplaintLock >= 0 && updateSourceLock < updateComplaintLock);
    }

    [Fact]
    public void 交付说明与离线契约_记录规范端点与浏览器验收口径()
    {
        var doc = ReadSource("docs", "complaint-canonical-entry.md");
        Assert.Contains("ERP-483", doc);
        Assert.Contains("/api/finance/complaints", doc);
        Assert.Contains("真实浏览器", doc);
        Assert.Contains("未执行", doc);

        var browser = ReadSource("tests", "complaint-canonical-entry.browser.js");
        Assert.Contains("BILL_CODE_MAP", browser);
        Assert.Contains("complaint", browser);
        Assert.Contains("ERP-483", browser);
    }
}

