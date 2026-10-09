using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-442 出口退税台账（<c>api/base/tax-refunds</c>）实时授权与权威客户范围 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 / 既有菜单</b>：以既有「出口退税台账」（<c>tax-refund</c>）菜单授权、ERP-097
/// 业务员数据范围与真实 <see cref="TaxRefundController"/> 验证特权 / 受限本人 / 受限他人 / 无主 / 撤销授权 /
/// 禁用 / 删除 / 无身份场景；</item>
/// <item><b>拒绝零写入</b>：被拒绝的新增 / 修改 / 删除 / 批量删除绝不落任何台账行、绝不改写原行与审计；</item>
/// <item><b>字段护栏</b>：非法金额 / 退税率 / 期间 / 币种 / 申报与到账日期以既有受控校验错误拒绝，不静默截断或回填。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class TaxRefundLedgerAuthorizationSqlServerTests
    : IClassFixture<TaxRefundLedgerAuthorizationSqlServerFixture>
{
    private readonly TaxRefundLedgerAuthorizationSqlServerFixture _fixture;

    public TaxRefundLedgerAuthorizationSqlServerTests(TaxRefundLedgerAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(TaxRefundLedgerAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器脚手架（身份只来自已认证请求主体） ====================

    private static TaxRefundController NewController(ErpDbContext db, long? userId)
    {
        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new TaxRefundController(new GenericService<BaseTaxRefund>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<PagedResult<BaseTaxRefund>> PageAsync(TaxRefundController controller)
    {
        var result = await controller.GetPaged(new PageQuery());
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<PagedResult<BaseTaxRefund>>>(ok.Value).Data!;
    }

    private static BaseTaxRefund Ledger(string refundNo, long? customerId, decimal exportAmount = 1000m,
        decimal refundRate = 13m, decimal refundableAmount = 130m, decimal refundedAmount = 0m,
        string currency = "USD", string period = "2026-08")
        => new()
        {
            RefundNo = refundNo,
            RefundPeriod = period,
            DeclareDate = new DateTime(2026, 8, 5),
            CustomerId = customerId,
            CustomerName = customerId is > 0 ? $"集成客户{customerId}" : string.Empty,
            ExportAmount = exportAmount,
            Currency = currency,
            RefundRate = refundRate,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            Status = "待申报"
        };

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(
        ErpDbContext db, string code, long? empId = null, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = code,
            Status = status,
            CreditStatus = "正常",
            Currency = "USD",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseEmployee> SeedEmployeeAsync(ErpDbContext db, string tag)
    {
        var code = $"INT_E442_EMP_{tag}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    /// <summary>播种受限业务员：登录名 = 员工编码（ERP-097 权威映射），并按需授予既有 tax-refund 菜单。</summary>
    private static async Task<(SysUser User, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, BaseEmployee employee, bool grantMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = employee.EmployeeCode,
            Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E442_ROLE_{employee.EmployeeCode}",
            RoleCode = $"INT_E442_{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = await db.SysMenus.FirstAsync(
                m => !m.IsDeleted && m.MenuCode == TaxRefundLedgerRules.RequiredMenuCode);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return (user, role);
    }

    private static async Task<SysUser> SeedPrivilegedUserAsync(ErpDbContext db, string tag)
    {
        var user = new SysUser
        {
            UserName = $"INT_E442_ADM_{tag}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = $"INT_E442_ADM_{tag}",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E442_SYS_{tag}",
            RoleCode = $"INT_E442_SYS_{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<BaseTaxRefund> SeedLedgerAsync(ErpDbContext db, string refundNo, long? customerId,
        decimal exportAmount = 1000m)
    {
        var entity = Ledger(refundNo, customerId, exportAmount: exportAmount);
        db.BaseTaxRefunds.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }


    // ==================== 1. 特权 / 受限本人 / 受限他人 / 无主 读取与写入 ====================

    [Fact]
    public async Task Privileged_and_in_scope_flows_read_and_persist_while_foreign_and_orphan_are_hidden()
    {
        Guard();
        var tag = Tag();
        long ownCustomerId, ownLedgerId, foreignLedgerId, orphanLedgerId, operatorUserId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            operatorUserId = (await SeedOperatorAsync(seed, employee)).User.Id;

            var own = await SeedCustomerAsync(seed, $"INT_E442_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E442_OTH_{tag}");
            ownCustomerId = own.Id;

            ownLedgerId = (await SeedLedgerAsync(seed, $"TR-E442-OWN-{tag}", own.Id, 1200m)).Id;
            foreignLedgerId = (await SeedLedgerAsync(seed, $"TR-E442-OTH-{tag}", foreign.Id, 2500m)).Id;
            orphanLedgerId = (await SeedLedgerAsync(seed, $"TR-E442-NONE-{tag}", null, 900m)).Id;
        }

        // 特权账号保留既有全部访问（含历史无主行）
        await using (var db = _fixture.CreateDbContext())
        {
            var admin = await SeedPrivilegedUserAsync(db, tag);
            var page = await PageAsync(NewController(db, admin.Id));
            Assert.Contains(page.Items, x => x.Id == ownLedgerId);
            Assert.Contains(page.Items, x => x.Id == foreignLedgerId);
            Assert.Contains(page.Items, x => x.Id == orphanLedgerId);

            var created = await NewController(db, admin.Id).Create(Ledger($"TR-E442-ADM-{tag}", null, 777m));
            var ok = Assert.IsType<OkObjectResult>(created);
            var saved = Assert.IsType<ApiResponse<BaseTaxRefund>>(ok.Value).Data!;
            Assert.True(saved.Id > 0);
        }

        // 受限账号：只读本人客户；他人与无主一律不泄露
        await using (var db = _fixture.CreateDbContext())
        {
            var page = await PageAsync(NewController(db, operatorUserId));
            Assert.Contains(page.Items, x => x.Id == ownLedgerId);
            Assert.DoesNotContain(page.Items, x => x.Id == foreignLedgerId);
            Assert.DoesNotContain(page.Items, x => x.Id == orphanLedgerId);
            Assert.DoesNotContain(page.Items, x => x.CustomerId is null);

            var own = await NewController(db, operatorUserId).GetById(ownLedgerId);
            Assert.IsType<OkObjectResult>(own);

            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId).GetById(foreignLedgerId));
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId).GetById(orphanLedgerId));

            // 本人客户新增：落库
            var created = await NewController(db, operatorUserId)
                .Create(Ledger($"TR-E442-INS-{tag}", ownCustomerId, 1500m));
            Assert.IsType<OkObjectResult>(created);
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            var ownRow = await verify.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == ownLedgerId);
            Assert.Equal(1200m, ownRow.ExportAmount);
            Assert.Equal((long?)ownCustomerId, ownRow.CustomerId);
            Assert.Single(await verify.BaseTaxRefunds.AsNoTracking()
                .Where(x => x.RefundNo == $"TR-E442-INS-{tag}").ToListAsync());
        }
    }


    [Fact]
    public async Task Foreign_create_update_delete_and_batch_delete_persist_no_ledger_row_change()
    {
        Guard();
        var tag = Tag();
        long ownCustomerId, foreignCustomerId, ownLedgerId, foreignLedgerId, operatorUserId;
        decimal foreignAmountBefore;
        DateTime? foreignUpdatedBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            operatorUserId = (await SeedOperatorAsync(seed, employee)).User.Id;

            var own = await SeedCustomerAsync(seed, $"INT_E442_W_OWN_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E442_W_OTH_{tag}");
            ownCustomerId = own.Id;
            foreignCustomerId = foreign.Id;

            ownLedgerId = (await SeedLedgerAsync(seed, $"TR-E442-W-OWN-{tag}", own.Id, 700m)).Id;
            var foreignRow = await SeedLedgerAsync(seed, $"TR-E442-W-OTH-{tag}", foreign.Id, 500m);
            foreignLedgerId = foreignRow.Id;
            foreignAmountBefore = foreignRow.ExportAmount;
            foreignUpdatedBefore = foreignRow.UpdatedAt;
        }

        int countBefore;
        await using (var before = _fixture.CreateDbContext())
            countBefore = await before.BaseTaxRefunds.AsNoTracking().CountAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            // 越界新增：拒绝且不落库
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId)
                    .Create(Ledger($"TR-E442-W-TRY-{tag}", foreignCustomerId, 999m)));

            // 越界改写他人行：拒绝
            var edit = Ledger($"TR-E442-W-OTH-{tag}", ownCustomerId, 9999m);
            edit.Id = foreignLedgerId;
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId).Update(foreignLedgerId, edit));

            // 把自己的行改派给范围外客户：拒绝
            var reassign = Ledger($"TR-E442-W-OWN-{tag}", foreignCustomerId, 1234m);
            reassign.Id = ownLedgerId;
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId).Update(ownLedgerId, reassign));

            // 越界删除 / 混合批量删除：拒绝
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId).Delete(foreignLedgerId));
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, operatorUserId)
                    .BatchDelete(new List<long> { ownLedgerId, foreignLedgerId }));
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(countBefore, await verify.BaseTaxRefunds.AsNoTracking().CountAsync());

            var foreign = await verify.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == foreignLedgerId);
            Assert.False(foreign.IsDeleted);
            Assert.Equal(foreignAmountBefore, foreign.ExportAmount);
            Assert.Equal(foreignUpdatedBefore, foreign.UpdatedAt);

            var own = await verify.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == ownLedgerId);
            Assert.False(own.IsDeleted);
            Assert.Equal(700m, own.ExportAmount);
            Assert.Equal((long?)ownCustomerId, own.CustomerId);
        }
    }


    // ==================== 2. 身份 / 账号状态 / 菜单授权 fail closed ====================

    [Fact]
    public async Task Missing_disabled_deleted_and_revoked_identities_are_denied_without_touching_rows()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, menuId, seedLedgerId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var employee = await SeedEmployeeAsync(seed, tag);
            var (user, role) = await SeedOperatorAsync(seed, employee);
            userId = user.Id;
            roleId = role.Id;
            menuId = await seed.SysMenus.AsNoTracking()
                .Where(m => !m.IsDeleted && m.MenuCode == TaxRefundLedgerRules.RequiredMenuCode)
                .Select(m => m.Id).FirstAsync();
            seedLedgerId = (await SeedLedgerAsync(seed, $"TR-E442-MENU-{tag}", null, 100m)).Id;
        }

        int countBefore;
        await using (var db = _fixture.CreateDbContext())
            countBefore = await db.BaseTaxRefunds.AsNoTracking().CountAsync();

        // 无身份 / 账号不存在：未认证
        await using (var db = _fixture.CreateDbContext())
        {
            var missing = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, null).GetPaged(new PageQuery()));
            Assert.Equal(ErrorCodes.Unauthorized, missing.Code);

            var ghost = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, 987654L).Create(Ledger($"TR-E442-GHOST-{tag}", null, 10m)));
            Assert.Equal(ErrorCodes.Unauthorized, ghost.Code);
        }

        // 撤销既有菜单授权：权限不足，且读取 / 写入都不执行
        await using (var db = _fixture.CreateDbContext())
        {
            var grant = await db.SysRoleMenus.FirstAsync(
                rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted);
            grant.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var revoked = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, userId).GetPaged(new PageQuery()));
            Assert.Equal(ErrorCodes.Forbidden, revoked.Code);
            Assert.Contains(TaxRefundLedgerRules.RequiredMenuCode, revoked.Message);

            var revokedWrite = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, userId).Create(Ledger($"TR-E442-REV-{tag}", null, 10m)));
            Assert.Equal(ErrorCodes.Forbidden, revokedWrite.Code);
        }

        // 恢复授权但禁用账号：权限不足
        await using (var db = _fixture.CreateDbContext())
        {
            var grant = await db.SysRoleMenus.FirstAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId);
            grant.IsDeleted = false;
            var user = await db.SysUsers.FirstAsync(u => u.Id == userId);
            user.Status = UserStatus.Disabled;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var disabled = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, userId).GetPaged(new PageQuery()));
            Assert.Equal(ErrorCodes.Forbidden, disabled.Code);
        }

        // 已删除账号：未认证
        await using (var db = _fixture.CreateDbContext())
        {
            var user = await db.SysUsers.FirstAsync(u => u.Id == userId);
            user.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var deleted = await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, userId).GetPaged(new PageQuery()));
            Assert.Equal(ErrorCodes.Unauthorized, deleted.Code);

            // 特权账号仍可读历史无主行（保留既有访问）
            var admin = await SeedPrivilegedUserAsync(db, tag);
            var page = await PageAsync(NewController(db, admin.Id));
            Assert.Contains(page.Items, x => x.Id == seedLedgerId);

            // 全程未新增 / 未改写任何台账行
            Assert.Equal(countBefore, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
        }
    }


    // ==================== 3. 金额 / 税率 / 期间 / 币种 / 日期护栏（不静默截断 / 回填） ====================

    [Fact]
    public async Task Invalid_amounts_rates_period_currency_and_dates_are_rejected_without_mutation()
    {
        Guard();
        var tag = Tag();
        long ledgerId;
        decimal amountBefore;
        decimal rateBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var row = await SeedLedgerAsync(seed, $"TR-E442-VAL-{tag}", null, 1000m);
            ledgerId = row.Id;
            amountBefore = row.ExportAmount;
            rateBefore = row.RefundRate;
        }

        long adminId;
        int countBefore;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = (await SeedPrivilegedUserAsync(db, tag)).Id;
            countBefore = await db.BaseTaxRefunds.AsNoTracking().CountAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            // 新增：负出口金额 / 越界退税率 / 已退超过可退 / 空币种 / 空期间 / 到账早于申报
            await Assert.ThrowsAsync<BusinessException>(() => NewController(db, adminId)
                .Create(Ledger($"TR-E442-VN-{tag}", null, -1m)));
            await Assert.ThrowsAsync<BusinessException>(() => NewController(db, adminId)
                .Create(Ledger($"TR-E442-VR-{tag}", null, 1000m, refundRate: 101m)));
            await Assert.ThrowsAsync<BusinessException>(() => NewController(db, adminId)
                .Create(Ledger($"TR-E442-VC-{tag}", null, 1000m, 13m, 100m, 200m)));
            await Assert.ThrowsAsync<BusinessException>(() => NewController(db, adminId)
                .Create(Ledger($"TR-E442-VU-{tag}", null, 1000m, currency: " ")));
            await Assert.ThrowsAsync<BusinessException>(() => NewController(db, adminId)
                .Create(Ledger($"TR-E442-VP-{tag}", null, 1000m, period: "")));

            var badDates = Ledger($"TR-E442-VD-{tag}", null);
            badDates.DeclareDate = new DateTime(2026, 8, 20);
            badDates.RefundDate = new DateTime(2026, 8, 10);
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, adminId).Create(badDates));

            Assert.Equal(countBefore, await db.BaseTaxRefunds.AsNoTracking().CountAsync());

            // 修改：越界退税率被拒绝，原行金额与税率不变
            var badEdit = Ledger($"TR-E442-VAL-{tag}", null, 9999m, refundRate: 150m);
            badEdit.Id = ledgerId;
            await Assert.ThrowsAsync<BusinessException>(
                () => NewController(db, adminId).Update(ledgerId, badEdit));
        }

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(countBefore, await verify.BaseTaxRefunds.AsNoTracking().CountAsync());
            var stored = await verify.BaseTaxRefunds.AsNoTracking().SingleAsync(x => x.Id == ledgerId);
            Assert.Equal(amountBefore, stored.ExportAmount);
            Assert.Equal(rateBefore, stored.RefundRate);
            Assert.False(stored.IsDeleted);
        }
    }

}

/// <summary>
/// ERP-442 入口授权 SQL Server 集成测试的专用 localdb 目标 Fixture：把目标库初始化为完整 NEWERP 结构 + 种子数据
/// （含既有「出口退税台账」菜单），供真实 <see cref="TaxRefundController"/> 使用。
/// <para>任何库访问 / 建库之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> /
/// 集成安全），发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class TaxRefundLedgerAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP442";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-442] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

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

        Console.WriteLine("[ERP-442] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class TaxRefundLedgerTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => TaxRefundLedgerAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
