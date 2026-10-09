using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-427 规范采购订单「实时主数据引用」护栏单元测试（纯内存 + 直接实例化控制器，不连接 SQL Server、
/// 不执行任何 DDL / 部署脚本）：
/// <list type="number">
/// <item><b>唯一规则</b>：必填供应商（存在 / 未删除 / 启用）、每条有效明细的必填商品、可选采购员（在职员工）、
/// 可选起运港（Port 港口字典项）与既有有效单位口径（基础单位 / 合法装箱单位，绝不臆造换算）；</item>
/// <item><b>控制器行为</b>：新增 / 修改在单号预约与字段 / 明细赋值之前拒绝失效引用且零写入 / 零单号；
/// 提交 / 审核在采购订单行锁内重查引用，来源失效（供应商 / 商品被删除 / 停用）时原子拒绝、状态不变；
/// 合法手工采购完整生命周期（新增 → 修改 → 提交 → 审核）仍可用；历史引用失效订单的读取 / 打印完全只读、不被回填；</item>
/// <item><b>授权前置与非披露</b>：写入授权先于主数据校验，缺菜单 / 越界的受限账号即便提交无效引用也返回同一受控权限错误。</item>
/// </list>
/// <para>安全口径：全部使用内存库 <see cref="TestDbFactory"/>，不读取 appsettings / .env / 生产凭据，
/// 不执行 drop / reset，也不使用生产数据。</para>
/// </summary>
public class PurchaseOrderMasterReferenceTests
{
    private const long ProductA = 957402L;
    private const long CustomerA = 957301L;
    private const long CustomerB = 957302L;

