using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-261 通用报表配置执行引擎单元测试（内存数据库，不连接 SQL Server / 不执行 SQL）：
/// 覆盖草稿 / 固定发布修订分发、跨所有者与已删除 fail closed、字段撤销 / 菜单撤销 / 陈旧定义 /
/// 不支持能力 / 多分组键 / 分页边界 / 无效分组覆盖，以及销售订单 / 应收账款两个适配器的分组 / 日期 / 分页口径。
/// </summary>
public class ReportConfigurationExecutionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

    private static string Serialize(ReportConfigurationDefinition definition)
        => JsonSerializer.Serialize(definition, JsonOptions);

    private static ReportConfigurationFormulaNode FormulaField(string key) => new() { Kind = "field", FieldKey = key };
    private static ReportConfigurationFormulaNode FormulaLiteral(decimal value) => new() { Kind = "literal", Literal = value };
    private static ReportConfigurationFormulaNode FormulaBinary(
        string kind, ReportConfigurationFormulaNode left, ReportConfigurationFormulaNode right)
        => new() { Kind = kind, Left = left, Right = right };

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

    private static ReportConfigurationDefinition ReceivableDefinition(string[]? fields = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetReceivable,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "customerName", "grossAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    [Fact]
    public async Task PreviewAsync_销售订单草稿_分组返回页面小计与币种分区()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-preview", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户一");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 200m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("分组报表", SalesOrderDefinition(groupBy: "customer")));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, preview.DatasetKey);
        Assert.False(preview.IsPinnedRevision);
        Assert.Contains(preview.Columns, c => c.Key == "totalAmount" && c.CurrencyUnit == "原币金额");
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal("customer", preview.GroupBy);

        var group = Assert.Single(preview.Groups!);
        Assert.Equal(2, group.Partitions.Count);
        Assert.Contains(group.Partitions, p => p.Currency == "USD" && p.Amount == 100m);
        Assert.Contains(group.Partitions, p => p.Currency == "CNY" && p.Amount == 200m);
        Assert.Equal(ReportConfigurationConstants.CoverageCurrentPage, preview.Evidence!.Coverage);
    }

    [Fact]
    public async Task PreviewAsync_固定已发布修订_返回修订定义字段()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-rev", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition(fields: new[] { "orderNo" })));
        var published = await service.PublishAsync(user, created.Id, created.Version);
        await service.UpdateAsync(user, created.Id, published.Version,
            SaveDto("报表", SalesOrderDefinition(fields: new[] { "currency", "totalAmount" })));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            RevisionVersion = 1,
        });

        Assert.True(preview.IsPinnedRevision);
        Assert.Equal(1, preview.PinnedRevisionVersion);
        Assert.Contains(preview.Columns, c => c.Key == "orderNo");
        Assert.DoesNotContain(preview.Columns, c => c.Key == "currency");
    }

    [Fact]
    public async Task PreviewAsync_应收账款_空数据_返回类型化结果与证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ar-preview", "customer");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("应收报表", ReceivableDefinition()));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, preview.DatasetKey);
        Assert.Contains(preview.Columns, c => c.Key == "grossAmount" && c.CurrencyUnit == "原币金额");
        Assert.Empty(preview.Rows);
        Assert.Equal(0, preview.Total);
        Assert.Equal(ReportConfigurationConstants.CoverageCurrentPage, preview.Evidence!.Coverage);
    }

    [Fact]
    public async Task PreviewAsync_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedPrivilegedUser(db, "owner-a", "sales-order");
        var ownerB = SeedPrivilegedUser(db, "owner-b", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(ownerA, SaveDto("报表", SalesOrderDefinition()));

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            ownerB, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_已删除配置_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));
        await service.DeleteAsync(user, created.Id, created.Version);

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_日期筛选_映射到既有日期范围()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-date", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-A", customer.Id, Currency.USD, 10m, new DateTime(2026, 1, 1));
        SeedOrder(db, "SO-B", customer.Id, Currency.USD, 20m, new DateTime(2026, 2, 1));

        var def = SalesOrderDefinition(fields: new[] { "orderNo", "orderDate", "totalAmount" });
        def.Filters = new List<ReportConfigurationFilter>
        {
            new ReportConfigurationFilter
            {
                FieldKey = "orderDate",
                Operator = ReportConfigurationConstants.OperatorGte,
                Value = "2026-02-01",
            },
        };

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("日期报表", def));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Single(preview.Rows);
        Assert.Equal("SO-B", preview.Rows[0]["orderNo"]);
    }

    [Fact]
    public async Task PreviewAsync_草稿_返回名称版本与规范化筛选日期范围()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-meta", "sales-order");

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
        var created = await service.CreateAsync(user, SaveDto("元数据报表", def));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Equal("元数据报表", preview.Name);
        Assert.Equal(1, preview.Version);
        Assert.Contains("≥", preview.NormalizedFiltersText);
        Assert.Equal("2026-09-01 起", preview.DateRangeText);
    }

    [Fact]
    public async Task PreviewAsync_字段被撤销_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        db.ReportConfigurations.Single().DefinitionJson = Serialize(SalesOrderDefinition(fields: new[] { "revokedField" }));
        db.SaveChanges();

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_菜单被撤销_拒绝()
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

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user.Id, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_陈旧定义_schema版本不支持_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        var stale = SalesOrderDefinition();
        stale.SchemaVersion = 999;
        db.ReportConfigurations.Single().DefinitionJson = Serialize(stale);
        db.SaveChanges();

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_不支持能力_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        var def = SalesOrderDefinition();
        def.Capabilities = new List<string> { ReportConfigurationConstants.CapabilityCustomFormula };
        db.ReportConfigurations.Single().DefinitionJson = Serialize(def);
        db.SaveChanges();

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_复合分组_返回有序复合维度且不改写源页事实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var customer = SeedCustomer(db, "C001", "客户一");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m, new DateTime(2026, 9, 1));
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 200m, new DateTime(2026, 10, 1));

        var def = SalesOrderDefinition();
        def.Grouping = new List<string> { "customer", "month" };

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("复合分组报表", def));

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Equal(new List<string> { "customer", "month" }, preview.Groupings);
        Assert.Equal(2, preview.Rows.Count);              // 源页事实不变、不重新分页、不复制行
        Assert.Equal(2, preview.Total);
        Assert.All(preview.Rows, r => Assert.False(r.ContainsKey("customerId")));  // 仅投影选定字段，剥离分组依赖
        Assert.All(preview.Rows, r => Assert.False(r.ContainsKey("orderDate")));
        Assert.All(preview.Rows, r => Assert.True(r.ContainsKey("orderNo")));

        Assert.NotNull(preview.Groups);
        Assert.Equal(2, preview.Groups!.Count);           // 客户一 × 2026年9月、客户一 × 2026年10月
        Assert.All(preview.Groups, g => Assert.Equal(2, g.Dimensions.Count));
        var first = preview.Groups[0];
        Assert.Equal("customer", first.Dimensions[0].Key);
        Assert.Equal("month", first.Dimensions[1].Key);
        Assert.StartsWith("客户 #", first.Dimensions[0].Label);
    }

    [Fact]
    public async Task PreviewAsync_分组覆盖冲突_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest
            {
                ConfigurationId = created.Id,
                GroupBy = "customer",
                Groupings = new List<string> { "month" },
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, PageSize = 201 }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_无效分组覆盖_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("报表", SalesOrderDefinition()));

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, GroupBy = "foo" }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PreviewAsync_计算列_投影计算值且不泄露隐藏依赖()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-calc", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var def = SalesOrderDefinition(fields: new[] { "orderNo" });
        def.ComputedColumns = new List<ReportConfigurationComputedColumn>
        {
            new()
            {
                Key = "doubleAmount",
                Label = "双倍金额",
                Expression = FormulaBinary(
                    ReportConfigurationFormulaRules.NodeMultiply,
                    FormulaField("totalAmount"),
                    FormulaLiteral(2m)),
            },
        };

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("计算报表", def));
        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.Contains(preview.Columns, c => c.Key == "orderNo");
        var computedColumn = Assert.Single(preview.Columns, c => c.Key == "doubleAmount");
        Assert.True(computedColumn.IsComputed);
        Assert.Equal("原币金额", computedColumn.CurrencyUnit);
        Assert.DoesNotContain(preview.Columns, c => c.Key == "totalAmount");

        var row = Assert.Single(preview.Rows);
        Assert.Equal(200m, row["doubleAmount"]);
        Assert.False(row.ContainsKey("totalAmount"));

        var evidence = Assert.Single(preview.ComputedColumns);
        Assert.Equal("doubleAmount", evidence.Key);
        Assert.Equal(new[] { "totalAmount" }, evidence.Dependencies);
    }

    [Fact]
    public async Task PreviewAsync_选中指标_仅计算选中指标且剥离隐藏依赖()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "so-metric", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 200m);

        var def = SalesOrderDefinition(fields: new[] { "orderNo" });
        def.Aggregates = new List<ReportConfigurationAggregate>
        {
            new() { Function = ReportConfigurationConstants.AggregateSum, FieldKey = "totalAmount" },
        };

        var service = BuildService(db);
        var created = await service.CreateAsync(user, SaveDto("指标报表", def));
        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        var metric = Assert.Single(preview.Metrics);
        Assert.Equal("totalAmount", metric.Key);
        Assert.Equal(ReportConfigurationConstants.AggregateSum, metric.Function);
        Assert.Equal(2, metric.Cells.Count);
        Assert.Equal(100m, Assert.Single(metric.Cells, c => c.Currency == "USD").Value);
        Assert.Equal(200m, Assert.Single(metric.Cells, c => c.Currency == "CNY").Value);

        Assert.DoesNotContain(preview.Columns, c => c.Key == "totalAmount");
        Assert.All(preview.Rows, r => Assert.False(r.ContainsKey("totalAmount")));
    }
}
