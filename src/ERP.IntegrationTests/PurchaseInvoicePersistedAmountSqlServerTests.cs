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
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-510 供应商采购发票「标量金额与登记算术存储边界」的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>实际列精度：<c>PurchaseInvoice.NetAmount / TaxAmount / GrossAmount</c> 与
/// <c>PurchaseInvoiceAllocation.AllocatedAmount</c> 的真实 <c>INFORMATION_SCHEMA</c> 列为 <c>decimal(18,2)</c>，
/// 与 <see cref="FinancePersistedAmountRules"/> 常量同源；恰好等于上限的金额原样往返，
/// 超过上限的金额连原始 EF 写入都被真实列拒绝；</item>
/// <item>创建 / 修改：受权账号提交「取整进位越过上限 / 1e16 / 极大十进制」一律在**任何表头写入之前**被受控拒绝
/// （受权同一控制器随后合法创建成功，证明拒绝原因是金额而非授权）；</item>
/// <item>保存关联：越界关联金额被拒绝且零关联行写入；</item>
/// <item>登记：锁内复核**已持久化**金额（等式不一致 / 未按币种取整的历史快照被拒且状态 / 字段不变），
/// 合法边界金额照常「创建 → 关联 → 登记」；小数位与零小数币种取整原样落库。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseInvoicePersistedAmountSqlServerTests
    : IClassFixture<PurchaseInvoicePersistedAmountSqlServerFixture>
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）

    private readonly PurchaseInvoicePersistedAmountSqlServerFixture _fixture;

    public PurchaseInvoicePersistedAmountSqlServerTests(PurchaseInvoicePersistedAmountSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseInvoicePersistedAmountSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 实际列精度 + 边界值原样往返 + 真实列拒绝越界 ====================

    [Fact]
    public async Task Actual_column_precision_matches_rules_and_boundary_values_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);
        Assert.Equal("decimal(18,2)", ColumnType(typeof(PurchaseInvoice), nameof(PurchaseInvoice.NetAmount)));
        Assert.Equal("decimal(18,2)", ColumnType(typeof(PurchaseInvoice), nameof(PurchaseInvoice.TaxAmount)));
        Assert.Equal("decimal(18,2)", ColumnType(typeof(PurchaseInvoice), nameof(PurchaseInvoice.GrossAmount)));
        Assert.Equal("decimal(18,2)", ColumnType(
            typeof(PurchaseInvoiceAllocation), nameof(PurchaseInvoiceAllocation.AllocatedAmount)));

        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "PurchaseInvoices", "NetAmount"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "PurchaseInvoices", "TaxAmount"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "PurchaseInvoices", "GrossAmount"));
        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "PurchaseInvoiceAllocations", "AllocatedAmount"));

        var supplierId = await SeedSupplierAsync(db, "发票精度边界供应商");
        var invoice = await SeedInvoiceAsync(db, $"PI-{Tag()}", supplierId, MaxAmount, 0m, MaxAmount);

        var reloaded = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(MaxAmount, reloaded.NetAmount);
        Assert.Equal(0m, reloaded.TaxAmount);
        Assert.Equal(MaxAmount, reloaded.GrossAmount);

        // 超过上限的原始 EF 写入被真实列 / 驱动拒绝（绝非应用层提前拦截）。
        db.PurchaseInvoices.Add(new PurchaseInvoice
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = $"RAW-{Tag()}",
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = $"RAW{Tag()}",
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierCode = "RAW",
            SupplierName = "原始越界",
            Currency = "CNY",
            NetAmount = OneE16,
            TaxAmount = 0m,
            GrossAmount = OneE16
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertProviderRejected(ex);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(1, await verify.PurchaseInvoices.CountAsync(i => i.SupplierId == supplierId));
        }
    }

    /// <summary>
    /// 异常链中必须出现提供程序层面的拒绝：SQL Server 算术溢出（<see cref="SqlException"/>）或
    /// SqlClient 客户端参数范围校验（<see cref="ArgumentException"/>）；绝不能是应用层提前拦截。
    /// </summary>
    private static void AssertProviderRejected(Exception exception)
    {
        var chain = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
            if (current is SqlException or ArgumentException) return;
        }
        Assert.Fail("expected the provider to reject the over-limit scalar amount: " + string.Join(" -> ", chain));
    }

    [Fact]
    public async Task Sql_server_itself_cannot_convert_over_limit_scalar_amount_to_the_column_type()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        // 真实列精度 decimal(18,2)：SQL Server 对越界标量报「算术溢出」（错误号 8115，与语言无关），
        // 证明应用层单列边界（FinancePersistedAmountRules）与真实存储边界逐分一致。
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            db.Database.SqlQueryRaw<decimal>("SELECT CAST(10000000000000000.00 AS decimal(18,2)) AS [Value]")
                .ToListAsync());
        Assert.Equal(8115, ex.Number);
    }

    // ==================== 2. 创建 / 修改：越界拒绝且零写入 ====================

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("1e16")]
    [InlineData("huge-decimal")]
    public async Task Create_invoice_rejects_unpersistable_amount_without_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票创建越界供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票创建越界操作员");
        var controller = NewController(db, userId);

        var before = await db.PurchaseInvoices.CountAsync(i => i.SupplierId == supplierId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            InvoiceDraft(supplierId, $"REJ-{Tag()}", OverLimit(scenario), 0m, OverLimit(scenario))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(before, await verify.PurchaseInvoices.CountAsync(i => i.SupplierId == supplierId));
        }

        // 同一受权控制器随后合法创建成功：证明拒绝原因是金额而非授权。
        var id = CreatedId(await controller.Create(
            InvoiceDraft(supplierId, $"OK-{Tag()}", 100m, 13m, 113m)));
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.True(id > 0);
            Assert.Equal(before + 1, await verify.PurchaseInvoices.CountAsync(i => i.SupplierId == supplierId));
        }
    }

    [Fact]
    public async Task Update_invoice_rejects_unpersistable_amount_and_leaves_stored_invoice_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票修改越界供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票修改越界操作员");
        var controller = NewController(db, userId);
        var number = $"UPD-{Tag()}";
        var id = CreatedId(await controller.Create(InvoiceDraft(supplierId, number, 1000m, 0m, 1000m)));
        var before = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == id);

        var request = InvoiceDraft(supplierId, number, OneE16, 0m, OneE16);
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == id);
        Assert.Equal(before.NetAmount, after.NetAmount);
        Assert.Equal(before.GrossAmount, after.GrossAmount);
        Assert.Equal(before.InvoiceNumber, after.InvoiceNumber);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // ==================== 3. 保存关联 / 取整口径与合法登记生命周期（真实 SQL） ====================

    [Fact]
    public async Task Save_allocations_rejects_unpersistable_amount_and_writes_no_allocation_rows()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票关联越界供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票关联越界操作员");
        var controller = NewController(db, userId);
        var id = CreatedId(await controller.Create(InvoiceDraft(supplierId, $"ALLOC-{Tag()}", 100m, 13m, 113m)));
        var order = await SeedOrderAsync(db, $"PO-{Tag()}", supplierId, MaxAmount);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.SaveAllocations(id, Lines((order.Id, OneE16))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.PurchaseInvoiceAllocations.CountAsync(a => a.PurchaseInvoiceId == id));
        }
    }

    [Fact]
    public async Task Create_invoice_rounds_fractional_amount_and_persists_exact_boundary()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票取整供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票取整操作员");
        var controller = NewController(db, userId);

        // CNY：100.4 / 0.1 / 100.5 按 2 位取整后等式成立（100.40 + 0.10 = 100.50）。
        var cnyId = CreatedId(await controller.Create(
            InvoiceDraft(supplierId, $"RND-{Tag()}", 100.4m, 0.1m, 100.5m)));
        var cny = await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == cnyId);
        Assert.Equal(100.40m, cny.NetAmount);
        Assert.Equal(0.10m, cny.TaxAmount);
        Assert.Equal(100.50m, cny.GrossAmount);

        // JPY：0 位小数，整数金额合法。
        var jpyId = CreatedId(await controller.Create(
            InvoiceDraft(supplierId, $"RNDJ-{Tag()}", 1000m, 100m, 1100m, "JPY")));
        Assert.Equal(1100m, (await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == jpyId)).GrossAmount);

        // 恰好等于存储上限（零税额）。
        var maxId = CreatedId(await controller.Create(
            InvoiceDraft(supplierId, $"RNDM-{Tag()}", MaxAmount, 0m, MaxAmount)));
        Assert.Equal(MaxAmount, (await db.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == maxId)).GrossAmount);

        // 零小数币种取整进位越过上限：受控拒绝、零写入。
        var before = await db.PurchaseInvoices.CountAsync(i => i.SupplierId == supplierId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            InvoiceDraft(supplierId, $"RNDX-{Tag()}", 9_999_999_999_999_999.6m, 0m, 9_999_999_999_999_999.6m, "JPY")));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(before, await verify.PurchaseInvoices.CountAsync(i => i.SupplierId == supplierId));
        }
    }

    [Fact]
    public async Task Valid_invoice_lifecycle_create_allocate_record_persists_boundary_amount()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票合法流转供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票合法流转操作员");
        var controller = NewController(db, userId);

        var id = CreatedId(await controller.Create(
            InvoiceDraft(supplierId, $"FLOW-{Tag()}", MaxAmount, 0m, MaxAmount)));
        var order = await SeedOrderAsync(db, $"PO-{Tag()}", supplierId, MaxAmount);
        Assert.IsType<OkObjectResult>(await controller.SaveAllocations(id, Lines((order.Id, MaxAmount))));
        Assert.IsType<OkObjectResult>(await controller.Record(id));

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == id);
            Assert.Equal(PurchaseInvoiceRules.StatusRecorded, stored.Status);
            Assert.Equal(MaxAmount, stored.NetAmount);
            Assert.Equal(MaxAmount, stored.GrossAmount);
            var allocation = await verify.PurchaseInvoiceAllocations.AsNoTracking()
                .SingleAsync(a => a.PurchaseInvoiceId == id);
            Assert.Equal(MaxAmount, allocation.AllocatedAmount);
        }
    }

    // ==================== 4. 登记：锁内复核已持久化金额（真实 SQL，撤销 / 读取不新增闸门） ====================

    [Fact]
    public async Task Record_rejects_persisted_inconsistent_amounts_and_leaves_snapshot_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票登记复核供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票登记复核操作员");
        var controller = NewController(db, userId);

        // 真实列可存储、但等式不一致的历史快照。
        var equationBroken = await SeedInvoiceAsync(db, $"EQ-{Tag()}", supplierId, 100m, 1m, 100m);
        // 真实列可存储、但未按币种精度取整的历史快照（JPY 存了小数）。
        var unrounded = await SeedInvoiceAsync(db, $"JPY-{Tag()}", supplierId, 100.5m, 0m, 100.5m, "JPY");
        var before = await db.PurchaseInvoices.AsNoTracking()
            .SingleAsync(i => i.Id == equationBroken.Id);

        var eqEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Record(equationBroken.Id));
        var jpyEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Record(unrounded.Id));
        Assert.Equal(ErrorCodes.RuleConflict, eqEx.Code);
        Assert.Equal(ErrorCodes.RuleConflict, jpyEx.Code);

        // 拒绝发生在改状态之前：状态 / 金额 / 单号 / 审计逐字段不变，绝不静默改写快照。
        await using (var verify = _fixture.CreateDbContext())
        {
            var after = await verify.PurchaseInvoices.AsNoTracking()
                .SingleAsync(i => i.Id == equationBroken.Id);
            Assert.Equal(PurchaseInvoiceRules.StatusDraft, after.Status);
            Assert.Equal(before.NetAmount, after.NetAmount);
            Assert.Equal(before.TaxAmount, after.TaxAmount);
            Assert.Equal(before.GrossAmount, after.GrossAmount);
            Assert.Equal(before.InvoiceNumber, after.InvoiceNumber);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Null(after.RecordedAt);
        }

        // 读取（历史证据照常可读）与作废（既有撤销语义）仍可用。
        Assert.IsType<OkObjectResult>(await controller.GetById(equationBroken.Id));
        Assert.IsType<OkObjectResult>(await controller.Void(
            equationBroken.Id, new PurchaseInvoiceVoidRequest { Reason = "历史快照更正" }));
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(PurchaseInvoiceRules.StatusVoided,
                (await verify.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == equationBroken.Id)).Status);
        }
    }

    [Fact]
    public async Task Record_persists_valid_boundary_amount()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "发票登记边界供应商");
        var userId = await SeedUserAsync(db, PurchaseInvoiceAuthorizationRules.RequiredMenuCode, "发票登记边界操作员");
        var controller = NewController(db, userId);
        var invoice = await SeedInvoiceAsync(db, $"MAX-{Tag()}", supplierId, MaxAmount, 0m, MaxAmount);

        Assert.IsType<OkObjectResult>(await controller.Record(invoice.Id));

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.PurchaseInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
            Assert.Equal(PurchaseInvoiceRules.StatusRecorded, stored.Status);
            Assert.Equal(MaxAmount, stored.NetAmount);
            Assert.NotNull(stored.RecordedAt);
        }
    }

    // ==================== 5. 真实 SQL 夹具种子与脚手架 ====================

    private static decimal OverLimit(string scenario) => scenario switch
    {
        "round-up-cross" => RoundUpCrossing,
        "1e16" => OneE16,
        _ => decimal.MaxValue
    };

    private static PurchaseInvoiceController NewController(ErpDbContext db, long? userId)
    {
        var controller = new PurchaseInvoiceController(db);
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<PurchaseInvoiceDto>>(ok.Value);
        return resp.Data!.Id;
    }

    private static PurchaseInvoiceSaveDto InvoiceDraft(
        long supplierId, string number, decimal net, decimal tax, decimal gross, string currency = "CNY") => new()
    {
        InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
        InvoiceCode = null,
        InvoiceNumber = number,
        InvoiceDate = DateTime.Today,
        SupplierId = supplierId,
        Currency = currency,
        NetAmount = net,
        TaxAmount = tax,
        GrossAmount = gross,
        DueDate = null,
        PaymentTerms = string.Empty,
        Remark = "ERP-510_SQL_TEST"
    };

    private static PurchaseInvoiceAllocationSaveRequest Lines(params (long OrderId, decimal Amount)[] lines)
        => new()
        {
            Lines = lines.Select(l => new PurchaseInvoiceAllocationSaveDto
            {
                PurchaseOrderId = l.OrderId,
                AllocatedAmount = l.Amount
            }).ToList()
        };

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-{Tag()}", SupplierName = name, Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<PurchaseOrder> SeedOrderAsync(
        ErpDbContext db, string orderNo, long supplierId, decimal totalAmount)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<PurchaseInvoice> SeedInvoiceAsync(
        ErpDbContext db, string number, long supplierId, decimal net, decimal tax, decimal gross,
        string currency = "CNY", int status = PurchaseInvoiceRules.StatusDraft)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierCode = "S-SQL",
            SupplierName = "供应商-SQL",
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            Status = status,
            UpdatedAt = new DateTime(2026, 10, 1, 8, 0, 0)
        };
        db.PurchaseInvoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    /// <summary>播种真实授权账号：既有「采购订单」（purchase-order）菜单授权 + 启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string menuCode, string displayName)
    {
        var role = new SysRole { RoleCode = $"PIAMT-{Tag()}", RoleName = displayName, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"piamt-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = displayName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static string ColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=(localdb)\\NEWERP_AutoAcceptance;" +
                          "Database=NEWERP_AUTOTEST_ERP510_MODEL_ONLY;Integrated Security=true;" +
                          "TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>），证明实际存储精度与规则常量同源。</summary>
    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-510）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class PurchaseInvoicePersistedAmountSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PURCHASEINVOICEAMOUNT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-510] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        // 破坏性重置前再次护栏：绝不使用生产回退。
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

        Console.WriteLine("[ERP-510] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseInvoicePersistedAmountTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            PurchaseInvoicePersistedAmountSqlServerFixture.AssertDedicatedTarget(connection));
}
