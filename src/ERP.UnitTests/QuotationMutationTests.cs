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
/// 报价单生命周期、版本创建与「报价单 → PI / 销售订单」转换的确定性来源行锁 / 原子事务 / 锁内权威复核
/// 护栏单元测试（ERP-400，内存库）。
/// <list type="bullet">
/// <item>锁与事务契约：报价单来源行锁、与 PI / 销售订单锁的确定顺序、Id 升序确定性加锁、原子事务口径；</item>
/// <item>各生命周转变更的锁内守卫（修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除）；</item>
/// <item>已完成且存在下游 PI / 销售订单链接的报价单一律冻结（保留显式历史，绝不做反向冲销）；</item>
/// <item>真实既有授权（身份 / quotation 菜单 / 客户范围）在列表 / 详情 / 打印 / 新增 / 修改 / 转换之前的 fail closed；</item>
/// <item>转换资格（重复生成 / 未审核 / 已作废 / 无明细）与转换结果权威一致性（数量 / 币种 / 汇率 / 合计 / 定金 / SourceQuotationId）；</item>
/// <item>预填保持只读（不落库、不占号）。</item>
/// </list>
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与真实既有身份（<see cref="TestAuth"/>），
/// 不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收；跨连接竞态由
/// <c>QuotationMutationSqlServerTests</c> 覆盖。</para>
/// </summary>
public class QuotationMutationTests
{
    // ==================== 脚手架 ====================

