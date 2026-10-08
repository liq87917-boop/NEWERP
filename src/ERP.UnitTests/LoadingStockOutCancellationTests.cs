using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-367 来源销售出库单取消 / 冲销前的「已审核装柜清单显式引用」护栏单元测试（内存库，真实控制器）。
/// <list type="number">
/// <item>存在「已审核、未删除」装柜清单明细显式链接（<c>SourceStockOutDetailId</c>）到本出库单明细时，
/// 取消 fail closed 拒绝并给出可执行的装柜撤销要求，且单据 / 明细 / 状态 / 库存 / 流水 / 装柜证据全部保持原样；</item>
/// <item>历史未链接（<c>null</c>）明细、未审核（待提交 / 已提交）、已取消、已删除的装柜清单，以及链接到其它出库单明细的行
/// 一律不阻断（绝不按商品 / 单据号 / 相似度猜测来源）；</item>
/// <item>装柜清单取消后护栏即时释放，但装柜链接与历史数量原样保留；</item>
/// <item>重复取消来源单据按既有规则幂等拒绝，不重复冲销；</item>
/// <item>既有 ERP-359 退货引用护栏不被绕过；合法「出运后销售退货」仍可登记 / 审核，后续装柜容量扣除已生效退货。</item>
/// </list>
/// <para>身份使用既有特权账号与真实业务员（既有菜单授权 + 真实客户数据范围），不使用匿名 / 管理员兜底；
/// 不连接 SQL Server、不运行浏览器验收、不触碰任何业务库。</para>
/// </summary>
public class LoadingStockOutCancellationTests
{
    private const long WarehouseA = 974001L;
    private const long ProductA = 974101L;
    private const long CustomerA = 974201L;

    // ==================== 脚手架与种子数据 ====================

