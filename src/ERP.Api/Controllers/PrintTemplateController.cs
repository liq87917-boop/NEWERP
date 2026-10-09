using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 打印模板控制器（打印设计）：按单据类型维护打印抬头、纸张、字号、打印字段顺序等配置。
/// <para>ERP-450：清单 / 默认模板 / 保存 / 删除 / Excel 导出 / Excel 导入在读取任何模板行或写入任何模板行
/// <b>之前</b>都先复核实时启用身份 + 既有「样式设计」（<c>print-design</c>）功能菜单
/// （见 <see cref="PrintTemplateAuthorizationRules"/>）；保存另校验既有持久化列边界与受支持字号
/// （不再静默改写 FontSize）。缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以既有受控非披露错误 fail closed，
/// 不新增任何授权，也不把空身份当作管理员。</para>
/// <para><b>进程内直调边界</b>（与 <c>EmployeeController</c> / <c>SalesOrderChangeRequestController</c> 同源）：
/// 真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行授权，真实匿名请求因处于请求管线内一律 fail closed；
/// 仅「既无任何登录身份、又不在 HTTP 请求管线内」的进程内直接调用（历史单元测试 / 内部派生读取）沿用既有语义，
/// 这类调用不可能由外部请求到达，绝不把缺失身份当作管理员。</para>
/// </summary>
[ApiController]
[Route("api/sys/print-templates")]
[Authorize]
public class PrintTemplateController : ControllerBase
{
    private readonly IErpDbContext _db;

    /// <summary>可打印对象的中文名称（业务单据 + 基础资料）</summary>
    private static readonly Dictionary<string, string> PrintableTitles = new(BillProcController.BillTitles)
    {
        ["customer"] = "客户资料", ["supplier"] = "供应商资料", ["employee"] = "员工资料",
        ["expense-account"] = "费用科目", ["warehouse"] = "仓库资料",
        ["product"] = "商品资料", ["other-info"] = "其他资料",
        // 阶段 3：EF 主子表单据（报价单 / 形式发票 PI）同样支持打印设计与打印预览
        ["quotation"] = "报价单", ["proforma-invoice"] = "形式发票 PI",
        // ERP-030：单证中心（doc-center）接入共享打印（打印预览 / 直接打印 / 打印设计）；
        // 该模块只在 MODULES 中登记一次（**不加入 BILL_CONFIG**），打印设计清单里不会与业务单据重复出现
        ["doc-center"] = "单证中心",
    };

