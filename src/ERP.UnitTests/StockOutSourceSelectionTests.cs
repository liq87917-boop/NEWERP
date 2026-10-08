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
/// ERP-376 销售出库来源候选 / 详情与业务表单显式选择单元测试（内存库，不连接 SQL Server、不启动 API）。
/// <para>覆盖：有界只读候选（客户数据范围先于计数 / 取数、只含未删除且已审核来源、客户筛选、有界关键字）、
/// 「来源销售订单 + 商品」聚合（授权数量按 ERP-343 折算基础单位，扣除已审核出库数量，待提交预留不计入）、
/// 重复 / 歧义明细、单位未知、商品缺失、已发货满额显式不可用、**销售退货绝不恢复发货容量**、
/// 来源详情 fail closed、显式选择保存后重开保留权威来源与部分数量、表单「未选择来源」（0）归一为 null 保留历史语义，
/// 以及复用既有「销售出库」+「销售订单」菜单与 ERP-097 实时客户数据范围的 fail closed 授权（不新增用户授权、无管理员兜底）。</para>
/// </summary>
public class StockOutSourceSelectionTests
{
    private const long CustomerA = 976001L;
    private const long CustomerB = 976002L;
    private const long WarehouseA = 976101L;
    private const long ProductA = 976201L;
    private const long ProductPack = 976202L;
    private const long ProductUnknown = 976203L;
    private const long MissingProduct = 976299L;

    // ==================== 播种辅助 ====================

