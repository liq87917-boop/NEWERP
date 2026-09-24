using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户资料控制器（ERP-036：新增可选的「指定货代」主数据指引字段）
/// </summary>
/// <remarks>
/// 复用既有的「其他资料」数据字典（<c>InfoType = Forwarder</c>），不新增任何单据关联：
/// 指定货代只写客户资料自身的引用 Id + 名称快照两列，不会自动写入订舱 / 装柜 / 报关 / 费用单据，
/// 也不涉及任何外部货代系统。
/// </remarks>
[ApiController]
[Route("api/base/customers")]
[Authorize]
public class CustomerController : BaseCrudController<BaseCustomer>
{
    private readonly IErpDbContext _db;

    public CustomerController(IGenericService<BaseCustomer> service, IErpDbContext db) : base(service)
    {
        _db = db;
    }

    /// <summary>指定货代下拉选项（只返回未删除、已启用、类型为 Forwarder 的字典项）</summary>
    [HttpGet("forwarder-options")]
    public async Task<IActionResult> GetForwarderOptions()
    {
        var options = await CustomerForwarderService.LoadOptionsAsync(_db);
        return Ok(ApiResponse<List<OtherInfoOptionDto>>.Success(options));
    }

    /// <summary>分页查询（补充指定货代引用的可用性标注，不写库）</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var result = await Service.GetPagedAsync(query);
        await CustomerForwarderService.AnnotateAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<BaseCustomer>>.Success(result));
    }

    /// <summary>根据主键获取（补充指定货代引用的可用性标注，不写库）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var result = await Service.GetByIdAsync(id);
        await CustomerForwarderService.AnnotateAsync(_db, new[] { result });
        return Ok(ApiResponse<BaseCustomer>.Success(result));
    }

    /// <summary>新增客户（指定货代必须是可用的 Forwarder 字典项，名称快照由服务端写入）</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseCustomer entity)
    {
        await CustomerForwarderService.ApplyAsync(_db, entity, stored: null);
        return await base.Create(entity);
    }

    /// <summary>
    /// 更新客户（指定货代必须是可用的 Forwarder 字典项；引用未变更时保留历史引用，不因字典项停用而清空）
    /// </summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseCustomer entity)
    {
        entity.Id = id;
        var stored = await _db.BaseCustomers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        // 客户不存在时不先校验货代引用，交由服务层统一报「数据不存在」，避免错误信息错位
        if (stored is not null)
            await CustomerForwarderService.ApplyAsync(_db, entity, stored);
        return await base.Update(id, entity);
    }
}

/// <summary>
/// 供应商资料控制器
/// </summary>
[ApiController]
[Route("api/base/suppliers")]
[Authorize]
public class SupplierController : BaseCrudController<BaseSupplier>
{
    public SupplierController(IGenericService<BaseSupplier> service) : base(service) { }
}

/// <summary>
/// 员工资料控制器
/// </summary>
[ApiController]
[Route("api/base/employees")]
[Authorize]
public class EmployeeController : BaseCrudController<BaseEmployee>
{
    public EmployeeController(IGenericService<BaseEmployee> service) : base(service) { }

    /// <summary>获取业务员列表（供下拉选择）</summary>
    [HttpGet("salesmen")]
    public async Task<IActionResult> GetSalesmen()
    {
        var items = await Service.GetAllAsync(e => e.IsSalesman && e.Status == 1);
        return Ok(ApiResponse<List<BaseEmployee>>.Success(items));
    }
}

/// <summary>
/// 费用科目控制器
/// </summary>
[ApiController]
[Route("api/base/expense-accounts")]
[Authorize]
public class ExpenseAccountController : BaseCrudController<BaseExpenseAccount>
{
    public ExpenseAccountController(IGenericService<BaseExpenseAccount> service) : base(service) { }
}

/// <summary>
/// 仓库资料控制器
/// </summary>
[ApiController]
[Route("api/base/warehouses")]
[Authorize]
public class WarehouseController : BaseCrudController<BaseWarehouse>
{
    public WarehouseController(IGenericService<BaseWarehouse> service) : base(service) { }
}

/// <summary>
/// 商品资料控制器（ERP-037：商品下可选的颜色 / 尺码 SKU 规格变体，规格维护见 <see cref="ProductVariantController"/>）
/// </summary>
[ApiController]
[Route("api/base/products")]
[Authorize]
public class ProductController : BaseCrudController<BaseProduct>
{
    private readonly OssStorageService _oss;
    private readonly ProductExcelExporter _exporter;
    private readonly IWebHostEnvironment _env;
    private readonly IErpDbContext _db;

    public ProductController(
        IGenericService<BaseProduct> service, OssStorageService oss, ProductExcelExporter exporter,
        IWebHostEnvironment env, IErpDbContext db) : base(service)
    {
        _oss = oss;
        _exporter = exporter;
        _env = env;
        _db = db;
    }

    /// <summary>
    /// 分页查询（补充规格计数标注，不写库）。
    /// 商品身份与字段完全不变；<c>variantCount</c> / <c>variantTotalCount</c> 只是读取标注，
    /// 没有维护规格的历史商品两项均为 0（仍按单规格商品使用）。
    /// </summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var result = await Service.GetPagedAsync(query);
        await ProductVariantService.AnnotateAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<BaseProduct>>.Success(result));
    }

    /// <summary>根据主键获取（补充规格计数标注，不写库）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var result = await Service.GetByIdAsync(id);
        await ProductVariantService.AnnotateAsync(_db, new[] { result });
        return Ok(ApiResponse<BaseProduct>.Success(result));
    }

    /// <summary>导出商品资料为 Excel（读取模板填充：图片/文本/数值/货币/公式/求和）</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var templatePath = Path.Combine(_env.ContentRootPath, "templates", "导出_商品信息.xlsx");
        var bytes = await _exporter.ExportAsync(templatePath);
        var fileName = $"商品信息_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>单张图片上传到阿里云 OSS</summary>
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        var url = await SaveImageAsync(file);
        return Ok(ApiResponse<object>.Success(new { url }, "上传成功"));
    }

    /// <summary>批量图片上传到阿里云 OSS</summary>
    [HttpPost("upload-batch")]
    public async Task<IActionResult> UploadBatch(List<IFormFile> files)
    {
        if (files is null || files.Count == 0)
            throw BusinessException.InvalidParameter("请选择要上传的图片");

        var urls = new List<string>();
        foreach (var file in files)
            urls.Add(await SaveImageAsync(file));

        return Ok(ApiResponse<object>.Success(new { urls }, "批量上传成功"));
    }

    /// <summary>校验并上传单张图片，返回 OSS 原图地址</summary>
    private async Task<string> SaveImageAsync(IFormFile file)
    {
        if (file is null || file.Length == 0)
            throw BusinessException.InvalidParameter("上传文件为空");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
        if (!allowed.Contains(ext))
            throw BusinessException.InvalidParameter("仅支持图片格式（jpg/png/gif/webp/bmp）");
        if (file.Length > 10 * 1024 * 1024)
            throw BusinessException.InvalidParameter("图片大小不能超过 10MB");

        // OSS 存放目录：oss/NEWERP/日期/GUID.扩展名，避免中文/特殊字符导致签名与访问异常
        var objectKey = $"oss/NEWERP/{DateTime.Now:yyyyMMdd}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        return await _oss.UploadAsync(stream, objectKey, file.ContentType ?? "application/octet-stream");
    }
}
