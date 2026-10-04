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
}
