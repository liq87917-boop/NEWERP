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
/// 动态代理服务费月度汇总报表（ERP-181）控制器：只读的字段目录与预览接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-agency-service-fee-monthly-report</b>：返回有限字段白名单目录（需登录 + 客户资料菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-agency-service-fee-monthly-report</b>：按选定字段与有界筛选预览 ERP-180 月度汇总，稳定分页、原币隔离、状态金额口径不变。</item>
/// <item><b>POST /api/dynamic-agency-service-fee-monthly-report/export</b>：下载当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序）。</item>
/// <item><b>POST /api/dynamic-agency-service-fee-monthly-report/pdf</b>：下载当前选定页为 PDF（只读，复用有界授权预览与选定列顺序）。</item>
/// </list>
/// <para>复用 ERP-180 的 <see cref="AgencyServiceFeeMonthlySummaryService.ForQueryAsync"/>：
/// 客户 / 币种 / 对账日期筛选、稳定分页与分组 / 金额口径全部由既有只读服务完成，本控制器只做授权与字段投影，不做写入。</para>
/// <para>每次目录 / 预览请求都重新校验身份、客户资料菜单授权与业务员数据范围（fail closed）；请求由既有
/// <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计（POST 预览落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-agency-service-fee-monthly-report")]
[Authorize]
public class DynamicAgencyServiceFeeMonthlyReportController : ControllerBase
{
    private readonly IErpDbContext _db;

