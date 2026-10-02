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
/// ERP-270 通用报表配置「保存排序先于源分页」单元测试（内存数据库，不连接 SQL Server / 不执行 SQL）：
/// 覆盖目录只暴露有限原生持久化排序字段并说明不可排序原因、纯规则拒绝非法 / 未选择 / 不可排序字段、
/// 两个夹具适配器把有限类型化 LINQ 排序应用到 Skip/Take 之前（含不可变身份并列决断、默认口径、
/// 多页一致、受限制业务员范围先于排序），以及非法排序在源读取前 fail closed。
/// </summary>
public class ReportConfigurationSortingTests
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

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static long SeedPrivilegedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}-{Guid.NewGuid():N}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
        return user.Id;
    }

    private static long SeedRestrictedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}-{Guid.NewGuid():N}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
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

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", DateTime? invoiceDate = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = invoiceDate ?? new DateTime(2026, 8, 20),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            NetAmount = grossAmount * 0.9m,
            TaxAmount = grossAmount * 0.1m,
            GrossAmount = grossAmount,
            Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
            IsDeleted = false,
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
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

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition SalesOrderDefinition(
        string[]? fields = null, string? sortFieldKey = null, string? sortDirection = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "orderNo", "orderDate", "customerId" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            Presentation = new ReportConfigurationPresentation
            {
                Page = 1,
                PageSize = 20,
                SortFieldKey = sortFieldKey,
                SortDirection = sortDirection,
            },
        };

    private static ReportConfigurationDefinition ReceivableDefinition(
        string[]? fields = null, string? sortFieldKey = null, string? sortDirection = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetReceivable,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "invoiceNumber", "invoiceDate", "customerId" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            Presentation = new ReportConfigurationPresentation
            {
                Page = 1,
                PageSize = 20,
                SortFieldKey = sortFieldKey,
                SortDirection = sortDirection,
            },
        };

    private static List<string> OrderNos(ReportConfigurationPreviewDto preview)
        => preview.Rows.Select(r => r.TryGetValue("orderNo", out var v) ? Convert.ToString(v) ?? string.Empty : string.Empty).ToList();

    private static List<string> InvoiceNumbers(ReportConfigurationPreviewDto preview)
        => preview.Rows.Select(r => r.TryGetValue("invoiceNumber", out var v) ? Convert.ToString(v) ?? string.Empty : string.Empty).ToList();

    // ==================== 1. 目录：有限可排序字段与不可排序原因 ====================

    [Fact]
    public async Task Catalog_销售订单_仅原生持久化字段可排序并说明原因()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-sort-catalog", "sales-order");
        var catalog = new ReportConfigurationCatalog(BuildProviders(db));

        var result = await catalog.GetCatalogAsync(user);
        var dataset = Assert.Single(result.Datasets);

        Assert.True(Assert.Single(dataset.Fields, f => f.Key == "orderDate").Sortable);
        Assert.True(Assert.Single(dataset.Fields, f => f.Key == "customerId").Sortable);
        Assert.True(Assert.Single(dataset.Fields, f => f.Key == "id").Sortable);

        Assert.False(Assert.Single(dataset.Fields, f => f.Key == "totalAmount").Sortable);
        Assert.False(Assert.Single(dataset.Fields, f => f.Key == "currency").Sortable);
        Assert.False(Assert.Single(dataset.Fields, f => f.Key == "orderNo").Sortable);
        Assert.False(string.IsNullOrWhiteSpace(dataset.SortingExplanation));
    }

    [Fact]
    public async Task Catalog_应收账款_仅原生持久化字段可排序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ar-sort-catalog", "customer");
        var catalog = new ReportConfigurationCatalog(BuildProviders(db));

        var result = await catalog.GetCatalogAsync(user);
        var dataset = Assert.Single(result.Datasets);

        Assert.True(Assert.Single(dataset.Fields, f => f.Key == "invoiceDate").Sortable);
        Assert.True(Assert.Single(dataset.Fields, f => f.Key == "customerId").Sortable);
        Assert.True(Assert.Single(dataset.Fields, f => f.Key == "invoiceId").Sortable);
        Assert.False(Assert.Single(dataset.Fields, f => f.Key == "grossAmount").Sortable);
        Assert.False(string.IsNullOrWhiteSpace(dataset.SortingExplanation));
    }

    // ==================== 2. 销售订单适配器：源侧排序 / 并列 / 多页 / 默认 ====================

    [Fact]
    public async Task PreviewAsync_销售订单_按订单日期升序_多页一致且并列按Id稳定()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-sort-date", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");
        var c3 = SeedCustomer(db, "C3", "客户三");
        var c4 = SeedCustomer(db, "C4", "客户四");

        // 两个 01-10（并列，期望按 Id 升序：A 先于 B）；其余日期互异
        SeedOrder(db, "SO-A", c2.Id, Currency.USD, 10m, new DateTime(2026, 1, 10));
        SeedOrder(db, "SO-B", c1.Id, Currency.USD, 10m, new DateTime(2026, 1, 10));
        SeedOrder(db, "SO-C", c3.Id, Currency.USD, 10m, new DateTime(2026, 1, 5));
        SeedOrder(db, "SO-D", c4.Id, Currency.USD, 10m, new DateTime(2026, 1, 20));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("日期升序", SalesOrderDefinition(
            fields: new[] { "orderNo", "orderDate", "customerId" },
            sortFieldKey: "orderDate",
            sortDirection: "asc")));

        var execution = BuildExecution(db);
        var page1 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 2,
        });
        var page2 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 2,
            PageSize = 2,
        });

        Assert.Equal(new[] { "SO-C", "SO-A" }, OrderNos(page1));
        Assert.Equal(new[] { "SO-B", "SO-D" }, OrderNos(page2));
        Assert.Equal(4, page1.Total);
        Assert.Equal("orderDate", page1.SortFieldKey);
        Assert.Equal("asc", page1.SortDirection);
        Assert.Contains("排序", page1.SortEvidence);
    }

    [Fact]
    public async Task PreviewAsync_销售订单_按客户降序_并列按Id升序稳定()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-sort-customer", "sales-order");
        var cA = SeedCustomer(db, "CA", "客户A");
        var cB = SeedCustomer(db, "CB", "客户B");
        var cC = SeedCustomer(db, "CC", "客户C");

        SeedOrder(db, "SO-B2", cB.Id, Currency.USD, 10m);
        SeedOrder(db, "SO-A1", cA.Id, Currency.USD, 10m);
        SeedOrder(db, "SO-B1", cB.Id, Currency.USD, 10m);
        SeedOrder(db, "SO-C1", cC.Id, Currency.USD, 10m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("客户降序", SalesOrderDefinition(
            fields: new[] { "orderNo", "orderDate", "customerId" },
            sortFieldKey: "customerId",
            sortDirection: "desc")));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 10,
        });

        Assert.Equal(new[] { "SO-C1", "SO-B2", "SO-B1", "SO-A1" }, OrderNos(preview));
        Assert.Equal("desc", preview.SortDirection);
    }

    [Fact]
    public async Task PreviewAsync_销售订单_无排序_默认按订单Id升序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-sort-default", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");

        SeedOrder(db, "SO-3", c1.Id, Currency.USD, 10m);
        SeedOrder(db, "SO-1", c1.Id, Currency.USD, 10m);
        SeedOrder(db, "SO-2", c1.Id, Currency.USD, 10m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("默认排序", SalesOrderDefinition(
            fields: new[] { "orderNo", "orderDate", "customerId" })));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 10,
        });

        Assert.Equal(new[] { "SO-3", "SO-1", "SO-2" }, OrderNos(preview));
        Assert.Null(preview.SortFieldKey);
        Assert.Null(preview.SortDirection);
    }

    [Fact]
    public async Task PreviewAsync_销售订单_非法排序字段_源读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-sort-invalid", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", c1.Id, Currency.USD, 10m);

        var service = BuildService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.CreateAsync(
            user,
            SaveDto("非法排序", SalesOrderDefinition(
                fields: new[] { "orderNo", "totalAmount" },
                sortFieldKey: "totalAmount",
                sortDirection: "asc"))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 3. 应收账款适配器：源侧排序 / 默认口径 ====================

    [Fact]
    public async Task PreviewAsync_应收账款_按开票日期升序_多页一致且并列按Id稳定()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ar-sort-date", "customer");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");

        SeedInvoice(db, "INV-A", c2.Id, 100m, "USD", new DateTime(2026, 3, 10));
        SeedInvoice(db, "INV-B", c1.Id, 100m, "USD", new DateTime(2026, 3, 10));
        SeedInvoice(db, "INV-C", c1.Id, 100m, "USD", new DateTime(2026, 3, 5));
        SeedInvoice(db, "INV-D", c2.Id, 100m, "USD", new DateTime(2026, 3, 20));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("发票日期升序", ReceivableDefinition(
            fields: new[] { "invoiceNumber", "invoiceDate", "customerId" },
            sortFieldKey: "invoiceDate",
            sortDirection: "asc")));

        var execution = BuildExecution(db);
        var page1 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 2,
        });
        var page2 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 2,
            PageSize = 2,
        });

        Assert.Equal(new[] { "INV-C", "INV-A" }, InvoiceNumbers(page1));
        Assert.Equal(new[] { "INV-B", "INV-D" }, InvoiceNumbers(page2));
        Assert.Equal(4, page1.Total);
        Assert.Equal("invoiceDate", page1.SortFieldKey);
        Assert.Contains("排序", page1.SortEvidence);
    }

    [Fact]
    public async Task PreviewAsync_应收账款_无排序_保持既有默认口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ar-sort-default", "customer");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");

        // 既有默认口径：客户升序 → 币种升序 → 开票日期降序 → Id 降序
        SeedInvoice(db, "INV-1", c2.Id, 100m, "USD", new DateTime(2026, 1, 1));
        SeedInvoice(db, "INV-2", c1.Id, 100m, "USD", new DateTime(2026, 1, 2));
        SeedInvoice(db, "INV-3", c1.Id, 100m, "USD", new DateTime(2026, 1, 3));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("应收默认", ReceivableDefinition(
            fields: new[] { "invoiceNumber", "invoiceDate", "customerId" })));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 10,
        });

        Assert.Equal(new[] { "INV-3", "INV-2", "INV-1" }, InvoiceNumbers(preview));
        Assert.Null(preview.SortFieldKey);
        Assert.Null(preview.SortDirection);
    }

    // ==================== 4. 范围：排序只作用于受限制业务员可见客户 ====================

    [Fact]
    public async Task PreviewAsync_销售订单_受限制业务员_排序仅作用于范围内客户()
    {
        using var db = TestDbFactory.Create();
        var userName = "salesman-scope-sort";
        var user = SeedRestrictedUser(db, userName, "sales-order");
        var employee = SeedEmployee(db, userName);
        var inScope = SeedCustomer(db, "IN-SCOPE", "范围内客户", empId: employee.Id);
        var outScope = SeedCustomer(db, "OUT-SCOPE", "范围外客户", empId: null);

        SeedOrder(db, "SO-OUT", outScope.Id, Currency.USD, 10m, new DateTime(2026, 1, 1));
        SeedOrder(db, "SO-IN-B", inScope.Id, Currency.USD, 10m, new DateTime(2026, 1, 20));
        SeedOrder(db, "SO-IN-A", inScope.Id, Currency.USD, 10m, new DateTime(2026, 1, 5));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("范围排序", SalesOrderDefinition(
            fields: new[] { "orderNo", "orderDate", "customerId" },
            sortFieldKey: "orderDate",
            sortDirection: "asc")));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 10,
        });

        Assert.Equal(new[] { "SO-IN-A", "SO-IN-B" }, OrderNos(preview));
        Assert.Equal(2, preview.Total);
    }
}




