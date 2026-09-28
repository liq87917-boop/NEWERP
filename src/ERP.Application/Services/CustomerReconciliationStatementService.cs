using System.Text;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户对账证据导出（ERP-086，只读派生、有界、分币种）：在 ERP-074 客户应收账款对账工作台
/// （<see cref="CustomerReceivableReconciliationService"/>）的同一派生引擎之上，把四类证据
/// 分别标注并输出为可下载的 CSV / HTML 对账证据导出：①ERP-055 发票含税总额证据、
/// ②ERP-073 有效收款分摊证据、③ERP-075 收款分摊上下文、④ERP-076 发票 → 出货链接证据；
/// 算术剩余证据 = 发票含税总额 − 有效分摊合计。不同币种分别成行、绝无跨币种总额；
/// 缺失 / 无效链接一律按「未知」呈现，绝不修复、改派或推断。
/// <para>边界：生成导出前重新校验登录身份与既有「角色 → 菜单」模块授权，来源客户不存在 / 已删除时 fail closed；
/// 全程只读、无逐行查库、不调用外部服务，也不改写客户、销售订单、出货、装柜、发票、分摊、收款、余额、
/// 税务、财务与结算记录。</para>
/// </summary>
public static class CustomerReconciliationStatementService
{
    /// <summary>模块元数据（白名单、有界额度与口径文案；与接口 / 界面 / 文档同源）</summary>
    public static CustomerReconciliationStatementMetadataDto GetMetadata() => new()
    {
        SupportedEvidenceClasses = CustomerReconciliationStatementRules.SupportedEvidenceClasses.ToList(),
        SupportedFormats = CustomerReconciliationStatementRules.SupportedFormats.ToList(),
        DefaultMaxRows = CustomerReconciliationStatementRules.DefaultMaxRows,
        MaxExportRows = CustomerReconciliationStatementRules.MaxExportRows,
        RequiredMenuCode = CustomerReceivableReconciliationRules.RequiredMenuCode,
        RequiredMenuText = CustomerReceivableReconciliationRules.RequiredMenuText,
        BoundaryText = CustomerReconciliationStatementRules.BoundaryText,
        ShipmentLinkUnavailableText = CustomerReconciliationStatementRules.ShipmentLinkUnavailableText,
        CurrencySeparationText = CustomerReconciliationStatementRules.CurrencySeparationText,
    };

