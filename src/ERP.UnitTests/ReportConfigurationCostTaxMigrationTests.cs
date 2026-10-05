using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-305 采购成本 / 退税汇总迁移的单元测试：覆盖两个受控数据集的目录暴露（有限字段 / 币种口径 / 菜单码）、
/// 单一币种安全分区预览与既有报表逐行一致、混合币种在聚合前拒绝（哨兵报表服务证明旧服务未被调用）、
/// 菜单撤销 / 受限制数据范围 fail closed、空行 / 分页 / 日期边界，以及迁移登记册 parity 边界。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationCostTaxMigrationTests
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
        var role = SeedRole(db, name + "-role", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static SysUser SeedRestrictedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-restricted", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static IReportService NewReportService(ErpDbContext db) => new ReportService(db);

    /// <summary>哨兵报表服务：一旦调用全局旧采购成本 / 退税汇总查询即置位并抛异常，用于证明「拒绝发生在旧服务调用之前」。</summary>
    private sealed class NoQueryReportService : IReportService
    {
        public bool PurchaseCostExecuted { get; private set; }
        public bool TaxRefundExecuted { get; private set; }

        private static T NotCalled<T>() => throw new NotSupportedException("本测试不应调用非采购成本 / 退税汇总的报表查询");

        public Task<List<ReportDtos.PurchaseCostItem>> GetPurchaseCostAsync(DateTime start, DateTime end)
        {
            PurchaseCostExecuted = true;
            return Task.FromException<List<ReportDtos.PurchaseCostItem>>(
                new InvalidOperationException("全局采购成本查询被执行（安全边界失效）"));
        }

        public Task<List<ReportDtos.TaxRefundSummaryItem>> GetTaxRefundSummaryAsync()
        {
            TaxRefundExecuted = true;
            return Task.FromException<List<ReportDtos.TaxRefundSummaryItem>>(
                new InvalidOperationException("全局退税汇总查询被执行（安全边界失效）"));
        }

        public Task<List<ReportDtos.ProductSalesRankItem>> GetProductSalesRankingAsync(
            DateTime start, DateTime end, int top, SalespersonDataScope scope, ProductSalesRankingFilterDto? filter = null)
            => NotCalled<Task<List<ReportDtos.ProductSalesRankItem>>>();

        public Task<List<ReportDtos.OrderProfitItem>> GetOrderProfitEstimateAsync(
            DateTime start, DateTime end, SalespersonDataScope scope, OrderProfitEstimateFilterDto? filter = null)
            => NotCalled<Task<List<ReportDtos.OrderProfitItem>>>();

        public Task<List<ReportDtos.CustomerShipmentItem>> GetCustomerShipmentStatsAsync(
            DateTime start, DateTime end, SalespersonDataScope scope, CustomerShipmentFilterDto? filter = null)
            => NotCalled<Task<List<ReportDtos.CustomerShipmentItem>>>();

        public Task<List<ReportDtos.SalesmanOutputItem>> GetSalesmanOutputAsync(
            DateTime start, DateTime end, SalespersonDataScope scope, SalesmanOutputFilterDto? filter = null)
            => NotCalled<Task<List<ReportDtos.SalesmanOutputItem>>>();

        public Task<ReportDtos.FinancialStatement> GetBalanceSheetAsync(DateTime asOfDate)
            => NotCalled<Task<ReportDtos.FinancialStatement>>();

        public Task<ReportDtos.FinancialStatement> GetIncomeStatementAsync(DateTime start, DateTime end)
            => NotCalled<Task<ReportDtos.FinancialStatement>>();

        public Task<ReportDtos.FinancialStatement> GetCashFlowStatementAsync(DateTime start, DateTime end)
            => NotCalled<Task<ReportDtos.FinancialStatement>>();

        public Task<List<ReportDtos.ArAgingItem>> GetArAgingAsync(DateTime asOfDate)
            => NotCalled<Task<List<ReportDtos.ArAgingItem>>>();

        public Task<List<ReportDtos.ContainerStatsItem>> GetContainerStatsAsync(DateTime start, DateTime end, SalespersonDataScope scope)
            => NotCalled<Task<List<ReportDtos.ContainerStatsItem>>>();

        public Task<DynamicContainerStatsReportPageDto> GetDynamicContainerStatsReportAsync(DynamicContainerStatsReportRequest request, SalespersonDataScope scope)
            => NotCalled<Task<DynamicContainerStatsReportPageDto>>();

        public Task<List<ReportDtos.StockAlertItem>> GetStockAlertAsync()
            => NotCalled<Task<List<ReportDtos.StockAlertItem>>>();

        public Task<List<ReportDtos.SalesCommissionItem>> GetSalesCommissionAsync(DateTime start, DateTime end, SalespersonDataScope scope)
            => NotCalled<Task<List<ReportDtos.SalesCommissionItem>>>();

        public Task<DynamicSalesCommissionReportPageDto> GetDynamicSalesCommissionReportAsync(DynamicSalesCommissionReportRequest request, SalespersonDataScope scope)
            => NotCalled<Task<DynamicSalesCommissionReportPageDto>>();

        public Task<List<ReportDtos.FollowUpDueItem>> GetFollowUpDueAsync(DateTime asOfDate, int aheadDays, SalespersonDataScope scope)
            => NotCalled<Task<List<ReportDtos.FollowUpDueItem>>>();

        public Task<DynamicFollowUpDueReportPageDto> GetDynamicFollowUpDueReportAsync(DynamicFollowUpDueReportRequest request, SalespersonDataScope scope)
            => NotCalled<Task<DynamicFollowUpDueReportPageDto>>();

        public Task<List<ReportDtos.QuotationConversionItem>> GetQuotationConversionAsync(
            DateTime start, DateTime end, SalespersonDataScope scope, QuotationConversionFilterDto? filter = null)
            => NotCalled<Task<List<ReportDtos.QuotationConversionItem>>>();

        public Task<ReportDtos.InventoryMovementReport> GetInventoryMovementReportAsync(
            ReportDtos.InventoryMovementReportQuery query, CancellationToken cancellationToken = default)
            => NotCalled<Task<ReportDtos.InventoryMovementReport>>();

        public Task<ReportDtos.InventoryAgingReport> GetInventoryAgingReportAsync(
            ReportDtos.InventoryAgingReportQuery query, CancellationToken cancellationToken = default)
            => NotCalled<Task<ReportDtos.InventoryAgingReport>>();
    }

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
        => BuildProvider(NewReportService(db), db, datasetKey);

    private static IReportConfigurationDatasetProvider BuildProvider(IReportService reportService, ErpDbContext db, string datasetKey)
    {
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetPurchaseCost => new PurchaseCostReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetTaxRefundSummary => new TaxRefundSummaryReportConfigurationDatasetProvider(reportService, db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };
    }

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new(new IReportConfigurationDatasetProvider[]
        {
            new PurchaseCostReportConfigurationDatasetProvider(NewReportService(db), db),
            new TaxRefundSummaryReportConfigurationDatasetProvider(NewReportService(db), db),
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

    private static List<string> PurchaseCostFields()
        => new() { "supplierName", "supplierType", "orderCount", "totalAmount", "avgAmount", "lastOrderDate" };

    private static List<string> TaxRefundFields()
        => new() { "refundPeriod", "recordCount", "declaredCount", "refundedCount", "exportAmount", "refundableAmount", "refundedAmount", "unrefundedAmount" };

    // ==================== 1. 目录暴露与字段 / 币种口径 ====================

    [Fact]
    public async Task 目录_两个数据集全部暴露且字段白名单与币种口径真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cost-tax", "purchase-cost", "tax-refund-summary");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        Assert.Equal(2, result.Datasets.Count);

        var pc = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetPurchaseCost);
        Assert.Equal("金额按原币呈现；按供应商聚合；不跨币种换算或合并", pc.CurrencyUnitSemantics);
        Assert.Equal("purchase-cost", pc.RequiredMenuCode);
        Assert.Equal(7, pc.Fields.Count);
        Assert.Contains(pc.Fields, f => f.Key == "supplierName" && f.Type == ReportConfigurationConstants.TypeText);
        var totalAmount = Assert.Single(pc.Fields, f => f.Key == "totalAmount");
        Assert.Equal("原币金额", totalAmount.CurrencyUnit);
        Assert.True(totalAmount.Aggregatable);
        var orderCount = Assert.Single(pc.Fields, f => f.Key == "orderCount");
        Assert.False(orderCount.Aggregatable);
        Assert.Contains(ReportConfigurationConstants.CapabilityDateRange, pc.SupportedCapabilities);
        Assert.NotEmpty(pc.Metrics);
        Assert.DoesNotContain(ReportConfigurationConstants.CapabilityCustomFormula, pc.SupportedCapabilities);

        var tr = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetTaxRefundSummary);
        Assert.Equal("金额按原币呈现；按退税期间聚合；不跨币种换算或合并", tr.CurrencyUnitSemantics);
        Assert.Equal("tax-refund-summary", tr.RequiredMenuCode);
        Assert.Equal(8, tr.Fields.Count);
        var export = Assert.Single(tr.Fields, f => f.Key == "exportAmount");
        Assert.Equal("原币金额", export.CurrencyUnit);
        var refundable = Assert.Single(tr.Fields, f => f.Key == "refundableAmount");
        Assert.Equal("人民币", refundable.CurrencyUnit);
        Assert.NotEmpty(tr.Metrics);
    }

    [Fact]
    public async Task 目录_未授权菜单_对应数据集不暴露()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pc-only", "purchase-cost");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(result.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetPurchaseCost, dataset.DatasetKey);
    }

    // ==================== 2. 采购成本：单一币种 / 混合币种 / 空行 / 分页 / 日期 ====================

    [Fact]
    public async Task 采购成本_单一币种_预览与既有报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pc-uniform", "purchase-cost");
        var supplier = new BaseSupplier { SupplierCode = "S1", SupplierName = "供应商甲", SupplierType = "工厂", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        db.PurchaseOrders.Add(new PurchaseOrder { OrderNo = "PO-1", OrderDate = DateTime.Today, SupplierId = supplier.Id, TotalAmount = 100m, Currency = Currency.CNY, Status = DocumentStatus.Approved });
        db.PurchaseOrders.Add(new PurchaseOrder { OrderNo = "PO-2", OrderDate = DateTime.Today, SupplierId = supplier.Id, TotalAmount = 50m, Currency = Currency.CNY, Status = DocumentStatus.Approved });
        db.SaveChanges();

        var reportService = NewReportService(db);
        var provider = new PurchaseCostReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetPurchaseCostAsync(DateTime.Today, DateTime.Today);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseCost, PurchaseCostFields().ToArray()),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        var row = Assert.Single(preview.Rows);
        Assert.Equal("供应商甲", (string)row["supplierName"]!);
        Assert.Equal("工厂", (string)row["supplierType"]!);
        Assert.Equal(2, (int)row["orderCount"]!);
        Assert.Equal(150m, (decimal)row["totalAmount"]!);
        Assert.Equal(75m, (decimal)row["avgAmount"]!);
    }

    [Fact]
    public async Task 采购成本_混合币种_聚合前拒绝且不调用旧服务()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pc-mixed", "purchase-cost");
        var supplier = new BaseSupplier { SupplierCode = "S1", SupplierName = "供应商甲", Status = 1 };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        db.PurchaseOrders.Add(new PurchaseOrder { OrderNo = "PO-1", OrderDate = DateTime.Today, SupplierId = supplier.Id, TotalAmount = 100m, Currency = Currency.CNY, Status = DocumentStatus.Approved });
        db.PurchaseOrders.Add(new PurchaseOrder { OrderNo = "PO-2", OrderDate = DateTime.Today, SupplierId = supplier.Id, TotalAmount = 50m, Currency = Currency.USD, Status = DocumentStatus.Approved });
        db.SaveChanges();

        var sentinel = new NoQueryReportService();
        var provider = new PurchaseCostReportConfigurationDatasetProvider(sentinel, db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseCost, PurchaseCostFields().ToArray()),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("供应商#", ex.Message);
        Assert.False(sentinel.PurchaseCostExecuted);
    }

    [Fact]
    public async Task 采购成本_空行_返回空页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pc-empty", "purchase-cost");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetPurchaseCost);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseCost, PurchaseCostFields().ToArray()),
            Params(), user.Id);

        Assert.Equal(0, preview.Total);
        Assert.Empty(preview.Rows);
        Assert.Equal(1, preview.TotalPages);
    }

    [Fact]
    public async Task 采购成本_页码小于1_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pc-page", "purchase-cost");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetPurchaseCost);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseCost, PurchaseCostFields().ToArray()),
            new ReportConfigurationPreviewParameters(0, 20, ReportConfigurationConstants.GroupNone, null, null),
            user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 采购成本_日期范围超上限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pc-daterange", "purchase-cost");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetPurchaseCost);

        var definition = Definition(ReportConfigurationConstants.DatasetPurchaseCost, PurchaseCostFields().ToArray());
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "orderDate", Operator = ReportConfigurationConstants.OperatorBetween, Value = "2020-01-01", Value2 = "2022-01-01" },
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(definition, Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 3. 退税汇总：单一币种 / 混合币种 ====================

    [Fact]
    public async Task 退税汇总_单一币种_预览与既有报表一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "tr-uniform", "tax-refund-summary");
        db.BaseTaxRefunds.Add(new BaseTaxRefund { RefundNo = "TR-1", RefundPeriod = "2026-08", Currency = "USD", ExportAmount = 100m, RefundableAmount = 13m, RefundedAmount = 5m, Status = "已申报" });
        db.BaseTaxRefunds.Add(new BaseTaxRefund { RefundNo = "TR-2", RefundPeriod = "2026-08", Currency = "USD", ExportAmount = 50m, RefundableAmount = 6m, RefundedAmount = 0m, Status = "待申报" });
        db.SaveChanges();

        var reportService = NewReportService(db);
        var provider = new TaxRefundSummaryReportConfigurationDatasetProvider(reportService, db);
        var legacy = await reportService.GetTaxRefundSummaryAsync();

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetTaxRefundSummary, TaxRefundFields().ToArray()),
            Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        var row = Assert.Single(preview.Rows);
        Assert.Equal("2026-08", (string)row["refundPeriod"]!);
        Assert.Equal(2, (int)row["recordCount"]!);
        Assert.Equal(1, (int)row["declaredCount"]!);
        Assert.Equal(0, (int)row["refundedCount"]!);
        Assert.Equal(150m, (decimal)row["exportAmount"]!);
        Assert.Equal(19m, (decimal)row["refundableAmount"]!);
        Assert.Equal(5m, (decimal)row["refundedAmount"]!);
        Assert.Equal(14m, (decimal)row["unrefundedAmount"]!);
    }

    [Fact]
    public async Task 退税汇总_混合币种_聚合前拒绝且不调用旧服务()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "tr-mixed", "tax-refund-summary");
        db.BaseTaxRefunds.Add(new BaseTaxRefund { RefundNo = "TR-1", RefundPeriod = "2026-08", Currency = "USD", ExportAmount = 100m, RefundableAmount = 13m, Status = "已申报" });
        db.BaseTaxRefunds.Add(new BaseTaxRefund { RefundNo = "TR-2", RefundPeriod = "2026-08", Currency = "EUR", ExportAmount = 50m, RefundableAmount = 6m, Status = "待申报" });
        db.SaveChanges();

        var sentinel = new NoQueryReportService();
        var provider = new TaxRefundSummaryReportConfigurationDatasetProvider(sentinel, db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetTaxRefundSummary, TaxRefundFields().ToArray()),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("2026-08", ex.Message);
        Assert.False(sentinel.TaxRefundExecuted);
    }

    // ==================== 4. 授权 / 数据范围 fail closed ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetPurchaseCost, "purchase-cost")]
    [InlineData(ReportConfigurationConstants.DatasetTaxRefundSummary, "tax-refund-summary")]
    public async Task 受限制用户_有菜单_数据集不暴露且预览拒绝(string datasetKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedRestrictedAuthorizedUser(db, "restricted-" + datasetKey, menuCode);
        var sentinel = new NoQueryReportService();
        var provider = BuildProvider(sentinel, db, datasetKey);

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(datasetKey, DefaultFields(datasetKey).ToArray()), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.False(sentinel.PurchaseCostExecuted);
        Assert.False(sentinel.TaxRefundExecuted);
    }

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetPurchaseCost, "purchase-cost")]
    [InlineData(ReportConfigurationConstants.DatasetTaxRefundSummary, "tax-refund-summary")]
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
            Definition(datasetKey, DefaultFields(datasetKey).ToArray()), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetPurchaseCost);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetPurchaseCost, PurchaseCostFields().ToArray()),
            Params(), null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 5. 迁移登记册 parity 边界 ====================

    [Fact]
    public async Task 迁移登记册_两预设就绪且非parity通过()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry", "purchase-cost", "tax-refund-summary");
        var registry = new ReportMigrationRegistry(
            BuildCatalog(db), db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());

        var result = await registry.GetRegistryAsync(user.Id);

        var pc = Assert.Single(result.Entries, e => e.LegacyKey == "report:purchase-cost");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, pc.ParityStatus);

        var tr = Assert.Single(result.Entries, e => e.LegacyKey == "report:tax-refund-summary");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, tr.ParityStatus);
    }

    private static List<string> DefaultFields(string datasetKey)
        => datasetKey == ReportConfigurationConstants.DatasetPurchaseCost
            ? PurchaseCostFields()
            : TaxRefundFields();
}
