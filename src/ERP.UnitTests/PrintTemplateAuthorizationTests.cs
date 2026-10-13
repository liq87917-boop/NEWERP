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
/// ERP-450 / ERP-493 打印模板（<c>api/sys/print-templates</c>）实时身份 / 既有 print-design 菜单 / 持久化列有界校验单元测试。
/// <list type="number">
/// <item>清单 / 默认模板 / 保存 / 删除 / Excel 导出 / Excel 导入在读取任何模板行之前，对缺失 / 为零 / 未知 / 已删除身份与
/// 无既有功能菜单身份 fail closed；撤销菜单后立即收敛。</item>
/// <item><b>请求形状无关</b>（ERP-493）：未绑定 <c>HttpContext</c>、空 <c>Request.Path</c> 与已赋值
/// <c>Request.Path</c> 对同一身份给出<b>完全相同</b>的判定 —— 原请求形状探针与其条件护栏已彻底移除，
/// 不存在任何进程内直调豁免；</item>
/// <item>放行路径：具备既有 print-design 菜单的操作员可读可写可删可导出；导入入口在授权后按既有契约处理空文件。</item>
/// <item>保存校验：非法单据类型 / 超长名称 / 越界字号 / 非法颜色 / 非法字段顺序一律以既有受控错误拒绝，
/// 且不落任何行、不改写既有模板与默认标记（不再静默改写 FontSize）。</item>
/// <item><b>源码契约</b>：控制器全部路由先授权再读写，请求形状探针 / <c>Request.Path</c> 已彻底移除。</item>
/// </list>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不新增任何权限。</para>
/// </summary>
public class PrintTemplateAuthorizationTests
{
    /// <summary>控制器路由前缀（模拟真实 HTTP 请求管线已赋值的 <c>Request.Path</c>；ERP-493 起与判定完全无关）。</summary>
    private const string BasePath = "/api/sys/print-templates";

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 无身份_全部路由_未认证拒绝且不读写任何模板()
    {
        using var db = TestDbFactory.Create();
        var seeded = SeedTemplate(db, "sales-order", "既有模板");
        var before = Snapshot(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetList(null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Save(ValidTemplate()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(seeded.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.ExportExcel(seeded.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.ImportExcel(null));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 已删除账号_按未认证拒绝_禁用账号_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var seeded = SeedTemplate(db, "sales-order", "既有模板");
        var deleted = SeedOperator(db, grantMenu: true, status: UserStatus.Enabled, deleted: true);
        var disabled = SeedOperator(db, grantMenu: true, status: UserStatus.Disabled, deleted: false);
        var before = Snapshot(db);

        var deletedCtl = NewController(db, deleted);
        await AssertCode(ErrorCodes.Unauthorized, () => deletedCtl.GetList(null));
        await AssertCode(ErrorCodes.Unauthorized, () => deletedCtl.Save(ValidTemplate()));
        await AssertCode(ErrorCodes.Unauthorized, () => deletedCtl.Delete(seeded.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => deletedCtl.ExportExcel(seeded.Id));

        var disabledCtl = NewController(db, disabled);
        await AssertCode(ErrorCodes.Forbidden, () => disabledCtl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Forbidden, () => disabledCtl.Save(ValidTemplate()));
        await AssertCode(ErrorCodes.Forbidden, () => disabledCtl.Delete(seeded.Id));
        await AssertCode(ErrorCodes.Forbidden, () => disabledCtl.ImportExcel(null));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 为零与未知身份_全部路由_未认证拒绝且不读写任何模板()
    {
        using var db = TestDbFactory.Create();
        var seeded = SeedTemplate(db, "sales-order", "既有模板");
        var before = Snapshot(db);

        foreach (var userId in new long?[] { 0, 9_999_999 })
        {
            var ctl = NewController(db, userId);

            await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetList(null));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetDefault("sales-order"));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.Save(ValidTemplate()));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(seeded.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.ExportExcel(seeded.Id));
            await AssertCode(ErrorCodes.Unauthorized, () => ctl.ImportExcel(null));
        }

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 无print_design菜单_全部路由_权限不足且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var seeded = SeedTemplate(db, "sales-order", "既有模板");
        var noMenu = SeedOperator(db, grantMenu: false);
        var before = Snapshot(db);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetList(null));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Save(ValidTemplate()));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(seeded.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.ExportExcel(seeded.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.ImportExcel(null));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 撤销print_design菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var seeded = SeedTemplate(db, "sales-order", "既有模板");
        var operatorId = SeedOperator(db, grantMenu: true);
        var ctl = NewController(db, operatorId);

        Assert.IsType<OkObjectResult>(await ctl.GetList(null));
        Assert.IsType<FileContentResult>(await ctl.ExportExcel(seeded.Id));

        RevokeMenus(db, operatorId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetList(null));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.ExportExcel(seeded.Id));
    }

    /// <summary>
    /// ERP-493：完全未绑定 <c>HttpContext</c> 的纯进程内直调不再存在任何授权豁免 —— 无身份一律受控未认证，
    /// 且拒绝先于任何模板行读取 / 写入（<c>SysPrintTemplates</c> 快照逐字节不变）。
    /// </summary>
    [Fact]
    public async Task 无HttpContext直调_缺失身份一律未认证且零写入()
    {
        using var db = TestDbFactory.Create();
        var seeded = SeedTemplate(db, "sales-order", "既有模板");
        var before = Snapshot(db);
        var ctl = ControllerWithoutHttp(db);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetList(null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Save(ValidTemplate()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(seeded.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.ExportExcel(seeded.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.ImportExcel(null));

        AssertUnchanged(db, before);
    }

    /// <summary>
    /// ERP-493：同一身份在「空请求路径」与「已赋值请求路径」下对全部 6 条路由的判定完全一致
    /// （请求形状探针已彻底移除）；无身份在任一形状（含未绑定 <c>HttpContext</c>）下读清单一律未认证。
    /// </summary>
    [Fact]
    public async Task 空路径与已赋值路径_同身份判定完全一致()
    {
        using var db = TestDbFactory.Create();
        var operatorId = SeedOperator(db, grantMenu: true);

        foreach (var path in new[] { "", BasePath, "/api/sys/print-templates/1/export-excel" })
        {
            var ctl = NewController(db, operatorId, path);

            // 保存：放行并落库
            var saved = GetData<SysPrintTemplate>(await ctl.Save(ValidTemplate()));
            Assert.True(saved.Id > 0);

            // 清单 / 默认模板：既有非删除模板可见
            var list = GetData<List<SysPrintTemplate>>(await ctl.GetList("sales-order"));
            Assert.Contains(list, t => t.Id == saved.Id);
            Assert.Equal(saved.Id, GetData<SysPrintTemplate>(await ctl.GetDefault("sales-order")).Id);

            // 导出：既有契约返回下载构件
            Assert.IsType<FileContentResult>(await ctl.ExportExcel(saved.Id));

            // 删除：既有软删除语义
            await ctl.Delete(saved.Id);
            Assert.DoesNotContain(GetData<List<SysPrintTemplate>>(await ctl.GetList("sales-order")),
                t => t.Id == saved.Id);
        }

        foreach (var path in new[] { "", BasePath })
        {
            var anonymous = NewController(db, userId: null, path);
            await AssertCode(ErrorCodes.Unauthorized, () => anonymous.GetList(null));
            await AssertCode(ErrorCodes.Unauthorized, () => anonymous.Save(ValidTemplate()));
        }
        await AssertCode(ErrorCodes.Unauthorized, () => ControllerWithoutHttp(db).GetList(null));
    }

    /// <summary>
    /// 源码契约（ERP-493）：控制器 6 条路由全部先授权再读写，请求形状探针（<c>RequiresLiveAuthorization</c>）
    /// 与 <c>Request.Path</c> / 环境变量 / 数据库提供程序 / 测试专用开关的放行分支已彻底移除，且无匿名 / 角色回退。
    /// </summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权且无请求形状探针()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "PrintTemplateController.cs"));

        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureAuthorizedAsync\(\);").Count);
        Assert.Contains("PrintTemplateAuthorizationRules.EnsureAuthorizedAsync", source);
        Assert.Contains("PrintTemplateAuthorizationRules.NormalizeForWrite", source);
        Assert.Contains("PrintTemplateAuthorizationRules.ValidateForSave", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);

        // ERP-493：请求形状探针（RequiresLiveAuthorization / Request.Path）与其条件护栏彻底移除，
        // 也不存在环境变量 / 测试专用开关的放行分支。
        Assert.DoesNotContain("RequiresLiveAuthorization", source);
        Assert.DoesNotContain("Request.Path", source);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source);
        Assert.Contains("Request.Path", PrintTemplateAuthorizationRules.PathIndependenceText);
    }

    // ==================== 2. 放行路径（既有 print-design 菜单） ====================

    [Fact]
    public async Task 已授权操作员_可读可写可删可导出_导入入口按既有契约()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));

