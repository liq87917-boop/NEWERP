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
/// ERP-318 销售单据打印族迁移单元测试：报价单 / 形式发票 PI 两个销售单据打印族的受控目录 / 数据集适配器 /
/// 迁移预设与登记册集成。覆盖目录映射、字段键 / 顺序 / 标签 / null / 软删除、表头与明细行两种粒度、
/// 原币 / 基础单位保留、客户业务员数据范围（fail closed）、撤销菜单立即收敛、未知字段 / 筛选精确拒绝，
/// 以及两个迁移预设与 parity 派生。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationSalesDocumentMigrationTests
{
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, bool privileged, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = new SysRole { RoleName = name + "-role", RoleCode = name + "-role", IsSystem = privileged };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            Department = "销售部",
            Position = "业务员",
            IsSalesman = isSalesman,
            Status = 1,
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            EmpId = empId,
            IsDeleted = deleted,
            Status = 1,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Quotation SeedQuotation(
        ErpDbContext db, string no, long? customerId, string customerName,
        Currency currency, decimal totalAmount, decimal totalAmountCny = 0,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var q = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = customerName,
            Currency = currency,
            ExchangeRate = 1,
            TotalAmount = totalAmount,
            TotalAmountCny = totalAmountCny,
            SalesmanName = "业务员",
            Status = status,
        };
        db.Quotations.Add(q);
        db.SaveChanges();
        return q;
    }

    private static QuotationDetail SeedQuotationDetail(
        ErpDbContext db, long quotationId, string no, int sortNo, string productCode,
        string unit, decimal quantity, decimal unitPrice, decimal amount, bool deleted = false)
    {
        var d = new QuotationDetail
        {
            QuotationId = quotationId,
            QuotationNo = no,
            SortNo = sortNo,
            ProductCode = productCode,
            ProductName = productCode,
            Spec = "标准",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = amount,
            IsDeleted = deleted,
        };
        db.QuotationDetails.Add(d);
        db.SaveChanges();
        return d;
    }

    private static ProformaInvoice SeedProformaInvoice(
        ErpDbContext db, string no, long? customerId, string customerName, string sourceQuotationNo,
        Currency currency, decimal totalAmount, decimal depositRatio, decimal depositAmount,
        DocumentStatus status = DocumentStatus.Pending)
    {
        var p = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = customerName,
            QuotationNo = sourceQuotationNo,
            Currency = currency,
            ExchangeRate = 1,
            TotalAmount = totalAmount,
            TotalAmountCny = totalAmount,
            DepositRatio = depositRatio,
            DepositAmount = depositAmount,
            SalesmanName = "业务员",
            Status = status,
        };
        db.ProformaInvoices.Add(p);
        db.SaveChanges();
        return p;
    }

    private static ProformaInvoiceDetail SeedProformaInvoiceDetail(
        ErpDbContext db, long piId, string no, int sortNo, string productCode,
        string unit, decimal quantity, decimal unitPrice, decimal amount, bool deleted = false)
    {
        var d = new ProformaInvoiceDetail
        {
            PiId = piId,
            PiNo = no,
            SortNo = sortNo,
            ProductCode = productCode,
            ProductName = productCode,
            Spec = "标准",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = amount,
            IsDeleted = deleted,
        };
        db.ProformaInvoiceDetails.Add(d);
        db.SaveChanges();
        return d;
    }

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new(ReportConfigurationSalesDocumentCatalog.Families
            .Select(f => (IReportConfigurationDatasetProvider)new SalesDocumentReportConfigurationDatasetProvider(db, f.DatasetKey))
            .ToList());

    private static IReadOnlyList<string> HeaderFields(string familyKey)
        => ReportConfigurationSalesDocumentCatalog.Resolve(familyKey).Columns
            .Where(c => c.Grain != ReportSalesDocumentGrain.Detail)
            .Select(c => c.Key)
            .ToList();

    private static ReportConfigurationDefinition Definition(string familyKey, IReadOnlyList<string>? fields = null)
    {
        var family = ReportConfigurationSalesDocumentCatalog.Resolve(familyKey);
        return new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = family.DatasetKey,
            Fields = (fields ?? HeaderFields(familyKey)).ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
        };
    }

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 100)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    // ==================== 1. 受控目录映射 ====================

    [Fact]
    public void 目录_两个销售单据族_均受控且映射非空数据集与列()
    {
        var keys = new[] { "quotation", "proforma-invoice" };
        Assert.Equal(keys.Length, ReportConfigurationSalesDocumentCatalog.Families.Count);

        foreach (var key in keys)
        {
            var family = ReportConfigurationSalesDocumentCatalog.Resolve(key);
            Assert.Equal(key, family.FamilyKey);
            Assert.Equal(ReportConfigurationSalesDocumentCatalog.DatasetKeyPrefix + key, family.DatasetKey);
            Assert.Equal(new[] { key }, family.RequiredMenuCodes.ToArray());
            Assert.NotEmpty(family.Columns);
            Assert.Contains(family.Columns, c => c.Key == "id" && c.Grain == ReportSalesDocumentGrain.Identity);
            Assert.Contains(family.Columns, c => c.Key == "sortNo" && c.Grain == ReportSalesDocumentGrain.Detail);
            Assert.Contains(family.Columns, c => c.Key == "totalAmount" && c.Grain == ReportSalesDocumentGrain.Header);

            Assert.All(family.Columns, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Key));
                Assert.False(string.IsNullOrWhiteSpace(c.Title));
                Assert.False(string.IsNullOrWhiteSpace(c.Type));
                Assert.False(string.IsNullOrWhiteSpace(c.Property));
                Assert.Contains(c.Grain, new[] { ReportSalesDocumentGrain.Identity, ReportSalesDocumentGrain.Header, ReportSalesDocumentGrain.Detail });
            });
        }
    }

    [Fact]
    public async Task 目录_两个销售单据数据集_字段键顺序与标签一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "catalog-all", privileged: true, "quotation", "proforma-invoice");

        foreach (var family in ReportConfigurationSalesDocumentCatalog.Families)
        {
            var provider = new SalesDocumentReportConfigurationDatasetProvider(db, family.DatasetKey);
            var dataset = await provider.GetDatasetAsync(user.Id);

            Assert.NotNull(dataset);
            Assert.Equal(family.DatasetKey, dataset.DatasetKey);
            Assert.Equal(family.Title, dataset.Label);
            Assert.Equal(family.RequiredMenuCodes[0], dataset.RequiredMenuCode);
            Assert.Equal(ReportConfigurationSalesDocumentCatalog.CurrencyUnitSemantics, dataset.CurrencyUnitSemantics);
            Assert.Equal(family.Columns.Select(c => c.Key).ToArray(), dataset.Fields.Select(f => f.Key).ToArray());
            Assert.Equal(family.Columns.Select(c => c.Title).ToArray(), dataset.Fields.Select(f => f.Label).ToArray());
        }
    }

    // ==================== 2. 预览：表头 / 明细两种粒度 ====================

    [Fact]
    public async Task 报价单预览_表头粒度_主键倒序且原币与表头金额一次呈现()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "q-header", privileged: true, "quotation");
        SeedQuotation(db, "QT-USD", null, "客户USD", Currency.USD, 100m);
        SeedQuotation(db, "QT-EUR", null, "客户EUR", Currency.EUR, 200m);

        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("quotation"), Params(), user.Id);

        Assert.Equal(2, preview.Total);
        Assert.Equal("QT-EUR", (string)preview.Rows[0]["docNo"]!);
        Assert.Equal("EUR", (string)preview.Rows[0]["currency"]!);
        Assert.Equal(200m, (decimal)preview.Rows[0]["totalAmount"]!);
        Assert.Equal("QT-USD", (string)preview.Rows[1]["docNo"]!);
        Assert.Equal("USD", (string)preview.Rows[1]["currency"]!);
        Assert.Equal(100m, (decimal)preview.Rows[1]["totalAmount"]!);
    }

    [Fact]
    public async Task 报价单预览_明细行粒度_行序稳定且表头金额不摊入明细()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "q-line", privileged: true, "quotation");
        var q1 = SeedQuotation(db, "QT-1", null, "客户", Currency.USD, 100m);
        SeedQuotationDetail(db, q1.Id, "QT-1", 1, "P-1", "PCS", 10m, 5m, 50m);
        SeedQuotationDetail(db, q1.Id, "QT-1", 2, "P-2", "PCS", 2m, 25m, 50m);
        SeedQuotationDetail(db, q1.Id, "QT-1", 3, "P-DEL", "PCS", 1m, 1m, 1m, deleted: true);
        var q2 = SeedQuotation(db, "QT-2", null, "客户", Currency.USD, 300m);
        SeedQuotationDetail(db, q2.Id, "QT-2", 1, "P-3", "BOX", 5m, 60m, 300m);

        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);
        var fields = new[] { "id", "docNo", "sortNo", "lineId", "productCode", "quantity", "unit", "unitPrice", "amount", "totalAmount", "currency" };

        var preview = await provider.PreviewAsync(Definition("quotation", fields), Params(), user.Id);

        Assert.Equal(3, preview.Total);
        Assert.Equal("QT-2", (string)preview.Rows[0]["docNo"]!);
        Assert.Equal(1, (int)preview.Rows[0]["sortNo"]!);
        Assert.Equal(300m, (decimal)preview.Rows[0]["amount"]!);
        Assert.Null(preview.Rows[0]["totalAmount"]);

        Assert.Equal("QT-1", (string)preview.Rows[1]["docNo"]!);
        Assert.Equal(1, (int)preview.Rows[1]["sortNo"]!);
        Assert.Equal(50m, (decimal)preview.Rows[1]["amount"]!);

        Assert.Equal("QT-1", (string)preview.Rows[2]["docNo"]!);
        Assert.Equal(2, (int)preview.Rows[2]["sortNo"]!);
        Assert.Equal(50m, (decimal)preview.Rows[2]["amount"]!);
    }

    [Fact]
    public async Task PI预览_表头与明细_PI专属字段保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "pi", privileged: true, "proforma-invoice");
        var pi = SeedProformaInvoice(db, "PI-1", null, "客户", "QT-SRC", Currency.EUR, 500m, 30m, 150m);
        SeedProformaInvoiceDetail(db, pi.Id, "PI-1", 1, "P-1", "PCS", 100m, 5m, 500m);

        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("proforma-invoice").DatasetKey);

        var header = await provider.PreviewAsync(Definition("proforma-invoice"), Params(), user.Id);
        Assert.Equal(1, header.Total);
        Assert.Equal("PI-1", (string)header.Rows[0]["docNo"]!);
        Assert.Equal("QT-SRC", (string)header.Rows[0]["quotationNo"]!);
        Assert.Equal(30m, (decimal)header.Rows[0]["depositRatio"]!);
        Assert.Equal(150m, (decimal)header.Rows[0]["depositAmount"]!);
        Assert.Equal("EUR", (string)header.Rows[0]["currency"]!);

        var lineFields = new[] { "id", "docNo", "sortNo", "productCode", "quantity", "unit", "amount" };
        var lines = await provider.PreviewAsync(Definition("proforma-invoice", lineFields), Params(), user.Id);
        Assert.Equal(1, lines.Total);
        Assert.Equal(500m, (decimal)lines.Rows[0]["amount"]!);
        Assert.Equal(100m, (decimal)lines.Rows[0]["quantity"]!);
        Assert.Equal("PCS", (string)lines.Rows[0]["unit"]!);
    }

    // ==================== 3. 授权 / 数据范围 fail closed ====================

    [Fact]
    public async Task 撤销菜单_目录返回null且预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoked", privileged: false, "proforma-invoice");

        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.PreviewAsync(Definition("quotation"), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 客户数据范围_受限制业务员仅见分配客户_未映射为空()
    {
        using var db = TestDbFactory.Create();
        var assigned = SeedAuthorizedUser(db, "salesman01", privileged: false, "quotation");
        var unassigned = SeedAuthorizedUser(db, "salesman02", privileged: false, "quotation");

        var employee = SeedEmployee(db, "salesman01", isSalesman: true);
        var c1 = SeedCustomer(db, "C-1", "分配客户", empId: employee.Id);
        var c2 = SeedCustomer(db, "C-2", "他人客户");
        SeedQuotation(db, "QT-1", c1.Id, c1.CustomerName, Currency.USD, 100m);
        SeedQuotation(db, "QT-2", c2.Id, c2.CustomerName, Currency.USD, 200m);

        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("quotation"), Params(), assigned.Id);
        Assert.Equal(1, preview.Total);
        Assert.Equal("QT-1", (string)preview.Rows.Single()["docNo"]!);

        var empty = await provider.PreviewAsync(Definition("quotation"), Params(), unassigned.Id);
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Rows);
    }

    // ==================== 4. 未知字段 / 筛选精确拒绝 ====================

    [Fact]
    public async Task 未知字段与筛选_精确拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown", privileged: true, "quotation");
        var provider = new SalesDocumentReportConfigurationDatasetProvider(
            db, ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey);

        var exField = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition("quotation", new[] { "docNo", "SecretColumn" }), Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exField.Code);

        var definition = Definition("quotation");
        definition.Filters.Add(new ReportConfigurationFilter
        {
            FieldKey = "customerName",
            Operator = ReportConfigurationConstants.OperatorEq,
            Value = "客户",
        });
        var exFilter = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.PreviewAsync(definition, Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exFilter.Code);
    }

    // ==================== 5. 迁移预设与登记册集成 ====================

    [Theory]
    [InlineData("print-template:quotation")]
    [InlineData("print-template:proforma-invoice")]
    public async Task 迁移预设_两个销售单据打印族均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();

        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }

    [Fact]
    public async Task 迁移登记册_销售单据族_经通用目录暴露_且遗留打印入口到达preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry", privileged: true, "quotation", "proforma-invoice");
        var catalog = BuildCatalog(db);

        // 迁移可用性：受控销售单据数据集经通用目录暴露（mapping availability）
        var quotationDataset = await catalog.GetDatasetAsync(
            ReportConfigurationSalesDocumentCatalog.Resolve("quotation").DatasetKey, user.Id);
        Assert.NotNull(quotationDataset);
        var piDataset = await catalog.GetDatasetAsync(
            ReportConfigurationSalesDocumentCatalog.Resolve("proforma-invoice").DatasetKey, user.Id);
        Assert.NotNull(piDataset);

        // 打印模板族已接入绑定目录：数据集 + 预设 + 兼容性声明齐备，parity 至少到 preset-ready；
        // 无真实比对证据，绝不 parity-passed（fail-closed 退役）。
        var registry = new ReportMigrationRegistry(
            catalog, db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());
        var result = await registry.GetRegistryAsync(user.Id);

        var quotation = Assert.Single(result.Entries, e => e.LegacyKey == "print-template:quotation");
        Assert.Equal("sales-document:quotation", quotation.DatasetKey);
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, quotation.ParityStatus);

        var pi = Assert.Single(result.Entries, e => e.LegacyKey == "print-template:proforma-invoice");
        Assert.Equal("sales-document:proforma-invoice", pi.DatasetKey);
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, pi.ParityStatus);

        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }
}




