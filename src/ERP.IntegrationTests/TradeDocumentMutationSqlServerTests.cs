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
/// ERP-395 单证表头生命周期与明细行变更共享父行锁 / 原子事务协议的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>两个独立连接竞态</b>：① 并发同一显式行序新增 → 唯一赢家 + 另一合法冲突（重复行序）且失败方整体回滚；
/// ② 并发自动行序新增 → 两行都落库且行序唯一（1 / 2）；③ 并发明细行「修改 vs 删除」→ 串行化、无撕裂状态；
/// ④ 并发「表头冻结（已提交客户）vs 明细行新增」→ 冻结后不再接受明细行写入；⑤ 并发「表头删除 vs 明细行新增」→
/// 删除后不再接受明细行写入。</item>
/// <item><b>受限 / 撤销请求不可修改</b>：越界客户单证的明细行写入 fail closed 且零落库；撤销既有单证中心菜单后
/// 实时授权立即收敛。</item>
/// <item><b>失败整体回滚</b>：冲突输家不留任何明细行与审计改写。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class TradeDocumentMutationSqlServerTests
    : IClassFixture<TradeDocumentMutationSqlServerFixture>
{
    private readonly TradeDocumentMutationSqlServerFixture _fixture;

    public TradeDocumentMutationSqlServerTests(TradeDocumentMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(TradeDocumentMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static TradeDocumentController NewController(
        ErpDbContext db, SalespersonDataScope? scope, long? userId = null)
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

    private static TradeDocumentItemSaveDto Item(
        decimal quantity = 2m, decimal unitPrice = 5m, int? lineOrder = null)
        => new()
        {
            ProductCode = "P-INT", ProductNameCn = "毛巾", Quantity = quantity,
            UnitPrice = unitPrice, Unit = "箱", LineOrder = lineOrder,
        };

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
        var code = $"INT_E395_EMP_{Tag()}";
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
            RoleName = $"INT_E395_ROLE_{code}", RoleCode = $"INT_E395_{Guid.NewGuid():N}", IsSystem = false
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
        ErpDbContext db, string docNo, long? customerId, string status = "待制作",
        string docType = "商业发票", decimal amount = 0m)
    {
        var document = new TradeDocument
        {
            DocNo = docNo, DocType = docType, Status = status, Currency = "USD", Amount = amount,
            IssueDate = DateTime.Today, CustomerId = customerId,
            CustomerName = customerId is > 0 ? $"集成客户{customerId}" : string.Empty, Copies = 3,
        };
        db.TradeDocuments.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    private static async Task<TradeDocumentItem> SeedItemAsync(
        ErpDbContext db, long documentId, decimal quantity = 2m, int lineNo = 1)
    {
        var item = new TradeDocumentItem
        {
            TradeDocumentId = documentId, LineNo = lineNo, ProductId = 0, ProductCode = "P-INT",
            ProductNameCn = "毛巾", Quantity = quantity, Unit = "箱", UnitPrice = 5m,
            LineAmount = quantity * 5m, Currency = "USD",
        };
        db.TradeDocumentItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    /// <summary>以既有权威单证为蓝本构造表头修改体（冻结到已提交客户，其余商业字段保持原值）。</summary>
    private static TradeDocument FreezeEdit(TradeDocument stored)
        => new()
        {
            Id = stored.Id, DocNo = stored.DocNo, DocType = stored.DocType, Status = "已提交客户",
            Amount = stored.Amount, Currency = stored.Currency, IssueDate = stored.IssueDate,
            CustomerId = stored.CustomerId, CustomerName = stored.CustomerName, Copies = stored.Copies,
            DeparturePort = stored.DeparturePort, DestinationPort = stored.DestinationPort,
            IssuedBy = stored.IssuedBy, SalesOrderNo = stored.SalesOrderNo, RefNo = stored.RefNo,
            DeclareNo = stored.DeclareNo, FileNote = stored.FileNote, Remark = stored.Remark,
        };

    // ==================== 1. 两个独立连接：明细行写入竞态 ====================

    [Fact]
    public async Task 两个独立连接_并发同显式行序_唯一赢家_另一合法冲突且回滚()
    {
        Guard();
        long documentId;
        await using (var seed = _fixture.CreateDbContext())
            documentId = (await SeedDocumentAsync(seed, $"INT_E395_DUP_{Tag()}", customerId: null)).Id;

        var scope = Privileged();
        var results = await RaceAsync(
            () => TryAsync(scope, c => c.CreateItem(documentId, Item(lineOrder: 5))),
            () => TryAsync(scope, c => c.CreateItem(documentId, Item(lineOrder: 5))));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Contains(results, r => !r.Success && r.Error.Contains("行序"));

        await using var verify = _fixture.CreateDbContext();
        var lines = await verify.TradeDocumentItems.AsNoTracking()
            .Where(i => i.TradeDocumentId == documentId && !i.IsDeleted).ToListAsync();
        Assert.Single(lines);                    // 冲突输家整体回滚：不留第二行
        Assert.Equal(5, lines[0].LineNo);
        Assert.Equal(2m, lines[0].Quantity);
    }

    [Fact]
    public async Task 两个独立连接_并发自动行序_两行都落库且行序唯一()
    {
        Guard();
        long documentId;
        await using (var seed = _fixture.CreateDbContext())
            documentId = (await SeedDocumentAsync(seed, $"INT_E395_AUTO_{Tag()}", customerId: null)).Id;

        var scope = Privileged();
        var results = await RaceAsync(
            () => TryAsync(scope, c => c.CreateItem(documentId, Item())),
            () => TryAsync(scope, c => c.CreateItem(documentId, Item())));

        Assert.All(results, r => Assert.True(r.Success, r.Error));

        await using var verify = _fixture.CreateDbContext();
        var lines = await verify.TradeDocumentItems.AsNoTracking()
            .Where(i => i.TradeDocumentId == documentId && !i.IsDeleted)
            .OrderBy(i => i.LineNo).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { 1, 2 }, lines.Select(i => i.LineNo));   // 父行锁串行化：行序不重复
    }

    [Fact]
    public async Task 两个独立连接_明细行修改与删除_串行化无撕裂()
    {
        Guard();
        long itemId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var document = await SeedDocumentAsync(seed, $"INT_E395_UD_{Tag()}", customerId: null);
            itemId = (await SeedItemAsync(seed, document.Id, quantity: 2m)).Id;
        }

        var scope = Privileged();
        var results = await RaceAsync(
            () => TryAsync(scope, c => c.UpdateItem(itemId, Item(quantity: 9m))),
            () => TryAsync(scope, c => c.DeleteItem(itemId)));

        Assert.True(results[1].Success, results[1].Error);   // 删除串行化后必定成功

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.TradeDocumentItems.AsNoTracking().SingleAsync(i => i.Id == itemId);
        Assert.True(stored.IsDeleted);
        Assert.True(stored.Quantity == 2m || stored.Quantity == 9m);   // 无撕裂：要么未改，要么改后删除
    }

    // ==================== 2. 两个独立连接：表头生命周期 vs 明细行写入 ====================

    [Fact]
    public async Task 两个独立连接_并发表头冻结与明细行新增_冻结后不再接受写入()
    {
        Guard();
        long documentId;
        TradeDocument freeze;
        await using (var seed = _fixture.CreateDbContext())
        {
            var document = await SeedDocumentAsync(seed, $"INT_E395_FREEZE_{Tag()}", customerId: null);
            documentId = document.Id;
            freeze = FreezeEdit(document);
        }

        var scope = Privileged();
        var results = await RaceAsync(
            () => TryAsync(scope, c => c.Update(documentId, freeze)),
            () => TryAsync(scope, c => c.CreateItem(documentId, Item())));

        Assert.True(results[0].Success, results[0].Error);   // 冻结只前进状态，必定成功

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.Equal("已提交客户", stored.Status);

        var lines = await verify.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => i.TradeDocumentId == documentId && !i.IsDeleted);

        // 确定性不变式：明细行只可能在冻结提交前写入；冻结提交后的写入一律被拒。
        if (lines > 0) Assert.True(results[1].Success, results[1].Error);
        else Assert.False(results[1].Success);
    }

    [Fact]
    public async Task 两个独立连接_并发表头删除与明细行新增_删除后不再接受写入()
    {
        Guard();
        long documentId;
        await using (var seed = _fixture.CreateDbContext())
            documentId = (await SeedDocumentAsync(seed, $"INT_E395_DEL_{Tag()}", customerId: null)).Id;

        var scope = Privileged();
        var results = await RaceAsync(
            () => TryAsync(scope, c => c.Delete(documentId)),
            () => TryAsync(scope, c => c.CreateItem(documentId, Item())));

        Assert.True(results[0].Success, results[0].Error);   // 删除必定成功

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.TradeDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.True(stored.IsDeleted);

        var lines = await verify.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => i.TradeDocumentId == documentId && !i.IsDeleted);

        if (lines > 0) Assert.True(results[1].Success, results[1].Error);
        else Assert.False(results[1].Success);
    }

    // ==================== 3. 受限 / 撤销请求不可修改 ====================

    [Fact]
    public async Task 受限账号_越界客户单证明细行写入fail_closed且零落库()
    {
        Guard();
        var tag = Tag();
        long userId, documentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            await SeedCustomerAsync(seed, $"INT_E395_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E395_FOREIGN_{tag}");
            documentId = (await SeedDocumentAsync(seed, $"INT_E395_OFF_{tag}", foreign.Id)).Id;
        }

        SalespersonDataScope scope;
        await using (var auth = _fixture.CreateDbContext())
            scope = await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(auth, userId);

        var result = await TryAsync(scope, c => c.CreateItem(documentId, Item()));
        Assert.False(result.Success);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => i.TradeDocumentId == documentId && !i.IsDeleted));
    }

    [Fact]
    public async Task 撤销既有菜单后_实时授权立即收敛且明细行不变()
    {
        Guard();
        long userId, roleId, documentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, role) = await SeedOperatorAsync(seed);
            userId = user.Id;
            roleId = role.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E395_RV_{Tag()}", employee.Id);
            documentId = (await SeedDocumentAsync(seed, $"INT_E395_RVD_{Tag()}", customer.Id)).Id;
        }

        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenuAsync(revoke, roleId);

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(0, await verify.TradeDocumentItems.AsNoTracking()
            .CountAsync(i => i.TradeDocumentId == documentId && !i.IsDeleted));
    }
}

/// <summary>
/// ERP-395 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class TradeDocumentMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_TRADEDOCMUTATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-395] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-395] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class TradeDocumentMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => TradeDocumentMutationSqlServerFixture.AssertDedicatedTarget(connection));
}
