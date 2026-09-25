using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户收款单 → 代理服务费对账单 收款分摊证据登记控制器（ERP-071）：登记「某张既有收款单把多少钱指向了
/// 哪几条已登记的代理服务费对账单证据」，让「客户付的这笔钱对应哪几笔代理服务费对账」可被显式登记与追溯。
/// <para>为什么需要本模块（ERP-071 审计结论）：ERP-070 的对账单证据只记录客户、服务来源与合计，
/// **没有任何收款级持久化引用**；ERP-053 是「收款单 → 销售订单」的**另一个**证据维度，两者口径不同。
/// 因此本模块提供**唯一**的「收款 → 代理服务费对账单」分摊模型：不在收款单或对账单上加列、
/// 不建第二套收款主数据，也不替换 / 复制 / 派生 ERP-053。</para>
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>银行入账 / 到账凭证、<strong>不是</strong>应收账款台账或余额、
/// <strong>不是</strong>货款核销、<strong>不是</strong>客户对账单、<strong>不是</strong>收入确认、
/// <strong>不是</strong>税务（销项）判断、<strong>不是</strong>结算确认，也<strong>不是</strong>会计凭证或总账记账分录；
/// 所有接口只读写 <c>AgencyServiceFeeCollectionAllocations</c> 一张表，<strong>不</strong>收款或付款、<strong>不</strong>记账、
/// <strong>不</strong>核销、<strong>不</strong>催收或联系客户、<strong>不</strong>调用任何外部服务，
/// 也<strong>不</strong>改写收款单及其引用行、对账单证据、ERP-069 协议证据、客户主数据、销售订单、装柜与单据、
/// 发票、库存与费用记录；生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由
/// SchemaUpgrader 第 44 段幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/agency-service-fee-collection-allocations")]
[Authorize]
public class AgencyServiceFeeCollectionAllocationController : ControllerBase
{
    private readonly IErpDbContext _db;

