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
/// ERP-477 采购入库规范主子表入口与「省略入库单号」手工录入契约单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：<list type="bullet">
/// <item>实体层：<see cref="StockIn.StockInNo"/> 不再显式 <c>[Required]</c>（省略 / 空串照常绑定），长度约束保留；</item>
/// <item>新增入口：省略 / 空串 / 伪造入库单号一律由服务端权威单号覆盖，主键 / 状态 / 删除态 / 时间戳与明细身份
/// 全部由服务端覆盖（客户端注入值不参与持久化）；</item>
/// <item>拒绝先于编号与持久化：缺菜单授权 / 无身份 / 非法来源 / 不存在供应商一律零表头 · 明细 · 状态变化且零占号；</item>
/// <item>来源清空：数字 0 与 <c>null</c> 都归一为「未链接」历史语义，修改可把已链接来源清空且单据号 / 状态不变；</item>
/// <item>接线契约：菜单路由不再落到旧存储过程族、规范模块与来源选择器接线、旧族读侧仍保留。</item>
/// </list></para>
/// </summary>
public class StockInCanonicalEntryTests
{
    private const long SupplierA = 981001L;
    private const long WarehouseA = 981101L;
    private const long ProductA = 981201L;

    // ==================== 1. 实体契约：省略入库单号 ====================

