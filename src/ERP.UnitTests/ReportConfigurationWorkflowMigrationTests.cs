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
/// ERP-302 跟进提醒 / 报价成交率 / 业务员产值迁移为受控数据集适配器的单元测试：
/// 覆盖目录暴露与币种 / 单位口径、预览与既有报表服务逐行一致、分页 / 空页、撤销菜单立即收敛（fail closed）、
/// 未知字段 / 筛选 / 分页超限拒绝，以及金额 / 数量可空证据语义。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationWorkflowMigrationTests
{
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = name,
            IsSalesman = true,
            Status = 1,
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static CustomerFollowUp SeedFollowUp(
        ErpDbContext db, string followNo, long customerId, string customerName, string salesmanName, DateTime nextFollowDate)
    {
        var followUp = new CustomerFollowUp
        {
            FollowNo = followNo,
            FollowDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = customerName,
            FollowType = "电话",
            ContactPerson = "对接人",
            SalesmanId = 1,
            SalesmanName = salesmanName,
            Subject = followNo + " 主题",
            Content = "跟进内容",
            Result = "有意向",
            NextFollowDate = nextFollowDate,
            Remark = string.Empty,
        };
        db.CustomerFollowUps.Add(followUp);
        db.SaveChanges();
        return followUp;
    }

    private static Quotation SeedQuotation(
        ErpDbContext db, string quotationNo, long customerId, string salesmanName, Currency currency, decimal totalAmount)
    {
        var quotation = new Quotation
        {
            QuotationNo = quotationNo,
            QuotationDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "客户",
            SalesmanName = salesmanName,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Pending,
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }


    private static IReportService NewReportService(ErpDbContext db) => new ReportService(db);

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
    {
        var reportService = NewReportService(db);
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetFollowUpDue => new FollowUpDueReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetQuotationConversion => new QuotationConversionReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetSalesmanOutput => new SalesmanOutputReportConfigurationDatasetProvider(reportService, db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };
    }

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 200)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    private static string[] DefaultFieldsFor(string datasetKey) => datasetKey switch
    {
        ReportConfigurationConstants.DatasetFollowUpDue => new[]
        {
            "id", "followNo", "followDate", "customerId", "customerName", "followType", "contactPerson",
            "salesmanId", "salesmanName", "subject", "content", "result", "nextFollowDate", "dueDays", "dueStatus", "remark",
        },
        ReportConfigurationConstants.DatasetQuotationConversion => new[]
        {
            "salesmanName", "currency", "quotationCount", "convertedCount", "conversionRate",
            "expiredCount", "cancelledCount", "totalAmount", "convertedAmount", "avgConvertedAmount",
        },
        ReportConfigurationConstants.DatasetSalesmanOutput => new[]
        {
            "salesmanId", "salesmanName", "currency", "currencyLabel", "orderCount", "totalAmount",
            "totalProfit", "amountLabel", "currencyEvidence", "profitEvidence", "salesmanIdentityEvidence", "sourceEvidence",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
    };

    // ==================== 1. 目录暴露与币种 / 单位口径 ====================

    [Fact]
    public async Task 目录_跟进提醒_字段与菜单授权一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "catalog-follow", "follow-up-due");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetFollowUpDue);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal("follow-up-due", dataset!.RequiredMenuCode);
        Assert.Equal(16, dataset.Fields.Count);
        Assert.Contains(dataset.Fields, f => f.Key == "dueStatus" && f.Filterable);
        Assert.Contains(dataset.Fields, f => f.Key == "nextFollowDate" && f.Type == ReportConfigurationConstants.TypeDate);
    }

    [Fact]
    public async Task 目录_报价成交率_币种口径与筛选一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "catalog-quotation", "quotation");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetQuotationConversion);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal("quotation", dataset!.RequiredMenuCode);
        Assert.Contains(dataset.Fields, f => f.Key == "currency" && f.Filterable);
        Assert.Contains(dataset.Fields, f => f.Key == "totalAmount" && f.CurrencyUnit == "原币金额");
    }

    [Fact]
    public async Task 目录_业务员产值_未知利润与来源依据一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "catalog-salesman", "salesman-output");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetSalesmanOutput);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal("salesman-output", dataset!.RequiredMenuCode);
        Assert.Contains(dataset.Fields, f => f.Key == "totalProfit" && f.CurrencyUnit == "原币金额");
        Assert.Contains(dataset.Fields, f => f.Key == "sourceEvidence");
    }


    // ==================== 2. 预览与既有报表逐行一致 ====================

    [Fact]
    public async Task 跟进提醒_预览与既有报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "follow-compare", "follow-up-due");
        var customer = SeedCustomer(db, "C-1", "客户甲");
        SeedFollowUp(db, "FU-1", customer.Id, "客户甲", "张三", DateTime.Today.AddDays(-2));
        SeedFollowUp(db, "FU-2", customer.Id, "客户甲", "李四", DateTime.Today);
        SeedFollowUp(db, "FU-3", customer.Id, "客户甲", "王五", DateTime.Today.AddDays(3));

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetFollowUpDue);
        var reportService = NewReportService(db);
        var legacy = await reportService.GetFollowUpDueAsync(DateTime.Today, 7, PrivilegedScope);
        var fields = new[] { "customerName", "salesmanName", "followDate", "result", "nextFollowDate", "dueDays", "dueStatus", "subject" };
        var preview = await provider.PreviewAsync(Definition(ReportConfigurationConstants.DatasetFollowUpDue, fields), Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var legacyRow in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["salesmanName"]! == legacyRow.SalesmanName);
            Assert.Equal(legacyRow.CustomerName, (string)row["customerName"]!);
            Assert.Equal(legacyRow.DueDays, (int)row["dueDays"]!);
            Assert.Equal(legacyRow.DueStatus, (string)row["dueStatus"]!);
            Assert.Equal(legacyRow.Subject, (string)row["subject"]!);
        }
    }

    [Fact]
    public async Task 报价成交率_预览与既有报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "quotation-compare", "quotation");
        var customer = SeedCustomer(db, "C-1", "客户甲");
        SeedQuotation(db, "QT-1", customer.Id, "张三", Currency.USD, 100m);
        SeedQuotation(db, "QT-2", customer.Id, "张三", Currency.EUR, 200m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetQuotationConversion);
        var reportService = NewReportService(db);
        var legacy = await reportService.GetQuotationConversionAsync(DateTime.Today, DateTime.Today, PrivilegedScope);
        var fields = new[] { "salesmanName", "currency", "quotationCount", "convertedCount", "conversionRate", "totalAmount", "convertedAmount" };
        var preview = await provider.PreviewAsync(Definition(ReportConfigurationConstants.DatasetQuotationConversion, fields), Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var legacyRow in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.SalesmanName, (string)row["salesmanName"]!);
            Assert.Equal(legacyRow.QuotationCount, (int)row["quotationCount"]!);
            Assert.Equal(legacyRow.TotalAmount, (decimal)row["totalAmount"]!);
        }
    }


    [Fact]
    public async Task 业务员产值_预览与既有报表逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "salesman-compare", "salesman-output");
        var customer = SeedCustomer(db, "C-1", "客户甲");
        var employee = SeedEmployee(db, "E-1", "张三");
        SeedOrder(db, "SO-1", customer.Id, employee.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, employee.Id, Currency.EUR, 200m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetSalesmanOutput);
        var reportService = NewReportService(db);
        var legacy = await reportService.GetSalesmanOutputAsync(DateTime.Today, DateTime.Today, PrivilegedScope);
        var fields = new[] { "salesmanId", "salesmanName", "currency", "orderCount", "totalAmount", "totalProfit" };
        var preview = await provider.PreviewAsync(Definition(ReportConfigurationConstants.DatasetSalesmanOutput, fields), Params(), user.Id);

        Assert.Equal(legacy.Count, preview.Total);
        Assert.Equal(legacy.Count, preview.Rows.Count);
        foreach (var legacyRow in legacy)
        {
            var row = Assert.Single(preview.Rows, r => (string)r["currency"]! == legacyRow.Currency);
            Assert.Equal(legacyRow.SalesmanId, (long)row["salesmanId"]!);
            Assert.Equal(legacyRow.OrderCount, (int)row["orderCount"]!);
            Assert.Equal(legacyRow.TotalAmount, (decimal?)row["totalAmount"]);
        }
    }

    [Fact]
    public async Task 业务员产值_未知币种金额与利润为null()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "salesman-unknown", "salesman-output");
        var customer = SeedCustomer(db, "C-1", "客户甲");
        var employee = SeedEmployee(db, "E-1", "张三");
        SeedOrder(db, "SO-1", customer.Id, employee.Id, (Currency)999, 50m);

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetSalesmanOutput);
        var reportService = NewReportService(db);
        var legacy = await reportService.GetSalesmanOutputAsync(DateTime.Today, DateTime.Today, PrivilegedScope);
        var unknownItem = Assert.Single(legacy, x => x.TotalAmount is null);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetSalesmanOutput, "salesmanId", "currency", "totalAmount", "totalProfit"),
            Params(), user.Id);

        var row = Assert.Single(preview.Rows, r => (string)r["currency"]! == unknownItem.Currency);
        Assert.Null(row["totalAmount"]);
        Assert.Null(row["totalProfit"]);
    }

    // ==================== 3. 分页 / 空页 ====================

    [Fact]
    public async Task 跟进提醒_分页_每页一条()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "follow-paging", "follow-up-due");
        var customer = SeedCustomer(db, "C-1", "客户甲");
        SeedFollowUp(db, "FU-1", customer.Id, "客户甲", "张三", DateTime.Today.AddDays(-1));
        SeedFollowUp(db, "FU-2", customer.Id, "客户甲", "李四", DateTime.Today);
        SeedFollowUp(db, "FU-3", customer.Id, "客户甲", "王五", DateTime.Today.AddDays(1));

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetFollowUpDue);
        var page1 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetFollowUpDue, "customerName", "salesmanName"),
            Params(page: 1, pageSize: 1), user.Id);
        var page2 = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetFollowUpDue, "customerName", "salesmanName"),
            Params(page: 2, pageSize: 1), user.Id);

        Assert.Equal(3, page1.Total);
        Assert.Single(page1.Rows);
        Assert.Equal(2, page2.Page);
        Assert.Single(page2.Rows);
    }

    [Fact]
    public async Task 报价成交率_无数据_空页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "quotation-empty", "quotation");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetQuotationConversion);

        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetQuotationConversion, "salesmanName", "currency"),
            Params(), user.Id);

        Assert.Equal(0, preview.Total);
        Assert.Empty(preview.Rows);
    }


    // ==================== 4. 菜单撤销立即收敛（fail closed） ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetFollowUpDue, "follow-up-due")]
    [InlineData(ReportConfigurationConstants.DatasetQuotationConversion, "quotation")]
    [InlineData(ReportConfigurationConstants.DatasetSalesmanOutput, "salesman-output")]
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
            Definition(datasetKey, DefaultFieldsFor(datasetKey)),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetQuotationConversion);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetQuotationConversion, "currency"),
            Params(), null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 5. 未知字段 / 筛选 / 分页超限拒绝 ====================

    [Fact]
    public async Task 未知字段与未知筛选_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unknown", "quotation");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetQuotationConversion);

        var exField = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetQuotationConversion, "doesNotExist"),
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exField.Code);

        var exFilter = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            new ReportConfigurationDefinition
            {
                SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
                DatasetKey = ReportConfigurationConstants.DatasetQuotationConversion,
                Fields = new List<string> { "currency" },
                Filters = new List<ReportConfigurationFilter>
                {
                    new() { FieldKey = "doesNotExist", Operator = "eq", Value = "USD" },
                },
            },
            Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exFilter.Code);
    }

    [Fact]
    public async Task 分页超限_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "page-over", "follow-up-due");
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetFollowUpDue);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetFollowUpDue, "customerName"),
            Params(page: 1, pageSize: 201), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 迁移预设登记（覆盖固定与动态登记册条目） ====================

    [Theory]
    [InlineData("report:follow-up-due")]
    [InlineData("dynamic:follow-up-due")]
    [InlineData("report:quotation-conversion")]
    [InlineData("dynamic:quotation-conversion")]
    [InlineData("report:salesman-output")]
    [InlineData("dynamic:salesman-output")]
    public async Task 迁移预设_六个登记册条目均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();

        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }
}

