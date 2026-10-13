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
/// ERP-486 供应商比价（<c>api/purchase/quotes</c>）与比价审批（<c>api/purchase/quote-decisions</c>）
/// 入口实时授权「与调用形状无关」单元测试。
/// <para>以真实 <see cref="DefaultHttpContext"/> + 真实启用身份 / 既有菜单 / 权威客户范围驱动真实控制器：
/// 空 <c>Request.Path</c>、已赋值 <c>Request.Path</c> 与 <strong>完全无 <c>HttpContext</c></strong> 三种调用形状必须给出
/// 一致的授权判定；缺失 / 零 / 未知 / 已删除身份一律未认证（2000），禁用 / 缺少或撤销既有菜单一律权限不足（2002），
/// 受限业务员仍按 ERP-097 权威客户范围收口，转单另须既有「采购订单」（<c>purchase-order</c>）菜单；
/// 全部先于任何证据读取、单据号消耗与写入。</para>
/// <para>全部使用隔离内存测试数据，绝不新增生产菜单 / 角色 / 用户授权，也不提供任何测试专用放行开关。</para>
/// </summary>
public class PurchaseQuoteEntryAuthorizationTests
{
    private const string QuotePath = "/api/purchase/quotes";
    private const string DecisionPath = "/api/purchase/quote-decisions";
    private const long ProductId = 4_860_001L;

