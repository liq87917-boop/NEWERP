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
/// ERP-466 规范采购订单（<c>api/purchase-orders</c>）入口实时授权 SQL Server 集成测试（专用 NEWERP_AUTOTEST 护栏）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有身份 / 菜单 / 数据范围</b>：以既有「采购订单」（<c>purchase-order</c>）菜单授权与既有
/// <see cref="SalespersonDataScopeService"/> 数据范围口径驱动真实 <see cref="PurchaseOrderController"/>。</item>
/// <item><b>与请求形状无关</b>：<strong>空 <c>Request.Path</c></strong> 与<strong>已赋值 <c>Request.Path</c></strong>
/// 必须给出完全一致的判定；缺失 / 零 / 未知 / 已删除身份一律未认证，禁用 / 撤销菜单一律权限不足，且都在任何订单 /
/// 明细 / 派生读取之前。</item>
/// <item><b>保留既有业务语义与零写入拒绝</b>：既有菜单授权身份照常读取列表 / 详情 / 打印 / 导出 / 派生视图并完成既有
/// 生命周期；被拒绝的请求零写入、零授权扩张、不消耗单据号。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseOrderEntryAuthorizationSqlServerTests
    : IClassFixture<PurchaseOrderEntryAuthorizationSqlServerFixture>
{
    private readonly PurchaseOrderEntryAuthorizationSqlServerFixture _fixture;

    public PurchaseOrderEntryAuthorizationSqlServerTests(PurchaseOrderEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private const string RoutePath = "/api/purchase-orders";
    private const long UnknownUserId = 9_466_999_999L;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseOrderEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    /// <summary>绑定真实登录身份的真实控制器；<paramref name="requestPath"/> 为 null 表示空路径请求。</summary>
    private static PurchaseOrderController NewController(ErpDbContext db, long? userId, string? requestPath)
    {
        var claims = userId is > 0
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (requestPath is not null) http.Request.Path = requestPath;
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>用一条独立连接执行控制器动作，返回受控错误码（成功返回 null）。</summary>
    private async Task<int?> DeniedCodeAsync(
        long? userId, string? requestPath, Func<PurchaseOrderController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await action(NewController(db, userId, requestPath));
            return null;
        }
        catch (BusinessException ex)
        {
            return ex.Code;
        }
    }

    /// <summary>用一条独立连接执行控制器动作，断言返回 Ok。</summary>
    private async Task AssertOkAsync(
        long? userId, string? requestPath, Func<PurchaseOrderController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        Assert.IsType<OkObjectResult>(await action(NewController(db, userId, requestPath)));
    }

    /// <summary>核心入口：先授权、后读写；拒绝时返回同一受控错误码。</summary>
    private async Task AssertAllCoreEntriesDeniedAsync(long? userId, string? requestPath, long orderId, int code)
    {
        var ids = orderId.ToString();
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetById(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.GetPrint(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Export(null, null)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.ExportExcel(null, null, null, null)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Timeline(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Progress(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.FinanceReconciliation(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.ReturnImpact(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.InvoiceEvidence(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.InvoiceEvidenceSummaries(ids)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.PaymentEvidence(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.PaymentEvidenceSummaries(ids)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.InvoicePaymentEvidence(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.InvoicePaymentEvidenceSummaries(ids)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Create(NewOrder(0L, 0L))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Update(orderId, NewOrder(0L, 0L))));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Submit(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Approve(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Cancel(orderId)));
        Assert.Equal(code, await DeniedCodeAsync(userId, requestPath, c => c.Delete(orderId)));
    }

    // ==================== 1. 缺失 / 零 / 未知 / 已删除身份：任何入口、任何请求形状一律未认证 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Missing_zero_unknown_and_deleted_identity_are_unauthorized(string? requestPath)
    {
        Guard();
        long orderId, deletedUserId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var order = await SeedOrderAsync(seed);
            var deleted = await SeedOperatorAsync(seed);
            await SoftDeleteUserAsync(seed, deleted.UserId);
            orderId = order.Id;
            deletedUserId = deleted.UserId;
        }

        await AssertAllCoreEntriesDeniedAsync(null, requestPath, orderId, ErrorCodes.Unauthorized);
        await AssertAllCoreEntriesDeniedAsync(0L, requestPath, orderId, ErrorCodes.Unauthorized);
        await AssertAllCoreEntriesDeniedAsync(UnknownUserId, requestPath, orderId, ErrorCodes.Unauthorized);
        await AssertAllCoreEntriesDeniedAsync(deletedUserId, requestPath, orderId, ErrorCodes.Unauthorized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Disabled_identity_is_forbidden_on_both_request_shapes(string? requestPath)
    {
        Guard();
        long orderId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var order = await SeedOrderAsync(seed);
            var disabled = await SeedOperatorAsync(seed, UserStatus.Disabled);
            orderId = order.Id;
            userId = disabled.UserId;
        }

        await AssertAllCoreEntriesDeniedAsync(userId, requestPath, orderId, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Revoked_menu_denies_every_entry_on_both_request_shapes(string? requestPath)
    {
        Guard();
        long orderId, userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var order = await SeedOrderAsync(seed);
            var owner = await SeedOperatorAsync(seed);
            await RevokeMenuAsync(seed, owner.RoleId);
            orderId = order.Id;
            userId = owner.UserId;
        }

        await AssertAllCoreEntriesDeniedAsync(userId, requestPath, orderId, ErrorCodes.Forbidden);
    }

    // ==================== 2. 授权身份：空路径与已赋值路径读出完全一致的既有契约 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Authorized_identity_reads_and_lifecycle_are_identical(string? requestPath)
    {
        Guard();
        long orderId, userId, supplierId, productId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (supplier, product) = await SeedMasterAsync(seed);
            var order = await SeedOrderAsync(seed, supplier.Id, product.Id);
            var owner = await SeedOperatorAsync(seed);
            orderId = order.Id;
            userId = owner.UserId;
            supplierId = supplier.Id;
            productId = product.Id;
        }

        await AssertOkAsync(userId, requestPath, c => c.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertOkAsync(userId, requestPath, c => c.GetById(orderId));
        await AssertOkAsync(userId, requestPath, c => c.GetPrint(orderId));
        await AssertOkAsync(userId, requestPath, c => c.Export(null, null));
        await AssertOkAsync(userId, requestPath, c => c.Timeline(orderId));
        await AssertOkAsync(userId, requestPath, c => c.Progress(orderId));
        await AssertOkAsync(userId, requestPath, c => c.ReturnImpact(orderId));
        await AssertOkAsync(userId, requestPath, c => c.InvoiceEvidence(orderId));
        await AssertOkAsync(userId, requestPath, c => c.PaymentEvidence(orderId));

        // 既有生命周期：新增 → 修改 → 提交 → 审核（写入经实时授权 + ERP-427 实时主数据复核）。
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, userId, requestPath);
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrder(supplierId, productId)));
        var createdId = await db.PurchaseOrders.OrderByDescending(o => o.Id).Select(o => o.Id).FirstAsync();
        Assert.IsType<OkObjectResult>(await ctl.Update(createdId, NewOrder(supplierId, productId)));
        Assert.IsType<OkObjectResult>(await ctl.Submit(createdId));
        Assert.IsType<OkObjectResult>(await ctl.Approve(createdId));
    }

    // ==================== 3. 拒绝调用零写入、零授权扩张且不消耗单据号 ====================

    [Theory]
    [InlineData(null)]
    [InlineData(RoutePath)]
    public async Task Denied_calls_persist_zero_rows_and_consume_no_document_number(string? requestPath)
    {
        Guard();
        long orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var order = await SeedOrderAsync(seed);
            orderId = order.Id;
        }

        long ordersBefore, usersBefore, rolesBefore, grantsBefore;
        await using (var before = _fixture.CreateDbContext())
        {
            ordersBefore = await before.PurchaseOrders.CountAsync();
            usersBefore = await before.SysUsers.CountAsync();
            rolesBefore = await before.SysRoles.CountAsync();
            grantsBefore = await before.SysRoleMenus.CountAsync(g => !g.IsDeleted);
        }

        await AssertAllCoreEntriesDeniedAsync(null, requestPath, orderId, ErrorCodes.Unauthorized);

        await using var after = _fixture.CreateDbContext();
        Assert.Equal(ordersBefore, await after.PurchaseOrders.CountAsync());
        Assert.Equal(usersBefore, await after.SysUsers.CountAsync());
        Assert.Equal(rolesBefore, await after.SysRoles.CountAsync());
        Assert.Equal(grantsBefore, await after.SysRoleMenus.CountAsync(g => !g.IsDeleted));
    }

    // ==================== 种子助手（EF 生成身份主键，绝不清理既有行） ====================

    private static async Task<(BaseSupplier Supplier, BaseProduct Product)> SeedMasterAsync(ErpDbContext db)
    {
        var code = $"INT_PO466_{Guid.NewGuid():N}";
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = code, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();

        var product = new BaseProduct
        {
            ProductCode = code, ProductName = code, Spec = "规格A", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return (supplier, product);
    }

    private static async Task<(long UserId, long RoleId)> SeedOperatorAsync(
        ErpDbContext db, UserStatus status = UserStatus.Enabled, bool menu = true)
    {
        var code = $"int-po466-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "采购入口集成角色", RoleCode = $"Po466Op-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (menu)
        {
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        return (user.Id, role.Id);
    }

    private static async Task RevokeMenuAsync(ErpDbContext db, long roleId)
    {
        foreach (var grant in await db.SysRoleMenus.Where(g => g.RoleId == roleId && !g.IsDeleted).ToListAsync())
            grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task SoftDeleteUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task<PurchaseOrder> SeedOrderAsync(ErpDbContext db, long supplierId = 0L, long productId = 0L)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"INT_PO466_{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TotalAmount = productId > 0 ? 1000m : 0m,
            Status = DocumentStatus.Pending
        };
        if (productId > 0)
        {
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = productId, ProductName = "集成商品", Spec = "规格A", Unit = "PCS",
                Quantity = 10m, UnitPrice = 100m, Amount = 1000m
            });
        }
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static PurchaseOrder NewOrder(long supplierId, long productId)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "集成商品", Spec = "规格A", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
}

/// <summary>
/// ERP-466 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
    /// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
    /// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
    /// </summary>
    public sealed class PurchaseOrderEntryAuthorizationSqlServerFixture : IAsyncLifetime
    {
        public const string InstanceMarker = "NEWERP_AutoAcceptance";
        public const string DatabasePrefix = "NEWERP_AUTOTEST";
        public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PURCHASEORDERENTRYAUTHORIZATION";

        public string ConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = BuildDefaultConnectionString();

            AssertDedicatedTarget(connectionString);
            ConnectionString = connectionString;

            Console.WriteLine($"[ERP-466] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

            Console.WriteLine("[ERP-466] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
        }
    }

    /// <summary>专用目标护栏的 fail-closed 单元覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public sealed class PurchaseOrderEntryAuthorizationTargetGuardTests
    {
        [Theory]
        [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
        [InlineData("Server=(localdb)\\\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
        [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
        [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
        public void Rejects_non_dedicated_targets_before_database_access(string connection)
            => Assert.ThrowsAny<Exception>(
                () => PurchaseOrderEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
    }

