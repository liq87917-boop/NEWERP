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
/// ERP-479 收款单 / 付款单规范入口与「省略单据号」手工录入契约单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="FinanceReceipt.ReceiptNo"/> 与 <see cref="FinancePayment.PaymentNo"/> 不再显式
/// <c>[Required]</c>（省略 / 空串照常绑定），长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造单据号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳一律服务端覆盖；</item>
/// <item>拒绝先于编号与持久化：无身份 / 缺菜单 / 客户或供应商不可用 / 非法币种与金额 / 越权客户 / 悬空 ·
/// 已取消 · 币种不一致 · 超申请金额的来源货款申请单一律零表头变化且零占号；</item>
/// <item>可空来源引用：付款单显式关联货款申请单、以及把已链接来源清空为 <c>null</c>，单据号与状态不变；</item>
/// <item>规范 Id 接线：刚保存的规范收款单 / 付款单 Id 可直接打开既有收款 / 付款引用工作台的只读汇总与候选；</item>
/// <item>接线契约：菜单路由不再落到旧存储过程族、规范模块与既有引用工作台接线、控制器校验先于单号预约。</item>
/// </list></para>
/// </summary>
public class ReceiptPaymentCanonicalEntryTests
{
    private const long CustomerA = 979001L;
    private const long SupplierA = 979101L;

    // ==================== 1. 测试脚手架 ====================

    /// <summary>按单据类型生成可区分前缀的伪单号服务，并统计预约次数（用于证明「被拒绝的请求零占号」）。</summary>
    private sealed class CountingDocumentNumberService : IDocumentNumberService
    {
        private int _seq;
        public int Generations { get; private set; }

        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
        {
            Generations++;
            var prefix = documentType == DocumentType.Receipt ? "RC" : "FK";
            return Task.FromResult($"{prefix}{DateTime.Now:yyyyMMdd}{++_seq:D4}");
        }
    }

