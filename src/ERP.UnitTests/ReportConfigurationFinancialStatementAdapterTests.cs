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
/// ERP-298 固定报表与财务报表数据集适配器单元测试：覆盖资产负债表 / 利润表 / 现金流量表 / 应收账龄
/// 四个数据集的目录暴露、有限字段白名单、真实币种 / 单位口径、预览与既有固定报表行名称 / 金额一致、
/// 菜单撤销立即收敛（fail closed），以及应收账龄不跨币种合并。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationFinancialStatementAdapterTests
{
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

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role");
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static IReportService NewReportService(ErpDbContext db) => new ReportService(db);

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
    {
        var reportService = NewReportService(db);
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetBalanceSheet => new BalanceSheetReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetIncomeStatement => new IncomeStatementReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetCashFlow => new CashFlowReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetArAging => new ArAgingReportConfigurationDatasetProvider(reportService, db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };
    }

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new(new IReportConfigurationDatasetProvider[]
        {
            new BalanceSheetReportConfigurationDatasetProvider(NewReportService(db), db),
            new IncomeStatementReportConfigurationDatasetProvider(NewReportService(db), db),
            new CashFlowReportConfigurationDatasetProvider(NewReportService(db), db),
            new ArAgingReportConfigurationDatasetProvider(NewReportService(db), db),
        });

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    private static List<string> DefaultFields(string datasetKey)
        => datasetKey == ReportConfigurationConstants.DatasetArAging
            ? new List<string> { "customerName", "orderNo", "currency", "balance" }
            : new List<string> { "lineName", "amount" };

    // ==================== 1. 目录暴露与币种 / 单位口径 ====================

    [Fact]
    public async Task Catalog_四个数据集全部暴露且字段白名单与币种口径真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "fin", "balance-sheet", "income-statement", "cash-flow", "ar-aging");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        Assert.Equal(4, result.Datasets.Count);

        var balance = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetBalanceSheet);
        Assert.Equal("金额按单据金额直接汇总（资产/负债/权益）；不跨币种换算", balance.CurrencyUnitSemantics);
        Assert.Equal("balance-sheet", balance.RequiredMenuCode);
        Assert.Equal(2, balance.Fields.Count);
        Assert.Contains(balance.Fields, f => f.Key == "lineName" && f.Type == ReportConfigurationConstants.TypeText);
        var amount = Assert.Single(balance.Fields, f => f.Key == "amount");
        Assert.Equal(ReportConfigurationConstants.TypeNumber, amount.Type);
        Assert.NotEqual("原币金额", amount.CurrencyUnit);
        Assert.False(amount.Aggregatable);
        Assert.DoesNotContain(ReportConfigurationConstants.CapabilityCustomFormula, balance.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.CapabilityPreview, balance.SupportedCapabilities);

        var income = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetIncomeStatement);
        Assert.Equal("金额按单据金额直接汇总（收入-成本-费用）；不跨币种换算", income.CurrencyUnitSemantics);
        Assert.Equal("income-statement", income.RequiredMenuCode);

        var cashFlow = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetCashFlow);
        Assert.Equal("金额按单据金额直接汇总（流入-流出）；不跨币种换算", cashFlow.CurrencyUnitSemantics);
        Assert.Equal("cash-flow", cashFlow.RequiredMenuCode);

        var ar = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetArAging);
        Assert.Equal("金额按原币呈现；账龄按自然日；不跨币种换算或合并", ar.CurrencyUnitSemantics);
        Assert.Equal("ar-aging", ar.RequiredMenuCode);
        Assert.Equal(11, ar.Fields.Count);
        var balanceField = Assert.Single(ar.Fields, f => f.Key == "balance");
        Assert.Equal("原币金额", balanceField.CurrencyUnit);
        var currencyField = Assert.Single(ar.Fields, f => f.Key == "currency");
        Assert.Equal("币种代码", currencyField.CurrencyUnit);
    }

    [Fact]
    public async Task Catalog_未授权菜单_对应数据集不暴露()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bs-only", "balance-sheet");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        var dataset = Assert.Single(result.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetBalanceSheet, dataset.DatasetKey);
    }

    // ==================== 2. 数据种子 ====================

    private static void SeedBalanceSheetData(ErpDbContext db)
    {
        var product = new BaseProduct { ProductCode = "P-BS", ProductName = "P-BS", CostPrice = 100m };
        db.BaseProducts.Add(product);
        db.SaveChanges();

        db.Stocks.Add(new Stock { ProductId = product.Id, WarehouseId = 1, Quantity = 5m });
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-BS",
            OrderDate = DateTime.Today,
            CustomerId = 1,
            TotalAmount = 200m,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
        });
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = "PO-BS",
            OrderDate = DateTime.Today,
            SupplierId = 1,
            TotalAmount = 150m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
        });
        db.SaveChanges();
    }

    private static void SeedIncomeCashFlowData(ErpDbContext db)
    {
        db.FinanceReceipts.Add(new FinanceReceipt
        {
            ReceiptNo = "FR-IC",
            ReceiptDate = DateTime.Today,
            CustomerId = 1,
            Amount = 1000m,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
        });
        db.FinancePayments.Add(new FinancePayment
        {
            PaymentNo = "FP-IC",
            PaymentDate = DateTime.Today,
            SupplierId = 1,
            Amount = 300m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
        });
        db.SaveChanges();
    }

    private static void SeedArAgingData(ErpDbContext db)
    {
        var customer = new BaseCustomer { CustomerCode = "C-AR", CustomerName = "客户A", CreditDays = 0 };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();

        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-AR-USD",
            OrderDate = DateTime.Today.AddDays(-10),
            CustomerId = customer.Id,
            TotalAmount = 100m,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
        });
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-AR-CNY",
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customer.Id,
            TotalAmount = 200m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
        });
        db.SaveChanges();
    }

    // ==================== 3. 预览与既有固定报表行名称 / 金额一致 ====================

    [Fact]
    public async Task 资产负债表_预览行名称与金额与既有报表一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bs-preview", "balance-sheet");
        SeedBalanceSheetData(db);

        var reportService = NewReportService(db);
        var provider = new BalanceSheetReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetBalanceSheetAsync(DateTime.Today);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetBalanceSheet, "lineName", "amount"),
            Params(), user.Id);

        Assert.Equal(legacy.Lines.Count, preview.Total);
        Assert.Equal(legacy.Lines.Count, preview.Rows.Count);

        var expectedNames = new[] { "库存价值", "应收账款", "资产合计", "应付账款", "负债合计", "所有者权益" };
        foreach (var name in expectedNames)
            Assert.Contains(preview.Rows, r => (string)r["lineName"]! == name);

        foreach (var line in legacy.Lines)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["lineName"]! == line.Name);
            Assert.Equal(line.Amount, (decimal)row["amount"]!);
        }
    }

    [Fact]
    public async Task 利润表_预览行名称与金额与既有报表一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "is-preview", "income-statement");
        SeedIncomeCashFlowData(db);

        var reportService = NewReportService(db);
        var provider = new IncomeStatementReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetIncomeStatementAsync(DateTime.MinValue, DateTime.Today);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetIncomeStatement, "lineName", "amount"),
            Params(), user.Id);

        Assert.Equal(legacy.Lines.Count, preview.Rows.Count);
        Assert.Equal(new[] { "营业收入", "营业支出", "净利润" },
            preview.Rows.Select(r => (string)r["lineName"]!).ToArray());
        foreach (var line in legacy.Lines)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["lineName"]! == line.Name);
            Assert.Equal(line.Amount, (decimal)row["amount"]!);
        }
    }

    [Fact]
    public async Task 现金流量表_预览行名称与金额与既有报表一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cf-preview", "cash-flow");
        SeedIncomeCashFlowData(db);

        var reportService = NewReportService(db);
        var provider = new CashFlowReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetCashFlowStatementAsync(DateTime.MinValue, DateTime.Today);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetCashFlow, "lineName", "amount"),
            Params(), user.Id);

        Assert.Equal(legacy.Lines.Count, preview.Rows.Count);
        Assert.Equal(new[] { "经营活动现金流入", "经营活动现金流出", "现金净流量" },
            preview.Rows.Select(r => (string)r["lineName"]!).ToArray());
        foreach (var line in legacy.Lines)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["lineName"]! == line.Name);
            Assert.Equal(line.Amount, (decimal)row["amount"]!);
        }
    }


    [Fact]
    public async Task 应收账龄_预览逐单原币呈现且不跨币种合并()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "ar-preview", "ar-aging");
        SeedArAgingData(db);

        var reportService = NewReportService(db);
        var provider = new ArAgingReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetArAgingAsync(DateTime.Today);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetArAging,
                "customerName", "orderNo", "currency", "orderAmount", "receivedAmount", "balance", "agingDays", "bucket", "status"),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);

        var currencies = preview.Rows.Select(r => (string)r["currency"]!).Distinct().ToList();
        Assert.Equal(2, currencies.Count);
        Assert.Contains("USD", currencies);
        Assert.Contains("CNY", currencies);

        foreach (var item in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["orderNo"]! == item.OrderNo);
            Assert.Equal(item.Currency, (string)row["currency"]!);
            Assert.Equal(item.Balance, (decimal)row["balance"]!);
            Assert.Equal(item.CustomerName, (string)row["customerName"]!);
        }
    }

    // ==================== 4. 菜单撤销立即收敛（fail closed） ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetBalanceSheet, "balance-sheet")]
    [InlineData(ReportConfigurationConstants.DatasetIncomeStatement, "income-statement")]
    [InlineData(ReportConfigurationConstants.DatasetCashFlow, "cash-flow")]
    [InlineData(ReportConfigurationConstants.DatasetArAging, "ar-aging")]
    public async Task 菜单撤销后_数据集不暴露且预览拒绝(string datasetKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoke-" + datasetKey, menuCode);
        var provider = BuildProvider(db, datasetKey);

        Assert.NotNull(await provider.GetDatasetAsync(user.Id));

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(datasetKey, DefaultFields(datasetKey).ToArray()),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetBalanceSheet);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetBalanceSheet, "lineName", "amount"),
            Params(), null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }


}