        var saved = GetData<SysPrintTemplate>(await ctl.Save(ValidTemplate()));
        Assert.True(saved.Id > 0);

        var list = GetData<List<SysPrintTemplate>>(await ctl.GetList("sales-order"));
        Assert.Contains(list, t => t.Id == saved.Id);

        var byType = GetData<SysPrintTemplate>(await ctl.GetDefault("sales-order"));
        Assert.Equal(saved.Id, byType.Id);

        Assert.IsType<FileContentResult>(await ctl.ExportExcel(saved.Id));

        // 导入入口在授权通过后按既有契约处理空文件（授权失败会抛 BusinessException）
        var ok = Assert.IsType<OkObjectResult>(await ctl.ImportExcel(null));
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.InvalidParameter, resp.Code);

        await ctl.Delete(saved.Id);
        list = GetData<List<SysPrintTemplate>>(await ctl.GetList("sales-order"));
        Assert.DoesNotContain(list, t => t.Id == saved.Id);
    }

    // ==================== 3. 保存持久化列有界校验（拒绝且不落库） ====================

    [Fact]
    public async Task 非法单据类型_保存被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Save(new SysPrintTemplate
        {
            BillType = "not-a-printable-bill", TemplateName = "模板X"
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("可打印单据类型", ex.Message);

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 空单据类型与空模板名称_按既有受控错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);

        var noType = AssertOkFail(await ctl.Save(new SysPrintTemplate { BillType = "", TemplateName = "模板X" }));
        Assert.Equal(ErrorCodes.InvalidParameter, noType.Code);

        var noName = AssertOkFail(await ctl.Save(new SysPrintTemplate { BillType = "sales-order", TemplateName = "  " }));
        Assert.Equal(ErrorCodes.InvalidParameter, noName.Code);

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 模板名称超长_保存被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);
        var model = ValidTemplate();
        model.TemplateName = new string('T', PrintTemplateAuthorizationRules.MaxTemplateNameLength + 1);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Save(model));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 正文字号越界_保存被拒绝_不静默改写既有模板与默认标记()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var existing = GetData<SysPrintTemplate>(await ctl.Save(new SysPrintTemplate
        {
            BillType = "sales-order", TemplateName = "既有默认模板", FontSize = 12, IsDefault = true
        }));
        var before = Snapshot(db);

        var model = ValidTemplate();
        model.Id = existing.Id;
        model.TemplateName = "既有默认模板";
        model.FontSize = 100;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Save(model));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("FontSize", ex.Message);

        AssertUnchanged(db, before);
        var stored = db.SysPrintTemplates.AsNoTracking().Single(t => t.Id == existing.Id);
        Assert.Equal(12, stored.FontSize);      // 不再被静默改写为 12 / 其它值
        Assert.True(stored.IsDefault);          // 默认标记不变
    }

    [Fact]
    public async Task 标题字号与公司字号越界_保存被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);

        var title = ValidTemplate();
        title.TitleFontSize = PrintTemplateAuthorizationRules.MaxTitleFontSize + 1;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(title));

        var company = ValidTemplate();
        company.CompanyFontSize = PrintTemplateAuthorizationRules.MinCompanyFontSize - 1;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(company));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 颜色格式非法_保存被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);

        var named = ValidTemplate();
        named.TitleColor = "red";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(named));

        var malformed = ValidTemplate();
        malformed.HeaderBgColor = "#GGGGGG";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(malformed));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task FieldKeys非法_保存被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);

        var notArray = ValidTemplate();
        notArray.FieldKeys = "{\"BillNo\":1}";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(notArray));

        var notStrings = ValidTemplate();
        notStrings.FieldKeys = "[1,2,3]";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(notStrings));

        var tooLong = ValidTemplate();
        tooLong.FieldKeys = "[\"" + new string('k', PrintTemplateAuthorizationRules.MaxFieldKeysLength) + "\"]";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(tooLong));

        AssertUnchanged(db, before);
    }

    [Fact]
    public async Task 纸张规格与超长文本列_保存被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db, SeedOperator(db, grantMenu: true));
        var before = Snapshot(db);

        var paper = ValidTemplate();
        paper.PaperSize = "B5";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(paper));

        var title = ValidTemplate();
        title.Title = new string('x', PrintTemplateAuthorizationRules.MaxTitleLength + 1);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(title));

        var address = ValidTemplate();
        address.CompanyAddress = new string('x', PrintTemplateAuthorizationRules.MaxCompanyAddressLength + 1);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(address));

        var footer = ValidTemplate();
        footer.FooterText = new string('x', PrintTemplateAuthorizationRules.MaxFooterTextLength + 1);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(footer));

        AssertUnchanged(db, before);
    }

    // ==================== 脚手架 ====================

    private static PrintTemplateController NewController(ErpDbContext db, long? userId, string? requestPath = BasePath)
        => new(db) { ControllerContext = Context(userId, requestPath) };

    /// <summary>
    /// 完全未绑定 <c>HttpContext</c> 的纯进程内直调控制器（外部请求无法构造）。
    /// <para>ERP-493：不存在任何授权豁免 —— 无 <c>HttpContext</c> 即无身份，一律受控未认证。</para>
    /// </summary>
    private static PrintTemplateController ControllerWithoutHttp(ErpDbContext db)
        => new(db) { ControllerContext = new ControllerContext() };

    /// <summary>
    /// 注入请求身份（可空 = 无 <c>NameIdentifier</c>）：<paramref name="requestPath"/> 为 null 或空串时
    /// 不赋值（等同于空路径），非空时为已赋值路径。
    /// <para>ERP-493：请求形状探针已彻底移除，三种形状对同一身份给出<b>完全相同</b>的判定。</para>
    /// </summary>
    private static ControllerContext Context(long? userId, string? requestPath)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (!string.IsNullOrEmpty(requestPath))
            http.Request.Path = requestPath;
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    /// <summary>构造一份合法的保存载荷（各字段均在既有持久化边界与受支持范围内）。</summary>
    private static SysPrintTemplate ValidTemplate() => new()
    {
        BillType = "sales-order",
        TemplateName = $"模板-{Guid.NewGuid():N}",
        Title = "销售订单",
        CompanyName = "测试公司",
        CompanyAddress = "测试地址",
        CompanyPhone = "0000-0000000",
        PaperSize = "A4",
        FontSize = 12,
        FieldKeys = "[\"BillNo\",\"OrderDate\"]",
        FooterText = "签字栏",
        FontFamily = "Microsoft YaHei",
        TitleFontSize = 16,
        TitleColor = "#1e3a8a",
        TitleAlign = "center",
        CompanyFontSize = 18,
        CompanyColor = "#000000",
        TextColor = "#000000",
        HeaderBgColor = "#f2f2f2",
        BorderColor = "#999999",
        BorderStyle = "solid",
        RowHeight = 34,
        CellPadding = 6,
    };

    private static SysPrintTemplate SeedTemplate(ErpDbContext db, string billType, string templateName)
    {
        var template = new SysPrintTemplate
        {
            BillType = billType,
            TemplateName = templateName,
            Title = "既有标题",
            FontSize = 12,
            IsDefault = true,
            CreatedAt = DateTime.Now
        };
        db.SysPrintTemplates.Add(template);
        db.SaveChanges();
        return template;
    }

    /// <summary>启用 / 禁用账号 + 角色；可选授予既有「样式设计」菜单（复用/新建菜单，不新增权限模型）。</summary>
    private static long SeedOperator(ErpDbContext db, bool grantMenu,
        UserStatus status = UserStatus.Enabled, bool deleted = false)
    {
        var user = new SysUser
        {
            UserName = $"print-design-op-{Guid.NewGuid():N}",
            DisplayName = "打印设计操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "打印设计操作员", RoleCode = $"PrintDesign-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantMenu) GrantMenu(db, role.Id, PrintTemplateAuthorizationRules.RequiredMenuCode);
        return user.Id;
    }

    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = menuCode,
                MenuName = PrintTemplateAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu,
                Path = "/print-design"
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>读取控制器返回的统一响应数据。</summary>
    private static T GetData<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static ApiResponse<object> AssertOkFail(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>全部打印模板行的可比较快照（含软删除行，用于断言「拒绝路径零读写 / 零改写」）。</summary>
    private static List<string> Snapshot(ErpDbContext db)
        => db.SysPrintTemplates.AsNoTracking().OrderBy(t => t.Id)
            .ToList()
            .Select(t => $"{t.Id}|{t.BillType}|{t.TemplateName}|{t.Title}|{t.FontSize}|{t.IsDefault}|{t.IsDeleted}")
            .ToList();

    private static void AssertUnchanged(ErpDbContext db, List<string> before)
        => Assert.Equal(before, Snapshot(db));
}
