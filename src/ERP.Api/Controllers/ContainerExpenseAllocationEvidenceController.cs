using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜费用分摊证据控制器（ERP-060）：把 ERP-042 已持久化的分摊批次与分摊行按
/// 「装柜清单（一柜）→ 币种 → 客户」**只读**呈现，并提供有界分页的工作台。
/// <para>审计口径：ERP-042 的分摊批次（<c>FinanceExpenseAllocationBatches</c>）与分摊行
/// （<c>FinanceExpenseAllocationLines</c>）是分摊证据的唯一权威登记册；
/// 本控制器<strong>不新建任何表、不新增任何列、不执行任何写操作</strong>，只按显式装柜清单 Id /
/// 结算单 Id 与显式筛选字段读取。</para>
/// <para>边界（控制器层同样遵守）：所有接口都是只读 —— 不写任何表、不改写装柜清单与明细 / 参与方 /
/// 分摊批次与分摊行 / 费用单 / 结算单 / 客户主数据，不记账、不生成凭证 / 收款 / 付款 / 结算单，
/// 也不回填任何历史留痕；缺失证据一律显示「无（未登记）」或「未知」，绝不推断为零费用、已结算、
/// 应收、应付或客户对账结论。</para>
/// </summary>
[ApiController]
[Route("api/container/expense-allocation-evidence")]
[Authorize]
public class ContainerExpenseAllocationEvidenceController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ContainerExpenseAllocationEvidenceController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 分摊证据工作台（分页，只读）：只按显式持久化字段筛选（柜号 / 装柜清单号 / 批次号 / 币种 /
    /// 批次状态 / 客户 Id / 关键字），默认包含已作废历史；单页内批量装载，不产生逐行数据库访问。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerExpenseAllocationEvidenceQuery query)
        => Ok(ApiResponse<ContainerExpenseAllocationEvidenceWorkspaceDto>.Success(
            await ContainerExpenseAllocationEvidenceService.ListAsync(
                _db, query, await EnsureAuthorizedAsync())));

    /// <summary>
    /// 按**显式装柜清单 Id** 读取分摊证据（只读）：有效批次与「币种 → 客户」分组、未分摊参考、
    /// 已作废 / 历史异常批次（<paramref name="includeHistory"/>）与逐行链接标注；
    /// 未登记分摊批次时返回「无（未登记任何有效分摊批次）」，不推断为零费用或已结清。
    /// </summary>
    [HttpGet("loading-lists/{loadingListId:long}")]
    public async Task<IActionResult> GetForLoadingList(
        long loadingListId,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
        => Ok(ApiResponse<ContainerExpenseAllocationEvidenceDto>.Success(
            await ContainerExpenseAllocationEvidenceService.GetForLoadingListAsync(
                _db, loadingListId, includeHistory, historyTake, await EnsureAuthorizedAsync()),
            "已按显式装柜清单返回分摊证据（只读：缺失显示「无 / 未知」，不改写任何单据）"));

    /// <summary>
    /// 按**显式装柜结算单 Id** 读取分摊证据（只读）：结算单持久化字段只读回显 + 其关联装柜清单上的
    /// 分摊证据，两者分开标注；分摊证据不参与结算金额计算，也不会写入结算单（金额对照只作算术证据）。
    /// 结算单未关联装柜清单时证据显示「未知」，不按柜号或客户推断。
    /// </summary>
    [HttpGet("settlements/{settlementId:long}")]
    public async Task<IActionResult> GetForSettlement(
        long settlementId,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
        => Ok(ApiResponse<ContainerSettlementAllocationEvidenceDto>.Success(
            await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(
                _db, settlementId, includeHistory, historyTake, await EnsureAuthorizedAsync()),
            "已按显式装柜结算单返回分摊证据（只读：结算金额字段为原值回显，分摊证据不参与结算计算）"));

    /// <summary>
    /// 分摊证据读取前的实时授权（ERP-385）：身份 / 账号状态 / 既有「费用单」（<c>expense-bill</c>）菜单授权与
    /// 权威客户数据范围，缺失 / 禁用 / 无授权一律 fail closed（不返回任何证据）。
    /// </summary>
    private Task<SalespersonDataScope> EnsureAuthorizedAsync()
        => ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());

    /// <summary>当前登录账号 Id（身份只来自已认证请求主体，缺失 / 非法返回 <c>null</c>）</summary>
    private long? CurrentUserId()
        => long.TryParse(HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && id > 0
            ? id
            : null;
}