    // ==================== 1. 必填供应商 ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task 供应商_非正整数_按参数错误拒绝(long supplierId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureSupplierAvailableAsync(db, supplierId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderMasterReferenceRules.SupplierRequiredText, ex.Message);
    }

    [Fact]
    public async Task 供应商_不存在_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureSupplierAvailableAsync(db, 974242L));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
    }

    [Fact]
    public async Task 供应商_已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, deleted: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureSupplierAvailableAsync(db, supplier.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 供应商_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureSupplierAvailableAsync(db, supplier.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 供应商_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db);

        var resolved = await PurchaseOrderMasterReferenceRules.EnsureSupplierAvailableAsync(db, supplier.Id);

        Assert.Equal(supplier.Id, resolved.Id);
        Assert.Equal(supplier.SupplierName, resolved.SupplierName);
    }

    // ==================== 2. 可选采购员 ====================

    [Fact]
    public async Task 采购员_未填写_跳过校验()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await PurchaseOrderMasterReferenceRules.EnsureBuyerAvailableAsync(db, null));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-2L)]
    public async Task 采购员_非正整数_按参数错误拒绝(long buyerId)
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureBuyerAvailableAsync(db, buyerId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderMasterReferenceRules.BuyerRequiredText, ex.Message);
    }

    [Fact]
    public async Task 采购员_已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var buyer = SeedBuyer(db, deleted: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureBuyerAvailableAsync(db, buyer.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 采购员_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var buyer = SeedBuyer(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureBuyerAvailableAsync(db, buyer.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("离职 / 停用", ex.Message);
    }

    [Fact]
    public async Task 采购员_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var buyer = SeedBuyer(db);

        var resolved = await PurchaseOrderMasterReferenceRules.EnsureBuyerAvailableAsync(db, buyer.Id);

        Assert.NotNull(resolved);
        Assert.Equal(buyer.Id, resolved!.Id);
    }

    // ==================== 3. 可选起运港 ====================

    [Fact]
    public async Task 起运港_未填写_跳过校验()
    {
        using var db = TestDbFactory.Create();

        Assert.Null(await PurchaseOrderMasterReferenceRules.EnsurePortAvailableAsync(db, null));
    }

    [Fact]
    public async Task 起运港_非正整数_按参数错误拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsurePortAvailableAsync(db, 0L));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderMasterReferenceRules.PortRequiredText, ex.Message);
    }

    [Fact]
    public async Task 起运港_已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        var port = SeedPort(db, deleted: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsurePortAvailableAsync(db, port.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 起运港_非港口字典项_按参数错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var other = SeedPort(db, infoType: "Currency");

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsurePortAvailableAsync(db, other.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不是港口字典项", ex.Message);
    }

    [Fact]
    public async Task 起运港_已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var port = SeedPort(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsurePortAvailableAsync(db, port.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 起运港_合法_返回实时实体()
    {
        using var db = TestDbFactory.Create();
        var port = SeedPort(db);

        var resolved = await PurchaseOrderMasterReferenceRules.EnsurePortAvailableAsync(db, port.Id);

        Assert.NotNull(resolved);
        Assert.Equal(port.Id, resolved!.Id);
    }

    // ==================== 4. 明细商品与既有有效单位口径 ====================

    [Fact]
    public async Task 明细_无有效行_跳过校验()
    {
        using var db = TestDbFactory.Create();

        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, null);
        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>());
        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
        {
            new() { ProductId = 0L, IsDeleted = true }
        });
    }

    [Fact]
    public async Task 明细_商品非正整数_按参数错误拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
            {
                new() { ProductId = 0L, Unit = "PCS" }
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(PurchaseOrderMasterReferenceRules.ProductRequiredText, ex.Message);
    }

    [Fact]
    public async Task 明细_商品不存在或已删除_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
            {
                new() { ProductId = 974888L, Unit = "PCS" }
            }));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var deleted = SeedProduct(db, deleted: true);
        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
            {
                new() { ProductId = deleted.Id, Unit = "PCS" }
            }));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);
    }

    [Fact]
    public async Task 明细_商品已停用_按规则冲突拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
            {
                new() { ProductId = product.Id, Unit = "PCS" }
            }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 明细_基础单位_放行()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS");

        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
        {
            new() { ProductId = product.Id, Unit = "PCS" }
        });
    }

    [Fact]
    public async Task 明细_合法装箱单位_放行_绝不臆造换算()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS", packageUnit: "BOX", unitsPerPackage: 12);

        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
        {
            new() { ProductId = product.Id, Unit = "BOX" }
        });

        var supported = PurchaseOrderMasterReferenceRules.SupportedUnits(product);
        Assert.Contains("PCS", supported);
        Assert.Contains("BOX", supported);
    }

    [Fact]
    public async Task 明细_装箱数不大于0_装箱单位不支持()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS", packageUnit: "BOX", unitsPerPackage: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
            {
                new() { ProductId = product.Id, Unit = "BOX" }
            }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不在既有有效单位口径内", ex.Message);
    }

    [Fact]
    public async Task 明细_商品未维护单位_不产生单位判定()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: string.Empty);

        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
        {
            new() { ProductId = product.Id, Unit = "任意单位" }
        });
    }

    [Fact]
    public async Task 明细_单位为空或不在口径内_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS");

        foreach (var unit in new[] { string.Empty, "SET" })
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
                {
                    new() { ProductId = product.Id, Unit = unit }
                }));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        }
    }

    [Fact]
    public async Task 明细_单位大小写与首尾空白_归一化后放行()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, unit: "PCS");

        Assert.True(PurchaseOrderMasterReferenceRules.IsSupportedUnit(product, " pcs "));

        await PurchaseOrderMasterReferenceRules.EnsureDetailProductsAsync(db, new List<PurchaseOrderDetail>
        {
            new() { ProductId = product.Id, Unit = " pcs " }
        });
    }

    [Fact]
    public async Task 完整复核_供应商标优先_失败整体拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var order = new PurchaseOrder
        {
            SupplierId = 974999L,
            Details = new List<PurchaseOrderDetail> { new() { ProductId = product.Id, Unit = "PCS" } }
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderMasterReferenceRules.EnsureMasterReferencesAsync(db, order));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("供应商", ex.Message);
    }

    // ==================== 5. 控制器：新增 / 修改（HTTP 请求管线） ====================

    [Fact]
    public async Task 新增_供应商缺失_受控拒绝且零写入零单号()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(974999L, product.Id, "PCS")));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Contains("供应商", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.PurchaseOrderDetails.AnyAsync());
        // 校验先于单号预约：单据字轨规则未被创建 / 未被消耗。
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_商品已删除或已停用_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var deleted = SeedProduct(db, deleted: true);
        var disabled = SeedProduct(db, status: 0);
        var ctl = HttpController(db, userId);

        var deletedEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, deleted.Id, "PCS")));
        Assert.Equal(ErrorCodes.NotFound, deletedEx.Code);

        var disabledEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, disabled.Id, "PCS")));
        Assert.Equal(ErrorCodes.RuleConflict, disabledEx.Code);

        Assert.False(await db.PurchaseOrders.AnyAsync());
        Assert.False(await db.SysDocumentNumberRules.AnyAsync());
    }

    [Fact]
    public async Task 新增_不支持的单位_受控拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db, unit: "PCS", packageUnit: "BOX", unitsPerPackage: 12);
        var ctl = HttpController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, product.Id, "KG")));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不在既有有效单位口径内", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());

        // 合法装箱单位（既有口径）照常放行，绝不臆造换算。
        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(supplier.Id, product.Id, "BOX")));
    }

    [Fact]
    public async Task 新增_可选采购员或起运港非法_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var disabledBuyer = SeedBuyer(db, status: 0);
        var disabledPort = SeedPort(db, status: 0);
        var ctl = HttpController(db, userId);

        var buyerEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS", buyerId: disabledBuyer.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, buyerEx.Code);

        var portEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS", portId: disabledPort.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, portEx.Code);

        var negativeEx = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS", buyerId: 0L)));
        Assert.Equal(ErrorCodes.InvalidParameter, negativeEx.Code);

        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 修改_商品被删除_受控拒绝且原始单据不被改动()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS")));
        var created = db.PurchaseOrders.AsNoTracking().Single();
        var createdLine = db.PurchaseOrderDetails.AsNoTracking().Single();
        Assert.Equal(50m, created.TotalAmount);            // 10 × 5

        product.IsDeleted = true;
        db.SaveChanges();

        var update = NewOrderBody(supplier.Id, product.Id, "PCS");
        update.Details[0].Quantity = 3m;
        update.Remark = "SHOULD_NOT_PERSIST";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(created.Id, update));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        var persisted = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == created.Id);
        Assert.NotEqual("SHOULD_NOT_PERSIST", persisted.Remark);
        Assert.Equal(50m, persisted.TotalAmount);
        var line = db.PurchaseOrderDetails.AsNoTracking().Single(d => d.PurchaseOrderId == created.Id);
        Assert.Equal(createdLine.Quantity, line.Quantity);
        Assert.Equal(createdLine.UnitPrice, line.UnitPrice);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task 提交_供应商提交前停用_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS")));
        var orderId = db.PurchaseOrders.AsNoTracking().Single().Id;

        supplier.Status = 0;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(orderId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Equal(DocumentStatus.Pending,
            db.PurchaseOrders.AsNoTracking().Single(o => o.Id == orderId).Status);
    }

    [Fact]
    public async Task 审核_商品审核前删除_原子拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS")));
        var orderId = db.PurchaseOrders.AsNoTracking().Single().Id;
        Assert.IsType<OkObjectResult>(await ctl.Submit(orderId));

        product.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(orderId));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(DocumentStatus.Submitted,
            db.PurchaseOrders.AsNoTracking().Single(o => o.Id == orderId).Status);
    }

    [Fact]
    public async Task 合法手工采购_完整生命周期_新增修改提交审核()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db, unit: "PCS", packageUnit: "BOX", unitsPerPackage: 12);
        var buyer = SeedBuyer(db);
        var port = SeedPort(db);
        var ctl = HttpController(db, userId);

        // 新增：合法供应商 / 商品 / 可选采购员 / 可选起运港。
        Assert.IsType<OkObjectResult>(await ctl.Create(
            NewOrderBody(supplier.Id, product.Id, "BOX", buyer.Id, port.Id)));
        var created = db.PurchaseOrders.AsNoTracking().Single();
        Assert.StartsWith("PO", created.OrderNo);
        Assert.Equal(DocumentStatus.Pending, created.Status);
        Assert.Equal(50m, created.TotalAmount);

        // 修改：仍为合法引用，明细整体替换且总额重算。
        var update = NewOrderBody(supplier.Id, product.Id, "PCS", buyer.Id, port.Id);
        update.Details[0].Quantity = 4m;
        update.Remark = "ERP427 改后";
        Assert.IsType<OkObjectResult>(await ctl.Update(created.Id, update));
        var updated = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == created.Id);
        Assert.Equal("ERP427 改后", updated.Remark);
        Assert.Equal(20m, updated.TotalAmount);            // 4 × 5

        // 提交 / 审核：引用仍实时有效。
        Assert.IsType<OkObjectResult>(await ctl.Submit(created.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(created.Id));
        Assert.Equal(DocumentStatus.Approved,
            db.PurchaseOrders.AsNoTracking().Single(o => o.Id == created.Id).Status);
    }

    [Fact]
    public async Task 历史读取_引用失效主数据_仍可读且不被回填()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS")));
        var orderId = db.PurchaseOrders.AsNoTracking().Single().Id;

        supplier.IsDeleted = true;
        product.IsDeleted = true;
        db.SaveChanges();

        var detail = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(orderId)).Value);
        Assert.Equal(supplier.Id, detail.Data!.SupplierId);
        Assert.Equal(product.Id, detail.Data.Details.Single().ProductId);

        var print = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(orderId)).Value);
        Assert.Equal(product.Id, print.Data!.Details.Single().ProductId);

        // 读取 / 打印绝不落库修正历史引用。
        var persisted = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == orderId);
        Assert.Equal(supplier.Id, persisted.SupplierId);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
    }

    // ==================== 6. 授权前置与非披露 ====================

    [Fact]
    public async Task 缺菜单受限账号_无效引用_返回同一受控权限错误且不披露主数据()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUserWithoutPurchaseOrderMenu(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(974999L, product.Id, "PCS")));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.DoesNotContain("供应商", ex.Message);
        Assert.False(await db.PurchaseOrders.AnyAsync());
    }

    [Fact]
    public async Task 越界归属客户_先于主数据披露且零写入()
    {
        using var db = TestDbFactory.Create();
        var role = SeedPurchaseOrderRole(db);
        var userId = SeedSalesman(db, role, CustomerA);
        SeedCustomer(db, CustomerB);
        var supplier = SeedSupplier(db);
        var product = SeedProduct(db);
        var ctl = HttpController(db, userId);

        // 越界归属客户：先于主数据 / 单位细节返回同一受控权限错误（非披露，不泄露供应商 / 商品是否存在）。
        var body = NewOrderBody(supplier.Id, product.Id, "PCS");
        body.OwningCustomerId = CustomerB;
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("数据范围", ex.Message);
        Assert.DoesNotContain("供应商", ex.Message);
        Assert.Empty(db.PurchaseOrders);

        // 受限账号的无归属备货采购同样 fail closed（先于主数据披露）。
        var unlinked = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewOrderBody(supplier.Id, product.Id, "PCS")));
        Assert.Equal(ErrorCodes.Forbidden, unlinked.Code);
        Assert.Equal(PurchaseOrderAuthorizationRules.UnlinkedDeniedText, unlinked.Message);
        Assert.Empty(db.PurchaseOrders);

        // 自有客户范围内仍可用（授权后行为不变）。
        var own = NewOrderBody(supplier.Id, product.Id, "PCS");
        own.OwningCustomerId = CustomerA;
        Assert.IsType<OkObjectResult>(await ctl.Create(own));
    }

    // ==================== 7. 控制器接线契约 ====================

    [Fact]
    public void 控制器_接入实时主数据引用复核_且规则文案自洽()
    {
        var controller = ReadSource("src/ERP.Api/Controllers/PurchaseOrderController.cs");
        Assert.Contains("PurchaseOrderMasterReferenceRules.EnsureMasterReferencesAsync", controller);
        Assert.Contains("RequiresLiveMasterValidation", controller);

        Assert.Contains("供应商", PurchaseOrderMasterReferenceRules.RuleText);
        Assert.Contains("商品", PurchaseOrderMasterReferenceRules.RuleText);
        Assert.Contains("单位", PurchaseOrderMasterReferenceRules.RuleText);
        Assert.Contains("不新增表", PurchaseOrderMasterReferenceRules.BoundaryText);
    }

    // ==================== 8. 脚手架与种子数据 ====================

    /// <summary>绑定到真实 HTTP 请求管线（<c>Request.Path</c> 已赋值）的控制器，使实时授权与主数据复核按真实请求口径生效。</summary>
    private static PurchaseOrderController HttpController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = "/api/purchase-orders";
        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static PurchaseOrder NewOrderBody(long supplierId, long productId, string unit,
        long? buyerId = null, long? portId = null)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            BuyerId = buyerId,
            PortId = portId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TaxRate = 0m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "ERP427 商品", Spec = "规格A",
                    Unit = unit, Quantity = 10m, UnitPrice = 5m
                }
            }
        };

    private static BaseSupplier SeedSupplier(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-MR-{Guid.NewGuid():N}", SupplierName = "ERP427 供应商",
            Status = status, IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, int status = 1, bool deleted = false,
        string unit = "PCS", string? packageUnit = null, int unitsPerPackage = 0)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-MR-{Guid.NewGuid():N}", ProductName = "ERP427 商品", Spec = "规格A",
            Unit = unit, PackageUnit = packageUnit ?? string.Empty, UnitsPerPackage = unitsPerPackage,
            Status = status, IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseEmployee SeedBuyer(ErpDbContext db, int status = 1, bool deleted = false)
    {
        var buyer = new BaseEmployee
        {
            EmployeeCode = $"E-MR-{Guid.NewGuid():N}", EmployeeName = "ERP427 采购员",
            Status = status, IsDeleted = deleted
        };
        db.BaseEmployees.Add(buyer);
        db.SaveChanges();
        return buyer;
    }

    private static BaseOtherInfo SeedPort(ErpDbContext db, int status = 1, bool deleted = false,
        string infoType = PurchaseOrderMasterReferenceRules.PortInfoType)
    {
        var port = new BaseOtherInfo
        {
            InfoType = infoType, InfoCode = $"PORT-MR-{Guid.NewGuid():N}", InfoName = "ERP427 起运港",
            Status = status, IsDeleted = deleted
        };
        db.BaseOtherInfos.Add(port);
        db.SaveChanges();
        return port;
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有 purchase-order 菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = SeedPurchaseOrderRole(db);
        role.IsSystem = true;
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    /// <summary>播种带既有 purchase-order 菜单的普通角色。</summary>
    private static SysRole SeedPurchaseOrderRole(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        var role = new SysRole { RoleCode = $"MR-{Guid.NewGuid():N}", RoleName = "ERP427 采购角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return role;
    }

    /// <summary>播种启用账号但只授予无关菜单（缺少既有 purchase-order 菜单）。</summary>
    private static long SeedUserWithoutPurchaseOrderMenu(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0, MenuCode = "stock-query", MenuName = "库存查询", Path = "/stock/query",
            MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        var role = new SysRole { RoleCode = $"MR-O-{Guid.NewGuid():N}", RoleName = "ERP427 其他角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return SeedUser(db, role, UserStatus.Enabled);
    }

    private static long SeedUser(ErpDbContext db, SysRole role, UserStatus status)
    {
        var user = new SysUser
        {
            UserName = $"mr-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP427 账号", Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>
    /// 播种受限采购业务员（登录账号 == 员工编码，ERP-097 权威映射），并把指定客户分配为其本人客户。
    /// </summary>
    private static long SeedSalesman(ErpDbContext db, SysRole? role, params long[] ownedCustomerIds)
    {
        var code = $"mr-s-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = "ERP427 采购业务员", IsSalesman = true, Status = 1
        };
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP427 采购业务员", Status = UserStatus.Enabled
        };
        db.BaseEmployees.Add(employee);
        db.SysUsers.Add(user);
        db.SaveChanges();

        if (role is not null)
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            db.SaveChanges();
        }

        foreach (var customerId in ownedCustomerIds)
        {
            var existing = db.BaseCustomers.FirstOrDefault(c => c.Id == customerId);
            if (existing is null)
                db.BaseCustomers.Add(new BaseCustomer
                {
                    Id = customerId, CustomerCode = $"C-{customerId}", CustomerName = $"客户{customerId}",
                    EmpId = employee.Id, Status = 1
                });
            else
                existing.EmpId = employee.Id;
            db.SaveChanges();
        }

        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, long? empId = null)
    {
        var existing = db.BaseCustomers.FirstOrDefault(c => c.Id == id);
        if (existing is not null) return existing;

        var customer = new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = $"客户{id}", EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }
}
