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
/// 费用科目（<c>api/base/expense-accounts</c>）实时授权与有界字段校验单元测试（ERP-498）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 零 / 未知 / 已删除身份（未认证）、禁用 / 缺少既有「费用科目」菜单 /
/// 被撤销菜单身份（权限不足）在<b>全部 7 条路由</b>（分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除）
/// fail closed，且 <c>BaseExpenseAccounts</c> 行逐字节不变（拒绝既不读取也不改写任何行）；</item>
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>放行</b>：具备既有「费用科目」菜单的授权身份下，既有读 / 写 / 软删除 / 批量删除契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段 / 唯一性 / 父级校验</b>：编码 / 名称空值、长度越界、类型或状态越界、编码重复、
/// 父级不存在或已删除一律按受控错误拒绝且不落任何行 / 不改写任何行；</item>
/// <item><b>源码契约</b>：控制器 7 条路由全部先授权再读写，且只复用既有 <c>expense-account</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（<see cref="TestDbFactory"/>），授权只播种在隔离测试数据里，不连接 SQL Server。
/// </summary>
public class ExpenseAccountAuthorizationTests
{
    private const string ExpensePath = "/api/base/expense-accounts";

    // ==================== 0. 测试脚手架 ====================

    /// <summary>真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值，缺失身份也必须 fail closed）。</summary>
    private static ControllerContext HttpContextFor(long? userId)
    {
        var http = new DefaultHttpContext { User = UserFor(userId) };
        http.Request.Path = ExpensePath;
        return new ControllerContext { HttpContext = http };
    }

