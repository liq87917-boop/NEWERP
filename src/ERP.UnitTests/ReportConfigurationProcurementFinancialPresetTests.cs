using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-304 采购 + 财务家族预设模板单元测试：验证 ERP-297（采购订单 / 供应商对账与账龄 / 供应商采购敞口）与
/// ERP-298（资产负债表 / 利润表 / 现金流量表 / 应收账龄）七个受控数据集的不可变预设，通过既有只读列出 + 私有物化
/// 编排完成发现与私有副本（绝不新增查询 / 报表 UI / 导出器）；覆盖撤销菜单、受限制数据范围、无效键与字段白名单校验。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationProcurementFinancialPresetTests
{
    private static readonly string[] SevenPresetKeys =
    {
        "purchase-order",
        "supplier-aging",
        "supplier-exposure",
        "balance-sheet",
        "income-statement",
        "cash-flow",
        "ar-aging",
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

    private static SysRole SeedRole(ErpDbContext db, string suffix, bool isSystem)
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

    private static long SeedPrivilegedAuthorizedUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, userName + "-sys", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static long SeedRestrictedAuthorizedUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, userName + "-restricted", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static void RevokeMenu(ErpDbContext db, long userId, string menuCode)
    {
        var menu = db.SysMenus.First(m => m.MenuCode == menuCode);
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var link in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && rm.MenuId == menu.Id))
            link.IsDeleted = true;
        db.SaveChanges();
    }

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new PurchaseOrderReportConfigurationDatasetProvider(new DynamicPurchaseOrderReportQuery(db)),
            new SupplierAgingReportConfigurationDatasetProvider(db),
            new SupplierExposureReportConfigurationDatasetProvider(db),
            new BalanceSheetReportConfigurationDatasetProvider(new ReportService(db), db),
            new IncomeStatementReportConfigurationDatasetProvider(new ReportService(db), db),
            new CashFlowReportConfigurationDatasetProvider(new ReportService(db), db),
            new ArAgingReportConfigurationDatasetProvider(new ReportService(db), db),
        });

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildCatalog(db));

    private static IReportConfigurationPresetCatalog BuildPresets(
        ErpDbContext db,
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service)
    {
        var migrationPresets = new ReportMigrationPresetCatalog();
        var registry = new ReportMigrationRegistry(catalog, db, migrationPresets);
        return new ReportConfigurationPresetCatalog(catalog, registry, service);
    }

    private static void AssertOrderedSubset(IReadOnlyList<string> presetFields, IReadOnlyList<string> liveKeys)
    {
        var cursor = 0;
        foreach (var field in presetFields)
        {
            var found = false;
            for (var i = cursor; i < liveKeys.Count; i++)
            {
                if (string.Equals(liveKeys[i], field, StringComparison.OrdinalIgnoreCase))
                {
                    cursor = i + 1;
                    found = true;
                    break;
                }
            }

            Assert.True(found, $"预设字段 {field} 不在数据集白名单内");
        }
    }

    // ==================== 1. 只读列出：七个受控预设全部被发现 ====================

    [Fact]
    public async Task ListPresets_特权账号授权五菜单_返回七个新增预设()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "fin-po",
            "purchase-order", "balance-sheet", "income-statement", "cash-flow", "ar-aging");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildCatalog(db), service);

        var result = await presets.ListPresetsAsync(user);

        var keys = result.Select(p => p.PresetKey).OrderBy(k => k).ToArray();
        Assert.Equal(SevenPresetKeys.OrderBy(k => k).ToArray(), keys);

        var byKey = result.ToDictionary(p => p.PresetKey, StringComparer.Ordinal);
        Assert.Equal("dynamic:purchase-order", byKey["purchase-order"].LegacyKey);
        Assert.Equal("report:balance-sheet", byKey["balance-sheet"].LegacyKey);
        Assert.Equal("report:ar-aging", byKey["ar-aging"].LegacyKey);
        Assert.Equal(ReportConfigurationConstants.DatasetSupplierAging, byKey["supplier-aging"].DatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetIncomeStatement, byKey["income-statement"].DatasetKey);
    }

    [Fact]
    public async Task ListPresets_撤销采购订单菜单_采购类预设隐藏_财务类保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "revoke-po",
            "purchase-order", "balance-sheet", "income-statement", "cash-flow", "ar-aging");
        var catalog = BuildCatalog(db);
        var service = BuildService(db);
        var presets = BuildPresets(db, catalog, service);

        RevokeMenu(db, user, "purchase-order");

        var result = await presets.ListPresetsAsync(user);
        var keys = result.Select(p => p.PresetKey).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("purchase-order", keys);
        Assert.DoesNotContain("supplier-aging", keys);
        Assert.DoesNotContain("supplier-exposure", keys);
        Assert.Contains("balance-sheet", keys);
        Assert.Contains("income-statement", keys);
        Assert.Contains("cash-flow", keys);
        Assert.Contains("ar-aging", keys);
    }

    [Fact]
    public async Task ListPresets_受限制用户有财务菜单_财务预设被隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedRestrictedAuthorizedUser(db, "restricted-fin", "balance-sheet");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildCatalog(db), service);

        var result = await presets.ListPresetsAsync(user);

        Assert.Empty(result);
    }

    // ==================== 2. 物化：字段白名单合法 + 私有副本语义 ====================

    [Fact]
    public async Task 七个预设_字段均为数据集白名单字段且保持目录顺序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "field-order",
            "purchase-order", "balance-sheet", "income-statement", "cash-flow", "ar-aging");
        var catalog = BuildCatalog(db);
        var service = BuildService(db);
        var presets = BuildPresets(db, catalog, service);

        foreach (var key in SevenPresetKeys)
        {
            var materialized = await presets.MaterializeAsync(key, user);
            var dataset = await catalog.GetDatasetAsync(materialized.DatasetKey, user);
            Assert.NotNull(dataset);
            AssertOrderedSubset(materialized.Definition!.Fields, dataset!.Fields.Select(f => f.Key).ToList());
        }
    }

    [Fact]
    public async Task Materialize_七个预设_经既有服务创建私有副本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "materialize-fin",
            "purchase-order", "balance-sheet", "income-statement", "cash-flow", "ar-aging");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildCatalog(db), service);

        foreach (var key in SevenPresetKeys)
        {
            var created = await presets.MaterializeAsync(key, user);

            Assert.Equal(user, created.OwnerUserId);
            Assert.Equal(ReportConfigurationStatus.Draft, created.Status);
            Assert.NotNull(created.Definition);
            Assert.False(string.IsNullOrWhiteSpace(created.Definition!.DatasetKey));
        }

        Assert.Equal(SevenPresetKeys.Length, db.ReportConfigurations.Count());
    }

    [Fact]
    public async Task Materialize_重复物化_预设不可变且不消费()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "immutable", "balance-sheet");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildCatalog(db), service);

        await presets.MaterializeAsync("balance-sheet", user);
        await presets.MaterializeAsync("balance-sheet", user);

        Assert.Equal(2, db.ReportConfigurations.Count());

        var preset = await presets.GetPresetAsync("balance-sheet", user);
        Assert.NotNull(preset);
    }

    [Fact]
    public async Task Materialize_未知预设_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "unknown-fin", "balance-sheet");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildCatalog(db), service);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("does-not-exist", user));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_撤销菜单_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedAuthorizedUser(db, "revoke-fin-mat", "balance-sheet");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildCatalog(db), service);

        await presets.MaterializeAsync("balance-sheet", user);
        Assert.Single(db.ReportConfigurations);

        RevokeMenu(db, user, "balance-sheet");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("balance-sheet", user));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Single(db.ReportConfigurations);
    }
}
