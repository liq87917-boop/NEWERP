using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 数据库上下文：通用报表配置平台（ERP-260 Stage 1）实体映射。
/// <para>表仍在既有 db_owner schema 下（由 <c>OnModelCreating</c> 的 <c>HasDefaultSchema</c> 统一指定）；
/// 本分部类只提供「集合暴露 + 模型配置」的最小辅助，并在 <c>OnModelCreating</c> 内做一次最小调用。</para>
/// </summary>
public partial class ErpDbContext
{
    /// <summary>私有报表配置集合</summary>
    public DbSet<ReportConfiguration> ReportConfigurations => Set<ReportConfiguration>();

    /// <summary>私有报表配置的不可变发布修订快照集合</summary>
    public DbSet<ReportConfigurationRevision> ReportConfigurationRevisions => Set<ReportConfigurationRevision>();

    /// <summary>
    /// 报表配置与发布修订的精确 EF 映射（ERP-260）：列长 / JSON 列、owner/deleted 索引、
    /// 唯一修订元组、乐观预期版本并发令牌、级联删除关系。
    /// </summary>
    private static void ConfigureReporting(ModelBuilder modelBuilder)
    {
        // ============ 私有报表配置 ============
        modelBuilder.Entity<ReportConfiguration>().Property(x => x.Name).HasMaxLength(200).IsRequired();
        modelBuilder.Entity<ReportConfiguration>().Property(x => x.DatasetKey).HasMaxLength(50).IsRequired();
        modelBuilder.Entity<ReportConfiguration>().Property(x => x.DefinitionJson).HasColumnType("nvarchar(max)").IsRequired();
        modelBuilder.Entity<ReportConfiguration>().Property(x => x.Version).IsConcurrencyToken();

        // 个人工作区按「所有者 + 是否删除」检索（列表 / 详情 / 跨所有者拒绝）
        modelBuilder.Entity<ReportConfiguration>()
            .HasIndex(x => new { x.OwnerUserId, x.IsDeleted })
            .HasDatabaseName("IX_ReportConfigurations_OwnerUserId_IsDeleted");

        modelBuilder.Entity<ReportConfiguration>()
            .HasMany(x => x.Revisions)
            .WithOne(x => x.ReportConfiguration)
            .HasForeignKey(x => x.ReportConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        // ============ 不可变发布修订快照 ============
        modelBuilder.Entity<ReportConfigurationRevision>().Property(x => x.Name).HasMaxLength(200).IsRequired();
        modelBuilder.Entity<ReportConfigurationRevision>().Property(x => x.DatasetKey).HasMaxLength(50).IsRequired();
        modelBuilder.Entity<ReportConfigurationRevision>().Property(x => x.DefinitionJson).HasColumnType("nvarchar(max)").IsRequired();

        // 同一配置内发布版本号唯一（恢复 = 追加新版本，旧修订保持原样，永不覆盖）
        modelBuilder.Entity<ReportConfigurationRevision>()
            .HasIndex(x => new { x.ReportConfigurationId, x.Version })
            .IsUnique()
            .HasDatabaseName("UX_ReportConfigurationRevisions_ConfigurationId_Version");

        modelBuilder.Entity<ReportConfigurationRevision>()
            .HasIndex(x => x.ReportConfigurationId)
            .HasDatabaseName("IX_ReportConfigurationRevisions_ConfigurationId");
    }
}
