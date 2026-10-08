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
/// 销售退货测试身份 / 主数据脚手架（ERP-357）：为直接实例化 <see cref="SalesReturnController"/> 的单元测试
/// 注入<b>真实 HTTP 身份</b>，并播种既有「销售退货」（<c>sales-return</c>）菜单 + 真实业务员账号与客户数据范围。
/// <para>不新增权限模型、不绕过鉴权、不使用管理员兜底：授权身份是「非特权业务员账号」，
/// 其可见客户严格等于被分配的客户；拒绝场景一律不播种菜单或播种禁用账号。</para>
/// </summary>
internal static class SalesReturnTestAuthorization
{
    /// <summary>播种既有 sales-return 菜单（幂等），供角色 → 菜单授权复用。</summary>
    public static SysMenu SeedMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == SalesReturnSourceRules.RequiredMenuCode);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = SalesReturnSourceRules.RequiredMenuCode,
            MenuName = SalesReturnSourceRules.RequiredMenuText,
            Path = "/logistics/sales-return",
            Icon = "package-minus",
            SortOrder = 60,
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>
    /// 播种一个<b>已授权的非特权业务员</b>（既有菜单 + 启用账号 + 业务员映射 + 被分配客户）并返回其用户 Id。
    /// 该账号只能看到 <paramref name="customerIds"/> 中的客户（真实数据范围，非管理员兜底）。
    /// </summary>
    public static long SeedAuthorizedSalesman(ErpDbContext db, params long[] customerIds)
    {
        var menu = SeedMenu(db);
        var role = new SysRole { RoleCode = $"SR-{Guid.NewGuid():N}", RoleName = "销售退货操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var userName = $"sr-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "销售退货操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = "销售退货操作员",
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
                    CustomerCode = $"SR-C-{customerId}",
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
            UserName = $"sr-no-menu-{Guid.NewGuid():N}",
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
        var role = new SysRole { RoleCode = $"SR-D-{Guid.NewGuid():N}", RoleName = "禁用操作员" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"sr-disabled-{Guid.NewGuid():N}",
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

    /// <summary>播种一张已审核的来源销售出库单（含明细），用于退货来源证据。</summary>
    public static StockOut SeedApprovedStockOut(ErpDbContext db, long id, string no, long customerId,
        long warehouseId, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            Id = id,
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = DocumentStatus.Approved,
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
        db.SaveChanges();
        return shipment;
    }

    public static SalesReturnController ForUser(ErpDbContext db, long? userId)
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

    public static SalesReturnController CreateAuthorized(ErpDbContext db, params long[] customerIds)
        => ForUser(db, SeedAuthorizedSalesman(db, customerIds));

}

/// <summary>
/// ERP-357 销售退货显式来源与可退容量单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：权威来源链接（存在 / 未删除 / 已审核 / 客户一致 / 同仓库 / 快照一致 / 商品与基础单位证据）在
/// 创建 / 提交 / 审核的 fail closed 行为、未链接历史退货保持显式无来源、拒绝时不消耗单据号不写库、
/// 可退容量（精确边界 / 超退 / 重复商品行合计 / 后续行回滚）、销审释放容量、实时授权与客户数据范围。</para>
/// </summary>
public class SalesReturnSourceTests
{
    private const long CustomerA = 970001L;
    private const long CustomerB = 970002L;
    private const long WarehouseA = 970101L;
    private const long WarehouseB = 970102L;
    private const long ProductA = 970201L;
    private const long ProductPack = 970202L;
    private const long ProductB = 970203L;

    private static void SeedMaster(ErpDbContext db)
    {
        SalesReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "退货仓A");
        SalesReturnTestAuthorization.SeedWarehouse(db, WarehouseB, "退货仓B");
        SalesReturnTestAuthorization.SeedProduct(db, ProductA, "SR-A", "PCS");
        SalesReturnTestAuthorization.SeedProduct(db, ProductPack, "SR-P", "PCS", "BOX", 12);
        SalesReturnTestAuthorization.SeedProduct(db, ProductB, "SR-B", "PCS");
    }

    private static SalesReturnDetail Line(long productId, decimal quantity, string unit = "PCS",
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

    private static SalesReturn Return(long? sourceId, string sourceNo, params SalesReturnDetail[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = sourceId,
            SourceStockOutNo = sourceNo,
            ReturnReason = "质量",
            Remark = "SR_TEST",
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
            DocumentType = DocumentType.SalesReturn,
            RuleCode = "SR-TEST",
            RuleName = "销售退货单",
            Prefix = "XTH",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = currentSequence
        });
        db.SaveChanges();
    }

    private static SalesReturnController AuthorizedOperator(ErpDbContext db, params long[] customerIds)
        => SalesReturnTestAuthorization.CreateAuthorized(db,
            customerIds.Length == 0 ? new[] { CustomerA } : customerIds);

    // ==================== 1. 权威来源链接：创建 ====================

    [Fact]
    public async Task 创建_关联已审核出库单_同客户同仓库_保存成功并回填权威来源单号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0001", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var result = await ctl.Create(Return(501L, string.Empty, Line(ProductA, 4m)));

        var saved = db.SalesReturns.Include(o => o.Details).Single();
        Assert.Equal(CreatedId(result), saved.Id);
        Assert.Equal("CK-SR-0001", saved.SourceStockOutNo);
        Assert.Equal(501L, saved.SourceStockOutId);
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

        var result = await ctl.Create(Return(null, "CK-LEGACY-TEXT", Line(ProductA, 4m)));

        var saved = db.SalesReturns.Single();
        Assert.Equal(CreatedId(result), saved.Id);
        Assert.Null(saved.SourceStockOutId);
        Assert.Equal("CK-LEGACY-TEXT", saved.SourceStockOutNo);   // 显式保留，绝不按文本猜测链接
    }

    [Fact]
    public async Task 创建_来源出库单不存在_拒绝且不消耗单据号不写库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db, 7);
        var ctl = AuthorizedOperator(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(999999L, string.Empty, Line(ProductA, 4m))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesReturns);
        Assert.Equal(7, db.SysDocumentNumberRules.Single().CurrentSequence);   // 拒绝不消耗单据号
    }

    [Fact]
    public async Task 创建_来源出库单未审核或已取消_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        var pending = SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA,
            WarehouseA, (ProductA, "PCS", 10m));
        pending.Status = DocumentStatus.Pending;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(501L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesReturns);

        pending.Status = DocumentStatus.Cancelled;
        db.SaveChanges();
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(501L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 创建_来源出库单已删除_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        var shipment = SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA,
            WarehouseA, (ProductA, "PCS", 10m));
        shipment.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(501L, string.Empty, Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 创建_客户或仓库与来源出库单不一致_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db, CustomerA, CustomerB);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        // 客户不一致
        var foreign = Return(501L, string.Empty, Line(ProductA, 2m));
        foreign.CustomerId = CustomerB;
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(foreign));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("客户", ex.Message);

        // 仓库不一致
        var wrongWarehouse = Return(501L, string.Empty, Line(ProductA, 2m));
        wrongWarehouse.WarehouseId = WarehouseB;
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(wrongWarehouse));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("仓库", ex.Message);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 创建_来源单号快照与权威来源冲突_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(501L, "CK-WRONG-NO", Line(ProductA, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不一致", ex.Message);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 创建_商品不在来源出库单或无出库证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(501L, string.Empty, Line(ProductB, 2m))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 创建_单位无法折算为基础单位_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(501L, string.Empty, Line(ProductA, 2m, unit: "CTN"))));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 创建_装箱单位按基础单位折算并可用于容量校验()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        // 来源出库 12 PCS（= 1 箱）
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductPack, "PCS", 12m));

        var id = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductPack, 1m, unit: "BOX"))));

        var saved = db.SalesReturns.Include(o => o.Details).Single(o => o.Id == id);
        Assert.Equal("PCS", saved.Details.Single().Unit);
        Assert.Equal(12m, saved.Details.Single().Quantity);   // 1 箱 → 12 PCS
        Assert.Equal(12m, saved.TotalQuantity);
    }


    // ==================== 2. 可退容量：审核 ====================

    [Fact]
    public async Task 审核_未超来源出库数量_退货入库恰好一次()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var id = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 4m, unitCost: 8m))));
        await ctl.Submit(id);
        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA);
        Assert.Equal(4m, stock.Quantity);
        Assert.Equal(32m, stock.TotalCost);
        Assert.Equal(8m, stock.AverageCost);

        var entity = db.SalesReturns.Single();
        Assert.Equal(DocumentStatus.Approved, entity.Status);
        Assert.Equal(40m, entity.TotalAmount);            // 4 × 10
        Assert.Equal("CK-SR-0501", entity.SourceStockOutNo);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryMovementType.SalesReturn, movement.MovementType);
        Assert.Equal(1, movement.Direction);
        Assert.Equal(8m, movement.UnitCost);
        Assert.Equal(32m, movement.Amount);

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Single(db.StockMovements);
        Assert.Equal(4m, db.Stocks.Single().Quantity);
    }

    [Fact]
    public async Task 审核_跨单据累计超退_拒绝且库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var first = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 6m, unitCost: 5m))));
        await ctl.Submit(first);
        await ctl.Approve(first);

        var second = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 5m, unitCost: 5m))));
        await ctl.Submit(second);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("超过", ex.Message);

        Assert.Equal(6m, db.Stocks.Single().Quantity);
        Assert.Single(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.SalesReturns.Single(o => o.Id == second).Status);
    }

    [Fact]
    public async Task 审核_精确边界_恰好等于已审核出库数量_允许()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var first = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 6m, unitCost: 5m))));
        await ctl.Submit(first);
        await ctl.Approve(first);
        var second = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 4m, unitCost: 5m))));
        await ctl.Submit(second);
        await ctl.Approve(second);                                        // 6 + 4 == 10：允许

        Assert.Equal(10m, db.Stocks.Single().Quantity);
        Assert.Equal(DocumentStatus.Approved, db.SalesReturns.Single(o => o.Id == second).Status);
    }


    [Fact]
    public async Task 审核_同一退货单重复商品行按基础单位合计超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 5m));

        var id = CreatedId(await ctl.Create(Return(501L, string.Empty,
            Line(ProductA, 3m, unitCost: 5m), Line(ProductA, 3m, unitCost: 5m))));
        await ctl.Submit(id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.SalesReturns.Single().Status);
    }

    [Fact]
    public async Task 审核_后续行超限_整单回滚不产生部分库存()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 5m), (ProductB, "PCS", 5m));

        var id = CreatedId(await ctl.Create(Return(501L, string.Empty,
            Line(ProductA, 4m, unitCost: 5m), Line(ProductB, 9m, unitCost: 5m))));
        await ctl.Submit(id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Stocks);                                  // 第一行也不得入库
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.SalesReturns.Single().Status);
    }

    [Fact]
    public async Task 审核_来源出库单客户被改_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db, CustomerA);
        var shipment = SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA,
            WarehouseA, (ProductA, "PCS", 10m));

        var id = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 4m, unitCost: 5m))));
        await ctl.Submit(id);

        shipment.CustomerId = CustomerB;               // 来源被外部改动
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.Stocks);
        Assert.Equal(DocumentStatus.Submitted, db.SalesReturns.Single().Status);
    }

    [Fact]
    public async Task 销审_释放可退容量_后续退货可再次审核()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var first = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 6m, unitCost: 5m))));
        await ctl.Submit(first);
        await ctl.Approve(first);
        var second = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 5m, unitCost: 5m))));
        await ctl.Submit(second);
        await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(second));

        await ctl.Unaudit(first);                     // 释放 6 → 已审核退货归零
        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(DocumentStatus.Pending, db.SalesReturns.Single(o => o.Id == first).Status);
        Assert.Equal(2, db.StockMovements.Count());   // 原流水 + 红字冲销，历史保留

        await ctl.Approve(second);                    // 此时 5 ≤ 10：允许
        Assert.Equal(5m, db.Stocks.Single().Quantity);
    }

    [Fact]
    public async Task 审核_数值为负的损坏退货证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, 501L, "CK-SR-0501", CustomerA, WarehouseA,
            (ProductA, "PCS", 10m));

        var id = CreatedId(await ctl.Create(Return(501L, string.Empty, Line(ProductA, 4m, unitCost: 5m))));
        await ctl.Submit(id);

        var detail = db.SalesReturnDetails.Single(d => d.SalesReturnId == id);
        detail.Quantity = -1m;                        // 直接制造损坏证据
        db.SaveChanges();

        await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }


    // ==================== 3. 实时授权与客户数据范围 ====================

    [Fact]
    public async Task 授权_无身份_按未认证拒绝且不写库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SalesReturnTestAuthorization.SeedMenu(db);
        var ctl = SalesReturnTestAuthorization.ForUser(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Return(null, string.Empty, Line(ProductA, 1m))));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 授权_账号已禁用或无菜单_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var disabled = SalesReturnTestAuthorization.ForUser(db, SalesReturnTestAuthorization.SeedDisabledMenuUser(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            disabled.Create(Return(null, string.Empty, Line(ProductA, 1m))));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        var noMenu = SalesReturnTestAuthorization.ForUser(db, SalesReturnTestAuthorization.SeedUserWithoutMenu(db));
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            noMenu.Create(Return(null, string.Empty, Line(ProductA, 1m))));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        Assert.Empty(db.SalesReturns);
    }

    [Fact]
    public async Task 授权_撤销菜单后下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var operatorId = SalesReturnTestAuthorization.SeedAuthorizedSalesman(db, CustomerA);
        var ctl = SalesReturnTestAuthorization.ForUser(db, operatorId);

        var id = CreatedId(await ctl.Create(Return(null, string.Empty, Line(ProductA, 1m))));

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.SalesReturns.Single().Status);
    }

    [Fact]
    public async Task 数据范围_客户不在当前账号范围内_按不存在拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db, CustomerA);      // 只可见 CustomerA

        var foreign = Return(null, string.Empty, Line(ProductA, 1m));
        foreign.CustomerId = CustomerB;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(foreign));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.SalesReturns);
    }

    // ==================== 4. 未链接历史退货保持可用 ====================

    [Fact]
    public async Task 未链接历史退货_仍可提交审核入库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = AuthorizedOperator(db);

        var id = CreatedId(await ctl.Create(Return(null, "CK-LEGACY-0001", Line(ProductA, 3m, unitCost: 4m))));
        await ctl.Submit(id);
        await ctl.Approve(id);

        var entity = db.SalesReturns.Single();
        Assert.Equal(DocumentStatus.Approved, entity.Status);
        Assert.Null(entity.SourceStockOutId);
        Assert.Equal("CK-LEGACY-0001", entity.SourceStockOutNo);
        Assert.Equal(3m, db.Stocks.Single().Quantity);
        Assert.Single(db.StockMovements);
    }

    // ==================== 5. 规则层纯函数 ====================

    [Fact]
    public void 基础单位折算_空单位或基础单位原样_装箱单位按装箱数折算_其他拒绝()
    {
        var product = new BaseProduct { Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12 };

        Assert.True(SalesReturnSourceRules.TryBaseUnitQuantity(product, "PCS", 5m, out var base1));
        Assert.Equal(5m, base1);
        Assert.True(SalesReturnSourceRules.TryBaseUnitQuantity(product, string.Empty, 5m, out var base2));
        Assert.Equal(5m, base2);
        Assert.True(SalesReturnSourceRules.TryBaseUnitQuantity(product, "box", 2m, out var base3));
        Assert.Equal(24m, base3);
        Assert.False(SalesReturnSourceRules.TryBaseUnitQuantity(product, "CTN", 2m, out _));

        var noBaseUnit = new BaseProduct { Unit = string.Empty, PackageUnit = "BOX", UnitsPerPackage = 12 };
        Assert.False(SalesReturnSourceRules.TryBaseUnitQuantity(noBaseUnit, "BOX", 1m, out _));
    }
}


