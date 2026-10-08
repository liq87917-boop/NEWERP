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
/// 询价单生命周期、「询价单 → 报价单」转换与批量删除的确定性来源行锁 / 原子事务 / 锁内权威复核护栏单元测试
/// （ERP-402，内存库）。
/// <list type="bullet">
/// <item>锁与事务契约：询价单来源行锁、与报价单锁的确定顺序、Id 升序确定性加锁、非关系型等价无操作；</item>
/// <item>真实既有授权（身份 / inquiry 菜单 / 客户范围）在列表 / 详情 / 导出 / 新增 / 修改 / 生命周期 / 批量删除 /
/// 预填 / 转报价单之前的 fail closed（转换另需 quotation 菜单）；</item>
/// <item>生命周期守卫保留既有口径；已转换来源（存在实时报价单下游）一律冻结，无反向自动冲销；</item>
/// <item>转换资格（重复生成 / 未审核 / 无有效明细）与「同一询价单至多一张报价单」唯一化；</item>
/// <item>预填保持只读（不落库、不占号、不改写来源状态）。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与真实既有身份（<see cref="TestAuth"/>），
/// 不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收；跨连接竞态由
/// <c>InquiryMutationSqlServerTests</c> 覆盖。</para>
/// </summary>
public class InquiryMutationTests
{
    // ==================== 脚手架 ====================

    /// <summary>特权身份控制器（既有系统内置角色口径，豁免菜单授权：用于非授权场景的行为断言）。</summary>
    private static InquiryController NewController(ErpDbContext db)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static InquiryController NewController(ErpDbContext db, long? userId)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>
    /// 播种一位**受限制**操作员（非系统内置角色 → 非特权）：员工编码 = 登录名、客户归属该员工、
    /// 并按需授予既有菜单授权（inquiry / quotation）。
    /// </summary>
    private static (SysUser User, BaseEmployee Employee, BaseCustomer Customer) SeedRestrictedOperator(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"inq-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{code}", CustomerName = "本人客户", Status = 1, CreditStatus = "正常",
            EmpId = employee.Id
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = code, RoleCode = $"R-{code}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return (user, employee, customer);
    }

