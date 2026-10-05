using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 报表预设模板的编译期种子（ERP-296 Stage 2）：有限、不可变；每条预设键控到一条迁移登记册条目，
/// 把已迁移旧报表重新表达为受控数据集上的可校验 <see cref="ReportConfigurationDefinition"/>。
/// <para>预设只作为「数据驱动的定义」存在，绝不新增权限 / 存储 / 每报表控制器或导出器。</para>
/// </summary>
internal sealed record ReportConfigurationPresetSeed(
    string PresetKey,
    string LegacyKey,
    string Name,
    string DatasetKey,
    ReportConfigurationDefinition Definition);

internal static class ReportConfigurationPresetManifest
{
    /// <summary>有限、不可变的预设清单（顺序稳定；与迁移登记册条目一一对应）。</summary>
    public static readonly IReadOnlyList<ReportConfigurationPresetSeed> Presets = new[]
    {
        new ReportConfigurationPresetSeed(
            "sales-order",
            "dynamic:sales-order",
            "销售订单（迁移预设）",
            ReportConfigurationConstants.DatasetSalesOrder,
            SalesOrderPresetDefinition()),
        new ReportConfigurationPresetSeed(
            "receivable",
            "dynamic:receivable",
            "客户应收账款证据（迁移预设）",
            ReportConfigurationConstants.DatasetReceivable,
            ReceivablePresetDefinition()),
    };

