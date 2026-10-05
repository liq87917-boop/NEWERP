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
/// ERP-317 基础资料打印族迁移单元测试：七个基础资料打印族的受控目录 / 数据集适配器 / 迁移预设与登记册集成。
/// 覆盖目录映射、字段键 / 顺序 / 标签 / null / 软删除、有界分页、客户业务员数据范围（fail closed）、
/// 撤销菜单立即收敛、未知字段 / 筛选精确拒绝，以及七个迁移预设与 parity 派生。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationMasterDataMigrationTests
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, bool privileged, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = new SysRole { RoleName = name + "-role", RoleCode = name + "-role", IsSystem = privileged };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            Department = "销售部",
            Position = "业务员",
            IsSalesman = isSalesman,
            Status = 1,
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            EmpId = empId,
            IsDeleted = deleted,
            Status = 1,
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string name, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = name,
            Spec = "标准",
            Unit = "PCS",
            SalePrice = 10m,
            CostPrice = 6m,
            IsDeleted = deleted,
            Status = 1,
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new(ReportConfigurationMasterDataCatalog.Families
            .Select(f => (IReportConfigurationDatasetProvider)new MasterDataReportConfigurationDatasetProvider(db, f.DatasetKey))
            .ToList());

    private static ReportConfigurationDefinition Definition(string familyKey, IReadOnlyList<string>? fields = null)
    {
        var family = ReportConfigurationMasterDataCatalog.Resolve(familyKey);
        return new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = family.DatasetKey,
            Fields = (fields ?? family.Columns.Select(c => c.Key).ToList()).ToList(),
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
        };
    }

    private static ReportConfigurationPreviewParameters Params(int page = 1, int pageSize = 100)
        => new(page, pageSize, ReportConfigurationConstants.GroupNone, null, null);

    // ==================== 1. 受控目录映射 ====================

    [Fact]
    public void 目录_七个基础资料族_均受控且映射非空数据集与别名()
    {
        var masters = new[] { "customer", "supplier", "employee", "expense-account", "warehouse", "product", "other-info" };
        Assert.Equal(masters.Length, ReportConfigurationMasterDataCatalog.Families.Count);

        foreach (var key in masters)
        {
            var family = ReportConfigurationMasterDataCatalog.Resolve(key);
            Assert.Equal(key, family.FamilyKey);
            Assert.Equal(ReportConfigurationMasterDataCatalog.DatasetKeyPrefix + key, family.DatasetKey);
            Assert.Equal(new[] { key }, family.RequiredMenuCodes.ToArray());
            Assert.NotEmpty(family.Columns);

            // 每个受控打印别名都有稳定字段键 / 标签 / 类型 / 实体属性映射
            Assert.All(family.Columns, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Key));
                Assert.False(string.IsNullOrWhiteSpace(c.Title));
                Assert.False(string.IsNullOrWhiteSpace(c.Type));
                Assert.False(string.IsNullOrWhiteSpace(c.Property));
            });
        }
    }

    [Fact]
    public async Task 目录_七个基础资料数据集_字段键顺序与标签一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "catalog-all", privileged: true,
            "customer", "supplier", "employee", "expense-account", "warehouse", "product", "other-info");

        foreach (var family in ReportConfigurationMasterDataCatalog.Families)
        {
            var provider = new MasterDataReportConfigurationDatasetProvider(db, family.DatasetKey);
            var dataset = await provider.GetDatasetAsync(user.Id);

            Assert.NotNull(dataset);
            Assert.Equal(family.DatasetKey, dataset.DatasetKey);
            Assert.Equal(family.Title, dataset.Label);
            Assert.Equal(family.RequiredMenuCodes[0], dataset.RequiredMenuCode);
            Assert.Equal(ReportConfigurationMasterDataCatalog.CurrencyUnitSemantics, dataset.CurrencyUnitSemantics);
            Assert.Equal(family.Columns.Select(c => c.Key).ToArray(), dataset.Fields.Select(f => f.Key).ToArray());
            Assert.Equal(family.Columns.Select(c => c.Title).ToArray(), dataset.Fields.Select(f => f.Label).ToArray());
        }
    }

    // ==================== 2. 预览：null / 软删除 / 有界分页 / 顺序 ====================

    [Fact]
    public async Task 客户预览_软删除过滤与null保留()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "cust-preview", privileged: true, "customer");
        SeedCustomer(db, "C-1", "客户一");
        SeedCustomer(db, "C-2", "客户二", deleted: true);

        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("customer"), Params(), user.Id);

        Assert.Equal(1, preview.Total);
        var row = Assert.Single(preview.Rows);
        Assert.Equal("C-1", (string)row["customerCode"]!);
        Assert.Null(row["creditDays"]);
    }

    [Fact]
    public async Task 商品预览_有界分页与主键倒序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "prod-preview", privileged: true, "product");
        SeedProduct(db, "P-1", "商品一");
        SeedProduct(db, "P-2", "商品二");
        SeedProduct(db, "P-3", "商品三");

        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("product").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("product"), Params(pageSize: 2), user.Id);

        Assert.Equal(3, preview.Total);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal("P-3", (string)preview.Rows[0]["productCode"]!);
        Assert.Equal(10m, (decimal)preview.Rows[0]["salePrice"]!);
    }

    // ==================== 3. 授权 / 数据范围 fail closed ====================

    [Fact]
    public async Task 撤销菜单_目录返回null且预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoked", privileged: false, "supplier");

        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey);

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.PreviewAsync(Definition("customer"), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 客户数据范围_受限制业务员仅见分配客户_未映射为空()
    {
        using var db = TestDbFactory.Create();
        var assignedUser = SeedAuthorizedUser(db, "salesman01", privileged: false, "customer");
        var unassignedUser = SeedAuthorizedUser(db, "salesman02", privileged: false, "customer");

        var employee = SeedEmployee(db, "salesman01", isSalesman: true);
        SeedCustomer(db, "C-1", "分配客户", empId: employee.Id);
        SeedCustomer(db, "C-2", "他人客户");

        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey);

        var preview = await provider.PreviewAsync(Definition("customer"), Params(), assignedUser.Id);
        Assert.Equal(1, preview.Total);
        Assert.Equal("C-1", (string)preview.Rows.Single()["customerCode"]!);

        var empty = await provider.PreviewAsync(Definition("customer"), Params(), unassignedUser.Id);
        Assert.Equal(0, empty.Total);
        Assert.Empty(empty.Rows);
    }

    // ==================== 4. 未知字段 / 筛选精确拒绝 ====================

    [Fact]
    public async Task 未知字段与筛选_精确拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown", privileged: true, "customer");

        var provider = new MasterDataReportConfigurationDatasetProvider(
            db, ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey);

        var exField = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition("customer", new[] { "customerCode", "SecretColumn" }), Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exField.Code);

        var definition = Definition("customer");
        definition.Filters.Add(new ReportConfigurationFilter
        {
            FieldKey = "customerCode",
            Operator = ReportConfigurationConstants.OperatorEq,
            Value = "C-1",
        });
        var exFilter = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.PreviewAsync(definition, Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, exFilter.Code);
    }

    // ==================== 5. 迁移预设与登记册集成 ====================

    [Theory]
    [InlineData("print-template:customer")]
    [InlineData("print-template:supplier")]
    [InlineData("print-template:employee")]
    [InlineData("print-template:expense-account")]
    [InlineData("print-template:warehouse")]
    [InlineData("print-template:product")]
    [InlineData("print-template:other-info")]
    public async Task 迁移预设_七个基础资料打印族均已注册(string legacyKey)
    {
        var presets = new ReportMigrationPresetCatalog();

        Assert.True(await presets.HasPresetAsync(legacyKey, userId: 1));
    }

    [Fact]
    public async Task 迁移登记册_基础资料族_经通用目录暴露_且遗留打印入口保持pending()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry", privileged: true, "customer", "product");
        var catalog = BuildCatalog(db);

        // 迁移可用性：受控基础资料数据集经通用目录暴露（mapping availability）
        var customerDataset = await catalog.GetDatasetAsync(
            ReportConfigurationMasterDataCatalog.Resolve("customer").DatasetKey, user.Id);
        Assert.NotNull(customerDataset);
        var productDataset = await catalog.GetDatasetAsync(
            ReportConfigurationMasterDataCatalog.Resolve("product").DatasetKey, user.Id);
        Assert.NotNull(productDataset);

        // 遗留打印入口保持不变（mapping availability 不标记迁移 parity 通过；fail-closed 退役）
        var registry = new ReportMigrationRegistry(
            catalog, db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());
        var result = await registry.GetRegistryAsync(user.Id);

        var customer = Assert.Single(result.Entries, e => e.LegacyKey == "print-template:customer");
        Assert.Equal(ReportMigrationParityStatusText.Pending, customer.ParityStatus);

        var product = Assert.Single(result.Entries, e => e.LegacyKey == "print-template:product");
        Assert.Equal(ReportMigrationParityStatusText.Pending, product.ParityStatus);

        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }
}


