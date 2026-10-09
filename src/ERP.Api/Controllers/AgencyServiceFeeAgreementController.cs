using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 代理服务费协议证据登记控制器（ERP-069）：登记客户代理服务费的**仓库内商业条款证据**
/// （协议号 / 权威客户 / 生效日期区间 / 币种 / 披露的计费方式与显式费率或固定金额 / 有界计费依据说明 / 有界备注），
/// 并可显式登记与作废。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>发票系统（不开发票、不连税务平台）、
/// <strong>不是</strong>会计 / 记账或凭证系统、<strong>不是</strong>付款授权或资金指令、<strong>不是</strong>法律意见，
/// 也<strong>不</strong>构成服务已交付或已收付款的证明；所有接口只读写
/// <c>AgencyServiceFeeAgreements</c> 一张表，<strong>不</strong>改写客户主数据（含佣金比例与信用状态）、
/// 销售订单（含佣金比例与金额）、装柜与单证、收款单及其引用行、销项发票证据、库存与库存成本、费用与退税记录，
/// 也<strong>不</strong>读取或改写业务员提成报表（<c>SalesCommissionRate</c>）口径；费用条款只接受显式提交的值，
/// 不在服务端推断。生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// <para>授权（ERP-439）：**每一条**路由在读取 / 写入之前都先复用
/// <see cref="AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync"/> 解析实时启用身份、既有
/// 「客户资料」（customer）功能菜单（缺一即 fail closed，与月度汇总及 ERP-437 登记册路由同码同源）与 ERP-097
/// 权威客户范围，再把范围下推到台账 / 详情 / 登记 / 修改 / 记录 / 作废；范围外 / 已删除 / 不存在的协议一律返回
/// 同一条不披露存在性的错误，<strong>不</strong>新增任何菜单 / 角色 / 用户授权，也<strong>不</strong>做匿名 / 管理员回退。</para>
/// </summary>
[ApiController]
[Route("api/agency-service-fee-agreements")]
[Authorize]
public class AgencyServiceFeeAgreementController : ControllerBase
{
    private readonly IErpDbContext _db;

    public AgencyServiceFeeAgreementController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 协议证据台账（分页，只读）：可按客户 / 状态 / 币种 / 计费方式 / 生效起始日期区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）；不同币种分别成行展示，绝不被系统合并为一个金额。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] AgencyServiceFeeAgreementQuery query)
    {
        var scope = await AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<PagedResult<AgencyServiceFeeAgreementDto>>.Success(
            await AgencyServiceFeeAgreementService.ListAsync(_db, query, scope)));
    }

    /// <summary>协议证据详情（含客户可用性标注与同源口径文案；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<AgencyServiceFeeAgreementDto>.Success(
            await AgencyServiceFeeAgreementService.GetAsync(_db, id, scope)));
    }

    /// <summary>
    /// 新增草稿协议证据：校验协议号、客户（必须存在、未删除且启用）、生效日期区间、币种、
    /// 计费方式与显式费用条款（比例费率或固定金额，不兼容组合一律拒绝）、计费依据说明，并拒绝重复身份。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgencyServiceFeeAgreementSaveDto dto)
    {
        var scope = await AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<AgencyServiceFeeAgreementDto>.Success(
            await AgencyServiceFeeAgreementService.CreateAsync(_db, dto, scope),
            "代理服务费协议证据草稿已登记（仅商业条款证据留痕；未开发票、未记账、未授权付款）"));
    }

    /// <summary>修改草稿协议证据（已登记 / 已作废拒绝修改；协议号与客户快照由服务端重新写入）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AgencyServiceFeeAgreementSaveDto dto)
    {
        var scope = await AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<AgencyServiceFeeAgreementDto>.Success(
            await AgencyServiceFeeAgreementService.UpdateAsync(_db, id, dto, scope),
            "代理服务费协议证据草稿已更新"));
    }

    /// <summary>
    /// 登记协议证据（草稿 → 已登记）：只改状态、登记时间与登记人（服务端按已认证身份写入，客户端不能提交该字段），
    /// <strong>不</strong>开发票、<strong>不</strong>记账、<strong>不</strong>授权或发起付款，也不改写任何既有记录。
    /// </summary>
    [HttpPost("{id:long}/record")]
    public async Task<IActionResult> Record(long id)
    {
        var scope = await AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<AgencyServiceFeeAgreementDto>.Success(
            await AgencyServiceFeeAgreementService.RecordAsync(_db, id, CurrentUserName(), scope),
            "代理服务费协议证据已登记（证据已冻结，可作废但不可改写；未开发票、未记账、未授权付款）"));
    }

    /// <summary>
    /// 作废协议证据（必须填写作废原因）：保留协议身份、费用条款、客户快照、登记人与时间戳及审计历史，
    /// 不物理删除、不改写已登记条款，也不产生任何发票 / 记账 / 付款 / 法律动作；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] AgencyServiceFeeAgreementVoidRequest? request)
    {
        var scope = await AgencyServiceFeeReconciliationRules.EnsureRegisterAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<AgencyServiceFeeAgreementDto>.Success(
            await AgencyServiceFeeAgreementService.VoidAsync(_db, id, request?.Reason, scope),
            "代理服务费协议证据已作废（原始条款与历史保留，可读）"));
    }

    /// <summary>当前登录用户名（登记人由服务端按已认证身份写入，不采信客户端提交的值；无身份时返回 null → 记「未知用户」）</summary>
    private string? CurrentUserName()
        => User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权规则 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
