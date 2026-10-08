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
/// ERP-374 销售退货来源候选 / 详情与业务表单显式选择单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：有界只读候选（客户数据范围先于计数 / 取数、只含未删除且已审核来源、客户 / 仓库筛选、有界关键字）、
/// 「来源出库单 + 商品」聚合（重复商品行按基础单位合计，绝不重复相乘）、净可退容量（扣减已生效退货）、
/// 零容量 / 单位未知 / 负数量证据显式不可用、来源详情 fail closed、显式选择保存后重开保留权威来源与明细，
/// 以及复用既有「销售退货」+「销售出库」菜单与实时客户数据范围的 fail closed 授权（不新增用户授权、无管理员兜底）。</para>
/// </summary>
public class SalesReturnSourceSelectionTests
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

    /// <summary>播种一个既有「销售退货」菜单（+ 可选既有「销售出库」菜单）授权的业务员账号并返回用户 Id。</summary>
    private static long SeedOperator(ErpDbContext db, bool withSourceMenu, params long[] customerIds)
    {
        var userId = SalesReturnTestAuthorization.SeedAuthorizedSalesman(db,
            customerIds.Length == 0 ? new[] { CustomerA } : customerIds);
        if (!withSourceMenu) return userId;

        var roleId = db.SysUserRoles.Single(ur => ur.UserId == userId && !ur.IsDeleted).RoleId;
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == SalesReturnSourceRules.SourceRequiredMenuCode);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuCode = SalesReturnSourceRules.SourceRequiredMenuCode,
                MenuName = SalesReturnSourceRules.SourceRequiredMenuText,
                Path = "/logistics/stock-out",
                Icon = "package-minus",
                SortOrder = 41,
                MenuType = MenuType.Menu,
                CreatedAt = DateTime.Now
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
        return userId;
    }

    private static SalesReturnController Authorized(ErpDbContext db, params long[] customerIds)
        => SalesReturnTestAuthorization.ForUser(db, SeedOperator(db, withSourceMenu: true, customerIds));

    private static StockOut SeedShipment(ErpDbContext db, long id, string no, long customerId, long warehouseId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
        => SeedShipmentState(db, id, no, customerId, warehouseId, DocumentStatus.Approved, false, lines);

    private static StockOut SeedShipmentState(ErpDbContext db, long id, string no, long customerId, long warehouseId,
        DocumentStatus status, bool deleted,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            Id = id,
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            IsDeleted = deleted,
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
        db.SaveChanges();
        return shipment;
    }

    private static void SeedApprovedReturn(ErpDbContext db, long id, long sourceStockOutId, long customerId,
        long warehouseId, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var ret = new SalesReturn
        {
            Id = id,
            ReturnNo = $"XTH-SEL-{id}",
            ReturnDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = $"客户{customerId}",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = db.StockOuts.Where(o => o.Id == sourceStockOutId).Select(o => o.StockOutNo).Single(),
            ReturnReason = "质量",
            Status = DocumentStatus.Approved,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new SalesReturnDetail
            {
                SalesReturnId = id,
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m
            }).ToList()
        };
        db.SalesReturns.Add(ret);
        db.SaveChanges();
    }

    private static List<SalesReturnSourceCandidateDto> Candidates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<List<SalesReturnSourceCandidateDto>>>(ok.Value).Data!;
    }

    private static SalesReturnSourceDetailDto Detail(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<SalesReturnSourceDetailDto>>(ok.Value).Data!;
    }

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    // ==================== 1. 有界候选：范围 / 状态 / 聚合 / 净可退容量 ====================

    [Fact]
    public async Task 候选_仅返回范围内已审核未删除来源_按来源加商品聚合且扣减已生效退货()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        // 601：同商品重复行 6 + 4 = 10（绝不重复相乘），另有 SR-B 3
        SeedShipment(db, 601L, "CK-SR-0601", CustomerA, WarehouseA,
            (ProductA, "PCS", 6m), (ProductA, "PCS", 4m), (ProductB, "PCS", 3m));
        // 602：另一张权威来源（同客户同仓库），SR-A 5
        SeedShipment(db, 602L, "CK-SR-0602", CustomerA, WarehouseA, (ProductA, "PCS", 5m));
        // 603：范围外客户；604：未审核；605：已删除 —— 一律不得出现（606 为其它仓库，未筛选仓库时仍可见）
        SeedShipment(db, 603L, "CK-SR-0603", CustomerB, WarehouseA, (ProductA, "PCS", 9m));
        SeedShipmentState(db, 604L, "CK-SR-0604", CustomerA, WarehouseA, DocumentStatus.Pending,
            false, (ProductA, "PCS", 9m));
        SeedShipmentState(db, 605L, "CK-SR-0605", CustomerA, WarehouseA, DocumentStatus.Approved,
            true, (ProductA, "PCS", 9m));
        SeedShipment(db, 606L, "CK-SR-0606", CustomerA, WarehouseB, (ProductA, "PCS", 9m));
        // 601 已有 3 件 SR-A 已审核退货：净可退 7（而不是按来源行重复计入）
        SeedApprovedReturn(db, 9001L, 601L, CustomerA, WarehouseA, (ProductA, "PCS", 3m));

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));

        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.SourceStockOutId is 603L or 604L or 605L);

        // 显式仓库筛选后只保留本仓库来源（606 被排除）
        var warehouseRows = Candidates(await ctl.GetSourceCandidates(null, WarehouseA, null, 0));
        Assert.Equal(3, warehouseRows.Count);
        Assert.DoesNotContain(warehouseRows, r => r.SourceStockOutId == 606L);

        var a601 = rows.Single(r => r.SourceStockOutId == 601L && r.ProductId == ProductA);
        Assert.Equal(10m, a601.SourceBaseQuantity);                 // 重复行合计，绝不重复相乘
        Assert.Equal(3m, a601.EffectiveReturnedBaseQuantity);
        Assert.Equal(7m, a601.RemainingBaseQuantity);
        Assert.True(a601.Available);
        Assert.Equal("PCS", a601.BaseUnit);
        Assert.Equal("规格A", a601.Spec);
        Assert.Equal("客户970001", a601.CustomerName);
        Assert.Equal("退货仓A", a601.WarehouseName);

        Assert.Equal(3m, rows.Single(r => r.SourceStockOutId == 601L && r.ProductId == ProductB)
            .RemainingBaseQuantity);
        Assert.Equal(5m, rows.Single(r => r.SourceStockOutId == 602L && r.ProductId == ProductA)
            .RemainingBaseQuantity);
    }

    [Fact]
    public async Task 候选_按客户与仓库显式筛选_并在关键字内做有界匹配()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db, CustomerA, CustomerB);
        SeedShipment(db, 611L, "CK-SR-0611", CustomerA, WarehouseA, (ProductA, "PCS", 8m));
        SeedShipment(db, 612L, "CK-SR-0612", CustomerB, WarehouseB, (ProductB, "PCS", 8m));

        var byWarehouse = Candidates(await ctl.GetSourceCandidates(null, WarehouseA, null, 0));
        Assert.Single(byWarehouse);
        Assert.Equal(611L, byWarehouse[0].SourceStockOutId);

        var byCustomer = Candidates(await ctl.GetSourceCandidates(CustomerB, null, null, 0));
        Assert.Single(byCustomer);
        Assert.Equal(612L, byCustomer[0].SourceStockOutId);

        // 关键字：单号 / 商品名均可命中；未命中的来源被排除
        Assert.Equal(611L, Candidates(await ctl.GetSourceCandidates(null, null, "CK-SR-0611", 0)).Single()
            .SourceStockOutId);
        Assert.Equal(ProductA, Candidates(await ctl.GetSourceCandidates(null, null, "商品SR-A", 0)).Single()
            .ProductId);
        Assert.Empty(Candidates(await ctl.GetSourceCandidates(null, null, "不存在的关键字", 0)));
    }

    [Fact]
    public void 候选_条数钳制与关键字规范化()
    {
        Assert.Equal(SalesReturnSourceRules.DefaultCandidateTake, SalesReturnSourceRules.ClampTake(0));
        Assert.Equal(SalesReturnSourceRules.DefaultCandidateTake, SalesReturnSourceRules.ClampTake(-3));
        Assert.Equal(SalesReturnSourceRules.MaxCandidateTake, SalesReturnSourceRules.ClampTake(9999));
        Assert.Equal(20, SalesReturnSourceRules.ClampTake(20));

        Assert.Equal(string.Empty, SalesReturnSourceRules.NormalizeKeyword(null));
        Assert.Equal("abc", SalesReturnSourceRules.NormalizeKeyword("  abc  "));
        Assert.Equal(SalesReturnSourceRules.MaxKeywordLength,
            SalesReturnSourceRules.NormalizeKeyword(new string('x', 300)).Length);
    }

    // ==================== 2. 零容量 / 损坏证据 / 单位未知：显式不可用 ====================

    [Fact]
    public async Task 候选_零容量与损坏证据与单位未知_显式标记不可用且不可选择()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        // 621：已全部退完 → 零容量
        SeedShipment(db, 621L, "CK-SR-0621", CustomerA, WarehouseA, (ProductA, "PCS", 5m));
        SeedApprovedReturn(db, 9101L, 621L, CustomerA, WarehouseA, (ProductA, "PCS", 5m));
        // 622：单位未知（既不是基础单位也不是装箱单位）
        SeedShipment(db, 622L, "CK-SR-0622", CustomerA, WarehouseA, (ProductPack, "CTN", 3m));
        // 623：负数量损坏证据
        db.StockOuts.Add(new StockOut
        {
            Id = 623L, StockOutNo = "CK-SR-0623", StockOutDate = DateTime.Today,
            CustomerId = CustomerA, WarehouseId = WarehouseA, Status = DocumentStatus.Approved,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = ProductB, ProductName = "商品SR-B", Spec = "规格A", Unit = "PCS", Quantity = -2m }
            }
        });
        db.SaveChanges();

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));

        var zero = rows.Single(r => r.SourceStockOutId == 621L);
        Assert.False(zero.Available);
        Assert.Equal(0m, zero.RemainingBaseQuantity);
        Assert.Equal(SalesReturnSourceRules.ZeroCapacityText, zero.UnavailableReason);

        var unknown = rows.Single(r => r.SourceStockOutId == 622L);
        Assert.False(unknown.Available);
        Assert.Equal(SalesReturnSourceRules.UnknownUnitText, unknown.UnavailableReason);

        var corrupt = rows.Single(r => r.SourceStockOutId == 623L);
        Assert.False(corrupt.Available);
        Assert.Equal(SalesReturnSourceRules.CorruptSourceText, corrupt.UnavailableReason);

        // 可选项排序在前：可用候选优先于不可用候选，界面只允许选择 Available = true
        Assert.True(rows.First().Available || rows.All(r => !r.Available));
    }

    [Fact]
    public async Task 候选_装箱单位按装箱数折算为基础单位_净可退容量同口径()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        SeedShipment(db, 631L, "CK-SR-0631", CustomerA, WarehouseA, (ProductPack, "BOX", 2m)); // 2 × 12 = 24 PCS
        SeedApprovedReturn(db, 9131L, 631L, CustomerA, WarehouseA, (ProductPack, "PCS", 4m));

        var row = Candidates(await ctl.GetSourceCandidates(null, null, null, 0)).Single();

        Assert.True(row.Available);
        Assert.Equal(24m, row.SourceBaseQuantity);
        Assert.Equal(4m, row.EffectiveReturnedBaseQuantity);
        Assert.Equal(20m, row.RemainingBaseQuantity);
        Assert.Equal("PCS", row.BaseUnit);
    }

    // ==================== 3. 授权 fail closed（不新增授权、无管理员兜底） ====================

    [Fact]
    public async Task 候选_无身份或缺少既有菜单授权_fail_closed且不返回证据()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedShipment(db, 641L, "CK-SR-0641", CustomerA, WarehouseA, (ProductA, "PCS", 5m));

        // 无身份 → 未认证
        var anonymous = SalesReturnTestAuthorization.ForUser(db, null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            anonymous.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);

        // 只有「销售退货」菜单、没有「销售出库」菜单 → 权限不足
        var noSourceMenu = SalesReturnTestAuthorization.ForUser(db, SeedOperator(db, withSourceMenu: false));
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            noSourceMenu.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 撤销「销售出库」菜单授权后下一次请求立即收敛（绝不缓存）
        var authorized = Authorized(db);
        Assert.Single(Candidates(await authorized.GetSourceCandidates(null, null, null, 0)));
        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            authorized.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 无库存 / 流水写入（候选 / 详情是只读投影）
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 候选与详情_客户数据范围外按不存在拒绝_不泄露归属()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);                                  // 只可见 CustomerA
        SeedShipment(db, 651L, "CK-SR-0651", CustomerB, WarehouseA, (ProductA, "PCS", 5m));

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));
        Assert.Empty(rows);                                        // 范围外来源不出现在候选

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSourceCandidates(CustomerB, null, null, 0));    // 显式请求范围外客户
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(651L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 4. 来源详情：权威表头 + 逐商品可退行 + 失效来源 fail closed ====================

    [Fact]
    public async Task 来源详情_返回权威表头与逐商品可退行_失效来源fail_closed且不改写()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        SeedShipment(db, 661L, "CK-SR-0661", CustomerA, WarehouseA,
            (ProductA, "PCS", 6m), (ProductA, "PCS", 4m), (ProductB, "PCS", 2m));
        SeedApprovedReturn(db, 9161L, 661L, CustomerA, WarehouseA, (ProductA, "PCS", 3m));
        SeedShipmentState(db, 662L, "CK-SR-0662", CustomerA, WarehouseA, DocumentStatus.Pending,
            false, (ProductA, "PCS", 5m));
        SeedShipmentState(db, 663L, "CK-SR-0663", CustomerA, WarehouseA, DocumentStatus.Cancelled,
            false, (ProductA, "PCS", 5m));
        SeedShipmentState(db, 664L, "CK-SR-0664", CustomerA, WarehouseA, DocumentStatus.Approved,
            true, (ProductA, "PCS", 5m));
        var before = db.SalesReturns.Count();

        var detail = Detail(await ctl.GetSourceCandidateDetail(661L));
        Assert.Equal(661L, detail.SourceStockOutId);
        Assert.Equal("CK-SR-0661", detail.SourceStockOutNo);
        Assert.Equal(CustomerA, detail.CustomerId);
        Assert.Equal("客户970001", detail.CustomerName);
        Assert.Equal(WarehouseA, detail.WarehouseId);
        Assert.Equal("退货仓A", detail.WarehouseName);
        Assert.True(detail.Available);
        Assert.Equal(2, detail.Lines.Count);
        var lineA = detail.Lines.Single(l => l.ProductId == ProductA);
        Assert.Equal(10m, lineA.SourceBaseQuantity);               // 重复行 6 + 4
        Assert.Equal(3m, lineA.EffectiveReturnedBaseQuantity);
        Assert.Equal(7m, lineA.RemainingBaseQuantity);

        // 未审核 / 已取消 → 冲突；已删除 → 不存在；非法 Id → 参数错误
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(662L));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(663L));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(664L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 只读投影：不写库、不改单据 / 库存 / 流水
        Assert.Equal(before, db.SalesReturns.Count());
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 5. 显式选择 → 保存 → 重开：保留权威来源与明细，不臆造价格 ====================

    [Fact]
    public async Task 显式选择保存后重开_保留权威来源与部分数量_绝不臆造价格()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        SeedShipment(db, 671L, "CK-SR-0671", CustomerA, WarehouseA, (ProductA, "PCS", 8m));
        SeedApprovedReturn(db, 9171L, 671L, CustomerA, WarehouseA, (ProductA, "PCS", 3m));

        // 业务表单显式选择来源：用详情行回填权威来源 Id / 单号 / 客户 / 仓库与可退商品行
        var detail = Detail(await ctl.GetSourceCandidateDetail(671L));
        var line = detail.Lines.Single();
        Assert.True(line.Available);
        Assert.Equal(5m, line.RemainingBaseQuantity);

        var create = new SalesReturn
        {
            ReturnDate = DateTime.Today,
            CustomerId = detail.CustomerId,
            CustomerName = detail.CustomerName,
            WarehouseId = detail.WarehouseId,
            SourceStockOutId = detail.SourceStockOutId,
            SourceStockOutNo = detail.SourceStockOutNo,
            ReturnReason = "质量",
            Details = new List<SalesReturnDetail>
            {
                new()
                {
                    ProductId = line.ProductId,
                    ProductName = line.ProductName,
                    Spec = line.Spec,
                    Unit = line.BaseUnit,
                    Quantity = 2m,                 // 正数部分数量（≤ 剩余可退容量）
                    UnitPrice = 0m,                // 绝不臆造价格：价格由操作员 / 来源决定
                    UnitCost = 0m                  // 成本语义保留：留 0 由服务端按来源出库成本兜底
                }
            }
        };

        var id = CreatedId(await ctl.Create(create));

        // 重开：权威来源 Id / 单号 / 客户 / 仓库与明细原样保留
        var reopened = Assert.IsType<SalesReturn>(
            Assert.IsType<ApiResponse<SalesReturn>>(Assert.IsType<OkObjectResult>(await ctl.GetById(id)).Value).Data!);
        Assert.Equal(671L, reopened.SourceStockOutId);
        Assert.Equal("CK-SR-0671", reopened.SourceStockOutNo);
        Assert.Equal(CustomerA, reopened.CustomerId);
        Assert.Equal(WarehouseA, reopened.WarehouseId);
        var savedLine = Assert.Single(reopened.Details);
        Assert.Equal(line.ProductId, savedLine.ProductId);
        Assert.Equal("PCS", savedLine.Unit);
        Assert.Equal(2m, savedLine.Quantity);
        Assert.Equal(0m, savedLine.UnitPrice);

        // 待提交退货不计入「已生效退货」：可退容量仍是 5（只在审核后才扣减）
        var after = Candidates(await ctl.GetSourceCandidates(null, null, null, 0)).Single();
        Assert.Equal(5m, after.RemainingBaseQuantity);
    }
}
