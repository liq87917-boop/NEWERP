using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-480 定金申请单 / 货款申请单规范入口与「省略申请单号」手工录入契约单元测试
/// （内存库 <see cref="TestDbFactory"/>，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="FinanceDepositApply.ApplyNo"/> / <see cref="FinancePaymentApply.ApplyNo"/>
/// 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造申请单号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳
/// 全部由服务端覆盖（客户端注入值不参与持久化）；</item>
/// <item>拒绝先于编号与持久化：无身份 / 缺菜单 / 客户不可用 / 非法币种 · 金额 · 汇率 / 来源悬空 · 已取消 ·
/// 跨客户 · 币种不一致一律零写入且零占号；</item>
/// <item>来源显式关联 / 清空：数字 0 与 <c>null</c> 都归一为「未关联来源」历史语义，单据号 / 状态不变；</item>
/// <item>有效生命周期：提交 → 审核 → 取消，非法流转（待提交直接审核、重复取消）拒绝且零状态变化；</item>
/// <item>接线契约：菜单路由不再落到旧存储过程族、规范模块与既有来源选择器接线、校验先于单号预约。</item>
/// </list></para>
/// <para>真实浏览器 + 真实隔离 SQL / HTTP 的端到端验收见
/// <c>ERP.IntegrationTests/FinanceApplyCanonicalEntrySqlServerTests.cs</c>。</para>
/// </summary>
public class FinanceApplyCanonicalEntryTests
{
    private const long CustomerA = 980001L;
    private const long CustomerB = 980002L;

    // ==================== 0. 测试脚手架 ====================

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;

