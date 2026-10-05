using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-312 受控打印模板绑定目录单元测试：封闭族清单、非空已保存模板枚举与设置保留、精确菜单拒绝、
/// 不支持族显式阻塞、字段顺序 / 别名注入 / 畸形超限 FieldKeys / LayoutJson / 数据集与归属拒绝，
/// 以及迁移登记册集成（fail-closed 退役）。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationPrintTemplateCatalogTests
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

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role");
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
        {
            var menu = SeedMenu(db, code);
            SeedRoleMenu(db, role.Id, menu.Id);
        }
        return user;
    }

    private static SysPrintTemplate SeedTemplate(
        ErpDbContext db, string billType, string templateName, bool isDefault = false,
        string? fieldKeys = null, string? layoutJson = null)
    {
        var template = new SysPrintTemplate
        {
            BillType = billType,
            TemplateName = templateName,
            Title = "测试打印标题",
            IsDefault = isDefault,
            PaperSize = "A4",
            FontSize = 12,
            FontFamily = "Microsoft YaHei",
            ShowCompanyHeader = true,
            ShowDetailTable = true,
            ShowRemark = true,
            FooterText = "页脚",
            FieldKeys = fieldKeys ?? string.Empty,
            LayoutJson = layoutJson,
        };
        db.SysPrintTemplates.Add(template);
        db.SaveChanges();
        return template;
    }

    private static ReportConfigurationDatasetDto BuildSalesOrderDataset()
    {
        var family = LegacyBillExportCatalog.Resolve("sales-order");
        var fields = family.Columns.Select(c => new ReportConfigurationFieldDto(
            c.Key, c.Title, c.Type, null, false, false, false, Array.Empty<string>())).ToList();
        return new ReportConfigurationDatasetDto(
            family.DatasetKey, family.Title, "一行一条单据表头",
            "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
            family.RequiredMenuCodes[0], family.RequiredMenuText, fields,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            20, 200, string.Empty, string.Empty);
    }

    private sealed class FakeCatalog : IReportConfigurationCatalog
    {
        private readonly IReadOnlyDictionary<string, ReportConfigurationDatasetDto?> _datasets;

        public FakeCatalog(IReadOnlyDictionary<string, ReportConfigurationDatasetDto?> datasets) => _datasets = datasets;

        public Task<ReportConfigurationCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReportConfigurationCatalogDto(
                ReportConfigurationRules.CurrentSchemaVersion,
                _datasets.Values.Where(v => v is not null).Select(v => v!).ToList()));

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
            string datasetKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(_datasets.TryGetValue(datasetKey, out var dataset) ? dataset : null);
    }

    private static ReportConfigurationPrintTemplateCatalog BuildService(
        ErpDbContext db, IReportConfigurationCatalog catalog)
        => new(db, catalog);

    private static IReportConfigurationCatalog SalesOrderCatalog()
        => new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            [LegacyBillExportCatalog.Resolve("sales-order").DatasetKey] = BuildSalesOrderDataset(),
        });

    // ==================== 1. 封闭族清单 ====================

    [Fact]
    public void Families_封闭清单_17支持加9显式不支持()
    {
        var families = ReportPrintTemplateFamilies.Families;

        Assert.Equal(26, families.Count);
        Assert.Equal(17, families.Count(f => f.Supported));
        Assert.Equal(9, families.Count(f => !f.Supported));
        Assert.Equal(families.Count, families.Select(f => f.FamilyKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var masters = new[] { "customer", "supplier", "employee", "expense-account", "warehouse", "product", "other-info" };
        foreach (var key in masters)
            Assert.False(Assert.Single(families, f => f.FamilyKey == key).Supported);
        Assert.False(Assert.Single(families, f => f.FamilyKey == "quotation").Supported);
        Assert.False(Assert.Single(families, f => f.FamilyKey == "proforma-invoice").Supported);
        Assert.True(Assert.Single(families, f => f.FamilyKey == "doc-center").Supported);
    }

    // ==================== 2. 目录枚举 ====================

    [Fact]
    public async Task Catalog_非空已保存模板_保留身份与设置()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "catalog", "sales-order", "sales-order-export");
        var first = SeedTemplate(db, "sales-order", "默认模板", isDefault: true, fieldKeys: "[\"BillNo\",\"OrderDate\"]");
        var second = SeedTemplate(db, "sales-order", "备用模板", isDefault: false, layoutJson: "{\"cols\":[120]}");

        var result = await BuildService(db, SalesOrderCatalog()).GetCatalogAsync(user.Id);

        var family = Assert.Single(result.Families, f => f.FamilyKey == "sales-order");
        Assert.True(family.Supported);
        Assert.Equal(ReportPrintTemplateCompatibilityText.Compatible, family.CompatibilityStatus);
        Assert.Equal("bill-export:sales-order", family.DatasetKey);
        Assert.Equal(2, family.Templates.Count);

        var descriptor = family.Templates[0];
        Assert.Equal(first.Id, descriptor.Id);
        Assert.Equal("默认模板", descriptor.TemplateName);
        Assert.Equal("测试打印标题", descriptor.Title);
        Assert.True(descriptor.IsDefault);
        Assert.Equal("A4", descriptor.PaperSize);
        Assert.Equal(12, descriptor.FontSize);
        Assert.Equal("Microsoft YaHei", descriptor.FontFamily);
        Assert.True(descriptor.ShowCompanyHeader);
        Assert.True(descriptor.ShowDetailTable);
        Assert.True(descriptor.ShowRemark);
        Assert.Equal("页脚", descriptor.FooterText);
        Assert.Equal(new[] { "BillNo", "OrderDate" }, descriptor.FieldKeys);
        Assert.False(descriptor.HasGridLayout);

        Assert.Equal(second.Id, family.Templates[1].Id);
        Assert.True(family.Templates[1].HasGridLayout);
    }

    [Fact]
    public async Task Catalog_缺少专用导出菜单_精确拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "deny", "sales-order");
        SeedTemplate(db, "sales-order", "默认模板", isDefault: true);

        var result = await BuildService(db, SalesOrderCatalog()).GetCatalogAsync(user.Id);

        Assert.DoesNotContain(result.Families, f => f.FamilyKey == "sales-order");
    }

    [Fact]
    public async Task Catalog_不支持族_显式阻塞()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unsupported", "customer", "quotation");

        var result = await BuildService(db, SalesOrderCatalog()).GetCatalogAsync(user.Id);

        var customer = Assert.Single(result.Families, f => f.FamilyKey == "customer");
        Assert.False(customer.Supported);
        Assert.Equal(ReportPrintTemplateCompatibilityText.Unsupported, customer.CompatibilityStatus);
        Assert.NotEmpty(customer.UnsupportedReason);
        Assert.Empty(customer.FieldAliases);

        var quotation = Assert.Single(result.Families, f => f.FamilyKey == "quotation");
        Assert.False(quotation.Supported);
        Assert.Equal(string.Empty, quotation.DatasetKey);
    }

    [Fact]
    public async Task Catalog_无身份_抛未认证()
    {
        using var db = TestDbFactory.Create();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).GetCatalogAsync(null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }


    // ==================== 3. 绑定校验 ====================

    private static ReportPrintTemplateBindingRequest Bind(long templateId, string familyKey, params string[] fieldKeys)
        => new()
        {
            FamilyKey = familyKey,
            TemplateId = templateId,
            FieldKeys = fieldKeys.ToList(),
        };

    [Fact]
    public async Task Bind_字段顺序_保留请求顺序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "order", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var result = await BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order", "OrderDate", "BillNo"), user.Id);

        Assert.Equal(2, result.BoundColumns.Count);
        Assert.Equal("OrderDate", result.BoundColumns[0].ColumnKey);
        Assert.Equal("BillNo", result.BoundColumns[1].ColumnKey);
        Assert.Equal("bill-export:sales-order", result.DatasetKey);
    }

    [Fact]
    public async Task Bind_空字段_默认族全列顺序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "default-cols", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var result = await BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order"), user.Id);

        var expected = LegacyBillExportCatalog.Resolve("sales-order").Columns.Select(c => c.Key).ToArray();
        Assert.Equal(expected, result.BoundColumns.Select(c => c.ColumnKey).ToArray());
    }

    [Fact]
    public async Task Bind_未知字段别名_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order", "BillNo", "SecretColumn"), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Bind_别名注入_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "inject", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order", "BillNo; DROP TABLE"), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Bind_字段键超长_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "long-key", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order", new string('a', 65)), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Bind_字段数量超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "too-many", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");
        var fields = Enumerable.Range(0, 33).Select(i => "BillNo" + i).ToList();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order", fields.ToArray()), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Bind_超大LayoutJson_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "layout-big", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            new ReportPrintTemplateBindingRequest
            {
                FamilyKey = "sales-order",
                TemplateId = template.Id,
                LayoutJson = new string('x', ReportConfigurationRules.MaxSerializedBytes + 1),
            }, user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Bind_畸形LayoutJson_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "layout-bad", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            new ReportPrintTemplateBindingRequest
            {
                FamilyKey = "sales-order",
                TemplateId = template.Id,
                LayoutJson = "not-json",
            }, user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    [Fact]
    public async Task Bind_不支持族_显式阻塞()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "block", "customer");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            new ReportPrintTemplateBindingRequest { FamilyKey = "customer", TemplateId = 1 }, user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task Bind_模板归属不匹配_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "owner", "sales-order", "sales-order-export");
        var receiptTemplate = SeedTemplate(db, "receipt", "收款模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(receiptTemplate.Id, "sales-order", "BillNo"), user.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Bind_数据集不可用或未授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "no-dataset", "sales-order", "sales-order-export");
        var template = SeedTemplate(db, "sales-order", "模板");
        var emptyCatalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, emptyCatalog).BindAsync(
            Bind(template.Id, "sales-order", "BillNo"), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Bind_菜单未授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bind-deny", "sales-order");
        var template = SeedTemplate(db, "sales-order", "模板");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => BuildService(db, SalesOrderCatalog()).BindAsync(
            Bind(template.Id, "sales-order", "BillNo"), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 4. 迁移登记册集成 ====================

    [Fact]
    public async Task MigrationRegistry_打印模板族_登记且失败关闭退役()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry", "sales-order", "sales-order-export", "customer");
        var registry = new ReportMigrationRegistry(
            SalesOrderCatalog(), db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());

        var result = await registry.GetRegistryAsync(user.Id);

        var supported = Assert.Single(result.Entries, e => e.LegacyKey == "print-template:sales-order");
        Assert.Equal(ReportMigrationRegistryCategories.PrintTemplate, supported.Category);
        Assert.Equal("bill-export:sales-order", supported.DatasetKey);
        Assert.Equal(ReportMigrationParityStatusText.DatasetReady, supported.ParityStatus);

        var unsupported = Assert.Single(result.Entries, e => e.LegacyKey == "print-template:customer");
        Assert.Equal(ReportMigrationParityStatusText.Pending, unsupported.ParityStatus);

        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }
}

