using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-375 采购入库来源候选 / 详情与业务表单显式选择单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：有界只读候选（客户数据范围先于计数 / 取数、只含未删除且已审核来源、供应商筛选、有界关键字）、
/// 「来源采购订单 + 商品」聚合（授权数量按 ERP-342 折算基础单位，扣除已审核入库数量）、重复 / 歧义明细、
/// 单位未知、商品缺失、已收货满额显式不可用、来源详情 fail closed、显式选择保存后重开保留权威来源与部分数量、
/// 表单「未选择来源」（0）归一为 null 保留历史语义，以及复用既有「采购入库」+「采购订单」菜单与 ERP-371
/// 实时客户数据范围的 fail closed 授权（不新增用户授权、无管理员兜底）。</para>
/// </summary>
public class StockInSourceSelectionTests
{
    private const long SupplierA = 971001L;
    private const long SupplierB = 971002L;
    private const long WarehouseA = 971101L;
    private const long ProductA = 971201L;
    private const long ProductPack = 971202L;
    private const long ProductUnknown = 971203L;
    private const long MissingProduct = 971299L;

    // ==================== 播种辅助 ====================

    private static void SeedMaster(ErpDbContext db)
    {
        StockInTestAuthorization.SeedSupplier(db, SupplierA, "供应商A");
        StockInTestAuthorization.SeedSupplier(db, SupplierB, "供应商B");
        StockInTestAuthorization.SeedWarehouse(db, WarehouseA, "入库仓A");
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductA, ProductCode = "SI-A", ProductName = "商品A", Spec = "规格A", Unit = "PCS"
        });
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductPack, ProductCode = "SI-P", ProductName = "商品P", Spec = "规格P",
            Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12
        });
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductUnknown, ProductCode = "SI-U", ProductName = "商品U", Spec = "规格U", Unit = "PCS"
        });
        db.SaveChanges();
    }

    private static SysMenu SeedPurchaseOrderMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == "purchase-order" && !m.IsDeleted);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0, MenuCode = "purchase-order", MenuName = "采购订单",
            Path = "/purchase/order", Icon = "ShoppingCart", SortOrder = 10,
            MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void AttachPurchaseOrderMenu(ErpDbContext db, long userId)
    {
        var menu = SeedPurchaseOrderMenu(db);
        var roleId = db.SysUserRoles.Single(ur => ur.UserId == userId && !ur.IsDeleted).RoleId;
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>播种特权入库操作员（既有「采购入库」菜单 + 可选既有「采购订单」菜单）。</summary>
    private static long SeedPrivilegedOperator(ErpDbContext db, bool withPurchaseOrderMenu = true)
    {
        var userId = StockInTestAuthorization.SeedPrivilegedInboundOperator(db);
        if (withPurchaseOrderMenu) AttachPurchaseOrderMenu(db, userId);
        return userId;
    }

    /// <summary>播种受限制入库操作员（业务员映射 + 既有「采购入库」菜单 + 可选既有「采购订单」菜单）。</summary>
    private static (long UserId, long CustomerId) SeedRestrictedOperator(ErpDbContext db,
        bool withPurchaseOrderMenu = true)
    {
        var (userId, customerId) = StockInTestAuthorization.SeedRestrictedInboundOperator(db);
        if (withPurchaseOrderMenu) AttachPurchaseOrderMenu(db, userId);
        return (userId, customerId);
    }

    private static long SeedForeignCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-FOREIGN-{Guid.NewGuid():N}", CustomerName = "范围外客户", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer.Id;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, DocumentStatus status, long supplierId,
        long? owningCustomerId = null, bool deleted = false,
        params (long ProductId, string ProductName, string Spec, string Unit, decimal Quantity)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.USD,
            ExchangeRate = 7.1m,
            Status = status,
            IsDeleted = deleted,
            OwningCustomerId = owningCustomerId
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();

        foreach (var line in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                Spec = line.Spec,
                Unit = line.Unit,
                Quantity = line.Quantity,
                UnitPrice = 5m,
                Amount = line.Quantity * 5m
            });
        }
        db.SaveChanges();
        return order;
    }

    private static void SeedReceipt(ErpDbContext db, long orderId, long supplierId, DocumentStatus status,
        bool deleted, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            StockInNo = $"RK-SI-{Guid.NewGuid():N}"[..20],
            StockInDate = DateTime.Today,
            PurchaseOrderId = orderId,
            SupplierId = supplierId,
            WarehouseId = WarehouseA,
            Status = status,
            IsDeleted = deleted,
            TotalQuantity = lines.Sum(l => l.Quantity)
        };
        db.StockIns.Add(receipt);
        db.SaveChanges();

        foreach (var line in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = receipt.Id,
                ProductId = line.ProductId,
                ProductName = $"商品{line.ProductId}",
                Spec = "规格A",
                Unit = line.Unit,
                Quantity = line.Quantity
            });
        }
        db.SaveChanges();
    }

    private static void SeedStockInNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockIn,
            RuleCode = "RK-SISEL",
            RuleName = "采购入库单号",
            Prefix = "RK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    // ==================== 结果解析辅助 ====================

    private static List<StockInSourceCandidateDto> Candidates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<List<StockInSourceCandidateDto>>>(ok.Value).Data!;
    }

    private static StockInSourceDetailDto Detail(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<StockInSourceDetailDto>>(ok.Value).Data!;
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    // ==================== 1. 有界只读候选：权威字段 + ERP-342 剩余可收数量 ====================

    [Fact]
    public async Task 候选只含已审核未删除订单_展示权威字段与剩余可收()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        var order = SeedOrder(db, "PO-SI-0001", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        SeedReceipt(db, order.Id, SupplierA, DocumentStatus.Approved, false, (ProductA, "PCS", 3m));

        var line = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.Equal(order.Id, line.PurchaseOrderId);
        Assert.Equal("PO-SI-0001", line.OrderNo);
        Assert.Equal(SupplierA, line.SupplierId);
        Assert.Equal("供应商A", line.SupplierName);
        Assert.Equal(Currency.USD, line.Currency);
        Assert.Equal(7.1m, line.ExchangeRate);
        Assert.Equal(ProductA, line.ProductId);
        Assert.Equal("商品A", line.ProductName);
        Assert.Equal("规格A", line.Spec);
        Assert.Equal("PCS", line.BaseUnit);
        Assert.Equal(10m, line.AuthorizedBaseQuantity);
        Assert.Equal(3m, line.ReceivedBaseQuantity);
        Assert.Equal(7m, line.RemainingBaseQuantity);
        Assert.True(line.Available);
        Assert.Equal(string.Empty, line.UnavailableReason);

        // 只读投影：不写库、不改单据 / 库存 / 流水
        Assert.Equal(1, db.PurchaseOrders.Count());
        Assert.Equal(1, db.StockIns.Count());
        Assert.Empty(db.StockMovements);
    }

    // ==================== 2. 未审核 / 已取消 / 已删除订单不作为来源 ====================

    [Fact]
    public async Task 未审核取消删除订单不返回()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        SeedOrder(db, "PO-SI-PENDING", DocumentStatus.Pending, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        SeedOrder(db, "PO-SI-CANCELLED", DocumentStatus.Cancelled, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        SeedOrder(db, "PO-SI-DELETED", DocumentStatus.Approved, SupplierA, deleted: true,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });

        Assert.Empty(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
    }

    // ==================== 3. 重复歧义 / 单位未知 / 商品缺失显式不可用 ====================

    [Fact]
    public async Task 重复歧义与单位未知与商品缺失显式不可用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        var ambiguous = SeedOrder(db, "PO-SI-AMBIG", DocumentStatus.Approved, SupplierA,
            lines: new[]
            {
                (ProductA, "商品A", "规格A", "PCS", 4m),
                (ProductA, "商品A", "规格A", "PCS", 6m)
            });
        var unknownUnit = SeedOrder(db, "PO-SI-UNIT", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductUnknown, "商品U", "规格U", "袋", 5m) });
        var missing = SeedOrder(db, "PO-SI-MISS", DocumentStatus.Approved, SupplierA,
            lines: new[] { (MissingProduct, "缺失商品", "", "PCS", 5m) });

        var list = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(3, list.Count);
        Assert.All(list, l => Assert.False(l.Available));
        Assert.All(list, l => Assert.Equal(0m, l.RemainingBaseQuantity));

        var ambiguousLine = list.Single(l => l.PurchaseOrderId == ambiguous.Id);
        Assert.Equal(StockInOrderFulfillmentRules.AmbiguousLineText, ambiguousLine.UnavailableReason);
        Assert.Equal(0m, ambiguousLine.AuthorizedBaseQuantity);

        Assert.Equal(StockInOrderFulfillmentRules.UnknownUnitText,
            list.Single(l => l.PurchaseOrderId == unknownUnit.Id).UnavailableReason);
        Assert.Equal(StockInOrderFulfillmentRules.CorruptSourceText,
            list.Single(l => l.PurchaseOrderId == missing.Id).UnavailableReason);
    }

    // ==================== 4. 装箱单位折算与已收货满额 ====================

    [Fact]
    public async Task 装箱单位折算为基础单位_已收货满额归零()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        var order = SeedOrder(db, "PO-SI-PACK", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductPack, "商品P", "规格P", "BOX", 2m) });

        // 2 箱 × 12 = 24 基础单位，未收货 → 剩余 24
        var first = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.Equal(24m, first.AuthorizedBaseQuantity);
        Assert.Equal(24m, first.RemainingBaseQuantity);
        Assert.True(first.Available);

        // 已审核入库 24（基础单位）→ 满额不再可选
        SeedReceipt(db, order.Id, SupplierA, DocumentStatus.Approved, false, (ProductPack, "PCS", 24m));
        var second = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.False(second.Available);
        Assert.Equal(0m, second.RemainingBaseQuantity);
        Assert.Equal(StockInOrderFulfillmentRules.ZeroCapacityText, second.UnavailableReason);

        // 未审核 / 已删除入库不计入已收数量
        SeedReceipt(db, order.Id, SupplierA, DocumentStatus.Pending, false, (ProductPack, "PCS", 24m));
        SeedReceipt(db, order.Id, SupplierA, DocumentStatus.Approved, true, (ProductPack, "PCS", 24m));
        var third = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.Equal(24m, third.ReceivedBaseQuantity);
    }

    // ==================== 5. 来源详情 fail closed ====================

    [Fact]
    public async Task 来源详情fail_closed_未审核冲突与已删除不存在()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        var approved = SeedOrder(db, "PO-SI-DETAIL", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        SeedReceipt(db, approved.Id, SupplierA, DocumentStatus.Approved, false, (ProductA, "PCS", 3m));
        var pending = SeedOrder(db, "PO-SI-DETAIL-P", DocumentStatus.Pending, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        var deleted = SeedOrder(db, "PO-SI-DETAIL-D", DocumentStatus.Approved, SupplierA, deleted: true,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });

        var detail = Detail(await ctl.GetSourceCandidateDetail(approved.Id));
        Assert.Equal(approved.Id, detail.PurchaseOrderId);
        Assert.Equal("PO-SI-DETAIL", detail.OrderNo);
        Assert.Equal("供应商A", detail.SupplierName);
        Assert.True(detail.Available);
        Assert.Equal(7m, Assert.Single(detail.Lines).RemainingBaseQuantity);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(pending.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 只读投影：不写库
        Assert.Equal(3, db.PurchaseOrders.Count());
        Assert.Single(db.StockIns);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 6. 数据范围：范围外归属来源不出现 ====================

    [Fact]
    public async Task 范围外归属来源不出现在候选与详情()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var foreignCustomer = SeedForeignCustomer(db);
        var (userId, ownCustomer) = SeedRestrictedOperator(db);
        var ctl = StockInTestAuthorization.ForUser(db, userId);

        var own = SeedOrder(db, "PO-SI-OWN", DocumentStatus.Approved, SupplierA, ownCustomer,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        var foreign = SeedOrder(db, "PO-SI-FOREIGN", DocumentStatus.Approved, SupplierA, foreignCustomer,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });

        var list = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(own.Id, Assert.Single(list).PurchaseOrderId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(foreign.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 7. 授权 fail closed（无身份 / 缺既有采购订单菜单） ====================

    [Fact]
    public async Task 来源候选授权fail_closed()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedOrder(db, "PO-SI-AUTH", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });

        // 无身份 → 未认证
        var anonymous = StockInTestAuthorization.ForUser(db, null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => anonymous.GetSourceCandidates(null, null, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);

        // 有「采购入库」但缺既有「采购订单」菜单 → 权限不足
        var (userId, _) = SeedRestrictedOperator(db, withPurchaseOrderMenu: false);
        var noSourceMenu = StockInTestAuthorization.ForUser(db, userId);
        ex = await Assert.ThrowsAsync<BusinessException>(() => noSourceMenu.GetSourceCandidates(null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("采购订单", ex.Message);

        ex = await Assert.ThrowsAsync<BusinessException>(() => noSourceMenu.GetSourceCandidateDetail(1L));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 8. 显式选择 → 保存 → 重开：保留权威来源与部分数量 ====================

    [Fact]
    public async Task 显式选择保存后重开_保留权威来源与部分数量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStockInNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        var order = SeedOrder(db, "PO-SI-SAVE", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        SeedReceipt(db, order.Id, SupplierA, DocumentStatus.Approved, false, (ProductA, "PCS", 3m));

        // 业务表单显式选择来源：用详情行回填权威来源 Id / 单号 / 供应商与可收商品行
        var detail = Detail(await ctl.GetSourceCandidateDetail(order.Id));
        var line = Assert.Single(detail.Lines);
        Assert.Equal(7m, line.RemainingBaseQuantity);

        var create = new StockIn
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = detail.PurchaseOrderId,
            SupplierId = detail.SupplierId,
            WarehouseId = WarehouseA,          // 保留用户所选仓库
            Remark = "ERP-375_SOURCE_SELECT",
            Details = new List<StockInDetail>
            {
                new()
                {
                    ProductId = line.ProductId,
                    ProductName = line.ProductName,
                    Spec = line.Spec,
                    Unit = line.BaseUnit,      // 权威基础单位
                    Quantity = 2m              // 正数部分数量（≤ 剩余可收数量）
                }
            }
        };

        var id = CreatedId(await ctl.Create(create));

        // 重开：权威来源 Id / 供应商 / 仓库与明细原样保留
        var reopened = Assert.IsType<StockIn>(Assert.IsType<ApiResponse<StockIn>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(id)).Value).Data!);
        Assert.Equal(order.Id, reopened.PurchaseOrderId);
        Assert.Equal(SupplierA, reopened.SupplierId);
        Assert.Equal(WarehouseA, reopened.WarehouseId);
        var saved = Assert.Single(reopened.Details);
        Assert.Equal(line.ProductId, saved.ProductId);
        Assert.Equal("PCS", saved.Unit);
        Assert.Equal(2m, saved.Quantity);
        Assert.Equal(2m, reopened.TotalQuantity);

        // 待提交入库不计入「已审核入库」：剩余可收仍是 7（只在审核后才扣减），且未产生库存 / 流水
        var after = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.Equal(7m, after.RemainingBaseQuantity);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    // ==================== 9. 未选择来源（0）→ null，保留历史无来源语义 ====================

    [Fact]
    public async Task 未选择来源归一为null_负数仍拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStockInNumberRule(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));

        var unlinked = new StockIn
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = 0,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Details = new List<StockInDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS", Quantity = 1m }
            }
        };

        var id = CreatedId(await ctl.Create(unlinked));
        Assert.Null(db.StockIns.Single(s => s.Id == id).PurchaseOrderId);

        // 负数不是「未选择」，仍 fail closed 拒绝且不落库、不消耗单号
        var before = db.StockIns.Count();
        var negative = new StockIn
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = -1,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Details = new List<StockInDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS", Quantity = 1m }
            }
        };
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(negative));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(before, db.StockIns.Count());
    }

    // ==================== 10. 供应商筛选与关键字有界匹配 ====================

    [Fact]
    public async Task 供应商筛选与关键字有界匹配()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockInTestAuthorization.ForUser(db, SeedPrivilegedOperator(db));
        SeedOrder(db, "PO-SI-ALPHA", DocumentStatus.Approved, SupplierA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        SeedOrder(db, "PO-SI-BETA", DocumentStatus.Approved, SupplierB,
            lines: new[] { (ProductPack, "商品P", "规格P", "BOX", 1m) });

        var bySupplier = Candidates(await ctl.GetSourceCandidates(SupplierB, null, 0));
        Assert.Single(bySupplier);
        Assert.Equal(SupplierB, bySupplier[0].SupplierId);

        var byOrderNo = Candidates(await ctl.GetSourceCandidates(null, "ALPHA", 0));
        Assert.Single(byOrderNo);
        Assert.Equal("PO-SI-ALPHA", byOrderNo[0].OrderNo);

        var bySupplierName = Candidates(await ctl.GetSourceCandidates(null, "供应商B", 0));
        Assert.Single(bySupplierName);
        Assert.Equal(SupplierB, bySupplierName[0].SupplierId);

        var bySpec = Candidates(await ctl.GetSourceCandidates(null, "规格P", 0));
        Assert.Single(bySpec);
        Assert.Equal(ProductPack, bySpec[0].ProductId);
    }

    // ==================== 11. 候选条数与关键字钳制（有界，绝不无界拉取） ====================

    [Fact]
    public void 候选条数与关键字钳制()
    {
        Assert.Equal(StockInOrderFulfillmentRules.DefaultCandidateTake,
            StockInOrderFulfillmentRules.ClampTake(0));
        Assert.Equal(StockInOrderFulfillmentRules.DefaultCandidateTake,
            StockInOrderFulfillmentRules.ClampTake(-5));
        Assert.Equal(StockInOrderFulfillmentRules.MaxCandidateTake,
            StockInOrderFulfillmentRules.ClampTake(9999));
        Assert.Equal(20, StockInOrderFulfillmentRules.ClampTake(20));

        Assert.Equal(string.Empty, StockInOrderFulfillmentRules.NormalizeKeyword(null));
        Assert.Equal("abc", StockInOrderFulfillmentRules.NormalizeKeyword("  abc  "));
        Assert.Equal(StockInOrderFulfillmentRules.MaxKeywordLength,
            StockInOrderFulfillmentRules.NormalizeKeyword(new string('x', 500)).Length);
    }
}
