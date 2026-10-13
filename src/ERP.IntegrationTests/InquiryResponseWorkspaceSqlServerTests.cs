using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// ERP-500 询价报价响应时间工作台（<c>api/inquiries/response-times</c>）实时身份 / 账号状态 / 既有「询价单」
/// （<c>inquiry</c>）菜单授权护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：分页入口在读取 / 计数任何 <c>Inquiries</c> 行之前解析实时启用身份与
/// 既有 <c>inquiry</c> 功能菜单，缺失 / 非法 / 已删除按未认证，禁用 / 缺菜单按权限不足，一律 fail closed。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 已删除 / 禁用 / 无菜单身份在并列路由上 fail closed，拒绝既不读取也不改写任何行。</item>
/// <item><b>放行路径</b>：种子管理员（既有全部菜单）与显式授予既有 <c>inquiry</c> 菜单并映射业务员的操作员均可持续读取，
/// 既有日期区间 / 客户 / 关键字筛选、稳定 Id 分页与 <c>ApiResponse&lt;InquiryResponseWorkspaceDto&gt;</c> 契约保持不变。</item>
/// <item><b>收敛与形状无关</b>：请求之间撤销既有菜单立即收敛为拒绝；空路径 / 已赋值路径 / 未绑定 <c>HttpContext</c> 的口径一致。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class InquiryResponseWorkspaceSqlServerTests
    : IClassFixture<InquiryResponseWorkspaceSqlServerFixture>
{
    private readonly InquiryResponseWorkspaceSqlServerFixture _fixture;

    public InquiryResponseWorkspaceSqlServerTests(InquiryResponseWorkspaceSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{InquiryResponseWorkspaceSqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(InquiryResponseWorkspaceSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_without_reading_or_changing_any_inquiry()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);

        // 无身份（含完全未绑定 HttpContext 的纯进程内直调）一律未认证。
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, userId: null).GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        await AssertCode(ErrorCodes.Unauthorized,
            () => new InquiryResponseWorkspaceController(db).GetPaged(new InquiryResponseQuery { PageSize = 100 }));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);
        var disabled = await SeedOperatorAsync(db, UserStatus.Disabled, deleted: false);
        var deleted = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: true);

        var forbidden = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, disabled).GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Code);

        var unauthenticated = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, deleted).GetPaged(new InquiryResponseQuery { PageSize = 100 }));
        Assert.Equal(ErrorCodes.Unauthorized, unauthenticated.Code);

        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Live_missing_inquiry_menu_is_forbidden_without_reading_any_inquiry()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);
        // 真实启用 + 已映射业务员，但未授予既有「询价单」菜单 → fail closed（绝不回退为放行）。
        var noMenu = await SeedGrantedOperatorAsync(db, grantInquiryMenu: false);

        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(db, noMenu).GetPaged(new InquiryResponseQuery { PageSize = 100 }));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedGrantedOperatorAsync(db, grantInquiryMenu: true);

        Assert.IsType<OkObjectResult>(
            await NewController(db, operatorId).GetPaged(new InquiryResponseQuery { PageSize = 1 }));

        await RevokeMenusAsync(db, operatorId);

        await AssertCode(ErrorCodes.Forbidden,
            () => NewController(db, operatorId).GetPaged(new InquiryResponseQuery { PageSize = 100 }));
    }

    // ==================== 2. 放行路径（种子管理员 / 已授予既有菜单的操作员） ====================

    [Fact]
    public async Task Live_seeded_admin_with_inquiry_menu_can_read_response_times()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var tag = Tag();
        var customer = await SeedCustomerAsync(db, "C-ERP500-A-" + tag, null);
        var inquiry = await SeedInquiryAsync(db, "INQ-ERP500-A-" + tag, customer, DateTime.Today);
        await SeedRootQuotationAsync(db, "QT-ERP500-A-" + tag, inquiry, DateTime.Today.AddDays(2));

        var page = AssertOk(await NewController(db, adminId).GetPaged(new InquiryResponseQuery
        {
            PageSize = 100,
            Keyword = "INQ-ERP500-A-" + tag
        }));

        var row = Assert.Single(page.Items);
        Assert.Equal(inquiry, row.InquiryId);
        Assert.Equal(customer, row.CustomerId);
        Assert.True(row.Quoted);
        Assert.Equal(2, row.ElapsedDays);
        Assert.False(string.IsNullOrWhiteSpace(page.ReadOnlyText));
        Assert.False(string.IsNullOrWhiteSpace(page.BoundaryText));
        Assert.False(string.IsNullOrWhiteSpace(page.DisclaimerText));
    }

    [Fact]
    public async Task Live_menu_granted_operator_keeps_existing_read_contract()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedGrantedOperatorAsync(db, grantInquiryMenu: true);
        var employeeId = await EmployeeIdOfAsync(db, operatorId);
        var tag = Tag();
        var customer = await SeedCustomerAsync(db, "C-ERP500-O-" + tag, employeeId);
        var first = await SeedInquiryAsync(db, "INQ-ERP500-O1-" + tag, customer, DateTime.Today);
        var second = await SeedInquiryAsync(db, "INQ-ERP500-O2-" + tag, customer, DateTime.Today.AddDays(-8));
        await SeedRootQuotationAsync(db, "QT-ERP500-O1-" + tag, first, DateTime.Today.AddDays(3));
        // 未显式链接（InquiryId 为空）的报价单：绝不按单号 / 文本推断链接。
        await SeedRootQuotationAsync(db, "QT-ERP500-NOLINK-" + tag, null, DateTime.Today.AddDays(1));

        // 关键字（只命中询价单号）+ 稳定询价单 Id 升序 + 有界分页。
        var page1 = AssertOk(await NewController(db, operatorId).GetPaged(new InquiryResponseQuery
        {
            Page = 1, PageSize = 1, Keyword = tag
        }));
        Assert.Equal(2, page1.Total);
        var firstRow = Assert.Single(page1.Items);
        Assert.Equal(first, firstRow.InquiryId);
        Assert.True(firstRow.Quoted);
        Assert.Equal(3, firstRow.ElapsedDays);
        Assert.Equal("QT-ERP500-O1-" + tag, firstRow.QuotationNo);

        var page2 = AssertOk(await NewController(db, operatorId).GetPaged(new InquiryResponseQuery
        {
            Page = 2, PageSize = 1, Keyword = tag
        }));
        var secondRow = Assert.Single(page2.Items);
        Assert.Equal(second, secondRow.InquiryId);
        Assert.False(secondRow.Quoted);
        Assert.Equal(InquiryResponseRules.StateUnquoted, secondRow.ResponseState);

        // 日期区间（含）：仅今天的那张。
        var todayOnly = AssertOk(await NewController(db, operatorId).GetPaged(new InquiryResponseQuery
        {
            PageSize = 100, Keyword = tag, StartDate = DateTime.Today, EndDate = DateTime.Today
        }));
        Assert.Equal(1, todayOnly.Total);
        Assert.Equal(first, Assert.Single(todayOnly.Items).InquiryId);

        // 可选客户筛选（同一客户两张）。
        var byCustomer = AssertOk(await NewController(db, operatorId).GetPaged(new InquiryResponseQuery
        {
            PageSize = 100, CustomerId = customer
        }));
        Assert.Equal(2, byCustomer.Total);

        // 单次上限 200：越界请求被收敛为 200。
        var bounded = AssertOk(await NewController(db, operatorId).GetPaged(new InquiryResponseQuery
        {
            PageSize = 5000, Keyword = tag
        }));
        Assert.Equal(200, bounded.PageSize);
        Assert.Equal(2, bounded.Total);
    }

    [Fact]
    public async Task Live_request_path_and_http_context_do_not_change_the_decision()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorId = await SeedGrantedOperatorAsync(db, grantInquiryMenu: true);

        // 已授权身份：空路径 / 已赋值路径口径一致。
        Assert.IsType<OkObjectResult>(await NewController(db, operatorId, string.Empty)
            .GetPaged(new InquiryResponseQuery { PageSize = 1 }));
        Assert.IsType<OkObjectResult>(await NewController(db, operatorId, "/api/inquiries/response-times")
            .GetPaged(new InquiryResponseQuery { PageSize = 1 }));

        // 未绑定 HttpContext 的纯进程内直调：无身份 → 未认证（绝无豁免）。
        await AssertCode(ErrorCodes.Unauthorized,
            () => new InquiryResponseWorkspaceController(db).GetPaged(new InquiryResponseQuery { PageSize = 1 }));

        // 无身份：空路径 / 已赋值路径一律未认证。
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, userId: null, string.Empty).GetPaged(new InquiryResponseQuery { PageSize = 1 }));
        await AssertCode(ErrorCodes.Unauthorized,
            () => NewController(db, userId: null, "/api/inquiries/response-times")
                .GetPaged(new InquiryResponseQuery { PageSize = 1 }));
    }

    // ==================== 3. 脚手架 ====================

    /// <summary>构造工作台控制器并注入指定登录身份（可空 = 无身份）；<c>Request.Path</c> 由参数显式控制。</summary>
    private static InquiryResponseWorkspaceController NewController(
        IErpDbContext db, long? userId, string requestPath = "/api/inquiries/response-times")
    {
        var controller = new InquiryResponseWorkspaceController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = requestPath;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>播种既有合法客户（可归属指定业务员），返回客户 Id。</summary>
    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string code, long? employeeId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = "ERP500 客户 " + code,
            Status = 1,
            CreditStatus = "正常",
            EmpId = employeeId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>播种既有询价单（未删除），返回询价单 Id。</summary>
    private static async Task<long> SeedInquiryAsync(ErpDbContext db, string no, long customerId, DateTime inquiryDate)
    {
        var inquiry = new Inquiry
        {
            InquiryNo = no,
            CustomerId = customerId,
            InquiryDate = inquiryDate,
            Status = DocumentStatus.Pending
        };
        db.Inquiries.Add(inquiry);
        await db.SaveChangesAsync();
        return inquiry.Id;
    }

    /// <summary>播种版本链根单报价单（显式 <c>InquiryId</c> 链接，<c>RootQuotationId == null</c>）。</summary>
    private static async Task SeedRootQuotationAsync(ErpDbContext db, string no, long? inquiryId, DateTime quotationDate)
    {
        db.Quotations.Add(new Quotation
        {
            QuotationNo = no,
            InquiryId = inquiryId,
            QuotationDate = quotationDate,
            RootQuotationId = null,
            RevisionNumber = 1,
            Status = DocumentStatus.Pending
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种可指定状态 / 删除 / 系统角色的操作员（非特权），返回用户 Id。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, UserStatus status, bool deleted, bool system = false)
    {
        var user = new SysUser
        {
            UserName = $"inq-resp-op-{Guid.NewGuid():N}",
            DisplayName = "询价报价响应时间操作员",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "询价报价响应时间操作员",
            RoleCode = $"InquiryResponseOp-{Guid.NewGuid():N}",
            IsSystem = system
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>非特权操作员 + 映射业务员（+ 可选既有「询价单」菜单授权），返回用户 Id。</summary>
    private static async Task<long> SeedGrantedOperatorAsync(ErpDbContext db, bool grantInquiryMenu)
    {
        var userId = await SeedOperatorAsync(db, UserStatus.Enabled, deleted: false);
        var userName = await db.SysUsers.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.UserName).SingleAsync();

        // 受限制账号必须映射为业务员（既有员工主数据），否则 fail closed（未映射业务员）。
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = "询价响应时间业务员",
            IsSalesman = true,
            Status = 1
        });
        await db.SaveChangesAsync();

        if (grantInquiryMenu)
        {
            var roleId = await db.SysUserRoles.AsNoTracking()
                .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).FirstAsync();
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == InquiryAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id).FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
            await db.SaveChangesAsync();
        }
        return userId;
    }

    /// <summary>账号映射的业务员员工 Id（用于把客户归入该账号的业务员数据范围）。</summary>
    private static async Task<long> EmployeeIdOfAsync(ErpDbContext db, long userId)
    {
        var userName = await db.SysUsers.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.UserName).SingleAsync();
        return await db.BaseEmployees.AsNoTracking()
            .Where(e => e.EmployeeCode == userName && !e.IsDeleted)
            .Select(e => e.Id).SingleAsync();
    }

    /// <summary>撤销账号当前角色下的全部菜单授权（模拟授权撤销，验证下一次请求立即收敛）。</summary>
    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToListAsync();
        foreach (var grant in await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>询价单行数（用于断言拒绝路径零写入 / 零新增）。</summary>
    private static Task<int> SnapshotAsync(ErpDbContext db)
        => db.Inquiries.AsNoTracking().CountAsync();

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static InquiryResponseWorkspaceDto AssertOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<InquiryResponseWorkspaceDto>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-500）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供询价报价响应时间工作台授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class InquiryResponseWorkspaceSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_INQRESP_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-500] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-500] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 inquiry 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class InquiryResponseWorkspaceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => InquiryResponseWorkspaceSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => InquiryResponseWorkspaceSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
