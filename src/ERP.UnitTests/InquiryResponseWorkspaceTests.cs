using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-104 询价报价响应时间工作台（只读派生）单元测试。
/// 覆盖：稳定询价单 Id 分页与日期 / 客户 / 关键字筛选、只按持久化 InquiryId 显式链接（不按单号 / 文本推断）、
/// 版本链根单口径（版本不重复计入）、缺失链接 / 缺失日期 / 负间隔（链接异常）分别标注、业务员数据范围、软删除行排除、
/// 分页有界与只读不写库、纯规则文案，以及接口与前端接线契约。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed，不做浏览器验收。</para>
/// </summary>
public class InquiryResponseWorkspaceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly DateTime InquiryDay = new(2026, 9, 10);

    private static InquiryResponseWorkspaceController BuildController(ErpDbContext db) => new(db);

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        SeedUserRole(db, user.Id, SeedRole(db, "Sales").Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", alice.Id);
        var others = SeedCustomer(db, "C002", "别人的客户", alice.Id + 1000);
        SeedInquiry(db, "INQ-MINE", mine.Id);
        SeedInquiry(db, "INQ-OTHER", others.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, user.Id);

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));

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
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
