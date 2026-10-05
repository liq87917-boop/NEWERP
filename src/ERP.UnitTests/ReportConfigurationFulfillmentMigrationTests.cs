using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-301 柜量统计 / 客户出货量 / 出货财务进度迁移为受控数据集适配器的单元测试：
/// 覆盖目录暴露与币种 / 单位口径、预览与既有报表服务逐行一致、分页 / 空页、撤销菜单立即收敛（fail closed）、
/// 未知字段 / 筛选 / 分页超限拒绝，以及金额 / 数量可空证据语义。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationFulfillmentMigrationTests
{
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };
    private static readonly DateTime AsOf = new(2026, 9, 24);

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

    private static SysRoleMenu SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        var link = new SysRoleMenu { RoleId = roleId, MenuId = menuId };
        db.SysRoleMenus.Add(link);
        db.SaveChanges();
        return link;
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

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf,
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity,
        string unit = "PCS")
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
        });
        db.SaveChanges();
    }

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            Status = status,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Quantity = quantity,
            });
        }
        db.SaveChanges();
        return stockOut;
    }

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static void SeedPaymentApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinancePaymentApplies.Add(new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static ContainerLoadingList SeedContainerList(ErpDbContext db, string no, DateTime loadingDate,
        string containerNo, long customerId, decimal cartons, decimal weight, decimal volume)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no,
            LoadingDate = loadingDate,
            ContainerNo = containerNo,
            CustomerId = customerId,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume,
            Status = DocumentStatus.Approved,
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
    {
        var reportService = new ReportService(db);
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetContainerStats => new ContainerStatsReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetCustomerShipment => new CustomerShipmentReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetShipmentFinance => new ShipmentFinanceReportConfigurationDatasetProvider(db),
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

    // ==================== 1. 目录暴露有限白名单与币种/单位口径 ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetContainerStats, "container-stats", "柜量与装柜利用率统计")]
    [InlineData(ReportConfigurationConstants.DatasetCustomerShipment, "customer-shipment", "客户出货量统计表")]
    [InlineData(ReportConfigurationConstants.DatasetShipmentFinance, "sales-order", "销售订单")]
    public async Task 三个数据集目录均暴露有限白名单与菜单口径(string datasetKey, string menuCode, string menuText)
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "catalog-" + datasetKey, menuCode);
        var provider = BuildProvider(db, datasetKey);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal(datasetKey, dataset!.DatasetKey);
        Assert.Equal(menuCode, dataset.RequiredMenuCode);
        Assert.Equal(menuText, dataset.RequiredMenuText);
        Assert.NotEmpty(dataset.Fields);
        Assert.All(dataset.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    // ==================== 2. 柜量统计：预览与既有报表逐行一致 ====================

    [Fact]
    public async Task 柜量统计_预览与既有报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "container-preview", "container-stats");
        var customer = SeedCustomer(db, "C-1", "甲客户");
        SeedContainerList(db, "ZL-1", DateTime.Today, "CSLU100", customer.Id, 10m, 500m, 30m);
        SeedContainerList(db, "ZL-2", DateTime.Today, "CSLU100", customer.Id, 5m, 250m, 15m);
        SeedContainerList(db, "ZL-3", DateTime.Today, "CSLU200", customer.Id, 8m, 400m, 20m);
        SeedContainerList(db, "ZL-4", DateTime.Today, "", customer.Id, 2m, 100m, 5m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetContainerStats);
        var legacy = await new ReportService(db).GetContainerStatsAsync(DateTime.Today, DateTime.Today, PrivilegedScope);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetContainerStats,
                "loadingDate", "containerNo", "loadingListCount", "authorizedCustomerCount",
                "totalCartons", "totalWeight", "totalVolume", "utilizationType", "reasons"),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var item in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["containerNo"]! == item.ContainerNo
                && (DateTime)r["loadingDate"]! == item.LoadingDate);
            Assert.Equal(item.LoadingListCount, (int)row["loadingListCount"]!);
            Assert.Equal(item.TotalCartons, (decimal)row["totalCartons"]!);
            Assert.Equal(item.TotalWeight, (decimal)row["totalWeight"]!);
            Assert.Equal(item.TotalVolume, (decimal)row["totalVolume"]!);
            Assert.Equal("未知", (string)row["utilizationType"]!);
        }
    }

    // ==================== 3. 客户出货量：预览与既有报表逐行一致（币种/单位分区） ====================

    [Fact]
    public async Task 客户出货量_预览与既有报表逐行一致_币种与单位分区()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "shipment-preview", "customer-shipment");
        var customer = SeedCustomer(db, "C-2", "乙客户");
        var usdOrder = SeedOrder(db, "SO-CS-1", customer.Id, Currency.USD, 100m, DateTime.Today);
        SeedDetail(db, usdOrder.Id, 1, 10m, "PCS");
        var cnyOrder = SeedOrder(db, "SO-CS-2", customer.Id, Currency.CNY, 300m, DateTime.Today);
        SeedDetail(db, cnyOrder.Id, 2, 5m, "KG");

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetCustomerShipment);
        var legacy = await new ReportService(db).GetCustomerShipmentStatsAsync(DateTime.Today, DateTime.Today, PrivilegedScope);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetCustomerShipment,
                "customerId", "customerName", "currency", "currencyLabel", "orderCount",
                "totalAmount", "totalQuantity", "unitGroups"),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var item in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["currency"]! == item.Currency);
            Assert.Equal(item.CustomerId, (long)row["customerId"]!);
            Assert.Equal(item.OrderCount, (int)row["orderCount"]!);
            Assert.Equal(item.TotalAmount, (decimal?)row["totalAmount"]);
            Assert.Equal(item.TotalQuantity, (decimal?)row["totalQuantity"]);
        }

        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "USD");
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "CNY");
    }

    // ==================== 4. 出货财务进度：预览与既有报表逐行一致（可空金额/数量） ====================

    [Fact]
    public async Task 出货财务进度_预览与既有报表逐行一致_保留可空金额与数量()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "finance-preview", "sales-order");
        var customer = SeedCustomer(db, "C-3", "丙客户");

        var linked = SeedOrder(db, "SO-SF-1", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, linked.Id, 1, 10m);
        SeedStockOut(db, "CK-SF-1", linked.Id, DocumentStatus.Approved, (1, 4m));
        SeedDepositApply(db, "DK-SF-1", linked.Id, 300m, Currency.USD, DocumentStatus.Approved);
        SeedPaymentApply(db, "HK-SF-1", linked.Id, 200m, Currency.USD, DocumentStatus.Approved);

        var unlinked = SeedOrder(db, "SO-SF-2", customer.Id, Currency.USD, 500m);
        SeedDetail(db, unlinked.Id, 2, 5m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetShipmentFinance);
        var legacy = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, new SalesOrderShipmentFinanceQuery { Page = 1, PageSize = 50 });
        var legacyRows = legacy.Groups.SelectMany(g => g.Orders).ToDictionary(o => o.OrderNo, StringComparer.Ordinal);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetShipmentFinance,
                "orderId", "orderNo", "currency", "orderAmount", "orderedQuantity", "shippedQuantity",
                "outstandingQuantity", "shipmentStatus", "financeLinkStatus", "linkedAmount",
                "uncoveredAmount", "submittedAmount"),
            Params(), user.Id);

        Assert.Equal(legacy.Total, preview.Total);
        Assert.Equal(legacyRows.Count, preview.Rows.Count);

        foreach (var row in preview.Rows)
        {
            var orderNo = (string)row["orderNo"]!;
            var legacyRow = legacyRows[orderNo];
            Assert.Equal(legacyRow.Currency, (string)row["currency"]!);
            Assert.Equal(legacyRow.OrderAmount, (decimal)row["orderAmount"]!);
            Assert.Equal(legacyRow.OrderedQuantity, (decimal?)row["orderedQuantity"]);
            Assert.Equal(legacyRow.ShippedQuantity, (decimal?)row["shippedQuantity"]);
            Assert.Equal(legacyRow.OutstandingQuantity, (decimal?)row["outstandingQuantity"]);
            Assert.Equal(legacyRow.ShipmentStatus, (string)row["shipmentStatus"]!);
            Assert.Equal(legacyRow.FinanceLinkStatus, (string)row["financeLinkStatus"]!);
            Assert.Equal(legacyRow.LinkedAmount, (decimal?)row["linkedAmount"]);
            Assert.Equal(legacyRow.UncoveredAmount, (decimal?)row["uncoveredAmount"]);
            Assert.Equal(legacyRow.SubmittedAmount, (decimal?)row["submittedAmount"]);
        }

        var linkedRow = Assert.Single(preview.Rows, r => (string)r["orderNo"]! == "SO-SF-1");
        Assert.Equal("linked", (string)linkedRow["financeLinkStatus"]!);
        Assert.Equal(500m, (decimal)linkedRow["linkedAmount"]!);
        var unlinkedRow = Assert.Single(preview.Rows, r => (string)r["orderNo"]! == "SO-SF-2");
        Assert.Equal("unlinked", (string)unlinkedRow["financeLinkStatus"]!);
        Assert.Null(unlinkedRow["linkedAmount"]);
    }

    // ==================== 5. 分页有界与空页 ====================

    [Fact]
    public async Task 出货财务进度_分页有界且空页显式()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "finance-paging", "sales-order");
        var customer = SeedCustomer(db, "C-4", "丁客户");
        SeedOrder(db, "SO-PG-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-PG-2", customer.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-PG-3", customer.Id, Currency.USD, 300m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetShipmentFinance);

        var page1 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetShipmentFinance, "orderNo"),
            Params(page: 1, pageSize: 2), user.Id);
        var page2 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetShipmentFinance, "orderNo"),
            Params(page: 2, pageSize: 2), user.Id);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Rows.Count);
        Assert.Single(page2.Rows);
        Assert.Equal(2, page2.Page);
    }

    [Fact]
    public async Task 出货财务进度_无数据_空页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "finance-empty", "sales-order");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetShipmentFinance);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetShipmentFinance, "orderNo"),
            Params(), user.Id);

        Assert.Equal(0, preview.Total);
        Assert.Empty(preview.Rows);
    }

    // ==================== 6. 菜单撤销立即收敛（fail closed） ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetContainerStats, "container-stats")]
    [InlineData(ReportConfigurationConstants.DatasetCustomerShipment, "customer-shipment")]
    [InlineData(ReportConfigurationConstants.DatasetShipmentFinance, "sales-order")]
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
            Definition(datasetKey, "orderNo"),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 7. 未知字段 / 筛选 / 分页超限拒绝 ====================

    [Fact]
    public async Task 未知字段与未知筛选_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unknown", "customer-shipment");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetCustomerShipment);

        var exField = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetCustomerShipment, "doesNotExist"),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exField.Code);

        var exFilter = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            new ReportConfigurationDefinition
            {
                SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
                DatasetKey = ReportConfigurationConstants.DatasetCustomerShipment,
                Fields = new List<string> { "customerName" },
                Filters = new List<ReportConfigurationFilter>
                {
                    new() { FieldKey = "doesNotExist", Operator = "eq", Value = "x" },
                },
            },
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exFilter.Code);
    }

    [Fact]
    public async Task 分页超限_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "page-over", "container-stats");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetContainerStats);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetContainerStats, "containerNo"),
            Params(page: 1, pageSize: 201), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 8. 迁移预设登记（覆盖固定与动态登记册条目） ====================

    [Theory]
    [InlineData("report:container-stats")]
    [InlineData("dynamic:container-stats")]
    [InlineData("report:customer-shipment")]
    [InlineData("dynamic:customer-shipment")]
    [InlineData("dynamic:shipment-finance")]
    public async Task 迁移预设_五个登记册条目均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();

        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }
}
