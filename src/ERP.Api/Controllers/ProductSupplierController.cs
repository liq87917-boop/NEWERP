using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 商品 / SKU 货源关系控制器（ERP-038）：把既有商品（或它的一个启用规格）与多个既有供应商关联起来。
/// <para>路由挂在既有商品资源下（<c>/api/base/products/{productId}/suppliers</c>），商品身份与商品接口保持不变。</para>
/// <para>边界：所有接口只读写 <c>BaseProductSuppliers</c> 子表 ——
/// <strong>不</strong>自动选择供应商、<strong>不</strong>定价 / 审批价、<strong>不</strong>生成或改写
/// 采购报价与采购订单、<strong>不</strong>改写库存成本与库存流水、<strong>不</strong>触碰任何历史单据；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/base/products/{productId:long}/suppliers")]
[Authorize]
public class ProductSupplierController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ProductSupplierController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 货源关系明细（含停用关系，便于历史可读）：按作用域（商品级在前）→ 排序号 → Id 返回，
    /// 并按上限收敛为**有界**视图；<paramref name="activeOnly"/> 为真时只返回启用中的关系（可选用口径）。
    /// <para>响应中的 <c>scopeText</c> 明确区分「商品级（整品通用）」与「规格级（SKU 专用）」。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(long productId, [FromQuery] bool activeOnly = false)
    {
        var rows = await ProductSupplierService.ListByProductAsync(_db, productId, activeOnly);
        return Ok(ApiResponse<List<ProductSupplierDto>>.Success(rows));
    }

    /// <summary>
    /// 可选用货源关系（只返回启用中的关系）：停用 / 已删除的关系不会出现，因此不会被当作启用货源；
    /// 历史关系仍可通过 <see cref="List"/> 读取。
    /// </summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(long productId)
    {
        var rows = await ProductSupplierService.LoadSelectableAsync(_db, productId);
        return Ok(ApiResponse<List<ProductSupplierDto>>.Success(rows));
    }

    /// <summary>新增货源关系（商品 / 规格 / 供应商引用校验；重复关系与重复首选拒绝）</summary>
    [HttpPost]
    public async Task<IActionResult> Create(long productId, [FromBody] ProductSupplierSaveDto dto)
    {
        var created = await ProductSupplierService.CreateAsync(_db, productId, dto);
        return Ok(ApiResponse<ProductSupplierDto>.Success(created, "货源关系新增成功"));
    }

    /// <summary>修改货源关系（引用变更时重新校验；未变更的历史引用允许继续编辑）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long productId, long id, [FromBody] ProductSupplierSaveDto dto)
    {
        var updated = await ProductSupplierService.UpdateAsync(_db, productId, id, dto);
        return Ok(ApiResponse<ProductSupplierDto>.Success(updated, "货源关系更新成功"));
    }

    /// <summary>停用货源关系（历史仍可读，但不再作为启用货源；同时释放首选标记）</summary>
    [HttpPost("{id:long}/disable")]
    public async Task<IActionResult> Disable(long productId, long id)
    {
        var disabled = await ProductSupplierService.DisableAsync(_db, productId, id);
        return Ok(ApiResponse<ProductSupplierDto>.Success(disabled, "货源关系已停用"));
    }

    /// <summary>重新启用货源关系（需商品 / 规格 / 供应商仍可用，且不会产生第二个启用首选）</summary>
    [HttpPost("{id:long}/enable")]
    public async Task<IActionResult> Enable(long productId, long id)
    {
        var enabled = await ProductSupplierService.EnableAsync(_db, productId, id);
        return Ok(ApiResponse<ProductSupplierDto>.Success(enabled, "货源关系已启用"));
    }

    /// <summary>
    /// 显式设置 / 取消首选（<paramref name="preferred"/> 默认 true）：
    /// 设为该范围首选时会先释放旧的启用首选再置新首选，结果与列表顺序无关。
    /// </summary>
    [HttpPost("{id:long}/preferred")]
    public async Task<IActionResult> SetPreferred(long productId, long id, [FromQuery] bool preferred = true)
    {
        var result = await ProductSupplierService.SetPreferredAsync(_db, productId, id, preferred);
        return Ok(ApiResponse<ProductSupplierDto>.Success(
            result, preferred ? "已设为该范围的首选货源" : "已取消首选"));
    }

    /// <summary>删除货源关系（本表软删除，保留行以便历史引用可读；不改动任何单据与库存）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long productId, long id)
    {
        await ProductSupplierService.DeleteAsync(_db, productId, id);
        return Ok(ApiResponse<object>.Success(null, "货源关系已删除"));
    }
}

/// <summary>
/// 供应商侧货源关系控制器（ERP-038）：供应商资料工作流中的**有界只读**列表，
/// 用于查看某供应商当前为哪些商品 / 规格供货（含停用关系的历史可读），
/// 同样不写任何采购单据、不改写库存与历史数据。
/// </summary>
[ApiController]
[Route("api/base/suppliers/{supplierId:long}/sourcing")]
[Authorize]
public class SupplierSourcingController : ControllerBase
{
    private readonly IErpDbContext _db;

    public SupplierSourcingController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 该供应商的货源关系列表（只读）：默认含停用关系（历史可读）与可用性标注，
    /// 按上限收敛为**有界**列表；<paramref name="activeOnly"/> 为真时只返回启用中的关系。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(long supplierId, [FromQuery] bool activeOnly = false)
    {
        var rows = await ProductSupplierService.ListBySupplierAsync(_db, supplierId, activeOnly);
        return Ok(ApiResponse<List<ProductSupplierDto>>.Success(rows));
    }
}
