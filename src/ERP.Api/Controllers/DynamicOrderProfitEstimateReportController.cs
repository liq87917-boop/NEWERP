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
/// 动态订单利润暂估报表（ERP-221）控制器：只读的字段目录、预览与 Excel 导出接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-order-profit-estimate-report</b>：返回订单利润暂估字段白名单目录（需登录 + 订单利润暂估表菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-order-profit-estimate-report</b>：按选定字段与有界日期窗口（start / end）预览当前账号数据范围内的订单行，稳定分页。</item>
/// <item><b>POST /api/dynamic-order-profit-estimate-report/export</b>：导出当前选定页为 Excel（xlsx，只读，复用有界授权预览与选定列顺序，含日期 / 分页 / 来源上限 / 原币 / 依据上下文工作表，绝不追加跨币种金额合计）。</item>
/// <item><b>POST /api/dynamic-order-profit-estimate-report/pdf</b>：下载当前选定页为分页中文 PDF（只读，复用有界授权预览与选定列顺序，分页渲染，字体缺失显式失败，绝不跨币种合计或声称已实现利润）。</item>
/// </list>
/// <para>复用既有「订单利润暂估表」（order-profit）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览 / 导出请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService.GetOrderProfitEstimateAsync"/>（ERP-219 / ERP-220）只读完成，
/// 本控制器只做授权、字段投影与分页，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览 / 导出落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-order-profit-estimate-report")]
[Authorize]
public class DynamicOrderProfitEstimateReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicOrderProfitEstimateReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicOrderProfitEstimateReportCatalogDto>.Success(
            DynamicOrderProfitEstimateReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口预览订单利润暂估（只读、分页有界；复用 ERP-219 / ERP-220 订单利润暂估口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicOrderProfitEstimateReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicOrderProfitEstimateReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-221，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 文本单元格做公式注入转义，数值按类型写入（身份 / 销售额 / 当前价估算为数值，日期为安全文本），
    /// null 金额显式呈现为「未知」（绝不写成数值 0），并追加「报表口径」上下文工作表标注日期窗口 / 分页 /
    /// 来源上限 / 原币口径 / 缺失成本依据 / 页面覆盖；绝不追加跨币种金额合计。每次请求重新校验身份 /
    /// 订单利润暂估表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页（fail closed）。授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicOrderProfitEstimateReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"OrderProfitEstimate_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 下载当前页为分页中文 PDF（ERP-222，只读）：复用同一有界、已授权预览与选定列顺序，仅导出当前页选定列；
    /// 已知销售额按数值、未知成本 / 利润 / 利润率显式「未知」呈现；原币口径 / 未知成本利润依据 / 页面覆盖 / 来源上限
    /// 上下文始终呈现（即使对应列被取消选择），宽列集跨页拆分，绝不跨币种合计、绝不声称已实现利润；空证据显式说明。
    /// 每次请求重新校验身份 / 订单利润暂估表菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页（fail closed）；
    /// 字体缺失或渲染失败返回清晰错误、不返回任何（损坏）文件。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「下载 PDF」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicOrderProfitEstimateReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 复用同一有界、已授权预览：即使未先预览，也重新校验身份 / 菜单授权 / 业务员数据范围 / 字段 / 日期 / 分页，
        // 由服务端同一 BuildPageAsync 派生当前页证据（绝不相信客户端字段 / 行 / 金额 / 身份 / 数据范围）
        var page = await BuildPageAsync(request);

        var bytes = DynamicOrderProfitEstimatePdfExporter.Export(page);
        return File(bytes, "application/pdf", $"OrderProfitEstimate_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>复用同一有界、已授权预览管线：先校验字段 / 日期 / 分页，再每次重新校验身份 / 菜单授权 / 数据范围，最后只读查询当前页</summary>
    private async Task<DynamicOrderProfitEstimateReportPageDto> BuildPageAsync(
        DynamicOrderProfitEstimateReportRequest request)
    {
        // 1) 纯校验先于任何订单读取（fail closed）
        var fieldKeys = DynamicOrderProfitEstimateReportRules.NormalizeFields(request.Fields);
        var (start, end) = DynamicOrderProfitEstimateReportRules.ValidateDateRange(request.Start, request.End);
        DynamicOrderProfitEstimateReportRules.ValidatePageBounds(request.Page, request.PageSize);

        // 2) 每次重新校验身份 + 订单利润暂估表菜单授权 + 业务员数据范围
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 3) 复用 ERP-219 / ERP-220 的有界、作用域化订单利润暂估读取（金额均为订单原币，绝不跨币种合计）
        var items = await _reportService.GetOrderProfitEstimateAsync(start, end, scope);

        return DynamicOrderProfitEstimateReportRules.BuildPage(items, fieldKeys, request.Page, request.PageSize, start, end);
    }

    /// <summary>生成 Excel：数据工作表（选定列顺序 + 类型化值 + 公式注入转义 + null→未知）+「报表口径」上下文工作表</summary>
    private static byte[] BuildWorkbook(DynamicOrderProfitEstimateReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicOrderProfitEstimateReportRules.BuildExportRow).ToList();
        var dataBytes = ExcelExporter.ExportRows(DynamicOrderProfitEstimateReportRules.RequiredMenuText, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表：日期 / 分页 / 来源上限 / 原币 / 依据 / 页面覆盖 / 只读声明；空页显式标注；绝不追加跨币种金额合计</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicOrderProfitEstimateReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicOrderProfitEstimateReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(
                DynamicOrderProfitEstimateReportRules.EscapeFormulaLeading(label) as string ?? string.Empty);
            row.CreateCell(1).SetCellValue(
                DynamicOrderProfitEstimateReportRules.EscapeFormulaLeading(value) as string ?? string.Empty);
        }

        AddLabel(0, DynamicOrderProfitEstimateReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(1, DynamicOrderProfitEstimateReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(2, DynamicOrderProfitEstimateReportRules.ContextPageLabel,
            DynamicOrderProfitEstimateReportRules.BuildPageContext(page.Page, page.PageSize, page.Total, page.TotalPages, page.Truncated));
        AddLabel(3, DynamicOrderProfitEstimateReportRules.ContextSourceLimitLabel, page.SourceLimitText);
        AddLabel(4, DynamicOrderProfitEstimateReportRules.ContextCurrencyLabel, page.CurrencyContextText);
        AddLabel(5, DynamicOrderProfitEstimateReportRules.ContextBasisLabel, page.UnknownBasisText);
        AddLabel(6, DynamicOrderProfitEstimateReportRules.ContextMissingCostBasisLabel, DynamicOrderProfitEstimateReportRules.MissingCostBasisText);
        AddLabel(7, DynamicOrderProfitEstimateReportRules.ContextCoverageLabel, page.PageOnlyText);
        AddLabel(8, DynamicOrderProfitEstimateReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        if (page.Rows is null || page.Rows.Count == 0)
            AddLabel(9, DynamicOrderProfitEstimateReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」订单利润暂估表模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态订单利润暂估报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicOrderProfitEstimateReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicOrderProfitEstimateReportRules.RequiredMenuText}」"
                + $"（{DynamicOrderProfitEstimateReportRules.RequiredMenuCode}）模块授权：拒绝预览动态订单利润暂估报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
