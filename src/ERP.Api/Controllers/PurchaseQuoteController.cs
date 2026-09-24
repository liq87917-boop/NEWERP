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

    /// <summary>
    /// 批次转换计划（ERP-027，**只读**）：按比价批次号（或比价行 Id）返回「将合并为哪些采购订单」的分组计划：
    /// 每组的供应商 / 币种 / 归属客户 / 付款条件等表头口径、包含的来源行、服务端重算合计与未落库草稿，
    /// 以及不合格行（未选中 / 已放弃 / 已转 / 未维护供应商 / 数量或单价非法）的跳过原因。
    /// 不写库、不占用单据号、不改来源状态；前端据此展示「将生成几张订单」并可取消。
    /// </summary>
    [HttpGet("batch-order-plan")]
    public async Task<IActionResult> BatchOrderPlan([FromQuery] string? quoteNo, [FromQuery] long? lineId)
    {
        var build = await PurchaseQuoteConversion.BuildBatchAsync(_db, quoteNo, lineId);
        var plan = PurchaseQuoteConversion.BuildPlan(build);
        return Ok(ApiResponse<PurchaseQuoteBatchPlan>.Success(plan,
            $"比价批次 {plan.SourceNo}：可转换 {plan.EligibleLineCount} 行、将生成 {plan.GroupCount} 张采购订单"));
    }

    /// <summary>
    /// 批次转采购订单（ERP-027）：把该批次内全部「已选中」且未转换的比价行按兼容分组
    /// （供应商 + 币种 + 归属客户 / 销售订单 + 付款条件 + 是否含税）合并生成采购订单：
    /// 组内多行合并为一张订单的多行明细（金额与总额由服务端按采购订单口径重算），
    /// 每组一张订单、逐行回写来源留痕（`RefOrderNo` + 备注来源标记 + 状态「已转采购订单」）；
    /// 不合格行不静默丢弃，随响应显式返回原因。同一批次重复转换由既有两道重复守卫拦截，不产生重复单据。
    /// </summary>
    [HttpPost("batch-to-order")]
    public async Task<IActionResult> BatchToOrder([FromBody] PurchaseQuoteBatchConversionRequest request)
    {
        var result = await PurchaseQuoteConversion.ConvertBatchAsync(_db, _noService, request);
        var message = result.Skipped.Count > 0
            ? $"已生成 {result.OrderCount} 张采购订单，跳过 {result.Skipped.Count} 行不合格比价行"
            : $"已生成 {result.OrderCount} 张采购订单";
        return Ok(ApiResponse<PurchaseQuoteBatchConversionResult>.Success(result, message));
    }
}
