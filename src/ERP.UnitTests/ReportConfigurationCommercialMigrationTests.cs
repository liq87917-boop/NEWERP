using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-300 商品销量排名 / 订单利润暂估 / 业务员提成迁移为受控数据集适配器的单元测试：
/// 覆盖目录暴露与币种 / 单位口径、预览与既有报表服务逐行一致、分页 / 空页、撤销菜单立即收敛（fail closed）、
/// 未知字段 / 筛选 / 分页超限拒绝，以及成本 / 利润 / 提成可空证据语义。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationCommercialMigrationTests
{
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 0. 测试脚手架 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role");
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static SysUser SeedPrivilegedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-sys-role", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = true,
            Status = 1,
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string name, decimal salePrice = 0m, decimal costPrice = 0m)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = name,
            Spec = "标准",
            Unit = "PCS",
            SalePrice = salePrice,
            CostPrice = costPrice,
            Status = 1,
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            Status = status,
            IsDeleted = false,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedStockOutDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string spec, string unit, decimal quantity)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = spec,
            Unit = unit,
            Quantity = quantity,
            IsDeleted = false,
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency, decimal totalAmount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static IReportService NewReportService(ErpDbContext db) => new ReportService(db);

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
    {
        var reportService = NewReportService(db);
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetProductSalesRanking => new ProductSalesRankingReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetOrderProfit => new OrderProfitEstimateReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetSalesCommission => new SalesCommissionReportConfigurationDatasetProvider(reportService, db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };
    }

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 200)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    private static List<string> DefaultFieldsFor(string datasetKey)
        => datasetKey switch
        {
            ReportConfigurationConstants.DatasetProductSalesRanking => new List<string>
            {
                "rank", "productId", "productCode", "productName", "spec", "unit", "totalQuantity", "totalAmount", "amountLabel",
            },
            ReportConfigurationConstants.DatasetOrderProfit => new List<string>
            {
                "orderId", "customerId", "orderNo", "orderDate", "customerName", "currency", "salesAmount",
                "costAmount", "profit", "profitRate", "currentPriceEstimate",
            },
            ReportConfigurationConstants.DatasetSalesCommission => new List<string>
            {
                "salesmanId", "salesmanName", "currency", "orderCount", "salesAmount",
                "profit", "profitRate", "commissionRate", "commissionAmount", "sourceLabel",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };

    // ==================== 1. 目录暴露与币种 / 单位口径 ====================

    [Fact]
    public async Task 商品销量排名_目录暴露有限白名单与单位口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "rank-catalog", "product-sales-ranking");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetProductSalesRanking);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal(ReportConfigurationConstants.DatasetProductSalesRanking, dataset!.DatasetKey);
        Assert.Equal("product-sales-ranking", dataset.RequiredMenuCode);
        var keys = dataset.Fields.Select(f => f.Key).ToList();
        Assert.Contains("rank", keys);
        Assert.Contains("productId", keys);
        Assert.Contains("unit", keys);
        Assert.Contains("totalQuantity", keys);
        Assert.Contains("totalAmount", keys);
        Assert.Contains("amountLabel", keys);
        Assert.Contains("绝不跨单位合计", dataset.CurrencyUnitSemantics);
        Assert.Contains("币种未知", dataset.CurrencyUnitSemantics);
    }

    [Fact]
    public async Task 订单利润暂估_目录暴露有限白名单与币种口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-catalog", "order-profit");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal(ReportConfigurationConstants.DatasetOrderProfit, dataset!.DatasetKey);
        Assert.Equal("order-profit", dataset.RequiredMenuCode);
        var keys = dataset.Fields.Select(f => f.Key).ToList();
        Assert.Contains("orderNo", keys);
        Assert.Contains("orderDate", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("salesAmount", keys);
        Assert.Contains("costAmount", keys);
        Assert.Contains("profit", keys);
        Assert.Contains("profitRate", keys);
        Assert.Contains("currentPriceEstimate", keys);
        Assert.Contains("绝不跨币种合计", dataset.CurrencyUnitSemantics);
        Assert.Contains("null", dataset.CurrencyUnitSemantics);
    }

    [Fact]
    public async Task 业务员提成_目录暴露有限白名单与币种口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "commission-catalog", "sales-commission");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetSalesCommission);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesCommission, dataset!.DatasetKey);
        Assert.Equal("sales-commission", dataset.RequiredMenuCode);
        var keys = dataset.Fields.Select(f => f.Key).ToList();
        Assert.Contains("salesmanName", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("orderCount", keys);
        Assert.Contains("salesAmount", keys);
        Assert.Contains("commissionRate", keys);
        Assert.Contains("commissionAmount", keys);
        Assert.Contains("sourceLabel", keys);
        Assert.Contains("绝不跨币种合计", dataset.CurrencyUnitSemantics);
        Assert.Contains("null", dataset.CurrencyUnitSemantics);
    }

    // ==================== 2. 预览与既有报表服务逐行一致 ====================

    [Fact]
    public async Task 商品销量排名_预览与既有固定报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "rank-preview", "product-sales-ranking");
        var product = SeedProduct(db, "P001", "商品一", salePrice: 10m);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedStockOutDetail(db, out1.Id, product.Id, "商品一", "大", "PCS", 100m);
        SeedStockOutDetail(db, out1.Id, product.Id, "商品一", "小", "PCS", 50m);

        var reportService = NewReportService(db);
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetProductSalesRanking);
        var legacy = await reportService.GetProductSalesRankingAsync(DateTime.Today, DateTime.Today, 200, PrivilegedScope);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetProductSalesRanking,
                "rank", "productId", "productCode", "productName", "spec", "unit", "totalQuantity", "totalAmount"),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var item in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (int)r["rank"]! == item.Rank);
            Assert.Equal(item.ProductCode, (string)row["productCode"]!);
            Assert.Equal(item.ProductName, (string)row["productName"]!);
            Assert.Equal(item.Spec, (string)row["spec"]!);
            Assert.Equal(item.TotalQuantity, (decimal)row["totalQuantity"]!);
            Assert.Equal(item.TotalAmount, (decimal)row["totalAmount"]!);
            Assert.Equal(item.Rank, (int)row["rank"]!);
        }
    }

    [Fact]
    public async Task 订单利润暂估_预览与既有固定报表逐行一致_保留可空成本利润证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-preview", "order-profit");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-A", customer.Id, null, Currency.USD, 200m);
        SeedOrder(db, "SO-B", customer.Id, null, Currency.EUR, 300m);

        var reportService = NewReportService(db);
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);
        var legacy = await reportService.GetOrderProfitEstimateAsync(DateTime.Today, DateTime.Today, PrivilegedScope);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit,
                "orderId", "orderNo", "customerName", "currency", "salesAmount",
                "costAmount", "profit", "profitRate", "currentPriceEstimate"),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var item in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["orderNo"]! == item.OrderNo);
            Assert.Equal(item.Currency, (string)row["currency"]!);
            Assert.Equal(item.SalesAmount, (decimal)row["salesAmount"]!);
            Assert.Null(row["costAmount"]);
            Assert.Null(row["profit"]);
            Assert.Null(row["profitRate"]);
            Assert.Null(row["currentPriceEstimate"]);
        }
    }

    [Fact]
    public async Task 业务员提成_预览与既有固定报表逐行一致_保留可空金额证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "commission-preview", "sales-commission");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-BAD", customer.Id, emp.Id, (Currency)999, 50m);

        var reportService = NewReportService(db);
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetSalesCommission);
        var legacy = await reportService.GetSalesCommissionAsync(DateTime.Today, DateTime.Today, PrivilegedScope);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSalesCommission,
                "salesmanId", "salesmanName", "currency", "orderCount", "salesAmount",
                "profit", "profitRate", "commissionRate", "commissionAmount"),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var item in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["currency"]! == item.Currency && (long?)r["salesmanId"] == item.SalesmanId);
            Assert.Equal(item.OrderCount, (int)row["orderCount"]!);
            Assert.Equal(item.SalesAmount, (decimal?)row["salesAmount"]);
            Assert.Null(row["profit"]);
            Assert.Null(row["profitRate"]);
            Assert.Null(row["commissionAmount"]);
        }
    }

    // ==================== 2.1 通用 Excel / PDF 导出语义 ====================

    [Fact]
    public async Task 订单利润暂估_通用Excel与PDF导出_消费同一有界预览()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-export", "order-profit");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-A", customer.Id, null, Currency.USD, 200m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo", "currency", "salesAmount", "costAmount", "profit"),
            Params(), user.Id);

        var excel = new ReportConfigurationExcelExporter().Build(preview);
        Assert.True(excel.Length > 0);
        Assert.Equal("PK", Encoding.ASCII.GetString(excel, 0, 2));

        var pdf = ReportConfigurationPdfExporter.Export(preview);
        Assert.True(pdf.Length > 0);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf));
    }

    // ==================== 3. 筛选与日期 / 分页边界 ====================

    [Fact]
    public async Task 订单利润暂估_日期超出366天_先于源读取拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-daterange", "order-profit");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);

        var definition = Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo", "salesAmount");
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new()
            {
                FieldKey = "orderDate",
                Operator = ReportConfigurationConstants.OperatorBetween,
                Value = new DateTime(2026, 1, 1),
                Value2 = new DateTime(2027, 1, 2),
            },
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(definition, Params(), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 订单利润暂估_分页超限_先于源读取拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-page-bound", "order-profit");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo"),
            Params(page: 1, pageSize: 201), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 商品销量排名_商品筛选_与既有固定报表一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "rank-filter", "product-sales-ranking");
        var product1 = SeedProduct(db, "P001", "商品一", salePrice: 10m);
        var product2 = SeedProduct(db, "P002", "商品二", salePrice: 20m);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedStockOutDetail(db, out1.Id, product1.Id, "商品一", "大", "PCS", 100m);
        SeedStockOutDetail(db, out1.Id, product2.Id, "商品二", "大", "PCS", 999m);

        var reportService = NewReportService(db);
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetProductSalesRanking);
        var legacy = await reportService.GetProductSalesRankingAsync(
            DateTime.Today, DateTime.Today, 200, PrivilegedScope,
            new ProductSalesRankingFilterDto { ProductId = product1.Id });

        var definition = Definition(ReportConfigurationConstants.DatasetProductSalesRanking, "productId", "totalQuantity");
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new()
            {
                FieldKey = "productId",
                Operator = ReportConfigurationConstants.OperatorEq,
                Value = product1.Id,
            },
        };

        var preview = await provider.PreviewAsync(definition, Params(), user.Id);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(product1.Id, (long)row["productId"]!);
        Assert.Equal(legacy.Single().TotalQuantity, (decimal)row["totalQuantity"]!);
    }

    [Fact]
    public async Task 业务员提成_业务员筛选_与既有报表一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "commission-filter", "sales-commission");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");
        SeedOrder(db, "SO-A", customer.Id, emp1.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-B", customer.Id, emp2.Id, Currency.USD, 200m);

        var reportService = NewReportService(db);
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetSalesCommission);

        var definition = Definition(ReportConfigurationConstants.DatasetSalesCommission, "salesmanId", "salesAmount");
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new()
            {
                FieldKey = "salesmanId",
                Operator = ReportConfigurationConstants.OperatorEq,
                Value = emp1.Id,
            },
        };

        var preview = await provider.PreviewAsync(definition, Params(), user.Id);

        var legacy = await reportService.GetSalesCommissionAsync(DateTime.Today, DateTime.Today, PrivilegedScope);
        var expected = Assert.Single(legacy, x => x.SalesmanId == emp1.Id);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(expected.SalesmanId, (long?)row["salesmanId"]);
        Assert.Equal(expected.SalesAmount, (decimal?)row["salesAmount"]);
    }

    // ==================== 4. 分页与空页 ====================

    [Fact]
    public async Task 订单利润暂估_分页_只返回当前页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-paging", "order-profit");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, null, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, null, Currency.USD, 100m);
        SeedOrder(db, "SO-3", customer.Id, null, Currency.USD, 100m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);

        var page1 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo"),
            Params(page: 1, pageSize: 2), user.Id);
        var page2 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo"),
            Params(page: 2, pageSize: 2), user.Id);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Rows.Count);
        Assert.Single(page2.Rows);
        Assert.Equal(2, page2.Page);
    }

    [Fact]
    public async Task 订单利润暂估_无数据_空页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "profit-empty", "order-profit");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo"),
            Params(), user.Id);

        Assert.Equal(0, preview.Total);
        Assert.Empty(preview.Rows);
    }

    // ==================== 5. 菜单撤销立即收敛（fail closed） ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetProductSalesRanking, "product-sales-ranking")]
    [InlineData(ReportConfigurationConstants.DatasetOrderProfit, "order-profit")]
    [InlineData(ReportConfigurationConstants.DatasetSalesCommission, "sales-commission")]
    public async Task 菜单撤销后_数据集不暴露且预览拒绝(string datasetKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "revoke-" + datasetKey, menuCode);
        var provider = BuildProvider(db, datasetKey);

        Assert.NotNull(await provider.GetDatasetAsync(user.Id));

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(datasetKey, DefaultFieldsFor(datasetKey).ToArray()),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetOrderProfit);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetOrderProfit, "orderNo"),
            Params(), null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 6. 迁移预设登记（覆盖固定与动态登记册条目） ====================

    [Theory]
    [InlineData("report:product-sales-ranking")]
    [InlineData("dynamic:product-sales-ranking")]
    [InlineData("report:order-profit")]
    [InlineData("dynamic:order-profit")]
    [InlineData("report:sales-commission")]
    [InlineData("dynamic:sales-commission")]
    public async Task 迁移预设_六个登记册条目均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();

        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }
}
