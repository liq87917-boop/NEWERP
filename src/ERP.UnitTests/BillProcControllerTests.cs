using System.Text.Json;
using ERP.Api.Controllers;
using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-469 通用单据控制器（<c>api/v2/bills</c>）默认值与导出门禁单元测试（内存库 + 真实既有授权规则）。
/// <list type="bullet">
/// <item><b>默认值 fail closed</b>：<c>{billType}/defaults</c> 先复用既有读侧门禁（实时身份 + 该族既有功能菜单 +
/// 业务员客户数据范围）；系统参数的 <c>DefaultCurrency</c> / <c>ExchangeRate</c> 缺失 / 非法时返回既有受控失败信封，
/// <b>绝不回退美元 / 1，绝不臆造汇率</b>，只有两者都合法才返回既有取值；全部零写入。</item>
/// <item><b>导出门禁</b>：<c>{billType}/export</c> 与新增的 <c>{billType}/export-preview</c> 共用同一授权；
/// 无身份 / 无既有功能菜单一律 fail closed 且不打开数据连接（未授权即不查询）。</item>
/// <item><b>源码契约</b>：日期列只接受已登记导出列白名单，导出与预览共用同一筛选构造。</item>
/// </list>
/// <para>真实 SQL Server 连接由既有 SQL 测试覆盖；本测试不连接任何数据库服务器。</para>
/// </summary>
public class BillProcControllerTests
{
    private const string UnusedConnectionString =
        "Server=(localdb)\\NEWERP_UnitTests_Gate;Database=UNUSED;Integrated Security=true;Connect Timeout=1";