    private static StockOutController NewStockOutController(ErpDbContext db, long? userId)
    {
        var ctl = new StockOutController(db, new DocumentNumberService(db), new InventoryService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerLoadingListController NewLoadingController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static SalesReturnController NewSalesReturnController(ErpDbContext db, long? userId)
    {
        var ctl = new SalesReturnController(db, new DocumentNumberService(db), new InventoryService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedProduct(ErpDbContext db, long id, string unit = "PCS")
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = $"P-{id}", ProductName = $"商品{id}", Spec = "规格A", Unit = unit
        });
        db.SaveChanges();
    }

    private static void SeedCustomer(ErpDbContext db, long id, string name)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"C-{id}", CustomerName = name, Status = 1
        });
        db.SaveChanges();
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId, ProductId = productId, Quantity = quantity,
            AvailableQuantity = quantity, TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        db.SaveChanges();
    }

    private static async Task<long> CreateApprovedStockOutAsync(ErpDbContext db, long userId, decimal quantity)
    {
        var ctl = NewStockOutController(db, userId);
        var result = await ctl.Create(new StockOut
        {
            StockOutDate = DateTime.Today,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            Remark = "ERP-367_TEST",
            Details = new List<StockOutDetail>
            {
                new() { ProductId = ProductA, ProductName = $"商品{ProductA}", Unit = "PCS", Quantity = quantity }
            }
        });
        var id = CreatedId(result);
        await ctl.Submit(id);
        await ctl.Approve(id);
        return id;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, DocumentStatus status,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, CustomerId = CustomerA, Status = status,
            Remark = "ERP-367_TEST"
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id, ProductId = productId, ProductName = $"商品{productId}",
                Quantity = quantity, SourceStockOutDetailId = sourceDetailId
            });
        }
        db.SaveChanges();
        return list;
    }

    private static void SeedApprovedSalesReturn(ErpDbContext db, long stockOutId, string stockOutNo,
        decimal quantity)
    {
        db.SalesReturns.Add(new SalesReturn
        {
            ReturnNo = $"XTH-367-{Guid.NewGuid():N}"[..24],
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = stockOutId,
            SourceStockOutNo = stockOutNo,
            ReturnReason = "质量",
            Status = DocumentStatus.Approved,
            TotalQuantity = quantity,
            Details = new List<SalesReturnDetail>
            {
                new() { ProductId = ProductA, ProductName = $"商品{ProductA}", Unit = "PCS",
                    Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m, UnitCost = 8m }
            }
        });
        db.SaveChanges();
    }

    private static long SourceDetailId(ErpDbContext db, long stockOutId)
    {
        db.ChangeTracker.Clear();
        return db.StockOutDetails.AsNoTracking()
            .Where(d => d.StockOutId == stockOutId && !d.IsDeleted)
            .OrderBy(d => d.Id).Select(d => d.Id).First();
    }

    private static DocumentStatus StockOutStatus(ErpDbContext db, long stockOutId)
    {
        db.ChangeTracker.Clear();
        return db.StockOuts.AsNoTracking().Where(o => o.Id == stockOutId).Select(o => o.Status).Single();
    }

    private static string StockOutNo(ErpDbContext db, long stockOutId)
    {
        db.ChangeTracker.Clear();
        return db.StockOuts.AsNoTracking().Where(o => o.Id == stockOutId).Select(o => o.StockOutNo).Single();
    }

    private static decimal StockQuantity(ErpDbContext db)
    {
        db.ChangeTracker.Clear();
        return db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA)
            .Select(s => s.Quantity).Single();
    }

    private static ContainerLoadingList ReloadList(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return db.ContainerLoadingLists.AsNoTracking().Include(o => o.Details).Single(o => o.Id == id);
    }

    private static long ReloadId(ErpDbContext db, string loadingListNo)
    {
        db.ChangeTracker.Clear();
        return db.ContainerLoadingLists.AsNoTracking()
            .Where(l => l.LoadingListNo == loadingListNo).Select(l => l.Id).Single();
    }

    private static long SeedUserWithMenus(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = "ERP-367 操作员", RoleCode = $"Erp367-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"erp367-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP-367 操作员", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuCode = menuCode, MenuName = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user.Id;
    }

    private static long CreatedId(IActionResult result)
    {
        Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    // ==================== 1. 已审核装柜清单显式引用 → 拒绝且保留全部证据 ====================

    [Fact]
    public async Task 来源出库单取消_已审核装柜清单显式引用_拒绝且保留全部证据()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        var sourceDetailId = SourceDetailId(db, stockOutId);
        var list = SeedLoadingList(db, "ZQ-GUARD-1", DocumentStatus.Approved, (ProductA, 5m, sourceDetailId));

        var ctl = NewStockOutController(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(stockOutId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(LoadingStockOutLinkRules.LoadingReversalRequirementText, ex.Message); // 可执行处置要求
        Assert.Contains("装柜", ex.Message);
        Assert.Contains(list.LoadingListNo, ex.Message);                                     // 指明阻断的装柜清单

        // 拒绝先于任何冲销 / 状态变更：单据 / 明细 / 状态 / 库存 / 流水全部原样
        Assert.Equal(DocumentStatus.Approved, StockOutStatus(db, stockOutId));
        Assert.Equal(5m, db.StockOutDetails.Where(d => d.StockOutId == stockOutId).Sum(d => d.Quantity));
        Assert.Equal(5m, StockQuantity(db));
        Assert.Single(db.StockMovements);
        Assert.False(db.StockMovements.Single().IsReversed);

        // 装柜历史证据（状态 / 链接 / 数量）原样保留，未被改写 / 回填 / 删除
        var reloaded = ReloadList(db, list.Id);
        Assert.Equal(DocumentStatus.Approved, reloaded.Status);
        Assert.Equal(sourceDetailId, reloaded.Details.Single().SourceStockOutDetailId);
        Assert.Equal(5m, reloaded.Details.Single().Quantity);
    }

    // ==================== 2. 非生效装柜清单 / 无关行 → 不阻断 ====================

    [Fact]
    public async Task 来源出库单取消_未审核或已取消或已删除装柜清单_不阻断()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        var sourceDetailId = SourceDetailId(db, stockOutId);
        // 待提交 / 已提交：不构成生效引用
        SeedLoadingList(db, "ZQ-GUARD-2-PENDING", DocumentStatus.Pending, (ProductA, 5m, sourceDetailId));
        SeedLoadingList(db, "ZQ-GUARD-2-SUBMITTED", DocumentStatus.Submitted, (ProductA, 5m, sourceDetailId));
        // 已取消：不构成生效引用
        SeedLoadingList(db, "ZQ-GUARD-2-CANCELLED", DocumentStatus.Cancelled, (ProductA, 5m, sourceDetailId));
        // 已删除（软删除）：不构成生效引用
        var deleted = SeedLoadingList(db, "ZQ-GUARD-2-DELETED", DocumentStatus.Approved,
            (ProductA, 5m, sourceDetailId));
        deleted.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewStockOutController(db, userId);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(stockOutId));

        Assert.Equal(DocumentStatus.Cancelled, StockOutStatus(db, stockOutId));
        Assert.Equal(10m, StockQuantity(db));
        // 既有红字冲销轨迹保留：1 条正向出库 + 1 条冲销
        Assert.Equal(2, db.StockMovements.Count());
        Assert.Single(db.StockMovements.Where(m => m.IsReversal));
        // 非生效装柜清单证据原样保留（状态 / 链接未被改写）
        Assert.Equal(DocumentStatus.Submitted, ReloadList(db, ReloadId(db, "ZQ-GUARD-2-SUBMITTED")).Status);
        Assert.Equal(DocumentStatus.Cancelled, ReloadList(db, ReloadId(db, "ZQ-GUARD-2-CANCELLED")).Status);
        Assert.True(ReloadList(db, ReloadId(db, "ZQ-GUARD-2-DELETED")).IsDeleted);
        Assert.Equal(sourceDetailId,
            ReloadList(db, ReloadId(db, "ZQ-GUARD-2-SUBMITTED")).Details.Single().SourceStockOutDetailId);
    }

    [Fact]
    public async Task 来源出库单取消_历史未链接或链接其它出库单_不阻断()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 20m, totalCost: 200m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        // 历史未链接（null = 显式无证据）明细：即使同商品 / 数量很大，也绝不按商品猜测来源、绝不阻断
        SeedLoadingList(db, "ZQ-GUARD-3-LEGACY", DocumentStatus.Approved, (ProductA, 100m, null));
        // 链接到另一张出库单明细：与本单无显式链接，不阻断
        var otherStockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        var otherDetailId = SourceDetailId(db, otherStockOutId);
        SeedLoadingList(db, "ZQ-GUARD-3-OTHER", DocumentStatus.Approved, (ProductA, 5m, otherDetailId));

        var ctl = NewStockOutController(db, userId);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(stockOutId));

        Assert.Equal(DocumentStatus.Cancelled, StockOutStatus(db, stockOutId));
        Assert.Equal(DocumentStatus.Approved, StockOutStatus(db, otherStockOutId));
        // 未链接的装柜历史证据原样保留
        var legacy = ReloadList(db, ReloadId(db, "ZQ-GUARD-3-LEGACY"));
        Assert.Equal(DocumentStatus.Approved, legacy.Status);
        Assert.Null(legacy.Details.Single().SourceStockOutDetailId);
        Assert.Equal(100m, legacy.Details.Single().Quantity);
        // 其它出库单的装柜链接不受影响
        var other = ReloadList(db, ReloadId(db, "ZQ-GUARD-3-OTHER"));
        Assert.Equal(DocumentStatus.Approved, other.Status);
        Assert.Equal(otherDetailId, other.Details.Single().SourceStockOutDetailId);
    }

    // ==================== 3. 装柜取消释放护栏（证据保留） ====================

    [Fact]
    public async Task 来源出库单取消_装柜清单取消后放行_且保留装柜链接证据()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        var sourceDetailId = SourceDetailId(db, stockOutId);
        var list = SeedLoadingList(db, "ZQ-GUARD-4", DocumentStatus.Approved, (ProductA, 5m, sourceDetailId));

        // 先取消已审核装柜清单：护栏即时释放，但装柜链接与数量原样保留（不删除历史证据）
        Assert.IsType<OkObjectResult>(await NewLoadingController(db, userId).Cancel(list.Id));
        var released = ReloadList(db, list.Id);
        Assert.Equal(DocumentStatus.Cancelled, released.Status);
        Assert.Equal(sourceDetailId, released.Details.Single().SourceStockOutDetailId);
        Assert.Equal(5m, released.Details.Single().Quantity);

        // 释放后来源出库单按既有流程取消：库存复原 + 红字冲销流水保留
        Assert.IsType<OkObjectResult>(await NewStockOutController(db, userId).Cancel(stockOutId));
        Assert.Equal(DocumentStatus.Cancelled, StockOutStatus(db, stockOutId));
        Assert.Equal(10m, StockQuantity(db));
        Assert.Equal(2, db.StockMovements.Count());
        Assert.Single(db.StockMovements.Where(m => m.IsReversal));
    }

    [Fact]
    public async Task 来源出库单取消_重复取消_幂等拒绝且不重复冲销()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        var ctl = NewStockOutController(db, userId);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(stockOutId));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(stockOutId));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已取消", ex.Message);
        // 不重复冲销：仍只有 1 条正向 + 1 条冲销
        Assert.Equal(10m, StockQuantity(db));
        Assert.Equal(2, db.StockMovements.Count());
    }

    // ==================== 4. 既有退货护栏不被绕过 ====================

    [Fact]
    public async Task 装柜清单已取消但已审核销售退货仍在_取消仍被退货护栏拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 5m);
        var stockOutNo = StockOutNo(db, stockOutId);
        var sourceDetailId = SourceDetailId(db, stockOutId);
        var list = SeedLoadingList(db, "ZQ-GUARD-5", DocumentStatus.Approved, (ProductA, 5m, sourceDetailId));
        Assert.IsType<OkObjectResult>(await NewLoadingController(db, userId).Cancel(list.Id));
        SeedApprovedSalesReturn(db, stockOutId, stockOutNo, quantity: 5m);

        var ctl = NewStockOutController(db, userId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(stockOutId));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("销审", ex.Message);                          // 既有 ERP-359 退货护栏仍然生效
        Assert.Equal(DocumentStatus.Approved, StockOutStatus(db, stockOutId));
        Assert.Equal(5m, StockQuantity(db));
        Assert.Single(db.StockMovements);
        Assert.False(db.StockMovements.Single().IsReversed);
    }

    // ==================== 5. 合法出运后销售退货不被禁止，后续容量扣除退货 ====================

    [Fact]
    public async Task 装柜清单已审核_合法出运后销售退货仍可登记审核_后续装柜容量扣除退货()
    {
        using var db = TestDbFactory.Create();
        // 合法销售退货操作员：既有特权数据范围 + 既有「销售退货」「销售出库」「装柜清单」菜单授权（不新增任何授权）。
        var userId = SeedUserWithMenus(db, SalesReturnSourceRules.RequiredMenuCode,
            LoadingStockOutLinkRules.SourceRequiredMenuCode, LoadingStockOutLinkRules.RequiredMenuCode);
        SeedCustomer(db, CustomerA, "客户A");
        SeedProduct(db, ProductA);
        SeedStock(db, WarehouseA, ProductA, quantity: 20m, totalCost: 200m);

        var stockOutId = await CreateApprovedStockOutAsync(db, userId, quantity: 10m);
        var stockOutNo = StockOutNo(db, stockOutId);
        var sourceDetailId = SourceDetailId(db, stockOutId);
        var shipped = SeedLoadingList(db, "ZQ-GUARD-6", DocumentStatus.Approved, (ProductA, 6m, sourceDetailId));

        // 出运后退货：本护栏绝不禁止合法销售退货 —— 仍按既有流程登记 / 提交 / 审核。
        var returnCtl = NewSalesReturnController(db, userId);
        var returnId = CreatedId(await returnCtl.Create(new SalesReturn
        {
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = stockOutId,
            SourceStockOutNo = stockOutNo,
            ReturnReason = "质量",
            Details = new List<SalesReturnDetail>
            {
                new() { ProductId = ProductA, ProductName = $"商品{ProductA}", Unit = "PCS",
                    Quantity = 4m, UnitPrice = 10m, Amount = 40m, UnitCost = 10m }
            }
        }));
        await returnCtl.Submit(returnId);
        await returnCtl.Approve(returnId);
        Assert.Equal(DocumentStatus.Approved, db.SalesReturns.AsNoTracking()
            .Where(r => r.Id == returnId).Select(r => r.Status).Single());

        // 后续装柜容量按「来源数量 − 已生效退货 − 已链接装载」计算：10 − 4 − 6 = 0，任何新增链接都溢出。
        var overflow = SeedLoadingList(db, "ZQ-GUARD-6-NEXT", DocumentStatus.Submitted,
            (ProductA, 1m, sourceDetailId));
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            LoadingStockOutLinkRules.ValidateApprovalAsync(db, ReloadList(db, overflow.Id), userId));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("可装柜容量", ex.Message);
        Assert.Equal(DocumentStatus.Submitted, ReloadList(db, overflow.Id).Status);

        // 装柜历史证据与来源出库单均未被改写
        var shippedReloaded = ReloadList(db, shipped.Id);
        Assert.Equal(DocumentStatus.Approved, shippedReloaded.Status);
        Assert.Equal(sourceDetailId, shippedReloaded.Details.Single().SourceStockOutDetailId);
        Assert.Equal(6m, shippedReloaded.Details.Single().Quantity);
        Assert.Equal(DocumentStatus.Approved, StockOutStatus(db, stockOutId));
    }

    // ==================== 6. 规则层与控制器接入 ====================

    [Fact]
    public void 规则层_取消护栏与装柜审核共用同一把来源出库单行锁()
    {
        Assert.Equal(ReturnSourceCancellationRules.LockStockOutRowSql, LoadingStockOutLinkRules.LockStockOutRowSql);
        Assert.Contains("db_owner.StockOuts WITH (UPDLOCK, HOLDLOCK)", LoadingStockOutLinkRules.LockStockOutRowSql);
    }

    [Fact]
    public void 规则层_取消护栏文案可执行且边界不删历史证据()
    {
        Assert.Contains("装柜", LoadingStockOutLinkRules.LoadingReversalRequirementText);
        Assert.Contains("取消", LoadingStockOutLinkRules.LoadingReversalRequirementText);
        Assert.Contains("SourceStockOutDetailId", LoadingStockOutLinkRules.CancellationRuleText);
        Assert.Contains("已审核", LoadingStockOutLinkRules.CancellationRuleText);
        Assert.Contains("不新增", LoadingStockOutLinkRules.CancellationBoundaryText);
        Assert.Contains("退货", LoadingStockOutLinkRules.CancellationRuleText);
    }

    [Fact]
    public void 规则层_内存库不视为关系型提供程序()
    {
        using var db = TestDbFactory.Create();
        Assert.False(LoadingStockOutLinkRules.IsRelationalProvider(db));
    }

    [Fact]
    public void 取消端点_先取来源行锁_再判定装柜护栏_最后才冲销()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockOutController.cs");
        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.Contains("LoadingStockOutLinkRules.EnsureNoEffectiveApprovedLoadingAsync", controller);
        Assert.Contains("ReturnSourceCancellationRules.EnsureNoEffectiveApprovedSalesReturnAsync", controller);

        var lockIndex = controller.IndexOf("await LockSourceShipmentRowAsync(id);", StringComparison.Ordinal);
        var loadingGuardIndex = controller.IndexOf(
            "await LoadingStockOutLinkRules.EnsureNoEffectiveApprovedLoadingAsync(Db, id);", StringComparison.Ordinal);
        var reversalIndex = controller.IndexOf("await ReverseStockAsync(entity);", StringComparison.Ordinal);

        Assert.True(lockIndex > 0, "取消端点必须先取得来源出库单行锁");
        Assert.True(loadingGuardIndex > lockIndex, "装柜护栏必须在来源行锁之内判定");
        Assert.True(reversalIndex > loadingGuardIndex, "装柜护栏必须先于库存冲销判定");
    }

    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
