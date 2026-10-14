using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-505 预装柜单草稿写入校验单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>创建与修改在单号预约 / 明细整体替换之前拒绝「商品 Id 缺失 / 不存在 / 已删除 / 已停用」；</item>
/// <item>拒绝「数量非正或不可表示 / 溢出、箱数 · 毛重 · 体积为负或不可表示、表头合计不可表示 / 溢出」（与 EF 实际精度同源）；</item>
/// <item>客户端 <c>IsDeleted=true</c> 不能隐藏坏行（服务端会把每一行都恢复为正常行），有效隐藏行照常持久化为正常行；</item>
/// <item>保留空草稿头（0 行）与重复商品行（累计数量语义），提交 / 审核要求至少一行有效明细且复核当前在用商品；</item>
/// <item>显式需求计划证据链接（<c>SourceSalesOrderDetailId</c>）与仅更新主表语义原样保留，绝不静默清空；</item>
/// <item>历史取消路径不新增任何校验闸门（已审核单据即使商品失效仍可取消，且不清除证据链接）；</item>
/// <item>每个拒绝场景都断言零单据、零明细、零单号消耗，且既有单据与明细逐字段不变。</item>
/// </list>
/// 说明：全部使用内存数据库；只有「实际 EF 精度」用例以 SQL Server 提供程序<b>仅构建模型</b>（不连接数据库）。
/// </summary>
public class PreLoadingDraftValidationTests
{
    private const long CustomerA = 986001L;
    private const long ProductA = 986101L;
    private const long ProductB = 986102L;
    private const long MissingProduct = 986999L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "规格A", Unit = "PCS"
        });
        db.SaveChanges();
    }

    private static void SeedCustomer(ErpDbContext db, long id, string code, string name)
    {
        db.BaseCustomers.Add(new BaseCustomer { Id = id, CustomerCode = code, CustomerName = name, Status = 1 });
        db.SaveChanges();
    }

    private static ContainerBooking SeedApprovedBooking(ErpDbContext db, string no)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = CustomerA,
            Status = DocumentStatus.Approved, BillOfLadingNo = "BL-" + no
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static SalesOrderDetail SeedApprovedOrderDetail(ErpDbContext db, string no, decimal capacity)
    {
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = CustomerA,
            Currency = Currency.CNY, ExchangeRate = 1m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        var detail = new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = ProductA, ProductName = "商品A",
            Spec = "规格A", Unit = "PCS", Quantity = capacity, UnitPrice = 10m, Amount = capacity * 10m
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PreLoading,
            RuleCode = "YZ-505",
            RuleName = "预装柜单号",
            Prefix = "YZ",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static void SoftDeleteProduct(ErpDbContext db, long id)
    {
        var product = db.BaseProducts.Single(p => p.Id == id);
        product.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableProduct(ErpDbContext db, long id)
    {
        var product = db.BaseProducts.Single(p => p.Id == id);
        product.Status = 0;
        db.SaveChanges();
    }

    private static ContainerPreLoading Draft(long? bookingId, params ContainerPreLoadingDetail[] lines)
        => new()
        {
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            ContainerNo = "CTN-505",
            SealNo = "SEAL-505",
            Remark = "ERP-505_TEST",
            Details = lines.ToList()
        };

    private static ContainerPreLoadingDetail Line(long productId, decimal quantity, decimal cartons = 0m,
        decimal weight = 0m, decimal volume = 0m, bool isDeleted = false, long? sourceDetailId = null)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Quantity = quantity,
            Cartons = cartons,
            Weight = weight,
            Volume = volume,
            IsDeleted = isDeleted,
            SourceSalesOrderDetailId = sourceDetailId
        };

    private static long NumberSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.PreLoading)
            .Select(r => r.CurrentSequence)
            .FirstOrDefault();

    /// <summary>拒绝场景的统一断言：零单据、零明细、零单号消耗。</summary>
    private static void AssertRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.ContainerPreLoadings);
        Assert.Empty(db.ContainerPreLoadingDetails);
        Assert.Equal(0L, NumberSequence(db));
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static ContainerPreLoading Reload(ErpDbContext db, long id)
        => db.ContainerPreLoadings.Include(o => o.Details).AsNoTracking().Single(p => p.Id == id);

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    // ==================== 1. 创建：未链接草稿的非法明细（零写入 / 零单号） ====================

    [Theory]
    [InlineData("missing-product-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-cartons", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cartons", ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-weight", ErrorCodes.InvalidParameter)]
    [InlineData("negative-volume", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", ErrorCodes.InvalidParameter)]
    public async Task 创建_未链接草稿非法明细_拒绝且零单据零明细零单号(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-DRAFT-1", "商品A");
        SeedNumberRule(db);
        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var line = scenario switch
        {
            "missing-product-id" => Line(0L, 1m),
            "missing-product" => Line(MissingProduct, 1m),
            "zero-quantity" => Line(ProductA, 0m),
            "negative-quantity" => Line(ProductA, -1m),
            "unrepresentable-quantity" => Line(ProductA, 1.005m),
            "overflow-quantity" => Line(ProductA, 10_000_000_000_000_000m),
            "negative-cartons" => Line(ProductA, 1m, cartons: -1m),
            "unrepresentable-cartons" => Line(ProductA, 1m, cartons: 0.005m),
            "negative-weight" => Line(ProductA, 1m, weight: -0.5m),
            "unrepresentable-weight" => Line(ProductA, 1m, weight: 0.005m),
            "negative-volume" => Line(ProductA, 1m, volume: -0.5m),
            "unrepresentable-volume" => Line(ProductA, 1m, volume: 0.005m),
            _ => Line(ProductA, 1m)
        };

        var controller = NewController(db, userId);
        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Draft(null, line)));

        Assert.Equal(expectedCode, error.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_表头合计溢出_拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-DRAFT-2", "商品A");
        SeedNumberRule(db);

        // 每行箱数各自可表示（5e15 < 9.99e15），但两行合计 1e16 超出 DECIMAL(18,2)。
        var controller = NewController(db, userId);
        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Draft(null,
            Line(ProductA, 1m, cartons: 5_000_000_000_000_000m),
            Line(ProductA, 1m, cartons: 5_000_000_000_000_000m))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出存储精度", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    public async Task 创建_客户端删除态隐藏坏行_不能绕过护栏(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-DRAFT-3", "商品A");
        SeedProduct(db, ProductB, "P-DRAFT-3B", "商品B");
        SeedNumberRule(db);

        var hidden = scenario switch
        {
            "zero-quantity" => Line(ProductB, 0m, isDeleted: true),
            "negative-weight" => Line(ProductB, 2m, weight: -1m, isDeleted: true),
            _ => Line(MissingProduct, 2m, isDeleted: true)
        };

        var controller = NewController(db, userId);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(null, Line(ProductA, 1m), hidden)));

        Assert.Equal(expectedCode, error.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_客户端删除态有效行_服务端恢复为正常行并计入合计()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-DRAFT-4", "商品A");
        SeedProduct(db, ProductB, "P-DRAFT-4B", "商品B");
        SeedNumberRule(db);

        var controller = NewController(db, userId);
        var hidden = Line(ProductB, 3m, cartons: 2m, weight: 6m, volume: 0.5m, isDeleted: true);
        hidden.Id = 424242L;
        hidden.PreLoadingId = 777777L;

        var result = await controller.Create(Draft(null, Line(ProductA, 2m, cartons: 1m), hidden));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, CreatedId(result));
        Assert.Equal(2, saved.Details.Count);
        Assert.Equal(3m, saved.TotalCartons);
        Assert.Equal(6m, saved.TotalWeight);
        Assert.Equal(0.5m, saved.TotalVolume);
        Assert.All(saved.Details, d => Assert.False(d.IsDeleted));
        Assert.All(saved.Details, d => Assert.NotEqual(424242L, d.Id));
        Assert.All(saved.Details, d => Assert.Equal(saved.Id, d.PreLoadingId));
        Assert.Equal(1L, NumberSequence(db));
    }


    // ==================== 2. 创建：受支持的空草稿头、重复商品行与显式链接 ====================

    [Fact]
    public async Task 创建_空草稿头_保存成功且只消耗一个单号()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedNumberRule(db);

        var result = await NewController(db, userId).Create(Draft(null));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, CreatedId(result));
        Assert.Empty(saved.Details);
        Assert.Equal(0m, saved.TotalCartons);
        Assert.Equal(0m, saved.TotalWeight);
        Assert.Equal(0m, saved.TotalVolume);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(1L, NumberSequence(db));
    }

    [Fact]
    public async Task 创建_重复商品行_按累计数量语义保留()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-DRAFT-5", "商品A");
        SeedNumberRule(db);

        var result = await NewController(db, userId).Create(Draft(null,
            Line(ProductA, 2m, cartons: 1m, weight: 3m),
            Line(ProductA, 5m, cartons: 4m, weight: 7m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, CreatedId(result));
        Assert.Equal(2, saved.Details.Count);
        Assert.Equal(7m, saved.Details.Sum(d => d.Quantity));
        Assert.Equal(5m, saved.TotalCartons);
        Assert.Equal(10m, saved.TotalWeight);
    }

    [Fact]
    public async Task 创建_未链接草稿_合法行保存成功且来源保持未链接()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-DRAFT-6", "商品A");
        SeedNumberRule(db);

        var result = await NewController(db, userId)
            .Create(Draft(null, Line(ProductA, 6m, cartons: 2m, weight: 30m, volume: 1.5m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, CreatedId(result));
        var detail = Assert.Single(saved.Details);
        Assert.Null(detail.SourceSalesOrderDetailId);   // 未链接是显式历史事实，绝不回填
        Assert.Equal(2m, saved.TotalCartons);
        Assert.Equal(30m, saved.TotalWeight);
        Assert.Equal(1.5m, saved.TotalVolume);
    }

    [Fact]
    public async Task 创建_链接需求计划证据的草稿_合法行保存且证据链接原样保留()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-DRAFT-1", "草稿客户");
        SeedProduct(db, ProductA, "P-DRAFT-7", "商品A");
        var booking = SeedApprovedBooking(db, "DG-DRAFT-1");
        var source = SeedApprovedOrderDetail(db, "SO-DRAFT-1", 100m);
        SeedNumberRule(db);

        var result = await NewController(db, userId).Create(Draft(booking.Id,
            Line(ProductA, 5m, cartons: 1m, sourceDetailId: source.Id)));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, CreatedId(result));
        var detail = Assert.Single(saved.Details);
        Assert.Equal(source.Id, detail.SourceSalesOrderDetailId);   // 显式证据链接不被静默清空
        Assert.Equal(booking.Id, saved.BookingId);
    }

    [Fact]
    public async Task 链接草稿_已停用商品_写入边界同样拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-DRAFT-2", "草稿客户");
        SeedProduct(db, ProductA, "P-DRAFT-8", "商品A");
        var booking = SeedApprovedBooking(db, "DG-DRAFT-2");
        var source = SeedApprovedOrderDetail(db, "SO-DRAFT-2", 100m);
        SeedNumberRule(db);
        DisableProduct(db, ProductA);   // 链接规则只看「存在 / 未删除」，停用由共享草稿护栏 fail closed

        var error = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId).Create(
            Draft(booking.Id, Line(ProductA, 5m, sourceDetailId: source.Id))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        AssertRejectedWithNoSideEffects(db);
    }


    // ==================== 3. 修改：写入边界与仅主表语义 ====================

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-cartons", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    [InlineData("hidden-bad-row", ErrorCodes.InvalidParameter)]
    public async Task 修改_非法输入_拒绝且既有单据明细逐字段不变(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-UPD-1", "商品A");
        SeedProduct(db, ProductB, "P-UPD-1B", "商品B");
        SeedNumberRule(db);

        var controller = NewController(db, userId);
        var id = CreatedId(await controller.Create(Draft(null, Line(ProductA, 4m, cartons: 1m, weight: 1m))));
        var before = Reload(db, id);
        var beforeDetail = before.Details.Single();
        var beforeSequence = NumberSequence(db);

        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var edit = scenario switch
        {
            "zero-quantity" => Draft(null, Line(ProductA, 0m)),
            "negative-cartons" => Draft(null, Line(ProductA, 3m, cartons: -1m)),
            "unrepresentable-volume" => Draft(null, Line(ProductA, 3m, volume: 0.005m)),
            "missing-product" => Draft(null, Line(MissingProduct, 3m)),
            "hidden-bad-row" => Draft(null, Line(ProductA, 3m), Line(ProductB, 0m, isDeleted: true)),
            _ => Draft(null, Line(ProductA, 3m))
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, edit));

        Assert.Equal(expectedCode, error.Code);

        var after = Reload(db, id);
        Assert.Equal(before.PreLoadingNo, after.PreLoadingNo);
        Assert.Equal(before.Status, after.Status);
        Assert.False(after.IsDeleted);
        Assert.Equal(before.BookingId, after.BookingId);
        Assert.Equal(before.ContainerNo, after.ContainerNo);
        Assert.Equal(before.SealNo, after.SealNo);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.TotalCartons, after.TotalCartons);
        Assert.Equal(before.TotalWeight, after.TotalWeight);
        Assert.Equal(before.TotalVolume, after.TotalVolume);

        var detail = Assert.Single(after.Details);
        Assert.Equal(beforeDetail.Id, detail.Id);
        Assert.Equal(beforeDetail.ProductId, detail.ProductId);
        Assert.Equal(beforeDetail.Quantity, detail.Quantity);
        Assert.Equal(beforeSequence, NumberSequence(db));
    }

    [Fact]
    public async Task 修改_合法明细_成功且不消耗新单号()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-UPD-2", "商品A");
        SeedProduct(db, ProductB, "P-UPD-2B", "商品B");
        SeedNumberRule(db);

        var controller = NewController(db, userId);
        var id = CreatedId(await controller.Create(Draft(null, Line(ProductA, 4m))));
        var number = Reload(db, id).PreLoadingNo;
        var sequence = NumberSequence(db);

        var result = await controller.Update(id, Draft(null, Line(ProductB, 7m, cartons: 2m, weight: 5m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, id);
        Assert.Equal(number, saved.PreLoadingNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(2m, saved.TotalCartons);
        Assert.Equal(5m, saved.TotalWeight);
        Assert.Equal(7m, saved.Details.Single().Quantity);
        Assert.Equal(sequence, NumberSequence(db));
    }


    [Fact]
    public async Task 修改_仅主表_保留既有明细与显式证据链接()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-UPD-1", "更新客户");
        SeedProduct(db, ProductA, "P-UPD-3", "商品A");
        var booking = SeedApprovedBooking(db, "DG-UPD-1");
        var source = SeedApprovedOrderDetail(db, "SO-UPD-1", 100m);
        SeedNumberRule(db);

        var existing = SeedPreLoading(db, booking.Id, DocumentStatus.Pending,
            Line(ProductA, 5m, cartons: 1m, sourceDetailId: source.Id));
        var detailId = existing.Details.Single().Id;

        var result = await NewController(db, userId).Update(existing.Id, Draft(booking.Id));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, existing.Id);
        var detail = Assert.Single(saved.Details);
        Assert.Equal(detailId, detail.Id);                          // 既有明细行原样保留
        Assert.Equal(source.Id, detail.SourceSalesOrderDetailId);    // 证据链接绝不因主表编辑被清空
        Assert.Equal(5m, detail.Quantity);
        Assert.Equal(1m, saved.TotalCartons);
    }

    [Fact]
    public async Task 修改_携带明细_显式证据链接随行保留()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-UPD-2", "更新客户");
        SeedProduct(db, ProductA, "P-UPD-4", "商品A");
        var booking = SeedApprovedBooking(db, "DG-UPD-2");
        var source = SeedApprovedOrderDetail(db, "SO-UPD-2", 100m);
        SeedNumberRule(db);

        var existing = SeedPreLoading(db, booking.Id, DocumentStatus.Pending,
            Line(ProductA, 5m, sourceDetailId: source.Id));

        var result = await NewController(db, userId).Update(existing.Id,
            Draft(booking.Id, Line(ProductA, 9m, cartons: 3m, sourceDetailId: source.Id)));

        Assert.IsType<OkObjectResult>(result);
        var saved = Reload(db, existing.Id);
        var detail = Assert.Single(saved.Details);
        Assert.Equal(9m, detail.Quantity);
        Assert.Equal(source.Id, detail.SourceSalesOrderDetailId);
        Assert.Equal(3m, saved.TotalCartons);
    }


    // ==================== 4. 提交 / 审核：承诺边界复核 ====================

    [Fact]
    public async Task 提交_空草稿头_拒绝且状态保持待提交()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedNumberRule(db);
        var empty = SeedPreLoading(db, null, DocumentStatus.Pending);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Submit(empty.Id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("无明细", error.Message);
        Assert.Equal(DocumentStatus.Pending, Reload(db, empty.Id).Status);
    }

    [Fact]
    public async Task 提交_已存明细商品失效_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SUB-1", "商品A");
        var pending = SeedPreLoading(db, null, DocumentStatus.Pending, Line(ProductA, 4m, cartons: 1m));
        SoftDeleteProduct(db, ProductA);   // 保存后主数据失效 → 提交必须实时复核

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Submit(pending.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Equal(DocumentStatus.Pending, Reload(db, pending.Id).Status);
    }

    [Fact]
    public async Task 提交_合法明细_成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-SUB-2", "商品A");
        var pending = SeedPreLoading(db, null, DocumentStatus.Pending, Line(ProductA, 4m, cartons: 1m));

        var result = await NewController(db, userId).Submit(pending.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, pending.Id).Status);
    }

    [Fact]
    public async Task 审核_空草稿头_拒绝且状态保持已提交()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        var empty = SeedPreLoading(db, null, DocumentStatus.Submitted);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Approve(empty.Id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, empty.Id).Status);
    }

    [Fact]
    public async Task 审核_已存数值被改写为非法_拒绝且状态与明细不变()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-APP-1", "商品A");
        var submitted = SeedPreLoading(db, null, DocumentStatus.Submitted, Line(ProductA, 4m, cartons: 1m));

        // 模拟历史 / 被并发改写的非法已存数值（绕过控制器直接写入数据库）。
        var detail = db.ContainerPreLoadingDetails.Single(d => d.PreLoadingId == submitted.Id);
        detail.Quantity = 0m;
        db.SaveChanges();

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Approve(submitted.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        var after = Reload(db, submitted.Id);
        Assert.Equal(DocumentStatus.Submitted, after.Status);
        Assert.Equal(0m, after.Details.Single().Quantity);
    }

    [Fact]
    public async Task 审核_合法明细_成功()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedProduct(db, ProductA, "P-APP-2", "商品A");
        var submitted = SeedPreLoading(db, null, DocumentStatus.Submitted, Line(ProductA, 4m, cartons: 1m));

        var result = await NewController(db, userId).Approve(submitted.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(DocumentStatus.Approved, Reload(db, submitted.Id).Status);
    }

    // ==================== 5. 历史取消路径不新增校验闸门 ====================

    [Fact]
    public async Task 取消_已审核且商品失效_仍可取消且不清除证据链接()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "C-CAN-1", "取消客户");
        SeedProduct(db, ProductA, "P-CAN-1", "商品A");
        var booking = SeedApprovedBooking(db, "DG-CAN-1");
        var source = SeedApprovedOrderDetail(db, "SO-CAN-1", 100m);
        var approved = SeedPreLoading(db, booking.Id, DocumentStatus.Approved,
            Line(ProductA, 5m, cartons: 1m, sourceDetailId: source.Id));
        SoftDeleteProduct(db, ProductA);   // 历史 undo 路径不因主数据失效新增闸门

        var result = await NewController(db, userId).Cancel(approved.Id);

        Assert.IsType<OkObjectResult>(result);
        var after = Reload(db, approved.Id);
        Assert.Equal(DocumentStatus.Cancelled, after.Status);
        Assert.Equal(source.Id, after.Details.Single().SourceSalesOrderDetailId);  // 证据链接作为历史证据保留
    }


    // ==================== 6. 实际 EF 精度、规则文案与源码接线契约 ====================

    [Fact]
    public void EF实际精度_数量箱数毛重体积与表头合计_与护栏常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoadingDetail), nameof(ContainerPreLoadingDetail.Quantity)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoadingDetail), nameof(ContainerPreLoadingDetail.Cartons)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoadingDetail), nameof(ContainerPreLoadingDetail.Weight)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoadingDetail), nameof(ContainerPreLoadingDetail.Volume)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoading), nameof(ContainerPreLoading.TotalCartons)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoading), nameof(ContainerPreLoading.TotalWeight)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerPreLoading), nameof(ContainerPreLoading.TotalVolume)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerLoadingDetail), nameof(ContainerLoadingDetail.Quantity)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(ContainerLoadingList), nameof(ContainerLoadingList.TotalVolume)));

        Assert.Equal(18, ContainerLoadingDraftValidationRules.QuantityPrecision);
        Assert.Equal(2, ContainerLoadingDraftValidationRules.QuantityScale);
        Assert.Equal(9_999_999_999_999_999.99m, ContainerLoadingDraftValidationRules.MaxDecimal18Scale2);
        Assert.True(ContainerLoadingDraftValidationRules.IsQuantityRepresentable(9_999_999_999_999_999.99m));
        Assert.True(ContainerLoadingDraftValidationRules.IsQuantityRepresentable(1.05m));
        Assert.False(ContainerLoadingDraftValidationRules.IsQuantityRepresentable(10_000_000_000_000_000m));
        Assert.False(ContainerLoadingDraftValidationRules.IsQuantityRepresentable(1.005m));
    }

    [Fact]
    public void 规则文案_明确拒绝口径与无货币合计边界()
    {
        var text = ContainerLoadingDraftValidationRules.DraftGuardRuleText;

        Assert.Contains("DECIMAL(18,2)", text);
        Assert.Contains("提交 / 审核要求至少一行有效明细", text);
        Assert.Contains("绝不静默清空", text);
        Assert.Contains("不存在货币合计", text);
        Assert.Contains("绝不新增表 / 列 / 索引 / 菜单 / 权限", text);
    }

    [Fact]
    public void 控制器接线_写入边界先于编号_承诺边界在锁内复核()
    {
        var source = ReadSource("src/ERP.Api/Controllers/ContainerPreLoadingController.cs");

        var create = MethodBody(source, "public async Task<IActionResult> Create");
        Assert.Contains("EnsurePreLoadingDraftValidAsync", create);
        Assert.True(Rank(create, "EnsurePreLoadingDraftValidAsync") < Rank(create, "GenerateAsync(DocumentType.PreLoading)"),
            "创建护栏必须严格先于单号生成");
        Assert.True(Rank(create, "EnsurePreLoadingDraftValidAsync") < Rank(create, "Db.ContainerPreLoadings.Add(entity)"),
            "创建护栏必须严格先于任何持久化");

        var update = MethodBody(source, "public async Task<IActionResult> Update");
        Assert.Contains("EnsurePreLoadingDraftValidAsync", update);
        Assert.True(Rank(update, "EnsurePreLoadingDraftValidAsync") < Rank(update, "RemoveRange(existing.Details)"),
            "修改护栏必须严格先于明细整体替换");

        var submit = MethodBody(source, "public override async Task<IActionResult> Submit");
        Assert.Contains("EnsurePreLoadingCommitReadyAsync(Db, entity, \"提交\")", submit);
        Assert.True(Rank(submit, "EnsurePreLoadingCommitReadyAsync") > Rank(submit, "AcquirePreLoadingRowLockAsync"),
            "提交复核必须在既有本单行锁之内");

        var approve = MethodBody(source, "public override async Task<IActionResult> Approve");
        Assert.Contains("EnsurePreLoadingCommitReadyAsync(Db, entity, \"审核\")", approve);
        Assert.True(Rank(approve, "EnsurePreLoadingCommitReadyAsync") > Rank(approve, "AcquirePreLoadingRowLockAsync"),
            "审核复核必须在既有本单行锁之内");
    }

    // ==================== 辅助（种子） ====================

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, long? bookingId, DocumentStatus status,
        params ContainerPreLoadingDetail[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = $"YZ-505-{Guid.NewGuid():N}"[..20],
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            ContainerNo = "CTN-SEED",
            SealNo = "SEAL-SEED",
            Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();

        foreach (var line in lines)
        {
            line.PreLoadingId = pre.Id;
            db.ContainerPreLoadingDetails.Add(line);
        }
        db.SaveChanges();
        return pre;
    }

    private static int Rank(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(index >= 0, $"未找到接线契约标记：{token}");
        return index;
    }

    /// <summary>截取控制器源码中某个路由方法（签名 → 下一个成员文档注释之前）的正文。</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到路由方法：{signature}");
        var end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

}
