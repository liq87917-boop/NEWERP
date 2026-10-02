using ERP.Application.DTOs;
using ERP.Application.Services;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态业务员提成证据报表（ERP-248）「全匹配」原币汇总 Excel 导出器（窄、无状态、由控制器直接构造，无新 DI / 包 / 配置）。
/// 复用 ERP-247 全匹配汇总 DTO / 规则（<see cref="DynamicSalesCommissionSummaryRules.BuildSummary"/>）与既有 NPOI；
/// 仅导出全匹配原币汇总（与预览当前页 / 选定明细列无关）：已知签名原币金额 / 计数 / 显式 0 当前参考比例写入数值单元格，
/// null 金额 / 利润 / 利润率 / 提成额显式「未知」（绝不写成 0）；文本单元格做公式注入转义（字面文本）；
/// 绝无员工明细行 / 跨币种合计金额 / 编造提成。
/// 并追加「报表口径」上下文工作表标注规范化日期 / 应用筛选 / 全匹配覆盖范围 / 来源上限 / 全局去重业务员桶 / 已审核订单 /
/// 来源 / 原币 / 当前参考比例 / 未知历史利润与提成依据（始终包含）。
/// <para>全程只读：仅生成 xlsx 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public sealed class DynamicSalesCommissionSummaryExcelExporter
{
    /// <summary>导出全匹配原币汇总为 xlsx 字节流（只读）。</summary>
    public byte[] Build(DynamicSalesCommissionSummaryDto summary, DynamicSalesCommissionReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(page);

        var columns = summary.CurrencyColumns
            .Select(c => (c.Key, c.Label))
            .ToList();
        var rows = summary.CurrencyRows
            .Select(DynamicSalesCommissionSummaryRules.BuildExportRow)
            .ToList();

        var dataBytes = ExcelExporter.ExportRows(
            DynamicSalesCommissionSummaryRules.SummarySheetName, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, summary, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表（全匹配覆盖 / 来源上限 / 全局去重计数 / 当前参考比例 / 未知历史利润与提成依据始终包含）。</summary>
    private static void AppendContextSheet(
        XSSFWorkbook workbook, DynamicSalesCommissionSummaryDto summary, DynamicSalesCommissionReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicSalesCommissionReportRules.ContextSheetName);

        var nextRow = 0;
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextFilterLabel,
            string.IsNullOrWhiteSpace(page.FilterText)
                ? DynamicSalesCommissionReportRules.ContextNoFilterText
                : page.FilterText);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddText(sheet, nextRow++, DynamicSalesCommissionSummaryRules.ContextCoverageLabel, summary.CoverageText);
        AddNumber(sheet, nextRow++, DynamicSalesCommissionSummaryRules.ContextGlobalBucketsLabel, summary.GlobalUniqueSalesmanBuckets);
        AddNumber(sheet, nextRow++, DynamicSalesCommissionSummaryRules.ContextGlobalOrdersLabel, summary.GlobalApprovedOrders);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextCurrencyLabel, page.CurrencyContextText);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextUnknownLabel, page.UnknownContextText);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextProfitLabel, page.ProfitContextText);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextCommissionLabel, page.CommissionContextText);
        AddRate(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextRateLabel, summary.CurrentReferenceRate);
        AddText(sheet, nextRow++, DynamicSalesCommissionSummaryRules.ContextRateReasonLabel,
            summary.CurrentReferenceRate.HasValue
                ? SalesCommissionEvidenceRules.CommissionRateEvidence
                : summary.CurrentReferenceRateReason);
        AddText(sheet, nextRow++, DynamicSalesCommissionSummaryRules.ContextProfitBasisLabel, summary.ProfitBasisText);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextSourceLabel, page.SourceContextText);
        AddText(sheet, nextRow++, DynamicSalesCommissionReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (!string.IsNullOrWhiteSpace(summary.EmptyText))
            AddText(sheet, nextRow, DynamicSalesCommissionReportRules.ContextEmptyLabel, summary.EmptyText);
    }

    /// <summary>文本单元格（标签 / 值）公式前导转义为字面文本。</summary>
    private static void AddText(ISheet sheet, int rowIndex, string label, string value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        row.CreateCell(1).SetCellValue(Escape(value));
    }

    /// <summary>计数单元格写入数值（全局去重业务员桶 / 已审核订单）。</summary>
    private static void AddNumber(ISheet sheet, int rowIndex, string label, int value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        row.CreateCell(1).SetCellValue(value);
    }

    /// <summary>当前参考比例：已知（含显式 0）写入数值；未知写入文本「未知」（绝不写成 0）。</summary>
    private static void AddRate(ISheet sheet, int rowIndex, string label, decimal? value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        if (value.HasValue)
            row.CreateCell(1).SetCellValue((double)value.Value);
        else
            row.CreateCell(1).SetCellValue(DynamicSalesCommissionReportRules.UnknownValueText);
    }

    private static string Escape(string? value)
        => DynamicSalesCommissionReportRules.EscapeFormulaLeading(value) as string ?? string.Empty;
}
