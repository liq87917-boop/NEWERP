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
/// ERP-532 样品管理（<c>api/crm/samples</c>）数量（<c>Quantity</c>）与样品费（<c>SampleFee</c>）
/// **实际存储精度**（<c>DECIMAL(18,4)</c>）边界单元测试（内存库 + 真实控制器 + 真实已认证身份）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：两列显式 <c>decimal(18,4)</c>
/// （仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>纯规则：四位小数与量级边界的逐位原样可表示判定（恰好上限放行、第 5 位小数与量级溢出拒绝、
/// 「半进位取整后恰好落到上限」的过小数位值仍拒绝、「当前已接受的边界值」继续放行）；</item>
/// <item><see cref="SampleAuthorizationRules.Validate"/>：<b>每一个</b>数值列的第 5 位小数 / 量级溢出 / 负数都以既有
/// 受控参数错误拒绝，且逐字段不变（绝不取整、截断或改写）；</item>
/// <item>真实控制器新增 / 修改：不可表示的值零 <c>Samples</c> 行、零改写，合法 4 位小数原样往返；</item>
/// <item>源码契约：EF 映射为两列声明 <c>HasPrecision(18,4)</c>，且不新增任何表 / 列 / 索引。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返 / 越界被真实列拒绝」由
/// <c>ERP.IntegrationTests/SamplePersistedNumericBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class SamplePersistedNumericBoundsTests
{
    /// <summary>样品数值列的有效位数（<c>DECIMAL(18,4)</c>，与建表脚本同源）。</summary>
    private const int DecimalPrecision = 18;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = SampleAuthorizationRules.MaxDecimalMagnitude;

    /// <summary>样品持久化 decimal 列（属性名 + 中文标签，与实体 / 建表脚本逐一对应）。</summary>
    private static readonly (string Property, string Label)[] DecimalColumns =
    {
        (nameof(Sample.Quantity), "数量"),
        (nameof(Sample.SampleFee), "样品费"),
    };

    /// <summary>两个 decimal 列名（供逐列 <c>[Theory]</c> 使用）。</summary>
    public static IEnumerable<object[]> DecimalColumnNames()
        => DecimalColumns.Select(c => new object[] { c.Property });

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    /// <summary>SQL Server 提供程序仅构建模型：两列 <c>GetPrecision() = 18</c>、<c>GetScale() = 4</c>。</summary>
    [Fact]
    public void 实际EF列精度_样品两列均为18_4()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP532_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(DecimalPrecision, 18);
        Assert.Equal(4, SampleAuthorizationRules.PersistedDecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        var entityType = db.Model.FindEntityType(typeof(Sample));
        Assert.NotNull(entityType);
        foreach (var (property, _) in DecimalColumns)
        {
            var mapped = entityType!.FindProperty(property);
            Assert.NotNull(mapped);
            Assert.Equal(18, mapped!.GetPrecision());
            Assert.Equal(SampleAuthorizationRules.PersistedDecimalScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());
        }
    }

    // ==================== 2. 可表示性纯判定 ====================

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("0.0125")]
    [InlineData("13.0555")]
    [InlineData("0.0001")]
    [InlineData("123456789.0123")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(SampleAuthorizationRules.IsPersistedDecimalRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1.00005")]
    [InlineData("0.00001")]
    [InlineData("0.01255")]
    [InlineData("99999999999999.99985")]
    [InlineData("99999999999999.99995")]
    [InlineData("100000000000000")]
    [InlineData("-99999999999999.99985")]
    public void 不可表示_第五位小数或量级溢出拒绝(string raw)
        => Assert.False(SampleAuthorizationRules.IsPersistedDecimalRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    // ==================== 3. Validate：逐列过小数位 / 量级溢出 / 负数拒绝且不改写字段 ====================

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void Validate_逐列第5位小数受控拒绝且不改写字段(string property)
    {
        var entity = NewSample(quantity: 1m, sampleFee: 1m);
        SetColumn(entity, property, 1.00005m);

        var ex = Assert.Throws<BusinessException>(() => SampleAuthorizationRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
        Assert.Equal(1.00005m, GetColumn(entity, property));
    }

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void Validate_逐列量级溢出受控拒绝且不改写字段(string property)
    {
        var entity = NewSample(quantity: 1m, sampleFee: 1m);
        SetColumn(entity, property, Max4 + 1m);

        var ex = Assert.Throws<BusinessException>(() => SampleAuthorizationRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
        Assert.Equal(Max4 + 1m, GetColumn(entity, property));
    }

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void Validate_逐列负数受控拒绝且不改写字段(string property)
    {
        var entity = NewSample(quantity: 1m, sampleFee: 1m);
        SetColumn(entity, property, -0.0001m);

        var ex = Assert.Throws<BusinessException>(() => SampleAuthorizationRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
        Assert.Equal(-0.0001m, GetColumn(entity, property));
    }

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void Validate_逐列四位小数与上限放行且逐位原样(string property)
    {
        foreach (var value in new[] { 0m, 1.5m, 0.0125m, 13.0555m, 0.0001m, Max4 })
        {
            var entity = NewSample(quantity: 1m, sampleFee: 1m);
            SetColumn(entity, property, value);

            SampleAuthorizationRules.Validate(entity);

            Assert.Equal(value, GetColumn(entity, property));
        }
    }

    /// <summary>既有已接受边界值（0 / 1 位小数 / 恰好上限）继续放行且不改写字段。</summary>
    [Fact]
    public void Validate_既有已接受边界值继续放行()
    {
        var entity = NewSample(quantity: 0m, sampleFee: 0m);
        SampleAuthorizationRules.Validate(entity);
        Assert.Equal(0m, entity.Quantity);
        Assert.Equal(0m, entity.SampleFee);

        entity = NewSample(quantity: 1.5m, sampleFee: Max4);
        SampleAuthorizationRules.Validate(entity);
        Assert.Equal(1.5m, entity.Quantity);
        Assert.Equal(Max4, entity.SampleFee);
    }


    // ==================== 4. 真实控制器：不可表示值零写入、合法 4 位小数原样往返 ====================

    [Fact]
    public async Task Controller_第5位小数与量级溢出新增受控拒绝零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var customer = SeedCustomer(db, "C001", "客户");
        var ctl = Controller(db, userId);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewSample("SP-5DP", customer.Id, quantity: 0.01255m)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewSample("SP-HALF", customer.Id, quantity: 99999999999999.99985m)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewSample("SP-OVR", customer.Id, sampleFee: Max4 + 1m)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewSample("SP-NEG", customer.Id, sampleFee: -0.0001m)));

        Assert.Equal(0, await db.Samples.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Controller_四位小数新增与修改原样往返()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var customer = SeedCustomer(db, "C001", "客户");
        var ctl = Controller(db, userId);

        var created = Data<Sample>(await ctl.Create(NewSample(
            "SP-4DP", customer.Id, quantity: 0.0125m, sampleFee: 1234.5678m)));
        Assert.Equal(0.0125m, created.Quantity);
        Assert.Equal(1234.5678m, created.SampleFee);

        var updated = Data<Sample>(await ctl.Update(created.Id, NewSample(
            "SP-4DP", customer.Id, quantity: 0.0001m, sampleFee: Max4)));
        Assert.Equal(0.0001m, updated.Quantity);
        Assert.Equal(Max4, updated.SampleFee);
    }

    [Fact]
    public async Task Controller_被拒修改不改写既有行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var customer = SeedCustomer(db, "C001", "客户");
        var existing = SeedSample(db, "SP-EXIST", customer.Id);
        existing.Quantity = 0.0125m;
        existing.SampleFee = 13.0555m;
        await db.SaveChangesAsync();
        var ctl = Controller(db, userId);

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(existing.Id, NewSample("SP-EXIST", customer.Id, quantity: 0.01255m)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(existing.Id, NewSample("SP-EXIST", customer.Id, sampleFee: Max4 + 1m)));
        Assert.Equal(before, Snapshot(db));

        var stored = await db.Samples.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(0.0125m, stored.Quantity);
        Assert.Equal(13.0555m, stored.SampleFee);
    }

    // ==================== 5. 源码契约 ====================

    /// <summary>EF 映射为两列声明 <c>HasPrecision(18, 4)</c>，不改任何列类型 / 新增索引。</summary>
    [Fact]
    public void 源码契约_EF映射声明两列四位小数精度()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs"));
        Assert.Contains("Entity<Sample>().Property(x => x.Quantity).HasPrecision(18, 4)", source);
        Assert.Contains("Entity<Sample>().Property(x => x.SampleFee).HasPrecision(18, 4)", source);
    }


    // ==================== 测试脚手架 ====================

    private static decimal GetColumn(Sample entity, string property) => property switch
    {
        nameof(Sample.Quantity) => entity.Quantity,
        _ => entity.SampleFee,
    };

    private static void SetColumn(Sample entity, string property, decimal value)
    {
        if (property == nameof(Sample.Quantity)) entity.Quantity = value;
        else entity.SampleFee = value;
    }

    private static SampleController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<Sample>(db), db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：仅注入 <c>NameIdentifier</c>。</summary>
    private static ControllerContext ContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/crm/samples";
        return new ControllerContext { HttpContext = http };
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        if (!db.SysRoleMenus.Any(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }


    /// <summary>播种具备既有「样品管理」菜单授权的真实启用身份（复用既有菜单，不新增权限模型）。</summary>
    private static long SeedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"sample-bounds-{Guid.NewGuid():N}",
            DisplayName = "样品精度用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "样品精度用例角色",
            RoleCode = $"SampleBoundsCase-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenu(db, role.Id, SampleAuthorizationRules.RequiredMenuCode, SampleAuthorizationRules.RequiredMenuText);
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
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Sample SeedSample(
        ErpDbContext db, string sampleNo, long? customerId, string customerName = "客户")
    {
        var sample = new Sample
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            SampleType = "打样",
            Quantity = 1m,
            SampleFee = 0m,
            Result = "待反馈",
            IsDeleted = false
        };
        db.Samples.Add(sample);
        db.SaveChanges();
        return sample;
    }

    private static Sample NewSample(
        string sampleNo = "SP-NEW", long? customerId = null, string customerName = "客户",
        decimal quantity = 1m, decimal sampleFee = 0m)
        => new()
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            SampleType = "打样",
            Quantity = quantity,
            Unit = "PCS",
            SampleFee = sampleFee,
            Currency = "CNY",
            Express = "顺丰",
            TrackingNo = "SF001",
            Result = "待反馈",
            Remark = ""
        };

    /// <summary>样品快照（校验拒绝后必须逐字节不变）。</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.Samples.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.IsDeleted}:{x.SampleNo}:{x.CustomerId}:{x.Quantity}:{x.SampleFee}")
            .ToList());

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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

