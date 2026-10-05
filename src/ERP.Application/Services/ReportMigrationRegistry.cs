using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 报表迁移登记册（ERP-295 Stage 2）实现：编译期清单 + 运行时派生。
/// <list type="number">
/// <item><b>菜单授权重检</b>：每次调用都按当前账号重新加载既有菜单授权，未声明菜单（空清单）或任一必需菜单缺失的条目被隐藏（fail closed，绝不泄露未授权报表的存在性）。</item>
/// <item><b>parity 派生</b>：从注册的数据集适配器目录 + 预设目录 + 逐条声明的兼容性清单派生 parity。</item>
/// <item><b>旧路由门控消费</b>：只有全部条目 parity-passed 才允许旧路由退役。</item>
/// </list>
/// </summary>
public sealed class ReportMigrationRegistry : IReportMigrationRegistry
{
    private readonly IReportConfigurationCatalog _catalog;
    private readonly IErpDbContext _db;
    private readonly IReportMigrationPresetCatalog _presets;
    private readonly IReportMigrationParityEvidenceProvider? _evidence;
    private readonly IReportMigrationParityEvidenceService? _evidenceService;
    private readonly IReportConfigurationBundlePresetCatalog? _bundlePresets;

    public ReportMigrationRegistry(
        IReportConfigurationCatalog catalog,
        IErpDbContext db,
        IReportMigrationPresetCatalog presets,
        IReportMigrationParityEvidenceProvider? evidence = null,
        IReportConfigurationBundlePresetCatalog? bundlePresets = null,
        IReportMigrationParityEvidenceService? evidenceService = null)
    {
        _catalog = catalog;
        _db = db;
        _presets = presets;
        _evidence = evidence;
        _evidenceService = evidenceService;
        _bundlePresets = bundlePresets;
    }

