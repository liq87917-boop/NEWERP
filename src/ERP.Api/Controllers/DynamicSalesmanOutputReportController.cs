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
/// 动态业务员产值证据报表（ERP-237）控制器：只读的字段目录、预览与 Excel 导出接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-salesman-output-report</b>：返回业务员产值证据字段白名单目录（需登录 + 业务员产值报表菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-salesman-output-report</b>：按选定字段与有界日期窗口（start / end）预览当前账号数据范围内的业务员 × 原币证据行，稳定分页。</item>
/// <item><b>POST /api/dynamic-salesman-output-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序，含日期 / 分页 / 来源上限 / 原币 / 未知 / 未知利润 / 来源上下文工作表，绝不追加跨币种合计）。</item>
/// <item><b>POST /api/dynamic-salesman-output-report/pdf</b>：下载当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，分页渲染，字体缺失显式失败，绝不跨币种合计或声称实际出货 / 收款 / 已实现利润）。</item>
/// </list>
/// <para>复用既有「业务员产值报表」（salesman-output）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览 / 导出请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService.GetSalesmanOutputAsync"/>（ERP-236）只读完成，
/// 本控制器只做授权、字段投影与分页，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览 / 导出落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-salesman-output-report")]
[Authorize]
public class DynamicSalesmanOutputReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicSalesmanOutputReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicSalesmanOutputReportCatalogDto>.Success(
            DynamicSalesmanOutputReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口预览业务员产值证据（只读、分页有界；复用 ERP-236 业务员产值口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicSalesmanOutputReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicSalesmanOutputReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-237，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 文本单元格做公式注入转义，已知金额 / 订单数按类型写入（数值），null 金额 / 利润显式呈现为「未知」
    /// （绝不写成数值 0），并追加「报表口径」上下文工作表标注日期窗口 / 分页 / 来源上限 / 原币 / 未知 / 未知利润 / 来源 / 页面覆盖；
    /// 绝不追加跨币种合计、绝不跨币种求和。每次请求重新校验身份 / 业务员产值报表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页（fail closed）。
    /// 授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicSalesmanOutputReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalesmanOutputEvidence_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 下载当前页为中文 PDF（ERP-238，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 已知原币金额按签名数值渲染、null 金额 / 利润显式呈现为「未知」（绝不写成 0、绝不跨币种合计），
    /// 分页渲染（宽列集拆多列页、行数超出拆多行页且重复表头），并标注日期 / 分页 / 来源上限 / 原币 / 未知 / 未知利润 / 来源 / 页面覆盖上下文；
    /// 绝不追加跨币种合计、绝不声称实际出货 / 收款 / 已实现利润。每次请求重新校验身份 / 业务员产值报表菜单授权 /
    /// 业务员数据范围 / 字段 / 日期 / 分页（fail closed）；授权撤销返回错误、不返回任何文件；字体缺失 / 渲染失败显式失败。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicSalesmanOutputReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicSalesmanOutputPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"SalesmanOutputEvidence_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>
    /// 下载当前筛选集的全匹配汇总为 Excel（ERP-240，只读）：独立重新校验身份 / 业务员产值报表菜单授权 /
    /// 业务员数据范围 / 字段 / 日期 / 分页 / 应用筛选，并复用同一服务端在全部匹配业务员 × 原币证据行上派生的
    /// ERP-239 全匹配原币汇总（与当前页 / 选定列无关，无需先预览、绝不含业务员 / 客户明细行）；绝不信任客户端行 / 金额 / 身份 / 数据范围。
    /// 工作簿含「全匹配原币汇总」数据工作表（已知金额 / 计数为数值，未知金额 / 利润 / 利润率显式「未知」）
    /// 与「报表口径」上下文工作表（日期 / 来源上限 / 覆盖范围 / 来源证据 / 未知口径 / 利润依据 / 应用筛选 / 空汇总 / 只读声明）；
    /// 绝不跨币种合计、绝无任何总计行、绝不做利润推算。授权撤销 / 无效输入 / 来源超限返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出全匹配汇总」）。</para>
    /// </summary>
    [HttpPost("export-summary")]
    public async Task<IActionResult> ExportSummary([FromBody] DynamicSalesmanOutputReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权管线：重新校验字段 / 日期 / 分页 / 应用筛选与身份 / 菜单授权 / 数据范围，
        // 并由服务端同一 BuildPageAsync 派生覆盖全部匹配业务员 × 原币证据行的全匹配汇总
        // （绝不相信客户端行 / 金额 / 身份 / 数据范围；即使未先预览或详情页越界，汇总仍覆盖全部匹配行）
        var page = await BuildPageAsync(request);
        var bytes = BuildSummaryWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalesmanOutputEvidenceSummary_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>复用同一有界、已授权预览管线：先校验字段 / 日期 / 分页 / 应用筛选，再每次重新校验身份 / 菜单授权 / 数据范围，最后只读查询当前页</summary>
    private async Task<DynamicSalesmanOutputReportPageDto> BuildPageAsync(
        DynamicSalesmanOutputReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) 纯校验先于任何订单读取（fail closed）
        var fieldKeys = DynamicSalesmanOutputReportRules.NormalizeFields(request.Fields);
        var (start, end) = DynamicSalesmanOutputReportRules.ValidateDateRange(request.Start, request.End);
        DynamicSalesmanOutputReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var filter = DynamicSalesmanOutputReportRules.NormalizeFilter(request.Filter);

        // 2) 每次重新校验身份 + 业务员产值报表菜单授权 + 业务员数据范围
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 3) 复用 ERP-236 的有界、作用域化业务员产值读取（金额按业务员 × 原币独立小计，绝不跨币种合计；
        //    500 订单来源上限 fail closed）；ERP-237 可选筛选与业务员数据范围相交后再做上限探测
        var items = await _reportService.GetSalesmanOutputAsync(start, end, scope, filter);
        var filterText = DynamicSalesmanOutputReportRules.BuildFilterContext(filter);

        return DynamicSalesmanOutputReportRules.BuildPage(items, fieldKeys, request.Page, request.PageSize, start, end, filterText);
    }

    /// <summary>生成当前页 Excel：数据工作表（选定列顺序 + 类型化值 + 公式注入转义 + null 显式未知）+ 报表口径上下文工作表</summary>
    private static byte[] BuildWorkbook(DynamicSalesmanOutputReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicSalesmanOutputReportRules.BuildExportRow).ToList();
        var dataBytes = ExcelExporter.ExportRows(DynamicSalesmanOutputReportRules.RequiredMenuText, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表：日期 / 分页 / 来源上限 / 币种 / 未知 / 未知利润 / 来源 / 页面覆盖 / 只读声明；空页显式标注；绝不追加跨币种合计</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicSalesmanOutputReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicSalesmanOutputReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(
                DynamicSalesmanOutputReportRules.EscapeFormulaLeading(label) as string ?? string.Empty);
            row.CreateCell(1).SetCellValue(
                DynamicSalesmanOutputReportRules.EscapeFormulaLeading(value) as string ?? string.Empty);
        }

        var nextRow = 0;
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextPageLabel,
            DynamicSalesmanOutputReportRules.BuildPageContext(page));
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextCurrencyLabel, page.CurrencyContextText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextUnknownLabel, page.UnknownContextText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextProfitLabel, page.ProfitContextText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextSourceLabel, page.SourceContextText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextPageOnlyLabel, DynamicSalesmanOutputReportRules.PageOnlyText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (!string.IsNullOrWhiteSpace(page.FilterText))
            AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextFilterLabel, page.FilterText);

        if (!string.IsNullOrWhiteSpace(page.EmptyText))
            AddLabel(nextRow, DynamicSalesmanOutputReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>生成全匹配原币汇总 Excel（ERP-240）：数据工作表（固定汇总列 + 类型化值 + 公式注入转义 + null 显式未知）+ 报表口径上下文工作表</summary>
    private static byte[] BuildSummaryWorkbook(DynamicSalesmanOutputReportPageDto page)
    {
        var summary = page.Summary
            ?? throw new BusinessException("全匹配原币汇总不可用", ErrorCodes.RuleConflict);

        var columns = summary.CurrencyColumns.Select(c => (c.Key, c.Label)).ToList();
        var rows = summary.CurrencyRows
            .Select(DynamicSalesmanOutputSummaryRules.BuildCurrencySummaryExportRow)
            .ToList();

        using var workbook = new XSSFWorkbook();
        AppendSummaryDataSheet(workbook, DynamicSalesmanOutputSummaryRules.SummaryCurrencySheetName, columns, rows);
        AppendSummaryContextSheet(workbook, page, summary);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加一张汇总数据工作表：表头文本做公式注入转义，已知金额 / 计数按类型写入数值单元格，未知显式文本（绝不写成 0）</summary>
    private static void AppendSummaryDataSheet(
        XSSFWorkbook workbook,
        string sheetName,
        List<(string Key, string Label)> columns,
        List<Dictionary<string, object?>> rows)
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

    /// <summary>追加全匹配汇总「报表口径」上下文工作表：日期 / 来源上限 / 覆盖范围 / 来源证据 / 未知口径 / 利润依据 / 应用筛选 / 空汇总 / 只读声明</summary>
    private static void AppendSummaryContextSheet(
        XSSFWorkbook workbook,
        DynamicSalesmanOutputReportPageDto page,
        DynamicSalesmanOutputSummaryDto summary)
    {
        var sheet = workbook.CreateSheet(DynamicSalesmanOutputReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(SafeText(label));
            row.CreateCell(1).SetCellValue(SafeText(value));
        }

        var nextRow = 0;
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddLabel(nextRow++, DynamicSalesmanOutputSummaryRules.ContextCoverageLabel, summary.CoverageText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextSourceLabel, page.SourceContextText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextUnknownLabel, page.UnknownContextText);
        AddLabel(nextRow++, DynamicSalesmanOutputSummaryRules.ContextProfitBasisLabel, summary.ProfitBasisText);
        AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextFilterLabel,
            string.IsNullOrWhiteSpace(page.FilterText) ? DynamicSalesmanOutputSummaryRules.ContextNoFilterText : page.FilterText);

        if (summary.CurrencyRows.Count == 0)
            AddLabel(nextRow++, DynamicSalesmanOutputReportRules.ContextEmptyLabel, page.EmptyText);

        AddLabel(nextRow, DynamicSalesmanOutputReportRules.ContextReadOnlyLabel, page.ReadOnlyText);
    }

    /// <summary>文本标签统一做公式注入转义（保持字面文本，与数据单元格同口径）</summary>
    private static string SafeText(string? value)
        => DynamicSalesmanOutputReportRules.EscapeFormulaLeading(value) as string ?? string.Empty;

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

    /// <summary>身份 + 既有「角色 → 菜单」业务员产值报表模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态业务员产值证据报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicSalesmanOutputReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicSalesmanOutputReportRules.RequiredMenuText}」"
                + $"（{DynamicSalesmanOutputReportRules.RequiredMenuCode}）模块授权：拒绝预览动态业务员产值证据报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}