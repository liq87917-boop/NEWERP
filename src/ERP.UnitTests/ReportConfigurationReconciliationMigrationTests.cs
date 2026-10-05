using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-303 Stage 2 代理服务费月度汇总 / 客户订单与收款核对迁移为受控数据集适配器的单元测试，并覆盖
/// ERP-301 出货财务进度运行时修正：证明受控数据集预览与既有权威查询逐行一致（含币种 / 单位 / 可空证据 / 未关联收款不并入订单），
/// 菜单撤销立即收敛（fail closed）、未知字段 / 筛选 / 分页超限拒绝，以及通用 Excel / PDF 导出可执行。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationReconciliationMigrationTests
{
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };
    private static readonly DateTime AsOf = new(2026, 9, 25);

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

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-2),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount,
        string currency = "USD", int status = AgencyServiceFeeStatementRules.StatusRecorded,
        DateTime? statementDate = null, string customerCode = "C001", string customerName = "客户A")
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = customerCode,
            CustomerName = customerName,
            Currency = currency,
            StatementDate = statementDate ?? AsOf,
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = false,
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }


    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
        => datasetKey switch
        {
            ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly =>
                new AgencyServiceFeeMonthlyReportConfigurationDatasetProvider(db),
            ReportConfigurationConstants.DatasetReceiptReconciliation =>
                new ReceiptReconciliationReportConfigurationDatasetProvider(db),
            ReportConfigurationConstants.DatasetShipmentFinance =>
                new ERP.Api.Controllers.ShipmentFinanceReportConfigurationDatasetProvider(db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields) => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = datasetKey,
        Fields = fields.ToList(),
    };

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 200)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    // ==================== 1. 代理服务费月度汇总：预览与既有查询一致 ====================

    [Fact]
    public async Task 代理服务费月度汇总_预览与既有查询一致_币种隔离()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "asf-monthly", "customer");
        var customer = SeedCustomer(db, "C001", "客户A");
        SeedStatement(db, "ASF-USD-1", customer.Id, 300m, "USD", AgencyServiceFeeStatementRules.StatusRecorded, new DateTime(2026, 9, 1));
        SeedStatement(db, "ASF-USD-2", customer.Id, 50m, "USD", AgencyServiceFeeStatementRules.StatusDraft, new DateTime(2026, 9, 1));
        SeedStatement(db, "ASF-CNY-1", customer.Id, 700m, "CNY", AgencyServiceFeeStatementRules.StatusRecorded, new DateTime(2026, 9, 2));

        var legacy = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(
            db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 200 }, PrivilegedScope);
        var provider = new AgencyServiceFeeMonthlyReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly,
                "statementMonthText", "currency", "registeredTotalAmount", "draftTotalAmount"),
            Params(), user.Id);

        Assert.Equal(legacy.Total, preview.Total);
        Assert.Equal(legacy.Rows.Count, preview.Rows.Count);
        foreach (var legacyRow in legacy.Rows)
        {
            var row = Assert.Single(preview.Rows, r =>
                (string)r["statementMonthText"]! == legacyRow.StatementMonthText &&
                (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.RegisteredTotalAmount, (decimal)row["registeredTotalAmount"]!);
            Assert.Equal(legacyRow.DraftTotalAmount, (decimal)row["draftTotalAmount"]!);
        }

        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "USD");
        Assert.Contains(preview.Rows, r => (string)r["currency"]! == "CNY");
    }


    // ==================== 2. 客户订单与收款核对：预览与既有查询一致，未关联收款不并入订单 ====================

    [Fact]
    public async Task 客户订单与收款核对_预览与既有查询一致_未关联收款不并入订单()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "receipt-recon", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-R-1", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, 958101L, 10m);
        SeedDepositApply(db, "DJ-R-1", order.Id, 300m, Currency.USD, DocumentStatus.Approved);
        SeedPaymentApply(db, "HK-R-1", order.Id, 200m, Currency.USD, DocumentStatus.Approved);
        // 未关联收款证据：只有客户级引用，绝不归属到订单
        SeedReceipt(db, "SK-R-1", customer.Id, 40m, Currency.USD, DocumentStatus.Approved);

        var legacy = await SalesOrderReceiptReconciliation.ForQueryAsync(
            db, new SalesOrderReceiptReconciliationQuery { PageSize = 200 }, PrivilegedScope);
        var provider = new ReceiptReconciliationReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetReceiptReconciliation,
                "orderNo", "currency", "orderAmount", "receiptCoverageStatus", "linkedReceiptAmount", "uncoveredAmount"),
            Params(), user.Id);

        var legacyOrder = Assert.Single(legacy.Groups.SelectMany(g => g.Orders));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(legacyOrder.OrderNo, (string)row["orderNo"]!);
        Assert.Equal(legacyOrder.OrderAmount, (decimal)row["orderAmount"]!);
        Assert.Equal(legacyOrder.ReceiptCoverageStatus, (string)row["receiptCoverageStatus"]!);
        Assert.Equal(legacyOrder.LinkedReceiptAmount, (decimal?)row["linkedReceiptAmount"]);
        Assert.Equal(legacyOrder.UncoveredAmount, (decimal?)row["uncoveredAmount"]);

        // 未关联收款证据在 legacy 路由中独立承载，绝不混入受控数据集的订单行
        Assert.Single(legacy.UnlinkedReceipts);
        Assert.DoesNotContain(preview.Rows, r => r.ContainsKey("receiptNo"));
    }


    // ==================== 3. 出货财务进度：Api 运行时适配器复用权威派生 ====================

    [Fact]
    public async Task 出货财务进度_Api适配器复用权威派生_未知金额保留null()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "ship-fin", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-SF-1", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, 958101L, 10m);
        SeedStockOut(db, "CK-SF-1", order.Id, DocumentStatus.Approved, (958101L, 6m));
        SeedDepositApply(db, "DJ-SF-1", order.Id, 300m, Currency.USD, DocumentStatus.Approved);
        // 无任何权威收款引用的订单：已关联金额 / 未覆盖金额照实保留 null，绝不回落为 0
        var unlinkedOrder = SeedOrder(db, "SO-SF-2", customer.Id, Currency.USD, 400m);
        SeedDetail(db, unlinkedOrder.Id, 958101L, 4m);

        var legacy = await SalesOrderShipmentFinanceReport.ForQueryAsync(
            db, new SalesOrderShipmentFinanceQuery { PageSize = 200 }, PrivilegedScope);
        var provider = new ERP.Api.Controllers.ShipmentFinanceReportConfigurationDatasetProvider(db); // Api 层运行时适配器
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetShipmentFinance,
                "orderNo", "currency", "orderAmount", "shippedQuantity", "outstandingQuantity",
                "financeLinkStatus", "linkedAmount", "uncoveredAmount"),
            Params(), user.Id);

        var legacyByNo = legacy.Groups.SelectMany(g => g.Orders).ToDictionary(o => o.OrderNo, StringComparer.Ordinal);
        Assert.Equal(2, preview.Rows.Count);
        foreach (var row in preview.Rows)
        {
            var orderNo = (string)row["orderNo"]!;
            var legacyOrder = legacyByNo[orderNo];
            Assert.Equal(legacyOrder.OrderAmount, (decimal)row["orderAmount"]!);
            Assert.Equal(legacyOrder.ShippedQuantity, (decimal?)row["shippedQuantity"]);
            Assert.Equal(legacyOrder.OutstandingQuantity, (decimal?)row["outstandingQuantity"]);
            Assert.Equal(legacyOrder.FinanceLinkStatus, (string)row["financeLinkStatus"]!);
            Assert.Equal(legacyOrder.LinkedAmount, (decimal?)row["linkedAmount"]);
            Assert.Equal(legacyOrder.UncoveredAmount, (decimal?)row["uncoveredAmount"]);
        }

        var unlinkedRow = Assert.Single(preview.Rows, r => (string)r["orderNo"]! == "SO-SF-2");
        Assert.Null(unlinkedRow["linkedAmount"]);
        Assert.Null(unlinkedRow["uncoveredAmount"]);
    }

    // ==================== 4. 迁移预设：三族均已注册 ====================

    [Theory]
    [InlineData("dynamic:agency-service-fee-monthly")]
    [InlineData("dynamic:receipt-reconciliation")]
    [InlineData("dynamic:shipment-finance")]
    public async Task 迁移预设_三族均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();
        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }


    // ==================== 5. 菜单撤销立即收敛（fail closed） ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetAgencyServiceFeeMonthly, "customer")]
    [InlineData(ReportConfigurationConstants.DatasetReceiptReconciliation, "sales-order")]
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
            Definition(datasetKey, "orderNo"), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 6. 未知字段 / 筛选 / 分页超限拒绝 ====================

    [Fact]
    public async Task 未知字段与筛选_分页超限_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unknown", "sales-order");
        var provider = new ReceiptReconciliationReportConfigurationDatasetProvider(db);

        var exField = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetReceiptReconciliation, "doesNotExist"),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exField.Code);

        var exFilter = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            new ReportConfigurationDefinition
            {
                SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
                DatasetKey = ReportConfigurationConstants.DatasetReceiptReconciliation,
                Fields = new List<string> { "orderNo" },
                Filters = new List<ReportConfigurationFilter>
                {
                    new() { FieldKey = "doesNotExist", Operator = "eq", Value = "x" },
                },
            },
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exFilter.Code);

        var exPage = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetReceiptReconciliation, "orderNo"),
            Params(pageSize: 201), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exPage.Code);
    }

    // ==================== 7. 通用 Excel / PDF 导出可执行 ====================

    [Fact]
    public async Task 通用Excel与PDF导出_可执行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "export", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户A");
        var order = SeedOrder(db, "SO-EXP-1", customer.Id, Currency.USD, 500m);
        SeedDetail(db, order.Id, 958101L, 5m);

        var provider = new ERP.Api.Controllers.ShipmentFinanceReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetShipmentFinance, "orderNo", "currency", "orderAmount"),
            Params(), user.Id);

        var excel = new ReportConfigurationExcelExporter().Build(preview);
        Assert.NotEmpty(excel);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationPdfExporter.Export(preview, fontPath);
            Assert.NotEmpty(pdf);
        }
    }
}

