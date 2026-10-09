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
/// ERP-430 采购订单规范运营读取（详情 / 打印 / JSON 导出）只返回**未删除**明细行的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「采购订单」功能菜单 + 业务员客户数据范围
/// 驱动真实 <see cref="PurchaseOrderController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>混合有效 / 已删除明细</b>：详情 / 打印 / JSON 导出三者只返回有效明细行，且彼此一致；表头历史存储金额原样保留。</item>
/// <item><b>已删除父单 / 越界客户非披露拒绝</b>：详情 / 打印被受控拒绝，导出结果不含已删除父单与范围外父单。</item>
/// <item><b>证据不变</b>：读取前后来源（采购订单 / 明细）、库存（入库单 / 明细 / 库存 / 流水）、应付（采购发票）、
/// 付款（付款引用行）与审计日志计数完全不变；被软删除明细行字段原样。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseOrderOperationalDetailSqlServerTests
    : IClassFixture<PurchaseOrderOperationalDetailSqlServerFixture>
{
    private readonly PurchaseOrderOperationalDetailSqlServerFixture _fx;

    public PurchaseOrderOperationalDetailSqlServerTests(PurchaseOrderOperationalDetailSqlServerFixture fixture)
        => _fx = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => PurchaseOrderOperationalDetailSqlServerFixture
        .AssertDedicatedTarget(_fx.ConnectionString);

    private static PurchaseOrderController NewController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/purchase-orders";

        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static T DataOf<T>(IActionResult result)
        => Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static async Task AssertDeniedAsync(int code, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
    }

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fx.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PurchaseOrders"] = await db.PurchaseOrders.CountAsync(),
            ["PurchaseOrderDetails"] = await db.PurchaseOrderDetails.CountAsync(),
            ["StockIns"] = await db.StockIns.CountAsync(),
            ["StockInDetails"] = await db.StockInDetails.CountAsync(),
            ["Stocks"] = await db.Stocks.CountAsync(),
            ["StockMovements"] = await db.StockMovements.CountAsync(),
            ["PurchaseInvoices"] = await db.PurchaseInvoices.CountAsync(),
            ["SupplierPaymentAllocations"] = await db.SupplierPaymentAllocations.CountAsync(),
            ["SysOperationLogs"] = await db.SysOperationLogs.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in before.Keys)
            Assert.Equal(before[table], after[table]);
    }

    // ==================== 1. 详情 / 打印 / JSON 导出有效明细一致 ====================

    [Fact]
    public async Task Detail_print_and_json_export_agree_on_live_lines_only()
    {
        Guard();
        await using var db = _fx.CreateDbContext();
        var ctl = NewController(db, _fx.PrivilegedUserId);

        var detail = DataOf<PurchaseOrder>(await ctl.GetById(_fx.MixedOrderId));
        var print = DataOf<PurchaseOrder>(await ctl.GetPrint(_fx.MixedOrderId));
        var exported = DataOf<List<PurchaseOrder>>(await ctl.Export(null, null))
            .Single(o => o.Id == _fx.MixedOrderId);

        var expected = new[] { _fx.LiveProductAId, _fx.LiveProductBId }.OrderBy(id => id).ToArray();
        Assert.Equal(expected, detail.Details.Select(d => d.ProductId).OrderBy(id => id).ToArray());
        Assert.Equal(expected, print.Details.Select(d => d.ProductId).OrderBy(id => id).ToArray());
        Assert.Equal(expected, exported.Details.Select(d => d.ProductId).OrderBy(id => id).ToArray());
        Assert.All(detail.Details, d => Assert.False(d.IsDeleted));
        Assert.All(print.Details, d => Assert.False(d.IsDeleted));
        Assert.All(exported.Details, d => Assert.False(d.IsDeleted));

        // 历史存储表头金额原样保留（有效明细合计 ≠ 表头金额时也绝不重算）。
        Assert.Equal(PurchaseOrderOperationalDetailSqlServerFixture.MixedOrderHeaderAmount, detail.TotalAmount);
        Assert.Equal(PurchaseOrderOperationalDetailSqlServerFixture.MixedOrderHeaderAmount, print.TotalAmount);
        Assert.Equal(PurchaseOrderOperationalDetailSqlServerFixture.MixedOrderHeaderAmount, exported.TotalAmount);
    }

    // ==================== 2. 已删除父单非披露拒绝 / 导出排除 ====================

    [Fact]
    public async Task Deleted_parent_is_denied_and_excluded_from_export()
    {
        Guard();
        await using var db = _fx.CreateDbContext();
        var ctl = NewController(db, _fx.PrivilegedUserId);

        await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.GetById(_fx.DeletedParentOrderId));
        await AssertDeniedAsync(ErrorCodes.NotFound, () => ctl.GetPrint(_fx.DeletedParentOrderId));

        var exported = DataOf<List<PurchaseOrder>>(await ctl.Export(null, null));
        Assert.Contains(exported, o => o.Id == _fx.MixedOrderId);
        Assert.DoesNotContain(exported, o => o.Id == _fx.DeletedParentOrderId);
    }

    // ==================== 3. 受限账号范围与越界拒绝 ====================

    [Fact]
    public async Task Foreign_scope_is_refused_and_export_is_scoped_without_leak()
    {
        Guard();
        await using var db = _fx.CreateDbContext();
        var ctl = NewController(db, _fx.RestrictedUserId);

        var own = DataOf<PurchaseOrder>(await ctl.GetById(_fx.MixedOrderId));
        Assert.Equal(2, own.Details.Count);
        Assert.All(own.Details, d => Assert.False(d.IsDeleted));

        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetById(_fx.ForeignOrderId));
        await AssertDeniedAsync(ErrorCodes.Forbidden, () => ctl.GetPrint(_fx.ForeignOrderId));

        var exported = DataOf<List<PurchaseOrder>>(await ctl.Export(null, null));
        Assert.Contains(exported, o => o.Id == _fx.MixedOrderId);
        Assert.DoesNotContain(exported, o => o.Id == _fx.ForeignOrderId);
    }

    // ==================== 4. 身份 fail closed（缺失 / 禁用 / 撤销菜单） ====================

    [Fact]
    public async Task Missing_disabled_and_revoked_identity_are_refused_without_evidence_change()
    {
        Guard();

        await using (var anonymousDb = _fx.CreateDbContext())
        {
            var anonymous = NewController(anonymousDb, null);
            await AssertDeniedAsync(ErrorCodes.Unauthorized, () => anonymous.GetById(_fx.MixedOrderId));
            await AssertDeniedAsync(ErrorCodes.Unauthorized, () => anonymous.GetPrint(_fx.MixedOrderId));
            await AssertDeniedAsync(ErrorCodes.Unauthorized, () => anonymous.Export(null, null));
        }

        var before = await SnapshotAsync();

        await using (var disabledDb = _fx.CreateDbContext())
        {
            var disabled = NewController(disabledDb, _fx.DisabledUserId);
            await AssertDeniedAsync(ErrorCodes.Forbidden, () => disabled.GetById(_fx.MixedOrderId));
            await AssertDeniedAsync(ErrorCodes.Forbidden, () => disabled.GetPrint(_fx.MixedOrderId));
            await AssertDeniedAsync(ErrorCodes.Forbidden, () => disabled.Export(null, null));
        }

        // 撤销既有菜单授权：运营读取在下一请求立即收敛（绝不缓存）；随后恢复以免影响同类其他用例。
        await using (var revokeDb = _fx.CreateDbContext())
        {
            var grants = await revokeDb.SysRoleMenus
                .Where(g => g.RoleId == _fx.RestrictedRoleId && !g.IsDeleted).ToListAsync();
            foreach (var grant in grants) grant.IsDeleted = true;
            await revokeDb.SaveChangesAsync();
        }

        try
        {
            await using var revokedDb = _fx.CreateDbContext();
            var revoked = NewController(revokedDb, _fx.RestrictedUserId);
            await AssertDeniedAsync(ErrorCodes.Forbidden, () => revoked.GetById(_fx.MixedOrderId));
            await AssertDeniedAsync(ErrorCodes.Forbidden, () => revoked.GetPrint(_fx.MixedOrderId));
            await AssertDeniedAsync(ErrorCodes.Forbidden, () => revoked.Export(null, null));
        }
        finally
        {
            await using var restoreDb = _fx.CreateDbContext();
            var grants = await restoreDb.SysRoleMenus.Where(g => g.RoleId == _fx.RestrictedRoleId).ToListAsync();
            foreach (var grant in grants) grant.IsDeleted = false;
            await restoreDb.SaveChangesAsync();
        }

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 5. 读取零副作用（来源 / 库存 / 应付 / 付款证据 + 软删除行） ====================

    [Fact]
    public async Task Reads_preserve_source_inventory_payable_payment_evidence_and_tombstoned_rows()
    {
        Guard();
        var before = await SnapshotAsync();
        var tombstonesBefore = await TombstonesAsync(_fx.MixedOrderId);

        await using (var db = _fx.CreateDbContext())
        {
            var ctl = NewController(db, _fx.PrivilegedUserId);
            Assert.IsType<OkObjectResult>(await ctl.GetById(_fx.MixedOrderId));
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(_fx.MixedOrderId));
            Assert.IsType<OkObjectResult>(await ctl.Export(null, null));
        }

        AssertSnapshotUnchanged(before, await SnapshotAsync());

        var tombstonesAfter = await TombstonesAsync(_fx.MixedOrderId);
        Assert.Equal(tombstonesBefore, tombstonesAfter);
        Assert.Equal(2, tombstonesAfter.Count);
    }

    private async Task<List<(long Id, bool IsDeleted, decimal Quantity, decimal UnitPrice, decimal Amount, DateTime? UpdatedAt)>>
        TombstonesAsync(long orderId)
    {
        await using var db = _fx.CreateDbContext();
        return await db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.PurchaseOrderId == orderId && d.IsDeleted)
            .OrderBy(d => d.Id)
            .Select(d => new ValueTuple<long, bool, decimal, decimal, decimal, DateTime?>(
                d.Id, d.IsDeleted, d.Quantity, d.UnitPrice, d.Amount, d.UpdatedAt))
            .ToListAsync();
    }
}

