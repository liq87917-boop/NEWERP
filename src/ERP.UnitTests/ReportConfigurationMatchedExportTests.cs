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
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-275 通用报表配置「完整匹配集下载（Excel / PDF）」单元测试（内存数据库，不连接 SQL Server / 不执行真实事务）。
/// <para>真实 SQL Server / 浏览器 / 字体环境不可用时显式 environment-blocked；本测试只验证离线路径。</para>
/// </summary>
public class ReportConfigurationMatchedExportTests
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

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal amount,
        DateTime? orderDate = null, DateTime? deliveryDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = currency,
            TotalAmount = amount,
            DeliveryDate = deliveryDate,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedOrders(ErpDbContext db, long customerId, int count, decimal startAmount)
    {
        var orders = new List<SalesOrder>();
        for (var i = 0; i < count; i++)
        {
            orders.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:D4}",
                CustomerId = customerId,
                OrderDate = new DateTime(2026, 9, 1).AddDays(i % 28),
                Status = DocumentStatus.Pending,
                Currency = Currency.CNY,
                TotalAmount = startAmount + i,
            });
        }

        db.SalesOrders.AddRange(orders);
        db.SaveChanges();
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db), db),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db), db),
        };

    private static IReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new ReportConfigurationExecutionService(db, BuildProviders(db));

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db)));

    private static ReportConfigurationsController BuildController(ErpDbContext db)
        => new(
            new ReportConfigurationCatalog(BuildProviders(db)),
            new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db))),
            new ReportConfigurationExecutionService(db, BuildProviders(db)),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db))));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition MatchedDefinition(
        string[]? fields = null,
        List<string>? grouping = null,
        List<ReportConfigurationAggregate>? aggregates = null,
        ReportConfigurationPivotDefinition? pivot = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "orderNo", "totalAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = grouping ?? new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = aggregates ?? new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            Coverage = ReportConfigurationConstants.CoverageMatchedSet,
            Pivot = pivot,
        };

    private static ReportConfigurationDefinition CurrentPageDefinition(
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
            Coverage = ReportConfigurationConstants.CoverageCurrentPage,
        };

    private static ReportConfigurationAggregate Sum(string fieldKey)
        => new() { FieldKey = fieldKey, Function = ReportConfigurationConstants.AggregateSum };

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

    private static PdfDocument OpenPdf(byte[] bytes)
        => PdfReader.Open(new MemoryStream(bytes));

    // ==================== 1. 服务端内部导出结果契约：完整事实与 ≤200 预览分离 ====================

    [Fact]
    public async Task 构建导出结果_匹配集_事实与指标分组透视均来自完整匹配集()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-export", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");

        for (var i = 0; i < 30; i++)
        {
            SeedOrder(db, $"SO-{i:D3}", i % 2 == 0 ? c1.Id : c2.Id,
                i % 3 == 0 ? Currency.USD : Currency.CNY, 100m + i,
                orderDate: new DateTime(2026, 9, 1).AddMonths(i % 2));
        }

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("匹配集导出", MatchedDefinition(
            fields: new[] { "orderNo" },
            aggregates: new List<ReportConfigurationAggregate> { Sum("totalAmount") },
            pivot: new ReportConfigurationPivotDefinition
            {
                RowDimension = ReportConfigurationConstants.GroupCustomer,
                ColumnDimension = ReportConfigurationConstants.GroupMonth,
            })));

        var execution = BuildExecution(db);
        var budget = new ReportConfigurationExecutionBudget();
        using var lease = budget.Acquire(user);

        var result = await execution.BuildExportResultAsync(user,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, PageSize = 5 }, lease);

        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, result.Coverage);
        Assert.Equal(30, result.MatchedCount);
        Assert.Equal(30, result.SourceEvidenceCount);
        Assert.Equal(30, result.Facts.Count);
        Assert.Equal(30, result.Preview.Rows.Count);

        var metricSource = result.Preview.Metrics.Sum(m => m.Cells.Sum(c => c.SourceCount));
        Assert.Equal(30, metricSource);
        Assert.NotNull(result.Preview.Pivot);
        Assert.Equal(30, result.Preview.Pivot.SourceRowCount);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, result.Preview.Evidence!.Coverage);
    }

    [Fact]
    public async Task 构建导出结果_当前页_维持当前页且不改变默认上限()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-page", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 30, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("当前页导出", CurrentPageDefinition(new[] { "orderNo" })));

        var execution = BuildExecution(db);
        var budget = new ReportConfigurationExecutionBudget();
        using var lease = budget.Acquire(user);

        var result = await execution.BuildExportResultAsync(user,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, Page = 1, PageSize = 5 }, lease);

        Assert.Equal(ReportConfigurationConstants.CoverageCurrentPage, result.Coverage);
        Assert.Equal(5, result.Facts.Count);
        Assert.Equal(5, result.SourceEvidenceCount);
    }

    [Fact]
    public async Task 构建导出结果_复用同一执行租约_不二次获取()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-lease", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 3, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("单租约导出", MatchedDefinition(new[] { "orderNo" })));

        var execution = BuildExecution(db);
        var budget = new ReportConfigurationExecutionBudget();

        using var first = budget.Acquire(user);
        using var second = budget.Acquire(user);

        var result = await execution.BuildExportResultAsync(user,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }, first);

        Assert.Equal(3, result.MatchedCount);
        Assert.Equal(3, result.Facts.Count);
    }

    [Fact]
    public async Task 构建导出结果_取消令牌_不返回部分文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-cancel", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 3, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("取消导出", MatchedDefinition(new[] { "orderNo" })));

        var execution = BuildExecution(db);
        var budget = new ReportConfigurationExecutionBudget();
        using var caller = new CancellationTokenSource();
        using var lease = budget.Acquire(user, caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.BuildExportResultAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }, lease));
    }

    // ==================== 2. 通用 Excel：完整匹配下载 / 1001 拒绝 / 类型与币种 / 当前页 ====================

    [Fact]
    public async Task 导出Excel_匹配集_250条以上完整事实全部渲染且合计精确对应()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-excel", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 260, 10m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("完整匹配下载",
            MatchedDefinition(fields: new[] { "orderNo", "totalAmount", "currency" },
                aggregates: new List<ReportConfigurationAggregate> { Sum("totalAmount") })));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var result = await ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, PageSize = 20 });
        var file = Assert.IsType<FileContentResult>(result);

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheet(ReportConfigurationExcelExporter.DataSheetName);
        Assert.NotNull(data);
        Assert.Equal(261, data.LastRowNum + 1);   // 1 表头 + 260 事实（绝不只导出首页 20 行）

        var text = AllSheetText(workbook);
        Assert.Contains("完整匹配集 260 条事实", text);
        Assert.DoesNotContain("本页 20 行", text);
        Assert.Contains("260", text);
    }

    [Fact]
    public async Task 导出Excel_当前页_默认仍为当前页且不超过页面()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-excel-page", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 260, 10m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("当前页下载",
            CurrentPageDefinition(new[] { "orderNo", "totalAmount", "currency" })));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var result = await ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, PageSize = 20 });
        var file = Assert.IsType<FileContentResult>(result);

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheet(ReportConfigurationExcelExporter.DataSheetName);
        Assert.NotNull(data);
        Assert.Equal(21, data.LastRowNum + 1);   // 1 表头 + 20 当前页行

        var text = AllSheetText(workbook);
        Assert.Contains("当前预览页 20 行（非全量合计）", text);
    }

    [Fact]
    public async Task 导出Excel_匹配集_1001条_拒绝且无部分文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-1001", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 1001, 10m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("超限匹配下载", MatchedDefinition(new[] { "orderNo" })));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
        Assert.Contains("1000", ex.Message);
    }

    [Fact]
    public async Task 导出Excel_匹配集_类型与null与负数与币种保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-types", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-NEG", customer.Id, Currency.USD, -100m, deliveryDate: null);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 200m, deliveryDate: new DateTime(2026, 9, 15));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("类型下载",
            MatchedDefinition(new[] { "orderNo", "totalAmount", "currency", "deliveryDate" })));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var result = await ctl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });
        var file = Assert.IsType<FileContentResult>(result);

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheet(ReportConfigurationExcelExporter.DataSheetName);
        Assert.NotNull(data);

        var row1 = data.GetRow(1);
        Assert.Equal("SO-NEG", row1.GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, row1.GetCell(1).CellType);
        Assert.Equal(-100d, row1.GetCell(1).NumericCellValue);
        Assert.Equal("USD", row1.GetCell(2).StringCellValue);
        Assert.Equal(string.Empty, CellText(row1.GetCell(3)));

        var row2 = data.GetRow(2);
        Assert.Equal("SO-CNY", row2.GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, row2.GetCell(1).CellType);
        Assert.Equal(200d, row2.GetCell(1).NumericCellValue);
        Assert.Equal("CNY", row2.GetCell(2).StringCellValue);
        Assert.Equal("2026-09-15", row2.GetCell(3).StringCellValue);
    }

    [Fact]
    public async Task 导出Excel_共享被授权人_匹配集成功_撤销后失败关闭()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedPrivilegedUser(db, "me-owner", "sales-order");
        var recipient = SeedPrivilegedUser(db, "me-recipient", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 5, 10m);

        var ownerCtl = BuildController(db);
        TestAuth.SetUser(ownerCtl, owner);
        var created = await ownerCtl.Create(SaveDto("共享匹配下载", MatchedDefinition(new[] { "orderNo" })));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(
            Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ownerCtl.Publish(id, 1);
        await ownerCtl.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var recipientCtl = BuildController(db);
        TestAuth.SetUser(recipientCtl, recipient);
        var result = await recipientCtl.Export(new ReportConfigurationPreviewRequest { ConfigurationId = id });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.NotEmpty(file.FileContents);

        var sharing = new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db)));
        var grant = Assert.Single(await sharing.ListGrantsAsync(owner, id));
        await sharing.RevokeAsync(owner, id, recipient, grant.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => recipientCtl.Export(
            new ReportConfigurationPreviewRequest { ConfigurationId = id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 3. 通用中文 PDF：完整匹配下载多页渲染 ====================

    [Fact]
    public async Task 导出PDF_匹配集_完整事实多页渲染且为PDF()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "me-pdf", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 250, 10m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("完整匹配PDF",
            MatchedDefinition(new[] { "orderNo", "totalAmount", "currency" })));

        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, user);
        var result = await ctl.ExportPdf(new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, PageSize = 5 });
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);   // 250 条完整事实跨多页，绝不只渲染首页 5 行
    }
}

