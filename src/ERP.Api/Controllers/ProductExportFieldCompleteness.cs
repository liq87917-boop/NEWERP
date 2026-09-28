using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 只读出口字段完整度工作台控制器（ERP-107）：把启用商品资料的既有出口 / 装箱字段
/// （英文报关品名、装箱单位与每箱数量、外箱尺寸 / 毛重、出口退税率）逐商品只读呈现，
/// 只报告字段「已填写 / 空白 / 为 0 / 无效值」与缺口，不做报关合规、退税资格或税率结论。
/// <para>审计口径：本控制器<strong>不新建任何表、不新增任何列、不执行任何写操作</strong>，
/// 只按显式字段读取（全库查询均为 <c>AsNoTracking</c>，无 Add / Update / Remove / SaveChanges），
/// 不读取图片、不改写商品 / 单证 / 报关单，也不调用任何外部服务。</para>
/// </summary>
[ApiController]
[Route("api/base/products/export-field-completeness")]
[Authorize]
public class ProductExportFieldCompletenessController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ProductExportFieldCompletenessController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 只读出口字段完整度工作台（分页）：按启用商品（排除软删除 / 停用）逐商品呈现，
    /// 可选按编码 / 名称关键字与完整度分组筛选，按稳定商品 Id 分页；无逐行查库、无外部查询。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetWorksheet([FromQuery] ProductExportFieldCompletenessQuery query)
    {
        query.Normalize();
        var group = ProductExportFieldCompletenessRules.NormalizeGroup(query.Group);

        var source = _db.BaseProducts.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == ProductExportFieldCompletenessRules.ActiveStatus);

        if (query.Keyword is { } keyword)
        {
            source = source.Where(p => p.ProductCode.Contains(keyword) || p.ProductName.Contains(keyword));
        }

        var groupPredicate = ProductExportFieldCompletenessRules.BuildGroupPredicate(group);
        if (groupPredicate is not null)
            source = source.Where(groupPredicate);

        var total = await source.CountAsync();
        var pageProducts = await source
            .OrderBy(p => p.Id)   // 稳定分页
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        var rows = pageProducts.Select(ProductExportFieldCompletenessRules.BuildRow).ToList();

        var worksheet = new ProductExportFieldCompletenessDto(
            rows,
            total,
            query.Page,
            query.PageSize,
            ProductExportFieldCompletenessRules.ReadOnlyText,
            ProductExportFieldCompletenessRules.BoundaryText,
            ProductExportFieldCompletenessRules.DisclaimerText);

        return Ok(ApiResponse<ProductExportFieldCompletenessDto>.Success(worksheet));
    }
}
