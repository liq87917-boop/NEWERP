using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-478 销售出库规范主子表入口与「省略出库单号」手工录入契约单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="StockOut.StockOutNo"/> 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造出库单号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳与明细身份
/// 全部由服务端覆盖（客户端注入值不参与持久化）；</item>
/// <item>拒绝先于编号与持久化：缺菜单授权 / 无身份 / 非法来源 / 越权客户一律零表头 · 明细 · 状态变化且零占号；</item>
/// <item>来源清空：数字 0 与 <c>null</c> 都归一为「未链接」历史语义，修改可把已链接来源清空且单据号 / 状态不变；</item>
/// <item>接线契约：菜单路由不再落到旧存储过程族、规范模块与来源选择器接线、旧族读侧仍保留。</item>
/// </list></para>
/// </summary>
public class StockOutCanonicalEntryTests
{
    private const long CustomerA = 982001L;
    private const long WarehouseA = 982101L;
    private const long ProductA = 982201L;

    // ==================== 1. 实体契约：省略出库单号 ====================

    [Fact]
    public void Entity_出库单号不再显式必填_长度约束保留()
    {
        var property = typeof(StockOut).GetProperty(nameof(StockOut.StockOutNo))!;

        Assert.Empty(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), inherit: true));
        var maxLength = Assert.Single(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), inherit: true)
            .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
    }

    // ==================== 2. 省略 / 空串 / 伪造出库单号：服务端权威覆盖 ====================

    [Fact]
    public async Task Create_省略出库单号_服务端生成权威单号与状态()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, StockOutTestAuthorization.SeedPrivilegedOutboundOperator(db));

        var result = await ctl.Create(NewStockOut(CustomerA, null, string.Empty, (ProductA, "PCS", 3m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockOuts.Single();
        Assert.StartsWith("CK", saved.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(3m, saved.TotalQuantity);
    }

    [Fact]
    public async Task Create_伪造单号与身份_一律由服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, StockOutTestAuthorization.SeedPrivilegedOutboundOperator(db));

        var entity = NewStockOut(CustomerA, null, "HACK-CK-478", (ProductA, "PCS", 3m));
        entity.Id = 999999L;                                    // 伪造主键
        entity.Status = DocumentStatus.Approved;                // 伪造状态
        entity.IsDeleted = true;                                // 伪造删除态
        entity.CreatedAt = new DateTime(2000, 1, 1);             // 伪造时间戳
        entity.UpdatedAt = new DateTime(2000, 1, 1);
        entity.Details[0].Id = 888888L;                         // 伪造明细身份
        entity.Details[0].StockOutId = 777777L;
        entity.Details[0].IsDeleted = true;

        var result = await ctl.Create(entity);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockOuts.Include(o => o.Details).Single();
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("CK", saved.StockOutNo);
        Assert.NotEqual("HACK-CK-478", saved.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.NotEqual(new DateTime(2000, 1, 1), saved.CreatedAt);
        var detail = saved.Details.Single();
        Assert.NotEqual(888888L, detail.Id);
        Assert.Equal(saved.Id, detail.StockOutId);
        Assert.False(detail.IsDeleted);
    }

    // ==================== 3. 拒绝先于编号与持久化：零写入零占号 ====================

    [Fact]
    public async Task Create_缺菜单授权_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var (userId, customerId) = StockOutTestAuthorization.SeedOperatorWithoutStockOutMenu(db);
        var ctl = StockOutTestAuthorization.ForUser(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(customerId, null, string.Empty, (ProductA, "PCS", 2m))));

        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.StockOutDetails);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, Sequence(db));                          // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task Create_无身份_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, null);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, null, string.Empty, (ProductA, "PCS", 2m))));

        Assert.Equal(ErrorCodes.Unauthorized, error.Code);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, Sequence(db));
    }

    [Fact]
    public async Task Create_非法来源_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, StockOutTestAuthorization.SeedPrivilegedOutboundOperator(db));

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(CustomerA, -1L, string.Empty, (ProductA, "PCS", 2m))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, Sequence(db));
    }

    [Fact]
    public async Task Create_越权客户_零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var (userId, ownedCustomerId) = StockOutTestAuthorization.SeedRestrictedOutboundOperator(db);
        var foreignCustomerId = StockOutTestAuthorization.SeedCustomer(db, CustomerA + 500, "范围外客户").Id;
        var ctl = StockOutTestAuthorization.ForUser(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockOut(foreignCustomerId, null, string.Empty, (ProductA, "PCS", 2m))));

        Assert.Equal(ErrorCodes.NotFound, error.Code);           // 范围外按「不存在」拒绝（不泄露归属）
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, Sequence(db));

        // 归属客户在范围内：正常写入（证明拒绝只针对越权客户，不是整体停用）。
        var ok = await ctl.Create(NewStockOut(ownedCustomerId, null, string.Empty, (ProductA, "PCS", 1m)));
        Assert.IsType<OkObjectResult>(ok);
        Assert.Single(db.StockOuts);
    }

    // ==================== 4. 未选择来源 / 清空来源：未链接历史语义 ====================

    [Fact]
    public async Task Create_来源为0或null_都保存为未链接()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, StockOutTestAuthorization.SeedPrivilegedOutboundOperator(db));

        await ctl.Create(NewStockOut(CustomerA, 0L, string.Empty, (ProductA, "PCS", 1m)));
        await ctl.Create(NewStockOut(CustomerA, null, "  ", (ProductA, "PCS", 2m)));

        var saved = db.StockOuts.OrderBy(o => o.Id).ToList();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, s => Assert.Null(s.SalesOrderId));
        Assert.All(saved, s => Assert.StartsWith("CK", s.StockOutNo));
    }

    [Fact]
    public async Task Update_清空已链接来源_保留服务端单号与状态()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, StockOutTestAuthorization.SeedPrivilegedOutboundOperator(db));
        var order = SeedApprovedOrder(db, "SO-478-CLEAR", (ProductA, "PCS", 10m));

        var createdId = CreatedId(await ctl.Create(NewStockOut(CustomerA, order.Id, string.Empty, (ProductA, "PCS", 2m))));
        var created = db.StockOuts.Single();
        var number = created.StockOutNo;

        var edit = NewStockOut(CustomerA, null, "HACK-CK-UPD", (ProductA, "PCS", 1m));
        edit.Status = DocumentStatus.Approved;
        edit.IsDeleted = true;
        edit.Details[0].Id = 424242L;

        var result = await ctl.Update(createdId, edit);
        Assert.IsType<OkObjectResult>(result);

        var reloaded = Assert.IsType<StockOut>(Assert.IsType<ApiResponse<StockOut>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(createdId)).Value).Data!);
        Assert.Equal(number, reloaded.StockOutNo);               // 单号不被客户端改写
        Assert.Equal(DocumentStatus.Pending, reloaded.Status);    // 状态不被客户端改写
        Assert.False(reloaded.IsDeleted);
        Assert.Null(reloaded.SalesOrderId);                       // 来源被显式清空 = 未链接
        var detail = Assert.Single(reloaded.Details);
        Assert.NotEqual(424242L, detail.Id);
        Assert.Equal(1m, detail.Quantity);
        Assert.Equal(1m, reloaded.TotalQuantity);
        Assert.Equal(created.Id, detail.StockOutId);
    }

    // ==================== 5. 控制器接线：服务端归属先于单号与持久化 ====================

    [Fact]
    public void Controller_校验先于单号_身份与明细由服务端覆盖()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "StockOutController.cs");

        var authorize = controller.IndexOf("StockOutAuthorizationRules.EnsureCustomerAuthorizedAsync", StringComparison.Ordinal);
        var validateLink = controller.IndexOf("StockOutOrderFulfillmentRules.ValidateLinkAsync", StringComparison.Ordinal);
        var generate = controller.IndexOf("entity.StockOutNo = await _noService.GenerateAsync", StringComparison.Ordinal);
        Assert.True(authorize >= 0 && validateLink > authorize && generate > validateLink,
            "授权与来源 / 容量校验必须先于单号预约（被拒绝方绝不消耗单据号）");
        Assert.Contains("entity.Status = DocumentStatus.Pending;", controller);
        Assert.Contains("entity.IsDeleted = false;", controller);
        Assert.Contains("entity.UpdatedAt = null;", controller);
        Assert.Contains("ApplyServerOwnedDetailIdentity(entity.Details, 0, now);", controller);
        Assert.Contains("ApplyServerOwnedDetailIdentity(entity.Details, id, DateTime.Now);", controller);
        Assert.Contains("entity.Details ??= new List<StockOutDetail>();", controller);

        // 明细身份由服务端统一重置（不采信客户端注入的主键 / 归属单据 / 删除态）。
        Assert.Contains("d.Id = 0;", controller);
        Assert.Contains("d.StockOutId = stockOutId;", controller);
        Assert.Contains("d.IsDeleted = false;", controller);
    }

    // ==================== 6. 菜单路由 / 规范模块 / 选择器接线契约 ====================

    [Fact]
    public void 菜单路由_销售出库走规范模块_不再落到旧存储过程族()
    {
        // ERP-478：菜单必须打开规范 EF 主子表模块（api/stock-outs，规范 Id），
        // 不得再经 BILL_CODE_MAP 落到旧存储过程族（其写路径已被 ERP-404 门禁整体停用）。
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        Assert.True(mapStart >= 0 && mapEnd > mapStart);
        var billCodeMap = billV2[mapStart..mapEnd];
        // 注释里会说明「为何不再映射旧族」，因此这里只断言**不存在 stock-out 键映射**（与离线契约测试同口径）。
        Assert.DoesNotMatch(@"'stock-out'\s*:", billCodeMap);
        Assert.DoesNotMatch(@"stock-out\s*:\s*['""]", billCodeMap);

        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js");
        Assert.Contains("title: '销售出库', api: '/api/stock-outs'", modules);
        Assert.Contains("detailFields: STOCK_OUT_DETAIL_FIELDS", modules);
        Assert.Contains("nullable: true, selector: 'stock-out-source'", modules);

        // 旧族自身（旧库单体表 Oid 读侧）保持只读可用：不删除旧配置、不把 Oid 当规范 Id。
        var billConfig2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-config2.js");
        Assert.Contains("'stock-out'", billConfig2);
        Assert.DoesNotContain("/api/v2/bills", ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js"));
    }

    [Fact]
    public void 来源选择器_显式清空来源_保留未链接语义且不改写仓库()
    {
        var picker = ReadSource("src", "ERP.Api", "wwwroot", "js", "stock-out-source-picker.js");

        Assert.Contains("function sosClearSource()", picker);
        Assert.Contains("sosSetField('salesOrderId', '')", picker);
        Assert.Contains("sosClearSource,", picker);                        // 导出给离线契约测试
        Assert.DoesNotContain("sosSetField('warehouseId'", picker);        // 保留用户所选仓库
        Assert.DoesNotContain("sosSetField('customerId', ''", picker);     // 清空来源绝不臆造 / 清空客户
        Assert.Contains("function sosInstallFormHook()", picker);
        Assert.Contains("field.selector !== 'stock-out-source'", picker);
        Assert.Contains("toast(sosReadonlyReason(SOS.status)", picker);    // 非草稿状态只读
    }

    // ==================== 辅助 ====================

    private static void SeedMaster(ErpDbContext db)
    {
        db.BaseWarehouses.Add(new BaseWarehouse
        {
            Id = WarehouseA, WarehouseCode = "SO-478-WH", WarehouseName = "出库仓", Status = 1
        });
        StockOutTestAuthorization.SeedCustomer(db, CustomerA, "客户A");
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductA, ProductCode = "P-478", ProductName = "商品A", Spec = "规格A", Unit = "PCS", Status = 1
        });
        db.SaveChanges();
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockOut,
            RuleCode = "CK-478",
            RuleName = "销售出库单号",
            Prefix = "CK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static SalesOrder SeedApprovedOrder(ErpDbContext db, string orderNo,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-3),
            CustomerId = CustomerA,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, quantity) in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = quantity,
                UnitPrice = 5m,
                Amount = quantity * 5m
            });
        }
        db.SaveChanges();
        return order;
    }

    private static StockOut NewStockOut(long customer, long? salesOrderId, string stockOutNo,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
        => new()
        {
            StockOutNo = stockOutNo,
            StockOutDate = DateTime.Today,
            SalesOrderId = salesOrderId,
            CustomerId = customer,
            WarehouseId = WarehouseA,
            Remark = "ERP-478_TEST",
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static long Sequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockOut)
            .Select(r => r.CurrentSequence)
            .SingleOrDefault();

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }
}
