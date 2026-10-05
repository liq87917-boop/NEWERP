using System.Globalization;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 迁移 parity 比对证据服务（ERP-332 Stage 2）实现：对一条旧报表，在同一有界隔离夹具上
/// （当前账号菜单 + 数据范围、统一日期 / 分页参数、既有执行预算 + 取消令牌）分别运行旧来源接缝
/// <see cref="ILegacyReportSource"/> 与通用数据集适配器预览，喂给四维比较器，产出
/// <see cref="ReportMigrationParityEvidenceDto"/>；无真实比对证据返回 null（fail closed）。
/// </summary>
public sealed class ReportMigrationParityEvidenceService : IReportMigrationParityEvidenceService
{
    private readonly ILegacyReportSource _legacySource;
    private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
    private readonly IReportMigrationParityComparator _comparator;
    private readonly IReportMigrationOutputComparator _outputComparator;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly IReportMigrationParityEvidenceStore _store;
    private readonly ILegacyReportArtifactSource? _artifactSource;

    public ReportMigrationParityEvidenceService(
        ILegacyReportSource legacySource,
        IEnumerable<IReportConfigurationDatasetProvider> providers,
        IReportMigrationParityComparator comparator,
        IReportMigrationOutputComparator outputComparator,
        IReportConfigurationExecutionBudget budget,
        IReportMigrationParityEvidenceStore store,
        ILegacyReportArtifactSource? artifactSource = null)
    {
        _legacySource = legacySource ?? throw new ArgumentNullException(nameof(legacySource));
        _providers = (providers ?? Array.Empty<IReportConfigurationDatasetProvider>())
            .OrderBy(p => p.DatasetKey, StringComparer.Ordinal)
            .ToList();
        _comparator = comparator ?? throw new ArgumentNullException(nameof(comparator));
        _outputComparator = outputComparator ?? throw new ArgumentNullException(nameof(outputComparator));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _artifactSource = artifactSource;
    }

    /// <inheritdoc />
    public Task<ReportMigrationParityEvidenceDto?> GetEvidenceAsync(
        string legacyKey, long userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            return Task.FromResult<ReportMigrationParityEvidenceDto?>(null);

        var definition = ResolveDefinition(legacyKey);
        if (definition is null)
            return Task.FromResult<ReportMigrationParityEvidenceDto?>(null);

        return _budget.ExecuteAsync(
            userId,
            cancellationToken,
            lease => ComputeAsync(definition, userId, lease.Token));
    }

    private async Task<ReportMigrationParityEvidenceDto?> ComputeAsync(
        ReportMigrationRegistryEntryDefinition definition, long userId, CancellationToken cancellationToken)
    {
        var request = BuildRequest(definition.LegacyKey, userId);

        LegacyReportSourceResult legacyResult;
        try
        {
            legacyResult = await _legacySource.ReadAsync(request, cancellationToken);
        }
        catch (BusinessException)
        {
            return null;
        }

        if (legacyResult.Status != LegacyReportSourceStatus.Success || legacyResult.Snapshot is null)
            return null;

        var legacy = legacyResult.Snapshot;

        var provider = FindProvider(definition.DatasetKey);
        if (provider is null)
            return null;

        ReportConfigurationDatasetDto? dataset;
        try
        {
            dataset = await provider.GetDatasetAsync(userId, cancellationToken);
        }
        catch (BusinessException)
        {
            return null;
        }

        if (dataset is null)
            return null;

        var mappings = BuildFieldMappings(legacy.Columns, dataset);
        if (mappings is null || mappings.Count == 0)
            return null;

        var genericDefinition = BuildGenericDefinition(definition.DatasetKey, mappings);
        var parameters = new ReportConfigurationPreviewParameters(
            1, 200, ReportConfigurationConstants.GroupNone, null, null);

        ReportConfigurationPreviewDto preview;
        try
        {
            preview = await provider.PreviewAsync(genericDefinition, parameters, userId, cancellationToken);
        }
        catch (BusinessException)
        {
            return null;
        }

        if (preview is null || preview.Rows is null)
            return null;

        var generic = ToSnapshot(preview, legacy, mappings);

        LegacyReportArtifactBytesDto? legacyArtifacts = null;
        if (_artifactSource is not null)
        {
            try
            {
                legacyArtifacts = await _artifactSource.ReadArtifactsAsync(request, cancellationToken);
            }
            catch (BusinessException)
            {
                legacyArtifacts = null;
            }
        }

        if ((definition.ExcelCompatible || definition.PdfCompatible) && legacyArtifacts is null)
            return null;

        var output = _outputComparator.Compare(
            BuildNormalizedPreview(preview, legacy, mappings),
            legacy,
            definition.ExcelCompatible,
            definition.PdfCompatible,
            legacyArtifacts: legacyArtifacts);

        var comparison = _comparator.Compare(legacy, generic, output.OutputSemanticsMatched);
        var evidence = comparison.Evidence;

        if (evidence.Complete)
            _store.Record(definition.LegacyKey, evidence);

        return evidence;
    }


