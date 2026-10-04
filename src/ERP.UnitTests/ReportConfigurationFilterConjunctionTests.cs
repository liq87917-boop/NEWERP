using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-278 合取语义：重复筛选谓词绝不 last-write-wins —— 重复标量相等值合并或冲突拒绝、
/// 日期下界/上界/eq/between 一律按交集合并（与输入顺序无关）、矛盾或不可表示合取 fail closed。
/// </summary>
public class ReportConfigurationFilterConjunctionTests
{
    private static ReportConfigurationFieldDto Field(string key, string type, bool aggregatable = false)
        => new(key, key, type, null, true, aggregatable, false,
            ReportConfigurationRules.GetOperatorsForType(type));

    private static ReportConfigurationDatasetDto BuildDataset()
        => new(
            "test", "测试数据集", "测试表", "原币",
            "test-menu", "测试菜单",
            new List<ReportConfigurationFieldDto>
            {
                Field("orderDate", ReportConfigurationConstants.TypeDate),
                Field("customerId", ReportConfigurationConstants.TypeNumber),
                Field("currency", ReportConfigurationConstants.TypeEnum),
                Field("status", ReportConfigurationConstants.TypeEnum),
                Field("amount", ReportConfigurationConstants.TypeNumber, aggregatable: true),
            },
            new[] { "none", "customer", "month" },
            new[] { "preview", "grouping", "date-range", "paging", "matched-set" },
            new[] { "custom-formula", "cross-dataset-join", "pivot", "all-match-total" },
            20, 200, "只读", "边界");

