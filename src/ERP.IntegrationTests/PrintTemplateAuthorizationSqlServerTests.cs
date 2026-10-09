using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-450 打印模板（<c>api/sys/print-templates</c>）实时身份 / 既有 print-design 菜单 / 持久化列有界校验的
/// 真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <para>覆盖：清单 / 默认模板 / 保存 / 删除 / Excel 导出 / Excel 导入在读取 / 写入任何模板行之前解析实时身份与
/// 既有 <c>print-design</c> 功能菜单（缺失 / 已删除按未认证，禁用 / 无菜单按权限不足，撤销后立即收敛），
/// 种子管理员与显式授予既有菜单的操作员可读写 / 导出，非法取值按既有受控错误拒绝且零写入、无下载构件。</para>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PrintTemplateAuthorizationSqlServerTests : IClassFixture<PrintTemplateAuthorizationSqlServerFixture>
{
    private readonly PrintTemplateAuthorizationSqlServerFixture _fixture;

    /// <summary>控制器路由前缀（模拟真实 HTTP 请求管线的 <c>Request.Path</c>，缺失身份的真实请求一律 fail closed）。</summary>
    private const string BasePath = "/api/sys/print-templates";

    public PrintTemplateAuthorizationSqlServerTests(PrintTemplateAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PrintTemplateAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_every_route_without_mutating_any_template()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-ANON"));
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetList(null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Save(ValidTemplate("sales-order", UniqueName("TPL-ANON"))));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.ExportExcel(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.ImportExcel(TextFile()));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-ID"));
        var disabled = await SeedOperatorAsync(db, UserStatus.Disabled, deleted: false);
        var deleted = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: true);
        var before = await SnapshotAsync(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetList(null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(db, disabled).Save(ValidTemplate("sales-order", UniqueName("TPL-D"))));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).ExportExcel(row.Id));
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, deleted).Save(ValidTemplate("sales-order", UniqueName("TPL-X"))));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(row.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).ImportExcel(TextFile()));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_identity_without_print_design_menu_is_forbidden_on_every_route()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-NOMENU"));
        var noMenu = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, noMenu);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetList(null));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Forbidden,
            () => ctl.Save(ValidTemplate("sales-order", UniqueName("TPL-NOMENU"))));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.ExportExcel(row.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.ImportExcel(TextFile()));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var row = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-REVOKE"));
        var operatorId = await SeedGrantedOperatorAsync(db);
        var ctl = NewController(db, operatorId);

        Assert.IsType<OkObjectResult>(await ctl.GetList(null));
        Assert.IsType<FileContentResult>(await ctl.ExportExcel(row.Id));

        RevokeMenus(db, operatorId);
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetList(null));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetDefault("sales-order"));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.ExportExcel(row.Id));
    }

    // ==================== 2. 放行路径（种子管理员 / 已授予既有菜单） ====================

    [Fact]
    public async Task Live_seeded_admin_with_print_design_menu_can_read_write_delete_and_export()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var seeded = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-ADMIN"));
        var ctl = NewController(db, adminId);

        var list = AssertOk<List<SysPrintTemplate>>(await ctl.GetList("sales-order"));
        Assert.Contains(list, t => t.Id == seeded.Id);

        var name = UniqueName("TPL-ADMIN-NEW");
        var saved = AssertOk<SysPrintTemplate>(await ctl.Save(ValidTemplate("sales-order", name)));
        Assert.True(saved.Id > 0);

        Assert.IsType<FileContentResult>(await ctl.ExportExcel(saved.Id));

        var ok = Assert.IsType<OkObjectResult>(await ctl.ImportExcel(null));
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.IsType<ApiResponse<object>>(ok.Value).Code);

        AssertOkResult(await ctl.Delete(saved.Id));
        Assert.False(await db.SysPrintTemplates.AsNoTracking().AnyAsync(t => t.Id == saved.Id && !t.IsDeleted));
    }

    [Fact]
    public async Task Live_menu_granted_operator_can_read_and_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var seeded = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-OP"));
        var operatorId = await SeedGrantedOperatorAsync(db);
        var ctl = NewController(db, operatorId);

        Assert.Contains(AssertOk<List<SysPrintTemplate>>(await ctl.GetList("sales-order")),
            t => t.Id == seeded.Id);
        Assert.Equal(seeded.Id, AssertOk<SysPrintTemplate>(await ctl.GetDefault("sales-order")).Id);
        Assert.IsType<FileContentResult>(await ctl.ExportExcel(seeded.Id));

        var name = UniqueName("TPL-OP-NEW");
        AssertOkResult(await ctl.Save(ValidTemplate("sales-order", name)));
        Assert.True(await db.SysPrintTemplates.AsNoTracking().AnyAsync(t => t.TemplateName == name && !t.IsDeleted));
    }

    // ==================== 3. 持久化列有界校验（拒绝且不落库 / 无构件） ====================

    [Fact]
    public async Task Live_invalid_payloads_are_rejected_without_mutating_any_template()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var existing = await SeedTemplateAsync(db, "sales-order", UniqueName("TPL-KEEP"));
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, adminId);

        // 非法单据类型 / 超长名称。
        await AssertCode(ErrorCodes.InvalidParameter,
            () => ctl.Save(ValidTemplate("not-a-printable-bill", UniqueName("TPL-BAD"))));
        var longName = ValidTemplate("sales-order", UniqueName("TPL-LONG"));
        longName.TemplateName = new string('T', PrintTemplateAuthorizationRules.MaxTemplateNameLength + 1);
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(longName));

        // 越界字号（不再静默改写）。
        var fontSize = ValidTemplate("sales-order", UniqueName("TPL-FS"));
        fontSize.FontSize = PrintTemplateAuthorizationRules.MaxBodyFontSize + 1;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(fontSize));

        // 非法颜色 / 非法字段顺序 / 非法纸张。
        var color = ValidTemplate("sales-order", UniqueName("TPL-COLOR"));
        color.TitleColor = "red";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(color));

        var fields = ValidTemplate("sales-order", UniqueName("TPL-FIELDS"));
        fields.FieldKeys = "[1,2,3]";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(fields));

        var paper = ValidTemplate("sales-order", UniqueName("TPL-PAPER"));
        paper.PaperSize = "B5";
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(paper));

        // 修改既有模板时同样拒绝且不改写原行 / 默认标记。
        var overwrite = ValidTemplate("sales-order", existing.TemplateName);
        overwrite.Id = existing.Id;
        overwrite.FontSize = 100;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Save(overwrite));

        await AssertUnchangedAsync(db, before);
        var stored = await db.SysPrintTemplates.AsNoTracking().SingleAsync(t => t.Id == existing.Id);
        Assert.Equal(12, stored.FontSize);
        Assert.True(stored.IsDefault);
    }

    // ==================== 4. 脚手架 ====================

    private static PrintTemplateController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new PrintTemplateController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = BasePath;   // 模拟真实 HTTP 请求管线：缺失身份的真实请求一律 fail closed
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctl;
    }

    /// <summary>生成不超过既有持久化上界的唯一模板名（测试内不会互相占用唯一索引）。</summary>
    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..48];

    private static SysPrintTemplate ValidTemplate(string billType, string templateName) => new()
    {
        BillType = billType,
        TemplateName = templateName,
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

    /// <summary>非 .xlsx 的导入文件（授权通过后按既有契约被拒，用于证明授权先于文件处理）。</summary>
    private static IFormFile TextFile()
    {
        var bytes = new byte[] { 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "template.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };
    }

    private static async Task<SysPrintTemplate> SeedTemplateAsync(ErpDbContext db, string billType, string templateName)
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
        await db.SaveChangesAsync();
        return template;
    }

    private static async Task<long> SeedOperatorAsync(ErpDbContext db, UserStatus status, bool deleted)
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
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>非特权操作员 + 既有「样式设计」菜单授权（复用 SeedData / SchemaUpgrader 已建菜单，不新增任何菜单）。</summary>
    private static async Task<long> SeedGrantedOperatorAsync(ErpDbContext db)
    {
        var userId = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var role = new SysRole
        {
            RoleName = "打印设计操作员",
            RoleCode = $"PrintDesignOp-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == PrintTemplateAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();
        return userId;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
    }

    private static async Task<List<string>> SnapshotAsync(ErpDbContext db)
    {
        var rows = await db.SysPrintTemplates.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        return rows
            .Select(t => $"{t.Id}|{t.BillType}|{t.TemplateName}|{t.Title}|{t.FontSize}|{t.IsDefault}|{t.IsDeleted}")
            .ToList();
    }

    private static async Task AssertUnchangedAsync(ErpDbContext db, List<string> before)
        => Assert.Equal(before, await SnapshotAsync(db));

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static void AssertOkResult(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-450）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供打印模板授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class PrintTemplateAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PRINTTEMPLATE_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-450] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁既有夹具或他人的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-450] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PrintTemplateAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PrintTemplateAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => PrintTemplateAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

