using System.Text;
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
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-264 通用报表配置中文 PDF 导出（只读、有界）单元测试。
/// 覆盖：纯离线列页拆分 / 身份列重复 / 本页序号回退 / 负数与 null 未知 / 折行不丢字 / 类型化单元格 /
/// 币种分区小计（无全匹配合计）、字体缺失显式失败（无部分文件）、以及导出端点复用同一有界已授权预览管线
/// （PDF 签名 / 内容类型 / A4 / 空页 / 分组小计 / 授权撤销 / 只读不写库）。
/// <para>全部使用内存数据库（TestDbFactory）或纯离线对象，不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationPdfTests
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

    private static SysRole SeedRole(ErpDbContext db, string suffix, bool isSystem = false)
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

    private static long SeedPrivilegedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, menuCode, isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
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

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db)));

    private static IReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new ReportConfigurationExecutionService(db, BuildProviders(db));

    private static ReportConfigurationsController BuildController(ErpDbContext db)
        => new(
            new ReportConfigurationCatalog(BuildProviders(db)),
            BuildService(db),
            BuildExecution(db),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db))));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition SalesOrderDefinition(
        string[]? fields = null, string? groupBy = "none")
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "orderNo", "totalAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { groupBy ?? ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    private static ReportConfigurationColumnDto Column(string key, string label, string type = "text")
        => new(key, label, type, null);

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    // ==================== 1. 列页拆分 / 身份列 / 本页序号 ====================

    [Fact]
    public void SplitColumnPages_宽列集_拆成多个列页并重复身份列()
    {
        var columns = new List<ReportConfigurationColumnDto> { Column("orderNo", "订单号") };
        for (var i = 1; i <= 12; i++)
            columns.Add(Column($"f{i}", "较长的中文字段标签用于触发列页拆分"));

        var bands = ReportConfigurationPdfExporter.SplitColumnPages(columns, Array.Empty<Dictionary<string, object?>>());

        Assert.True(bands.Count >= 2);
        foreach (var band in bands)
            Assert.Equal("orderNo", band[0].Key);
    }

    [Fact]
    public void SplitColumnPages_无允许身份列_回退本页序号()
    {
        var columns = new List<ReportConfigurationColumnDto>
        {
            Column("customerName", "客户名称"),
            Column("totalAmount", "金额", "number"),
        };

        var bands = ReportConfigurationPdfExporter.SplitColumnPages(columns, Array.Empty<Dictionary<string, object?>>());
        var band = Assert.Single(bands);

        Assert.Equal(ReportConfigurationPdfExporter.RowOrdinalColumnKey, band[0].Key);
        Assert.Equal(ReportConfigurationPdfExporter.RowOrdinalLabel, band[0].Label);
    }

    // ==================== 2. 单元格 / 折行 / 类型化 ====================

    [Fact]
    public void BuildRowCells_保留选定顺序_负数与null未知()
    {
        var columns = new[]
        {
            Column("orderNo", "订单号"),
            Column("totalAmount", "金额", "number"),
            Column("currency", "币种"),
        };
        var row = new Dictionary<string, object?>
        {
            ["orderNo"] = "SO-1",
            ["totalAmount"] = -123.45m,
            ["currency"] = null,
        };

        var cells = ReportConfigurationPdfExporter.BuildRowCells(columns, row, 1);

        Assert.Equal(new[] { "SO-1", "-123.45", "" }, cells);
    }

    [Fact]
    public void BuildRowCells_本页序号()
    {
        var columns = new[]
        {
            ReportConfigurationPdfExporter.RowOrdinalColumn(),
            Column("customerName", "客户名称"),
        };
        var row = new Dictionary<string, object?> { ["customerName"] = "客户" };

        var cells = ReportConfigurationPdfExporter.BuildRowCells(columns, row, 7);

        Assert.Equal(new[] { "7", "客户" }, cells);
    }

    [Fact]
    public void WrapText_绝不丢字并保留负号()
    {
        const string text = "-123456.789";
        var lines = ReportConfigurationPdfExporter.WrapText(text, 8, 16);

        Assert.Equal(text, string.Concat(lines));
        Assert.StartsWith("-", lines[0]);
    }

    [Fact]
    public void FormatCellValue_布尔日期null与负数()
    {
        Assert.Equal("是", ReportConfigurationPdfExporter.FormatCellValue(true));
        Assert.Equal("否", ReportConfigurationPdfExporter.FormatCellValue(false));
        Assert.Equal("2026-09-01", ReportConfigurationPdfExporter.FormatCellValue(new DateTime(2026, 9, 1)));
        Assert.Equal(string.Empty, ReportConfigurationPdfExporter.FormatCellValue(null));
        Assert.Equal("-5.5", ReportConfigurationPdfExporter.FormatCellValue(-5.5m));
    }

    [Fact]
    public void BuildSubtotalRows_币种分区_无全匹配合计()
    {
        var groups = new List<ReportConfigurationGroupSubtotalDto>
        {
            new("c1", "客户", new List<ReportConfigurationCurrencyPartitionDto>
            {
                new("USD", 1, 100m, null, null, null, string.Empty),
                new("CNY", 1, 200m, null, null, null, string.Empty),
            }),
        };

        var rows = ReportConfigurationPdfExporter.BuildSubtotalRows(groups);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => Equals(r["currency"], "USD") && Equals(r["amount"], 100m));
        Assert.Contains(rows, r => Equals(r["currency"], "CNY") && Equals(r["amount"], 200m));
        Assert.DoesNotContain(rows, r => string.Equals(r["currency"] as string, "全匹配", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMetricRows_拍平选中指标_保留数值与来源条数()
    {
        var metrics = new List<ReportConfigurationMetricResultDto>
        {
            new()
            {
                Key = "totalAmount",
                Function = ReportConfigurationConstants.AggregateSum,
                Label = "金额",
                Unit = "原币金额",
                CurrencyBehavior = ReportConfigurationMetricRules.CurrencyBehaviorPartition,
                Cells = new List<ReportConfigurationMetricCellDto>
                {
                    new() { GroupLabel = "全部", Currency = "USD", Value = 150m, KnownCount = 2, MissingCount = 0, SourceCount = 2 },
                },
            },
        };

        var rows = ReportConfigurationPdfExporter.BuildMetricRows(metrics);
        var row = Assert.Single(rows);
        Assert.Equal("金额（合计）（原币金额）", row["metric"]);
        Assert.Equal(150m, row["value"]);
        Assert.Equal(2, row["source"]);
    }

    [Fact]
    public void 复合分组_小计与指标_按保存顺序渲染全部维度列()
    {
        var groups = new List<ReportConfigurationGroupSubtotalDto>
        {
            new(
                "key",
                "客户 #1 · 2026年9月",
                new List<ReportConfigurationCurrencyPartitionDto>
                {
                    new("USD", 1, 100m, null, null, null, string.Empty),
                })
            {
                Dimensions = new List<ReportConfigurationGroupDimensionValueDto>
                {
                    new("customer", "客户 #1", "1", false),
                    new("month", "2026年9月", "2026-09", false),
                },
            },
        };
        var metrics = new List<ReportConfigurationMetricResultDto>
        {
            new()
            {
                Key = "totalAmount",
                Function = ReportConfigurationConstants.AggregateSum,
                Label = "金额",
                Unit = "原币金额",
                CurrencyBehavior = ReportConfigurationMetricRules.CurrencyBehaviorPartition,
                Cells = new List<ReportConfigurationMetricCellDto>
                {
                    new()
                    {
                        GroupLabel = "客户 #1 · 2026年9月",
                        Currency = "USD",
                        Value = 100m,
                        KnownCount = 1,
                        MissingCount = 0,
                        SourceCount = 1,
                        Dimensions = new List<ReportConfigurationGroupDimensionValueDto>
                        {
                            new("customer", "客户 #1", "1", false),
                            new("month", "2026年9月", "2026-09", false),
                        },
                    },
                },
            },
        };

        var subtotalColumns = ReportConfigurationPdfExporter.BuildSubtotalColumns(groups);
        Assert.Equal(new[] { "customer", "month", "currency", "count", "amount" },
            subtotalColumns.Take(5).Select(c => c.Key));

        var subtotalRow = Assert.Single(ReportConfigurationPdfExporter.BuildSubtotalRows(groups));
        Assert.Equal("客户 #1", subtotalRow["customer"]);
        Assert.Equal("2026年9月", subtotalRow["month"]);
        Assert.Equal(100m, subtotalRow["amount"]);

        var metricColumns = ReportConfigurationPdfExporter.BuildMetricColumns(metrics);
        Assert.Equal(new[] { "metric", "customer", "month", "currency", "value" },
            metricColumns.Take(5).Select(c => c.Key));

        var metricRow = Assert.Single(ReportConfigurationPdfExporter.BuildMetricRows(metrics));
        Assert.Equal("客户 #1", metricRow["customer"]);
        Assert.Equal("2026年9月", metricRow["month"]);
    }

    [Fact]
    public void BuildPivotRows_行轴与单元格文本_与Excel同口径()
    {
        var pivot = new ReportConfigurationPivotResultDto
        {
            RowDimension = ReportConfigurationConstants.GroupCustomer,
            ColumnDimension = ReportConfigurationConstants.GroupMonth,
            RowAxis = new List<ReportConfigurationPivotAxisDto>
            {
                new() { Key = "1", Label = "客户 #1", SortKey = "1" },
            },
            ColumnAxis = new List<ReportConfigurationPivotAxisDto>
            {
                new() { Key = "m1", Label = "2026年9月", SortKey = "202609" },
                new() { Key = "m2", Label = "2026年10月", SortKey = "202610" },
            },
        };
        var metric = new ReportConfigurationPivotMetricDto
        {
            Key = "totalAmount",
            Function = ReportConfigurationConstants.AggregateSum,
            Label = "金额",
            Unit = "原币金额",
            Cells = new List<ReportConfigurationPivotCellDto>
            {
                new() { RowIndex = 0, ColumnIndex = 0, Currency = "USD", Value = 100m },
                new() { RowIndex = 0, ColumnIndex = 1, Currency = "CNY", Value = 200m },
            },
        };

        var columns = ReportConfigurationPdfExporter.BuildPivotColumns(pivot);
        Assert.Equal("客户", columns[0].Label);
        Assert.Equal("2026年9月", columns[1].Label);

        var rows = ReportConfigurationPdfExporter.BuildPivotRows(pivot, metric);
        var row = Assert.Single(rows);
        Assert.Equal("客户 #1", row["__pivotRow__"]);
        Assert.Equal("USD 100", row["m1"]);
        Assert.Equal("CNY 200", row["m2"]);
    }

    // ==================== 3. 字体缺失 / 渲染失败 ====================

    [Fact]
    public void Export_字体缺失_显式失败且无部分文件()
    {
        var preview = new ReportConfigurationPreviewDto();

        var ex = Assert.Throws<BusinessException>(
            () => ReportConfigurationPdfExporter.Export(preview, fontPath: null));

        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 4. 导出端点复用有界已授权预览管线 ====================

    [Fact]
    public async Task ExportPdf_销售订单_返回PDF且只读()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-pdf", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("PDF报表", SalesOrderDefinition()));

        var before = db.SalesOrders.Count();
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var result = await ctl.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task ExportPdf_空数据_返回PDF并标注()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-empty", "sales-order");

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("空报表", SalesOrderDefinition()));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var result = await ctl.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
    }

    [Fact]
    public async Task ExportPdf_分组_币种分区小计()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-group", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 200m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user,
            SaveDto("分组报表", SalesOrderDefinition(new[] { "orderNo", "totalAmount", "currency" }, "customer")));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);

        var result = await ctl.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var file = Assert.IsType<FileContentResult>(result);

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 2);   // 数据页 + 分组小计页

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var subtotalRows = ReportConfigurationPdfExporter.BuildSubtotalRows(preview.Groups);

        Assert.Equal(2, subtotalRows.Count);
        Assert.Contains(subtotalRows, r => Equals(r["currency"], "USD") && Equals(r["amount"], 100m));
        Assert.Contains(subtotalRows, r => Equals(r["currency"], "CNY") && Equals(r["amount"], 200m));
    }

    [Fact]
    public async Task ExportPdf_授权撤销_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "owner");
        var role = SeedRole(db, "sales-order");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, "sales-order");
        SeedRoleMenu(db, role.Id, menu.Id);

        var service = BuildService(db);
        var created = await service.CreateAsync(user.Id, SaveDto("报表", SalesOrderDefinition()));

        menu.IsDeleted = true;
        db.SaveChanges();

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public void Export_计算列_返回有效PDF且不失败()
    {
        var preview = new ReportConfigurationPreviewDto
        {
            ConfigurationId = 1,
            Name = "计算报表",
            Version = 1,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Columns = new List<ReportConfigurationColumnDto>
            {
                new("orderNo", "订单号", ReportConfigurationConstants.TypeText, null),
                new("doubleAmount", "双倍金额", ReportConfigurationConstants.TypeNumber, "原币金额",
                    ReportConfigurationFormulaRules.UnknownReasonText, IsComputed: true),
            },
            Rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["orderNo"] = "SO-1", ["doubleAmount"] = 200m },
            },
            ComputedColumns = new List<ReportConfigurationComputedColumnEvidenceDto>
            {
                new()
                {
                    Key = "doubleAmount",
                    Label = "双倍金额",
                    Unit = "原币金额",
                    UnknownReason = ReportConfigurationFormulaRules.UnknownReasonText,
                    Dependencies = new List<string> { "totalAmount" },
                },
            },
            Total = 1,
            Page = 1,
            PageSize = 20,
            TotalPages = 1,
            GroupBy = ReportConfigurationConstants.GroupNone,
            Evidence = new ReportConfigurationEvidenceContextDto(
                ReportConfigurationConstants.DatasetSalesOrder,
                "销售订单（一行一条销售订单）",
                "金额按订单原币呈现，不跨币种换算或合并",
                "只读声明",
                "边界声明",
                "免责声明",
                ReportConfigurationConstants.CoverageCurrentPage),
        };

        var bytes = ReportConfigurationPdfExporter.Export(preview);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));

        using var pdf = OpenPdf(bytes);
        Assert.True(pdf.Pages.Count >= 1);
    }
}
