using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 代理服务费对账与账龄工作台控制器（ERP-072，**只读派生**）：在 ERP-070 的持久化对账单证据与
/// ERP-071 的持久化「客户收款 → 代理服务费对账单」分摊行之上派生①对账单合计证据、
/// ②**有效**收款分摊证据、③算术剩余证据，并按**显式到期日**与**显式 as-of 日期**计算互斥账龄桶
/// （未登记显式到期日的对账单进入独立的「未知到期日」分组）。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>总账或应收账款余额、
/// <strong>不是</strong>经审计的客户对账单或法律意义上的对账确认、<strong>不是</strong>付款通知或催款函、
/// <strong>不是</strong>收款授权与收款执行、<strong>不是</strong>收入确认、<strong>不是</strong>税务（销项）判断、
/// <strong>不是</strong>结算确认或核销结果；所有接口<strong>只读</strong>，<strong>不新增 / 不修改任何表与列</strong>、
/// 不做任何回填，也不开票、不记账、不核销、不收款或付款、不催收或联系客户、不调用任何外部服务，
/// 更不改写对账单证据、ERP-071 分摊行、收款单、ERP-069 协议证据、客户主数据、销售订单、装柜清单、
/// 单证、发票、库存与库存成本、费用与退税、结算与余额记录。</para>
/// </summary>
[ApiController]
[Route("api/agency-service-fee-reconciliation")]
[Authorize]
public class AgencyServiceFeeReconciliationController : ControllerBase
{
    private readonly IErpDbContext _db;

    public AgencyServiceFeeReconciliationController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 工作台报表（分页，只读派生）：可按客户 / 协议 / 对账单身份 / 服务来源 / 对账日期区间 /
    /// 显式到期日区间 / 币种 / 对账单状态 / 分配状态过滤；汇总、账龄桶与币种汇总只统计本次返回页，
    /// 不同币种分别成行、**没有任何跨币种总额**。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Report([FromQuery] AgencyServiceFeeReconciliationQuery query)
        => Ok(ApiResponse<AgencyServiceFeeReconciliationReport>.Success(
            await AgencyServiceFeeReconciliationService.ForQueryAsync(_db, query)));

    /// <summary>
    /// 模块元数据（只读）：账龄桶 / 分配状态 / 对账单状态 / 服务来源白名单、有界额度与口径文案
    /// （派生 / 账龄 / 币种隔离 / 范围 / 模块边界 / 未知到期日 / 无效证据 / fail closed / 导出 /
    /// 证据维度分离 / 历史只读 / 只读边界），供界面与接口同源显示。
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<AgencyServiceFeeReconciliationMetadataDto>.Success(
            AgencyServiceFeeReconciliationService.GetMetadata()));

    /// <summary>
    /// 单张对账单的对账证据明细（只读派生，与列表同一派生口径）：打开时**重新校验**当前登录身份与
    /// 既有「角色 → 菜单」模块授权，并重新读取持久化对账单、ERP-071 分摊行与收款单证据；
    /// 未认证 / 未获该模块授权 / 对账单不存在或已删除时一律**拒绝**（fail closed），
    /// 不返回任何部分证据，也不做来源修复或改派。
    /// </summary>
    [HttpGet("statements/{statementId:long}/detail")]
    public async Task<IActionResult> StatementDetail(
        long statementId, [FromQuery] DateTime? asOfDate = null)
        => Ok(ApiResponse<AgencyServiceFeeReconciliationStatementDetail>.Success(
            await AgencyServiceFeeReconciliationService.ForStatementDetailAsync(
                _db, statementId, CurrentUserId(), asOfDate)));

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由明细接口 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : null;
}
