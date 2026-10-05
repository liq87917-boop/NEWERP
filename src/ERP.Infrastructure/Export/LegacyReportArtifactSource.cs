using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 旧报表实际产物来源实现（ERP-333 Stage 2）：有界有限登记册，把受支持旧报表键映射到其既有规范导出器
/// （旧 Excel / PDF 导出），产出真实旧产物字节。绝不使用通用平台导出器充当旧导出器，绝不返回 caller proof boolean。
/// <list type="bullet">
/// <item><b>有界有限</b>：仅显式登记受支持旧报表键；未登记键在打开任何查询之前返回 null（fail closed）。</item>
/// <item><b>复用既有读取</b>：经既有 <see cref="IDynamicSalesOrderReportQuery"/> 重新校验菜单与数据范围，缺字体 / 渲染失败 / 超限返回 null。</item>
/// </list>
/// <para>全程只读：仅生成旧产物字节流，不写库、不执行任意 SQL、不扩权。</para>
/// </summary>
public sealed class LegacyReportArtifactSource : ILegacyReportArtifactSource
{
    private const int MaxKeyLength = 128;
    private const int MaxPageSize = 200;
    private const int MaxArtifactBytes = 32 * 1024 * 1024;

    private readonly IDynamicSalesOrderReportQuery _salesOrderQuery;

    public LegacyReportArtifactSource(IDynamicSalesOrderReportQuery salesOrderQuery)
    {
        _salesOrderQuery = salesOrderQuery ?? throw new ArgumentNullException(nameof(salesOrderQuery));
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

        if (string.Equals(key, "dynamic:sales-order", StringComparison.OrdinalIgnoreCase))
            return await ReadSalesOrderArtifactsAsync(request, cancellationToken);

        // 有界有限集合之外：无旧导出器可调用 → fail closed。
        return null;
    }

    private async Task<LegacyReportArtifactBytesDto?> ReadSalesOrderArtifactsAsync(
        LegacyReportSourceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _salesOrderQuery.PreviewAsync(new DynamicSalesOrderReportRequest
            {
                StartDate = request.Start,
                EndDate = request.End,
                Page = request.Page < 1 ? 1 : request.Page,
                PageSize = ClampPageSize(request.PageSize, MaxPageSize),
            }, request.UserId, cancellationToken);

            var groupBy = DynamicSalesOrderReportRules.NormalizeGroupBy(null);
            var groups = DynamicSalesOrderReportRules.BuildGroupSubtotals(page.Rows, groupBy);

            var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
            var rows = page.Rows.Select(DynamicSalesOrderReportRules.BuildExportRow).ToList();
            var excelBytes = ExcelExporter.ExportRows("销售订单", rows, columns);
            var pdfBytes = DynamicSalesOrderPdfExporter.Export(page, groups, groupBy);

            if (excelBytes.Length == 0 || pdfBytes.Length == 0)
                return null;
            if (excelBytes.Length > MaxArtifactBytes || pdfBytes.Length > MaxArtifactBytes)
                return null;

            return new LegacyReportArtifactBytesDto(excelBytes, pdfBytes);
        }
        catch (BusinessException)
        {
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

    private static int ClampPageSize(int pageSize, int max)
        => pageSize < 1 ? max : Math.Min(pageSize, max);
}