    // ==================== 清单 / 提供程序解析 ====================

    private static ReportMigrationRegistryEntryDefinition? ResolveDefinition(string? legacyKey)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            return null;

        var key = legacyKey.Trim();
        foreach (var definition in ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries))
        {
            if (string.Equals(definition.LegacyKey, key, StringComparison.OrdinalIgnoreCase))
                return definition;
        }

        return null;
    }

    private IReportConfigurationDatasetProvider? FindProvider(string? datasetKey)
    {
        if (string.IsNullOrWhiteSpace(datasetKey))
            return null;

        return _providers.FirstOrDefault(p =>
            string.Equals(p.DatasetKey, datasetKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 有界隔离夹具 ====================

    private static LegacyReportSourceRequest BuildRequest(string legacyKey, long userId)
        => new()
        {
            LegacyKey = legacyKey,
            UserId = userId,
            Page = 1,
            PageSize = 200,
            Top = 200,
            AheadDays = 7,
        };

    private static ReportConfigurationDefinition BuildGenericDefinition(
        string datasetKey, IReadOnlyList<FieldMapping> mappings)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = mappings.Select(m => m.FieldKey).ToList(),
        };


    // ==================== 旧列键 -> 通用字段键对齐 ====================

    private sealed record FieldMapping(string LegacyKey, string FieldKey, string Type, string Label);

    private static List<FieldMapping>? BuildFieldMappings(
        IReadOnlyList<ReportMigrationParityColumnDto> legacyColumns,
        ReportConfigurationDatasetDto dataset)
    {
        var mappings = new List<FieldMapping>(legacyColumns.Count);
        foreach (var column in legacyColumns)
        {
            var field = FindField(dataset, column.Key);
            if (field is null)
                return null;

            mappings.Add(new FieldMapping(column.Key, field.Key, field.Type, field.Label));
        }

        return mappings;
    }

    private static ReportConfigurationFieldDto? FindField(ReportConfigurationDatasetDto dataset, string legacyKey)
    {
        foreach (var field in dataset.Fields)
        {
            if (string.Equals(field.Key, legacyKey, StringComparison.Ordinal))
                return field;
        }

        foreach (var field in dataset.Fields)
        {
            if (string.Equals(field.Key, legacyKey, StringComparison.OrdinalIgnoreCase))
                return field;
        }

        var camel = ToCamelCase(legacyKey);
        foreach (var field in dataset.Fields)
        {
            if (string.Equals(field.Key, camel, StringComparison.Ordinal))
                return field;
        }

        var pascal = ToPascalCase(legacyKey);
        foreach (var field in dataset.Fields)
        {
            if (string.Equals(field.Key, pascal, StringComparison.Ordinal))
                return field;
        }

        return null;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsLower(value[0]))
            return value;
        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value) || char.IsUpper(value[0]))
            return value;
        return char.ToUpperInvariant(value[0]) + value[1..];
    }


    // ==================== 通用预览 -> 有界结果快照 ====================

    private static readonly string[] RowKeyPriority =
    {
        "BillNo", "billNo", "Id", "id", "OrderId", "orderId", "CustomerId", "customerId",
        "ProductId", "TradeDocumentId", "SupplierId", "OrderNo", "DocNo", "RefundPeriod",
        "BucketKey", "SalesmanName", "Name", "FollowNo", "Oid", "invoiceId", "purchaseOrderId",
        "tradeDocumentId",
    };

    private static readonly string[] IdPriority =
    {
        "Id", "CustomerId", "OrderId", "ProductId", "TradeDocumentId", "SupplierId",
    };

    private static ReportMigrationParitySnapshotDto ToSnapshot(
        ReportConfigurationPreviewDto preview,
        ReportMigrationParitySnapshotDto legacy,
        IReadOnlyList<FieldMapping> mappings)
    {
        var columns = mappings
            .Select(m => new ReportMigrationParityColumnDto(m.LegacyKey, m.Type, null, null))
            .ToList();

        var rows = new List<ReportMigrationParityRowDto>(preview.Rows.Count);
        for (var i = 0; i < preview.Rows.Count; i++)
        {
            var row = preview.Rows[i];
            var cells = mappings
                .Select(m => TryGet(row, m.FieldKey, out var value) ? value : null)
                .ToList();

            rows.Add(new ReportMigrationParityRowDto(
                new[] { DeriveRowKey(row, cells, i) },
                Get(row, "currency"),
                Get(row, "unit"),
                cells));
        }

        var permissions = new ReportMigrationParityPermissionsDto(
            OwnedIdsFromDicts(preview.Rows),
            legacy.Permissions.DataScopeFingerprint);

        return new ReportMigrationParitySnapshotDto(columns, rows, permissions);
    }

    private static ReportConfigurationPreviewDto BuildNormalizedPreview(
        ReportConfigurationPreviewDto preview,
        ReportMigrationParitySnapshotDto legacy,
        IReadOnlyList<FieldMapping> mappings)
    {
        var labelByKey = mappings.ToDictionary(m => m.LegacyKey, m => m.Label, StringComparer.Ordinal);
        var columns = legacy.Columns
            .Select(c => new ReportConfigurationColumnDto(
                c.Key,
                labelByKey.TryGetValue(c.Key, out var label) ? label : c.Key,
                c.Type,
                null))
            .ToList();

        var rows = preview.Rows
            .Select(row =>
            {
                var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var mapping in mappings)
                    normalized[mapping.LegacyKey] = TryGet(row, mapping.FieldKey, out var value) ? value : null;
                return normalized;
            })
            .ToList();

        return new ReportConfigurationPreviewDto
        {
            Name = "通用预览",
            DatasetKey = preview.DatasetKey,
            Columns = columns,
            Rows = rows,
            Total = rows.Count,
            Page = 1,
            PageSize = rows.Count,
            TotalPages = 1,
            GroupBy = ReportConfigurationConstants.GroupNone,
        };
    }


    private static string DeriveRowKey(Dictionary<string, object?> row, IReadOnlyList<object?> cells, int index)
    {
        foreach (var key in RowKeyPriority)
        {
            if (TryGet(row, key, out var value) && !IsBlank(value))
                return ToInvariant(value);
        }

        foreach (var cell in cells)
        {
            if (!IsBlank(cell))
                return ToInvariant(cell);
        }

        return $"row:{index}";
    }

    private static IReadOnlyList<long> OwnedIdsFromDicts(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var ids = new List<long>();
        foreach (var row in rows)
        {
            foreach (var key in IdPriority)
            {
                if (TryGet(row, key, out var value) && ToLong(value) is { } id && id != 0)
                {
                    ids.Add(id);
                    break;
                }
            }
        }

        return ids.Distinct().OrderBy(x => x).ToList();
    }

    private static string? Get(Dictionary<string, object?> row, string name)
    {
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value is null ? null : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool TryGet(Dictionary<string, object?> row, string name, out object? value)
    {
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static long? ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        _ => null,
    };

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        byte b => b == 0,
        short s => s == 0,
        int i => i == 0,
        long l => l == 0,
        decimal d => d == 0,
        double d => d == 0,
        float f => f == 0,
        _ => false,
    };

    private static string ToInvariant(object? value)
        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}

