using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections;
using System.Globalization;
using System.Reflection;

namespace ERP.Api.Controllers;

/// <summary>
/// 基础资料导入导出控制器：为全部基础资料提供统一的 Excel 导出 / 导入能力
/// 路由：api/base/io/{resource}/export 与 api/base/io/{resource}/import
/// </summary>
[ApiController]
[Route("api/base/io")]
[Authorize]
public class BaseDataIoController : ControllerBase
{
    private readonly IErpDbContext _db;

    /// <summary>单次导入的最大行数</summary>
    private const int MaxImportRows = 2000;

    /// <summary>列定义（Key 为实体属性名，Title 为导出/导入使用的中文表头）</summary>
    private sealed record ColumnDef(string Key, string Title);

    /// <summary>基础资料资源定义</summary>
    private sealed record ResourceDef(string Key, string Title, Type EntityType, string DbSetProperty, List<ColumnDef> Columns);

    /// <summary>支持导入导出的基础资料资源</summary>
    private static readonly Dictionary<string, ResourceDef> Resources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customers"] = new("customers", "客户资料", typeof(BaseCustomer), nameof(IErpDbContext.BaseCustomers), new List<ColumnDef>
        {
            new("CustomerCode", "客户编码"), new("CustomerName", "客户名称"), new("EnglishName", "英文名称"),
            new("ContactPerson", "联系人"), new("Phone", "电话"), new("Email", "邮箱"),
            new("Country", "国家"), new("Address", "地址"), new("PaymentTerms", "付款条件"),
            new("CreditLimit", "信用额度"), new("DepositRatio", "定金比例%"), new("TaxNumber", "税号"),
            new("BusinessNature", "业务性质"), new("Currency", "默认币种"), new("SettlementMethod", "结算方式"),
            new("TradeTerms", "贸易条款"), new("DestinationPort", "目的港"), new("Consignee", "收货人"),
            new("NotifyParty", "通知人"), new("DefaultShippingMark", "默认唛头"), new("CreditDays", "账期天数"),
            new("CommissionRatio", "佣金比例%"), new("CustomerLevel", "客户等级"), new("CreditStatus", "信用状态"),
            new("Source", "客户来源"),
            new("EmpId", "业务员Id"), new("Remark", "备注")
        }),
        ["suppliers"] = new("suppliers", "供应商资料", typeof(BaseSupplier), nameof(IErpDbContext.BaseSuppliers), new List<ColumnDef>
        {
            new("SupplierCode", "供应商编码"), new("SupplierName", "供应商名称"), new("EnglishName", "英文名称"),
            new("ContactPerson", "联系人"), new("Phone", "电话"), new("Email", "邮箱"),
            new("Country", "国家"), new("Address", "地址"), new("PaymentTerms", "付款条件"),
            new("SupplierType", "供应商类型"), new("BoothLocation", "档口位置"), new("MainCategory", "主营品类"),
            new("SettlementMethod", "结算方式"), new("InvoiceAbility", "开票能力"), new("TaxRate", "税率%"),
            new("DeliveryDays", "交期天数"), new("RebateRatio", "返点比例%"), new("WeChat", "微信/WhatsApp"),
            new("BankName", "开户银行"), new("BankAccount", "银行账号"), new("Remark", "备注")
        }),
        ["employees"] = new("employees", "员工资料", typeof(BaseEmployee), nameof(IErpDbContext.BaseEmployees), new List<ColumnDef>
        {
            new("EmployeeCode", "员工编码"), new("EmployeeName", "姓名"), new("Department", "部门"),
            new("Position", "职位"), new("Phone", "电话"), new("Email", "邮箱"),
            new("HireDate", "入职日期"), new("IsSalesman", "是否业务员")
        }),
        ["expense-accounts"] = new("expense-accounts", "费用科目", typeof(BaseExpenseAccount), nameof(IErpDbContext.BaseExpenseAccounts), new List<ColumnDef>
        {
            new("AccountCode", "科目编码"), new("AccountName", "科目名称"), new("AccountType", "类型(1收入/2支出)"),
            new("Description", "说明"), new("ParentId", "上级科目Id")
        }),
        ["warehouses"] = new("warehouses", "仓库资料", typeof(BaseWarehouse), nameof(IErpDbContext.BaseWarehouses), new List<ColumnDef>
        {
            new("WarehouseCode", "仓库编码"), new("WarehouseName", "仓库名称"), new("Address", "地址"),
            new("Manager", "负责人"), new("Phone", "电话"), new("Remark", "备注")
        }),
        ["products"] = new("products", "商品资料", typeof(BaseProduct), nameof(IErpDbContext.BaseProducts), new List<ColumnDef>
        {
            new("ProductCode", "商品编码"), new("ProductName", "商品名称"), new("EnglishName", "英文名称"),
            new("Spec", "规格"), new("Unit", "单位"), new("Category", "分类"),
            new("HsCode", "HS编码"), new("Barcode", "条形码"), new("PurchasePrice", "采购价"),
            new("SalePrice", "销售价"), new("CostPrice", "成本价"), new("Weight", "毛重(kg)"),
            new("Volume", "体积(m³)"), new("Length", "长(cm)"), new("Width", "宽(cm)"),
            new("Height", "高(cm)"), new("Image1", "图片1"), new("Image2", "图片2"),
            new("Image3", "图片3"),
            new("PackageUnit", "装箱单位"), new("UnitsPerPackage", "每箱数量"), new("UnitConversion", "单位换算"),
            new("OuterLength", "外箱长cm"), new("OuterWidth", "外箱宽cm"), new("OuterHeight", "外箱高cm"),
            new("OuterWeight", "外箱毛重kg"), new("VolumeWeight", "体积重kg"), new("EnglishDeclareName", "英文报关品名"),
            new("RefundRate", "退税率%"), new("Brand", "品牌"), new("Certification", "认证"),
            new("CustomerItemNo", "客户货号"), new("FactoryItemNo", "工厂货号"), new("MinOrderQty", "起订量"),
            new("TaxIncluded", "是否含税"), new("Remark", "备注")
        }),
        ["tax-refunds"] = new("tax-refunds", "出口退税台账", typeof(BaseTaxRefund), nameof(IErpDbContext.BaseTaxRefunds), new List<ColumnDef>
        {
            new("RefundNo", "台账编号"), new("RefundPeriod", "退税期间"), new("DeclareDate", "申报日期"),
            new("DeclareNo", "报关单号"), new("InvoiceNo", "出口发票号"), new("SalesOrderNo", "销售订单号"),
            new("CustomerName", "客户名称"), new("ExportAmount", "出口金额"), new("Currency", "币种"),
            new("ExchangeRate", "汇率"), new("RefundRate", "退税率%"), new("RefundableAmount", "可退税额"),
            new("RefundedAmount", "已退税额"), new("RefundDate", "退税到账日"), new("Status", "状态"),
            new("Remark", "备注")
        }),
        ["other-infos"] = new("other-infos", "其他资料", typeof(BaseOtherInfo), nameof(IErpDbContext.BaseOtherInfos), new List<ColumnDef>
        {
            new("InfoType", "资料类型"), new("InfoCode", "编码"), new("InfoName", "名称"),
            new("EnglishName", "英文名称"), new("IsDefault", "是否默认"), new("SortOrder", "排序号"),
            new("Remark", "备注")
        }),
    };

    public BaseDataIoController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>导出基础资料为 Excel 文件（导出全部未删除记录）</summary>
    [HttpGet("{resource}/export")]
    public IActionResult Export(string resource)
    {
        if (!Resources.TryGetValue(resource, out var def))
            return Ok(ApiResponse<object>.Fail("不支持该基础资料的导出", ErrorCodes.InvalidParameter));

        var rows = LoadEntities(def)
            .Select(entity =>
            {
                var row = new Dictionary<string, object?>();
                foreach (var column in def.Columns)
                    row[column.Key] = ReadProperty(entity, column.Key);
                return row;
            })
            .ToList();

        var columns = def.Columns.Select(c => (c.Key, c.Title)).ToList();
        var bytes = ExcelExporter.ExportRows(def.Title, rows, columns);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{def.Title}_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>下载基础资料导入模板（首行中文表头）</summary>
    [HttpGet("{resource}/import-template")]
    public IActionResult DownloadTemplate(string resource)
    {
        if (!Resources.TryGetValue(resource, out var def))
            return Ok(ApiResponse<object>.Fail("不支持该基础资料的导入", ErrorCodes.InvalidParameter));

        var columns = def.Columns.Select(c => (c.Key, c.Title)).ToList();
        var bytes = ExcelImporter.BuildTemplate(def.Title, columns);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{def.Title}_导入模板_{DateTime.Now:yyyyMMdd}.xlsx");
    }

    /// <summary>导入基础资料（Excel 首行中文表头，逐行写入数据库）</summary>
    [HttpPost("{resource}/import")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Import(string resource, IFormFile? file)
    {
        if (!Resources.TryGetValue(resource, out var def))
            return Ok(ApiResponse<object>.Fail("不支持该基础资料的导入", ErrorCodes.InvalidParameter));
        if (file is null || file.Length == 0)
            return Ok(ApiResponse<object>.Fail("请选择要导入的 Excel 文件", ErrorCodes.InvalidParameter));
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Ok(ApiResponse<object>.Fail("仅支持 .xlsx 格式的 Excel 文件", ErrorCodes.InvalidParameter));

        var headerMap = def.Columns.ToDictionary(c => c.Title, c => c.Key, StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, string>> rows;
        try
        {
            using var stream = file.OpenReadStream();
            rows = ExcelImporter.ReadRows(stream, headerMap);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "基础资料导入解析失败：{Resource}", resource);
            return Ok(ApiResponse<object>.Fail("Excel 解析失败，请确认文件由「导入模板」填写，且未被其他程序占用"));
        }

        if (rows.Count == 0)
            return Ok(ApiResponse<object>.Fail("未读取到有效数据行，请核对表头是否与导入模板一致", ErrorCodes.InvalidParameter));
        if (rows.Count > MaxImportRows)
            return Ok(ApiResponse<object>.Fail($"单次导入不得超过 {MaxImportRows} 行，请拆分后分批导入", ErrorCodes.InvalidParameter));

        var dbContext = (DbContext)_db;
        var success = 0;
        var failed = 0;
        var errors = new List<object>();

        for (var i = 0; i < rows.Count; i++)
        {
            try
            {
                var entity = (BaseEntity)Activator.CreateInstance(def.EntityType)!;
                foreach (var kv in rows[i])
                {
                    if (ValueNormalizer.IsSystemField(kv.Key)) continue;
                    WriteProperty(entity, kv.Key, kv.Value);
                }
                dbContext.Add(entity);
                await _db.SaveChangesAsync();
                success++;
            }
            catch (Exception ex)
            {
                failed++;
                dbContext.ChangeTracker.Clear();
                if (errors.Count < 50)
                    errors.Add(new { row = i + 2, message = CleanMessage(ex.Message) });
            }
        }

        var message = $"「{def.Title}」导入完成：成功 {success} 条，失败 {failed} 条";
        Serilog.Log.Information("{Message}", message);
        return Ok(ApiResponse<object>.Success(new { total = rows.Count, success, failed, errors }, message));
    }

    /// <summary>读取资源对应的全部未删除实体（按 Id 倒序）</summary>
    private List<BaseEntity> LoadEntities(ResourceDef def)
    {
        var property = typeof(IErpDbContext).GetProperty(def.DbSetProperty)
            ?? throw BusinessException.NotFound($"未找到基础资料数据源：{def.Key}");
        var enumerable = (IEnumerable?)property.GetValue(_db)
            ?? throw BusinessException.NotFound($"基础资料数据源为空：{def.Key}");
        return enumerable.Cast<object>()
            .OfType<BaseEntity>()
            .Where(e => !e.IsDeleted)
            .OrderByDescending(e => e.Id)
            .ToList();
    }

    /// <summary>读取实体属性值（属性不存在时返回 null）</summary>
    private static object? ReadProperty(BaseEntity entity, string propertyName)
        => entity.GetType().GetProperty(propertyName)?.GetValue(entity);

    /// <summary>按属性类型将文本写入实体（类型转换失败时抛出可读异常）</summary>
    private static void WriteProperty(BaseEntity entity, string propertyName, string? raw)
    {
        var property = entity.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || !property.CanWrite) return;

        // 空值：引用类型与可空类型写入 null，值类型保持默认值
        if (string.IsNullOrWhiteSpace(raw))
        {
            var isNullable = Nullable.GetUnderlyingType(property.PropertyType) is not null;
            if (isNullable || !property.PropertyType.IsValueType)
                property.SetValue(entity, null);
            return;
        }

        var text = raw.Trim();
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? value;
        try
        {
            if (targetType == typeof(string))
                value = text;
            else if (targetType == typeof(bool))
                value = text is "1" or "是" or "Y" or "y" or "true" or "TRUE" or "True";
            else if (targetType == typeof(DateTime))
                value = DateTime.Parse(text, CultureInfo.InvariantCulture);
            else if (targetType == typeof(decimal))
                value = decimal.Parse(CleanNumber(text), NumberStyles.Any, CultureInfo.InvariantCulture);
            else if (targetType == typeof(int))
                value = int.Parse(CleanNumber(text), NumberStyles.Any, CultureInfo.InvariantCulture);
            else if (targetType == typeof(long))
                value = long.Parse(CleanNumber(text), NumberStyles.Any, CultureInfo.InvariantCulture);
            else
                value = Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            throw BusinessException.InvalidParameter($"字段「{propertyName}」的值“{text}”格式不正确");
        }
        property.SetValue(entity, value);
    }

    /// <summary>清除数值文本中的千分位与货币符号</summary>
    private static string CleanNumber(string text) =>
        text.Replace(",", string.Empty).Replace("￥", string.Empty).Replace("$", string.Empty).Trim();

    /// <summary>精简数据库异常信息（去掉 EF 包装，便于前端展示）</summary>
    private static string CleanMessage(string message)
    {
        var index = message.IndexOf("See the inner exception", StringComparison.OrdinalIgnoreCase);
        var cleaned = index > 0 ? message[..index] : message;
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }
}
