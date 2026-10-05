using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-307 Stage 2 通用报表配置捆绑单元测试（内存数据库，不连接 SQL Server / 不执行 SQL）：
/// 覆盖有界有序多节预览（两个不同授权数据集 / 固定发布版本 / 共享节 / 空节 / 节数边界 / 非法 Id 与版本）、
/// 任一节被撤销 / 拒绝即整体失败（无部分数据）、以及通用多工作表 Excel / 多节 PDF 导出语义
/// （字段顺序 / 类型化值 / null 留空 / 公式转义 / 来源工作表 / 字体缺失显式失败）。
/// </summary>
public class ReportConfigurationBundleTests
{
    // ==================== 脚手架 ====================

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

    private static SysRole SeedRole(ErpDbContext db, string suffix, bool isSystem = true)
    {
        var role = new SysRole
        {
            RoleName = $"Role-{suffix}",
            RoleCode = $"Role-{suffix}-{Guid.NewGuid():N}",
            IsSystem = isSystem,
        };
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

    private static long SeedUserWithMenus(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        Currency currency, decimal amount, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = currency,
            TotalAmount = amount,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(ErpDbContext db, string invoiceNumber,
        long customerId, string currency, decimal grossAmount, DateTime? invoiceDate = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", string.Empty),
            InvoiceDate = invoiceDate ?? new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "客户",
            Currency = currency,
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db), db),
        };

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db)));

    private static ReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new(db, BuildProviders(db));

    private static IReportConfigurationBundleService BuildBundle(ErpDbContext db)
        => new ReportConfigurationBundleService(BuildExecution(db));

    private static IReportConfigurationSharingService BuildSharing(ErpDbContext db)
        => new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db)));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition SalesOrderDefinition(string[]? fields = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 } ? fields.ToList() : new List<string> { "orderNo", "currency", "totalAmount" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    private static ReportConfigurationDefinition ReceivableDefinition(string[]? fields = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetReceivable,
            Fields = fields is { Length: > 0 } ? fields.ToList() : new List<string> { "invoiceNumber", "currency", "grossAmount" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    // ==================== 1. 有界有序多节预览 ====================

    [Fact]
    public async Task 预览_两个不同授权数据集_保留节标题与行数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "bundle-two", "sales-order", "customer");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 200m);
        SeedInvoice(db, "INV-1", customer.Id, "USD", 500m);

        var service = BuildService(db);
        var so = await service.CreateAsync(user, SaveDto("销售订单节", SalesOrderDefinition()));
        var recv = await service.CreateAsync(user, SaveDto("应收节", ReceivableDefinition()));

        var bundle = BuildBundle(db);
        var preview = await bundle.PreviewAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = so.Id, Title = "订单" },
                new() { ConfigurationId = recv.Id, Title = "发票" },
            },
        });

        Assert.Equal(2, preview.SectionCount);
        Assert.Equal(2, preview.Sections.Count);
        Assert.Equal("订单", preview.Sections[0].Title);
        Assert.Equal("发票", preview.Sections[1].Title);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, preview.Sections[0].Preview.DatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, preview.Sections[1].Preview.DatasetKey);
        Assert.Equal(2, preview.Sections[0].Preview.Rows.Count);
        Assert.Single(preview.Sections[1].Preview.Rows);
        Assert.Equal(3, preview.TotalRowCount);
    }

    [Fact]
    public async Task 预览_固定发布版本与共享节_重新校验访问()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedUserWithMenus(db, "bundle-owner", "sales-order");
        var recipient = SeedUserWithMenus(db, "bundle-recipient", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(owner, SaveDto("共享报表", SalesOrderDefinition()));
        var published = await service.PublishAsync(owner, created.Id, created.Version);

        var sharing = BuildSharing(db);
        var grant = await sharing.GrantAsync(owner, created.Id, new ReportConfigurationGrantRequestDto
        {
            RecipientUserId = recipient,
            RevisionVersion = published.CurrentPublishedVersion,
        });

        var bundle = BuildBundle(db);
        var preview = await bundle.PreviewAsync(recipient, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = created.Id, RevisionVersion = grant.RevisionVersion, Title = "共享" },
            },
        });

        Assert.Single(preview.Sections);
        Assert.True(preview.Sections[0].Preview.IsPinnedRevision);
        Assert.Equal(grant.RevisionVersion, preview.Sections[0].Preview.PinnedRevisionVersion);

        await sharing.RevokeAsync(owner, created.Id, recipient, grant.Version);
        await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(recipient, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = created.Id, RevisionVersion = grant.RevisionVersion },
            },
        }));
    }

    [Fact]
    public async Task 预览_任一节被拒绝_整体失败无部分()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "bundle-deny", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var service = BuildService(db);
        var so = await service.CreateAsync(user, SaveDto("合法节", SalesOrderDefinition()));

        var bundle = BuildBundle(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = so.Id },
                new() { ConfigurationId = 999999 },
            },
        }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 预览_节数边界与非法Id版本_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "bundle-bounds", "sales-order");
        var bundle = BuildBundle(db);

        var exEmpty = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user, new ReportConfigurationBundleRequest()));
        Assert.Equal(ErrorCodes.InvalidParameter, exEmpty.Code);

        var nine = new List<ReportConfigurationBundleSectionRequest>();
        for (var i = 0; i < 9; i++)
            nine.Add(new ReportConfigurationBundleSectionRequest { ConfigurationId = i + 1 });
        var exNine = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user, new ReportConfigurationBundleRequest { Sections = nine }));
        Assert.Equal(ErrorCodes.InvalidParameter, exNine.Code);

        var exId = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest> { new() { ConfigurationId = 0 } },
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, exId.Code);

        var exVer = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest> { new() { ConfigurationId = 1, RevisionVersion = 0 } },
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, exVer.Code);

        var exPage = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest> { new() { ConfigurationId = 1, Page = 0 } },
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, exPage.Code);
    }

    [Fact]
    public async Task 预览_空节_保留空节并继续其他节()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "bundle-empty", "sales-order", "customer");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var service = BuildService(db);
        var so = await service.CreateAsync(user, SaveDto("有数据节", SalesOrderDefinition()));
        var recv = await service.CreateAsync(user, SaveDto("空节", ReceivableDefinition()));

        var bundle = BuildBundle(db);
        var preview = await bundle.PreviewAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = so.Id, Title = "订单" },
                new() { ConfigurationId = recv.Id, Title = "空发票" },
            },
        });

        Assert.Equal(2, preview.Sections.Count);
        Assert.Empty(preview.Sections[1].Preview.Rows);
        Assert.Equal(1, preview.TotalRowCount);
    }

    // ==================== 2. 通用多工作表 Excel ====================

    private static ReportConfigurationBundleExportResultDto SyntheticBundle()
    {
        var columns1 = new List<ReportConfigurationColumnDto>
        {
            new("orderNo", "订单号", ReportConfigurationConstants.TypeText, null),
            new("totalAmount", "金额", ReportConfigurationConstants.TypeNumber, "原币金额"),
        };
        var columns2 = new List<ReportConfigurationColumnDto>
        {
            new("invoiceNumber", "发票号", ReportConfigurationConstants.TypeText, null),
            new("grossAmount", "含税", ReportConfigurationConstants.TypeNumber, null),
        };
        var evidence = new ReportConfigurationEvidenceContextDto(
            ReportConfigurationConstants.DatasetSalesOrder, "grain", "semantics", "readonly", "boundary", "disclaimer", ReportConfigurationConstants.CoverageCurrentPage);

        return new ReportConfigurationBundleExportResultDto
        {
            Name = "报表配置捆绑",
            CorrelationId = "corr",
            SectionCount = 2,
            TotalRowCount = 3,
            Sections = new List<ReportConfigurationBundleSectionExportDto>
            {
                new()
                {
                    Ordinal = 1, Title = "订单节", ConfigurationId = 1,
                    Preview = new ReportConfigurationPreviewDto { Name = "订单报表", Version = 1, DatasetKey = ReportConfigurationConstants.DatasetSalesOrder, Columns = columns1, Evidence = evidence },
                    Facts = new List<Dictionary<string, object?>>
                    {
                        new(StringComparer.Ordinal) { ["orderNo"] = "=HYPERLINK(\"http://evil\")", ["totalAmount"] = -50m },
                        new(StringComparer.Ordinal) { ["orderNo"] = "SO-2", ["totalAmount"] = null },
                    },
                },
                new()
                {
                    Ordinal = 2, Title = "发票节", ConfigurationId = 2,
                    Preview = new ReportConfigurationPreviewDto { Name = "发票报表", Version = 1, DatasetKey = ReportConfigurationConstants.DatasetReceivable, Columns = columns2, Evidence = evidence },
                    Facts = new List<Dictionary<string, object?>> { new(StringComparer.Ordinal) { ["invoiceNumber"] = "INV-1", ["grossAmount"] = 500m } },
                },
            },
        };
    }

    [Fact]
    public void 导出_Excel_多工作表_字段顺序_类型化值与公式转义()
    {
        var bytes = new ReportConfigurationBundleExcelExporter().Build(SyntheticBundle());
        using var workbook = new XSSFWorkbook(new MemoryStream(bytes));

        Assert.Equal(3, workbook.NumberOfSheets);

        var sheet1 = workbook.GetSheet("01-订单节");
        Assert.NotNull(sheet1);
        Assert.Equal("订单号", sheet1.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("金额（原币金额）", sheet1.GetRow(0).GetCell(1).StringCellValue);
        Assert.StartsWith("'", sheet1.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet1.GetRow(1).GetCell(1).CellType);
        Assert.Equal(-50d, sheet1.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("SO-2", sheet1.GetRow(2).GetCell(0).StringCellValue);
        Assert.NotEqual(CellType.Numeric, sheet1.GetRow(2).GetCell(1).CellType);

        var sheet2 = workbook.GetSheet("02-发票节");
        Assert.NotNull(sheet2);

        var provenance = workbook.GetSheet(ReportConfigurationBundleExcelExporter.ProvenanceSheetName);
        Assert.NotNull(provenance);
    }

    [Fact]
    public async Task 导出_Excel_两个数据集_真实管线多工作表()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "bundle-excel", "sales-order", "customer");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedInvoice(db, "INV-1", customer.Id, "USD", 500m);

        var service = BuildService(db);
        var so = await service.CreateAsync(user, SaveDto("订单", SalesOrderDefinition()));
        var recv = await service.CreateAsync(user, SaveDto("发票", ReceivableDefinition()));

        var bundle = BuildBundle(db);
        using var lease = new ReportConfigurationExecutionBudget().Acquire(user);
        var result = await bundle.BuildExportResultAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = so.Id },
                new() { ConfigurationId = recv.Id },
            },
        }, lease);

        var bytes = new ReportConfigurationBundleExcelExporter().Build(result);
        using var workbook = new XSSFWorkbook(new MemoryStream(bytes));
        Assert.True(workbook.NumberOfSheets >= 3);
    }

    // ==================== 3. 多节 PDF ====================

    [Fact]
    public void 导出_PDF_字体缺失_显式失败()
    {
        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationBundlePdfExporter.Export(SyntheticBundle(), fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    [Fact]
    public async Task 导出_PDF_两个数据集_多节内容()
    {
        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is null)
            return;   // 字体缺失已在单独用例覆盖（Windows CI 有 SimHei）

        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "bundle-pdf", "sales-order", "customer");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedInvoice(db, "INV-1", customer.Id, "USD", 500m);

        var service = BuildService(db);
        var so = await service.CreateAsync(user, SaveDto("订单", SalesOrderDefinition()));
        var recv = await service.CreateAsync(user, SaveDto("发票", ReceivableDefinition()));

        var bundle = BuildBundle(db);
        using var lease = new ReportConfigurationExecutionBudget().Acquire(user);
        var result = await bundle.BuildExportResultAsync(user, new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = so.Id },
                new() { ConfigurationId = recv.Id },
            },
        }, lease);

        var bytes = ReportConfigurationBundlePdfExporter.Export(result, fontPath);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));

        using var pdf = PdfReader.Open(new MemoryStream(bytes));
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public void 导出_PDF_宽节_列带拆分与长文本_可读PDF()
    {
        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is null)
            return;   // 字体缺失已在单独用例覆盖（Windows CI 有 SimHei）

        var columns = new List<ReportConfigurationColumnDto> { new("orderNo", "订单号", ReportConfigurationConstants.TypeText, null) };
        for (var i = 1; i <= 27; i++)
            columns.Add(new($"f{i}", $"字段{i}超长中文说明用于验证列带拆分", ReportConfigurationConstants.TypeText, null));

        var evidence = new ReportConfigurationEvidenceContextDto(
            ReportConfigurationConstants.DatasetSalesOrder, "grain", "原币金额，不跨币种", "只读", "边界", "免责", ReportConfigurationConstants.CoverageCurrentPage);

        var longText = "这是一个非常长的中文单元格文本用于验证导出时折行而不丢失任何字符同时保持行连续完整可读";

        Dictionary<string, object?> WideRow(string orderNo, string? text, decimal? amount)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["orderNo"] = orderNo };
            for (var i = 1; i <= 27; i++)
            {
                if (i == 1) row[$"f{i}"] = text;
                else if (i == 2) row[$"f{i}"] = amount;
                else row[$"f{i}"] = $"值{i}";
            }
            return row;
        }

        var bundle = new ReportConfigurationBundleExportResultDto
        {
            Name = "宽列捆绑",
            SectionCount = 2,
            TotalRowCount = 3,
            Sections = new List<ReportConfigurationBundleSectionExportDto>
            {
                new()
                {
                    Ordinal = 1, Title = "宽节", ConfigurationId = 1,
                    Preview = new ReportConfigurationPreviewDto
                    {
                        Name = "宽列报表", Version = 1, DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
                        Columns = columns, Evidence = evidence,
                    },
                    Facts = new List<Dictionary<string, object?>>
                    {
                        WideRow("SO-1", longText, null),
                        WideRow("SO-2", "零值行", 0m),
                    },
                },
                new()
                {
                    Ordinal = 2, Title = "窄节", ConfigurationId = 2,
                    Preview = new ReportConfigurationPreviewDto
                    {
                        Name = "窄报表", Version = 1, DatasetKey = ReportConfigurationConstants.DatasetReceivable,
                        Columns = new List<ReportConfigurationColumnDto>
                        {
                            new("invoiceNumber", "发票号", ReportConfigurationConstants.TypeText, null),
                            new("grossAmount", "含税金额", ReportConfigurationConstants.TypeNumber, null),
                        },
                        Evidence = evidence,
                    },
                    Facts = new List<Dictionary<string, object?>>
                    {
                        new(StringComparer.Ordinal) { ["invoiceNumber"] = "INV-1", ["grossAmount"] = 500m },
                    },
                },
            },
        };

        var bytes = ReportConfigurationBundlePdfExporter.Export(bundle, fontPath);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));

        using var pdf = PdfReader.Open(new MemoryStream(bytes));
        Assert.True(pdf.Pages.Count >= 3);   // 宽节多个列带页 + 窄节一页
    }

    [Fact]
    public void 导出_PDF_已取消_抛出取消()
    {
        var bundle = SyntheticBundle();
        Assert.Throws<OperationCanceledException>(() =>
            ReportConfigurationBundlePdfExporter.Export(bundle, SimHeiPdfFontResolver.FindFontPath(), new CancellationToken(true)));
    }
}

