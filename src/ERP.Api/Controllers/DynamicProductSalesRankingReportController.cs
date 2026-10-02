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
/// 动态商品销量排名报表（ERP-213）控制器：只读的发货数量证据字段目录、预览与 Excel 导出接口。
/// <list type="number">
/// <item><b>GET /api/dynamic-product-sales-ranking-report</b>：返回发货数量证据字段白名单目录（需登录 + 商品销量排名榜菜单授权 + 业务员数据范围）；</item>
/// <item><b>POST /api/dynamic-product-sales-ranking-report</b>：按选定字段与有界日期窗口（start / end）与 Top 预览当前账号数据范围内的排名行（发货数量证据，排除金额估算）；</item>
/// <item><b>POST /api/dynamic-product-sales-ranking-report/export</b>：导出选定 Top 结果为 Excel（xlsx，只读，复用有界授权预览与选定列顺序，含日期 / Top / 单位与发货证据口径上下文工作表，绝不追加金额合计）。</item>
/// <item><b>POST /api/dynamic-product-sales-ranking-report/pdf</b>：下载选定 Top 结果为中文 PDF（只读，复用有界授权预览与选定列顺序，分页渲染，字体缺失显式失败，绝不追加金额合计）。</item>
/// </list>
/// <para>复用既有「商品销量排名榜」菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 每次目录 / 预览 / 导出请求都重新校验身份、菜单授权与业务员数据范围（fail closed），
/// 数据由既有 <see cref="ReportService.GetProductSalesRankingAsync"/>（ERP-212）只读完成，
/// 本控制器只做授权、字段投影与 Top 有界返回，不做写入。请求由既有 <c>OperationLogMiddleware</c> 按 HTTP 方法记录审计
/// （POST 预览 / 导出落操作日志，GET 目录沿用只读约定）。</para>
/// </summary>
[ApiController]
[Route("api/dynamic-product-sales-ranking-report")]
[Authorize]
public class DynamicProductSalesRankingReportController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IReportService _reportService;

    public DynamicProductSalesRankingReportController(IErpDbContext db, IReportService reportService)
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
        return Ok(ApiResponse<DynamicProductSalesRankingReportCatalogDto>.Success(
            DynamicProductSalesRankingReportRules.GetCatalogDto()));
    }

    /// <summary>按选定字段与有界日期窗口 / Top 预览发货数量排名（只读、Top 有界；复用 ERP-212 商品销量排名口径）</summary>
    [HttpPost]
    public async Task<IActionResult> Preview([FromBody] DynamicProductSalesRankingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(ApiResponse<DynamicProductSalesRankingReportPageDto>.Success(await BuildPageAsync(request)));
    }

    /// <summary>
    /// 导出选定 Top 结果为 Excel（ERP-213，只读）：复用同一有界、已授权预览与选定列顺序，仅导出选定 Top 结果字段；
    /// 文本单元格做公式注入转义，数值（排名 / 商品Id / 发货数量）按类型写入数值单元格，并追加「报表口径」上下文
    /// 工作表标注日期窗口 / Top 限定 / 发货证据 / 单位口径；绝不追加金额估算合计。每次请求重新校验身份 / 商品销量排名榜菜单授权 /
    /// 业务员数据范围 / 字段 / 日期 / Top（fail closed）。授权撤销返回错误、不返回任何工作簿。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] DynamicProductSalesRankingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = BuildWorkbook(page);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"ProductSalesRanking_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 下载选定 Top 结果为中文 PDF（ERP-214，只读）：复用同一有界、已授权预览管线，每次请求重新校验身份 /
    /// 商品销量排名榜菜单授权 / 业务员数据范围 / 字段 / 日期 / Top（fail closed），仅导出选定 Top 结果字段；
    /// 选定列顺序、中文标签、日期窗口 / Top 限定 / 单位口径 / 已审核发货证据口径与空结果说明显式保留，宽列集 / 行数超出按
    /// 列页 / 行页分页，绝不追加金额估算合计、绝不跨单位合计数量。
    /// <para>中文字体固定使用 Windows 黑体（SimHei，共享解析器），字体缺失或渲染失败时显式失败（不产出乱码 / 缺字 / 损坏 PDF）。</para>
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] DynamicProductSalesRankingReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await BuildPageAsync(request);
        var bytes = DynamicProductSalesRankingPdfExporter.Export(page);
        return File(bytes, "application/pdf", $"ProductSalesRanking_{DateTime.Now:yyyyMMddHHmmss}.pdf");
    }

    /// <summary>复用同一有界、已授权预览管线：先校验字段 / 日期 / Top，再每次重新校验身份 / 菜单授权 / 数据范围，最后只读查询 Top 排名</summary>
    private async Task<DynamicProductSalesRankingReportPageDto> BuildPageAsync(
        DynamicProductSalesRankingReportRequest request)
    {
        // 1) 纯校验先于任何发货数据读取（fail closed；含 ERP-215 客户 / 商品 / 单位筛选校验）
        var fieldKeys = DynamicProductSalesRankingReportRules.NormalizeFields(request.Fields);
        var (start, end) = DynamicProductSalesRankingReportRules.ValidateDateRange(request.Start, request.End);
        var top = DynamicProductSalesRankingReportRules.ValidateTop(request.Top);
        var filter = DynamicProductSalesRankingReportRules.NormalizeFilter(request.Filter);

        // 2) 每次重新校验身份 + 商品销量排名榜菜单授权 + 业务员数据范围
        var scope = await EnsureAuthorizedAsync(CurrentUserId());

        // 3) 复用 ERP-212 的有界、作用域化发货数量读取（已审核销售出库，按商品 / 规格 / 单位分桶；筛选在分组 / 排名 / Take 之前应用）
        var items = await _reportService.GetProductSalesRankingAsync(start, end, top, scope, filter);

        var filterText = DynamicProductSalesRankingReportRules.BuildFilterContext(filter);
        return DynamicProductSalesRankingReportRules.BuildPage(items, fieldKeys, top, start, end, filterText);
    }

    /// <summary>生成 Excel：数据工作表（选定列顺序 + 类型化值 + 公式注入转义） + 「报表口径」上下文工作表</summary>
    private static byte[] BuildWorkbook(DynamicProductSalesRankingReportPageDto page)
    {
        var columns = page.Columns.Select(c => (c.Key, c.Label)).ToList();
        var rows = page.Rows.Select(DynamicProductSalesRankingReportRules.BuildExportRow).ToList();
        var dataBytes = ExcelExporter.ExportRows(DynamicProductSalesRankingReportRules.RequiredMenuText, rows, columns);

        using var input = new MemoryStream(dataBytes);
        using var workbook = new XSSFWorkbook(input);
        AppendContextSheet(workbook, page);

        using var output = new MemoryStream();
        workbook.Write(output);
        return output.ToArray();
    }

    /// <summary>追加「报表口径」上下文工作表：开始 / 结束日期、Top 限定、发货证据口径、单位口径、只读声明；空结果显式标注；绝不追加金额合计</summary>
    private static void AppendContextSheet(XSSFWorkbook workbook, DynamicProductSalesRankingReportPageDto page)
    {
        var sheet = workbook.CreateSheet(DynamicProductSalesRankingReportRules.ContextSheetName);

        void AddLabel(int rowIndex, string label, string value)
        {
            var row = sheet.CreateRow(rowIndex);
            row.CreateCell(0).SetCellValue(
                DynamicProductSalesRankingReportRules.EscapeFormulaLeading(label) as string ?? string.Empty);
            row.CreateCell(1).SetCellValue(
                DynamicProductSalesRankingReportRules.EscapeFormulaLeading(value) as string ?? string.Empty);
        }

        AddLabel(0, DynamicProductSalesRankingReportRules.ContextStartLabel, page.Start.ToString("yyyy-MM-dd"));
        AddLabel(1, DynamicProductSalesRankingReportRules.ContextEndLabel, page.End.ToString("yyyy-MM-dd"));
        AddLabel(2, DynamicProductSalesRankingReportRules.ContextTopLabel,
            DynamicProductSalesRankingReportRules.BuildTopContext(page.Top, page.TopLimited));
        AddLabel(3, DynamicProductSalesRankingReportRules.ContextApprovedShipmentLabel, page.ApprovedShipmentText);
        AddLabel(4, DynamicProductSalesRankingReportRules.ContextUnitLabel, page.UnitContextText);
        AddLabel(5, DynamicProductSalesRankingReportRules.ContextReadOnlyLabel, page.ReadOnlyText);

        var nextRow = 6;
        if (!string.IsNullOrEmpty(page.FilterText))
        {
            AddLabel(nextRow, DynamicProductSalesRankingReportRules.ContextFilterLabel, page.FilterText);
            nextRow++;
        }

        if (page.Rows is null || page.Rows.Count == 0)
            AddLabel(nextRow, DynamicProductSalesRankingReportRules.ContextEmptyLabel, page.EmptyText);
    }

    /// <summary>身份 + 既有「角色 → 菜单」商品销量排名榜模块授权 + 业务员数据范围（fail closed，绝不猜测身份）</summary>
    private async Task<SalespersonDataScope> EnsureAuthorizedAsync(long? userId)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再预览动态商品销量排名报表", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
            _db, userId.Value);
        if (!menuCodes.Contains(DynamicProductSalesRankingReportRules.RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{DynamicProductSalesRankingReportRules.RequiredMenuText}」"
                + $"（{DynamicProductSalesRankingReportRules.RequiredMenuCode}）模块授权：拒绝预览动态商品销量排名报表"
                + "（fail closed，不返回任何数据）",
                ErrorCodes.Forbidden);
        }

        return await SalespersonDataScopeService.ResolveAsync(_db, userId);
    }
}
