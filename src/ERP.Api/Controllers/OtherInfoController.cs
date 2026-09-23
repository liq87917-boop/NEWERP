using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 其他资料控制器（币种、港口、货代、唛头等）
/// </summary>
[ApiController]
[Route("api/base/other-infos")]
[Authorize]
public class OtherInfoController : BaseCrudController<BaseOtherInfo>
{
    public OtherInfoController(IGenericService<BaseOtherInfo> service) : base(service) { }

    /// <summary>按资料类型查询（如 Currency/Port/Forwarder/ShippingMark）</summary>
    [HttpGet("by-type/{infoType}")]
    public async Task<IActionResult> GetByType(string infoType)
    {
        var items = await Service.GetAllAsync(o => o.InfoType == infoType && o.Status == 1);
        return Ok(ApiResponse<List<BaseOtherInfo>>.Success(items));
    }
}
