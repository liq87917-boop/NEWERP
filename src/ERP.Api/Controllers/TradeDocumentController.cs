using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 出口单证台账控制器（单证中心，阶段 2 新增）
/// 维护：报关单 / 装箱单 / 商业发票 CI / 形式发票 PI / 产地证 CO·Form A·Form E / 提单 B/L 等
///       单证的登记、状态跟踪（待制作 → 已制作 → 已提交客户 → 已使用）与份数管理
/// </summary>
/// <remarks>
/// ERP-019 补齐审计发现的可用性缺口：
/// 1) 由销售订单 / 装柜清单「带入预填 + 直接生成」的入口分别落在来源单据控制器
///    （<c>/api/sales-orders/{id}/trade-documents</c>、<c>/api/container/loading-lists/{id}/trade-documents</c>），
///    共用 <see cref="TradeDocumentGeneration"/> 的映射与重复生成守卫；
/// 2) 本控制器新增 <c>export-excel</c>：单证中心列表（含筛选条件）与单条单证均可导出为 Excel，
///    导出实现沿用既有约定（<see cref="ExcelExporter.ExportRows"/> + 中文列头 + xlsx 附件）。
/// </remarks>
[ApiController]
[Route("api/trade/documents")]
[Authorize]
public class TradeDocumentController : BaseCrudController<TradeDocument>
{
    private readonly IErpDbContext _db;

    public TradeDocumentController(IGenericService<TradeDocument> service, IErpDbContext db) : base(service)
    {
        _db = db;
    }

    /// <summary>单证导出列定义（顺序即 Excel 列顺序；中文列头与页面字段一致）</summary>
    private static readonly List<(string Key, string Title)> ExcelColumns = new()
    {
        ("DocNo", "单证编号"), ("DocType", "单证类型"), ("IssueDate", "出具/签发日期"),
        ("SalesOrderNo", "关联销售订单号"), ("RefNo", "关联柜号/订舱号"), ("DeclareNo", "关联报关单号"),
        ("CustomerName", "客户名称"), ("Amount", "单证金额"), ("Currency", "币种"),
        ("DeparturePort", "起运港"), ("DestinationPort", "目的港"),
        ("IssuedBy", "制作人/出证机构"), ("Copies", "份数"), ("Status", "状态"),
        ("FileNote", "附件说明"), ("Remark", "备注"),
    };

    // ==================== ERP-052：明细行导出（layout=lines） ====================

    /// <summary>明细行导出布局参数值（`GET /export-excel?layout=lines`）</summary>
    public const string LinesLayout = "lines";

    /// <summary>明细行导出的行类型：明细行</summary>
    public const string RowTypeDetail = "明细行";

    /// <summary>明细行导出的行类型：行金额合计（按币种分开，每个币种一行）</summary>
    public const string RowTypeTotal = "行合计";

    /// <summary>明细行导出的行类型：箱数 / 净重 / 毛重合计（只累加已登记行）</summary>
    public const string RowTypePackagingTotal = "箱数/重量合计";

    /// <summary>明细行导出的行类型：该单证没有明细行（老单证照常导出一行）</summary>
    public const string RowTypeNoLines = "无明细行";

    /// <summary>明细行导出的行类型：因导出上限被截断的说明行</summary>
    public const string RowTypeTruncated = "导出截断说明";

    /// <summary>单次明细行导出的行数上限（有界；超出时截断并在末尾说明，不静默丢失）</summary>
    public const int MaxExportLineRows = 5000;

