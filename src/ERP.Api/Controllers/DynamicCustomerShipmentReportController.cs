using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态客户出货量证据报表（ERP-229）控制器：只读的字段目录、预览与 Excel 导出接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-customer-shipment-report</b>：返回客户出货量证据字段白名单目录（需登录 + 客户出货量统计表菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-customer-shipment-report</b>：按选定字段与有界日期窗口（start / end）预览当前账号数据范围内的客户 × 原币证据行，稳定分页。</item>
/// <item><b>POST /api/dynamic-customer-shipment-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序，含日期 / 分页 / 来源上限 / 原币 / 单位 / 未知 / 来源上下文工作表，绝不追加跨币种 / 跨单位合计）。</item>
/// <item><b>POST /api/dynamic-customer-shipment-report/pdf</b>：下载当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，分页渲染行与宽列，字体缺失显式失败，绝不跨币种 / 跨单位合计或声称实际出库 / 装柜 / 收款）。</item>
/// </list>
/// <para>复用既有「客户出货量统计表」（customer-shipment）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览 / 导出请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService.GetCustomerShipmentStatsAsync"/>（ERP-227 / ERP-228）只读完成，
/// 本控制器只做授权、字段投影与分页，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览 / 导出落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-customer-shipment-report")]
[Authorize]
public class DynamicCustomerShipmentReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicCustomerShipmentReportController(IErpDbContext db, IReportService reportService)
    {
        _db = db;
        _reportService = reportService;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>字段白名单目录（有限、只读）</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        await EnsureAuthorizedAsync(CurrentUserId());
        return Ok(ApiResponse<DynamicCustomerShipmentReportCatalogDto>.Success(
            DynamicCustomerShipmentReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口预览客户出货量证据（只读、分页有界；复用 ERP-227 / ERP-228 客户出货量统计口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicCustomerShipmentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicCustomerShipmentReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-229，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 文本单元格做公式注入转义，已知金额 / 订单数 / 数量按类型写入（数值），null 金额 / 数量显式呈现为「未知」
    /// （绝不写成数值 0），并追加「报表口径」上下文工作表标注日期窗口 / 分页 / 来源上限 / 原币 / 单位 / 未知 / 来源 / 页面覆盖；
    /// 绝不追加跨币种 / 跨单位金额或数量合计。每次请求重新校验身份 / 客户出货量统计表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页（fail closed）。
    /// 授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicCustomerShipmentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"CustomerShipmentEvidence_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 下载当前页为中文 PDF（ERP-230，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 分页渲染行与宽列，中文字体固定使用 Windows 黑体（SimHei），字体缺失或渲染失败显式失败（不产出乱码 / 缺字 / 损坏 PDF）；
    /// 保持原币 / 单位 / 未知 / 日期 / 分页 / 来源上限 / 已审核订单证据上下文（即使对应列被取消选择），绝不追加跨币种 / 跨单位合计、
    /// 绝不声称实际出库 / 装柜 / 收款。每次请求重新校验身份 / 客户出货量统计表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页（fail closed），
    /// 授权撤销 / 无效 / 越界请求返回错误、不返回任何文件。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「下载 PDF」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicCustomerShipmentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicCustomerShipmentPdfExporter.Export(page);
        return File(bytes, "application/pdf",
            $"CustomerShipmentEvidence_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>
    /// 下载当前筛选集的全匹配汇总为 Excel（ERP-233，只读）：独立重新校验身份 / 客户出货量统计表菜单授权 /
    /// 业务员数据范围 / 字段 / 日期 / 分页 / 应用筛选，并复用同一服务端在全部匹配客户 × 原币证据行上派生的
    /// ERP-232 全匹配汇总（与当前页 / 选定列无关，无需先预览、绝不含客户明细行）；绝不信任客户端行 / 金额 / 身份 / 数据范围。
    /// 工作簿含「原币金额汇总」「精确单位数量汇总」两张数据工作表（已知金额 / 数量 / 计数为数值，未知显式「未知」）
    /// 与「报表口径」上下文工作表（日期 / 来源上限 / 覆盖范围 / 来源证据 / 未知口径 / 完整度 / 应用筛选 / 只读声明）；
    /// 绝不把金额复制到单位行、绝不跨币种 / 跨单位合计、绝无任何总计行。授权撤销 / 无效输入 / 来源超限返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出全匹配汇总」）。</para>
    /// </summary>
    [HttpPost("export-summary")]
    public async Task<IActionResult> ExportSummary([FromBody] DynamicCustomerShipmentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权管线：重新校验字段 / 日期 / 分页 / 应用筛选与身份 / 菜单授权 / 数据范围，
        // 并由服务端同一 BuildPageAsync 派生覆盖全部匹配客户 × 原币证据行的全匹配汇总
        // （绝不相信客户端行 / 金额 / 身份 / 数据范围；即使未先预览或详情页越界，汇总仍覆盖全部匹配行）
        var page = await BuildPageAsync(request);
        var bytes = BuildSummaryWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"CustomerShipmentEvidenceSummary_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>复用同一有界、已授权预览管线：先校验字段 / 日期 / 分页，再每次重新校验身份 / 菜单授权 / 数据范围，最后只读查询当前页</summary>
    private async Task<DynamicCustomerShipmentReportPageDto> BuildPageAsync(
        DynamicCustomerShipmentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 纯校验先于任何订单读取（fail closed）
        var fieldKeys = DynamicCustomerShipmentReportRules.NormalizeFields(request.Fields);
        var (start, end) = DynamicCustomerShipmentReportRules.ValidateDateRange(request.Start, request.End);
        DynamicCustomerShipmentReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var filter = CustomerShipmentReportFilterRules.NormalizeFilter(request.Filter);

        // 2) 每次重新校验身份 + 客户出货量统计表菜单授权 + 业务员数据范围
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 3) 复用 ERP-227 / ERP-228 的有界、作用域化客户出货量读取（金额按客户 × 原币独立小计，绝不跨币种 / 跨单位合计；
        //    500 订单 / 10000 明细来源上限 fail closed）；ERP-231 可选筛选与业务员数据范围相交后再做上限探测
        var items = await _reportService.GetCustomerShipmentStatsAsync(start, end, scope, filter);
        var filterText = CustomerShipmentReportFilterRules.BuildFilterContext(filter);

        return DynamicCustomerShipmentReportRules.BuildPage(items, fieldKeys, request.Page, request.PageSize, start, end, filterText);
    }

    /// <summary>生成当前页 Excel：数据工作表（选定列顺序 + 类型化值 + 公式注入转义 + null 显式未知）+ 报表口径上下文工作表</summary>
    private static byte[] BuildWorkbook(DynamicCustomerShipmentReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicCustomerShipmentReportRules.BuildExportRow).ToList();
        var dataBytes = ExcelExporter.ExportRows(DynamicCustomerShipmentReportRules.RequiredMenuText, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表：日期 / 分页 / 来源上限 / 币种 / 单位 / 未知 / 来源 / 页面覆盖 / 只读声明；空页显式标注；绝不追加跨币种 / 跨单位合计</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicCustomerShipmentReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicCustomerShipmentReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(
                DynamicCustomerShipmentReportRules.EscapeFormulaLeading(label) as string ?? string.Empty);
            row.CreateCell(1).SetCellValue(
                DynamicCustomerShipmentReportRules.EscapeFormulaLeading(value) as string ?? string.Empty);
        }

        var nextRow = 0;
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextPageLabel,
            DynamicCustomerShipmentReportRules.BuildPageContext(page));
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextCurrencyLabel, page.CurrencyContextText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextUnitLabel, page.UnitContextText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextUnknownLabel, page.UnknownContextText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextSourceLabel, page.SourceContextText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextPageOnlyLabel, DynamicCustomerShipmentReportRules.PageOnlyText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (!string.IsNullOrWhiteSpace(page.FilterText))
            AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextFilterLabel, page.FilterText);

        if (!string.IsNullOrWhiteSpace(page.EmptyText))
            AddLabel(nextRow, DynamicCustomerShipmentReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>生成全匹配汇总 Excel（ERP-233）：原币金额汇总 + 精确单位数量汇总两张数据工作表 +「报表口径」上下文工作表；绝不含客户明细行或总计行</summary>
    private static byte[] BuildSummaryWorkbook(DynamicCustomerShipmentReportPageDto page)
    {
        var summary = page.Summary
            ?? throw BusinessException.RuleConflict("全匹配汇总不可用");

        var currencyColumns = summary.CurrencyColumns.Select(c => (c.Key, c.Label)).ToList();
        var currencyRows = summary.CurrencyRows
            .Select(DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow).ToList();

        var unitColumns = summary.UnitColumns.Select(c => (c.Key, c.Label)).ToList();
        var unitRows = summary.UnitRows
            .Select(DynamicCustomerShipmentReportRules.BuildUnitSummaryExportRow).ToList();

        using var workbook = new XSSFWorkbook();
        AppendSummaryDataSheet(workbook, DynamicCustomerShipmentReportRules.SummaryCurrencySheetName, currencyColumns, currencyRows);
        AppendSummaryDataSheet(workbook, DynamicCustomerShipmentReportRules.SummaryUnitSheetName, unitColumns, unitRows);
        AppendSummaryContextSheet(workbook, page, summary);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加一张汇总数据工作表：表头文本做公式注入转义，已知金额 / 数量 / 计数按类型写入数值单元格，未知显式文本（绝不写成 0）</summary>
    private static void AppendSummaryDataSheet(
        XSSFWorkbook workbook,
        string sheetName,
        IReadOnlyList<(string Key, string Label)> columns,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var sheet = workbook.CreateSheet(sheetName);

        var header = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
            header.CreateCell(c).SetCellValue(SafeText(columns[c].Label));

        for (var r = 0; r < rows.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            for (var c = 0; c < columns.Count; c++)
            {
                var value = rows[r].TryGetValue(columns[c].Key, out var v) ? v : null;
                SetTypedCell(row.CreateCell(c), value);
            }
        }
    }

    /// <summary>追加全匹配汇总「报表口径」上下文工作表：日期 / 来源上限 / 覆盖范围 / 来源证据 / 未知口径 / 完整度 / 原币证据 / 应用筛选 / 空汇总 / 只读声明</summary>
    private static void AppendSummaryContextSheet(
        XSSFWorkbook workbook,
        DynamicCustomerShipmentReportPageDto page,
        DynamicCustomerShipmentSummaryDto summary)
    {
        var sheet = workbook.CreateSheet(DynamicCustomerShipmentReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(SafeText(label));
            row.CreateCell(1).SetCellValue(SafeText(value));
        }

        var nextRow = 0;
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextCoverageLabel, summary.CoverageText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextSourceLabel, page.SourceContextText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextUnknownLabel, page.UnknownContextText);

        var completenessText = summary.CompletenessReasons is { Count: > 0 }
            ? $"{summary.IncompleteBucketCount} 个不完整桶；{string.Join("；", summary.CompletenessReasons)}"
            : "数量证据完整";
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextCompletenessLabel, completenessText);

        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextCurrencyEvidenceLabel, DynamicCustomerShipmentReportRules.ContextCurrencyEvidenceText);
        AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (!string.IsNullOrWhiteSpace(page.FilterText))
            AddLabel(nextRow++, DynamicCustomerShipmentReportRules.ContextFilterLabel, page.FilterText);

        if (summary.CurrencyRows.Count == 0 && summary.UnitRows.Count == 0)
            AddLabel(nextRow, DynamicCustomerShipmentReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>文本标签统一做公式注入转义（保持字面文本，与数据单元格同口径）</summary>
    private static string SafeText(string? value)
        => DynamicCustomerShipmentReportRules.EscapeFormulaLeading(value) as string ?? string.Empty;

    /// <summary>按值类型写入单元格：整数 / 小数写入数值单元格，其余写入文本；null 不写成 0（由导出行保证未知显式文本）</summary>
    private static void SetTypedCell(ICell cell, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                cell.SetCellValue(string.Empty);
                break;
            case int i:
                cell.SetCellValue(i);
                break;
            case long l:
                cell.SetCellValue(l);
                break;
            case decimal m:
                cell.SetCellValue((double)m);
                break;
            case double d:
                cell.SetCellValue(d);
                break;
            case float f:
                cell.SetCellValue((double)f);
                break;
            default:
                cell.SetCellValue(value.ToString() ?? string.Empty);
                break;
        }
    }


    /// <summary>身份 + 既有「角色 → 菜单」客户出货量统计表模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态客户出货量证据报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicCustomerShipmentReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicCustomerShipmentReportRules.RequiredMenuText}」"
                + $"（{DynamicCustomerShipmentReportRules.RequiredMenuCode}）模块授权：拒绝预览动态客户出货量证据报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
