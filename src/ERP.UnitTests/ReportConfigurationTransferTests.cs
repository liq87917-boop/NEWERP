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
/// ERP-276 可移植报表定义导入 / 导出单元测试（Stage 1，纯内存）：覆盖自有草稿 / 自有发布修订 /
/// 共享固定快照导出（信封只含格式 / 版本 / 名称 / 定义，不含身份 / 授权 / 历史），撤销后导出失败关闭，
/// 严格导入校验（大小 / 深度 / 顶层键 / 未知字段 / 身份共享状态 / SQL 脚本载荷 / 数据集授权），
/// 以及 base / 分组 / 透视 / 公式 / 关系定义的端到端内存往返。
/// <para>只使用内存数据库与既有数据集适配器，不连接 SQL Server、不执行 SQL。</para>
/// </summary>
public class ReportConfigurationTransferTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ==================== 0. 脚手架 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName, UserStatus status = UserStatus.Enabled)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = status,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string suffix)
    {
        var role = new SysRole
        {
            RoleName = $"Role-{suffix}",
            RoleCode = $"Role-{suffix}-{Guid.NewGuid():N}",
            IsSystem = false,
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

    private static long SeedAuthorizedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, menuCode);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
        return user.Id;
    }

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(BuildProviders(db));

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildCatalog(db));

    private static IReportConfigurationSharingService BuildSharing(ErpDbContext db)
        => new ReportConfigurationSharingService(db, BuildCatalog(db));

    private static IReportConfigurationTransferService BuildTransfer(ErpDbContext db)
        => new ReportConfigurationTransferService(db, BuildCatalog(db), BuildService(db));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static string EnvelopeJson(ReportConfigurationTransferEnvelopeDto envelope)
        => JsonSerializer.Serialize(envelope, JsonOptions);

    private static string EnvelopeJson(string name, ReportConfigurationDefinition definition)
        => EnvelopeJson(new ReportConfigurationTransferEnvelopeDto
        {
            Format = ReportConfigurationConstants.TransferFormat,
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            Name = name,
            Definition = definition,
        });

    // ==================== 0.1 有界定义样本 ====================

    private static ReportConfigurationDefinition BaseDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
        Fields = new List<string> { "orderNo", "totalAmount", "currency" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string>(),
    };

    private static ReportConfigurationDefinition GroupedDefinition()
    {
        var definition = BaseDefinition();
        definition.Grouping = new List<string> { ReportConfigurationConstants.GroupCustomer };
        definition.Aggregates = new List<ReportConfigurationAggregate>
        {
            new() { Function = ReportConfigurationConstants.AggregateSum, FieldKey = "totalAmount" },
        };
        return definition;
    }

    private static ReportConfigurationDefinition PivotDefinition()
    {
        var definition = BaseDefinition();
        definition.Aggregates = new List<ReportConfigurationAggregate>
        {
            new() { Function = ReportConfigurationConstants.AggregateSum, FieldKey = "totalAmount" },
        };
        definition.Pivot = new ReportConfigurationPivotDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            RowDimension = ReportConfigurationConstants.GroupCustomer,
            ColumnDimension = ReportConfigurationConstants.GroupMonth,
        };
        return definition;
    }

    private static ReportConfigurationDefinition FormulaDefinition()
    {
        var definition = BaseDefinition();
        definition.Fields = new List<string> { "orderNo", "totalAmount" };
        definition.ComputedColumns = new List<ReportConfigurationComputedColumn>
        {
            new()
            {
                Key = "netAmount",
                Label = "净额",
                Expression = new ReportConfigurationFormulaNode
                {
                    Kind = ReportConfigurationFormulaRules.NodeField,
                    FieldKey = "totalAmount",
                },
            },
        };
        return definition;
    }

    private static ReportConfigurationDefinition RelationalDefinition()
    {
        var definition = BaseDefinition();
        definition.Fields = new List<string> { "orderNo", "totalAmount" };
        definition.Relations = new List<ReportConfigurationRelationSelection>
        {
            new()
            {
                RelationKey = ReportConfigurationConstants.RelationCustomer,
                Fields = new List<string>
                {
                    ReportConfigurationRelationRules.FieldCustomerCode,
                    ReportConfigurationRelationRules.FieldCountry,
                },
            },
        };
        return definition;
    }

    // ==================== 1. 导出 ====================

    [Fact]
    public async Task 导出_自有草稿_信封只含格式版本名称与定义_不含身份授权历史()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var transfer = BuildTransfer(db);

        var created = await service.CreateAsync(owner, SaveDto("我的报表", BaseDefinition()));

        var envelope = await transfer.ExportAsync(owner,
            new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id });

        Assert.Equal(ReportConfigurationConstants.TransferFormat, envelope.Format);
        Assert.Equal(ReportConfigurationRules.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal("我的报表", envelope.Name);
        Assert.NotNull(envelope.Definition);

        var json = EnvelopeJson(envelope);
        Assert.DoesNotContain("ownerUserId", json);
        Assert.DoesNotContain("grantedByUserId", json);
        Assert.DoesNotContain("recipientUserId", json);
        Assert.DoesNotContain("createdAt", json);
        Assert.DoesNotContain("updatedAt", json);
        Assert.DoesNotContain("publishedAt", json);
        Assert.DoesNotContain("revisions", json);
        Assert.DoesNotContain("definitionJson", json);
    }

    [Fact]
    public async Task 导出_自有发布修订_返回固定快照定义()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var transfer = BuildTransfer(db);

        var created = await service.CreateAsync(owner, SaveDto("报表", BaseDefinition()));
        await service.PublishAsync(owner, created.Id, created.Version);
        await service.UpdateAsync(owner, created.Id, 2, SaveDto("报表", GroupedDefinition()));

        var envelope = await transfer.ExportAsync(owner,
            new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id, RevisionVersion = 1 });

        Assert.Equal("报表", envelope.Name);
        Assert.Equal(new List<string> { "none" }, envelope.Definition!.Grouping);
    }

    [Fact]
    public async Task 导出_共享固定发布快照_成功且撤销后失败关闭()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);
        var transfer = BuildTransfer(db);

        var created = await service.CreateAsync(owner, SaveDto("共享报表", BaseDefinition()));
        await service.PublishAsync(owner, created.Id, created.Version);
        await sharing.GrantAsync(owner, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var envelope = await transfer.ExportAsync(recipient,
            new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id });
        Assert.Equal("共享报表", envelope.Name);
        Assert.NotNull(envelope.Definition);

        var grant = Assert.Single(db.ReportConfigurationGrants.Where(g => !g.IsDeleted));
        await sharing.RevokeAsync(owner, created.Id, recipient, grant.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ExportAsync(recipient,
                new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 导出_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var stranger = SeedAuthorizedUser(db, "stranger", "sales-order");
        var service = BuildService(db);
        var transfer = BuildTransfer(db);

        var created = await service.CreateAsync(owner, SaveDto("报表", BaseDefinition()));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ExportAsync(stranger,
                new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 2. 导入 ====================

    [Fact]
    public async Task 导入_有效信封_创建接收人私有草稿且不覆盖不发布()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var transfer = BuildTransfer(db);

        var existing = await service.CreateAsync(recipient, SaveDto("已有报表", BaseDefinition()));
        var json = EnvelopeJson("导入报表", GroupedDefinition());

        var imported = await transfer.ImportAsync(recipient,
            new ReportConfigurationTransferImportRequest { Json = json });

        Assert.NotEqual(existing.Id, imported.Id);
        Assert.Equal(recipient, imported.OwnerUserId);
        Assert.Equal(ReportConfigurationStatus.Draft, imported.Status);
        Assert.Equal(1, imported.Version);
        Assert.Equal(0, imported.CurrentPublishedVersion);

        Assert.Equal(2, db.ReportConfigurations.Count(c => !c.IsDeleted));
        var existingEntity = await service.GetAsync(recipient, existing.Id);
        Assert.Equal("已有报表", existingEntity.Name);
    }

    [Fact]
    public async Task 导入_失败不写入任何持久化定义()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var json = EnvelopeJson("导入报表", BaseDefinition())
            .Replace("\"definition\":", "\"definitionX\":");

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient,
                new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_未知顶层键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var json = "{\"format\":\"report-configuration\",\"schemaVersion\":1,\"name\":\"x\",\"definition\":"
            + JsonSerializer.Serialize(BaseDefinition(), JsonOptions) + ",\"extra\":1}";

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_重复顶层键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var json = "{\"format\":\"report-configuration\",\"schemaVersion\":1,\"name\":\"x\",\"name\":\"y\",\"definition\":"
            + JsonSerializer.Serialize(BaseDefinition(), JsonOptions) + "}";

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_恶意SQL脚本载荷_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var json = EnvelopeJson("select * from orders", BaseDefinition());

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_超64KiB_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var json = "{\"format\":\"report-configuration\",\"schemaVersion\":1,\"name\":\""
            + new string('a', 70_000) + "\",\"definition\":{\"schemaVersion\":1,\"datasetKey\":\"sales-order\",\"fields\":[]}}";

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_深度超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var deep = new string('[', 40) + new string(']', 40);
        var definition = "{\"schemaVersion\":1,\"datasetKey\":\"sales-order\",\"fields\":[],"
            + "\"filters\":[{\"fieldKey\":\"orderNo\",\"operator\":\"eq\",\"value\":" + deep + "}]}";
        var json = "{\"format\":\"report-configuration\",\"schemaVersion\":1,\"name\":\"x\",\"definition\":"
            + definition + "}";

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_身份或共享状态字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var definition = "{\"schemaVersion\":1,\"datasetKey\":\"sales-order\",\"fields\":[\"orderNo\"],\"ownerUserId\":7}";
        var json = "{\"format\":\"report-configuration\",\"schemaVersion\":1,\"name\":\"x\",\"definition\":"
            + definition + "}";

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_失去数据集授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "no-menu");
        var transfer = BuildTransfer(db);

        var json = EnvelopeJson("导入报表", BaseDefinition());

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(user.Id, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task 导入_未授权字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var transfer = BuildTransfer(db);

        var bad = BaseDefinition();
        bad.Fields = new List<string> { "secretColumn" };
        var json = EnvelopeJson("未授权字段", bad);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            transfer.ImportAsync(recipient, new ReportConfigurationTransferImportRequest { Json = json }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    // ==================== 3. 端到端内存往返 ====================

    [Fact]
    public async Task 往返_基类分组透视公式关系定义_导入后与导出定义一致()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var transfer = BuildTransfer(db);

        var samples = new (string Name, ReportConfigurationDefinition Definition)[]
        {
            ("基础", BaseDefinition()),
            ("分组", GroupedDefinition()),
            ("透视", PivotDefinition()),
            ("公式", FormulaDefinition()),
            ("关系", RelationalDefinition()),
        };

        foreach (var (name, definition) in samples)
        {
            var created = await service.CreateAsync(owner, SaveDto(name, definition));
            var envelope = await transfer.ExportAsync(owner,
                new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id });
            var json = EnvelopeJson(envelope);

            var imported = await transfer.ImportAsync(recipient,
                new ReportConfigurationTransferImportRequest { Json = json });
            var loaded = await service.GetAsync(recipient, imported.Id);

            Assert.Equal(
                JsonSerializer.Serialize(definition, JsonOptions),
                JsonSerializer.Serialize(loaded.Definition, JsonOptions));
        }
    }

    [Fact]
    public async Task 往返_共享固定快照_导出再导入为接收人草稿()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);
        var transfer = BuildTransfer(db);

        var created = await service.CreateAsync(owner, SaveDto("共享报表", BaseDefinition()));
        await service.PublishAsync(owner, created.Id, created.Version);
        await sharing.GrantAsync(owner, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var envelope = await transfer.ExportAsync(recipient,
            new ReportConfigurationTransferExportRequest { ConfigurationId = created.Id });
        var imported = await transfer.ImportAsync(recipient,
            new ReportConfigurationTransferImportRequest { Json = EnvelopeJson(envelope) });
        var loaded = await service.GetAsync(recipient, imported.Id);

        Assert.Equal(recipient, imported.OwnerUserId);
        Assert.Equal(ReportConfigurationStatus.Draft, imported.Status);
        Assert.Equal(
            JsonSerializer.Serialize(envelope.Definition, JsonOptions),
            JsonSerializer.Serialize(loaded.Definition, JsonOptions));
    }

    // ==================== 4. 前端接线静态契约 ====================

    [Fact]
    public void 前端接线_传输下载上传控件与失败保留未保存编辑()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("onclick=\"rccExportDefinition()\"", js);
        Assert.Contains("function rccExportDefinition()", js);
        Assert.Contains("function rccImportDefinition(json)", js);
        Assert.Contains("function rccOnImportFile(input)", js);
        Assert.Contains("rcc-import-file", js);
        Assert.Contains("RCC_API + '/transfer/export'", js);
        Assert.Contains("RCC_API + '/transfer/import'", js);
        Assert.Contains("a.download = '报表定义_'", js);
        Assert.Contains("RCC.lastAction = 'exportDefinition'", js);
        Assert.Contains("RCC.lastAction = 'importDefinition'", js);
        Assert.DoesNotContain("localStorage.setItem('rcc", js);
    }

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));
}