    private static void SeedMaster(ErpDbContext db)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = CustomerA, CustomerCode = "C-479", CustomerName = "客户A", Status = 1, CreditStatus = "正常"
        });
        db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = SupplierA, SupplierCode = "S-479", SupplierName = "供应商A", Status = 1
        });
        db.SaveChanges();
    }

    private static SysMenu EnsureMenu(ErpDbContext db, string code, string name, string path)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted && m.MenuCode == code);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path, MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种一个具备指定菜单授权的特权账号（系统内置角色 = 不受限客户范围），返回用户 Id。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string menuCode, string menuName, string path)
    {
        var role = new SysRole
        {
            RoleName = $"{menuName}特权角色", RoleCode = $"{menuCode}-Priv-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"{menuCode}-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = $"{menuName}特权用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsureMenu(db, menuCode, menuName, path);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static long SeedReceiptPrivilegedUser(ErpDbContext db)
        => SeedPrivilegedUser(db, CustomerReceiptLifecycleRules.RequiredMenuCode,
            CustomerReceiptLifecycleRules.RequiredMenuText, "/finance/receipt");

    private static long SeedPaymentPrivilegedUser(ErpDbContext db)
        => SeedPrivilegedUser(db, SupplierPaymentLifecycleRules.RequiredMenuCode,
            SupplierPaymentLifecycleRules.RequiredMenuText, "/finance/payment");

    /// <summary>播种一个「有登录身份但没有任何菜单授权」的账号（非系统角色、不分配任何菜单）。</summary>
    private static long SeedUserWithoutMenus(ErpDbContext db)
    {
        var role = new SysRole { RoleName = "无菜单角色", RoleCode = $"NoMenu-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"no-menu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// 播种一个受限制业务员（非系统角色 + 指定菜单 + 登录账号 → 员工映射），返回 (用户 Id, 员工 Id)：
    /// 客户数据范围只覆盖 <c>EmpId == 员工 Id</c> 的客户，用于越权客户 fail closed 场景。
    /// </summary>
    private static (long UserId, long EmployeeId) SeedRestrictedSalesman(
        ErpDbContext db, string userName, string menuCode, string menuName, string path)
    {
        var role = new SysRole { RoleName = "业务员角色", RoleCode = $"Sales-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = userName, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsureMenu(db, menuCode, menuName, path);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return (user.Id, employee.Id);
    }

    private static FinanceReceiptController BuildReceiptController(
        ErpDbContext db, long? userId, CountingDocumentNumberService noService)
    {
        var controller = new FinanceReceiptController(db, noService);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static FinancePaymentController BuildPaymentController(
        ErpDbContext db, long? userId, CountingDocumentNumberService noService)
    {
        var controller = new FinancePaymentController(db, noService);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static FinanceReceipt NewReceipt(
        string receiptNo = "", decimal amount = 1000m, Currency currency = Currency.USD,
        long customerId = CustomerA, PaymentMethod paymentMethod = PaymentMethod.BankTransfer)
        => new()
        {
            ReceiptNo = receiptNo,
            ReceiptDate = DateTime.Today,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = paymentMethod,
            BankAccount = "BANK-479",
            Remark = "ERP-479_TEST"
        };

    private static FinancePayment NewPayment(
        string paymentNo = "", decimal amount = 1000m, Currency currency = Currency.CNY,
        long supplierId = SupplierA, long? paymentApplyId = null)
        => new()
        {
            PaymentNo = paymentNo,
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "BANK-479",
            Remark = "ERP-479_TEST"
        };

    private static FinancePaymentApply SeedPaymentApply(
        ErpDbContext db, string applyNo, decimal amount, Currency currency,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = DateTime.Today,
            CustomerId = CustomerA,
            Amount = amount,
            Currency = currency,
            BankAccount = "BANK-479",
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 2. 实体契约：省略收款单号 / 付款单号 ====================

    [Theory]
    [InlineData(typeof(FinanceReceipt), nameof(FinanceReceipt.ReceiptNo))]
    [InlineData(typeof(FinancePayment), nameof(FinancePayment.PaymentNo))]
    public void Entity_单据号不再显式必填_长度约束保留(Type entityType, string propertyName)
    {
        var property = entityType.GetProperty(propertyName)!;

        // ERP-479：显式 [Required]（AllowEmptyStrings=false）会把「省略单据号」在模型校验阶段拒绝为 HTTP 400，
        // 服务端权威单号根本没有机会生成；移除后省略 / 空串照常绑定。
        Assert.Empty(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), inherit: true));
        var maxLength = Assert.Single(property.GetCustomAttributes(
                typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), inherit: true)
            .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
        Assert.Equal(typeof(string), property.PropertyType);
    }

    // ==================== 3. 收款单：省略 / 伪造号码与服务端权威身份 ====================

    [Fact]
    public async Task Create_省略收款单号_服务端生成权威单号与状态()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedReceiptPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, userId, noService);

        var result = await ctl.Create(NewReceipt());

        Assert.IsType<OkObjectResult>(result);
        var saved = db.FinanceReceipts.Single();
        Assert.True(saved.Id > 0);
        Assert.StartsWith("RC", saved.ReceiptNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.Equal(CustomerA, saved.CustomerId);
        Assert.Equal(1000m, saved.Amount);
        Assert.Equal(Currency.USD, saved.Currency);
        Assert.True(saved.UpdatedAt is null || saved.UpdatedAt.Value.Year > 2020);
        Assert.Equal(1, noService.Generations);
    }

    [Fact]
    public async Task Create_空串与伪造收款单号与身份_一律由服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedReceiptPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, userId, noService);

        var forged = NewReceipt(receiptNo: "HACK-RC-479");
        forged.Id = 999999L;                                   // 伪造主键
        forged.Status = DocumentStatus.Approved;                // 伪造状态
        forged.IsDeleted = true;                                // 伪造删除态
        forged.CreatedAt = new DateTime(2000, 1, 1);            // 伪造时间戳
        forged.UpdatedAt = new DateTime(2000, 1, 1);

        Assert.IsType<OkObjectResult>(await ctl.Create(forged));
        var saved = db.FinanceReceipts.Single();
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("RC", saved.ReceiptNo, StringComparison.Ordinal);
        Assert.NotEqual("HACK-RC-479", saved.ReceiptNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.UpdatedAt);
        Assert.True(saved.CreatedAt.Year > 2020);

        // 空串 / 纯空白同样由服务端权威覆盖（手工表单不提交收款单号的口径）。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewReceipt(receiptNo: "   ")));
        var second = db.FinanceReceipts.OrderByDescending(r => r.Id).First();
        Assert.StartsWith("RC", second.ReceiptNo, StringComparison.Ordinal);
        Assert.NotEqual("   ", second.ReceiptNo);
        Assert.Equal(2, noService.Generations);
    }

    [Fact]
    public async Task Create_无收款单身份_未认证且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, null, noService);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => ctl.Create(NewReceipt()));

        Assert.Empty(db.FinanceReceipts);
        Assert.Equal(0, noService.Generations);                 // 被拒绝的请求绝不消耗单据号
    }

    [Fact]
    public async Task Create_缺收款单菜单_权限不足且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedUserWithoutMenus(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, userId, noService);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => ctl.Create(NewReceipt()));

        Assert.Empty(db.FinanceReceipts);
        Assert.Equal(0, noService.Generations);
    }

    [Theory]
    [InlineData("unknownCustomer")]
    [InlineData("disabledCustomer")]
    public async Task Create_收款单客户不可用_零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedReceiptPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, userId, noService);

        long customerId;
        int expected;
        if (scenario == "unknownCustomer")
        {
            customerId = 987654321L;
            expected = ErrorCodes.NotFound;
        }
        else
        {
            var disabled = new BaseCustomer
            {
                CustomerCode = "C-OFF-479", CustomerName = "停用客户", Status = 0, CreditStatus = "正常"
            };
            db.BaseCustomers.Add(disabled);
            db.SaveChanges();
            customerId = disabled.Id;
            expected = ErrorCodes.RuleConflict;
        }

        await AssertBusinessCodeAsync(expected, () => ctl.Create(NewReceipt(customerId: customerId)));

        Assert.Empty(db.FinanceReceipts);
        Assert.Equal(0, noService.Generations);
    }

    [Fact]
    public async Task Create_收款单非法币种或非正金额_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedReceiptPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, userId, noService);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewReceipt(currency: (Currency)999)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewReceipt(amount: 0m)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewReceipt(amount: -5m)));

        Assert.Empty(db.FinanceReceipts);
        Assert.Equal(0, noService.Generations);
    }

    [Fact]
    public async Task Create_收款单越权客户_权限不足且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales479",
            CustomerReceiptLifecycleRules.RequiredMenuCode, CustomerReceiptLifecycleRules.RequiredMenuText,
            "/finance/receipt");
        var noService = new CountingDocumentNumberService();
        var ctl = BuildReceiptController(db, userId, noService);

        // 客户 A 未分配给该业务员（EmpId 为空）→ 越界 fail closed。
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => ctl.Create(NewReceipt(customerId: CustomerA)));
        Assert.Empty(db.FinanceReceipts);
        Assert.Equal(0, noService.Generations);

        // 范围内的客户照常可建（证明拒绝来自客户数据范围，而不是正常录入路径被削弱）。
        var inScope = new BaseCustomer
        {
            CustomerCode = "C-IN-479", CustomerName = "范围内客户", Status = 1, CreditStatus = "正常", EmpId = employeeId
        };
        db.BaseCustomers.Add(inScope);
        db.SaveChanges();

        Assert.IsType<OkObjectResult>(await ctl.Create(NewReceipt(customerId: inScope.Id)));
        Assert.Single(db.FinanceReceipts);
        Assert.Equal(1, noService.Generations);
    }

    // ==================== 4. 付款单：省略 / 伪造号码与零副作用拒绝 ====================

    [Fact]
    public async Task Create_省略付款单号_服务端生成权威单号与状态()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);

        var result = await ctl.Create(NewPayment());

        Assert.IsType<OkObjectResult>(result);
        var saved = db.FinancePayments.Single();
        Assert.True(saved.Id > 0);
        Assert.StartsWith("FK", saved.PaymentNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.Equal(SupplierA, saved.SupplierId);
        Assert.Null(saved.PaymentApplyId);                      // 未关联来源 = 历史未链接语义
        Assert.Equal(1000m, saved.Amount);
        Assert.Equal(Currency.CNY, saved.Currency);
        Assert.True(saved.UpdatedAt is null || saved.UpdatedAt.Value.Year > 2020);
        Assert.Equal(1, noService.Generations);
    }

    [Fact]
    public async Task Create_伪造付款单号与身份_一律由服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);

        var forged = NewPayment(paymentNo: "HACK-FK-479");
        forged.Id = 999999L;
        forged.Status = DocumentStatus.Approved;
        forged.IsDeleted = true;
        forged.CreatedAt = new DateTime(2000, 1, 1);
        forged.UpdatedAt = new DateTime(2000, 1, 1);

        Assert.IsType<OkObjectResult>(await ctl.Create(forged));
        var saved = db.FinancePayments.Single();
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("FK", saved.PaymentNo, StringComparison.Ordinal);
        Assert.NotEqual("HACK-FK-479", saved.PaymentNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.UpdatedAt);
        Assert.True(saved.CreatedAt.Year > 2020);
    }

    [Fact]
    public async Task Create_无付款单身份或缺菜单_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var noService = new CountingDocumentNumberService();

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => BuildPaymentController(db, null, noService).Create(NewPayment()));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => BuildPaymentController(db, SeedUserWithoutMenus(db), noService).Create(NewPayment()));

        Assert.Empty(db.FinancePayments);
        Assert.Equal(0, noService.Generations);
    }

    [Fact]
    public async Task Create_付款单供应商不可用或非法币种金额_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => ctl.Create(NewPayment(supplierId: 987654321L)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewPayment(currency: (Currency)999)));
        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewPayment(amount: 0m)));

        Assert.Empty(db.FinancePayments);
        Assert.Equal(0, noService.Generations);
    }

    [Fact]
    public async Task Create_显式关联货款申请单_保留可空来源引用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);
        var apply = SeedPaymentApply(db, "HK-479-LINK", 5000m, Currency.CNY);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPayment(paymentApplyId: apply.Id)));

        var saved = db.FinancePayments.Single();
        Assert.Equal(apply.Id, saved.PaymentApplyId);
        Assert.StartsWith("FK", saved.PaymentNo, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("deleted")]
    [InlineData("cancelled")]
    [InlineData("currencyMismatch")]
    [InlineData("exceedsApplyAmount")]
    public async Task Create_来源货款申请单不可用_零写入零占号(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);

        long applyId;
        decimal amount = 1000m;
        var currency = Currency.CNY;
        var expected = ErrorCodes.RuleConflict;
        switch (scenario)
        {
            case "dangling":
                applyId = 987654321L;
                expected = ErrorCodes.NotFound;
                break;
            case "deleted":
                applyId = SeedPaymentApply(db, "HK-479-DEL", 5000m, Currency.CNY, deleted: true).Id;
                expected = ErrorCodes.NotFound;
                break;
            case "cancelled":
                applyId = SeedPaymentApply(db, "HK-479-CAN", 5000m, Currency.CNY,
                    DocumentStatus.Cancelled).Id;
                break;
            case "currencyMismatch":
                applyId = SeedPaymentApply(db, "HK-479-USD", 5000m, Currency.USD).Id;
                break;
            default:
                applyId = SeedPaymentApply(db, "HK-479-SMALL", 100m, Currency.CNY).Id;
                break;                                           // 付款金额 1000 超过申请金额 100
        }

        await AssertBusinessCodeAsync(expected,
            () => ctl.Create(NewPayment(amount: amount, currency: currency, paymentApplyId: applyId)));

        Assert.Empty(db.FinancePayments);
        Assert.Equal(0, noService.Generations);                 // 被拒绝的请求零写入、零占号
    }

    [Fact]
    public async Task Update_清空来源引用_可空引用与单据号状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);
        var apply = SeedPaymentApply(db, "HK-479-CLEAR", 5000m, Currency.CNY);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPayment(paymentApplyId: apply.Id)));
        var created = db.FinancePayments.Single();
        var number = created.PaymentNo;
        Assert.Equal(apply.Id, created.PaymentApplyId);

        // 表单留空 → 声明式可空引用提交 null（显式断开来源）；单据号 / 状态保持服务端值。
        var edit = NewPayment(paymentNo: "HACK-FK-UPD", paymentApplyId: null);
        edit.Status = DocumentStatus.Approved;
        edit.IsDeleted = true;

        Assert.IsType<OkObjectResult>(await ctl.Update(created.Id, edit));

        var reloaded = db.FinancePayments.AsNoTracking().Single(p => p.Id == created.Id);
        Assert.Null(reloaded.PaymentApplyId);
        Assert.Equal(number, reloaded.PaymentNo);
        Assert.Equal(DocumentStatus.Pending, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(1, noService.Generations);                 // 修改绝不重新占号
    }

    // ==================== 5. 规范 Id → 既有收款 / 付款引用工作台（既有服务路径） ====================

    [Fact]
    public async Task 规范收款单Id_打开既有收款引用工作台汇总与候选()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedReceiptPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var receipts = BuildReceiptController(db, userId, noService);
        Assert.IsType<OkObjectResult>(await receipts.Create(NewReceipt()));

        var saved = db.FinanceReceipts.Single();
        var allocations = new CustomerReceiptAllocationController(db);
        TestAuth.SetUser(allocations, userId);

        // 刚保存的规范 Id 直接命中既有收款单侧只读汇总（工作台选择收款单的同一路径）。
        var summary = AssertOk<CustomerReceiptAllocationReceiptSummaryDto>(
            await allocations.ReceiptSummary(saved.Id));
        Assert.Equal(saved.Id, summary.ReceiptId);
        Assert.Equal(saved.ReceiptNo, summary.ReceiptNo);
        Assert.Equal(saved.CustomerId, summary.CustomerId);
        Assert.Equal(1000m, summary.ReceiptAmount);
        Assert.Equal(0m, summary.AllocatedAmount);

        // 既有可引用收款单候选清单同样按规范 Id 列出刚保存的收款单。
        var candidates = AssertOk<List<CustomerReceiptAllocationReceiptCandidateDto>>(
            await allocations.ReceiptCandidates(saved.CustomerId, saved.ReceiptNo, 50));
        Assert.Contains(candidates, c => c.ReceiptId == saved.Id && c.ReceiptNo == saved.ReceiptNo);
    }

    [Fact]
    public async Task 规范付款单Id_打开既有付款引用工作台汇总与候选()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var payments = BuildPaymentController(db, userId, noService);
        Assert.IsType<OkObjectResult>(await payments.Create(NewPayment()));

        var saved = db.FinancePayments.Single();
        var allocations = new SupplierPaymentAllocationController(db);
        TestAuth.SetUser(allocations, userId);

        var summary = AssertOk<SupplierPaymentAllocationPaymentSummaryDto>(
            await allocations.PaymentSummary(saved.Id));
        Assert.Equal(saved.Id, summary.PaymentId);
        Assert.Equal(saved.PaymentNo, summary.PaymentNo);
        Assert.Equal(saved.SupplierId, summary.SupplierId);
        Assert.Equal(1000m, summary.PaymentAmount);
        Assert.Equal(0m, summary.AllocatedAmount);

        var candidates = AssertOk<List<SupplierPaymentAllocationPaymentCandidateDto>>(
            await allocations.PaymentCandidates(saved.SupplierId, saved.PaymentNo, 50));
        Assert.Contains(candidates, c => c.PaymentId == saved.Id && c.PaymentNo == saved.PaymentNo);
    }

    [Fact]
    public async Task 规范付款单Id_存在有效引用证据_取消被拒绝且零状态变化()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var userId = SeedPaymentPrivilegedUser(db);
        var noService = new CountingDocumentNumberService();
        var ctl = BuildPaymentController(db, userId, noService);
        Assert.IsType<OkObjectResult>(await ctl.Create(NewPayment()));

        var saved = db.FinancePayments.Single();
        // 规范付款单落库后，既有「付款单 → 采购订单」有效引用证据占用同一付款单金额预算。
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            PaymentId = saved.Id,
            PaymentNo = saved.PaymentNo,
            PaymentDate = saved.PaymentDate,
            PaymentStatus = (int)saved.Status,
            PaymentStatusText = saved.Status.ToString(),
            PaymentAmount = saved.Amount,
            PurchaseOrderId = 979201L,
            OrderNo = "PO-479",
            OrderDate = DateTime.Today,
            OrderStatus = (int)DocumentStatus.Approved,
            OrderCurrency = saved.Currency.ToString(),
            SupplierId = saved.SupplierId,
            SupplierCode = "S-479",
            SupplierName = "供应商A",
            AllocatedAmount = 300m,
            Currency = saved.Currency.ToString(),
            Status = SupplierPaymentAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now
        });
        db.SaveChanges();

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => ctl.Cancel(saved.Id));

        var reloaded = db.FinancePayments.AsNoTracking().Single(p => p.Id == saved.Id);
        Assert.Equal(DocumentStatus.Pending, reloaded.Status);   // 生命周期与预算证据保持权威，零状态变化
        Assert.Equal(saved.PaymentNo, reloaded.PaymentNo);
        Assert.Equal(saved.Amount, reloaded.Amount);
    }

    // ==================== 6. 接线契约：菜单路由 / 规范模块 / 校验先于单号预约 ====================

    [Fact]
    public void 菜单路由_收款单付款单走规范模块_不再落到旧存储过程族()
    {
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        Assert.True(mapStart >= 0 && mapEnd > mapStart);
        var billCodeMap = billV2[mapStart..mapEnd];

        // ERP-479：菜单编码 receipt / payment 不再映射到旧存储过程族（旧族写路径已 fail closed）。
        Assert.DoesNotMatch(@"\breceipt\s*:", billCodeMap);
        Assert.DoesNotMatch(@"\bpayment\s*:", billCodeMap);
        Assert.DoesNotMatch(@"receipt\s*:\s*['""]", billCodeMap);
        Assert.DoesNotMatch(@"payment\s*:\s*['""]", billCodeMap);
        Assert.Contains("'deposit-apply': 'deposit-apply'", billCodeMap);   // 其它旧族映射不受影响
        Assert.Contains("ERP-479", billV2);

        // app.js 先判 BILL_CODE_MAP 再判 MODULES：移除映射后菜单必然落到规范模块。
        var app = ReadSource("src", "ERP.Api", "wwwroot", "js", "app.js");
        var billMapCheck = app.IndexOf("else if (BILL_CODE_MAP[code]) renderBillV2", StringComparison.Ordinal);
        var modulesLookup = app.IndexOf("const mod = MODULES[code]", StringComparison.Ordinal);
        Assert.True(billMapCheck >= 0 && modulesLookup > billMapCheck,
            "app.js 在 MODULES 之前判定 BILL_CODE_MAP：移除映射即落到规范收款单 / 付款单模块");

        // 规范模块声明：既有 api/finance 规范端点 + 声明式可空来源引用 + 既有引用工作台入口。
        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-finance.js");
        Assert.Contains("title: '收款单', api: '/api/finance/receipts'", modules);
        Assert.Contains("title: '付款单', api: '/api/finance/payments'", modules);
        Assert.Contains("type: 'number', nullable: true }", modules);       // paymentApplyId 可空引用
        Assert.Contains("openCustomerReceiptAllocationRegisterForReceipt", modules);
        Assert.Contains("openSupplierPaymentAllocationRegisterForPayment", modules);
        Assert.Contains("openCustomerSalesInvoiceCollectionAllocationForReceipt", modules);
        Assert.DoesNotContain("/api/v2/bills", modules);

        // crud.js 规范路径绝不调用旧存储过程族，且声明式可空引用留空提交 null（而不是哨兵值 0）。
        var crud = ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js");
        Assert.DoesNotContain("/api/v2/bills", crud);
        Assert.Contains("f.nullable === true ? null : 0", crud);
        Assert.Contains("await api(`${mod.api}/${id}`, 'PUT', body)", crud);
        Assert.Contains("await api(mod.api, 'POST', body)", crud);
    }

    [Fact]
    public void Controllers_校验先于单号预约_身份状态由服务端覆盖()
    {
        var receipt = ReadSource("src", "ERP.Api", "Controllers", "FinanceReceiptComplaintControllers.cs");
        var receiptAuthorize = receipt.IndexOf(
            "CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId)",
            StringComparison.Ordinal);
        var receiptGenerate = receipt.IndexOf(
            "entity.ReceiptNo = await _noService.GenerateAsync", StringComparison.Ordinal);
        Assert.True(receiptAuthorize >= 0 && receiptGenerate > receiptAuthorize,
            "收款单授权 / 客户 · 币种 · 金额校验必须先于单号预约（被拒绝方零写入、零占号）");
        Assert.Contains("entity.IsDeleted = false;", receipt);
        Assert.Contains("entity.UpdatedAt = null;", receipt);
        Assert.Contains("entity.Status = DocumentStatus.Pending;", receipt);

        var payment = ReadSource("src", "ERP.Api", "Controllers", "FinancePaymentController.cs");
        var paymentApply = payment.IndexOf(
            "SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(", StringComparison.Ordinal);
        var paymentAuthorize = payment.IndexOf(
            "SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), apply?.CustomerId)",
            StringComparison.Ordinal);
        var paymentGenerate = payment.IndexOf(
            "entity.PaymentNo = await _noService.GenerateAsync", StringComparison.Ordinal);
        Assert.True(paymentApply >= 0 && paymentAuthorize > paymentApply && paymentGenerate > paymentAuthorize,
            "付款单来源解析 · 授权必须先于单号预约（被拒绝方零写入、零占号）");
        Assert.Contains("entity.IsDeleted = false;", payment);
        Assert.Contains("entity.UpdatedAt = null;", payment);
        Assert.Contains("entity.Status = DocumentStatus.Pending;", payment);
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
}