    /// <summary>特权身份控制器（既有系统内置角色口径，豁免菜单授权：用于非授权场景的行为断言）。</summary>
    private static QuotationController NewController(ErpDbContext db)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static QuotationController NewController(ErpDbContext db, long? userId)
    {
        var controller = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>
    /// 播种一位**受限制**操作员（非系统内置角色 → 非特权）：员工编码 = 登录名、客户归属该员工、
    /// 并按需授予既有菜单授权（quotation / proforma-invoice / sales-order）。
    /// </summary>
    private static (SysUser User, BaseEmployee Employee, BaseCustomer Customer) SeedRestrictedOperator(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"qt-op-{Guid.NewGuid():N}";
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

        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return (user, employee, customer);
    }

    private static Quotation SeedQuotation(ErpDbContext db, string no, DocumentStatus status,
        long? customerId, params QuotationDetail[] details)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "义乌客户（护栏测试）",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            SalesmanId = 9L,
            SalesmanName = "业务员 A",
            Status = status
        };
        quotation.Details = details.Length > 0
            ? details.ToList()
            : new List<QuotationDetail>
            {
                new()
                {
                    ProductCode = "P001", ProductName = "饰品 A", Unit = "PCS",
                    SortNo = 1, Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            };
        quotation.TotalAmount = quotation.Details.Sum(d => d.Amount);
        quotation.TotalAmountCny = quotation.TotalAmount * quotation.ExchangeRate;
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    /// <summary>播种一张指向来源报价单的 PI（把报价单标记为「已完成」+ 有下游链接）。</summary>
    private static ProformaInvoice SeedLinkedPi(ErpDbContext db, Quotation quotation)
    {
        var pi = new ProformaInvoice
        {
            PiNo = $"PI-LINK-{quotation.Id}", PiDate = DateTime.Today,
            QuotationId = quotation.Id, QuotationNo = quotation.QuotationNo,
            CustomerId = quotation.CustomerId, CustomerName = quotation.CustomerName,
            Currency = quotation.Currency, ExchangeRate = quotation.ExchangeRate
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static SalesOrder SeedLinkedOrder(ErpDbContext db, Quotation quotation)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-LINK-{quotation.Id}", OrderDate = DateTime.Today,
            CustomerId = quotation.CustomerId ?? 0, Currency = quotation.Currency,
            ExchangeRate = quotation.ExchangeRate, TotalAmount = quotation.TotalAmount,
            DepositRatio = 30m, DepositAmount = quotation.TotalAmount * 0.3m,
            Status = DocumentStatus.Pending,
            SourceQuotationId = quotation.Id, SourceQuotationNo = quotation.QuotationNo
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static async Task<BusinessException> Denied(Func<Task<IActionResult>> action)
        => await Assert.ThrowsAsync<BusinessException>(action);

    // ==================== 1. 锁 / 事务契约 ====================

    [Fact]
    public void 锁语句与事务口径_与PI锁同源且跨单据锁序一致()
    {
        Assert.Contains("db_owner.Quotations", QuotationMutationRules.QuotationRowLockSql);
        Assert.Contains("UPDLOCK", QuotationMutationRules.QuotationRowLockSql);
        Assert.Contains("HOLDLOCK", QuotationMutationRules.QuotationRowLockSql);

        // 报价单行锁是链上的第一把锁：PI 侧引用同一常量，保证「报价单 → PI」与「报价单 → 销售订单」共用同一把锁。
        Assert.Equal(QuotationMutationRules.QuotationRowLockSql, ProformaInvoiceMutationRules.QuotationRowLockSql);
        Assert.Equal(ProformaInvoiceMutationRules.PiRowLockSql, QuotationMutationRules.ProformaInvoiceRowLockSql);
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, QuotationMutationRules.SalesOrderRowLockSql);

        Assert.Contains("报价单", QuotationMutationRules.LockOrderText);
        Assert.Contains("不存在锁环", QuotationMutationRules.LockOrderText);
        Assert.Contains("报价单", ProformaInvoiceMutationRules.CrossDocumentLockOrderText);
        Assert.Contains("报价单", QuotationMutationRules.BoundaryText);
    }

    [Fact]
    public void 合并加锁Id_去重升序_仅正整数()
    {
        var ids = QuotationMutationRules.MergeLockIds(new long[] { 3, -1, 0, 1, 3, 2 });
        Assert.Equal(new long[] { 1, 2, 3 }, ids);
        Assert.Empty(QuotationMutationRules.MergeLockIds(null));
    }

    [Fact]
    public async Task 非关系型提供程序_行锁与事务等价无操作()
    {
        using var db = TestDbFactory.Create();
        Assert.False(QuotationMutationRules.IsRelationalProvider(db));
        Assert.True(await QuotationMutationRules.LockQuotationRowAsync(db, 1));
        Assert.Null(await QuotationMutationRules.BeginMutationTransactionAsync(db));
    }

    // ==================== 2. 生命周期守卫（纯规则） ====================

    [Fact]
    public void 生命周期守卫_锁内资格逐项()
    {
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureNoDownstreamLink(true));
        QuotationMutationRules.EnsureNoDownstreamLink(false);
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureNotSuperseded(true));
        QuotationMutationRules.EnsureNotSuperseded(false);

        foreach (var editable in new[] { DocumentStatus.Pending, DocumentStatus.Submitted })
            QuotationMutationRules.EnsureEditAllowed(editable);
        foreach (var locked in new[] { DocumentStatus.Approved, DocumentStatus.Cancelled, DocumentStatus.Completed })
            Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureEditAllowed(locked));

