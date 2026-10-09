using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-413 规范销售订单（<c>api/sales-orders</c>）执行证据入口实时授权护栏单元测试。
/// <para>覆盖：受限业务员（既有「销售订单」菜单 + 客户数据范围）在本单 / 他人 / 已删除 / 不存在订单上的
/// 时间线 / 财务核对 / 进度 / 退货影响 / 收款引用证据 / 销项发票证据单张与批量汇总授权；
/// 无身份 / 已删除 / 已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单一律 fail closed；
/// 批量显式 Id 混入不可访问订单**整批拒绝**（无部分行 / 计数）；特权账号保留既有全量口径；
/// 允许派生保留既有部分出货 / 退货 / 发票 / 收款语义且与直接派生结果一致；拒绝后零写入；
/// 进程内无身份直调免授权（保持既有单元测试口径），真实 HTTP 匿名请求 fail closed。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class SalesOrderExecutionAuthorizationTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);
    private const long ProductId = 9_413_100L;

    // ==================== 0. 测试脚手架 ====================

    private static DefaultHttpContext HttpFor(long? userId, string? path = "/api/sales-orders")
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return http;
    }

    private static SalesOrderController NewController(ErpDbContext db, long? userId, string? path = "/api/sales-orders")
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = HttpFor(userId, path) };
        return controller;
    }

    private static (long UserId, long EmployeeId, SysRole Role) SeedOperator(ErpDbContext db,
        bool menu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp413-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (menu) GrantMenu(db, role.Id, SalesOrderExecutionAuthorizationRules.RequiredMenuCode);
        db.SaveChanges();
        return (user.Id, employee.Id, role);
    }

    private static SysMenu GrantMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
        return menu;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-10),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.1m,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>以受限业务员（既有「销售订单」菜单 + 本人客户）身份调用控制器动作，断言返回 Ok 并取出 DTO。</summary>
    private static async Task<T> OkDataAsync<T>(Func<Task<IActionResult>> action)
        => Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(await action()).Value).Data!;

    /// <summary>断言拒绝：同一非披露错误（NotFound + 统一文案）。</summary>
    private static async Task AssertNonDisclosingNotFoundAsync(Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.NotFoundText, ex.Message);
    }

    // ==================== 1. 受限业务员：本人订单可读（全部执行证据入口） ====================

    [Fact]
    public async Task 受限业务员_本人订单_全部执行证据入口可读()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-OWN", employeeId);
        var order = SeedOrder(db, "SO-413-OWN", own.Id);

        var ctl = NewController(db, userId);
        Assert.NotNull(await OkDataAsync<List<OrderTimelineEvent>>(() => ctl.Timeline(order.Id)));
        Assert.NotNull(await OkDataAsync<OrderFinanceReconciliationView>(() => ctl.FinanceReconciliation(order.Id)));
        Assert.NotNull(await OkDataAsync<SalesOrderProgressView>(() => ctl.Progress(order.Id)));
        Assert.NotNull(await OkDataAsync<SalesOrderReturnImpactView>(() => ctl.ReturnImpact(order.Id)));
        Assert.NotNull(await OkDataAsync<SalesOrderReceiptEvidenceDetail>(() => ctl.ReceiptEvidence(order.Id)));
        Assert.NotNull(await OkDataAsync<SalesOrderInvoiceEvidenceDetail>(() => ctl.InvoiceEvidence(order.Id)));

        var receiptBatch = await OkDataAsync<SalesOrderReceiptEvidenceBatch>(() =>
            ctl.ReceiptEvidenceSummaries(new SalesOrderReceiptEvidenceQuery { Ids = order.Id.ToString() }));
        Assert.Equal(1, receiptBatch.ItemCount);
        Assert.Equal(order.Id, receiptBatch.Items[0].SalesOrderId);

        var invoiceBatch = await OkDataAsync<SalesOrderInvoiceEvidenceBatch>(() =>
            ctl.InvoiceEvidenceSummaries(new SalesOrderInvoiceEvidenceQuery { Ids = order.Id.ToString() }));
        Assert.Equal(1, invoiceBatch.ItemCount);
        Assert.Equal(order.Id, invoiceBatch.Items[0].SalesOrderId);

        // 详情（list/detail 实时身份 + 持久化客户归属）
        Assert.Equal(order.Id, (await OkDataAsync<SalesOrder>(() => ctl.GetById(order.Id))).Id);
    }

    // ==================== 2. 受限业务员：他人 / 已删除 / 不存在订单统一非披露错误 ====================

    [Fact]
    public async Task 受限业务员_他人订单与不存在订单_单张证据返回同一非披露错误()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-A", employeeId);
        var foreign = SeedCustomer(db, "C413-B", null);
        var ownOrder = SeedOrder(db, "SO-413-A", own.Id);
        var foreignOrder = SeedOrder(db, "SO-413-B", foreign.Id);
        const long missingId = 9_413_999L;

        var ctl = NewController(db, userId);

        // 本人订单放行（对照），他人与不存在订单必须返回同一受控错误。
        Assert.NotNull(await OkDataAsync<SalesOrderProgressView>(() => ctl.Progress(ownOrder.Id)));

        await AssertNonDisclosingNotFoundAsync(() => ctl.Timeline(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.FinanceReconciliation(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReturnImpact(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidence(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidence(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(foreignOrder.Id));

        await AssertNonDisclosingNotFoundAsync(() => ctl.Timeline(missingId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.FinanceReconciliation(missingId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(missingId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReturnImpact(missingId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidence(missingId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidence(missingId));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(missingId));
    }

    [Fact]
    public async Task 受限业务员_已删除订单_与非披露错误一致()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-DEL", employeeId);
        var deleted = SeedOrder(db, "SO-413-DEL", own.Id, deleted: true);

        var ctl = NewController(db, userId);
        await AssertNonDisclosingNotFoundAsync(() => ctl.Timeline(deleted.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(deleted.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidence(deleted.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidence(deleted.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.GetById(deleted.Id));
    }

    // ==================== 3. 批量显式 Id：混入不可访问订单整批拒绝 ====================

    [Fact]
    public async Task 批量汇总_混入他人或不存在订单_整批拒绝且无部分行()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-MINE", employeeId);
        var foreign = SeedCustomer(db, "C413-FOREIGN", null);
        var ownOrder = SeedOrder(db, "SO-413-MINE", own.Id);
        var foreignOrder = SeedOrder(db, "SO-413-FOREIGN", foreign.Id);
        const long missingId = 9_413_998L;

        var ctl = NewController(db, userId);

        // 单独本人订单放行（对照）。
        await OkDataAsync<SalesOrderReceiptEvidenceBatch>(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = ownOrder.Id.ToString() }));

        // 本人 + 他人 / 本人 + 不存在：整批拒绝，绝不返回部分行。
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = $"{ownOrder.Id},{foreignOrder.Id}" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = $"{ownOrder.Id},{missingId}" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{ownOrder.Id},{foreignOrder.Id}" }));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{ownOrder.Id},{missingId}" }));
    }

    [Fact]
    public async Task 批量汇总_仅本人订单或空清单_正常返回()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-BATCH", employeeId);
        var first = SeedOrder(db, "SO-413-B1", own.Id);
        var second = SeedOrder(db, "SO-413-B2", own.Id);

        var ctl = NewController(db, userId);

        var receipt = await OkDataAsync<SalesOrderReceiptEvidenceBatch>(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery { Ids = $"{first.Id},{second.Id}" }));
        Assert.Equal(2, receipt.ItemCount);
        Assert.Equal(new[] { first.Id, second.Id }, receipt.Items.Select(i => i.SalesOrderId).ToArray());

        var invoice = await OkDataAsync<SalesOrderInvoiceEvidenceBatch>(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{first.Id},{second.Id}" }));
        Assert.Equal(2, invoice.ItemCount);

        var empty = await OkDataAsync<SalesOrderReceiptEvidenceBatch>(() => ctl.ReceiptEvidenceSummaries(
            new SalesOrderReceiptEvidenceQuery()));
        Assert.Equal(0, empty.RequestedCount);
        Assert.Empty(empty.Items);
    }

    // ==================== 4. 身份 / 菜单 fail closed ====================

    [Fact]
    public async Task 无菜单与仅导出菜单_执行证据入口_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var (noMenuUser, noMenuEmp, _) = SeedOperator(db, menu: false);
        var noMenuCustomer = SeedCustomer(db, "C413-NOMENU", noMenuEmp);
        var noMenuOrder = SeedOrder(db, "SO-413-NOMENU", noMenuCustomer.Id);

        var (exportOnlyUser, exportEmp, exportRole) = SeedOperator(db, menu: false);
        GrantMenu(db, exportRole.Id, "sales-order-export");
        var exportCustomer = SeedCustomer(db, "C413-EXP", exportEmp);
        var exportOrder = SeedOrder(db, "SO-413-EXP", exportCustomer.Id);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, noMenuUser).Progress(noMenuOrder.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex1.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.MenuDeniedText, ex1.Message);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, exportOnlyUser).Progress(exportOrder.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex2.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.MenuDeniedText, ex2.Message);
    }

    [Fact]
    public async Task 撤销菜单后下一次请求立即收敛_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, role) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-REVOKE", employeeId);
        var order = SeedOrder(db, "SO-413-REVOKE", own.Id);

        // 授权期间可读。
        Assert.NotNull(await OkDataAsync<SalesOrderProgressView>(() => NewController(db, userId).Progress(order.Id)));

        // 回收既有「销售订单」菜单授权（角色仍在）→ 下一次请求立即收敛。
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == role.Id));
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, userId).Progress(order.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.MenuDeniedText, ex.Message);
    }

    [Fact]
    public async Task 已禁用账号_执行证据入口_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db, status: UserStatus.Disabled);
        var own = SeedCustomer(db, "C413-DISABLED", employeeId);
        var order = SeedOrder(db, "SO-413-DISABLED", own.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, userId).Progress(order.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.UserDisabledText, ex.Message);
    }

    [Fact]
    public async Task 已删除账号与无身份真实请求_执行证据入口_Unauthorized()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-GONE", employeeId);
        var order = SeedOrder(db, "SO-413-GONE", own.Id);

        var user = db.SysUsers.Single(u => u.Id == userId);
        user.IsDeleted = true;
        db.SaveChanges();

        var deleted = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, userId).Progress(order.Id));
        Assert.Equal(ErrorCodes.Unauthorized, deleted.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.UserDeletedText, deleted.Message);

        // 真实匿名请求（请求管线内、无 NameIdentifier）：绝不当作匿名 / 管理员，fail closed。
        var anonymous = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, null, "/api/sales-orders/1/progress").Progress(order.Id));
        Assert.Equal(ErrorCodes.Unauthorized, anonymous.Code);
        Assert.Equal(SalesOrderExecutionAuthorizationRules.UnauthorizedText, anonymous.Message);
    }

    // ==================== 5. 特权账号与进程内直调 ====================

    [Fact]
    public async Task 特权账号_保留既有全量口径_可读他人订单证据()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        var foreign = SeedCustomer(db, "C413-PRIV-FOREIGN", null);
        var order = SeedOrder(db, "SO-413-PRIV", foreign.Id);

        var ctl = NewController(db, userId);
        Assert.NotNull(await OkDataAsync<SalesOrderProgressView>(() => ctl.Progress(order.Id)));
        Assert.NotNull(await OkDataAsync<SalesOrderReceiptEvidenceDetail>(() => ctl.ReceiptEvidence(order.Id)));
        Assert.NotNull(await OkDataAsync<SalesOrder>(() => ctl.GetById(order.Id)));
    }

    [Fact]
    public async Task 进程内无身份直调_免授权_保持既有单元测试口径()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C413-INPROC", null);
        var order = SeedOrder(db, "SO-413-INPROC", customer.Id);

        // 无 ControllerContext（既非 HTTP 请求管线，也无登录身份）：不可能由外部请求到达，执行证据入口保持既有免授权口径。
        var inProcess = new SalesOrderController(db, new DocumentNumberService(db));
        Assert.NotNull(await OkDataAsync<SalesOrderProgressView>(() => inProcess.Progress(order.Id)));

        // 详情入口沿用既有口径（历史实现即要求范围解析）：无身份直调一律 fail closed（绝不返回订单数据）。
        await Assert.ThrowsAnyAsync<Exception>(() => inProcess.GetById(order.Id));
    }

    // ==================== 6. 拒绝时零写入 ====================

    [Fact]
    public async Task 拒绝请求_零写入_不改写任何业务记录()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-ZW-OWN", employeeId);
        var foreign = SeedCustomer(db, "C413-ZW-FOREIGN", null);
        var ownOrder = SeedOrder(db, "SO-413-ZW-OWN", own.Id);
        var foreignOrder = SeedOrder(db, "SO-413-ZW-FOREIGN", foreign.Id);

        var before = new
        {
            Orders = db.SalesOrders.Count(),
            Details = db.SalesOrderDetails.Count(),
            StockOuts = db.StockOuts.Count(),
            Movements = db.StockMovements.Count(),
            Receipts = db.FinanceReceipts.Count(),
            ReceiptAllocations = db.CustomerReceiptAllocations.Count(),
            Invoices = db.CustomerSalesInvoiceEvidences.Count(),
        };
        var foreignState = db.SalesOrders.AsNoTracking().Single(o => o.Id == foreignOrder.Id);

        var ctl = NewController(db, userId);
        await AssertNonDisclosingNotFoundAsync(() => ctl.Progress(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.ReceiptEvidence(foreignOrder.Id));
        await AssertNonDisclosingNotFoundAsync(() => ctl.InvoiceEvidenceSummaries(
            new SalesOrderInvoiceEvidenceQuery { Ids = $"{ownOrder.Id},{foreignOrder.Id}" }));

        Assert.Equal(before.Orders, db.SalesOrders.Count());
        Assert.Equal(before.Details, db.SalesOrderDetails.Count());
        Assert.Equal(before.StockOuts, db.StockOuts.Count());
        Assert.Equal(before.Movements, db.StockMovements.Count());
        Assert.Equal(before.Receipts, db.FinanceReceipts.Count());
        Assert.Equal(before.ReceiptAllocations, db.CustomerReceiptAllocations.Count());
        Assert.Equal(before.Invoices, db.CustomerSalesInvoiceEvidences.Count());

        var after = db.SalesOrders.AsNoTracking().Single(o => o.Id == foreignOrder.Id);
        Assert.Equal(foreignState.Status, after.Status);
        Assert.Equal(foreignState.IsDeleted, after.IsDeleted);
        Assert.Equal(foreignState.TotalAmount, after.TotalAmount);
        Assert.Equal(0, db.SysOperationLogs.Count());
    }

    // ==================== 7. 允许派生保留既有语义且与直接派生一致 ====================

    [Fact]
    public async Task 允许派生_保留部分出货退货收款发票语义_且与直接派生一致()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C413-SEM", employeeId);
        var order = SeedOrder(db, "SO-413-SEM", own.Id);
        SeedDetail(db, order.Id, ProductId, 10m);
        var stockOut = SeedStockOut(db, "CK-413-SEM", order.Id, own.Id, DocumentStatus.Approved, (ProductId, 4m));
        SeedSalesReturn(db, "XTH-413-SEM", own.Id, stockOut.Id, ProductId, 1m);
        var receipt = SeedReceipt(db, "SK-413-SEM", own.Id, 600m, Currency.USD);
        SeedReceiptAllocation(db, 9_413_001L, receipt, order, 350m);
        var invoice = SeedInvoice(db, 9_413_002L, "INV-413-SEM", own.Id, Currency.USD, 600m);
        SeedInvoiceAllocation(db, 9_413_003L, invoice, order, 350m);

        var ctl = NewController(db, userId);

        // 部分出货 / 部分退货的既有口径不变。
        var progress = await OkDataAsync<SalesOrderProgressView>(() => ctl.Progress(order.Id));
        Assert.Equal(4m, progress.Shipment.ShippedQuantity);
        Assert.Equal(6m, progress.Shipment.OutstandingQuantity);
        Assert.Equal(SalesOrderProgress.ShipmentPartial, progress.Shipment.ShipmentStatus);

        var impact = await OkDataAsync<SalesOrderReturnImpactView>(() => ctl.ReturnImpact(order.Id));
        Assert.Equal(3m, impact.NetShippedTotal);

        // 部分收款引用 / 部分开票证据的既有口径不变。
        var receiptEvidence = await OkDataAsync<SalesOrderReceiptEvidenceDetail>(() => ctl.ReceiptEvidence(order.Id));
        Assert.Equal(350m, receiptEvidence.Summary.RecordedAllocatedAmount);
        var invoiceEvidence = await OkDataAsync<SalesOrderInvoiceEvidenceDetail>(() => ctl.InvoiceEvidence(order.Id));
        Assert.Equal(350m, invoiceEvidence.Summary.RecordedInvoicedAmount);

        // 与直接派生服务结果逐项一致（授权不改写派生口径与响应 DTO 契约）。
        Assert.Equal(receiptEvidence.Summary.RecordedAllocatedAmount,
            (await SalesOrderReceiptEvidence.ForOrderAsync(db, order.Id)).Summary.RecordedAllocatedAmount);
        Assert.Equal(invoiceEvidence.Summary.RecordedInvoicedAmount,
            (await SalesOrderInvoiceEvidence.ForOrderAsync(db, order.Id)).Summary.RecordedInvoicedAmount);
        Assert.Equal(progress.Shipment.ShippedQuantity,
            (await SalesOrderProgress.ForSalesOrderAsync(db, order.Id)).Shipment.ShippedQuantity);
        Assert.Equal(impact.NetShippedTotal,
            (await SalesOrderReturnImpact.ForOrderAsync(db, order.Id)).NetShippedTotal);
    }

    // ==================== 9. ERP-432 运营核对读取路由：实时授权 + 范围下推（交期异常 / 出货财务 / 订单收款核对） ====================

    [Fact]
    public async Task 受限业务员_三个运营核对读取路由_只返回范围内客户数据()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C432-OWN", employeeId);
        var foreign = SeedCustomer(db, "C432-FOREIGN", null);
        SeedOrder(db, "SO-432-OWN", own.Id);
        SeedOrder(db, "SO-432-FOREIGN", foreign.Id);

        var ctl = NewController(db, userId);

        var delivery = await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()));
        Assert.Equal(1, delivery.Total);
        Assert.Equal(own.Id, Assert.Single(delivery.Items).CustomerId);

        var shipment = await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()));
        Assert.Equal(1, shipment.Total);
        Assert.Equal(own.Id, Assert.Single(shipment.Groups).CustomerId);

        var receipt = await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()));
        Assert.Equal(1, receipt.Total);
        Assert.Equal(own.Id, Assert.Single(receipt.Groups).CustomerId);
    }

    [Fact]
    public async Task 受限业务员_筛选范围外客户_三个运营核对读取路由返回空且无计数()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C432-SCOPE-OWN", employeeId);
        var foreign = SeedCustomer(db, "C432-SCOPE-FOREIGN", null);
        SeedOrder(db, "SO-432-SCOPE-OWN", own.Id);
        SeedOrder(db, "SO-432-SCOPE-FOREIGN", foreign.Id);

        var ctl = NewController(db, userId);

        var delivery = await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery { CustomerId = foreign.Id }));
        Assert.Equal(0, delivery.Total);
        Assert.Empty(delivery.Items);

        var shipment = await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery { CustomerId = foreign.Id }));
        Assert.Equal(0, shipment.Total);
        Assert.Empty(shipment.Groups);

        var receipt = await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery { CustomerId = foreign.Id }));
        Assert.Equal(0, receipt.Total);
        Assert.Empty(receipt.Groups);
    }

    [Fact]
    public async Task 特权账号_三个运营核对读取路由保留既有全量口径()
    {
        using var db = TestDbFactory.Create();
        var privilegedUserId = TestAuth.SeedPrivilegedUser(db);
        var first = SeedCustomer(db, "C432-PRIV-A", null);
        var second = SeedCustomer(db, "C432-PRIV-B", null);
        SeedOrder(db, "SO-432-PRIV-A", first.Id);
        SeedOrder(db, "SO-432-PRIV-B", second.Id);

        var ctl = NewController(db, privilegedUserId);

        Assert.Equal(2, (await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()))).Total);
        Assert.Equal(2, (await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()))).Total);
        Assert.Equal(2, (await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()))).Total);
    }


    [Fact]
    public async Task 身份与菜单拒绝矩阵_三个运营核对读取路由fail_closed且零写入()
    {
        using var db = TestDbFactory.Create();
        var (menuLessId, _, _) = SeedOperator(db, menu: false);
        var (exportOnlyId, _, exportOnlyRole) = SeedOperator(db, menu: false);
        GrantMenu(db, exportOnlyRole.Id, SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode);
        var (revokedId, _, revokedRole) = SeedOperator(db);
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == revokedRole.Id));
        db.SaveChanges();
        var (disabledId, _, _) = SeedOperator(db, status: UserStatus.Disabled);
        var (deletedId, _, _) = SeedOperator(db);
        var deletedUser = db.SysUsers.Single(u => u.Id == deletedId);
        deletedUser.IsDeleted = true;
        db.SaveChanges();

        var customer = SeedCustomer(db, "C432-DENY", null);
        SeedOrder(db, "SO-432-DENY", customer.Id);
        var ordersBefore = db.SalesOrders.Count();

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, SalesOrderExecutionAuthorizationRules.UnauthorizedText),
            (deletedId, ErrorCodes.Unauthorized, SalesOrderExecutionAuthorizationRules.UserDeletedText),
            (disabledId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.UserDisabledText),
            (menuLessId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.MenuDeniedText),
            (exportOnlyId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.MenuDeniedText),
            (revokedId, ErrorCodes.Forbidden, SalesOrderExecutionAuthorizationRules.MenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = NewController(db, userId);

            var delivery = await Assert.ThrowsAsync<BusinessException>(
                () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()));
            Assert.Equal(code, delivery.Code);
            Assert.Equal(text, delivery.Message);

            var shipment = await Assert.ThrowsAsync<BusinessException>(
                () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()));
            Assert.Equal(code, shipment.Code);
            Assert.Equal(text, shipment.Message);

            var receipt = await Assert.ThrowsAsync<BusinessException>(
                () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()));
            Assert.Equal(code, receipt.Code);
            Assert.Equal(text, receipt.Message);
        }

        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.Equal(0, db.SysOperationLogs.Count());
    }


    [Fact]
    public async Task 受限业务员_多客户_三个运营核对读取路由返回范围内全部客户且不含范围外客户()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var first = SeedCustomer(db, "C432-MULTI-A", employeeId);
        var second = SeedCustomer(db, "C432-MULTI-B", employeeId);
        var foreign = SeedCustomer(db, "C432-MULTI-X", null);
        var firstOrder = SeedOrder(db, "SO-432-MULTI-A", first.Id);
        var secondOrder = SeedOrder(db, "SO-432-MULTI-B", second.Id);
        SeedOrder(db, "SO-432-MULTI-X", foreign.Id);
        SeedDetail(db, firstOrder.Id, ProductId, 10m);
        SeedDetail(db, secondOrder.Id, ProductId, 6m);

        var ctl = NewController(db, userId);

        // 交期异常工作台的权威范围**集合**下推（先于计数 / 分页 / 物化）：未指定客户时返回范围内全部客户的
        // 合法订单与正确数量，绝不返回范围外客户的订单计数 / 数量。
        var delivery = await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery()));
        Assert.Equal(2, delivery.Total);
        Assert.Equal(2, delivery.Counts.Total);
        Assert.Equal(2, delivery.Items.Count);
        Assert.All(delivery.Items, i => Assert.Contains(i.CustomerId, new[] { first.Id, second.Id }));
        Assert.Equal(10m, delivery.Items.Single(i => i.CustomerId == first.Id).OrderedQuantity);
        Assert.Equal(6m, delivery.Items.Single(i => i.CustomerId == second.Id).OrderedQuantity);

        // 出货 / 财务进度与订单收款核对同样只返回范围内客户，绝不返回范围外客户。
        var shipment = await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery()));
        Assert.Equal(2, shipment.Total);
        Assert.All(shipment.Groups, g => Assert.Contains(g.CustomerId, new[] { first.Id, second.Id }));

        var receipt = await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery()));
        Assert.Equal(2, receipt.Total);
        Assert.All(receipt.Groups, g => Assert.Contains(g.CustomerId, new[] { first.Id, second.Id }));
    }

    [Fact]
    public async Task 受限业务员_显式指定范围内客户_三条运营核对读取路由只返回该客户()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var first = SeedCustomer(db, "C432-PICK-A", employeeId);
        var second = SeedCustomer(db, "C432-PICK-B", employeeId);
        var foreign = SeedCustomer(db, "C432-PICK-X", null);
        SeedOrder(db, "SO-432-PICK-A", first.Id);
        SeedOrder(db, "SO-432-PICK-B", second.Id);
        SeedOrder(db, "SO-432-PICK-X", foreign.Id);

        var ctl = NewController(db, userId);

        var delivery = await OkDataAsync<SalesOrderDeliveryExceptionReport>(
            () => ctl.DeliveryExceptions(new SalesOrderDeliveryExceptionQuery { CustomerId = first.Id }));
        Assert.Equal(1, delivery.Total);
        Assert.Equal(first.Id, Assert.Single(delivery.Items).CustomerId);

        var shipment = await OkDataAsync<SalesOrderShipmentFinanceReportView>(
            () => ctl.ShipmentFinanceReport(new SalesOrderShipmentFinanceQuery { CustomerId = first.Id }));
        Assert.Equal(1, shipment.Total);
        Assert.Equal(first.Id, Assert.Single(shipment.Groups).CustomerId);

        var receipt = await OkDataAsync<SalesOrderReceiptReconciliationReport>(
            () => ctl.ReceiptReconciliationReport(new SalesOrderReceiptReconciliationQuery { CustomerId = first.Id }));
        Assert.Equal(1, receipt.Total);
        Assert.Equal(first.Id, Assert.Single(receipt.Groups).CustomerId);
    }

    [Fact]
    public async Task 交期异常工作台_权威范围下推_多客户全返回_显式范围外为空_空范围fail_closed()
    {
        using var db = TestDbFactory.Create();
        var first = SeedCustomer(db, "C432-PUSH-A", null);
        var second = SeedCustomer(db, "C432-PUSH-B", null);
        var foreign = SeedCustomer(db, "C432-PUSH-X", null);
        var firstOrder = SeedOrder(db, "SO-432-PUSH-A", first.Id);
        var secondOrder = SeedOrder(db, "SO-432-PUSH-B", second.Id);
        var foreignOrder = SeedOrder(db, "SO-432-PUSH-X", foreign.Id);
        SeedDetail(db, firstOrder.Id, ProductId, 10m);
        SeedDetail(db, secondOrder.Id, ProductId, 6m);
        SeedDetail(db, foreignOrder.Id, ProductId, 999m);

        // 范围 = 两个分配客户：未指定客户时两户都返回（正确计数 / 数量），范围外客户不出现。
        var multi = new SalespersonDataScope
        {
            IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { first.Id, second.Id }
        };
        var scoped = await SalesOrderDeliveryExceptions.ForQueryAsync(
            db, new SalesOrderDeliveryExceptionQuery(), multi);
        Assert.Equal(2, scoped.Total);
        Assert.All(scoped.Items, i => Assert.Contains(i.CustomerId, new[] { first.Id, second.Id }));
        Assert.Equal(10m, scoped.Items.Single(i => i.CustomerId == first.Id).OrderedQuantity);
        Assert.Equal(6m, scoped.Items.Single(i => i.CustomerId == second.Id).OrderedQuantity);

        // 显式指定范围内客户：只返回该客户子集。
        var picked = await SalesOrderDeliveryExceptions.ForQueryAsync(
            db, new SalesOrderDeliveryExceptionQuery { CustomerId = second.Id }, multi);
        Assert.Equal(1, picked.Total);
        Assert.Equal(second.Id, Assert.Single(picked.Items).CustomerId);

        // 显式指定范围外客户：空集与 0 计数（不透露不可访问客户的存在性）。
        var denied = await SalesOrderDeliveryExceptions.ForQueryAsync(
            db, new SalesOrderDeliveryExceptionQuery { CustomerId = foreign.Id }, multi);
        Assert.Equal(0, denied.Total);
        Assert.Empty(denied.Items);
        Assert.Equal(0, denied.Counts.Total);

        // 空范围：一律空集 fail closed。
        var none = new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long>() };
        var empty = await SalesOrderDeliveryExceptions.ForQueryAsync(
            db, new SalesOrderDeliveryExceptionQuery(), none);
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Items);

        // scope 为 null（进程内既有两参调用 / 特权豁免）：保持既有不过滤口径。
        var unscoped = await SalesOrderDeliveryExceptions.ForQueryAsync(db, new SalesOrderDeliveryExceptionQuery());
        Assert.Equal(3, unscoped.Total);
    }

    // ==================== 8. 派生夹具（部分出货 / 退货 / 收款 / 发票证据） ====================

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m
        });
        db.SaveChanges();
    }

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long salesOrderId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            WarehouseId = 1,
            Status = status
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();

        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity
            });
        }

        db.SaveChanges();
        stockOut.TotalQuantity = db.StockOutDetails.Where(d => d.StockOutId == stockOut.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockOut;
    }

    private static void SeedSalesReturn(ErpDbContext db, string returnNo, long customerId, long sourceStockOutId,
        long productId, decimal quantity)
    {
        var salesReturn = new SalesReturn
        {
            ReturnNo = returnNo,
            ReturnDate = AsOf.AddDays(-2),
            CustomerId = customerId,
            CustomerName = "客户快照",
            WarehouseId = 1,
            SourceStockOutId = sourceStockOutId,
            TotalQuantity = quantity,
            Status = DocumentStatus.Approved
        };
        db.SalesReturns.Add(salesReturn);
        db.SaveChanges();
        db.SalesReturnDetails.Add(new SalesReturnDetail
        {
            SalesReturnId = salesReturn.Id,
            ReturnNo = returnNo,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Unit = "PCS",
            Quantity = quantity
        });
        db.SaveChanges();
    }

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static void SeedReceiptAllocation(ErpDbContext db, long id, FinanceReceipt receipt, SalesOrder order,
        decimal amount)
    {
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            Id = id,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = string.Empty,
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = receipt.CustomerId,
            CustomerCode = "C413-SNAP",
            CustomerName = "客户快照",
            AllocatedAmount = amount,
            Currency = receipt.Currency.ToString(),
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = AsOf
        });
        db.SaveChanges();
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(ErpDbContext db, long id, string invoiceNumber,
        long customerId, Currency currency, decimal grossAmount)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            Id = id,
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(invoiceNumber),
            InvoiceDate = AsOf,
            CustomerId = customerId,
            CustomerCode = "C413-SNAP",
            CustomerName = "客户快照",
            Currency = currency.ToString(),
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
            RecordedAt = AsOf
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static void SeedInvoiceAllocation(ErpDbContext db, long id, CustomerSalesInvoiceEvidence invoice,
        SalesOrder order, decimal amount)
    {
        db.CustomerSalesInvoiceAllocations.Add(new CustomerSalesInvoiceAllocation
        {
            Id = id,
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = invoice.CustomerId,
            CustomerCode = "C413-SNAP",
            CustomerName = "客户快照",
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            CreatedAt = AsOf
        });
        db.SaveChanges();
    }
}