    private static void SeedMaster(ErpDbContext db)
    {
        db.BaseWarehouses.Add(new BaseWarehouse
        {
            Id = WarehouseA, WarehouseCode = "SO-WH-A", WarehouseName = "出库仓A", Status = 1
        });
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = CustomerA, CustomerCode = "SO-C-A", CustomerName = "客户A", Status = 1
        });
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductA, ProductCode = "SO-A", ProductName = "商品A", Spec = "规格A", Unit = "PCS", Status = 1
        });
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductPack, ProductCode = "SO-P", ProductName = "商品P", Spec = "规格P",
            Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12, Status = 1
        });
        db.BaseProducts.Add(new BaseProduct
        {
            Id = ProductUnknown, ProductCode = "SO-U", ProductName = "商品U", Spec = "规格U", Unit = "PCS", Status = 1
        });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code, string name, string path)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            Icon = "Package", SortOrder = 10, MenuType = MenuType.Menu, CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void AttachMenu(ErpDbContext db, long roleId, string code, string name, string path)
    {
        var menu = SeedMenu(db, code, name, path);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

    /// <summary>播种特权出库来源操作员（系统内置角色 + 既有「销售出库」菜单 + 可选既有「销售订单」菜单）。</summary>
    private static long SeedSourceOperator(ErpDbContext db, bool withSalesOrderMenu = true,
        UserStatus status = UserStatus.Enabled)
    {
        var role = new SysRole { RoleCode = $"SO-SRC-{Guid.NewGuid():N}", RoleName = "出库来源管理员", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"so-src-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "出库来源管理员", Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        AttachMenu(db, role.Id, StockOutAuthorizationRules.RequiredMenuCode,
            StockOutAuthorizationRules.RequiredMenuText, "/logistics/stock-out");
        if (withSalesOrderMenu)
            AttachMenu(db, role.Id, StockOutAuthorizationRules.SourceRequiredMenuCode,
                StockOutAuthorizationRules.SourceRequiredMenuText, "/order/sales");

        return user.Id;
    }
    /// <summary>播种受限制出库操作员（业务员映射 + 既有「销售出库」菜单 + 可选既有「销售订单」菜单）。</summary>
    private static (long UserId, long CustomerId) SeedRestrictedOperator(ErpDbContext db,
        bool withSalesOrderMenu = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"so-op-{Guid.NewGuid():N}", EmployeeName = "出库操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "出库操作员", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-OWN-{Guid.NewGuid():N}", CustomerName = "本人客户", EmpId = employee.Id, Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"SOOp-{Guid.NewGuid():N}", RoleName = "出库操作员角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        AttachMenu(db, role.Id, StockOutAuthorizationRules.RequiredMenuCode,
            StockOutAuthorizationRules.RequiredMenuText, "/logistics/stock-out");
        if (withSalesOrderMenu)
            AttachMenu(db, role.Id, StockOutAuthorizationRules.SourceRequiredMenuCode,
                StockOutAuthorizationRules.SourceRequiredMenuText, "/order/sales");

        return (user.Id, customer.Id);
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

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, DocumentStatus status, long customerId,
        bool deleted = false,
        params (long ProductId, string ProductName, string Spec, string Unit, decimal Quantity)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        foreach (var line in lines)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
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
    private static StockOut SeedShipment(ErpDbContext db, string no, long? orderId, long customerId,
        DocumentStatus status, bool deleted = false,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            SalesOrderId = orderId,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            Status = status,
            IsDeleted = deleted,
            TotalQuantity = lines.Sum(l => l.Quantity)
        };
        db.StockOuts.Add(shipment);
        db.SaveChanges();

        foreach (var line in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = shipment.Id,
                ProductId = line.ProductId,
                ProductName = $"商品{line.ProductId}",
                Spec = "规格A",
                Unit = line.Unit,
                Quantity = line.Quantity
            });
        }
        db.SaveChanges();
        return shipment;
    }

    /// <summary>播种销售退货单（用于证明「销售退货绝不恢复发货容量」）。</summary>
    private static void SeedReturn(ErpDbContext db, long sourceStockOutId, long customerId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-SO-{Guid.NewGuid():N}"[..20],
            ReturnDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "客户",
            WarehouseId = WarehouseA,
            SourceStockOutId = sourceStockOutId,
            ReturnReason = "质量",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity)
        };
        db.SalesReturns.Add(ret);
        db.SaveChanges();

        foreach (var line in lines)
        {
            db.SalesReturnDetails.Add(new SalesReturnDetail
            {
                SalesReturnId = ret.Id,
                ProductId = line.ProductId,
                ProductName = $"商品{line.ProductId}",
                Spec = "规格A",
                Unit = line.Unit,
                Quantity = line.Quantity
            });
        }
        db.SaveChanges();
    }

    private static void SeedStockOutNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockOut,
            RuleCode = "CK-SOSEL",
            RuleName = "销售出库单号",
            Prefix = "CK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    // ==================== 结果解析辅助 ====================

    private static List<StockOutSourceCandidateDto> Candidates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<List<StockOutSourceCandidateDto>>>(ok.Value).Data!;
    }

    private static StockOutSourceDetailDto Detail(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<StockOutSourceDetailDto>>(ok.Value).Data!;
    }

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    // ==================== 1. 候选投影：ERP-343 授权 / 已出库 / 剩余可发（待提交预留不计入） ====================

    [Fact]
    public async Task 候选投影_按ERP343给出授权已发与剩余基础数量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        var order = SeedOrder(db, "SO-SEL-0001", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m), (ProductPack, "商品P", "规格P", "BOX", 2m) });
        SeedShipment(db, "CK-SEL-A", order.Id, CustomerA, DocumentStatus.Approved, false, (ProductA, "PCS", 3m));
        SeedShipment(db, "CK-SEL-P", order.Id, CustomerA, DocumentStatus.Pending, false, (ProductPack, "PCS", 24m));
        SeedShipment(db, "CK-SEL-D", order.Id, CustomerA, DocumentStatus.Approved, true, (ProductA, "PCS", 100m));
        // 无来源 / 其它订单的出库单不影响本订单剩余可发
        SeedShipment(db, "CK-SEL-N", null, CustomerA, DocumentStatus.Approved, false, (ProductA, "PCS", 50m));

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(2, rows.Count);

        var lineA = rows.Single(r => r.ProductId == ProductA);
        Assert.Equal(order.Id, lineA.SalesOrderId);
        Assert.Equal("SO-SEL-0001", lineA.OrderNo);
        Assert.Equal(CustomerA, lineA.CustomerId);
        Assert.Equal("客户A", lineA.CustomerName);
        Assert.Equal("商品A", lineA.ProductName);
        Assert.Equal("规格A", lineA.Spec);
        Assert.Equal("PCS", lineA.BaseUnit);
        Assert.Equal(10m, lineA.AuthorizedBaseQuantity);
        Assert.Equal(3m, lineA.ShippedBaseQuantity);
        Assert.Equal(7m, lineA.RemainingBaseQuantity);
        Assert.True(lineA.Available);
        Assert.Equal(string.Empty, lineA.UnavailableReason);

        // 装箱单位折算：2 箱 × 12 = 24；待提交预留不计入已发货
        var linePack = rows.Single(r => r.ProductId == ProductPack);
        Assert.Equal(24m, linePack.AuthorizedBaseQuantity);
        Assert.Equal(0m, linePack.ShippedBaseQuantity);
        Assert.Equal(24m, linePack.RemainingBaseQuantity);
        Assert.True(linePack.Available);

        // 不暴露无关订单字段（币种 / 汇率 / 金额 / 条款 / 备注）
        Assert.DoesNotContain(typeof(StockOutSourceCandidateDto).GetProperties(), p =>
            p.Name is "Currency" or "ExchangeRate" or "TotalAmount" or "TradeTerms" or "Remark");

        // 只读投影：不落库、不改单据 / 库存 / 流水
        Assert.Single(db.SalesOrders);
        Assert.Equal(4, db.StockOuts.Count());
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    // ==================== 2. 未审核 / 已取消 / 已删除订单不作为来源 ====================

    [Fact]
    public async Task 未审核取消删除订单不返回()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        SeedOrder(db, "SO-SEL-PENDING", DocumentStatus.Pending, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        SeedOrder(db, "SO-SEL-CANCELLED", DocumentStatus.Cancelled, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        SeedOrder(db, "SO-SEL-DELETED", DocumentStatus.Approved, CustomerA, deleted: true,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });

        Assert.Empty(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
    }

    // ==================== 3. 重复歧义 / 单位未知 / 商品缺失显式不可用 ====================

    [Fact]
    public async Task 重复歧义与单位未知与商品缺失显式不可用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        var ambiguous = SeedOrder(db, "SO-SEL-AMBIG", DocumentStatus.Approved, CustomerA,
            lines: new[]
            {
                (ProductA, "商品A", "规格A", "PCS", 4m),
                (ProductA, "商品A", "规格A", "PCS", 6m)
            });
        var unknownUnit = SeedOrder(db, "SO-SEL-UNIT", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductUnknown, "商品U", "规格U", "袋", 5m) });
        var missing = SeedOrder(db, "SO-SEL-MISS", DocumentStatus.Approved, CustomerA,
            lines: new[] { (MissingProduct, "缺失商品", "", "PCS", 5m) });

        var list = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.Equal(3, list.Count);
        Assert.All(list, l => Assert.False(l.Available));
        Assert.All(list, l => Assert.Equal(0m, l.RemainingBaseQuantity));

        var ambiguousLine = list.Single(l => l.SalesOrderId == ambiguous.Id);
        Assert.Equal(StockOutOrderFulfillmentRules.AmbiguousLineText, ambiguousLine.UnavailableReason);
        Assert.Equal(0m, ambiguousLine.AuthorizedBaseQuantity);

        Assert.Equal(StockOutOrderFulfillmentRules.UnknownUnitText,
            list.Single(l => l.SalesOrderId == unknownUnit.Id).UnavailableReason);
        Assert.Equal(StockOutOrderFulfillmentRules.CorruptSourceText,
            list.Single(l => l.SalesOrderId == missing.Id).UnavailableReason);
    }
    // ==================== 4. 已发货满额归零：销售退货绝不恢复发货容量 ====================

    [Fact]
    public async Task 已发货满额归零_销售退货绝不恢复发货容量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        var order = SeedOrder(db, "SO-SEL-FULL", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        var shipment = SeedShipment(db, "CK-SEL-FULL", order.Id, CustomerA, DocumentStatus.Approved, false,
            (ProductA, "PCS", 10m));

        var full = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.False(full.Available);
        Assert.Equal(0m, full.RemainingBaseQuantity);
        Assert.Equal(StockOutOrderFulfillmentRules.ZeroCapacityText, full.UnavailableReason);

        // 已审核销售退货（退货入库）绝不恢复发货容量：ERP-343 只认已审核出库数量
        SeedReturn(db, shipment.Id, CustomerA, DocumentStatus.Approved, (ProductA, "PCS", 10m));
        var afterReturn = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.False(afterReturn.Available);
        Assert.Equal(0m, afterReturn.RemainingBaseQuantity);
        Assert.Equal(10m, afterReturn.ShippedBaseQuantity);

        // 未审核 / 已删除出库单不计入已发货数量（绝不当作已过账发货）
        SeedShipment(db, "CK-SEL-PEND2", order.Id, CustomerA, DocumentStatus.Pending, false, (ProductA, "PCS", 10m));
        SeedShipment(db, "CK-SEL-DEL2", order.Id, CustomerA, DocumentStatus.Approved, true, (ProductA, "PCS", 10m));
        var stillFull = Assert.Single(Candidates(await ctl.GetSourceCandidates(null, null, 0)));
        Assert.Equal(10m, stillFull.ShippedBaseQuantity);
        Assert.Equal(0m, stillFull.RemainingBaseQuantity);
    }

    // ==================== 5. 来源详情 fail closed ====================

    [Fact]
    public async Task 来源详情fail_closed_未审核冲突与已删除不存在()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        var approved = SeedOrder(db, "SO-SEL-DETAIL", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        SeedShipment(db, "CK-SEL-DT", approved.Id, CustomerA, DocumentStatus.Approved, false, (ProductA, "PCS", 3m));
        var pending = SeedOrder(db, "SO-SEL-DETAIL-P", DocumentStatus.Pending, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        var deleted = SeedOrder(db, "SO-SEL-DETAIL-D", DocumentStatus.Approved, CustomerA, deleted: true,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });

        var detail = Detail(await ctl.GetSourceCandidateDetail(approved.Id));
        Assert.Equal(approved.Id, detail.SalesOrderId);
        Assert.Equal("SO-SEL-DETAIL", detail.OrderNo);
        Assert.Equal(CustomerA, detail.CustomerId);
        Assert.Equal("客户A", detail.CustomerName);
        Assert.True(detail.Available);
        Assert.Equal(7m, Assert.Single(detail.Lines).RemainingBaseQuantity);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(pending.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(deleted.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(0L));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 只读投影：不写库
        Assert.Equal(3, db.SalesOrders.Count());
        Assert.Single(db.StockOuts);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 6. 数据范围：范围外归属来源不出现 ====================

    [Fact]
    public async Task 范围外归属来源不出现在候选与详情()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedForeignCustomer(db);
        var (userId, ownCustomer) = SeedRestrictedOperator(db);
        var ctl = StockOutTestAuthorization.ForUser(db, userId);

        var own = SeedOrder(db, "SO-SEL-OWN", DocumentStatus.Approved, ownCustomer,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        var foreign = SeedOrder(db, "SO-SEL-FOREIGN", DocumentStatus.Approved, CustomerB,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });

        var rows = Candidates(await ctl.GetSourceCandidates(null, null, 0));
        Assert.Single(rows);
        Assert.Equal(own.Id, rows[0].SalesOrderId);
        Assert.DoesNotContain(rows, r => r.SalesOrderId == foreign.Id);

        Assert.Equal(own.Id, Detail(await ctl.GetSourceCandidateDetail(own.Id)).SalesOrderId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.GetSourceCandidateDetail(foreign.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }
    // ==================== 7. 授权 fail closed（既有「销售出库」+「销售订单」菜单，无匿名 / 管理员兜底） ====================

    [Fact]
    public async Task 来源授权fail_closed()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedOrder(db, "SO-SEL-AUTH", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });

        // 无身份 → 未认证
        var anonymous = StockOutTestAuthorization.ForUser(db, null);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => anonymous.GetSourceCandidates(null, null, 0));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);

        // 有「销售出库」但缺既有「销售订单」菜单 → 权限不足
        var noSourceMenu = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db, withSalesOrderMenu: false));
        ex = await Assert.ThrowsAsync<BusinessException>(() => noSourceMenu.GetSourceCandidates(null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("销售订单", ex.Message);
        ex = await Assert.ThrowsAsync<BusinessException>(() => noSourceMenu.GetSourceCandidateDetail(1L));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        // 已禁用账号 → 权限不足
        var disabled = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db, status: UserStatus.Disabled));
        ex = await Assert.ThrowsAsync<BusinessException>(() => disabled.GetSourceCandidates(null, null, 0));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        Assert.Empty(db.Stocks);
    }

    // ==================== 8. 显式选择 → 保存 → 重开：保留权威来源与部分数量 ====================

    [Fact]
    public async Task 显式选择保存后重开_保留权威来源与部分数量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStockOutNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        var order = SeedOrder(db, "SO-SEL-SAVE", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 10m) });
        SeedShipment(db, "CK-SEL-SAVE-B", order.Id, CustomerA, DocumentStatus.Approved, false, (ProductA, "PCS", 3m));

        // 业务表单显式选择来源：用详情行回填权威来源 Id / 客户与可发商品行
        var detail = Detail(await ctl.GetSourceCandidateDetail(order.Id));
        var line = Assert.Single(detail.Lines);
        Assert.Equal(7m, line.RemainingBaseQuantity);

        var create = new StockOut
        {
            StockOutDate = DateTime.Today,
            SalesOrderId = detail.SalesOrderId,
            CustomerId = detail.CustomerId,
            WarehouseId = WarehouseA,          // 保留用户所选仓库
            Remark = "ERP-376_SOURCE_SELECT",
            Details = new List<StockOutDetail>
            {
                new()
                {
                    ProductId = line.ProductId,
                    ProductName = line.ProductName,
                    Spec = line.Spec,
                    Unit = line.BaseUnit,      // 权威基础单位
                    Quantity = 2m              // 正数部分发货（≤ 剩余可发数量）
                }
            }
        };

        var id = CreatedId(await ctl.Create(create));

        // 重开：权威来源 Id / 客户 / 仓库与明细原样保留
        var reopened = Assert.IsType<StockOut>(Assert.IsType<ApiResponse<StockOut>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(id)).Value).Data!);
        Assert.Equal(order.Id, reopened.SalesOrderId);
        Assert.Equal(CustomerA, reopened.CustomerId);
        Assert.Equal(WarehouseA, reopened.WarehouseId);
        var saved = Assert.Single(reopened.Details);
        Assert.Equal(line.ProductId, saved.ProductId);
        Assert.Equal("PCS", saved.Unit);
        Assert.Equal(2m, saved.Quantity);
        Assert.Equal(2m, reopened.TotalQuantity);

        // 待提交出库不计入「已审核出库」：剩余可发仍是 7（只在审核后才扣减），且未产生库存 / 流水
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
        SeedStockOutNumberRule(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));

        var unlinked = new StockOut
        {
            StockOutDate = DateTime.Today,
            SalesOrderId = 0,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS", Quantity = 1m }
            }
        };

        var id = CreatedId(await ctl.Create(unlinked));
        Assert.Null(db.StockOuts.Single(s => s.Id == id).SalesOrderId);

        // 负数不是「未选择」，仍 fail closed 拒绝且不落库、不消耗单号
        var before = db.StockOuts.Count();
        var negative = new StockOut
        {
            StockOutDate = DateTime.Today,
            SalesOrderId = -1,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            Details = new List<StockOutDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS", Quantity = 1m }
            }
        };
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(negative));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(before, db.StockOuts.Count());
    }
    // ==================== 10. 客户筛选与关键字有界匹配 ====================

    [Fact]
    public async Task 客户筛选与关键字有界匹配()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = StockOutTestAuthorization.ForUser(db, SeedSourceOperator(db));
        SeedOrder(db, "SO-SEL-ALPHA", DocumentStatus.Approved, CustomerA,
            lines: new[] { (ProductA, "商品A", "规格A", "PCS", 5m) });
        var other = SeedForeignCustomer(db);
        SeedOrder(db, "SO-SEL-BETA", DocumentStatus.Approved, other,
            lines: new[] { (ProductPack, "商品P", "规格P", "BOX", 1m) });

        var byCustomer = Candidates(await ctl.GetSourceCandidates(other, null, 0));
        Assert.Single(byCustomer);
        Assert.Equal(other, byCustomer[0].CustomerId);

        var byOrderNo = Candidates(await ctl.GetSourceCandidates(null, "ALPHA", 0));
        Assert.Single(byOrderNo);
        Assert.Equal("SO-SEL-ALPHA", byOrderNo[0].OrderNo);

        var byCustomerName = Candidates(await ctl.GetSourceCandidates(null, "范围外客户", 0));
        Assert.Single(byCustomerName);
        Assert.Equal(other, byCustomerName[0].CustomerId);

        var bySpec = Candidates(await ctl.GetSourceCandidates(null, "规格P", 0));
        Assert.Single(bySpec);
        Assert.Equal(ProductPack, bySpec[0].ProductId);
    }

    // ==================== 11. 候选条数与关键字钳制（有界，绝不无界拉取） ====================

    [Fact]
    public void 候选条数与关键字钳制()
    {
        Assert.Equal(StockOutOrderFulfillmentRules.DefaultCandidateTake,
            StockOutOrderFulfillmentRules.ClampTake(0));
        Assert.Equal(StockOutOrderFulfillmentRules.DefaultCandidateTake,
            StockOutOrderFulfillmentRules.ClampTake(-5));
        Assert.Equal(StockOutOrderFulfillmentRules.MaxCandidateTake,
            StockOutOrderFulfillmentRules.ClampTake(9999));
        Assert.Equal(20, StockOutOrderFulfillmentRules.ClampTake(20));

        Assert.Equal(string.Empty, StockOutOrderFulfillmentRules.NormalizeKeyword(null));
        Assert.Equal("abc", StockOutOrderFulfillmentRules.NormalizeKeyword("  abc  "));
        Assert.Equal(StockOutOrderFulfillmentRules.MaxKeywordLength,
            StockOutOrderFulfillmentRules.NormalizeKeyword(new string('x', 500)).Length);
    }
}