    private static StoredProcedureService StoredProcedures()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = UnusedConnectionString
            })
            .Build();
        return new StoredProcedureService(configuration);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static BillProcController NewController(ErpDbContext db, long? userId)
    {
        var controller = new BillProcController(StoredProcedures(), db,
            new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static JsonElement Payload(ApiResponse<object> response)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Data));
        return document.RootElement.Clone();
    }

    private static SysParameter Parameter(string key, string value, bool deleted = false) => new()
    {
        ParamKey = key,
        ParamName = key,
        ParamValue = value,
        Description = string.Empty,
        IsDeleted = deleted,
        CreatedAt = DateTime.Now,
    };

    private static List<string> Snapshot(ErpDbContext db)
        => db.SysParameters.AsNoTracking().OrderBy(p => p.Id).ToList()
            .Select(p => $"{p.Id}|{p.ParamKey}|{p.ParamValue}|{p.IsDeleted}")
            .ToList();

    /// <summary>播种受限制操作员（非系统内置角色 → 非特权），未授予任何既有功能菜单。</summary>
    private static long SeedRestrictedOperator(ErpDbContext db)
    {
        var role = new SysRole { RoleName = "询价单操作员", RoleCode = $"INQ-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"bill-proc-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "询价单操作员",
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>在独立内存库中播种参数行并读取默认值（每次都断言参数表零改写）。</summary>
    private static async Task<ApiResponse<object>> ReadDefaultsAsync(params SysParameter[] parameters)
    {
        await using var db = TestDbFactory.Create();
        var user = TestAuth.SeedPrivilegedUser(db);
        foreach (var parameter in parameters) db.SysParameters.Add(parameter);
        db.SaveChanges();

        var before = Snapshot(db);
        var response = Envelope(await NewController(db, user).GetDefaults("inquiry"));
        Assert.Equal(before, Snapshot(db));      // 只读：绝不新增 / 改写任何参数行
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
        return response;
    }

    // ==================== 1. 默认值授权（fail closed） ====================

    [Fact]
    public async Task 默认值_无身份_未认证且不返回任何默认值()
    {
        await using var db = TestDbFactory.Create();
        db.SysParameters.Add(Parameter("DefaultCurrency", "CNY"));
        db.SysParameters.Add(Parameter("ExchangeRate", "7.2"));
        db.SaveChanges();

        var anonymous = Envelope(await NewController(db, null).GetDefaults("inquiry"));
        Assert.Equal(ErrorCodes.Unauthorized, anonymous.Code);
        Assert.Null(anonymous.Data);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 默认值_无既有询价单菜单_权限不足_授予菜单后放行_撤销后立即收敛()
    {
        await using var db = TestDbFactory.Create();
        db.SysParameters.Add(Parameter("DefaultCurrency", "CNY"));
        db.SysParameters.Add(Parameter("ExchangeRate", "7.2"));
        db.SaveChanges();

        var user = SeedRestrictedOperator(db);

        var denied = Envelope(await NewController(db, user).GetDefaults("inquiry"));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);
        Assert.Null(denied.Data);

        GrantMenu(db, user, "inquiry");
        var allowed = Envelope(await NewController(db, user).GetDefaults("inquiry"));
        Assert.Equal(ErrorCodes.Success, allowed.Code);

        RevokeMenus(db, user);
        var revoked = Envelope(await NewController(db, user).GetDefaults("inquiry"));
        Assert.Equal(ErrorCodes.Forbidden, revoked.Code);
        Assert.Null(revoked.Data);

        // 未知单据类型在读取任何参数行之前拒绝。
        var unknown = Envelope(await NewController(db, user).GetDefaults("no-such-bill"));
        Assert.Equal(ErrorCodes.InvalidParameter, unknown.Code);

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    // ==================== 2. 默认值取值（fail closed，绝不臆造汇率） ====================

    [Fact]
    public async Task 默认值_参数完全缺失_受控拒绝且不臆造汇率()
    {
        var missing = await ReadDefaultsAsync();
        Assert.Equal(ErrorCodes.InvalidParameter, missing.Code);
        Assert.Null(missing.Data);
        Assert.Contains("DefaultCurrency", missing.Message);
    }

    [Fact]
    public async Task 默认值_缺其一或取值非法_受控拒绝且不臆造汇率()
    {
        // 只有默认币种：绝不臆造一个汇率。
        var onlyCurrency = await ReadDefaultsAsync(Parameter("DefaultCurrency", "CNY"));
        Assert.Equal(ErrorCodes.InvalidParameter, onlyCurrency.Code);
        Assert.Null(onlyCurrency.Data);

        // 只有汇率：绝不回退美元。
        var onlyRate = await ReadDefaultsAsync(Parameter("ExchangeRate", "7.2"));
        Assert.Equal(ErrorCodes.InvalidParameter, onlyRate.Code);
        Assert.Null(onlyRate.Data);

        // 币种不受支持。
        var badCurrency = await ReadDefaultsAsync(Parameter("DefaultCurrency", "XYZ"), Parameter("ExchangeRate", "7.2"));
        Assert.Equal(ErrorCodes.InvalidParameter, badCurrency.Code);
        Assert.Null(badCurrency.Data);

        // 汇率非正 / 不可解析。
        foreach (var value in new[] { "0", "-1.5", "abc" })
        {
            var badRate = await ReadDefaultsAsync(
                Parameter("DefaultCurrency", "USD"), Parameter("ExchangeRate", value));
            Assert.Equal(ErrorCodes.InvalidParameter, badRate.Code);
            Assert.Null(badRate.Data);
        }

        // 已删除参数行不参与读取（仍按缺失 fail closed）。
        var deleted = await ReadDefaultsAsync(
            Parameter("DefaultCurrency", "CNY", deleted: true),
            Parameter("ExchangeRate", "7.2", deleted: true));
        Assert.Equal(ErrorCodes.InvalidParameter, deleted.Code);
        Assert.Null(deleted.Data);
    }

    [Fact]
    public async Task 默认值_参数合法_只返回既有取值()
    {
        var response = await ReadDefaultsAsync(
            Parameter("DefaultCurrency", "cny"), Parameter("ExchangeRate", " 7.20 "));
        Assert.Equal(ErrorCodes.Success, response.Code);

        var payload = Payload(response);
        Assert.Equal((int)Currency.CNY, payload.GetProperty("currency").GetInt32());
        Assert.Equal(7.20m, payload.GetProperty("exchangeRate").GetDecimal());
    }

    // ==================== 3. 导出 / 导出前预览授权（fail closed） ====================

    [Fact]
    public async Task 导出与导出前预览_无身份或无既有菜单_一律failclosed且不打开数据连接()
    {
        await using var db = TestDbFactory.Create();
        var user = SeedRestrictedOperator(db);

        // 无身份：既有读侧门禁按未认证拒绝，且绝不打开任何数据连接（连接串不可用也不会被触发）。
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, null).Export("inquiry", null, null, "InquiryDate", null, null)).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, null).ExportPreview("inquiry", null, null, "InquiryDate", null, null)).Code);

        // 有身份但缺少既有「询价单」功能菜单：权限不足，且导出类菜单绝不顶替功能菜单。
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, user).Export("inquiry", "INQ-1", 1, "InquiryDate", null, null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, user).ExportPreview("inquiry", "INQ-1", 1, "InquiryDate", null, null)).Code);

        // 未知单据类型：在授权之前以既有受控错误拒绝。
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await NewController(db, user).Export("no-such-bill", null, null, null, null, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await NewController(db, user).ExportPreview("no-such-bill", null, null, null, null, null)).Code);

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(db.SysOperationLogs);
        Assert.Empty(db.SysDingTalkLogs);
    }

    // ==================== 4. 源码契约 ====================

    [Fact]
    public void 源码契约_导出与预览共用门禁与筛选白名单()
    {
        var export = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BillProcController.Export.cs"));
        Assert.Contains("[HttpGet(\"{billType}/export-preview\")]", export);
        Assert.Contains("GuardLegacyExportAsync(billType)", export);
        Assert.Contains("LegacyBillAuthorizationRules.EnsureReadAuthorizedAsync(_db, CurrentUserId(), billType)", export);
        Assert.Contains("BuildExportFilter(billType, keyword, status, dateField, start, end)", export);
        Assert.Contains("ExportColumns.TryGetValue(billType, out var registered)", export);
        Assert.DoesNotContain("AND {dateField}", export);

        var controller = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BillProcController.cs"));
        Assert.Contains("SystemParameterAuthorizationRules.ResolveBillCurrencyDefaults", controller);
        Assert.DoesNotContain("var exchangeRate = 1m;", controller);
    }

    // ==================== 5. 测试辅助 ====================

    private static string RepoFile(params string[] parts)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", Path.Combine(parts)));

    private static void GrantMenu(ErpDbContext db, long userId, string menuCode)
    {
        var roleId = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).First();
        var menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }
}
