using ERP.Application.DTOs;
using ERP.Application.Services;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-253）Excel 导出器（窄、无状态、由控制器直接构造，无新 DI / 包 / 配置）。
/// 复用 ERP-252 有界、已授权预览与选定列顺序，仅导出当前页选定列：有符号签名箱数 / 毛重 / 体积与授权范围计数写入数值单元格；
/// 装载率 / 柜型恒为字面「未知」（绝不写成 0 或按 68m³ 推导百分比）；原始柜号等文本做公式注入转义（字面文本）。
/// 并追加「报表口径」上下文工作表标注规范化日期 / 应用筛选 / 页面覆盖 / 来源计数与上限 / 客户范围 / 分组身份 / 数量单位 /
/// 未知实际容积 / 柜型 / 出运 / 来源证据（即使对应列被取消选择也始终包含）。绝不追加全匹配 / 跨单位合计 / 实体柜数量声称或未选定列。
/// <para>全程只读：仅生成 xlsx 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public sealed class DynamicContainerStatsExcelExporter
{
    /// <summary>导出当前预览页为 xlsx 字节流（只读）。</summary>
    public byte[] Build(DynamicContainerStatsReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var fieldKeys = page.Columns.Select(c => c.Key).ToList();
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows
            .Select(r => DynamicContainerStatsReportRules.BuildExportRow(r, fieldKeys))
            .ToList();

        var dataBytes = ExcelExporter.ExportRows(
            DynamicContainerStatsReportRules.DataSheetName, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表（规范化日期 / 筛选 / 页面覆盖 / 来源计数与上限 / 客户范围 / 分组身份 / 单位 / 未知容积 / 柜型 / 出运始终包含）。</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicContainerStatsReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicContainerStatsReportRules.ContextSheetName);

        var nextRow = 0;
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextFilterLabel,
            string.IsNullOrWhiteSpace(page.FilterText)
                ? DynamicContainerStatsReportRules.ContextNoFilterText
                : page.FilterText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextPageLabel,
            DynamicContainerStatsReportRules.BuildPageContext(page));
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextPageOnlyLabel, page.PageOnlyText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextSourceCountLabel,
            DynamicContainerStatsReportRules.BuildSourceCountContext(page.Context));
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextCustomerScopeLabel, page.CustomerScopeContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextGroupingLabel, page.GroupingContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextUnitLabel, page.UnitContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextUnknownCapacityLabel, page.UnknownCapacityContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextTypeLabel, page.TypeContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextShippingLabel, page.ShippingContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextSourceLabel, page.SourceContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextBoundaryLabel, page.BoundaryText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextDisclaimerLabel, page.DisclaimerText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (!string.IsNullOrWhiteSpace(page.EmptyText))
            AddText(sheet, nextRow, DynamicContainerStatsReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>文本单元格（标签 / 值）公式前导转义为字面文本。</summary>
    private static void AddText(ISheet sheet, int rowIndex, string label, string value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        row.CreateCell(1).SetCellValue(Escape(value));
    }

    private static string Escape(string? value)
        => DynamicContainerStatsReportRules.EscapeFormulaLeading(value) as string ?? string.Empty;
}