/// <summary>
/// ERP-430 真实 SQL 夹具：专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c> 上的一次性**全新 GUID 库**
/// （<c>NEWERP_AUTOTEST_POOPDETAIL_&lt;guid&gt;</c>），完整 NEWERP 结构 + 种子数据；绝不 drop / reset / 复用任何库。
/// <para>只使用既有认证授权（既有「采购订单」菜单 + 系统内置 / 普通角色 + 业务员客户数据范围），
/// 不新增任何菜单 / 角色 / 用户授权。</para>
/// </summary>
public sealed class PurchaseOrderOperationalDetailSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>混合订单的历史存储表头金额（刻意不等于有效明细合计，验证读取不重算）。</summary>
    public const decimal MixedOrderHeaderAmount = 3000m;

    private const string DefaultDatabaseName = DatabasePrefix + "_POOPDETAIL";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long RestrictedRoleId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long SupplierId { get; private set; }
    public long VisibleCustomerId { get; private set; }
    public long OutOfScopeCustomerId { get; private set; }
    public long LiveProductAId { get; private set; }
    public long LiveProductBId { get; private set; }
    public long MixedOrderId { get; private set; }
    public long DeletedParentOrderId { get; private set; }
    public long ForeignOrderId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-430] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        Console.WriteLine("[ERP-430] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    private static async Task<SysMenu> EnsurePurchaseOrderMenuAsync(ErpDbContext db)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m =>
            m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    private static SysUser NewUser(UserStatus status, string? userName = null) => new()
    {
        UserName = userName ?? $"erp430-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        DisplayName = "ERP430 隔离账号",
        Status = status
    };

    // ==================== 既有授权（不新增权限模型）+ 混合明细夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();
        await SeedAuthorizedUsersAsync(db);
        await SeedMastersAndDocumentsAsync(db);
    }

    private async Task SeedAuthorizedUsersAsync(ErpDbContext db)
    {
        var menu = await EnsurePurchaseOrderMenuAsync(db);

        // 特权账号：系统内置角色 + 既有「采购订单」菜单。
        var privilegedRole = new SysRole { RoleName = "ERP430 特权角色", RoleCode = $"ERP430-P-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = privilegedRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        // 受限业务员：既有「采购订单」菜单 + 业务员客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var employee = new BaseEmployee
        {
            EmployeeCode = $"E-430-{Guid.NewGuid():N}", EmployeeName = "ERP430 受限采购业务员",
            IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        var restricted = NewUser(UserStatus.Enabled, employee.EmployeeCode);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRole = new SysRole { RoleName = $"ERP430-R-{Guid.NewGuid():N}", RoleCode = $"ERP430-R-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(restrictedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRole.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        var visibleCustomer = new BaseCustomer
        {
            CustomerCode = $"C-430-{Guid.NewGuid():N}", CustomerName = "ERP430 可见客户",
            EmpId = employee.Id, Status = 1, CreditStatus = "正常"
        };
        var outOfScopeCustomer = new BaseCustomer
        {
            CustomerCode = $"C-430X-{Guid.NewGuid():N}", CustomerName = "ERP430 越界客户",
            Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(visibleCustomer, outOfScopeCustomer);
        await db.SaveChangesAsync();

        // 禁用账号：具备既有「采购订单」菜单但账号禁用 → 权限不足。
        var disabledRole = new SysRole { RoleName = $"ERP430-D-{Guid.NewGuid():N}", RoleCode = $"ERP430-D-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(disabledRole);
        await db.SaveChangesAsync();
        var disabled = NewUser(UserStatus.Disabled);
        db.SysUsers.Add(disabled);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = disabled.Id, RoleId = disabledRole.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = disabledRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        PrivilegedUserId = privileged.Id;
        RestrictedUserId = restricted.Id;
        RestrictedRoleId = restrictedRole.Id;
        DisabledUserId = disabled.Id;
        VisibleCustomerId = visibleCustomer.Id;
        OutOfScopeCustomerId = outOfScopeCustomer.Id;
    }

    private async Task SeedMastersAndDocumentsAsync(ErpDbContext db)
    {
        // 供应商 + 有效商品。
        var supplier = new BaseSupplier { SupplierCode = $"S-430-{Guid.NewGuid():N}", SupplierName = "ERP430 供应商", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();

        var productA = new BaseProduct { ProductCode = $"P-430A-{Guid.NewGuid():N}", ProductName = "ERP430 商品A", Unit = "PCS", Status = 1 };
        var productB = new BaseProduct { ProductCode = $"P-430B-{Guid.NewGuid():N}", ProductName = "ERP430 商品B", Unit = "PCS", Status = 1 };
        db.BaseProducts.AddRange(productA, productB);
        await db.SaveChangesAsync();

        // 混合有效 / 已删除明细的订单 + 已删除父单 + 越界客户订单。
        var mixed = await SeedOrderAsync("PO-430-MIX", supplier.Id, VisibleCustomerId, deleted: false,
            MixedOrderHeaderAmount,
            new[] { (productA.Id, 1m, 100m), (productB.Id, 1m, 100m) },
            new[] { (productA.Id, 9m, 9m), (productB.Id, 9m, 9m) });
        var deletedParent = await SeedOrderAsync("PO-430-DEL", supplier.Id, VisibleCustomerId, deleted: true,
            1000m, new[] { (productA.Id, 1m, 100m) }, new[] { (productB.Id, 9m, 9m) });
        var foreign = await SeedOrderAsync("PO-430-FOR", supplier.Id, OutOfScopeCustomerId, deleted: false,
            1000m, new[] { (productA.Id, 1m, 100m) }, new[] { (productB.Id, 9m, 9m) });

        // 应付 / 付款证据（本表刻意不建外键，纯台账）。
        db.PurchaseInvoices.Add(new PurchaseInvoice
        {
            InvoiceType = "普票", InvoiceNumber = $"INV-430-{Guid.NewGuid():N}",
            InvoiceDate = new DateTime(2026, 3, 5), SupplierId = supplier.Id,
            SupplierCode = supplier.SupplierCode, SupplierName = supplier.SupplierName,
            Currency = "CNY", NetAmount = 100m, TaxAmount = 0m, GrossAmount = 100m, Status = 1
        });
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            PaymentId = 430_000_001L, PaymentNo = "PM-430", PaymentDate = new DateTime(2026, 3, 6),
            PaymentStatus = (int)DocumentStatus.Approved, PaymentStatusText = "已审核", PaymentAmount = 100m,
            PurchaseOrderId = mixed.Id, OrderNo = mixed.OrderNo, OrderDate = mixed.OrderDate,
            OrderStatus = (int)mixed.Status, OrderCurrency = "CNY",
            SupplierId = supplier.Id, SupplierCode = supplier.SupplierCode, SupplierName = supplier.SupplierName,
            AllocatedAmount = 100m, Currency = "CNY", Status = 1, AllocatedAt = new DateTime(2026, 3, 6)
        });
        await db.SaveChangesAsync();

        SupplierId = supplier.Id;
        LiveProductAId = productA.Id;
        LiveProductBId = productB.Id;
        MixedOrderId = mixed.Id;
        DeletedParentOrderId = deletedParent.Id;
        ForeignOrderId = foreign.Id;
    }

    private async Task<PurchaseOrder> SeedOrderAsync(string no, long supplierId, long? owningCustomerId,
        bool deleted, decimal headerAmount,
        IEnumerable<(long ProductId, decimal Quantity, decimal UnitPrice)> liveLines,
        IEnumerable<(long ProductId, decimal Quantity, decimal UnitPrice)> deletedLines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = new DateTime(2026, 3, 1),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            OwningCustomerName = owningCustomerId is null ? string.Empty : "ERP430 客户",
            Status = DocumentStatus.Pending,
            TotalAmount = headerAmount,
            Remark = "ERP430 运营快照",
            IsDeleted = deleted,
            CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
            UpdatedAt = new DateTime(2026, 3, 2, 9, 30, 0)
        };
        foreach (var (productId, quantity, unitPrice) in liveLines)
            order.Details.Add(NewDetail(productId, quantity, unitPrice, isDeleted: false));
        foreach (var (productId, quantity, unitPrice) in deletedLines)
            order.Details.Add(NewDetail(productId, quantity, unitPrice, isDeleted: true));

        await using var db = CreateDbContext();
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static PurchaseOrderDetail NewDetail(long productId, decimal quantity, decimal unitPrice, bool isDeleted) => new()
    {
        ProductId = productId,
        ProductName = $"ERP430 商品{productId}",
        Spec = "规格A",
        Unit = "PCS",
        Quantity = quantity,
        UnitPrice = unitPrice,
        Amount = quantity * unitPrice,
        IsDeleted = isDeleted,
        CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0),
        UpdatedAt = new DateTime(2026, 3, 2, 9, 30, 0)
    };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseOrderOperationalDetailTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseOrderOperationalDetailSqlServerFixture.AssertDedicatedTarget(connection));
}







