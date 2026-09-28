using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 库位 + 批次库存基础（ERP-096）控制器：只读暴露「库位级 + 批次级」库存余额，
/// 并提供入库 / 出库 / 调拨 / 退货移动携带「仓库库位 + 可选批次」身份的校验接口。
/// <para>审计口径：本控制器只读 + 校验，不新增或修改任何表、不写任何库存 / 单据，不改变既有商品级成本口径。</para>
/// </summary>
[ApiController]
[Route("api/inventory/location-lot")]
[Authorize]
public class InventoryLocationLotController : ControllerBase
{
    private readonly IInventoryLocationLotService _service;

    public InventoryLocationLotController(IInventoryLocationLotService service)
    {
        _service = service;
    }

    /// <summary>库位级 + 批次级库存余额（只读派生，可按仓库 / 商品过滤）。</summary>
    [HttpGet("balances")]
    public async Task<IActionResult> GetBalances([FromQuery] long? warehouseId, [FromQuery] long? productId)
    {
        var report = await _service.GetLocationLotBalancesAsync(warehouseId, productId);
        return Ok(ApiResponse<LocationLotBalanceReport>.Success(report));
    }

    /// <summary>校验一次库存移动携带的「仓库库位 + 可选批次」身份（入库 / 出库 / 调拨 / 退货共用）。</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] MovementLocationLotInput input)
    {
        var validated = await _service.ValidateMovementAsync(input);
        return Ok(ApiResponse<ValidatedMovementLocationLot>.Success(validated));
    }
}
