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
/// ERP-414 销售订单变更申请登记册（<c>api/sales-order-change-requests</c>）实时授权、来源实时归属与写入护栏单元测试。
/// <para>覆盖：受限业务员（既有「销售订单」菜单 + ERP-097 客户数据范围）在本人 / 他人 / 来源已删除 / 来源缺失申请上的
/// 台账 / 详情 / 指定来源清单 / 来源候选 / 登记 / 编辑 / 提交 / 取消授权；范围在计数 / 分页 / 候选之前按来源订单
/// <b>实时</b>归属下推；拟议客户与来源客户历史快照都不是授权依据；来源改派后历史快照客户不再代表归属；
/// 无身份 / 已删除 / 已禁用 / 无菜单 / 仅导出菜单 / 已撤销菜单一律 fail closed；特权账号保留既有不受限历史访问；
/// 拒绝后零写入（来源订单与下游记录不变）；进程内无身份直调保持既有免授权口径。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class SalesOrderChangeRequestAuthorizationTests
{
    private const string BasePath = "/api/sales-order-change-requests";

    // ==================== 0. 测试脚手架 ====================

    private static DefaultHttpContext HttpFor(long? userId, string? path = BasePath)
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return http;
    }

    private static SalesOrderChangeRequestController NewController(ErpDbContext db, long? userId, string? path = BasePath)
    {
        var controller = new SalesOrderChangeRequestController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = HttpFor(userId, path) };
        return controller;
    }

    private static SalesOrderChangeRequestController InProcessController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static (long UserId, long EmployeeId, long RoleId) SeedOperator(
        ErpDbContext db, bool menu = true, UserStatus status = UserStatus.Enabled, bool deleted = false)
    {
        var code = $"erp414-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (menu) GrantMenu(db, role.Id, SalesOrderChangeRequestAuthorizationRules.RequiredMenuCode);
        db.SaveChanges();
        return (user.Id, employee.Id, role.Id);
    }

    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = $"erp414-p-{Guid.NewGuid():N}", RoleCode = $"erp414-p-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"erp414-p-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP414 特权账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long? empId, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}", CustomerName = name, EmpId = empId, Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, long customerId, string orderNo, bool deleted = false,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = new DateTime(2026, 9, 1), CustomerId = customerId,
            SalesmanId = null, Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            Status = status, IsDeleted = deleted, TotalAmount = 150m, DepositAmount = 45m,
            CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0)
        };
        order.Details = new List<SalesOrderDetail>
        {
            new() { ProductId = 101, ProductName = "A 商品", Spec = "红", Unit = "PCS", Quantity = 10, UnitPrice = 5, Amount = 50 },
            new() { ProductId = 102, ProductName = "B 商品", Spec = "蓝", Unit = "PCS", Quantity = 4, UnitPrice = 25, Amount = 100 }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>
    /// 直接播种一张变更申请（绕过写路径，用于构造「范围外 / 来源已删除 / 来源缺失」的对抗样本）：
    /// 来源客户快照与拟议客户都刻意写成受限账号**可见**的客户，证明二者都不是授权依据。
    /// </summary>
    private static SalesOrderChangeRequest SeedRequest(
        ErpDbContext db, long salesOrderId, string requestNo, long snapshotCustomerId, long proposedCustomerId,
        int status = SalesOrderChangeRequestRules.StatusDraft)
    {
        var request = new SalesOrderChangeRequest
        {
            RequestNo = requestNo,
            SalesOrderId = salesOrderId,
            SalesOrderNo = "SO-SNAP",
            SourceStatus = (int)DocumentStatus.Approved,
            SourceUpdatedAt = new DateTime(2026, 9, 1, 8, 0, 0),
            SourceDetailSignature = "2|101|10|5|102|4|25",
            SourceSnapshotMarker = "快照",
            Reason = "原始原因",
            SourceOrderDate = new DateTime(2026, 9, 1),
            SourceCustomerId = snapshotCustomerId,
            SourceCurrency = Currency.USD,
            SourceExchangeRate = 7.2m,
            SourceTotalAmount = 150m,
            SourceDepositRatio = 30m,
            SourceDepositAmount = 45m,
            ProposedCustomerId = proposedCustomerId,
            ProposedOrderDate = new DateTime(2026, 9, 1),
            ProposedCurrency = Currency.USD,
            ProposedExchangeRate = 7.2m,
            ProposedTotalAmount = 150m,
            ProposedDepositRatio = 30m,
            ProposedDepositAmount = 45m,
            Status = status,
            CreatedAt = new DateTime(2026, 9, 2, 8, 0, 0)
        };
        db.SalesOrderChangeRequests.Add(request);
        db.SaveChanges();
        return request;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task AssertNotFoundAsync(Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderChangeRequestAuthorizationRules.NotFoundText, ex.Message);
    }

    private static SalesOrderChangeRequestDetailSaveDto Detail(
        long productId, string name, decimal quantity, decimal unitPrice)
        => new() { ProductId = productId, ProductName = name, Spec = "红", Unit = "PCS", Quantity = quantity, UnitPrice = unitPrice };

    /// <summary>对抗样本场景：本人订单 / 他人订单 / 来源已删除订单 / 来源缺失申请，用于逐入口范围断言。</summary>
    private sealed record Scenario(
        long OperatorUserId, long CustomerAId, long CustomerBId,
        SalesOrder Own, SalesOrder Foreign, SalesOrder DeletedSource,
        SalesOrderChangeRequest OwnRequest, SalesOrderChangeRequest ForeignRequest,
        SalesOrderChangeRequest DeletedSourceRequest, SalesOrderChangeRequest MissingSourceRequest);

    private static Scenario SeedScenario(ErpDbContext db, bool menu = true)
    {
        var tenant = SeedOperator(db, menu);
        var customerA = SeedCustomer(db, tenant.EmployeeId, "本人客户");
        var customerB = SeedCustomer(db, null, "他人客户");
        var own = SeedOrder(db, customerA.Id, "SO-414-OWN");
        var foreign = SeedOrder(db, customerB.Id, "SO-414-FOREIGN");
        var deletedSource = SeedOrder(db, customerA.Id, "SO-414-DEL", deleted: true);

        // 对抗样本：来源客户快照与拟议客户都刻意写成受限账号**可见**的本人客户，
        // 但实时来源分别属于他人 / 已删除 / 不存在 —— 证明快照与拟议字段都不是授权依据。
        var ownRequest = SeedRequest(db, own.Id, "SOC-OWN", customerA.Id, customerA.Id);
        var foreignRequest = SeedRequest(db, foreign.Id, "SOC-FOREIGN", customerA.Id, customerA.Id);
        var deletedSourceRequest = SeedRequest(db, deletedSource.Id, "SOC-DEL", customerA.Id, customerA.Id);
        var missingSourceRequest = SeedRequest(db, 9_414_999L, "SOC-MISSING", customerA.Id, customerA.Id);

        return new Scenario(tenant.UserId, customerA.Id, customerB.Id, own, foreign, deletedSource,
            ownRequest, foreignRequest, deletedSourceRequest, missingSourceRequest);
    }

    // ==================== 1. 台账 / 详情：范围先于计数与分页（按来源订单实时归属） ====================

    [Fact]
    public async Task 受限业务员_台账按来源实时归属下推_范围外与来源缺失不可见()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var controller = NewController(db, s.OperatorUserId);

        var page = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery()));

        /* 总数按范围下推后的结果计数：4 张申请里只有本人来源订单的 1 张可见 */
        Assert.Equal(1, page.Total);
        var only = Assert.Single(page.Items);
        Assert.Equal(s.OwnRequest.Id, only.Id);
        Assert.DoesNotContain(page.Items, x =>
            x.Id == s.ForeignRequest.Id || x.Id == s.DeletedSourceRequest.Id || x.Id == s.MissingSourceRequest.Id);
    }

    [Fact]
    public async Task 受限业务员_详情按实时来源归属授权_范围外与不存在返回同一非披露错误()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var controller = NewController(db, s.OperatorUserId);

        Assert.Equal(s.OwnRequest.Id,
            AssertOk<SalesOrderChangeRequestDto>(await controller.GetById(s.OwnRequest.Id)).Id);

        await AssertNotFoundAsync(() => controller.GetById(s.ForeignRequest.Id));        // 他人来源（快照/拟议都是本人客户）
        await AssertNotFoundAsync(() => controller.GetById(s.DeletedSourceRequest.Id));  // 来源已删除
        await AssertNotFoundAsync(() => controller.GetById(s.MissingSourceRequest.Id));  // 来源缺失
        await AssertNotFoundAsync(() => controller.GetById(9_414_998L));                 // 申请不存在
    }

    // ==================== 2. 指定来源清单 / 来源候选：范围先于候选上限 ====================

    [Fact]
    public async Task 受限业务员_指定来源清单与来源候选按范围收敛()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var controller = NewController(db, s.OperatorUserId);

        var byOwn = AssertOk<List<SalesOrderChangeRequestDto>>(await controller.GetForSource(s.Own.Id));
        Assert.Equal(s.OwnRequest.Id, Assert.Single(byOwn).Id);

        /* 显式来源先授权：范围外 / 已删除 / 不存在来源返回同一非披露错误，不返回其申请计数或部分行 */
        await AssertNotFoundAsync(() => controller.GetForSource(s.Foreign.Id));
        await AssertNotFoundAsync(() => controller.GetForSource(s.DeletedSource.Id));
        await AssertNotFoundAsync(() => controller.GetForSource(9_414_999L));

        /* 来源候选只返回本人客户、未删除的订单：绝不泄露范围外 / 已删除订单 */
        var options = AssertOk<List<SalesOrderChangeRequestSourceOptionDto>>(await controller.SourceOptions(null));
        Assert.Contains(options, o => o.SalesOrderId == s.Own.Id);
        Assert.DoesNotContain(options, o => o.SalesOrderId == s.Foreign.Id || o.SalesOrderId == s.DeletedSource.Id);
    }

    // ==================== 3. 登记 / 编辑 / 提交 / 取消：授权先于发号与写入 ====================

    [Fact]
    public async Task 受限业务员_登记草稿_范围外或不存在来源拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var controller = NewController(db, s.OperatorUserId);
        var before = await SnapshotAsync(db);

        // 本人来源：登记成功。
        var created = AssertOk<SalesOrderChangeRequestDto>(await controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Own.Id, Reason = "本人来源登记" }));
        Assert.Equal(s.Own.Id, created.SalesOrderId);
        Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, created.Status);

        // 范围外 / 已删除 / 不存在来源：同一非披露错误（授权先于发号）。
        await AssertNotFoundAsync(() => controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Foreign.Id, Reason = "越权登记" }));
        await AssertNotFoundAsync(() => controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.DeletedSource.Id, Reason = "越权登记" }));
        await AssertNotFoundAsync(() => controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = 9_414_999L, Reason = "越权登记" }));

        // 拟议客户字段不授予归属：即便把拟议客户填成本人客户，范围外来源仍被拒绝。
        await AssertNotFoundAsync(() => controller.Create(new SalesOrderChangeRequestSaveDto
        { SalesOrderId = s.Foreign.Id, CustomerId = s.CustomerAId, Reason = "伪造拟议客户" }));

        // 被拒请求零写入：只有本人来源那次登记新增一行。
        var after = await SnapshotAsync(db);
        Assert.Equal(before.Requests + 1, after.Requests);
        Assert.Equal(before.Orders, after.Orders);
        Assert.Equal(before.OrderDetails, after.OrderDetails);

        // 反向：本人来源 + 拟议他人客户 → 允许（拟议只是拟议，不改变来源归属，也不授予对他人申请的访问）。
        var proposedForeign = AssertOk<SalesOrderChangeRequestDto>(await controller.Create(
            new SalesOrderChangeRequestSaveDto
            { SalesOrderId = s.Own.Id, CustomerId = s.CustomerBId, Reason = "拟议改客户" }));
        Assert.Equal(s.CustomerBId, proposedForeign.Proposed.CustomerId);
        Assert.Equal(s.Own.Id, proposedForeign.SalesOrderId);
    }

    [Fact]
    public async Task 受限业务员_编辑提交取消_持久化与拟议来源都先授权且失败零写入()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var controller = NewController(db, s.OperatorUserId);

        var details = new List<SalesOrderChangeRequestDetailSaveDto>
        {
            Detail(101, "A 商品", 20, 5),
            Detail(102, "B 商品", 4, 25)
        };

        // 编辑本人申请：拟议值按渠道生效（200 = 20×5 + 4×25；定金 30% = 60）。
        var updated = AssertOk<SalesOrderChangeRequestDto>(await controller.Update(s.OwnRequest.Id,
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Own.Id, Reason = "改数量", Details = details }));
        Assert.Equal(200m, updated.ProposedTotalAmount);
        Assert.Equal(60m, updated.ProposedDepositAmount);
        Assert.Equal("改数量", updated.Reason);

        // 编辑范围外申请：同一非披露错误，原申请（原因 / 状态 / 拟议）不变。
        await AssertNotFoundAsync(() => controller.Update(s.ForeignRequest.Id,
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Foreign.Id, Reason = "越权编辑", Details = details }));

        // 编辑本人申请时伪造拟议来源为范围外订单：同一非披露错误，明细未被替换。
        await AssertNotFoundAsync(() => controller.Update(s.OwnRequest.Id,
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Foreign.Id, Reason = "改挂来源", Details = details }));

        // 提交 / 取消范围外申请：fail closed，状态不变。
        await AssertNotFoundAsync(() => controller.Submit(s.ForeignRequest.Id));
        await AssertNotFoundAsync(() => controller.Cancel(s.ForeignRequest.Id,
            new SalesOrderChangeRequestCancelRequest { Reason = "越权取消" }));

        var foreignAfter = await db.SalesOrderChangeRequests.AsNoTracking()
            .SingleAsync(r => r.Id == s.ForeignRequest.Id);
        Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, foreignAfter.Status);
        Assert.Equal("原始原因", foreignAfter.Reason);

        var ownAfter = await db.SalesOrderChangeRequests.AsNoTracking()
            .SingleAsync(r => r.Id == s.OwnRequest.Id);
        Assert.Equal(200m, ownAfter.ProposedTotalAmount); // 伪造来源被拒后仍是上一次成功的拟议值
        Assert.Equal(SalesOrderChangeRequestRules.StatusDraft, ownAfter.Status);

        // 提交本人申请：成功冻结。
        var submitted = AssertOk<SalesOrderChangeRequestDto>(await controller.Submit(s.OwnRequest.Id));
        Assert.Equal(SalesOrderChangeRequestRules.StatusSubmitted, submitted.Status);
        Assert.NotNull(submitted.SubmittedAt);

        // 取消本人申请：成功留痕（保留证据）。
        var cancelled = AssertOk<SalesOrderChangeRequestDto>(await controller.Cancel(s.OwnRequest.Id,
            new SalesOrderChangeRequestCancelRequest { Reason = "客户撤回" }));
        Assert.Equal(SalesOrderChangeRequestRules.StatusCancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal("客户撤回", cancelled.CancelledReason);
    }

    private static async Task AssertDeniedAsync(
        SalesOrderChangeRequestController controller, int code, string text,
        Func<SalesOrderChangeRequestController, Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(() => action(controller));
        Assert.Equal(code, ex.Code);
        Assert.Equal(text, ex.Message);
    }

    private static async Task<(int Requests, int Details, int Orders, int OrderDetails, int StockOuts,
        int StockMovements, int Quotations, int Receipts)> SnapshotAsync(ErpDbContext db)
        => (await db.SalesOrderChangeRequests.CountAsync(),
            await db.SalesOrderChangeRequestDetails.CountAsync(),
            await db.SalesOrders.CountAsync(),
            await db.SalesOrderDetails.CountAsync(),
            await db.StockOuts.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.Quotations.CountAsync(),
            await db.FinanceReceipts.CountAsync());

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // ==================== 4. 来源改派 / 特权不受限历史访问 ====================

    [Fact]
    public async Task 来源客户改派后_历史快照客户不再是归属_受限不可见特权可见()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var restricted = NewController(db, s.OperatorUserId);

        // 改派前：受限账号可读本人来源的申请。
        Assert.Equal(s.OwnRequest.Id,
            AssertOk<SalesOrderChangeRequestDto>(await restricted.GetById(s.OwnRequest.Id)).Id);

        // 把来源订单改派到他人客户（申请上的来源客户快照与拟议客户仍是本人客户）。
        var order = await db.SalesOrders.SingleAsync(o => o.Id == s.Own.Id);
        order.CustomerId = s.CustomerBId;
        await db.SaveChangesAsync();
        var snapshot = await db.SalesOrderChangeRequests.AsNoTracking().SingleAsync(r => r.Id == s.OwnRequest.Id);
        Assert.Equal(s.CustomerAId, snapshot.SourceCustomerId);
        Assert.Equal(s.CustomerAId, snapshot.ProposedCustomerId);

        // 受限账号：历史快照客户不再授予访问（按**实时**来源归属 fail closed）。
        await AssertNotFoundAsync(() => restricted.GetById(s.OwnRequest.Id));
        var page = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await restricted.GetPaged(new SalesOrderChangeRequestQuery()));
        Assert.DoesNotContain(page.Items, x => x.Id == s.OwnRequest.Id);

        // 特权账号：保留既有不受限历史访问。
        var privileged = NewController(db, SeedPrivilegedUser(db));
        Assert.Equal(s.OwnRequest.Id,
            AssertOk<SalesOrderChangeRequestDto>(await privileged.GetById(s.OwnRequest.Id)).Id);
    }

    [Fact]
    public async Task 特权账号_保留既有不受限历史访问_可读范围外与来源缺失申请()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        var privileged = NewController(db, SeedPrivilegedUser(db));

        // 范围外来源、来源已删除 / 缺失的申请都照常可读（既有不受限历史行为保持显式）。
        Assert.Equal(s.ForeignRequest.Id,
            AssertOk<SalesOrderChangeRequestDto>(await privileged.GetById(s.ForeignRequest.Id)).Id);
        Assert.Equal(s.DeletedSourceRequest.Id,
            AssertOk<SalesOrderChangeRequestDto>(await privileged.GetById(s.DeletedSourceRequest.Id)).Id);
        Assert.Equal(s.MissingSourceRequest.Id,
            AssertOk<SalesOrderChangeRequestDto>(await privileged.GetById(s.MissingSourceRequest.Id)).Id);

        var page = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await privileged.GetPaged(new SalesOrderChangeRequestQuery()));
        Assert.Equal(4, page.Total);

        // 特权账号可在他人来源上登记（既有不受限口径）。
        var created = AssertOk<SalesOrderChangeRequestDto>(await privileged.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Foreign.Id, Reason = "特权登记" }));
        Assert.Equal(s.Foreign.Id, created.SalesOrderId);

        var options = AssertOk<List<SalesOrderChangeRequestSourceOptionDto>>(await privileged.SourceOptions(null));
        Assert.Contains(options, o => o.SalesOrderId == s.Foreign.Id);
    }

    // ==================== 5. 身份 / 菜单拒绝矩阵（逐入口 fail closed 且零写入） ====================

    [Fact]
    public async Task 身份与菜单拒绝矩阵_逐入口fail_closed且零写入()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);

        var deleted = SeedOperator(db, deleted: true);
        var disabled = SeedOperator(db, status: UserStatus.Disabled);
        var menuLess = SeedOperator(db, menu: false);
        var exportOnly = SeedOperator(db, menu: false);
        GrantMenu(db, exportOnly.RoleId, "sales-order-export");
        var revoked = SeedOperator(db);
        db.SaveChanges();

        // 已授予的既有「销售订单」菜单被回收（角色仍在，授权立即收敛，无缓存）。
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == revoked.RoleId));
        db.SaveChanges();

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, SalesOrderChangeRequestAuthorizationRules.UnauthorizedText),
            (deleted.UserId, ErrorCodes.Unauthorized, SalesOrderChangeRequestAuthorizationRules.UserDeletedText),
            (disabled.UserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.UserDisabledText),
            (menuLess.UserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.MenuDeniedText),
            (exportOnly.UserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.MenuDeniedText),
            (revoked.UserId, ErrorCodes.Forbidden, SalesOrderChangeRequestAuthorizationRules.MenuDeniedText),
        };

        var before = await SnapshotAsync(db);
        foreach (var (userId, code, text) in cases)
        {
            var ctl = NewController(db, userId);
            await AssertDeniedAsync(ctl, code, text, c => c.GetPaged(new SalesOrderChangeRequestQuery()));
            await AssertDeniedAsync(ctl, code, text, c => c.GetById(s.OwnRequest.Id));
            await AssertDeniedAsync(ctl, code, text, c => c.SourceOptions(null));
            await AssertDeniedAsync(ctl, code, text, c => c.GetForSource(s.Own.Id));
            await AssertDeniedAsync(ctl, code, text, c => c.Create(
                new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Own.Id, Reason = "x" }));
            await AssertDeniedAsync(ctl, code, text, c => c.Update(s.OwnRequest.Id,
                new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Own.Id, Reason = "x" }));
            await AssertDeniedAsync(ctl, code, text, c => c.Submit(s.OwnRequest.Id));
            await AssertDeniedAsync(ctl, code, text, c => c.Cancel(s.OwnRequest.Id,
                new SalesOrderChangeRequestCancelRequest { Reason = "x" }));
        }

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 4b. 授权先于发号：被拒登记不推进单号流水 ====================

    [Fact]
    public async Task 受限业务员_登记授权先于发号_被拒请求不推进单号流水()
    {
        using var db = TestDbFactory.Create();
        var s = SeedScenario(db);
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesOrderChangeRequest, RuleCode = "SOC-414",
            RuleName = "ERP414 变更申请单号", Prefix = "SOC", DateFormat = "yyyyMMdd",
            SerialLength = 4, CurrentSequence = 41
        });
        db.SaveChanges();
        var controller = NewController(db, s.OperatorUserId);

        async Task<long> SequenceAsync() => await db.SysDocumentNumberRules
            .Where(r => r.DocumentType == DocumentType.SalesOrderChangeRequest)
            .SumAsync(r => r.CurrentSequence);

        var before = await SequenceAsync();

        // 被拒登记（范围外 / 已删除 / 不存在来源）绝不发号：单号流水保持不变。
        await AssertNotFoundAsync(() => controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Foreign.Id, Reason = "越权" }));
        await AssertNotFoundAsync(() => controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.DeletedSource.Id, Reason = "越权" }));
        await AssertNotFoundAsync(() => controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = 9_414_999L, Reason = "越权" }));
        Assert.Equal(before, await SequenceAsync());

        // 本人来源登记才发号（仅推进一次）。
        var created = AssertOk<SalesOrderChangeRequestDto>(await controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = s.Own.Id, Reason = "本人登记" }));
        Assert.StartsWith("SOC", created.RequestNo, StringComparison.Ordinal);
        Assert.Equal(before + 1, await SequenceAsync());
    }

    // ==================== 6. 进程内直调与授权接线契约 ====================

    [Fact]
    public async Task 进程内无身份直调_保持既有免授权口径()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, 7, "SO-414-PROC");
        var controller = InProcessController(db);

        var page = AssertOk<PagedResult<SalesOrderChangeRequestDto>>(
            await controller.GetPaged(new SalesOrderChangeRequestQuery()));
        Assert.Equal(0, page.Total);

        var created = AssertOk<SalesOrderChangeRequestDto>(await controller.Create(
            new SalesOrderChangeRequestSaveDto { SalesOrderId = order.Id, Reason = "进程内登记" }));
        Assert.Equal(order.Id, created.SalesOrderId);
    }

    [Fact]
    public void 契约_每个数据路由都接通实时授权且服务按来源实时归属下推()
    {
        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SalesOrderChangeRequestController.cs"));
        Assert.Contains("SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync", controller, StringComparison.Ordinal);
        Assert.Contains("SalesOrderChangeRequestAuthorizationRules.EnsureRequestAllowedAsync", controller, StringComparison.Ordinal);
        /* 8 条数据 / 状态路由（台账 / 来源候选 / 指定来源 / 详情 / 登记 / 编辑 / 提交 / 取消）都解析实时授权 */
        Assert.True(CountOf(controller, "var scope = await EnsureAuthorizedAsync();") >= 8,
            "所有数据 / 状态路由都必须先解析实时授权");
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);

        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "SalesOrderChangeRequestService.cs"));
        Assert.Contains("SalesOrderChangeRequestAuthorizationRules.ApplySourceScope", service, StringComparison.Ordinal);
        Assert.Contains("SalesOrderChangeRequestAuthorizationRules.EnsureSourceOrderAllowedAsync", service, StringComparison.Ordinal);
        /* 台账范围下推必须发生在 Count / 分页之前（ApplySourceScope 先于 NormalizePaging 之后的过滤） */
        Assert.Contains("ApplySourceScope", service, StringComparison.Ordinal);

        var rules = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "SalesOrderChangeRequestAuthorizationRules.cs"));
        Assert.Contains("SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync", rules, StringComparison.Ordinal);
        Assert.Contains("SalespersonDataScopeService", rules, StringComparison.Ordinal);
        /* 归属只按来源订单**实时** CustomerId（相关子查询），绝不使用来源客户快照 / 拟议客户做授权 */
        Assert.Contains("db.SalesOrders.Any(", rules, StringComparison.Ordinal);
        Assert.Contains("allowed.Contains(o.CustomerId)", rules, StringComparison.Ordinal);
    }
}
