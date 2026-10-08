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
/// ERP-382 供应商采购发票并发与授权护栏单元测试（内存库 + 真实 HTTP 身份）。
/// <list type="number">
/// <item><b>实时授权</b>：缺失身份 / 禁用账号 / 撤销菜单 / 无采购订单模块授权在核心路由（台账 / 详情 / 候选 / 预览 /
/// 新增 / 修改 / 关联替换 / 登记 / 作废）上 fail closed 且零副作用；受限业务员只能读取 / 改写来源采购订单属于本人客户
/// 的发票，无来源或来源越界一律拒绝；特权账号保留历史无来源发票的可读 / 可操作能力；绝不新增任何用户授权。</item>
/// <item><b>容量护栏</b>：并发多张发票不得合计超过同一采购订单总额（订单发票容量）；并发不同付款单不得合计超过同一
/// 发票含税总额（发票容量）；ERP-351 共用付款额度（付款单 → 采购订单 与 付款单 → 采购发票 合计）保持不变。</item>
/// <item><b>原子拒绝与证据保留</b>：作废保留作废原因与冻结审计；被拒绝 / 越权的请求不改写任何发票 / 关联行 / 订单 /
/// 付款证据。</item>
/// </list>
/// <para>内存库无行锁语义（<see cref="PurchaseInvoiceConcurrencyRules.IsRelationalProvider"/> 为 false 时锁定与事务等价无操作），
/// 因此本文件的「并发」以确定性容量口径 + 原子拒绝验证；真实 SQL 两个独立连接竞态见
/// <c>ERP.IntegrationTests/PurchaseInvoiceConcurrencySqlServerTests.cs</c>。</para>
/// </summary>
public class PurchaseInvoiceConcurrencyTests
{
    // ==================== 0. 脚手架 ====================

