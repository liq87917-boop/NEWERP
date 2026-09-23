using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 报表服务单元测试：覆盖 7 个业务报表的核心数值逻辑与边界场景
/// （商品销量排名 / 订单利润暂估 / 客户出货量 / 业务员产值 / 资产负债表 / 利润表 / 现金流量表）
/// </summary>
public class ReportServiceTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 1. 商品销量排名榜 ====================

    [Fact]
    public async Task GetProductSalesRankingAsync_按销量降序_取Top_并填充商品Code()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);
        var (out1, out2) = SeedStockOutsInRange(db);

        // 商品 1 出 100、商品 2 出 50、商品 3 出 30
        db.StockOutDetails.AddRange(
            new StockOutDetail { StockOutId = out1.Id, ProductId = 1, ProductName = "热销商品", Spec = "大", Unit = "PCS", Quantity = 100m },
            new StockOutDetail { StockOutId = out1.Id, ProductId = 2, ProductName = "平销商品", Spec = "中", Unit = "PCS", Quantity = 50m },
            new StockOutDetail { StockOutId = out2.Id, ProductId = 3, ProductName = "冷门商品", Spec = "小", Unit = "PCS", Quantity = 30m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10);

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal(100m, result[0].TotalQuantity);
        Assert.Equal("P001", result[0].ProductCode);
        Assert.Equal(100m * 100m, result[0].TotalAmount);   // 销量 × 售价
        Assert.Equal(2, result[1].Rank);
        Assert.Equal(50m, result[1].TotalQuantity);
        Assert.Equal(3, result[2].Rank);
        Assert.Equal(30m, result[2].TotalQuantity);
    }

    [Fact]
    public async Task GetProductSalesRankingAsync_日期范围外的出库_不计入()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);

        var inRange = new StockOut { StockOutNo = "IN", StockOutDate = new DateTime(2026, 9, 10), Status = DocumentStatus.Approved };
        var outOfRange = new StockOut { StockOutNo = "OUT", StockOutDate = new DateTime(2026, 8, 25), Status = DocumentStatus.Approved }; // 8 月底
        db.StockOuts.AddRange(inRange, outOfRange);
        await db.SaveChangesAsync();

        db.StockOutDetails.AddRange(
            new StockOutDetail { StockOutId = inRange.Id, ProductId = 1, ProductName = "热销商品", Quantity = 100m, Unit = "PCS" },
            new StockOutDetail { StockOutId = outOfRange.Id, ProductId = 1, ProductName = "热销商品", Quantity = 999m, Unit = "PCS" }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10);

        Assert.Single(result);
        Assert.Equal(100m, result[0].TotalQuantity);
    }

    [Fact]
    public async Task GetProductSalesRankingAsync_软删除的明细不计入()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);
        var (out1, _) = SeedStockOutsInRange(db);

        db.StockOutDetails.AddRange(
            new StockOutDetail { StockOutId = out1.Id, ProductId = 1, ProductName = "热销商品", Quantity = 100m, Unit = "PCS" },
            new StockOutDetail { StockOutId = out1.Id, ProductId = 2, ProductName = "平销商品", Quantity = 50m, Unit = "PCS", IsDeleted = true }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 10);

        Assert.Single(result);
        Assert.Equal(100m, result[0].TotalQuantity);
    }

    // ==================== 2. 订单利润暂估表 ====================

    [Fact]
    public async Task GetOrderProfitEstimateAsync_销售额减明细成本_并按利润率()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        SeedProducts(db); // 商品 1 成本 60、商品 2 成本 120

        var order = new SalesOrder
        {
            OrderNo = "SO001",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            Status = DocumentStatus.Approved,
            TotalAmount = 5000m
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        // 明细：商品 1 × 10（成本 60 → 600）+ 商品 2 × 5（成本 120 → 600）= 1200 成本
        db.SalesOrderDetails.AddRange(
            new SalesOrderDetail { SalesOrderId = order.Id, ProductId = 1, ProductName = "热销商品", Quantity = 10m, Unit = "PCS", UnitPrice = 100m, Amount = 1000m },
            new SalesOrderDetail { SalesOrderId = order.Id, ProductId = 2, ProductName = "平销商品", Quantity = 5m, Unit = "PCS", UnitPrice = 800m, Amount = 4000m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End);

        Assert.Single(result);
        Assert.Equal(5000m, result[0].SalesAmount);
        Assert.Equal(1200m, result[0].CostAmount);
        Assert.Equal(3800m, result[0].Profit);
        Assert.Equal(76.00m, result[0].ProfitRate);    // 3800 / 5000 * 100
    }

    [Fact]
    public async Task GetOrderProfitEstimateAsync_已取消订单排除()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedProducts(db);

        db.SalesOrders.AddRange(
            new SalesOrder { OrderNo = "SO-A", OrderDate = new DateTime(2026, 9, 5), CustomerId = customer.Id, Status = DocumentStatus.Approved, TotalAmount = 1000m },
            new SalesOrder { OrderNo = "SO-B", OrderDate = new DateTime(2026, 9, 6), CustomerId = customer.Id, Status = DocumentStatus.Cancelled, TotalAmount = 9999m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End);

        Assert.Single(result);
        Assert.Equal("SO-A", result[0].OrderNo);
    }

    [Fact]
    public async Task GetOrderProfitEstimateAsync_零金额订单的利润率不抛异常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedProducts(db);

        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-ZERO",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            Status = DocumentStatus.Approved,
            TotalAmount = 0m
        });
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End);

        Assert.Single(result);
        Assert.Equal(0m, result[0].ProfitRate);   // 分母保护：0 除以 0 不抛
    }

    // ==================== 3. 客户出货量统计表 ====================

    [Fact]
    public async Task GetCustomerShipmentStatsAsync_按客户聚合_按总金额降序()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);
        var (cA, cB) = (SeedCustomer(db, "CA", "A客户"), SeedCustomer(db, "CB", "B客户"));

        // A 客户：2 单合计 10000；B 客户：1 单合计 8000
        db.SalesOrders.AddRange(
            new SalesOrder { OrderNo = "SO-A1", OrderDate = new DateTime(2026, 9, 5), CustomerId = cA.Id, Status = DocumentStatus.Approved, TotalAmount = 4000m },
            new SalesOrder { OrderNo = "SO-A2", OrderDate = new DateTime(2026, 9, 12), CustomerId = cA.Id, Status = DocumentStatus.Submitted, TotalAmount = 6000m },
            new SalesOrder { OrderNo = "SO-B1", OrderDate = new DateTime(2026, 9, 15), CustomerId = cB.Id, Status = DocumentStatus.Approved, TotalAmount = 8000m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End);

        Assert.Equal(2, result.Count);
        Assert.Equal("A客户", result[0].CustomerName);
        Assert.Equal(2, result[0].OrderCount);
        Assert.Equal(10000m, result[0].TotalAmount);
        Assert.Equal("B客户", result[1].CustomerName);
        Assert.Equal(1, result[1].OrderCount);
        Assert.Equal(8000m, result[1].TotalAmount);
    }

    // ==================== 4. 业务员产值报表 ====================

    [Fact]
    public async Task GetSalesmanOutputAsync_业务员产值与利润_qty乘sale减cost()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);   // 商品1 售价100/成本60、 商品2 售价200/成本120
        var customer = SeedCustomer(db, "C001", "客户");
        var s1 = SeedEmployee(db, "S001", "业务员甲");
        var s2 = SeedEmployee(db, "S002", "业务员乙");

        db.SalesOrders.AddRange(
            new SalesOrder { OrderNo = "SO-1", OrderDate = new DateTime(2026, 9, 5),  CustomerId = customer.Id, SalesmanId = s1.Id, Status = DocumentStatus.Approved, TotalAmount = 1000m },
            new SalesOrder { OrderNo = "SO-2", OrderDate = new DateTime(2026, 9, 12), CustomerId = customer.Id, SalesmanId = s2.Id, Status = DocumentStatus.Approved, TotalAmount = 4000m }
        );
        await db.SaveChangesAsync();

        // 业务员1 产值 1000，利润明细：商品1 × 10 × (100-60) = 400
        // 业务员2 产值 4000，利润明细：商品2 × 20 × (200-120) = 1600
        db.SalesOrderDetails.AddRange(
            new SalesOrderDetail { SalesOrderId = 1, ProductId = 1, Quantity = 10m, UnitPrice = 100m, Amount = 1000m },
            new SalesOrderDetail { SalesOrderId = 2, ProductId = 2, Quantity = 20m, UnitPrice = 200m, Amount = 4000m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End);

        Assert.Equal(2, result.Count);
        // 按产值降序
        Assert.Equal("业务员乙", result[0].SalesmanName);
        Assert.Equal(4000m, result[0].TotalAmount);
        Assert.Equal(1600m, result[0].TotalProfit);
        Assert.Equal("业务员甲", result[1].SalesmanName);
        Assert.Equal(1000m, result[1].TotalAmount);
        Assert.Equal(400m, result[1].TotalProfit);
    }

    [Fact]
    public async Task GetSalesmanOutputAsync_无业务员订单被排除()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);
        var customer = SeedCustomer(db, "C001", "客户");

        db.SalesOrders.AddRange(
            new SalesOrder { OrderNo = "SO-YES", OrderDate = new DateTime(2026, 9, 5), CustomerId = customer.Id, SalesmanId = 99, Status = DocumentStatus.Approved, TotalAmount = 1000m },
            new SalesOrder { OrderNo = "SO-NO",  OrderDate = new DateTime(2026, 9, 6), CustomerId = customer.Id, SalesmanId = null, Status = DocumentStatus.Approved, TotalAmount = 5000m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End);

        // SO-YES 会被算到一个不存在的业务员（id=99），SO-NO 被排除
        Assert.Single(result);
        Assert.Equal(1000m, result[0].TotalAmount);
    }

    // ==================== 5. 资产负债表 ====================

    [Fact]
    public async Task GetBalanceSheetAsync_库存应收应付聚合_资产负债权益公式()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db); // 商品 1 成本 60、商品 2 成本 120
        var customer = SeedCustomer(db, "C001", "客户");
        SeedWarehouse(db);

        // 库存：商品 1 × 10 + 商品 2 × 5 = 600 + 600 = 1200
        db.Stocks.AddRange(
            new Stock { WarehouseId = 1, ProductId = 1, Quantity = 10m },
            new Stock { WarehouseId = 1, ProductId = 2, Quantity = 5m }
        );

        // 应收：已提交/已审核销售单总和（排除已驳回/草稿）
        db.SalesOrders.AddRange(
            new SalesOrder { OrderNo = "SO-A", OrderDate = new DateTime(2026, 9, 5),  CustomerId = customer.Id, Status = DocumentStatus.Approved,  TotalAmount = 3000m },
            new SalesOrder { OrderNo = "SO-B", OrderDate = new DateTime(2026, 9, 10), CustomerId = customer.Id, Status = DocumentStatus.Submitted, TotalAmount = 2000m },
            new SalesOrder { OrderNo = "SO-C", OrderDate = new DateTime(2026, 9, 12), CustomerId = customer.Id, Status = DocumentStatus.Pending,    TotalAmount = 9999m } // 草稿不计入应收
        );

        // 应付：已提交/已审核采购单总和
        db.PurchaseOrders.AddRange(
            new PurchaseOrder { OrderNo = "PO-A", OrderDate = new DateTime(2026, 9, 4), SupplierId = 1, Status = DocumentStatus.Approved,  TotalAmount = 1500m },
            new PurchaseOrder { OrderNo = "PO-B", OrderDate = new DateTime(2026, 9, 8), SupplierId = 1, Status = DocumentStatus.Rejected,  TotalAmount = 7777m } // 驳回不计入
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var bs = await service.GetBalanceSheetAsync(new DateTime(2026, 9, 30));

        Assert.Equal("资产负债表", bs.Title);
        var inventory = bs.Lines.Single(x => x.Name == "库存价值").Amount;
        var receivable = bs.Lines.Single(x => x.Name == "应收账款").Amount;
        var totalAssets = bs.Lines.Single(x => x.Name == "资产合计").Amount;
        var payable = bs.Lines.Single(x => x.Name == "应付账款").Amount;
        var totalLiab = bs.Lines.Single(x => x.Name == "负债合计").Amount;
        var equity = bs.Lines.Single(x => x.Name == "所有者权益").Amount;

        Assert.Equal(1200m, inventory);
        Assert.Equal(5000m, receivable);            // 3000 + 2000
        Assert.Equal(6200m, totalAssets);           // 1200 + 5000
        Assert.Equal(1500m, payable);
        Assert.Equal(1500m, totalLiab);
        Assert.Equal(4700m, equity);                // 6200 - 1500
    }

    [Fact]
    public async Task GetBalanceSheetAsync_asOfDate之后的单据不计入应收应付()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        db.SalesOrders.AddRange(
            new SalesOrder { OrderNo = "SO-BEFORE", OrderDate = new DateTime(2026, 8, 30), CustomerId = customer.Id, Status = DocumentStatus.Approved, TotalAmount = 1000m },
            new SalesOrder { OrderNo = "SO-AFTER",  OrderDate = new DateTime(2026, 10, 5),  CustomerId = customer.Id, Status = DocumentStatus.Approved, TotalAmount = 9999m }
        );
        db.PurchaseOrders.AddRange(
            new PurchaseOrder { OrderNo = "PO-BEFORE", OrderDate = new DateTime(2026, 8, 30), SupplierId = 1, Status = DocumentStatus.Approved, TotalAmount = 500m },
            new PurchaseOrder { OrderNo = "PO-AFTER",  OrderDate = new DateTime(2026, 10, 5),  SupplierId = 1, Status = DocumentStatus.Approved, TotalAmount = 8888m }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var bs = await service.GetBalanceSheetAsync(new DateTime(2026, 9, 30));

        Assert.Equal(1000m, bs.Lines.Single(x => x.Name == "应收账款").Amount);
        Assert.Equal(500m,  bs.Lines.Single(x => x.Name == "应付账款").Amount);
    }

    // ==================== 6. 利润表 ====================

    [Fact]
    public async Task GetIncomeStatementAsync_已审核收入减支出_且日期范围过滤()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        // 9 月内已审核 1000、9 月内草稿 9999（不计入）
        db.FinanceReceipts.AddRange(
            new FinanceReceipt { ReceiptNo = "R-IN",  ReceiptDate = new DateTime(2026, 9, 5),  CustomerId = customer.Id, Amount = 1000m, Status = DocumentStatus.Approved },
            new FinanceReceipt { ReceiptNo = "R-OUT", ReceiptDate = new DateTime(2026, 9, 8),  CustomerId = customer.Id, Amount = 9999m, Status = DocumentStatus.Pending }, // 草稿不计入
            new FinanceReceipt { ReceiptNo = "R-PAST", ReceiptDate = new DateTime(2026, 8, 25), CustomerId = customer.Id, Amount = 5555m, Status = DocumentStatus.Approved }     // 8 月不计入
        );
        db.FinancePayments.AddRange(
            new FinancePayment { PaymentNo = "P-IN",  PaymentDate = new DateTime(2026, 9, 6),  SupplierId = 1, Amount = 300m, Status = DocumentStatus.Approved },
            new FinancePayment { PaymentNo = "P-OUT", PaymentDate = new DateTime(2026, 9, 12), SupplierId = 1, Amount = 7777m, Status = DocumentStatus.Submitted } // 未审核不计入
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var inc = await service.GetIncomeStatementAsync(Start, End);

        Assert.Equal("利润表", inc.Title);
        Assert.Equal(1000m, inc.Lines.Single(x => x.Name == "营业收入").Amount);
        Assert.Equal(300m,  inc.Lines.Single(x => x.Name == "营业支出").Amount);
        Assert.Equal(700m,  inc.Lines.Single(x => x.Name == "净利润").Amount);
        Assert.Equal(700m,  inc.Total);
    }

    // ==================== 7. 现金流量表 ====================

    [Fact]
    public async Task GetCashFlowStatementAsync_流入减流出_与利润表共享规则()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        db.FinanceReceipts.AddRange(
            new FinanceReceipt { ReceiptNo = "R1", ReceiptDate = new DateTime(2026, 9, 5), CustomerId = customer.Id, Amount = 2000m, Status = DocumentStatus.Approved },
            new FinanceReceipt { ReceiptNo = "R2", ReceiptDate = new DateTime(2026, 9, 15), CustomerId = customer.Id, Amount = 800m, Status = DocumentStatus.Approved }
        );
        db.FinancePayments.AddRange(
            new FinancePayment { PaymentNo = "P1", PaymentDate = new DateTime(2026, 9, 10), SupplierId = 1, Amount = 500m, Status = DocumentStatus.Approved },
            new FinancePayment { PaymentNo = "P2", PaymentDate = new DateTime(2026, 9, 20), SupplierId = 1, Amount = 300m, Status = DocumentStatus.Approved }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var cf = await service.GetCashFlowStatementAsync(Start, End);

        Assert.Equal("现金流量表", cf.Title);
        Assert.Equal(2800m, cf.Lines.Single(x => x.Name == "经营活动现金流入").Amount);  // 2000 + 800
        Assert.Equal(800m,  cf.Lines.Single(x => x.Name == "经营活动现金流出").Amount);  // 500 + 300
        Assert.Equal(2000m, cf.Lines.Single(x => x.Name == "现金净流量").Amount);          // 2800 - 800
        Assert.Equal(2000m, cf.Total);
    }

    // ==================== 种子数据辅助 ====================

    private static void SeedProducts(ErpDbContext db)
    {
        db.BaseProducts.AddRange(
            new BaseProduct { ProductCode = "P001", ProductName = "热销商品", SalePrice = 100m, CostPrice = 60m,  Status = 1 },
            new BaseProduct { ProductCode = "P002", ProductName = "平销商品", SalePrice = 200m, CostPrice = 120m, Status = 1 },
            new BaseProduct { ProductCode = "P003", ProductName = "冷门商品", SalePrice = 300m, CostPrice = 200m, Status = 1 }
        );
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var c = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(c);
        db.SaveChanges();
        return c;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name)
    {
        var e = new BaseEmployee { EmployeeCode = code, EmployeeName = name, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static void SeedWarehouse(ErpDbContext db)
    {
        db.BaseWarehouses.Add(new BaseWarehouse { WarehouseCode = "W01", WarehouseName = "主仓", Status = 1 });
        db.SaveChanges();
    }

    private static (StockOut out1, StockOut out2) SeedStockOutsInRange(ErpDbContext db)
    {
        var out1 = new StockOut { StockOutNo = "OUT1", StockOutDate = new DateTime(2026, 9, 10), Status = DocumentStatus.Approved };
        var out2 = new StockOut { StockOutNo = "OUT2", StockOutDate = new DateTime(2026, 9, 15), Status = DocumentStatus.Approved };
        db.StockOuts.AddRange(out1, out2);
        db.SaveChanges();
        return (out1, out2);
    }

    // ==================== 边界场景（追加）====================

    [Fact]
    public async Task GetProductSalesRankingAsync_Top超限_只返回前N个且Rank连续()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);
        var (out1, _) = SeedStockOutsInRange(db);

        // 5 个商品，top=2 → 只返回 2 个，Rank 1/2
        db.StockOutDetails.AddRange(
            new StockOutDetail { StockOutId = out1.Id, ProductId = 1, ProductName = "商品1", Quantity = 100m, Unit = "PCS" },
            new StockOutDetail { StockOutId = out1.Id, ProductId = 2, ProductName = "商品2", Quantity = 90m,  Unit = "PCS" },
            new StockOutDetail { StockOutId = out1.Id, ProductId = 3, ProductName = "商品3", Quantity = 80m,  Unit = "PCS" },
            // 商品 1 同明细再加一次，应合并（按 ProductId 分组）
            new StockOutDetail { StockOutId = out1.Id, ProductId = 1, ProductName = "商品1", Quantity = 50m,  Unit = "PCS" }
        );
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetProductSalesRankingAsync(Start, End, 2);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal(150m, result[0].TotalQuantity);  // 100 + 50 合并
        Assert.Equal(2, result[1].Rank);
        Assert.Equal(90m, result[1].TotalQuantity);
    }

    [Fact]
    public async Task GetProductSalesRankingAsync_无任何出库_返回空列表()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);   // 商品存在但没出库
        var service = new ReportService(db);

        var result = await service.GetProductSalesRankingAsync(Start, End, 10);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSalesmanOutputAsync_销售价低于成本_利润为负()
    {
        using var db = TestDbFactory.Create();
        SeedProducts(db);    // 必须先有商品，否则后面 prod 查询为空
        var customer = SeedCustomer(db, "C001", "客户");
        var s = SeedEmployee(db, "S001", "业务员甲");

        // 商品成本 60，售价 100 改为 50（手动覆盖）
        var prod = db.BaseProducts.Single(p => p.ProductCode == "P001");
        prod.SalePrice = 50m;
        await db.SaveChangesAsync();   // 必须 SaveChanges 才能让 ReportService 读到新的 SalePrice

        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-LOSS",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            SalesmanId = s.Id,
            Status = DocumentStatus.Approved,
            TotalAmount = 500m
        });
        await db.SaveChangesAsync();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = 1,
            ProductId = prod.Id,
            Quantity = 10m,
            UnitPrice = 50m,
            Amount = 500m
        });
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End);

        Assert.Single(result);
        Assert.Equal(500m, result[0].TotalAmount);
        Assert.Equal(-100m, result[0].TotalProfit);   // 10 × (50 − 60) = −100
    }

    [Fact]
    public async Task GetIncomeStatementAsync_无任何收付款_返回零值报表()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var inc = await service.GetIncomeStatementAsync(Start, End);

        Assert.Equal("利润表", inc.Title);
        Assert.Equal(0m, inc.Lines.Single(x => x.Name == "营业收入").Amount);
        Assert.Equal(0m, inc.Lines.Single(x => x.Name == "营业支出").Amount);
        Assert.Equal(0m, inc.Lines.Single(x => x.Name == "净利润").Amount);
        Assert.Equal(0m, inc.Total);
    }

    [Fact]
    public async Task GetBalanceSheetAsync_无库存无订单_返回零值报表_权益为零()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var bs = await service.GetBalanceSheetAsync(new DateTime(2026, 9, 30));

        Assert.Equal("资产负债表", bs.Title);
        Assert.Equal(0m, bs.Lines.Single(x => x.Name == "库存价值").Amount);
        Assert.Equal(0m, bs.Lines.Single(x => x.Name == "应收账款").Amount);
        Assert.Equal(0m, bs.Lines.Single(x => x.Name == "资产合计").Amount);
        Assert.Equal(0m, bs.Lines.Single(x => x.Name == "应付账款").Amount);
        Assert.Equal(0m, bs.Lines.Single(x => x.Name == "负债合计").Amount);
        Assert.Equal(0m, bs.Lines.Single(x => x.Name == "所有者权益").Amount);    // 0 - 0 = 0
        Assert.Equal(0m, bs.Total);
    }
}