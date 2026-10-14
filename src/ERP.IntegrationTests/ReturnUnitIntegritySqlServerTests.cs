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
/// ERP-524 销售退货 / 采购退货单位完整性护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// 用与运行时完全相同的调用路径（真实 <see cref="SalesReturnController"/> / <see cref="PurchaseReturnController"/>
/// + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>手工未链接与显式链接来源草稿对「显式非空且不在商品当前基础 / 装箱单位内」的单位一律拒绝，
/// 且零单据、零明细、零库存流水、零单号消耗；</item>
/// <item>空 / 空白单位保持既有回退；大小写 / 首尾空白不敏感的基础 / 装箱单位放行，装箱单位恰好折算一次；</item>
/// <item>保存后商品单位主数据被改写（清空 / 改名）时，提交与审核在承诺边界内拒绝，零库存、零流水、状态不变；</item>
/// <item>链接来源按折算后的基础单位累计容量，审核不重复折算，销审释放容量。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，
/// 每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class ReturnUnitIntegritySqlServerTests : IClassFixture<ReturnUnitIntegritySqlServerFixture>
{
    private readonly ReturnUnitIntegritySqlServerFixture _fixture;

    public ReturnUnitIntegritySqlServerTests(ReturnUnitIntegritySqlServerFixture fixture) => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReturnUnitIntegritySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 显式越界单位：创建即拒绝（未链接与链接同口径） ====================

    [Theory]
    [InlineData("sales", "KG", false)]
    [InlineData("sales", "kg", false)]
    [InlineData("sales", " SET ", true)]
    [InlineData("purchase", "KG", false)]
    [InlineData("purchase", "工程量", true)]
    public async Task Create_rejects_explicit_unsupported_unit_before_any_write(string kind, string unit,
        bool linked)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        if (linked) await SeedSourceAsync(db, kind, master);
        var before = await SnapshotAsync(db);
        var actor = ControllerFor(db, kind, master);

        var error = await Assert.ThrowsAsync<BusinessException>(() => CreateAsync(actor, kind,
            linked ? master.SourceId : null, master.WarehouseId, (master.ProductId, 1m, unit)));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    // ==================== 2. 支持口径：空白回退 / 基础单位 / 装箱折算恰好一次 ====================

    [Theory]
    [InlineData("sales", "", 3.5, "", 3.5)]
    [InlineData("sales", "   ", 2, "   ", 2)]
    [InlineData("sales", "PCS", 2, "PCS", 2)]
    [InlineData("sales", " pcs ", 2, " pcs ", 2)]
    [InlineData("sales", "CTN", 2, "PCS", 24)]
    [InlineData("sales", "ctn", 2, "PCS", 24)]
    [InlineData("purchase", "", 3.5, "", 3.5)]
    [InlineData("purchase", "PCS", 2, "PCS", 2)]
    [InlineData("purchase", " Ctn ", 2, "PCS", 24)]
    public async Task Create_accepts_supported_units(string kind, string unit, decimal quantity,
        string expectedUnit, decimal expectedQuantity)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        var actor = ControllerFor(db, kind, master);

        var id = CreatedId(await CreateAsync(actor, kind, null, master.WarehouseId,
            (master.ProductId, quantity, unit)));

        var (storedUnit, storedQuantity, totalQuantity) = await FirstReturnDetailAsync(db, kind, id);
        Assert.Equal(expectedUnit, storedUnit);
        Assert.Equal(expectedQuantity, storedQuantity);
        Assert.Equal(expectedQuantity, totalQuantity);
    }

    // ==================== 3. 提交 / 审核承诺边界：主数据被改写后拒绝 ====================

    [Theory]
    [InlineData("sales", "cleared")]
    [InlineData("sales", "mutated")]
    [InlineData("purchase", "cleared")]
    [InlineData("purchase", "mutated")]
    public async Task Submit_and_approve_reject_when_master_unit_changed_after_draft(string kind, string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        if (kind == "purchase") await SeedStockAsync(db, master.WarehouseId, master.ProductId, 100m);
        var actor = ControllerFor(db, kind, master);

        var id = CreatedId(await CreateAsync(actor, kind, null, master.WarehouseId,
            (master.ProductId, 2m, "PCS")));

        if (scenario == "cleared")
            await SetProductUnitAsync(db, master.ProductId, string.Empty, string.Empty);
        else
            await SetProductUnitAsync(db, master.ProductId, "BOX", "CTN");

        var beforeSubmit = await SnapshotAsync(db);
        var submitError = await Assert.ThrowsAsync<BusinessException>(() => SubmitAsync(actor, kind, id));
        Assert.Equal(ErrorCodes.RuleConflict, submitError.Code);
        Assert.Contains("不在当前有效单位口径内", submitError.Message);
        Assert.Equal(DocumentStatus.Pending, await StatusAsync(db, kind, id));
        await AssertNoDocumentOrNumberChangeAsync(db, beforeSubmit);

        await SetDocumentStatusAsync(db, kind, id, DocumentStatus.Submitted);
        var beforeApprove = await SnapshotAsync(db);
        var approveError = await Assert.ThrowsAsync<BusinessException>(() => ApproveAsync(actor, kind, id));
        Assert.Equal(ErrorCodes.RuleConflict, approveError.Code);
        Assert.Contains("不在当前有效单位口径内", approveError.Message);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(db, kind, id));
        await AssertNoDocumentOrNumberChangeAsync(db, beforeApprove);
    }

    // ==================== 4. 链接来源：折算后按基础单位累计容量且不重复折算 ====================

    [Theory]
    [InlineData("sales")]
    [InlineData("purchase")]
    public async Task Linked_returns_use_converted_base_quantity_for_source_capacity_without_double_conversion(
        string kind)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        await SeedSourceAsync(db, kind, master);
        if (kind == "purchase") await SeedStockAsync(db, master.WarehouseId, master.ProductId, 100m);
        var actor = ControllerFor(db, kind, master);

        var firstId = CreatedId(await CreateAsync(actor, kind, master.SourceId, master.WarehouseId,
            (master.ProductId, 2m, "CTN")));
        await SubmitAsync(actor, kind, firstId);
        Assert.IsType<OkObjectResult>(await ApproveAsync(actor, kind, firstId));

        var (storedUnit, storedQuantity, _) = await FirstReturnDetailAsync(db, kind, firstId);
        Assert.Equal("PCS", storedUnit);
        Assert.Equal(24m, storedQuantity);                       // 2 CTN → 24 PCS，恰好一次

        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.SourceDocId == firstId
            && m.SourceDocType == (kind == "sales"
                ? InventoryDocumentHelper.SalesReturnType
                : InventoryDocumentHelper.PurchaseReturnType));
        Assert.Equal(24m, movement.Quantity);                    // 承诺边界不重复折算（不是 288）

        var firstStock = kind == "sales" ? 24m : 76m;
        Assert.Equal(firstStock, await StockQuantityAsync(db, master));

        // 第二张：来源授权 24 已被第一张占满，再退 1 CTN（12 PCS）在审核时按累计容量拒绝。
        var secondId = CreatedId(await CreateAsync(actor, kind, master.SourceId, master.WarehouseId,
            (master.ProductId, 1m, "CTN")));
        await SubmitAsync(actor, kind, secondId);
        var over = await Assert.ThrowsAsync<BusinessException>(() => ApproveAsync(actor, kind, secondId));
        Assert.Equal(ErrorCodes.RuleConflict, over.Code);
        Assert.Equal(firstStock, await StockQuantityAsync(db, master));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(db, kind, secondId));

        await UnauditAsync(actor, kind, firstId);                // 销审按红字流水冲销并释放可退容量
        var baseline = kind == "sales" ? 0m : 100m;
        Assert.Equal(baseline, await StockQuantityAsync(db, master));
        Assert.IsType<OkObjectResult>(await ApproveAsync(actor, kind, secondId));  // 1 CTN = 12 PCS
        // 销售退货入库（+12）/ 采购退货出库（−12），方向与既有记账口径一致。
        var expectedAfterSwap = kind == "sales" ? baseline + 12m : baseline - 12m;
        Assert.Equal(expectedAfterSwap, await StockQuantityAsync(db, master));
    }

    // ==================== 辅助 ====================

    /// <summary>本用例播种的真实主数据（一律使用 SQL Server 自增主键，不显式指定 Id）。</summary>
    private sealed class Master
    {
        public long WarehouseId { get; init; }
        public long ProductId { get; init; }
        public long CustomerId { get; init; }
        public long SupplierId { get; init; }
        public long OperatorId { get; init; }
        public long SourceId { get; set; }
    }

    /// <summary>真实已授权身份 + 两类退货控制器（销售 / 采购共用一套用例骨架，不绕过鉴权）。</summary>
    private sealed record DraftActor(string Kind, Master Master,
        SalesReturnController Sales, PurchaseReturnController Purchase);

    private static DraftActor ControllerFor(ErpDbContext db, string kind, Master master)
        => new(kind, master,
            new SalesReturnController(db, new DocumentNumberService(db), new InventoryService(db))
            {
                ControllerContext = Context(master.OperatorId)
            },
            new PurchaseReturnController(db, new DocumentNumberService(db), new InventoryService(db))
            {
                ControllerContext = Context(master.OperatorId)
            });

    private static ControllerContext Context(long userId)
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
            }
        };

    private static async Task<Master> SeedMasterAsync(ErpDbContext db, string kind)
    {
        var warehouseId = await SeedWarehouseAsync(db, $"退货仓-{Tag()}");
        var productId = await SeedProductAsync(db, $"退货商品-{Tag()}", "PCS", "CTN", 12);
        var customerId = kind == "sales" ? await SeedCustomerAsync(db, "退货客户") : 0L;
        var supplierId = kind == "purchase" ? await SeedSupplierAsync(db, "退货供应商") : 0L;
        var operatorId = await SeedOperatorAsync(db, kind, customerId == 0L ? null : customerId);
        await EnsureNumberRuleAsync(db, DocumentType.SalesReturn, "XTH");
        await EnsureNumberRuleAsync(db, DocumentType.PurchaseReturn, "CTH");

        return new Master
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OperatorId = operatorId
        };
    }

    private static async Task SeedSourceAsync(ErpDbContext db, string kind, Master master)
    {
        if (kind == "sales")
        {
            var shipment = await SeedShipmentAsync(db, master.CustomerId, master.WarehouseId,
                DocumentStatus.Approved, (master.ProductId, "PCS", 24m));
            master.SourceId = shipment.Id;
            return;
        }

        var receipt = await SeedReceiptAsync(db, master.SupplierId, master.WarehouseId,
            DocumentStatus.Approved, (master.ProductId, "PCS", 24m));
        master.SourceId = receipt.Id;
    }

    private static async Task<IActionResult> CreateAsync(DraftActor actor, string kind, long? sourceId,
        long warehouseId, params (long ProductId, decimal Quantity, string Unit)[] lines)
        => kind == "sales"
            ? await actor.Sales.Create(SalesDraft(actor, sourceId, warehouseId, lines))
            : await actor.Purchase.Create(PurchaseDraft(actor, sourceId, warehouseId, lines));

    private static async Task<IActionResult> SubmitAsync(DraftActor actor, string kind, long id)
        => kind == "sales" ? await actor.Sales.Submit(id) : await actor.Purchase.Submit(id);

    private static async Task<IActionResult> ApproveAsync(DraftActor actor, string kind, long id)
        => kind == "sales" ? await actor.Sales.Approve(id) : await actor.Purchase.Approve(id);

    private static async Task<IActionResult> UnauditAsync(DraftActor actor, string kind, long id)
        => kind == "sales" ? await actor.Sales.Unaudit(id) : await actor.Purchase.Unaudit(id);

    private static SalesReturn SalesDraft(DraftActor actor, long? sourceId, long warehouseId,
        params (long ProductId, decimal Quantity, string Unit)[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            CustomerId = actor.Master.CustomerId,
            CustomerName = "退货客户",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceId,
            SourceStockOutNo = string.Empty,
            ReturnReason = "质量",
            Remark = "ERP-524_SQL_TEST",
            Details = lines.Select(l => new SalesReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m,
                UnitCost = 5m
            }).ToList()
        };

    private static PurchaseReturn PurchaseDraft(DraftActor actor, long? sourceId, long warehouseId,
        params (long ProductId, decimal Quantity, string Unit)[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            SupplierId = actor.Master.SupplierId,
            SupplierName = "退货供应商",
            WarehouseId = warehouseId,
            SourceStockInId = sourceId,
            SourceStockInNo = string.Empty,
            ReturnReason = "规格不符",
            Remark = "ERP-524_SQL_TEST",
            Details = lines.Select(l => new PurchaseReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m,
                UnitCost = 5m
            }).ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static async Task<(string Unit, decimal Quantity, decimal TotalQuantity)> FirstReturnDetailAsync(
        ErpDbContext db, string kind, long id)
    {
        if (kind == "sales")
        {
            var detail = await db.SalesReturnDetails.AsNoTracking()
                .FirstAsync(d => d.SalesReturnId == id && !d.IsDeleted);
            var total = await db.SalesReturns.Where(r => r.Id == id).Select(r => r.TotalQuantity).SingleAsync();
            return (detail.Unit, detail.Quantity, total);
        }

        var purchaseDetail = await db.PurchaseReturnDetails.AsNoTracking()
            .FirstAsync(d => d.PurchaseReturnId == id && !d.IsDeleted);
        var purchaseTotal = await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.TotalQuantity)
            .SingleAsync();
        return (purchaseDetail.Unit, purchaseDetail.Quantity, purchaseTotal);
    }

    private static async Task<DocumentStatus> StatusAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturns.Where(r => r.Id == id).Select(r => r.Status).SingleAsync()
            : await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.Status).SingleAsync();

    private static async Task SetDocumentStatusAsync(ErpDbContext db, string kind, long id,
        DocumentStatus status)
    {
        if (kind == "sales")
        {
            var document = await db.SalesReturns.SingleAsync(r => r.Id == id);
            document.Status = status;
        }
        else
        {
            var document = await db.PurchaseReturns.SingleAsync(r => r.Id == id);
            document.Status = status;
        }

        await db.SaveChangesAsync();
    }

    private static async Task<decimal> StockQuantityAsync(ErpDbContext db, Master master)
        => await db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == master.WarehouseId && s.ProductId == master.ProductId)
            .Select(s => s.Quantity).SingleAsync();

    private static async Task SetProductUnitAsync(ErpDbContext db, long productId, string unit, string packageUnit)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Unit = unit;
        product.PackageUnit = packageUnit;
        await db.SaveChangesAsync();
    }

    private sealed record Snapshot(int SalesReturns, int SalesDetails, int PurchaseReturns,
        int PurchaseDetails, int Movements, int NumberRules, long? SalesSequence, long? PurchaseSequence);

    private static async Task<Snapshot> SnapshotAsync(ErpDbContext db)
        => new(
            await db.SalesReturns.CountAsync(),
            await db.SalesReturnDetails.CountAsync(),
            await db.PurchaseReturns.CountAsync(),
            await db.PurchaseReturnDetails.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.SysDocumentNumberRules.CountAsync(),
            await SequenceAsync(db, DocumentType.SalesReturn),
            await SequenceAsync(db, DocumentType.PurchaseReturn));

    /// <summary>拒绝场景的统一断言：单据 / 明细 / 流水 / 单号规则与单号流水相对快照逐项不变。</summary>
    private static async Task AssertNoDocumentOrNumberChangeAsync(ErpDbContext db, Snapshot before)
    {
        Assert.Equal(before.SalesReturns, await db.SalesReturns.CountAsync());
        Assert.Equal(before.SalesDetails, await db.SalesReturnDetails.CountAsync());
        Assert.Equal(before.PurchaseReturns, await db.PurchaseReturns.CountAsync());
        Assert.Equal(before.PurchaseDetails, await db.PurchaseReturnDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.SalesSequence, await SequenceAsync(db, DocumentType.SalesReturn));
        Assert.Equal(before.PurchaseSequence, await SequenceAsync(db, DocumentType.PurchaseReturn));
    }

    private static async Task<long?> SequenceAsync(ErpDbContext db, DocumentType documentType)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == documentType)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"SIU-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit, string packageUnit,
        int unitsPerPackage)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SIU-{Tag()}",
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage,
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer { CustomerCode = $"SIU-{Tag()}", CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = $"SIU-{Tag()}", SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = quantity * 6m,
            AverageCost = 6m
        });
        await db.SaveChangesAsync();
    }

    /// <summary>播种真实授权账号：既有菜单（销售退货 / 采购退货）+ 角色授权 + 启用账号（销售侧含客户数据范围）。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, string kind, long? customerId)
    {
        var isSales = kind == "sales";
        var menuCode = isSales ? SalesReturnSourceRules.RequiredMenuCode : PurchaseReturnSourceRules.RequiredMenuCode;
        var menuText = isSales ? SalesReturnSourceRules.RequiredMenuText : PurchaseReturnSourceRules.RequiredMenuText;

        var role = new SysRole { RoleCode = $"SIU-{Tag()}", RoleName = isSales ? "销售退货操作员" : "采购退货操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"siu-{Tag()}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = role.RoleName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = role.RoleName,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        if (customerId.HasValue)
        {
            var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId.Value);
            customer.EmpId = employee.Id;
        }

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuCode = menuCode,
                MenuName = menuText,
                Path = isSales ? "/logistics/sales-return" : "/logistics/purchase-return",
                Icon = isSales ? "package-minus" : "package-plus",
                SortOrder = 60,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }

    /// <summary>播种已审核的来源销售出库单（已审核、同客户同仓库、商品基础单位证据充足）。</summary>
    private static async Task<StockOut> SeedShipmentAsync(ErpDbContext db, long customerId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            StockOutNo = $"CK-SIU-{Tag()}",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockOuts.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    /// <summary>播种已审核的来源采购入库单（已审核、同供应商同仓库、商品基础单位证据充足）。</summary>
    private static async Task<StockIn> SeedReceiptAsync(ErpDbContext db, long supplierId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            StockInNo = $"RK-SIU-{Tag()}",
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockInDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockIns.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task EnsureNumberRuleAsync(ErpDbContext db, DocumentType documentType, string prefix)
    {
        if (await db.SysDocumentNumberRules.AnyAsync(r => r.DocumentType == documentType && !r.IsDeleted))
            return;

        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = documentType,
            RuleCode = $"{prefix}-524",
            RuleName = documentType == DocumentType.SalesReturn ? "销售退货单" : "采购退货单",
            Prefix = prefix,
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 1000
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-524）：全新 GUID 后缀库 → 完整 NEWERP 结构 + 种子数据。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 也绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReturnUnitIntegritySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RETURNUNIT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-524] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        // 破坏性初始化前再次护栏：绝不使用生产回退，也绝不复用已存在的库。
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

        Console.WriteLine("[ERP-524] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ReturnUnitIntegrityTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;User Id=sa;Password=pw")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ReturnUnitIntegritySqlServerFixture.AssertDedicatedTarget(connection));
}
