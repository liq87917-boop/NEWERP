using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 代理服务费对账单证据控制器（ERP-070）：登记客户代理服务费的**仓库内操作性费用证据**
/// （对账单号 / 权威客户 / 币种 / 对账日期 / **可选**到期日 / 服务期间 / 显式关联的 ERP-069 协议 /
/// 一条或多条**显式服务来源引用行**），并可显式登记与作废。
/// <para>来源链接口径：每一行只按<b>来源类型 + 来源记录 Id（持久化标识符）</b>显式引用既有的销售订单 / 装柜清单，
/// 单号 / 日期 / 状态 / 客户 / 币种快照由服务端权威写入；接口<strong>不</strong>接受按单号文本、金额、日期或相似度
/// 匹配来源的请求，也<strong>不</strong>提供「自动关联 / 推荐来源」的入口。</para>
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>税务发票系统、<strong>不是</strong>具有法律效力的客户
/// 对账单确认、<strong>不是</strong>收入确认、<strong>不是</strong>付款通知或催收函、<strong>不是</strong>结算 / 核销确认，
/// 也<strong>不是</strong>会计凭证或总账记账分录；所有接口只读写
/// <c>AgencyServiceFeeStatements</c> 与 <c>AgencyServiceFeeStatementLines</c> 两张表，
/// <strong>不</strong>开票 / 报税、<strong>不</strong>记账、<strong>不</strong>收款或付款、<strong>不</strong>催收或联系客户、
/// <strong>不</strong>调用任何外部服务，也<strong>不</strong>改写 ERP-069 协议证据、客户主数据、销售订单、装柜与装柜清单、
/// 单证、发票、收款单及其引用行、库存与库存成本、费用与退税、结算与余额记录。行金额与计费基础数量只接受显式提交的值，
/// 合计由服务端计算（客户端提交的合计不被采信）。生产库结构变更仍由 Human Gate 控制
/// （本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 第 43 段幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/agency-service-fee-statements")]
[Authorize]
public class AgencyServiceFeeStatementController : ControllerBase
{
    private readonly IErpDbContext _db;

    public AgencyServiceFeeStatementController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 对账单证据台账（分页，只读）：可按客户 / 协议 / 状态 / 币种 / 来源类型 / 对账日期区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）；列表返回行数摘要，行的完整快照请看详情接口。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] AgencyServiceFeeStatementQuery query)
        => Ok(ApiResponse<PagedResult<AgencyServiceFeeStatementDto>>.Success(
            await AgencyServiceFeeStatementService.ListAsync(_db, query)));

    /// <summary>
    /// 模块元数据（只读）：来源类型 / 状态白名单、支持币种、有界额度与口径文案（来源链接 / 金额 / 到期日 /
    /// 唯一性 / 与发票收款记账的分离 / 模块边界），供界面与接口同源显示。
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<AgencyServiceFeeStatementMetadataDto>.Success(
            AgencyServiceFeeStatementService.GetMetadata()));

    /// <summary>
    /// 可引用的**显式服务来源**候选（只读、**有界**）：必须显式给出客户与对账单币种（资格判定依赖它们），
    /// 单次最多 <c>AgencyServiceFeeStatementService.MaxSourceOptions</c> 条；只返回身份 / 日期 / 状态 / 客户 / 币种，
    /// <strong>不回显来源金额</strong>（来源金额不是费用依据），关键字只匹配单号，绝不按相似度推荐来源。
    /// </summary>
    [HttpGet("source-options")]
    public async Task<IActionResult> SourceOptions(
        [FromQuery] string? sourceType,
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = AgencyServiceFeeStatementService.MaxSourceOptions)
        => Ok(ApiResponse<List<AgencyServiceFeeStatementSourceOptionDto>>.Success(
            await AgencyServiceFeeStatementService.ListSourceOptionsAsync(
                _db, sourceType, customerId, currency, keyword, take)));

    /// <summary>对账单证据详情（含全部有界行清单、来源快照与客户 / 协议可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<AgencyServiceFeeStatementDto>.Success(
            await AgencyServiceFeeStatementService.GetAsync(_db, id)));

    /// <summary>
    /// 新增草稿对账单证据：校验对账单号、客户（存在、未删除且启用）、显式关联的 ERP-069 协议
    /// （必须已登记，且客户与币种与对账单一致）、对账日期、可选到期日、服务期间、每一条显式来源行
    /// （来源存在、未删除、未取消、客户与币种兼容）与行金额（按币种精度取整、必须大于 0），
    /// 并在服务端计算合计；重复身份与已被其它未作废对账单引用的来源都被拒绝。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgencyServiceFeeStatementSaveDto dto)
        => Ok(ApiResponse<AgencyServiceFeeStatementDto>.Success(
            await AgencyServiceFeeStatementService.CreateAsync(_db, dto),
            "代理服务费对账单证据草稿已登记（仅操作性费用证据留痕；未开票、未收款、未联系客户、未记账）"));

    /// <summary>修改草稿对账单证据（已登记 / 已作废拒绝修改；客户与协议快照、行号与来源快照由服务端重新写入）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AgencyServiceFeeStatementSaveDto dto)
        => Ok(ApiResponse<AgencyServiceFeeStatementDto>.Success(
            await AgencyServiceFeeStatementService.UpdateAsync(_db, id, dto),
            "代理服务费对账单证据草稿已更新（合计按已校验行在服务端重算）"));

    /// <summary>
    /// 登记对账单证据（草稿 → 已登记）：按持久化行金额在服务端重算合计并冻结表头与全部行，
    /// 登记人由服务端按已认证身份写入（客户端不能提交该字段）；
    /// <strong>不</strong>开票、<strong>不</strong>记账、<strong>不</strong>收款或催收，也不改写任何来源与协议记录。
    /// </summary>
    [HttpPost("{id:long}/record")]
    public async Task<IActionResult> Record(long id)
        => Ok(ApiResponse<AgencyServiceFeeStatementDto>.Success(
            await AgencyServiceFeeStatementService.RecordAsync(_db, id, CurrentUserName()),
            "代理服务费对账单证据已登记（证据已冻结，可作废但不可改写；未开票、未收款、未记账）"));

    /// <summary>
    /// 作废对账单证据（必须填写作废原因）：保留对账单身份、全部原始行、来源快照、客户与协议快照、
    /// 登记人与时间戳，不物理删除、不改写原始金额；作废同时释放被占用的来源身份；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] AgencyServiceFeeStatementVoidRequest? request)
        => Ok(ApiResponse<AgencyServiceFeeStatementDto>.Success(
            await AgencyServiceFeeStatementService.VoidAsync(_db, id, request?.Reason),
            "代理服务费对账单证据已作废（原始行与历史保留，可读）"));

    /// <summary>当前登录用户名（登记人由服务端按已认证身份写入，不采信客户端提交的值；无身份时返回 null → 记「未知用户」）</summary>
    private string? CurrentUserName()
        => User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