    private static void GrantMenus(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    /// <summary>播种一位有既有菜单授权但**未映射业务员**的受限制账号（fail closed 场景）。</summary>
    private static SysUser SeedUnmappedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"inq-nomap-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = code, RoleCode = $"R-{code}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return user;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long? empId = null, string? code = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code ?? $"C-{Guid.NewGuid():N}", CustomerName = "客户", Status = 1,
            CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>
    /// 播种一条既有单据号规则（用于观察「被拒绝的调用方绝不消耗单据号」：失败后 CurrentSequence 必须保持不变）。
    /// </summary>
    private static SysDocumentNumberRule SeedNumberRule(ErpDbContext db, DocumentType type, string prefix)
    {
        var rule = new SysDocumentNumberRule
        {
            DocumentType = type, RuleCode = prefix, RuleName = prefix, Prefix = prefix,
            DateFormat = "yyyyMMdd", SerialLength = 4, CurrentSequence = 0, YearlyReset = true
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private static Inquiry SeedInquiry(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Pending, bool withDetail = true)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no, InquiryDate = DateTime.Today, CustomerId = customerId,
            ContactPerson = "C", ContactPhone = "138", Currency = Currency.USD,
            ExchangeRate = 7.1m, ValidDays = 30, Status = status
        };
        if (withDetail)
        {
            inquiry.Details.Add(new InquiryDetail
            {
                ProductId = 1, ProductName = "P1", Spec = "A", Unit = "PCS",
                Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
        }
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    /// <summary>既有「询价单」菜单编码（与 <see cref="InquiryAuthorizationRules"/> 同源）。</summary>
    private const string InquiryMenu = InquiryAuthorizationRules.RequiredMenuCode;

    /// <summary>既有「报价单」菜单编码（与 <see cref="QuotationAuthorizationRules"/> 同源）。</summary>
    private const string QuotationMenu = QuotationAuthorizationRules.RequiredMenuCode;

    // ==================== 1. 锁 / 事务契约 ====================

    [Fact]
    public void 锁契约_询价单行锁与报价单行锁同源且确定锁序()
    {
        Assert.Contains("db_owner.Inquiries", InquiryMutationRules.InquiryRowLockSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", InquiryMutationRules.InquiryRowLockSql);

        // 跨单据锁序的第一把锁为「询价单行锁」，第二把为 ERP-400 同一把「报价单行锁」。
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql, InquiryMutationRules.QuotationRowLockSql);
        Assert.Equal(InquiryMutationRules.InquiryRowLockSql, QuotationMutationRules.InquiryRowLockSql);
        Assert.Equal(QuotationMutationRules.RowLockRetryAttempts, InquiryMutationRules.RowLockRetryAttempts);

        Assert.Contains("询价单行锁", InquiryMutationRules.LockOrderText);
        Assert.Contains("报价单行锁", InquiryMutationRules.LockOrderText);
        Assert.Contains("询价单行锁", QuotationMutationRules.InquiryToQuotationLockOrderText);
        Assert.Contains("报价单行锁", QuotationMutationRules.InquiryToQuotationLockOrderText);
    }

    [Fact]
    public void MergeLockIds_去重升序且仅正整数()
    {
        var ids = InquiryMutationRules.MergeLockIds(new long[] { 5, -1, 3, 0, 5, 9, 3 });
        Assert.Equal(new long[] { 3, 5, 9 }, ids);
        Assert.Empty(InquiryMutationRules.MergeLockIds(null));
    }

    [Fact]
    public async Task 非关系型提供程序_行锁与事务等价无操作()
    {
        using var db = TestDbFactory.Create();
        var inquiry = SeedInquiry(db, "INQ-LOCK", 1L);

        Assert.False(InquiryMutationRules.IsRelationalProvider(db));
        Assert.Null(await InquiryMutationRules.BeginMutationTransactionAsync(db));
        Assert.True(await InquiryMutationRules.LockInquiryRowAsync(db, inquiry.Id));
        Assert.False(await InquiryMutationRules.LockInquiryRowAsync(db, 0));
    }

    // ==================== 2. 实时授权（fail closed） ====================

    [Fact]
    public async Task 无身份_未认证且零写入()
    {
        using var db = TestDbFactory.Create();
        var inquiry = SeedInquiry(db, "INQ-NOAUTH", 1L);
        var controller = NewController(db, null);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.GetPaged(new PageQuery(), null));
        Assert.Equal(ErrorCodes.Unauthorized, error.Code);
        await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(inquiry.Id));

        Assert.Equal(DocumentStatus.Pending, db.Inquiries.AsNoTracking().Single().Status);
        Assert.Empty(db.Quotations);
    }

    [Fact]
    public async Task 缺询价单菜单的受限账号_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (user, _, customer) = SeedRestrictedOperator(db);
        var inquiry = SeedInquiry(db, "INQ-NOMENU", customer.Id);
        var controller = NewController(db, user.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.GetPaged(new PageQuery(), null));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.BatchDelete(new List<long> { inquiry.Id }));

        Assert.False(db.Inquiries.AsNoTracking().Single().IsDeleted);
    }

