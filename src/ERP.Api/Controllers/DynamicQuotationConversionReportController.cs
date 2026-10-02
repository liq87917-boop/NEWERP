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
/// 动态报价成交率报表（ERP-206）控制器：只读的字段目录、预览与 Excel 导出接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-quotation-conversion-report</b>：返回报价成交率字段白名单目录（需登录 + 报价单菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-quotation-conversion-report</b>：按选定字段与有界日期窗口（start / end）预览当前账号数据范围内的报价成交率分桶（业务员 × 原币），稳定分页。</item>
/// <item><b>POST /api/dynamic-quotation-conversion-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序，含日期 / 原币口径上下文工作表，绝不追加跨币种金额合计）。</item>
/// <item><b>POST /api/dynamic-quotation-conversion-report/pdf</b>：导出当前选定页为 PDF（只读，复用同一有界授权预览与选定列顺序、日期与原币口径，分页渲染行与宽列，绝不追加跨币种金额合计）。</item>
/// </list>
/// <para>复用既有「报价单」（quotation）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览 / 导出请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService.GetQuotationConversionAsync"/>（ERP-205）只读完成，
/// 本控制器只做授权、字段投影与分页，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览 / 导出落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-quotation-conversion-report")]
[Authorize]
public class DynamicQuotationConversionReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicQuotationConversionReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicQuotationConversionReportCatalogDto>.Success(
            DynamicQuotationConversionReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口预览报价成交率分桶（只读、分页有界；复用 ERP-205 报价成交率口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicQuotationConversionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicQuotationConversionReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-206，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 文本单元格做公式注入转义，数值按类型写入（计数为整数、金额 / 成交率为小数），并追加「报表口径」上下文
    /// 工作表标注日期窗口与原币口径；绝不追加跨币种金额合计。每次请求重新校验身份 / 报价单菜单授权 /
    /// 业务员数据范围 / 字段 / 日期 / 分页（fail closed）。授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicQuotationConversionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"QuotationConversion_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 导出当前页为 PDF（ERP-207，只读）：复用「有界、已授权预览」与选定列顺序、日期与原币口径
    /// （每次请求重新校验身份 / 报价单菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页，fail closed），
    /// 仅导出当前页选定列；业务员 × 原币分桶行照实呈现，绝不追加跨币种金额合计。缺失中文字体（SimHei）
    /// 或渲染失败时显式失败（不返回任何文件）。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicQuotationConversionReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicQuotationConversionPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"QuotationConversion_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>复用同一有界、已授权预览管线：先校验字段 / 日期 / 分页，再每次重新校验身份 / 菜单授权 / 数据范围，最后只读查询当前页</summary>
    private async Task<DynamicQuotationConversionReportPageDto> BuildPageAsync(
        DynamicQuotationConversionReportRequest request)
    {
        // 1) 纯校验先于任何报价单读取（fail closed；含 ERP-208 应用筛选校验）
        var fieldKeys = DynamicQuotationConversionReportRules.NormalizeFields(request.Fields);
        var (start, end) = DynamicQuotationConversionReportRules.ValidateDateRange(request.Start, request.End);
        DynamicQuotationConversionReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var filter = DynamicQuotationConversionReportRules.NormalizeFilter(request.Filter);

        // 2) 每次重新校验身份 + 报价单菜单授权 + 业务员数据范围
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 3) 复用 ERP-205 的有界、作用域化报价成交率读取（业务员 × 原币分桶，金额绝不跨币种合计）
        var items = await _reportService.GetQuotationConversionAsync(start, end, scope, filter);

        var filterText = DynamicQuotationConversionReportRules.BuildFilterContext(filter);
        return DynamicQuotationConversionReportRules.BuildPage(items, fieldKeys, request.Page, request.PageSize, start, end, filterText);
    }

    /// <summary>生成 Excel：数据工作表（选定列顺序 + 类型化值 + 公式注入转义） + 「报表口径」上下文工作表</summary>
    private static byte[] BuildWorkbook(DynamicQuotationConversionReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicQuotationConversionReportRules.BuildExportRow).ToList();
        var dataBytes = ExcelExporter.ExportRows(DynamicQuotationConversionReportRules.RequiredMenuText, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表：开始 / 结束日期、原币口径、只读声明；空页显式标注；绝不追加金额合计</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicQuotationConversionReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicQuotationConversionReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(
                DynamicQuotationConversionReportRules.EscapeFormulaLeading(label) as string ?? string.Empty);
            row.CreateCell(1).SetCellValue(
                DynamicQuotationConversionReportRules.EscapeFormulaLeading(value) as string ?? string.Empty);
        }

        AddLabel(0, DynamicQuotationConversionReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(1, DynamicQuotationConversionReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(2, DynamicQuotationConversionReportRules.ContextCurrencyLabel, DynamicQuotationConversionReportRules.ContextCurrencyText);
        AddLabel(3, DynamicQuotationConversionReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        var nextRow = 4;
        if (!string.IsNullOrWhiteSpace(page.FilterText))
            AddLabel(nextRow++, DynamicQuotationConversionReportRules.ContextFilterLabel, page.FilterText);

        if (page.Rows is null || page.Rows.Count == 0)
            AddLabel(nextRow, DynamicQuotationConversionReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」报价单模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态报价成交率报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicQuotationConversionReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicQuotationConversionReportRules.RequiredMenuText}」"
                + $"（{DynamicQuotationConversionReportRules.RequiredMenuCode}）模块授权：拒绝预览动态报价成交率报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}