    /// <summary>明细行导出列（顺序即 Excel 列顺序；类型不适配的列为空，绝不写成 0）</summary>
    private static readonly List<(string Key, string Title)> ExcelLineColumns = new()
    {
        ("RowType", "行类型"), ("DocNo", "单证编号"), ("DocType", "单证类型"), ("Status", "状态"),
        ("IssueDate", "出具/签发日期"), ("CustomerName", "客户名称"), ("SalesOrderNo", "关联销售订单号"),
        ("RefNo", "关联柜号/订舱号"), ("LineNo", "行序"), ("ProductCode", "商品编码"),
        ("ProductNameCn", "商品中文名称"), ("ProductNameEn", "商品英文名称"), ("Spec", "规格型号"),
        ("Quantity", "数量"), ("Unit", "单位"), ("UnitPrice", "单价"), ("LineCurrency", "行币种"),
        ("LineAmount", "行金额"), ("PackageCount", "箱数"), ("NetWeight", "净重kg"),
        ("GrossWeight", "毛重kg"), ("LineRemark", "行备注"),
    };

    /// <summary>
    /// 打印数据（ERP-030；ERP-052 增加明细行快照）：按单证台账既有字段输出打印模型（字段键 / 中文标签 / 是否有落库值），
    /// 并按单证类型带出**有序明细行**（商业发票 → 单价与行金额；装箱单 → 箱数与净重 / 毛重）与行合计，
    /// 供共享打印预览、直接打印与打印设计使用；台账中没有落库值的字段一律 Available=false，
    /// 由前端按空白渲染，不回查客户档案、不做文本推断。
    /// <para>明细行一次有界查询（多取一行判定截断），未登记的箱数 / 净重 / 毛重保持 null（不写成 0）；
    /// 老单证（无明细行 / 类型不支持明细行）照常返回表头字段，打印件不含明细表。</para>
    /// 打印模板由 <c>/api/sys/print-templates/doc-center</c> 提供。
    /// </summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var document = await _db.TradeDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted)
            ?? throw BusinessException.NotFound("单证不存在");

