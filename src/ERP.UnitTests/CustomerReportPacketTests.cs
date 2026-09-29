using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-122 客户报告包预览（只读、有界、作用域化、双菜单授权）单元测试。
/// 覆盖：双菜单授权（销售订单 + 客户资料任一缺失即拒绝整个响应）、正整数客户 Id、日期 / 页大小有界校验、
/// 页码归一化、业务员数据范围（越界客户两分区都空、不泄露）、两个独立分区各自计数与原币 / 剩余证据字段、
/// 不推断跨单链接、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class CustomerReportPacketTests
{
    // ==================== 0. 测试脚手架 ====================

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

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
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

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        Currency currency = Currency.USD, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = currency,
            TotalAmount = 100
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        DateTime? invoiceDate = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = invoiceDate ?? new DateTime(2026, 8, 20),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            NetAmount = grossAmount * 0.9m,
            TaxAmount = grossAmount * 0.1m,
            GrossAmount = grossAmount,
            Status = status,
            IsDeleted = false
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency = Currency.USD)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = DocumentStatus.Approved,
            IsDeleted = false
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerSalesInvoiceCollectionAllocation SeedAllocation(
        ErpDbContext db, CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt, decimal amount)
    {
        var row = new CustomerSalesInvoiceCollectionAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = "已登记",
            InvoiceGrossAmount = invoice.GrossAmount,
            InvoiceCurrency = invoice.Currency,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            CustomerId = invoice.CustomerId,
            CustomerCode = invoice.CustomerCode,
            CustomerName = invoice.CustomerName,
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            Remark = string.Empty,
            Status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive,
            AllocatedAt = new DateTime(2026, 9, 21),
            AllocatedBy = "张三"
        };
        db.CustomerSalesInvoiceCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    /// <summary>播种同时具备「销售订单 + 客户资料」两个菜单授权的特权账号（系统内置角色），返回用户 Id。</summary>
    private static long SeedPrivilegedBothMenus(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种只具备「客户资料」菜单授权的特权账号（用于验证缺少销售订单菜单时拒绝整个响应）。</summary>
    private static long SeedPrivilegedCustomerMenuOnly(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种只具备「销售订单」菜单授权的特权账号（用于验证缺少客户资料菜单时拒绝整个响应）。</summary>
    private static long SeedPrivilegedOrderMenuOnly(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种同时具备两个菜单授权的受限业务员账号，返回（用户 Id、本人客户、他人客户）。</summary>
    private static (long UserId, long Mine, long Other) SeedRestrictedBothMenus(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Sales-{userName}");
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);

        var employee = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"{userName}-C1", "我的客户", employee.Id);
        var other = SeedCustomer(db, $"{userName}-C2", "别人的客户", employee.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static CustomerReportPacketController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new CustomerReportPacketController(
            new DynamicSalesOrderReportQuery(db),
            new DynamicReceivableReportQuery(db),
            db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return ctl;
    }

    private static CustomerReportPacketDto PacketOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<CustomerReportPacketDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 双菜单授权与独立分区 ====================

    [Fact]
    public async Task Preview_双菜单授权齐全_返回两个独立分区()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY);
        var invoice = SeedInvoice(db, "INV-1", customer.Id, 1000m, "USD");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, 400m, Currency.USD);
        SeedAllocation(db, invoice, receipt, 400m);
        var ctl = BuildController(db, uid);

        var packet = PacketOk(await ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
            PageSize = 20,
        }));

        Assert.Equal(customer.Id, packet.CustomerId);
        Assert.NotNull(packet.SalesOrders);
        Assert.NotNull(packet.ReceivableEvidence);
        Assert.Equal(2, packet.SalesOrders.Total);
        Assert.Equal(1, packet.ReceivableEvidence.Total);
        Assert.Contains(packet.SalesOrders.Columns, c => c.Key == "currency");
        Assert.Contains(packet.ReceivableEvidence.Columns, c => c.Key == "currency");
        Assert.Contains(packet.ReceivableEvidence.Columns, c => c.Key == "remainingState");
        Assert.Contains(packet.ReceivableEvidence.Columns, c => c.Key == "remainingStateText");
        Assert.Contains(packet.ReceivableEvidence.Columns, c => c.Key == "effectiveAmount");
    }

    [Fact]
    public async Task Preview_不推断跨单链接_分区字段互不串用()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD);
        SeedInvoice(db, "INV-1", customer.Id, 1000m, "USD");
        var ctl = BuildController(db, uid);

        var packet = PacketOk(await ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
            PageSize = 20,
        }));

        var orderRow = Assert.Single(packet.SalesOrders.Rows);
        var receivableRow = Assert.Single(packet.ReceivableEvidence.Rows);

        Assert.DoesNotContain("invoiceNumber", orderRow.Keys);
        Assert.DoesNotContain("remainingState", orderRow.Keys);
        Assert.DoesNotContain("effectiveAmount", orderRow.Keys);
        Assert.DoesNotContain("orderNo", receivableRow.Keys);
        Assert.DoesNotContain("totalAmount", receivableRow.Keys);
    }

    [Fact]
    public async Task Preview_缺少销售订单菜单_拒绝整个响应()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedCustomerMenuOnly(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
        }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_缺少客户资料菜单_拒绝整个响应()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedOrderMenuOnly(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedInvoice(db, "INV-1", customer.Id, 1000m);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
        }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = 1,
        }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 2. 有界校验 ====================

    [Fact]
    public async Task Preview_客户Id非正整数_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = 0,
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_日期区间倒置_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = 1,
            StartDate = new DateTime(2026, 9, 30),
            EndDate = new DateTime(2026, 9, 1),
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = 1,
            PageSize = CustomerReportPacketRules.MaxPageSize + 1,
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_页码小于1_归一到第一页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        var ctl = BuildController(db, uid);

        var packet = PacketOk(await ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
            Page = 0,
            PageSize = 20,
        }));

        Assert.Equal(1, packet.SalesOrders.Page);
        Assert.Equal(1, packet.ReceivableEvidence.Page);
    }

    // ==================== 3. 业务员数据范围（fail closed） ====================

    [Fact]
    public async Task Preview_受限业务员_越界客户_两分区都空_不泄露()
    {
        using var db = TestDbFactory.Create();
        var (uid, _, other) = SeedRestrictedBothMenus(db, "alice");
        SeedOrder(db, "SO-OTHER", other);
        SeedInvoice(db, "INV-OTHER", other, 1000m);
        var ctl = BuildController(db, uid);

        var packet = PacketOk(await ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = other,
            PageSize = 20,
        }));

        Assert.Equal(0, packet.SalesOrders.Total);
        Assert.Empty(packet.SalesOrders.Rows);
        Assert.Equal(0, packet.ReceivableEvidence.Total);
        Assert.Empty(packet.ReceivableEvidence.Rows);
    }

    // ==================== 4. 只读不写库 ====================

    [Fact]
    public async Task Preview_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        SeedInvoice(db, "INV-1", customer.Id, 1000m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new CustomerReportPacketController(
            new DynamicSalesOrderReportQuery(counting.Proxy),
            new DynamicReceivableReportQuery(counting.Proxy),
            counting.Proxy);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, uid.ToString()) };
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var packet = PacketOk(await ctl.Preview(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
            PageSize = 20,
        }));

        Assert.Equal(1, packet.SalesOrders.Total);
        Assert.Equal(1, packet.ReceivableEvidence.Total);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 5. 只读计数上下文（断言不写库） ====================

    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int WriteCalls { get; private set; }

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
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}
