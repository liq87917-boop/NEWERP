using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商报价比价控制器（阶段 2 新增）
/// 维护：同一采购需求向多家供应商询价，一条记录 = 一家供应商对某需求的报价；
///       相同 QuoteNo 归为一批，便于横向比价与标记选中（IsSelected）。
/// ERP-020：选中的比价行可「带入预填 / 直接生成」采购订单（见 <see cref="PurchaseQuoteConversion" />）。
/// </summary>
[ApiController]
[Route("api/purchase/quotes")]
[Authorize]
public class PurchaseQuoteController : BaseCrudController<PurchaseQuote>
{
    private readonly IErpDbContext _db;
    private readonly IDocumentNumberService _noService;

    public PurchaseQuoteController(IGenericService<PurchaseQuote> service, IErpDbContext db,
        IDocumentNumberService noService) : base(service)
    {
        _db = db;
        _noService = noService;
    }

    /// <summary>
    /// 带入预填采购订单（ERP-020）：按选中的比价行返回一张**未落库**的采购订单草稿，
    /// 前端据此打开「采购订单 → 新增」表单继续编辑后再保存
    /// （保存走 <c>POST /api/purchase-orders</c>，服务端复核数量 / 单价 / 合计）。
    /// 与 <see cref="ToPurchaseOrder" /> 共用同一套守卫（见 <see cref="PurchaseQuoteConversion.BuildDraftAsync" />）：
    /// 只允许「已选中且未放弃、未生成过采购订单」的比价行，本接口不占用单据号、不写库。
    /// </summary>
    [HttpGet("{id:long}/order-prefill")]
    public async Task<IActionResult> OrderPrefill(long id)
    {
        var quote = await _db.PurchaseQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted)
            ?? throw BusinessException.NotFound("比价记录不存在");

        var order = await PurchaseQuoteConversion.BuildDraftAsync(_db, quote);
        return Ok(ApiResponse<PurchaseOrderPrefillResult>.Success(new PurchaseOrderPrefillResult
        {
            SourceType = PurchaseQuoteConversion.PurchaseQuoteSourceType,
            SourceId = quote.Id,
            SourceNo = quote.QuoteNo,
            Order = order
        }, "已按比价行带入采购订单草稿"));
    }

    /// <summary>
    /// 转为采购订单（ERP-020）：按选中的比价行生成一张采购订单（EF 主子表路径，不走存储过程）。
    /// 守卫：同一比价行仅生成一张（以比价行 <c>RefOrderNo</c> 指向的采购单号 +
    /// 采购订单备注来源标记为准，见 <see cref="PurchaseQuoteConversion.FindGeneratedOrderAsync" />），
    /// 只新增单据、绝不覆盖既有订单；生成后比价行状态置「已转采购订单」并把采购单号写回 <c>RefOrderNo</c> 留痕
    /// （不新增 / 不修改任何数据库结构）。
    /// </summary>
    [HttpPost("{id:long}/to-order")]
    public async Task<IActionResult> ToPurchaseOrder(long id)
    {
        var quote = await _db.PurchaseQuotes
            .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted)
            ?? throw BusinessException.NotFound("比价记录不存在");

        var order = await PurchaseQuoteConversion.BuildDraftAsync(_db, quote);
        order.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder);
        _db.PurchaseOrders.Add(order);
        PurchaseQuoteConversion.MarkConverted(quote, order.OrderNo);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<PurchaseOrderConversionResult>.Success(new PurchaseOrderConversionResult
        {
            Id = order.Id,
            OrderNo = order.OrderNo,
            SourceNo = quote.QuoteNo
        }, "已生成采购订单"));
    }
}
