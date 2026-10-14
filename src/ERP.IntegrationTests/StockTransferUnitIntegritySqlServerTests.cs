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
/// ERP-525 仓库调拨单位完整性的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockTransferController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建 / 修改对「显式非空但不是商品当前基础单位」的明细单位（装箱单位 / 任意单位 / 大小写与空白变体）
/// 一律拒绝，且零单据、零明细、零库存流水、零单号消耗；</item>
/// <item>空 / 空白单位保持既有回退，基础单位（大小写、首尾空白）受理且数量 / 成本原样不折算；</item>
/// <item>商品未维护基础单位时，显式非空单位受控拒绝（绝不臆造新单位）；</item>
/// <item>提交 / 审核在既有单据 / 库存锁内用当前商品快照复核已存单位：主数据基础单位被改写即整体拒绝，
/// 状态 / 明细 / 库存余额 / 流水 / 单号逐项不变；直接落库的历史越界单位草稿同样被审核拒绝且零记账、仍可读；</item>
/// <item>合法基础单位 / 空单位审核恰好一次记账（两仓数量与金额守恒、单位归一），重复审核被拒绝，销审仍可冲销还原。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockTransferUnitIntegritySqlServerTests
    : IClassFixture<StockTransferUnitIntegritySqlServerFixture>
{
    private readonly StockTransferUnitIntegritySqlServerFixture _fixture;

    public StockTransferUnitIntegritySqlServerTests(StockTransferUnitIntegritySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockTransferUnitIntegritySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 创建：非基础单位一律拒绝（零写入 / 零单号） ====================

    [Theory]
    [InlineData("CTN")]
    [InlineData(" ctn ")]
    [InlineData("KG")]
    [InlineData("SET")]
    [InlineData("工程量")]
    public async Task Create_rejects_non_base_units_before_any_write(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(fromId, toId, (productId, "箱装商品", unit, 12m, 5m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        await AssertNoDocumentOrNumberChangeAsync(db, before);
        Assert.Equal(0, await db.StockTransferDetails.CountAsync(d => d.ProductId == productId));
    }

    [Fact]
    public async Task Create_rejects_nonempty_unit_when_base_unit_is_missing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "缺基础单位商品", unit: "", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(fromId, toId, (productId, "缺基础单位商品", "PCS", 1m, 1m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置", error.Message);
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    // ==================== 2. 创建：空 / 基础单位受理且不折算 ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pcs")]
    [InlineData(" PCS ")]
    public async Task Create_accepts_blank_and_base_units_without_conversion(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var before = await SnapshotAsync(db);

        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, "箱装商品", unit, 12m, 5m))));

        var transfer = await db.StockTransfers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(DocumentStatus.Pending, transfer.Status);
        Assert.Equal(12m, transfer.TotalQuantity);          // 不做装箱折算
        Assert.Equal(60m, transfer.TotalAmount);

        var detail = await db.StockTransferDetails.AsNoTracking().SingleAsync(d => d.StockTransferId == id);
        Assert.Equal(unit, detail.Unit);
        Assert.Equal(12m, detail.Quantity);
        Assert.Equal(5m, detail.UnitCost);
        Assert.Equal(60m, detail.Amount);

        Assert.Equal((before.RuleSequence ?? 0L) + 1L, await CurrentTransferSequenceAsync(db));  // 受理恰好消耗一个单号
        Assert.Equal(0, await db.StockMovements.CountAsync());
    }

    // ==================== 3. 修改：越界单位在明细整体替换之前收敛 ====================

    [Fact]
    public async Task Update_rejects_non_base_unit_and_keeps_existing_rows()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));

        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, "箱装商品", "PCS", 5m, 2m))));
        var transferNo = await db.StockTransfers.Where(t => t.Id == id).Select(t => t.TransferNo).SingleAsync();
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id,
            Draft(fromId, toId, (productId, "箱装商品", "CTN", 9m, 2m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        var after = await db.StockTransfers.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(transferNo, after.TransferNo);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(5m, after.TotalQuantity);
        Assert.Equal(10m, after.TotalAmount);

        var detail = await db.StockTransferDetails.AsNoTracking().SingleAsync(d => d.StockTransferId == id);
        Assert.Equal("PCS", detail.Unit);                      // 明细未被整体替换
        Assert.Equal(5m, detail.Quantity);
        Assert.Equal(2m, detail.UnitCost);

        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.RuleSequence, await CurrentTransferSequenceAsync(db));
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
    }

    // ==================== 4. 提交 / 审核：锁内复核已存单位 ====================

    [Fact]
    public async Task Submit_recheck_rejects_after_live_base_unit_edit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, "箱装商品", "PCS", 6m, 2m))));

        await SetProductUnitsAsync(db, productId, unit: "BOX", packageUnit: "CTN", factor: 12);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Pending,
            await db.StockTransfers.Where(t => t.Id == id).Select(t => t.Status).SingleAsync());
        Assert.Equal("PCS",
            await db.StockTransferDetails.Where(d => d.StockTransferId == id).Select(d => d.Unit).SingleAsync());
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    [Fact]
    public async Task Approve_recheck_rejects_after_live_base_unit_edit_and_changes_nothing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, fromId, productId, quantity: 100m, totalCost: 1000m);

        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, "箱装商品", "PCS", 30m, 0m))));
        await controller.Submit(id);
        await SetProductUnitsAsync(db, productId, unit: "BOX", packageUnit: "CTN", factor: 12);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Equal(DocumentStatus.Submitted,
            await db.StockTransfers.Where(t => t.Id == id).Select(t => t.Status).SingleAsync());

        var from = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == fromId && s.ProductId == productId);
        Assert.Equal(100m, from.Quantity);
        Assert.Equal(1000m, from.TotalCost);
        Assert.Equal(0, await db.Stocks.CountAsync(s => s.WarehouseId == toId));     // 调入仓未被创建
        await AssertNoDocumentOrNumberChangeAsync(db, before);

        var detail = await db.StockTransferDetails.AsNoTracking().SingleAsync(d => d.StockTransferId == id);
        Assert.Equal("PCS", detail.Unit);                     // 未被 product.Unit 静默覆盖
        Assert.Equal(30m, detail.Quantity);
        Assert.Equal(0m, detail.UnitCost);
    }

    [Fact]
    public async Task Legacy_submitted_draft_with_foreign_unit_is_rejected_and_stays_readable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, fromId, productId, quantity: 100m, totalCost: 1000m);
        var id = await SeedSubmittedTransferAsync(db, fromId, toId, productId, unit: "CTN", quantity: 12m,
            unitCost: 5m);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Submitted,
            await db.StockTransfers.Where(t => t.Id == id).Select(t => t.Status).SingleAsync());
        Assert.Equal(100m, await db.Stocks.Where(s => s.WarehouseId == fromId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync());
        await AssertNoDocumentOrNumberChangeAsync(db, before);

        // 历史读取不新增资格闸门：已存单位原样可读。
        var read = await controller.GetById(id);
        var payload = Assert.IsType<ApiResponse<StockTransfer>>(
            Assert.IsType<OkObjectResult>(read).Value).Data!;
        Assert.Equal("CTN", payload.Details.Single().Unit);
    }

    // ==================== 5. 合法审核恰好一次 + 销审回归 ====================

    [Theory]
    [InlineData("PCS")]
    [InlineData(" pcs ")]
    [InlineData("")]
    public async Task Approve_posts_once_and_conserves_quantity_and_cost(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, fromId, productId, quantity: 100m, totalCost: 1000m);

        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, "箱装商品", unit, 30m, 0m))));
        await controller.Submit(id);
        await controller.Approve(id);

        var from = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == fromId && s.ProductId == productId);
        var to = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == toId && s.ProductId == productId);
        Assert.Equal(70m, from.Quantity);
        Assert.Equal(700m, from.TotalCost);
        Assert.Equal(30m, to.Quantity);
        Assert.Equal(300m, to.TotalCost);
        Assert.Equal(100m, from.Quantity + to.Quantity);            // 数量守恒
        Assert.Equal(1000m, from.TotalCost + to.TotalCost);         // 金额守恒
        Assert.Equal(10m, to.AverageCost);                          // 两侧同一成本单价
        Assert.Equal(DocumentStatus.Approved,
            await db.StockTransfers.Where(t => t.Id == id).Select(t => t.Status).SingleAsync());
        Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == id));

        var detail = await db.StockTransferDetails.AsNoTracking().SingleAsync(d => d.StockTransferId == id);
        Assert.True(StockTransferPostingRules.IsBaseUnitEligible(detail.Unit, "PCS"));   // 落库单位仍是基础单位口径
        if (string.IsNullOrWhiteSpace(unit))
            Assert.Equal("PCS", detail.Unit);                     // 空单位按商品基础单位兜底
        else
            Assert.Equal(unit, detail.Unit);                      // 非空已校验单位保持原样
        Assert.Equal(30m, detail.Quantity);                       // 数量未被改写
        Assert.Equal(10m, detail.UnitCost);

        // 并发 / 幂等回归：重复审核被拒绝且不新增流水。
        var again = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == id));
    }

    [Fact]
    public async Task Unaudit_still_reverses_after_live_base_unit_edit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var fromId = await SeedWarehouseAsync(db, "调出仓");
        var toId = await SeedWarehouseAsync(db, "调入仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, fromId, productId, quantity: 100m, totalCost: 1000m);

        var id = CreatedId(await controller.Create(Draft(fromId, toId, (productId, "箱装商品", "PCS", 40m, 10m))));
        await controller.Submit(id);
        await controller.Approve(id);
        await SetProductUnitsAsync(db, productId, unit: "BOX", packageUnit: "CTN", factor: 12);

        await controller.Unaudit(id);

        var from = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == fromId && s.ProductId == productId);
        var to = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == toId && s.ProductId == productId);
        Assert.Equal(100m, from.Quantity);
        Assert.Equal(1000m, from.TotalCost);
        Assert.Equal(0m, to.Quantity);
        Assert.Equal(0m, to.TotalCost);
        Assert.Equal(4, await db.StockMovements.CountAsync(m => m.SourceDocId == id));     // 2 原流水 + 2 红字
        Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == id && m.IsReversal));
        Assert.Equal(DocumentStatus.Pending,
            await db.StockTransfers.Where(t => t.Id == id).Select(t => t.Status).SingleAsync());
    }

    // ==================== 测试辅助 ====================

    private static StockTransferController CreateController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };

    private static StockTransfer Draft(long fromWarehouseId, long toWarehouseId,
        params (long ProductId, string Name, string Unit, decimal Quantity, decimal UnitCost)[] lines)
        => new()
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Remark = "ERP-525_SQL_TEST",
            Details = lines.Select(l => new StockTransferDetail
            {
                ProductId = l.ProductId,
                ProductName = l.Name,
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitCost = l.UnitCost
            }).ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    /// <summary>调用前基线快照（同一夹具库被多个用例复用，因此拒绝断言一律相对快照比较）。</summary>
    private static async Task<DraftSnapshot> SnapshotAsync(ErpDbContext db)
        => new(
            await db.StockTransfers.CountAsync(),
            await db.StockTransferDetails.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.SysDocumentNumberRules.CountAsync(),
            await CurrentTransferSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：单据 / 明细 / 流水 / 单号规则与调拨流水号相对快照逐项不变。</summary>
    private static async Task AssertNoDocumentOrNumberChangeAsync(ErpDbContext db, DraftSnapshot before)
    {
        Assert.Equal(before.Transfers, await db.StockTransfers.CountAsync());
        Assert.Equal(before.Details, await db.StockTransferDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.RuleSequence, await CurrentTransferSequenceAsync(db));   // 拒绝方绝不消耗单据号
    }

    private sealed record DraftSnapshot(int Transfers, int Details, int Movements, int NumberRules,
        long? RuleSequence);

    private static async Task<long?> CurrentTransferSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockTransfer)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"STUW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit,
        string packageUnit, int factor)
    {
        var product = new BaseProduct
        {
            ProductCode = $"STUP-{Tag()}",
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = factor,
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task SetProductUnitsAsync(ErpDbContext db, long productId, string unit,
        string packageUnit, int factor)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Unit = unit;
        product.PackageUnit = packageUnit;
        product.UnitsPerPackage = factor;
        await db.SaveChangesAsync();
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId,
        decimal quantity, decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        await db.SaveChangesAsync();
    }

    /// <summary>直接落库一张「已提交」的历史调拨单（绕过控制器），用于验证审核期复核与历史读取。</summary>
    private static async Task<long> SeedSubmittedTransferAsync(ErpDbContext db, long fromWarehouseId,
        long toWarehouseId, long productId, string unit, decimal quantity, decimal unitCost)
    {
        var transfer = new StockTransfer
        {
            TransferNo = $"ST-U-{Tag()}",
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Status = DocumentStatus.Submitted,
            TotalQuantity = quantity,
            TotalAmount = quantity * unitCost
        };
        db.StockTransfers.Add(transfer);
        await db.SaveChangesAsync();

        db.StockTransferDetails.Add(new StockTransferDetail
        {
            StockTransferId = transfer.Id,
            TransferNo = transfer.TransferNo,
            SortNo = 1,
            ProductId = productId,
            ProductName = "历史商品",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitCost = unitCost,
            Amount = quantity * unitCost
        });
        await db.SaveChangesAsync();
        return transfer.Id;
    }

    /// <summary>播种真实授权账号：既有 stock-query 菜单（种子数据已含）+ 角色授权 + 启用账号。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db)
    {
        var role = new SysRole { RoleCode = $"ST-UNIT-{Tag()}", RoleName = "调拨单位集成操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"st-unit-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "调拨单位集成操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m =>
            m.MenuCode == StockTransferPostingRules.RequiredMenuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-525）：把目标库重置为完整 NEWERP 结构 + 种子数据，供调拨单位完整性集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockTransferUnitIntegritySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKTRANSFERUNIT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-525] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await ResetToFullDatabaseAsync();
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

    private async Task ResetToFullDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

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

        Console.WriteLine("[ERP-525] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝（ERP-525）。</summary>
public sealed class StockTransferUnitIntegrityTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockTransferUnitIntegritySqlServerFixture.AssertDedicatedTarget(connection));
}
