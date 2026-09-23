using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商报价比价控制器（阶段 2 新增）
/// 维护：同一采购需求向多家供应商询价，一条记录 = 一家供应商对某需求的报价；
///       相同 QuoteNo 归为一批，便于横向比价与标记选中（IsSelected）。
/// </summary>
[ApiController]
[Route("api/purchase/quotes")]
[Authorize]
public class PurchaseQuoteController : BaseCrudController<PurchaseQuote>
{
    public PurchaseQuoteController(IGenericService<PurchaseQuote> service) : base(service) { }
}