    private static ClaimsPrincipal UserFor(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    /// <summary>按请求形状绑定：<c>empty</c> = 不设 <c>Request.Path</c>；<c>populated</c> = 真实路由；<c>no-context</c> = 不绑定。</summary>
    private static ExpenseAccountController ControllerWith(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new ExpenseAccountController(new GenericService<BaseExpenseAccount>(db), db);
        if (pathMode == "no-context") return controller;
        var http = new DefaultHttpContext { User = UserFor(userId) };
        if (pathMode == "populated") http.Request.Path = ExpensePath;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static ExpenseAccountController Controller(ErpDbContext db, long? userId)
        => ControllerWith(db, userId, "populated");

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 费用科目菜单），返回用户 Id（每个用例独立）。</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantMenu = true)
    {
        var user = new SysUser
        {
            UserName = $"expense-auth-{Guid.NewGuid():N}",
            DisplayName = "费用科目授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "费用科目授权用例角色",
            RoleCode = $"ExpenseAuthCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        LastRoleId = role.Id;
        if (grantMenu)
            GrantMenu(db, role.Id, ExpenseAccountAuthorizationRules.RequiredMenuCode,
                ExpenseAccountAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>最近一次 <see cref="SeedUser"/> 未授予菜单时创建的角色 Id（用于后续授予 / 撤销）。</summary>
    private static long LastRoleId;

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单）。</summary>
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

    /// <summary>整行软删除该角色对既有「费用科目」菜单的授权（模拟被撤销）。</summary>
    private static void RevokeMenu(ErpDbContext db, long roleId)
    {
        foreach (var link in db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted))
            link.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>播种一条费用科目行，返回其主键。</summary>
    private static long SeedAccount(ErpDbContext db, string code = "EA-EXIST", string name = "既有费用科目",
        int type = 2, int status = 1, long? parentId = null, bool deleted = false, string description = "")
    {
        var row = new BaseExpenseAccount
        {
            AccountCode = code,
            AccountName = name,
            AccountType = type,
            Status = status,
            ParentId = parentId,
            Description = description,
            IsDeleted = deleted
        };
        db.BaseExpenseAccounts.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private static BaseExpenseAccount NewAccount(string code = "EA-NEW", string name = "新增费用科目",
        int type = 2, int status = 1, string description = "", long? parentId = null)
        => new()
        {
            AccountCode = code,
            AccountName = name,
            AccountType = type,
            Status = status,
            Description = description,
            ParentId = parentId
        };

    private static PageQuery Page() => new() { Page = 1, PageSize = 20 };

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

    /// <summary>费用科目表逐行快照（拒绝调用后必须逐字节不变 —— 零写入）。</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("||",
        db.BaseExpenseAccounts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"E{x.Id}:{x.AccountCode}:{x.AccountName}:{x.AccountType}:{x.Status}:{x.ParentId}:{x.Description}:{x.IsDeleted}")
            .ToList());

    /// <summary>按请求形状逐条调用 7 条路由并断言全部返回指定错误码，且费用科目表逐字节不变（零写入）。</summary>
    private static async Task AssertAllRoutesDeniedAsync(
        ErpDbContext db, long? userId, string pathMode, int expectedCode)
    {
        var existingId = SeedAccount(db, "EA-GUARD", "守卫用例科目");
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode).GetPaged(Page()));
        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode).GetAll());
        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode).GetById(existingId));
        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode).Create(NewAccount()));
        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode).Update(existingId, NewAccount()));
        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode).Delete(existingId));
        await AssertCode(expectedCode, () => ControllerWith(db, userId, pathMode)
            .BatchDelete(new List<long> { existingId }));

        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 1. 拒绝矩阵（全部 7 条路由，路径无关） ====================

    /// <summary>缺失身份：空路径 / 已赋值路径 / 未绑定上下文三种形状一律未认证且零写入。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    [InlineData("no-context")]
    public async Task Auth_缺失身份_全部路由未认证且零写入(string pathMode)
    {
        using var db = TestDbFactory.Create();
        await AssertAllRoutesDeniedAsync(db, null, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>零身份 / 未知身份：三种请求形状一律未认证且零写入。</summary>
    [Theory]
    [InlineData("empty", 0L)]
    [InlineData("populated", 0L)]
    [InlineData("no-context", 0L)]
    [InlineData("populated", 987_654_321L)]
    public async Task Auth_零或未知身份_全部路由未认证且零写入(string pathMode, long userId)
    {
        using var db = TestDbFactory.Create();
        await AssertAllRoutesDeniedAsync(db, userId, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>已删除身份：已赋值 / 空路径均未认证且零写入；完全未绑定上下文因无法解析身份同样未认证。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    [InlineData("no-context")]
    public async Task Auth_已删除身份_全部路由未认证且零写入(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, UserStatus.Enabled, deleted: true);
        await AssertAllRoutesDeniedAsync(db, userId, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>禁用身份：已赋值路径与空路径均权限不足且零写入。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Auth_禁用身份_全部路由权限不足且零写入(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, UserStatus.Disabled, deleted: false);
        await AssertAllRoutesDeniedAsync(db, userId, pathMode, ErrorCodes.Forbidden);
    }

    /// <summary>缺少既有「费用科目」菜单：已赋值路径与空路径均权限不足且零写入。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Auth_缺少既有菜单_全部路由权限不足且零写入(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        await AssertAllRoutesDeniedAsync(db, userId, pathMode, ErrorCodes.Forbidden);
    }

    /// <summary>被撤销既有菜单授权：已赋值路径与空路径均权限不足且零写入。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Auth_撤销菜单_全部路由权限不足且零写入(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        RevokeMenu(db, LastRoleId);
        await AssertAllRoutesDeniedAsync(db, userId, pathMode, ErrorCodes.Forbidden);
    }

    /// <summary>未绑定上下文且携带身份时无法解析身份：仍按未认证 fail closed（绝不放行、绝不降级）。</summary>
    [Fact]
    public async Task Auth_未绑定上下文_无法解析身份仍未认证()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        await AssertAllRoutesDeniedAsync(db, userId, "no-context", ErrorCodes.Unauthorized);
    }

    /// <summary>拒绝先于读取：对既有未删除行按主键读取在拒绝时返回受控授权错误（而非 1002 未找到），证明护栏先于任何行读取。</summary>
    [Fact]
    public async Task Auth_拒绝先于任何读取_按主键不返回既有行()
    {
        using var db = TestDbFactory.Create();
        var rowId = SeedAccount(db, "EA-READ", "读取守卫科目");
        var disabled = SeedUser(db, UserStatus.Disabled);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => Controller(db, disabled).GetById(rowId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.DoesNotContain("EA-READ", ex.Message);
    }

    // ==================== 2. 授权身份放行 + 撤销收敛 ====================

    /// <summary>具备既有「费用科目」菜单的授权身份：7 条路由的既有读 / 写 / 软删除 / 批量删除契约保持。</summary>
    [Fact]
    public async Task Auth_授权身份_全部读写契约放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var created = Data<BaseExpenseAccount>(await ctl.Create(NewAccount("EA-1", "差旅费")));
        Assert.True(created.Id > 0);
        Assert.Equal("EA-1", created.AccountCode);

        var paged = Data<PagedResult<BaseExpenseAccount>>(await Controller(db, userId).GetPaged(Page()));
        Assert.Contains(paged.Items, x => x.Id == created.Id);
        var all = Data<List<BaseExpenseAccount>>(await Controller(db, userId).GetAll());
        Assert.Contains(all, x => x.Id == created.Id);
        var byId = Data<BaseExpenseAccount>(await Controller(db, userId).GetById(created.Id));
        Assert.Equal("差旅费", byId.AccountName);

        var updated = Data<BaseExpenseAccount>(
            await Controller(db, userId).Update(created.Id, NewAccount("EA-1", "差旅费（改）")));
        Assert.Equal("差旅费（改）", updated.AccountName);

        await Controller(db, userId).Delete(created.Id);
        Assert.True((await db.BaseExpenseAccounts.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);

        var second = Data<BaseExpenseAccount>(await Controller(db, userId).Create(NewAccount("EA-2", "办公费")));
        var third = Data<BaseExpenseAccount>(await Controller(db, userId).Create(NewAccount("EA-3", "运输费")));
        await Controller(db, userId).BatchDelete(new List<long> { second.Id, third.Id });
        Assert.All(
            await db.BaseExpenseAccounts.AsNoTracking().Where(x => x.Id == second.Id || x.Id == third.Id).ToListAsync(),
            x => Assert.True(x.IsDeleted));
    }

    /// <summary>撤销既有菜单授权后下一次请求立即收敛为权限不足，且零写入（每次请求重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var existingId = SeedAccount(db, "EA-CONV", "收敛用例科目");

        Assert.IsType<OkObjectResult>(await Controller(db, userId).GetPaged(Page()));

        RevokeMenu(db, LastRoleId);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetPaged(Page()));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).GetById(existingId));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Create(NewAccount("EA-CONV-2", "收敛新增")));
        await AssertCode(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(existingId));
        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 3. 有界字段 / 唯一性 / 父级校验 ====================

    /// <summary>字段越界 / 类型或状态非法：按 1001 拒绝，新增不落行、修改不改写既有行。</summary>
    [Theory]
    [InlineData("blank-code")]
    [InlineData("blank-name")]
    [InlineData("code-too-long")]
    [InlineData("name-too-long")]
    [InlineData("description-too-long")]
    [InlineData("bad-type-0")]
    [InlineData("bad-type-3")]
    [InlineData("bad-status-2")]
    [InlineData("bad-status--1")]
    public async Task Validation_非法载荷_拒绝且零写入(string caseKey)
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var existingId = SeedAccount(db, "EA-KEEP", "既有科目", description: "既有说明");
        var before = Snapshot(db);

        var payload = caseKey switch
        {
            "blank-code" => NewAccount(code: "   "),
            "blank-name" => NewAccount(name: "  "),
            "code-too-long" => NewAccount(code: new string('C', ExpenseAccountAuthorizationRules.MaxAccountCodeLength + 1)),
            "name-too-long" => NewAccount(name: new string('N', ExpenseAccountAuthorizationRules.MaxAccountNameLength + 1)),
            "description-too-long" => NewAccount(description: new string('D', ExpenseAccountAuthorizationRules.MaxDescriptionLength + 1)),
            "bad-type-0" => NewAccount(type: 0),
            "bad-type-3" => NewAccount(type: 3),
            "bad-status-2" => NewAccount(status: 2),
            _ => NewAccount(status: -1)
        };

        await AssertCode(ErrorCodes.InvalidParameter, () => Controller(db, userId).Create(payload));
        await AssertCode(ErrorCodes.InvalidParameter, () => Controller(db, userId).Update(existingId, payload));

        Assert.Equal(before, Snapshot(db));
        Assert.Equal("既有科目", (await db.BaseExpenseAccounts.AsNoTracking().SingleAsync(x => x.Id == existingId)).AccountName);
    }

    /// <summary>科目编码与未删除行重复：按 1003 拒绝且零写入；与已删除行同码则放行（未删除行内唯一）。</summary>
    [Fact]
    public async Task Validation_编码重复_拒绝且零写入_已删除行不占用编码()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        SeedAccount(db, "EA-DUP", "既有科目");
        var deletedId = SeedAccount(db, "EA-FREE", "已删除科目", deleted: true);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.Duplicate, () => Controller(db, userId).Create(NewAccount("EA-DUP", "重复科目")));
        Assert.Equal(before, Snapshot(db));

        // 与已删除行同码：未删除行内唯一，放行。
        var recreated = Data<BaseExpenseAccount>(
            await Controller(db, userId).Create(NewAccount("EA-FREE", "复用已删除编码")));
        Assert.Equal("EA-FREE", recreated.AccountCode);

        // 更新为与其它未删除行重复：拒绝且既有行逐字段不变。
        var target = SeedAccount(db, "EA-OTHER", "待改科目");
        var beforeUpdate = Snapshot(db);
        await AssertCode(ErrorCodes.Duplicate,
            () => Controller(db, userId).Update(target, NewAccount("EA-DUP", "改成重复")));
        Assert.Equal(beforeUpdate, Snapshot(db));

        Assert.NotNull(await db.BaseExpenseAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deletedId));
    }

    /// <summary>父级科目不存在 / 非法 / 已删除：按 1001 拒绝且零写入；指向既有未删除科目则放行。</summary>
    [Fact]
    public async Task Validation_父级科目_不存在或已删除_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var liveParent = SeedAccount(db, "EA-PARENT", "有效父级");
        var deletedParent = SeedAccount(db, "EA-DELPARENT", "已删除父级", deleted: true);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => Controller(db, userId).Create(NewAccount("EA-C1", "无效父级", parentId: 987_654_321L)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => Controller(db, userId).Create(NewAccount("EA-C2", "零父级", parentId: 0L)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => Controller(db, userId).Create(NewAccount("EA-C3", "已删除父级", parentId: deletedParent)));
        Assert.Equal(before, Snapshot(db));

        var child = Data<BaseExpenseAccount>(
            await Controller(db, userId).Create(NewAccount("EA-CHILD", "有效子科目", parentId: liveParent)));
        Assert.Equal(liveParent, child.ParentId);
    }

    /// <summary>边界值放行：编码 / 名称 / 说明恰好等于持久化长度上限，类型 1 / 2 与状态 0 / 1 都接受；超一位即拒绝。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);

        var codeAtLimit = new string('C', ExpenseAccountAuthorizationRules.MaxAccountCodeLength);
        var nameAtLimit = new string('N', ExpenseAccountAuthorizationRules.MaxAccountNameLength);
        var created = Data<BaseExpenseAccount>(await Controller(db, userId).Create(NewAccount(
            code: codeAtLimit,
            name: nameAtLimit,
            type: ExpenseAccountAuthorizationRules.IncomeAccountType,
            status: ExpenseAccountAuthorizationRules.DisabledStatus,
            description: new string('D', ExpenseAccountAuthorizationRules.MaxDescriptionLength))));
        Assert.Equal(codeAtLimit, created.AccountCode);
        Assert.Equal(nameAtLimit, created.AccountName);
        Assert.Equal(ExpenseAccountAuthorizationRules.IncomeAccountType, created.AccountType);
        Assert.Equal(ExpenseAccountAuthorizationRules.DisabledStatus, created.Status);

        var expenseTyped = Data<BaseExpenseAccount>(await Controller(db, userId)
            .Create(NewAccount("EA-TYPE-2", "支出科目", type: ExpenseAccountAuthorizationRules.ExpenseAccountType,
                status: ExpenseAccountAuthorizationRules.EnabledStatus)));
        Assert.Equal(ExpenseAccountAuthorizationRules.ExpenseAccountType, expenseTyped.AccountType);
        Assert.Equal(ExpenseAccountAuthorizationRules.EnabledStatus, expenseTyped.Status);

        var before = Snapshot(db);
        await AssertCode(ErrorCodes.InvalidParameter, () => Controller(db, userId)
            .Create(NewAccount(codeAtLimit + "C", "超长编码")));
        await AssertCode(ErrorCodes.InvalidParameter, () => Controller(db, userId)
            .Create(NewAccount("EA-LONG-NAME", nameAtLimit + "N")));
        await AssertCode(ErrorCodes.InvalidParameter, () => Controller(db, userId)
            .Create(NewAccount("EA-LONG-DESC", "超长说明",
                description: new string('D', ExpenseAccountAuthorizationRules.MaxDescriptionLength + 1))));
        Assert.Equal(before, Snapshot(db));
    }

    /// <summary>修改保留自身编码（未删除行内唯一排除自身）放行；既有软删除 / 批量删除契约不变。</summary>
    [Fact]
    public async Task Validation_修改保留自身编码放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var id = SeedAccount(db, "EA-SELF", "自身编码科目");

        var updated = Data<BaseExpenseAccount>(
            await Controller(db, userId).Update(id, NewAccount("EA-SELF", "自身编码科目（改）", type: 1, status: 0)));
        Assert.Equal("EA-SELF", updated.AccountCode);
        Assert.Equal("自身编码科目（改）", updated.AccountName);
        Assert.Equal(1, updated.AccountType);
        Assert.Equal(0, updated.Status);
    }

    // ==================== 4. 源码 / 菜单 / 文档契约 ====================

    /// <summary>控制器源码契约：7 条路由全部先经实时授权再读写，且无匿名 / 角色 / 环境 / 提供程序兜底。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureExpenseAccountAuthorizedAsync\(\);").Count);
        Assert.Contains("await ExpenseAccountAuthorizationRules.EnsureWritableAsync(_db, entity);", source);
        Assert.Contains("await ExpenseAccountAuthorizationRules.EnsureWritableAsync(_db, entity, id);", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
        Assert.DoesNotContain("RequiresLiveAuthorization", source);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source);
        Assert.DoesNotContain("IsInMemory", source);
    }

    /// <summary>授权口径复用既有「费用科目」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有费用科目菜单常量_不新增菜单()
    {
        Assert.Equal("expense-account", ExpenseAccountAuthorizationRules.RequiredMenuCode);
        Assert.Equal("费用科目", ExpenseAccountAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{ExpenseAccountAuthorizationRules.RequiredMenuCode}\", \"{ExpenseAccountAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>授权契约文案固化为可回归口径：与请求路径无关、未认证 / 权限不足、无匿名 / 管理员 / 测试专用兜底。</summary>
    [Fact]
    public void Auth_路径无关契约文案()
    {
        var text = ExpenseAccountAuthorizationRules.PathIndependenceText;
        Assert.Contains("与请求路径", text);
        Assert.Contains("未绑定任何请求上下文", text);
        Assert.Contains("未认证", text);
        Assert.Contains("权限不足", text);
        Assert.Contains("绝无匿名 / 管理员回退", text);
        Assert.Contains("不存在任何测试专用放行开关", text);
        Assert.Contains("expense-account", ExpenseAccountAuthorizationRules.AuthorizationRuleText);
    }

    /// <summary>交付文档存在并固化授权 / 校验 / 边界口径。</summary>
    [Fact]
    public void Docs_费用科目授权文档存在且口径完整()
    {
        var text = File.ReadAllText(RepoFile("docs", "expense-account-authority.md"));
        Assert.Contains("expense-account", text);
        Assert.Contains("费用科目", text);
        Assert.Contains("EnsureExpenseAccountAuthorizedAsync", text);
        Assert.Contains("AccountCode", text);
        Assert.Contains("ParentId", text);
        Assert.Contains("NEWERP_AutoAcceptance", text);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
