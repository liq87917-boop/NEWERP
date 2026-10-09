using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 出口退税台账实时授权、权威客户范围与金额 / 税率 / 期间 / 日期护栏单元测试（ERP-442，内存库）。
/// 覆盖：实时身份 / 账号状态 / 既有「出口退税台账」（tax-refund）菜单授权 / 未映射业务员 fail closed；
/// 受限 / 特权数据范围下推（分页 / 全部 / 详情同口径，越界与无主一律不泄露）；完整 CRUD 覆盖
/// （详情 / 新增 / 修改 / 删除 / 批量删除的范围与真实性与字段校验，混合批次整体拒绝、无部分删除）；
/// 以及「拒绝时不静默截断 / 回填、不落任何台账行」。
/// <para>进程内直接调用控制器动作仍会实时解析身份（无身份 fail closed），绝不把空身份当作管理员。</para>
/// </summary>
public class TaxRefundLedgerTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>绑定已认证身份的出口退税台账控制器（<paramref name="userId"/> 为 null 表示未认证请求主体）。</summary>
    private static TaxRefundController Controller(ErpDbContext db, long? userId)
    {
        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new TaxRefundController(new GenericService<BaseTaxRefund>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static SalespersonDataScope RestrictedScope(long salesmanId, params long[] allowedCustomerIds) => new()
    {
        IsPrivileged = false,
        SalesmanId = salesmanId,
        AllowedCustomerIds = allowedCustomerIds.ToHashSet()
    };

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, long? empId = null, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = code,
            Status = status,
            CreditStatus = "正常",
            Currency = "USD",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseTaxRefund SeedLedger(ErpDbContext db, string refundNo, long? customerId,
        decimal exportAmount = 1000m, decimal refundRate = 13m, decimal refundableAmount = 130m,
        decimal refundedAmount = 0m, string currency = "USD", string period = "2026-08")
    {
        var entity = new BaseTaxRefund
        {
            RefundNo = refundNo,
            RefundPeriod = period,
            DeclareDate = new DateTime(2026, 8, 5),
            CustomerId = customerId,
            CustomerName = customerId is > 0 ? $"客户{customerId}" : string.Empty,
            ExportAmount = exportAmount,
            Currency = currency,
            RefundRate = refundRate,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            Status = "待申报"
        };
        db.BaseTaxRefunds.Add(entity);
        db.SaveChanges();
        return entity;
    }

    /// <summary>播种既有「出口退税台账」菜单（与生产 SchemaUpgrader 同编码，不为测试新增任何授权模型）。</summary>
    private static SysMenu EnsureTaxRefundMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == TaxRefundLedgerRules.RequiredMenuCode);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            MenuName = TaxRefundLedgerRules.RequiredMenuText,
            MenuCode = TaxRefundLedgerRules.RequiredMenuCode,
            Path = "/finance/tax-refund",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种受限业务员账号：登录名 = 员工编码（ERP-097 权威映射），可选既有菜单授权。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool grantMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"TAX442-OP-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"退税台账操作角色-{code}", RoleCode = $"TAX442-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = EnsureTaxRefundMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    /// <summary>播种特权账号（系统内置角色，ERP-097 特权口径），仅需实时身份校验。</summary>
    private static async Task<SysUser> SeedPrivilegedUserAsync(ErpDbContext db)
    {
        var code = $"TAX442-ADM-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"退税台账系统角色-{code}", RoleCode = $"TAX442-SYS-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user;
    }

    private static BaseTaxRefund Draft(long? customerId, decimal exportAmount = 1000m, decimal refundRate = 13m,
        decimal refundableAmount = 130m, decimal refundedAmount = 0m, string currency = "USD",
        string period = "2026-08", DateTime? declareDate = null, DateTime? refundDate = null)
        => new()
        {
            RefundNo = $"TR-{Guid.NewGuid():N}",
            RefundPeriod = period,
            DeclareDate = declareDate ?? new DateTime(2026, 8, 5),
            CustomerId = customerId,
            ExportAmount = exportAmount,
            Currency = currency,
            RefundRate = refundRate,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            RefundDate = refundDate,
            Status = "待申报"
        };


    // ==================== 1. 金额 / 税率 / 期间 / 日期校验（纯规则） ====================

    [Fact]
    public void 校验_合法台账_通过_不改写字段()
    {
        var entity = Draft(customerId: null, exportAmount: 1000m, refundRate: 13m,
            refundableAmount: 130m, refundedAmount: 30m, currency: "USD", period: "2026-08",
            declareDate: new DateTime(2026, 8, 5), refundDate: new DateTime(2026, 9, 20));

        TaxRefundLedgerRules.Validate(entity);

        Assert.Equal(1000m, entity.ExportAmount);
        Assert.Equal(13m, entity.RefundRate);
        Assert.Equal(130m, entity.RefundableAmount);
        Assert.Equal(30m, entity.RefundedAmount);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1000)]
    public void 校验_出口金额为负_拒绝(decimal value)
    {
        var entity = Draft(customerId: null, exportAmount: value);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("ExportAmount", ex.Message);
        Assert.Equal(value, entity.ExportAmount);   // 不静默截断 / 回填
    }

    [Fact]
    public void 校验_可退税额为负_拒绝()
    {
        var entity = Draft(customerId: null, refundableAmount: -1m, refundedAmount: 0m);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundableAmount", ex.Message);
        Assert.Equal(-1m, entity.RefundableAmount);
    }

    [Fact]
    public void 校验_已退税额为负_拒绝()
    {
        var entity = Draft(customerId: null, refundableAmount: 0m, refundedAmount: -5m);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundedAmount", ex.Message);
        Assert.Equal(-5m, entity.RefundedAmount);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(150)]
    public void 校验_退税率越界_拒绝(decimal rate)
    {
        var entity = Draft(customerId: null, refundRate: rate);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundRate", ex.Message);
        Assert.Equal(rate, entity.RefundRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void 校验_退税率边界_通过(decimal rate)
    {
        var entity = Draft(customerId: null, refundRate: rate);
        TaxRefundLedgerRules.Validate(entity);
        Assert.Equal(rate, entity.RefundRate);
    }

    [Fact]
    public void 校验_已退税超过可退税_拒绝()
    {
        var entity = Draft(customerId: null, refundableAmount: 100m, refundedAmount: 100.01m);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundedAmount", ex.Message);
        Assert.Equal(100.01m, entity.RefundedAmount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("USDT-USDT-USDT-USDT-US")]   // 21 字符 > 20
    public void 校验_币种无效_拒绝(string currency)
    {
        var entity = Draft(customerId: null, currency: currency);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("Currency", ex.Message);
        Assert.Equal(currency, entity.Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026-08-EXTENDED-PERIOD")]  // > 20 字符
    public void 校验_退税期间无效_拒绝(string period)
    {
        var entity = Draft(customerId: null, period: period);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundPeriod", ex.Message);
        Assert.Equal(period, entity.RefundPeriod);
    }

    [Fact]
    public void 校验_到账日期早于申报日期_拒绝()
    {
        var entity = Draft(customerId: null,
            declareDate: new DateTime(2026, 8, 20), refundDate: new DateTime(2026, 8, 19));
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("RefundDate", ex.Message);
        Assert.Equal(new DateTime(2026, 8, 19), entity.RefundDate);   // 不静默回填
    }

    [Fact]
    public void 校验_申报日期极小值_拒绝()
    {
        var entity = Draft(customerId: null, declareDate: DateTime.MinValue, refundDate: null);
        var ex = Assert.Throws<BusinessException>(() => TaxRefundLedgerRules.Validate(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal((DateTime?)DateTime.MinValue, entity.DeclareDate);
    }

    [Fact]
    public void 校验_同一申报与到账日期_通过()
    {
        var day = new DateTime(2026, 8, 20);
        var entity = Draft(customerId: null, declareDate: day, refundDate: day);
        TaxRefundLedgerRules.Validate(entity);
    }


    // ==================== 2. 实时身份 / 账号状态 / 菜单授权（规则层） ====================

    [Fact]
    public async Task 规则层_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 规则层_非法身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 规则层_账号不存在_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, 987654));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 规则层_账号已删除_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db);
        user.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 规则层_账号已禁用_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db, status: UserStatus.Disabled);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("已禁用", ex.Message);
    }

    [Fact]
    public async Task 规则层_无既有菜单授权_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db, grantMenu: false);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(TaxRefundLedgerRules.RequiredMenuCode, ex.Message);
    }

    [Fact]
    public async Task 规则层_未映射业务员_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var menu = EnsureTaxRefundMenu(db);
        var user = new SysUser
        {
            UserName = "tax442-not-a-salesman", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "未映射", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "角色", RoleCode = $"R-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射", ex.Message);
    }

    [Fact]
    public async Task 规则层_菜单加业务员映射_返回受限实时范围()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-OWN", employee.Id);
        SeedCustomer(db, "TAX442-FOREIGN", empId: null);

        var scope = await TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, user.Id);

        Assert.False(scope.IsPrivileged);
        Assert.Equal(employee.Id, scope.SalesmanId);
        Assert.True(scope.AllowsCustomer(own.Id));
        Assert.False(scope.AllowsCustomer(null));
    }

    [Fact]
    public async Task 规则层_特权账号_不过滤且豁免菜单()
    {
        using var db = TestDbFactory.Create();
        var user = await SeedPrivilegedUserAsync(db);

        var scope = await TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(db, user.Id);

        Assert.True(scope.IsPrivileged);
        Assert.Null(scope.AllowedCustomerIds);
        Assert.True(scope.AllowsCustomer(null));   // 历史无主行对特权账号可见
    }


    // ==================== 3. 控制器：范围读取 ====================

    private static async Task<PagedResult<BaseTaxRefund>> PageAsync(TaxRefundController controller)
    {
        var result = await controller.GetPaged(new PageQuery());
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<PagedResult<BaseTaxRefund>>>(ok.Value).Data!;
    }

    [Fact]
    public async Task 控制器_特权_分页包含全部行含无主行()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);
        var customer = SeedCustomer(db, "TAX442-P-OWN");
        var own = SeedLedger(db, "TR-P-1", customer.Id);
        var orphan = SeedLedger(db, "TR-P-2", null);

        var page = await PageAsync(Controller(db, admin.Id));

        Assert.Contains(page.Items, x => x.Id == own.Id);
        Assert.Contains(page.Items, x => x.Id == orphan.Id);
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task 控制器_受限_分页仅返回本人客户_排除他人与无主行()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-C-OWN", employee.Id);
        var foreign = SeedCustomer(db, "TAX442-C-OTH");
        var ownRow = SeedLedger(db, "TR-C-OWN", own.Id);
        var foreignRow = SeedLedger(db, "TR-C-OTH", foreign.Id);
        var orphan = SeedLedger(db, "TR-C-NONE", null);

        var page = await PageAsync(Controller(db, user.Id));

        Assert.Contains(page.Items, x => x.Id == ownRow.Id);
        Assert.DoesNotContain(page.Items, x => x.Id == foreignRow.Id);
        Assert.DoesNotContain(page.Items, x => x.Id == orphan.Id);
        Assert.Equal(1, page.Total);   // 计数与分页之前下推，不泄露范围外计数
    }

    [Fact]
    public async Task 控制器_受限_全部路由同口径过滤()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-A-OWN", employee.Id);
        SeedLedger(db, "TR-A-OWN", own.Id);
        SeedLedger(db, "TR-A-OTH", null);

        var result = await Controller(db, user.Id).GetAll();
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<List<BaseTaxRefund>>>(ok.Value).Data!;

        Assert.Single(data);
        Assert.Equal((long?)own.Id, data[0].CustomerId);
    }

    [Fact]
    public async Task 控制器_受限_详情越界或无主_拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db);
        var foreign = SeedCustomer(db, "TAX442-D-OTH");
        var foreignRow = SeedLedger(db, "TR-D-OTH", foreign.Id);
        var orphan = SeedLedger(db, "TR-D-NONE", null);

        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).GetById(foreignRow.Id));
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).GetById(orphan.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 控制器_特权_详情可读历史无主行()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);
        var orphan = SeedLedger(db, "TR-D-P", null);

        var result = await Controller(db, admin.Id).GetById(orphan.Id);
        Assert.IsType<OkObjectResult>(result);
    }

    // ==================== 4. 控制器：写入 ====================

    [Fact]
    public async Task 控制器_受限_新增本人客户_落库()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-W-OWN", employee.Id);

        var result = await Controller(db, user.Id).Create(Draft(own.Id));

        Assert.IsType<OkObjectResult>(result);
        var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync();
        Assert.Equal((long?)own.Id, stored.CustomerId);
        Assert.Equal(13m, stored.RefundRate);
    }

    [Fact]
    public async Task 控制器_受限_新增越界客户_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db);
        var foreign = SeedCustomer(db, "TAX442-W-OTH");

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).Create(Draft(foreign.Id)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_受限_新增无主行_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).Create(Draft(customerId: null)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_特权_新增无主行_落库()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);

        var result = await Controller(db, admin.Id).Create(Draft(customerId: null));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_特权_新增无效金额或税率_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var admin = await SeedPrivilegedUserAsync(db);

        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Create(Draft(customerId: null, exportAmount: -1m)));
        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id).Create(Draft(customerId: null, refundRate: 101m)));
        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, admin.Id)
                .Create(Draft(customerId: null, refundableAmount: 100m, refundedAmount: 200m)));

        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }


    [Fact]
    public async Task 控制器_受限_修改越界_拒绝且不改写()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-U-OWN", employee.Id);
        var foreign = SeedCustomer(db, "TAX442-U-OTH");
        var foreignRow = SeedLedger(db, "TR-U-OTH", foreign.Id, exportAmount: 500m);
        var ownRow = SeedLedger(db, "TR-U-OWN", own.Id, exportAmount: 700m);

        // 越界改写他人行：拒绝
        var edit = Draft(own.Id, exportAmount: 9999m);
        edit.Id = foreignRow.Id;
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).Update(foreignRow.Id, edit));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 把本人行改派到范围外客户：拒绝
        var reassign = Draft(foreign.Id, exportAmount: 1234m);
        reassign.Id = ownRow.Id;
        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).Update(ownRow.Id, reassign));

        var storedForeign = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == foreignRow.Id);
        Assert.Equal(500m, storedForeign.ExportAmount);
        var storedOwn = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == ownRow.Id);
        Assert.Equal(700m, storedOwn.ExportAmount);
        Assert.Equal((long?)own.Id, storedOwn.CustomerId);
    }

    [Fact]
    public async Task 控制器_受限_修改本人行_落库()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-U2-OWN", employee.Id);
        var row = SeedLedger(db, "TR-U2-OWN", own.Id, exportAmount: 700m);

        var edit = Draft(own.Id, exportAmount: 800m, refundRate: 9m);
        edit.Id = row.Id;
        var result = await Controller(db, user.Id).Update(row.Id, edit);

        Assert.IsType<OkObjectResult>(result);
        var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(800m, stored.ExportAmount);
        Assert.Equal(9m, stored.RefundRate);
    }

    [Fact]
    public async Task 控制器_受限_删除越界_拒绝且不软删()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db);
        var foreign = SeedCustomer(db, "TAX442-DEL-OTH");
        var foreignRow = SeedLedger(db, "TR-DEL-OTH", foreign.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).Delete(foreignRow.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == foreignRow.Id);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 控制器_受限_批量删除混合_整批拒绝且不部分删除()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-B-OWN", employee.Id);
        var foreign = SeedCustomer(db, "TAX442-B-OTH");
        var ownRow = SeedLedger(db, "TR-B-OWN", own.Id);
        var foreignRow = SeedLedger(db, "TR-B-OTH", foreign.Id);

        await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).BatchDelete(new List<long> { ownRow.Id, foreignRow.Id }));

        Assert.False((await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == ownRow.Id)).IsDeleted);
        Assert.False((await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == foreignRow.Id)).IsDeleted);
    }

    [Fact]
    public async Task 控制器_受限_批量删除本人行_落库()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "TAX442-B2-OWN", employee.Id);
        var ownRow = SeedLedger(db, "TR-B2-OWN", own.Id);

        var result = await Controller(db, user.Id).BatchDelete(new List<long> { ownRow.Id });

        Assert.IsType<OkObjectResult>(result);
        Assert.True((await db.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == ownRow.Id)).IsDeleted);
    }


    // ==================== 5. 控制器：身份 / 菜单 fail closed ====================

    [Fact]
    public async Task 控制器_无身份_拒绝且不返回不写入()
    {
        using var db = TestDbFactory.Create();
        SeedLedger(db, "TR-N-1", null);

        await Assert.ThrowsAsync<BusinessException>(() => Controller(db, null).GetPaged(new PageQuery()));
        await Assert.ThrowsAsync<BusinessException>(() => Controller(db, null).Create(Draft(null)));
        Assert.Equal(1, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_账号禁用_拒绝且不写入()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db, status: UserStatus.Disabled);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).Create(Draft(null)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_撤销菜单后_立即收敛()
    {
        using var db = TestDbFactory.Create();
        var (user, _, role) = await SeedOperatorAsync(db);
        _ = EnsureTaxRefundMenu(db);

        // 撤销授权前放行（受限账号无本人客户，只能读空列表）
        await PageAsync(Controller(db, user.Id));

        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == role.Id && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 控制器_账号已删除_拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db);
        user.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db, user.Id).GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 6. 接线契约（完整覆盖 + 无匿名回退 + 单一权威入口） ====================

    [Fact]
    public void 控制器_完整覆盖基类CRUD_均由自身声明()
    {
        var type = typeof(TaxRefundController);
        foreach (var name in new[] { "GetPaged", "GetAll", "GetById", "Create", "Update", "Delete", "BatchDelete" })
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.Equal(type, method!.DeclaringType);   // 必须由 TaxRefundController 自身覆盖，而非继承基类
        }

        Assert.Contains(type.GetCustomAttributes(inherit: true), a => a is AuthorizeAttribute);
        Assert.DoesNotContain(type.GetCustomAttributes(inherit: true), a => a is AllowAnonymousAttribute);
    }

    [Fact]
    public void 控制器_每条路由先授权_并把范围下推读取()
    {
        var source = ReadSource("src/ERP.Api/Controllers/TaxRefundController.cs");

        Assert.Contains("EnsureMenuAuthorizedAsync", source);
        Assert.Contains("TaxRefundLedgerRules.ScopeFilter", source);
        Assert.Contains("EnsureStoredScopeAllowed", source);
        Assert.Contains("EnsureProposedScopeAllowedAsync", source);
        Assert.Contains("TaxRefundLedgerRules.Validate", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);

        // 七条路由各自调用一次入口授权，且都先于任何服务读取 / 写入。
        var authorizations = source.Split("await EnsureAuthorizedAsync()").Length - 1;
        Assert.Equal(7, authorizations);
    }

    [Fact]
    public void 规则层_复用既有菜单与唯一权威数据范围_不新增授权模型()
    {
        var source = ReadSource("src/ERP.Application/Services/TaxRefundLedgerRules.cs");

        Assert.Contains("RequiredMenuCode = \"tax-refund\"", source);
        Assert.Contains("LoadAuthorizedMenuCodesAsync", source);
        Assert.Contains("SalespersonDataScopeService.ResolveAsync", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("SysRoleMenus.Add", source);      // 绝不新增任何授权
        Assert.DoesNotContain("IsPrivileged = true", source);
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }
                .Concat(relativePath.Split('/')).ToArray())));

}