        /// <summary>已被请求生成的单号次数（用于证明被拒请求绝不消耗申请单号）。</summary>
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Calls++;
            var prefix = documentType == DocumentType.PaymentApply ? "HK" : "DJ";
            return Task.FromResult($"{prefix}{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, long id, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedSalesOrder(
        ErpDbContext db, string orderNo, long customerId,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Pending)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = 10000m,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SysMenu GrantMenu(ErpDbContext db, long roleId, string code, string name, string path)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = code,
            MenuName = name,
            Path = path,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个特权操作员（<c>IsSystem</c> 角色 = 既有系统内置角色口径，可见全部客户），
    /// 并按需授予既有「定金申请单」/「货款申请单」与「销售订单」菜单（不新增任何权限）。
    /// </summary>
    private static (long UserId, long RoleId) SeedOperator(
        ErpDbContext db, bool withDepositMenu = true, bool withPaymentMenu = true, bool withSalesOrderMenu = false)
    {
        var role = new SysRole
        {
            RoleName = "财务申请单操作角色",
            RoleCode = $"FinanceApplyOp-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"finance-apply-op-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "财务申请单操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withDepositMenu)
            GrantMenu(db, role.Id, FinanceDepositApplyLifecycleRules.RequiredMenuCode,
                FinanceDepositApplyLifecycleRules.RequiredMenuText, "/finance/deposit-applies");
        if (withPaymentMenu)
            GrantMenu(db, role.Id, FinancePaymentApplyLifecycleRules.RequiredMenuCode,
                FinancePaymentApplyLifecycleRules.RequiredMenuText, "/finance/payment-applies");
        if (withSalesOrderMenu)
            GrantMenu(db, role.Id, SalesOrderCancellationRules.RequiredMenuCode,
                SalesOrderCancellationRules.RequiredMenuText, "/sales/orders");

        return (user.Id, role.Id);
    }

    private static FinanceDepositApplyController BuildDepositController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static FinancePaymentApplyController BuildPaymentController(ErpDbContext db, FakeDocumentNumberService no)
        => new(db, no);

    private static FinanceDepositApply NewDepositRequest(
        long customerId, string applyNo = "", decimal amount = 1000m, Currency currency = Currency.USD,
        long? salesOrderId = null, decimal exchangeRate = 1m)
        => new()
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 5),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            SalesOrderId = salesOrderId,
            ExchangeRate = exchangeRate,
            BankAccount = "6222-APPLY",
            Payee = "收款方",
            Reason = "申请事由",
            Remark = "请求备注"
        };

    private static FinancePaymentApply NewPaymentRequest(
        long customerId, string applyNo = "", decimal amount = 1000m, Currency currency = Currency.USD,
        long? salesOrderId = null, decimal exchangeRate = 1m)
        => new()
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 5),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            SalesOrderId = salesOrderId,
            ExchangeRate = exchangeRate,
            BankAccount = "6222-APPLY",
            Payee = "收款方",
            Reason = "申请事由",
            Remark = "请求备注"
        };

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static string CreatedNo(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (string)payload.GetType().GetProperty("ApplyNo")!.GetValue(payload)!;
    }

    private static int ApplyCount(ErpDbContext db)
        => db.FinanceDepositApplies.Count() + db.FinancePaymentApplies.Count();

    private static FinanceDepositApply StoredDeposit(ErpDbContext db, long id)
        => db.FinanceDepositApplies.AsNoTracking().Single(a => a.Id == id);

    private static FinancePaymentApply StoredPayment(ErpDbContext db, long id)
        => db.FinancePaymentApplies.AsNoTracking().Single(a => a.Id == id);

    private static DocumentStatus StoredSalesOrderStatus(ErpDbContext db, long salesOrderId)
        => db.SalesOrders.AsNoTracking().Single(o => o.Id == salesOrderId).Status;

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>剥离块注释与行注释（用于只对「生效中的代码」做断言，说明性迁移记录不参与判定）。</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }

    // ==================== 1. 实体契约：省略申请单号 ====================

    [Theory]
    [InlineData(typeof(FinanceDepositApply))]
    [InlineData(typeof(FinancePaymentApply))]
    public void 实体_申请单号不再显式必填_长度约束保留(Type entityType)
    {
        var property = entityType.GetProperty(nameof(FinanceDepositApply.ApplyNo))!;

        Assert.Empty(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), inherit: true));
        var maxLength = Assert.Single(property.GetCustomAttributes(
                typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), inherit: true)
            .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
    }

    // ==================== 2. 省略 / 空串 / 伪造申请单号：服务端权威覆盖 ====================

    [Fact]
    public async Task 定金申请_省略申请单号_服务端权威单号与默认状态与身份()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildDepositController(db, no);
        TestAuth.SetUser(controller, userId);

        var result = await controller.Create(NewDepositRequest(CustomerA, applyNo: string.Empty));

        var id = CreatedId(result);
        var number = CreatedNo(result);
        Assert.StartsWith("DJ", number, StringComparison.Ordinal);
        Assert.Equal(1, no.Calls);

        var saved = StoredDeposit(db, id);
        Assert.Equal(number, saved.ApplyNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        // 审计时间戳由服务端写入（客户端提交值一律被覆盖，无法伪造 / 清空）
        Assert.NotNull(saved.UpdatedAt);
        Assert.True(saved.UpdatedAt > DateTime.Now.AddMinutes(-5));
        Assert.True(saved.CreatedAt > DateTime.Now.AddMinutes(-5));
        Assert.Equal(CustomerA, saved.CustomerId);
        Assert.Equal(1000m, saved.Amount);
    }

    [Fact]
    public async Task 货款申请_省略申请单号_服务端权威单号与默认状态与身份()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildPaymentController(db, no);
        TestAuth.SetUser(controller, userId);

        var result = await controller.Create(NewPaymentRequest(CustomerA, applyNo: string.Empty));

        var id = CreatedId(result);
        var number = CreatedNo(result);
        Assert.StartsWith("HK", number, StringComparison.Ordinal);
        Assert.Equal(1, no.Calls);

        var saved = StoredPayment(db, id);
        Assert.Equal(number, saved.ApplyNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.NotNull(saved.UpdatedAt);                            // 服务端审计时间戳
        Assert.True(saved.UpdatedAt > DateTime.Now.AddMinutes(-5));
        Assert.Equal(CustomerA, saved.CustomerId);
    }

    [Fact]
    public async Task 定金申请_伪造单号主键状态删除态时间戳_一律服务端覆盖()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildDepositController(db, no);
        TestAuth.SetUser(controller, userId);

        var entity = NewDepositRequest(CustomerA, applyNo: "HACK-DJ-480");
        entity.Id = 999999L;                                   // 伪造主键
        entity.Status = DocumentStatus.Approved;               // 伪造状态
        entity.IsDeleted = true;                               // 伪造删除态
        entity.CreatedAt = new DateTime(2000, 1, 1);            // 伪造时间戳
        entity.UpdatedAt = new DateTime(2000, 1, 1);
        entity.CreatedBy = 4242L;

        var id = CreatedId(await controller.Create(entity));

        var saved = StoredDeposit(db, id);
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("DJ", saved.ApplyNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", saved.ApplyNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.True(saved.UpdatedAt > new DateTime(2000, 1, 1));      // 服务端审计时间戳覆盖伪造值
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.CreatedAt);
    }

    [Fact]
    public async Task 货款申请_伪造单号主键状态删除态时间戳_一律服务端覆盖()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var no = new FakeDocumentNumberService();
        var controller = BuildPaymentController(db, no);
        TestAuth.SetUser(controller, userId);

        var entity = NewPaymentRequest(CustomerA, applyNo: "HACK-HK-480");
        entity.Id = 999999L;
        entity.Status = DocumentStatus.Cancelled;
        entity.IsDeleted = true;
        entity.CreatedAt = new DateTime(2000, 1, 1);
        entity.UpdatedAt = new DateTime(2000, 1, 1);

        var id = CreatedId(await controller.Create(entity));

        var saved = StoredPayment(db, id);
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("HK", saved.ApplyNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", saved.ApplyNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.UpdatedAt);   // 服务端审计时间戳覆盖伪造值
    }

    // ==================== 3. 拒绝先于编号与持久化（零写入 · 零占号） ====================

    [Theory]
    [InlineData("noIdentity")]
    [InlineData("missingMenu")]
    [InlineData("unknownCustomer")]
    [InlineData("disabledCustomer")]
    [InlineData("badCurrency")]
    [InlineData("nonPositiveAmount")]
    [InlineData("nonPositiveRate")]
    [InlineData("danglingSource")]
    [InlineData("cancelledSource")]
    [InlineData("sourceCustomerMismatch")]
    [InlineData("sourceCurrencyMismatch")]
    public async Task 定金申请_非法请求_受控拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, withDepositMenu: scenario != "missingMenu");
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        SeedCustomer(db, CustomerB, "C-480-B", "客户B", status: scenario == "disabledCustomer" ? 0 : 1);
        var order = SeedSalesOrder(db, "SO-480-A", CustomerA);
        var foreignOrder = SeedSalesOrder(db, "SO-480-B", CustomerB);
        var cancelledOrder = SeedSalesOrder(db, "SO-480-C", CustomerA, status: DocumentStatus.Cancelled);
        var cnyOrder = SeedSalesOrder(db, "SO-480-D", CustomerA, currency: Currency.CNY);

        var no = new FakeDocumentNumberService();
        var controller = BuildDepositController(db, no);
        TestAuth.SetUser(controller, scenario == "noIdentity" ? null : userId);

        var request = NewDepositRequest(CustomerA);
        var expectedCode = ErrorCodes.InvalidParameter;
        switch (scenario)
        {
            case "noIdentity":
                expectedCode = ErrorCodes.Unauthorized;
                break;
            case "missingMenu":
                expectedCode = ErrorCodes.Forbidden;
                break;
            case "unknownCustomer":
                request.CustomerId = 987654321L;
                expectedCode = ErrorCodes.NotFound;
                break;
            case "disabledCustomer":
                request.CustomerId = CustomerB;
                expectedCode = ErrorCodes.RuleConflict;
                break;
            case "badCurrency":
                request.Currency = (Currency)999;
                break;
            case "nonPositiveAmount":
                request.Amount = 0m;
                break;
            case "nonPositiveRate":
                request.ExchangeRate = 0m;
                break;
            case "danglingSource":
                request.SalesOrderId = 987654321L;
                expectedCode = ErrorCodes.NotFound;
                break;
            case "cancelledSource":
                request.SalesOrderId = cancelledOrder.Id;
                expectedCode = ErrorCodes.RuleConflict;
                break;
            case "sourceCustomerMismatch":
                request.SalesOrderId = foreignOrder.Id;
                expectedCode = ErrorCodes.RuleConflict;
                break;
            default:
                request.SalesOrderId = cnyOrder.Id;               // 申请单 USD 对 CNY 来源（不做汇率换算）
                expectedCode = ErrorCodes.RuleConflict;
                break;
        }

        await AssertBusinessCodeAsync(expectedCode, () => controller.Create(request));

        Assert.Equal(0, no.Calls);                                 // 被拒请求绝不消耗申请单号
        Assert.Equal(0, ApplyCount(db));                            // 零表头写入
        Assert.Equal(DocumentStatus.Pending, StoredSalesOrderStatus(db, order.Id));      // 来源零改写
        Assert.Equal(DocumentStatus.Cancelled, StoredSalesOrderStatus(db, cancelledOrder.Id));
    }

    [Theory]
    [InlineData("noIdentity")]
    [InlineData("missingMenu")]
    [InlineData("unknownCustomer")]
    [InlineData("disabledCustomer")]
    [InlineData("badCurrency")]
    [InlineData("nonPositiveAmount")]
    [InlineData("nonPositiveRate")]
    [InlineData("danglingSource")]
    [InlineData("cancelledSource")]
    [InlineData("sourceCustomerMismatch")]
    [InlineData("sourceCurrencyMismatch")]
    public async Task 货款申请_非法请求_受控拒绝且零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, withPaymentMenu: scenario != "missingMenu");
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        SeedCustomer(db, CustomerB, "C-480-B", "客户B", status: scenario == "disabledCustomer" ? 0 : 1);
        var order = SeedSalesOrder(db, "SO-480-A", CustomerA);
        var foreignOrder = SeedSalesOrder(db, "SO-480-B", CustomerB);
        var cancelledOrder = SeedSalesOrder(db, "SO-480-C", CustomerA, status: DocumentStatus.Cancelled);
        var cnyOrder = SeedSalesOrder(db, "SO-480-D", CustomerA, currency: Currency.CNY);

        var no = new FakeDocumentNumberService();
        var controller = BuildPaymentController(db, no);
        TestAuth.SetUser(controller, scenario == "noIdentity" ? null : userId);

        var request = NewPaymentRequest(CustomerA);
        var expectedCode = ErrorCodes.InvalidParameter;
        switch (scenario)
        {
            case "noIdentity":
                expectedCode = ErrorCodes.Unauthorized;
                break;
            case "missingMenu":
                expectedCode = ErrorCodes.Forbidden;
                break;
            case "unknownCustomer":
                request.CustomerId = 987654321L;
                expectedCode = ErrorCodes.NotFound;
                break;
            case "disabledCustomer":
                request.CustomerId = CustomerB;
                expectedCode = ErrorCodes.RuleConflict;
                break;
            case "badCurrency":
                request.Currency = (Currency)999;
                break;
            case "nonPositiveAmount":
                request.Amount = -1m;
                break;
            case "nonPositiveRate":
                request.ExchangeRate = 0m;
                break;
            case "danglingSource":
                request.SalesOrderId = 987654321L;
                expectedCode = ErrorCodes.NotFound;
                break;
            case "cancelledSource":
                request.SalesOrderId = cancelledOrder.Id;
                expectedCode = ErrorCodes.RuleConflict;
                break;
            case "sourceCustomerMismatch":
                request.SalesOrderId = foreignOrder.Id;
                expectedCode = ErrorCodes.RuleConflict;
                break;
            default:
                request.SalesOrderId = cnyOrder.Id;
                expectedCode = ErrorCodes.RuleConflict;
                break;
        }

        await AssertBusinessCodeAsync(expectedCode, () => controller.Create(request));

        Assert.Equal(0, no.Calls);
        Assert.Equal(0, ApplyCount(db));
        Assert.Equal(DocumentStatus.Pending, StoredSalesOrderStatus(db, order.Id));
        Assert.Equal(DocumentStatus.Cancelled, StoredSalesOrderStatus(db, cancelledOrder.Id));
    }

    // ==================== 4. 往返：省略单号 + 来源显式关联 / 清空 ====================

    [Fact]
    public async Task 定金申请_省略申请单号往返_列表详情修改_来源显式关联与清空()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, withSalesOrderMenu: true);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var order = SeedSalesOrder(db, "SO-480-LINK", CustomerA);
        var no = new FakeDocumentNumberService();
        var controller = BuildDepositController(db, no);
        TestAuth.SetUser(controller, userId);

        // 新增（省略单号 + 显式来源）
        var id = CreatedId(await controller.Create(NewDepositRequest(CustomerA, salesOrderId: order.Id)));
        var created = StoredDeposit(db, id);
        Assert.Equal(order.Id, created.SalesOrderId);

        // 详情回读（表单重开口径）
        var detail = Assert.IsType<ApiResponse<FinanceDepositApply>>(
            Assert.IsType<OkObjectResult>(await controller.GetById(id)).Value).Data!;
        Assert.Equal(created.ApplyNo, detail.ApplyNo);
        Assert.Equal(order.Id, detail.SalesOrderId);
        Assert.Equal(DocumentStatus.Pending, detail.Status);

        // 列表按单号命中规范 Id
        var page = Assert.IsType<ApiResponse<PagedResult<FinanceDepositApply>>>(
            Assert.IsType<OkObjectResult>(await controller.GetPaged(
                new PageQuery { Keyword = created.ApplyNo }, null)).Value).Data!;
        Assert.Contains(page.Items, a => a.Id == id);

        // 已存储来源只读展示（既有接口按规范 Id）
        var sourceView = Assert.IsType<ApiResponse<FinanceApplySalesOrderSourceViewDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetStoredSalesOrderSource(id)).Value).Data!;
        Assert.True(sourceView.Linked);
        Assert.Equal(order.Id, sourceView.SalesOrderId);

        // 修改：显式清空来源（数字 0 归一为未关联语义），单号 / 状态保持服务端值
        await controller.Update(id, NewDepositRequest(CustomerA, applyNo: "HACK-DJ-480", salesOrderId: 0L));
        var cleared = StoredDeposit(db, id);
        Assert.Null(cleared.SalesOrderId);
        Assert.Equal(created.ApplyNo, cleared.ApplyNo);
        Assert.Equal(DocumentStatus.Pending, cleared.Status);
        Assert.False(cleared.IsDeleted);
        Assert.NotNull(cleared.UpdatedAt);

        // 修改：重新显式关联（显式 Id）
        await controller.Update(id, NewDepositRequest(CustomerA, salesOrderId: order.Id));
        Assert.Equal(order.Id, StoredDeposit(db, id).SalesOrderId);
    }

    [Fact]
    public async Task 货款申请_省略申请单号往返_列表详情修改_来源显式关联与清空()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, withSalesOrderMenu: true);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var order = SeedSalesOrder(db, "SO-480-LINK", CustomerA);
        var no = new FakeDocumentNumberService();
        var controller = BuildPaymentController(db, no);
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(NewPaymentRequest(CustomerA, salesOrderId: order.Id)));
        var created = StoredPayment(db, id);
        Assert.Equal(order.Id, created.SalesOrderId);
        Assert.StartsWith("HK", created.ApplyNo, StringComparison.Ordinal);

        var detail = Assert.IsType<ApiResponse<FinancePaymentApply>>(
            Assert.IsType<OkObjectResult>(await controller.GetById(id)).Value).Data!;
        Assert.Equal(created.ApplyNo, detail.ApplyNo);
        Assert.Equal(order.Id, detail.SalesOrderId);

        var page = Assert.IsType<ApiResponse<PagedResult<FinancePaymentApply>>>(
            Assert.IsType<OkObjectResult>(await controller.GetPaged(
                new PageQuery { Keyword = created.ApplyNo }, null)).Value).Data!;
        Assert.Contains(page.Items, a => a.Id == id);

        // 显式清空来源：null 提交（声明式可空引用）同样归一为「未关联来源」
        await controller.Update(id, NewPaymentRequest(CustomerA, salesOrderId: null));
        var cleared = StoredPayment(db, id);
        Assert.Null(cleared.SalesOrderId);
        Assert.Equal(created.ApplyNo, cleared.ApplyNo);
        Assert.Equal(DocumentStatus.Pending, cleared.Status);
        Assert.NotNull(cleared.UpdatedAt);

        // 非待提交不可修改（审核后只读），失败时零改写
        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(id));
        var before = StoredPayment(db, id);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(id, NewPaymentRequest(CustomerA, amount: 500m)));
        var after = StoredPayment(db, id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Amount, after.Amount);
        Assert.Equal(before.ApplyNo, after.ApplyNo);
    }

    // ==================== 5. 有效生命周期与非法流转 ====================

    [Fact]
    public async Task 定金申请_有效生命周期_提交审核取消与非法流转拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var controller = BuildDepositController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(NewDepositRequest(CustomerA)));

        // 待提交直接审核 = 非法流转（零状态变化）
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Approve(id));
        Assert.Equal(DocumentStatus.Pending, StoredDeposit(db, id).Status);

        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.Equal(DocumentStatus.Submitted, StoredDeposit(db, id).Status);

        Assert.IsType<OkObjectResult>(await controller.Approve(id));
        Assert.Equal(DocumentStatus.Approved, StoredDeposit(db, id).Status);

        Assert.IsType<OkObjectResult>(await controller.Cancel(id));
        Assert.Equal(DocumentStatus.Cancelled, StoredDeposit(db, id).Status);

        // 重复取消拒绝且零状态变化
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(id));
        var stored = StoredDeposit(db, id);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(1000m, stored.Amount);
    }

    [Fact]
    public async Task 货款申请_有效生命周期_提交审核取消与删除护栏()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        var controller = BuildPaymentController(db, new FakeDocumentNumberService());
        TestAuth.SetUser(controller, userId);

        var id = CreatedId(await controller.Create(NewPaymentRequest(CustomerA)));

        Assert.IsType<OkObjectResult>(await controller.Submit(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(id));
        Assert.IsType<OkObjectResult>(await controller.Cancel(id));
        Assert.Equal(DocumentStatus.Cancelled, StoredPayment(db, id).Status);

        // 已取消不可删除（仅待提交可删），失败时零删除标记变化
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(id));
        var stored = StoredPayment(db, id);
        Assert.False(stored.IsDeleted);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
    }

    // ==================== 6. 来源候选：授权先于取数（既有 ERP-390 / ERP-391 服务） ====================

    [Fact]
    public async Task 来源候选_按精确客户返回且越界拒绝_不消耗单号()
    {
        using var db = TestDbFactory.Create();
        var (userId, _) = SeedOperator(db, withSalesOrderMenu: true);
        SeedCustomer(db, CustomerA, "C-480", "客户A");
        SeedCustomer(db, CustomerB, "C-480-B", "客户B");
        var order = SeedSalesOrder(db, "SO-480-CAND", CustomerA);
        SeedSalesOrder(db, "SO-480-OTHER", CustomerB);

        var no = new FakeDocumentNumberService();
        var controller = BuildDepositController(db, no);
        TestAuth.SetUser(controller, userId);

        var candidates = Assert.IsType<ApiResponse<FinanceApplySalesOrderCandidatePageDto>>(
            Assert.IsType<OkObjectResult>(await controller.GetSalesOrderCandidates(
                CustomerA, "USD", null, 1, 20)).Value).Data!;
        var candidate = Assert.Single(candidates.Items);
        Assert.Equal(order.Id, candidate.SalesOrderId);
        Assert.True(candidate.Eligible);

        // 精确客户缺失 → 拒绝（不返回任意客户订单，也不新增授权）
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.GetSalesOrderCandidates(null, null, null, 1, 20));
        Assert.Equal(0, no.Calls);
        Assert.Equal(0, ApplyCount(db));
    }

    // ==================== 7. 接线契约：菜单路由 / 规范模块 / 校验先于单号预约 ====================

    [Fact]
    public void 菜单路由_财务申请单走规范模块_不再落到旧存储过程族()
    {
        // ERP-480：菜单必须打开规范 EF 模块（api/finance/deposit-applies、api/finance/payment-applies，规范 Id），
        // 不得再经 BILL_CODE_MAP 落到旧存储过程族（其写路径已被 ERP-404 门禁整体停用）。
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        Assert.True(mapStart >= 0 && mapEnd > mapStart);
        var billCodeMap = billV2[mapStart..mapEnd];

        // 说明性注释里保留了原字面量（迁移记录），因此这里先剥离注释，只对**生效中的映射键**断言。
        var activeMap = StripComments(billCodeMap);
        Assert.DoesNotMatch(@"'deposit-apply'\s*:", activeMap);
        Assert.DoesNotMatch(@"'payment-apply'\s*:", activeMap);
        Assert.DoesNotContain("deposit-apply", activeMap);
        Assert.DoesNotContain("payment-apply", activeMap);
        Assert.Contains("ERP-480", billV2);

        // app.js 先判 BILL_CODE_MAP 再判 MODULES：移除映射后菜单必然落到规范模块。
        var app = ReadSource("src", "ERP.Api", "wwwroot", "js", "app.js");
        var billMapCheck = app.IndexOf("else if (BILL_CODE_MAP[code]) renderBillV2", StringComparison.Ordinal);
        var modulesLookup = app.IndexOf("const mod = MODULES[code]", StringComparison.Ordinal);
        Assert.True(billMapCheck >= 0 && modulesLookup > billMapCheck,
            "app.js 在 MODULES 之前判定 BILL_CODE_MAP：移除映射即落到规范申请单模块");

        // 规范模块声明：既有 api/finance 规范端点 + 显式可空来源字段 + 既有来源选择器。
        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-finance.js");
        Assert.Contains("title: '定金申请单', api: '/api/finance/deposit-applies'", modules);
        Assert.Contains("title: '货款申请单', api: '/api/finance/payment-applies'", modules);
        Assert.Contains("type: 'number', nullable: true, selector: 'finance-apply-sales-order-source'", modules);
        Assert.DoesNotContain("/api/v2/bills", modules);

        // crud.js 规范路径绝不调用旧存储过程族，且声明式可空引用留空提交 null（而不是哨兵值 0）。
        var crud = ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js");
        Assert.DoesNotContain("/api/v2/bills", crud);
        Assert.Contains("f.nullable === true ? null : 0", crud);
    }

    [Fact]
    public void 旧族读侧保留_规范Id与旧Oid之间不存在映射_交付说明记录口径()
    {
        var billConfig = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-config2.js")
            + ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-config3.js");
        Assert.Contains("'deposit-apply'", billConfig);
        Assert.Contains("'payment-apply'", billConfig);

        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        var activeMap = StripComments(billV2[mapStart..mapEnd]);
        Assert.DoesNotContain("'deposit-apply': 'deposit-apply'", activeMap);
        Assert.DoesNotContain("'payment-apply': 'payment-apply'", activeMap);

        // 交付说明存在且记录规范端点与「真实浏览器 / 不声称整阶段验收」口径。
        var doc = ReadSource("docs", "finance-apply-canonical-entry.md");
        Assert.Contains("ERP-480", doc);
        Assert.Contains("/api/finance/deposit-applies", doc);
        Assert.Contains("/api/finance/payment-applies", doc);
        Assert.Contains("真实浏览器", doc);
    }

    [Fact]
    public void Controllers_校验先于单号预约_身份状态由服务端覆盖()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "FinanceApplyControllers.cs");
        var paymentClass = controller.IndexOf("class FinancePaymentApplyController", StringComparison.Ordinal);
        var deposit = controller[..paymentClass];

        // 省略单号照常绑定（实体无显式必填）→ 授权与来源校验先于单号预约。
        var depositAuthorize = deposit.IndexOf(
            "FinanceDepositApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId)",
            StringComparison.Ordinal);
        var depositGenerate = deposit.IndexOf(
            "entity.ApplyNo = await _noService.GenerateAsync", StringComparison.Ordinal);
        Assert.True(depositAuthorize >= 0 && depositGenerate > depositAuthorize,
            "定金申请单授权 / 客户 · 币种 · 金额 · 汇率 / 来源校验必须先于单号预约（被拒方零写入、零占号）");

        // 主键 / 状态 / 删除态 / 时间戳一律服务端覆盖（客户端注入值不参与持久化）。
        Assert.Contains("entity.Id = 0;", deposit);
        Assert.Contains("entity.Status = DocumentStatus.Pending;", deposit);
        Assert.Contains("entity.IsDeleted = false;", deposit);
        Assert.Contains("entity.UpdatedAt = null;", deposit);
        Assert.Contains("entity.SalesOrderId = entity.SalesOrderId is > 0 ? entity.SalesOrderId : null;", deposit);

        var payment = controller[paymentClass..];
        var paymentAuthorize = payment.IndexOf(
            "FinancePaymentApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId)",
            StringComparison.Ordinal);
        var paymentGenerate = payment.IndexOf(
            "entity.ApplyNo = await _noService.GenerateAsync", StringComparison.Ordinal);
        Assert.True(paymentAuthorize >= 0 && paymentGenerate > paymentAuthorize,
            "货款申请单授权 / 客户 · 币种 · 金额 · 汇率 / 来源校验必须先于单号预约（被拒方零写入、零占号）");
        Assert.Contains("entity.IsDeleted = false;", payment);
        Assert.Contains("entity.UpdatedAt = null;", payment);
        Assert.Contains("entity.SalesOrderId = entity.SalesOrderId is > 0 ? entity.SalesOrderId : null;", payment);

        // 不新增任何权限 / 旧族写适配器 / 任意 SQL。
        Assert.DoesNotContain("/api/v2/bills", controller);
        Assert.DoesNotContain("FromSql", controller);
        Assert.DoesNotContain("ExecuteSql", controller);
    }
}
