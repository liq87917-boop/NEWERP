using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-385 费用单 / 分摊批次 / 分摊证据实时授权与权威客户范围护栏单元测试。
/// <para>覆盖：身份（缺失 / 已删除 / 禁用）、既有「费用单」（<c>expense-bill</c>）菜单授权与未映射业务员的 fail closed；
/// HTTP 入口过滤器在任何动作之前拒绝未授权请求；费用 CRUD / 列表 / 详情 / 删除按持久化 CustomerId 的范围下推与实体级复核；
/// 受限账号不能访问无权威归属的费用单而特权账号保留历史访问；传统分摊与 ERP-042 批次预览 / 生成 / 台账 / 详情 / 作废的
/// 共享柜参与方 / 来源费用 / 逐行生成客户范围；写入前的启用客户 / 受支持币种 / 正金额 / 正汇率校验；
/// 被拒绝的编辑 / 删除 / 作废不改写原行、状态、审计与批次留痕；身份不来自请求体；不新增表 / 列 / 权限。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class ExpenseAuthorizationTests
{
    // ==================== 脚手架 ====================

    /// <summary>以显式范围模拟「已通过 HTTP 授权管线」的控制器（范围来自已认证主体，绝不来自请求体）</summary>
    private static ExpenseBillController NewExpenseController(
        ErpDbContext db, SalespersonDataScope? scope, long? userId = null)
    {
        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        if (scope is not null)
            httpContext.Items[ExpenseRequestAuthorizationFilter.ScopeItemKey] = scope;

        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new ControllerActionDescriptor()
        };
        return controller;
    }

    private static ContainerExpenseAllocationEvidenceController NewEvidenceController(ErpDbContext db)
    {
        var controller = new ContainerExpenseAllocationEvidenceController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
            ActionDescriptor = new ControllerActionDescriptor()
        };
        return controller;
    }

    /// <summary>仅解析 IErpDbContext 的最小服务提供器（用于直接驱动真实授权过滤器）</summary>
    private sealed class SingleDbContextServices : IServiceProvider
    {
        private readonly IErpDbContext _db;

        public SingleDbContextServices(IErpDbContext db) => _db = db;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IErpDbContext) ? _db
             : serviceType == typeof(IServiceProvider) ? this
             : null;
    }

    /// <summary>驱动真实 <see cref="ExpenseRequestAuthorizationFilter"/>（HTTP 入口）并返回结果</summary>
    private static async Task<(ExpenseBillController Controller, BusinessException? Denied)>
        RunAuthorizationFilterAsync(ErpDbContext db, long? userId)
    {
        var httpContext = new DefaultHttpContext { RequestServices = new SingleDbContextServices(db) };
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new ControllerActionDescriptor()
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
        var executing = new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller);
        var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller);

        try
        {
            await new ExpenseRequestAuthorizationFilter().OnActionExecutionAsync(
                executing, () => Task.FromResult(executed));
            return (controller, null);
        }
        catch (BusinessException ex)
        {
            return (controller, ex);
        }
    }

    // ==================== 种子数据 ====================

    private static SalespersonDataScope Restricted(long salesmanId, params long[] allowedCustomerIds)
        => new()
        {
            IsPrivileged = false,
            SalesmanId = salesmanId,
            AllowedCustomerIds = allowedCustomerIds.ToHashSet()
        };

    private static SalespersonDataScope Privileged()
        => new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, long? empId = null, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            CreditStatus = "正常",
            EmpId = empId,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SysUser SeedUser(
        ErpDbContext db, string userName, bool privileged = false, bool enabled = true, bool deleted = false,
        params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"测试角色-{Guid.NewGuid():N}",
            RoleCode = $"Role-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = enabled ? UserStatus.Enabled : UserStatus.Disabled,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes)
        {
            var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuCode = code, MenuName = code, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                db.SaveChanges();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }
        return user;
    }

    /// <summary>受限业务员：登录账号 = 员工编码，且必须有既有菜单授权</summary>
    private static (SysUser User, BaseEmployee Employee) SeedRestrictedOperator(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"exp-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        var user = SeedUser(db, code, menuCodes: menuCodes);
        return (user, employee);
    }

    private static FinanceExpense SeedExpense(
        ErpDbContext db, string expenseNo, long? customerId, decimal amount = 100m, string currency = "CNY",
        decimal exchangeRate = 1m, string refType = "拼柜", string refNo = "TCLU-001",
        string allocationBatchNo = "", long? allocationSourceExpenseId = null,
        string allocationSourceExpenseNo = "")
    {
        var expense = new FinanceExpense
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = exchangeRate,
            AmountCny = amount,
            RefType = refType,
            RefNo = refNo,
            CustomerId = customerId,
            CustomerName = customerId.HasValue ? $"客户{customerId}" : string.Empty,
            AllocationBase = "不分摊",
            PaymentStatus = "未付",
            AllocationBatchNo = allocationBatchNo,
            AllocationSourceExpenseId = allocationSourceExpenseId,
            AllocationSourceExpenseNo = allocationSourceExpenseNo
        };
        db.FinanceExpenses.Add(expense);
        db.SaveChanges();
        return expense;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string listNo, string containerNo, long customerId,
        decimal cartons = 10m, decimal weight = 500m, decimal volume = 3.5m)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = DateTime.Today,
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ContainerLoadingListParticipant SeedParticipant(
        ErpDbContext db, long loadingListId, BaseCustomer customer, bool primary = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId,
            CustomerId = customer.Id,
            CustomerCode = customer.CustomerCode,
            CustomerName = customer.CustomerName,
            IsPrimary = primary,
            Status = ContainerLoadingParticipantRules.ActiveStatus
        };
        db.ContainerLoadingListParticipants.Add(participant);
        db.SaveChanges();
        return participant;
    }

    private static FinanceExpenseAllocationBatch SeedBatch(
        ErpDbContext db, string batchNo, ContainerLoadingList list, FinanceExpense source,
        params (long ParticipantId, long CustomerId, decimal Amount)[] lines)
    {
        var batch = new FinanceExpenseAllocationBatch
        {
            BatchNo = batchNo,
            SourceExpenseId = source.Id,
            SourceExpenseNo = source.ExpenseNo,
            LoadingListId = list.Id,
            LoadingListNo = list.LoadingListNo,
            ContainerNo = list.ContainerNo,
            AllocationMethod = "按体积",
            BasisKind = "按体积",
            Currency = "CNY",
            ExchangeRate = 1m,
            SourceAmount = source.Amount,
            AllocatedTotal = lines.Sum(l => l.Amount),
            LineCount = lines.Length,
            Status = ContainerExpenseAllocationRules.BatchActive
        };
        db.FinanceExpenseAllocationBatches.Add(batch);
        db.SaveChanges();

        var sort = 0;
        foreach (var line in lines)
        {
            db.FinanceExpenseAllocationLines.Add(new FinanceExpenseAllocationLine
            {
                BatchId = batch.Id,
                BatchNo = batchNo,
                SourceExpenseId = source.Id,
                SourceExpenseNo = source.ExpenseNo,
                LoadingListId = list.Id,
                LoadingListNo = list.LoadingListNo,
                ContainerNo = list.ContainerNo,
                ParticipantId = line.ParticipantId,
                CustomerId = line.CustomerId,
                CustomerCode = $"C-{line.CustomerId}",
                CustomerName = $"客户{line.CustomerId}",
                AllocationMethod = "按体积",
                BasisKind = "按体积",
                BasisSource = ContainerExpenseAllocationRules.BasisSourceRequest,
                BasisValue = line.Amount,
                Ratio = 100m,
                AllocatedAmount = line.Amount,
                AllocatedAmountCny = line.Amount,
                Currency = "CNY",
                ExpenseNo = $"EXP-{batchNo}-{sort:00}",
                SortOrder = sort++
            });
            db.SaveChanges();
        }
        return batch;
    }

    // ==================== 断言 ====================

    private static async Task<BusinessException> AssertCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    // ==================== 1. 纯规则与菜单授权 ====================

    [Fact]
    public void Rules_币种金额汇率校验_非法取值一律拒绝()
    {
        Assert.Equal("CNY", ExpenseAuthorizationRules.NormalizeCurrencyStrict(" cny "));
        Assert.Equal("USD", ExpenseAuthorizationRules.NormalizeCurrencyStrict("usd"));
        Assert.Equal("CNY", ExpenseAuthorizationRules.NormalizeCurrencyStrict(null));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeCurrencyStrict("KRW"));

        Assert.Equal(0.01m, ExpenseAuthorizationRules.NormalizeAmount(0.005m));
        Assert.Equal(100m, ExpenseAuthorizationRules.NormalizeAmount(100m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeAmount(0.004m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeAmount(-1m));

        Assert.Equal(1.01m, ExpenseAuthorizationRules.NormalizeRate(1.005m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeRate(0m));
        Assert.Throws<BusinessException>(() => ExpenseAuthorizationRules.NormalizeRate(0.004m));
    }

    [Fact]
    public async Task 身份账号状态与菜单授权_缺失删除禁用无菜单未映射一律failClosed()
    {
        using var db = TestDbFactory.Create();

        await AssertCodeAsync(ErrorCodes.Unauthorized,
            () => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, null));
        await AssertCodeAsync(ErrorCodes.Unauthorized,
            () => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, 0));

        var deleted = SeedUser(db, "u-deleted", deleted: true);
        await AssertCodeAsync(ErrorCodes.Unauthorized,
            () => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, deleted.Id));

        var disabled = SeedUser(db, "u-disabled", enabled: false,
            menuCodes: ExpenseAuthorizationRules.RequiredMenuCode);
        var disabledEx = await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, disabled.Id));
        Assert.Contains("已禁用", disabledEx.Message);

        var noMenu = SeedUser(db, "u-no-menu");
        var noMenuEx = await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, noMenu.Id));
        Assert.Contains(ExpenseAuthorizationRules.RequiredMenuCode, noMenuEx.Message);

        var unmapped = SeedUser(db, "u-unmapped", menuCodes: ExpenseAuthorizationRules.RequiredMenuCode);
        var unmappedEx = await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, unmapped.Id));
        Assert.Contains("未映射为业务员", unmappedEx.Message);

        var (operatorUser, employee) = SeedRestrictedOperator(db, ExpenseAuthorizationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C-A", "本人客户", empId: employee.Id);
        var scope = await ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, operatorUser.Id);
        Assert.False(scope.IsPrivileged);
        Assert.Equal(employee.Id, scope.SalesmanId);
        Assert.True(scope.AllowsCustomer(customer.Id));
        Assert.False(scope.AllowsCustomer(customer.Id + 1));

        var privileged = SeedUser(db, "u-privileged", privileged: true);
        var privilegedScope = await ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(db, privileged.Id);
        Assert.True(privilegedScope.IsPrivileged);
        Assert.Null(privilegedScope.AllowedCustomerIds);
    }

    [Fact]
    public async Task Http入口过滤器_未认证与无菜单请求在读取与写入之前被拒绝()
    {
        using var db = TestDbFactory.Create();

        var (_, anonymous) = await RunAuthorizationFilterAsync(db, null);
        Assert.NotNull(anonymous);
        Assert.Equal(ErrorCodes.Unauthorized, anonymous!.Code);

        var noMenu = SeedUser(db, "u-filter-no-menu");
        var (_, denied) = await RunAuthorizationFilterAsync(db, noMenu.Id);
        Assert.NotNull(denied);
        Assert.Equal(ErrorCodes.Forbidden, denied!.Code);

        // 过滤器放行后必须已注入权威范围：列表在计数之前按本人客户过滤（同时排除无主行）
        var (operatorUser, employee) = SeedRestrictedOperator(db, ExpenseAuthorizationRules.RequiredMenuCode);
        var own = SeedCustomer(db, "C-OWN", "本人客户", empId: employee.Id);
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        SeedExpense(db, "EXP-OWN", own.Id);
        SeedExpense(db, "EXP-FOREIGN", foreign.Id);
        SeedExpense(db, "EXP-SOURCE-LESS", null);

        var (controller, allowed) = await RunAuthorizationFilterAsync(db, operatorUser.Id);
        Assert.Null(allowed);

        var page = AssertOk<PagedResult<FinanceExpense>>(await controller.GetPaged(new PageQuery()));
        Assert.Equal(1, page.Total);
        Assert.Equal("EXP-OWN", Assert.Single(page.Items).ExpenseNo);

        Assert.Equal(3, await db.FinanceExpenses.CountAsync());
    }

    [Fact]
    public void 范围谓词_特权不过滤_受限排除无主与越界()
    {
        Assert.Null(ExpenseAuthorizationRules.ExpenseScopeFilter(null));
        Assert.Null(ExpenseAuthorizationRules.ExpenseScopeFilter(Privileged()));

        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        SeedExpense(db, "EXP-OWN", own.Id);
        SeedExpense(db, "EXP-FOREIGN", foreign.Id);
        SeedExpense(db, "EXP-NONE", null);

        var visible = ExpenseAuthorizationRules
            .ApplyExpenseScope(db.FinanceExpenses.AsNoTracking(), Restricted(1, own.Id))
            .Select(x => x.ExpenseNo)
            .ToList();
        Assert.Equal(new[] { "EXP-OWN" }, visible);

        Assert.Empty(ExpenseAuthorizationRules
            .ApplyExpenseScope(db.FinanceExpenses.AsNoTracking(), Restricted(2))
            .Select(x => x.ExpenseNo)
            .ToList());

        Assert.Equal(3, ExpenseAuthorizationRules
            .ApplyExpenseScope(db.FinanceExpenses.AsNoTracking(), Privileged()).Count());
    }

    // ==================== 2. 费用单 CRUD / 读取 / 删除 ====================

    private static FinanceExpense NewExpense(
        long? customerId, decimal amount = 100m, string currency = "CNY", decimal rate = 1m,
        string? expenseNo = null, string allocationBatchNo = "")
        => new()
        {
            ExpenseNo = expenseNo ?? $"EXP-{Guid.NewGuid():N}"[..12],
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = rate,
            CustomerId = customerId,
            RefType = "客户",
            RefNo = "TCLU-001",
            AllocationBase = "不分摊",
            AllocationBatchNo = allocationBatchNo
        };

    [Fact]
    public async Task 新增_拟议客户范围与写入校验_拒绝时不留任何行()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var disabled = SeedCustomer(db, "C-DIS", "停用客户", status: 0);
        var scope = Restricted(1, own.Id, disabled.Id, 987654321L);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).Create(NewExpense(foreign.Id)));

        // 无权威归属（CustomerId 缺失）：受限账号 fail closed
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).Create(NewExpense(null)));

        // 客户真实存在性 / 启用状态：范围内但不存在 / 已停用一律拒绝
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).Create(NewExpense(987654321L)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).Create(NewExpense(disabled.Id)));

        // 受支持币种 / 取整后为正的金额 / 为正的汇率
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).Create(NewExpense(own.Id, currency: "KRW")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).Create(NewExpense(own.Id, amount: 0.004m)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).Create(NewExpense(own.Id, rate: 0m)));

        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
    }

    [Fact]
    public async Task 新增_成功写入本人客户且忽略客户端批次留痕()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var scope = Restricted(1, own.Id);
        var controller = NewExpenseController(db, scope);

        var created = AssertOk<FinanceExpense>(await controller.Create(
            NewExpense(own.Id, allocationBatchNo: "EAB-CLIENT-001")));

        Assert.Equal(own.Id, created.CustomerId);
        Assert.Equal("CNY", created.Currency);

        var stored = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(own.Id, stored.CustomerId);
        Assert.Equal(string.Empty, stored.AllocationBatchNo);
        Assert.Null(stored.AllocationSourceExpenseId);
        Assert.Equal(string.Empty, stored.AllocationSourceExpenseNo);
    }

    [Fact]
    public async Task 详情_越界与无主一律拒绝_本人客户可读()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var ownRow = SeedExpense(db, "EXP-OWN", own.Id);
        var foreignRow = SeedExpense(db, "EXP-FOREIGN", foreign.Id);
        var orphanRow = SeedExpense(db, "EXP-NONE", null);
        var scope = Restricted(1, own.Id);

        var detail = AssertOk<FinanceExpense>(
            await NewExpenseController(db, scope).GetById(ownRow.Id));
        Assert.Equal("EXP-OWN", detail.ExpenseNo);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).GetById(foreignRow.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).GetById(orphanRow.Id));

        // 特权账号保留历史访问（含无主行与范围外行）
        var privileged = NewExpenseController(db, Privileged());
        Assert.Equal("EXP-FOREIGN",
            AssertOk<FinanceExpense>(await privileged.GetById(foreignRow.Id)).ExpenseNo);
        Assert.Equal("EXP-NONE",
            AssertOk<FinanceExpense>(await privileged.GetById(orphanRow.Id)).ExpenseNo);
    }

    [Fact]
    public async Task 删除与批量删除_越界一律拒绝且不改写任何行()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var ownRow = SeedExpense(db, "EXP-OWN", own.Id);
        var foreignRow = SeedExpense(db, "EXP-FOREIGN", foreign.Id);
        var scope = Restricted(1, own.Id);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).Delete(foreignRow.Id));
        Assert.False(db.FinanceExpenses.Single(x => x.Id == foreignRow.Id).IsDeleted);

        // 批量删除：任一行越界即整体拒绝，本人行也不得被删除
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).BatchDelete(new List<long> { ownRow.Id, foreignRow.Id }));
        Assert.False(db.FinanceExpenses.Single(x => x.Id == ownRow.Id).IsDeleted);
        Assert.False(db.FinanceExpenses.Single(x => x.Id == foreignRow.Id).IsDeleted);

        AssertOk<object>(await NewExpenseController(db, scope).Delete(ownRow.Id));
        Assert.True(db.FinanceExpenses.Single(x => x.Id == ownRow.Id).IsDeleted);
        Assert.False(db.FinanceExpenses.Single(x => x.Id == foreignRow.Id).IsDeleted);
    }

    [Fact]
    public async Task 修改_越界拒绝且批次生成行审计不可被客户端改写()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var generated = SeedExpense(db, "EXP-GEN", own.Id, amount: 300m,
            allocationBatchNo: "EAB-20261001-001", allocationSourceExpenseId: 42,
            allocationSourceExpenseNo: "EXP-SRC");
        var foreignRow = SeedExpense(db, "EXP-FOREIGN", foreign.Id);
        var scope = Restricted(1, own.Id);

        // 越界行：拒绝且字段 / 审计不变
        var foreignBefore = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == foreignRow.Id);
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).Update(foreignRow.Id, NewExpense(own.Id, amount: 999m)));
        var foreignAfter = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == foreignRow.Id);
        Assert.Equal(foreignBefore.Amount, foreignAfter.Amount);
        Assert.Equal(foreignBefore.UpdatedAt, foreignAfter.UpdatedAt);

        // 本人行：允许修改金额 / 备注，但批次留痕不得被清空（客户端提交体无法改写审计）
        var body = NewExpense(own.Id, amount: 350m, allocationBatchNo: "");
        body.Remark = "调整金额";
        var updated = AssertOk<FinanceExpense>(
            await NewExpenseController(db, scope).Update(generated.Id, body));
        Assert.Equal(350m, updated.Amount);
        Assert.Equal("EAB-20261001-001", updated.AllocationBatchNo);
        Assert.Equal(42L, updated.AllocationSourceExpenseId);
        Assert.Equal("EXP-SRC", updated.AllocationSourceExpenseNo);

        // 越界客户改派同样被拒绝（拟议范围校验）
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).Update(generated.Id, NewExpense(foreign.Id, amount: 350m)));
        Assert.Equal("EAB-20261001-001",
            db.FinanceExpenses.Single(x => x.Id == generated.Id).AllocationBatchNo);
    }

    // ==================== 3. 传统分摊与 ERP-042 批次路由 ====================

    private static ExpenseAllocateRequest AllocateRequest(
        long? customerId, decimal total = 100m, string currency = "CNY", decimal rate = 1m)
        => new()
        {
            RefType = "拼柜",
            RefNo = "TCLU-LEGACY",
            ExpenseType = "报关费",
            TotalAmount = total,
            Currency = currency,
            ExchangeRate = rate,
            AllocationBase = "按体积",
            Details = new List<ExpenseAllocateDetail>
            {
                new() { CustomerId = customerId, CustomerName = "客户", Volume = 1m }
            }
        };

    [Fact]
    public async Task 传统分摊_越界与非法取值一律拒绝且不写库()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var scope = Restricted(1, own.Id);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).AllocatePreview(AllocateRequest(foreign.Id)));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).AllocateApply(AllocateRequest(foreign.Id)));

        // 无权威归属（CustomerId 缺失）的明细行对受限账号 fail closed
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).AllocateApply(AllocateRequest(null)));

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).AllocateApply(AllocateRequest(own.Id, total: 0m)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).AllocateApply(AllocateRequest(own.Id, currency: "KRW")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            NewExpenseController(db, scope).AllocateApply(AllocateRequest(own.Id, rate: 0m)));

        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        var preview = AssertOk<List<ExpenseAllocateResultItem>>(
            await NewExpenseController(db, scope).AllocatePreview(AllocateRequest(own.Id)));
        Assert.Equal(100m, Assert.Single(preview).AllocatedAmount);

        var apply = await NewExpenseController(db, scope).AllocateApply(AllocateRequest(own.Id));
        Assert.IsType<OkObjectResult>(apply);
        var generated = await db.FinanceExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(own.Id, generated.CustomerId);
        Assert.Equal(100m, generated.Amount);
    }

    [Fact]
    public async Task 批次预览与生成_共享柜越界参与方拒绝且不留任何行()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var list = SeedLoadingList(db, "ZG-SHARED", "TCLU-SHARED", own.Id);
        var ownParticipant = SeedParticipant(db, list.Id, own, primary: true);
        var foreignParticipant = SeedParticipant(db, list.Id, foreign);
        var source = SeedExpense(db, "EXP-SHARED-SRC", own.Id, amount: 1000m, refNo: "TCLU-SHARED");
        var scope = Restricted(1, own.Id);

        var request = new ContainerExpenseAllocationRequest
        {
            SourceExpenseId = source.Id,
            LoadingListId = list.Id,
            AllocationMethod = "按体积",
            Lines = new List<ContainerExpenseAllocationBasisDto>
            {
                new() { ParticipantId = ownParticipant.Id, BasisValue = 1m },
                new() { ParticipantId = foreignParticipant.Id, BasisValue = 1m }
            }
        };

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).AllocationPreview(request));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            NewExpenseController(db, scope).AllocationGenerate(request));
        Assert.Equal(0, await db.FinanceExpenseAllocationBatches.CountAsync());
        Assert.Equal(1, await db.FinanceExpenses.CountAsync());

        // 特权账号保留既有口径：预览与生成均可用
        var privileged = NewExpenseController(db, Privileged());
        var preview = AssertOk<ContainerExpenseAllocationPreviewDto>(await privileged.AllocationPreview(request));
        Assert.Equal(2, preview.Lines.Count);

        var generated = AssertOk<ContainerExpenseAllocationGenerateResultDto>(
            await privileged.AllocationGenerate(request));
        Assert.Equal(2, generated.LineCount);
        Assert.Equal(1, await db.FinanceExpenseAllocationBatches.CountAsync());
    }

    [Fact]
    public async Task 批次台账详情与作废_受限账号屏蔽共享批次且作废不改状态()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var list = SeedLoadingList(db, "ZG-BATCH", "TCLU-BATCH", own.Id);
        var ownParticipant = SeedParticipant(db, list.Id, own, primary: true);
        var foreignParticipant = SeedParticipant(db, list.Id, foreign);
        var source = SeedExpense(db, "EXP-BATCH-SRC", own.Id, amount: 1000m, refNo: "TCLU-BATCH");
        var mixed = SeedBatch(db, "EAB-MIXED-001", list, source,
            (ownParticipant.Id, own.Id, 600m), (foreignParticipant.Id, foreign.Id, 400m));
        var scope = Restricted(1, own.Id);

        // 台账在计数与分页之前把权威范围下推数据库：受限账号看不到含范围外客户行的批次
        var restrictedPage = await ContainerExpenseAllocationService.ListBatchesAsync(
            db, new ContainerExpenseAllocationBatchQuery(), scope);
        Assert.Equal(0, restrictedPage.Total);
        Assert.Empty(restrictedPage.Items);

        var unrestrictedPage = await ContainerExpenseAllocationService.ListBatchesAsync(
            db, new ContainerExpenseAllocationBatchQuery(), Privileged());
        Assert.Equal(1, unrestrictedPage.Total);

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationService.GetBatchAsync(db, mixed.Id, scope));
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationService.VoidAsync(db, mixed.Id, "更正", scope));

        // 被拒绝的作废不改写状态 / 审计与逐行留痕
        var untouched = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .SingleAsync(x => x.Id == mixed.Id);
        Assert.Equal(ContainerExpenseAllocationRules.BatchActive, untouched.Status);
        Assert.Null(untouched.VoidedAt);
        Assert.Equal(string.Empty, untouched.VoidReason);
        Assert.Equal(2, await db.FinanceExpenseAllocationLines.CountAsync(l => l.BatchId == mixed.Id));

        // 特权账号可读取与显式作废（保留历史与逐行留痕）
        Assert.Equal(mixed.Id,
            (await ContainerExpenseAllocationService.GetBatchAsync(db, mixed.Id, Privileged())).Id);
        var voided = await ContainerExpenseAllocationService.VoidAsync(
            db, mixed.Id, "更正口径", Privileged());
        Assert.True(voided.IsVoided);
        Assert.Equal(2, voided.Lines.Count);
        Assert.Equal(2, await db.FinanceExpenseAllocationLines.CountAsync(l => l.BatchId == mixed.Id));
    }

    [Fact]
    public void 批次范围谓词_进程内调用与特权账号不过滤()
    {
        using var db = TestDbFactory.Create();
        Assert.Null(ExpenseAuthorizationRules.BatchScopeFilter(db, null));
        Assert.Null(ExpenseAuthorizationRules.BatchScopeFilter(db, Privileged()));
        Assert.NotNull(ExpenseAuthorizationRules.BatchScopeFilter(db, Restricted(1)));
    }

    // ==================== 4. ERP-060 分摊证据 ====================

    [Fact]
    public async Task 分摊证据_受限账号屏蔽共享批次与越界清单_特权账号可读()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "C-OWN", "本人客户");
        var foreign = SeedCustomer(db, "C-OTHER", "他人客户");
        var list = SeedLoadingList(db, "ZG-EVIDENCE", "TCLU-EVIDENCE", own.Id);
        var ownParticipant = SeedParticipant(db, list.Id, own, primary: true);
        var foreignParticipant = SeedParticipant(db, list.Id, foreign);
        var source = SeedExpense(db, "EXP-EV-SRC", own.Id, amount: 1000m, refNo: "TCLU-EVIDENCE");
        SeedBatch(db, "EAB-EV-001", list, source,
            (ownParticipant.Id, own.Id, 600m), (foreignParticipant.Id, foreign.Id, 400m));
        var scope = Restricted(1, own.Id);

        // 工作台在计数 / 分页之前下推范围
        var restricted = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery(), scope);
        Assert.Equal(0, restricted.Total);

        var unrestricted = await ContainerExpenseAllocationEvidenceService.ListAsync(
            db, new ContainerExpenseAllocationEvidenceQuery());
        Assert.Equal(1, unrestricted.Total);

        // 装柜清单维度证据：共享柜含范围外参与方一律拒绝
        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id, true, 20, scope));

        var evidence = await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(db, list.Id);
        Assert.True(evidence.HasEvidence);

        // 结算单维度证据：结算客户越界一律拒绝
        var settlement = new FinanceContainerSettlement
        {
            SettlementNo = $"SET-{Guid.NewGuid():N}"[..14],
            SettlementDate = DateTime.Today,
            CustomerId = foreign.Id,
            LoadingListId = list.Id,
            TotalAmount = 1000m,
            Status = DocumentStatus.Pending
        };
        db.FinanceContainerSettlements.Add(settlement);
        db.SaveChanges();

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, settlement.Id, true, 20, scope));
        Assert.NotNull(await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(db, settlement.Id));
    }

    [Fact]
    public async Task 分摊证据控制器_未认证请求一律未授权()
    {
        using var db = TestDbFactory.Create();
        var controller = NewEvidenceController(db);

        await AssertCodeAsync(ErrorCodes.Unauthorized, async () =>
        {
            await controller.GetPaged(new ContainerExpenseAllocationEvidenceQuery());
        });
        await AssertCodeAsync(ErrorCodes.Unauthorized, async () =>
        {
            await controller.GetForLoadingList(1);
        });
        await AssertCodeAsync(ErrorCodes.Unauthorized, async () =>
        {
            await controller.GetForSettlement(1);
        });
    }

    // ==================== 5. 边界与接线 ====================

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadRepoFile(params string[] relativeParts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray()));

    [Fact]
    public void 边界_不新增表列与权限_身份不来自请求体_控制器与基类接线同源()
    {
        // 身份只来自已认证请求主体：请求体不包含任何账号 / 角色 / 范围字段
        var requestProperties = typeof(ContainerExpenseAllocationRequest)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(requestProperties, name =>
            name.Contains("User", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Role", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Scope", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Identity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Permission", StringComparison.OrdinalIgnoreCase));

        // 不新增任何「费用授权 / 权限」数据集（无新表、无新列）
        var dbSets = typeof(IErpDbContext).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(dbSets, name => name.Contains("ExpenseAuthorization", StringComparison.Ordinal));
        Assert.DoesNotContain(dbSets, name => name.Contains("ExpensePermission", StringComparison.Ordinal));

        // 复用既有 expense-bill 菜单（与 SchemaUpgrader / SeedData 同源），绝不新增授权
        Assert.Equal("expense-bill", ExpenseAuthorizationRules.RequiredMenuCode);
        Assert.Contains("expense-bill", ExpenseAuthorizationRules.RuleText);
        Assert.Contains("不新增", ExpenseAuthorizationRules.BoundaryText);

        // 费用单控制器：HTTP 入口过滤器 + 全部分摊路由范围接线
        var controller = ReadRepoFile("src", "ERP.Api", "Controllers", "ExpenseBillController.cs");
        Assert.Contains("[ExpenseRequestAuthorizationFilter]", controller);
        Assert.Contains("ExpenseRequestScope", controller);
        Assert.Contains("[HttpPost(\"allocation-preview\")]", controller);
        Assert.Contains("[HttpPost(\"allocation-generate\")]", controller);
        Assert.Contains("[HttpGet(\"allocation-context\")]", controller);
        Assert.Contains("[HttpGet(\"allocation-batches\")]", controller);
        Assert.Contains("[HttpPost(\"allocation-batches/{batchId:long}/void\")]", controller);
        Assert.Contains("allocation-batches/{batchId:long}", controller);

        // 分摊证据控制器：动作内实时授权（缺失 / 无菜单 fail closed）
        var evidenceController = ReadRepoFile(
            "src", "ERP.Api", "Controllers", "ContainerExpenseAllocationEvidenceController.cs");
        Assert.Contains("EnsureMenuAuthorizedAsync", evidenceController);

        // 通用 CRUD 基类的删除路由已 virtual（费用单可复核范围而不影响其他模块）
        var baseController = ReadRepoFile("src", "ERP.Api", "Controllers", "BaseCrudController.cs");
        Assert.Contains("public virtual async Task<IActionResult> Delete(long id)", baseController);
        Assert.Contains("public virtual async Task<IActionResult> BatchDelete", baseController);
        Assert.Contains("class ExpenseRequestAuthorizationFilter", baseController);
    }
}