    [Fact]
    public async Task 停用账号_即使菜单齐全也一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, customer) = SeedRestrictedOperator(db, InquiryMenu, QuotationMenu);
        var stored = db.SysUsers.Single(u => u.Id == user.Id);
        stored.Status = UserStatus.Disabled;
        db.SaveChanges();
        var inquiry = SeedInquiry(db, "INQ-DISABLED", customer.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user.Id).Submit(inquiry.Id));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Equal(DocumentStatus.Pending, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 未映射业务员的受限账号_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUnmappedOperator(db, InquiryMenu);
        var inquiry = SeedInquiry(db, "INQ-NOMAP", 1L);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user.Id).GetById(inquiry.Id));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
    }

    [Fact]
    public async Task 转换需既有报价单菜单_仅询价单菜单拒绝且不占号()
    {
        using var db = TestDbFactory.Create();
        var (user, _, customer) = SeedRestrictedOperator(db, InquiryMenu);
        var inquiry = SeedInquiry(db, "INQ-QTMENU", customer.Id, DocumentStatus.Approved);
        var rule = SeedNumberRule(db, DocumentType.Quotation, "QT");

        var controller = NewController(db, user.Id);
        await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.QuotationPrefill(inquiry.Id));

        Assert.Empty(db.Quotations);
        Assert.Equal(0, db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == rule.Id).CurrentSequence);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转换_双菜单权限时本人客户放行且落库报价单与来源一致()
    {
        using var db = TestDbFactory.Create();
        var (user, _, customer) = SeedRestrictedOperator(db, InquiryMenu, QuotationMenu);
        var inquiry = SeedInquiry(db, "INQ-OK", customer.Id, DocumentStatus.Approved);

        var result = await NewController(db, user.Id).ToQuotation(inquiry.Id);
        Assert.IsType<OkObjectResult>(result);

        var quotation = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(inquiry.Id, quotation.InquiryId);
        Assert.Equal(inquiry.InquiryNo, quotation.InquiryNo);
        Assert.Equal(customer.Id, quotation.CustomerId);
        Assert.Equal(1000m, quotation.TotalAmount);
        Assert.Equal("PCS", quotation.Details.Single().Unit);
        Assert.Equal(10m, quotation.Details.Single().Quantity);
        Assert.Equal(DocumentStatus.Completed, db.Inquiries.AsNoTracking().Single().Status);
    }

    // ==================== 3. 客户数据范围（计数 / 分页 / 导出下推） ====================

    [Fact]
    public async Task 受限账号_列表与详情与导出只返回本人客户()
    {
        using var db = TestDbFactory.Create();
        var (user, _, own) = SeedRestrictedOperator(db, InquiryMenu);
        var foreign = SeedCustomer(db, SeedRestrictedOperator(db, InquiryMenu).Employee.Id);
        var ownInquiry = SeedInquiry(db, "INQ-OWN", own.Id);
        var foreignInquiry = SeedInquiry(db, "INQ-FOREIGN", foreign.Id);
        var controller = NewController(db, user.Id);

        var paged = GetData<PagedResult<Inquiry>>(
            await controller.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        Assert.Equal(1, paged.Total);
        Assert.Equal(ownInquiry.Id, paged.Items.Single().Id);

        var exported = GetData<List<Inquiry>>(await controller.Export(null, null));
        Assert.Equal(ownInquiry.Id, exported.Single().Id);

        Assert.IsType<OkObjectResult>(await controller.GetById(ownInquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.GetById(foreignInquiry.Id));
    }

    [Fact]
    public async Task 受限账号_新增越界客户拒绝且不消耗单据号()
    {
        using var db = TestDbFactory.Create();
        var (user, _, own) = SeedRestrictedOperator(db, InquiryMenu);
        var foreign = SeedCustomer(db, SeedRestrictedOperator(db, InquiryMenu).Employee.Id);
        var rule = SeedNumberRule(db, DocumentType.Inquiry, "INQ");

        var allowed = await NewController(db, user.Id).Create(new Inquiry
        {
            CustomerId = own.Id,
            Details = new List<InquiryDetail> { new() { ProductId = 1, Quantity = 2m, UnitPrice = 5m } }
        });
        Assert.IsType<OkObjectResult>(allowed);
        Assert.Single(db.Inquiries);
        var consumed = db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == rule.Id).CurrentSequence;

        var denied = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, user.Id)
            .Create(new Inquiry { CustomerId = foreign.Id }));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);
        Assert.Single(db.Inquiries);
        Assert.Equal(consumed, db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == rule.Id).CurrentSequence);
    }

    [Fact]
    public async Task 受限账号_修改同时复核已存与拟议客户()
    {
        using var db = TestDbFactory.Create();
        var (user, _, own) = SeedRestrictedOperator(db, InquiryMenu);
        var foreign = SeedCustomer(db, SeedRestrictedOperator(db, InquiryMenu).Employee.Id);
        var ownInquiry = SeedInquiry(db, "INQ-EDIT-OWN", own.Id);
        var foreignInquiry = SeedInquiry(db, "INQ-EDIT-FR", foreign.Id);
        var controller = NewController(db, user.Id);

        // 拟议客户越界：拒绝且零改写。
        var proposedDenied = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(ownInquiry.Id,
            new Inquiry { CustomerId = foreign.Id }));
        Assert.Equal(ErrorCodes.Forbidden, proposedDenied.Code);
        Assert.Equal(own.Id, db.Inquiries.AsNoTracking().Single(i => i.Id == ownInquiry.Id).CustomerId);

        // 已存归属越界：拒绝（fail closed，不泄露范围外询价单）。
        var storedDenied = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(foreignInquiry.Id,
            new Inquiry { CustomerId = own.Id }));
        Assert.Equal(ErrorCodes.Forbidden, storedDenied.Code);
        Assert.Equal(foreign.Id, db.Inquiries.AsNoTracking().Single(i => i.Id == foreignInquiry.Id).CustomerId);
    }

    [Fact]
    public async Task 受限账号_批量删除越界批次整体拒绝不做部分删除()
    {
        using var db = TestDbFactory.Create();
        var (user, _, own) = SeedRestrictedOperator(db, InquiryMenu);
        var foreign = SeedCustomer(db, SeedRestrictedOperator(db, InquiryMenu).Employee.Id);
        var ownInquiry = SeedInquiry(db, "INQ-BD-OWN", own.Id);
        var foreignInquiry = SeedInquiry(db, "INQ-BD-FR", foreign.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, user.Id)
            .BatchDelete(new List<long> { ownInquiry.Id, foreignInquiry.Id }));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);

        Assert.False(db.Inquiries.AsNoTracking().Single(i => i.Id == ownInquiry.Id).IsDeleted);
        Assert.False(db.Inquiries.AsNoTracking().Single(i => i.Id == foreignInquiry.Id).IsDeleted);
    }

    // ==================== 4. 生命周期守卫（保留既有口径） ====================

    [Fact]
    public async Task 生命周期_提交审核与取消既有口径不变()
    {
        using var db = TestDbFactory.Create();
        var inquiry = SeedInquiry(db, "INQ-LIFE", 1L);
        var controller = NewController(db);

        await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(inquiry.Id));   // 草稿不可直接审核
        Assert.IsType<OkObjectResult>(await controller.Submit(inquiry.Id));
        Assert.Equal(DocumentStatus.Submitted, Status(db, inquiry.Id));
        Assert.IsType<OkObjectResult>(await controller.Approve(inquiry.Id));
        Assert.Equal(DocumentStatus.Approved, Status(db, inquiry.Id));

        // 无实时下游时可取消（既有语义：不限状态）。
        Assert.IsType<OkObjectResult>(await controller.Cancel(inquiry.Id));
        Assert.Equal(DocumentStatus.Cancelled, Status(db, inquiry.Id));
    }

    [Fact]
    public async Task 删除_仅待提交可删()
    {
        using var db = TestDbFactory.Create();
        var pending = SeedInquiry(db, "INQ-DEL-OK", 1L);
        var approved = SeedInquiry(db, "INQ-DEL-NO", 2L, DocumentStatus.Approved);
        var controller = NewController(db);

        await Assert.ThrowsAsync<BusinessException>(() => controller.Delete(approved.Id));
        Assert.False(db.Inquiries.AsNoTracking().Single(i => i.Id == approved.Id).IsDeleted);

        Assert.IsType<OkObjectResult>(await controller.Delete(pending.Id));
        Assert.True(db.Inquiries.AsNoTracking().Single(i => i.Id == pending.Id).IsDeleted);
    }

    [Fact]
    public async Task 批量删除_混合状态整体拒绝且不做部分删除()
    {
        using var db = TestDbFactory.Create();
        var pending = SeedInquiry(db, "INQ-BD-P", 1L);
        var approved = SeedInquiry(db, "INQ-BD-A", 2L, DocumentStatus.Approved);

        var error = await Assert.ThrowsAsync<BusinessException>(() => NewController(db)
            .BatchDelete(new List<long> { pending.Id, approved.Id }));
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);

        Assert.False(db.Inquiries.AsNoTracking().Single(i => i.Id == pending.Id).IsDeleted);
        Assert.False(db.Inquiries.AsNoTracking().Single(i => i.Id == approved.Id).IsDeleted);
    }

    [Fact]
    public async Task 批量删除_归一化重复与非法Id后整体软删除()
    {
        using var db = TestDbFactory.Create();
        var a = SeedInquiry(db, "INQ-BD-1", 1L);
        var b = SeedInquiry(db, "INQ-BD-2", 2L);

        Assert.IsType<OkObjectResult>(await NewController(db)
            .BatchDelete(new List<long> { b.Id, a.Id, a.Id, -5, 0 }));
        Assert.True(db.Inquiries.AsNoTracking().Single(i => i.Id == a.Id).IsDeleted);
        Assert.True(db.Inquiries.AsNoTracking().Single(i => i.Id == b.Id).IsDeleted);

        var empty = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db).BatchDelete(new List<long>()));
        Assert.Equal(ErrorCodes.InvalidParameter, empty.Code);
    }

    // ==================== 5. 转换资格与下游冻结 ====================

    [Fact]
    public async Task 转换_未审核或无明细一律拒绝且不占号()
    {
        using var db = TestDbFactory.Create();
        var pending = SeedInquiry(db, "INQ-CVT-P", 1L);
        var noDetail = SeedInquiry(db, "INQ-CVT-D", 2L, DocumentStatus.Approved, withDetail: false);
        var rule = SeedNumberRule(db, DocumentType.Quotation, "QT");
        var controller = NewController(db);

        var notApproved = await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(pending.Id));
        Assert.Contains("已审核", notApproved.Message);
        var noDetails = await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(noDetail.Id));
        Assert.Contains("有效明细", noDetails.Message);

        Assert.Empty(db.Quotations);
        Assert.Equal(0, db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == rule.Id).CurrentSequence);
    }

    [Fact]
    public async Task 转换_同一询价单只生成一张报价单()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-CVT-ONE", customer.Id, DocumentStatus.Approved);
        var controller = NewController(db);

        Assert.IsType<OkObjectResult>(await controller.ToQuotation(inquiry.Id));
        var duplicate = await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(inquiry.Id));
        Assert.Contains("不能重复转换", duplicate.Message);

        Assert.Single(db.Quotations);
        Assert.Single(db.Quotations.Include(q => q.Details).Single().Details);
    }

    [Fact]
    public async Task 已转换来源_取消修改删除与重新转换一律拒绝且来源保持完成()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-FROZEN", customer.Id, DocumentStatus.Approved);
        var controller = NewController(db);
        await controller.ToQuotation(inquiry.Id);

        await Assert.ThrowsAsync<BusinessException>(() => controller.Cancel(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Delete(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Update(inquiry.Id,
            new Inquiry { CustomerId = inquiry.CustomerId }));
        await Assert.ThrowsAsync<BusinessException>(() => controller.BatchDelete(new List<long> { inquiry.Id }));

        var stored = db.Inquiries.AsNoTracking().Single();
        Assert.Equal(DocumentStatus.Completed, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Single(db.Quotations.Where(q => !q.IsDeleted));
    }

    [Fact]
    public async Task 预填_只读_不落库不占号不改写来源状态()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var inquiry = SeedInquiry(db, "INQ-PREFILL", customer.Id, DocumentStatus.Approved);
        var controller = NewController(db);

        Assert.IsType<OkObjectResult>(await controller.QuotationPrefill(inquiry.Id));

        Assert.Empty(db.Quotations);
        Assert.Empty(db.SysDocumentNumberRules);
        Assert.Equal(DocumentStatus.Approved, Status(db, inquiry.Id));
    }

    // ==================== 6. 接口接线与规则文案契约 ====================

    [Fact]
    public void 控制器接线_全部路由经配额授权与锁协议()
    {
        var source = ReadSource("src", "ERP.Api", "Controllers", "InquiryController.cs");
        Assert.Contains("InquiryAuthorizationRules.EnsureAuthorizedAsync", source);
        Assert.Contains("EnsureQuotationConversionAuthorizedAsync", source);
        Assert.Contains("InquiryAuthorizationRules.ApplyScope", source);
        Assert.Contains("InquiryAuthorizationRules.EnsureProposedCustomerInScope", source);
        Assert.Contains("InquiryAuthorizationRules.EnsureStoredCustomerInScope", source);
        Assert.Contains("InquiryMutationRules.BeginMutationTransactionAsync", source);
        Assert.Contains("InquiryMutationRules.LockInquiryRowAsync", source);
        Assert.Contains("InquiryMutationRules.LockInquiryRowsAsync", source);
        Assert.Contains("InquiryMutationRules.MergeLockIds", source);
        Assert.Contains("InquiryMutationRules.EnsureBatchDeleteAllowed", source);
        Assert.Contains("InquiryMutationRules.EnsureNoLiveDownstream", source);
        Assert.Contains("InquiryMutationRules.EnsureQuotationConversionEligible", source);
        Assert.Contains("InquiryMutationRules.DiscardTrackedChanges", source);
        Assert.Contains("InquiryQuotationConversion.BuildDraftAsync(Db, inquiry)", source);

        var conversion = ReadSource("src", "ERP.Api", "Controllers", "InquiryQuotationConversion.cs");
        Assert.Contains("InquiryMutationRules.EnsureQuotationConversionEligible", conversion);
        Assert.Contains("BuildDraftAsync(IErpDbContext db, Inquiry inquiry)", conversion);
    }

    [Fact]
    public void 规则与边界文案_不新增权限且不泄露范围外数据()
    {
        Assert.Equal("inquiry", InquiryAuthorizationRules.RequiredMenuCode);
        Assert.Equal(QuotationAuthorizationRules.RequiredMenuCode, InquiryAuthorizationRules.QuotationMenuCode);
        Assert.Contains("fail closed", InquiryAuthorizationRules.RuleText);
        Assert.Contains("绝不降级为全局 / 管理员可见", InquiryAuthorizationRules.RuleText);
        Assert.Contains("不新增任何表 / 列 / 菜单 / 角色 / 用户授权", InquiryAuthorizationRules.BoundaryText);
        Assert.Contains("保留库存来源单据审计", InquiryAuthorizationRules.BoundaryText);
        Assert.Contains("反向自动冲销", InquiryMutationRules.DownstreamLinkedText);
        Assert.Contains("反向自动冲销", InquiryMutationRules.BoundaryText);
    }

    // ==================== 脚手架（响应读取 / 源文件读取） ====================

    private static T GetData<T>(IActionResult action)
    {
        var response = Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(action).Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static DocumentStatus Status(ErpDbContext db, long id)
        => db.Inquiries.AsNoTracking().Single(i => i.Id == id).Status;

    /// <summary>读取仓库内源文件（从测试输出目录向上定位解决方案根，与既有并发护栏测试同源）。</summary>
    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }
}
