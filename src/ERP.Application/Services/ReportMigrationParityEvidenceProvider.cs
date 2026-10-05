using System.Collections.Concurrent;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 迁移 parity 比对证据写入接缝（ERP-332 Stage 2）：<see cref="ReportMigrationParityEvidenceService"/>
/// 在成功完成一条旧报表的「旧路由 vs 通用平台」四维比对后，把完整证据写入本存储；无证据绝不写入。
/// </summary>
public interface IReportMigrationParityEvidenceStore
{
    /// <summary>记录一条旧报表的完整比对证据（只写入非空证据；空证据不写入，绝不覆盖既有 fail-closed 状态）。</summary>
    void Record(string legacyKey, ReportMigrationParityEvidenceDto evidence);
}

/// <summary>
/// 迁移 parity 比对证据源实现（ERP-332 Stage 2）：替换空的默认实现，按旧报表键（LegacyKey）提供
/// 已产出的真实四维比对证据（数据粒度 / 币种单位 / 权限 / 输出语义）。无证据返回 null（fail closed）。
/// <para>本实现只是一个有界内存缓存，绝不自己触碰数据库；证据由 <see cref="IReportMigrationParityEvidenceService"/>
/// 在完成真实比对后写入。未写入证据的条目恒 null，因此旧路由绝不会提前退役。</para>
/// </summary>
public sealed class ReportMigrationParityEvidenceProvider
    : IReportMigrationParityEvidenceProvider, IReportMigrationParityEvidenceStore
{
    private readonly ConcurrentDictionary<string, ReportMigrationParityEvidenceDto> _evidence =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ReportMigrationParityEvidenceDto? GetEvidence(string legacyKey)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            return null;

        return _evidence.TryGetValue(legacyKey, out var evidence) ? evidence : null;
    }

    /// <inheritdoc />
    public void Record(string legacyKey, ReportMigrationParityEvidenceDto evidence)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            return;

        var key = legacyKey.Trim();
        if (!ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries)
            .Any(entry => string.Equals(entry.LegacyKey, key, StringComparison.OrdinalIgnoreCase)))
            return;

        if (evidence is not { Complete: true })
        {
            _evidence.TryRemove(key, out _);
            return;
        }

        _evidence[key] = evidence;
    }
}
