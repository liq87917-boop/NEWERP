using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Globalization;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-538 费用单（<c>api/finance/expenses</c>）七个**客户端可持久化标量**的**声明列精度**单元测试
/// （内存库 + 真实控制器 + 真实已认证身份）：
/// <list type="number">
/// <item>声明列精度与 EF 映射同源：<c>Amount</c> / <c>AmountCny</c> / <c>TaxRate</c> / <c>TaxAmount</c> /
/// <c>AllocationRatio</c> / <c>AllocatedAmount</c> = <c>decimal(18,4)</c>、<c>ExchangeRate</c> = <c>decimal(18,6)</c>
/// （仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；ERP-042 批次 / 分摊行列精度保持不变；</item>
/// <item>纯规则：逐位原样可表示判定（4 位小数 / 6 位小数与恰好上限放行；第 5 / 第 7 位小数、量级越界、
/// 「半进位取整后恰好落到上限」的过精度值一律拒绝）；</item>
/// <item><see cref="ExpenseAuthorizationRules.NormalizeAmount"/> / <c>NormalizeRate</c> / <c>EnsureNonNegativeScalar</c>：
/// 可表示值**不取整、原样返回**（含声明列上限），非正 / 负数 / 过精度 / 越界以既有受控错误拒绝；</item>
/// <item>真实控制器新增 / 修改：逐列过精度与量级越界**零写入 / 零改写**（任何已跟踪写入之前），
/// 4 位小数金额 · 汇率与 6 位小数汇率**逐位原样**往返；</item>
/// <item>传统分摊：客户端分摊总额按声明列精度判定（预览与生成同边界），**派生**金额仍沿用既有货币精度取整。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返 / 越界被真实列拒绝 / `INFORMATION_SCHEMA` 列类型」
/// 由 <c>ERP.IntegrationTests/ExpenseDeclaredPrecisionSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class ExpenseDeclaredPrecisionTests
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（费用单金额类声明列上限）。</summary>
    private static readonly decimal Max4 = FinancePersistedAmountRules.MaxDecimal18Scale4;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（费用单汇率声明列上限）。</summary>
    private static readonly decimal Max6 = FinancePersistedAmountRules.MaxDecimal18Scale6;

    private static readonly DateTime FixedDate = new(2026, 10, 14);

    /// <summary>七个客户端可持久化标量（属性名 + 声明列小数位）。</summary>
    private static readonly (string Property, int Scale)[] DeclaredScalars =
    {
        (nameof(FinanceExpense.Amount), 4),
        (nameof(FinanceExpense.AmountCny), 4),
        (nameof(FinanceExpense.TaxRate), 4),
        (nameof(FinanceExpense.TaxAmount), 4),
        (nameof(FinanceExpense.AllocationRatio), 4),
        (nameof(FinanceExpense.AllocatedAmount), 4),
        (nameof(FinanceExpense.ExchangeRate), 6),
    };

    /// <summary>五个**可选**标量（零值 = 未启用税 / 未分摊，必须非负）。</summary>
    private static readonly string[] OptionalScalars =
    {
        nameof(FinanceExpense.AmountCny),
        nameof(FinanceExpense.TaxRate),
        nameof(FinanceExpense.TaxAmount),
        nameof(FinanceExpense.AllocationRatio),
        nameof(FinanceExpense.AllocatedAmount),
    };

    public static IEnumerable<object[]> DeclaredScalarNames()
        => DeclaredScalars.Select(s => new object[] { s.Property });

    public static IEnumerable<object[]> OptionalScalarNames()
        => OptionalScalars.Select(p => new object[] { p });

    private static decimal Parse(string raw) => decimal.Parse(raw, CultureInfo.InvariantCulture);

    // ==================== 1. 声明列精度常量与实际 EF 列精度同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 声明列精度常量与实际EF列精度同源()
    {
        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(4, FinancePersistedAmountRules.DecimalScale4);
        Assert.Equal(6, FinancePersistedAmountRules.DecimalScale6);
        Assert.Equal(4, ExpenseAuthorizationRules.PersistedAmountScale);
        Assert.Equal(6, ExpenseAuthorizationRules.PersistedExchangeRateScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);
        Assert.Equal(999_999_999_999.999999m, Max6);

        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP538_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(FinanceExpense));
        Assert.NotNull(entityType);
        foreach (var (property, scale) in DeclaredScalars)
        {
            var mapped = entityType!.FindProperty(property);
            Assert.NotNull(mapped);
            Assert.Equal(18, mapped!.GetPrecision());
            Assert.Equal(scale, mapped.GetScale());
            Assert.Equal($"decimal(18,{scale})", mapped.GetColumnType());
        }

        // ERP-042 批次 / 分摊行的既有列精度保持不变（本次不改批次表结构）。
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.SourceAmount), 18, 2);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationBatch),
            nameof(FinanceExpenseAllocationBatch.ExchangeRate), 18, 6);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.BasisValue), 18, 4);
        AssertDecimalColumn(db.Model, typeof(FinanceExpenseAllocationLine),
            nameof(FinanceExpenseAllocationLine.AllocatedAmountCny), 18, 2);
    }

    private static void AssertDecimalColumn(
        IModel model, Type entityType, string propertyName, int precision, int scale)
    {
        var property = model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
        Assert.Equal($"decimal({precision},{scale})", property.GetColumnType());
    }

    // ==================== 2. 可表示性纯判定：逐位原样 ====================

    [Theory]
    [InlineData("0")]
    [InlineData("0.0001")]
    [InlineData("1")]
    [InlineData("1234.5678")]
    [InlineData("0.0125")]
    [InlineData("13.0555")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 可表示_DECIMAL18_4_四位小数与上限放行(string raw)
        => Assert.True(ExpenseAuthorizationRules.IsWithinDeclaredPrecision(Parse(raw), 4));

    [Theory]
    [InlineData("0.000001")]
    [InlineData("1")]
    [InlineData("7.123456")]
    [InlineData("1.000000")]
    [InlineData("999999999999.999999")]
    [InlineData("-999999999999.999999")]
    public void 可表示_DECIMAL18_6_六位小数与上限放行(string raw)
        => Assert.True(ExpenseAuthorizationRules.IsWithinDeclaredPrecision(Parse(raw), 6));

    [Theory]
    [InlineData("1.00005")]                       // 第 5 位小数
    [InlineData("0.00001")]                       // 第 5 位小数
    [InlineData("13.00005")]                      // 第 5 位小数
    [InlineData("99999999999999.99985")]          // 半进位取整后恰好落到上限，但本身不可逐位原样存储
    [InlineData("99999999999999.99995")]          // 半进位取整后越过上限
    [InlineData("100000000000000")]               // 1e14 量级越界
    [InlineData("-100000000000000")]              // 量级越界（负向）
    [InlineData("999999999999.999999")]           // 超过 (18,4) 的量级
    public void 不可表示_DECIMAL18_4_过精度或量级越界拒绝(string raw)
        => Assert.False(ExpenseAuthorizationRules.IsWithinDeclaredPrecision(Parse(raw), 4));

    [Theory]
    [InlineData("1.0000005")]                     // 第 7 位小数
    [InlineData("0.0000001")]                     // 第 7 位小数
    [InlineData("999999999999.9999995")]          // 半进位取整后恰好落到上限，但本身不可逐位原样存储
    [InlineData("1000000000000")]                 // 1e12 量级越界
    [InlineData("-1000000000000")]                // 量级越界（负向）
    public void 不可表示_DECIMAL18_6_过精度或量级越界拒绝(string raw)
        => Assert.False(ExpenseAuthorizationRules.IsWithinDeclaredPrecision(Parse(raw), 6));


    // ==================== 3. 金额 / 汇率 / 可选标量的写入校验规则 ====================

    [Fact]
    public void 金额与汇率_可表示值原样返回_过精度与越界受控拒绝()
    {
        // 逐位原样：不取整、不改写（含 3 / 4 位小数金额与 3~6 位小数汇率、各自声明列上限）。
        Assert.Equal(0.005m, ExpenseAuthorizationRules.NormalizeAmount(0.005m));
        Assert.Equal(1234.5678m, ExpenseAuthorizationRules.NormalizeAmount(1234.5678m));
        Assert.Equal(Max4, ExpenseAuthorizationRules.NormalizeAmount(Max4));
        Assert.Equal(1.005m, ExpenseAuthorizationRules.NormalizeRate(1.005m));
        Assert.Equal(7.123456m, ExpenseAuthorizationRules.NormalizeRate(7.123456m));
        Assert.Equal(Max6, ExpenseAuthorizationRules.NormalizeRate(Max6));

        var amountEx = Assert.Throws<BusinessException>(
            () => ExpenseAuthorizationRules.NormalizeAmount(1.00005m));
        Assert.Equal(ErrorCodes.InvalidParameter, amountEx.Code);
        Assert.Contains("DECIMAL(18,4)", amountEx.Message, StringComparison.Ordinal);

        var magnitudeEx = Assert.Throws<BusinessException>(
            () => ExpenseAuthorizationRules.NormalizeAmount(Max4 + 1m));
        Assert.Equal(ErrorCodes.InvalidParameter, magnitudeEx.Code);
        Assert.Contains("DECIMAL(18,4)", magnitudeEx.Message, StringComparison.Ordinal);

        var rateEx = Assert.Throws<BusinessException>(
            () => ExpenseAuthorizationRules.NormalizeRate(1.0000005m));
        Assert.Equal(ErrorCodes.InvalidParameter, rateEx.Code);
        Assert.Contains("DECIMAL(18,6)", rateEx.Message, StringComparison.Ordinal);

        var rateMagnitudeEx = Assert.Throws<BusinessException>(
            () => ExpenseAuthorizationRules.NormalizeRate(Max6 + 1m));
        Assert.Equal(ErrorCodes.InvalidParameter, rateMagnitudeEx.Code);
        Assert.Contains("DECIMAL(18,6)", rateMagnitudeEx.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(OptionalScalarNames))]
    public void 可选标量_零值与四位小数放行_负数过精度与量级越界拒绝(string property)
    {
        // 零值（未启用税 / 未分摊）与 4 位小数照常放行，且**逐位原样**返回。
        Assert.Equal(0m, ExpenseAuthorizationRules.EnsureNonNegativeScalar(0m, property));
        Assert.Equal(1.2345m, ExpenseAuthorizationRules.EnsureNonNegativeScalar(1.2345m, property));
        Assert.Equal(Max4, ExpenseAuthorizationRules.EnsureNonNegativeScalar(Max4, property));

        foreach (var bad in new[] { -0.0001m, -1m })
        {
            var negativeEx = Assert.Throws<BusinessException>(
                () => ExpenseAuthorizationRules.EnsureNonNegativeScalar(bad, property));
            Assert.Equal(ErrorCodes.InvalidParameter, negativeEx.Code);
            Assert.Contains("不能为负数", negativeEx.Message, StringComparison.Ordinal);
        }

        var scaleEx = Assert.Throws<BusinessException>(
            () => ExpenseAuthorizationRules.EnsureNonNegativeScalar(1.00005m, property));
        Assert.Equal(ErrorCodes.InvalidParameter, scaleEx.Code);
        Assert.Contains("DECIMAL(18,4)", scaleEx.Message, StringComparison.Ordinal);

        var magnitudeEx = Assert.Throws<BusinessException>(
            () => ExpenseAuthorizationRules.EnsureNonNegativeScalar(Max4 + 1m, property));
        Assert.Equal(ErrorCodes.InvalidParameter, magnitudeEx.Code);
        Assert.Contains("DECIMAL(18,4)", magnitudeEx.Message, StringComparison.Ordinal);

        // 纯判定（不抛异常）口径一致：4 位小数与上限可表示；过精度 / 量级越界不可表示。
        // 注意纯判定只回答「能否逐位原样存储」（decimal 列本身可为负），**非负**语义由写入护栏
        // EnsureNonNegativeScalar 单独施加。
        Assert.True(ExpenseAuthorizationRules.IsScalarRepresentable(0m));
        Assert.True(ExpenseAuthorizationRules.IsScalarRepresentable(Max4));
        Assert.True(ExpenseAuthorizationRules.IsScalarRepresentable(-1m));
        Assert.False(ExpenseAuthorizationRules.IsScalarRepresentable(1.00005m));
        Assert.False(ExpenseAuthorizationRules.IsScalarRepresentable(-100_000_000_000_000m));
    }


    // ==================== 4. 真实控制器：新增 / 修改逐列边界与逐位原样往返 ====================

    [Theory]
    [MemberData(nameof(DeclaredScalarNames))]
    public async Task 新增_逐列过精度与量级越界拒绝且零写入(string property)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-538", "声明精度客户");
        var controller = BuildController(db);
        var scale = DeclaredScalars.Single(s => s.Property == property).Scale;
        var overScale = scale == 4 ? 1.00005m : 1.0000005m;
        var overMagnitude = scale == 4 ? Max4 + 1m : Max6 + 1m;

        foreach (var bad in new[] { overScale, overMagnitude, -1m })
        {
            var entity = NewExpense(customer.Id);
            SetScalar(entity, property, bad);

            var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(entity));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            if (bad > 0m)
                Assert.Contains($"DECIMAL(18,{scale})", ex.Message, StringComparison.Ordinal);
        }

        // 被拒请求零写入。
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        // 同一受权控制器随后合法创建成功（证明拒绝原因是标量精度而不是授权）。
        var created = AssertOk<FinanceExpense>(await controller.Create(NewExpense(customer.Id)));
        Assert.Equal(1, await db.FinanceExpenses.CountAsync());
        Assert.Equal(created.Id, (await db.FinanceExpenses.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task 新增与修改_声明列上限与四位小数逐位原样往返()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-538-RT", "往返客户");
        var controller = BuildController(db);

        var created = AssertOk<FinanceExpense>(await controller.Create(NewExpense(
            customer.Id, amount: Max4, currency: "USD", rate: Max6,
            amountCny: Max4, taxRate: 1.2345m, taxAmount: 0.0001m,
            allocationRatio: 33.3333m, allocatedAmount: 0.0001m)));
        Assert.Equal(Max4, created.Amount);
        Assert.Equal(Max6, created.ExchangeRate);
        Assert.Equal(1.2345m, created.TaxRate);

        var stored = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(Max4, stored.Amount);
        Assert.Equal(Max4, stored.AmountCny);
        Assert.Equal(Max6, stored.ExchangeRate);
        Assert.Equal(1.2345m, stored.TaxRate);
        Assert.Equal(0.0001m, stored.TaxAmount);
        Assert.Equal(33.3333m, stored.AllocationRatio);
        Assert.Equal(0.0001m, stored.AllocatedAmount);

        // 修改：4 位小数金额 / 比例与 6 位小数汇率照常改写（逐位原样，绝不取整到 2 位）。
        var update = NewExpense(
            customer.Id, amount: 12345.6789m, currency: "USD", rate: 7.123456m,
            amountCny: 87654.3219m, taxRate: 0.0001m, taxAmount: 0.0002m,
            allocationRatio: 12.3456m, allocatedAmount: 98.7654m);
        update.Id = created.Id;
        Assert.IsType<OkObjectResult>(await controller.Update(created.Id, update));

        var afterUpdate = await db.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(12345.6789m, afterUpdate.Amount);
        Assert.Equal(87654.3219m, afterUpdate.AmountCny);
        Assert.Equal(7.123456m, afterUpdate.ExchangeRate);
        Assert.Equal(0.0001m, afterUpdate.TaxRate);
        Assert.Equal(0.0002m, afterUpdate.TaxAmount);
        Assert.Equal(12.3456m, afterUpdate.AllocationRatio);
        Assert.Equal(98.7654m, afterUpdate.AllocatedAmount);
    }


    // ==================== 5. 传统分摊：客户端总额按声明列精度判定，派生金额仍按货币精度取整 ====================

    [Fact]
    public async Task 传统分摊_总额按声明列精度判定_派生金额仍按货币精度取整()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-538-ALLOC", "传统分摊客户");
        var controller = BuildController(db);

        // 总额过精度（第 5 位小数）与量级越界：预览与生成都在任何写入 / 单号消耗之前受控拒绝。
        foreach (var badTotal in new[] { 1.00005m, Max4 + 1m })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => controller.AllocateApply(AllocateRequest(customer.Id, total: badTotal)));
            Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
            await Assert.ThrowsAsync<BusinessException>(
                () => controller.AllocatePreview(AllocateRequest(customer.Id, total: badTotal)));
        }
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());

        // 合法 4 位小数总额照常生成：总额逐位原样参与计算，既有**派生**分摊金额仍按货币精度取整（2 位）。
        Assert.IsType<OkObjectResult>(
            await controller.AllocateApply(AllocateRequest(customer.Id, total: 100.1234m)));
        var generated = await db.FinanceExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(100.12m, generated.Amount);
        Assert.Equal(100.12m, generated.AmountCny);
        Assert.Equal(100m, generated.AllocationRatio);
        Assert.Equal(100.12m, generated.AllocatedAmount);
    }

    // ==================== 6. 既有授权语义回归（金额边界不改变授权口径） ====================

    [Fact]
    public async Task 授权回归_无实时身份仍failClosed且不写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C-538-AUTH", "授权回归客户");
        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(NewExpense(customer.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(0, await db.FinanceExpenses.CountAsync());
    }


    // ==================== 7. 测试脚手架（隔离内存库 + 真实 HTTP 身份 + 既有主数据） ====================

    /// <summary>
    /// 真实费用单控制器：控制器动作**自行**实时解析身份 / 账号状态 / 既有「费用单」（<c>expense-bill</c>）菜单 /
    /// 权威客户范围，因此夹具绑定一个**真实启用**、持有既有菜单授权的特权账号（既有系统内置角色口径）。
    /// </summary>
    private static ExpenseBillController BuildController(ErpDbContext db)
    {
        var userId = SeedPrivilegedExpenseOperator(db);
        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
            },
            ActionDescriptor = new ControllerActionDescriptor()
        };
        return controller;
    }

    /// <summary>播种真实启用、持有既有「费用单」菜单的特权操作员（只在隔离测试数据内创建，不新增生产授权）。</summary>
    private static long SeedPrivilegedExpenseOperator(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = $"费用单声明精度夹具角色-{Guid.NewGuid():N}",
            RoleCode = $"Role-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"exp-precision-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "费用单声明精度夹具账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = db.SysMenus
            .FirstOrDefault(m => m.MenuCode == ExpenseAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuCode = ExpenseAuthorizationRules.RequiredMenuCode,
                MenuName = ExpenseAuthorizationRules.RequiredMenuText,
                Path = "/finance/expense-bill",
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceExpense NewExpense(
        long? customerId, decimal amount = 100m, string currency = "CNY", decimal rate = 1m,
        decimal amountCny = 0m, decimal taxRate = 0m, decimal taxAmount = 0m,
        decimal allocationRatio = 0m, decimal allocatedAmount = 0m)
        => new()
        {
            ExpenseNo = $"EXP-538-{Guid.NewGuid():N}"[..14],
            ExpenseDate = FixedDate,
            ExpenseType = "报关费",
            Amount = amount,
            Currency = currency,
            ExchangeRate = rate,
            AmountCny = amountCny,
            TaxRate = taxRate,
            TaxAmount = taxAmount,
            AllocationRatio = allocationRatio,
            AllocatedAmount = allocatedAmount,
            CustomerId = customerId,
            RefType = "客户",
            RefNo = "TCLU-538",
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };

    private static void SetScalar(FinanceExpense entity, string property, decimal value)
    {
        switch (property)
        {
            case nameof(FinanceExpense.Amount): entity.Amount = value; break;
            case nameof(FinanceExpense.AmountCny): entity.AmountCny = value; break;
            case nameof(FinanceExpense.TaxRate): entity.TaxRate = value; break;
            case nameof(FinanceExpense.TaxAmount): entity.TaxAmount = value; break;
            case nameof(FinanceExpense.AllocationRatio): entity.AllocationRatio = value; break;
            case nameof(FinanceExpense.AllocatedAmount): entity.AllocatedAmount = value; break;
            case nameof(FinanceExpense.ExchangeRate): entity.ExchangeRate = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, "未知的费用单标量列");
        }
    }

    private static ExpenseAllocateRequest AllocateRequest(
        long? customerId, decimal total = 100m, string currency = "CNY", decimal rate = 1m)
        => new()
        {
            RefType = "拼柜",
            RefNo = "TCLU-538-LEGACY",
            ExpenseType = "报关费",
            TotalAmount = total,
            Currency = currency,
            ExchangeRate = rate,
            AllocationBase = "按体积",
            ExpenseDate = FixedDate,
            Details = new List<ExpenseAllocateDetail>
            {
                new() { CustomerId = customerId, CustomerName = "传统分摊客户", Volume = 1m }
            }
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

}