    /// <summary>构造绑定到真实 HTTP 请求管线（<c>Request.Path</c> 已赋值）的控制器，使实时授权按真实请求口径生效。</summary>
    private static PurchaseInvoiceController ForUser(ErpDbContext db, long? userId, bool httpBound = true)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (httpBound) http.Request.Path = "/api/purchase-invoices";
        return new PurchaseInvoiceController(db)
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
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static SysRole SeedRole(ErpDbContext db, params SysMenu[] menus)
    {
        var role = new SysRole { RoleCode = $"PiConc-{Guid.NewGuid():N}", RoleName = "发票并发测试角色" };
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
            UserName = $"pi-conc-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "发票并发测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种特权账号（系统内置角色 + 既有「采购订单」菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var menu = SeedMenu(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            PurchaseInvoiceAuthorizationRules.RequiredMenuText);
        var role = new SysRole { RoleCode = $"PiPriv-{Guid.NewGuid():N}", RoleName = "发票特权角色", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种受限业务员（员工编码映射 + 指定角色 / 菜单 + 本人客户），返回（用户 Id，员工 Id）。</summary>
    private static (long UserId, long EmployeeId) SeedSalesman(ErpDbContext db, SysRole role, params long[] ownedCustomerIds)
    {
        var code = $"pi-sales-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "发票业务员",
            Status = UserStatus.Enabled
        };
        db.BaseEmployees.Add(employee);
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
        {
            var customer = db.BaseCustomers.Single(c => c.Id == customerId);
            customer.EmpId = employee.Id;
        }
        db.SaveChanges();
        return (user.Id, employee.Id);
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
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

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 8, 1),
            CustomerId = customerId,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, long? owningCustomerId = null,
        long? owningSalesOrderId = null, decimal totalAmount = 1000m, string currency = "CNY")
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = Enum.Parse<Currency>(currency),
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算",
            OwningCustomerId = owningCustomerId,
            OwningSalesOrderId = owningSalesOrderId
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal gross = 100m, string currency = "CNY",
        int status = PurchaseInvoiceRules.StatusDraft, string invoiceType = "普票", string code = "")
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = invoiceType,
            InvoiceCode = code,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(code),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            SupplierCode = "S001",
            SupplierName = "义乌档口",
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross,
            Status = status
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static void SeedAllocation(
        ErpDbContext db, long invoiceId, PurchaseOrder order, decimal amount, string currency = "CNY")
    {
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoiceId,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderCurrency = currency,
            SupplierId = order.SupplierId,
            SupplierCode = "S001",
            SupplierName = "义乌档口",
            AllocatedAmount = amount,
            Currency = currency,
            SortOrder = 1
        });
        db.SaveChanges();
    }

    private static FinancePayment SeedPayment(ErpDbContext db, string paymentNo, long supplierId, decimal amount)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 9, 10),
            SupplierId = supplierId,
            Amount = amount,
            Currency = Currency.CNY,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "BANK",
            Status = DocumentStatus.Approved
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static PurchaseInvoiceSaveDto InvoiceDto(
        long supplierId, string number = "0001", decimal gross = 100m, string currency = "CNY")
        => new()
        {
            InvoiceType = "普票",
            InvoiceNumber = number,
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            Currency = currency,
            NetAmount = gross,
            TaxAmount = 0m,
            GrossAmount = gross
        };

    private static PurchaseInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new PurchaseInvoiceAllocationSaveDto
            {
                PurchaseOrderId = l.OrderId,
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

    /// <summary>库存 / 原始失败日志等既有事实：授权失败绝不新增用户授权、绝不做匿名 / 管理员兜底。</summary>
    [Fact]
    public void Authorization_contract_has_no_anonymous_or_admin_fallback_and_no_new_grants()
    {
        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseInvoiceAuthorizationRules.cs");
        Assert.Contains("ErrorCodes.Unauthorized", rules);
        Assert.Contains("ErrorCodes.Forbidden", rules);
        Assert.Contains("purchase-order", rules);
        Assert.Contains("fail closed", rules);

        Assert.Equal("purchase-order", PurchaseInvoiceAuthorizationRules.RequiredMenuCode);
        Assert.Contains("绝不新增权限模型", PurchaseInvoiceAuthorizationRules.RuleText);
        Assert.Contains("fail closed", PurchaseInvoiceAuthorizationRules.RuleText);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Missing_identity_is_rejected_on_every_route_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var invoice = SeedInvoice(db, "INV-MISS", supplier.Id, 100m);
        var ctl = ForUser(db, null);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Unauthorized);

        Assert.Equal(PurchaseInvoiceRules.StatusDraft, StoredStatus(db, invoice.Id));
        Assert.Empty(db.PurchaseInvoiceAllocations);
        Assert.Single(db.PurchaseInvoices);
    }

    [Fact]
    public async Task Disabled_account_is_rejected_with_forbidden_on_every_route()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            PurchaseInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var userId = SeedUser(db, role, UserStatus.Disabled);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var invoice = SeedInvoice(db, "INV-DISABLED", supplier.Id, 100m);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Forbidden);

        Assert.Equal(PurchaseInvoiceRules.StatusDraft, StoredStatus(db, invoice.Id));
        Assert.Empty(db.PurchaseInvoiceAllocations);
    }

    [Fact]
    public async Task Account_without_purchase_order_menu_is_rejected_with_forbidden()
    {
        using var db = TestDbFactory.Create();
        var other = SeedMenu(db, "stock-query", "库存查询");
        var role = SeedRole(db, other);
        var userId = SeedUser(db, role, UserStatus.Enabled);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var invoice = SeedInvoice(db, "INV-NOMENU", supplier.Id, 100m);
        var ctl = ForUser(db, userId);

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Revoked_menu_authorization_converges_immediately()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            PurchaseInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var customer = SeedCustomer(db, "C001", "客户A");
        var (userId, _) = SeedSalesman(db, role, customer.Id);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var order = SeedOrder(db, "PO-REVOKE", supplier.Id, owningCustomerId: customer.Id);
        var invoice = SeedInvoice(db, "INV-REVOKE", supplier.Id, 100m);
        SeedAllocation(db, invoice.Id, order, 40m);
        var ctl = ForUser(db, userId);

        AssertOk<PurchaseInvoiceDto>(await ctl.GetById(invoice.Id));

        foreach (var grant in db.SysRoleMenus.Where(g => g.RoleId == role.Id && !g.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        await AssertAllRoutesDeniedAsync(ctl, invoice.Id, supplier.Id, ErrorCodes.Forbidden);
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, StoredStatus(db, invoice.Id));
    }

    private static async Task AssertAllRoutesDeniedAsync(
        PurchaseInvoiceController ctl, long invoiceId, long supplierId, int code)
    {
        await AssertDeniedAsync(code, () => ctl.GetPaged(new PurchaseInvoiceQuery()));
        await AssertDeniedAsync(code, () => ctl.GetById(invoiceId));
        await AssertDeniedAsync(code, () => ctl.OrderCandidates(invoiceId, null, 10));
        await AssertDeniedAsync(code, () => ctl.PreviewAllocations(invoiceId, Lines((1L, 10m))));
        await AssertDeniedAsync(code, () => ctl.SaveAllocations(invoiceId, Lines((1L, 10m))));
        await AssertDeniedAsync(code, () => ctl.Create(InvoiceDto(supplierId, "INV-NEW")));
        await AssertDeniedAsync(code, () => ctl.Update(invoiceId, InvoiceDto(supplierId, "INV-UPD")));
        await AssertDeniedAsync(code, () => ctl.Record(invoiceId));
        await AssertDeniedAsync(code, () => ctl.Void(invoiceId, new PurchaseInvoiceVoidRequest { Reason = "重开" }));
    }

    private static int StoredStatus(ErpDbContext db, long invoiceId)
        => db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == invoiceId).Status;

    // ==================== 2. 权威来源范围（受限业务员 / 特权账号） ====================

    [Fact]
    public async Task Restricted_salesman_reads_only_invoices_sourced_from_own_customers()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            PurchaseInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户");
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "他人客户");
        var (userId, _) = SeedSalesman(db, role, ownCustomer.Id);

        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var ownOrder = SeedOrder(db, "PO-OWN", supplier.Id, owningCustomerId: ownCustomer.Id);
        var foreignOrder = SeedOrder(db, "PO-FOREIGN", supplier.Id, owningCustomerId: foreignCustomer.Id);
        var unownedOrder = SeedOrder(db, "PO-UNOWNED", supplier.Id);

        var ownInvoice = SeedInvoice(db, "INV-OWN", supplier.Id, 100m);
        SeedAllocation(db, ownInvoice.Id, ownOrder, 40m);
        var foreignInvoice = SeedInvoice(db, "INV-FOREIGN", supplier.Id, 100m);
        SeedAllocation(db, foreignInvoice.Id, foreignOrder, 40m);
        var unlinkedInvoice = SeedInvoice(db, "INV-UNLINKED", supplier.Id, 100m);
        // 混源发票：一张来源订单属于本人客户、另一张无权威归属 → 全部来源都必须允许，否则拒绝（fail closed）
        var mixedInvoice = SeedInvoice(db, "INV-MIXED", supplier.Id, 100m);
        SeedAllocation(db, mixedInvoice.Id, ownOrder, 30m);
        SeedAllocation(db, mixedInvoice.Id, unownedOrder, 30m);

        var ctl = ForUser(db, userId);

        var page = AssertOk<PagedResult<PurchaseInvoiceDto>>(await ctl.GetPaged(new PurchaseInvoiceQuery()));
        Assert.Equal(ownInvoice.Id, Assert.Single(page.Items).Id);

        AssertOk<PurchaseInvoiceDto>(await ctl.GetById(ownInvoice.Id));
        AssertOk<List<PurchaseInvoiceOrderCandidateDto>>(await ctl.OrderCandidates(ownInvoice.Id, null, 10));

        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(foreignInvoice.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(mixedInvoice.Id));

        var unlinked = await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(unlinkedInvoice.Id));
        Assert.Equal(PurchaseInvoiceAuthorizationRules.UnlinkedDeniedText, unlinked.Message);
    }

    [Fact]
    public async Task Privileged_account_retains_historical_unlinked_invoice_access()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var unlinkedInvoice = SeedInvoice(db, "INV-HIST", supplier.Id, 100m);
        var ctl = ForUser(db, userId);

        var page = AssertOk<PagedResult<PurchaseInvoiceDto>>(await ctl.GetPaged(new PurchaseInvoiceQuery()));
        Assert.Equal(unlinkedInvoice.Id, Assert.Single(page.Items).Id);
        AssertOk<PurchaseInvoiceDto>(await ctl.GetById(unlinkedInvoice.Id));
    }

    [Fact]
    public async Task Restricted_salesman_cannot_replace_allocations_with_foreign_or_unowned_source()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            PurchaseInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户");
        var foreignCustomer = SeedCustomer(db, "C-FOREIGN", "他人客户");
        var (userId, _) = SeedSalesman(db, role, ownCustomer.Id);

        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var ownOrder = SeedOrder(db, "PO-OWN", supplier.Id, owningCustomerId: ownCustomer.Id);
        var foreignOrder = SeedOrder(db, "PO-FOREIGN", supplier.Id, owningCustomerId: foreignCustomer.Id);
        var unownedOrder = SeedOrder(db, "PO-UNOWNED", supplier.Id);
        var invoice = SeedInvoice(db, "INV-REPLACE", supplier.Id, 100m);
        SeedAllocation(db, invoice.Id, ownOrder, 40m);

        var ctl = ForUser(db, userId);

        // 合法：拟提议来源仍在本人客户范围内，整体替换成功
        AssertOk<PurchaseInvoiceDto>(await ctl.SaveAllocations(invoice.Id, Lines((ownOrder.Id, 80m))));

        // 越界：拟提议来源为他人客户 / 无权威归属 —— 在替换之前拒绝且不改写任何关联行
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.SaveAllocations(invoice.Id, Lines((foreignOrder.Id, 10m))));
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.SaveAllocations(invoice.Id, Lines((unownedOrder.Id, 10m))));

        var rows = db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id).ToList();
        Assert.Equal(80m, Assert.Single(rows).AllocatedAmount);
        Assert.Equal(ownOrder.Id, rows[0].PurchaseOrderId);
    }

    [Fact]
    public async Task Restricted_salesman_cannot_mutate_invoices_without_provable_source()
    {
        using var db = TestDbFactory.Create();
        var menu = SeedMenu(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode,
            PurchaseInvoiceAuthorizationRules.RequiredMenuText);
        var role = SeedRole(db, menu);
        var ownCustomer = SeedCustomer(db, "C-OWN", "本人客户");
        var (userId, _) = SeedSalesman(db, role, ownCustomer.Id);

        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var unlinkedInvoice = SeedInvoice(db, "INV-NOSRC", supplier.Id, 100m);
        var ctl = ForUser(db, userId);

        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Update(unlinkedInvoice.Id, InvoiceDto(supplier.Id, "INV-NOSRC")));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.Record(unlinkedInvoice.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden,
            () => ctl.Void(unlinkedInvoice.Id, new PurchaseInvoiceVoidRequest { Reason = "误开" }));

        var stored = db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == unlinkedInvoice.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, stored.Status);
        Assert.Null(stored.VoidedAt);
        Assert.Equal(string.Empty, stored.VoidReason);
        Assert.Equal(100m, stored.GrossAmount);
    }

    // ==================== 3. 容量护栏（发票 / 订单）与原子拒绝 ====================

    [Fact]
    public async Task Concurrent_invoices_cannot_collectively_exceed_order_invoice_capacity()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var order = SeedOrder(db, "PO-CAP", supplier.Id, totalAmount: 100m);
        var first = SeedInvoice(db, "INV-CAP-1", supplier.Id, 100m);
        var second = SeedInvoice(db, "INV-CAP-2", supplier.Id, 100m);
        var ctl = ForUser(db, userId);

        AssertOk<PurchaseInvoiceDto>(await ctl.SaveAllocations(first.Id, Lines((order.Id, 100m))));

        // 并发方（第二张发票）在同一采购订单上超过剩余容量：整体拒绝，绝不写任何关联行
        var ex = await AssertDeniedAsync(ErrorCodes.RuleConflict,
            () => ctl.SaveAllocations(second.Id, Lines((order.Id, 1m))));
        Assert.Contains("容量", ex.Message);

        Assert.Empty(db.PurchaseInvoiceAllocations.Where(a => a.PurchaseInvoiceId == second.Id));
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, StoredStatus(db, second.Id));
    }

    [Fact]
    public async Task Record_rechecks_order_invoice_capacity_under_lock()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var order = SeedOrder(db, "PO-RECCAP", supplier.Id, totalAmount: 100m);

        // 已登记发票先占满订单容量（100），随后另一张草稿发票已持久化超容量关联（100 + 50 > 100）。
        var recorded = SeedInvoice(db, "INV-R1", supplier.Id, 100m, status: PurchaseInvoiceRules.StatusRecorded);
        SeedAllocation(db, recorded.Id, order, 100m);
        var draft = SeedInvoice(db, "INV-R2", supplier.Id, 50m);
        SeedAllocation(db, draft.Id, order, 50m);

        var ctl = ForUser(db, userId);
        var ex = await AssertDeniedAsync(ErrorCodes.RuleConflict, () => ctl.Record(draft.Id));
        Assert.Contains("容量", ex.Message);

        var stored = db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == draft.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusDraft, stored.Status);
        Assert.Null(stored.RecordedAt);
    }

    [Fact]
    public async Task Distinct_payments_cannot_collectively_exceed_invoice_capacity()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var invoice = SeedInvoice(db, "INV-PAYCAP", supplier.Id, 100m,
            status: PurchaseInvoiceRules.StatusRecorded);
        var first = SeedPayment(db, "FK-CAP-1", supplier.Id, 100m);
        var second = SeedPayment(db, "FK-CAP-2", supplier.Id, 100m);

        await SupplierPaymentInvoiceAllocationService.CreateAsync(db, new SupplierPaymentInvoiceAllocationSaveDto
        {
            PaymentId = first.Id, PurchaseInvoiceId = invoice.Id, AllocatedAmount = 100m
        }, "tester");

        // 第二张付款单（并发方）超出发票同币种容量：原子拒绝，不新增任何引用行
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentInvoiceAllocationService.CreateAsync(db, new SupplierPaymentInvoiceAllocationSaveDto
            {
                PaymentId = second.Id, PurchaseInvoiceId = invoice.Id, AllocatedAmount = 1m
            }, "tester"));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过含税总额", ex.Message);

        var rows = db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive).ToList();
        Assert.Equal(100m, Assert.Single(rows).AllocatedAmount);
        Assert.Equal(first.Id, rows[0].PaymentId);
    }

    [Fact]
    public async Task Shared_payment_budget_with_order_allocation_is_preserved()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var order = SeedOrder(db, "PO-BUDGET", supplier.Id);
        var invoice = SeedInvoice(db, "INV-BUDGET", supplier.Id, 100m,
            status: PurchaseInvoiceRules.StatusRecorded);
        var payment = SeedPayment(db, "FK-BUDGET", supplier.Id, 100m);

        // ERP-049：付款单 → 采购订单 引用 60（占用同一付款额度）
        await SupplierPaymentAllocationService.CreateAsync(db, new SupplierPaymentAllocationSaveDto
        {
            PaymentId = payment.Id, PurchaseOrderId = order.Id, AllocatedAmount = 60m
        });

        // ERP-066：付款单 → 发票 再引用 50 → 合计 110 > 付款单 100：拒绝且不新增引用行
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentInvoiceAllocationService.CreateAsync(db, new SupplierPaymentInvoiceAllocationSaveDto
            {
                PaymentId = payment.Id, PurchaseInvoiceId = invoice.Id, AllocatedAmount = 50m
            }, "tester"));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过付款单金额", ex.Message);

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(60m, funding.OrderAllocated);
        Assert.Equal(0m, funding.InvoiceAllocated);
        Assert.Single(db.SupplierPaymentAllocations.Where(a => !a.IsDeleted));
        Assert.Empty(db.SupplierPaymentInvoiceAllocations);
    }

    // ==================== 4. 作废：原因与冻结审计保留、作废后证据失效 ====================

    [Fact]
    public async Task Void_preserves_reason_and_frozen_audit_and_blocks_new_evidence()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var invoice = SeedInvoice(db, "INV-VOID", supplier.Id, 100m);
        var ctl = ForUser(db, userId);

        var recorded = AssertOk<PurchaseInvoiceDto>(await ctl.Record(invoice.Id));
        Assert.NotNull(recorded.RecordedAt);

        var voided = AssertOk<PurchaseInvoiceDto>(await ctl.Void(invoice.Id,
            new PurchaseInvoiceVoidRequest { Reason = "  录错单价  " }));
        Assert.Equal(PurchaseInvoiceRules.StatusVoided, voided.Status);
        Assert.Equal("录错单价", voided.VoidReason);

        var stored = db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == invoice.Id);
        Assert.Equal("录错单价", stored.VoidReason);
        Assert.Equal(recorded.RecordedAt, stored.RecordedAt);   // 冻结审计不被作废改写
        Assert.NotNull(stored.VoidedAt);
        Assert.Equal(100m, stored.GrossAmount);

        // 重复作废：锁内状态门拒绝且不改写作废原因
        await AssertDeniedAsync(ErrorCodes.RuleConflict,
            () => ctl.Void(invoice.Id, new PurchaseInvoiceVoidRequest { Reason = "重开" }));
        Assert.Equal("录错单价", db.PurchaseInvoices.AsNoTracking().Single(i => i.Id == invoice.Id).VoidReason);

        // 作废后发票不再是可引用的付款证据：新引用被拒绝且不新增行
        var payment = SeedPayment(db, "FK-AFTER-VOID", supplier.Id, 50m);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => SupplierPaymentInvoiceAllocationService.CreateAsync(db, new SupplierPaymentInvoiceAllocationSaveDto
            {
                PaymentId = payment.Id, PurchaseInvoiceId = invoice.Id, AllocatedAmount = 10m
            }, "tester"));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SupplierPaymentInvoiceAllocations);
    }

    // ==================== 5. 锁与授权接线契约（源码同源） ====================

    [Fact]
    public void Service_and_controller_contract_wires_locks_and_authorization()
    {
        var service = ReadSource("src", "ERP.Application", "Services", "PurchaseInvoiceService.cs");
        Assert.Contains("PurchaseInvoiceConcurrencyRules.LockInvoiceRowAsync", service);
        Assert.Contains("PurchaseInvoiceConcurrencyRules.LockPurchaseOrderRowsAsync", service);
        Assert.Contains("EnsureOrderInvoiceCapacity", service);
        // 修改 / 关联替换 / 登记 / 作废四条写路由都在原子事务内
        Assert.True(Count(service, "BeginTransactionAsync") >= 4);

        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseInvoiceController.cs");
        Assert.Contains("PurchaseInvoiceAuthorizationRules.EnsureMenuAuthorizedAsync", controller);
        Assert.Contains("PurchaseInvoiceAuthorizationRules.EnsureInvoiceAuthorizedAsync", controller);
        Assert.Contains("PurchaseInvoiceAuthorizationRules.EnsureOrderIdsAuthorizedAsync", controller);
        Assert.Contains("BuildScopePredicateAsync", controller);
        Assert.Contains("RequiresLiveAuthorization", controller);

        var invoiceAllocation = ReadSource("src", "ERP.Application", "Services",
            "SupplierPaymentInvoiceAllocationService.cs");
        Assert.Contains("PurchaseInvoiceConcurrencyRules.LockInvoiceRowAsync", invoiceAllocation);
        Assert.Contains("PurchaseInvoiceConcurrencyRules.LockApplicationAndPaymentRowsAsync", invoiceAllocation);

        var orderAllocation = ReadSource("src", "ERP.Application", "Services", "SupplierPaymentAllocationService.cs");
        Assert.Contains("PurchaseInvoiceConcurrencyRules.LockPurchaseOrderRowsAsync", orderAllocation);

        var orderCancellation = ReadSource("src", "ERP.Application", "Services", "PurchaseOrderCancellationRules.cs");
        Assert.Contains("GlobalLockOrderText", orderCancellation);
        Assert.Contains("PurchaseOrders WITH (UPDLOCK, HOLDLOCK)", ReadSource("src", "ERP.Api", "Controllers",
            "PurchaseOrderController.cs"));
    }

    [Fact]
    public void Global_lock_order_is_documented_once_across_callers()
    {
        Assert.Contains("唯一全局锁序", PurchaseInvoiceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("来源采购订单行", PurchaseInvoiceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("供应商采购发票行", PurchaseInvoiceConcurrencyRules.GlobalLockOrderText);
        Assert.Contains("付款单行", PurchaseInvoiceConcurrencyRules.GlobalLockOrderText);

        Assert.Contains("唯一全局锁序", SupplierPaymentLifecycleRules.GlobalLockOrderText);
        Assert.Contains("唯一全局锁序", PurchaseOrderCancellationRules.GlobalLockOrderText);

        // 锁序解析：去重 + 只保留正整数 + Id 升序
        Assert.Equal(new long[] { 3, 5, 9 }, PurchaseInvoiceConcurrencyRules.MergeOrderLockIds(
            new long[] { 9, 5, 3, 5, 0, -2 }));
        Assert.Empty(PurchaseInvoiceConcurrencyRules.MergeOrderLockIds(null));
    }
}
