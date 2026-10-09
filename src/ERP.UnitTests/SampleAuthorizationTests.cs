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
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 样品管理（<c>api/crm/samples</c>，样品管理页面维护）实时授权、ERP-097 业务员数据范围与有界字段校验单元测试
/// （ERP-459）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「样品管理」菜单 / 无菜单的身份在<b>全部 7 条路由</b>
/// （分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除）fail closed，且 <c>Samples</c> 行逐字节不变
/// （拒绝既不读取也不改写任何行）；</item>
/// <item><b>放行</b>：具备既有「样品管理」菜单的授权身份下，既有读 / 写契约与分页 / 响应契约保持；</item>
/// <item><b>ERP-097 数据范围</b>：授权身份下的受限业务员仍只看到自己被分配客户的样品，越界读取按「不存在」
/// fail closed，写入的客户必须落在范围内；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段校验</b>：样品编号空值 / 文本长度越界 / 数量与样品费为负或超范围 / 客户、商品与业务员引用非法
/// 一律按受控参数错误拒绝且零写入；</item>
/// <item><b>源码契约</b>：控制器 7 条路由全部先授权再读写，且只复用既有 <c>sample</c> 菜单，
/// 不按请求路径 / 环境 / 空请求降级（无 <c>AllowAnonymous</c> / 角色回退）。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class SampleAuthorizationTests
{
    private const string RequiredMenuCode = "sample";

    // ==================== 0. 测试脚手架 ====================

    private static SampleController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<Sample>(db), db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>
    /// 真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：null = 无身份。
    /// 控制器<b>不</b>按请求路径降级，因此缺失身份一律实时授权并 fail closed。
    /// </summary>
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

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 样品菜单 / 特权角色），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantMenu = true, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"sample-auth-{Guid.NewGuid():N}",
            DisplayName = "样品授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "样品授权用例角色",
            RoleCode = $"SampleAuthCase-{Guid.NewGuid():N}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, RequiredMenuCode, SampleAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>播种受限业务员（登录账号 == 员工编码，非特权角色），返回账号 / 员工</summary>
    private static (long UserId, BaseEmployee Employee) SeedRestrictedSalesman(ErpDbContext db, bool grantMenu = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"sales-{Guid.NewGuid():N}",
            EmployeeName = "受限业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            DisplayName = "受限业务员账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "Sales", RoleCode = $"Sales-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, RequiredMenuCode, SampleAuthorizationRules.RequiredMenuText);
        return (user.Id, employee);
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string name, bool deleted = false)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = name, Status = 1, IsDeleted = deleted };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
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
        long? productId = null, string productName = "样品", string spec = "规格",
        string sampleType = "打样", decimal quantity = 1m, string unit = "PCS",
        decimal sampleFee = 0m, string currency = "CNY", string express = "顺丰",
        string trackingNo = "SF001", string result = "待反馈", long? salesmanId = null,
        string salesmanName = "张三", string remark = "")
        => new()
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            ProductId = productId,
            ProductName = productName,
            Spec = spec,
            SampleType = sampleType,
            Quantity = quantity,
            Unit = unit,
            SampleFee = sampleFee,
            Currency = currency,
            Express = express,
            TrackingNo = trackingNo,
            Result = result,
            SalesmanId = salesmanId,
            SalesmanName = salesmanName,
            Remark = remark
        };

    /// <summary>样品快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.Samples.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.IsDeleted}:{x.SampleNo}:{x.CustomerId}:{x.CustomerName}:{x.ProductId}:" +
                         $"{x.Quantity}:{x.SampleFee}:{x.Result}:{x.SalesmanId}:{x.Remark}")
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

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有样品菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「样品管理」菜单 / 无任何菜单的身份：全部 7 条路由
    /// 在读取或写入任何样品之前 fail closed，且样品快照逐字节不变。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何样品(string scenario)
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var sample = SeedSample(db, "SP-1", customer.Id);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetAll());
        await AssertCode(expectedCode, () => ctl.GetById(sample.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewSample("SP-DENIED", customer.Id)));
        await AssertCode(expectedCode, () => ctl.Update(sample.Id, NewSample("SP-1", customer.Id, "被拒改名")));
        await AssertCode(expectedCode, () => ctl.Delete(sample.Id));
        await AssertCode(expectedCode, () => ctl.BatchDelete(new List<long> { sample.Id }));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.Samples.AsNoTracking().SingleAsync(x => x.Id == sample.Id);
        Assert.Equal("SP-1", stored.SampleNo);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    /// <summary>具备既有「样品管理」菜单的授权身份：分页 / 全部 / 按主键 / 新增 / 修改 / 软删除 / 批量删除全部放行。</summary>
    [Fact]
    public async Task Auth_授权身份_既有读写契约全部放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var product = SeedProduct(db, "P001", "保温杯");
        var employee = SeedEmployee(db, "emp-1");
        var ctl = Controller(db, userId);

        var created = Data<Sample>(await ctl.Create(NewSample(
            "SP-OK", customer.Id, "客户", product.Id, salesmanId: employee.Id)));
        Assert.True(created.Id > 0);

        var page = Data<PagedResult<Sample>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, s => s.Id == created.Id && s.SampleNo == "SP-OK");
        Assert.Contains(Data<List<Sample>>(await ctl.GetAll()), s => s.Id == created.Id);
        Assert.Equal("SP-OK", Data<Sample>(await ctl.GetById(created.Id)).SampleNo);

        var updated = Data<Sample>(
            await ctl.Update(created.Id, NewSample("SP-OK", customer.Id, "改名客户", product.Id)));
        Assert.Equal("改名客户", updated.CustomerName);

        var second = Data<Sample>(await ctl.Create(NewSample("SP-OK2", customer.Id)));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.Samples.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.Samples.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    // ==================== 3. ERP-097 受限业务员读取 / 写入范围 ====================

    /// <summary>
    /// 授权身份下的受限业务员（登录账号 == 员工编码）沿用 ERP-097 数据范围：分页 / 全部只返回自己被分配客户的
    /// 样品，越界读取按「不存在」fail closed；新增 / 修改必须写入范围内客户，范围外请求拒绝且零写入。
    /// </summary>
    [Fact]
    public async Task Auth_授权身份_保留ERP097受限业务员范围()
    {
        using var db = TestDbFactory.Create();
        var (userId, employee) = SeedRestrictedSalesman(db);
        var mine = SeedCustomer(db, "C-MINE", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C-OTHER", "别人的客户", employee.Id + 1000);
        var mineSample = SeedSample(db, "SP-MINE", mine.Id, "我的客户");
        var otherSample = SeedSample(db, "SP-OTHER", other.Id, "别人的客户");
        SeedSample(db, "SP-NULL", null, "匿名客户");

        var ctl = Controller(db, userId);

        var page = Data<PagedResult<Sample>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Single(page.Items);
        Assert.Equal(mineSample.Id, page.Items[0].Id);
        Assert.Equal(1, page.Total);

        var all = Data<List<Sample>>(await ctl.GetAll());
        Assert.Single(all);
        Assert.Equal(mineSample.Id, all[0].Id);

        Assert.Equal(mineSample.Id, Data<Sample>(await ctl.GetById(mineSample.Id)).Id);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetById(otherSample.Id));

        var created = Data<Sample>(await ctl.Create(NewSample("SP-MINE-NEW", mine.Id, "我的客户")));
        Assert.True(created.Id > 0);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewSample("SP-OTHER-NEW", other.Id, "别人的客户")));
        await AssertCode(ErrorCodes.Forbidden,
            () => ctl.Update(mineSample.Id, NewSample("SP-MINE", other.Id, "别人的客户")));

        var after = Snapshot(db);
        Assert.Contains("SP-MINE-NEW", after);
        Assert.DoesNotContain("SP-OTHER-NEW", after);
        Assert.Equal(1, await db.Samples.AsNoTracking().CountAsync(s => s.CustomerId == other.Id));
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var sample = SeedSample(db, "SP-1", customer.Id);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<Sample>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewSample("SP-REVOKED", customer.Id)));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(sample.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { sample.Id }));

        Assert.False((await db.Samples.AsNoTracking().SingleAsync(x => x.Id == sample.Id)).IsDeleted);
        Assert.Equal(1, await db.Samples.AsNoTracking().CountAsync());
    }

    // ==================== 4. 有界字段校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>
    /// 授权身份下的非法载荷（样品编号空值 / 文本长度越界 / 数量与样品费为负或超范围 / 客户、商品与业务员引用非法）：
    /// 新增与修改都按受控参数错误拒绝，且不落任何新行、不改写任何既有行。
    /// </summary>
    [Theory]
    [InlineData("empty-sampleno")]
    [InlineData("sampleno-too-long")]
    [InlineData("sampletype-too-long")]
    [InlineData("unit-too-long")]
    [InlineData("currency-too-long")]
    [InlineData("express-too-long")]
    [InlineData("trackingno-too-long")]
    [InlineData("result-too-long")]
    [InlineData("customername-too-long")]
    [InlineData("productname-too-long")]
    [InlineData("spec-too-long")]
    [InlineData("salesmanname-too-long")]
    [InlineData("remark-too-long")]
    [InlineData("quantity-negative")]
    [InlineData("samplefee-negative")]
    [InlineData("quantity-too-large")]
    [InlineData("samplefee-too-large")]
    [InlineData("customer-missing")]
    [InlineData("customer-unknown")]
    [InlineData("customer-deleted")]
    [InlineData("product-unknown")]
    [InlineData("product-deleted")]
    [InlineData("salesman-unknown")]
    [InlineData("salesman-deleted")]
    public async Task Validation_非法载荷_新增与修改均拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var deletedCustomer = SeedCustomer(db, "C-DEL", "已删客户");
        deletedCustomer.IsDeleted = true;
        db.SaveChanges();
        var product = SeedProduct(db, "P001", "商品");
        var deletedProduct = SeedProduct(db, "P-DEL", "已删商品", deleted: true);
        var employee = SeedEmployee(db, "emp-1");
        var deletedEmployee = SeedEmployee(db, "emp-del");
        deletedEmployee.IsDeleted = true;
        db.SaveChanges();
        var existing = SeedSample(db, "SP-EXIST", customer.Id, "既有客户");
        var ctl = Controller(db, userId);

        Sample Payload() => scenario switch
        {
            "empty-sampleno" => NewSample("", customer.Id),
            "sampleno-too-long" => NewSample(
                new string('N', SampleAuthorizationRules.MaxSampleNoLength + 1), customer.Id),
            "sampletype-too-long" => NewSample("SP-1", customer.Id,
                sampleType: new string('T', SampleAuthorizationRules.MaxSampleTypeLength + 1)),
            "unit-too-long" => NewSample("SP-1", customer.Id,
                unit: new string('U', SampleAuthorizationRules.MaxUnitLength + 1)),
            "currency-too-long" => NewSample("SP-1", customer.Id,
                currency: new string('C', SampleAuthorizationRules.MaxCurrencyLength + 1)),
            "express-too-long" => NewSample("SP-1", customer.Id,
                express: new string('E', SampleAuthorizationRules.MaxExpressLength + 1)),
            "trackingno-too-long" => NewSample("SP-1", customer.Id,
                trackingNo: new string('K', SampleAuthorizationRules.MaxTrackingNoLength + 1)),
            "result-too-long" => NewSample("SP-1", customer.Id,
                result: new string('R', SampleAuthorizationRules.MaxResultLength + 1)),
            "customername-too-long" => NewSample("SP-1", customer.Id,
                customerName: new string('C', SampleAuthorizationRules.MaxCustomerNameLength + 1)),
            "productname-too-long" => NewSample("SP-1", customer.Id,
                productName: new string('P', SampleAuthorizationRules.MaxProductNameLength + 1)),
            "spec-too-long" => NewSample("SP-1", customer.Id,
                spec: new string('S', SampleAuthorizationRules.MaxSpecLength + 1)),
            "salesmanname-too-long" => NewSample("SP-1", customer.Id,
                salesmanName: new string('S', SampleAuthorizationRules.MaxSalesmanNameLength + 1)),
            "remark-too-long" => NewSample("SP-1", customer.Id,
                remark: new string('M', SampleAuthorizationRules.MaxRemarkLength + 1)),
            "quantity-negative" => NewSample("SP-1", customer.Id, quantity: -1m),
            "samplefee-negative" => NewSample("SP-1", customer.Id, sampleFee: -1m),
            "quantity-too-large" => NewSample(
                "SP-1", customer.Id, quantity: SampleAuthorizationRules.MaxDecimalMagnitude + 1m),
            "samplefee-too-large" => NewSample(
                "SP-1", customer.Id, sampleFee: SampleAuthorizationRules.MaxDecimalMagnitude + 1m),
            "customer-missing" => NewSample("SP-1", null),
            "customer-unknown" => NewSample("SP-1", 999999),
            "customer-deleted" => NewSample("SP-1", deletedCustomer.Id),
            "product-unknown" => NewSample("SP-1", customer.Id, productId: 999999),
            "product-deleted" => NewSample("SP-1", customer.Id, productId: deletedProduct.Id),
            "salesman-unknown" => NewSample("SP-1", customer.Id, salesmanId: 999999),
            _ => NewSample("SP-1", customer.Id, salesmanId: deletedEmployee.Id)
        };

        var before = Snapshot(db);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.Samples.AsNoTracking().CountAsync());

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, Payload()));
        Assert.Equal(before, Snapshot(db));
        var stored = await db.Samples.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("SP-EXIST", stored.SampleNo);
    }

    /// <summary>边界值必须放行：各文本字段恰好等于持久化长度上限、数值等于 <c>DECIMAL(18,4)</c> 上限时接受。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, privileged: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var employee = SeedEmployee(db, "emp-1");
        var ctl = Controller(db, userId);

        var sampleNoAtLimit = new string('N', SampleAuthorizationRules.MaxSampleNoLength);
        var created = Data<Sample>(await ctl.Create(NewSample(
            sampleNoAtLimit, customer.Id,
            customerName: new string('C', SampleAuthorizationRules.MaxCustomerNameLength),
            productName: new string('P', SampleAuthorizationRules.MaxProductNameLength),
            spec: new string('S', SampleAuthorizationRules.MaxSpecLength),
            sampleType: new string('T', SampleAuthorizationRules.MaxSampleTypeLength),
            unit: new string('U', SampleAuthorizationRules.MaxUnitLength),
            currency: new string('Y', SampleAuthorizationRules.MaxCurrencyLength),
            express: new string('E', SampleAuthorizationRules.MaxExpressLength),
            trackingNo: new string('K', SampleAuthorizationRules.MaxTrackingNoLength),
            result: new string('R', SampleAuthorizationRules.MaxResultLength),
            salesmanId: employee.Id,
            salesmanName: new string('M', SampleAuthorizationRules.MaxSalesmanNameLength),
            remark: new string('B', SampleAuthorizationRules.MaxRemarkLength),
            quantity: SampleAuthorizationRules.MaxDecimalMagnitude,
            sampleFee: SampleAuthorizationRules.MaxDecimalMagnitude)));
        Assert.Equal(sampleNoAtLimit, created.SampleNo);
        Assert.Equal(SampleAuthorizationRules.MaxDecimalMagnitude, created.Quantity);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewSample(sampleNoAtLimit + "N", customer.Id)));
        Assert.Equal(1, await db.Samples.AsNoTracking().CountAsync());
    }

    // ==================== 5. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：7 条路由全部先经实时授权再读写，且无匿名 / 角色 / 请求路径 / 环境回退。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SampleController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(7, Regex.Matches(source, @"await EnsureAuthorizedScopeAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
        Assert.DoesNotContain("Request.Path", source);
        Assert.DoesNotContain("Environment.", source);
    }

    /// <summary>授权口径复用既有「样品管理」菜单（与 SchemaUpgrader 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有样品菜单常量_不新增菜单()
    {
        Assert.Equal("sample", SampleAuthorizationRules.RequiredMenuCode);
        Assert.Equal("样品管理", SampleAuthorizationRules.RequiredMenuText);
        Assert.Contains("fail closed", SampleAuthorizationRules.AuthorizationRuleText);
        Assert.Contains("ERP-097", SampleAuthorizationRules.ScopeRuleText);

        var upgrader = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.Contains("N'sample'", upgrader);
        Assert.Contains("N'样品管理'", upgrader);
    }
}
