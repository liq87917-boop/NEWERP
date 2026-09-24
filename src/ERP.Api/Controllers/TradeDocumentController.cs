using ERP.Application.Common;
using ERP.Application.Interfaces;
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
}
