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
/// ERP-334 旧打印快照统一读取接缝单元测试：覆盖基础资料打印族（列白名单 / Id 倒序 / null vs 零 / 客户数据范围）
/// 与销售单据打印族（表头 + 明细身份 / SortNo 行序 / 原币与基础单位分区 / 空单据 / 单证 Id GetPrint 复用），
/// 以及菜单撤销 Forbidden、未知来源 EnvironmentBlocked、软删除排除与未认证拒绝。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class LegacyPrintSnapshotSourceTests
{
    private static LegacyPrintSnapshotReadService BuildReader(ErpDbContext db)
        => new(db);

    private static LegacyPrintSnapshotRequest Req(string sourceKey, long? userId, long? documentId = null)
        => new() { SourceKey = sourceKey, UserId = userId, Page = 1, PageSize = 200, DocumentId = documentId };

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

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem)
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

    private static void SeedRoleMenu(ErpDbContext db, long roleId, string code)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = SeedMenu(db, code).Id });
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, code);
        return user;
    }

    private static SysUser SeedRestrictedUser(
        ErpDbContext db, string name, string employeeCode, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, code);

        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = employeeCode,
            EmployeeName = employeeCode,
            IsSalesman = true,
            Status = 1,
        });
        db.SaveChanges();
        return user;
    }

    private static int ColIndex(LegacyPrintSnapshot snapshot, string key)
        => Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == key);

    // ==================== 基础资料打印族 ====================

    [Fact]
    public async Task 客户_列白名单_Id倒序_null与零原样保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "master-cust", "customer");

        db.BaseCustomers.Add(new BaseCustomer
        {
            CustomerCode = "C-NULL",
            CustomerName = "空值客户",
            Currency = "USD",
            CreditLimit = 0m,
            CreditDays = null,
            DepositRatio = 0m,
            Status = 1,
        });
        db.BaseCustomers.Add(new BaseCustomer
        {
            CustomerCode = "C-ZERO",
            CustomerName = "零值客户",
            Currency = "EUR",
            CreditLimit = 150m,
            CreditDays = 0,
            DepositRatio = 20m,
            Status = 1,
        });
        db.SaveChanges();

        var result = await BuildReader(db).ReadAsync(Req("print-template:customer", user.Id));

        Assert.Equal(LegacyPrintSnapshotStatus.Success, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(
            ReportConfigurationMasterDataCatalog.Resolve("customer").Columns.Select(c => c.Key).ToArray(),
            snapshot.Columns.Select(c => c.Key).ToArray());

        var rows = snapshot.Rows.OrderBy(r => (string)r.Cells[ColIndex(snapshot, "customerCode")]!).ToList();
        Assert.Equal(2, rows.Count);

        var nullRow = rows[0];
        Assert.Equal("C-NULL", (string)nullRow.Cells[ColIndex(snapshot, "customerCode")]!);
        Assert.Null(nullRow.Cells[ColIndex(snapshot, "creditDays")]);
        Assert.Equal(0m, (decimal)nullRow.Cells[ColIndex(snapshot, "creditLimit")]!);

        var zeroRow = rows[1];
        Assert.Equal("C-ZERO", (string)zeroRow.Cells[ColIndex(snapshot, "customerCode")]!);
        Assert.Equal(0, zeroRow.Cells[ColIndex(snapshot, "creditDays")]);
        Assert.Equal(150m, (decimal)zeroRow.Cells[ColIndex(snapshot, "creditLimit")]!);
    }

    [Fact]
    public async Task 客户_受限制业务员仅见分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedRestrictedUser(db, "scope-sales", "scope-sales", "customer");
        var employee = db.BaseEmployees.Single();
        db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "C-MINE", CustomerName = "我的客户", EmpId = employee.Id, Status = 1 });
        db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "C-OTHER", CustomerName = "他人客户", EmpId = null, Status = 1 });
        db.SaveChanges();

        var result = await BuildReader(db).ReadAsync(Req("print-template:customer", user.Id));

        Assert.Equal(LegacyPrintSnapshotStatus.Success, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Single(snapshot.Rows);
        Assert.Equal("C-MINE", (string)snapshot.Rows[0].Cells[ColIndex(snapshot, "customerCode")]!);
    }


    [Fact]
    public async Task 商品_数值字段原样保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "master-prod", "product");
        db.BaseProducts.Add(new BaseProduct
        {
            ProductCode = "P-1",
            ProductName = "商品一",
            Spec = "标准",
            Unit = "PCS",
            UnitsPerPackage = 100,
            SalePrice = 12.5m,
            CostPrice = 7.25m,
            RefundRate = 13m,
            MinStock = 0m,
            Status = 1,
        });
        db.SaveChanges();

        var result = await BuildReader(db).ReadAsync(Req("print-template:product", user.Id));

        Assert.Equal(LegacyPrintSnapshotStatus.Success, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(
            ReportConfigurationMasterDataCatalog.Resolve("product").Columns.Select(c => c.Key).ToArray(),
            snapshot.Columns.Select(c => c.Key).ToArray());

        var row = Assert.Single(snapshot.Rows);
        Assert.Equal("P-1", (string)row.Cells[ColIndex(snapshot, "productCode")]!);
        Assert.Equal(12.5m, (decimal)row.Cells[ColIndex(snapshot, "salePrice")]!);
        Assert.Equal(100, row.Cells[ColIndex(snapshot, "unitsPerPackage")]);
        Assert.Equal(0m, (decimal)row.Cells[ColIndex(snapshot, "minStock")]!);
    }

    [Fact]
    public async Task 软删除来源_排除()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "master-del", "warehouse");
        db.BaseWarehouses.Add(new BaseWarehouse { WarehouseCode = "W-LIVE", WarehouseName = "在用", Status = 1 });
        db.BaseWarehouses.Add(new BaseWarehouse { WarehouseCode = "W-DEL", WarehouseName = "已删", IsDeleted = true, Status = 1 });
        db.SaveChanges();

        var result = await BuildReader(db).ReadAsync(Req("print-template:warehouse", user.Id));

        var snapshot = result.Snapshot!;
        Assert.Single(snapshot.Rows);
        Assert.Equal("W-LIVE", (string)snapshot.Rows[0].Cells[ColIndex(snapshot, "warehouseCode")]!);
    }

    [Fact]
    public async Task 菜单撤销_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "menu-revoke", "product");

        var result = await BuildReader(db).ReadAsync(Req("print-template:customer", user.Id));

        Assert.Equal(LegacyPrintSnapshotStatus.Forbidden, result.Status);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task 未知来源_EnvironmentBlocked()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown-src", "customer");

        var result = await BuildReader(db).ReadAsync(Req("print-template:does-not-exist", user.Id));

        Assert.Equal(LegacyPrintSnapshotStatus.EnvironmentBlocked, result.Status);
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, result.ErrorCode);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            BuildReader(db).ReadAsync(Req("print-template:customer", null)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }


    // ==================== 销售单据打印族 ====================

    private static Quotation SeedQuotation(ErpDbContext db, string no, string currency, int lineCount)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "客户甲",
            Currency = currency == "USD" ? Currency.USD : Currency.EUR,
            TotalAmount = 120m,
            TotalAmountCny = 840m,
            Status = DocumentStatus.Approved,
            Details = new List<QuotationDetail>(),
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();

        for (var i = 1; i <= lineCount; i++)
        {
            db.QuotationDetails.Add(new QuotationDetail
            {
                QuotationId = quotation.Id,
                QuotationNo = no,
                SortNo = i,
                ProductId = i,
                ProductCode = "P-" + i,
                ProductName = "商品" + i,
                Spec = "标准",
                Unit = "PCS",
                Quantity = i,
                UnitPrice = 30m,
                Amount = i * 30m,
                Moq = "500 pcs",
            });
        }
        db.SaveChanges();
        return quotation;
    }

    [Fact]
    public async Task 报价单_表头与明细身份_SortNo行序_原币与单位分区()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sales-qt", "quotation");
        var quotation = SeedQuotation(db, "QT-USD", "USD", 2);

        var result = await BuildReader(db).ReadAsync(Req("print-template:quotation", user.Id));

        Assert.Equal(LegacyPrintSnapshotStatus.Success, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(
            ReportConfigurationSalesDocumentCatalog.Resolve("quotation").Columns.Select(c => c.Key).ToArray(),
            snapshot.Columns.Select(c => c.Key).ToArray());

        Assert.Equal(3, snapshot.Rows.Count);

        var header = snapshot.Rows[0];
        Assert.Equal(new[] { quotation.Id.ToString() }, header.RowKeys);
        Assert.Equal("USD", header.Currency);
        Assert.Null(header.Unit);
        Assert.Equal("QT-USD", (string)header.Cells[ColIndex(snapshot, "docNo")]!);
        Assert.Equal(120m, (decimal)header.Cells[ColIndex(snapshot, "totalAmount")]!);
        Assert.Null(header.Cells[ColIndex(snapshot, "sortNo")]);

        var line1 = snapshot.Rows[1];
        Assert.Equal(2, line1.RowKeys.Count);
        Assert.Equal(quotation.Id.ToString(), line1.RowKeys[0]);
        Assert.Equal("USD", line1.Currency);
        Assert.Equal("PCS", line1.Unit);
        Assert.Equal(1, line1.Cells[ColIndex(snapshot, "sortNo")]);
        Assert.Equal(30m, (decimal)line1.Cells[ColIndex(snapshot, "amount")]!);
        Assert.Null(line1.Cells[ColIndex(snapshot, "totalAmount")]);

        Assert.Equal(2, snapshot.Rows[2].Cells[ColIndex(snapshot, "sortNo")]);
    }

    [Fact]
    public async Task 报价单_空明细_仅表头行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sales-empty", "quotation");
        var quotation = SeedQuotation(db, "QT-EMPTY", "USD", 0);

        var result = await BuildReader(db).ReadAsync(Req("print-template:quotation", user.Id));

        var snapshot = result.Snapshot!;
        var row = Assert.Single(snapshot.Rows);
        Assert.Equal(new[] { quotation.Id.ToString() }, row.RowKeys);
        Assert.Null(row.Cells[ColIndex(snapshot, "sortNo")]);
    }

    [Fact]
    public async Task 报价单_按单证Id读取_复用GetPrint()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sales-byid", "quotation");
        SeedQuotation(db, "QT-1", "USD", 1);
        var second = SeedQuotation(db, "QT-2", "EUR", 1);

        var result = await BuildReader(db).ReadAsync(Req("print-template:quotation", user.Id, second.Id));

        var snapshot = result.Snapshot!;
        Assert.Equal(2, snapshot.Rows.Count);
        Assert.Equal("QT-2", (string)snapshot.Rows[0].Cells[ColIndex(snapshot, "docNo")]!);
        Assert.Equal("EUR", snapshot.Rows[0].Currency);
    }


    [Fact]
    public async Task PI_表头专属字段与原币保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sales-pi", "proforma-invoice");
        var pi = new ProformaInvoice
        {
            PiNo = "PI-1",
            PiDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "客户甲",
            QuotationNo = "QT-EUR",
            Currency = Currency.EUR,
            TotalAmount = 300m,
            TotalAmountCny = 2100m,
            DepositRatio = 30m,
            DepositAmount = 90m,
            Status = DocumentStatus.Approved,
        };
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();

        var result = await BuildReader(db).ReadAsync(Req("print-template:proforma-invoice", user.Id));

        var snapshot = result.Snapshot!;
        var header = Assert.Single(snapshot.Rows);
        Assert.Equal("PI-1", (string)header.Cells[ColIndex(snapshot, "docNo")]!);
        Assert.Equal("EUR", header.Currency);
        Assert.Equal("QT-EUR", (string)header.Cells[ColIndex(snapshot, "quotationNo")]!);
        Assert.Equal(30m, (decimal)header.Cells[ColIndex(snapshot, "depositRatio")]!);
        Assert.Equal(90m, (decimal)header.Cells[ColIndex(snapshot, "depositAmount")]!);
    }

    // ==================== 旧报表来源登记册绑定 ====================

    [Fact]
    public async Task 登记册_基础资料与销售单据打印族_返回Success()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry-bind", "customer", "quotation");
        db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "C-REG", CustomerName = "客户", Status = 1 });
        SeedQuotation(db, "QT-REG", "USD", 1);

        var registry = new LegacyReportSourceRegistry(
            db,
            new ReportService(db),
            new DynamicSalesOrderReportQuery(db),
            new DynamicReceivableReportQuery(db),
            new DynamicPurchaseOrderReportQuery(db),
            new FakeBillExportReader(),
            new LegacyPrintSnapshotReadService(db));

        foreach (var key in new[] { "print-template:customer", "print-template:quotation" })
        {
            var result = await registry.ReadAsync(new LegacyReportSourceRequest
            {
                LegacyKey = key,
                UserId = user.Id,
                Page = 1,
                PageSize = 200,
            });

            Assert.Equal(LegacyReportSourceStatus.Success, result.Status);
            Assert.NotNull(result.Snapshot);
        }
    }

    private sealed class FakeBillExportReader : ILegacyBillExportReadService
    {
        public Task<LegacyBillExportPage> ReadPageAsync(
            LegacyBillExportQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new LegacyBillExportPage
            {
                Columns = new List<LegacyBillExportColumn>(),
                Rows = new List<Dictionary<string, object?>>(),
                Total = 0,
                Page = 1,
                PageSize = 200,
                TotalPages = 0,
            });
    }

}

