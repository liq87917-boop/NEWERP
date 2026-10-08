using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 旧报表实际产物来源实现（ERP-333 / ERP-337 Stage 2）：有界有限登记册，把受支持旧报表键映射到其既有规范导出器
/// （旧 Excel / PDF 导出），产出真实旧产物字节。绝不使用通用平台导出器充当旧导出器，绝不返回 caller proof boolean。
/// <list type="bullet">
/// <item><b>有界有限</b>：仅显式登记受支持旧报表键；未登记键在打开任何查询之前返回 null（fail closed）。</item>
/// <item><b>复用既有读取</b>：经既有查询 / 读取接缝重新校验菜单与数据范围，缺字体 / 渲染失败 / 超限返回 null。</item>
/// </list>
/// <para>全程只读：仅生成旧产物字节流，不写库、不执行任意 SQL、不扩权。</para>
/// </summary>
public sealed class LegacyReportArtifactSource : ILegacyReportArtifactSource
{
    private const int MaxKeyLength = 128;

    private readonly LegacyReportArtifactAdapterRegistry _registry;

    public LegacyReportArtifactSource(
        IDynamicSalesOrderReportQuery salesOrderQuery,
        IDynamicReceivableReportQuery? receivableQuery = null,
        IDynamicPurchaseOrderReportQuery? purchaseOrderQuery = null,
        ILegacyBillExportReadService? billExportReader = null,
        IErpDbContext? db = null)
    {
        if (salesOrderQuery is null)
            throw new ArgumentNullException(nameof(salesOrderQuery));

        _registry = LegacyReportArtifactAdapterRegistry.Create(
            salesOrderQuery, receivableQuery, purchaseOrderQuery, billExportReader, db);
    }

    /// <inheritdoc />
    public async Task<LegacyReportArtifactBytesDto?> ReadArtifactsAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var key = ResolveKey(request.LegacyKey);
        if (key is null || request.UserId is null or <= 0)
            return null;

        if (!_registry.TryGetAdapter(key, out var adapter))
            return null;

        try
        {
            return await adapter.ReadAsync(request, cancellationToken);
        }
        catch (BusinessException)
        {
            // 旧导出不可用 / 未授权 / 缺字体 / 超限 → fail closed，绝不猜测、绝不回退为通用导出器。
            return null;
        }
    }

    private static string? ResolveKey(string? legacyKey)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            return null;

        var key = legacyKey.Trim();
        if (key.Length > MaxKeyLength || ContainsUnsafeKey(key))
            return null;

        return key;
    }

    private static bool ContainsUnsafeKey(string value)
        => value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':'));
}
