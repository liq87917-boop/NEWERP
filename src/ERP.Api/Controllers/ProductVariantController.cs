using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 商品规格变体控制器（ERP-037）：商品资料下的颜色 / 尺码 SKU 子表维护。
/// <para>路由挂在既有商品资源下（<c>/api/base/products/{productId}/variants</c>），
/// 商品身份与商品接口保持不变：规格是「零到多条」的可选细分，没有规格的商品仍按单规格商品使用。</para>
/// <para>边界：所有接口只读写 <c>BaseProductVariants</c> 子表 —— 不改写任何历史单据行
/// （询价 / 报价 / PI / 订单 / 库存 / 库存流水），不拆分或重算已有库存，也不触达外部系统；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/base/products/{productId:long}/variants")]
[Authorize]
public class ProductVariantController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ProductVariantController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 规格明细（含停用规格，便于历史可读）：按排序号 / Id 返回，并按上限收敛为有界视图。
    /// <paramref name="activeOnly"/> 为真时只返回启用中的规格（可选用口径）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(long productId, [FromQuery] bool activeOnly = false)
    {
        var variants = await ProductVariantService.ListAsync(_db, productId, activeOnly);
        return Ok(ApiResponse<List<ProductVariantDto>>.Success(variants));
    }

    /// <summary>
    /// 可选用规格（只返回启用中的规格）：停用 / 已删除的规格不会出现，因此无法被新选用；
    /// 历史规格仍可通过 <see cref="List"/> 读取。
    /// </summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(long productId)
    {
        var variants = await ProductVariantService.LoadSelectableAsync(_db, productId);
        return Ok(ApiResponse<List<ProductVariantDto>>.Success(variants));
    }

    /// <summary>新增规格（编码同商品内唯一；颜色与尺码至少填一个；启用组合不可重复）</summary>
    [HttpPost]
    public async Task<IActionResult> Create(long productId, [FromBody] ProductVariantSaveDto dto)
    {
        var created = await ProductVariantService.CreateAsync(_db, productId, dto);
        return Ok(ApiResponse<ProductVariantDto>.Success(created, "规格新增成功"));
    }

    /// <summary>修改规格（编码 / 颜色 / 尺码 / 备注 / 状态；唯一性重新判定）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long productId, long id, [FromBody] ProductVariantSaveDto dto)
    {
        var updated = await ProductVariantService.UpdateAsync(_db, productId, id, dto);
        return Ok(ApiResponse<ProductVariantDto>.Success(updated, "规格更新成功"));
    }

    /// <summary>停用规格（历史仍可读，但不能再被新选用）</summary>
    [HttpPost("{id:long}/disable")]
    public async Task<IActionResult> Disable(long productId, long id)
    {
        var disabled = await ProductVariantService.DisableAsync(_db, productId, id);
        return Ok(ApiResponse<ProductVariantDto>.Success(disabled, "规格已停用"));
    }

    /// <summary>重新启用规格（需仍满足「启用中颜色 + 尺码组合唯一」，否则拒绝）</summary>
    [HttpPost("{id:long}/enable")]
    public async Task<IActionResult> Enable(long productId, long id)
    {
        var enabled = await ProductVariantService.EnableAsync(_db, productId, id);
        return Ok(ApiResponse<ProductVariantDto>.Success(enabled, "规格已启用"));
    }

    /// <summary>删除规格（本表软删除，保留行以便历史引用可读；不改动任何单据与库存）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long productId, long id)
    {
        await ProductVariantService.DeleteAsync(_db, productId, id);
        return Ok(ApiResponse<object>.Success(null, "规格已删除"));
    }
}
