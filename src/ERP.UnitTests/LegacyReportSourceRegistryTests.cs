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
/// ERP-330 旧报表来源统一接缝单元测试：覆盖每个旧报表分类的非空解析、代表族键 / 行与既有读取一致、
/// 菜单撤销 fail closed、未知 / 空白 / 畸形键拒绝、未绑定来源 environment-blocked 与未认证拒绝。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class LegacyReportSourceRegistryTests
{
    // ==================== 0. 测试脚手架 ====================

    [Fact]
    public async Task Legacy_source_rejects_oversized_snapshot_instead_of_passing_partial_parity()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "oversized", "sales-order", "sales-order-export");
        var page = new LegacyBillExportPage
        {
            Columns = new List<LegacyBillExportColumn> { new("BillNo", "Bill No", ReportConfigurationConstants.TypeText) },
            Rows = Enumerable.Range(1, 201).Select(i => new Dictionary<string, object?> { ["BillNo"] = "BOUND-" + i }).ToList(),
            Total = 201, Page = 1, PageSize = 200, TotalPages = 2,
        };
        var registry = BuildRegistry(db, new FakeBillExportReader(page));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => registry.ReadAsync(Req("export:bill-proc:sales-order", user.Id)));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
    }

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

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = true)
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

    private static LegacyReportSourceRegistry BuildRegistry(
        ErpDbContext db, ILegacyBillExportReadService? billReader = null)
        => new(
            db,
            new ReportService(db),
            new DynamicSalesOrderReportQuery(db),
            new DynamicReceivableReportQuery(db),
            new DynamicPurchaseOrderReportQuery(db),
            billReader ?? new FakeBillExportReader(),
            new LegacyPrintSnapshotReadService(db));

    private sealed class FakeBillExportReader : ILegacyBillExportReadService
    {
        private readonly LegacyBillExportPage _page;
        public LegacyBillExportQuery? LastQuery { get; private set; }

        public FakeBillExportReader(LegacyBillExportPage? page = null)
            => _page = page ?? EmptyPage();

        public Task<LegacyBillExportPage> ReadPageAsync(
            LegacyBillExportQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(_page);
        }

        private static LegacyBillExportPage EmptyPage()
            => new()
            {
                Columns = new List<LegacyBillExportColumn>(),
                Rows = new List<Dictionary<string, object?>>(),
                Total = 0,
                Page = 1,
                PageSize = 20,
                TotalPages = 1,
            };
    }

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
        db.SaveChanges();
    }

    private static TradeDocument SeedDocument(ErpDbContext db, long id, string docNo, string docType,
        decimal amount, string currency = "USD")
        => db.TradeDocuments.Add(new TradeDocument
        {
            Id = id,
            DocNo = docNo,
            DocType = docType,
            CustomerName = "客户甲",
            Amount = amount,
            Currency = currency,
            IssueDate = DateTime.Today,
            Status = "待制作",
            Copies = 1,
        }).Entity;

    private static void SeedItem(ErpDbContext db, long id, long docId, int lineNo, string productCode,
        decimal quantity, string unit, decimal unitPrice = 0m, decimal lineAmount = 0m, string currency = "USD")
        => db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            Id = id,
            TradeDocumentId = docId,
            LineNo = lineNo,
            ProductCode = productCode,
            ProductNameCn = productCode,
            Quantity = quantity,
            Unit = unit,
            UnitPrice = unitPrice,
            LineAmount = lineAmount,
            Currency = currency,
        });

    // ==================== 1. 每个旧报表分类均解析非空快照 ====================

    [Fact]
    public async Task 每个分类_授权用户_返回非空快照()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "all-categories",
            "product-sales-ranking", "balance-sheet", "sales-order", "sales-order-export", "customer", "doc-center");
        SeedDocument(db, 1, "TD-1", "商业发票", 100m, "USD");
        db.SaveChanges();

        var registry = BuildRegistry(db);

        var cases = new (string Key, LegacyReportSourceRequest Request)[]
        {
            ("report:product-sales-ranking", Req("report:product-sales-ranking", user.Id)),
            ("report:balance-sheet", Req("report:balance-sheet", user.Id)),
            ("dynamic:sales-order", Req("dynamic:sales-order", user.Id)),
            ("export:bill-proc:sales-order", Req("export:bill-proc:sales-order", user.Id)),
            ("packet:customer-report-packet", Req("packet:customer-report-packet", user.Id, customerId: 1)),
            ("document:trade-document-print", Req("document:trade-document-print", user.Id, documentId: 1)),
            ("print-template:sales-order", Req("print-template:sales-order", user.Id)),
        };

        foreach (var (key, request) in cases)
        {
            var result = await registry.ReadAsync(request);
            Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
            Assert.NotNull(result.Snapshot);
            Assert.Equal(key, result.LegacyKey);
        }
    }

    private static LegacyReportSourceRequest Req(
        string key, long? userId, long? customerId = null, long? documentId = null)
        => new()
        {
            LegacyKey = key,
            UserId = userId,
            Start = DateTime.Today.AddDays(-30),
            End = DateTime.Today,
            AsOfDate = DateTime.Today,
            CustomerId = customerId,
            DocumentId = documentId,
        };

    // ==================== 2. 代表族键 / 行与既有读取一致 ====================

    [Fact]
    public async Task 资产负债表_快照键与行与既有服务一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bs", "balance-sheet");
        SeedBalanceSheetData(db);

        var reports = new ReportService(db);
        var legacy = await reports.GetBalanceSheetAsync(DateTime.Today);

        var registry = BuildRegistry(db);
        var result = await registry.ReadAsync(Req("report:balance-sheet", user.Id));

        Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
        var snapshot = Assert.IsType<ReportMigrationParitySnapshotDto>(result.Snapshot);
        Assert.Equal(new[] { "lineName", "amount" }, snapshot.Columns.Select(c => c.Key).ToArray());
        Assert.Equal(legacy.Lines.Count, snapshot.Rows.Count);
        for (var i = 0; i < legacy.Lines.Count; i++)
        {
            Assert.Equal(legacy.Lines[i].Name, snapshot.Rows[i].RowKeys[0]);
            Assert.Equal(legacy.Lines[i].Amount, (decimal)snapshot.Rows[i].Cells[1]!);
        }
    }

    [Fact]
    public async Task 应收账龄_快照键与行与既有服务一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "ar", "ar-aging");
        SeedArAgingData(db);

        var reports = new ReportService(db);
        var legacy = await reports.GetArAgingAsync(DateTime.Today);

        var registry = BuildRegistry(db);
        var result = await registry.ReadAsync(Req("report:ar-aging", user.Id));

        Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
        var snapshot = Assert.IsType<ReportMigrationParitySnapshotDto>(result.Snapshot);
        Assert.Equal(legacy.Count, snapshot.Rows.Count);

        var orderNoIndex = Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == "OrderNo");
        var currencyIndex = Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == "Currency");
        var balanceIndex = Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == "Balance");
        Assert.True(orderNoIndex >= 0 && currencyIndex >= 0 && balanceIndex >= 0);

        for (var i = 0; i < legacy.Count; i++)
        {
            Assert.Equal(legacy[i].OrderNo, snapshot.Rows[i].RowKeys[0]);
            Assert.Equal(legacy[i].Currency, (string)snapshot.Rows[i].Cells[currencyIndex]!);
            Assert.Equal(legacy[i].Balance, (decimal)snapshot.Rows[i].Cells[balanceIndex]!);
        }
    }

    [Fact]
    public async Task 动态销售订单_快照键与行与既有查询一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "dso", "sales-order");
        var customer = new BaseCustomer { CustomerCode = "C-SO", CustomerName = "客户B", CreditDays = 0 };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-DYN",
            OrderDate = DateTime.Today,
            CustomerId = customer.Id,
            TotalAmount = 300m,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
        });
        db.SaveChanges();

        var query = new DynamicSalesOrderReportQuery(db);
        var request = new DynamicSalesOrderReportRequest { Page = 1, PageSize = 20 };
        var legacy = await query.PreviewAsync(request, user.Id);

        var registry = BuildRegistry(db);
        var result = await registry.ReadAsync(Req("dynamic:sales-order", user.Id));

        Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
        var snapshot = Assert.IsType<ReportMigrationParitySnapshotDto>(result.Snapshot);
        Assert.Equal(legacy.Columns.Select(c => c.Key).ToArray(), snapshot.Columns.Select(c => c.Key).ToArray());
        Assert.Equal(legacy.Rows.Count, snapshot.Rows.Count);
        if (legacy.Rows.Count > 0)
        {
            var orderNoIndex = Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == "orderNo");
            Assert.True(orderNoIndex >= 0);
            Assert.Equal((string)legacy.Rows[0]["orderNo"]!, (string)snapshot.Rows[0].Cells[orderNoIndex]!);
        }
    }

    [Fact]
    public async Task 旧单据导出_快照键与行与既有读取一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bill", "sales-order", "sales-order-export");

        var columns = new List<LegacyBillExportColumn>
        {
            new("BillNo", "单据号", ReportConfigurationConstants.TypeText),
            new("OrderDate", "订单日期", ReportConfigurationConstants.TypeDate),
            new("Amount", "金额", ReportConfigurationConstants.TypeNumber),
        };
        var rows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal)
            {
                ["BillNo"] = "SO-EXP-1",
                ["OrderDate"] = new DateTime(2026, 9, 1),
                ["Amount"] = 88.5m,
            },
        };
        var page = new LegacyBillExportPage
        {
            Columns = columns,
            Rows = rows,
            Total = 1,
            Page = 1,
            PageSize = 20,
            TotalPages = 1,
        };
        var reader = new FakeBillExportReader(page);

        var registry = BuildRegistry(db, reader);
        var result = await registry.ReadAsync(Req("export:bill-proc:sales-order", user.Id));

        Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
        var snapshot = Assert.IsType<ReportMigrationParitySnapshotDto>(result.Snapshot);
        Assert.Equal(new[] { "BillNo", "OrderDate", "Amount" }, snapshot.Columns.Select(c => c.Key).ToArray());
        Assert.Single(snapshot.Rows);
        Assert.Equal("SO-EXP-1", snapshot.Rows[0].RowKeys[0]);
        Assert.Equal(88.5m, (decimal)snapshot.Rows[0].Cells[2]!);
    }

    [Fact]
    public async Task 单证打印_快照键与行与既有读取一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "doc", "doc-center");
        SeedDocument(db, 1, "TD-P", "商业发票", 100m, "USD");
        SeedItem(db, 11, 1, 1, "P1", 2m, "箱", unitPrice: 10m, lineAmount: 20m, currency: "USD");
        db.SaveChanges();

        var legacy = await TradeDocumentItemService.ListAsync(db, 1);

        var registry = BuildRegistry(db);
        var result = await registry.ReadAsync(Req("document:trade-document-print", user.Id, documentId: 1));

        Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
        var snapshot = Assert.IsType<ReportMigrationParitySnapshotDto>(result.Snapshot);
        Assert.Equal(legacy.Items.Count, snapshot.Rows.Count);
        Assert.Equal(legacy.Items[0].Id.ToString(), snapshot.Rows[0].RowKeys[0]);

        var idIndex = Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == "Id");
        var lineAmountIndex = Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == "LineAmount");
        Assert.True(idIndex >= 0 && lineAmountIndex >= 0);
        Assert.Equal(legacy.Items[0].Id, (long)snapshot.Rows[0].Cells[idIndex]!);
        Assert.Equal(legacy.Items[0].LineAmount, (decimal)snapshot.Rows[0].Cells[lineAmountIndex]!);
    }

    // ==================== 3. 菜单撤销 fail closed 与键 / 来源边界 ====================

    [Fact]
    public async Task 菜单撤销_来源失败关闭()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoke", "balance-sheet");
        var registry = BuildRegistry(db);

        var first = await registry.ReadAsync(Req("report:balance-sheet", user.Id));
        Assert.Equal(LegacyReportSourceStatus.Success, first.Status);

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        var second = await registry.ReadAsync(Req("report:balance-sheet", user.Id));
        Assert.Equal(LegacyReportSourceStatus.Forbidden, second.Status);
        Assert.Null(second.Snapshot);
        Assert.Equal(ErrorCodes.Forbidden, second.ErrorCode);
    }

    [Fact]
    public async Task 未知键_精确拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown", "sales-order");
        var registry = BuildRegistry(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            registry.ReadAsync(Req("report:does-not-exist", user.Id)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("未知的旧报表来源键", ex.Message);
    }

    [Fact]
    public async Task 空白键_精确拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "blank", "sales-order");
        var registry = BuildRegistry(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            registry.ReadAsync(new LegacyReportSourceRequest { LegacyKey = "  ", UserId = user.Id }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不能为空", ex.Message);
    }

    [Fact]
    public async Task 畸形键_精确拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "malformed", "sales-order");
        var registry = BuildRegistry(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            registry.ReadAsync(new LegacyReportSourceRequest { LegacyKey = "report:bad;key", UserId = user.Id }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("非法", ex.Message);
    }

    [Fact]
    public async Task 未绑定来源_显式环境阻塞()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "blocked", "product");
        var registry = BuildRegistry(db);

        var result = await registry.ReadAsync(Req("export:product-export-field-completeness", user.Id));

        Assert.Equal(LegacyReportSourceStatus.EnvironmentBlocked, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, result.ErrorCode);
    }

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var registry = BuildRegistry(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            registry.ReadAsync(new LegacyReportSourceRequest { LegacyKey = "report:balance-sheet" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }
}




