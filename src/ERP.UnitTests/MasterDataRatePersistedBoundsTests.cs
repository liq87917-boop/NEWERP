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
/// ERP-531 供应商税率 / 返点比例与客户信用额度 / 定金比例 / 佣金比例的**实际存储精度**
/// （<c>DECIMAL(18,4)</c>）边界单元测试（内存库 + 真实控制器 + 真实已认证身份）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：5 个 decimal 列显式 <c>decimal(18,4)</c>
/// （仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>纯规则：四位小数与量级边界的逐位原样可表示判定（恰好上限放行、第 5 位小数与量级溢出拒绝、
/// 「半进位取整后恰好落到上限」的过小数位值仍拒绝、「当前已接受的边界值」继续放行）；</item>
/// <item><see cref="SupplierAuthorizationRules.Validate"/> / <see cref="CustomerAuthorizationRules.Validate"/>：
/// <b>每一个</b>数值列的第 5 位小数 / 量级溢出都以既有受控参数错误拒绝，且逐字段不变（绝不取整 / 截断 / 改写）；</item>
/// <item>真实控制器新增 / 修改：不可表示的值零 <c>BaseSuppliers</c> / <c>BaseCustomers</c> 行、零改写，
/// 合法 4 位小数原样往返；</item>
/// <item>源码契约：EF 映射为 5 列声明 <c>HasPrecision(18,4)</c>，且既有唯一索引不变。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返 / 越界被真实列拒绝 / 绕过护栏被静默取整」由
/// <c>ERP.IntegrationTests/MasterDataRatePersistedBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class MasterDataRatePersistedBoundsTests
{
    /// <summary>主数据数值列的有效位数（<c>DECIMAL(18,4)</c>，与建表脚本同源）。</summary>
    private const int DecimalPrecision = 18;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（供应商 / 客户规则常量同源）。</summary>
    private static readonly decimal Max4 = SupplierAuthorizationRules.MaxRateMagnitude;

    /// <summary>供应商全部持久化数值列（属性名 + 中文标签，与实体 / 建表脚本逐一对应）。</summary>
    private static readonly (string Property, string Label)[] SupplierColumns =
    {
        (nameof(BaseSupplier.TaxRate), "税率"),
        (nameof(BaseSupplier.RebateRatio), "返点/佣金比例"),
    };

    /// <summary>客户全部持久化数值列（属性名 + 中文标签，与实体 / 建表脚本逐一对应）。</summary>
    private static readonly (string Property, string Label)[] CustomerColumns =
    {
        (nameof(BaseCustomer.CreditLimit), "信用额度"),
        (nameof(BaseCustomer.DepositRatio), "定金比例"),
        (nameof(BaseCustomer.CommissionRatio), "佣金比例"),
    };

    /// <summary>供应商数值列名（供逐列 <c>[Theory]</c> 使用）。</summary>
    public static IEnumerable<object[]> SupplierColumnNames()
        => SupplierColumns.Select(c => new object[] { c.Property });

    /// <summary>客户数值列名（供逐列 <c>[Theory]</c> 使用）。</summary>
    public static IEnumerable<object[]> CustomerColumnNames()
        => CustomerColumns.Select(c => new object[] { c.Property });

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度_供应商与客户的五个数值列均为18_4()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP531_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(DecimalPrecision, 18);
        Assert.Equal(4, SupplierAuthorizationRules.PersistedRateScale);
        Assert.Equal(4, CustomerAuthorizationRules.PersistedDecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        var supplierType = db.Model.FindEntityType(typeof(BaseSupplier));
        Assert.NotNull(supplierType);
        foreach (var (property, _) in SupplierColumns)
        {
            var mapped = supplierType!.FindProperty(property);
            Assert.NotNull(mapped);
            Assert.Equal(DecimalPrecision, mapped!.GetPrecision());
            Assert.Equal(SupplierAuthorizationRules.PersistedRateScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());
        }

        var customerType = db.Model.FindEntityType(typeof(BaseCustomer));
        Assert.NotNull(customerType);
        foreach (var (property, _) in CustomerColumns)
        {
            var mapped = customerType!.FindProperty(property);
            Assert.NotNull(mapped);
            Assert.Equal(DecimalPrecision, mapped!.GetPrecision());
            Assert.Equal(CustomerAuthorizationRules.PersistedDecimalScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());
        }
    }

    /// <summary>源码契约：EF 映射为全部 5 个数值列声明 <c>HasPrecision(18, 4)</c>，且既有唯一索引不变。</summary>
    [Fact]
    public void 源码契约_EF映射为五个数值列声明18_4且唯一索引不变()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs"));

        foreach (var (property, _) in SupplierColumns)
            Assert.Contains(
                $"modelBuilder.Entity<BaseSupplier>().Property(x => x.{property}).HasPrecision(18, 4);", source);

        foreach (var (property, _) in CustomerColumns)
            Assert.Contains(
                $"modelBuilder.Entity<BaseCustomer>().Property(x => x.{property}).HasPrecision(18, 4);", source);

        Assert.Contains("modelBuilder.Entity<BaseSupplier>().HasIndex(s => s.SupplierCode).IsUnique();", source);
        Assert.Contains("modelBuilder.Entity<BaseCustomer>().HasIndex(c => c.CustomerCode).IsUnique();", source);
    }

    // ==================== 2. 可表示性边界（纯规则） ====================

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("13.0555")]
    [InlineData("1.2345")]
    [InlineData("0.1250")]
    [InlineData("-0.1250")]
    [InlineData("0.0001")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 供应商可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(SupplierAuthorizationRules.IsPersistedRateRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1.00005")]                 // 第 5 位小数
    [InlineData("0.00001")]                 // 第 5 位小数
    [InlineData("1.23455")]                 // 第 5 位小数
    [InlineData("99999999999999.99985")]    // 第 5 位小数：半进位取整后恰好落到上限，仍拒绝
    [InlineData("99999999999999.99995")]    // 第 5 位小数：半进位取整后越过上限，仍拒绝
    [InlineData("100000000000000")]         // 量级越界（1e14）
    [InlineData("100000000000000.0000")]    // 量级越界
    [InlineData("-100000000000000.0000")]   // 量级越界（负向）
    public void 供应商不可表示_过小数位或量级越界(string raw)
        => Assert.False(SupplierAuthorizationRules.IsPersistedRateRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("100.0001")]
    [InlineData("0.1250")]
    [InlineData("-0.1250")]
    [InlineData("0.0001")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 客户可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(CustomerAuthorizationRules.IsPersistedDecimalRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("100.00005")]               // 第 5 位小数
    [InlineData("0.00001")]                 // 第 5 位小数
    [InlineData("0.12505")]                 // 第 5 位小数
    [InlineData("99999999999999.99985")]    // 第 5 位小数：半进位取整后恰好落到上限，仍拒绝
    [InlineData("99999999999999.99995")]    // 第 5 位小数：半进位取整后越过上限，仍拒绝
    [InlineData("100000000000000")]         // 量级越界（1e14）
    [InlineData("100000000000000.0000")]    // 量级越界
    [InlineData("-100000000000000.0000")]   // 量级越界（负向）
    public void 客户不可表示_过小数位或量级越界(string raw)
        => Assert.False(CustomerAuthorizationRules.IsPersistedDecimalRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    // ==================== 3. Validate：逐列第 5 位小数 / 量级越界受控拒绝，字段不被改写 ====================

    [Theory]
    [MemberData(nameof(SupplierColumnNames))]
    public void 供应商校验_每个数值列第五位小数_受控拒绝且值不变(string property)
    {
        var supplier = NewSupplier();

        SetSupplierRate(supplier, property, 1.23455m);
        var ex = Assert.Throws<BusinessException>(() => SupplierAuthorizationRules.Validate(supplier));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
        Assert.Contains("DECIMAL(18,4)", ex.Message);
        Assert.Equal(1.23455m, GetSupplierRate(supplier, property));
    }

    [Theory]
    [MemberData(nameof(SupplierColumnNames))]
    public void 供应商校验_每个数值列量级越界_受控拒绝(string property)
    {
        var supplier = NewSupplier();

        SetSupplierRate(supplier, property, Max4 + 0.0001m);
        var ex = Assert.Throws<BusinessException>(() => SupplierAuthorizationRules.Validate(supplier));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
    }

    [Theory]
    [MemberData(nameof(SupplierColumnNames))]
    public void 供应商校验_每个数值列四位小数与上限_原样放行(string property)
    {
        var atFourDecimals = NewSupplier();
        SetSupplierRate(atFourDecimals, property, 1.2345m);
        SupplierAuthorizationRules.Validate(atFourDecimals);
        Assert.Equal(1.2345m, GetSupplierRate(atFourDecimals, property));

        var atLimit = NewSupplier();
        SetSupplierRate(atLimit, property, Max4);
        SupplierAuthorizationRules.Validate(atLimit);
        Assert.Equal(Max4, GetSupplierRate(atLimit, property));
    }

    [Theory]
    [MemberData(nameof(CustomerColumnNames))]
    public void 客户校验_每个数值列第五位小数_受控拒绝且值不变(string property)
    {
        var customer = NewCustomer();

        SetCustomerDecimal(customer, property, 0.12505m);
        var ex = Assert.Throws<BusinessException>(() => CustomerAuthorizationRules.Validate(customer));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
        Assert.Contains("DECIMAL(18,4)", ex.Message);
        Assert.Equal(0.12505m, GetCustomerDecimal(customer, property));
    }

    [Theory]
    [MemberData(nameof(CustomerColumnNames))]
    public void 客户校验_每个数值列量级越界_受控拒绝(string property)
    {
        var customer = NewCustomer();

        SetCustomerDecimal(customer, property, Max4 + 0.0001m);
        var ex = Assert.Throws<BusinessException>(() => CustomerAuthorizationRules.Validate(customer));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
    }

    [Theory]
    [MemberData(nameof(CustomerColumnNames))]
    public void 客户校验_每个数值列四位小数与上限_原样放行(string property)
    {
        var atFourDecimals = NewCustomer();
        SetCustomerDecimal(atFourDecimals, property, 0.1250m);
        CustomerAuthorizationRules.Validate(atFourDecimals);
        Assert.Equal(0.1250m, GetCustomerDecimal(atFourDecimals, property));

        var atLimit = NewCustomer();
        SetCustomerDecimal(atLimit, property, Max4);
        CustomerAuthorizationRules.Validate(atLimit);
        Assert.Equal(Max4, GetCustomerDecimal(atLimit, property));
    }

    /// <summary>当前已接受的边界值（0 / 2 位小数 / 恰好上限）继续放行，且逐字段原样保留。</summary>
    [Fact]
    public void 校验_既有可接受边界值_继续放行且不改写字段()
    {
        var supplier = NewSupplier();
        supplier.TaxRate = Max4;
        supplier.RebateRatio = 1.25m;
        SupplierAuthorizationRules.Validate(supplier);
        Assert.Equal(Max4, supplier.TaxRate);
        Assert.Equal(1.25m, supplier.RebateRatio);

        var customer = NewCustomer();
        customer.CreditLimit = Max4;
        customer.DepositRatio = 10.50m;
        customer.CommissionRatio = -0.1250m;
        CustomerAuthorizationRules.Validate(customer);
        Assert.Equal(Max4, customer.CreditLimit);
        Assert.Equal(10.50m, customer.DepositRatio);
        Assert.Equal(-0.1250m, customer.CommissionRatio);
    }

    // ==================== 4. 真实控制器：不可表示值零写入 / 合法 4 位小数原样往返 ====================

    [Fact]
    public async Task 供应商控制器_不可表示值_零写入且不改写既有行()
    {
        using var db = TestDbFactory.Create();
        var existing = SeedSupplier(db, "S-PREC-EXIST", "既有供应商");
        var userId = SeedSupplierUser(db);
        var ctl = SupplierController(db, userId);
        var before = Snapshot(db);

        // 新增：税率第 5 位小数 / 返点量级越界 → 受控拒绝，零新行。
        var fifthPlace = NewSupplier(code: "S-PREC-BAD");
        fifthPlace.TaxRate = 1.00005m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(fifthPlace));

        var overflow = NewSupplier(code: "S-PREC-OVF");
        overflow.RebateRatio = Max4 + 0.0001m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(overflow));

        // 修改：返点第 5 位小数 → 受控拒绝，既有行逐字节不变。
        var badUpdate = NewSupplier(code: "S-PREC-EXIST", name: "既有供应商");
        badUpdate.RebateRatio = 1.23455m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, badUpdate));

        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.BaseSuppliers.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 供应商控制器_合法四位小数_原样落库往返()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedSupplierUser(db);
        var ctl = SupplierController(db, userId);

        var payload = NewSupplier(code: "S-PREC-OK", name: "精度供应商");
        payload.TaxRate = 13.0555m;
        payload.RebateRatio = 1.2345m;

        var created = Data<BaseSupplier>(await ctl.Create(payload));
        Assert.True(created.Id > 0);
        Assert.Equal(13.0555m, created.TaxRate);
        Assert.Equal(1.2345m, created.RebateRatio);

        var stored = await db.BaseSuppliers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(13.0555m, stored.TaxRate);
        Assert.Equal(1.2345m, stored.RebateRatio);

        // 修改：恰好上限也原样往返。
        stored.TaxRate = Max4;
        stored.RebateRatio = 0.1250m;
        var updated = Data<BaseSupplier>(await ctl.Update(stored.Id, stored));
        Assert.Equal(Max4, updated.TaxRate);
        Assert.Equal(0.1250m, updated.RebateRatio);
    }

    [Fact]
    public async Task 客户控制器_不可表示值_零写入且不改写既有行()
    {
        using var db = TestDbFactory.Create();
        var existing = SeedCustomer(db, "C-PREC-EXIST", "既有客户");
        var userId = SeedCustomerUser(db);
        var ctl = CustomerController(db, userId);
        var before = Snapshot(db);

        // 新增：信用额度第 5 位小数 / 佣金量级越界 → 受控拒绝，零新行。
        var fifthPlace = NewCustomer(code: "C-PREC-BAD");
        fifthPlace.CreditLimit = 100.00005m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(fifthPlace));

        var overflow = NewCustomer(code: "C-PREC-OVF");
        overflow.CommissionRatio = Max4 + 0.0001m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(overflow));

        // 修改：定金比例第 5 位小数 → 受控拒绝，既有行逐字节不变。
        var badUpdate = NewCustomer(code: "C-PREC-EXIST", name: "既有客户");
        badUpdate.DepositRatio = 1.23455m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, badUpdate));

        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.BaseCustomers.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 客户控制器_合法四位小数_原样落库往返()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedCustomerUser(db);
        var ctl = CustomerController(db, userId);

        var payload = NewCustomer(code: "C-PREC-OK", name: "精度客户");
        payload.CreditLimit = 123456.7890m;
        payload.DepositRatio = 10.5000m;
        payload.CommissionRatio = 0.1250m;

        var created = Data<BaseCustomer>(await ctl.Create(payload));
        Assert.True(created.Id > 0);
        Assert.Equal(123456.7890m, created.CreditLimit);
        Assert.Equal(10.5000m, created.DepositRatio);
        Assert.Equal(0.1250m, created.CommissionRatio);

        var stored = await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(123456.7890m, stored.CreditLimit);
        Assert.Equal(10.5000m, stored.DepositRatio);
        Assert.Equal(0.1250m, stored.CommissionRatio);

        // 修改：恰好上限也原样往返。
        stored.CreditLimit = Max4;
        stored.CommissionRatio = -Max4;
        var updated = Data<BaseCustomer>(await ctl.Update(stored.Id, stored));
        Assert.Equal(Max4, updated.CreditLimit);
        Assert.Equal(-Max4, updated.CommissionRatio);
    }

    // ==================== 5. 逐列读写辅助 ====================

    private static void SetSupplierRate(BaseSupplier supplier, string property, decimal value)
    {
        switch (property)
        {
            case nameof(BaseSupplier.TaxRate): supplier.TaxRate = value; break;
            case nameof(BaseSupplier.RebateRatio): supplier.RebateRatio = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, "未知供应商数值列");
        }
    }

    private static decimal GetSupplierRate(BaseSupplier supplier, string property) => property switch
    {
        nameof(BaseSupplier.TaxRate) => supplier.TaxRate,
        nameof(BaseSupplier.RebateRatio) => supplier.RebateRatio,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "未知供应商数值列")
    };

    private static void SetCustomerDecimal(BaseCustomer customer, string property, decimal value)
    {
        switch (property)
        {
            case nameof(BaseCustomer.CreditLimit): customer.CreditLimit = value; break;
            case nameof(BaseCustomer.DepositRatio): customer.DepositRatio = value; break;
            case nameof(BaseCustomer.CommissionRatio): customer.CommissionRatio = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, "未知客户数值列");
        }
    }

    private static decimal GetCustomerDecimal(BaseCustomer customer, string property) => property switch
    {
        nameof(BaseCustomer.CreditLimit) => customer.CreditLimit,
        nameof(BaseCustomer.DepositRatio) => customer.DepositRatio,
        nameof(BaseCustomer.CommissionRatio) => customer.CommissionRatio,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "未知客户数值列")
    };

    // ==================== 6. 测试脚手架（真实控制器 + 真实已认证身份） ====================

    private static SupplierController SupplierController(ErpDbContext db, long? userId) =>
        new(new GenericService<BaseSupplier>(db), db)
        {
            ControllerContext = ContextWithUser(userId, "/api/base/suppliers")
        };

    private static CustomerController CustomerController(ErpDbContext db, long? userId) =>
        new(new GenericService<BaseCustomer>(db), db)
        {
            ControllerContext = ContextWithUser(userId, "/api/base/customers")
        };

    /// <summary>
    /// 带（可空）<c>NameIdentifier</c> 的真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：
    /// null = 无身份，真实请求因处于请求管线内一律 fail closed 拒绝。
    /// </summary>
    private static ControllerContext ContextWithUser(long? userId, string path)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = path;
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个具备既有「供应商资料」菜单授权的独立启用身份，返回用户 Id（每个用例独立）</summary>
    private static long SeedSupplierUser(ErpDbContext db)
        => SeedUser(db, SupplierAuthorizationRules.RequiredMenuCode, SupplierAuthorizationRules.RequiredMenuText,
            "supplier-prec", "供应商精度用例账号");

    /// <summary>播种一个具备既有「客户资料」菜单授权的独立启用身份，返回用户 Id（每个用例独立）</summary>
    private static long SeedCustomerUser(ErpDbContext db)
        => SeedUser(db, CustomerAuthorizationRules.RequiredMenuCode, CustomerAuthorizationRules.RequiredMenuText,
            "customer-prec", "客户精度用例账号");

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单，不新增菜单模型）</summary>
    private static long SeedUser(ErpDbContext db, string menuCode, string menuText, string prefix, string displayName)
    {
        var user = new SysUser
        {
            UserName = $"{prefix}-{Guid.NewGuid():N}",
            DisplayName = displayName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = displayName,
            RoleCode = $"{prefix}-role-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuText, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        if (!db.SysRoleMenus.Any(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }
        return user.Id;
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseSupplier NewSupplier(string code = "S-NEW", string name = "新供应商", int status = 1)
        => new() { SupplierCode = code, SupplierName = name, Status = status };

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseCustomer NewCustomer(string code = "C-NEW", string name = "新客户", int status = 1)
        => new() { CustomerCode = code, CustomerName = name, Status = status, CreditStatus = "正常" };

    /// <summary>供应商 / 客户主表快照（含全部 ERP-531 数值列；拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseSuppliers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"S:{x.Id}:{x.Status}:{x.IsDeleted}:{x.SupplierCode}:{x.SupplierName}:" +
                         $"{x.TaxRate}:{x.RebateRatio}")
            .ToList()
            .Concat(db.BaseCustomers.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => $"C:{x.Id}:{x.Status}:{x.IsDeleted}:{x.CustomerCode}:{x.CustomerName}:" +
                             $"{x.CreditLimit}:{x.DepositRatio}:{x.CommissionRatio}")
                .ToList()));

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