        QuotationMutationRules.EnsureSubmitAllowed(DocumentStatus.Pending);
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureSubmitAllowed(DocumentStatus.Approved));

        QuotationMutationRules.EnsureApproveAllowed(DocumentStatus.Pending, 1);
        QuotationMutationRules.EnsureApproveAllowed(DocumentStatus.Submitted, 1);
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureApproveAllowed(DocumentStatus.Approved, 1));
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureApproveAllowed(DocumentStatus.Cancelled, 1));
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureApproveAllowed(DocumentStatus.Completed, 1));
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureApproveAllowed(DocumentStatus.Pending, 0));

        QuotationMutationRules.EnsureUnauditAllowed(DocumentStatus.Approved);
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureUnauditAllowed(DocumentStatus.Pending));

        QuotationMutationRules.EnsureCancelAllowed(DocumentStatus.Approved);
        QuotationMutationRules.EnsureVoidAllowed(DocumentStatus.Pending);
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureVoidAllowed(DocumentStatus.Cancelled));
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureVoidAllowed(DocumentStatus.Completed));

        QuotationMutationRules.EnsureDeleteAllowed(DocumentStatus.Pending);
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureDeleteAllowed(DocumentStatus.Submitted));
        QuotationMutationRules.EnsureBatchDeleteAllowed(new[] { DocumentStatus.Pending, DocumentStatus.Pending });
        Assert.Throws<BusinessException>(() => QuotationMutationRules.EnsureBatchDeleteAllowed(
            new[] { DocumentStatus.Pending, DocumentStatus.Approved }));

        QuotationMutationRules.EnsurePersistedVersionCurrent(null, null);
        QuotationMutationRules.EnsurePersistedVersionCurrent(new byte[] { 1 }, new byte[] { 1 });
        Assert.Throws<BusinessException>(() =>
            QuotationMutationRules.EnsurePersistedVersionCurrent(new byte[] { 1 }, new byte[] { 2 }));

        QuotationRevisionRules.EnsureSourceEligible(DocumentStatus.Approved);
        Assert.Throws<BusinessException>(() => QuotationRevisionRules.EnsureSourceEligible(DocumentStatus.Cancelled));
    }

    // ==================== 3. 真实既有授权（身份 / 菜单 / 客户范围） ====================

    [Fact]
    public async Task 无身份与缺菜单与禁用账号_一律fail_closed且不写库()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-AUTH", DocumentStatus.Approved, 1L);

        Assert.Equal(ErrorCodes.Unauthorized,
            (await Denied(() => NewController(db, null).GetById(quotation.Id))).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            (await Denied(() => NewController(db, null).ToSalesOrder(quotation.Id))).Code);

        var (user, _, _) = SeedRestrictedOperator(db);   // 未授予任何菜单授权
        Assert.Equal(ErrorCodes.Forbidden,
            (await Denied(() => NewController(db, user.Id).GetById(quotation.Id))).Code);

        user.Status = UserStatus.Disabled;
        db.SaveChanges();
        Assert.Equal(ErrorCodes.Forbidden,
            (await Denied(() => NewController(db, user.Id).GetById(quotation.Id))).Code);

        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.SalesOrders);
        Assert.Single(db.Quotations);
    }

    [Fact]
    public async Task 受限账号_仅本人客户可见_列表详情打印与版本皆fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (user, _, own) = SeedRestrictedOperator(db, QuotationAuthorizationRules.RequiredMenuCode);
        var foreign = new BaseCustomer
        {
            CustomerCode = "C-FR", CustomerName = "他人客户", Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(foreign);
        db.SaveChanges();

        var ownQuotation = SeedQuotation(db, "QT-OWN", DocumentStatus.Approved, own.Id);
        var foreignQuotation = SeedQuotation(db, "QT-FR", DocumentStatus.Approved, foreign.Id);
        var ctl = NewController(db, user.Id);

        // 列表：范围在计数之前下推，绝不泄露范围外报价单
        var paged = GetData<PagedResult<Quotation>>(await ctl.GetPaged(new PageQuery(), null, null, null));
        Assert.Equal(1, paged.Total);
        Assert.Equal(ownQuotation.Id, Assert.Single(paged.Items).Id);

        Assert.Equal(ownQuotation.Id, GetData<Quotation>(await ctl.GetById(ownQuotation.Id)).Id);
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetById(foreignQuotation.Id))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetPrint(foreignQuotation.Id))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetRevisions(foreignQuotation.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.CreateRevision(foreignQuotation.Id))).Code);

        // 拟议客户越界：写入之前 fail closed（不消耗单据号、不落库）
        var proposedForeign = new Quotation { CustomerId = foreign.Id, CustomerName = "越界客户" };
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Create(proposedForeign))).Code);
        Assert.Equal(2, db.Quotations.Count());          // 两张来源报价单一行未变
        Assert.Empty(db.SysDocumentNumberRules);          // 被拒绝的写入绝不消耗单据号
    }

    [Fact]
    public async Task 受限账号_转换另需目标菜单授权_来源可见不等于目标授权()
    {
        using var db = TestDbFactory.Create();
        // 只有报价单菜单，没有 PI / 销售订单菜单
        var (user, _, own) = SeedRestrictedOperator(db, QuotationAuthorizationRules.RequiredMenuCode);
        var quotation = SeedQuotation(db, "QT-CONV-MENU", DocumentStatus.Approved, own.Id);
        var ctl = NewController(db, user.Id);

        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.ToProformaInvoice(quotation.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.ToSalesOrder(quotation.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.OrderPrefill(quotation.Id))).Code);

        Assert.Empty(db.ProformaInvoices);
        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
    }

    // ==================== 4. 锁内生命周期（已完成 + 下游链接冻结） ====================

    [Fact]
    public async Task 已完成且存在下游链接_破坏性生命周期变更一律冻结()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-LINKED", DocumentStatus.Approved, 1L);
        SeedLinkedPi(db, quotation);
        var ctl = NewController(db);

        var update = new Quotation
        {
            QuotationDate = DateTime.Today, CustomerName = "改名客户", Currency = Currency.USD,
            ExchangeRate = 1m,
            Details = new List<QuotationDetail> { new() { ProductCode = "X", Quantity = 1m, UnitPrice = 1m } }
        };
        Assert.Contains("下游", (await Denied(() => ctl.Update(quotation.Id, update))).Message);
        Assert.Contains("下游", (await Denied(() => ctl.Submit(quotation.Id))).Message);
        Assert.Contains("下游", (await Denied(() => ctl.Approve(quotation.Id))).Message);
        Assert.Contains("下游", (await Denied(() => ctl.Unaudit(quotation.Id))).Message);
        Assert.Contains("下游", (await Denied(() => ctl.Cancel(quotation.Id))).Message);
        Assert.Contains("下游", (await Denied(() => ctl.Void(quotation.Id))).Message);
        Assert.Contains("下游", (await Denied(() => ctl.Delete(quotation.Id))).Message);

        // 来源报价单一行未变：状态 / 软删除 / 客户 / 明细 / 合计，且未做任何反向冲销。
        var after = db.Quotations.AsNoTracking().Include(q => q.Details).Single();
        Assert.Equal(DocumentStatus.Approved, after.Status);
        Assert.False(after.IsDeleted);
        Assert.Equal("义乌客户（护栏测试）", after.CustomerName);
        Assert.Equal(1000m, after.TotalAmount);
        Assert.Single(db.ProformaInvoices);
        Assert.False(db.ProformaInvoices.AsNoTracking().Single().IsDeleted);
    }

    [Fact]
    public async Task 审核_无有效明细_拒绝且不改状态()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-NODETAIL", DocumentStatus.Pending, 1L);
        db.QuotationDetails.RemoveRange(db.QuotationDetails);
        db.SaveChanges();

        Assert.Contains("明细", (await Denied(() => NewController(db).Approve(quotation.Id))).Message);
        Assert.Equal(DocumentStatus.Pending, db.Quotations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 审核后销审可继续修改_状态机既有口径不变()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-CYCLE", DocumentStatus.Pending, 1L);
        var ctl = NewController(db);

        Assert.IsType<OkObjectResult>(await ctl.Approve(quotation.Id));
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
        Assert.IsType<OkObjectResult>(await ctl.Unaudit(quotation.Id));
        Assert.Equal(DocumentStatus.Pending, db.Quotations.AsNoTracking().Single().Status);

        var update = new Quotation
        {
            QuotationDate = DateTime.Today, CustomerName = "改名客户", Currency = Currency.USD,
            ExchangeRate = 1m,
            Details = new List<QuotationDetail> { new() { ProductCode = "X", Quantity = 2m, UnitPrice = 3m } }
        };
        Assert.IsType<OkObjectResult>(await ctl.Update(quotation.Id, update));
        var saved = db.Quotations.AsNoTracking().Single();
        Assert.Equal("改名客户", saved.CustomerName);
        Assert.Equal(6m, saved.TotalAmount);
    }

    [Fact]
    public async Task 批量删除_越界与无主与已审核混合批次_整体拒绝不部分删除()
    {
        using var db = TestDbFactory.Create();
        var (user, _, own) = SeedRestrictedOperator(db, QuotationAuthorizationRules.RequiredMenuCode);
        var foreign = new BaseCustomer
        {
            CustomerCode = "C-FR2", CustomerName = "他人客户", Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(foreign);
        db.SaveChanges();

        var pending = SeedQuotation(db, "QT-BD-1", DocumentStatus.Pending, own.Id);
        var approved = SeedQuotation(db, "QT-BD-2", DocumentStatus.Approved, own.Id);
        var foreignQuotation = SeedQuotation(db, "QT-BD-3", DocumentStatus.Pending, foreign.Id);
        var ctl = NewController(db, user.Id);

        // 混合（越界 + 允许）→ 整体拒绝，未做部分删除
        Assert.Equal(ErrorCodes.Forbidden,
            (await Denied(() => ctl.BatchDelete(new List<long> { pending.Id, foreignQuotation.Id }))).Code);
        Assert.All(db.Quotations.AsNoTracking().ToList(), q => Assert.False(q.IsDeleted));

        // 无主缺失（不存在）→ 整体拒绝
        Assert.Equal(ErrorCodes.NotFound,
            (await Denied(() => ctl.BatchDelete(new List<long> { pending.Id, 999999 }))).Code);
        Assert.All(db.Quotations.AsNoTracking().ToList(), q => Assert.False(q.IsDeleted));

        // 合法客户但含非待提交行 → 整体拒绝
        Assert.Equal(ErrorCodes.RuleConflict,
            (await Denied(() => ctl.BatchDelete(new List<long> { pending.Id, approved.Id }))).Code);
        Assert.All(db.Quotations.AsNoTracking().ToList(), q => Assert.False(q.IsDeleted));

        // 全部合法 → 一次性软删除
        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { pending.Id }));
        Assert.True(db.Quotations.AsNoTracking().Single(q => q.Id == pending.Id).IsDeleted);
        Assert.False(db.Quotations.AsNoTracking().Single(q => q.Id == approved.Id).IsDeleted);
    }

    // ==================== 5. 转换（重复生成唯一化 / 权威一致性 / 预填只读） ====================

    [Fact]
    public async Task 转PI_未审核与已作废_拒绝_重复生成只一张完整PI()
    {
        using var db = TestDbFactory.Create();
        var draft = SeedQuotation(db, "QT-PI-DRAFT", DocumentStatus.Pending, 1L);
        var cancelled = SeedQuotation(db, "QT-PI-CANCEL", DocumentStatus.Cancelled, 1L);
        var ctl = NewController(db);

        Assert.Contains("未审核", (await Denied(() => ctl.ToProformaInvoice(draft.Id))).Message);
        Assert.Contains("已作废", (await Denied(() => ctl.ToProformaInvoice(cancelled.Id))).Message);
        Assert.Empty(db.ProformaInvoices);

        var ready = SeedQuotation(db, "QT-PI-READY", DocumentStatus.Approved, 1L);
        Assert.IsType<OkObjectResult>(await ctl.ToProformaInvoice(ready.Id));
        Assert.Single(db.ProformaInvoices);
        Assert.Single(db.ProformaInvoiceDetails);
        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single(q => q.Id == ready.Id).Status);

        Assert.Contains("不能重复转换", (await Denied(() => ctl.ToProformaInvoice(ready.Id))).Message);
        Assert.Single(db.ProformaInvoices);
        Assert.Single(db.ProformaInvoiceDetails);
    }

    [Fact]
    public async Task 转销售订单_重复生成只一张_并留痕来源与权威口径()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-SO-1", DocumentStatus.Approved, 1L);
        var ctl = NewController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(quotation.Id));
        var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value).Data!;
        Assert.StartsWith("SO", result.OrderNo);

        var order = db.SalesOrders.AsNoTracking().Include(o => o.Details).Single();
        Assert.Equal(quotation.Id, order.SourceQuotationId);
        Assert.Equal(quotation.QuotationNo, order.SourceQuotationNo);
        Assert.Equal(quotation.Currency, order.Currency);
        Assert.Equal(quotation.ExchangeRate, order.ExchangeRate);
        Assert.Equal(quotation.TotalAmount, order.TotalAmount);
        Assert.Equal(quotation.Details.Sum(d => d.Quantity), order.Details.Sum(d => d.Quantity));
        Assert.Equal(DocumentStatus.Completed, db.Quotations.AsNoTracking().Single().Status);

        Assert.Contains("不能重复生成", (await Denied(() => ctl.ToSalesOrder(quotation.Id))).Message);
        Assert.Single(db.SalesOrders);
        Assert.Single(db.SalesOrderDetails);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.FinanceReceipts);
    }

    [Fact]
    public async Task 带入预填保持只读_不落库不占号不改来源状态()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-PREFILL-G", DocumentStatus.Approved, 1L);

        var ok = Assert.IsType<OkObjectResult>(await NewController(db).OrderPrefill(quotation.Id));
        var prefill = Assert.IsType<ApiResponse<SalesOrderPrefillResult>>(ok.Value).Data!;
        Assert.Equal(string.Empty, prefill.Order.OrderNo);
        Assert.Equal(quotation.Id, prefill.Order.SourceQuotationId);
        Assert.Equal(quotation.QuotationNo, prefill.Order.SourceQuotationNo);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SysDocumentNumberRules);
        Assert.Equal(DocumentStatus.Approved, db.Quotations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转换结果与来源权威不一致_原子拒绝()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-MISMATCH", DocumentStatus.Approved, 1L);

        var draft = await SalesOrderConversion.FromQuotationAsync(db, quotation);
        SalesOrderConversion.EnsureQuotationDraftMatchesSource(quotation, draft);

        var tamperedSource = await SalesOrderConversion.FromQuotationAsync(db, quotation);
        tamperedSource.SourceQuotationId = null;
        Assert.Equal(SalesOrderConversion.QuotationDraftMismatchText,
            Assert.Throws<BusinessException>(() =>
                SalesOrderConversion.EnsureQuotationDraftMatchesSource(quotation, tamperedSource)).Message);

        var tamperedQuantity = await SalesOrderConversion.FromQuotationAsync(db, quotation);
        tamperedQuantity.Details[0].Quantity += 1m;
        Assert.Throws<BusinessException>(() =>
            SalesOrderConversion.EnsureQuotationDraftMatchesSource(quotation, tamperedQuantity));
    }

    [Fact]
    public void 控制器接线_生命周期与转换都在报价单来源行锁与原子事务内()
    {
        var source = ReadSource("src", "ERP.Api", "Controllers", "QuotationController.cs");

        Assert.Contains("QuotationMutationRules.BeginMutationTransactionAsync(Db)", source);
        Assert.Contains("QuotationMutationRules.LockQuotationRowAsync", source);
        Assert.Contains("QuotationMutationRules.LockQuotationRowsAsync", source);
        Assert.Contains("QuotationMutationRules.EnsureBatchDeleteAllowed", source);
        Assert.Contains("QuotationMutationRules.EnsureNoDownstreamLink", source);
        Assert.Contains("QuotationMutationRules.EnsureProformaInvoiceConversionEligible", source);
        Assert.Contains("QuotationMutationRules.EnsureSalesOrderConversionEligible", source);
        Assert.Contains("SalesOrderConversion.EnsureQuotationDraftMatchesSource(quotation, order)", source);
        Assert.Contains("QuotationAuthorizationRules.EnsureAuthorizedAsync", source);
        Assert.Contains("QuotationAuthorizationRules", source);
        Assert.Contains("EnsureProformaInvoiceConversionAuthorizedAsync", source);
        Assert.Contains("EnsureSalesOrderConversionAuthorizedAsync", source);
        Assert.Contains("QuotationMutationRules.DiscardTrackedChanges(Db)", source);
    }

    // ==================== 脚手架（响应读取 / 源文件读取） ====================

    private static T GetData<T>(IActionResult action)
    {
        var response = Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(action).Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

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