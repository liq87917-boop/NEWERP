using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-280 数据库引导映射回归测试：离线验证 EF 对 <see cref="ContainerBooking"/> 的精确映射
/// （实体 → <c>db_owner.ContainerBookings</c>），并对 <see cref="SchemaUpgrader"/> 第 28 段源码做静态护栏，
/// 防止历史上「EF 建 <c>ContainerBookings</c>、升级器却改 <c>ContainerBooking</c>（单数）」的表名错配再次出现。
/// <para>全部离线，不连接 SQL Server、不执行任何 SQL / seed。</para>
/// </summary>
public class DatabaseBootstrapMappingTests
{
    /// <summary>解决方案根目录（测试程序集 bin 目录向上 5 级）。</summary>
    private static string RepoRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string SchemaUpgraderPath() => Path.Combine(
        RepoRoot(), "src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs");

    /// <summary>第 28 段补齐的订柜外贸 / 物流跟踪列（与 <see cref="ContainerBooking"/> 实体逐一对应）。</summary>
    private static readonly string[] TrackingColumns =
    {
        "ShipmentMode",
        "BillOfLadingNo",
        "ShippingOrderNo",
        "TransitPort",
        "Etd",
        "Eta",
        "Atd",
        "Ata",
        "TruckerName",
        "CustomsBrokerId",
        "CustomsBrokerName",
        "InspectionRequired",
        "InspectionDate",
        "CustomsReleaseDate",
    };

