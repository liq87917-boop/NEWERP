using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户对账证据导出控制器（ERP-086，**只读派生、有界、分币种**）：按客户导出可下载的 CSV / HTML 对账证据，
/// 复用 ERP-074 客户应收账款对账工作台的同一派生引擎与筛选口径，把四类证据分别标注：
/// ①ERP-055 发票含税总额证据、②ERP-073 有效收款分摊证据、③ERP-075 收款分摊上下文、④ERP-076 发票 → 出货链接证据；
/// 算术剩余证据 = 发票含税总额 − 有效分摊合计。不同币种分别成行、绝无跨币种总额。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>总账或应收账款余额、<strong>不是</strong>经审计的客户对账单、
/// <strong>不是</strong>收入确认、<strong>不是</strong>税务申报、<strong>不是</strong>付款通知或催收函、<strong>不是</strong>结算确认；
/// 所有接口<strong>只读</strong>，<strong>不新增 / 不修改任何表与列</strong>、不回填，不开票、不记账、不核销、不收款或付款、
/// 不催收或联系客户、不调用外部服务，也不改写客户、销售订单、出货、装柜、发票、分摊、收款、余额、税务、财务与结算记录。</para>
/// </summary>
[ApiController]
[Route("api/customer-reconciliation-statement")]
[Authorize]
public class CustomerReconciliationStatementController : ControllerBase
{
    private readonly IErpDbContext _db;

    public CustomerReconciliationStatementController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>模块元数据（只读）：证据类 / 格式白名单、有界额度与边界口径（与接口 / 界面 / 文档同源）</summary>
    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<CustomerReconciliationStatementMetadataDto>.Success(
            CustomerReconciliationStatementService.GetMetadata()));

    /// <summary>
    /// 对账证据（JSON，只读派生）：生成前重新校验登录身份与既有「角色 → 菜单」模块授权，来源客户不存在 / 已删除时 fail closed。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Statement([FromQuery] CustomerReconciliationStatementQuery query)
        => Ok(ApiResponse<CustomerReconciliationStatementDto>.Success(
            await CustomerReconciliationStatementService.ForStatementAsync(_db, query, CurrentUserId())));

    /// <summary>
    /// 下载对账证据导出（CSV / HTML，只读派生、有界）：与 JSON 接口同一派生口径与授权复核；
    /// 文件名附带生成时间，响应以附件方式返回（浏览器不内联渲染导出内容）。
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] CustomerReconciliationStatementQuery query)
    {
        var statement = await CustomerReconciliationStatementService.ForStatementAsync(_db, query, CurrentUserId());
        var content = query.Format == CustomerReconciliationStatementRules.FormatHtml
            ? CustomerReconciliationStatementService.BuildHtml(statement)
            : CustomerReconciliationStatementService.BuildCsv(statement);

        var bytes = Encoding.UTF8.GetBytes(content);
        var extension = query.Format == CustomerReconciliationStatementRules.FormatHtml ? "html" : "csv";
        var contentType = query.Format == CustomerReconciliationStatementRules.FormatHtml
            ? "text/html; charset=utf-8"
            : "text/csv; charset=utf-8";
        var fileName = $"customer-reconciliation-statement_{DateTime.Now:yyyyMMddHHmmss}.{extension}";

        return File(bytes, contentType, fileName);
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由导出 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : null;
}
