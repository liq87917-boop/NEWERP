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
/// ERP-259 通用报表配置目录与数据集适配器单元测试：覆盖无身份 fail closed、双数据集授权聚合、
/// 菜单撤销后立即收敛（不暴露撤销数据集）、未知键 / 未授权数据集返回 null、有限字段白名单与
/// 真实的能力 / 粒度 / 币种口径。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationCatalogTests
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

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
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

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        });

    private static readonly string[] UnsupportedCapabilities =
    {
        ReportConfigurationConstants.CapabilityCustomFormula,
        ReportConfigurationConstants.CapabilityCrossDatasetJoin,
        ReportConfigurationConstants.CapabilityPivot,
        ReportConfigurationConstants.CapabilityAllMatchTotal,
    };

    // ==================== 1. 身份与授权 ====================

    [Fact]
    public async Task Catalog_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var catalog = BuildCatalog(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => catalog.GetCatalogAsync(null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Catalog_仅销售订单授权_只返回销售订单数据集()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "so-only");
        var role = SeedRole(db, "Role-so-only");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        var dataset = Assert.Single(result.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, dataset.DatasetKey);
        Assert.Equal(31, dataset.Fields.Count);
        Assert.Equal(ReportConfigurationRules.CurrentSchemaVersion, result.SchemaVersion);
    }

    [Fact]
    public async Task Catalog_仅客户资料授权_只返回应收账款数据集()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "ar-only");
        var role = SeedRole(db, "Role-ar-only");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "customer").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        var dataset = Assert.Single(result.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, dataset.DatasetKey);
        Assert.Equal(29, dataset.Fields.Count);
    }

    [Fact]
    public async Task Catalog_双授权_返回两个数据集()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "both");
        var role = SeedRole(db, "Role-both");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "customer").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);

        Assert.Equal(2, result.Datasets.Count);
        // 目录按数据集键确定性排序（Ordinal：receivable < sales-order）
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, result.Datasets[0].DatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, result.Datasets[1].DatasetKey);
    }

    [Fact]
    public async Task Catalog_菜单撤销后_数据集立即消失()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "revoke");
        var role = SeedRole(db, "Role-revoke");
        SeedUserRole(db, user.Id, role.Id);
        var salesMenu = SeedMenu(db, "sales-order");
        SeedRoleMenu(db, role.Id, salesMenu.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "customer").Id);
        var catalog = BuildCatalog(db);

        var before = await catalog.GetCatalogAsync(user.Id);
        Assert.Equal(2, before.Datasets.Count);

        // 撤销销售订单菜单授权（软删除角色-菜单关联；每次请求都重新查询 → 立即收敛）
        var link = db.SysRoleMenus.First(rm => rm.RoleId == role.Id && rm.MenuId == salesMenu.Id);
        link.IsDeleted = true;
        db.SaveChanges();

        var after = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(after.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, dataset.DatasetKey);
    }


    // ==================== 2. 单数据集解析 ====================

    [Fact]
    public async Task GetDataset_未知键_返回null()
    {
        using var db = TestDbFactory.Create();
        var catalog = BuildCatalog(db);

        Assert.Null(await catalog.GetDatasetAsync("not-exist", 1L));
    }

    [Fact]
    public async Task GetDataset_未授权数据集_返回null()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "so-user");
        var role = SeedRole(db, "Role-so-user");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var catalog = BuildCatalog(db);

        Assert.Null(await catalog.GetDatasetAsync(ReportConfigurationConstants.DatasetReceivable, user.Id));
    }

    // ==================== 3. 有限字段与真实口径 ====================

    [Fact]
    public async Task Catalog_销售订单_字段与能力口径真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "meta");
        var role = SeedRole(db, "Role-meta");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(result.Datasets);

        Assert.False(string.IsNullOrWhiteSpace(dataset.Label));
        Assert.False(string.IsNullOrWhiteSpace(dataset.Grain));
        Assert.False(string.IsNullOrWhiteSpace(dataset.CurrencyUnitSemantics));
        Assert.Contains(ReportConfigurationConstants.CapabilityPreview, dataset.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.CapabilityGrouping, dataset.SupportedCapabilities);
        Assert.Contains(ReportConfigurationConstants.GroupCustomer, dataset.GroupingKeys);
        Assert.Contains(ReportConfigurationConstants.GroupMonth, dataset.GroupingKeys);

        foreach (var capability in UnsupportedCapabilities)
            Assert.Contains(capability, dataset.UnsupportedCapabilities);

        // 中文标签 + 有限字段
        Assert.Contains(dataset.Fields, f => f.Key == "totalAmount" && f.Label == "订单总额");
        Assert.All(dataset.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
        Assert.All(dataset.Fields, f => Assert.True(ReportConfigurationRules.IsKnownFieldType(f.Type)));

        // 币种 / 单位语义
        var currency = Assert.Single(dataset.Fields, f => f.Key == "currency");
        Assert.Equal("币种代码", currency.CurrencyUnit);
        var amount = Assert.Single(dataset.Fields, f => f.Key == "totalAmount");
        Assert.Equal("原币金额", amount.CurrencyUnit);
        Assert.True(amount.Aggregatable);

        // 操作符有限（文本字段不支持 between）
        var orderNo = Assert.Single(dataset.Fields, f => f.Key == "orderNo");
        Assert.DoesNotContain(ReportConfigurationConstants.OperatorBetween, orderNo.FilterOperators);
    }

    [Fact]
    public async Task Catalog_应收账款_字段与能力口径真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "ar-meta");
        var role = SeedRole(db, "Role-ar-meta");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "customer").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(result.Datasets);

        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, dataset.DatasetKey);
        Assert.Contains(ReportConfigurationConstants.CapabilityDateRange, dataset.SupportedCapabilities);
        Assert.Equal(100, dataset.MaxPageSize);
        Assert.Contains(dataset.Fields, f => f.Key == "grossAmount" && f.CurrencyUnit == "原币金额");
        Assert.All(dataset.Fields, f => Assert.True(ReportConfigurationRules.IsKnownFieldType(f.Type)));
    }

    [Fact]
    public async Task Catalog_销售订单_指标描述符契约真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "metric-meta");
        var role = SeedRole(db, "Role-metric-meta");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(result.Datasets);

        Assert.NotEmpty(dataset.Metrics);
        var amount = Assert.Single(dataset.Metrics, m => m.Key == "totalAmount");
        Assert.Equal("订单总额", amount.Label);
        Assert.Equal("原币金额", amount.Unit);
        Assert.Equal(ReportConfigurationMetricRules.CurrencyBehaviorPartition, amount.CurrencyBehavior);
        Assert.Contains(ReportConfigurationConstants.AggregateSum, amount.AllowedFunctions);
        Assert.Equal(ReportConfigurationConstants.CoverageCurrentPage, amount.Coverage);

        var orderNo = Assert.Single(dataset.Metrics, m => m.Key == "orderNo");
        Assert.Equal(new[] { ReportConfigurationConstants.AggregateCount }, orderNo.AllowedFunctions);
        Assert.Equal(ReportConfigurationMetricRules.CurrencyBehaviorNone, orderNo.CurrencyBehavior);
    }

    [Fact]
    public async Task Catalog_销售订单_分组维度元数据真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "dim-meta");
        var role = SeedRole(db, "Role-dim-meta");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(result.Datasets);

        Assert.Equal(2, dataset.GroupingDimensions.Count);
        var customer = dataset.GroupingDimensions[0];
        Assert.Equal(ReportConfigurationConstants.GroupCustomer, customer.Key);
        Assert.Equal("customerId", customer.FieldKey);
        Assert.Equal(ReportConfigurationConstants.TypeNumber, customer.FieldType);
        Assert.Equal(ReportConfigurationConstants.GroupingSemanticsIdentity, customer.Semantics);

        var month = dataset.GroupingDimensions[1];
        Assert.Equal(ReportConfigurationConstants.GroupMonth, month.Key);
        Assert.Equal("orderDate", month.FieldKey);
        Assert.Equal(ReportConfigurationConstants.TypeDate, month.FieldType);
        Assert.Equal(ReportConfigurationConstants.GroupingSemanticsCalendarMonth, month.Semantics);
    }
}