        var (lines, truncated) = await LoadPrintLinesAsync(document);
        return Ok(ApiResponse<TradeDocumentPrintModel>.Success(
            TradeDocumentPrintModel.From(document, lines, truncated)));
    }

    /// <summary>
    /// 有界装载单证明细行（**一次**查询，避免逐行查库）：按行序升序，多取一行用于判定是否被截断；
    /// 截断时返回前 200 行并由合计说明「只是部分合计」。
    /// </summary>
    private async Task<(List<TradeDocumentPrintLine> Lines, bool Truncated)> LoadPrintLinesAsync(
        TradeDocument document)
    {
        var rows = await _db.TradeDocumentItems.AsNoTracking()
            .Where(i => !i.IsDeleted && i.TradeDocumentId == document.Id)
            .OrderBy(i => i.LineNo).ThenBy(i => i.Id)
            .Take(TradeDocumentItemRules.MaxLinesPerDocument + 1)
            .ToListAsync();

        var truncated = rows.Count > TradeDocumentItemRules.MaxLinesPerDocument;
        if (truncated) rows = rows.Take(TradeDocumentItemRules.MaxLinesPerDocument).ToList();

        var lines = rows
            .Select(row => TradeDocumentPrintLine.From(row, document.DocType, document.Currency))
            .ToList();
        return (lines, truncated);
    }

    /// <summary>单证导出（ERP-019；ERP-052 增加明细行布局）：列表 / 单条（传 id）按筛选条件导出为 Excel。
    /// 支持：单证编号·客户·柜号·订单号关键字、单证类型、状态、出具日期区间；日期为空表示不限。
    /// <para><c>layout=lines</c> 时按**明细行**导出（每行一条记录 + 行合计；无明细行的单证照常导出一行），
    /// 其余取值（默认）保持既有表头导出行为不变。</para>
    /// </summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] long? id, [FromQuery] string? keyword,
        [FromQuery] string? docType, [FromQuery] string? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end,
        [FromQuery] string? layout = null)
    {
        var source = _db.TradeDocuments.AsNoTracking().Where(d => !d.IsDeleted);
        if (id.HasValue) source = source.Where(d => d.Id == id.Value);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            source = source.Where(d => d.DocNo.Contains(kw) || d.CustomerName.Contains(kw)
                                       || d.RefNo.Contains(kw) || d.SalesOrderNo.Contains(kw)
                                       || d.DeclareNo.Contains(kw));
        }
        if (!string.IsNullOrWhiteSpace(docType)) source = source.Where(d => d.DocType == docType);
        if (!string.IsNullOrWhiteSpace(status)) source = source.Where(d => d.Status == status);
        if (start.HasValue) source = source.Where(d => d.IssueDate >= start.Value);
        if (end.HasValue) source = source.Where(d => d.IssueDate <= end.Value);

        var documents = await source.OrderByDescending(d => d.Id).ToListAsync();

        if (IsLinesLayout(layout))
        {
            var lineRows = await BuildLineExportRowsAsync(documents);
            var lineBytes = ExcelExporter.ExportRows("TradeDocumentLines", lineRows, ExcelLineColumns);
            var lineFileName = id.HasValue
                ? $"TradeDocumentLines_{id.Value}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
                : $"TradeDocumentLines_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
            return File(lineBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                lineFileName);
        }

        var rows = documents.Select(d => new Dictionary<string, object?>
        {
            ["DocNo"] = d.DocNo, ["DocType"] = d.DocType, ["IssueDate"] = d.IssueDate,
            ["SalesOrderNo"] = d.SalesOrderNo, ["RefNo"] = d.RefNo, ["DeclareNo"] = d.DeclareNo,
            ["CustomerName"] = d.CustomerName, ["Amount"] = d.Amount, ["Currency"] = d.Currency,
            ["DeparturePort"] = d.DeparturePort, ["DestinationPort"] = d.DestinationPort,
            ["IssuedBy"] = d.IssuedBy, ["Copies"] = d.Copies, ["Status"] = d.Status,
            ["FileNote"] = d.FileNote, ["Remark"] = d.Remark,
        }).ToList();

        var bytes = ExcelExporter.ExportRows("TradeDocuments", rows, ExcelColumns);
        var fileName = id.HasValue ? $"TradeDocument_{id.Value}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
            : $"TradeDocuments_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // ==================== ERP-052：明细行导出的数据构造 ====================

    /// <summary>是否按明细行布局导出（<c>layout=lines</c>；大小写不敏感，其余取值走既有表头导出）</summary>
    public static bool IsLinesLayout(string? layout)
        => string.Equals((layout ?? string.Empty).Trim(), LinesLayout, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 构造明细行导出数据行（有序）：单证（按筛选顺序）→ 该单证的明细行（按行序）→ 行合计 → 下一张单证；
    /// 无明细行的单证（老单证 / 类型不支持明细行）照常输出一行并标明「无明细行」。
    /// <para>明细行**一次有界查询**（按单证 Id 集合批量取，避免逐单证 / 逐行查库）；
    /// 超过导出上限时截断并在末尾明确说明（不静默丢失、不把截断当成完整导出）。</para>
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> BuildLineExportRowsAsync(List<TradeDocument> documents)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (documents.Count == 0) return rows;

        var documentIds = documents.Select(d => d.Id).ToList();
        var loaded = await _db.TradeDocumentItems.AsNoTracking()
            .Where(i => !i.IsDeleted && documentIds.Contains(i.TradeDocumentId))
            .OrderBy(i => i.TradeDocumentId).ThenBy(i => i.LineNo).ThenBy(i => i.Id)
            .Take(MaxExportLineRows + 1)
            .ToListAsync();

        var truncated = loaded.Count > MaxExportLineRows;
        if (truncated) loaded = loaded.Take(MaxExportLineRows).ToList();

        var byDocument = loaded.GroupBy(i => i.TradeDocumentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.LineNo).ThenBy(i => i.Id).ToList());

        foreach (var document in documents)
        {
            var documentLines = byDocument.GetValueOrDefault(document.Id);
            if (documentLines is null || documentLines.Count == 0)
            {
                rows.Add(LineExportRow(document, RowTypeNoLines, line: null, totals: null, currency: null,
                    hasTotal: false));
                continue;
            }

            var view = documentLines
                .Select(row => TradeDocumentPrintLine.From(row, document.DocType, document.Currency))
                .ToList();

            foreach (var line in view)
                rows.Add(LineExportRow(document, RowTypeDetail, line, totals: null, currency: line.Currency,
                    hasTotal: false));

            var totals = TradeDocumentPrintLineTotals.From(view);

            // 行金额按币种分开合计（不做汇率换算与跨币种合并）：每个币种一行
            foreach (var currencyTotal in totals.AmountByCurrency)
                rows.Add(LineExportRow(document, RowTypeTotal, line: null, totals, currencyTotal.Currency,
                    hasTotal: true));

            // 箱数 / 净重 / 毛重合计（只累加已登记行；无行登记时整行留空，不写成 0）
            if (totals.PackageCountRecordedLines > 0 || totals.WeightRecordedLines > 0)
                rows.Add(LineExportRow(document, RowTypePackagingTotal, line: null, totals, currency: null,
                    hasTotal: true));
        }

        if (truncated)
            rows.Add(new Dictionary<string, object?>
            {
                ["RowType"] = RowTypeTruncated,
                ["LineRemark"] = $"明细行超过单次导出上限 {MaxExportLineRows} 行：本次只导出前 {MaxExportLineRows} 行，"
                                 + "请缩小筛选范围后分批导出（系统不静默丢失、不把截断当成完整导出）",
            });

        return rows;
    }

    /// <summary>一行导出数据（行类型 + 单证表头字段 + 类型适配的明细字段；无值一律留空，绝不写成 0）</summary>
    private static Dictionary<string, object?> LineExportRow(TradeDocument document, string rowType,
        TradeDocumentPrintLine? line, TradeDocumentPrintLineTotals? totals, string? currency, bool hasTotal)
    {
        return new Dictionary<string, object?>
        {
            ["RowType"] = rowType,
            ["DocNo"] = document.DocNo,
            ["DocType"] = document.DocType,
            ["Status"] = document.Status,
            ["IssueDate"] = document.IssueDate,
            ["CustomerName"] = document.CustomerName,
            ["SalesOrderNo"] = document.SalesOrderNo,
            ["RefNo"] = document.RefNo,
            ["LineNo"] = line?.LineNo,
            ["ProductCode"] = line?.ProductCode,
            ["ProductNameCn"] = line?.ProductNameCn,
            ["ProductNameEn"] = line?.ProductNameEn,
            ["Spec"] = line?.Spec,
            ["Quantity"] = line?.Quantity,
            ["Unit"] = line?.Unit,
            // 类型适配：装箱单行不含价格口径（留空而不是 0），商业发票行不含箱数与重量口径（留空）
            ["UnitPrice"] = line is { HasPricing: true } ? line.UnitPrice : null,
            ["LineCurrency"] = line is not null ? line.Currency : currency,
            ["LineAmount"] = line is { HasPricing: true } ? line.LineAmount
                : hasTotal ? TotalAmountOf(totals, currency) : null,
            ["PackageCount"] = line is { HasPackaging: true } ? line.PackageCount
                : hasTotal ? totals?.PackageCountTotal : null,
            ["NetWeight"] = line is { HasPackaging: true } ? line.NetWeight
                : hasTotal ? totals?.NetWeightTotal : null,
            ["GrossWeight"] = line is { HasPackaging: true } ? line.GrossWeight
                : hasTotal ? totals?.GrossWeightTotal : null,
            ["LineRemark"] = line is not null ? line.Remark : TotalsRemark(rowType, totals),
        };
    }

    /// <summary>合计行的行金额（按指定币种取该币种合计；未指定币种时为空，不做跨币种合并）</summary>
    private static decimal? TotalAmountOf(TradeDocumentPrintLineTotals? totals, string? currency)
        => string.IsNullOrWhiteSpace(currency) || totals is null
            ? null
            : totals.AmountByCurrency
                .FirstOrDefault(t => string.Equals(t.Currency, currency, StringComparison.Ordinal))?.Amount;

    /// <summary>合计行备注：照实说明统计口径与登记行数（不臆造任何值）</summary>
    private static string TotalsRemark(string rowType, TradeDocumentPrintLineTotals? totals)
    {
        if (rowType == RowTypeNoLines)
            return "本单证没有明细行：照实导出，不做任何合计（不臆造行与数值）";
        if (totals is null) return string.Empty;
        if (rowType == RowTypePackagingTotal)
            return $"箱数登记 {totals.PackageCountRecordedLines} 行 / 重量登记 {totals.WeightRecordedLines} 行；"
                   + "未登记留空，不写成 0";
        return $"行金额为服务端计算值的合计（{totals.LineCount} 行）；不同币种分开列示，不做汇率换算";
    }

    // ==================== ERP-051：单证明细行快照（商业发票 / 装箱单的商品明细证据行） ====================

    /// <summary>
    /// 单证明细行清单（只读、有界）：按行序返回明细行、行金额合计与「行合计 vs 单证表头金额」提示；
    /// 只有商业发票与装箱单可维护明细行，其余类型照实标注为不支持（读取不受影响）；
    /// 已提交客户 / 已使用（以及未知状态）的单证返回只读标注，前端据此禁用编辑。
    /// <para>本接口<strong>不</strong>改写单证台账与任何来源单据，也不回写单证金额。</para>
    /// </summary>
    [HttpGet("{id:long}/items")]
    public async Task<IActionResult> GetItems(
        long id, [FromQuery] int take = TradeDocumentItemRules.MaxLinesPerDocument)
        => Ok(ApiResponse<TradeDocumentItemListDto>.Success(
            await TradeDocumentItemService.ListAsync(_db, id, take)));

    /// <summary>
    /// 新增一条明细行快照：单证必须存在 / 未删除、类型为商业发票或装箱单、状态处于准备状态
    /// （待制作 / 已制作）；数量 / 单价 / 精度 / 币种 / 箱数与重量服务端校验，行金额服务端计算
    /// （客户端金额不被信任）。
    /// <para>本接口<strong>不</strong>改写商品资料、销售订单、采购订单、装柜清单、库存与库存流水、
    /// 发票、退税、费用或财务记录，也<strong>不</strong>改动单证台账字段。</para>
    /// </summary>
    [HttpPost("{id:long}/items")]
    public async Task<IActionResult> CreateItem(long id, [FromBody] TradeDocumentItemSaveDto dto)
        => Ok(ApiResponse<TradeDocumentItemDto>.Success(
            await TradeDocumentItemService.CreateAsync(_db, id, dto),
            "明细行已保存（行快照留痕；未改写商品资料与来源单据）"));

    /// <summary>
    /// 修改一条明细行：与新增同一套校验；商品引用未变化时保留已登记的商品快照文本（绝不静默刷新），
    /// 只有显式改指商品资料时才重新取商品资料的权威快照；已提交客户 / 已使用的单证明细一律拒绝。
    /// </summary>
    [HttpPut("items/{itemId:long}")]
    public async Task<IActionResult> UpdateItem(long itemId, [FromBody] TradeDocumentItemSaveDto dto)
        => Ok(ApiResponse<TradeDocumentItemDto>.Success(
            await TradeDocumentItemService.UpdateAsync(_db, itemId, dto),
            "明细行已更新（行金额由服务端重算；未改写商品资料与来源单据）"));

    /// <summary>
    /// 显式删除一条明细行（软删除，保留审计字段）：只在准备状态允许；已提交客户 / 已使用的单证明细
    /// 既不能修改也不能删除（已冻结快照不做硬删除、不做静默替换）。
    /// </summary>
    [HttpDelete("items/{itemId:long}")]
    public async Task<IActionResult> DeleteItem(long itemId)
        => Ok(ApiResponse<TradeDocumentItemDto>.Success(
            await TradeDocumentItemService.DeleteAsync(_db, itemId),
            "明细行已删除（仅在准备状态允许；已提交 / 已使用的单证明细不可删改）"));
}
