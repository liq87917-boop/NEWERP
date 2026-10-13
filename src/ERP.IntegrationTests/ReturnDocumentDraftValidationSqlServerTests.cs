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
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-503 退货草稿写入护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// 用与运行时完全相同的调用路径（真实 <see cref="SalesReturnController"/> / <see cref="PurchaseReturnController"/>
/// + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建对「仓库 Id 非正 / 不存在 / 已软删除 / 已停用」与「数量非正或不可表示、单价为负或不可表示、
/// 成本为负或不可表示、商品缺失 / 不存在 / 已删除 / 已停用、行金额与合计不可表示」一律拒绝，
/// 且零单据、零明细、零库存流水、零单号消耗（<b>链接与未链接来源同口径</b>）；</item>
/// <item>修改在明细整体替换之前收敛同样的非法输入，既有单据与明细逐字段不变、不消耗新单号；</item>
/// <item>空草稿头与重复商品行保持既有语义（空头可保存但提交 / 审核要求有效明细）；</item>
/// <item>合法草稿（链接 / 未链接）仍可提交审核并恰好一次写入库存台账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，
/// 绝不 drop / reset 任何数据库。</para>
/// </summary>
public sealed class ReturnDocumentDraftValidationSqlServerTests
    : IClassFixture<ReturnDocumentDraftValidationSqlServerFixture>
{
    private readonly ReturnDocumentDraftValidationSqlServerFixture _fixture;

    public ReturnDocumentDraftValidationSqlServerTests(ReturnDocumentDraftValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReturnDocumentDraftValidationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 创建：非法仓库（零写入 / 零单号，链接与未链接同口径） ====================

    [Theory]
    [InlineData("sales", "zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "negative", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "missing", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "deleted", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "disabled", false, ErrorCodes.RuleConflict)]
    [InlineData("purchase", "zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "negative", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "missing", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "deleted", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "disabled", false, ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_warehouse_before_any_write(string kind, string scenario,
        bool linked, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);

        if (scenario == "deleted") await SetWarehouseFlagsAsync(db, master.WarehouseId, isDeleted: true, status: null);
        if (scenario == "disabled") await SetWarehouseFlagsAsync(db, master.WarehouseId, isDeleted: null, status: 0);

        var warehouseId = scenario switch
        {
            "zero" => 0L,
            "negative" => -3L,
            "missing" => 999_999_999L,
            _ => master.WarehouseId
        };
        if (linked) await SeedSourceAsync(db, kind, master);

        var before = await SnapshotAsync(db);
        var actor = await CreateActorAsync(db, kind, master);

        var error = await Assert.ThrowsAsync<BusinessException>(() => CreateAsync(actor,
            linked ? master.SourceId : null, warehouseId, (master.ProductId, 1m, 10m, 5m)));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    // ==================== 2. 创建：非法明细（零写入 / 零单号，链接与未链接同口径） ====================

    [Theory]
    [InlineData("sales", "missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "missing-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "disabled-product", true, ErrorCodes.RuleConflict)]
    [InlineData("sales", "zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "unrepresentable-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "unrepresentable-cost", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "missing-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "disabled-product", true, ErrorCodes.RuleConflict)]
    [InlineData("purchase", "zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "unrepresentable-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "negative-cost", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "unrepresentable-cost", true, ErrorCodes.InvalidParameter)]
    public async Task Create_rejects_invalid_lines_before_any_write(string kind, string scenario,
        bool linked, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);

        if (scenario == "deleted-product")
            await SetProductFlagsAsync(db, master.ProductId, isDeleted: true, status: null);
        if (scenario == "disabled-product")
            await SetProductFlagsAsync(db, master.ProductId, isDeleted: null, status: 0);

        var (productId, quantity, unitPrice, unitCost) = scenario switch
        {
            "missing-id" => (0L, 1m, 10m, 5m),
            "missing-product" => (999_999_999L, 1m, 10m, 5m),
            "zero-quantity" => (master.ProductId, 0m, 10m, 5m),
            "negative-quantity" => (master.ProductId, -1m, 10m, 5m),
            "unrepresentable-quantity" => (master.ProductId, 1.00001m, 10m, 5m),
            "negative-price" => (master.ProductId, 1m, -1m, 5m),
            "unrepresentable-price" => (master.ProductId, 1m, 1.00001m, 5m),
            "negative-cost" => (master.ProductId, 1m, 10m, -5m),
            "unrepresentable-cost" => (master.ProductId, 1m, 10m, 0.0000001m),
            _ => (master.ProductId, 1m, 10m, 5m)
        };
        if (linked) await SeedSourceAsync(db, kind, master);

        var before = await SnapshotAsync(db);
        var actor = await CreateActorAsync(db, kind, master);

        var error = await Assert.ThrowsAsync<BusinessException>(() => CreateAsync(actor,
            linked ? master.SourceId : null, master.WarehouseId, (productId, quantity, unitPrice, unitCost)));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    // ==================== 3. 创建：行金额 / 合计不可表示（零写入 / 零单号） ====================

    [Theory]
    [InlineData("sales", "total-quantity")]
    [InlineData("sales", "line-amount")]
    [InlineData("sales", "total-amount")]
    [InlineData("purchase", "total-quantity")]
    [InlineData("purchase", "line-amount")]
    [InlineData("purchase", "total-amount")]
    public async Task Create_rejects_unrepresentable_totals_before_any_write(string kind, string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        var before = await SnapshotAsync(db);
        var actor = await CreateActorAsync(db, kind, master);

        var lines = scenario switch
        {
            "total-quantity" => new[]
            {
                (master.ProductId, 60_000_000_000_000m, 1m, 0m),
                (master.SecondProductId, 60_000_000_000_000m, 1m, 0m)
            },
            "line-amount" => new[] { (master.ProductId, 60_000_000_000_000m, 2m, 0m) },
            _ => new[]
            {
                (master.ProductId, 1m, 60_000_000_000_000m, 0m),
                (master.SecondProductId, 1m, 60_000_000_000_000m, 0m)
            }
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            CreateAsync(actor, null, master.WarehouseId, lines));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    // ==================== 4. 修改：非法输入（既有单据与明细逐字段不变、零新单号） ====================

    [Theory]
    [InlineData("sales", "warehouse-zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "warehouse-missing", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "warehouse-deleted", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "warehouse-disabled", true, ErrorCodes.RuleConflict)]
    [InlineData("sales", "zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "missing-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("sales", "disabled-product", true, ErrorCodes.RuleConflict)]
    [InlineData("purchase", "warehouse-zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "warehouse-missing", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "warehouse-deleted", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "warehouse-disabled", true, ErrorCodes.RuleConflict)]
    [InlineData("purchase", "zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "missing-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("purchase", "disabled-product", true, ErrorCodes.RuleConflict)]
    public async Task Update_rejects_invalid_input_and_keeps_existing_rows(string kind, string scenario,
        bool linked, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        if (linked) await SeedSourceAsync(db, kind, master);
        var actor = await CreateActorAsync(db, kind, master);

        var id = CreatedId(await CreateAsync(actor, linked ? master.SourceId : null,
            master.WarehouseId, (master.ProductId, 4m, 10m, 5m)));
        var documentNo = await DocumentNoAsync(db, kind, id);
        var before = await SnapshotAsync(db);

        if (scenario == "warehouse-deleted")
            await SetWarehouseFlagsAsync(db, master.WarehouseId, isDeleted: true, status: null);
        if (scenario == "warehouse-disabled")
            await SetWarehouseFlagsAsync(db, master.WarehouseId, isDeleted: null, status: 0);
        if (scenario == "deleted-product")
            await SetProductFlagsAsync(db, master.ProductId, isDeleted: true, status: null);
        if (scenario == "disabled-product")
            await SetProductFlagsAsync(db, master.ProductId, isDeleted: null, status: 0);

        var warehouseId = scenario switch
        {
            "warehouse-zero" => 0L,
            "warehouse-missing" => 999_999_999L,
            _ => master.WarehouseId
        };
        var (productId, quantity, unitPrice) = scenario switch
        {
            "zero-quantity" => (master.ProductId, 0m, 10m),
            "negative-quantity" => (master.ProductId, -3m, 10m),
            "unrepresentable-quantity" => (master.ProductId, 1.00001m, 10m),
            "negative-price" => (master.ProductId, 2m, -10m),
            "missing-id" => (0L, 2m, 10m),
            "missing-product" => (999_999_999L, 2m, 10m),
            _ => (master.ProductId, 2m, 10m)
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() => UpdateAsync(actor, id,
            linked ? master.SourceId : null, warehouseId, (productId, quantity, unitPrice, 5m)));

        Assert.Equal(expectedCode, error.Code);

        // 既有单据 / 明细逐字段不变（含单号与状态），未消耗新单号，未产生流水
        Assert.Equal(documentNo, await DocumentNoAsync(db, kind, id));
        Assert.Equal(DocumentStatus.Pending, await StatusAsync(db, kind, id));
        Assert.Equal(master.WarehouseId, await WarehouseIdAsync(db, kind, id));
        Assert.Equal(4m, await TotalQuantityAsync(db, kind, id));
        Assert.Equal(40m, await TotalAmountAsync(db, kind, id));

        var detail = await FirstDetailAsync(db, kind, id);
        Assert.Equal(master.ProductId, detail.ProductId);
        Assert.Equal(4m, detail.Quantity);
        Assert.Equal(10m, detail.UnitPrice);

        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.SalesSequence, await SequenceAsync(db, DocumentType.SalesReturn));
        Assert.Equal(before.PurchaseSequence, await SequenceAsync(db, DocumentType.PurchaseReturn));
    }

    // ==================== 5. 空草稿头与重复商品行（既有语义保留） ====================

    [Theory]
    [InlineData("sales")]
    [InlineData("purchase")]
    public async Task Empty_draft_header_saved_but_submit_and_approve_rejected(string kind)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        var actor = await CreateActorAsync(db, kind, master);
        var before = await SnapshotAsync(db);
        var documentType = kind == "sales" ? DocumentType.SalesReturn : DocumentType.PurchaseReturn;
        var beforeSequence = kind == "sales" ? before.SalesSequence : before.PurchaseSequence;

        // 空草稿头（0 行有效明细）仍受支持：照常保存并占用一次服务端单号
        var emptyId = CreatedId(await CreateAsync(actor, null, master.WarehouseId));
        Assert.Equal(DocumentStatus.Pending, await StatusAsync(db, kind, emptyId));
        Assert.Equal(0m, await TotalQuantityAsync(db, kind, emptyId));
        Assert.Equal(0, await ReturnDetailCountAsync(db, kind, emptyId));
        Assert.Equal((beforeSequence ?? 0) + 1, await SequenceAsync(db, documentType));

        // 提交要求至少一行有效明细（空承诺被拒绝，状态与单号不变）
        var submit = await Assert.ThrowsAsync<BusinessException>(() => SubmitAsync(actor, kind, emptyId));
        Assert.Equal(ErrorCodes.RuleConflict, submit.Code);
        Assert.Equal(DocumentStatus.Pending, await StatusAsync(db, kind, emptyId));
        Assert.Equal((beforeSequence ?? 0) + 1, await SequenceAsync(db, documentType));

        // 历史遗留「已提交但无明细」直写单据：审核同样被拒绝且零库存 / 流水
        var legacyId = await SeedSubmittedEmptyReturnAsync(db, kind, master);
        var movementsBefore = await db.StockMovements.CountAsync();
        var approve = await Assert.ThrowsAsync<BusinessException>(() => ApproveAsync(actor, kind, legacyId));
        Assert.Equal(ErrorCodes.RuleConflict, approve.Code);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(db, kind, legacyId));
        Assert.Equal(movementsBefore, await db.StockMovements.CountAsync());
    }

    [Theory]
    [InlineData("sales", false)]
    [InlineData("sales", true)]
    [InlineData("purchase", false)]
    [InlineData("purchase", true)]
    public async Task Repeated_lines_and_permitted_flow_post_inventory_once(string kind, bool linked)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var master = await SeedMasterAsync(db, kind);
        await SeedStockAsync(db, master.WarehouseId, master.ProductId, quantity: 100m, totalCost: 600m);
        if (linked) await SeedSourceAsync(db, kind, master);
        var actor = await CreateActorAsync(db, kind, master);
        var before = await SnapshotAsync(db);
        var documentType = kind == "sales" ? DocumentType.SalesReturn : DocumentType.PurchaseReturn;
        var beforeSequence = kind == "sales" ? before.SalesSequence : before.PurchaseSequence;

        // 重复商品行受支持：两行明细按累计数量语义保留，合计为 2 + 3
        var id = CreatedId(await CreateAsync(actor, linked ? master.SourceId : null, master.WarehouseId,
            (master.ProductId, 2m, 10m, 5m), (master.ProductId, 3m, 10m, 5m)));

        Assert.Equal(2, await ReturnDetailCountAsync(db, kind, id));
        Assert.Equal(5m, await TotalQuantityAsync(db, kind, id));
        Assert.Equal(50m, await TotalAmountAsync(db, kind, id));
        Assert.Equal((beforeSequence ?? 0) + 1, await SequenceAsync(db, documentType));

        await SubmitAsync(actor, kind, id);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(db, kind, id));
        Assert.IsType<OkObjectResult>(await ApproveAsync(actor, kind, id));
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(db, kind, id));

        // 库存恰好一次变动、每行各产生一条流水
        var expectedQuantity = kind == "sales" ? 105m : 95m;
        Assert.Equal(expectedQuantity, await db.Stocks
            .Where(s => s.WarehouseId == master.WarehouseId && s.ProductId == master.ProductId)
            .Select(s => s.Quantity).SingleAsync());
        Assert.Equal(before.Movements + 2, await db.StockMovements.CountAsync());
        Assert.Equal((beforeSequence ?? 0) + 1, await SequenceAsync(db, documentType));  // 提交 / 审核不再消耗单号
    }

    // ==================== 测试辅助 ====================

    /// <summary>本用例播种的真实主数据（一律使用 SQL Server 自增主键，不显式指定 Id）。</summary>
    private sealed class MasterContext
    {
        public long WarehouseId { get; init; }
        public long ProductId { get; init; }
        public long SecondProductId { get; init; }
        public long CustomerId { get; init; }
        public long SupplierId { get; init; }
        public long OperatorId { get; init; }
        public long SourceId { get; set; }
    }

    /// <summary>真实已授权身份 + 两类退货控制器（销售 / 采购共用一套用例骨架，不绕过鉴权）。</summary>
    private sealed record DraftActor(string Kind, MasterContext Master,
        SalesReturnController Sales, PurchaseReturnController Purchase);

    private static async Task<MasterContext> SeedMasterAsync(ErpDbContext db, string kind)
    {
        var warehouseId = await SeedWarehouseAsync(db, $"退货仓-{Tag()}");
        var productId = await SeedProductAsync(db, $"退货商品-{Tag()}");
        var secondProductId = await SeedProductAsync(db, $"退货商品2-{Tag()}");
        var customerId = kind == "sales" ? await SeedCustomerAsync(db, "退货客户") : 0L;
        var supplierId = kind == "purchase" ? await SeedSupplierAsync(db, "退货供应商") : 0L;
        var operatorId = await SeedOperatorAsync(db, kind, customerId == 0L ? null : customerId);
        await EnsureNumberRuleAsync(db, DocumentType.SalesReturn, "XTH");
        await EnsureNumberRuleAsync(db, DocumentType.PurchaseReturn, "CTH");

        return new MasterContext
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            SecondProductId = secondProductId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OperatorId = operatorId
        };
    }

    private static Task<DraftActor> CreateActorAsync(ErpDbContext db, string kind, MasterContext master)
        => Task.FromResult(new DraftActor(kind, master,
            new SalesReturnController(db, new DocumentNumberService(db), new InventoryService(db))
            {
                ControllerContext = Context(master.OperatorId)
            },
            new PurchaseReturnController(db, new DocumentNumberService(db), new InventoryService(db))
            {
                ControllerContext = Context(master.OperatorId)
            }));

    private static ControllerContext Context(long userId)
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
            }
        };

    private static async Task SeedSourceAsync(ErpDbContext db, string kind, MasterContext master)
    {
        if (kind == "sales")
        {
            var shipment = await SeedShipmentAsync(db, master.CustomerId, master.WarehouseId,
                DocumentStatus.Approved, (master.ProductId, "PCS", 100m));
            master.SourceId = shipment.Id;
            return;
        }

        var receipt = await SeedReceiptAsync(db, master.SupplierId, master.WarehouseId,
            DocumentStatus.Approved, (master.ProductId, "PCS", 100m));
        master.SourceId = receipt.Id;
    }

    private static async Task<IActionResult> CreateAsync(DraftActor actor, long? sourceId, long warehouseId,
        params (long ProductId, decimal Quantity, decimal UnitPrice, decimal UnitCost)[] lines)
        => actor.Kind == "sales"
            ? await actor.Sales.Create(SalesDraft(actor.Master, sourceId, warehouseId, lines))
            : await actor.Purchase.Create(PurchaseDraft(actor.Master, sourceId, warehouseId, lines));

    private static async Task<IActionResult> UpdateAsync(DraftActor actor, long id, long? sourceId,
        long warehouseId, params (long ProductId, decimal Quantity, decimal UnitPrice, decimal UnitCost)[] lines)
        => actor.Kind == "sales"
            ? await actor.Sales.Update(id, SalesDraft(actor.Master, sourceId, warehouseId, lines))
            : await actor.Purchase.Update(id, PurchaseDraft(actor.Master, sourceId, warehouseId, lines));

    private static async Task<IActionResult> SubmitAsync(DraftActor actor, string kind, long id)
        => kind == "sales" ? await actor.Sales.Submit(id) : await actor.Purchase.Submit(id);

    private static async Task<IActionResult> ApproveAsync(DraftActor actor, string kind, long id)
        => kind == "sales" ? await actor.Sales.Approve(id) : await actor.Purchase.Approve(id);

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static SalesReturn SalesDraft(MasterContext master, long? sourceStockOutId, long warehouseId,
        params (long ProductId, decimal Quantity, decimal UnitPrice, decimal UnitCost)[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            CustomerId = master.CustomerId,
            CustomerName = "退货客户",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = string.Empty,
            ReturnReason = "质量",
            Remark = "ERP-503_SQL_TEST",
            Details = lines.Select(l => new SalesReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                UnitCost = l.UnitCost
            }).ToList()
        };

    private static PurchaseReturn PurchaseDraft(MasterContext master, long? sourceStockInId, long warehouseId,
        params (long ProductId, decimal Quantity, decimal UnitPrice, decimal UnitCost)[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            SupplierId = master.SupplierId,
            SupplierName = "退货供应商",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = string.Empty,
            ReturnReason = "规格不符",
            Remark = "ERP-503_SQL_TEST",
            Details = lines.Select(l => new PurchaseReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = "PCS",
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                UnitCost = l.UnitCost
            }).ToList()
        };

    private static async Task<string> DocumentNoAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturns.Where(r => r.Id == id).Select(r => r.ReturnNo).SingleAsync()
            : await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.ReturnNo).SingleAsync();

    private static async Task<DocumentStatus> StatusAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturns.Where(r => r.Id == id).Select(r => r.Status).SingleAsync()
            : await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.Status).SingleAsync();

    private static async Task<long> WarehouseIdAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturns.Where(r => r.Id == id).Select(r => r.WarehouseId).SingleAsync()
            : await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.WarehouseId).SingleAsync();

    private static async Task<decimal> TotalQuantityAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturns.Where(r => r.Id == id).Select(r => r.TotalQuantity).SingleAsync()
            : await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.TotalQuantity).SingleAsync();

    private static async Task<decimal> TotalAmountAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturns.Where(r => r.Id == id).Select(r => r.TotalAmount).SingleAsync()
            : await db.PurchaseReturns.Where(r => r.Id == id).Select(r => r.TotalAmount).SingleAsync();

    private static async Task<int> ReturnDetailCountAsync(ErpDbContext db, string kind, long id)
        => kind == "sales"
            ? await db.SalesReturnDetails.CountAsync(d => d.SalesReturnId == id && !d.IsDeleted)
            : await db.PurchaseReturnDetails.CountAsync(d => d.PurchaseReturnId == id && !d.IsDeleted);

    private static async Task<(long? ProductId, decimal Quantity, decimal UnitPrice)> FirstDetailAsync(
        ErpDbContext db, string kind, long id)
    {
        if (kind == "sales")
        {
            var sales = await db.SalesReturnDetails.AsNoTracking()
                .FirstAsync(d => d.SalesReturnId == id && !d.IsDeleted);
            return (sales.ProductId, sales.Quantity, sales.UnitPrice);
        }

        var purchase = await db.PurchaseReturnDetails.AsNoTracking()
            .FirstAsync(d => d.PurchaseReturnId == id && !d.IsDeleted);
        return (purchase.ProductId, purchase.Quantity, purchase.UnitPrice);
    }

    private static async Task<long?> SequenceAsync(ErpDbContext db, DocumentType documentType)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == documentType)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    /// <summary>调用前基线快照（同一夹具库被多个用例复用，因此拒绝断言一律相对快照比较）。</summary>
    private sealed record DraftSnapshot(int SalesReturns, int SalesDetails, int PurchaseReturns,
        int PurchaseDetails, int Movements, int NumberRules, long? SalesSequence, long? PurchaseSequence);

    private static async Task<DraftSnapshot> SnapshotAsync(ErpDbContext db)
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
    private static async Task AssertNoDocumentOrNumberChangeAsync(ErpDbContext db, DraftSnapshot before)
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

    private static async Task EnsureNumberRuleAsync(ErpDbContext db, DocumentType documentType, string prefix)
    {
        if (await db.SysDocumentNumberRules.AnyAsync(r => r.DocumentType == documentType && !r.IsDeleted))
            return;

        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = documentType,
            RuleCode = $"{prefix}-503",
            RuleName = documentType == DocumentType.SalesReturn ? "销售退货单" : "采购退货单",
            Prefix = prefix,
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 1000
        });
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"RETWH-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name)
    {
        var product = new BaseProduct
        {
            ProductCode = $"RETP-{Tag()}",
            ProductName = name,
            Spec = "规格A",
            Unit = "PCS",
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"RETC-{Tag()}", CustomerName = name, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"RETS-{Tag()}", SupplierName = name, Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task SetWarehouseFlagsAsync(ErpDbContext db, long id, bool? isDeleted, int? status)
    {
        var warehouse = await db.BaseWarehouses.SingleAsync(w => w.Id == id);
        if (isDeleted.HasValue) warehouse.IsDeleted = isDeleted.Value;
        if (status.HasValue) warehouse.Status = status.Value;
        await db.SaveChangesAsync();
    }

    private static async Task SetProductFlagsAsync(ErpDbContext db, long id, bool? isDeleted, int? status)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == id);
        if (isDeleted.HasValue) product.IsDeleted = isDeleted.Value;
        if (status.HasValue) product.Status = status.Value;
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

    /// <summary>播种真实授权账号：既有菜单（销售退货 / 采购退货）+ 角色授权 + 启用账号（销售侧含客户数据范围）。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db, string kind, long? customerId)
    {
        var isSales = kind == "sales";
        var menuCode = isSales ? SalesReturnSourceRules.RequiredMenuCode : PurchaseReturnSourceRules.RequiredMenuCode;
        var menuText = isSales ? SalesReturnSourceRules.RequiredMenuText : PurchaseReturnSourceRules.RequiredMenuText;

        var role = new SysRole
        {
            RoleCode = $"RET-{Tag()}", RoleName = isSales ? "销售退货操作员" : "采购退货操作员"
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"ret-{Tag()}";
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
            await db.SaveChangesAsync();
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
            StockOutNo = $"CK-RET-SQL-{Tag()}",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
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
            StockInNo = $"RK-RET-SQL-{Tag()}",
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockInDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockIns.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    /// <summary>历史遗留「已提交但无明细」直写单据（不经过 Create、不消耗单号）。</summary>
    private static async Task<long> SeedSubmittedEmptyReturnAsync(ErpDbContext db, string kind, MasterContext master)
    {
        if (kind == "sales")
        {
            var sales = new SalesReturn
            {
                ReturnNo = $"XTH-RET-SQL-{Tag()}",
                ReturnDate = DateTime.Today,
                CustomerId = master.CustomerId,
                CustomerName = "退货客户",
                WarehouseId = master.WarehouseId,
                Status = DocumentStatus.Submitted
            };
            db.SalesReturns.Add(sales);
            await db.SaveChangesAsync();
            return sales.Id;
        }

        var purchase = new PurchaseReturn
        {
            ReturnNo = $"CTH-RET-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            SupplierId = master.SupplierId,
            SupplierName = "退货供应商",
            WarehouseId = master.WarehouseId,
            Status = DocumentStatus.Submitted
        };
        db.PurchaseReturns.Add(purchase);
        await db.SaveChangesAsync();
        return purchase.Id;
    }
}

/// <summary>
/// 专用 localdb 夹具（ERP-503）：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀
/// <c>NEWERP_AUTOTEST</c>、集成安全时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，
/// 发现同名库已存在立即拒绝，绝不 drop / reset 任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ReturnDocumentDraftValidationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RETURNDRAFT";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-503] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-503] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ReturnDocumentDraftValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;User Id=sa;Password=pw")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ReturnDocumentDraftValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
