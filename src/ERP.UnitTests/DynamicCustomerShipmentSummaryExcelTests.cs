using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-233 动态客户出货量证据报表「全匹配汇总 Excel」导出（只读、有界、作用域化）聚焦单元测试。
/// 覆盖：独立汇总导出端点（无需先预览、与详情页 / 选定列无关、每次重新校验身份 / 菜单授权 / 数据范围 / 字段 / 日期 / 分页 / 筛选）、
/// 「原币金额汇总」「精确单位数量汇总」两张数据工作表 +「报表口径」上下文工作表（日期 / 来源上限 / 覆盖范围 / 来源证据 / 未知口径 / 完整度 / 原币证据 / 只读）、
/// 已知金额 / 数量 / 计数为数值、未知显式「未知」（绝不写成 0）、单位行绝不携带金额、绝不跨币种 / 跨单位合计、绝无总计行、绝不含客户明细行、
/// 稳定分组顺序、空证据仍产出两张汇总表、越界详情页仍导出全部匹配汇总、公式前导标签转义、授权 / 无效输入拒绝、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicCustomerShipmentSummaryExcelTests
{
    private const string MenuCode = "customer-shipment";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(
        ErpDbContext db, long orderId, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = 1,
            ProductName = "商品",
            Quantity = quantity,
            Unit = unit,
            IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static DynamicCustomerShipmentReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static FileContentResult ExportOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        return file;
    }

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new XSSFWorkbook(ms);
    }

    /// <summary>读取表头列名 → 列索引映射，避免对列位置硬编码。</summary>
    private static Dictionary<string, int> HeaderMap(ISheet sheet)
    {
        var header = sheet.GetRow(0);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var c = 0; c < header.LastCellNum; c++)
        {
            var cell = header.GetCell(c);
            if (cell is not null && !string.IsNullOrEmpty(cell.StringCellValue))
                map[cell.StringCellValue] = c;
        }
        return map;
    }

    private static List<IRow> DataRows(ISheet sheet)
    {
        var rows = new List<IRow>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }

    // ==================== 1. 纯规则：汇总导出行 ====================

    [Fact]
    public void 汇总导出行_已知币种金额与计数为数值_未知币种金额显式未知_键与列白名单一致()
    {
        var known = DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow(
            new DynamicCustomerShipmentCurrencySummaryDto
            {
                Currency = "USD",
                CurrencyLabel = "USD 美元",
                CustomerCount = 2,
                OrderCount = 3,
                TotalAmount = 300m,
                Evidence = CustomerShipmentEvidenceRules.KnownCurrencyEvidence
            });
        Assert.Equal("USD", known["currency"]);
        Assert.Equal("USD 美元", known["currencyLabel"]);
        Assert.Equal(2, known["customerCount"]);
        Assert.Equal(3, known["orderCount"]);
        Assert.Equal(300m, known["totalAmount"]);
        Assert.Equal(CustomerShipmentEvidenceRules.KnownCurrencyEvidence, known["evidence"]);

        var unknown = DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow(
            new DynamicCustomerShipmentCurrencySummaryDto
            {
                Currency = CustomerShipmentEvidenceRules.UnknownCurrencyGroup,
                CurrencyLabel = CustomerShipmentEvidenceRules.UnknownCurrencyGroup,
                CustomerCount = 1,
                OrderCount = 4,
                TotalAmount = null,
                Evidence = CustomerShipmentEvidenceRules.UnknownCurrencyEvidence
            });
        Assert.Equal(DynamicCustomerShipmentReportRules.UnknownValueText, unknown["totalAmount"]);
        Assert.Equal(4, unknown["orderCount"]);

        Assert.Equal(
            new[] { "currency", "currencyLabel", "customerCount", "orderCount", "totalAmount", "evidence" },
            known.Keys.ToArray());
    }

    [Fact]
    public void 汇总导出行_单位行已知数量为数值_未知单位数量显式未知_绝不携带金额()
    {
        var known = DynamicCustomerShipmentReportRules.BuildUnitSummaryExportRow(
            new DynamicCustomerShipmentUnitSummaryDto
            {
                Currency = "USD",
                Unit = "PCS",
                Quantity = 10m,
                DetailCount = 2,
                QuantityLabel = CustomerShipmentEvidenceRules.KnownUnitQuantityLabel
            });
        Assert.Equal("USD", known["currency"]);
        Assert.Equal("PCS", known["unit"]);
        Assert.Equal(10m, known["quantity"]);
        Assert.Equal(2, known["detailCount"]);

        var unknown = DynamicCustomerShipmentReportRules.BuildUnitSummaryExportRow(
            new DynamicCustomerShipmentUnitSummaryDto
            {
                Currency = "USD",
                Unit = CustomerShipmentEvidenceRules.UnknownUnitGroup,
                Quantity = null,
                DetailCount = 5,
                QuantityLabel = CustomerShipmentEvidenceRules.UnknownUnitQuantityLabel
            });
        Assert.Equal(DynamicCustomerShipmentReportRules.UnknownValueText, unknown["quantity"]);
        Assert.Equal(5, unknown["detailCount"]);

        Assert.Equal(
            new[] { "currency", "unit", "quantity", "detailCount", "quantityLabel" },
            known.Keys.ToArray());
        Assert.DoesNotContain(known.Keys, k => k.Contains("Amount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 汇总导出行_公式前导币种键标签原始单位未知原因转义为字面文本()
    {
        var currency = DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow(
            new DynamicCustomerShipmentCurrencySummaryDto
            {
                Currency = "=cmd()",
                CurrencyLabel = "+label",
                CustomerCount = 1,
                OrderCount = 2,
                TotalAmount = 3m,
                Evidence = "-evidence"
            });
        Assert.Equal("'=cmd()", currency["currency"]);
        Assert.Equal("'+label", currency["currencyLabel"]);
        Assert.Equal("'-evidence", currency["evidence"]);

        var unit = DynamicCustomerShipmentReportRules.BuildUnitSummaryExportRow(
            new DynamicCustomerShipmentUnitSummaryDto
            {
                Currency = "=cur()",
                Unit = "+PCS",
                Quantity = null,
                DetailCount = 4,
                QuantityLabel = "-label"
            });
        Assert.Equal("'=cur()", unit["currency"]);
        Assert.Equal("'+PCS", unit["unit"]);
        Assert.Equal("'-label", unit["quantityLabel"]);
    }


    // ==================== 2. 导出端点：工作簿结构与类型化单元格 ====================

    [Fact]
    public async Task ExportSummary_返回xlsx附件_两个汇总数据表与口径表_绝不含客户明细行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedDetail(db, order.Id, "PCS", 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(3, workbook.NumberOfSheets);
        Assert.Equal(DynamicCustomerShipmentReportRules.SummaryCurrencySheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicCustomerShipmentReportRules.SummaryUnitSheetName, workbook.GetSheetAt(1).SheetName);
        Assert.Equal(DynamicCustomerShipmentReportRules.ContextSheetName, workbook.GetSheetAt(2).SheetName);

        // 明确区分于「当前页明细导出」的「客户出货量统计表」数据工作表
        Assert.DoesNotContain(
            Enumerable.Range(0, workbook.NumberOfSheets),
            i => workbook.GetSheetAt(i).SheetName == DynamicCustomerShipmentReportRules.RequiredMenuText);
    }

    [Fact]
    public async Task ExportSummary_已知金额数量计数为数值_未知金额数量显式未知()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var usd1 = SeedOrder(db, "SO-USD-1", customer.Id, Currency.USD, 100m);
        SeedDetail(db, usd1.Id, "PCS", 10m);
        SeedDetail(db, usd1.Id, "PCS", -2m);
        SeedOrder(db, "SO-USD-2", customer.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-UNK", customer.Id, (Currency)999, 777m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);

        // 原币金额汇总：USD 金额为数值、未知币种金额为显式文本「未知」
        var currencySheet = workbook.GetSheetAt(0);
        var currencyHeader = HeaderMap(currencySheet);
        Assert.Equal(new[] { "原币币种", "原币币种标签", "客户数", "已审核订单数", "原币金额合计", "金额证据" },
            currencyHeader.Keys.OrderBy(k => currencyHeader[k]).ToArray());

        var usdRow = DataRows(currencySheet).Single(r => r.GetCell(currencyHeader["原币币种"]).StringCellValue == "USD");
        Assert.Equal("USD 美元", usdRow.GetCell(currencyHeader["原币币种标签"]).StringCellValue);
        Assert.Equal(CellType.Numeric, usdRow.GetCell(currencyHeader["客户数"]).CellType);
        Assert.Equal(1d, usdRow.GetCell(currencyHeader["客户数"]).NumericCellValue);
        Assert.Equal(CellType.Numeric, usdRow.GetCell(currencyHeader["已审核订单数"]).CellType);
        Assert.Equal(2d, usdRow.GetCell(currencyHeader["已审核订单数"]).NumericCellValue);
        Assert.Equal(CellType.Numeric, usdRow.GetCell(currencyHeader["原币金额合计"]).CellType);
        Assert.Equal(300d, usdRow.GetCell(currencyHeader["原币金额合计"]).NumericCellValue, 2);

        var unknownRow = DataRows(currencySheet).Single(r => r.GetCell(currencyHeader["原币币种"]).StringCellValue == "未知币种");
        Assert.Equal(CellType.String, unknownRow.GetCell(currencyHeader["原币金额合计"]).CellType);
        Assert.Equal("未知", unknownRow.GetCell(currencyHeader["原币金额合计"]).StringCellValue);

        // 精确单位数量汇总：已知单位数量为数值（10 + -2 = 8）
        var unitSheet = workbook.GetSheetAt(1);
        var unitHeader = HeaderMap(unitSheet);
        Assert.Equal(new[] { "原币币种", "精确单位", "数量合计", "明细条数", "数量证据" },
            unitHeader.Keys.OrderBy(k => unitHeader[k]).ToArray());

        var pcsRow = DataRows(unitSheet).Single(r => r.GetCell(unitHeader["原币币种"]).StringCellValue == "USD");
        Assert.Equal("PCS", pcsRow.GetCell(unitHeader["精确单位"]).StringCellValue);
        Assert.Equal(CellType.Numeric, pcsRow.GetCell(unitHeader["数量合计"]).CellType);
        Assert.Equal(8d, pcsRow.GetCell(unitHeader["数量合计"]).NumericCellValue, 2);
        Assert.Equal(CellType.Numeric, pcsRow.GetCell(unitHeader["明细条数"]).CellType);
        Assert.Equal(2d, pcsRow.GetCell(unitHeader["明细条数"]).NumericCellValue);
    }


    [Fact]
    public async Task ExportSummary_单位行绝不含金额_绝不跨币种跨单位合计_无总计行()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var usd = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedDetail(db, usd.Id, "PCS", 10m);
        var eur = SeedOrder(db, "SO-EUR", customer.Id, Currency.EUR, 500m);
        SeedDetail(db, eur.Id, "BOX", 3m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);

        var unitSheet = workbook.GetSheetAt(1);
        var unitHeader = HeaderMap(unitSheet);
        Assert.DoesNotContain(unitHeader.Keys, k => k.Contains("金额") || k.Contains("Amount", StringComparison.OrdinalIgnoreCase));

        // 「数量合计」是每个精确单位的独立数量小计（绝不跨单位），不视为跨币种 / 跨单位总计行；
        // 仅断言没有任何「总计」列，且数据行中不存在「合计 / 总计」分组键
        Assert.DoesNotContain(unitHeader.Keys, k => k.Contains("总计"));
        Assert.DoesNotContain(
            DataRows(unitSheet),
            r => r.GetCell(unitHeader["精确单位"]).StringCellValue.Contains("合计")
              || r.GetCell(unitHeader["精确单位"]).StringCellValue.Contains("总计"));

        var unitCurrencies = DataRows(unitSheet)
            .Select(r => r.GetCell(unitHeader["原币币种"]).StringCellValue)
            .ToList();
        Assert.Equal(new[] { "EUR", "USD" }, unitCurrencies);

        var currencySheet = workbook.GetSheetAt(0);
        var currencyHeader = HeaderMap(currencySheet);
        Assert.DoesNotContain(currencyHeader.Keys, k => k.Contains("总计"));
        Assert.DoesNotContain(
            DataRows(currencySheet),
            r => r.GetCell(currencyHeader["原币币种"]).StringCellValue.Contains("合计")
              || r.GetCell(currencyHeader["原币币种"]).StringCellValue.Contains("总计"));

        var currencyCurrencies = DataRows(currencySheet)
            .Select(r => r.GetCell(currencyHeader["原币币种"]).StringCellValue)
            .ToList();
        Assert.Equal(new[] { "EUR", "USD" }, currencyCurrencies);
    }


    [Fact]
    public async Task ExportSummary_上下文_日期来源上限覆盖来源未知完整度原币证据只读()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedDetail(db, order.Id, "PCS", 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(2);

        var byLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var r = 0; r <= ctx.LastRowNum; r++)
        {
            var row = ctx.GetRow(r);
            if (row is null) continue;
            byLabel[row.GetCell(0).StringCellValue] = row.GetCell(1)?.StringCellValue ?? string.Empty;
        }

        Assert.Equal("2026-09-01", byLabel[DynamicCustomerShipmentReportRules.ContextStartLabel]);
        Assert.Equal("2026-09-30", byLabel[DynamicCustomerShipmentReportRules.ContextEndLabel]);
        Assert.Contains("500 张订单", byLabel[DynamicCustomerShipmentReportRules.ContextSourceLimitLabel]);
        Assert.Contains("全部匹配", byLabel[DynamicCustomerShipmentReportRules.ContextCoverageLabel]);
        Assert.Contains("非实际出库", byLabel[DynamicCustomerShipmentReportRules.ContextSourceLabel]);
        Assert.Contains("绝不回落为 0", byLabel[DynamicCustomerShipmentReportRules.ContextUnknownLabel]);
        Assert.Contains("数量证据完整", byLabel[DynamicCustomerShipmentReportRules.ContextCompletenessLabel]);
        Assert.Contains("无任何跨币种金额总计", byLabel[DynamicCustomerShipmentReportRules.ContextCurrencyEvidenceLabel]);
        Assert.Equal(DynamicCustomerShipmentReportRules.ReadOnlyText, byLabel[DynamicCustomerShipmentReportRules.ContextReadOnlyLabel]);
    }

    [Fact]
    public async Task ExportSummary_应用筛选_上下文可见规范化筛选()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End,
            Filter = new CustomerShipmentFilterDto { CustomerId = customer.Id, Currency = "usd" }
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(2);

        var filterRow = Enumerable.Range(0, ctx.LastRowNum + 1)
            .Select(r => ctx.GetRow(r))
            .First(r => r is not null && r.GetCell(0).StringCellValue == DynamicCustomerShipmentReportRules.ContextFilterLabel);
        Assert.Contains("客户 Id " + customer.Id, filterRow.GetCell(1).StringCellValue);
        Assert.Contains("原币币种 USD", filterRow.GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task ExportSummary_空证据_两个汇总表仍存在_口径表标注空说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(3, workbook.NumberOfSheets);

        Assert.Equal(0, workbook.GetSheetAt(0).LastRowNum);   // 仅表头，无数据行
        Assert.Equal(0, workbook.GetSheetAt(1).LastRowNum);

        var ctx = workbook.GetSheetAt(2);
        var emptyRow = Enumerable.Range(0, ctx.LastRowNum + 1)
            .Select(r => ctx.GetRow(r))
            .First(r => r is not null && r.GetCell(0).StringCellValue == DynamicCustomerShipmentReportRules.ContextEmptyLabel);
        Assert.Equal(DynamicCustomerShipmentReportRules.EmptyText, emptyRow.GetCell(1).StringCellValue);
    }


    [Fact]
    public async Task ExportSummary_越界详情页_仍导出全部匹配汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var a = SeedCustomer(db, "C-A", "客户A");
        SeedOrder(db, "SO-USD-A", a.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY-A", a.Id, Currency.CNY, 500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End,
            Page = 99,
            PageSize = 1
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var currencySheet = workbook.GetSheetAt(0);
        var header = HeaderMap(currencySheet);

        var currencies = DataRows(currencySheet)
            .Select(r => r.GetCell(header["原币币种"]).StringCellValue)
            .ToList();
        Assert.Equal(new[] { "CNY", "USD" }, currencies);     // 越界页仍覆盖全部匹配行
    }

    [Fact]
    public async Task ExportSummary_未知字段或页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End, Fields = new List<string> { "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End, PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task ExportSummary_无菜单授权或无身份_拒绝_不返回文件()
    {
        using var db = TestDbFactory.Create();

        // 无菜单授权（角色未挂载 customer-shipment 菜单）
        var role = SeedRole(db, "NoMenu", isSystem: false);
        var user = SeedUser(db, "no-menu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        var forbidden = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummary(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Code);

        // 无身份
        var ctl2 = NewController(db);
        TestAuth.SetUser(ctl2, null);
        var unauthorized = await Assert.ThrowsAsync<BusinessException>(() => ctl2.ExportSummary(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, unauthorized.Code);
    }

    [Fact]
    public async Task ExportSummary_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedDetail(db, order.Id, "PCS", 10m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = ExportOk(await ctl.ExportSummary(new DynamicCustomerShipmentReportRequest
        {
            Start = Start,
            End = End
        }));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

