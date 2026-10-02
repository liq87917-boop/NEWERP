using ERP.Application.DTOs;
using ERP.Application.Services;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态业务员提成证据报表（ERP-245）Excel 导出器（窄、无状态、由控制器直接构造，无新 DI 注册 / 包 / 配置）。
/// 复用 ERP-244 有界、已授权预览与选定列顺序，仅导出当前页选定列：已知签名原币金额 / 计数按类型写入数值单元格；
/// null 金额 / 利润 / 利润率 / 提成比例 / 提成额显式「未知」（绝不写成 0），配置为 0 的当前参考比例仍写入数值 0
/// （依据列标注「当前参考」）；业务员 Id null（未指定业务员桶）留空；文本单元格做公式注入转义（字面文本）。
/// 并追加「报表口径」上下文工作表标注规范化日期 / 应用筛选 / 页面覆盖 / 来源计数与上限 / 当前用户受限已审核订单来源 /
/// 当前参考比例 / 未知历史利润与提成口径（即使对应列被取消选择也始终包含）。绝不追加跨币种合计或未选定列。
/// <para>全程只读：仅生成 xlsx 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public sealed class DynamicSalesCommissionExcelExporter
{
    /// <summary>导出当前预览页为 xlsx 字节流（只读）。</summary>
    public byte[] Build(DynamicSalesCommissionReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var fieldKeys = page.Columns.Select(c => c.Key).ToList();
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows
            .Select(r => DynamicSalesCommissionReportRules.BuildExportRow(r, fieldKeys))
            .ToList();

        var dataBytes = ExcelExporter.ExportRows(
            DynamicSalesCommissionReportRules.DataSheetName, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表（即使对应列被取消选择也始终包含）。</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicSalesCommissionReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicSalesCommissionReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(
                DynamicSalesCommissionReportRules.EscapeFormulaLeading(label) as string ?? string.Empty);
            row.CreateCell(1).SetCellValue(
                DynamicSalesCommissionReportRules.EscapeFormulaLeading(value) as string ?? string.Empty);
        }

        var nextRow = 0;
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextPageLabel,
            DynamicSalesCommissionReportRules.BuildPageContext(page));
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextSourceCountLabel,
            DynamicSalesCommissionReportRules.BuildSourceCountContext(page.Context));
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextCurrencyLabel, page.CurrencyContextText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextUnknownLabel, page.UnknownContextText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextProfitLabel, page.ProfitContextText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextCommissionLabel, page.CommissionContextText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextRateLabel, page.RateContextText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextSourceLabel, page.SourceContextText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextPageOnlyLabel,
            DynamicSalesCommissionReportRules.PageOnlyText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextFilterLabel,
            string.IsNullOrWhiteSpace(page.FilterText)
                ? DynamicSalesCommissionReportRules.ContextNoFilterText
                : page.FilterText);
        AddLabel(nextRow++, DynamicSalesCommissionReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (!string.IsNullOrWhiteSpace(page.EmptyText))
            AddLabel(nextRow, DynamicSalesCommissionReportRules.ContextEmptyLabel, page.EmptyText);
    }
}
