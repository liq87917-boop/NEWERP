using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户资料控制器
/// </summary>
[ApiController]
[Route("api/base/customers")]
[Authorize]
public class CustomerController : BaseCrudController<BaseCustomer>
{
    public CustomerController(IGenericService<BaseCustomer> service) : base(service) { }
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
/// 商品资料控制器
/// </summary>
[ApiController]
[Route("api/base/products")]
[Authorize]
public class ProductController : BaseCrudController<BaseProduct>
{
    private readonly OssStorageService _oss;
    private readonly ProductExcelExporter _exporter;
    private readonly IWebHostEnvironment _env;

    public ProductController(IGenericService<BaseProduct> service, OssStorageService oss, ProductExcelExporter exporter, IWebHostEnvironment env) : base(service)
    {
        _oss = oss;
        _exporter = exporter;
        _env = env;
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
