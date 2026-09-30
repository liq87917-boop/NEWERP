using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NPOI.XSSF.UserModel;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 动态跟进提醒报表（ERP-193）控制器：只读的字段目录与预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-follow-up-due-report</b>：返回跟进提醒证据字段白名单目录（需登录 + 跟进提醒菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-follow-up-due-report</b>：按选定字段与有界筛选（as-of 日期 / 提前天数 / 可选到期状态）预览当前账号数据范围内的跟进证据，稳定分页。</item>
/// <item><b>POST /api/dynamic-follow-up-due-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/dynamic-follow-up-due-report/pdf</b>：导出当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，宽列集跨页拆分）。</item>
/// <item><b>POST /api/dynamic-follow-up-due-report/status-summary</b>：导出筛选集状态汇总为 Excel（只读，仅已逾期 / 今日到期 / 即将到期计数，不含任何明细行）。</item>
/// </list>
/// <para>复用 ERP-192 的「跟进提醒」菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览请求都重新校验身份、菜单授权与业务员数据范围（fail closed），查询由 <see cref="ReportService"/> 只读完成，
/// 本控制器只做授权与字段投影，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-follow-up-due-report")]
[Authorize]
public class DynamicFollowUpDueReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicFollowUpDueReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicFollowUpDueReportCatalogDto>.Success(
            DynamicFollowUpDueReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览跟进提醒证据（只读、分页有界；复用 ERP-192 跟进提醒口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicFollowUpDueReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-195，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 文本单元格做公式注入转义，数值 / 日期按类型写入。每次请求重新校验身份 / 跟进提醒菜单授权 /
    /// 业务员数据范围 / 字段 / 筛选 / 分页（fail closed）。授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"FollowUpDue_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>复用同一有界、已授权预览管线：每次重新校验身份 / 菜单授权 / 数据范围，再只读查询当前页</summary>
    private async Task<DynamicFollowUpDueReportPageDto> BuildPageAsync(DynamicFollowUpDueReportRequest request)
    {
        var scope = await EnsureAuthorizedAsync(CurrentUserId());
        return await _reportService.GetDynamicFollowUpDueReportAsync(request, scope);
    }

    /// <summary>
    /// 导出当前页为分页中文 PDF（ERP-196，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 /
    /// 跟进提醒菜单授权 / 业务员数据范围 / 字段 / 筛选 / 分页，fail closed），仅导出当前页选定列；选定字段、中文标签、
    /// 到期证据（下次跟进日期 / 到期天数 / 到期状态）与只读 / 边界 / 免责文案、空页说明显式保留，宽列集按可用页宽
    /// 跨页拆分、行数超出按行页拆分避免裁切。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失时显式失败（不产出乱码或缺字 PDF）。</para>
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicFollowUpDuePdfExporter.Export(page);
        return File(bytes, "application/pdf", $"FollowUpDue_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>
    /// 导出筛选集状态汇总为 Excel（ERP-203，只读）：复用同一有界、已授权预览，仅导出筛选集（分页前全量）的
    /// 已逾期 / 今日到期 / 即将到期三项状态计数，不含任何明细行；工作表标注 as-of 日期、筛选条件与范围口径。
    /// 数值计数写入数值单元格，文本做公式注入转义；每次请求重新校验身份 / 跟进提醒菜单授权 / 业务员数据范围 /
    /// 字段 / 筛选 / 分页（fail closed）。授权撤销或无效请求返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计。</para>
    /// </summary>
    [HttpPost("status-summary")]
    public async Task<IActionResult> ExportStatusSummary([FromBody] DynamicFollowUpDueReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildStatusSummaryWorkbook(request, page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"FollowUpDueStatusSummary_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>汇总表「项目 / 值」两列布局的列键与列表头（计数列使用数值单元格）</summary>
    private const string SummaryLabelKey = "label";
    private const string SummaryValueKey = "value";
    private const string SummaryLabelTitle = "项目";
    private const string SummaryValueTitle = "值";

    /// <summary>
    /// 生成筛选集状态汇总工作簿（ERP-203，只读）：只包含 as-of 日期 / 筛选条件 / 范围口径标签与三项状态计数 + 合计，
    /// 不含任何明细行或范围外数据；计数取自同一有界、已授权预览的 <see cref="DynamicFollowUpDueReportPageDto.DueStatusTotals"/>
    /// （分页前全量），文本统一做公式注入转义。
    /// </summary>
    private static byte[] BuildStatusSummaryWorkbook(
        DynamicFollowUpDueReportRequest request, DynamicFollowUpDueReportPageDto page)
    {
        var totals = page.DueStatusTotals
            ?? DynamicFollowUpDueReportRules.BuildDueStatusTotals(0, 0, 0);

        var asOf = DynamicFollowUpDueReportRules.NormalizeAsOfDate(request.AsOfDate);
        var rows = new List<Dictionary<string, object?>>
        {
            SummaryRow(DynamicFollowUpDueReportRules.SummaryAsOfLabel, asOf.ToString("yyyy-MM-dd")),
            SummaryRow(DynamicFollowUpDueReportRules.SummaryFilterLabel,
                DynamicFollowUpDueReportRules.BuildSummaryFilterContext(request)),
            SummaryRow(DynamicFollowUpDueReportRules.SummaryScopeLabel,
                DynamicFollowUpDueReportRules.SummaryScopeText),
            SummaryRow(DynamicFollowUpDueReportRules.DueOverdueText, totals.Overdue),
            SummaryRow(DynamicFollowUpDueReportRules.DueTodayText, totals.Today),
            SummaryRow(DynamicFollowUpDueReportRules.DueUpcomingText, totals.Upcoming),
            SummaryRow(DynamicFollowUpDueReportRules.SummaryTotalLabel,
                totals.Overdue + totals.Today + totals.Upcoming),
        };

        var columns = new List<(string Key, string Title)>
        {
            (SummaryLabelKey, SummaryLabelTitle),
            (SummaryValueKey, SummaryValueTitle),
        };

        return ExcelExporter.ExportRows(DynamicFollowUpDueReportRules.SummarySheetName, rows, columns);
    }

    /// <summary>汇总表单行：标签与值都做公式注入转义（文本保持字面、数值原样写入对应类型单元格）</summary>
    private static Dictionary<string, object?> SummaryRow(string label, object? value)
        => new(StringComparer.Ordinal)
        {
            [SummaryLabelKey] = DynamicFollowUpDueReportRules.EscapeFormulaLeading(label),
            [SummaryValueKey] = DynamicFollowUpDueReportRules.EscapeFormulaLeading(value),
        };

    /// <summary>分组计数工作表名称（ERP-198，仅 dueStatus / salesman 分组时追加）</summary>
    private const string GroupCountSheetName = "分组计数";

    /// <summary>分组计数工作表列标题（数量为数值单元格，键 / 标签为公式安全文本；不含未选定列）</summary>
    private const string GroupKeyColumn = "分组键";
    private const string GroupLabelColumn = "分组标签";
    private const string GroupCountColumn = "数量";

    /// <summary>分组工作表空页显式说明（当前页没有符合分组条件的跟进提醒证据）</summary>
    private const string GroupCountEmptyNote = "当前页没有符合分组条件的跟进提醒证据（空页）";

    /// <summary>
    /// 生成 Excel 工作簿（ERP-195 / ERP-198，只读）：复用有界、已授权预览；none 模式保持既有单工作表不变；
    /// dueStatus / salesman 分组模式在选定列数据工作表之后追加「分组计数」工作表（复用 ERP-197 同一批有界、已授权分组计数，
    /// 只统计当前页、绝不从全部记录重算、不暴露未选定列）。
    /// </summary>
    private static byte[] BuildWorkbook(DynamicFollowUpDueReportPageDto page)
    {
        var dataBytes = BuildDataWorkbook(page);

        if (!IsCountGroupedExport(page.GroupBy))
            return dataBytes;

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendGroupCountSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>生成选定列数据工作表（ERP-195 既有逻辑：选定列顺序 + 类型化值 + 公式注入转义）</summary>
    private static byte[] BuildDataWorkbook(DynamicFollowUpDueReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicFollowUpDueReportRules.BuildExportRow).ToList();
        return ExcelExporter.ExportRows(DynamicFollowUpDueReportRules.RequiredMenuText, rows, columns);
    }

    /// <summary>是否需要追加分组计数工作表（仅 dueStatus / salesman；none 保持既有单工作表）</summary>
    private static bool IsCountGroupedExport(string? groupBy)
        => string.Equals(groupBy, DynamicFollowUpDueReportRules.GroupDueStatus, StringComparison.OrdinalIgnoreCase)
           || string.Equals(groupBy, DynamicFollowUpDueReportRules.GroupSalesman, StringComparison.OrdinalIgnoreCase);

    /// <summary>追加「分组计数」工作表：分组键 + 标签 + 数量（数值单元格），并显式标注空页；只统计当前页、不重算。</summary>
    private static void AppendGroupCountSheet(XSSFWorkbook workbook, DynamicFollowUpDueReportPageDto page)
    {
        var sheet = workbook.CreateSheet(GroupCountSheetName);

        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue(GroupKeyColumn);
        header.CreateCell(1).SetCellValue(GroupLabelColumn);
        header.CreateCell(2).SetCellValue(GroupCountColumn);

        var groups = page.Groups ?? new List<DynamicFollowUpDueReportGroupDto>();
        var rowIndex = 1;
        foreach (var group in groups)
        {
            var row = sheet.CreateRow(rowIndex++);
            row.CreateCell(0).SetCellValue(SafeGroupText(group.Key));
            row.CreateCell(1).SetCellValue(SafeGroupText(group.Label));
            row.CreateCell(2).SetCellValue(group.Count);
        }

        if (IsEmptyPage(page))
        {
            var emptyRow = sheet.CreateRow(rowIndex);
            emptyRow.CreateCell(0).SetCellValue(SafeGroupText(GroupCountEmptyNote));
        }
    }

    /// <summary>当前页是否为空（没有已分页的跟进提醒行）</summary>
    private static bool IsEmptyPage(DynamicFollowUpDueReportPageDto page)
        => page.Rows is null || page.Rows.Count == 0;

    /// <summary>分组工作表文本统一做公式注入转义（保持字面文本，不被当作公式执行）</summary>
    private static string SafeGroupText(string? value)
        => DynamicFollowUpDueReportRules.EscapeFormulaLeading(value) as string ?? string.Empty;

    /// <summary>身份 + 既有「角色 → 菜单」跟进提醒模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态跟进提醒报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicFollowUpDueReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicFollowUpDueReportRules.RequiredMenuText}」"
                + $"（{DynamicFollowUpDueReportRules.RequiredMenuCode}）模块授权：拒绝预览动态跟进提醒报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