    /// <inheritdoc />
    public async Task<ReportMigrationRegistryDto> GetRegistryAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(userId);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId!.Value);

        var entries = new List<ReportMigrationRegistryEntryDto>();
        foreach (var definition in ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries))
        {
            if (!IsMenuAuthorized(definition, menuCodes))
                continue;

            var parity = await DeriveParityAsync(definition, userId.Value, cancellationToken);
            entries.Add(new ReportMigrationRegistryEntryDto(
                definition.LegacyKey,
                definition.Title,
                definition.Category,
                definition.DatasetKey,
                definition.RequiredMenuCodes,
                definition.RequiredMenuText,
                definition.CurrencyUnitSemantics,
                definition.ExcelCompatible,
                definition.PdfCompatible,
                ReportMigrationParityStatusText.Of(parity)));
        }

        var retirable = await CanRetireLegacyRoutesCoreAsync(userId, cancellationToken);
        return new ReportMigrationRegistryDto(
            ReportConfigurationRules.CurrentSchemaVersion,
            entries,
            retirable);
    }

    /// <inheritdoc />
    public Task<bool> CanRetireLegacyRoutesAsync(long? userId, CancellationToken cancellationToken = default)
        => CanRetireLegacyRoutesCoreAsync(userId, cancellationToken);

    /// <summary>全量清单逐条派生：任一未 parity-passed（或无身份）即不可退役。</summary>
    private async Task<bool> CanRetireLegacyRoutesCoreAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is null or <= 0)
            return false;

        foreach (var definition in ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries))
        {
            var parity = await DeriveParityAsync(definition, userId.Value, cancellationToken);
            if (parity != ReportMigrationParityStatus.ParityPassed)
                return false;
        }

        return true;
    }

    /// <summary>从注册的数据集适配器目录 + 预设目录 + 逐条兼容性清单派生 parity（单调递进）。</summary>
    private async Task<ReportMigrationParityStatus> DeriveParityAsync(
        ReportMigrationRegistryEntryDefinition definition,
        long userId,
        CancellationToken cancellationToken)
    {
        var dataset = await _catalog.GetDatasetAsync(definition.DatasetKey, userId, cancellationToken);
        if (dataset is null)
            return await DeriveBundleParityAsync(definition, userId, cancellationToken);

        var presetExists = await _presets.HasPresetAsync(definition.LegacyKey, userId, cancellationToken);
        if (!presetExists)
            return ReportMigrationParityStatus.DatasetReady;

        if (!SemanticsMatch(definition, dataset) || !CompatibilityDeclared(definition))
            return ReportMigrationParityStatus.PresetReady;

        // ERP-296：parity-passed 需要真实「旧路由 vs 通用平台」夹具比对证据（数据/粒度、币种/单位、权限、导出语义）；
        // 目录存在 / 预设存在 / 兼容性声明绝不构成 parity；证据缺失或任一维度缺失一律停在 preset-ready（fail closed）。
        var evidence = _evidenceService is not null
            ? await _evidenceService.GetEvidenceAsync(definition.LegacyKey, userId, cancellationToken)
            : _evidence?.GetEvidence(definition.LegacyKey);
        if (evidence is not { Complete: true })
            return ReportMigrationParityStatus.PresetReady;

        return ReportMigrationParityStatus.ParityPassed;
    }

    /// <summary>
    /// 捆绑组合条目 parity 派生（ERP-322）：单一数据集未注册时，改从捆绑预设目录（ERP-310）按 LegacyKey
    /// 解析多节预设，并逐节按当前账号重新校验受控数据集授权（fail closed，绝不信任预设载荷）。
    /// 全部节数据集就绪 → preset-ready；任一节缺失 / 被撤销 → pending；parity-passed 仍须现有四维比对证据。
    /// </summary>
    private async Task<ReportMigrationParityStatus> DeriveBundleParityAsync(
        ReportMigrationRegistryEntryDefinition definition,
        long userId,
        CancellationToken cancellationToken)
    {
        var bundle = await ResolveBundlePresetByLegacyKeyAsync(definition.LegacyKey, userId, cancellationToken);
        if (bundle is null || bundle.Sections is not { Count: > 0 })
            return ReportMigrationParityStatus.Pending;

        // 逐节重检：任一节数据集缺失 / 未授权即 fail closed（pending），绝不信任捆绑预设自报的 readiness。
        foreach (var section in bundle.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.DatasetKey))
                return ReportMigrationParityStatus.Pending;

            if (await _catalog.GetDatasetAsync(section.DatasetKey, userId, cancellationToken) is null)
                return ReportMigrationParityStatus.Pending;
        }

        // 兼容性声明缺失仍不达 parity-passed（fail closed），与单数据集路径一致。
        if (!CompatibilityDeclared(definition))
            return ReportMigrationParityStatus.PresetReady;

        var evidence = _evidenceService is not null
            ? await _evidenceService.GetEvidenceAsync(definition.LegacyKey, userId, cancellationToken)
            : _evidence?.GetEvidence(definition.LegacyKey);
        if (evidence is not { Complete: true })
            return ReportMigrationParityStatus.PresetReady;

        return ReportMigrationParityStatus.ParityPassed;
    }

    /// <summary>按 LegacyKey 从捆绑预设目录解析多节预设（未注册 / 未列出均 fail closed → null）。</summary>
    private async Task<ReportConfigurationBundlePresetDto?> ResolveBundlePresetByLegacyKeyAsync(
        string legacyKey,
        long userId,
        CancellationToken cancellationToken)
    {
        if (_bundlePresets is null)
            return null;

        var presets = await _bundlePresets.ListPresetsAsync(userId, cancellationToken);
        return presets?.FirstOrDefault(p =>
            string.Equals(p.LegacyKey, legacyKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>旧语义匹配：权限（必需菜单与受控数据集必需菜单一致）+ 币种 / 单位口径一致。</summary>
    private static bool SemanticsMatch(
        ReportMigrationRegistryEntryDefinition definition,
        ReportConfigurationDatasetDto dataset)
    {
        var menuMatch = RequiredMenuCodesMatch(definition.RequiredMenuCodes, dataset.RequiredMenuCode);
        var currencyUnitMatch = string.Equals(
            definition.CurrencyUnitSemantics,
            dataset.CurrencyUnitSemantics,
            StringComparison.Ordinal);
        return menuMatch && currencyUnitMatch;
    }

    /// <summary>
    /// 菜单语义匹配（fail closed，与 <see cref="IsMenuAuthorized"/> 一致地按集合语义）：
    /// 条目声明的必需菜单集合必须与受控数据集的必需菜单集合完全一致；单菜单条目行为与旧实现一致，
    /// 多菜单条目（旧单据导出族）按完整声明集合逐项比对，任一必需菜单缺失即不匹配。
    /// <para>受控数据集对多菜单族用 '+' 连接必需菜单（见 LegacyBillExport / MasterData 数据集适配器）。</para>
    /// </summary>
    private static bool RequiredMenuCodesMatch(
        IReadOnlyList<string> declaredMenuCodes,
        string datasetRequiredMenuCode)
    {
        if (declaredMenuCodes is null || declaredMenuCodes.Count == 0)
            return false;

        if (string.IsNullOrWhiteSpace(datasetRequiredMenuCode))
            return false;

        var datasetMenuCodes = new HashSet<string>(
            datasetRequiredMenuCode.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        if (declaredMenuCodes.Count != datasetMenuCodes.Count)
            return false;

        foreach (var code in declaredMenuCodes)
        {
            if (!datasetMenuCodes.Contains(code))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 兼容性声明：至少声明一种 Excel / PDF 兼容性，或显式声明两者均不支持。两者均 false 表示旧路由本就不产出
    /// Excel / PDF（固定报表 / 财务报表 / 字段完整度等仅 JSON/tables 的旧路由），通用平台作为超集补齐 Excel / PDF，
    /// 故为真空兼容（有效声明），不阻断 parity-passed。parity-passed 的 fail-closed 由后续四维比对证据门控单独保证。
    /// </summary>
    private static bool CompatibilityDeclared(ReportMigrationRegistryEntryDefinition definition)
        => definition.ExcelCompatible
            || definition.PdfCompatible
            || (!definition.ExcelCompatible && !definition.PdfCompatible);

    /// <summary>菜单授权重检（fail closed）：未声明菜单或任一必需菜单缺失 → 隐藏。</summary>
    private static bool IsMenuAuthorized(
        ReportMigrationRegistryEntryDefinition definition,
        HashSet<string> authorizedMenuCodes)
    {
        if (definition.RequiredMenuCodes.Count == 0)
            return false;

        foreach (var code in definition.RequiredMenuCodes)
        {
            if (!authorizedMenuCodes.Contains(code))
                return false;
        }

        return true;
    }

    private static void EnsureAuthenticated(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问报表迁移登记册", ErrorCodes.Unauthorized);
    }
}

/// <summary>
/// 空预设目录（ERP-295 Stage 2 默认）：预设尚未落地，恒返回 false —— 任何条目都无法达到 preset-ready / parity-passed，
/// 因此旧路由绝不会被提前移除。仅作为 parity 派生的前置接缝存在，不新增权限或存储。
/// </summary>
public sealed class EmptyReportMigrationPresetCatalog : IReportMigrationPresetCatalog
{
    /// <inheritdoc />
    public Task<bool> HasPresetAsync(string legacyKey, long? userId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// 空比对证据源（ERP-296 Stage 2 默认）：迁移 parity 尚无真实夹具比对证据，恒返回 null —— 任何条目都无法达到
/// parity-passed，因此旧路由绝不会被提前移除。仅作为 parity 派生的事前接缝存在，不新增权限或存储。
/// </summary>
public sealed class EmptyReportMigrationParityEvidenceProvider : IReportMigrationParityEvidenceProvider
{
    /// <inheritdoc />
    public ReportMigrationParityEvidenceDto? GetEvidence(string legacyKey) => null;
}

