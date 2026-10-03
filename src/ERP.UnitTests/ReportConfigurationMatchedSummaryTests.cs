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
/// ERP-274 通用报表配置「完整匹配集汇总」单元测试（内存数据库，不连接 SQL Server / 不执行真实事务）：
/// 覆盖「事实超出展示页仍纳入指标 / 分组 / 透视汇总」「切换页码 / 每页条数不改变汇总」「混合币种按币种分区」
/// 「null 计数已知 / 缺失」「数值溢出显式原因」「匹配集 1000 条上限受控 reduce-range 错误」「取消令牌」。
/// <para>真实 SQL Server / 浏览器 / 字体环境不可用时显式 environment-blocked；本测试只验证离线路径，绝不声称真实交易验收。</para>
/// </summary>
public class ReportConfigurationMatchedSummaryTests
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

    private static ReportConfigurationAggregate Sum(string fieldKey)
        => new() { FieldKey = fieldKey, Function = ReportConfigurationConstants.AggregateSum };

    private static ReportConfigurationAggregate Count(string fieldKey)
        => new() { FieldKey = fieldKey, Function = ReportConfigurationConstants.AggregateCount };

    private static ReportConfigurationMetricCellDto? SingleCell(
        ReportConfigurationPreviewDto preview, string fieldKey, string function, string? currency)
    {
        var metric = preview.Metrics.FirstOrDefault(m =>
            string.Equals(m.Key, fieldKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Function, function, StringComparison.OrdinalIgnoreCase));
        return metric?.Cells.FirstOrDefault(c =>
            string.Equals(c.Currency ?? string.Empty, currency ?? string.Empty, StringComparison.Ordinal));
    }

    // ==================== 1. 指标汇总：完整匹配事实、分页无关 ====================

    [Fact]
    public async Task 匹配集汇总_事实超出展示页_指标汇总覆盖全部匹配事实且分页无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-metric", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");

        // 5 条事实：3 条 CNY（其中 1 条交货日期为 null）、2 条 USD（其中 1 条交货日期为 null）
        SeedOrder(db, "SO-1", customer.Id, Currency.CNY, 100m, deliveryDate: null);
        SeedOrder(db, "SO-2", customer.Id, Currency.CNY, 200m, deliveryDate: new DateTime(2026, 9, 2));
        SeedOrder(db, "SO-3", customer.Id, Currency.CNY, 300m, deliveryDate: new DateTime(2026, 9, 3));
        SeedOrder(db, "SO-4", customer.Id, Currency.USD, 50m, deliveryDate: new DateTime(2026, 9, 4));
        SeedOrder(db, "SO-5", customer.Id, Currency.USD, 60m, deliveryDate: null);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "超页匹配汇总",
            Definition = MatchedDefinition(
                fields: new[] { "orderNo" },
                aggregates: new List<ReportConfigurationAggregate> { Sum("totalAmount"), Count("deliveryDate") }),
        });

        var execution = BuildExecution(db);
        var page1 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 2,
        });
        var page3 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 3,
            PageSize = 2,
        });

        // 展示页始终只有 2 行（第 3 页仅 1 行），但汇总依据为完整 5 条匹配事实
        Assert.Equal(2, page1.Rows.Count);
        Assert.Equal(5, page1.MatchedCount);
        Assert.Equal(5, page1.SourceEvidenceCount);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, page1.Evidence!.Coverage);

        // 金额按币种分区：CNY = 600，USD = 110（绝不跨币种合并；分页无关）
        Assert.Equal(600m, SingleCell(page1, "totalAmount", "sum", "CNY")!.Value);
        Assert.Equal(110m, SingleCell(page1, "totalAmount", "sum", "USD")!.Value);

        // 计数：已知 3（非 null 交货日期）、缺失 2（null 交货日期）；来源 5
        var count1 = SingleCell(page1, "deliveryDate", "count", null);
        Assert.Equal(3m, count1!.Value);
        Assert.Equal(3, count1.KnownCount);
        Assert.Equal(2, count1.MissingCount);
        Assert.Equal(5, count1.SourceCount);

        // 切换到第 3 页，汇总完全一致
        Assert.Single(page3.Rows);
        Assert.Equal(600m, SingleCell(page3, "totalAmount", "sum", "CNY")!.Value);
        Assert.Equal(110m, SingleCell(page3, "totalAmount", "sum", "USD")!.Value);
        Assert.Equal(3m, SingleCell(page3, "deliveryDate", "count", null)!.Value);
    }

    // ==================== 2. 分组小计：完整匹配事实、分页无关 ====================

    [Fact]
    public async Task 匹配集汇总_分组小计覆盖全部匹配事实_切换页码不改变()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-group", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");

        SeedOrder(db, "SO-1", c1.Id, Currency.CNY, 100m);
        SeedOrder(db, "SO-2", c1.Id, Currency.USD, 50m);
        SeedOrder(db, "SO-3", c2.Id, Currency.CNY, 200m);
        SeedOrder(db, "SO-4", c2.Id, Currency.USD, 60m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "超页分组汇总",
            Definition = MatchedDefinition(
                fields: new[] { "orderNo" },
                grouping: new List<string> { ReportConfigurationConstants.GroupCustomer }),
        });

        var execution = BuildExecution(db);
        var page1 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 1,
        });
        var page2 = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 2,
            PageSize = 1,
        });

        var group1 = Assert.Single(page1.Groups!, g => g.Key == $"customer:{c1.Id}");
        var group2 = Assert.Single(page1.Groups!, g => g.Key == $"customer:{c2.Id}");
        Assert.Contains(group1.Partitions, p => p.Currency == "CNY" && p.Count == 1 && p.Amount == 100m);
        Assert.Contains(group1.Partitions, p => p.Currency == "USD" && p.Count == 1 && p.Amount == 50m);
        Assert.Contains(group2.Partitions, p => p.Currency == "CNY" && p.Count == 1 && p.Amount == 200m);
        Assert.Contains(group2.Partitions, p => p.Currency == "USD" && p.Count == 1 && p.Amount == 60m);

        var group1b = Assert.Single(page2.Groups!, g => g.Key == $"customer:{c1.Id}");
        Assert.Contains(group1b.Partitions, p => p.Currency == "CNY" && p.Amount == 100m);
        Assert.Contains(group1b.Partitions, p => p.Currency == "USD" && p.Amount == 50m);
    }

    // ==================== 2.1 复合分组小计：完整匹配事实 ====================

    [Fact]
    public async Task 匹配集汇总_复合分组小计覆盖全部匹配事实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-composite", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");

        SeedOrder(db, "SO-1", c1.Id, Currency.CNY, 100m, orderDate: new DateTime(2026, 9, 1));
        SeedOrder(db, "SO-2", c1.Id, Currency.CNY, 200m, orderDate: new DateTime(2026, 10, 1));
        SeedOrder(db, "SO-3", c2.Id, Currency.CNY, 300m, orderDate: new DateTime(2026, 9, 2));
        SeedOrder(db, "SO-4", c2.Id, Currency.CNY, 400m, orderDate: new DateTime(2026, 10, 2));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "超页复合分组汇总",
            Definition = MatchedDefinition(
                fields: new[] { "orderNo" },
                grouping: new List<string>
                {
                    ReportConfigurationConstants.GroupCustomer,
                    ReportConfigurationConstants.GroupMonth,
                }),
        });

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 1,
        });

        // 展示页仅 1 行，但复合分组小计覆盖全部 4 条匹配事实（客户 × 月份 = 4 个桶）
        Assert.Single(preview.Rows);
        var groups = preview.Groups!;
        Assert.Equal(4, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Dimensions.Count));
        Assert.Equal(4, groups.SelectMany(g => g.Partitions).Sum(p => p.Count));
        Assert.Equal(1000m, groups.SelectMany(g => g.Partitions).Sum(p => p.Amount ?? 0m));
    }

    // ==================== 3. 透视：完整匹配事实 + 覆盖口径 ====================

    [Fact]
    public async Task 匹配集汇总_透视覆盖全部匹配事实_带覆盖口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-pivot", "sales-order");
        var c1 = SeedCustomer(db, "C1", "客户一");
        var c2 = SeedCustomer(db, "C2", "客户二");

        SeedOrder(db, "SO-1", c1.Id, Currency.CNY, 100m, orderDate: new DateTime(2026, 9, 1));
        SeedOrder(db, "SO-2", c1.Id, Currency.CNY, 200m, orderDate: new DateTime(2026, 9, 2));
        SeedOrder(db, "SO-3", c2.Id, Currency.CNY, 300m, orderDate: new DateTime(2026, 10, 1));
        SeedOrder(db, "SO-4", c2.Id, Currency.CNY, 400m, orderDate: new DateTime(2026, 10, 2));
        SeedOrder(db, "SO-5", c2.Id, Currency.USD, 50m, orderDate: new DateTime(2026, 11, 1));

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "超页透视汇总",
            Definition = MatchedDefinition(
                fields: new[] { "orderNo" },
                aggregates: new List<ReportConfigurationAggregate> { Sum("totalAmount") },
                pivot: new ReportConfigurationPivotDefinition
                {
                    SchemaVersion = 1,
                    RowDimension = ReportConfigurationConstants.GroupCustomer,
                    ColumnDimension = ReportConfigurationConstants.GroupMonth,
                }),
        });

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 1,
            PageSize = 2,
        });

        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(5, preview.MatchedCount);
        Assert.NotNull(preview.Pivot);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, preview.Pivot!.Coverage);
        Assert.Equal(5, preview.Pivot.SourceRowCount);

        var pivotMetric = Assert.Single(preview.Pivot.Metrics);
        Assert.Equal(5, pivotMetric.SourceCount);
        Assert.Equal(5, pivotMetric.KnownCount);
    }

    // ==================== 4. 匹配集上限：受控 reduce-range 错误 ====================

    [Fact]
    public async Task 匹配集汇总_超过1000条_受控缩减范围错误_绝不截断冒充足量()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-cap", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");

        for (var i = 0; i < 1001; i++)
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:D4}",
                CustomerId = customer.Id,
                OrderDate = new DateTime(2026, 9, 1).AddDays(i % 28),
                Status = DocumentStatus.Pending,
                Currency = Currency.CNY,
                TotalAmount = 100m + i,
            });
        }
        db.SaveChanges();

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "超限匹配汇总",
            Definition = MatchedDefinition(
                fields: new[] { "orderNo" },
                aggregates: new List<ReportConfigurationAggregate> { Sum("totalAmount") }),
        });

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
        Assert.Contains("1000", ex.Message);
    }

    // ==================== 4.1 分组输出上限：受控 reduce-range 错误 ====================

    [Fact]
    public async Task 匹配集汇总_分组数超过200_受控缩减范围错误_绝不topN冒充足量()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-group-cap", "sales-order");

        var customers = new List<BaseCustomer>();
        for (var i = 0; i < 201; i++)
        {
            customers.Add(new BaseCustomer { CustomerCode = $"C{i:D3}", CustomerName = $"客户{i}", Status = 1, CreditStatus = "正常" });
        }
        db.BaseCustomers.AddRange(customers);
        db.SaveChanges();

        var orders = customers.Select((c, i) => new SalesOrder
        {
            OrderNo = $"SO-{i:D4}",
            CustomerId = c.Id,
            OrderDate = new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = Currency.CNY,
            TotalAmount = 100m + i,
        }).ToList();
        db.SalesOrders.AddRange(orders);
        db.SaveChanges();

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "分组超限匹配汇总",
            Definition = MatchedDefinition(
                fields: new[] { "orderNo" },
                grouping: new List<string> { ReportConfigurationConstants.GroupCustomer }),
        });

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
        Assert.Contains("分组数", ex.Message);
    }

    // ==================== 5. 数值溢出：显式原因，绝不静默置零 ====================

    [Fact]
    public void 匹配集汇总_数值溢出_显式溢出原因且绝不静默置零()
    {
        var field = new ReportConfigurationFieldDto(
            "totalAmount", "订单总额", ReportConfigurationConstants.TypeNumber, "原币金额",
            Filterable: false, Aggregatable: true, Hidden: false,
            FilterOperators: ReportConfigurationRules.GetOperatorsForType(ReportConfigurationConstants.TypeNumber));

        var dataset = new ReportConfigurationDatasetDto(
            "test", "测试数据集", "测试行", "原币",
            "test-menu", "测试菜单",
            new List<ReportConfigurationFieldDto> { field },
            new[] { "none" },
            new[] { "preview", "grouping", "paging", "matched-set" },
            new[] { "custom-formula", "cross-dataset-join", "pivot", "all-match-total" },
            20, 200, "只读", "边界");

        var definition = new ReportConfigurationDefinition
        {
            SchemaVersion = 1,
            DatasetKey = "test",
            Fields = new List<string> { "totalAmount" },
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate> { Sum("totalAmount") },
            Coverage = ReportConfigurationConstants.CoverageMatchedSet,
        };

        var rows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.Ordinal) { ["totalAmount"] = decimal.MaxValue, ["currency"] = "CNY" },
            new(StringComparer.Ordinal) { ["totalAmount"] = decimal.MaxValue, ["currency"] = "CNY" },
        };

        var results = ReportConfigurationMetricRules.Compute(definition, dataset, rows, new[] { "none" });
        var metric = Assert.Single(results);
        var cell = Assert.Single(metric.Cells);

        Assert.Null(cell.Value);
        Assert.Equal(ReportConfigurationMetricRules.OverflowReason, cell.Reason);
        Assert.Equal(2, cell.SourceCount);
    }

    // ==================== 6. 取消令牌 ====================

    [Fact]
    public async Task 匹配集汇总_取消令牌_抛受控取消()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "ms-cancel", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.CNY, 100m);

        var provider = new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db), db);
        var definition = MatchedDefinition(fields: new[] { "orderNo" });
        var parameters = new ReportConfigurationPreviewParameters(1, 20, "none", null, null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.OpenReadSnapshotAsync(definition, parameters, user, Guid.NewGuid().ToString("N"), cts.Token));
    }
}
