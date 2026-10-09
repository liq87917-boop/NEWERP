using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-445 单据号规则（<c>api/sys/document-number-rules</c>）实时身份 / 既有 doc-rule 菜单 / 编号形状有界校验护栏单元测试。
/// <para>覆盖：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除在读取或写入任何规则行之前，对缺失 / 已删除 /
/// 禁用身份与无既有「单据号规则」菜单身份 fail closed（无管理员兜底）；撤销菜单后立即收敛；已授予既有菜单的账号
/// 可读可写；新增 / 修改对未知单据类型、空 / 超长 / 重复规则编码、超长前缀 / 分隔符 / 日期格式、不受支持的日期形状、
/// 越界流水位与负当前流水返回既有受控错误，且被拒绝时不落任何行、不消耗任何单据号。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何授权。</para>
/// </summary>
public class DocumentNumberRuleAuthorizationTests
{
    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 全部路由_无身份_一律未认证且不读取或改写任何规则行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-ANON");
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewRule("PT-ANON")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(row.Id, NewRule("SO-ANON")));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.BatchDelete(new List<long> { row.Id }));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-ID");
        var before = Snapshot(db);
        var disabled = SeedUser(db, UserStatus.Disabled, deleted: false, grantMenu: true);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true, grantMenu: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Create(NewRule("PT-D")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetAll());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 无既有单据号规则菜单_全部路由拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-NOMENU");
        var before = Snapshot(db);
        var noMenu = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewRule("PT-NEW")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(row.Id, NewRule("SO-NOMENU")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { row.Id }));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 撤销既有菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedRule(db, "SO-REVOKE");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        Assert.IsType<OkObjectResult>(await ctl.GetPaged(new PageQuery()));

        RevokeMenus(db, user);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetAll());
    }

    [Fact]
    public async Task 特权账号_缺少既有菜单_仍按权限不足拒绝_无管理员兜底()
    {
        using var db = TestDbFactory.Create();
        SeedRule(db, "SO-PRIV-NOMENU");
        // 系统内置角色（特权）但不授予 doc-rule 菜单：不得因特权而绕过既有功能菜单。
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: false, systemRole: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetPaged(new PageQuery()));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, privileged).GetAll());

        // 显式授予既有 doc-rule 菜单后立即放行，证明拒绝只因缺少既有功能菜单。
        GrantMenu(db, PrimaryRoleId(db, privileged), DocumentNumberRuleAuthorizationRules.RequiredMenuCode,
            DocumentNumberRuleAuthorizationRules.RequiredMenuText);
        Assert.IsType<OkObjectResult>(await NewController(db, privileged).GetPaged(new PageQuery()));
    }

    // ==================== 2. 放行路径（已授予既有菜单的账号） ====================

    [Fact]
    public async Task 已授予既有菜单的账号_可读可写且保留既有响应契约()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-OK");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var paged = AssertOk<PagedResult<SysDocumentNumberRule>>(await ctl.GetPaged(new PageQuery()));
        Assert.Contains(paged.Items, r => r.Id == row.Id);
        Assert.Contains(AssertOk<List<SysDocumentNumberRule>>(await ctl.GetAll()), r => r.Id == row.Id);
        Assert.Equal(row.Id, AssertOk<SysDocumentNumberRule>(await ctl.GetById(row.Id)).Id);

        var created = AssertOk<SysDocumentNumberRule>(await ctl.Create(NewRule("PT-OK")));
        Assert.True(created.Id > 0);
        Assert.True(db.SysDocumentNumberRules.Any(r => r.Id == created.Id));

        var updated = AssertOk<SysDocumentNumberRule>(
            await ctl.Update(created.Id, NewRule("PT-OK", serialLength: 6)));
        Assert.Equal(6, updated.SerialLength);

        Assert.IsType<OkObjectResult>(await ctl.Delete(created.Id));
        Assert.True(db.SysDocumentNumberRules.Single(r => r.Id == created.Id).IsDeleted);
    }

    [Fact]
    public async Task 特权账号_具备既有菜单_保留既有读_写访问()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-PRIV");
        var privileged = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true, systemRole: true);
        var ctl = NewController(db, privileged);

        Assert.Contains(AssertOk<List<SysDocumentNumberRule>>(await ctl.GetAll()), r => r.Id == row.Id);
        var created = AssertOk<SysDocumentNumberRule>(await ctl.Create(NewRule("PT-PRIV")));
        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { created.Id }));
        Assert.True(db.SysDocumentNumberRules.Single(r => r.Id == created.Id).IsDeleted);
    }

    // ==================== 3. 编号形状有界校验（拒绝且不落库 / 不改写） ====================

    [Fact]
    public async Task 新增_非法编号形状_拒绝且不落任何规则行()
    {
        using var db = TestDbFactory.Create();
        SeedRule(db, "SO-KEEP");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-T", documentType: (DocumentType)999)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule(string.Empty)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule(new string('C', 51))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-P", prefix: new string('P', 21))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-S", separator: new string('-', 6))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-F", dateFormat: "q")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-F", dateFormat: "abc")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-F", dateFormat: new string('y', 21))));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-N", serialLength: 0)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-N", serialLength: -1)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-N", serialLength: 11)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewRule("PT-Q", currentSequence: -1)));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 新增_重复规则编码_按既有唯一语义拒绝且原行不变()
    {
        using var db = TestDbFactory.Create();
        SeedRule(db, "SO-DUP");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewRule("SO-DUP")));
        await AssertCode(ErrorCodes.Duplicate, () => ctl.Create(NewRule("  SO-DUP  ")));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_非法字段_拒绝且不改写原行()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-EDIT");
        var other = SeedRule(db, "PT-OTHER");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var before = Snapshot(db);
        var ctl = NewController(db, user);

        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRule("SO-EDIT", documentType: (DocumentType)999)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRule(string.Empty)));
        await AssertCode(ErrorCodes.Duplicate,
            () => ctl.Update(row.Id, NewRule(other.RuleCode)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRule("SO-EDIT", prefix: new string('P', 21))));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRule("SO-EDIT", dateFormat: "q")));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRule("SO-EDIT", serialLength: 0)));
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Update(row.Id, NewRule("SO-EDIT", currentSequence: -5)));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 修改_合法字段_写入成功且可用空白日期格式回退默认()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-UPD");
        var user = SeedUser(db, UserStatus.Enabled, deleted: false, grantMenu: true);
        var ctl = NewController(db, user);

        var updated = AssertOk<SysDocumentNumberRule>(await ctl.Update(row.Id,
            NewRule("SO-UPD", prefix: "SO2", dateFormat: "  ", serialLength: 5, separator: "-")));

        Assert.Equal("SO2", updated.Prefix);
        Assert.Null(updated.DateFormat);          // 空白日期格式归一为 null，回到既有默认兜底
        Assert.Equal(5, updated.SerialLength);
        Assert.Equal("-", updated.Separator);

        var stored = db.SysDocumentNumberRules.Single(r => r.Id == row.Id);
        Assert.Equal("SO2", stored.Prefix);
        Assert.Null(stored.DateFormat);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public void 日期格式形状判定_复用既有种子且拒绝未知记号与纯时间()
    {
        Assert.True(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("yyyyMMdd"));
        Assert.True(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("yyyy-MM-dd"));
        Assert.True(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("yyMM"));
        Assert.True(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("yyyyMMddHHmmss"));

        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat(null));
        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat(string.Empty));
        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("   "));
        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("q"));
        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("abc"));
        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat("HH:mm:ss"));
        Assert.False(DocumentNumberRuleAuthorizationRules.IsSupportedDateFormat(new string('y', 21)));
    }

    [Fact]
    public async Task 拒绝的写入_不消耗单据号且默认兜底与既有流水语义不变()
    {
        using var db = TestDbFactory.Create();
        var row = SeedRule(db, "SO-SEQ", currentSequence: 5);
        var before = Snapshot(db);

        // 无身份被拒：规则行（含 CurrentSequence）逐字节不变。
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).Create(NewRule("PT-NOSEQ")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).Update(row.Id, NewRule("SO-SEQ")));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).Delete(row.Id));
        AssertUnchanged(db, before);

        // 既有规则仍按既有流水语义生成：CurrentSequence + 1。
        var service = new DocumentNumberService(db);
        var no = await service.GenerateAsync(DocumentType.SalesOrder, new DateTime(2026, 8, 19));
        Assert.Equal("PT202608190006", no);

        // 未配置规则的单据类型仍走既有默认兜底（prefix + yyyyMMdd + 4 位流水）。
        var fallback = await service.GenerateAsync(DocumentType.SalesReturn, new DateTime(2026, 8, 19));
        Assert.StartsWith("XTH20260819", fallback);
    }

    // ==================== 4. 源码与菜单契约 ====================

    [Fact]
    public void 控制器源码契约_全部路由先授权_复用既有单据号规则菜单()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "SysSimpleControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Contains("DocumentNumberRuleAuthorizationRules.EnsureAuthorizedAsync", source);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureAuthorizedAsync\(\);").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("Authorize(Roles", source);

        Assert.Equal("doc-rule", DocumentNumberRuleAuthorizationRules.RequiredMenuCode);
        Assert.Equal("单据号规则", DocumentNumberRuleAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"system\", \"{DocumentNumberRuleAuthorizationRules.RequiredMenuCode}\", "
            + $"\"{DocumentNumberRuleAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    // ==================== 5. 测试辅助 ====================

    /// <summary>构造单据号规则控制器（内存库 + 通用 CRUD 服务）并注入指定登录身份（可空 = 无身份）。</summary>
    private static DocumentNumberRuleController NewController(ErpDbContext db, long? userId)
    {
        var controller = new DocumentNumberRuleController(new GenericService<SysDocumentNumberRule>(db), db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>待写入的单据号规则（默认合法编号形状）。</summary>
    private static SysDocumentNumberRule NewRule(
        string ruleCode,
        DocumentType documentType = DocumentType.SalesOrder,
        string ruleName = "测试单据号规则",
        string prefix = "PT",
        string? dateFormat = "yyyyMMdd",
        int serialLength = 4,
        string separator = "",
        long currentSequence = 0)
        => new()
        {
            DocumentType = documentType,
            RuleCode = ruleCode,
            RuleName = ruleName,
            Prefix = prefix,
            DateFormat = dateFormat,
            SerialLength = serialLength,
            Separator = separator,
            CurrentSequence = currentSequence
        };

    /// <summary>播种一条既有单据号规则（用于读取 / 唯一性 / 流水语义场景）。</summary>
    private static SysDocumentNumberRule SeedRule(ErpDbContext db, string ruleCode, long currentSequence = 0)
    {
        var rule = NewRule(ruleCode, currentSequence: currentSequence);
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    /// <summary>播种账号（可选启用 / 删除 / 系统内置角色 / 既有 doc-rule 菜单授权），返回用户 Id。</summary>
    private static long SeedUser(
        ErpDbContext db, UserStatus status, bool deleted, bool grantMenu, bool systemRole = false)
    {
        var user = new SysUser
        {
            UserName = $"doc-rule-{Guid.NewGuid():N}",
            DisplayName = "单据号规则授权账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "单据号规则操作员",
            RoleCode = $"DocRuleRole-{Guid.NewGuid():N}",
            IsSystem = systemRole
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu)
            GrantMenu(db, role.Id, DocumentNumberRuleAuthorizationRules.RequiredMenuCode,
                DocumentNumberRuleAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单）。</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = menuCode,
                MenuName = menuName,
                MenuType = MenuType.Menu,
                Path = $"/system/{menuCode}"
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>账号当前主角色 Id（仅用于「授予既有菜单后放行」等场景）。</summary>
    private static long PrimaryRoleId(ErpDbContext db, long userId)
        => db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).First();

    /// <summary>撤销账号当前角色下的全部菜单授权（模拟授权撤销，验证下一次请求立即收敛）。</summary>
    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>单据号规则行快照（含流水号，用于断言拒绝路径不新增 / 不改写 / 不消耗任何单据号）。</summary>
    private static List<string> Snapshot(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking().OrderBy(r => r.Id).ToList()
            .Select(r => $"{r.Id}|{r.DocumentType}|{r.RuleCode}|{r.RuleName}|{r.Prefix}|{r.DateFormat}"
                + $"|{r.SerialLength}|{r.Separator}|{r.CurrentSequence}|{r.IsDeleted}")
            .ToList();

    private static void AssertUnchanged(ErpDbContext db, List<string> before)
        => Assert.Equal(before, Snapshot(db));

    /// <summary>断言抛出指定业务错误码。</summary>
    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>断言成功响应并取出数据（业务码必须为 0）。</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