    /// <summary>
    /// 生成对账证据导出（只读、有界）：重新校验授权 → 复用工作台派生引擎取数 → 证据类过滤 → 映射为导出行 →
    /// 分币种汇总 → 组装报表头（as-of / 筛选口径 / 生成时间 / 边界声明）。
    /// </summary>
    public static async Task<CustomerReconciliationStatementDto> ForStatementAsync(
        IErpDbContext db, CustomerReconciliationStatementQuery query, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再生成客户对账证据导出", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(
                CustomerReceivableReconciliationRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{CustomerReceivableReconciliationRules.RequiredMenuText}」"
                + $"（{CustomerReceivableReconciliationRules.RequiredMenuCode}）模块授权：拒绝生成对账证据导出"
                + "（fail closed，不返回任何证据、不做来源修复或改派）",
                ErrorCodes.Forbidden);
        }

        var customerCode = string.Empty;
        var customerName = string.Empty;
        if (query.CustomerId.HasValue)
        {
            var customer = await db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == query.CustomerId.Value && !c.IsDeleted);
            if (customer is null)
            {
                throw new BusinessException(
                    "客户不存在或已删除：无法确认对账证据导出（fail closed，不返回部分证据，也不做任何修复或改派）",
                    ErrorCodes.NotFound);
            }

            customerCode = customer.CustomerCode ?? string.Empty;
            customerName = customer.CustomerName ?? string.Empty;
        }

        var reconciliationQuery = new CustomerReceivableReconciliationQuery
        {
            InvoiceId = query.InvoiceId,
            CustomerId = query.CustomerId,
            Currency = query.Currency,
            InvoiceDateFrom = query.InvoiceDateFrom,
            InvoiceDateTo = query.InvoiceDateTo,
            InvoiceStatus = query.InvoiceStatus,
            AllocationState = query.AllocationState,
            AsOfDate = query.AsOfDate,
            Page = 1,
            PageSize = CustomerReceivableReconciliationRules.MaxPageSize,
        };

        var (rows, truncated, total) = await CustomerReceivableReconciliationService.ForStatementRowsAsync(
            db, reconciliationQuery, query.MaxRows);

        var filtered = rows.Where(r => MatchesEvidenceClass(r, query.EvidenceClass!)).ToList();
        var statementRows = filtered.Select(MapRow).ToList();
        var currencies = BuildCurrencySummaries(statementRows);

        var invoiceStatusFilter = query.InvoiceStatus ?? CustomerReceivableReconciliationRules.InvoiceStatusRecorded;

        return new CustomerReconciliationStatementDto
        {
            CustomerId = query.CustomerId,
            CustomerCode = customerCode,
            CustomerName = customerName,
            AsOfDate = (query.AsOfDate ?? DateTime.Today).Date,
            GeneratedAt = DateTime.Now,
            CurrencyFilter = query.Currency ?? string.Empty,
            InvoiceDateRangeText = BuildDateRangeText(query.InvoiceDateFrom, query.InvoiceDateTo),
            InvoiceStatusFilter = invoiceStatusFilter,
            InvoiceStatusFilterText = CustomerReceivableReconciliationRules.InvoiceStatusText(invoiceStatusFilter),
            AllocationStateFilter = query.AllocationState ?? string.Empty,
            AllocationStateFilterText = query.AllocationState is null
                ? "全部分配状态（none / historical_only / partial / full）"
                : CustomerReceivableReconciliationRules.AllocationStateText(query.AllocationState),
            EvidenceClassFilter = query.EvidenceClass!,
            EvidenceClassFilterText = CustomerReconciliationStatementRules.EvidenceClassText(query.EvidenceClass!),
            Total = total,
            RowCount = statementRows.Count,
            Truncated = truncated,
            TruncatedNote = truncated
                ? $"命中系统有界导出行数上限（{query.MaxRows} 行）：超过部分不导出，也不给部分合计。"
                : string.Empty,
            BoundaryText = CustomerReconciliationStatementRules.BoundaryText,
            ShipmentLinkUnavailableText = CustomerReconciliationStatementRules.ShipmentLinkUnavailableText,
            CurrencySeparationText = CustomerReconciliationStatementRules.CurrencySeparationText,
            Rows = statementRows,
            Currencies = currencies,
        };
    }

    /// <summary>证据类过滤（只按本行既有派生字段判定；出货链接证据无权威持久化链接册，恒为 false → 空结果）</summary>
    private static bool MatchesEvidenceClass(CustomerReceivableReconciliationInvoiceRow row, string evidenceClass)
        => evidenceClass switch
        {
            CustomerReconciliationStatementRules.ClassInvoice or CustomerReconciliationStatementRules.ClassAll => true,
            CustomerReconciliationStatementRules.ClassReceiptAllocation => row.TotalRowCount > 0,
            CustomerReconciliationStatementRules.ClassReceiptContext => row.EffectiveCount > 0,
            CustomerReconciliationStatementRules.ClassShipmentLink => false,
            _ => true,
        };

    /// <summary>把工作台派生行映射为导出行（纯计算，不访问数据库）</summary>
    private static CustomerReconciliationStatementRowDto MapRow(CustomerReceivableReconciliationInvoiceRow row)
        => new()
        {
            InvoiceId = row.InvoiceId,
            InvoiceType = row.InvoiceType,
            InvoiceCode = row.InvoiceCode,
            InvoiceNumber = row.InvoiceNumber,
            IdentityText = row.IdentityText,
            InvoiceDate = row.InvoiceDate,
            CustomerId = row.CustomerId,
            CustomerCode = row.CustomerCode,
            CustomerName = row.CustomerName,
            Currency = row.Currency,
            AmountDecimals = row.AmountDecimals,
            InvoiceGrossAmount = row.GrossAmount,
            IsDraft = row.IsDraft,
            IsVoided = row.IsVoided,
            IsActiveEvidence = row.IsActiveEvidence,
            InvoiceStatusText = row.StatusText,
            EffectiveAllocatedAmount = row.EffectiveAmount,
            EffectiveAllocationCount = row.EffectiveCount,
            EffectiveReceiptCount = row.EffectiveReceiptCount,
            ReceiptAllocationTotalRows = row.TotalRowCount,
            ReceiptVoidedRows = row.VoidedRowCount,
            ReceiptAllocationState = row.AllocationState,
            ReceiptAllocationStateText = row.AllocationStateText,
            ShipmentLinkState = CustomerReconciliationStatementRules.ShipmentLinkUnknown,
            ShipmentLinkText = CustomerReconciliationStatementRules.ShipmentLinkUnavailableText,
            RemainingAmount = row.RemainingAmount,
            RemainingState = row.RemainingState,
            RemainingStateText = row.RemainingStateText,
            DueDateKnown = row.DueDateKnown,
            AgingBucket = row.AgingBucket,
            AgingBucketText = row.AgingBucketText,
        };

    /// <summary>按币种汇总（只统计有效证据行；不同币种分别成行，绝无跨币种总额）</summary>
    private static List<CustomerReconciliationCurrencySummaryDto> BuildCurrencySummaries(
        IReadOnlyList<CustomerReconciliationStatementRowDto> rows)
        => rows
            .Where(r => r.IsActiveEvidence)
            .GroupBy(r => r.Currency, StringComparer.Ordinal)
            .Select(g =>
            {
                var active = g.ToList();
                var remaining = active.Select(r => r.RemainingAmount).ToList();
                return new CustomerReconciliationCurrencySummaryDto
                {
                    Currency = g.Key,
                    AmountDecimals = active[0].AmountDecimals,
                    InvoiceCount = active.Count,
                    InvoiceGrossAmount = active.Sum(r => r.InvoiceGrossAmount),
                    EffectiveAllocatedAmount = active.Sum(r => r.EffectiveAllocatedAmount),
                    RemainingAmount = remaining.Any(v => v is null) ? null : remaining.Sum(v => v!.Value),
                };
            })
            .OrderBy(g => g.Currency, StringComparer.Ordinal)
            .ToList();

    private static string BuildDateRangeText(DateTime? from, DateTime? to)
        => (from, to) switch
        {
            (not null, not null) => $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}",
            (not null, null) => $"{from:yyyy-MM-dd} 起",
            (null, not null) => $"截至 {to:yyyy-MM-dd}",
            _ => string.Empty,
        };

    // ==================== CSV / HTML 输出 ====================

    /// <summary>CSV 导出（UTF-8 BOM；四类证据分别成列，金额未知留空绝不回落为 0）</summary>
    public static string BuildCsv(CustomerReconciliationStatementDto statement)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        AppendCsvLine(sb, "客户对账证据导出（ERP-086 操作性证据核对）");
        AppendCsvLine(sb, "客户", FormatCustomer(statement));
        AppendCsvLine(sb, "as-of", statement.AsOfDate.ToString("yyyy-MM-dd"));
        AppendCsvLine(sb, "生成时间", statement.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        AppendCsvLine(sb, "币种筛选", EmptyOr(statement.CurrencyFilter, "全部"));
        AppendCsvLine(sb, "发票日期区间", EmptyOr(statement.InvoiceDateRangeText, "全部"));
        AppendCsvLine(sb, "发票状态", statement.InvoiceStatusFilterText);
        AppendCsvLine(sb, "分配状态", statement.AllocationStateFilterText);
        AppendCsvLine(sb, "证据类", statement.EvidenceClassFilterText);
        AppendCsvLine(sb, "边界声明", statement.BoundaryText);
        AppendCsvLine(sb, "出货链接证据说明", statement.ShipmentLinkUnavailableText);
        AppendCsvLine(sb, "币种隔离", statement.CurrencySeparationText);
        if (statement.Truncated) AppendCsvLine(sb, "截断提示", statement.TruncatedNote);
        sb.AppendLine();

        AppendCsvLine(sb, HeaderColumns());
        foreach (var row in statement.Rows) AppendCsvLine(sb, DataCells(row));
        sb.AppendLine();

        AppendCsvLine(sb, "币种汇总（仅有效证据）");
        AppendCsvLine(sb, "币种", "发票张数", "发票含税总额", "有效收款分摊", "算术剩余");
        foreach (var c in statement.Currencies) AppendCsvLine(sb, CurrencySummaryCells(c));

        return sb.ToString();
    }

    private static string FormatCustomer(CustomerReconciliationStatementDto statement)
        => string.IsNullOrWhiteSpace(statement.CustomerCode) && string.IsNullOrWhiteSpace(statement.CustomerName)
            ? "全部客户"
            : $"{statement.CustomerCode} {statement.CustomerName}".Trim();

    private static string EmptyOr(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatAmount(decimal? value, int decimals)
        => value.HasValue
            ? value.Value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    private static string[] HeaderColumns() =>
    [
        "发票号", "发票类型", "发票代码", "开票日期", "币种", "发票状态",
        "发票含税总额(证据)", "有效收款分摊(ERP-073)", "有效分摊行数", "有效收款单数",
        "分摊行总数(ERP-075)", "已作废分摊行数", "分摊状态", "出货链接证据(ERP-076)", "算术剩余证据", "剩余状态", "账龄分组",
    ];

    private static string[] DataCells(CustomerReconciliationStatementRowDto r) =>
    [
        r.IdentityText, r.InvoiceType, r.InvoiceCode, r.InvoiceDate.ToString("yyyy-MM-dd"), r.Currency, r.InvoiceStatusText,
        FormatAmount(r.InvoiceGrossAmount, r.AmountDecimals),
        FormatAmount(r.EffectiveAllocatedAmount, r.AmountDecimals),
        r.EffectiveAllocationCount.ToString(),
        r.EffectiveReceiptCount.ToString(),
        r.ReceiptAllocationTotalRows.ToString(),
        r.ReceiptVoidedRows.ToString(),
        r.ReceiptAllocationStateText,
        r.ShipmentLinkState,
        FormatAmount(r.RemainingAmount, r.AmountDecimals),
        r.RemainingStateText,
        r.AgingBucketText,
    ];

    private static string[] CurrencySummaryCells(CustomerReconciliationCurrencySummaryDto c) =>
    [
        c.Currency,
        c.InvoiceCount.ToString(),
        FormatAmount(c.InvoiceGrossAmount, c.AmountDecimals),
        FormatAmount(c.EffectiveAllocatedAmount, c.AmountDecimals),
        FormatAmount(c.RemainingAmount, c.AmountDecimals),
    ];

    private static void AppendCsvLine(StringBuilder sb, params string[] cells)
        => sb.AppendLine(string.Join(",", cells.Select(CsvEscape)));

    private static string CsvEscape(string? value)
    {
        var v = value ?? string.Empty;
        if (!v.Contains(',') && !v.Contains('"') && !v.Contains('\n') && !v.Contains('\r')) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>HTML 导出（四类证据分别成列，金额未知留空绝不回落为 0）</summary>
    public static string BuildHtml(CustomerReconciliationStatementDto statement)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh\"><head><meta charset=\"utf-8\"><title>客户对账证据导出</title>");
        sb.AppendLine("<style>body{font-family:sans-serif;margin:16px;}table{border-collapse:collapse;margin:8px 0;}th,td{border:1px solid #ccc;padding:4px 8px;font-size:13px;}th{background:#f2f2f2;}td.num{text-align:right;}.meta td:first-child{font-weight:bold;white-space:nowrap;}.note{color:#666;}</style></head><body>");
        sb.AppendLine("<h1>客户对账证据导出</h1>");
        sb.AppendLine("<table class=\"meta\">");
        AppendHtmlMetaRow(sb, "客户", FormatCustomer(statement));
        AppendHtmlMetaRow(sb, "as-of", statement.AsOfDate.ToString("yyyy-MM-dd"));
        AppendHtmlMetaRow(sb, "生成时间", statement.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        AppendHtmlMetaRow(sb, "币种筛选", EmptyOr(statement.CurrencyFilter, "全部"));
        AppendHtmlMetaRow(sb, "发票日期区间", EmptyOr(statement.InvoiceDateRangeText, "全部"));
        AppendHtmlMetaRow(sb, "发票状态", statement.InvoiceStatusFilterText);
        AppendHtmlMetaRow(sb, "分配状态", statement.AllocationStateFilterText);
        AppendHtmlMetaRow(sb, "证据类", statement.EvidenceClassFilterText);
        sb.AppendLine("</table>");
        sb.AppendLine($"<p class=\"note\">{HtmlEncode(statement.BoundaryText)}</p>");
        sb.AppendLine($"<p class=\"note\">{HtmlEncode(statement.ShipmentLinkUnavailableText)}</p>");
        sb.AppendLine($"<p class=\"note\">{HtmlEncode(statement.CurrencySeparationText)}</p>");
        if (statement.Truncated) sb.AppendLine($"<p class=\"note\">{HtmlEncode(statement.TruncatedNote)}</p>");

        sb.AppendLine("<table><thead><tr>");
        foreach (var header in HeaderColumns()) sb.AppendLine($"<th>{HtmlEncode(header)}</th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var row in statement.Rows)
        {
            sb.AppendLine("<tr>");
            var cells = DataCells(row);
            for (var i = 0; i < cells.Length; i++)
            {
                sb.AppendLine(i is 6 or 7 or 14
                    ? $"<td class=\"num\">{HtmlEncode(cells[i])}</td>"
                    : $"<td>{HtmlEncode(cells[i])}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");

        sb.AppendLine("<h2>币种汇总（仅有效证据）</h2>");
        sb.AppendLine("<table><thead><tr><th>币种</th><th>发票张数</th><th>发票含税总额</th><th>有效收款分摊</th><th>算术剩余</th></tr></thead><tbody>");
        foreach (var c in statement.Currencies)
        {
            var cells = CurrencySummaryCells(c);
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{HtmlEncode(cells[0])}</td>");
            for (var i = 1; i < cells.Length; i++) sb.AppendLine($"<td class=\"num\">{HtmlEncode(cells[i])}</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");
        sb.AppendLine($"<footer>生成时间：{HtmlEncode(statement.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"))}</footer>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static void AppendHtmlMetaRow(StringBuilder sb, string label, string value)
        => sb.AppendLine($"<tr><td>{HtmlEncode(label)}</td><td>{HtmlEncode(value)}</td></tr>");

    private static string HtmlEncode(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}