    [Fact]
    public async Task 无HttpContext的进程内直调_逐入口受控拒绝且零写入()
    {
        using var env = new Env();
        var quote = env.SeedQuote("ERP486-NOCONTEXT", env.CustomerAId);
        var ctl = env.InProcessQuoteController();
        var decisions = env.InProcessDecisionController();

        await AssertAllQuoteEntriesDenied(ctl, quote, ErrorCodes.Unauthorized,
            PurchaseQuoteAuthorizationRules.UnauthorizedText);
        await AssertDecisionEntriesDenied(decisions, quote, ErrorCodes.Unauthorized,
            PurchaseQuoteAuthorizationRules.UnauthorizedText);

        env.AssertZeroMutations(quote.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 绑定请求但无身份_空路径与已赋值路径判定一致(string? path)
    {
        using var env = new Env();
        var quote = env.SeedQuote("ERP486-NOID", env.CustomerAId);

        await AssertAllQuoteEntriesDenied(env.BoundQuoteController(null, path), quote, ErrorCodes.Unauthorized,
            PurchaseQuoteAuthorizationRules.UnauthorizedText);
        await AssertDecisionEntriesDenied(env.BoundDecisionController(null, DecisionPath), quote,
            ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UnauthorizedText);

        env.AssertZeroMutations(quote.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 零与已删除身份_空路径与已赋值路径判定一致(string? path)
    {
        using var env = new Env();
        var quote = env.SeedQuote("ERP486-ZERODEL", env.CustomerAId);
        var deleted = env.SeedEnabledUser(grantQuoteMenu: true, deleted: true);

        await AssertAllQuoteEntriesDenied(env.BoundQuoteController(0L, path), quote, ErrorCodes.Unauthorized,
            PurchaseQuoteAuthorizationRules.UnauthorizedText);
        await AssertAllQuoteEntriesDenied(env.BoundQuoteController(deleted, path), quote, ErrorCodes.Unauthorized,
            PurchaseQuoteAuthorizationRules.UserDeletedText);

        env.AssertZeroMutations(quote.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 禁用身份_空路径与已赋值路径判定一致(string? path)
    {
        using var env = new Env();
        var quote = env.SeedQuote("ERP486-DISABLED", env.CustomerAId);
        var disabled = env.SeedDisabledUser();

        await AssertAllQuoteEntriesDenied(env.BoundQuoteController(disabled, path), quote, ErrorCodes.Forbidden,
            PurchaseQuoteAuthorizationRules.UserDisabledText);
        await AssertDecisionEntriesDenied(env.BoundDecisionController(disabled, DecisionPath), quote,
            ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.UserDisabledText);

        env.AssertZeroMutations(quote.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 缺少或撤销供应商比价菜单_空路径与已赋值路径判定一致(string? path)
    {
        using var env = new Env();
        var quote = env.SeedQuote("ERP486-NOMENU", env.CustomerAId);
        var restricted = env.SeedRestrictedOperator(grantQuoteMenu: false, grantOrderMenu: true);

        await AssertAllQuoteEntriesDenied(env.BoundQuoteController(restricted, path), quote, ErrorCodes.Forbidden,
            PurchaseQuoteAuthorizationRules.MenuDeniedText);

        // 授予既有「供应商比价」菜单后立即放行（绝不缓存）。
        env.GrantMenu(restricted, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        Assert.IsType<OkObjectResult>(await env.BoundQuoteController(restricted, path)
            .GetPaged(new PageQuery { PageSize = 10 }));

        // 撤销后再下一次请求立即收敛。
        env.RevokeMenu(restricted, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        await AssertAllQuoteEntriesDenied(env.BoundQuoteController(restricted, path), quote, ErrorCodes.Forbidden,
            PurchaseQuoteAuthorizationRules.MenuDeniedText);
    }

    // ==================== 4. 真实启用身份：空路径 / 已赋值路径一致放行（既有行为保留） ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 真实启用身份_空路径与已赋值路径一致放行读写与预填(string? path)
    {
        using var env = new Env();
        var view = env.SeedQuote("ERP486-AUTHORIZED", env.CustomerAId);
        var ctl = env.PrivilegedQuoteController(path);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 10 }));
        Assert.IsType<OkObjectResult>(await ctl.GetAll());
        Assert.IsType<OkObjectResult>(await ctl.GetById(view.Id));

        var draft = NewDraft($"PQ-486-CREATE-{Guid.NewGuid():N}", env.CustomerAId);
        Assert.IsType<OkObjectResult>(await ctl.Create(draft));

        var selected = env.SeedConvertibleQuote("ERP486-PREFILL", env.CustomerAId);
        var prefill = Assert.IsType<OkObjectResult>(await ctl.OrderPrefill(selected.Id));
        Assert.NotNull(Assert.IsType<ApiResponse<PurchaseOrderPrefillResult>>(prefill.Value).Data);
    }

    // ==================== 5. 受限业务员：权威客户范围在两种路径下一致下推 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 受限业务员_范围在空路径与已赋值路径一致下推(string? path)
    {
        using var env = new Env();
        var own = env.SeedQuote("ERP486-SCOPE-OWN", env.CustomerAId);
        var foreign = env.SeedQuote("ERP486-SCOPE-FOREIGN", env.CustomerBId);
        var restricted = env.SeedRestrictedOperator(grantQuoteMenu: true, grantOrderMenu: true);
        var ctl = env.BoundQuoteController(restricted, path);

        var page = Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery { PageSize = 100 }));
        var paged = Assert.IsType<ApiResponse<PagedResult<PurchaseQuote>>>(page.Value).Data!;
        Assert.Equal(1, paged.Total);
        Assert.Equal(own.Id, Assert.Single(paged.Items).Id);

        Assert.IsType<OkObjectResult>(await ctl.GetById(own.Id));
        await AssertDeniedAsync(ErrorCodes.NotFound, PurchaseQuoteAuthorizationRules.NotFoundText,
            () => ctl.GetById(foreign.Id));
        await AssertDeniedAsync(ErrorCodes.NotFound, PurchaseQuoteAuthorizationRules.NotFoundText,
            () => ctl.QuotePriceHistory(foreign.Id));
        await AssertDeniedAsync(ErrorCodes.NotFound, PurchaseQuoteAuthorizationRules.NotFoundText,
            () => ctl.BatchOrderPlan(foreign.QuoteNo, null));
    }

    // ==================== 6. 转单：另须既有「采购订单」菜单（先于发号与写入） ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 转单缺少采购订单菜单_受控拒绝且零发号零写入(string? path)
    {
        using var env = new Env();
        var selected = env.SeedConvertibleQuote("ERP486-DESTMENU", env.CustomerAId);
        var restricted = env.SeedRestrictedOperator(grantQuoteMenu: true, grantOrderMenu: false);
        var ctl = env.BoundQuoteController(restricted, path);

        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.DestinationMenuDeniedText,
            () => ctl.OrderPrefill(selected.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.DestinationMenuDeniedText,
            () => ctl.ToPurchaseOrder(selected.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.DestinationMenuDeniedText,
            () => ctl.BatchOrderPlan(selected.QuoteNo, null));
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.DestinationMenuDeniedText,
            () => ctl.BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = selected.QuoteNo }));

        Assert.Empty(env.Db.PurchaseOrders);
        Assert.Equal(0L, env.PurchaseOrderSequence());
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == selected.Id).Status);
    }

    // ==================== 7. 授权转单：既有流程 / 发号 / 留痕一字不改 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(QuotePath)]
    public async Task 授权转单_既有采购订单菜单_保留既有转换与发号(string? path)
    {
        using var env = new Env();
        var selected = env.SeedConvertibleQuote("ERP486-CONVERT", env.CustomerAId);
        var restricted = env.SeedRestrictedOperator(grantQuoteMenu: true, grantOrderMenu: true);
        var ctl = env.BoundQuoteController(restricted, path);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToPurchaseOrder(selected.Id));
        var result = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Data!;
        Assert.StartsWith("PO", result.OrderNo);

        var stored = env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == selected.Id);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, stored.Status);
        Assert.Equal(result.OrderNo, stored.RefOrderNo);
        Assert.Single(env.Db.PurchaseOrders);
        Assert.Equal(1L, env.PurchaseOrderSequence());
    }

    // ==================== 8. 拒绝调用不读取证据 / 不消耗单号（审批决定入口） ====================

    [Fact]
    public async Task 审批决定入口_无身份_受控拒绝且零决定()
    {
        using var env = new Env();
        var quote = env.SeedQuote("ERP486-DECISION", env.CustomerAId);
        var decisionsBefore = env.Db.PurchaseQuoteDecisions.Count();

        var decisions = env.InProcessDecisionController();
        await AssertDeniedAsync(ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UnauthorizedText,
            () => decisions.BatchStatus(quote.QuoteNo));
        await AssertDeniedAsync(ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UnauthorizedText,
            () => decisions.Decide(new PurchaseQuoteDecisionRequest
            {
                QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved
            }));

        Assert.Equal(decisionsBefore, env.Db.PurchaseQuoteDecisions.Count());
        Assert.Equal(PurchaseQuoteMutationRules.PendingStatus,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).Status);
    }


    // ==================== 9. 源码契约 ====================

    [Fact]
    public void 契约_授权与调用形状无关且无请求路径豁免()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteController.cs");
        Assert.DoesNotContain("RequiresLiveAuthorization", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureDestinationAuthorizedAsync", controller);

        var decision = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteDecisionController.cs");
        Assert.DoesNotContain("RequiresLiveAuthorization", decision, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowAnonymous", decision, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync", decision);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureLiveActorAsync", decision);

        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseQuoteAuthorizationRules.cs");
        Assert.Contains("PathIndependenceText", rules);
        Assert.Contains("与请求形状无关", rules);

        var doc = ReadSource("docs", "purchase-quote-entry-authorization.md");
        Assert.Contains("purchase-quote", doc);
        Assert.Contains("purchase-order", doc);
        Assert.Contains("Request.Path", doc);
        Assert.Contains("HttpContext", doc);
        Assert.Contains("(localdb)\\NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
    }

    // ==================== 断言与源码读取 ====================

    private static async Task AssertAllQuoteEntriesDenied(PurchaseQuoteController ctl, PurchaseQuote quote,
        int code, string text)
    {
        await AssertDeniedAsync(code, text, () => ctl.GetPaged(new PageQuery { PageSize = 10 }));
        await AssertDeniedAsync(code, text, () => ctl.GetAll());
        await AssertDeniedAsync(code, text, () => ctl.GetById(quote.Id));
        await AssertDeniedAsync(code, text, () => ctl.Create(NewDraft($"PQ-486-DENY-{Guid.NewGuid():N}", null)));
        await AssertDeniedAsync(code, text, () => ctl.Update(quote.Id, NewDraft($"PQ-486-DENY-{Guid.NewGuid():N}", null)));
        await AssertDeniedAsync(code, text, () => ctl.Delete(quote.Id));
        await AssertDeniedAsync(code, text, () => ctl.BatchDelete(new List<long> { quote.Id }));
        await AssertDeniedAsync(code, text, () => ctl.OrderPrefill(quote.Id));
        await AssertDeniedAsync(code, text, () => ctl.ToPurchaseOrder(quote.Id));
        await AssertDeniedAsync(code, text, () => ctl.BatchOrderPlan(quote.QuoteNo, null));
        await AssertDeniedAsync(code, text,
            () => ctl.BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = quote.QuoteNo }));
        await AssertDeniedAsync(code, text,
            () => ctl.PriceHistory(new PurchaseQuotePriceHistoryQuery { ProductId = ProductId }));
        await AssertDeniedAsync(code, text, () => ctl.QuotePriceHistory(quote.Id));
        await AssertDeniedAsync(code, text, () => ctl.OrderPriceVariance(new PurchaseQuoteOrderPriceVarianceQuery()));
        await AssertDeniedAsync(code, text, () => ctl.ConversionFunnel(new PurchaseQuoteConversionFunnelQuery()));
    }

    private static async Task AssertDecisionEntriesDenied(PurchaseQuoteDecisionController ctl, PurchaseQuote quote,
        int code, string text)
    {
        await AssertDeniedAsync(code, text, () => ctl.BatchStatus(quote.QuoteNo));
        await AssertDeniedAsync(code, text, () => ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = quote.Id, Decision = PurchaseQuoteApproval.Approved
        }));
    }

    private static async Task AssertDeniedAsync(int code, string text, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(text, ex.Message);
    }

    private static PurchaseQuote NewDraft(string quoteNo, long? customerId) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 10, 1),
        ProductId = ProductId,
        ProductName = "ERP486 商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 10m,
        SupplierId = 88L,
        SupplierName = "ERP486 供应商",
        SupplierType = "档口",
        QuotePrice = 3m,
        TotalAmount = 30m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = PurchaseQuoteMutationRules.PendingStatus,
        CustomerId = customerId,
        CustomerName = "ERP486 客户",
        Remark = "ERP486_TEST"
    };

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray()));
    }


    // ==================== 隔离内存测试夹具（真实身份 / 菜单 / 客户范围） ====================

    private sealed class Env : IDisposable
    {
        public Env()
        {
            Db = TestDbFactory.Create();
            CustomerAId = SeedCustomer("ERP486 可见客户A");
            CustomerBId = SeedCustomer("ERP486 隐藏客户B");
            Db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
            {
                DocumentType = DocumentType.PurchaseOrder,
                RuleCode = $"PO-486-{Guid.NewGuid():N}",
                RuleName = "ERP486 采购订单号",
                Prefix = "PO",
                DateFormat = "yyyyMMdd",
                SerialLength = 4,
                Separator = string.Empty,
                CurrentSequence = 0
            });
            Db.SaveChanges();
            PrivilegedUserId = SeedPrivilegedUser();
        }

        public ErpDbContext Db { get; }
        public long CustomerAId { get; }
        public long CustomerBId { get; }
        public long PrivilegedUserId { get; }

        public long PurchaseOrderSequence()
            => Db.SysDocumentNumberRules.AsNoTracking()
                .Single(r => r.DocumentType == DocumentType.PurchaseOrder).CurrentSequence;

        public PurchaseQuoteController InProcessQuoteController()
            => new(new GenericService<PurchaseQuote>(Db), Db, new DocumentNumberService(Db));

        public PurchaseQuoteDecisionController InProcessDecisionController() => new(Db);

        public PurchaseQuoteController BoundQuoteController(long? userId, string? path)
            => Bind(new PurchaseQuoteController(new GenericService<PurchaseQuote>(Db), Db,
                new DocumentNumberService(Db)), userId, path);

        public PurchaseQuoteDecisionController BoundDecisionController(long? userId, string? path)
            => Bind(new PurchaseQuoteDecisionController(Db), userId, path);

        public PurchaseQuoteController PrivilegedQuoteController(string? path)
            => Bind(new PurchaseQuoteController(new GenericService<PurchaseQuote>(Db), Db,
                new DocumentNumberService(Db)), PrivilegedUserId, path);

        /// <summary>把身份绑定到请求上下文；<paramref name="path"/> 为 <c>null</c> 时刻意不设置 <c>Request.Path</c>。</summary>
        private static T Bind<T>(T controller, long? userId, string? path) where T : ControllerBase
        {
            var http = new DefaultHttpContext();
            if (path is not null) http.Request.Path = path;
            http.User = userId.HasValue
                ? new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
                : new ClaimsPrincipal(new ClaimsIdentity());
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }

        // -------- 既有身份 / 菜单 / 客户范围（隔离测试数据，绝不改生产种子） --------

        private long SeedCustomer(string name)
        {
            var customer = new BaseCustomer
            {
                CustomerCode = $"C-486-{Guid.NewGuid():N}", CustomerName = name, Status = 1, CreditStatus = "正常"
            };
            Db.BaseCustomers.Add(customer);
            Db.SaveChanges();
            return customer.Id;
        }

        /// <summary>既有特权身份（系统内置角色 → 不受限既有数据范围）+ 既有「供应商比价」/「采购订单」菜单。</summary>
        public long SeedPrivilegedUser()
        {
            var code = $"erp486-p-{Guid.NewGuid():N}";
            var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
            Db.SysRoles.Add(role);
            Db.SaveChanges();
            var user = new SysUser
            {
                UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
                DisplayName = "ERP486 特权测试账号", Status = UserStatus.Enabled
            };
            Db.SysUsers.Add(user);
            Db.SaveChanges();
            Db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            Db.SaveChanges();
            GrantMenu(user.Id, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
            GrantMenu(user.Id, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
            return user.Id;
        }

        public long SeedEnabledUser(bool grantQuoteMenu, bool deleted)
        {
            var code = $"erp486-u-{Guid.NewGuid():N}";
            var user = new SysUser
            {
                UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
                Status = UserStatus.Enabled, IsDeleted = deleted
            };
            Db.SysUsers.Add(user);
            Db.SaveChanges();
            AttachPlainRole(user.Id, grantQuoteMenu, grantOrderMenu: false);
            return user.Id;
        }

        public long SeedDisabledUser()
        {
            var code = $"erp486-d-{Guid.NewGuid():N}";
            var user = new SysUser
            {
                UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
                Status = UserStatus.Disabled
            };
            Db.SysUsers.Add(user);
            Db.SaveChanges();
            AttachPlainRole(user.Id, grantQuoteMenu: true, grantOrderMenu: false);
            return user.Id;
        }

        /// <summary>受限业务员：员工编码 = 登录名（ERP-097 映射），只分配到可见客户 A。</summary>
        public long SeedRestrictedOperator(bool grantQuoteMenu, bool grantOrderMenu)
        {
            var code = $"erp486-o-{Guid.NewGuid():N}";
            var employee = new BaseEmployee
            {
                EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
            };
            Db.BaseEmployees.Add(employee);
            Db.SaveChanges();

            var customerA = Db.BaseCustomers.Single(c => c.Id == CustomerAId);
            customerA.EmpId = employee.Id;
            Db.SaveChanges();

            var user = new SysUser
            {
                UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
                Status = UserStatus.Enabled
            };
            Db.SysUsers.Add(user);
            Db.SaveChanges();
            AttachPlainRole(user.Id, grantQuoteMenu, grantOrderMenu);
            return user.Id;
        }

        private void AttachPlainRole(long userId, bool grantQuoteMenu, bool grantOrderMenu)
        {
            var code = $"erp486-r-{Guid.NewGuid():N}";
            var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
            Db.SysRoles.Add(role);
            Db.SaveChanges();
            Db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
            Db.SaveChanges();
            if (grantQuoteMenu) GrantMenu(userId, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
            if (grantOrderMenu) GrantMenu(userId, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        }

        public void GrantMenu(long userId, string menuCode)
        {
            var roleId = Db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
                .Select(ur => ur.RoleId).First();
            var menu = Db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                Db.SysMenus.Add(menu);
                Db.SaveChanges();
            }
            Db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            Db.SaveChanges();
        }

        public void RevokeMenu(long userId, string menuCode)
        {
            var roleId = Db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
                .Select(ur => ur.RoleId).First();
            var menuId = Db.SysMenus.Where(m => m.MenuCode == menuCode).Select(m => m.Id).First();
            foreach (var link in Db.SysRoleMenus.Where(rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted))
                link.IsDeleted = true;
            Db.SaveChanges();
        }

        // -------- 来源行 --------

        public PurchaseQuote SeedQuote(string quoteNo, long? customerId)
            => SeedQuoteCore(quoteNo, customerId, selected: false, status: PurchaseQuoteMutationRules.PendingStatus);

        public PurchaseQuote SeedConvertibleQuote(string quoteNo, long customerId)
        {
            var quote = SeedQuoteCore(quoteNo, customerId, selected: true, status: PurchaseQuoteConversion.SelectedStatus);
            Db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
            {
                QuoteId = quote.Id,
                QuoteNo = quote.QuoteNo,
                Decision = PurchaseQuoteApproval.Approved,
                SelectedSupplierId = quote.SupplierId,
                SelectedSupplierName = quote.SupplierName,
                DecisionBasis = "单价最优",
                DecidedBy = 1L,
                DecidedByName = "ERP486 审批人",
                DecidedAt = DateTime.Now,
                DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
            });
            Db.SaveChanges();
            return quote;
        }

        private PurchaseQuote SeedQuoteCore(string quoteNo, long? customerId, bool selected, string status)
        {
            var quote = new PurchaseQuote
            {
                QuoteNo = quoteNo,
                QuoteDate = DateTime.Today,
                ProductId = ProductId,
                ProductName = "ERP486 商品",
                Spec = "大号",
                Unit = "PCS",
                Quantity = 10m,
                SupplierId = 88L,
                SupplierName = "ERP486 供应商",
                SupplierType = "档口",
                QuotePrice = 3m,
                TotalAmount = 30m,
                Currency = "USD",
                TaxIncluded = false,
                DeliveryDays = 10,
                MinOrderQty = 1,
                PaymentTerms = "现结",
                IsSelected = selected,
                Status = status,
                CustomerId = customerId,
                CustomerName = "ERP486 客户快照",
                RefOrderNo = string.Empty,
                Remark = "ERP486_TEST"
            };
            Db.PurchaseQuotes.Add(quote);
            Db.SaveChanges();
            return quote;
        }

        /// <summary>被拒绝的调用：来源状态 / 软删除标志 / 采购订单 / 明细 / 决定 / 单据号一律零变更。</summary>
        public void AssertZeroMutations(long quoteId)
        {
            var quote = Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quoteId);
            Assert.Equal(PurchaseQuoteMutationRules.PendingStatus, quote.Status);
            Assert.False(quote.IsDeleted);
            Assert.True(string.IsNullOrEmpty(quote.RefOrderNo));
            Assert.Empty(Db.PurchaseOrders);
            Assert.Empty(Db.PurchaseOrderDetails);
            Assert.Empty(Db.PurchaseQuoteDecisions);
            Assert.Equal(0L, PurchaseOrderSequence());
        }

        public void Dispose() => Db.Dispose();
    }


}