    public PrintTemplateController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// 是否需要执行实时授权（与仓库 / 供应商 / 员工既有口径同源）：真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）
    /// 一律执行；仅「既无任何登录身份、又不在 HTTP 请求管线内」的<b>进程内直接调用</b>（历史单元测试 / 内部派生读取）
    /// 沿用既有语义 —— 这类调用不可能由外部请求到达，真实匿名请求因处于请求管线内一律 fail closed，
    /// 绝不把缺失身份当作管理员。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>打印模板入口授权（实时身份 + 账号状态 + 既有 print-design 菜单）；进程内无身份直调保持既有免授权语义。</summary>
    private async Task EnsureAuthorizedAsync()
    {
        if (RequiresLiveAuthorization())
            await PrintTemplateAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>查询打印模板列表（可按单据类型筛选）</summary>
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] string? billType)
    {
        await EnsureAuthorizedAsync();
        var source = _db.SysPrintTemplates.AsNoTracking().Where(t => !t.IsDeleted);
        if (!string.IsNullOrWhiteSpace(billType))
            source = source.Where(t => t.BillType == billType);

        var items = await source.OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id).ToListAsync();
        return Ok(ApiResponse<List<SysPrintTemplate>>.Success(items));
    }

    /// <summary>获取指定单据类型的打印模板（优先默认模板，无模板时返回内置默认配置）</summary>
    [HttpGet("{billType}")]
    public async Task<IActionResult> GetDefault(string billType)
    {
        await EnsureAuthorizedAsync();
        var template = await _db.SysPrintTemplates.AsNoTracking()
            .Where(t => !t.IsDeleted && t.BillType == billType)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id)
            .FirstOrDefaultAsync();

        return Ok(ApiResponse<SysPrintTemplate>.Success(template ?? await BuildDefaultAsync(billType)));
    }

    /// <summary>保存打印模板（Id 为 0 时新增，否则更新）；设为默认时同类型其他模板取消默认</summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SysPrintTemplate model)
    {
        await EnsureAuthorizedAsync();
        if (model is null)
            throw BusinessException.InvalidParameter("请求内容不能为空");
        if (string.IsNullOrWhiteSpace(model.BillType))
            return Ok(ApiResponse<object>.Fail("单据类型不能为空", ErrorCodes.InvalidParameter));
        if (string.IsNullOrWhiteSpace(model.TemplateName))
            return Ok(ApiResponse<object>.Fail("模板名称不能为空", ErrorCodes.InvalidParameter));

        PrintTemplateAuthorizationRules.NormalizeForWrite(model);
        // ERP-450：持久化列有界校验 —— 非法单据类型 / 模板名称 / 纸张 / 字号 / 颜色 / 字段顺序一律以既有受控错误拒绝，
        // 不再静默改写 FontSize；被拒绝时不加载、不改写、不写入任何模板行。
        PrintTemplateAuthorizationRules.ValidateForSave(model, PrintableTitles.Keys.ToArray());

        SysPrintTemplate entity;
        if (model.Id > 0)
        {
            entity = await _db.SysPrintTemplates.FirstOrDefaultAsync(t => t.Id == model.Id && !t.IsDeleted)
                ?? throw BusinessException.NotFound("打印模板不存在");
        }
        else
        {
            var exists = await _db.SysPrintTemplates
                .AnyAsync(t => !t.IsDeleted && t.BillType == model.BillType && t.TemplateName == model.TemplateName);
            if (exists)
                return Ok(ApiResponse<object>.Fail("同一单据类型下已存在同名模板", ErrorCodes.Duplicate));

            // 同名模板曾被软删除时复用之：唯一索引 (BillType, TemplateName) 不含 IsDeleted 过滤，
            // 直接新增会触发重复键异常（500），复用可让「删除后可重新创建同名模板」正常工作
            var reuse = await _db.SysPrintTemplates
                .FirstOrDefaultAsync(t => t.IsDeleted && t.BillType == model.BillType && t.TemplateName == model.TemplateName);
            if (reuse != null)
            {
                entity = reuse;
                entity.IsDeleted = false;
            }
            else
            {
                entity = new SysPrintTemplate { CreatedAt = DateTime.Now };
                _db.SysPrintTemplates.Add(entity);
            }
        }

        entity.BillType = model.BillType;
        entity.TemplateName = model.TemplateName;
        entity.Title = model.Title ?? string.Empty;
        entity.CompanyName = model.CompanyName ?? string.Empty;
        entity.CompanyAddress = model.CompanyAddress ?? string.Empty;
        entity.CompanyPhone = model.CompanyPhone ?? string.Empty;
        entity.ShowCompanyHeader = model.ShowCompanyHeader;
        entity.ShowDetailTable = model.ShowDetailTable;
        entity.ShowRemark = model.ShowRemark;
        entity.PaperSize = model.PaperSize;
        entity.FontSize = model.FontSize;
        entity.FieldKeys = model.FieldKeys ?? string.Empty;
        entity.FooterText = model.FooterText ?? string.Empty;
        entity.IsDefault = model.IsDefault;

        // 外观样式（字体 / 字号 / 颜色 / 单元格尺寸）：字号 / 颜色已在 ValidateForSave 校验，
        // 此处不再静默截断或回落非法字号（仅对空白的颜色回落默认色）
        entity.FontFamily = model.FontFamily;
        entity.TitleFontSize = model.TitleFontSize;
        entity.TitleColor = NormalizeColor(model.TitleColor, "#000000");
        entity.TitleAlign = model.TitleAlign;
        entity.CompanyFontSize = model.CompanyFontSize;
        entity.CompanyColor = NormalizeColor(model.CompanyColor, "#000000");
        entity.TextColor = NormalizeColor(model.TextColor, "#000000");
        entity.HeaderBgColor = NormalizeColor(model.HeaderBgColor, "#f2f2f2");
        entity.BorderColor = NormalizeColor(model.BorderColor, "#999999");
        entity.BorderStyle = model.BorderStyle;
        entity.RowHeight = Math.Clamp(model.RowHeight, 0, 120);
        entity.CellPadding = Math.Clamp(model.CellPadding < 0 ? 6 : model.CellPadding, 0, 24);
        entity.LayoutJson = string.IsNullOrWhiteSpace(model.LayoutJson) ? null : model.LayoutJson;
        entity.UpdatedAt = DateTime.Now;

        await _db.SaveChangesAsync();

        // 设为默认：同类型其他模板取消默认标记
        if (entity.IsDefault)
        {
            var others = await _db.SysPrintTemplates
                .Where(t => !t.IsDeleted && t.BillType == entity.BillType && t.Id != entity.Id && t.IsDefault)
                .ToListAsync();
            if (others.Count > 0)
            {
                foreach (var other in others) other.IsDefault = false;
                await _db.SaveChangesAsync();
            }
        }

        return Ok(ApiResponse<SysPrintTemplate>.Success(entity, "保存成功"));
    }

    /// <summary>删除打印模板（软删除）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await EnsureAuthorizedAsync();
        var entity = await _db.SysPrintTemplates.FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted)
            ?? throw BusinessException.NotFound("打印模板不存在");
        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>按网格模板导出 Excel（.xlsx：文字 / 字体字号 / 颜色 / 边框 / 对齐 / 合并 / 列宽行高）</summary>
    [HttpGet("{id:long}/export-excel")]
    public async Task<IActionResult> ExportExcel(long id)
    {
        await EnsureAuthorizedAsync();
        var tpl = await _db.SysPrintTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted)
            ?? throw BusinessException.NotFound("模板不存在");

        var bytes = Services.PrintGridExcel.Build(tpl.LayoutJson ?? string.Empty, tpl.TemplateName);
        var fileName = System.Net.WebUtility.UrlEncode((tpl.TemplateName ?? "打印模板") + ".xlsx");
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>由 Excel 解析为网格布局（不落库；前端可选择新建模板或覆盖当前模板布局）</summary>
    [HttpPost("import-excel")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> ImportExcel(IFormFile? file)
    {
        await EnsureAuthorizedAsync();
        if (file == null || file.Length == 0)
            return Ok(ApiResponse<object>.Fail("请选择要导入的 Excel 文件", ErrorCodes.InvalidParameter));
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return Ok(ApiResponse<object>.Fail("仅支持 .xlsx 格式（Excel 2007 及以上）", ErrorCodes.InvalidParameter));

        try
        {
            using var stream = file.OpenReadStream();
            var layoutJson = Services.PrintGridExcel.Parse(stream);
            var layout = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(layoutJson);
            return Ok(ApiResponse<object>.Success(new { layout }));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "解析 Excel 模板失败：{File}", file.FileName);
            return Ok(ApiResponse<object>.Fail("Excel 解析失败：" + ex.Message, ErrorCodes.RuleConflict));
        }
    }

    /// <summary>颜色值规范化：仅接受 #RGB / #RRGGBB，非法值回落默认色</summary>
    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var v = value.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(v, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
            ? v : fallback;
    }

    /// <summary>构建内置默认模板（公司名称取自系统参数 CompanyName）</summary>
    private async Task<SysPrintTemplate> BuildDefaultAsync(string billType)
    {
        var companyName = await _db.SysParameters.AsNoTracking()
            .Where(p => !p.IsDeleted && p.ParamKey == "CompanyName")
            .Select(p => p.ParamValue).FirstOrDefaultAsync() ?? string.Empty;

        return new SysPrintTemplate
        {
            BillType = billType,
            TemplateName = "默认模板",
            Title = PrintableTitles.TryGetValue(billType, out var title) ? title : billType,
            CompanyName = companyName,
            ShowCompanyHeader = true,
            ShowDetailTable = true,
            ShowRemark = true,
            PaperSize = "A4",
            FontSize = 12,
            FieldKeys = string.Empty,
            IsDefault = true
        };
    }
}
