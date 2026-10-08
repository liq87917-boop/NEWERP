using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-383 客户销项发票并发与授权护栏单元测试（内存库 + 真实 HTTP 身份）。
/// <list type="number">
/// <item><b>实时授权</b>：缺失身份 / 禁用账号 / 无销售订单模块授权 / 撤销授权 / 无菜单授权在核心路由
/// （台账 / 详情 / 收款时效证据 / 商业发票候选 / 可分摊订单候选 / 分摊预览 / 新增 / 修改 / 分摊整体替换 / 登记 / 作废）
/// 上 fail closed 且零副作用；受限业务员只能读取 / 改写「已存储客户 + 全部来源订单 + 交叉引用单证」都在本人客户范围内的
/// 发票（混源、无来源、来源不可证明一律拒绝）；特权账号保留历史发票的可读 / 可操作能力；绝不新增任何用户授权。</item>
/// <item><b>容量护栏</b>：并发多张发票不得合计超过同一销售订单总额（订单发票容量）；并发不同收款单不得合计超过同一
/// 发票含税总额（发票同币种收款容量）；ERP-350 共用收款额度与 ERP-073 证据维度分离保持不变。</item>
/// <item><b>原子拒绝与证据保留</b>：作废保留作废原因与冻结审计；被拒绝 / 越权的请求不改写任何发票 / 分摊行 /
/// 销售订单 / 单证 / 收款单 / 收款分摊证据。</item>
/// </list>
/// <para>内存库无行锁语义（<see cref="CustomerSalesInvoiceConcurrencyRules.IsRelationalProvider"/> 为 false 时锁定与事务等价无操作），
/// 因此本文件的「并发」以确定性容量口径 + 原子拒绝验证；真实 SQL 两个独立连接竞态见
/// <c>ERP.IntegrationTests/CustomerSalesInvoiceConcurrencySqlServerTests.cs</c>。</para>
/// </summary>
public class CustomerSalesInvoiceConcurrencyTests
{
    // ==================== 0. 脚手架 ====================

