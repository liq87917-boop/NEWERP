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
/// ERP-488「付款单 → 供应商采购发票」付款发票引用（分摊）证据入口（
/// <c>api/supplier-payment-invoice-allocations</c>）实时授权单元测试。
/// <para>以真实 <see cref="DefaultHttpContext"/> 身份驱动真实
/// <see cref="SupplierPaymentInvoiceAllocationController"/>：空路径与已赋值路径必须给出完全一致的判定；
/// 缺失 / 为零 / 未知 / 已删除身份一律未认证（2000），禁用 / 无菜单 / 被撤销既有「付款单」（<c>payment</c>）菜单
/// 一律权限不足（2002），受限业务员越界付款单 / 来源 / 发票一律受控非披露拒绝，且全部先于任何计数、候选、快照与写入；
/// 被拒调用零引用行 / 付款单 / 发票 / 状态 / 号码改动。</para>
/// <para>登记 / 作废<strong>不再存在</strong>「无 HTTP 请求管线且无身份」或「空请求路径且无身份」的无授权服务直调旁路：
/// 完全未绑定请求（<c>HttpContext</c> 为 null）同样 fail closed。全部使用隔离内存测试数据（<see cref="TestDbFactory"/>），
/// 只播种既有菜单 / 角色授权与真实启用身份，绝不新增生产授权，也不提供任何测试专用开关。
/// 真实隔离 SQL 用例见
/// <c>ERP.IntegrationTests/SupplierPaymentInvoiceEntryAuthorizationSqlServerTests.cs</c>。</para>
/// </summary>
public class SupplierPaymentInvoiceEntryAuthorizationTests
{
    private const string AllocationPath = "/api/supplier-payment-invoice-allocations";

    // ==================== 0. 测试脚手架（隔离测试数据，绝不新增生产授权） ====================