    public AgencyServiceFeeCollectionAllocationController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 收款分摊行台账（分页，只读）：可按对账单 / 收款单 / 客户 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）；对账单与收款单的可用性都是只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] AgencyServiceFeeCollectionAllocationQuery query)
        => Ok(ApiResponse<PagedResult<AgencyServiceFeeCollectionAllocationDto>>.Success(
            await AgencyServiceFeeCollectionAllocationService.ListAsync(_db, query)));

    /// <summary>
    /// 模块元数据（只读）：支持币种、有界额度与口径文案（分摊 / 金额 / 唯一性 / 证据维度分离 / 历史只读 /
    /// 模块边界），供界面与接口同源显示。
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<AgencyServiceFeeCollectionAllocationMetadataDto>.Success(
            AgencyServiceFeeCollectionAllocationService.GetMetadata()));

    /// <summary>
    /// 可分摊收款单候选（只读、有界）：必须显式给出客户与币种（资格判定依赖它们），单次最多
    /// <c>AgencyServiceFeeCollectionAllocationRules.MaxReceiptCandidates</c> 条；只返回未删除且未取消的收款单，
    /// 附**本维度**已分摊金额与可分摊余额（不与 ERP-053 销售订单收款引用相加）。
    /// </summary>
    [HttpGet("receipts")]
    public async Task<IActionResult> ReceiptCandidates(
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = AgencyServiceFeeCollectionAllocationService.MaxReceiptCandidates)
        => Ok(ApiResponse<List<AgencyServiceFeeCollectionAllocationReceiptCandidateDto>>.Success(
            await AgencyServiceFeeCollectionAllocationService.ListReceiptCandidatesAsync(
                _db, customerId, currency, keyword, take)));

    /// <summary>
    /// 可承接收款分摊的对账单候选（只读、有界）：只列出未删除且**已登记**的代理服务费对账单证据，
    /// 附**本维度**已分摊金额与未分摊额（未分摊额是算术证据，不是已付 / 已结清 / 逾期或记账状态）。
    /// </summary>
    [HttpGet("statements")]
    public async Task<IActionResult> StatementCandidates(
        [FromQuery] long customerId,
        [FromQuery] string? currency,
        [FromQuery] string? keyword,
        [FromQuery] int take = AgencyServiceFeeCollectionAllocationService.MaxStatementCandidates)
        => Ok(ApiResponse<List<AgencyServiceFeeCollectionAllocationStatementCandidateDto>>.Success(
            await AgencyServiceFeeCollectionAllocationService.ListStatementCandidatesAsync(
                _db, customerId, currency, keyword, take)));

    /// <summary>
    /// 收款单侧汇总（只读派生）：收款单快照 + **本维度**有效分摊金额 / 可分摊余额、行数与已作废行数 +
    /// 有界逐行明细；已作废历史永不并入有效合计（只单独计数与列出）。
    /// </summary>
    [HttpGet("receipts/{receiptId:long}/summary")]
    public async Task<IActionResult> ReceiptSummary(long receiptId)
        => Ok(ApiResponse<AgencyServiceFeeCollectionAllocationReceiptSummaryDto>.Success(
            await AgencyServiceFeeCollectionAllocationService.GetReceiptSummaryAsync(_db, receiptId)));

    /// <summary>指定收款单的分摊行清单（只读、有界；status 传 1 只看有效 / 传 2 只看已作废）</summary>
    [HttpGet("receipts/{receiptId:long}/allocations")]
    public async Task<IActionResult> AllocationsForReceipt(
        long receiptId,
        [FromQuery] int? status = null,
        [FromQuery] int take = AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt)
        => Ok(ApiResponse<List<AgencyServiceFeeCollectionAllocationDto>>.Success(
            await AgencyServiceFeeCollectionAllocationService.ListForReceiptAsync(_db, receiptId, status, take)));

    /// <summary>
    /// 对账单侧汇总（只读派生）：对账单快照 + **本维度**有效分摊金额 / 未分摊额、行数与已作废行数 +
    /// 有界逐行明细；未分摊额绝不被静默核销或改派，也不表示已收款 / 已结清 / 收入确认 / 记账状态。
    /// </summary>
    [HttpGet("statements/{statementId:long}/summary")]
    public async Task<IActionResult> StatementSummary(long statementId)
        => Ok(ApiResponse<AgencyServiceFeeCollectionAllocationStatementSummaryDto>.Success(
            await AgencyServiceFeeCollectionAllocationService.GetStatementSummaryAsync(_db, statementId)));

    /// <summary>指定对账单的分摊行清单（只读、有界；status 传 1 只看有效 / 传 2 只看已作废）</summary>
    [HttpGet("statements/{statementId:long}/allocations")]
    public async Task<IActionResult> AllocationsForStatement(
        long statementId,
        [FromQuery] int? status = null,
        [FromQuery] int take = AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerStatement)
        => Ok(ApiResponse<List<AgencyServiceFeeCollectionAllocationDto>>.Success(
            await AgencyServiceFeeCollectionAllocationService.ListForStatementAsync(_db, statementId, status, take)));

    /// <summary>分摊行详情（含对账单与收款单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<AgencyServiceFeeCollectionAllocationDto>.Success(
            await AgencyServiceFeeCollectionAllocationService.GetAsync(_db, id)));

    /// <summary>
    /// 登记一条收款分摊行：校验对账单（未删除 / **已登记**）与收款单（未删除 / 未取消）、二者客户与币种一致性、
    /// 分摊金额（币种精度 + 大于 0）、两侧有效行数上限、重复有效行，以及**收款单可分摊余额**与
    /// **对账单未分摊额**；登记人由服务端按已认证身份写入（客户端不能提交该字段）。
    /// <para>本接口<strong>不</strong>改写收款单与对账单的任何字段，也<strong>不</strong>执行收款、记账、核销或结算。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgencyServiceFeeCollectionAllocationSaveDto dto)
        => Ok(ApiResponse<AgencyServiceFeeCollectionAllocationDto>.Success(
            await AgencyServiceFeeCollectionAllocationService.CreateAsync(_db, dto, CurrentUserName()),
            "收款分摊证据已登记（仅证据留痕；未执行收款、未核销、未结算、未记账）"));

    /// <summary>
    /// 作废分摊行（必须填写作废原因）：保留原始分摊金额、对账单 / 收款单 / 客户快照、登记人与时间戳，
    /// 不物理删除、不改派、不改写原始金额；作废后该组合可重新登记一条新的有效分摊行；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] AgencyServiceFeeCollectionAllocationVoidRequest? request)
        => Ok(ApiResponse<AgencyServiceFeeCollectionAllocationDto>.Success(
            await AgencyServiceFeeCollectionAllocationService.VoidAsync(_db, id, request?.Reason),
            "收款分摊证据已作废（原始金额与历史保留，可读）"));

    /// <summary>当前登录用户名（登记人由服务端按已认证身份写入，不采信客户端提交的值；无身份时返回 null → 记「未知用户」）</summary>
    private string? CurrentUserName()
        => User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