    private static ReportConfigurationDefinition ValidDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = "test",
            Fields = new() { "amount", "orderDate" },
            Filters = new(),
            Grouping = new() { "none" },
            Aggregates = new(),
            Capabilities = new(),
        };

    private static ReportConfigurationFilter Filter(string key, string op, object? value, object? value2 = null)
        => new() { FieldKey = key, Operator = op, Value = value, Value2 = value2 };

    private static void AssertValid(ReportConfigurationDefinition definition)
        => ReportConfigurationRules.Validate(definition, BuildDataset());

    private static void AssertInvalid(ReportConfigurationDefinition definition)
    {
        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationRules.Validate(definition, BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_重复相等标量_相同值_合并通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("customerId", ReportConfigurationConstants.OperatorEq, 5),
            Filter("customerId", ReportConfigurationConstants.OperatorEq, 5L),
        };
        AssertValid(definition);
    }

    [Fact]
    public void Validate_重复相等标量_冲突值_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("customerId", ReportConfigurationConstants.OperatorEq, 5),
            Filter("customerId", ReportConfigurationConstants.OperatorEq, 6),
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_重复币种_大小写不同_合并通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("currency", ReportConfigurationConstants.OperatorEq, "USD"),
            Filter("currency", ReportConfigurationConstants.OperatorEq, "usd"),
        };
        AssertValid(definition);
    }

    [Fact]
    public void Validate_日期_两个下界_交集通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorGte, new DateTime(2026, 2, 1)),
            Filter("orderDate", ReportConfigurationConstants.OperatorGte, new DateTime(2026, 1, 1)),
        };
        AssertValid(definition);
    }

    [Fact]
    public void Validate_日期_两个上界_交集通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorLte, new DateTime(2026, 1, 31)),
            Filter("orderDate", ReportConfigurationConstants.OperatorLte, new DateTime(2026, 2, 28)),
        };
        AssertValid(definition);
    }

    [Fact]
    public void Validate_日期_下界与上界矛盾_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorGte, new DateTime(2026, 3, 1)),
            Filter("orderDate", ReportConfigurationConstants.OperatorLte, new DateTime(2026, 2, 1)),
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_日期_gt_lt_交集为空_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorGt, new DateTime(2026, 2, 28)),
            Filter("orderDate", ReportConfigurationConstants.OperatorLt, new DateTime(2026, 2, 28)),
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_日期_eq_与_between_非空交集_通过()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorEq, new DateTime(2026, 5, 15)),
            Filter("orderDate", ReportConfigurationConstants.OperatorBetween, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)),
        };
        AssertValid(definition);
    }

    [Fact]
    public void Validate_日期_eq_与_between_空交集_拒绝()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorEq, new DateTime(2026, 5, 15)),
            Filter("orderDate", ReportConfigurationConstants.OperatorBetween, new DateTime(2026, 6, 1), new DateTime(2026, 6, 30)),
        };
        AssertInvalid(definition);
    }

    [Fact]
    public void Validate_日期_极端上界_gt_拒绝且不溢出()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorGt, DateTime.MaxValue),
        };
        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationRules.Validate(definition, BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void Validate_日期_极端下界_lt_拒绝且不溢出()
    {
        var definition = ValidDefinition();
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorLt, DateTime.MinValue),
        };
        var ex = Assert.Throws<BusinessException>(() => ReportConfigurationRules.Validate(definition, BuildDataset()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 受控翻译（提供器，内存库） ====================

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

    private static SalesOrderReportConfigurationDatasetProvider BuildProvider(ErpDbContext db)
        => new(new DynamicSalesOrderReportQuery(db));

    private static ReportConfigurationDefinition SalesOrderDefinition(string[]? fields = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 } ? fields.ToList() : new List<string> { "orderNo", "totalAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
        };

    private static ReportConfigurationPreviewParameters Parameters()
        => new(1, 20, ReportConfigurationConstants.GroupNone, null, null);

    [Fact]
    public async Task PreviewAsync_日期两个上界_交集而非最后写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db, "conj-lte", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 10m, new DateTime(2026, 1, 15));
        SeedOrder(db, "SO-2", customer.Id, Currency.USD, 20m, new DateTime(2026, 2, 15));

        var definition = SalesOrderDefinition(new[] { "orderNo", "orderDate" });
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorLte, new DateTime(2026, 1, 31)),
            Filter("orderDate", ReportConfigurationConstants.OperatorLte, new DateTime(2026, 2, 28)),
        };

        var provider = BuildProvider(db);
        var preview = await provider.PreviewAsync(definition, Parameters(), userId);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("SO-1", row["orderNo"]);
    }

    [Fact]
    public async Task PreviewAsync_日期两个下界_交集而非最后写入()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db, "conj-gte", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 10m, new DateTime(2026, 1, 15));
        SeedOrder(db, "SO-2", customer.Id, Currency.USD, 20m, new DateTime(2026, 2, 15));

        var definition = SalesOrderDefinition(new[] { "orderNo", "orderDate" });
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorGte, new DateTime(2026, 2, 1)),
            Filter("orderDate", ReportConfigurationConstants.OperatorGte, new DateTime(2026, 1, 1)),
        };

        var provider = BuildProvider(db);
        var preview = await provider.PreviewAsync(definition, Parameters(), userId);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("SO-2", row["orderNo"]);
    }

    [Fact]
    public async Task PreviewAsync_日期_gt_lt_保留日历日包含与排除()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db, "conj-gtlt", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 10m, new DateTime(2026, 2, 1));
        SeedOrder(db, "SO-2", customer.Id, Currency.USD, 20m, new DateTime(2026, 2, 28));

        var definition = SalesOrderDefinition(new[] { "orderNo", "orderDate" });
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorGte, new DateTime(2026, 2, 1)),
            Filter("orderDate", ReportConfigurationConstants.OperatorLt, new DateTime(2026, 2, 28)),
        };

        var provider = BuildProvider(db);
        var preview = await provider.PreviewAsync(definition, Parameters(), userId);

        var row = Assert.Single(preview.Rows);
        Assert.Equal("SO-1", row["orderNo"]);
    }

    [Fact]
    public async Task PreviewAsync_日期_上界先于下界输入_交集一致()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db, "conj-order", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 10m, new DateTime(2026, 3, 1));
        SeedOrder(db, "SO-2", customer.Id, Currency.USD, 20m, new DateTime(2026, 3, 31));

        var definition = SalesOrderDefinition(new[] { "orderNo", "orderDate" });
        definition.Filters = new()
        {
            Filter("orderDate", ReportConfigurationConstants.OperatorLt, new DateTime(2026, 4, 1)),
            Filter("orderDate", ReportConfigurationConstants.OperatorGt, new DateTime(2026, 2, 28)),
        };

        var provider = BuildProvider(db);
        var preview = await provider.PreviewAsync(definition, Parameters(), userId);

        Assert.Equal(2, preview.Rows.Count);
    }

    [Fact]
    public async Task PreviewAsync_重复标量相等_相同值_合并通过()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db, "conj-eq", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 10m);

        var definition = SalesOrderDefinition(new[] { "orderNo" });
        definition.Filters = new()
        {
            Filter("customerId", ReportConfigurationConstants.OperatorEq, customer.Id),
            Filter("customerId", ReportConfigurationConstants.OperatorEq, customer.Id),
        };

        var provider = BuildProvider(db);
        var preview = await provider.PreviewAsync(definition, Parameters(), userId);

        Assert.Single(preview.Rows);
    }

    [Fact]
    public async Task PreviewAsync_重复标量相等_冲突值_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db, "conj-eq-conflict", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 10m);

        var definition = SalesOrderDefinition(new[] { "orderNo" });
        definition.Filters = new()
        {
            Filter("customerId", ReportConfigurationConstants.OperatorEq, customer.Id),
            Filter("customerId", ReportConfigurationConstants.OperatorEq, customer.Id + 1),
        };

        var provider = BuildProvider(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.PreviewAsync(definition, Parameters(), userId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }
}
