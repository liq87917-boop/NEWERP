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
/// 供应商付款引用（分摊）登记单元测试（ERP-049）。覆盖：付款单 / 采购订单权威资格（存在、未删除、未取消、
/// 供应商一致、币种一致）、金额精度与大于 0、有效行合计不得超过付款单金额、同一订单同一付款单不得重复有效引用、
/// 全额 / 部分引用与未引用金额、作废保留原始值与历史（重复作废拒绝、作废后可重新登记）、
/// 付款单 / 订单软删除后历史仍可读、台账过滤与分页有界、候选金额派生（其他付款单已引用 / 剩余未覆盖）、
/// 非变更边界（付款单、采购订单、发票、库存、退税、费用均不被改写），以及模型 / 幂等结构 / 前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class SupplierPaymentAllocationTests
{
    // ==================== 0. 测试脚手架 ====================

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
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种具备既有付款单菜单的特权账号（数据范围不受限，聚焦引用证据语义）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole { RoleName = "付款引用特权角色", RoleCode = $"AllocPriv-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"alloc-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "付款引用特权用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsurePaymentMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种受限业务员账号（登录名 = 员工编码，ERP-097 权威映射；仅可见被分配客户）</summary>
    private static long SeedRestrictedSalesman(ErpDbContext db, string userName)
    {
        var role = new SysRole { RoleName = "付款引用业务员角色", RoleCode = $"AllocSales-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

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

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsurePaymentMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1
        });
        db.SaveChanges();
        return user.Id;
    }

    internal static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        // 标记为真实 HTTP 路由（Request.Path 已赋值）：缺失身份也必须实时授权并 fail closed。
        http.Request.Path = "/api/supplier-payment-allocations";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    /// <summary>默认注入具备既有付款单菜单的特权身份（未显式指定 userId 时），聚焦引用证据业务语义</summary>
    private static SupplierPaymentAllocationController BuildController(ErpDbContext db, long? userId = null)
    {
        var controller = new SupplierPaymentAllocationController(db);
        SetUser(controller, userId ?? SeedPrivilegedUser(db));
        return controller;
    }

    /// <summary>严格按给定身份（含 null = 无身份）构建控制器，用于权限边界测试</summary>
    private static SupplierPaymentAllocationController BuildIdentityController(ErpDbContext db, long? userId)
    {
        var controller = new SupplierPaymentAllocationController(db);
        SetUser(controller, userId);
        return controller;
    }

    private static BaseSupplier SeedSupplier(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = code,
            SupplierName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m, bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount = 1000m,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 9, 10),
            SupplierId = supplierId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static SupplierPaymentAllocationSaveDto AllocateDto(
        long paymentId, long orderId, decimal amount, string remark = "")
        => new()
        {
            PaymentId = paymentId,
            PurchaseOrderId = orderId,
            AllocatedAmount = amount,
            Remark = remark
        };

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static async Task<SupplierPaymentAllocationDto> CreateAllocationAsync(
        SupplierPaymentAllocationController controller, long paymentId, long orderId,
        decimal amount, string remark = "")
        => AssertOk<SupplierPaymentAllocationDto>(
            await controller.Create(AllocateDto(paymentId, orderId, amount, remark)));

    // ==================== 0b. 实时身份 / 菜单 / 客户数据范围（ERP-433） ====================

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

    /// <summary>受限业务员账号 + 自有客户（EmpId = 本人）/ 他人客户 的完整范围脚手架</summary>
    private static (long SalesmanId, BaseCustomer Own, BaseCustomer Foreign, BaseSupplier Supplier) SeedScope(
        ErpDbContext db, string userName)
    {
        var salesmanId = SeedRestrictedSalesman(db, userName);
        var employeeId = db.BaseEmployees.First(e => e.EmployeeCode == userName).Id;
        var own = SeedCustomer(db, $"{userName}-OWN", "自有客户", employeeId);
        var foreign = SeedCustomer(db, $"{userName}-FOREIGN", "他人客户");
        var supplier = SeedSupplier(db, $"{userName}-SUP", "义乌档口");
        return (salesmanId, own, foreign, supplier);
    }

    [Fact]
    public async Task 缺失已删除身份按未认证拒绝_禁用账号与撤销菜单按权限不足拒绝且不读不写()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 1000m);
        var order = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);

        var disabledId = SeedPrivilegedUser(db);
        var disabled = await db.SysUsers.FirstAsync(u => u.Id == disabledId);
        disabled.Status = UserStatus.Disabled;

        var revokedId = SeedPrivilegedUser(db);
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted
                     && db.SysUserRoles.Any(ur => ur.RoleId == rm.RoleId && ur.UserId == revokedId)).ToList())
            grant.IsDeleted = true;

        var deletedId = SeedPrivilegedUser(db);
        var deleted = await db.SysUsers.FirstAsync(u => u.Id == deletedId);
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        // 缺失 / 非法 / 已删除身份 → 未认证（Unauthorized）
        foreach (long? userId in new long?[] { null, 0, deletedId })
        {
            var controller = BuildIdentityController(db, userId);
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetPaged(new SupplierPaymentAllocationQuery()));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.PaymentCandidates(null, null));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetById(1));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.Create(AllocateDto(payment.Id, order.Id, 10m)));
            await AssertBusinessAsync(ErrorCodes.Unauthorized,
                () => controller.Void(1, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }));
        }

        // 禁用 / 撤销菜单 → 权限不足（Forbidden）
        foreach (var userId in new[] { disabledId, revokedId })
        {
            var controller = BuildIdentityController(db, userId);
            await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetPaged(new SupplierPaymentAllocationQuery()));
            await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.PaymentCandidates(null, null));
            await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetById(1));
            await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(AllocateDto(payment.Id, order.Id, 10m)));
            await AssertBusinessAsync(ErrorCodes.Forbidden,
                () => controller.Void(1, new SupplierPaymentAllocationVoidRequest { Reason = "作废" }));
        }

        // 被拒请求一律不落任何引用行
        Assert.Empty(await db.SupplierPaymentAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 越范围付款单与不存在付款单一律同一条不披露错误_且台账候选不泄露()
    {
        using var db = TestDbFactory.Create();
        var (salesmanId, own, foreign, supplier) = SeedScope(db, "erp433-scope");
        var ownApply = SeedPaymentApply(db, "DJ-OWN", own.Id);
        var foreignApply = SeedPaymentApply(db, "DJ-FOREIGN", foreign.Id);
        var ownPayment = SeedScopedPayment(db, "FK-OWN", supplier.Id, ownApply.Id);
        var foreignPayment = SeedScopedPayment(db, "FK-FOREIGN", supplier.Id, foreignApply.Id);
        var ownOrder = SeedOrder(db, "PO-OWN", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        var foreignOrder = SeedOrder(db, "PO-FOREIGN", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);

        var controller = BuildController(db, salesmanId);

        // 自有范围内付款单：允许登记（被许可的登记生命周期）
        var ownRow = await CreateAllocationAsync(controller, ownPayment.Id, ownOrder.Id, 100m);
        Assert.True(ownRow.IsActive);

        // 越范围付款单：与不存在付款单同一条不披露错误（NotFound），绝不暴露范围外 Id / 金额 / 单号
        var foreignDenied = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(foreignPayment.Id, foreignOrder.Id, 100m)));
        var missingDenied = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(987_654_321L, foreignOrder.Id, 100m)));
        Assert.Equal(missingDenied.Message, foreignDenied.Message);
        Assert.DoesNotContain(foreignPayment.Id.ToString(), foreignDenied.Message);
        Assert.DoesNotContain("FK-FOREIGN", foreignDenied.Message);

        // 越范围付款单的汇总 / 明细 / 候选同样不披露（同一错误）
        Assert.Equal(missingDenied.Message, (await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.PaymentSummary(foreignPayment.Id))).Message);
        Assert.Equal(missingDenied.Message, (await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.AllocationsForPayment(foreignPayment.Id))).Message);
        Assert.Equal(missingDenied.Message, (await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.OrderCandidates(foreignPayment.Id, null))).Message);

        var candidates = AssertOk<List<SupplierPaymentAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(null, null));
        Assert.Contains(candidates, c => c.PaymentId == ownPayment.Id);
        Assert.DoesNotContain(candidates, c => c.PaymentId == foreignPayment.Id);

        // 台账只列出范围内付款单的引用行
        var ledger = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery()));
        Assert.Equal(1, ledger.Total);
        Assert.Equal(ownPayment.Id, ledger.Items.Single().PaymentId);

        // 被拒写入不落行：仅 1 条范围内有效引用行
        Assert.Single(await db.SupplierPaymentAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 越范围或已删除付款单_拒绝登记与作废_且零证据变更()
    {
        using var db = TestDbFactory.Create();
        var (salesmanId, own, foreign, supplier) = SeedScope(db, "erp433-void");
        var ownApply = SeedPaymentApply(db, "DJ-OWN", own.Id);
        var foreignApply = SeedPaymentApply(db, "DJ-FOREIGN", foreign.Id);
        var ownPayment = SeedScopedPayment(db, "FK-OWN", supplier.Id, ownApply.Id);
        var foreignPayment = SeedScopedPayment(db, "FK-FOREIGN", supplier.Id, foreignApply.Id);
        var deletionPayment = SeedScopedPayment(db, "FK-DEL", supplier.Id, ownApply.Id);
        var ownOrder = SeedOrder(db, "PO-OWN", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        var foreignOrder = SeedOrder(db, "PO-FOREIGN", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        var deletionOrder = SeedOrder(db, "PO-DEL", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);

        // 特权账号先建立三条有效引用行（含越范围与即将软删除的付款单），用于验证越权作废不改写证据
        var privileged = BuildController(db);
        var ownRow = await CreateAllocationAsync(privileged, ownPayment.Id, ownOrder.Id, 100m);
        var foreignRow = await CreateAllocationAsync(privileged, foreignPayment.Id, foreignOrder.Id, 100m);
        var deletionRow = await CreateAllocationAsync(privileged, deletionPayment.Id, deletionOrder.Id, 100m);

        var deletion = await db.FinancePayments.FirstAsync(p => p.Id == deletionPayment.Id);
        deletion.IsDeleted = true;
        await db.SaveChangesAsync();

        var controller = BuildController(db, salesmanId);

        // 自有范围内：作废允许（被许可的生命周期）
        var voided = AssertOk<SupplierPaymentAllocationDto>(
            await controller.Void(ownRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "录错" }));
        Assert.True(voided.IsVoided);

        // 越范围 / 已删除付款单：作废按不存在拒绝；登记同样按不存在拒绝
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Void(foreignRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "越权作废" }));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Void(deletionRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "已删除作废" }));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(foreignPayment.Id, ownOrder.Id, 10m)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(deletionPayment.Id, deletionOrder.Id, 10m)));

        // 证据零变更：被拒行仍为有效、无作废时间、金额不变；总行数不变
        var storedForeign = await db.SupplierPaymentAllocations.AsNoTracking().FirstAsync(a => a.Id == foreignRow.Id);
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive, storedForeign.Status);
        Assert.Null(storedForeign.VoidedAt);
        Assert.Equal(100m, storedForeign.AllocatedAmount);
        var storedDeletion = await db.SupplierPaymentAllocations.AsNoTracking().FirstAsync(a => a.Id == deletionRow.Id);
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive, storedDeletion.Status);
        Assert.Null(storedDeletion.VoidedAt);
        Assert.Equal(3, (await db.SupplierPaymentAllocations.AsNoTracking().ToListAsync()).Count);
    }

    [Fact]
    public void 控制器与服务复用既有付款护栏且无匿名或管理员回退()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SupplierPaymentAllocationController.cs"));
        Assert.Contains("SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync", controller);
        Assert.DoesNotContain("AllowAnonymous", controller);
        Assert.DoesNotContain("[Authorize(Roles", controller);

        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "SupplierPaymentAllocationService.cs"));
        Assert.Contains("SupplierPaymentLifecycleRules.ResolveAuthorizedScopeAsync", service);
        Assert.Contains("SupplierPaymentLifecycleRules.ResolveAuthorizedPaymentAsync", service);
        Assert.Contains("SupplierPaymentLifecycleRules.EnsurePaymentInScopeAsync", service);

        var rules = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "SupplierPaymentLifecycleRules.cs"));
        Assert.Contains("AllocationNotFoundText", rules);
        Assert.Contains("EnsureMenuAuthorizedAsync", rules);
    }

    // ==================== 1. 登记：部分 / 全额引用与快照 ====================

    [Fact]
    public async Task 部分引用两张订单_保留服务端快照且不改写付款单与采购订单()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var orderA = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 600m);
        var orderB = SeedOrder(db, "PO-B", supplier.Id, Currency.CNY, DocumentStatus.Approved, 500m);
        var controller = BuildController(db);

        var first = await CreateAllocationAsync(controller, payment.Id, orderA.Id, 300m, "首款");
        var second = await CreateAllocationAsync(controller, payment.Id, orderB.Id, 200.005m, "尾款");

        Assert.Equal(payment.Id, first.PaymentId);
        Assert.Equal("FK20260910", first.PaymentNo);
        Assert.Equal(new DateTime(2026, 9, 10), first.PaymentDate);
        Assert.Equal((int)DocumentStatus.Approved, first.PaymentStatus);
        Assert.Equal("已审核", first.PaymentStatusText);
        Assert.Equal(1000m, first.PaymentAmount);
        Assert.Equal(orderA.Id, first.PurchaseOrderId);
        Assert.Equal("PO-A", first.OrderNo);
        Assert.Equal("CNY", first.Currency);
        Assert.Equal("CNY", first.OrderCurrency);
        Assert.Equal(2, first.AmountDecimals);
        Assert.Equal(300m, first.AllocatedAmount);
        Assert.Equal("首款", first.Remark);
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive, first.Status);
        Assert.Equal("有效", first.StatusText);
        Assert.True(first.IsActive);
        Assert.False(first.IsVoided);
        Assert.Equal("S001", first.SupplierCode);
        Assert.Equal("义乌档口", first.SupplierName);
        Assert.True(first.PaymentAvailable);
        Assert.True(first.OrderAvailable);
        Assert.Contains("引用证据", first.BoundaryText);

        // 金额按币种精度取整（0.5 进位，2 位小数）
        Assert.Equal(200.01m, second.AllocatedAmount);

        // 付款单与采购订单的既有字段完全不被改写
        var storedPayment = await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(1000m, storedPayment.Amount);
        Assert.Equal(Currency.CNY, storedPayment.Currency);
        Assert.Equal(DocumentStatus.Approved, storedPayment.Status);
        Assert.Equal(payment.UpdatedAt, storedPayment.UpdatedAt);
        foreach (var order in await db.PurchaseOrders.AsNoTracking().ToListAsync())
        {
            Assert.Equal(order.Id == orderA.Id ? 600m : 500m, order.TotalAmount);
            Assert.Equal("未到货", order.ArrivalProgress);
            Assert.Equal("未结算", order.SettlementProgress);
            Assert.Equal(DocumentStatus.Approved, order.Status);
        }
    }

    [Fact]
    public async Task 全额引用后付款单汇总显示未引用为0且状态为已全额引用()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 500m);
        var orderA = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 200m);
        var orderB = SeedOrder(db, "PO-B", supplier.Id, Currency.CNY, DocumentStatus.Approved, 300m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, payment.Id, orderA.Id, 200m);
        await CreateAllocationAsync(controller, payment.Id, orderB.Id, 300m);

        var summary = AssertOk<SupplierPaymentAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));

        Assert.Equal(500m, summary.PaymentAmount);
        Assert.Equal(500m, summary.AllocatedAmount);
        Assert.Equal(0m, summary.UnallocatedAmount);
        Assert.Equal(2, summary.AllocationCount);
        Assert.Equal(0, summary.VoidedCount);
        Assert.Equal(SupplierPaymentAllocationRules.LinkageFullyAllocated, summary.LinkageStatus);
        Assert.Contains("已全额引用", summary.LinkageText);
        Assert.Contains("不是银行付款凭证", summary.BoundaryText);
        Assert.Equal(2, summary.Allocations.Count);
        Assert.True(summary.PaymentAvailable);
    }

    [Fact]
    public async Task 未引用付款单汇总为未引用且列表无编码差异()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-EMPTY", supplier.Id, 120m);
        var controller = BuildController(db);

        var summary = AssertOk<SupplierPaymentAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));

        Assert.Equal(0m, summary.AllocatedAmount);
        Assert.Equal(120m, summary.UnallocatedAmount);
        Assert.Equal(0, summary.AllocationCount);
        Assert.Equal(SupplierPaymentAllocationRules.LinkageUnallocated, summary.LinkageStatus);
        Assert.Contains("未被引用", summary.LinkageText);
        Assert.Empty(summary.Allocations);

        var list = AssertOk<List<SupplierPaymentAllocationDto>>(
            await controller.AllocationsForPayment(payment.Id));
        Assert.Empty(list);
    }

    // ==================== 2. 权威资格与金额校验 ====================

    [Fact]
    public async Task 供应商或币种不一致的订单被拒绝且绝不换算或改派()
    {
        using var db = TestDbFactory.Create();
        var supplierA = SeedSupplier(db, "S001", "义乌档口");
        var supplierB = SeedSupplier(db, "S002", "广州工厂");
        var payment = SeedPayment(db, "FK-1", supplierA.Id, 1000m, Currency.CNY);
        var otherSupplierOrder = SeedOrder(db, "PO-OTHER", supplierB.Id, Currency.CNY);
        var otherCurrencyOrder = SeedOrder(db, "PO-USD", supplierA.Id, Currency.USD);
        var controller = BuildController(db);

        var supplierConflict = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, otherSupplierOrder.Id, 100m)));
        Assert.Contains("供应商", supplierConflict.Message);
        Assert.Contains("不一致", supplierConflict.Message);

        var currencyConflict = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, otherCurrencyOrder.Id, 100m)));
        Assert.Contains("币种", currencyConflict.Message);
        Assert.Contains("不做汇率换算", currencyConflict.Message);

        Assert.Empty(await db.SupplierPaymentAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 已取消或已删除订单与已删除付款单被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 1000m);
        var deletedPayment = SeedPayment(db, "FK-DEL", supplier.Id, 100m, Currency.CNY,
            DocumentStatus.Approved, deleted: true);
        var cancelled = SeedOrder(db, "PO-CANCEL", supplier.Id, Currency.CNY, DocumentStatus.Cancelled);
        var deleted = SeedOrder(db, "PO-DEL", supplier.Id, Currency.CNY, DocumentStatus.Approved,
            totalAmount: 100m, deleted: true);
        var controller = BuildController(db);

        var cancelledConflict = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, cancelled.Id, 100m)));
        Assert.Contains("已取消", cancelledConflict.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(payment.Id, deleted.Id, 100m)));

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(payment.Id, 999999L, 100m)));

        var deletedPaymentError = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(deletedPayment.Id, cancelled.Id, 10m)));
        Assert.Contains("不存在或已删除", deletedPaymentError.Message);
    }

    [Fact]
    public async Task 引用金额合计不得超过付款单金额并给出已引用与本次金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 400m);
        var orderA = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 900m);
        var orderB = SeedOrder(db, "PO-B", supplier.Id, Currency.CNY, DocumentStatus.Approved, 900m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, payment.Id, orderA.Id, 250m);

        var over = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, orderB.Id, 150.01m)));
        Assert.Contains("超过付款单金额", over.Message);
        Assert.Contains("已引用 250", over.Message);

        // 恰好等于付款金额边界可以登记（250 + 150 = 400）
        var boundary = await CreateAllocationAsync(controller, payment.Id, orderB.Id, 150m);
        Assert.Equal(150m, boundary.AllocatedAmount);
    }

    [Fact]
    public async Task 同一订单同一付款单重复有效引用被拒绝_作废后可重新登记()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 1000m);
        var order = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var first = await CreateAllocationAsync(controller, payment.Id, order.Id, 100m);

        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(AllocateDto(payment.Id, order.Id, 50m)));
        Assert.Contains("只能有一条有效引用行", duplicate.Message);

        // 作废原行后可以重新登记同一订单的有效引用（已作废行不占用有效额度）
        var voided = AssertOk<SupplierPaymentAllocationDto>(
            await controller.Void(first.Id, new SupplierPaymentAllocationVoidRequest { Reason = "金额录错" }));
        Assert.True(voided.IsVoided);

        var again = await CreateAllocationAsync(controller, payment.Id, order.Id, 80m);
        Assert.True(again.IsActive);
        Assert.Equal(80m, again.AllocatedAmount);

        // 已作废历史仍然保留（不物理删除）
        var all = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentId == payment.Id).ToListAsync();
        Assert.Equal(2, all.Count);
        var historical = all.Single(a => a.Status == SupplierPaymentAllocationRules.StatusVoided);
        Assert.Equal(100m, historical.AllocatedAmount);
        Assert.Equal("金额录错", historical.VoidReason);
    }

    [Fact]
    public async Task 引用金额必须为正且按币种精度取整_未知币种付款单拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-JPY", supplier.Id, 1000m, Currency.JPY);
        var order = SeedOrder(db, "PO-JPY", supplier.Id, Currency.JPY, DocumentStatus.Approved, 2000m);
        var zeroCurrencyPayment = SeedPayment(db, "FK-BAD", supplier.Id, 100m, (Currency)99);
        var controller = BuildController(db);

        var jpy = await CreateAllocationAsync(controller, payment.Id, order.Id, 100.6m);
        Assert.Equal(101m, jpy.AllocatedAmount);
        Assert.Equal(0, jpy.AmountDecimals);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(payment.Id, order.Id, 0m)));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(0L, order.Id, 10m)));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(payment.Id, 0L, 10m)));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(zeroCurrencyPayment.Id, order.Id, 10m)));
    }

    [Fact]
    public async Task 备注与作废原因有界且必填()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 1000m);
        var order = SeedOrder(db, "PO-A", supplier.Id);
        var controller = BuildController(db);

        var overlong = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(payment.Id, order.Id, 10m,
                new string('x', SupplierPaymentAllocationRules.MaxRemarkLength + 1))));
        Assert.Contains("备注长度", overlong.Message);

        var row = await CreateAllocationAsync(controller, payment.Id, order.Id, 10m);

        var noReason = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(row.Id, new SupplierPaymentAllocationVoidRequest { Reason = "  " }));
        Assert.Contains("作废原因", noReason.Message);

        var repeat = AssertOk<SupplierPaymentAllocationDto>(
            await controller.Void(row.Id, new SupplierPaymentAllocationVoidRequest { Reason = "选错订单" }));
        Assert.Equal("选错订单", repeat.VoidReason);

        var again = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(row.Id, new SupplierPaymentAllocationVoidRequest { Reason = "再次作废" }));
        Assert.Contains("不能重复作废", again.Message);
    }

    // ==================== 3. 历史可读与作废后汇总 ====================

    [Fact]
    public async Task 付款单或订单软删除后历史引用仍可读并显式标注()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 1000m);
        var order = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 100m);
        var controller = BuildController(db);

        var row = await CreateAllocationAsync(controller, payment.Id, order.Id, 100m);

        var storedPayment = await db.FinancePayments.FirstAsync(p => p.Id == payment.Id);
        storedPayment.IsDeleted = true;
        var storedOrder = await db.PurchaseOrders.FirstAsync(o => o.Id == order.Id);
        storedOrder.IsDeleted = true;
        await db.SaveChangesAsync();

        var detail = AssertOk<SupplierPaymentAllocationDto>(await controller.GetById(row.Id));
        Assert.Equal("FK-1", detail.PaymentNo);
        Assert.Equal("PO-A", detail.OrderNo);
        Assert.Equal(100m, detail.AllocatedAmount);
        Assert.False(detail.PaymentAvailable);
        Assert.False(detail.OrderAvailable);
        Assert.Contains("已删除", detail.PaymentAvailabilityText);
        Assert.Contains("已删除", detail.OrderAvailabilityText);

        // 台账仍能列出（历史证据保留可读）
        var list = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery()));
        Assert.Single(list.Items);

        // 已删除付款单不能再登记新引用
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(payment.Id, order.Id, 1m)));
    }

    [Fact]
    public async Task 已取消订单的历史引用可读但不再作为可引用候选()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 1000m);
        var order = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 100m);
        var controller = BuildController(db);

        var row = await CreateAllocationAsync(controller, payment.Id, order.Id, 100m);

        var stored = await db.PurchaseOrders.FirstAsync(o => o.Id == order.Id);
        stored.Status = DocumentStatus.Cancelled;
        await db.SaveChangesAsync();

        var detail = AssertOk<SupplierPaymentAllocationDto>(await controller.GetById(row.Id));
        Assert.True(detail.OrderAvailable);
        Assert.Contains("已取消", detail.OrderAvailabilityText);

        var candidates = AssertOk<List<SupplierPaymentAllocationOrderCandidateDto>>(
            await controller.OrderCandidates(payment.Id, null));
        var candidate = Assert.Single(candidates);
        Assert.True(candidate.Cancelled);
        Assert.False(candidate.Eligible);
        Assert.Contains("已取消", candidate.EligibilityText);
    }

    [Fact]
    public async Task 作废后付款单汇总不再计入且历史行单独计数()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 500m);
        var orderA = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 500m);
        var orderB = SeedOrder(db, "PO-B", supplier.Id, Currency.CNY, DocumentStatus.Approved, 500m);
        var controller = BuildController(db);

        var first = await CreateAllocationAsync(controller, payment.Id, orderA.Id, 300m);
        await CreateAllocationAsync(controller, payment.Id, orderB.Id, 100m);

        AssertOk<SupplierPaymentAllocationDto>(
            await controller.Void(first.Id, new SupplierPaymentAllocationVoidRequest { Reason = "重复登记" }));

        var summary = AssertOk<SupplierPaymentAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));
        Assert.Equal(100m, summary.AllocatedAmount);
        Assert.Equal(400m, summary.UnallocatedAmount);
        Assert.Equal(1, summary.AllocationCount);
        Assert.Equal(1, summary.VoidedCount);
        Assert.Equal(SupplierPaymentAllocationRules.LinkagePartial, summary.LinkageStatus);
        Assert.Contains("部分引用", summary.LinkageText);

        // 作废后释放的额度可以重新引用（100 + 400 = 500）
        var again = await CreateAllocationAsync(controller, payment.Id, orderA.Id, 400m);
        Assert.True(again.IsActive);

        // 只作废行筛选
        var voidedOnly = AssertOk<List<SupplierPaymentAllocationDto>>(
            await controller.AllocationsForPayment(payment.Id, SupplierPaymentAllocationRules.StatusVoided));
        Assert.Single(voidedOnly);
        Assert.True(voidedOnly[0].IsVoided);
    }

    // ==================== 4. 台账过滤、分页有界与候选派生 ====================

    [Fact]
    public async Task 台账按状态币种订单与关键字过滤_分页有界且默认分页上限截断()
    {
        using var db = TestDbFactory.Create();
        var supplierA = SeedSupplier(db, "S001", "义乌档口");
        var supplierB = SeedSupplier(db, "S002", "广州工厂");
        var paymentCny = SeedPayment(db, "FK-CNY", supplierA.Id, 5000m, Currency.CNY);
        var paymentUsd = SeedPayment(db, "FK-USD", supplierB.Id, 5000m, Currency.USD);
        var orderCny = SeedOrder(db, "PO-CNY", supplierA.Id, Currency.CNY, DocumentStatus.Approved, 5000m);
        var orderUsd = SeedOrder(db, "PO-USD", supplierB.Id, Currency.USD, DocumentStatus.Approved, 5000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, paymentCny.Id, orderCny.Id, 100m);
        var usdRow = await CreateAllocationAsync(controller, paymentUsd.Id, orderUsd.Id, 200m);
        AssertOk<SupplierPaymentAllocationDto>(
            await controller.Void(usdRow.Id, new SupplierPaymentAllocationVoidRequest { Reason = "作废测试" }));

        var all = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery()));
        Assert.Equal(2, all.Total);
        Assert.Equal(1, all.Page);
        Assert.Equal(SupplierPaymentAllocationQuery.DefaultPageSize, all.PageSize);

        var activeOnly = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery
            {
                Status = SupplierPaymentAllocationRules.StatusActive
            }));
        Assert.Equal(1, activeOnly.Total);
        Assert.Equal(100m, activeOnly.Items[0].AllocatedAmount);

        var byOrder = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery { PurchaseOrderId = orderUsd.Id }));
        Assert.Equal(1, byOrder.Total);
        Assert.Equal("FK-USD", byOrder.Items[0].PaymentNo);

        var bySupplierAndCurrency = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery
            {
                SupplierId = supplierA.Id,
                Currency = " cny "
            }));
        Assert.Equal(1, bySupplierAndCurrency.Total);
        Assert.Equal("CNY", bySupplierAndCurrency.Items[0].Currency);

        var byKeyword = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery { Keyword = "PO-USD" }));
        Assert.Equal(1, byKeyword.Total);

        var paged = AssertOk<PagedResult<SupplierPaymentAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentAllocationQuery { Page = 2, PageSize = 1 }));
        Assert.Equal(2, paged.Total);
        Assert.Equal(2, paged.Page);
        Assert.Single(paged.Items);

        // 未知状态 / 未知币种 / 超长关键字一律拒绝（不静默忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SupplierPaymentAllocationQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SupplierPaymentAllocationQuery { Currency = "RUB" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SupplierPaymentAllocationQuery { Keyword = new string('k', 101) }));
    }

    [Fact]
    public async Task 付款单候选只列未删除付款单并派生已引用未引用与资格()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var openPayment = SeedPayment(db, "FK-OPEN", supplier.Id, 500m);
        var fullPayment = SeedPayment(db, "FK-FULL", supplier.Id, 100m);
        var deletedPayment = SeedPayment(db, "FK-DEL", supplier.Id, 100m, Currency.CNY,
            DocumentStatus.Approved, deleted: true);
        var orderA = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 500m);
        var orderB = SeedOrder(db, "PO-B", supplier.Id, Currency.CNY, DocumentStatus.Approved, 500m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, openPayment.Id, orderA.Id, 200m);
        await CreateAllocationAsync(controller, fullPayment.Id, orderB.Id, 100m);

        var candidates = AssertOk<List<SupplierPaymentAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(null, null));

        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.PaymentId == deletedPayment.Id);

        var full = candidates.Single(c => c.PaymentId == fullPayment.Id);
        Assert.Equal(100m, full.PaymentAmount);
        Assert.Equal(100m, full.AllocatedAmount);
        Assert.Equal(0m, full.UnallocatedAmount);
        Assert.False(full.Eligible);
        Assert.Contains("占满", full.EligibilityText);

        var open = candidates.Single(c => c.PaymentId == openPayment.Id);
        Assert.Equal(200m, open.AllocatedAmount);
        Assert.Equal(300m, open.UnallocatedAmount);
        Assert.Equal(1, open.AllocationCount);
        Assert.True(open.Eligible);
        Assert.Equal("S001", open.SupplierCode);
        Assert.Contains("剩余可引用", open.EligibilityText);

        var keyword = AssertOk<List<SupplierPaymentAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(supplier.Id, "FULL"));
        Assert.Single(keyword);
        Assert.Equal(fullPayment.Id, keyword[0].PaymentId);
    }

    [Fact]
    public async Task 订单候选只列同供应商同币种并派生其他付款单已引用与剩余未覆盖()
    {
        using var db = TestDbFactory.Create();
        var supplierA = SeedSupplier(db, "S001", "义乌档口");
        var supplierB = SeedSupplier(db, "S002", "广州工厂");
        var payment = SeedPayment(db, "FK-1", supplierA.Id, 5000m, Currency.CNY);
        var otherPayment = SeedPayment(db, "FK-2", supplierA.Id, 5000m, Currency.CNY);
        var orderA = SeedOrder(db, "PO-A", supplierA.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        var orderB = SeedOrder(db, "PO-B", supplierA.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        SeedOrder(db, "PO-OTHER-SUPPLIER", supplierB.Id, Currency.CNY, DocumentStatus.Approved, 1000m);
        SeedOrder(db, "PO-USD", supplierA.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        SeedOrder(db, "PO-DELETED", supplierA.Id, Currency.CNY, DocumentStatus.Approved, 1000m, deleted: true);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, payment.Id, orderA.Id, 200m);
        await CreateAllocationAsync(controller, otherPayment.Id, orderA.Id, 300m);
        await CreateAllocationAsync(controller, otherPayment.Id, orderB.Id, 400m);

        var candidates = AssertOk<List<SupplierPaymentAllocationOrderCandidateDto>>(
            await controller.OrderCandidates(payment.Id, null));

        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.OrderNo == "PO-OTHER-SUPPLIER");
        Assert.DoesNotContain(candidates, c => c.OrderNo == "PO-USD");
        Assert.DoesNotContain(candidates, c => c.OrderNo == "PO-DELETED");

        var first = candidates.Single(c => c.OrderNo == "PO-A");
        Assert.Equal(1000m, first.OrderedAmount);
        Assert.Equal(200m, first.AllocatedByThisPayment);
        Assert.Equal(300m, first.AllocatedByOtherPayments);
        Assert.Equal(500m, first.RemainingUnallocatedAmount);
        Assert.True(first.Eligible);
        Assert.Equal("CNY", first.Currency);
        Assert.Equal("S001", first.SupplierCode);

        var second = candidates.Single(c => c.OrderNo == "PO-B");
        Assert.Equal(0m, second.AllocatedByThisPayment);
        Assert.Equal(400m, second.AllocatedByOtherPayments);
        Assert.Equal(600m, second.RemainingUnallocatedAmount);

        // 关键字只匹配采购单号 / 合同号（不按相似度猜测）
        var filtered = AssertOk<List<SupplierPaymentAllocationOrderCandidateDto>>(
            await controller.OrderCandidates(payment.Id, "PO-B"));
        Assert.Single(filtered);
        Assert.Equal("PO-B", filtered[0].OrderNo);

        // 其他付款单的引用行不影响本单额度上限：本单仍可引用到付款金额上限
        var row = await CreateAllocationAsync(controller, payment.Id, orderB.Id, 600m);
        Assert.Equal(600m, row.AllocatedAmount);
    }

    [Fact]
    public async Task 没有可引用付款金额的付款单候选不可引用()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var zeroPayment = SeedPayment(db, "FK-ZERO", supplier.Id, 0m);
        var order = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 100m);
        var controller = BuildController(db);

        var candidates = AssertOk<List<SupplierPaymentAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(null, null));
        var zero = Assert.Single(candidates);
        Assert.False(zero.Eligible);
        Assert.Contains("没有可引用的付款金额", zero.EligibilityText);

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(zeroPayment.Id, order.Id, 1m)));
    }

    // ==================== 5. 非变更边界 ====================

    [Fact]
    public async Task 登记与作废不改写付款单订单发票库存退税费用与供应商记录()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, 900m);
        var order = SeedOrder(db, "PO-A", supplier.Id, Currency.CNY, DocumentStatus.Approved, 700m);
        var controller = BuildController(db);

        var paymentBefore = await db.FinancePayments.AsNoTracking().SingleAsync();
        var orderBefore = await db.PurchaseOrders.AsNoTracking().SingleAsync();
        var supplierBefore = await db.BaseSuppliers.AsNoTracking().SingleAsync();

        var countsBefore = new[]
        {
            await db.BaseTaxRefunds.CountAsync(),
            await db.FinanceExpenses.CountAsync(),
            await db.PurchaseInvoices.CountAsync(),
            await db.PurchaseInvoiceAllocations.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.FinancePayments.CountAsync(),
            await db.PurchaseOrders.CountAsync()
        };

        var row = await CreateAllocationAsync(controller, payment.Id, order.Id, 250m, "预付款引用");
        AssertOk<SupplierPaymentAllocationDto>(
            await controller.Void(row.Id, new SupplierPaymentAllocationVoidRequest { Reason = "订单选错" }));

        var paymentAfter = await db.FinancePayments.AsNoTracking().SingleAsync();
        Assert.Equal(paymentBefore.PaymentNo, paymentAfter.PaymentNo);
        Assert.Equal(paymentBefore.PaymentDate, paymentAfter.PaymentDate);
        Assert.Equal(paymentBefore.SupplierId, paymentAfter.SupplierId);
        Assert.Equal(paymentBefore.Amount, paymentAfter.Amount);
        Assert.Equal(paymentBefore.Currency, paymentAfter.Currency);
        Assert.Equal(paymentBefore.PaymentMethod, paymentAfter.PaymentMethod);
        Assert.Equal(paymentBefore.BankAccount, paymentAfter.BankAccount);
        Assert.Equal(paymentBefore.Status, paymentAfter.Status);
        Assert.Equal(paymentBefore.Remark, paymentAfter.Remark);
        Assert.Equal(paymentBefore.IsDeleted, paymentAfter.IsDeleted);

        var orderAfter = await db.PurchaseOrders.AsNoTracking().SingleAsync();
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(orderBefore.Status, orderAfter.Status);
        Assert.Equal(orderBefore.ArrivalProgress, orderAfter.ArrivalProgress);
        Assert.Equal(orderBefore.SettlementProgress, orderAfter.SettlementProgress);
        Assert.Equal(orderBefore.SupplierConfirmedDate, orderAfter.SupplierConfirmedDate);

        var supplierAfter = await db.BaseSuppliers.AsNoTracking().SingleAsync();
        Assert.Equal(supplierBefore.Status, supplierAfter.Status);
        Assert.Equal(supplierBefore.SettlementMethod, supplierAfter.SettlementMethod);
        Assert.Equal(supplierBefore.Remark, supplierAfter.Remark);

        // 发票 / 库存流水 / 退税 / 费用记录不受影响，也没有任何请求期回填
        Assert.Equal(countsBefore, new[]
        {
            await db.BaseTaxRefunds.CountAsync(),
            await db.FinanceExpenses.CountAsync(),
            await db.PurchaseInvoices.CountAsync(),
            await db.PurchaseInvoiceAllocations.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.FinancePayments.CountAsync(),
            await db.PurchaseOrders.CountAsync()
        });

        // 引用行保留（作废不是删除）
        Assert.Single(await db.SupplierPaymentAllocations.AsNoTracking().ToListAsync());
    }

    // ==================== 6. 纯规则 ====================

    [Fact]
    public void 纯规则_状态币种金额精度与文案()
    {
        Assert.Equal("有效", SupplierPaymentAllocationRules.StatusText(SupplierPaymentAllocationRules.StatusActive));
        Assert.Equal("已作废", SupplierPaymentAllocationRules.StatusText(SupplierPaymentAllocationRules.StatusVoided));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.StatusText(9));
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive,
            SupplierPaymentAllocationRules.NormalizeStatusFilter(SupplierPaymentAllocationRules.StatusActive));
        Assert.Null(SupplierPaymentAllocationRules.NormalizeStatusFilter(null));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeStatusFilter(7));

        Assert.Equal("USD", SupplierPaymentAllocationRules.NormalizeCurrencyStrict(" usd "));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeCurrencyStrict("RUB"));
        Assert.Equal(100.46m, SupplierPaymentAllocationRules.NormalizeAllocationAmount(100.455m, "CNY"));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeAllocationAmount(0.004m, "CNY"));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeAllocationAmount(-1m, "CNY"));
        Assert.Equal(101m, SupplierPaymentAllocationRules.NormalizeAllocationAmount(100.5m, "JPY"));
        Assert.Equal(500.00m, SupplierPaymentAllocationRules.AuthoritativePaymentAmount(500m, "USD"));

        Assert.Equal(SupplierPaymentAllocationRules.LinkageUnallocated,
            SupplierPaymentAllocationRules.LinkageStatusOf(100m, 0m));
        Assert.Equal(SupplierPaymentAllocationRules.LinkagePartial,
            SupplierPaymentAllocationRules.LinkageStatusOf(100m, 50m));
        Assert.Equal(SupplierPaymentAllocationRules.LinkageFullyAllocated,
            SupplierPaymentAllocationRules.LinkageStatusOf(100m, 100m));
        Assert.Contains("未被引用", SupplierPaymentAllocationRules.LinkageText(100m, 0m, 0, "CNY"));
        Assert.Contains("部分引用", SupplierPaymentAllocationRules.LinkageText(100m, 50m, 1, "CNY"));
        Assert.Contains("已全额引用", SupplierPaymentAllocationRules.LinkageText(100m, 100m, 2, "CNY"));

        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeVoidReason(" "));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeRemark(
            new string('x', SupplierPaymentAllocationRules.MaxRemarkLength + 1)));
        Assert.Throws<BusinessException>(() => SupplierPaymentAllocationRules.NormalizeKeyword(
            new string('k', 101)));

        // 边界声明必须明确「不是付款凭证 / 不是核销 / 不是税务 / 不是余额」，且明确「不会真的付款」
        Assert.Contains("不是银行付款凭证", SupplierPaymentAllocationRules.BoundaryText);
        Assert.Contains("不是应付账款核销", SupplierPaymentAllocationRules.BoundaryText);
        Assert.Contains("不是税务", SupplierPaymentAllocationRules.BoundaryText);
        Assert.Contains("不是供应商余额", SupplierPaymentAllocationRules.BoundaryText);
        Assert.Contains("不会真的付款", SupplierPaymentAllocationRules.BoundaryText);
        Assert.Contains("有效行合计不得超过付款单金额", SupplierPaymentAllocationRules.RuleText);
        Assert.Contains("不会按单号相似度", SupplierPaymentAllocationRules.RuleText);

        Assert.Equal("待提交", SupplierPaymentAllocationRules.PaymentStatusText((int)DocumentStatus.Pending));
        Assert.Equal("已审核", SupplierPaymentAllocationRules.PaymentStatusText((int)DocumentStatus.Approved));
        Assert.Contains("未知", SupplierPaymentAllocationRules.PaymentStatusText(42));
        Assert.Equal("已取消", SupplierPaymentAllocationRules.OrderStatusText((int)DocumentStatus.Cancelled));

        Assert.Contains("已删除", SupplierPaymentAllocationRules.PaymentAvailabilityText(null));
        Assert.False(SupplierPaymentAllocationRules.IsPaymentSelectable(new FinancePayment { IsDeleted = true }));
        Assert.True(SupplierPaymentAllocationRules.IsPaymentSelectable(new FinancePayment()));
        Assert.Contains("已停用", SupplierPaymentAllocationRules.SupplierAvailabilityText(
            new BaseSupplier { Status = 0 }));

        // 币种精度与装柜费用分摊 / 供应商发票共用同一权威实现，系统中不存在第二套取整规则
        Assert.Equal(ContainerExpenseAllocationRules.PrecisionOf("jpy"), CurrencyAmountRules.PrecisionOf("JPY"));
        Assert.Equal(PurchaseInvoiceRules.NormalizeCurrencyStrict("eur"),
            SupplierPaymentAllocationRules.NormalizeCurrencyStrict("EUR"));
        Assert.Equal(Enum.GetNames(typeof(Currency)), SupplierPaymentAllocationRules.SupportedCurrencies);
    }

    // ==================== 7. 模型 / 幂等结构 / 前端接线契约 ====================

    [Fact]
    public void 模型配置_精度长度过滤唯一索引且刻意不建外键()
    {
        using var db = TestDbFactory.Create();

        var allocation = db.Model.FindEntityType(typeof(SupplierPaymentAllocation));
        Assert.NotNull(allocation);
        Assert.Equal(2, allocation!.FindProperty(nameof(SupplierPaymentAllocation.AllocatedAmount))!.GetScale());
        Assert.Equal(2, allocation.FindProperty(nameof(SupplierPaymentAllocation.PaymentAmount))!.GetScale());
        Assert.Equal(50, allocation.FindProperty(nameof(SupplierPaymentAllocation.PaymentNo))!.GetMaxLength());
        Assert.Equal(30, allocation.FindProperty(nameof(SupplierPaymentAllocation.PaymentStatusText))!.GetMaxLength());
        Assert.Equal(50, allocation.FindProperty(nameof(SupplierPaymentAllocation.OrderNo))!.GetMaxLength());
        Assert.Equal(20, allocation.FindProperty(nameof(SupplierPaymentAllocation.Currency))!.GetMaxLength());
        Assert.Equal(200, allocation.FindProperty(nameof(SupplierPaymentAllocation.SupplierName))!.GetMaxLength());
        Assert.Equal(500, allocation.FindProperty(nameof(SupplierPaymentAllocation.Remark))!.GetMaxLength());
        Assert.Equal(500, allocation.FindProperty(nameof(SupplierPaymentAllocation.VoidReason))!.GetMaxLength());

        var paymentOrder = Assert.Single(allocation.GetIndexes(), i => i.IsUnique);
        Assert.Equal("UX_SupplierPaymentAllocations_PaymentOrder", paymentOrder.GetDatabaseName());
        Assert.Equal(2, paymentOrder.Properties.Count);
        Assert.Contains("IsDeleted = 0", paymentOrder.GetFilter());
        Assert.Contains("Status <> 2", paymentOrder.GetFilter());
        Assert.Contains(allocation.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_SupplierPaymentAllocations_PurchaseOrderId");
        Assert.Contains(allocation.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_SupplierPaymentAllocations_Status_AllocatedAt");

        // 刻意不建任何外键（付款单只做软删除；采购订单 / 供应商可能被软删除 / 取消 / 改名）
        Assert.Empty(allocation.GetForeignKeys());

        // 付款单与采购订单侧不新增导航属性（既有单据结构与读取口径完全不变）
        var paymentType = db.Model.FindEntityType(typeof(FinancePayment));
        Assert.NotNull(paymentType);
        Assert.DoesNotContain(paymentType!.GetNavigations(),
            n => n.ClrType == typeof(SupplierPaymentAllocation));
        var orderType = db.Model.FindEntityType(typeof(PurchaseOrder));
        Assert.NotNull(orderType);
        Assert.DoesNotContain(orderType!.GetNavigations(),
            n => n.ClrType == typeof(SupplierPaymentAllocation));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或资金语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.SupplierPaymentAllocations') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.SupplierPaymentAllocations", script);
        Assert.Contains("PaymentAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 1", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_SupplierPaymentAllocations_PaymentOrder", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);
        Assert.Contains("CREATE INDEX IX_SupplierPaymentAllocations_PurchaseOrderId", script);

        // 不建任何外键（付款单 / 采购订单 / 供应商侧都不被本段引用或改写）
        Assert.DoesNotContain("FK_SupplierPaymentAllocations", script);
        Assert.DoesNotContain("ALTER TABLE db_owner.FinancePayment", script);

        var start = script.IndexOf("// 34. 供应商付款引用登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/supplier-payment-allocations.js", index);

        var docModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("openSupplierPaymentAllocationRegister", docModules);

        var financeModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-finance.js"));
        Assert.Contains("openSupplierPaymentAllocationRegister()", financeModules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "supplier-payment-allocations.js"));
        Assert.Contains("async function openSupplierPaymentAllocationRegister", js);
        Assert.Contains("'/api/supplier-payment-allocations'", js);
        Assert.Contains("'/api/supplier-payment-allocations?'", js);
        Assert.Contains("/summary", js);
        Assert.Contains("/order-candidates?", js);
        Assert.Contains("/void", js);
        Assert.Contains("spaCreateAllocation", js);
        Assert.Contains("不会真的付款", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SupplierPaymentAllocationController.cs"));
        Assert.Contains("[Route(\"api/supplier-payment-allocations\")]", controller);
        Assert.Contains("payments/{paymentId:long}/summary", controller);
        Assert.Contains("payments/{paymentId:long}/allocations", controller);
        Assert.Contains("payments/{paymentId:long}/order-candidates", controller);
        Assert.Contains("{id:long}/void", controller);
    }
}
