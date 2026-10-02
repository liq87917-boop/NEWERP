using ERP.Application.DTOs;
using ERP.Application.Services;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 动态柜量与装柜利用率证据报表（ERP-256）「全匹配」每日证据汇总 Excel 导出器（窄、无状态、由控制器直接构造，无新 DI / 包 / 配置）。
/// 复用 ERP-255 全匹配汇总 DTO / 规则（<see cref="DynamicContainerStatsSummaryRules.BuildSummary"/>）与既有 NPOI；
/// 只导出固定每日汇总工作表（装柜日历日 / 证据桶数 / 已审核装柜清单数 / 缺柜号清单数与签名有符号箱数 / 毛重 / 体积各自独立）与
/// 期间合计 / 口径工作表（规范化日期 / 应用筛选 / 全匹配覆盖 / 客户范围 / 来源上限 / 数量单位 / 分组身份 / 未知实际容积 / 柜型 / 出运）。
/// 绝不追加客户 / 柜号 / 清单明细行或全局去重客户数 / 实体柜容量合计；日期规范化为 yyyy-MM-dd 文本，计数与有符号计量写入数值单元格
/// （保留 0 / 负数、不按金额格式化），危险公式前导文本 / 筛选 / 口径字符串转义为字面文本；空证据工作簿显式标注。
/// <para>全程只读：仅生成 xlsx 字节流，不写库、不执行任意 SQL；请求审计由既有 OperationLogMiddleware 记录。</para>
/// </summary>
public sealed class DynamicContainerStatsSummaryExcelExporter
{
    /// <summary>导出全匹配每日证据汇总为 xlsx 字节流（只读）。</summary>
    public byte[] Build(DynamicContainerStatsReportSummaryDto summary, DynamicContainerStatsReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(page);

        using var workbook = new XSSFWorkbook();
        BuildDailySheet(workbook, summary);
        BuildPeriodContextSheet(workbook, summary, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>固定每日汇总工作表：装柜日历日 / 证据桶数 / 已审核装柜清单数 / 缺柜号清单数 / 箱数 / 毛重 / 体积；空证据显式标注。</summary>
    private static void BuildDailySheet(XSSFWorkbook workbook, DynamicContainerStatsReportSummaryDto summary)
    {
        var sheet = workbook.CreateSheet(DynamicContainerStatsSummaryRules.SummarySheetName);

        var header = sheet.CreateRow(0);
        for (var c = 0; c < summary.DailyColumns.Count; c++)
        {
            header.CreateCell(c).SetCellValue(Escape(summary.DailyColumns[c].Label));
            sheet.SetColumnWidth(c, Math.Max(summary.DailyColumns[c].Label.Length * 2 + 4, 12) * 256);
        }

        if (summary.DailyRows.Count == 0)
        {
            sheet.CreateRow(1).CreateCell(0).SetCellValue(Escape(DynamicContainerStatsSummaryRules.EmptyEvidenceRowText));
            return;
        }

        var rowIndex = 1;
        foreach (var day in summary.DailyRows)
        {
            var row = sheet.CreateRow(rowIndex++);
            row.CreateCell(0).SetCellValue(day.Date.ToString("yyyy-MM-dd"));
            row.CreateCell(1).SetCellValue(day.BucketCount);
            row.CreateCell(2).SetCellValue(day.ApprovedLists);
            row.CreateCell(3).SetCellValue(day.MissingContainerNoCount);
            row.CreateCell(4).SetCellValue((double)day.TotalCartons);
            row.CreateCell(5).SetCellValue((double)day.TotalWeight);
            row.CreateCell(6).SetCellValue((double)day.TotalVolume);
        }
    }

    /// <summary>期间合计 / 口径工作表：期间合计数值 + 规范化日期 / 筛选 / 全匹配覆盖 / 客户范围 / 来源上限 / 单位 / 分组 / 未知容积 / 柜型 / 出运始终包含。</summary>
    private static void BuildPeriodContextSheet(
        XSSFWorkbook workbook, DynamicContainerStatsReportSummaryDto summary, DynamicContainerStatsReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicContainerStatsSummaryRules.PeriodContextSheetName);
        sheet.SetColumnWidth(0, 24 * 256);
        sheet.SetColumnWidth(1, 100 * 256);

        var nextRow = 0;
        AddNumber(sheet, nextRow++, DynamicContainerStatsSummaryRules.PeriodBucketLabel, summary.Period.BucketCount);
        AddNumber(sheet, nextRow++, DynamicContainerStatsSummaryRules.PeriodApprovedListsLabel, summary.Period.ApprovedLists);
        AddNumber(sheet, nextRow++, DynamicContainerStatsSummaryRules.PeriodMissingLabel, summary.Period.MissingContainerNoCount);
        AddDecimal(sheet, nextRow++, DynamicContainerStatsSummaryRules.PeriodCartonsLabel, summary.Period.TotalCartons);
        AddDecimal(sheet, nextRow++, DynamicContainerStatsSummaryRules.PeriodWeightLabel, summary.Period.TotalWeight);
        AddDecimal(sheet, nextRow++, DynamicContainerStatsSummaryRules.PeriodVolumeLabel, summary.Period.TotalVolume);

        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextFilterLabel,
            string.IsNullOrWhiteSpace(page.FilterText)
                ? DynamicContainerStatsReportRules.ContextNoFilterText
                : page.FilterText);
        AddText(sheet, nextRow++, DynamicContainerStatsSummaryRules.ContextCoverageLabel, summary.CoverageText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextCustomerScopeLabel, page.CustomerScopeContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextUnitLabel, page.UnitContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextGroupingLabel, page.GroupingContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextUnknownCapacityLabel, page.UnknownCapacityContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextTypeLabel, page.TypeContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextShippingLabel, page.ShippingContextText);
        AddText(sheet, nextRow++, DynamicContainerStatsReportRules.ContextSourceLabel, page.SourceContextText);

        if (!string.IsNullOrWhiteSpace(summary.NoEvidenceContext))
            AddText(sheet, nextRow, DynamicContainerStatsReportRules.ContextEmptyLabel, summary.NoEvidenceContext);
    }

    /// <summary>文本单元格（标签 / 值）公式前导转义为字面文本。</summary>
    private static void AddText(ISheet sheet, int rowIndex, string label, string value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        row.CreateCell(1).SetCellValue(Escape(value));
    }

    /// <summary>计数单元格写入数值（证据桶数 / 已审核装柜清单数 / 缺柜号清单数）。</summary>
    private static void AddNumber(ISheet sheet, int rowIndex, string label, int value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        row.CreateCell(1).SetCellValue(value);
    }

    /// <summary>有符号计量单元格写入数值（箱数 / 毛重 / 体积，保留 0 / 负数、不按金额格式化）。</summary>
    private static void AddDecimal(ISheet sheet, int rowIndex, string label, decimal value)
    {
        var row = sheet.CreateRow(rowIndex);
        row.CreateCell(0).SetCellValue(Escape(label));
        row.CreateCell(1).SetCellValue((double)value);
    }

    private static string Escape(string? value)
        => DynamicContainerStatsReportRules.EscapeFormulaLeading(value) as string ?? string.Empty;
}

