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
/// ERP-416 供应商比价（<c>api/purchase/quotes</c>）与比价审批（<c>api/purchase/quote-decisions</c>）
/// 实时授权、持久化归属与写入护栏单元测试。
/// <para>覆盖：受限业务员（既有「供应商比价」菜单 + ERP-097 客户数据范围）在本人 / 他人 / 空归属 / 已删除比价行上的
/// 列表 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除、带入预填 / 单行转单 / 批次计划 / 批次转单、报价历史 /
/// 价格差异 / 转化漏斗与审批状态 / 记录决定授权；范围先于计数 / 分页 / 分组下推；<c>CustomerName</c> 与
/// <c>RefOrderNo</c> 文本都不是授权依据；空归属只在不受限口径下可见；批次混入范围外行即整批拒绝且零写入；
/// 无身份 / 已删除 / 已禁用 / 无菜单一律 fail closed；转单另须既有「采购订单」菜单与权威目的地范围；
/// 特权账号保留既有不受限口径；进程内无身份直调保持既有免授权口径。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class PurchaseQuoteAuthorizationTests
{
    private const string BasePath = "/api/purchase/quotes";

    // ==================== 0. 脚手架 ====================

    private static DefaultHttpContext HttpFor(long? userId, string path = BasePath)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return http;
    }

    private static PurchaseQuoteController QuoteController(ErpDbContext db, long? userId, bool httpBound = true)
    {
        var controller = new PurchaseQuoteController(
            new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));
        if (httpBound)
            controller.ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) };
        return controller;
    }

    private static PurchaseQuoteDecisionController DecisionController(ErpDbContext db, long? userId, bool httpBound = true)
    {
        var controller = new PurchaseQuoteDecisionController(db);
        if (httpBound)
            controller.ControllerContext = new ControllerContext { HttpContext = HttpFor(userId, BasePath) };
        return controller;
    }

    /// <summary>进程内无 HTTP 管线直调（历史单元测试口径：保持既有免授权）。</summary>
    private static PurchaseQuoteController InProcessController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

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
        Assert.Equal(PurchaseQuoteAuthorizationRules.NotFoundText, ex.Message);
    }

    private static async Task AssertDeniedAsync(int code, string text, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(text, ex.Message);
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

    // ==================== 1. 场景夹具 ====================

    private sealed class Env : IDisposable
    {
        public ErpDbContext Db { get; init; } = null!;
        public long PrivilegedUserId { get; set; }
        public long OperatorUserId { get; set; }
        public long CustomerAId { get; set; }
        public long CustomerBId { get; set; }
        public long OwnQuoteId { get; set; }
        public long ForeignQuoteId { get; set; }
        public long NullOwnerQuoteId { get; set; }
        public long DeletedQuoteId { get; set; }
        public string OwnQuoteNo { get; set; } = string.Empty;
        public string BatchNo { get; set; } = string.Empty;
        public long BatchOwnLineId { get; set; }
        public long BatchForeignLineId { get; set; }
        public void Dispose() => Db.Dispose();
    }

    /// <summary>
    /// 播种：特权账号、受限业务员（既有「供应商比价」菜单 + 客户范围 A）、客户 A/B、本人 / 他人 / 空归属 / 已删除
    /// 比价行，以及一个「本人行 + 他人行」的混合批次。空归属行的 <c>CustomerName</c> 刻意写成 A 的客户名，
    /// 用于证明 <c>CustomerName</c> 不是授权依据。
    /// </summary>
    private static Env CreateEnv()
    {
        var db = TestDbFactory.Create();
        var env = new Env { Db = db };

        var privilegedRole = new SysRole
        {
            RoleName = $"erp416-p-{Guid.NewGuid():N}", RoleCode = $"erp416-p-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        db.SaveChanges();
        var privileged = new SysUser
        {
            UserName = $"erp416-p-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP416 特权账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(privileged);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        db.SaveChanges();
        env.PrivilegedUserId = privileged.Id;

        var code = $"erp416-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var restricted = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(restricted);
        db.SaveChanges();
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = role.Id });
        GrantMenu(db, role.Id, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        db.SaveChanges();
        env.OperatorUserId = restricted.Id;

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}", CustomerName = "ERP416 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}", CustomerName = "ERP416 隐藏客户",
            EmpId = null, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        db.SaveChanges();
        env.CustomerAId = customerA.Id;
        env.CustomerBId = customerB.Id;

        var own = AddQuote(db, $"PQ-OWN-{Guid.NewGuid():N}", customerA.Id, "ERP416 可见客户");
        env.OwnQuoteId = own.Id;
        env.OwnQuoteNo = own.QuoteNo;

        env.ForeignQuoteId = AddQuote(db, $"PQ-FOREIGN-{Guid.NewGuid():N}", customerB.Id, "ERP416 隐藏客户").Id;
        // 空归属：CustomerName 刻意写成受限账号可见客户，但 CustomerId 为空（CustomerName 不是授权依据）。
        env.NullOwnerQuoteId = AddQuote(db, $"PQ-NULL-{Guid.NewGuid():N}", null, "ERP416 可见客户").Id;
        env.DeletedQuoteId = AddQuote(db, $"PQ-DEL-{Guid.NewGuid():N}", customerA.Id, "ERP416 可见客户",
            deleted: true).Id;

        env.BatchNo = $"PQ-BATCH-{Guid.NewGuid():N}";
        env.BatchOwnLineId = AddQuote(db, env.BatchNo, customerA.Id, "ERP416 可见客户").Id;
        env.BatchForeignLineId = AddQuote(db, env.BatchNo, customerB.Id, "ERP416 隐藏客户").Id;

        return env;
    }

    private static PurchaseQuote AddQuote(ErpDbContext db, string quoteNo, long? customerId, string customerName,
        bool deleted = false, bool selected = true, string status = PurchaseQuoteConversion.SelectedStatus,
        string refOrderNo = "")
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = new DateTime(2026, 9, 1),
            ProductId = 310L,
            ProductName = "ERP416 商品",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 100m,
            SupplierId = 88L,
            SupplierName = "ERP416 档口",
            SupplierType = "档口",
            QuotePrice = 2m,
            TotalAmount = 200m,
            Currency = "USD",
            TaxIncluded = false,
            DeliveryDays = 10,
            MinOrderQty = 1,
            PaymentTerms = "现结",
            IsSelected = selected,
            Status = status,
            CustomerId = customerId,
            CustomerName = customerName,
            RefOrderNo = refOrderNo,
            Remark = "ERP416_TEST",
            IsDeleted = deleted
        };
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();

        if (selected && status == PurchaseQuoteConversion.SelectedStatus)
        {
            db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
            {
                QuoteId = quote.Id,
                QuoteNo = quote.QuoteNo,
                Decision = PurchaseQuoteApproval.Approved,
                SelectedSupplierId = quote.SupplierId,
                SelectedSupplierName = quote.SupplierName,
                DecisionBasis = "单价最优",
                DecidedBy = 1L,
                DecidedByName = "审批人",
                DecidedAt = DateTime.Now,
                DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
            });
            db.SaveChanges();
        }

        return quote;
    }

    // ==================== 2. 读取入口：范围先于计数 / 分页 ====================

    [Fact]
    public async Task 受限业务员_列表与全部按持久化归属下推_空归属与范围外不可见()
    {
        using var env = CreateEnv();
        var ctl = QuoteController(env.Db, env.OperatorUserId);

        // 台账：5 张未删除比价行里只剩本人客户 2 张（本人 + 混合批次本人行），Total 只计可访问行。
        var page = AssertOk<PagedResult<PurchaseQuote>>(await ctl.GetPaged(new PageQuery { PageSize = 100 }));
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, q => Assert.Equal(env.CustomerAId, q.CustomerId));
        Assert.DoesNotContain(page.Items, q => q.Id == env.ForeignQuoteId);
        Assert.DoesNotContain(page.Items, q => q.Id == env.NullOwnerQuoteId);

        var all = AssertOk<List<PurchaseQuote>>(await ctl.GetAll());
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, q => q.Id == env.ForeignQuoteId || q.Id == env.NullOwnerQuoteId);
    }

    [Fact]
    public async Task 受限业务员_详情范围外已删除不存在返回同一非披露错误_CustomerName不是依据()
    {
        using var env = CreateEnv();
        var ctl = QuoteController(env.Db, env.OperatorUserId);

        var own = AssertOk<PurchaseQuote>(await ctl.GetById(env.OwnQuoteId));
        Assert.Equal(env.OwnQuoteId, own.Id);

        await AssertNotFoundAsync(() => ctl.GetById(env.ForeignQuoteId));
        await AssertNotFoundAsync(() => ctl.GetById(env.NullOwnerQuoteId));   // CustomerName 写成可见客户也无用
        await AssertNotFoundAsync(() => ctl.GetById(env.DeletedQuoteId));
        await AssertNotFoundAsync(() => ctl.GetById(9_416_999L));
    }

    // ==================== 3. 写入口：授权先于计数 / 字段替换，被拒零写入 ====================

    [Fact]
    public async Task 受限业务员_新增与改派按拟提交归属授权_范围外与空归属拒绝且零写入()
    {
        using var env = CreateEnv();
        var ctl = QuoteController(env.Db, env.OperatorUserId);
        var before = env.Db.PurchaseQuotes.Count();

        // 新增：本人客户放行。
        var created = AssertOk<PurchaseQuote>(await ctl.Create(NewQuote($"PQ-NEW-{Guid.NewGuid():N}", env.CustomerAId)));
        Assert.Equal(env.CustomerAId, created.CustomerId);

        // 新增：范围外客户 / 空归属客户一律拒绝（空归属只在不受限口径下可用）。
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.ProposedCustomerDeniedText,
            () => ctl.Create(NewQuote($"PQ-NEW-{Guid.NewGuid():N}", env.CustomerBId)));
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.ProposedCustomerDeniedText,
            () => ctl.Create(NewQuote($"PQ-NEW-{Guid.NewGuid():N}", null)));
        Assert.Equal(before + 1, env.Db.PurchaseQuotes.Count());

        // 改派：本人行改到范围外客户被拒，原行归属保持不变。
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.ProposedCustomerDeniedText,
            () => ctl.Update(env.OwnQuoteId, NewQuote(env.OwnQuoteNo, env.CustomerBId)));
        Assert.Equal(env.CustomerAId,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.OwnQuoteId).CustomerId);

        // 改派：范围外行本身不可改（同一非披露错误）。
        await AssertNotFoundAsync(() => ctl.Update(env.ForeignQuoteId, NewQuote("PQ-X", env.CustomerAId)));
    }

    [Fact]
    public async Task 受限业务员_删除与批量删除混入范围外即整批拒绝且零写入()
    {
        using var env = CreateEnv();
        var ctl = QuoteController(env.Db, env.OperatorUserId);

        // 单条删除范围外行：被拒，行未被删除。
        await AssertNotFoundAsync(() => ctl.Delete(env.ForeignQuoteId));
        Assert.False(env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.ForeignQuoteId).IsDeleted);

        // 批量删除混入范围外行：整批拒绝，连本人行也不删除（无部分写入）。
        await AssertNotFoundAsync(() => ctl.BatchDelete(new List<long> { env.OwnQuoteId, env.ForeignQuoteId }));
        Assert.False(env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.OwnQuoteId).IsDeleted);
        Assert.False(env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.ForeignQuoteId).IsDeleted);

        // 仅本人行：放行——但必须是真正未决定 / 未转换的草稿（ERP-419 修复 ERP-416 授权夹具的冲突前提：
        // 本人登记行本身带既有批准历史且缺失创建人元数据，绝不因操作人元数据缺失被当作可删除）。
        var draft = AddQuote(env.Db, $"PQ-OWN-DRAFT-{Guid.NewGuid():N}", env.CustomerAId, "ERP416 可见客户",
            selected: false, status: PurchaseQuoteMutationRules.PendingStatus);
        Assert.IsType<OkObjectResult>(await ctl.Delete(draft.Id));
        Assert.True(env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == draft.Id).IsDeleted);

        // 已批准历史（缺失创建人元数据 = 未知归属）不是删除许可：来源与决定一律保留。
        var frozen = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(env.OwnQuoteId));
        Assert.Equal(ErrorCodes.RuleConflict, frozen.Code);
        Assert.Equal(PurchaseQuoteMutationRules.DecidedNoDeleteText, frozen.Message);
        Assert.False(env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.OwnQuoteId).IsDeleted);
        Assert.False(env.Db.PurchaseQuoteDecisions.AsNoTracking()
            .Single(d => d.QuoteId == env.OwnQuoteId).IsDeleted);
    }

    private static PurchaseQuote NewQuote(string quoteNo, long? customerId) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 9, 2),
        ProductId = 310L,
        ProductName = "ERP416 新商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = 10m,
        SupplierId = 88L,
        SupplierName = "ERP416 档口",
        SupplierType = "档口",
        QuotePrice = 3m,
        TotalAmount = 30m,
        Currency = "USD",
        TaxIncluded = false,
        DeliveryDays = 5,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = "待比较",
        CustomerId = customerId,
        CustomerName = "伪造客户名",
        Remark = "ERP416_NEW"
    };

    private static void GrantMenuToUser(ErpDbContext db, long userId, string menuCode)
    {
        var roleId = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).First();
        GrantMenu(db, roleId, menuCode);
        db.SaveChanges();
    }

    // ==================== 4. 转单：来源归属 + 目的地菜单 + 权威目的地范围，先于发号与写入 ====================

    [Fact]
    public async Task 受限业务员_带入预填与单行转单按来源与目的地归属授权_拒绝零写入()
    {
        using var env = CreateEnv();
        var ctl = QuoteController(env.Db, env.OperatorUserId);

        // 缺少既有「采购订单」菜单：带入预填与真实转单都 fail closed，且不产生任何采购订单。
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.DestinationMenuDeniedText,
            () => ctl.OrderPrefill(env.OwnQuoteId));
        await AssertDeniedAsync(ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.DestinationMenuDeniedText,
            () => ctl.ToPurchaseOrder(env.OwnQuoteId));
        Assert.Equal(0, env.Db.PurchaseOrders.Count());

        // 具备「采购订单」菜单后：本人来源放行（预填不落库），范围外 / 空归属来源仍非披露拒绝。
        GrantMenuToUser(env.Db, env.OperatorUserId, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        var prefill = AssertOk<PurchaseOrderPrefillResult>(await ctl.OrderPrefill(env.OwnQuoteId));
        Assert.Equal(env.OwnQuoteId, prefill.SourceId);
        Assert.Equal(0, env.Db.PurchaseOrders.Count());

        await AssertNotFoundAsync(() => ctl.OrderPrefill(env.ForeignQuoteId));
        await AssertNotFoundAsync(() => ctl.OrderPrefill(env.NullOwnerQuoteId));
        await AssertNotFoundAsync(() => ctl.ToPurchaseOrder(env.ForeignQuoteId));
        Assert.Equal(0, env.Db.PurchaseOrders.Count());

        var converted = AssertOk<PurchaseOrderConversionResult>(await ctl.ToPurchaseOrder(env.OwnQuoteId));
        Assert.True(converted.Id > 0);
        Assert.Single(env.Db.PurchaseOrders);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.OwnQuoteId).Status);
    }

    [Fact]
    public async Task 转单权威目的地范围_归属销售订单客户越界即拒绝且不发号不写入()
    {
        using var env = CreateEnv();
        GrantMenuToUser(env.Db, env.OperatorUserId, PurchaseQuoteAuthorizationRules.DestinationMenuCode);

        // 归属销售订单属于他人客户：比价行本身（客户 A）在范围内，但目的地权威归属（B）越界。
        var soNo = $"SO-416-{Guid.NewGuid():N}";
        env.Db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = soNo, OrderDate = DateTime.Today, CustomerId = env.CustomerBId,
            Currency = Currency.USD, ExchangeRate = 7.2m, TotalAmount = 100m, Status = DocumentStatus.Approved
        });
        env.Db.SaveChanges();
        var quote = AddQuote(env.Db, $"PQ-DEST-{Guid.NewGuid():N}", env.CustomerAId, "ERP416 可见客户", refOrderNo: soNo);

        const string scopeDenied = "当前账号的客户数据范围不包含该采购订单归属客户（fail closed，不泄露范围外采购订单）";
        var ctl = QuoteController(env.Db, env.OperatorUserId);
        await AssertDeniedAsync(ErrorCodes.Forbidden, scopeDenied, () => ctl.OrderPrefill(quote.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, scopeDenied, () => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(0, env.Db.PurchaseOrders.Count());
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == quote.Id).Status);
    }

    // ==================== 5. 批次：整批判定，混入范围外行即拒绝且零写入 ====================

    [Fact]
    public async Task 受限业务员_批次混入范围外行即整批拒绝_计划与转单都非披露且零写入()
    {
        using var env = CreateEnv();
        GrantMenuToUser(env.Db, env.OperatorUserId, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        var ctl = QuoteController(env.Db, env.OperatorUserId);
        var decisionsBefore = env.Db.PurchaseQuoteDecisions.Count();

        // 混合批次：计划 / 转单 / 显式行清单都整批拒绝（不返回隐藏行计数 / 跳过原因，也不落任何单据）。
        await AssertNotFoundAsync(() => ctl.BatchOrderPlan(env.BatchNo, null));
        await AssertNotFoundAsync(() => ctl.BatchOrderPlan(null, env.ForeignQuoteId));
        await AssertNotFoundAsync(() => ctl.BatchToOrder(new PurchaseQuoteBatchConversionRequest
        {
            QuoteNo = env.BatchNo, LineIds = new List<long> { env.BatchOwnLineId, env.BatchForeignLineId }
        }));
        await AssertNotFoundAsync(() => ctl.BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = env.BatchNo }));

        Assert.Equal(0, env.Db.PurchaseOrders.Count());
        Assert.Equal(decisionsBefore, env.Db.PurchaseQuoteDecisions.Count());
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.BatchOwnLineId).Status);

        // 纯本人批次：计划与转单都放行。
        var ownBatch = $"PQ-OWNBATCH-{Guid.NewGuid():N}";
        var lineA = AddQuote(env.Db, ownBatch, env.CustomerAId, "ERP416 可见客户");
        var plan = AssertOk<PurchaseQuoteBatchPlan>(await ctl.BatchOrderPlan(ownBatch, null));
        Assert.Equal(1, plan.EligibleLineCount);

        var result = AssertOk<PurchaseQuoteBatchConversionResult>(
            await ctl.BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = ownBatch }));
        Assert.Equal(1, result.OrderCount);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus,
            env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == lineA.Id).Status);
    }

    // ==================== 6. 审批：批次状态与记录决定都按归属授权 ====================

    [Fact]
    public async Task 受限业务员_审批批次与记录决定按归属授权_范围外拒绝且不追加决定()
    {
        using var env = CreateEnv();
        var ctl = DecisionController(env.Db, env.OperatorUserId);

        // 混合批次状态：整批非披露拒绝（不暴露隐藏行计数 / 决定历史）。
        await AssertNotFoundAsync(() => ctl.BatchStatus(env.BatchNo));

        // 纯本人批次：放行并返回逐行状态。
        var ownBatch = $"PQ-APVBATCH-{Guid.NewGuid():N}";
        var pending = AddQuote(env.Db, ownBatch, env.CustomerAId, "ERP416 可见客户", selected: false, status: "待比较");
        var batch = AssertOk<PurchaseQuoteDecisionBatch>(await ctl.BatchStatus(ownBatch));
        Assert.Equal(1, batch.PendingCount);

        // 记录决定：范围外 / 空归属 / 已删除比价行一律非披露拒绝，且不追加任何决定。
        var decisionsBefore = env.Db.PurchaseQuoteDecisions.Count();
        await AssertNotFoundAsync(() => ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = env.ForeignQuoteId, Decision = PurchaseQuoteApproval.Approved, DecidedByName = "伪造决定人"
        }));
        await AssertNotFoundAsync(() => ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = env.NullOwnerQuoteId, Decision = PurchaseQuoteApproval.Approved
        }));
        await AssertNotFoundAsync(() => ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = env.DeletedQuoteId, Decision = PurchaseQuoteApproval.Approved
        }));
        Assert.Equal(decisionsBefore, env.Db.PurchaseQuoteDecisions.Count());

        // 本人比价行：放行（决定人字段不参与授权，但保留既有落库口径）。
        var decision = AssertOk<PurchaseQuoteDecision>(await ctl.Decide(new PurchaseQuoteDecisionRequest
        {
            QuoteId = pending.Id, Decision = PurchaseQuoteApproval.Rejected, DecisionBasis = "交期过长"
        }));
        Assert.Equal(pending.Id, decision.QuoteId);
    }

    // ==================== 7. 派生只读：范围先于计数 / 分页 / 分组 ====================

    private static long SeedDeniedUser(ErpDbContext db, UserStatus status, bool deleted, params string[] menus)
    {
        var code = $"erp416-d-{Guid.NewGuid():N}";
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
        foreach (var menu in menus) GrantMenu(db, role.Id, menu);
        db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task 受限业务员_报价历史价格差异转化漏斗按范围收敛()
    {
        using var env = CreateEnv();

        // 已转采购订单的本人 / 他人行（价格差异只统计「已转」行）＋ 他人客户行。
        var ownConverted = AddQuote(env.Db, $"PQ-OWN-CVT-{Guid.NewGuid():N}", env.CustomerAId, "ERP416 可见客户",
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-ERP416-OWN");
        var foreignConverted = AddQuote(env.Db, $"PQ-FGN-CVT-{Guid.NewGuid():N}", env.CustomerBId, "ERP416 隐藏客户",
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-ERP416-FGN");

        var ctl = QuoteController(env.Db, env.OperatorUserId);

        // 报价历史：只统计本人客户 3 张（本人 / 混合批次本人行 / 本人已转行），隐藏行不进计数。
        var history = AssertOk<PurchaseQuotePriceHistoryView>(await ctl.PriceHistory(
            new PurchaseQuotePriceHistoryQuery { ProductId = 310L, PageSize = 200 }));
        Assert.Equal(3, history.TotalCount);
        Assert.DoesNotContain(history.Groups.SelectMany(g => g.Rows), r => r.QuoteId == env.ForeignQuoteId);

        await AssertNotFoundAsync(() => ctl.QuotePriceHistory(env.ForeignQuoteId));
        Assert.Equal(env.OwnQuoteId,
            AssertOk<PurchaseQuotePriceHistoryView>(await ctl.QuotePriceHistory(env.OwnQuoteId)).ReferenceQuoteId);

        // 价格差异：只统计本人客户的 1 张已转行。
        var variance = AssertOk<PurchaseQuoteOrderPriceVarianceView>(await ctl.OrderPriceVariance(
            new PurchaseQuoteOrderPriceVarianceQuery { PageSize = 200 }));
        Assert.Equal(1, variance.TotalCount);
        Assert.Equal(ownConverted.Id, Assert.Single(variance.Rows).QuoteId);

        await AssertNotFoundAsync(() => ctl.QuoteOrderPriceVariance(foreignConverted.Id));
        Assert.Equal(ownConverted.Id, Assert.Single(
            AssertOk<PurchaseQuoteOrderPriceVarianceView>(await ctl.QuoteOrderPriceVariance(ownConverted.Id)).Rows).QuoteId);

        // 转化漏斗：只统计本人客户 3 行（隐藏行不进批次计数）。
        var funnel = AssertOk<PurchaseQuoteConversionFunnelView>(await ctl.ConversionFunnel(
            new PurchaseQuoteConversionFunnelQuery { PageSize = 200 }));
        Assert.Equal(3, funnel.TotalLineCount);
        Assert.DoesNotContain(funnel.Batches.SelectMany(b => b.Lines), l => l.QuoteId == env.ForeignQuoteId);
    }

    // ==================== 8. 身份 / 菜单拒绝矩阵（逐入口 fail closed 且零写入） ====================

    [Fact]
    public async Task 身份与菜单拒绝矩阵_逐入口fail_closed且零写入()
    {
        using var env = CreateEnv();
        var deletedUser = SeedDeniedUser(env.Db, UserStatus.Enabled, deleted: true,
            PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        var disabledUser = SeedDeniedUser(env.Db, UserStatus.Disabled, deleted: false,
            PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        var menuLessUser = SeedDeniedUser(env.Db, UserStatus.Enabled, deleted: false);
        var wrongMenuUser = SeedDeniedUser(env.Db, UserStatus.Enabled, deleted: false,
            PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        var ordersBefore = env.Db.PurchaseOrders.Count();
        var decisionsBefore = env.Db.PurchaseQuoteDecisions.Count();

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UnauthorizedText),
            (deletedUser, ErrorCodes.Unauthorized, PurchaseQuoteAuthorizationRules.UserDeletedText),
            (disabledUser, ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.UserDisabledText),
            (menuLessUser, ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.MenuDeniedText),
            (wrongMenuUser, ErrorCodes.Forbidden, PurchaseQuoteAuthorizationRules.MenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = QuoteController(env.Db, userId);
            var decisions = DecisionController(env.Db, userId);
            await AssertDeniedAsync(code, text, () => ctl.GetPaged(new PageQuery()));
            await AssertDeniedAsync(code, text, () => ctl.GetAll());
            await AssertDeniedAsync(code, text, () => ctl.GetById(env.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.Create(NewQuote("PQ-X", env.CustomerAId)));
            await AssertDeniedAsync(code, text, () => ctl.Update(env.OwnQuoteId, NewQuote("PQ-X", env.CustomerAId)));
            await AssertDeniedAsync(code, text, () => ctl.Delete(env.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.BatchDelete(new List<long> { env.OwnQuoteId }));
            await AssertDeniedAsync(code, text, () => ctl.OrderPrefill(env.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.ToPurchaseOrder(env.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.BatchOrderPlan(env.BatchNo, null));
            await AssertDeniedAsync(code, text, () => ctl.BatchToOrder(
                new PurchaseQuoteBatchConversionRequest { QuoteNo = env.BatchNo }));
            await AssertDeniedAsync(code, text, () => ctl.PriceHistory(new PurchaseQuotePriceHistoryQuery { ProductId = 310L }));
            await AssertDeniedAsync(code, text, () => ctl.QuotePriceHistory(env.OwnQuoteId));
            await AssertDeniedAsync(code, text, () => ctl.OrderPriceVariance(new PurchaseQuoteOrderPriceVarianceQuery()));
            await AssertDeniedAsync(code, text, () => ctl.ConversionFunnel(new PurchaseQuoteConversionFunnelQuery()));
            await AssertDeniedAsync(code, text, () => decisions.BatchStatus(env.BatchNo));
            await AssertDeniedAsync(code, text, () => decisions.Decide(new PurchaseQuoteDecisionRequest
            {
                QuoteId = env.OwnQuoteId, Decision = PurchaseQuoteApproval.Approved
            }));
        }

        Assert.Equal(ordersBefore, env.Db.PurchaseOrders.Count());
        Assert.Equal(decisionsBefore, env.Db.PurchaseQuoteDecisions.Count());
        Assert.False(env.Db.PurchaseQuotes.AsNoTracking().Single(q => q.Id == env.OwnQuoteId).IsDeleted);
    }

    // ==================== 9. 特权账号 / 进程内直调 ====================

    [Fact]
    public async Task 特权账号_保留既有不受限口径_可见空归属与范围外行并可转单()
    {
        using var env = CreateEnv();
        GrantMenuToUser(env.Db, env.PrivilegedUserId, PurchaseQuoteAuthorizationRules.RequiredMenuCode);
        GrantMenuToUser(env.Db, env.PrivilegedUserId, PurchaseQuoteAuthorizationRules.DestinationMenuCode);
        var ctl = QuoteController(env.Db, env.PrivilegedUserId);

        // 读取：全部未删除行可见（含范围外与空归属）。
        var page = AssertOk<PagedResult<PurchaseQuote>>(await ctl.GetPaged(new PageQuery { PageSize = 100 }));
        Assert.Equal(5, page.Total);
        Assert.Equal(env.ForeignQuoteId, AssertOk<PurchaseQuote>(await ctl.GetById(env.ForeignQuoteId)).Id);
        Assert.Equal(env.NullOwnerQuoteId, AssertOk<PurchaseQuote>(await ctl.GetById(env.NullOwnerQuoteId)).Id);

        // 新增：空归属 / 范围外客户在不受限口径下保留既有可用性。
        Assert.Equal(env.CustomerBId,
            AssertOk<PurchaseQuote>(await ctl.Create(NewQuote($"PQ-P-{Guid.NewGuid():N}", env.CustomerBId))).CustomerId);
        Assert.Null((await AssertOkAsync<PurchaseQuote>(() =>
            ctl.Create(NewQuote($"PQ-P-{Guid.NewGuid():N}", null)))).CustomerId);

        // 转单：特权目的地范围放行范围外来源。
        Assert.Equal(env.ForeignQuoteId, AssertOk<PurchaseOrderPrefillResult>(await ctl.OrderPrefill(env.ForeignQuoteId)).SourceId);
    }

    [Fact]
    public async Task 进程内无身份直调_保持既有免授权口径()
    {
        using var env = CreateEnv();
        var ctl = InProcessController(env.Db);

        var page = AssertOk<PagedResult<PurchaseQuote>>(await ctl.GetPaged(new PageQuery { PageSize = 100 }));
        Assert.Equal(5, page.Total);
        Assert.Equal(env.ForeignQuoteId, AssertOk<PurchaseQuote>(await ctl.GetById(env.ForeignQuoteId)).Id);
        Assert.Equal(env.ForeignQuoteId, AssertOk<PurchaseOrderPrefillResult>(await ctl.OrderPrefill(env.ForeignQuoteId)).SourceId);

        // 审批控制器进程内直调（无 HTTP 管线）同样保持既有口径。
        var decisions = new PurchaseQuoteDecisionController(env.Db);
        Assert.Equal(2, AssertOk<PurchaseQuoteDecisionBatch>(await decisions.BatchStatus(env.BatchNo)).LineCount);
    }

    private static async Task<T> AssertOkAsync<T>(Func<Task<IActionResult>> action) => AssertOk<T>(await action());

    // ==================== 10. 源码契约 ====================

    [Fact]
    public void 契约_控制器与规则类接通实时授权与范围下推()
    {
        var rules = ReadSource("src", "ERP.Application", "Services", "PurchaseQuoteAuthorizationRules.cs");
        Assert.Contains("RequiredMenuCode = \"purchase-quote\"", rules);
        Assert.Contains("SalespersonDataScopeService.ResolveAsync", rules);
        Assert.Contains("LoadAuthorizedMenuCodesAsync", rules);
        Assert.Contains("PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync", rules);
        Assert.Contains("EnsureOrderScopeAllowedAsync", rules);

        var controller = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteController.cs");
        Assert.DoesNotContain("AllowAnonymous", controller, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureDestinationAuthorizedAsync", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureQuotesAllowedAsync", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureBatchAllowedAsync", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureProposedCustomerAllowed", controller);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureDestinationScopeAllowedAsync", controller);
        // 派生只读路由必须把范围下推到查询，而不是内存过滤。
        Assert.Contains("PurchaseQuotePriceHistory.QueryAsync(_db, query, scope)", controller);
        Assert.Contains("PurchaseQuoteOrderPriceVariance.QueryAsync(_db, query, scope)", controller);
        Assert.Contains("PurchaseQuoteConversionFunnel.QueryAsync(_db, query, scope)", controller);

        var decision = ReadSource("src", "ERP.Api", "Controllers", "PurchaseQuoteDecisionController.cs");
        Assert.DoesNotContain("AllowAnonymous", decision, StringComparison.Ordinal);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync", decision);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureBatchAllowedAsync", decision);
        Assert.Contains("PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync", decision);

        foreach (var helper in new[] { "PurchaseQuotePriceHistory.cs", "PurchaseQuoteOrderPriceVariance.cs", "PurchaseQuoteConversionFunnel.cs" })
            Assert.Contains("PurchaseQuoteAuthorizationRules.ApplyScope",
                ReadSource("src", "ERP.Api", "Controllers", helper));

        var doc = ReadSource("docs", "purchase-quote-authority.md");
        Assert.Contains("purchase-quote", doc);
        Assert.Contains("purchase-order", doc);
        Assert.Contains("SalespersonDataScopeService", doc);
        Assert.Contains("(localdb)\\NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
        Assert.Contains("绝不", doc);
    }

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray())));
}