    private static ReportConfigurationDefinition SalesOrderPresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
        Fields = new List<string> { "orderNo", "orderDate", "customerId", "currency", "totalAmount", "status" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static ReportConfigurationDefinition ReceivablePresetDefinition() => new()
    {
        SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
        DatasetKey = ReportConfigurationConstants.DatasetReceivable,
        Fields = new List<string> { "invoiceNumber", "invoiceDate", "customerName", "currency", "grossAmount", "remainingAmount", "statusText" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };
}

/// <summary>
/// 迁移登记册预设接缝（ERP-296 Stage 2）：只回答「某旧报表是否已有预设模板」，供 <see cref="ReportMigrationRegistry"/>
/// 派生 parity 使用。无状态、只读，绝不依赖预设编排或物化结果（避免循环依赖）。
/// </summary>
public sealed class ReportMigrationPresetCatalog : IReportMigrationPresetCatalog
{
    /// <inheritdoc />
    public Task<bool> HasPresetAsync(string legacyKey, long? userId, CancellationToken cancellationToken = default)
        => Task.FromResult(
            ReportConfigurationPresetManifest.Presets.Any(p =>
                string.Equals(p.LegacyKey, legacyKey, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// 报表预设模板编排（ERP-296 Stage 2）实现：只读列出 + 私有物化。
/// <list type="number">
/// <item><b>只读列出</b>：按迁移登记册逐条重检原始菜单授权与派生 parity，并按当前账号重新校验数据集授权，
/// 未授权 / 未 ready（pending）的预设被隐藏，绝不通过模板授予权限。</item>
/// <item><b>物化</b>：先确认预设 ready（至少 dataset-ready），再对当前授权数据集重新校验定义（绝不信任预设载荷），
/// 最后经既有 <see cref="ReportConfigurationService.CreateAsync"/> 落为当前用户私有草稿。</item>
/// </list>
/// </summary>
public sealed class ReportConfigurationPresetCatalog : IReportConfigurationPresetCatalog
{
    private readonly IReportConfigurationCatalog _catalog;
    private readonly IReportMigrationRegistry _registry;
    private readonly IReportConfigurationService _service;

    public ReportConfigurationPresetCatalog(
        IReportConfigurationCatalog catalog,
        IReportMigrationRegistry registry,
        IReportConfigurationService service)
    {
        _catalog = catalog;
        _registry = registry;
        _service = service;
    }

    /// <inheritdoc />
    public async Task<List<ReportConfigurationPresetDto>> ListPresetsAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);

        var registry = await _registry.GetRegistryAsync(userId, cancellationToken);
        var byLegacy = registry.Entries.ToDictionary(e => e.LegacyKey, StringComparer.OrdinalIgnoreCase);

        var result = new List<ReportConfigurationPresetDto>();
        foreach (var preset in ReportConfigurationPresetManifest.Presets)
        {
            var dto = await TryBuildAsync(preset, byLegacy, userId!.Value, cancellationToken);
            if (dto is not null)
                result.Add(dto);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationPresetDto?> GetPresetAsync(
        string presetKey, long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);
        if (string.IsNullOrWhiteSpace(presetKey))
            return null;

        var preset = ReportConfigurationPresetManifest.Presets.FirstOrDefault(p =>
            string.Equals(p.PresetKey, presetKey.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return null;

        var registry = await _registry.GetRegistryAsync(userId, cancellationToken);
        var byLegacy = registry.Entries.ToDictionary(e => e.LegacyKey, StringComparer.OrdinalIgnoreCase);

        return await TryBuildAsync(preset, byLegacy, userId!.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDto> MaterializeAsync(
        string presetKey, long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);

        var preset = await RequireReadyPresetAsync(presetKey, userId!.Value, cancellationToken);

        // 绝不信任预设载荷：对当前授权数据集重新校验定义，缺失适配器 / 无效定义 / 撤销菜单一律 fail closed。
        var dataset = await _catalog.GetDatasetAsync(preset.DatasetKey, userId, cancellationToken)
            ?? throw BusinessException.InvalidParameter($"数据集 {preset.DatasetKey} 不存在或未授权");
        ReportConfigurationRules.Validate(preset.Definition, dataset);

        var saveDto = new ReportConfigurationSaveDto
        {
            Name = preset.Name,
            Definition = preset.Definition,
        };

        return await _service.CreateAsync(userId.Value, saveDto, cancellationToken);
    }

    private async Task<ReportConfigurationPresetDto?> TryBuildAsync(
        ReportConfigurationPresetSeed preset,
        IReadOnlyDictionary<string, ReportMigrationRegistryEntryDto> byLegacy,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!byLegacy.TryGetValue(preset.LegacyKey, out var entry))
            return null; // 原始菜单未授权，或非完整迁移登记册条目

        if (string.Equals(entry.ParityStatus, ReportMigrationParityStatusText.Pending, StringComparison.Ordinal))
            return null; // 未 ready：受控数据集适配器缺失 / 未授权

        var dataset = await _catalog.GetDatasetAsync(preset.DatasetKey, userId, cancellationToken);
        if (dataset is null)
            return null; // 数据集授权已失效（fail closed 双重校验）

        return new ReportConfigurationPresetDto
        {
            PresetKey = preset.PresetKey,
            LegacyKey = preset.LegacyKey,
            Name = preset.Name,
            DatasetKey = preset.DatasetKey,
            DatasetLabel = dataset.Label,
            ParityStatus = entry.ParityStatus,
        };
    }

    private async Task<ReportConfigurationPresetSeed> RequireReadyPresetAsync(
        string presetKey, long userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presetKey))
            throw BusinessException.NotFound("预设模板不存在");

        var preset = ReportConfigurationPresetManifest.Presets.FirstOrDefault(p =>
            string.Equals(p.PresetKey, presetKey.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.NotFound("预设模板不存在");

        var registry = await _registry.GetRegistryAsync(userId, cancellationToken);
        var entry = registry.Entries.FirstOrDefault(e =>
            string.Equals(e.LegacyKey, preset.LegacyKey, StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.NotFound("预设模板不存在或无权访问");

        if (string.Equals(entry.ParityStatus, ReportMigrationParityStatusText.Pending, StringComparison.Ordinal))
            throw BusinessException.RuleConflict("预设模板尚未就绪，暂不能物化");

        return preset;
    }

    private static void EnsureAuthenticated(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问报表预设模板", ErrorCodes.Unauthorized);
    }
}

