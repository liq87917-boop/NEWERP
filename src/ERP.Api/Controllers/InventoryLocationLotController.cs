using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 库位 + 批次库存基础（ERP-096）控制器：只读暴露「库位级 + 批次级」库存余额，
/// 并提供入库 / 出库 / 调拨 / 退货移动携带「仓库库位 + 可选批次」身份的校验接口。
/// <para>授权口径（ERP-436）：两个端点在校验 / 读取前都把当前登录身份传入服务层，由服务层复用既有
/// <see cref="StockQueryAuthorizationRules"/>（实时身份 → 账号状态 → 既有「库存查询」<c>stock-query</c>
/// 菜单 → 权威数据范围）fail closed；缺失 / 禁用 / 已删除 / 无菜单 / 无权威仓库级范围的账号一律拒绝，
/// 不返回任何库位 / 批次数量，也不新增任何授权或匿名 / 管理员回退。</para>
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

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由服务端实时授权 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>库位级 + 批次级库存余额（只读派生，可按仓库 / 商品过滤）。</summary>
    [HttpGet("balances")]
    public async Task<IActionResult> GetBalances([FromQuery] long? warehouseId, [FromQuery] long? productId)
    {
        // 身份随请求下推到服务层：先实时授权并应用范围，再统计 / 返回任何库位、商品或批次数量。
        var report = await _service.GetLocationLotBalancesAsync(warehouseId, productId, CurrentUserId());
        return Ok(ApiResponse<LocationLotBalanceReport>.Success(report));
    }

    /// <summary>校验一次库存移动携带的「仓库库位 + 可选批次」身份（入库 / 出库 / 调拨 / 退货共用）。</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] MovementLocationLotInput input)
    {
        // 先实时授权（fail closed），再校验仓库 / 库位 / 批次身份；未授权时不返回任何校验事实。
        var validated = await _service.ValidateMovementAsync(input, CurrentUserId());
        return Ok(ApiResponse<ValidatedMovementLocationLot>.Success(validated));
    }
}
