using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 商品图片库控制器（ERP-039，<b>只读</b>）：在既有商品资料的三个图片位（<c>Image1</c> / <c>Image2</c> / <c>Image3</c>）之上
/// 提供可分页、可筛选的浏览视图，并逐位标注引用是否可安全渲染。
/// <para>商品身份、商品字段与既有商品接口完全不变；本控制器<b>只有 GET 端点</b>，没有任何写入路径。</para>
/// <para>边界：不上传 / 覆盖 / 删除 OSS 对象、不引入或读取任何存储凭据、不请求任何图片地址（不做服务端抓取）、
/// 不改写商品图片字段，也不推断对象归属或访问授权；因此控制器只依赖 <see cref="IErpDbContext"/>，
/// 不注入任何存储 / OSS 服务，也没有任何 DDL 或生产库动作。</para>
/// </summary>
[ApiController]
[Route("api/base/product-images")]
[Authorize]
public class ProductImageLibraryController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ProductImageLibraryController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 商品图片库分页查询（只读）：返回本页商品的三张图片位、引用可用性标注与本页计数。
    /// <para>筛选参数：<c>keyword</c>（商品编码 / 名称）、<c>productId</c>、<c>status</c>（1 启用 / 0 停用）、
    /// <c>imageState</c>（all / has / full / partial / none）、<c>page</c>、<c>pageSize</c>（上限 200，超出按上限截断）。</para>
    /// <para>不可渲染的引用（不安全协议 / 可疑标记 / 无法安全渲染 / 超长）只以文本形式返回，
    /// 由界面显示占位文案，绝不会被当作图片地址或可执行内容。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] ProductImageLibraryQuery query, CancellationToken cancellationToken)
    {
        var result = await ProductImageLibraryService.QueryAsync(_db, query, cancellationToken);
        return Ok(ApiResponse<ProductImageLibraryPage>.Success(result));
    }
}
