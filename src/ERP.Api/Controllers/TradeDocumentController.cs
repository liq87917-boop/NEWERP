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

    /// <summary>
    /// 打印数据（ERP-030）：按单证台账既有字段输出打印模型（字段键 / 中文标签 / 是否有落库值），
    /// 供共享打印预览、直接打印与打印设计使用；台账中没有落库值的字段一律 Available=false，
    /// 由前端按空白渲染，不回查客户档案、不做文本推断。
    /// 打印模板由 <c>/api/sys/print-templates/doc-center</c> 提供。
    /// </summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var document = await _db.TradeDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted)
            ?? throw BusinessException.NotFound("单证不存在");

        return Ok(ApiResponse<TradeDocumentPrintModel>.Success(TradeDocumentPrintModel.From(document)));
    }

    /// <summary>
    /// 单证导出（ERP-019）：列表 / 单条（传 id）按筛选条件导出为 Excel。
    /// 支持：单证编号·客户·柜号·订单号关键字、单证类型、状态、出具日期区间；日期为空表示不限。
    /// </summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] long? id, [FromQuery] string? keyword,
        [FromQuery] string? docType, [FromQuery] string? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
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