/// <summary>
/// ERP-486：为既有进程内（未绑定 <c>Request.Path</c>）夹具提供「真实启用身份 + 既有菜单授权」的最小适配脚手架。
/// <para>在隔离内存测试数据中播种既有系统内置特权角色（不受限既有数据范围）与既有「供应商比价」/「采购订单」菜单，
/// 再把该身份绑定到控制器请求上下文（<b>刻意不设置 <c>Request.Path</c></b>，证明授权与请求形状无关）。</para>
/// <para>绝不新增生产菜单 / 角色 / 用户授权，也不提供任何测试专用放行开关；缺失身份的历史直调一律 fail closed。</para>
/// </summary>
internal static class PurchaseQuoteTestAuthority
{
    public const string DefaultDisplayName = "ERP486 特权测试账号";

    /// <summary>播种真实启用身份（既有系统内置特权角色 + 既有菜单授权）并返回其用户 Id。</summary>
    public static long SeedAuthorizedUser(ErpDbContext db, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var code = $"erp486-a-{Guid.NewGuid():N}";
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName!,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in new[]
                 {
                     PurchaseQuoteAuthorizationRules.RequiredMenuCode,
                     PurchaseQuoteAuthorizationRules.DestinationMenuCode
                 })
        {
            var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                db.SaveChanges();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user.Id;
    }

    /// <summary>把已播种的真实启用身份绑定到控制器（刻意不设置 <c>Request.Path</c>）。</summary>
    public static T Bind<T>(T controller, long userId) where T : ControllerBase
    {
        ArgumentNullException.ThrowIfNull(controller);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>既有进程内夹具：比价控制器 + 真实启用身份（既有菜单 / 不受限范围），不带请求路径。</summary>
    public static PurchaseQuoteController NewQuoteController(ErpDbContext db, string? displayName = null)
        => Bind(new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db)), SeedAuthorizedUser(db, displayName));

    /// <summary>既有进程内夹具：审批控制器 + 真实启用身份（既有菜单 / 不受限范围），不带请求路径。</summary>
    public static PurchaseQuoteDecisionController NewDecisionController(ErpDbContext db, string? displayName = null)
        => Bind(new PurchaseQuoteDecisionController(db), SeedAuthorizedUser(db, displayName));
}