    public DynamicAgencyServiceFeeMonthlyReportController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权检查 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>字段白名单目录（有限、只读）</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog()
    {
        await EnsureAuthorizedAsync(CurrentUserId());
        return Ok(ApiResponse<DynamicAgencyServiceFeeMonthlyReportCatalogDto>.Success(
            DynamicAgencyServiceFeeMonthlyReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界筛选预览代理服务费月度汇总（只读、分页有界；复用 ERP-180 只读服务）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicAgencyServiceFeeMonthlyReportRequest request)
        => Ok(ApiResponse<DynamicAgencyServiceFeeMonthlyReportPageDto>.Success(
            await BuildPageAsync(request)));

    /// <summary>
    /// 下载当前选定页为 Excel（ERP-182，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），仅导出当前页选定列；金额保留原币、草稿 / 已作废金额与已登记合计分开列示，
    /// 文本单元格做公式注入转义，空页显式说明（证据数字不代表收入或应收）。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicAgencyServiceFeeMonthlyReportRequest request)
    {
        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 业务员数据范围
        var page = await BuildPageAsync(request);

        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"AgencyServiceFeeMonthly_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 下载当前选定页为 PDF（ERP-183，只读）：复用「有界、已授权预览」与选定列顺序（每次请求重新校验身份 / 菜单授权 /
    /// 字段 / 筛选 / 页大小 / 业务员数据范围），仅导出当前页选定列；金额保留原币、草稿 / 已作废金额与已登记合计分开列示，
    /// 宽列集按可用页宽拆分「列页」、行数超出拆分「行页」避免裁切，空页显式说明；缺失黑体字体时显式失败（不产出乱码 PDF）。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicAgencyServiceFeeMonthlyReportRequest request)
    {
        // 复用同一有界、已授权预览：重新校验身份 / 菜单授权 / 字段 / 筛选 / 页大小 / 业务员数据范围
        var page = await BuildPageAsync(request);

        var bytes = DynamicAgencyServiceFeeMonthlyPdfExporter.Export(page);
        return File(bytes, "application/pdf",
            $"AgencyServiceFeeMonthly_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>复用同一有界、已授权预览管线：授权 → 校验 → 只读查询 → 选定列投影（与 ERP-181 预览同源，fail closed）</summary>
    private async Task<DynamicAgencyServiceFeeMonthlyReportPageDto> BuildPageAsync(
        DynamicAgencyServiceFeeMonthlyReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 字段 / 分组 / 分页 / 筛选全部在读取 ERP-180 数据源之前校验（未知 / 重复字段、未知分组、越界分页、非法币种、日期倒置均 fail closed）
        var fieldKeys = DynamicAgencyServiceFeeMonthlyReportRules.NormalizeFields(request.Fields);
        DynamicAgencyServiceFeeMonthlyReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var groupBy = DynamicAgencyServiceFeeMonthlyReportRules.NormalizeGroupBy(request.GroupBy);
        var query = DynamicAgencyServiceFeeMonthlyReportRules.BuildQuery(request);

        var view = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(_db, query, scope);

        var columns = fieldKeys
            .Select(k => DynamicAgencyServiceFeeMonthlyReportRules.GetField(k)!)
            .ToList();
        var rows = view.Rows
            .Select(r => DynamicAgencyServiceFeeMonthlyReportRules.BuildRow(r, fieldKeys))
            .ToList();

        // 分组汇总（ERP-184 / ERP-186）：只统计当前授权预览页的月度汇总行，原币隔离，绝不跨币种合并或换算；金额按原币保留精度
        var groupCounts = DynamicAgencyServiceFeeMonthlyReportRules.BuildGroupCounts(view.Rows, groupBy);

        return new DynamicAgencyServiceFeeMonthlyReportPageDto(
            columns,
            rows,
            view.Total,
            view.Page,
            view.PageSize,
            view.TotalPages,
            view.Truncated,
            view.GroupCount,
            view.EmptyText,
            DynamicAgencyServiceFeeMonthlyReportRules.ReadOnlyText,
            DynamicAgencyServiceFeeMonthlyReportRules.BoundaryText,
            DynamicAgencyServiceFeeMonthlyReportRules.EvidenceOnlyText,
            DynamicAgencyServiceFeeMonthlyReportRules.CurrencyIsolationText,
            DynamicAgencyServiceFeeMonthlyReportRules.NoProrationText,
            groupBy,
            groupCounts,
            DynamicAgencyServiceFeeMonthlyReportRules.GroupCountScopeText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」客户资料模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览代理服务费月度汇总报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuText}」"
                + $"（{DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode}）模块授权：拒绝预览代理服务费月度汇总报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }

    /// <summary>既有选定列数据工作表名称（ERP-182，单工作表）</summary>
    private const string DataSheetName = "代理服务费月度汇总";

    /// <summary>空页显式说明（证据数字，不代表收入或应收）</summary>
    private const string EmptyPageNote = "没有符合筛选条件的代理服务费对账单证据（或已被软删除；证据数字不代表收入或应收）";

    /// <summary>分组计数工作表名称（ERP-188，仅 month / customer 分组时追加）</summary>
    private const string GroupCountSheetName = "分组计数";

    /// <summary>分组计数工作表列标题（计数单元格为数值，标签 / 币种为公式安全文本；不含金额列）</summary>
    private const string GroupLabelColumn = "分组标签";
    private const string CurrencyColumn = "原币";
    private const string RowCountColumn = "月度行数";
    private const string RegisteredCountColumn = "已登记张数";
    private const string DraftCountColumn = "草稿张数";
    private const string VoidedCountColumn = "已作废张数";
    private const string StatementCountColumn = "对账单总张数";

    /// <summary>仅本页说明（分组计数只统计当前授权预览页，不做跨币种 / 跨页合计，计数只是证据数字）</summary>
    private const string GroupCountPageOnlyNote =
        "本工作表只统计当前授权预览页的月度汇总行，不覆盖整份报表，也绝不跨币种、跨页合计；"
        + "计数只是证据数字，不代表收入 / 应收 / 已收款等会计结论。";

    /// <summary>空页显式说明（当前页没有符合分组条件的对账单证据）</summary>
    private const string GroupCountEmptyNote = "当前页没有符合分组条件的对账单证据（空页）";

    /// <summary>截断显式说明（后续分页未计入本工作表）</summary>
    private const string GroupCountTruncatedNote = "当前页已被截断，后续分页未计入本工作表（仅本页）";

    /// <summary>
    /// 生成 Excel 工作簿（ERP-182 / ERP-188，只读）：复用有界、已授权预览；none 模式保持既有单工作表不变；
    /// month / customer 分组模式在选定列证据工作表之后追加「分组计数」工作表（复用 ERP-184 同一批有界、已授权分组计数，
    /// 只计数、不含金额列、绝不声明跨页合计）。
    /// </summary>
    private static byte[] BuildWorkbook(DynamicAgencyServiceFeeMonthlyReportPageDto page)
    {
        var dataBytes = BuildDataWorkbook(page);

        if (!IsCountGroupedExport(page.GroupBy))
            return dataBytes;

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendCountGroupSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>生成选定列证据工作表（ERP-182 既有逻辑：选定列顺序 + 数值金额 + 公式注入转义 + 空页显式说明）</summary>
    private static byte[] BuildDataWorkbook(DynamicAgencyServiceFeeMonthlyReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(BuildExportRow).ToList();

        if (rows.Count == 0 && columns.Count > 0)
        {
            var emptyNote = string.IsNullOrWhiteSpace(page.EmptyText) ? EmptyPageNote : page.EmptyText;
            var noteRow = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [columns[0].Key] = EscapeFormulaLeading(emptyNote) ?? string.Empty,
            };
            rows.Add(noteRow);
        }

        return ExcelExporter.ExportRows(DataSheetName, rows, columns);
    }

    /// <summary>是否需要追加分组计数工作表（仅 month / customer；none 保持既有工作簿）</summary>
    private static bool IsCountGroupedExport(string? groupBy)
        => string.Equals(groupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, StringComparison.OrdinalIgnoreCase)
           || string.Equals(groupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer, StringComparison.OrdinalIgnoreCase);

    /// <summary>追加「分组计数」工作表：分组标签 + 原币 + 月度行数与各状态张数（数值单元格），
    /// 并显式标注空页 / 截断 / 仅本页；不含金额列、不声明跨页合计。</summary>
    private static void AppendCountGroupSheet(XSSFWorkbook workbook, DynamicAgencyServiceFeeMonthlyReportPageDto page)
    {
        var sheet = workbook.CreateSheet(GroupCountSheetName);

        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue(GroupLabelColumn);
        header.CreateCell(1).SetCellValue(CurrencyColumn);
        header.CreateCell(2).SetCellValue(RowCountColumn);
        header.CreateCell(3).SetCellValue(RegisteredCountColumn);
        header.CreateCell(4).SetCellValue(DraftCountColumn);
        header.CreateCell(5).SetCellValue(VoidedCountColumn);
        header.CreateCell(6).SetCellValue(StatementCountColumn);

        var groups = page.GroupCounts ?? new List<DynamicAgencyServiceFeeMonthlyReportGroupCountDto>();
        var rowIndex = 1;
        foreach (var group in groups)
        {
            var row = sheet.CreateRow(rowIndex++);
            row.CreateCell(0).SetCellValue(SafeGroupText(GroupLabel(group)));
            row.CreateCell(1).SetCellValue(SafeGroupText(group.Currency));
            row.CreateCell(2).SetCellValue(group.RowCount);
            row.CreateCell(3).SetCellValue(group.RegisteredCount);
            row.CreateCell(4).SetCellValue(group.DraftCount);
            row.CreateCell(5).SetCellValue(group.VoidedCount);
            row.CreateCell(6).SetCellValue(group.StatementCount);
        }

        if (groups.Count == 0)
        {
            var emptyRow = sheet.CreateRow(rowIndex++);
            emptyRow.CreateCell(0).SetCellValue(SafeGroupText(GroupCountEmptyNote));
        }

        if (page.Truncated)
        {
            var truncatedRow = sheet.CreateRow(rowIndex++);
            truncatedRow.CreateCell(0).SetCellValue(SafeGroupText(GroupCountTruncatedNote));
        }

        var pageOnlyRow = sheet.CreateRow(rowIndex);
        pageOnlyRow.CreateCell(0).SetCellValue(SafeGroupText(GroupCountPageOnlyNote));
    }

    /// <summary>分组标签：month → 年月文案（yyyy-MM）；customer → 客户名称（回退客户编码）</summary>
    private static string GroupLabel(DynamicAgencyServiceFeeMonthlyReportGroupCountDto group)
    {
        if (string.Equals(group.GroupBy, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(group.StatementMonthText)
                ? (group.StatementYear.HasValue && group.StatementMonth.HasValue
                    ? $"{group.StatementYear.Value:0000}-{group.StatementMonth.Value:00}"
                    : string.Empty)
                : group.StatementMonthText;
        }

        return string.IsNullOrWhiteSpace(group.CustomerName) ? group.CustomerCode : group.CustomerName;
    }

    /// <summary>分组工作表文本统一做公式注入转义（保持字面文本，不被当作公式执行）</summary>
    private static string SafeGroupText(string? value)
        => (string?)EscapeFormulaLeading(value) ?? string.Empty;

    /// <summary>把一页预览行转成导出行：对每个单元格做公式注入转义，键保持不变</summary>
    private static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = EscapeFormulaLeading(kv.Value);
        return export;
    }

    /// <summary>文本是否以电子表格公式字符开头（会触发 Excel 公式注入）</summary>
    private static bool IsFormulaLeading(string? value)
        => !string.IsNullOrEmpty(value)
           && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    /// <summary>转义 Excel 公式前导文本：以危险字符开头的文本前缀单引号，保持字面文本、不被当作公式执行；
    /// 数值 / 日期 / 布尔等类型原样返回（由 ExcelExporter 按其类型写入数值单元格）。</summary>
    private static object? EscapeFormulaLeading(object? value)
    {
        if (value is string s && IsFormulaLeading(s))
            return "'" + s;
        return value;
    }
}
