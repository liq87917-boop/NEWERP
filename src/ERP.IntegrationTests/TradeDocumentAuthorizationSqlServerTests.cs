using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-394 单证中心实时授权、权威客户范围与批量删除原子性的真实 SQL Server 集成测试（GUID 独占 NEWERP_AUTOTEST 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器</b>：以既有「单证中心」（<c>doc-center</c>）菜单授权、既有业务员数据范围与真实
/// <see cref="TradeDocumentController"/> / <see cref="SalesOrderController"/> 验证本人 / 他人 / 无主 / 撤销菜单 /
/// 禁用账号场景；自定义路由（详情 / 打印 / 导出 / 明细行）与伪造新增 / 修改一律 fail closed，被拒绝的请求不改写任何行。</item>
/// <item><b>两个独立连接竞态</b>：① 两个独立连接并发伪造新增越界客户单证 → 二者都 fail closed 且零落库；
/// ② 同一单证上「改写到越界客户」与「合法删除」并发 → 改写照旧拒绝、删除成功，归属与状态一致无撕裂。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class TradeDocumentAuthorizationSqlServerTests
    : IClassFixture<TradeDocumentAuthorizationSqlServerFixture>
{
    private readonly TradeDocumentAuthorizationSqlServerFixture _fixture;

    public TradeDocumentAuthorizationSqlServerTests(TradeDocumentAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(TradeDocumentAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    /// <summary>绑定真实 HttpContext 与（可空）已解析范围的单证中心控制器。</summary>
    private static TradeDocumentController NewController(ErpDbContext db, SalespersonDataScope? scope, long? userId = null)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        if (scope is not null)
            http.Items[TradeDocumentRequestAuthorizationFilter.ScopeItemKey] = scope;

        var controller = new TradeDocumentController(new GenericService<TradeDocument>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static SalespersonDataScope Restricted(long salesmanId, params long[] allowedCustomerIds) => new()
    {
        IsPrivileged = false, SalesmanId = salesmanId, AllowedCustomerIds = allowedCustomerIds.ToHashSet()
    };

    private static SalespersonDataScope Privileged() =>
        new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接 / 事务），返回成功标志与错误。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        SalespersonDataScope? scope, Func<TradeDocumentController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, scope));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐），返回两侧结果。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first, Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(
        ErpDbContext db, string code, long? empId = null, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = status, CreditStatus = "正常",
            Currency = "USD", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>播种受限业务员账号：登录名 = 员工编码（ERP-097 权威映射），并按需授予既有 doc-center 菜单。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool grantMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E394_EMP_{Tag()}";
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
            RoleName = $"INT_E394_ROLE_{code}", RoleCode = $"INT_E394_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == "doc-center");
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    private static async Task<TradeDocument> SeedDocumentAsync(
        ErpDbContext db, string docNo, long? customerId, string status = "待制作")
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = "商业发票", Status = status, Currency = "USD",
            IssueDate = DateTime.Today, CustomerId = customerId,
            CustomerName = customerId is > 0 ? $"集成客户{customerId}" : string.Empty
        };
        db.TradeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task<SalesOrder> SeedOrderAsync(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 100m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    // ==================== 1. 真实身份 / 菜单授权（规则层） ====================

    [Fact]
    public async Task 真实身份_本人可访问_撤销菜单与禁用账号立即收敛()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, role) = await SeedOperatorAsync(seed);
            userId = user.Id;
            roleId = role.Id;
            customerId = (await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var scope = await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId);
            Assert.False(scope.IsPrivileged);
            Assert.True(scope.AllowsCustomer(customerId));
        }

        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, roleId);

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
    }

    [Fact]
    public async Task 真实身份_禁用账号按权限不足拒绝()
    {
        Guard();
        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _, _) = await SeedOperatorAsync(seed, status: UserStatus.Disabled);
            userId = user.Id;
        }

        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 真实控制器：CRUD / 自定义路由 / 批量删除 ====================

    [Fact]
    public async Task 列表详情打印导出明细_受限范围_越界与无主fail_closed()
    {
        Guard();
        var tag = Tag();
        SalespersonDataScope scope;
        long ownId, foreignId, unlinkedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employee, _) = await SeedOperatorAsync(seed);
            var own = await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E394_F_{tag}");
            ownId = (await SeedDocumentAsync(seed, $"INT_E394_DOC_OWN_{tag}", own.Id)).Id;
            foreignId = (await SeedDocumentAsync(seed, $"INT_E394_DOC_FOREIGN_{tag}", foreign.Id)).Id;
            unlinkedId = (await SeedDocumentAsync(seed, $"INT_E394_DOC_UNLINKED_{tag}", null)).Id;
            scope = Restricted(employee.Id, own.Id);
        }

        Assert.True((await TryAsync(scope, c => c.GetPaged(new PageQuery { PageSize = 100000 }))).Success);
        Assert.True((await TryAsync(scope, c => c.GetById(ownId))).Success);
        Assert.False((await TryAsync(scope, c => c.GetById(foreignId))).Success);
        Assert.False((await TryAsync(scope, c => c.GetPrint(foreignId))).Success);
        Assert.False((await TryAsync(scope, c => c.GetItems(foreignId))).Success);
        Assert.False((await TryAsync(scope, c => c.ExportExcel(foreignId, null, null, null, null, null))).Success);
        // 无主（CustomerId 缺失）单证对受限账号同样 fail closed
        Assert.False((await TryAsync(scope, c => c.GetById(unlinkedId))).Success);
        Assert.False((await TryAsync(scope, c => c.GetPrint(unlinkedId))).Success);
    }

    [Fact]
    public async Task 伪造新增与修改_越界客户拒绝且不改写()
    {
        Guard();
        var tag = Tag();
        SalespersonDataScope scope;
        long foreignCustomerId, ownId, ownCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employee, _) = await SeedOperatorAsync(seed);
            var own = await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E394_F_{tag}");
            foreignCustomerId = foreign.Id;
            ownCustomerId = own.Id;
            ownId = (await SeedDocumentAsync(seed, $"INT_E394_DOC_{tag}", own.Id)).Id;
            scope = Restricted(employee.Id, own.Id);
        }

        var forged = new TradeDocument
        {
            DocNo = $"INT_E394_FORGED_{tag}", DocType = "商业发票", CustomerId = foreignCustomerId
        };
        Assert.False((await TryAsync(scope, c => c.Create(forged))).Success);

        var edit = new TradeDocument
        {
            DocNo = $"INT_E394_DOC_{tag}", DocType = "商业发票", CustomerId = foreignCustomerId
        };
        Assert.False((await TryAsync(scope, c => c.Update(ownId, edit))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.TradeDocuments.AnyAsync(d => d.DocNo == $"INT_E394_FORGED_{tag}"));
            var stored = await verify.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == ownId);
            Assert.False(stored.IsDeleted);
            Assert.Equal(ownCustomerId, stored.CustomerId);
        }
    }

    [Fact]
    public async Task 批量删除_混合允许与越界_整体拒绝且无部分删除()
    {
        Guard();
        var tag = Tag();
        SalespersonDataScope scope;
        long ownId, foreignId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employee, _) = await SeedOperatorAsync(seed);
            var own = await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E394_F_{tag}");
            ownId = (await SeedDocumentAsync(seed, $"INT_E394_BATCH_OWN_{tag}", own.Id)).Id;
            foreignId = (await SeedDocumentAsync(seed, $"INT_E394_BATCH_FOREIGN_{tag}", foreign.Id)).Id;
            scope = Restricted(employee.Id, own.Id);
        }

        Assert.False((await TryAsync(scope, c => c.BatchDelete(new List<long> { ownId, foreignId }))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.TradeDocuments.AnyAsync(d => d.Id == ownId && d.IsDeleted));
            Assert.False(await verify.TradeDocuments.AnyAsync(d => d.Id == foreignId && d.IsDeleted));
        }
    }

    // ==================== 3. 来源生成（目标授权不可绕过） ====================

    /// <summary>用一条独立连接以真实范围执行销售订单生成入口。</summary>
    private async Task<(bool Success, string Error)> TryOrderGenerateAsync(SalespersonDataScope scope, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        var http = new DefaultHttpContext();
        http.Items[TradeDocumentRequestAuthorizationFilter.ScopeItemKey] = scope;
        var controller = new SalesOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        try
        {
            var result = await controller.GenerateTradeDocuments(orderId, null);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [Fact]
    public async Task 销售订单生成_越界拒绝_本人成功且目标归属权威客户()
    {
        Guard();
        var tag = Tag();
        SalespersonDataScope scope;
        long ownOrderId, foreignOrderId, ownCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employee, _) = await SeedOperatorAsync(seed);
            var own = await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E394_F_{tag}");
            ownCustomerId = own.Id;
            ownOrderId = (await SeedOrderAsync(seed, $"INT_E394_SO_OWN_{tag}", own.Id)).Id;
            foreignOrderId = (await SeedOrderAsync(seed, $"INT_E394_SO_FOREIGN_{tag}", foreign.Id)).Id;
            scope = Restricted(employee.Id, own.Id);
        }

        Assert.False((await TryOrderGenerateAsync(scope, foreignOrderId)).Success);
        Assert.True((await TryOrderGenerateAsync(scope, ownOrderId)).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.TradeDocuments.AnyAsync(d => d.SalesOrderNo == $"INT_E394_SO_FOREIGN_{tag}"));
            var generated = await verify.TradeDocuments.AsNoTracking()
                .Where(d => d.SalesOrderNo == $"INT_E394_SO_OWN_{tag}").ToListAsync();
            Assert.NotEmpty(generated);
            Assert.All(generated, d => Assert.Equal(ownCustomerId, d.CustomerId));
        }
    }

    // ==================== 4. 两个独立连接竞态 ====================

    [Fact]
    public async Task 竞态一_两连接并发伪造新增越界客户_二者都拒绝且零落库()
    {
        Guard();
        var tag = Tag();
        SalespersonDataScope scope;
        long foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employee, _) = await SeedOperatorAsync(seed);
            var own = await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E394_F_{tag}");
            foreignCustomerId = foreign.Id;
            scope = Restricted(employee.Id, own.Id);
        }

        var results = await RaceAsync(
            () => TryAsync(scope, c => c.Create(new TradeDocument
            {
                DocNo = $"INT_E394_RACE1_A_{tag}", DocType = "商业发票", CustomerId = foreignCustomerId
            })),
            () => TryAsync(scope, c => c.Create(new TradeDocument
            {
                DocNo = $"INT_E394_RACE1_B_{tag}", DocType = "商业发票", CustomerId = foreignCustomerId
            })));

        Assert.All(results, r => Assert.False(r.Success));
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.TradeDocuments.AnyAsync(
                d => d.DocNo == $"INT_E394_RACE1_A_{tag}" || d.DocNo == $"INT_E394_RACE1_B_{tag}"));
        }
    }

    [Fact]
    public async Task 竞态二_改写到越界客户与合法删除并发_改写拒绝删除生效且无撕裂()
    {
        Guard();
        var tag = Tag();
        SalespersonDataScope scope;
        long ownId, ownCustomerId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (_, employee, _) = await SeedOperatorAsync(seed);
            var own = await SeedCustomerAsync(seed, $"INT_E394_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E394_F_{tag}");
            ownCustomerId = own.Id;
            foreignCustomerId = foreign.Id;
            ownId = (await SeedDocumentAsync(seed, $"INT_E394_RACE2_{tag}", own.Id)).Id;
            scope = Restricted(employee.Id, own.Id);
        }

        var edit = new TradeDocument
        {
            DocNo = $"INT_E394_RACE2_{tag}", DocType = "商业发票", CustomerId = foreignCustomerId
        };
        var results = await RaceAsync(
            () => TryAsync(scope, c => c.Update(ownId, edit)),
            () => TryAsync(scope, c => c.Delete(ownId)));

        Assert.False(results[0].Success);   // 改写越界客户始终被拒绝
        Assert.True(results[1].Success);    // 合法删除在范围内单证上生效

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == ownId);
            Assert.True(stored.IsDeleted);                    // 删除生效且未回滚
            Assert.Equal(ownCustomerId, stored.CustomerId);   // 归属从未被改写为越界客户（无撕裂）
        }
    }
}

/// <summary>
/// ERP-394 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class TradeDocumentAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_TRADEDOCAUTHORIZATION_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-394] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
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

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-394] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class TradeDocumentAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => TradeDocumentAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