    /// <summary>构造绑定到真实 HTTP 请求管线（<c>Request.Path</c> 已赋值）的控制器，使实时授权按真实请求口径生效。</summary>
    private static CustomerSalesInvoiceEvidenceController ForUser(ErpDbContext db, long? userId, bool httpBound = true)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (httpBound) http.Request.Path = "/api/customer-sales-invoices";
        return new CustomerSalesInvoiceEvidenceController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code, string name)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = "/sales/sales-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static SysRole SeedRole(ErpDbContext db, SysMenu[] menus, bool isSystem = false)
    {
        var role = new SysRole
        {
            RoleCode = $"CsiConc-{Guid.NewGuid():N}", RoleName = "销项发票并发测试角色", IsSystem = isSystem
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        foreach (var menu in menus)
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return role;
    }

    private static long SeedUser(ErpDbContext db, SysRole role, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"csi-conc-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销项发票并发测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种特权账号（系统内置角色 + 既有「销售订单」菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var menu = SeedMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuText);
        var role = new SysRole { RoleCode = $"CsiPriv-{Guid.NewGuid():N}", RoleName = "销项发票特权角色", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种受限业务员（员工编码映射 + 指定角色 / 菜单 + 本人客户），返回用户 Id。</summary>
    private static long SeedSalesman(ErpDbContext db, SysRole role, params long[] ownedCustomerIds)
    {
        var code = $"csi-sales-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销项发票业务员",
            Status = UserStatus.Enabled
        };
        db.BaseEmployees.Add(employee);
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
            db.BaseCustomers.Single(c => c.Id == customerId).EmpId = employee.Id;
        db.SaveChanges();
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, EmpId = empId, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.CNY,
        decimal totalAmount = 1000m, DocumentStatus status = DocumentStatus.Approved,
        bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 8, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static TradeDocument SeedTradeDocument(
        ErpDbContext db, string docNo, long? customerId, string docType = "商业发票")
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            IssueDate = new DateTime(2026, 8, 10),
            CustomerId = customerId,
            CustomerName = customerId is > 0 ? "单证客户" : string.Empty,
            Amount = 100m,
            Currency = "CNY",
            Status = "已制作"
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string number, long customerId, string currency = "CNY", decimal gross = 100m,
        int status = CustomerSalesInvoiceEvidenceRules.StatusDraft, long? tradeDocumentId = null,
        bool deleted = false)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "发票客户",
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            TradeDocumentId = tradeDocumentId,
            TradeDocumentNo = tradeDocumentId is null ? string.Empty : $"DOC-{tradeDocumentId}",
            TradeDocumentDocType = tradeDocumentId is null ? string.Empty : "商业发票",
            Status = status,
            IsDeleted = deleted
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static void SeedAllocation(
        ErpDbContext db, long invoiceId, SalesOrder order, decimal amount, string currency = "CNY")
    {
        db.CustomerSalesInvoiceAllocations.Add(new CustomerSalesInvoiceAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoiceId,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = currency,
            CustomerId = order.CustomerId,
            CustomerCode = "C001",
            CustomerName = "发票客户",
            AllocatedAmount = amount,
            Currency = currency,
            SortOrder = 1
        });
        db.SaveChanges();
    }

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Amount = amount,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerSalesInvoiceEvidenceSaveDto InvoiceDto(
        long customerId, string number = "INV-001", decimal gross = 100m, string currency = "CNY",
        long? tradeDocumentId = null)
        => new()
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceNumber = number,
            InvoiceDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            TradeDocumentId = tradeDocumentId
        };

    private static CustomerSalesInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new CustomerSalesInvoiceAllocationSaveDto
            {
                SalesOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    private static async Task<BusinessException> AssertDeniedAsync(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    // ==================== 1. 合约：绝不匿名 / 管理员兜底、绝不新增授权 ====================

    [Fact]
    public void Authorization_contract_has_no_anonymous_or_admin_fallback_and_no_new_grants()
    {
        var rules = ReadSource("src", "ERP.Application", "Services", "CustomerSalesInvoiceAuthorizationRules.cs");
        Assert.Contains("ErrorCodes.Unauthorized", rules);
        Assert.Contains("ErrorCodes.Forbidden", rules);
        Assert.Contains("sales-order", rules);
        Assert.Contains("fail closed", rules);

        Assert.Equal("sales-order", CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode);
        Assert.Equal(SalesOrderCancellationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode);
        Assert.Contains("绝不新增权限模型", CustomerSalesInvoiceAuthorizationRules.RuleText);
        Assert.Contains("fail closed", CustomerSalesInvoiceAuthorizationRules.RuleText);
        Assert.Contains("混源", CustomerSalesInvoiceAuthorizationRules.RuleText);
        Assert.Contains("显式交叉引用单证", CustomerSalesInvoiceAuthorizationRules.RuleText);
    }

    [Fact]
    public async Task Missing_identity_is_rejected_on_every_route_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-1", customer.Id);
        var invoice = SeedInvoice(db, "INV-MISS", customer.Id);
        SeedAllocation(db, invoice.Id, order, 40m);
        var ctl = ForUser(db, null);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, customer.Id, ErrorCodes.Unauthorized);

        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, StoredStatus(db, invoice.Id));
        Assert.Single(db.CustomerSalesInvoiceAllocations);
        Assert.Single(db.CustomerSalesInvoiceEvidences);
    }

    [Fact]
    public async Task Disabled_account_is_rejected_with_forbidden_on_every_route()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, new[] { menu });
        var userId = SeedUser(db, role, UserStatus.Disabled);
        var customer = SeedCustomer(db, "C001", "客户A");
        var invoice = SeedInvoice(db, "INV-DISABLED", customer.Id);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, customer.Id, ErrorCodes.Forbidden);

        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, StoredStatus(db, invoice.Id));
        Assert.Empty(db.CustomerSalesInvoiceAllocations);
    }

    [Fact]
    public async Task Account_without_sales_order_menu_is_rejected_with_forbidden()
    {
        using var db = TestDbFactory.Create();
        var other = SeedMenu(db, "stock-query", "库存查询");
        SeedMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, new[] { other });
        var customer = SeedCustomer(db, "C001", "客户A");
        var userId = SeedSalesman(db, role, customer.Id);
        var invoice = SeedInvoice(db, "INV-NOMENU", customer.Id);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, customer.Id, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Role_without_any_menu_grant_is_rejected_when_menu_is_provisioned()
    {
        using var db = TestDbFactory.Create();
        SeedMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, Array.Empty<SysMenu>());
        var customer = SeedCustomer(db, "C001", "客户A");
        var userId = SeedSalesman(db, role, customer.Id);
        var invoice = SeedInvoice(db, "INV-EMPTY", customer.Id);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, customer.Id, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Revoked_menu_authorization_converges_immediately()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuText);
        var other = SeedMenu(db, "sales-order-query", "销售订单查询");
        var role = SeedRole(db, new[] { menu, other });
        var customer = SeedCustomer(db, "C001", "客户A");
        var userId = SeedSalesman(db, role, customer.Id);
        var invoice = SeedInvoice(db, "INV-REVOKE", customer.Id);
        var ctl = ForUser(db, userId);

        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.GetById(invoice.Id));

        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == role.Id && g.MenuId == menu.Id).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, customer.Id, ErrorCodes.Forbidden);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, StoredStatus(db, invoice.Id));
    }

    private static async Task AssertAllRoutesDeniedAsync(
        CustomerSalesInvoiceEvidenceController ctl, long invoiceId, long customerId, int code)
    {
        await AssertDeniedAsync(code, () => ctl.GetPaged(new CustomerSalesInvoiceEvidenceQuery()));
        await AssertDeniedAsync(code, () => ctl.GetById(invoiceId));
        await AssertDeniedAsync(code, () => ctl.CollectionTiming(new CustomerInvoiceCollectionTimingQuery()));
        await AssertDeniedAsync(code, () => ctl.CommercialInvoiceCandidates(null, 10));
        await AssertDeniedAsync(code, () => ctl.OrderCandidates(invoiceId, null, 10));
        await AssertDeniedAsync(code, () => ctl.PreviewAllocations(invoiceId, Lines((1L, 10m))));
        await AssertDeniedAsync(code, () => ctl.SaveAllocations(invoiceId, Lines((1L, 10m))));
        await AssertDeniedAsync(code, () => ctl.Create(InvoiceDto(customerId, "INV-NEW")));
        await AssertDeniedAsync(code, () => ctl.Update(invoiceId, InvoiceDto(customerId, "INV-UPD")));
        await AssertDeniedAsync(code, () => ctl.Record(invoiceId));
        await AssertDeniedAsync(code, () => ctl.Void(invoiceId, new CustomerSalesInvoiceVoidRequest { Reason = "重开" }));
    }

    private static int StoredStatus(ErpDbContext db, long invoiceId)
        => db.CustomerSalesInvoiceEvidences.AsNoTracking().Single(i => i.Id == invoiceId).Status;

    // ==================== 2. 权威来源范围（受限业务员 / 特权账号） ====================

    private sealed record ScopeFixture(
        long UserId, long OwnCustomerId, long ForeignCustomerId,
        SalesOrder OwnOrder, SalesOrder ForeignOrder, SalesOrder DeletedOrder,
        TradeDocument OwnDocument, TradeDocument ForeignDocument, TradeDocument UnownedDocument,
        CustomerSalesInvoiceEvidence OwnInvoice, CustomerSalesInvoiceEvidence ForeignInvoice,
        CustomerSalesInvoiceEvidence MixedInvoice, CustomerSalesInvoiceEvidence UnprovableInvoice,
        CustomerSalesInvoiceEvidence OwnDocumentInvoice, CustomerSalesInvoiceEvidence ForeignDocumentInvoice,
        CustomerSalesInvoiceEvidence UnownedDocumentInvoice);

    private static ScopeFixture SeedScopeFixture(ErpDbContext db, bool privileged = false)
    {
        var required = SeedMenu(db, CustomerSalesInvoiceAuthorizationRules.RequiredMenuCode,
            CustomerSalesInvoiceAuthorizationRules.RequiredMenuText);
        var other = SeedMenu(db, "sales-order-query", "销售订单查询");
        var role = privileged
            ? SeedRole(db, Array.Empty<SysMenu>(), isSystem: true)
            : SeedRole(db, new[] { required, other });

        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户");
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "他人客户");
        var userId = privileged
            ? SeedUser(db, role, UserStatus.Enabled)
            : SeedSalesman(db, role, ownCustomer.Id);

        var ownOrder = SeedSalesOrder(db, "SO-OWN", ownCustomer.Id);
        var foreignOrder = SeedSalesOrder(db, "SO-FOREIGN", foreignCustomer.Id);
        var deletedOrder = SeedSalesOrder(db, "SO-DELETED", ownCustomer.Id, deleted: true);

        var ownDocument = SeedTradeDocument(db, "CI-OWN", ownCustomer.Id);
        var foreignDocument = SeedTradeDocument(db, "CI-FOREIGN", foreignCustomer.Id);
        var unownedDocument = SeedTradeDocument(db, "CI-UNOWNED", null);

        var ownInvoice = SeedInvoice(db, "INV-OWN", ownCustomer.Id);
        SeedAllocation(db, ownInvoice.Id, ownOrder, 40m);

        var foreignInvoice = SeedInvoice(db, "INV-FOREIGN", foreignCustomer.Id);
        SeedAllocation(db, foreignInvoice.Id, foreignOrder, 40m);

        // 混源：一张发票的分摊来源同时包含本人客户与他人客户的订单 → 每一个相关客户都必须允许，否则拒绝。
        var mixedInvoice = SeedInvoice(db, "INV-MIXED", ownCustomer.Id);
        SeedAllocation(db, mixedInvoice.Id, ownOrder, 30m);
        SeedAllocation(db, mixedInvoice.Id, foreignOrder, 20m);

        // 来源订单已删除 → 归属不可证明：受限账号一律 fail closed。
        var unprovableInvoice = SeedInvoice(db, "INV-UNPROVABLE", ownCustomer.Id);
        SeedAllocation(db, unprovableInvoice.Id, deletedOrder, 10m);

        var ownDocumentInvoice = SeedInvoice(db, "INV-DOC-OWN", ownCustomer.Id,
            tradeDocumentId: ownDocument.Id);
        var foreignDocumentInvoice = SeedInvoice(db, "INV-DOC-FOREIGN", ownCustomer.Id,
            tradeDocumentId: foreignDocument.Id);
        var unownedDocumentInvoice = SeedInvoice(db, "INV-DOC-UNOWNED", ownCustomer.Id,
            tradeDocumentId: unownedDocument.Id);

        return new ScopeFixture(userId, ownCustomer.Id, foreignCustomer.Id, ownOrder, foreignOrder,
            deletedOrder, ownDocument, foreignDocument, unownedDocument, ownInvoice, foreignInvoice,
            mixedInvoice, unprovableInvoice, ownDocumentInvoice, foreignDocumentInvoice,
            unownedDocumentInvoice);
    }

    [Fact]
    public async Task Restricted_salesman_reads_only_invoices_with_every_source_in_scope()
    {
        using var db = TestDbFactory.Create();
        var f = SeedScopeFixture(db);
        var ctl = ForUser(db, f.UserId);

        var page = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await ctl.GetPaged(new CustomerSalesInvoiceEvidenceQuery()));
        Assert.Equal(
            new[] { f.OwnInvoice.Id, f.OwnDocumentInvoice.Id, f.UnownedDocumentInvoice.Id }.OrderBy(x => x),
            page.Items.Select(x => x.Id).OrderBy(x => x));

        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.GetById(f.OwnInvoice.Id));
        AssertOk<List<CustomerSalesInvoiceOrderCandidateDto>>(await ctl.OrderCandidates(f.OwnInvoice.Id, null, 10));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.GetById(f.OwnDocumentInvoice.Id));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.GetById(f.UnownedDocumentInvoice.Id));

        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(f.ForeignInvoice.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(f.MixedInvoice.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(f.ForeignDocumentInvoice.Id));

        var unprovable = await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(f.UnprovableInvoice.Id));
        Assert.Equal(CustomerSalesInvoiceAuthorizationRules.UnprovableSourceText, unprovable.Message);
    }

    [Fact]
    public async Task Privileged_account_retains_historical_invoice_access()
    {
        using var db = TestDbFactory.Create();
        var f = SeedScopeFixture(db, privileged: true);
        var ctl = ForUser(db, f.UserId);

        var page = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await ctl.GetPaged(new CustomerSalesInvoiceEvidenceQuery()));
        Assert.Equal(
            new[]
            {
                f.OwnInvoice.Id, f.ForeignInvoice.Id, f.MixedInvoice.Id, f.UnprovableInvoice.Id,
                f.OwnDocumentInvoice.Id, f.ForeignDocumentInvoice.Id, f.UnownedDocumentInvoice.Id
            }.OrderBy(x => x),
            page.Items.Select(x => x.Id).OrderBy(x => x));

        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.GetById(f.UnprovableInvoice.Id));
        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.GetById(f.ForeignInvoice.Id));
    }

    [Fact]
    public async Task Restricted_salesman_cannot_mutate_foreign_or_unprovable_invoice()
    {
        using var db = TestDbFactory.Create();
        var f = SeedScopeFixture(db);
        var ctl = ForUser(db, f.UserId);

        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Update(f.ForeignInvoice.Id, InvoiceDto(f.OwnCustomerId, "INV-FOREIGN")));
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Update(f.UnprovableInvoice.Id, InvoiceDto(f.OwnCustomerId, "INV-UNPROVABLE")));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.Record(f.ForeignInvoice.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Void(f.ForeignInvoice.Id, new CustomerSalesInvoiceVoidRequest { Reason = "越界作废" }));

        // 拟提议客户越界同样在写入之前拒绝：被拒绝的调用方绝不改写任何发票。
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Update(f.OwnInvoice.Id,
                InvoiceDto(f.ForeignCustomerId, "INV-OWN")));

        var foreign = db.CustomerSalesInvoiceEvidences.AsNoTracking().Single(i => i.Id == f.ForeignInvoice.Id);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, foreign.Status);
        Assert.Null(foreign.VoidedAt);
        Assert.Equal(string.Empty, foreign.VoidReason);
        Assert.Equal(f.ForeignCustomerId, foreign.CustomerId);

        var own = db.CustomerSalesInvoiceEvidences.AsNoTracking().Single(i => i.Id == f.OwnInvoice.Id);
        Assert.Equal(f.OwnCustomerId, own.CustomerId);
        Assert.Equal(100m, own.GrossAmount);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, own.Status);
    }

    [Fact]
    public async Task Restricted_salesman_cannot_replace_allocations_with_foreign_or_unowned_source()
    {
        using var db = TestDbFactory.Create();
        var f = SeedScopeFixture(db);
        var ctl = ForUser(db, f.UserId);

        // 合法：拟提议来源仍在本人客户范围内，整体替换成功
        AssertOk<CustomerSalesInvoiceEvidenceDto>(
            await ctl.SaveAllocations(f.OwnInvoice.Id, Lines((f.OwnOrder.Id, 80m))));
        AssertOk<CustomerSalesInvoiceAllocationPreviewDto>(
            await ctl.PreviewAllocations(f.OwnInvoice.Id, Lines((f.OwnOrder.Id, 80m))));

        // 越界：他人客户订单 / 已删除（归属不可证明）订单 —— 在替换之前拒绝且不改写任何分摊行
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.SaveAllocations(f.OwnInvoice.Id, Lines((f.ForeignOrder.Id, 10m))));
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.SaveAllocations(f.OwnInvoice.Id, Lines((f.DeletedOrder.Id, 10m))));
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.PreviewAllocations(f.OwnInvoice.Id, Lines((f.ForeignOrder.Id, 10m))));

        var rows = db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == f.OwnInvoice.Id).ToList();
        Assert.Equal(80m, Assert.Single(rows).AllocatedAmount);
        Assert.Equal(f.OwnOrder.Id, rows[0].SalesOrderId);
    }

    [Fact]
    public async Task Restricted_salesman_cannot_expand_scope_with_foreign_trade_document()
    {
        using var db = TestDbFactory.Create();
        var f = SeedScopeFixture(db);
        var ctl = ForUser(db, f.UserId);
        var before = await db.CustomerSalesInvoiceEvidences.CountAsync();

        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Create(InvoiceDto(f.OwnCustomerId, "INV-NEW-DOC", tradeDocumentId: f.ForeignDocument.Id)));
        Assert.Equal(before, await db.CustomerSalesInvoiceEvidences.CountAsync());

        // 拟提议单证归属越界：更新在写入之前拒绝，已存储引用保持不变
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Update(f.OwnInvoice.Id,
                InvoiceDto(f.OwnCustomerId, "INV-OWN", tradeDocumentId: f.ForeignDocument.Id)));
        Assert.Null(db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Single(i => i.Id == f.OwnInvoice.Id).TradeDocumentId);

        // 无权威归属的历史单证不额外阻断（发票自身客户仍在范围内）
        var created = AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.Create(
            InvoiceDto(f.OwnCustomerId, "INV-NEW-OK", tradeDocumentId: f.UnownedDocument.Id)));
        Assert.Equal(f.UnownedDocument.Id, created.TradeDocumentId);
    }

    [Fact]
    public async Task Commercial_invoice_candidates_are_scoped_for_restricted_accounts()
    {
        using var db = TestDbFactory.Create();
        var f = SeedScopeFixture(db);

        var restricted = ForUser(db, f.UserId);
        var scoped = AssertOk<List<CustomerSalesInvoiceTradeDocumentCandidateDto>>(
            await restricted.CommercialInvoiceCandidates(null, 200));
        Assert.Equal(new[] { f.OwnDocument.Id }, scoped.Select(c => c.TradeDocumentId));

        var privileged = ForUser(db, SeedUser(db,
            SeedRole(db, Array.Empty<SysMenu>(), isSystem: true), UserStatus.Enabled));
        var all = AssertOk<List<CustomerSalesInvoiceTradeDocumentCandidateDto>>(
            await privileged.CommercialInvoiceCandidates(null, 200));
        Assert.Equal(
            new[] { f.OwnDocument.Id, f.ForeignDocument.Id, f.UnownedDocument.Id }.OrderBy(x => x),
            all.Select(c => c.TradeDocumentId).OrderBy(x => x));
    }

    // ==================== 3. 容量护栏（订单 / 发票）与原子拒绝 ====================

    [Fact]
    public async Task Concurrent_invoices_cannot_collectively_exceed_order_invoice_capacity()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-CAP", customer.Id, totalAmount: 100m);
        var first = SeedInvoice(db, "INV-CAP-1", customer.Id);
        var second = SeedInvoice(db, "INV-CAP-2", customer.Id);
        var ctl = ForUser(db, userId);

        AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.SaveAllocations(first.Id, Lines((order.Id, 100m))));

        // 并发方（第二张发票）在同一销售订单上超过剩余容量：整体拒绝，绝不写任何分摊行
        var ex = await AssertDeniedAsync(ErrorCodes.RuleConflict,
            () => ctl.SaveAllocations(second.Id, Lines((order.Id, 1m))));
        Assert.Contains("容量", ex.Message);

        Assert.Empty(db.CustomerSalesInvoiceAllocations.Where(a => a.CustomerSalesInvoiceEvidenceId == second.Id));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, StoredStatus(db, second.Id));
    }

    [Fact]
    public async Task Record_rechecks_order_invoice_capacity_under_lock()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedSalesOrder(db, "SO-RECCAP", customer.Id, totalAmount: 100m);

        // 已登记发票先占满订单容量（100），随后另一张草稿发票已持久化超容量分摊（100 + 50 > 100）
        var recorded = SeedInvoice(db, "INV-R1", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusRecorded);
        SeedAllocation(db, recorded.Id, order, 100m);
        var draft = SeedInvoice(db, "INV-R2", customer.Id, gross: 50m);
        SeedAllocation(db, draft.Id, order, 50m);

        var ctl = ForUser(db, userId);
        var ex = await AssertDeniedAsync(ErrorCodes.RuleConflict, () => ctl.Record(draft.Id));
        Assert.Contains("容量", ex.Message);

        var stored = db.CustomerSalesInvoiceEvidences.AsNoTracking().Single(i => i.Id == draft.Id);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusDraft, stored.Status);
        Assert.Null(stored.RecordedAt);
    }

    [Fact]
    public async Task Distinct_receipts_cannot_collectively_exceed_invoice_capacity()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var invoice = SeedInvoice(db, "INV-RCAP", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusRecorded);
        var first = SeedReceipt(db, "SK-CAP-1", customer.Id, 100m);
        var second = SeedReceipt(db, "SK-CAP-2", customer.Id, 100m);

        await CustomerSalesInvoiceCollectionAllocationService.CreateAsync(
            db,
            new CustomerSalesInvoiceCollectionAllocationSaveDto
            {
                CustomerSalesInvoiceEvidenceId = invoice.Id, ReceiptId = first.Id, AllocatedAmount = 100m
            }, "tester");

        // 第二张收款单（并发方）超出发票同币种收款容量：原子拒绝，不新增任何分摊行
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CustomerSalesInvoiceCollectionAllocationService.CreateAsync(
                db,
                new CustomerSalesInvoiceCollectionAllocationSaveDto
                {
                    CustomerSalesInvoiceEvidenceId = invoice.Id, ReceiptId = second.Id, AllocatedAmount = 1m
                }, "tester"));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("未分摊含税额", ex.Message);

        var rows = db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive).ToList();
        Assert.Equal(100m, Assert.Single(rows).AllocatedAmount);
        Assert.Equal(first.Id, rows[0].ReceiptId);
    }

    [Fact]
    public async Task Void_preserves_reason_and_frozen_audit_and_blocks_new_receipt_evidence()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var invoice = SeedInvoice(db, "INV-VOID", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusRecorded);
        var ctl = ForUser(db, userId);

        var voided = AssertOk<CustomerSalesInvoiceEvidenceDto>(await ctl.Void(invoice.Id,
            new CustomerSalesInvoiceVoidRequest { Reason = "发票号码录错重开" }));
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided, voided.Status);
        Assert.Equal("发票号码录错重开", voided.VoidReason);

        var stored = db.CustomerSalesInvoiceEvidences.AsNoTracking().Single(i => i.Id == invoice.Id);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusVoided, stored.Status);
        Assert.NotNull(stored.VoidedAt);
        Assert.Equal("发票号码录错重开", stored.VoidReason);
        Assert.Equal(100m, stored.GrossAmount);
        Assert.Equal(100m, stored.NetAmount);

        // 重复作废在状态门被拒绝（保留原始作废证据）
        var repeat = await AssertDeniedAsync(ErrorCodes.RuleConflict,
            () => ctl.Void(invoice.Id, new CustomerSalesInvoiceVoidRequest { Reason = "再作废一次" }));
        Assert.Contains("重复作废", repeat.Message);

        // 作废后发票不再是可分摊的收款证据：新登记被拒绝且不新增行
        var receipt = SeedReceipt(db, "SK-AFTER-VOID", customer.Id, 50m);
        var create = await Assert.ThrowsAsync<BusinessException>(
            () => CustomerSalesInvoiceCollectionAllocationService.CreateAsync(
                db,
                new CustomerSalesInvoiceCollectionAllocationSaveDto
                {
                    CustomerSalesInvoiceEvidenceId = invoice.Id, ReceiptId = receipt.Id, AllocatedAmount = 10m
                }, "tester"));
        Assert.Equal(ErrorCodes.RuleConflict, create.Code);
        Assert.Empty(db.CustomerSalesInvoiceCollectionAllocations);
    }

    [Fact]
    public async Task Receipt_dimension_separation_preserves_shared_funding_conventions()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var invoice = SeedInvoice(db, "INV-DIM", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusRecorded);
        var receipt = SeedReceipt(db, "SK-DIM", customer.Id, 100m);
        var order = SeedSalesOrder(db, "SO-DIM", customer.Id);

        // 既有 ERP-053 维度：收款单 → 销售订单（占满 ERP-350 共用额度；本任务不改写它）
        var erp053 = await CustomerReceiptAllocationService.CreateAsync(db,
            new CustomerReceiptAllocationSaveDto
            {
                ReceiptId = receipt.Id, SalesOrderId = order.Id, AllocatedAmount = 100m
            });
        Assert.Equal(100m, erp053.AllocatedAmount);

        // ERP-073 维度独立登记：不读取 / 不改写 ERP-053 行，也不改写收款单
        var erp073 = await CustomerSalesInvoiceCollectionAllocationService.CreateAsync(db,
            new CustomerSalesInvoiceCollectionAllocationSaveDto
            {
                CustomerSalesInvoiceEvidenceId = invoice.Id, ReceiptId = receipt.Id, AllocatedAmount = 100m
            }, "tester");
        Assert.Equal(100m, erp073.AllocatedAmount);

        Assert.Equal(100m, db.CustomerReceiptAllocations.AsNoTracking().Single().AllocatedAmount);
        Assert.Empty(db.AgencyServiceFeeCollectionAllocations);

        var funding = await CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db, receipt.Id);
        Assert.Equal(100m, funding.CustomerOrderAllocated);
        Assert.Equal(0m, funding.AgencyAllocated);
        Assert.Equal(100m, funding.CombinedAllocated);
        Assert.Equal(100m, db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Amount);
    }

    // ==================== 4. 锁与授权接线契约（源码同源） ====================

    [Fact]
    public void Service_and_controller_contract_wires_locks_and_authorization()
    {
        var service = ReadSource("src", "ERP.Application", "Services", "CustomerSalesInvoiceEvidenceService.cs");
        Assert.Contains("CustomerSalesInvoiceConcurrencyRules.LockInvoiceRowAsync", service);
        Assert.Contains("CustomerSalesInvoiceConcurrencyRules.LockSalesOrderRowsAsync", service);
        Assert.Contains("EnsureOrderInvoiceCapacity", service);
        // 修改 / 分摊替换 / 登记 / 作废四条写路由都在原子事务内
        Assert.True(Count(service, "BeginTransactionAsync") >= 4);

        var controller = ReadSource("src", "ERP.Api", "Controllers", "CustomerSalesInvoiceEvidenceController.cs");
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureMenuAuthorizedAsync", controller);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureInvoiceAuthorizedWithScopeAsync", controller);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureSalesOrderIdsAuthorizedAsync", controller);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.EnsureTradeDocumentAuthorizedAsync", controller);
        Assert.Contains("CustomerSalesInvoiceAuthorizationRules.BuildScopePredicateAsync", controller);
        Assert.Contains("RequiresLiveAuthorization", controller);

        var collection = ReadSource("src", "ERP.Application", "Services",
            "CustomerSalesInvoiceCollectionAllocationService.cs");
        Assert.Contains("CustomerSalesInvoiceConcurrencyRules.LockInvoiceAndReceiptRowsAsync", collection);
        Assert.True(Count(collection, "BeginTransactionAsync") >= 2);

        var receiptRules = ReadSource("src", "ERP.Application", "Services", "CustomerReceiptLifecycleRules.cs");
        Assert.Contains("GlobalLockOrderText", receiptRules);
        Assert.Contains("GlobalLockOrderText",
            ReadSource("src", "ERP.Application", "Services", "SalesOrderCancellationRules.cs"));
        Assert.Contains("ERP-383",
            ReadSource("src", "ERP.Application", "Services", "CustomerReceiptAllocationService.cs"));
        Assert.Contains("ERP-383",
            ReadSource("src", "ERP.Application", "Services", "AgencyServiceFeeCollectionAllocationService.cs"));
    }

    [Fact]
    public void Global_lock_order_is_documented_once_across_callers()
    {
        Assert.Contains("唯一全局锁序", CustomerSalesInvoiceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("来源销售订单行", CustomerSalesInvoiceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("客户销项发票行", CustomerSalesInvoiceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("客户收款单行", CustomerSalesInvoiceConcurrencyRules.GlobalLockOrderText);

        Assert.Contains("唯一全局锁序", CustomerReceiptLifecycleRules.GlobalLockOrderText);
        Assert.Contains("唯一全局锁序", SalesOrderCancellationRules.GlobalLockOrderText);

        // 锁序解析：去重 + 只保留正整数 + Id 升序
        Assert.Equal(new long[] { 3, 5, 9 }, CustomerSalesInvoiceConcurrencyRules.MergeOrderLockIds(
            new long[] { 9, 5, 3, 5, 0, -2 }));
        Assert.Empty(CustomerSalesInvoiceConcurrencyRules.MergeOrderLockIds(null));
    }
}