    /// <summary>补缺失的既有菜单行（与 <c>SeedData.Menus</c> 同源编码 / 文案），绝不改写既有菜单语义。</summary>
    private static SysMenu EnsurePaymentMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted
            && m.MenuCode == SupplierPaymentLifecycleRules.RequiredMenuCode);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = SupplierPaymentLifecycleRules.RequiredMenuCode,
            MenuName = SupplierPaymentLifecycleRules.RequiredMenuText,
            Path = "/finance/payment",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种启用账号 + 真实角色授权：<paramref name="privileged"/> = 系统内置角色口径（不受客户范围限制）；
    /// 否则为受限业务员口径（须另行调用 <see cref="SeedSalesman"/> 映射员工编码与本人客户）。
    /// </summary>
    private static long SeedOperator(
        ErpDbContext db, bool paymentMenu = true, UserStatus status = UserStatus.Enabled, bool privileged = true)
    {
        var role = new SysRole
        {
            RoleCode = $"SpiaEntry-{Guid.NewGuid():N}",
            RoleName = "付款发票引用入口测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"spia-entry-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "付款发票引用入口测试账号",
            Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (paymentMenu)
        {
            var menu = EnsurePaymentMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user.Id;
    }

    /// <summary>把账号映射为受限业务员（登录名 = 员工编码，ERP-097 权威映射），并把给定客户分配给它。</summary>
    private static void SeedSalesman(ErpDbContext db, long userId, params long[] ownedCustomerIds)
    {
        var user = db.SysUsers.Single(u => u.Id == userId);
        var employee = new BaseEmployee
        {
            EmployeeCode = user.UserName,
            EmployeeName = user.DisplayName,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        foreach (var customerId in ownedCustomerIds)
            db.BaseCustomers.Single(c => c.Id == customerId).EmpId = employee.Id;
        db.SaveChanges();
    }

    /// <summary>撤销指定账号全部菜单授权（保留角色关联，模拟既有授权被撤销）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name, int status = 1)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinancePaymentApply SeedPaymentApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m, Currency currency = Currency.CNY)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo, ApplyDate = new DateTime(2026, 9, 1), CustomerId = customerId,
            Amount = amount, Currency = currency, Status = DocumentStatus.Approved
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment SeedScopedPayment(
        ErpDbContext db, string paymentNo, long supplierId, long paymentApplyId,
        decimal amount = 1000m, Currency currency = Currency.CNY, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo, PaymentDate = new DateTime(2026, 9, 10), SupplierId = supplierId,
            PaymentApplyId = paymentApplyId, Amount = amount, Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer, Status = DocumentStatus.Approved, IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal grossAmount = 1000m,
        string currency = "CNY", int status = PurchaseInvoiceRules.StatusRecorded, bool deleted = false)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(string.Empty),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            SupplierCode = "S" + supplierId,
            SupplierName = "供应商" + supplierId,
            Currency = currency,
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    // ==================== 控制器工厂与断言助手 ====================

    /// <summary>
    /// 绑定真实 HTTP 身份（可空 = 缺身份）的付款发票引用控制器；<paramref name="path"/> 为 null 时刻意保持
    /// 空 <c>Request.Path</c>，用于证明授权判定与请求形状无关。
    /// </summary>
    private static SupplierPaymentInvoiceAllocationController AllocationController(
        ErpDbContext db, long? userId, string? path, string? userName = null)
    {
        var claims = new List<Claim>(Claims(userId));
        if (userName is not null) claims.Add(new Claim(ClaimTypes.Name, userName));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (path is not null) http.Request.Path = path;
        return new SupplierPaymentInvoiceAllocationController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>未绑定任何请求上下文的纯进程内直调（外部请求无法构造）。</summary>
    private static SupplierPaymentInvoiceAllocationController InProcessController(ErpDbContext db)
        => new(db);

    private static Claim[] Claims(long? userId)
        => userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        Assert.NotNull(response.Data);
        return response.Data!;
    }

    private static async Task<BusinessException> AssertDenied(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", Path.Combine(segments))));

    private static SupplierPaymentInvoiceAllocationSaveDto AllocateDto(long paymentId, long invoiceId, decimal amount)
        => new()
        {
            PaymentId = paymentId,
            PurchaseInvoiceId = invoiceId,
            AllocatedAmount = amount,
            Remark = "ERP488"
        };

    // ==================== 1. 控制器源码契约：授权门与请求形状无关、无兜底 ====================

    [Fact]
    public void Controller_gate_has_no_request_shape_or_in_process_waiver()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers",
            "SupplierPaymentInvoiceAllocationController.cs");

        // 不再读取 Request.Path；无匿名 / 角色白名单 / 环境变量 / 内存库开关兜底。
        Assert.DoesNotContain("Request.Path", controller);
        Assert.DoesNotContain("RequiresLiveAuthorization", controller);
        Assert.DoesNotContain("AllowAnonymous", controller);
        Assert.DoesNotContain("[Authorize(Roles", controller);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", controller);
        Assert.DoesNotContain("UseInMemoryDatabase", controller);
        Assert.Contains("EnsureMenuAuthorizedAsync", controller);

        // 登记 / 作废不再有无授权的服务直调旁路，全部走授权服务重载。
        Assert.Contains("SupplierPaymentInvoiceAllocationService.CreateAuthorizedAsync", controller);
        Assert.Contains("SupplierPaymentInvoiceAllocationService.VoidAuthorizedAsync", controller);
        Assert.DoesNotContain("SupplierPaymentInvoiceAllocationService.CreateAsync", controller);
        Assert.DoesNotContain("SupplierPaymentInvoiceAllocationService.VoidAsync", controller);

        // 口径文案与实现同源（接口 / 文档 / 测试共用）。
        Assert.Contains("未认证", SupplierPaymentLifecycleRules.InvoiceAllocationPathIndependenceText);
        Assert.Contains("权限不足", SupplierPaymentLifecycleRules.InvoiceAllocationPathIndependenceText);
        Assert.Contains("无授权的服务直调", SupplierPaymentLifecycleRules.InvoiceAllocationPathIndependenceText);
        Assert.Contains("Request.Path", SupplierPaymentLifecycleRules.PathIndependenceText);
    }

    // ==================== 2. 逐入口一致判定助手 ====================

    private static async Task AssertAllRoutesAsync(
        ErpDbContext db, long? userId, string? path, long paymentId, long invoiceId, int code)
    {
        var ctl = AllocationController(db, userId, path);
        Assert.Equal(code, (await TryAsync(() => ctl.GetPaged(new SupplierPaymentInvoiceAllocationQuery()))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.PaymentCandidates(null, null))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.PaymentSummary(paymentId))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.AllocationsForPayment(paymentId))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.InvoiceCandidates(paymentId, null))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.InvoiceSummary(invoiceId))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.AllocationsForInvoice(invoiceId))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.GetById(1L))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.Create(AllocateDto(paymentId, invoiceId, 10m)))).Code);
        Assert.Equal(code, (await TryAsync(() => ctl.Void(1L,
            new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "越权" }))).Code);
    }

    private static async Task<(bool Success, int Code, string Error)> TryAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            var result = await action();
            return (result is OkObjectResult, ErrorCodes.Success, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, -1, ex.ToString());
        }
    }

    // ==================== 3. 缺 / 零 / 未知 / 已删除 / 禁用 / 撤销菜单：任何请求形状一致 ====================

    [Fact]
    public async Task Entry_requires_live_identity_and_menu_independently_of_request_shape()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S488-AUTH", "义乌档口");
        var customer = SeedCustomer(db, "C488-AUTH", "授权客户");
        var apply = SeedPaymentApply(db, "DJ-488-AUTH", customer.Id);
        var payment = SeedScopedPayment(db, "FK-488-AUTH", supplier.Id, apply.Id);
        var invoice = SeedInvoice(db, "INV-488-AUTH", supplier.Id);

        var enabledId = SeedOperator(db);

        var deletedId = SeedOperator(db);
        (await db.SysUsers.SingleAsync(u => u.Id == deletedId)).IsDeleted = true;
        await db.SaveChangesAsync();

        // 缺 / 零 / 未知 / 已删除身份：空路径 / 已赋值路径 / 其它路径全部未认证（登记 / 作废不再退回无授权服务直调）。
        foreach (var path in new string?[] { null, AllocationPath, "/api/supplier-payment-invoice-allocations/other" })
            foreach (long? identity in new long?[] { null, 0L, 987_654_321L, deletedId })
            {
                var expected = identity is null or <= 0
                    ? SupplierPaymentLifecycleRules.UnauthorizedText
                    : SupplierPaymentLifecycleRules.DeletedIdentityText;
                Assert.Equal(expected,
                    (await AssertDenied(ErrorCodes.Unauthorized,
                        () => AllocationController(db, identity, path)
                            .Create(AllocateDto(payment.Id, invoice.Id, 10m)))).Message);
                await AssertAllRoutesAsync(db, identity, path, payment.Id, invoice.Id, ErrorCodes.Unauthorized);
            }

        // 禁用账号：任何请求形状一律权限不足。
        var disabledId = SeedOperator(db);
        (await db.SysUsers.SingleAsync(u => u.Id == disabledId)).Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
        foreach (var path in new string?[] { null, AllocationPath })
        {
            Assert.Equal(SupplierPaymentLifecycleRules.DisabledIdentityText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => AllocationController(db, disabledId, path)
                        .Create(AllocateDto(payment.Id, invoice.Id, 10m)))).Message);
            await AssertAllRoutesAsync(db, disabledId, path, payment.Id, invoice.Id, ErrorCodes.Forbidden);
        }

        // 撤销既有「付款单」菜单：任何请求形状一律权限不足。
        var revokedId = SeedOperator(db);
        RevokeMenus(db, revokedId);
        foreach (var path in new string?[] { null, AllocationPath })
        {
            Assert.Equal(SupplierPaymentLifecycleRules.MenuDeniedText,
                (await AssertDenied(ErrorCodes.Forbidden,
                    () => AllocationController(db, revokedId, path)
                        .Create(AllocateDto(payment.Id, invoice.Id, 10m)))).Message);
            await AssertAllRoutesAsync(db, revokedId, path, payment.Id, invoice.Id, ErrorCodes.Forbidden);
        }

        // 授权身份在两种请求形状下都可用（既有授权未被本次改动收紧）。
        foreach (var path in new string?[] { null, AllocationPath })
            Assert.True((await TryAsync(() => AllocationController(db, enabledId, path)
                .GetPaged(new SupplierPaymentInvoiceAllocationQuery()))).Success);

        // 零写入：被拒调用不新增任何引用行，付款单 / 发票保持原值。
        Assert.Empty(await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync());
        Assert.Equal(1000m, (await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id)).Amount);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded,
            (await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id)).Status);
    }

    // ==================== 4. 完全未绑定请求上下文：登记 / 作废同样 fail closed ====================

    [Fact]
    public async Task Absent_http_context_denies_create_and_void_with_zero_mutation()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S488-CTX", "义乌档口");
        var customer = SeedCustomer(db, "C488-CTX", "上下文客户");
        var apply = SeedPaymentApply(db, "DJ-488-CTX", customer.Id);
        var payment = SeedScopedPayment(db, "FK-488-CTX", supplier.Id, apply.Id);
        var invoice = SeedInvoice(db, "INV-488-CTX", supplier.Id);
        SeedOperator(db); // 即使存在真实启用特权身份，空上下文也不得退化为任何身份。

        var ctl = InProcessController(db);

        Assert.Equal(SupplierPaymentLifecycleRules.UnauthorizedText,
            (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Create(AllocateDto(payment.Id, invoice.Id, 10m)))).Message);
        Assert.Equal(SupplierPaymentLifecycleRules.UnauthorizedText,
            (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.Void(1L, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "越权作废" }))).Message);
        Assert.Equal(ErrorCodes.Unauthorized,
            (await AssertDenied(ErrorCodes.Unauthorized,
                () => ctl.GetPaged(new SupplierPaymentInvoiceAllocationQuery()))).Code);

        Assert.Empty(await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync());
        Assert.Equal(1000m, (await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id)).Amount);
    }

    // ==================== 5. 授权身份：两种请求形状都完成既有生命周期 ====================

    [Fact]
    public async Task Permitted_identity_completes_lifecycle_on_both_request_shapes()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S488-LIFE", "义乌档口");
        var customer = SeedCustomer(db, "C488-LIFE", "生命周期客户");
        var apply = SeedPaymentApply(db, "DJ-488-LIFE", customer.Id, amount: 1000m);
        var payment = SeedScopedPayment(db, "FK-488-LIFE", supplier.Id, apply.Id, amount: 1000m);
        var invoiceA = SeedInvoice(db, "INV-488-LIFE-A", supplier.Id, 1000m);
        var invoiceB = SeedInvoice(db, "INV-488-LIFE-B", supplier.Id, 1000m);
        var userId = SeedOperator(db);

        var invoiceIds = new[] { invoiceA.Id, invoiceB.Id };
        var index = 0;
        foreach (var path in new string?[] { null, AllocationPath })
        {
            var ctl = AllocationController(db, userId, path, "入口业务员");

            // 读侧入口在授权后可用。
            Assert.True((await TryAsync(() => ctl.GetPaged(new SupplierPaymentInvoiceAllocationQuery()))).Success);
            Assert.True((await TryAsync(() => ctl.PaymentCandidates(null, null))).Success);
            Assert.True((await TryAsync(() => ctl.PaymentSummary(payment.Id))).Success);
            Assert.True((await TryAsync(() => ctl.AllocationsForPayment(payment.Id))).Success);
            Assert.True((await TryAsync(() => ctl.InvoiceCandidates(payment.Id, null))).Success);
            Assert.True((await TryAsync(() => ctl.InvoiceSummary(invoiceIds[index]))).Success);
            Assert.True((await TryAsync(() => ctl.AllocationsForInvoice(invoiceIds[index]))).Success);

            // 登记：登记人取登录账号；金额 / 币种 / 共享额度口径不变。
            var created = AssertOk<SupplierPaymentInvoiceAllocationDto>(
                await ctl.Create(AllocateDto(payment.Id, invoiceIds[index], 100m)));
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusActive, created.Status);
            Assert.Equal(100m, created.AllocatedAmount);
            Assert.Equal("入口业务员", created.RecordedBy);
            Assert.Equal(payment.Id, created.PaymentId);

            Assert.True((await TryAsync(() => ctl.GetById(created.Id))).Success);

            // 作废：保留原始金额与历史（状态改为已作废，金额不变）。
            var voided = AssertOk<SupplierPaymentInvoiceAllocationDto>(await ctl.Void(created.Id,
                new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "录错" }));
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusVoided, voided.Status);
            Assert.Equal(100m, voided.AllocatedAmount);
            Assert.Contains("录错", voided.VoidReason);

            index++;
        }

        // 作废保留历史（两行仍在、金额不变）；付款单与发票未被改写。
        var rows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(100m, r.AllocatedAmount));
        Assert.Equal(1000m, (await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id)).Amount);
        var storedInvoice = await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoiceA.Id);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, storedInvoice.Status);
        Assert.Equal(1000m, storedInvoice.GrossAmount);
    }

    // ==================== 6. 受限业务员：越界付款单非披露拒绝 + 自有范围登记 / 作废 ====================

    [Fact]
    public async Task Restricted_scope_denies_foreign_payment_and_allows_own_flow()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S488-SCOPE", "义乌档口");
        var own = SeedCustomer(db, "C488-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C488-FOREIGN", "范围外客户");
        var ownApply = SeedPaymentApply(db, "DJ-488-OWN", own.Id);
        var foreignApply = SeedPaymentApply(db, "DJ-488-FOREIGN", foreign.Id);
        var ownPayment = SeedScopedPayment(db, "FK-488-OWN", supplier.Id, ownApply.Id);
        var foreignPayment = SeedScopedPayment(db, "FK-488-FOREIGN", supplier.Id, foreignApply.Id);
        var ownInvoice = SeedInvoice(db, "INV-488-OWN", supplier.Id, 1000m);
        var foreignInvoice = SeedInvoice(db, "INV-488-FOREIGN", supplier.Id, 1000m);

        var salesmanId = SeedOperator(db, privileged: false);
        SeedSalesman(db, salesmanId, own.Id);

        // 特权账号先为范围外付款单建立一条引用行（用于证明受限账号不可见、且越权作废不改写证据）。
        var privileged = AllocationController(db, SeedOperator(db), AllocationPath);
        var foreignRow = AssertOk<SupplierPaymentInvoiceAllocationDto>(
            await privileged.Create(AllocateDto(foreignPayment.Id, foreignInvoice.Id, 100m)));

        foreach (var path in new string?[] { null, AllocationPath })
        {
            var ctl = AllocationController(db, salesmanId, path, "本人业务员");

            // 越界付款单：与「不存在」同一条不披露错误（不泄露范围外 Id / 单号 / 金额）。
            var foreignDenied = await AssertDenied(ErrorCodes.NotFound,
                () => ctl.Create(AllocateDto(foreignPayment.Id, ownInvoice.Id, 10m)));
            var missingDenied = await AssertDenied(ErrorCodes.NotFound,
                () => ctl.Create(AllocateDto(987_654_321L, ownInvoice.Id, 10m)));
            Assert.Equal(missingDenied.Message, foreignDenied.Message);
            Assert.DoesNotContain("FK-488-FOREIGN", foreignDenied.Message);

            // 越界作废：同一条不披露错误，且原证据保持有效。
            Assert.Equal(missingDenied.Message, (await AssertDenied(ErrorCodes.NotFound,
                () => ctl.Void(foreignRow.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "越权" }))).Message);
            var stored = await db.SupplierPaymentInvoiceAllocations.AsNoTracking().FirstAsync(a => a.Id == foreignRow.Id);
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusActive, stored.Status);
            Assert.Null(stored.VoidedAt);

            // 自有范围许可：登记（登记人取登录账号）→ 作废（保留原始金额与历史）。
            var created = AssertOk<SupplierPaymentInvoiceAllocationDto>(
                await ctl.Create(AllocateDto(ownPayment.Id, ownInvoice.Id, 50m)));
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusActive, created.Status);
            Assert.Equal("本人业务员", created.RecordedBy);

            var voided = AssertOk<SupplierPaymentInvoiceAllocationDto>(await ctl.Void(created.Id,
                new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "录错" }));
            Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusVoided, voided.Status);
            Assert.Equal(50m, voided.AllocatedAmount);
        }

        // 范围外证据未被改写；范围内登记 / 作废各留两行历史（含 1 条范围外有效行）。
        Assert.Equal(3, await db.SupplierPaymentInvoiceAllocations.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .CountAsync(a => a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive));
    }
}
