using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-259 Stage 1）的目录聚合实现：汇总全部已注册 <see cref="IReportConfigurationDatasetProvider"/>，
/// 每次调用都按当前账号重新校验既有菜单授权，只暴露已授权数据集的有限字段 / 能力 / 粒度 / 币种口径。
/// <para>无身份时 fail closed（抛未认证）；无对应菜单授权的数据集被省略（不暴露）；
/// 已保存定义本身绝不授予任何权限。</para>
/// </summary>
public sealed class ReportConfigurationCatalog : IReportConfigurationCatalog
{
    private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;

    public ReportConfigurationCatalog(IEnumerable<IReportConfigurationDatasetProvider> providers)
    {
        _providers = (providers ?? Array.Empty<IReportConfigurationDatasetProvider>())
            .OrderBy(p => p.DatasetKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationCatalogDto> GetCatalogAsync(
        long? userId, CancellationToken cancellationToken = default)
    {
        var datasets = new List<ReportConfigurationDatasetDto>();
        foreach (var provider in _providers)
        {
            var dataset = await provider.GetDatasetAsync(userId, cancellationToken);
            if (dataset is not null)
                datasets.Add(dataset);
        }

        return new ReportConfigurationCatalogDto(ReportConfigurationRules.CurrentSchemaVersion, datasets);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationDatasetDto?> GetDatasetAsync(
        string datasetKey, long? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(datasetKey))
            return null;

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.DatasetKey, datasetKey.Trim(), StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return null;

        return await provider.GetDatasetAsync(userId, cancellationToken);
    }
}