    [Fact]
    public void Entity_入库单号不再显式必填_长度约束保留()
    {
        var property = typeof(StockIn).GetProperty(nameof(StockIn.StockInNo))!;

        Assert.Empty(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), inherit: true));
        var maxLength = Assert.Single(property.GetCustomAttributes(
            typeof(System.ComponentModel.DataAnnotations.MaxLengthAttribute), inherit: true)
            .Cast<System.ComponentModel.DataAnnotations.MaxLengthAttribute>());
        Assert.Equal(50, maxLength.Length);
    }

    // ==================== 2. 省略 / 空串 / 伪造入库单号：服务端权威覆盖 ====================

    [Fact]
    public async Task Create_省略入库单号_服务端生成权威单号与状态()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

        var result = await ctl.Create(NewStockIn(SupplierA, null, string.Empty, (ProductA, "PCS", 3m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Single();
        Assert.StartsWith("RK", saved.StockInNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task Create_伪造单号与身份_一律由服务端权威覆盖()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

        var entity = NewStockIn(SupplierA, null, "HACK-RK-477", (ProductA, "PCS", 3m));
        entity.Id = 999999L;                                    // 伪造主键
        entity.Status = DocumentStatus.Approved;                // 伪造状态
        entity.IsDeleted = true;                                // 伪造删除态
        entity.CreatedAt = new DateTime(2000, 1, 1);             // 伪造创建时间
        entity.UpdatedAt = new DateTime(2000, 1, 1);
        entity.Details[0].Id = 888888L;                         // 伪造明细身份
        entity.Details[0].StockInId = 777777L;
        entity.Details[0].IsDeleted = true;

        var result = await ctl.Create(entity);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        Assert.NotEqual(999999L, saved.Id);
        Assert.StartsWith("RK", saved.StockInNo);
        Assert.NotEqual("HACK-RK-477", saved.StockInNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.False(saved.IsDeleted);
        Assert.True(saved.UpdatedAt > DateTime.Today.AddMinutes(-5));   // 伪造时间戳被服务端覆盖
        Assert.True(saved.CreatedAt > DateTime.Today.AddMinutes(-5));

        var detail = Assert.Single(saved.Details);
        Assert.NotEqual(888888L, detail.Id);
        Assert.Equal(saved.Id, detail.StockInId);
        Assert.False(detail.IsDeleted);
        Assert.Equal(3m, detail.Quantity);
        Assert.Equal(3m, saved.TotalQuantity);
    }

    // ==================== 3. 拒绝先于编号与持久化（零写入、零占号） ====================

    [Fact]
    public async Task Create_缺采购入库菜单_拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedUserWithoutMenus(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockIn(SupplierA, null, string.Empty, (ProductA, "PCS", 3m))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockInDetails);
        Assert.Equal(0L, Sequence(db));
    }

    [Fact]
    public async Task Create_无身份_拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockIn(SupplierA, null, string.Empty, (ProductA, "PCS", 3m))));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.StockIns);
        Assert.Equal(0L, Sequence(db));
    }

    [Fact]
    public async Task Create_非法来源_拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockIn(SupplierA, -1L, string.Empty, (ProductA, "PCS", 3m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockInDetails);
        Assert.Equal(0L, Sequence(db));
    }

    [Fact]
    public async Task Create_不存在供应商_拒绝且零写入零占号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewStockIn(999999L, null, string.Empty, (ProductA, "PCS", 3m))));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockInDetails);
        Assert.Equal(0L, Sequence(db));
    }

    // ==================== 4. 未选择来源 / 清空来源：未链接历史语义 ====================

    [Fact]
    public async Task Create_来源为0或null_都保存为未链接()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

        await ctl.Create(NewStockIn(SupplierA, 0L, string.Empty, (ProductA, "PCS", 1m)));
        await ctl.Create(NewStockIn(SupplierA, null, "  ", (ProductA, "PCS", 2m)));

        var saved = db.StockIns.OrderBy(o => o.Id).ToList();
        Assert.Equal(2, saved.Count);
        Assert.All(saved, s => Assert.Null(s.PurchaseOrderId));
        Assert.All(saved, s => Assert.StartsWith("RK", s.StockInNo));
    }

    [Fact]
    public async Task Update_清空已链接来源_保留服务端单号与状态()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));
        var order = SeedApprovedOrder(db, "PO-477-CLEAR", (ProductA, "PCS", 10m));

        var createdId = CreatedId(await ctl.Create(NewStockIn(SupplierA, order.Id, string.Empty, (ProductA, "PCS", 2m))));
        var created = db.StockIns.Single();
        var number = created.StockInNo;

        var edit = NewStockIn(SupplierA, null, "HACK-RK-UPD", (ProductA, "PCS", 1m));
        edit.Status = DocumentStatus.Approved;
        edit.IsDeleted = true;
        edit.Details[0].Id = 424242L;

        var result = await ctl.Update(createdId, edit);
        Assert.IsType<OkObjectResult>(result);

        var reloaded = Assert.IsType<StockIn>(Assert.IsType<ApiResponse<StockIn>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(createdId)).Value).Data!);
        Assert.Equal(number, reloaded.StockInNo);               // 单号不被客户端改写
        Assert.Equal(DocumentStatus.Pending, reloaded.Status);   // 状态不被客户端改写
        Assert.False(reloaded.IsDeleted);
        Assert.Null(reloaded.PurchaseOrderId);                   // 来源被显式清空 = 未链接
        var detail = Assert.Single(reloaded.Details);
        Assert.NotEqual(424242L, detail.Id);
        Assert.Equal(1m, detail.Quantity);
        Assert.Equal(1m, reloaded.TotalQuantity);
        Assert.Equal(created.Id, detail.StockInId);
    }

    // ==================== 5. 控制器接线：服务端归属先于单号与持久化 ====================

    [Fact]
    public void Controller_校验先于单号_身份与明细由服务端覆盖()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "StockInController.cs");

        var authorize = controller.IndexOf("StockInAuthorizationRules.EnsureAuthorizedAsync", StringComparison.Ordinal);
        var validateLink = controller.IndexOf("StockInOrderFulfillmentRules.ValidateLinkAsync", StringComparison.Ordinal);
        var generate = controller.IndexOf("entity.StockInNo = await _noService.GenerateAsync", StringComparison.Ordinal);
        Assert.True(authorize >= 0 && validateLink > authorize && generate > validateLink,
            "授权与来源 / 容量校验必须先于单号预约（被拒绝方绝不消耗单据号）");
        Assert.Contains("entity.Status = DocumentStatus.Pending;", controller);
        Assert.Contains("entity.IsDeleted = false;", controller);
        Assert.Contains("entity.UpdatedAt = null;", controller);
        Assert.Contains("ApplyServerOwnedDetailIdentity(entity.Details, 0, now);", controller);
        Assert.Contains("ApplyServerOwnedDetailIdentity(entity.Details, id, DateTime.Now);", controller);
        Assert.Contains("entity.Details ??= new List<StockInDetail>();", controller);

        // 明细身份由服务端统一重置（不采信客户端注入的主键 / 归属单据 / 删除态）。
        Assert.Contains("d.Id = 0;", controller);
        Assert.Contains("d.StockInId = stockInId;", controller);
        Assert.Contains("d.IsDeleted = false;", controller);
    }

    /// <summary>
    /// ERP-352：每一条读取 / 生命周期入口都必须先执行统一实时闸门（身份 / 账号状态 / 业务员映射 / 既有「采购入库」菜单），
    /// 闸门先于任何入库单字节读取、先于流水查询与任何行锁；分页列表在任何计数之前完成范围下推。
    /// </summary>
    [Fact]
    public void Controller_实时闸门先于任何单据读取与行锁()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "StockInController.cs");

        foreach (var signature in new[]
                 {
                     "public async Task<IActionResult> GetById(long id)",
                     "public async Task<IActionResult> GetMovements(long id)",
                     "public async Task<IActionResult> Create([FromBody] StockIn entity)",
                     "public async Task<IActionResult> Update(long id, [FromBody] StockIn entity)",
                     "public override async Task<IActionResult> Submit(long id)",
                     "public override async Task<IActionResult> Delete(long id)",
                     "public override async Task<IActionResult> Approve(long id)",
                     "public override async Task<IActionResult> Cancel(long id)"
                 })
        {
            var body = MethodBody(controller, signature);
            var gate = body.IndexOf("StockInAuthorizationRules.EnsureMenuAuthorizedAsync", StringComparison.Ordinal);
            var receiptRead = body.IndexOf("FirstOrDefaultAsync", StringComparison.Ordinal);
            Assert.True(gate >= 0, $"{signature} 必须先执行实时身份 / 菜单闸门");
            Assert.True(receiptRead < 0 || gate < receiptRead, $"{signature} 的闸门必须先于任何入库单字节读取");
        }

        // 行锁 / 流水查询同样在闸门与可见性判定之后。
        var cancel = MethodBody(controller, "public override async Task<IActionResult> Cancel(long id)");
        Assert.True(cancel.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal)
                    < cancel.IndexOf("LockSourceReceiptRowAsync(id)", StringComparison.Ordinal));
        var approve = MethodBody(controller, "public override async Task<IActionResult> Approve(long id)");
        Assert.True(approve.IndexOf("EnsureMenuAuthorizedAsync", StringComparison.Ordinal)
                    < approve.IndexOf("AcquireOrderApprovalLockAsync(entity.PurchaseOrderId)", StringComparison.Ordinal));

        // 分页列表在任何计数之前完成范围下推（闸门在 ApplyScopeAsync 内）。
        var paged = MethodBody(controller, "public async Task<IActionResult> GetPaged(");
        Assert.True(paged.IndexOf("StockInAuthorizationRules.ApplyScopeAsync", StringComparison.Ordinal)
                    < paged.IndexOf("CountAsync", StringComparison.Ordinal));

        // 绝无匿名 / 测试开关旁路：身份只来自已认证请求主体，缺失请求上下文安全解析为 null（受控未认证）。
        Assert.DoesNotContain("[AllowAnonymous]", controller);
        Assert.Contains("private long? CurrentInboundUserId()", controller);
        Assert.Contains("=> ControllerContext?.HttpContext is null ? null : CurrentUserId();", controller);
    }

    // ==================== 6. 菜单路由 / 规范模块 / 选择器接线契约 ====================

    [Fact]
    public void 菜单路由_采购入库走规范模块_不再落到旧存储过程族()
    {
        // ERP-477：菜单必须打开规范 EF 主子表模块（api/stock-ins，规范 Id），
        // 不得再经 BILL_CODE_MAP 落到旧存储过程族（其写路径已被 ERP-404 门禁整体停用）。
        var billV2 = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-v2.js");
        var mapStart = billV2.IndexOf("const BILL_CODE_MAP = {", StringComparison.Ordinal);
        var mapEnd = billV2.IndexOf("};", mapStart, StringComparison.Ordinal);
        Assert.True(mapStart >= 0 && mapEnd > mapStart);
        var billCodeMap = billV2[mapStart..mapEnd];
        // 注释里会说明「为何不再映射旧族」，因此这里只断言**不存在 stock-in 键映射**（与离线契约测试同口径）。
        Assert.DoesNotMatch(@"'stock-in'\s*:", billCodeMap);
        Assert.DoesNotMatch(@"stock-in\s*:\s*['""]", billCodeMap);

        var modules = ReadSource("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js");
        Assert.Contains("title: '采购入库', api: '/api/stock-ins'", modules);
        Assert.Contains("detailKey: 'details'", modules);
        Assert.Contains("detailFields: STOCK_IN_DETAIL_FIELDS", modules);
        Assert.Contains("nullable: true, selector: 'stock-in-source'", modules);

        // 旧族自身（旧库单体表 Oid 读侧）保持只读可用：不删除旧配置、不把 Oid 当规范 Id。
        var billConfig = ReadSource("src", "ERP.Api", "wwwroot", "js", "bill-config.js");
        Assert.Contains("billType: 'stock-in'", billConfig);
        Assert.DoesNotContain("/api/v2/bills", ReadSource("src", "ERP.Api", "wwwroot", "js", "crud.js"));
    }

    [Fact]
    public void 来源选择器_显式清空来源_保留未链接语义且不改写仓库()
    {
        var picker = ReadSource("src", "ERP.Api", "wwwroot", "js", "stock-in-source-picker.js");

        Assert.Contains("function sisClearSource()", picker);
        Assert.Contains("sisSetField('purchaseOrderId', '')", picker);
        Assert.Contains("sisClearSource,", picker);                       // 导出给离线契约测试
        Assert.DoesNotContain("sisSetField('warehouseId'", picker);        // 保留用户所选仓库
        Assert.DoesNotContain("sisSetField('supplierId', '')", picker);    // 清空来源绝不臆造 / 清空供应商
        Assert.Contains("function sisInstallFormHook()", picker);
        Assert.Contains("field.selector !== 'stock-in-source'", picker);
        Assert.Contains("toast(sisReadonlyReason(SIS.status)", picker);    // 非草稿状态只读
    }

    // ==================== 辅助 ====================

    private static void SeedMaster(ErpDbContext db)
    {
        StockInTestAuthorization.SeedSupplier(db, SupplierA, "源选择供应商");
        StockInTestAuthorization.SeedWarehouse(db, WarehouseA, "入库仓");
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductA, ProductCode = "P-477", ProductName = "商品A", Spec = "规格A", Unit = "PCS"
        });
        db.SaveChanges();
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockIn,
            RuleCode = "RK-477",
            RuleName = "采购入库单号",
            Prefix = "RK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static long SeedUserWithoutMenus(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"no-menu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private static PurchaseOrder SeedApprovedOrder(ErpDbContext db, string orderNo,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-3),
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, quantity) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
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

    private static StockIn NewStockIn(long supplier, long? purchaseOrderId, string stockInNo,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
        => new()
        {
            StockInNo = stockInNo,
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplier,
            WarehouseId = WarehouseA,
            Remark = "ERP-477_TEST",
            Details = lines.Select(l => new StockInDetail
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
            .Where(r => r.DocumentType == DocumentType.StockIn)
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

    /// <summary>截取控制器源码中某个路由方法（签名 → 下一个成员文档注释之前）的正文。</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到路由方法：{signature}");
        var end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
