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
/// 采购退货测试身份 / 主数据脚手架（ERP-358）：为直接实例化 <see cref="PurchaseReturnController"/> 的单元测试
/// 注入<b>真实 HTTP 身份</b>，并播种既有「采购退货」（<c>purchase-return</c>）菜单 + 真实业务员账号与供应商 / 仓库主数据。
/// <para>不新增权限模型、不绕过鉴权、不使用管理员兜底：授权身份是「非特权账号」（既有菜单 + 真实员工映射），
/// 拒绝场景一律不播种菜单或播种禁用账号。</para>
/// </summary>
internal static class PurchaseReturnTestAuthorization
{
    /// <summary>播种既有 purchase-return 菜单（幂等），供角色 → 菜单授权复用。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == PurchaseReturnSourceRules.RequiredMenuCode);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseReturnSourceRules.RequiredMenuCode,
            MenuName = PurchaseReturnSourceRules.RequiredMenuText,
            Path = "/logistics/purchase-return",
            Icon = "package-plus",
            SortOrder = 70,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个<b>已授权的非特权账号</b>（既有菜单 + 启用账号 + 业务员映射 + 被分配客户）并返回其用户 Id。
    /// 该账号只能看到 <paramref name="customerIds"/> 中的客户（真实数据范围，非管理员兜底）。
    /// </summary>
    public static long SeedAuthorizedOperator(ErpDbContext db, params long[] customerIds)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = $"PR-{Guid.NewGuid():N}", RoleName = "采购退货操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"pr-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购退货操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = "采购退货操作员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        foreach (var customerId in customerIds)
        {
            var customer = db.BaseCustomers.FirstOrDefault(c => c.Id == customerId);
            if (customer is null)
            {
                customer = new BaseCustomer
                {
                    Id = customerId,
                    CustomerCode = $"PR-C-{customerId}",
                    CustomerName = $"客户{customerId}",
                    Status = 1
                };
                db.BaseCustomers.Add(customer);
            }
            customer.EmpId = employee.Id;
        }
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种一个已启用但<b>没有任何菜单授权</b>的账号（fail closed 场景）。</summary>
    public static long SeedUserWithoutMenu(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"pr-no-menu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种一个拥有菜单授权但<b>已禁用</b>的账号（fail closed 场景）。</summary>
    public static long SeedDisabledMenuUser(ErpDbContext db)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = $"PR-D-{Guid.NewGuid():N}", RoleName = "禁用操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"pr-disabled-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "禁用操作员",
            Status = UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    public static BaseWarehouse SeedWarehouse(ErpDbContext db, long id, string name)
    {
        var warehouse = new BaseWarehouse { Id = id, WarehouseCode = $"WH-{id}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        db.SaveChanges();
        return warehouse;
    }

    public static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string unit,
        string packageUnit = "", int unitsPerPackage = 0)
    {
        var product = new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = $"商品{code}",
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage,
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    public static BaseSupplier SeedSupplier(ErpDbContext db, long id, string name)
    {
        var supplier = new BaseSupplier { Id = id, SupplierCode = $"SUP-{id}", SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    /// <summary>播种一张已审核的来源采购入库单（含明细），用于退货来源证据。</summary>
    public static StockIn SeedApprovedStockIn(ErpDbContext db, long id, string no, long supplierId,
        long warehouseId, params (long ProductId, string Unit, decimal Quantity)[] lines)
        => SeedStockIn(db, id, no, supplierId, warehouseId, DocumentStatus.Approved, null, lines);

    /// <summary>播种来源采购入库单（可指定状态 / 归属采购订单），用于退货来源证据与上游客户范围。</summary>
    public static StockIn SeedStockIn(ErpDbContext db, long id, string no, long supplierId, long warehouseId,
        DocumentStatus status, long? purchaseOrderId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            Id = id,
            StockInNo = no,
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            PurchaseOrderId = purchaseOrderId,
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
        db.SaveChanges();
        return receipt;
    }

    /// <summary>播种归属客户的采购订单（上游客户范围证据）。</summary>
    public static PurchaseOrder SeedPurchaseOrder(ErpDbContext db, long id, long supplierId, long owningCustomerId)
    {
        var order = new PurchaseOrder
        {
            Id = id,
            OrderNo = $"PO-PR-{id}",
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            OwningCustomerId = owningCustomerId,
            OwningCustomerName = $"客户{owningCustomerId}"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    public static PurchaseReturnController ForUser(ErpDbContext db, long? userId)
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

    public static PurchaseReturnController CreateAuthorized(ErpDbContext db, params long[] customerIds)
        => ForUser(db, SeedAuthorizedOperator(db, customerIds));

}

/// <summary>
/// ERP-358 采购退货显式来源与可退容量单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：权威来源链接（存在 / 未删除 / 已审核 / 供应商一致 / 同仓库 / 快照一致 / 商品与基础单位证据）在
/// 创建 / 提交 / 审核的 fail closed 行为、未链接历史退货保持显式无来源、拒绝时不消耗单据号不写库、
/// 可退容量（精确边界 / 超退 / 重复商品行合计 / 后续行回滚）、销审释放容量、实时授权与上游客户数据范围。</para>
/// </summary>
public class PurchaseReturnSourceTests
{
    private const long SupplierA = 970301L;
    private const long SupplierB = 970302L;
    private const long CustomerA = 970001L;
    private const long CustomerB = 970002L;
    private const long WarehouseA = 970101L;
    private const long WarehouseB = 970102L;
    private const long ProductA = 970201L;
    private const long ProductPack = 970202L;
    private const long ProductB = 970203L;

    private static void SeedMaster(ErpDbContext db)
    {
        PurchaseReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "退货仓A");
        PurchaseReturnTestAuthorization.SeedWarehouse(db, WarehouseB, "退货仓B");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductA, "PR-A", "PCS");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductPack, "PR-P", "PCS", "BOX", 12);
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductB, "PR-B", "PCS");
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierA, "供应商A");
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierB, "供应商B");
    }

