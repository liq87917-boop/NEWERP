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
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-263 通用报表配置 Excel 导出（只读、有界）单元测试。
/// 覆盖：通用工作簿保留选定列顺序与类型化值（数值保留符号、null 未知留空、日期 / 布尔正确呈现）、
/// 公式前导文本转义、分组小计仅当前预览页且按币种分区（绝不追加全匹配合计）、「报表口径」始终包含
/// 定义名称 / 版本、数据集 / 行粒度、规范化查询筛选与日期范围、币种 / 单位语义、只读与边界、未知值说明，
/// 以及导出端点复用同一有界、已授权预览管线（授权撤销 / 跨所有者 fail closed）。
/// <para>全部使用内存数据库（TestDbFactory）或纯离线工作簿，不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationExcelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

    private static ReportConfigurationsController BuildController(ErpDbContext db)
        => new(
            new ReportConfigurationCatalog(BuildProviders(db)),
            new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db))),
            new ReportConfigurationExecutionService(db, BuildProviders(db)),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db))));

    private static string Serialize(ReportConfigurationDefinition definition)
        => JsonSerializer.Serialize(definition, JsonOptions);

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

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new XSSFWorkbook(ms);
    }

    private static string CellText(ICell? cell)
        => cell is null ? string.Empty : (cell.CellType == CellType.String ? cell.StringCellValue : string.Empty);

    private static string AllSheetText(XSSFWorkbook workbook)
    {
        var parts = new List<string>();
        for (var i = 0; i < workbook.NumberOfSheets; i++)
        {
            var sheet = workbook.GetSheetAt(i);
            for (var r = 0; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r);
                if (row is null) continue;
                foreach (var cell in row.Cells)
                    if (cell.CellType == CellType.String)
                        parts.Add(cell.StringCellValue);
            }
        }
        return string.Join("|", parts);
    }

    // ==================== 1. 通用工作簿：列顺序 / 类型 / null / 日期 / 布尔 ====================

    private static ReportConfigurationPreviewDto MixedPreview()
        => new()
        {
            ConfigurationId = 1,
            Name = "测试报表",
            Version = 2,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Columns = new List<ReportConfigurationColumnDto>
            {
                new("orderNo", "订单号", ReportConfigurationConstants.TypeText, null),
                new("totalAmount", "金额", ReportConfigurationConstants.TypeNumber, "原币金额"),
                new("orderDate", "下单日期", ReportConfigurationConstants.TypeDate, null),
                new("isActive", "是否有效", ReportConfigurationConstants.TypeBoolean, null),
            },
            Rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal)
                {
                    ["orderNo"] = "=HYPERLINK(\"http://evil\")",
                    ["totalAmount"] = -50m,
                    ["orderDate"] = new DateTime(2026, 9, 1),
                    ["isActive"] = true,
                },
                new(StringComparer.Ordinal)
                {
                    ["orderNo"] = "SO-2",
                    ["totalAmount"] = null,
                    ["orderDate"] = null,
                    ["isActive"] = false,
                },
            },
            Total = 2,
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

    [Fact]
    public void Build_选定列顺序与类型化值_null未知留空()
    {
        var workbook = OpenWorkbook(new ReportConfigurationExcelExporter().Build(MixedPreview()));

        var sheet = workbook.GetSheetAt(0);
        Assert.Equal(ReportConfigurationExcelExporter.DataSheetName, sheet.SheetName);

        // 选定列顺序 + 币种单位语义列头
        Assert.Equal("订单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("金额（原币金额）", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("下单日期", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("是否有效", sheet.GetRow(0).GetCell(3).StringCellValue);

        // 第一行：负数金额保留符号、日期 / 布尔正确呈现、公式前导文本转义
        Assert.StartsWith("'", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(1).CellType);
        Assert.Equal(-50d, sheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("2026-09-01", sheet.GetRow(1).GetCell(2).StringCellValue);
        Assert.Equal("是", sheet.GetRow(1).GetCell(3).StringCellValue);

        // 第二行：null 未知一律留空（绝不写成 0），布尔 false 呈现「否」
        Assert.Equal("SO-2", sheet.GetRow(2).GetCell(0).StringCellValue);
        Assert.NotEqual(CellType.Numeric, sheet.GetRow(2).GetCell(1).CellType);
        Assert.Equal(string.Empty, CellText(sheet.GetRow(2).GetCell(1)));
        Assert.Equal(string.Empty, CellText(sheet.GetRow(2).GetCell(2)));
        Assert.Equal("否", sheet.GetRow(2).GetCell(3).StringCellValue);
    }

    [Fact]
    public void Build_公式前导文本转义为字面文本()
    {
        var preview = MixedPreview();
        preview.Rows[0]["orderNo"] = "=1+1";
        preview.Rows[1]["orderNo"] = "@SUM(A1)";

        var workbook = OpenWorkbook(new ReportConfigurationExcelExporter().Build(preview));
        var sheet = workbook.GetSheetAt(0);

        Assert.StartsWith("'=", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.StartsWith("'@", sheet.GetRow(2).GetCell(0).StringCellValue);
    }

    [Fact]
    public void Build_报表口径始终包含证据与未知值说明_即使无金额列()
    {
        var preview = MixedPreview();
        preview.Columns = new List<ReportConfigurationColumnDto>
        {
            new("orderNo", "订单号", ReportConfigurationConstants.TypeText, null),
        };

        var workbook = OpenWorkbook(new ReportConfigurationExcelExporter().Build(preview));
        var context = workbook.GetSheet(ReportConfigurationExcelExporter.ContextSheetName);
        Assert.NotNull(context);

        var text = AllSheetText(workbook);
        Assert.Contains("报表名称", text);
        Assert.Contains("测试报表", text);
        Assert.Contains("草稿（版本令牌 2）", text);
        Assert.Contains("行粒度", text);
        Assert.Contains("销售订单（一行一条销售订单）", text);
        Assert.Contains("币种/单位口径", text);
        Assert.Contains("只读与边界", text);
        Assert.Contains(ReportConfigurationExcelExporter.UnknownValueText, text);
    }

    [Fact]
    public void Build_分组小计按币种分区_绝不追加全匹配合计()
    {
        var preview = MixedPreview();
        preview.GroupBy = ReportConfigurationConstants.GroupCustomer;
        preview.Groups = new List<ReportConfigurationGroupSubtotalDto>
        {
            new("C001", "客户一", new List<ReportConfigurationCurrencyPartitionDto>
            {
                new("USD", 1, 100m, null, null, null, ""),
                new("CNY", 1, 200m, null, null, null, ""),
            }),
        };

        var workbook = OpenWorkbook(new ReportConfigurationExcelExporter().Build(preview));
        var groups = workbook.GetSheet(ReportConfigurationExcelExporter.GroupsSheetName);
        Assert.NotNull(groups);

        Assert.Equal("客户一", groups.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("USD", groups.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal("客户一", groups.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal("CNY", groups.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(2, groups.LastRowNum);   // 仅两个币种分区行，无全匹配合计行

        Assert.DoesNotContain("全匹配", AllSheetText(workbook));
    }

    // ==================== 2. 导出端点复用有界、已授权预览管线 ====================

    [Fact]
    public async Task Export_销售订单草稿_名称版本与规范化筛选日期范围()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-export", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-A", customer.Id, Currency.USD, 10m, new DateTime(2026, 9, 1));

        var def = SalesOrderDefinition(fields: new[] { "orderNo", "orderDate", "totalAmount" });
        def.Filters = new List<ReportConfigurationFilter>
        {
            new ReportConfigurationFilter
            {
                FieldKey = "orderDate",
                Operator = ReportConfigurationConstants.OperatorGte,
                Value = "2026-09-01",
            },
        };

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("日期报表", def));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var result = await ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);

        var workbook = OpenWorkbook(file.FileContents);
        var text = AllSheetText(workbook);
        Assert.Contains("日期报表", text);
        Assert.Contains("草稿（版本令牌 1）", text);
        Assert.Contains("查询筛选", text);
        Assert.Contains("≥", text);
        Assert.Contains("日期范围", text);
        Assert.Contains("2026-09-01 起", text);
        Assert.Contains(ReportConfigurationExcelExporter.UnknownValueText, text);
    }

    [Fact]
    public async Task Export_销售订单分组_币种分区_无全匹配合计()
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
        var result = await ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var file = Assert.IsType<FileContentResult>(result);

        var workbook = OpenWorkbook(file.FileContents);
        var groups = workbook.GetSheet(ReportConfigurationExcelExporter.GroupsSheetName);
        Assert.NotNull(groups);

        var currencies = new List<string>();
        for (var r = 1; r <= groups.LastRowNum; r++)
        {
            var cell = groups.GetRow(r)?.GetCell(1);
            if (cell?.CellType == CellType.String)
                currencies.Add(cell.StringCellValue);
        }
        Assert.Equal(2, currencies.Count);          // 仅两个币种分区行，无全匹配合计行
        Assert.Contains("USD", currencies);
        Assert.Contains("CNY", currencies);
        Assert.DoesNotContain("全匹配", AllSheetText(workbook));
    }

    [Fact]
    public async Task Export_无效修订_拒绝_不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-bad-rev", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, RevisionVersion = 99 }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void Build_计算列_上下文包含单位与未知值口径()
    {
        var preview = MixedPreview();
        preview.Columns.Add(new ReportConfigurationColumnDto(
            "doubleAmount", "双倍金额", ReportConfigurationConstants.TypeNumber, "原币金额",
            ReportConfigurationFormulaRules.UnknownReasonText, IsComputed: true));
        preview.ComputedColumns = new List<ReportConfigurationComputedColumnEvidenceDto>
        {
            new()
            {
                Key = "doubleAmount",
                Label = "双倍金额",
                Unit = "原币金额",
                UnknownReason = ReportConfigurationFormulaRules.UnknownReasonText,
                Dependencies = new List<string> { "totalAmount" },
            },
        };

        var workbook = OpenWorkbook(new ReportConfigurationExcelExporter().Build(preview));
        var text = AllSheetText(workbook);

        Assert.Contains("计算列口径", text);
        Assert.Contains("双倍金额", text);
        Assert.Contains(ReportConfigurationFormulaRules.UnknownReasonText, text);
        Assert.Contains("totalAmount", text);
    }
}
