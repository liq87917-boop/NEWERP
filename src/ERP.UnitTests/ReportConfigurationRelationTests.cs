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
/// ERP-268 受控关系（客户维度）单元测试：覆盖关系元数据白名单校验（任意 / 基数不安全 / 不支持 fail closed）、
/// 目录广告、解析器批量只读补全（缺失 / 已删除 / 越权 / 无菜单 / 保留事实粒度与金额 / 不泄露隐藏依赖）与前端接线。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationRelationTests
{
    // ==================== 0. 脚手架 ====================

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

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name,
        string country = "中国", long? empId = null, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Country = country,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted,
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
        Currency currency = Currency.USD, decimal amount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = currency,
            TotalAmount = amount,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static long SeedPrivilegedUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}-{Guid.NewGuid():N}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static long SeedSalesmanUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}-{Guid.NewGuid():N}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(BuildProviders(db));

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildCatalog(db));

    private static IReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new ReportConfigurationExecutionService(db, BuildProviders(db), new ReportConfigurationRelationResolver(db));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition SalesOrderDefinition(
        string[]? fields = null, IReadOnlyList<ReportConfigurationRelationSelection>? relations = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 } ? fields.ToList() : new List<string> { "orderNo", "totalAmount" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            Relations = relations?.ToList() ?? new List<ReportConfigurationRelationSelection>(),
        };

    private static ReportConfigurationRelationSelection CustomerRelation(params string[] fields)
        => new() { RelationKey = ReportConfigurationConstants.RelationCustomer, Fields = fields.ToList() };

    private static ReportConfigurationDatasetDto TestDataset(
        IReadOnlyList<ReportConfigurationRelationDto>? relations = null)
        => new(
            "test",
            "测试数据集",
            "测试行",
            "原币",
            "test-menu",
            "测试菜单",
            new List<ReportConfigurationFieldDto>(),
            new[] { ReportConfigurationConstants.GroupNone },
            new[] { ReportConfigurationConstants.CapabilityPreview },
            new List<string>(),
            20,
            200,
            "只读",
            "边界")
        {
            Relations = relations?.ToList() ?? new List<ReportConfigurationRelationDto>(),
        };

    // ==================== 1. 关系元数据校验（纯规则，无数据库） ====================

    [Fact]
    public void 关系规则_有效客户关系_通过()
    {
        var dataset = TestDataset(new[] { ReportConfigurationRelationRules.BuildCustomerRelation("测试行") });
        var def = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "orderNo" },
            Relations = new List<ReportConfigurationRelationSelection> { CustomerRelation("code", "country") },
        };

        ReportConfigurationRelationRules.Validate(def, dataset);
    }

    [Fact]
    public void 关系规则_未知关系键_失败关闭()
    {
        var dataset = TestDataset(new[] { ReportConfigurationRelationRules.BuildCustomerRelation("测试行") });
        var def = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "orderNo" },
            Relations = new List<ReportConfigurationRelationSelection> { new() { RelationKey = "supplier", Fields = new() { "code" } } },
        };

        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationRelationRules.Validate(def, dataset));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void 关系规则_未知字段_失败关闭()
    {
        var dataset = TestDataset(new[] { ReportConfigurationRelationRules.BuildCustomerRelation("测试行") });
        var def = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "orderNo" },
            Relations = new List<ReportConfigurationRelationSelection> { CustomerRelation("address") },
        };

        Assert.Throws<BusinessException>(() => ReportConfigurationRelationRules.Validate(def, dataset));
    }

    [Fact]
    public void 关系规则_空字段_失败关闭()
    {
        var dataset = TestDataset(new[] { ReportConfigurationRelationRules.BuildCustomerRelation("测试行") });
        var def = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "orderNo" },
            Relations = new List<ReportConfigurationRelationSelection> { new() { RelationKey = "customer", Fields = new() } },
        };

        Assert.Throws<BusinessException>(() => ReportConfigurationRelationRules.Validate(def, dataset));
    }

    [Fact]
    public void 关系规则_重复关系键_失败关闭()
    {
        var dataset = TestDataset(new[] { ReportConfigurationRelationRules.BuildCustomerRelation("测试行") });
        var def = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "orderNo" },
            Relations = new List<ReportConfigurationRelationSelection> { CustomerRelation("code"), CustomerRelation("country") },
        };

        Assert.Throws<BusinessException>(() => ReportConfigurationRelationRules.Validate(def, dataset));
    }

    [Fact]
    public void 关系规则_基数不安全_失败关闭()
    {
        var badRelation = new ReportConfigurationRelationDto(
            "customer", "客户", "测试行", "customerId", "BaseCustomer.Id",
            "one-to-many", "customer", "客户资料",
            ReportConfigurationRelationRules.Fields,
            ReportConfigurationRelationRules.MissingDeletedSemantics);
        var dataset = TestDataset(new[] { badRelation });
        var def = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new List<string> { "orderNo" },
            Relations = new List<ReportConfigurationRelationSelection> { CustomerRelation("code") },
        };

        Assert.Throws<BusinessException>(() => ReportConfigurationRelationRules.Validate(def, dataset));
    }

    // ==================== 2. 目录广告 ====================

    [Fact]
    public async Task 目录_销售订单_广告客户关系与有限字段()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-rel", "sales-order");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user);
        var dataset = Assert.Single(result.Datasets);
        var relation = Assert.Single(dataset.Relations);

        Assert.Equal(ReportConfigurationConstants.RelationCustomer, relation.Key);
        Assert.Equal(ReportConfigurationConstants.CardinalityManyToOne, relation.Cardinality);
        Assert.Equal("customerId", relation.SourceFactKey);
        Assert.Equal("customer", relation.RequiredMenuCode);
        Assert.Contains(relation.Fields, f => f.Key == "code" && f.Label == "客户编码");
        Assert.Contains(relation.Fields, f => f.Key == "country");
        Assert.DoesNotContain(relation.Fields, f => f.Key == "address");
    }

    [Fact]
    public async Task 目录_应收账款_广告客户关系()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ar-rel", "customer");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user);
        var dataset = Assert.Single(result.Datasets);
        Assert.Single(dataset.Relations);
    }

    // ==================== 3. 解析器（内存数据库集成） ====================

    [Fact]
    public async Task 解析器_已授权当前页_补全维度_保留事实粒度与金额_不暴露依赖()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-res", "sales-order", "customer");
        var customer = SeedCustomer(db, "C001", "义乌进出口", "中国");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 250m);

        var def = SalesOrderDefinition(
            fields: new[] { "orderNo", "totalAmount", "currency" },
            relations: new[] { CustomerRelation("code", "country") });
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("客户维度", def));
        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Equal(2, preview.Total);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Contains(preview.Columns, c => c.Key == "customer.code");
        Assert.Contains(preview.Columns, c => c.Key == "customer.country");
        Assert.DoesNotContain(preview.Columns, c => c.Key == "customerId");

        foreach (var row in preview.Rows)
        {
            Assert.False(row.ContainsKey("customerId"));
            Assert.Equal("C001", row["customer.code"]);
            Assert.Equal("中国", row["customer.country"]);
        }

        var amounts = preview.Rows.Select(r => r["totalAmount"]).ToList();
        Assert.Contains(100m, amounts);
        Assert.Contains(250m, amounts);

        var evidence = Assert.Single(preview.RelationEvidence);
        Assert.Equal(ReportConfigurationConstants.RelationCustomer, evidence.RelationKey);
        Assert.Contains("resolved=4", evidence.StatusSummary);
    }

    [Fact]
    public async Task 解析器_缺失客户_返回空加缺失原因()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-miss", "sales-order", "customer");
        SeedOrder(db, "SO-1", 9999, Currency.USD, 100m);

        var def = SalesOrderDefinition(fields: new[] { "orderNo" }, relations: new[] { CustomerRelation("code") });
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("缺失", def));
        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        var row = Assert.Single(preview.Rows);
        Assert.Null(row["customer.code"]);
        Assert.Equal("缺失", preview.CellReasons[0]["customer.code"]);
    }

    [Fact]
    public async Task 解析器_已删除客户_返回空加已删除原因()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-del", "sales-order", "customer");
        var customer = SeedCustomer(db, "C-DEL", "已删除客户", deleted: true);
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var def = SalesOrderDefinition(fields: new[] { "orderNo" }, relations: new[] { CustomerRelation("code") });
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("已删除", def));
        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        var row = Assert.Single(preview.Rows);
        Assert.Null(row["customer.code"]);
        Assert.Equal("已删除", preview.CellReasons[0]["customer.code"]);
    }

    [Fact]
    public async Task 解析器_无客户资料菜单_返回空加越权原因()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-nomenu", "sales-order");
        var customer = SeedCustomer(db, "C001", "义乌进出口", "中国");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var def = SalesOrderDefinition(fields: new[] { "orderNo" }, relations: new[] { CustomerRelation("code") });
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("无菜单", def));
        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        var row = Assert.Single(preview.Rows);
        Assert.Null(row["customer.code"]);
        Assert.Equal("越权", preview.CellReasons[0]["customer.code"]);
    }

    [Fact]
    public async Task 解析器_越权客户_返回空加越权原因()
    {
        using var db = TestDbFactory.Create();
        var user = SeedSalesmanUser(db, "salesman1", "sales-order", "customer");
        var employee = SeedEmployee(db, "salesman1");
        var inScope = SeedCustomer(db, "C-IN", "范围内", empId: employee.Id);
        var outScope = SeedCustomer(db, "C-OUT", "范围外", empId: null);

        var preview = new ReportConfigurationPreviewDto
        {
            Columns = new List<ReportConfigurationColumnDto> { new("orderNo", "订单号", "text", null) },
            Rows = new List<Dictionary<string, object?>>
            {
                new() { ["orderNo"] = "SO-1", ["customerId"] = outScope.Id },
                new() { ["orderNo"] = "SO-2", ["customerId"] = inScope.Id },
            },
            PageSize = 20,
        };
        var def = SalesOrderDefinition(fields: new[] { "orderNo" }, relations: new[] { CustomerRelation("code") });
        var resolver = new ReportConfigurationRelationResolver(db);
        await resolver.EnrichAsync(preview, def, user);

        Assert.Null(preview.Rows[0]["customer.code"]);
        Assert.Equal("越权", preview.CellReasons[0]["customer.code"]);
        Assert.Equal("C-IN", preview.Rows[1]["customer.code"]);
    }

    // ==================== 4. 前端接线（源码静态断言） ====================

    [Fact]
    public void 前端接线_受控关系选择器与定义组装_且不含任意联接SQL()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccBuildRelations(state)", js);
        Assert.Contains("relations: rccBuildRelations(state)", js);
        Assert.Contains("function rccRelationsHtml()", js);
        Assert.Contains("function rccOnRelationField(", js);
        Assert.Contains("relations: []", js);
        Assert.DoesNotContain("JOIN ", js);
        Assert.DoesNotContain("SELECT ", js);
    }

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}


