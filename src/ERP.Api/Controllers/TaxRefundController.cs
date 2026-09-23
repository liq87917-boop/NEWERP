using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 出口退税台账控制器（阶段 1 新增）
/// 维护：按报关单/出口发票记录退税率、可退税额、已退税额与到账日期
/// </summary>
[ApiController]
[Route("api/base/tax-refunds")]
[Authorize]
public class TaxRefundController : BaseCrudController<BaseTaxRefund>
{
    public TaxRefundController(IGenericService<BaseTaxRefund> service) : base(service) { }
}
