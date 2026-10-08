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
/// ERP-377 采购退货来源候选 / 详情与业务表单显式选择单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：有界只读候选（既有采购入库归属范围先于计数 / 取数、只含未删除且已审核来源、供应商 / 仓库筛选、有界关键字）、
/// 「来源入库单 + 商品」聚合（重复商品行按基础单位合计，绝不重复相乘）、净可退容量（扣减已生效退货）、
/// 零容量 / 负数量 / 单位未知证据显式不可用、来源详情 fail closed、显式选择保存后重开保留权威来源与部分数量、
/// 以及复用既有「采购退货」+「采购入库」菜单与采购入库归属范围的 fail closed 授权（不新增用户授权、无管理员兜底）。</para>
/// </summary>
public class PurchaseReturnSourceSelectionTests
{
    private const long SupplierA = 971301L;
    private const long SupplierB = 971302L;
    private const long CustomerA = 971001L;
    private const long CustomerB = 971002L;
    private const long WarehouseA = 971101L;
    private const long WarehouseB = 971102L;
    private const long ProductA = 971201L;
    private const long ProductB = 971204L;
    private const long ProductPack = 971202L;
    private const long MissingProduct = 971299L;

    // ==================== 播种辅助 ====================

    private static void SeedMaster(ErpDbContext db)
    {
        PurchaseReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "退货仓A");
        PurchaseReturnTestAuthorization.SeedWarehouse(db, WarehouseB, "退货仓B");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductA, "PRSEL-A", "PCS");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductB, "PRSEL-B", "PCS");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductPack, "PRSEL-P", "PCS", "BOX", 12);
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierA, "供应商A");
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierB, "供应商B");
    }

    private static void SeedCustomer(ErpDbContext db, long id, string name)
    {
        if (db.BaseCustomers.Any(c => c.Id == id)) return;
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"PRSEL-C-{id}", CustomerName = name, Status = 1
        });
        db.SaveChanges();
    }

    /// <summary>为账号追加既有「采购入库」（<c>stock-in</c>）菜单授权（复用既有菜单，绝不新增权限模型）。</summary>
    private static void AttachStockInMenu(ErpDbContext db, long userId)
    {
        var menu = db.SysMenus.FirstOrDefault(m =>
            m.MenuCode == StockInAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                ParentId = 0,
                MenuCode = StockInAuthorizationRules.RequiredMenuCode,
                MenuName = StockInAuthorizationRules.RequiredMenuText,
                Path = "/logistics/stock-in",
                Icon = "package-plus",
                SortOrder = 40,
                MenuType = MenuType.Menu,
                CreatedAt = DateTime.Now
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        var roleId = db.SysUserRoles.Single(ur => ur.UserId == userId && !ur.IsDeleted).RoleId;
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>播种既有「采购退货」+「采购入库」双菜单授权的真实业务员账号。</summary>
    private static long SeedOperator(ErpDbContext db, params long[] customerIds)
    {
        var userId = PurchaseReturnTestAuthorization.SeedAuthorizedOperator(db,
            customerIds.Length == 0 ? new[] { CustomerA } : customerIds);
        AttachStockInMenu(db, userId);
        return userId;
    }

    private static PurchaseReturnController Authorized(ErpDbContext db, params long[] customerIds)
        => PurchaseReturnTestAuthorization.ForUser(db, SeedOperator(db, customerIds));

    private static StockIn SeedReceipt(ErpDbContext db, long id, string no, long supplierId, long warehouseId,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
        => PurchaseReturnTestAuthorization.SeedStockIn(db, id, no, supplierId, warehouseId,
            DocumentStatus.Approved, null, lines);

    /// <summary>播种已生效（已审核、未删除）退货证据（占用来源入库单的净可退容量）。</summary>
    private static void SeedEffectiveReturn(ErpDbContext db, long sourceStockInId, long supplierId,
        long warehouseId, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receiptNo = db.StockIns.Where(s => s.Id == sourceStockInId).Select(s => s.StockInNo).Single();
        var ret = new PurchaseReturn
        {
            ReturnNo = $"CTH-PRSEL-{Guid.NewGuid():N}"[..20],
            ReturnDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierName = "供应商A",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = receiptNo,
            ReturnReason = "质量",
            Status = DocumentStatus.Approved,
            TotalQuantity = lines.Sum(l => l.Quantity)
        };
        db.PurchaseReturns.Add(ret);
        db.SaveChanges();

        var sortNo = 0;
        foreach (var line in lines)
        {
            db.PurchaseReturnDetails.Add(new PurchaseReturnDetail
            {
                PurchaseReturnId = ret.Id,
                ReturnNo = ret.ReturnNo,
                SortNo = ++sortNo,
                ProductId = line.ProductId,
                ProductName = $"商品{line.ProductId}",
                Spec = "规格A",
                Unit = line.Unit,
                Quantity = line.Quantity,
                UnitPrice = 10m,
                Amount = line.Quantity * 10m,
                UnitCost = 5m
            });
        }
        db.SaveChanges();
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseReturn,
            RuleCode = "PRSEL",
            RuleName = "采购退货单",
            Prefix = "CTH",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    // ==================== 结果解析辅助 ====================

    private static List<PurchaseReturnSourceCandidateDto> Candidates(IActionResult result)
        => Assert.IsType<ApiResponse<List<PurchaseReturnSourceCandidateDto>>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static PurchaseReturnSourceDetailDto Detail(IActionResult result)
        => Assert.IsType<ApiResponse<PurchaseReturnSourceDetailDto>>(
            Assert.IsType<OkObjectResult>(result).Value).Data!;

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    // ==================== 1. 候选聚合 / 净可退容量（重复商品行合一，绝不重复相乘） ====================

    [Fact]
    public async Task 候选聚合重复商品行并按已生效退货扣减净可退容量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        // 同商品重复行 6 + 4 = 10；另有 B 4；已审核退货 A 6（部分退货）→ A 净可退 4
        SeedReceipt(db, 601L, "RK-PRSEL-0601", SupplierA, WarehouseA,
            (ProductA, "PCS", 6m), (ProductA, "PCS", 4m), (ProductB, "PCS", 4m));
        SeedEffectiveReturn(db, 601L, SupplierA, WarehouseA, (ProductA, "PCS", 6m));

        // 零容量来源：B 全部退完
        SeedReceipt(db, 602L, "RK-PRSEL-0602", SupplierA, WarehouseA, (ProductB, "PCS", 2m));
        SeedEffectiveReturn(db, 602L, SupplierA, WarehouseA, (ProductB, "PCS", 2m));

        // 未审核 / 已删除来源：一律不得出现
        PurchaseReturnTestAuthorization.SeedStockIn(db, 603L, "RK-PRSEL-0603", SupplierA, WarehouseA,
            DocumentStatus.Pending, null, (ProductA, "PCS", 9m));
        var deleted = PurchaseReturnTestAuthorization.SeedStockIn(db, 604L, "RK-PRSEL-0604", SupplierA,
            WarehouseA, DocumentStatus.Approved, null, (ProductA, "PCS", 9m));
        deleted.IsDeleted = true;
        db.SaveChanges();

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));

        Assert.DoesNotContain(rows, r => r.SourceStockInId == 603L);
        Assert.DoesNotContain(rows, r => r.SourceStockInId == 604L);

        var lineA = rows.Single(r => r.SourceStockInId == 601L && r.ProductId == ProductA);
        Assert.Equal(10m, lineA.SourceBaseQuantity);              // 重复行合计，绝不重复相乘
        Assert.Equal(6m, lineA.EffectiveReturnedBaseQuantity);
        Assert.Equal(4m, lineA.RemainingBaseQuantity);
        Assert.True(lineA.Available);
        Assert.Equal(SupplierA, lineA.SupplierId);
        Assert.Equal("供应商A", lineA.SupplierName);
        Assert.Equal(WarehouseA, lineA.WarehouseId);
        Assert.Equal("退货仓A", lineA.WarehouseName);
        Assert.Equal("PCS", lineA.BaseUnit);

        var lineZero = rows.Single(r => r.SourceStockInId == 602L);
        Assert.False(lineZero.Available);
        Assert.Equal(PurchaseReturnSourceRules.ZeroCapacityText, lineZero.UnavailableReason);

        // 只读投影：候选查询不产生库存 / 流水
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 2. 零容量 / 单位未知 / 负数量 / 商品缺失显式不可用 ====================

    [Fact]
    public async Task 零容量与损坏证据显式不可用_绝不猜容量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        SeedReceipt(db, 611L, "RK-PRSEL-0611", SupplierA, WarehouseA, (ProductA, "PCS", 5m));
        SeedEffectiveReturn(db, 611L, SupplierA, WarehouseA, (ProductA, "PCS", 5m));            // 零容量
        SeedReceipt(db, 612L, "RK-PRSEL-0612", SupplierA, WarehouseA, (ProductA, "袋", 5m));     // 单位未知
        SeedReceipt(db, 613L, "RK-PRSEL-0613", SupplierA, WarehouseA, (ProductA, "PCS", -3m));   // 负数量
        SeedReceipt(db, 614L, "RK-PRSEL-0614", SupplierA, WarehouseA, (MissingProduct, "PCS", 5m));

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.False(r.Available));
        Assert.All(rows, r => Assert.Equal(0m, r.RemainingBaseQuantity));

        Assert.Equal(PurchaseReturnSourceRules.ZeroCapacityText,
            rows.Single(r => r.SourceStockInId == 611L).UnavailableReason);
        Assert.Equal(PurchaseReturnSourceRules.UnknownUnitText,
            rows.Single(r => r.SourceStockInId == 612L).UnavailableReason);
        Assert.Equal(PurchaseReturnSourceRules.CorruptSourceText,
            rows.Single(r => r.SourceStockInId == 613L).UnavailableReason);
        // 商品主数据缺失 → 无法折算基础单位（fail closed，绝不猜单位）
        Assert.Equal(PurchaseReturnSourceRules.UnknownUnitText,
            rows.Single(r => r.SourceStockInId == 614L).UnavailableReason);
    }

    // ==================== 3. 装箱单位折算与容量扣减 ====================

    [Fact]
    public async Task 装箱单位折算为基础单位并按已生效退货扣减()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        // 2 箱 × 12 = 24 基础单位；已审核退货 24（PCS）→ 满额归零
        SeedReceipt(db, 621L, "RK-PRSEL-0621", SupplierA, WarehouseA, (ProductPack, "BOX", 2m));
        SeedEffectiveReturn(db, 621L, SupplierA, WarehouseA, (ProductPack, "PCS", 24m));
        // 1 箱 × 12 = 12；已审核退货 3 → 净可退 9
        SeedReceipt(db, 622L, "RK-PRSEL-0622", SupplierA, WarehouseA, (ProductPack, "BOX", 1m));
        SeedEffectiveReturn(db, 622L, SupplierA, WarehouseA, (ProductPack, "PCS", 3m));

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));

        var full = rows.Single(r => r.SourceStockInId == 621L);
        Assert.Equal(24m, full.SourceBaseQuantity);
        Assert.False(full.Available);
        Assert.Equal(PurchaseReturnSourceRules.ZeroCapacityText, full.UnavailableReason);

        var partial = rows.Single(r => r.SourceStockInId == 622L);
        Assert.Equal(12m, partial.SourceBaseQuantity);
        Assert.Equal(3m, partial.EffectiveReturnedBaseQuantity);
        Assert.Equal(9m, partial.RemainingBaseQuantity);
        Assert.True(partial.Available);
    }

    // ==================== 4. 供应商 / 仓库筛选与关键字有界匹配 ====================

    [Fact]
    public async Task 供应商仓库筛选与关键字有界匹配()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        SeedReceipt(db, 631L, "RK-PRSEL-ALPHA", SupplierA, WarehouseA, (ProductA, "PCS", 5m));
        SeedReceipt(db, 632L, "RK-PRSEL-BETA", SupplierB, WarehouseB, (ProductPack, "BOX", 1m));

        var bySupplier = Candidates(await ctl.GetSourceCandidates(SupplierB, null, null, 0));
        Assert.Equal(SupplierB, Assert.Single(bySupplier).SupplierId);

        var byWarehouse = Candidates(await ctl.GetSourceCandidates(null, WarehouseB, null, 0));
        Assert.Equal(WarehouseB, Assert.Single(byWarehouse).WarehouseId);

        var byNo = Candidates(await ctl.GetSourceCandidates(null, null, "ALPHA", 0));
        Assert.Equal("RK-PRSEL-ALPHA", Assert.Single(byNo).SourceStockInNo);

        var bySupplierName = Candidates(await ctl.GetSourceCandidates(null, null, "供应商B", 0));
        Assert.Equal(SupplierB, Assert.Single(bySupplierName).SupplierId);

        var bySpec = Candidates(await ctl.GetSourceCandidates(null, null, "规格A", 0));
        Assert.Equal(2, bySpec.Count);
    }

    // ==================== 5. 来源详情 fail closed 与只读投影 ====================

    [Fact]
    public async Task 来源详情fail_closed_未审核冲突与已删除不存在()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        SeedReceipt(db, 641L, "RK-PRSEL-0641", SupplierA, WarehouseA, (ProductA, "PCS", 10m));
        SeedEffectiveReturn(db, 641L, SupplierA, WarehouseA, (ProductA, "PCS", 3m));
        PurchaseReturnTestAuthorization.SeedStockIn(db, 642L, "RK-PRSEL-0642", SupplierA, WarehouseA,
            DocumentStatus.Pending, null, (ProductA, "PCS", 5m));
        var deleted = PurchaseReturnTestAuthorization.SeedStockIn(db, 643L, "RK-PRSEL-0643", SupplierA,
            WarehouseA, DocumentStatus.Cancelled, null, (ProductA, "PCS", 5m));
        deleted.IsDeleted = true;
        db.SaveChanges();

        var detail = Detail(await ctl.GetSourceCandidateDetail(641L));
        Assert.Equal(641L, detail.SourceStockInId);
        Assert.Equal("RK-PRSEL-0641", detail.SourceStockInNo);
        Assert.Equal(SupplierA, detail.SupplierId);
        Assert.Equal("供应商A", detail.SupplierName);
        Assert.Equal(WarehouseA, detail.WarehouseId);
        Assert.Equal("退货仓A", detail.WarehouseName);
        Assert.True(detail.Available);
        var line = Assert.Single(detail.Lines);
        Assert.Equal(7m, line.RemainingBaseQuantity);
        Assert.Equal(3m, line.EffectiveReturnedBaseQuantity);

        // 未审核 → 冲突；已删除 → 不存在；非法 Id → 参数错误
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(642L));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(643L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 只读投影：不写库、不改单据 / 库存 / 流水
        Assert.Equal(3, db.StockIns.Count());
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 6. 授权 fail closed（无身份 / 缺采购入库菜单 / 撤销菜单） ====================

    [Fact]
    public async Task 来源候选授权fail_closed()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedReceipt(db, 651L, "RK-PRSEL-0651", SupplierA, WarehouseA, (ProductA, "PCS", 10m));

        // 无身份 → 未认证
        var anonymous = PurchaseReturnTestAuthorization.ForUser(db, null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            anonymous.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);

        // 有既有「采购退货」但缺既有「采购入库」菜单 → 权限不足
        var noStockInMenu = PurchaseReturnTestAuthorization.ForUser(db,
            PurchaseReturnTestAuthorization.SeedAuthorizedOperator(db, CustomerA));
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            noStockInMenu.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("采购入库", ex.Message);
        ex = await Assert.ThrowsAsync<BusinessException>(() => noStockInMenu.GetSourceCandidateDetail(651L));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 撤销既有菜单授权后下一次请求立即收敛（不缓存授权）
        var ctl = Authorized(db);
        Assert.NotEmpty(Candidates(await ctl.GetSourceCandidates(null, null, null, 0)));
        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();
        ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 7. 采购入库归属范围：范围外归属来源不出现 ====================

    [Fact]
    public async Task 归属范围外来源不出现在候选与详情()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedCustomer(db, CustomerA, "客户A");
        SeedCustomer(db, CustomerB, "客户B");
        var ctl = Authorized(db, CustomerA);                       // 只可见 CustomerA

        var ownOrder = PurchaseReturnTestAuthorization.SeedPurchaseOrder(db, 701L, SupplierA, CustomerA);
        var foreignOrder = PurchaseReturnTestAuthorization.SeedPurchaseOrder(db, 702L, SupplierA, CustomerB);
        PurchaseReturnTestAuthorization.SeedStockIn(db, 703L, "RK-PRSEL-0703", SupplierA, WarehouseA,
            DocumentStatus.Approved, ownOrder.Id, (ProductA, "PCS", 10m));
        PurchaseReturnTestAuthorization.SeedStockIn(db, 704L, "RK-PRSEL-0704", SupplierA, WarehouseA,
            DocumentStatus.Approved, foreignOrder.Id, (ProductA, "PCS", 10m));
        // 未链接入库单：已映射入库操作员可见（不泄露范围外客户）
        SeedReceipt(db, 705L, "RK-PRSEL-0705", SupplierA, WarehouseA, (ProductA, "PCS", 4m));

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, null, 0));
        Assert.Equal(new long[] { 703L, 705L },
            rows.Select(r => r.SourceStockInId).OrderBy(i => i).ToArray());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(704L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 8. 显式选择 → 保存 → 重开：保留权威来源与部分数量，不臆造价格 ====================

    [Fact]
    public async Task 显式选择保存后重开_保留权威来源与部分数量_绝不臆造价格()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var ctl = Authorized(db);
        SeedReceipt(db, 671L, "RK-PRSEL-0671", SupplierA, WarehouseA, (ProductA, "PCS", 8m));
        SeedEffectiveReturn(db, 671L, SupplierA, WarehouseA, (ProductA, "PCS", 3m));

        // 业务表单显式选择来源：用详情行回填权威来源 Id / 单号 / 供应商 / 仓库与可退商品行
        var detail = Detail(await ctl.GetSourceCandidateDetail(671L));
        var line = Assert.Single(detail.Lines);
        Assert.True(line.Available);
        Assert.Equal(5m, line.RemainingBaseQuantity);

        var create = new PurchaseReturn
        {
            ReturnDate = DateTime.Today,
            SupplierId = detail.SupplierId,
            SupplierName = detail.SupplierName,
            WarehouseId = detail.WarehouseId,
            SourceStockInId = detail.SourceStockInId,
            SourceStockInNo = detail.SourceStockInNo,
            ReturnReason = "质量",
            Details = new List<PurchaseReturnDetail>
            {
                new()
                {
                    ProductId = line.ProductId,
                    ProductName = line.ProductName,
                    Spec = line.Spec,
                    Unit = line.BaseUnit,
                    Quantity = 2m,              // 正数部分数量（≤ 净可退容量）
                    UnitPrice = 0m,             // 绝不臆造价格
                    UnitCost = 0m               // 成本语义保留：留 0 由服务端按来源入库成本兜底
                }
            }
        };

        var id = CreatedId(await ctl.Create(create));

        // 重开：权威来源 Id / 单号 / 供应商 / 仓库与明细原样保留
        var reopened = Assert.IsType<PurchaseReturn>(Assert.IsType<ApiResponse<PurchaseReturn>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(id)).Value).Data!);
        Assert.Equal(671L, reopened.SourceStockInId);
        Assert.Equal("RK-PRSEL-0671", reopened.SourceStockInNo);
        Assert.Equal(SupplierA, reopened.SupplierId);
        Assert.Equal(WarehouseA, reopened.WarehouseId);
        var savedLine = Assert.Single(reopened.Details);
        Assert.Equal(line.ProductId, savedLine.ProductId);
        Assert.Equal("PCS", savedLine.Unit);
        Assert.Equal(2m, savedLine.Quantity);
        Assert.Equal(0m, savedLine.UnitPrice);

        // 待提交退货不计入「已生效退货」：净可退容量仍是 5（只在审核后才扣减）
        var after = Candidates(await ctl.GetSourceCandidates(null, null, null, 0))
            .Single(r => r.SourceStockInId == 671L);
        Assert.Equal(5m, after.RemainingBaseQuantity);

        // 未选择来源 = 显式无来源（null），绝不按文本猜测链接
        var unlinked = new PurchaseReturn
        {
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            ReturnReason = "无来源",
            Details = new List<PurchaseReturnDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Spec = "规格A",
                    Unit = "PCS", Quantity = 1m
                }
            }
        };
        var unlinkedId = CreatedId(await ctl.Create(unlinked));
        Assert.Null(db.PurchaseReturns.Single(o => o.Id == unlinkedId).SourceStockInId);
    }

    // ==================== 9. 候选条数与关键字钳制（有界，绝不无界拉取） ====================

    [Fact]
    public void 候选条数与关键字钳制()
    {
        Assert.Equal(PurchaseReturnSourceRules.DefaultCandidateTake,
            PurchaseReturnSourceRules.ClampTake(0));
        Assert.Equal(PurchaseReturnSourceRules.DefaultCandidateTake,
            PurchaseReturnSourceRules.ClampTake(-5));
        Assert.Equal(PurchaseReturnSourceRules.MaxCandidateTake,
            PurchaseReturnSourceRules.ClampTake(9999));
        Assert.Equal(20, PurchaseReturnSourceRules.ClampTake(20));

        Assert.Equal(string.Empty, PurchaseReturnSourceRules.NormalizeKeyword(null));
        Assert.Equal("abc", PurchaseReturnSourceRules.NormalizeKeyword("  abc  "));
        Assert.Equal(PurchaseReturnSourceRules.MaxKeywordLength,
            PurchaseReturnSourceRules.NormalizeKeyword(new string('x', 500)).Length);
    }
}