    /// <summary>播种可用库存（退货出库需要足够库存；均价 6）。</summary>
    private static void SeedStock(ErpDbContext db, long productId, decimal quantity = 100m)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = Math.Round(quantity * 6m, 4),
            AverageCost = 6m
        });
        db.SaveChanges();
    }

    private static PurchaseReturnDetail Line(long productId, decimal quantity, string unit = "PCS",
        decimal unitPrice = 10m, decimal unitCost = 5m)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = unitPrice,
            UnitCost = unitCost
        };

    private static PurchaseReturn Return(long? sourceId, string sourceNo, params PurchaseReturnDetail[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            SourceStockInId = sourceId,
            SourceStockInNo = sourceNo,
            ReturnReason = "规格不符",
            Remark = "PR_TEST",
            Details = lines.ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static void SeedNumberRule(ErpDbContext db, long currentSequence)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseReturn,
            RuleCode = "PR-TEST",
            RuleName = "采购退货单",
            Prefix = "CTH",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = currentSequence
        });
        db.SaveChanges();
    }

    private static PurchaseReturnController AuthorizedOperator(ErpDbContext db, params long[] customerIds)
        => PurchaseReturnTestAuthorization.CreateAuthorized(db,
            customerIds.Length == 0 ? new[] { CustomerA } : customerIds);

    // ==================== 1. 权威来源链接：创建 / 提交 ====================

    [Fact]
    public async Task 创建_关联已审核入库单_同供应商同仓库_保存成功并回填权威来源单号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0001", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var result = await ctl.Create(Return(601L, string.Empty, Line(ProductA, 4m)));

        var saved = db.PurchaseReturns.Include(o => o.Details).Single();
        Assert.Equal(CreatedId(result), saved.Id);
        Assert.Equal("RK-PR-0001", saved.SourceStockInNo);
        Assert.Equal(601L, saved.SourceStockInId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(4m, saved.TotalQuantity);
        Assert.Equal(1, saved.Details.Single().SortNo);
    }

    [Fact]
    public async Task 创建_未关联来源_保持历史行为_来源单号文本原样保留()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);

        var result = await ctl.Create(Return(null, "RK-LEGACY-TEXT", Line(ProductA, 4m)));

        var saved = db.PurchaseReturns.Single();
        Assert.Equal(CreatedId(result), saved.Id);
        Assert.Null(saved.SourceStockInId);
        Assert.Equal("RK-LEGACY-TEXT", saved.SourceStockInNo);   // 显式保留，绝不按文本猜测链接
    }

    [Fact]
    public async Task 创建_来源入库单不存在_拒绝且不消耗单据号不写库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db, 7);
        var ctl = AuthorizedOperator(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(999999L, string.Empty, Line(ProductA, 4m))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseReturns);
        Assert.Equal(7, db.SysDocumentNumberRules.Single().CurrentSequence);   // 拒绝不消耗单据号
    }

    [Fact]
    public async Task 创建_来源入库单未审核或已取消_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        var receipt = PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA,
            WarehouseA, (ProductA, "PCS", 10m));
        receipt.Status = DocumentStatus.Pending;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseReturns);

        receipt.Status = DocumentStatus.Cancelled;
        db.SaveChanges();
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 创建_来源入库单已删除_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        var receipt = PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA,
            WarehouseA, (ProductA, "PCS", 10m));
        receipt.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 创建_供应商或仓库与来源入库单不一致_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierB, WarehouseA,
            (ProductA, "PCS", 10m));

        // 供应商不一致（退货单为 SupplierA，来源入库单为 SupplierB）
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("供应商", ex.Message);

        // 仓库不一致（来源入库单入库仓为 WarehouseB）
        var receipt = db.StockIns.Single();
        receipt.SupplierId = SupplierA;
        receipt.WarehouseId = WarehouseB;
        db.SaveChanges();
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("仓库", ex.Message);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 创建_来源单号快照与权威来源冲突_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, "RK-WRONG-NO", Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不一致", ex.Message);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 创建_商品不在来源入库单或无入库证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductB, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 创建_单位无法折算为基础单位_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m, unit: "CTN"))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 创建_装箱单位按基础单位折算并可用于容量校验()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        // 来源入库 12 PCS（= 1 箱）
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductPack, "PCS", 12m));

        var id = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductPack, 1m, unit: "BOX"))));

        var saved = db.PurchaseReturns.Include(o => o.Details).Single(o => o.Id == id);
        Assert.Equal("PCS", saved.Details.Single().Unit);
        Assert.Equal(12m, saved.Details.Single().Quantity);   // 1 箱 → 12 PCS
        Assert.Equal(12m, saved.TotalQuantity);
    }

    [Fact]
    public async Task 提交_来源入库单被取消后_拒绝提交且状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        var receipt = PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA,
            WarehouseA, (ProductA, "PCS", 10m));
        var id = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));

        receipt.Status = DocumentStatus.Cancelled;      // 来源在窗口内被取消
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.PurchaseReturns.Single(o => o.Id == id).Status);
    }

    // ==================== 2. 可退容量：审核 ====================

    [Fact]
    public async Task 审核_未超来源入库数量_退货出库恰好一次()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var id = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 4m, unitCost: 8m))));
        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA);
        Assert.Equal(96m, stock.Quantity);                     // 100 - 4
        var entity = db.PurchaseReturns.Single();
        Assert.Equal(DocumentStatus.Approved, entity.Status);
        Assert.Equal(40m, entity.TotalAmount);                 // 4 × 10
        Assert.Equal("RK-PR-0601", entity.SourceStockInNo);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.PurchaseReturn, movement.MovementType);
        Assert.Equal(-1, movement.Direction);
        Assert.Equal(8m, movement.UnitCost);                   // 明细成本优先
        Assert.Equal(-32m, movement.Amount);

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Single(db.StockMovements);
        Assert.Equal(96m, db.Stocks.Single().Quantity);
    }

    [Fact]
    public async Task 审核_跨单据累计超退_拒绝且库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var first = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 6m, unitCost: 5m))));
        await ctl.Submit(first);
        await ctl.Approve(first);

        var second = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 5m, unitCost: 5m))));
        await ctl.Submit(second);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过", ex.Message);

        Assert.Equal(94m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Single(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseReturns.Single(o => o.Id == second).Status);
    }

    [Fact]
    public async Task 审核_精确边界_恰好等于已审核入库数量_允许()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var first = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 6m, unitCost: 5m))));
        await ctl.Submit(first);
        await ctl.Approve(first);
        var second = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 4m, unitCost: 5m))));
        await ctl.Submit(second);
        await ctl.Approve(second);                                        // 6 + 4 == 10：允许

        Assert.Equal(90m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseReturns.Single(o => o.Id == second).Status);
    }

    [Fact]
    public async Task 审核_同一退货单重复商品行按基础单位合计超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 5m));

        var id = CreatedId(await ctl.Create(Return(601L, string.Empty,
            Line(ProductA, 3m, unitCost: 5m), Line(ProductA, 3m, unitCost: 5m))));
        await ctl.Submit(id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockMovements);
        Assert.Equal(100m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);   // 不产生任何出库
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseReturns.Single().Status);
    }

    [Fact]
    public async Task 审核_后续行超限_整单回滚不产生部分出库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        SeedStock(db, ProductB);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 5m), (ProductB, "PCS", 5m));

        var id = CreatedId(await ctl.Create(Return(601L, string.Empty,
            Line(ProductA, 4m, unitCost: 5m), Line(ProductB, 9m, unitCost: 5m))));
        await ctl.Submit(id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockMovements);                                  // 第一行也不得出库
        Assert.Equal(100m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(100m, db.Stocks.Single(s => s.ProductId == ProductB).Quantity);
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseReturns.Single().Status);
    }

    [Fact]
    public async Task 审核_来源入库单供应商被改_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        var receipt = PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA,
            WarehouseA, (ProductA, "PCS", 10m));

        var id = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 4m, unitCost: 5m))));
        await ctl.Submit(id);

        receipt.SupplierId = SupplierB;                  // 来源被外部改动
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.StockMovements);
        Assert.Equal(100m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseReturns.Single().Status);
    }

    [Fact]
    public async Task 审核_数值为负的损坏退货证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var id = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 4m, unitCost: 5m))));
        await ctl.Submit(id);

        var detail = db.PurchaseReturnDetails.Single(d => d.PurchaseReturnId == id);
        detail.Quantity = -1m;                        // 直接制造损坏证据
        db.SaveChanges();

        await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Empty(db.StockMovements);
        Assert.Equal(100m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task 销审_释放可退容量_后续退货可再次审核()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 10m));

        var first = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 6m, unitCost: 5m))));
        await ctl.Submit(first);
        await ctl.Approve(first);
        var second = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 5m, unitCost: 5m))));
        await ctl.Submit(second);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second));

        await ctl.Unaudit(first);                     // 释放 6 → 已审核退货归零
        Assert.Equal(100m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Pending, db.PurchaseReturns.Single(o => o.Id == first).Status);
        Assert.Equal(2, db.StockMovements.Count());   // 原流水 + 红字冲销，历史保留

        await ctl.Approve(second);                    // 此时 5 ≤ 10：允许
        Assert.Equal(95m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    // ==================== 3. 实时授权与上游客户数据范围 ====================

    [Fact]
    public async Task 授权_无身份_按未认证拒绝且不写库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        PurchaseReturnTestAuthorization.SeedMenu(db);
        var ctl = PurchaseReturnTestAuthorization.ForUser(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(null, string.Empty, Line(ProductA, 1m))));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 授权_账号已禁用或无菜单_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var disabled = PurchaseReturnTestAuthorization.ForUser(db,
            PurchaseReturnTestAuthorization.SeedDisabledMenuUser(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            disabled.Create(Return(null, string.Empty, Line(ProductA, 1m))));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        var noMenu = PurchaseReturnTestAuthorization.ForUser(db,
            PurchaseReturnTestAuthorization.SeedUserWithoutMenu(db));
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            noMenu.Create(Return(null, string.Empty, Line(ProductA, 1m))));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        Assert.Empty(db.PurchaseReturns);
    }

    [Fact]
    public async Task 授权_撤销菜单后下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var operatorId = PurchaseReturnTestAuthorization.SeedAuthorizedOperator(db, CustomerA);
        var ctl = PurchaseReturnTestAuthorization.ForUser(db, operatorId);

        var id = CreatedId(await ctl.Create(Return(null, string.Empty, Line(ProductA, 1m))));

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.PurchaseReturns.Single().Status);
    }

    [Fact]
    public async Task 上游客户范围_来源入库单归属客户不在当前账号范围内_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db, CustomerA);         // 只可见 CustomerA
        var order = PurchaseReturnTestAuthorization.SeedPurchaseOrder(db, 701L, SupplierA, CustomerB);
        PurchaseReturnTestAuthorization.SeedStockIn(db, 601L, "RK-PR-0601", SupplierA, WarehouseA,
            DocumentStatus.Approved, order.Id, (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.PurchaseReturns);

        // 归属客户落在范围内时放行（不放大范围，也不阻断合法业务）
        order.OwningCustomerId = CustomerA;
        db.SaveChanges();
        var id = CreatedId(await ctl.Create(Return(601L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(601L, db.PurchaseReturns.Single(o => o.Id == id).SourceStockInId);
    }

    // ==================== 4. 未链接历史退货保持可用 ====================

    [Fact]
    public async Task 未链接历史退货_仍可提交审核出库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA);
        var ctl = AuthorizedOperator(db);

        var id = CreatedId(await ctl.Create(Return(null, "RK-LEGACY-0001", Line(ProductA, 3m, unitCost: 4m))));
        await ctl.Submit(id);
        await ctl.Approve(id);

        var entity = db.PurchaseReturns.Single();
        Assert.Equal(DocumentStatus.Approved, entity.Status);
        Assert.Null(entity.SourceStockInId);
        Assert.Equal("RK-LEGACY-0001", entity.SourceStockInNo);
        Assert.Equal(97m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Single(db.StockMovements);
    }

    // ==================== 5. 规则层纯函数 ====================

    [Fact]
    public void 基础单位折算_空单位或基础单位原样_装箱单位按装箱数折算_其他拒绝()
    {
        var product = new BaseProduct { Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12 };

        Assert.True(PurchaseReturnSourceRules.TryBaseUnitQuantity(product, "PCS", 5m, out var base1));
        Assert.Equal(5m, base1);
        Assert.True(PurchaseReturnSourceRules.TryBaseUnitQuantity(product, string.Empty, 5m, out var base2));
        Assert.Equal(5m, base2);
        Assert.True(PurchaseReturnSourceRules.TryBaseUnitQuantity(product, "box", 2m, out var base3));
        Assert.Equal(24m, base3);
        Assert.False(PurchaseReturnSourceRules.TryBaseUnitQuantity(product, "CTN", 2m, out _));

        var noBaseUnit = new BaseProduct { Unit = string.Empty, PackageUnit = "BOX", UnitsPerPackage = 12 };
        Assert.False(PurchaseReturnSourceRules.TryBaseUnitQuantity(noBaseUnit, "BOX", 1m, out _));
    }
}