    /// <summary>EF 映射：<see cref="ContainerBooking"/> 实体必须落在 <c>db_owner.ContainerBookings</c>（复数）。</summary>
    [Fact]
    public void EF模型映射_ContainerBooking映射到db_owner_ContainerBookings()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=localhost;Database=__offline_bootstrap__;Trusted_Connection=True;Encrypt=False;")
            .Options;
        using var db = new ErpDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(ContainerBooking));
        Assert.NotNull(entityType);
        Assert.Equal("ContainerBookings", entityType!.GetTableName());
        Assert.Equal("db_owner", entityType.GetSchema());

        // 第 28 段的跟踪列全部仍映射在该实体上（离线建模即回归护栏）
        foreach (var column in TrackingColumns)
        {
            Assert.NotNull(entityType.FindProperty(column));
        }
    }

    /// <summary>升级器第 28 段：只对齐 EF 复数表名，绝不残留单数 <c>db_owner.ContainerBooking</c> 目标。</summary>
    [Fact]
    public void SchemaUpgrader第28段_只对齐ContainerBookings_不存在单数ContainerBooking表()
    {
        var source = File.ReadAllText(SchemaUpgraderPath());

        // 每一列都按复数表名做 COL_LENGTH + ALTER（与 EF 映射一致）
        foreach (var column in TrackingColumns)
        {
            Assert.Contains($"COL_LENGTH('db_owner.ContainerBookings', '{column}')", source);
            Assert.Contains($"ALTER TABLE db_owner.ContainerBookings ADD {column}", source);
        }

        // 去掉全部复数引用后，任何单数 db_owner.ContainerBooking 残留都视为回归失败
        var withoutPluralTarget = source.Replace("db_owner.ContainerBookings", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("db_owner.ContainerBooking", withoutPluralTarget, StringComparison.Ordinal);
    }

    // ==================== ERP-281：报表配置三表引导映射（离线） ====================

    /// <summary>SchemaUpgrader 第 47 段补齐的报表配置索引名（与 <see cref="ErpDbContext.Reporting"/> 映射一致）。</summary>
    private static readonly string[] ReportIndexNames =
    {
        "IX_ReportConfigurations_OwnerUserId_IsDeleted",
        "UX_ReportConfigurationRevisions_ConfigurationId_Version",
        "IX_ReportConfigurationRevisions_ConfigurationId",
        "UX_ReportConfigurationGrants_Recipient_Configuration",
        "IX_ReportConfigurationGrants_ConfigurationId_IsDeleted",
        "IX_ReportConfigurationGrants_RecipientUserId_IsDeleted",
    };

    /// <summary>EF 映射：报表配置三表必须落在 <c>db_owner</c> 下，列长 / 并发令牌 / 索引 / 级联外键与领域一致。</summary>
    [Fact]
    public void EF模型映射_报表配置三表映射到db_owner且列索引关系一致()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=localhost;Database=__offline_bootstrap__;Trusted_Connection=True;Encrypt=False;")
            .Options;
        using var db = new ErpDbContext(options);

        // 私有报表配置
        var configType = db.Model.FindEntityType(typeof(ReportConfiguration));
        Assert.NotNull(configType);
        Assert.Equal("ReportConfigurations", configType!.GetTableName());
        Assert.Equal("db_owner", configType.GetSchema());
        Assert.Equal(200, configType.FindProperty(nameof(ReportConfiguration.Name))!.GetMaxLength());
        Assert.Equal(50, configType.FindProperty(nameof(ReportConfiguration.DatasetKey))!.GetMaxLength());
        Assert.Equal("nvarchar(max)", configType.FindProperty(nameof(ReportConfiguration.DefinitionJson))!.GetColumnType());
        Assert.True(configType.FindProperty(nameof(ReportConfiguration.Version))!.IsConcurrencyToken);
        Assert.Contains(configType.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "OwnerUserId", "IsDeleted" }));

        // 不可变发布修订快照
        var revisionType = db.Model.FindEntityType(typeof(ReportConfigurationRevision));
        Assert.NotNull(revisionType);
        Assert.Equal("ReportConfigurationRevisions", revisionType!.GetTableName());
        Assert.Equal("db_owner", revisionType.GetSchema());
        Assert.Equal(200, revisionType.FindProperty(nameof(ReportConfigurationRevision.Name))!.GetMaxLength());
        Assert.Equal(50, revisionType.FindProperty(nameof(ReportConfigurationRevision.DatasetKey))!.GetMaxLength());
        Assert.Contains(revisionType.GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "ReportConfigurationId", "Version" }));
        var revisionFk = Assert.Single(revisionType.GetForeignKeys());
        Assert.Equal(typeof(ReportConfiguration), revisionFk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, revisionFk.DeleteBehavior);

        // 只读共享授权
        var grantType = db.Model.FindEntityType(typeof(ReportConfigurationGrant));
        Assert.NotNull(grantType);
        Assert.Equal("ReportConfigurationGrants", grantType!.GetTableName());
        Assert.Equal("db_owner", grantType.GetSchema());
        Assert.True(grantType.FindProperty(nameof(ReportConfigurationGrant.Version))!.IsConcurrencyToken);

        var uniqueGrantIndex = grantType.GetIndexes().Single(
            i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "RecipientUserId", "ReportConfigurationId" }));
        Assert.Equal("[IsDeleted] = 0", uniqueGrantIndex.GetFilter());

        Assert.Contains(grantType.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "ReportConfigurationId", "IsDeleted" }));
        Assert.Contains(grantType.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "RecipientUserId", "IsDeleted" }));
        var grantFk = Assert.Single(grantType.GetForeignKeys());
        Assert.Equal(typeof(ReportConfiguration), grantFk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, grantFk.DeleteBehavior);
    }

    /// <summary>升级器第 47 段：报表配置三表只做复数表名的幂等补齐，绝不残留单数 <c>db_owner.ReportConfiguration</c> 目标。</summary>
    [Fact]
    public void SchemaUpgrader第47段_报表三表幂等补齐且无单数残留()
    {
        var source = File.ReadAllText(SchemaUpgraderPath());

        Assert.Contains("IF OBJECT_ID('db_owner.ReportConfigurations') IS NULL", source);
        Assert.Contains("IF OBJECT_ID('db_owner.ReportConfigurationRevisions') IS NULL", source);
        Assert.Contains("IF OBJECT_ID('db_owner.ReportConfigurationGrants') IS NULL", source);

        foreach (var indexName in ReportIndexNames)
        {
            Assert.Contains(indexName, source);
        }

        Assert.Contains("FK_ReportConfigurationRevisions_ReportConfigurations_ReportConfigurationId", source);
        Assert.Contains("FK_ReportConfigurationGrants_ReportConfigurations_ReportConfigurationId", source);

        // 无单数 db_owner.ReportConfiguration 表目标残留（三表都是 ReportConfigurations / Revisions / Grants 后缀）
        Assert.DoesNotContain("'db_owner.ReportConfiguration'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("db_owner.ReportConfiguration ", source, StringComparison.Ordinal);
    }
}
