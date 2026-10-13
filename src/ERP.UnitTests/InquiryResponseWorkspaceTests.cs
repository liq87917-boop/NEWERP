using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-104 询价报价响应时间工作台（只读派生）单元测试。
/// 覆盖：稳定询价单 Id 分页与日期 / 客户 / 关键字筛选、只按持久化 InquiryId 显式链接（不按单号 / 文本推断）、
/// 版本链根单口径（版本不重复计入）、缺失链接 / 缺失日期 / 负间隔（链接异常）分别标注、业务员数据范围、软删除行排除、
/// 分页有界与只读不写库、纯规则文案，以及接口与前端接线契约。
/// <para>授权（ERP-500）：本工作台与 <c>api/inquiries</c> 是并列路由，控制器分页入口在读取任何 <c>Inquiries</c>
/// 计数或行之前<b>无条件</b>先经 <see cref="InquiryAuthorizationRules.EnsureAuthorizedAsync"/>（实时启用未删除身份 +
/// 账号状态 + 既有「询价单」菜单）。因此本文件的既有读契约用例均注入一个<b>真实启用</b>（非特权）且已授予既有
/// <c>inquiry</c> 菜单并映射业务员的身份（<see cref="SeedAuthorizedIdentity"/>），既不放宽也不依赖
/// <c>Request.Path</c> / 环境 / 空上下文绕过判定。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed，不做浏览器验收。</para>
/// </summary>
public class InquiryResponseWorkspaceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly DateTime InquiryDay = new(2026, 9, 10);

    /// <summary>授权身份持有的既有菜单（与 <c>SeedData.Menus</c> 的 <c>("inquiry","inquiry",…)</c> 同源，不新增菜单）。</summary>
    private const string InquiryMenuCode = InquiryAuthorizationRules.RequiredMenuCode;

    /// <summary>按稳定询价单 Id 分页的既有路由（仅用于构造已赋值请求路径，不参与任何授权分支）。</summary>
    private const string WorkspacePath = "/api/inquiries/response-times";

    /// <summary>默认工作台控制器：播种一个真实启用 + 既有 inquiry 菜单 + 映射业务员的身份并注入 HttpContext。</summary>
    private static InquiryResponseWorkspaceController BuildController(ErpDbContext db)
        => BuildController(db, SeedAuthorizedIdentity(db));

    /// <summary>按指定身份构造工作台控制器（不额外播种身份；<c>null</c> = 无身份）。</summary>
    private static InquiryResponseWorkspaceController BuildController(IErpDbContext db, long? userId)
        => NewController(db, userId?.ToString(), requestPath: null);

    /// <summary>
    /// 构造工作台控制器并注入（可空 / 可非数字）身份与请求路径；<c>requestPath = null</c> 时不设置 <c>Request.Path</c>。
    /// 授权是无条件的，绝不依赖请求路径放行或绕过。
    /// </summary>
    private static InquiryResponseWorkspaceController NewController(
        IErpDbContext db, string? rawIdentity, string? requestPath)
    {
        var controller = new InquiryResponseWorkspaceController(db);
        var claims = rawIdentity is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, rawIdentity) };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        if (requestPath is not null)
            http.Request.Path = requestPath;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>
    /// 播种一个<b>真实启用</b>（非特权）身份：既有「询价单」（<c>inquiry</c>）菜单授权 + 同名业务员员工映射，
    /// 并把已播种客户归入该业务员（仅隔离测试数据），因此菜单放行后仍走既有权威客户数据范围。
    /// 复用既有菜单（若隔离库尚无该菜单按种子口径补建一条），不新增任何角色 / 用户授权。
    /// </summary>
    private static long SeedAuthorizedIdentity(ErpDbContext db)
    {
        var user = SeedUser(db, $"inquiry-response-{Guid.NewGuid():N}");
        var role = SeedRole(db, $"InquiryResponseCase-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        GrantMenu(db, role.Id, InquiryMenuCode, InquiryAuthorizationRules.RequiredMenuText);

        var employee = SeedEmployee(db, user.UserName);
        foreach (var customer in db.BaseCustomers.Where(c => !c.IsDeleted).ToList())
            customer.EmpId = employee.Id;
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（复用既有菜单；缺失时按种子口径补建一条功能菜单，不新增权限模型）。</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>撤销账号当前角色下的全部菜单授权（模拟授权撤销，验证下一次请求立即收敛）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static InquiryResponseWorkspaceDto GetData(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<InquiryResponseWorkspaceDto>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Inquiry SeedInquiry(ErpDbContext db, string no, long customerId,
        DateTime? inquiryDate = null, bool deleted = false)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no,
            CustomerId = customerId,
            InquiryDate = inquiryDate ?? InquiryDay,
            IsDeleted = deleted
        };
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, long? inquiryId, DateTime quotationDate,
        long? rootQuotationId = null, bool deleted = false, string inquiryNo = "")
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            InquiryId = inquiryId,
            InquiryNo = inquiryNo,
            QuotationDate = quotationDate,
            RootQuotationId = rootQuotationId,
            RevisionNumber = rootQuotationId is null ? 1 : 2,
            IsDeleted = deleted
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    // 受限制业务员数据范围脚手架（与 SalespersonDataScopeTests 同口径）
    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = isSalesman, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    // ==================== 1. 分页与筛选 ====================

    [Fact]
    public async Task Workspace_pages_by_stable_inquiry_id()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-3", customer.Id, new DateTime(2026, 9, 3));
        SeedInquiry(db, "INQ-1", customer.Id, new DateTime(2026, 9, 1));
        SeedInquiry(db, "INQ-2", customer.Id, new DateTime(2026, 9, 2));

        var controller = BuildController(db);

        var page1 = GetData(await controller.GetPaged(new InquiryResponseQuery { Page = 1, PageSize = 2 }));
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        // 按稳定询价单 Id 升序分页，不按询价日期 / 单号排序
        Assert.Equal(new[] { "INQ-3", "INQ-1" }, page1.Items.Select(i => i.InquiryNo).ToArray());

        var page2 = GetData(await controller.GetPaged(new InquiryResponseQuery { Page = 2, PageSize = 2 }));
        Assert.Single(page2.Items);
        Assert.Equal("INQ-2", page2.Items[0].InquiryNo);
    }

    [Fact]
    public async Task Workspace_filters_by_date_range()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inRange = SeedInquiry(db, "INQ-A", customer.Id, new DateTime(2026, 9, 10));
        SeedInquiry(db, "INQ-B", customer.Id, new DateTime(2026, 9, 1));
        SeedInquiry(db, "INQ-C", customer.Id, new DateTime(2026, 9, 20));

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery
        {
            PageSize = 100,
            StartDate = new DateTime(2026, 9, 5),
            EndDate = new DateTime(2026, 9, 15)
        }));

        Assert.Single(data.Items);
        Assert.Equal(inRange.Id, data.Items[0].InquiryId);
    }

    [Fact]
    public async Task Workspace_filters_by_customer_and_keyword()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C001", "客户甲");
        var b = SeedCustomer(db, "C002", "客户乙");
        SeedInquiry(db, "INQ-AAA", a.Id);
        SeedInquiry(db, "INQ-BBB", b.Id);

        var controller = BuildController(db);

        var byCustomer = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100, CustomerId = a.Id }));
        Assert.Single(byCustomer.Items);
        Assert.Equal("INQ-AAA", byCustomer.Items[0].InquiryNo);

        var byKeyword = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100, Keyword = "BBB" }));
        Assert.Single(byKeyword.Items);
        Assert.Equal("INQ-BBB", byKeyword.Items[0].InquiryNo);
    }

    // ==================== 2. 显式链接 / 版本链 / 日期异常 ====================

    [Fact]
    public async Task Workspace_links_only_by_persisted_inquiry_id_not_text()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inquiry = SeedInquiry(db, "INQ-1", customer.Id);
        // 显式链接：InquiryId 指向该询价单
        SeedQuotation(db, "QT-LINK", inquiry.Id, InquiryDay.AddDays(3));
        // 文本相同但 InquiryId 为空：绝不按单号 / 文本推断链接
        SeedQuotation(db, "QT-NOLINK", null, InquiryDay.AddDays(1), inquiryNo: "INQ-1");

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.True(row.Quoted);
        Assert.Equal(InquiryResponseRules.StateQuoted, row.ResponseState);
        Assert.Equal(InquiryDay.AddDays(3), row.FirstQuotationDate);
        Assert.Equal("QT-LINK", row.QuotationNo);
    }

    [Fact]
    public async Task Workspace_revisions_never_inflate_response()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inquiry = SeedInquiry(db, "INQ-1", customer.Id);
        // 根单（初始版本）
        var root = SeedQuotation(db, "QT-ROOT", inquiry.Id, InquiryDay.AddDays(5));
        // 版本：RootQuotationId 指向根单、InquiryId 仍指向询价单、且日期更早（若计入版本会把「首张」错算成它）
        SeedQuotation(db, "QT-ROOT-R2", inquiry.Id, InquiryDay.AddDays(1), rootQuotationId: root.Id);

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.True(row.Quoted);
        // 只取版本链根单：首张有效报价日期 = 根单日期，版本日期（更早）不被计入
        Assert.Equal(InquiryDay.AddDays(5), row.FirstQuotationDate);
        Assert.Equal("QT-ROOT", row.QuotationNo);
        Assert.Equal(5, row.ElapsedDays);
    }

    [Fact]
    public async Task Workspace_negative_interval_is_inconsistent_link()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inquiry = SeedInquiry(db, "INQ-1", customer.Id, InquiryDay);
        // 报价日期早于询价日期 → 负间隔 → 链接异常
        SeedQuotation(db, "QT-EARLY", inquiry.Id, InquiryDay.AddDays(-9));

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.False(row.Quoted);
        Assert.Equal(InquiryResponseRules.StateInconsistentLink, row.ResponseState);
        Assert.Equal(-9, row.ElapsedDays);
        Assert.Contains("早于询价日期", row.EvidenceText);
    }

    [Fact]
    public async Task Workspace_missing_date_is_unquoted()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inquiry = SeedInquiry(db, "INQ-1", customer.Id, InquiryDay);
        // 报价日期缺失 / 无效（DateTime 默认值年份为 1）→ 未报价（缺失日期证据）
        SeedQuotation(db, "QT-NODATE", inquiry.Id, default);

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.False(row.Quoted);
        Assert.Equal(InquiryResponseRules.StateUnquoted, row.ResponseState);
        Assert.Null(row.FirstQuotationDate);
        Assert.Null(row.ElapsedDays);
        Assert.Contains("缺失", row.EvidenceText);
    }

    [Fact]
    public async Task Workspace_unquoted_when_no_linked_quotation()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id, InquiryDay);

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.False(row.Quoted);
        Assert.Equal(InquiryResponseRules.StateUnquoted, row.ResponseState);
        Assert.Null(row.FirstQuotationDate);
        Assert.Contains("无有效报价单链接", row.EvidenceText);
    }

    [Fact]
    public async Task Workspace_applies_salesperson_data_scope()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        // ERP-500：受限账号必须具备既有「询价单」菜单授权（否则 fail closed），并映射为业务员。
        GrantMenu(db, role.Id, InquiryMenuCode, InquiryAuthorizationRules.RequiredMenuText);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", alice.Id);
        var others = SeedCustomer(db, "C002", "别人的客户", alice.Id + 1000);
        SeedInquiry(db, "INQ-MINE", mine.Id);
        SeedInquiry(db, "INQ-OTHER", others.Id);

        var controller = BuildController(db, user.Id);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.Equal("INQ-MINE", row.InquiryNo);
    }

    // ==================== 3. 软删除 / 只读 / 前端接线 ====================

    [Fact]
    public async Task Workspace_excludes_soft_deleted_rows()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        // 软删除询价单：列表直接排除
        SeedInquiry(db, "INQ-DEL", customer.Id, InquiryDay, deleted: true);
        // 正常询价单 + 软删除报价单：报价单排除 → 未报价
        var inquiry = SeedInquiry(db, "INQ-KEEP", customer.Id, InquiryDay);
        SeedQuotation(db, "QT-DEL", inquiry.Id, InquiryDay.AddDays(2), deleted: true);

        var controller = BuildController(db);

        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        var row = Assert.Single(data.Items);
        Assert.Equal("INQ-KEEP", row.InquiryNo);
        Assert.False(row.Quoted);
        Assert.Equal(InquiryResponseRules.StateUnquoted, row.ResponseState);
    }

    [Fact]
    public async Task Workspace_is_read_only_no_write()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inquiry = SeedInquiry(db, "INQ-1", customer.Id, InquiryDay);
        SeedQuotation(db, "QT-1", inquiry.Id, InquiryDay.AddDays(3));

        var inquiryCount = db.Inquiries.Count();
        var quotationCount = db.Quotations.Count();

        var controller = BuildController(db);
        var data = GetData(await controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));

        // 只读：无新增 / 无修改 / 无删除，无任何待保存的变更追踪
        Assert.Equal(inquiryCount, db.Inquiries.Count());
        Assert.Equal(quotationCount, db.Quotations.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
        Assert.False(string.IsNullOrWhiteSpace(data.ReadOnlyText));
        Assert.False(string.IsNullOrWhiteSpace(data.BoundaryText));
        Assert.False(string.IsNullOrWhiteSpace(data.DisclaimerText));
    }

    [Fact]
    public void Frontend_wiring_registers_workspace()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "inquiry-response-workspace.js"));
        Assert.Contains("openInquiryResponseWorkspace", js);
        Assert.Contains("/api/inquiries/response-times", js);

        var modulesDoc = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("openInquiryResponseWorkspace()", modulesDoc);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/inquiry-response-workspace.js", index);
    }

    // ==================== 4. ERP-500 实时授权（身份 / 账号状态 / 既有询价单菜单） ====================

    /// <summary>播种可指定状态 / 删除 / 系统角色 / 菜单 / 业务员映射的用例身份（每个用例独立），返回用户 Id。</summary>
    private static long SeedScenarioUser(ErpDbContext db, UserStatus status = UserStatus.Enabled,
        bool deleted = false, bool system = false, bool grantInquiryMenu = false, bool mapSalesman = false)
    {
        var user = new SysUser
        {
            UserName = $"inq-resp-case-{Guid.NewGuid():N}",
            DisplayName = "询价报价响应时间授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "询价报价响应时间授权用例角色",
            RoleCode = $"InquiryResponseCase-{Guid.NewGuid():N}",
            IsSystem = system
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantInquiryMenu)
            GrantMenu(db, role.Id, InquiryMenuCode, InquiryAuthorizationRules.RequiredMenuText);
        if (mapSalesman)
            SeedEmployee(db, user.UserName);
        return user.Id;
    }

    /// <summary>断言本次调用从未访问 <c>Inquiries</c> 数据集（拒绝路径零计数 / 零行读取）且零写入。</summary>
    private static void AssertZeroInquiryReads(CountingDbContext counter)
    {
        Assert.DoesNotContain(nameof(IErpDbContext.Inquiries), counter.ReadProperties);
        Assert.Equal(0, counter.WriteCalls);
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    [Fact]
    public async Task Authorization_missing_identity_is_unauthenticated_and_reads_no_inquiries()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id);

        var counting = CountingDbContext.Wrap(db);
        var controller = NewController(counting.Proxy, rawIdentity: null, requestPath: WorkspacePath);

        await AssertCode(ErrorCodes.Unauthorized,
            () => controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(counting);
    }

    [Fact]
    public async Task Authorization_zero_negative_or_non_numeric_identity_is_unauthenticated_without_reads()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id);

        foreach (var raw in new[] { "0", "-5", "abc", " " })
        {
            var counting = CountingDbContext.Wrap(db);
            var controller = NewController(counting.Proxy, raw, WorkspacePath);
            await AssertCode(ErrorCodes.Unauthorized,
                () => controller.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
            AssertZeroInquiryReads(counting);
        }
    }

    [Fact]
    public async Task Authorization_unknown_or_deleted_identity_is_unauthenticated_without_reads()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id);
        var deleted = SeedScenarioUser(db, deleted: true, grantInquiryMenu: true, mapSalesman: true);

        var deletedCounting = CountingDbContext.Wrap(db);
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(deletedCounting.Proxy, deleted.ToString(), WorkspacePath)
                .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(deletedCounting);

        // 未知（不存在）账号与已删除账号同为未认证，绝不因数据库里没有该 Id 而放行。
        var missing = deleted + 1000;
        var missingCounting = CountingDbContext.Wrap(db);
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(missingCounting.Proxy, missing.ToString(), WorkspacePath)
                .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(missingCounting);
    }

    [Fact]
    public async Task Authorization_disabled_identity_is_forbidden_without_reads()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id);
        var disabled = SeedScenarioUser(db, UserStatus.Disabled, grantInquiryMenu: true, mapSalesman: true);

        var counting = CountingDbContext.Wrap(db);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(counting.Proxy, disabled.ToString(), WorkspacePath)
                .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(counting);
    }

    [Fact]
    public async Task Authorization_missing_inquiry_menu_is_forbidden_without_reads()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id);
        // 真实启用 + 已映射业务员，但未授予既有「询价单」菜单 → fail closed（绝不回退为放行）。
        var noMenu = SeedScenarioUser(db, grantInquiryMenu: false, mapSalesman: true);

        var counting = CountingDbContext.Wrap(db);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(counting.Proxy, noMenu.ToString(), WorkspacePath)
                .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(counting);
    }

    [Fact]
    public async Task Authorization_revoked_inquiry_menu_converges_to_forbidden_on_next_request()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inquiry = SeedInquiry(db, "INQ-1", customer.Id);
        var userId = SeedAuthorizedIdentity(db);

        var allowed = CountingDbContext.Wrap(db);
        var data = GetData(await NewController(allowed.Proxy, userId.ToString(), WorkspacePath)
            .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        Assert.Equal(inquiry.Id, Assert.Single(data.Items).InquiryId);
        Assert.Contains(nameof(IErpDbContext.Inquiries), allowed.ReadProperties);

        // 请求之间撤销既有菜单授权：下一次请求立即收敛为拒绝（绝不缓存），且拒绝路径零 Inquiries 读取。
        RevokeMenus(db, userId);

        var denied = CountingDbContext.Wrap(db);
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(denied.Proxy, userId.ToString(), WorkspacePath)
                .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(denied);
    }

    [Fact]
    public async Task Authorization_menu_granted_identity_keeps_the_existing_read_contract()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        var inRange = SeedInquiry(db, "INQ-IN-RANGE", customer.Id, new DateTime(2026, 9, 10));
        SeedInquiry(db, "INQ-EARLY", customer.Id, new DateTime(2026, 9, 1));
        SeedQuotation(db, "QT-ROOT", inRange.Id, new DateTime(2026, 9, 13));
        var userId = SeedAuthorizedIdentity(db);

        var counting = CountingDbContext.Wrap(db);
        // 未设置 Request.Path：放行与请求路径无关。
        var page = GetData(await NewController(counting.Proxy, userId.ToString(), null)
            .GetPaged(new InquiryResponseQuery
            {
                PageSize = 100,
                StartDate = new DateTime(2026, 9, 5),
                EndDate = new DateTime(2026, 9, 15),
                Keyword = "IN-RANGE"
            }));

        var row = Assert.Single(page.Items);
        Assert.Equal(inRange.Id, row.InquiryId);
        Assert.True(row.Quoted);
        Assert.Equal(3, row.ElapsedDays);
        Assert.Equal(1, page.Total);
        // 既有只读 / 边界 / 免责文案保持不变。
        Assert.False(string.IsNullOrWhiteSpace(page.ReadOnlyText));
        Assert.False(string.IsNullOrWhiteSpace(page.BoundaryText));
        Assert.False(string.IsNullOrWhiteSpace(page.DisclaimerText));
        Assert.Contains(nameof(IErpDbContext.Inquiries), counting.ReadProperties);
        Assert.Equal(0, counting.WriteCalls);
    }

    [Fact]
    public async Task Authorization_is_independent_of_http_context_and_request_path()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户甲");
        SeedInquiry(db, "INQ-1", customer.Id);
        var userId = SeedAuthorizedIdentity(db);

        // 1) 完全未绑定 HttpContext 的纯进程内直调：同样执行实时授权（无法解析身份 → 未认证），绝无豁免。
        var bare = CountingDbContext.Wrap(db);
        var bareController = new InquiryResponseWorkspaceController(bare.Proxy);
        await AssertCode(ErrorCodes.Unauthorized,
            () => bareController.GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        AssertZeroInquiryReads(bare);

        // 2) 空路径 / 已赋值路径 + 已授权身份：口径完全一致（放行既有读契约）。
        foreach (var path in new[] { string.Empty, WorkspacePath, "/api/other/path" })
        {
            var page = GetData(await NewController(db, userId.ToString(), path)
                .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
            Assert.Single(page.Items);
        }

        // 3) 空路径 / 已赋值路径 + 无身份：一律未认证（绝无匿名兜底）。
        foreach (var path in new[] { string.Empty, WorkspacePath, "/api/other/path" })
        {
            await AssertCode(ErrorCodes.Unauthorized,
                () => NewController(db, rawIdentity: null, requestPath: path)
                    .GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        }
    }

    [Fact]
    public void Controller_enforces_live_authorization_and_reuses_the_existing_inquiry_menu()
    {
        // 复用既有「询价单」菜单常量（与 SeedData.Menus 同源），不新增任何菜单 / 角色 / 用户授权。
        Assert.Equal("inquiry", InquiryAuthorizationRules.RequiredMenuCode);
        Assert.Equal("询价单", InquiryAuthorizationRules.RequiredMenuText);

        var controllerSource = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "InquiryResponseWorkspace.cs"));

        Assert.Contains("InquiryAuthorizationRules.EnsureAuthorizedAsync", controllerSource);
        Assert.Contains("InquiryAuthorizationRules.ApplyScope", controllerSource);
        Assert.Contains("ClaimTypes.NameIdentifier", controllerSource);
        Assert.DoesNotContain("AllowAnonymous", controllerSource);
        Assert.DoesNotContain("[Authorize(Roles", controllerSource);
        Assert.DoesNotContain("FromSql", controllerSource);
        Assert.DoesNotContain("SalespersonDataScopeService.ResolveAsync", controllerSource);

        // 可执行代码（去掉注释行）：只读、不写库，且绝不引用请求路径 / 环境作为放行条件。
        var code = string.Join("\n", controllerSource.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("SaveChanges", code);
        Assert.DoesNotContain("Request.Path", code);
        Assert.DoesNotContain("IsDevelopment", code);
        Assert.DoesNotContain("Environment.", code);

        // 授权必须先于任何 Inquiries 计数 / 行读取。
        var authorizeIndex = code.IndexOf("EnsureAuthorizedAsync", StringComparison.Ordinal);
        var inquiryReadIndex = code.IndexOf("_db.Inquiries", StringComparison.Ordinal);
        Assert.True(authorizeIndex >= 0 && inquiryReadIndex > authorizeIndex,
            "分页入口必须先执行实时授权，再读取任何 Inquiries 计数 / 行");

        // 路由模板保持既有契约不变。
        Assert.Equal("api/inquiries/response-times", typeof(InquiryResponseWorkspaceController)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template);

        // 授权规则复用既有 inquiry 菜单常量，且没有匿名回退。
        var rulesSource = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "InquiryAuthorizationRules.cs"));
        Assert.Contains("RequiredMenuCode = \"inquiry\"", rulesSource);
        Assert.DoesNotContain("AllowAnonymous", rulesSource);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「拒绝路径零 Inquiries 读取」与「只读不写库」；不改动生产代码。
    /// </summary>
    private class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>被访问的数据集属性名</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读入口恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(
                    args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
                ReadProperties.Add(targetMethod.Name[4..]);

            return targetMethod.Invoke(_inner, args);
        }
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
