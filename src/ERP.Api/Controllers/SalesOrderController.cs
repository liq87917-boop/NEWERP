using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单控制器
/// </summary>
[Route("api/sales-orders")]
public class SalesOrderController : DocumentControllerBase<SalesOrder>
{
    private readonly IDocumentNumberService _noService;

    public SalesOrderController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>
    /// ERP-413：是否必须执行实时授权。真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
    /// 进程内直接调用（历史单元测试 / 内部派生读取，无 HTTP 请求管线）仅在携带当前登录身份时执行。
    /// 只对「既无任何登录身份、又不在 HTTP 请求管线内」的调用免授权：这类调用不可能由外部请求到达，
    /// 也绝不把缺失身份当作管理员（真实匿名请求因处于请求管线内一律 fail closed）。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>
    /// ERP-413 执行证据入口授权（实时身份 + 既有「销售订单」菜单 + 权威客户范围）：
    /// 进程内无身份直调返回 <c>null</c>（免授权，保持既有单元测试口径）。
    /// </summary>
    private async Task<SalespersonDataScope?> EnsureExecutionEvidenceAuthorizedAsync()
        => RequiresLiveAuthorization()
            ? await SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync(Db, CurrentUserId())
            : null;

    /// <summary>ERP-413 单张订单证据的非披露归属复核（<paramref name="scope"/> 为 null 表示进程内免授权调用）。</summary>
    private async Task EnsureOrderEvidenceAuthorizedAsync(long id, SalespersonDataScope? scope)
    {
        if (scope is null) return;
        await SalesOrderExecutionAuthorizationRules.EnsureOrderAllowedAsync(Db, scope, id);
    }

    /// <summary>ERP-413 批量显式 Id 证据的整批非披露归属复核（<paramref name="scope"/> 为 null 或未请求任何订单时直接放行）。</summary>
    private async Task EnsureOrdersEvidenceAuthorizedAsync(IReadOnlyCollection<long> orderIds, SalespersonDataScope? scope)
    {
        if (scope is null || orderIds.Count == 0) return;
        await SalesOrderExecutionAuthorizationRules.EnsureOrdersAllowedAsync(Db, scope, orderIds);
    }

    /// <summary>
    /// ERP-413 列表 / 详情入口的实时身份与权威范围（缺失 / 已删除按未认证拒绝，已禁用按权限不足拒绝）；
    /// 进程内无身份直调返回 <c>null</c>（免授权，保持既有单元测试口径）。列表 / 详情沿用既有 ERP-097 范围口径，
    /// 不额外要求模块菜单（执行证据入口才要求）。
    /// </summary>
    private async Task<SalespersonDataScope?> ResolveListDetailScopeAsync()
        => RequiresLiveAuthorization()
            ? await SalesOrderExecutionAuthorizationRules.EnsureLiveIdentityAsync(Db, CurrentUserId())
            : null;

    /// <summary>
    /// ERP-420 规范写入授权（实时身份 + 既有「销售订单」菜单 + ERP-097 权威客户范围）：缺失 / 非法 / 已删除身份
    /// 按未认证拒绝，已禁用 / 缺菜单按权限不足拒绝。写入路径**不做**「无请求路径 / 匿名进程内调用」豁免，
    /// 因此直接调用控制器的写入方法同样 fail closed；每次调用都重新查询，撤销授权后立即收敛。
    /// </summary>
    private Task<SalespersonDataScope> EnsureCanonicalWriteAuthorizedAsync()
        => SalesOrderMutationAuthorizationRules.EnsureWriteAuthorizedAsync(Db, CurrentUserId());

    /// <summary>ERP-420 持久化订单归属复核（范围外 / 已删除 / 不存在返回同一非披露错误）。</summary>
    private Task<long> EnsurePersistedOrderAllowedAsync(SalespersonDataScope scope, long id)
        => SalesOrderMutationAuthorizationRules.EnsureOrderAllowedAsync(Db, scope, id);

    /// <summary>
    /// ERP-424 规范文档输出入口授权（实时身份 + 既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 +
    /// ERP-097 权威客户范围）：打印 / JSON 单据导出 / Excel 导出**不做**「无请求路径 / 匿名进程内调用」豁免，
    /// 因此直接调用控制器的这三个方法同样 fail closed；每次调用都重新查询，撤销授权 / 账号停用后立即收敛。
    /// </summary>
    private Task<SalespersonDataScope> EnsureDocumentOutputAuthorizedAsync()
        => SalesOrderDocumentOutputAuthorizationRules.EnsureDocumentOutputAuthorizedAsync(Db, CurrentUserId());

    /// <summary>ERP-424 单张订单文档输出的持久化归属复核（范围外 / 已删除 / 不存在 / 非正 Id 返回同一非披露错误）。</summary>
    private Task<long> EnsureDocumentOutputOrderAllowedAsync(SalespersonDataScope scope, long id)
        => SalesOrderDocumentOutputAuthorizationRules.EnsureOrderAllowedAsync(Db, scope, id);

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var scope = await ResolveListDetailScopeAsync() ?? await ResolveScopeAsync();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // 关键字同时匹配订单号 / 客户 PO 号 / 合同号（外贸合同核对时按客户 PO 或合同号检索）
            var kw = query.Keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.CustomerPoNo.Contains(kw) || o.ContractNo.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<SalesOrder>>.Success(
            new PagedResult<SalesOrder> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情（ERP-413：实时身份 + 持久化客户归属先于读取；范围外用与不存在同一非披露错误）。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await ResolveListDetailScopeAsync();
        if (scope is not null)
            await SalesOrderExecutionAuthorizationRules.EnsureOrderAllowedAsync(Db, scope, id);

        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound(SalesOrderExecutionAuthorizationRules.NotFoundText);

        if (scope is null && !(await ResolveScopeAsync()).AllowsCustomer(entity.CustomerId))
            throw BusinessException.NotFound(SalesOrderExecutionAuthorizationRules.NotFoundText);

        return Ok(ApiResponse<SalesOrder>.Success(entity));
    }

    /// <summary>从现有订单、销售出库、出口单证和客诉记录派生只读执行时间线（ERP-413：先实时授权再派生）。</summary>
    [HttpGet("{id:long}/timeline")]
    public async Task<IActionResult> Timeline(long id)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        await EnsureOrderEvidenceAuthorizedAsync(id, scope);
        return Ok(ApiResponse<List<OrderTimelineEvent>>.Success(
            await OrderExecutionTimeline.ForSalesOrderAsync(Db, id)));
    }

    /// <summary>
    /// 财务核对（ERP-028，只读派生）：按既有引用字段把本单与定金 / 货款申请单、付款单、费用单、客诉单、
    /// 收款单与结算单关联；只有「权威引用 + 已审核 + 币种一致」的记录计入金额，其余仅列出，金额未知为 null（不推断）。
    /// <para>ERP-413：证据派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围。</para>
    /// </summary>
    [HttpGet("{id:long}/finance-reconciliation")]
    public async Task<IActionResult> FinanceReconciliation(long id)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        await EnsureOrderEvidenceAuthorizedAsync(id, scope);
        return Ok(ApiResponse<OrderFinanceReconciliationView>.Success(
            await OrderFinanceReconciliation.ForSalesOrderAsync(Db, id)));
    }

    /// <summary>
    /// 出货与收款进度（ERP-032，只读派生）：出货数量按「以本单为来源（SalesOrderId）、未删除、已审核」的销售出库单明细派生
    /// （待提交 / 已提交只单列，已驳回 / 已取消不计入）；收款链接复用 ERP-028 的既有引用字段（定金 / 货款申请单的 SalesOrderId），
    /// 只有「已审核 + 币种一致」计入金额，无权威引用或命中派生上限时金额为 null（未知，不用 0 顶替）。
    /// <para>ERP-413：证据派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围。</para>
    /// </summary>
    [HttpGet("{id:long}/progress")]
    public async Task<IActionResult> Progress(long id)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        await EnsureOrderEvidenceAuthorizedAsync(id, scope);
        return Ok(ApiResponse<SalesOrderProgressView>.Success(
            await SalesOrderProgress.ForSalesOrderAsync(Db, id)));
    }

    /// <summary>
    /// 销售交期异常工作台（ERP-102，只读派生、分页有界）：按客户 + 显式 as-of 基准日过滤销售订单，
    /// 报告订单头 / 明细行交货日期（明细行优先）与「已订 / 已审核出货 / 未出」数量证据，并派生逾期 / 即将到期 / 已出齐 / 在途 / 未知交期状态；
    /// 缺日期或出货证据不完整为未知；不改写订单状态与已登记进度，不执行迁移 / 生产 SQL / 真实数据库操作 / 部署。
    /// <para>ERP-432：派生之前先复核实时身份 / 既有「销售订单」菜单 / ERP-097 权威客户范围，并把权威客户范围
    /// （含「范围内全部客户」的集合过滤）下推到计数 / 分页之前；受限账号只命中范围内客户，
    /// 显式筛选范围外客户返回空报表（0 计数、无行，不透露其存在性）；
    /// 拒绝时返回既有受控非披露错误，绝不返回任何行或计数。</para>
    /// </summary>
    [HttpGet("delivery-exceptions")]
    public async Task<IActionResult> DeliveryExceptions([FromQuery] SalesOrderDeliveryExceptionQuery query)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        return Ok(ApiResponse<SalesOrderDeliveryExceptionReport>.Success(
            await SalesOrderDeliveryExceptions.ForQueryAsync(Db, query, scope)));
    }

    /// <summary>
    /// 销售订单退货影响（ERP-101，只读派生）：按显式链接链「销售退货 → 来源出库单 → 本销售订单」派生
    /// 毛出货 / 有效退货 / 净出货数量，并把未审核、客户不一致、来源已删除、来源不属于本单、未关联来源
    /// 的退货作为异常单列（绝不推断为扣减）；超退净额为负、不静默钳制；证据不完整为未知。
    /// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单任何已登记进度。</para>
    /// <para>ERP-413：证据派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围。</para>
    /// </summary>
    [HttpGet("{id:long}/return-impact")]
    public async Task<IActionResult> ReturnImpact(long id)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        await EnsureOrderEvidenceAuthorizedAsync(id, scope);
        return Ok(ApiResponse<SalesOrderReturnImpactView>.Success(
            await SalesOrderReturnImpact.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 销售订单出货 / 财务进度报表（ERP-032，只读派生、分页有界）：按「客户 + 币种」分组汇总已按权威口径派生的出货数量与收款链接金额，
    /// 不同币种分别成行、绝不合并、不做汇率换算；未链接 / 命中上限一律显式标注未知，不作为应收余额或账龄使用。
    /// <para>ERP-432：派生之前先复核实时身份 / 既有「销售订单」菜单 / ERP-097 权威客户范围，并把范围下推到计数 / 分页
    /// 之前；拒绝时返回既有受控非披露错误，绝不返回任何行或计数。</para>
    /// </summary>
    [HttpGet("shipment-finance-report")]
    public async Task<IActionResult> ShipmentFinanceReport([FromQuery] SalesOrderShipmentFinanceQuery query)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        return Ok(ApiResponse<SalesOrderShipmentFinanceReportView>.Success(
            await SalesOrderShipmentFinanceReport.ForQueryAsync(Db, query, scope)));
    }

    /// <summary>
    /// 客户订单与收款核对报表（ERP-046，**只读派生**、分页有界）：按「客户 + 币种」分组核对销售订单与收款证据 ——
    /// 订单侧暴露已订 / 已出 / 未出数量与订单金额（完全复用 ERP-032 的权威派生：已审核销售出库单；定金 / 货款申请单的
    /// SalesOrderId 才是权威收款引用，且只有「已审核 + 同币种」计入已关联收款金额），
    /// 收款单（<c>FinanceReceipt</c>）只记录客户、没有订单级引用，因此一律作为**未关联证据**单独列出（链接状态恒为 unlinked），
    /// 系统绝不按客户名 / 订单号文本 / 日期 / 金额相似度把它归到任何销售订单；未知一律记 null（不用 0 顶替）。
    /// <para>本接口<strong>不是</strong>应收账款台账、<strong>不是</strong>客户对账单、<strong>不是</strong>收款授权或结算结果，
    /// 也<strong>不是</strong>账龄表：不推算账期与到期日、不判断是否已收讫，且<strong>不写库</strong>
    /// （不改销售订单、出库单、收款单、收款申请、客户信用、库存、财务与税务记录）。</para>
    /// <para>ERP-432：派生之前先复核实时身份 / 既有「销售订单」菜单 / ERP-097 权威客户范围，并把范围下推到计数 / 分页
    /// 之前；拒绝时返回既有受控非披露错误，绝不返回任何行或计数。</para>
    /// </summary>
    [HttpGet("receipt-reconciliation-report")]
    public async Task<IActionResult> ReceiptReconciliationReport([FromQuery] SalesOrderReceiptReconciliationQuery query)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        return Ok(ApiResponse<SalesOrderReceiptReconciliationReport>.Success(
            await SalesOrderReceiptReconciliation.ForQueryAsync(Db, query, scope)));
    }

    /// <summary>
    /// 单张销售订单的收款引用证据（ERP-054，**只读派生**）：只按 ERP-053 的持久化收款引用行
    /// （<c>CustomerReceiptAllocations</c>）汇总「收款单指向本订单」的有效（未作废）引用金额、收款单张数与
    /// 未指向本单 / 订单金额未被引用证据覆盖的上下文；已作废历史证据、无效历史证据（客户 / 币种或快照不一致）
    /// 与无法确认证据（收款单 / 订单不存在或已删除）一律单独分桶列出，绝不并入有效合计。
    /// <para>本接口<strong>不是</strong>银行入账凭证、<strong>不是</strong>应收余额或货款核销、<strong>不是</strong>客户对账单，
    /// 也<strong>不是</strong>账龄表：不判断是否已收款 / 已结清 / 逾期，且<strong>不写库</strong>
    /// （不改销售订单、收款单、客户信用、发票、库存、装柜单证、佣金或费用退税记录）。</para>
    /// <para>ERP-413：证据派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围。</para>
    /// </summary>
    [HttpGet("{id:long}/receipt-evidence")]
    public async Task<IActionResult> ReceiptEvidence(long id)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        await EnsureOrderEvidenceAuthorizedAsync(id, scope);
        return Ok(ApiResponse<SalesOrderReceiptEvidenceDetail>.Success(
            await SalesOrderReceiptEvidence.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 一批销售订单的收款引用证据汇总（ERP-054，**只读派生**、有界批量）：列表页按页取 Id 一次请求取回本页汇总，
    /// 单次最多 200 张订单，绝不逐行查库；命中行数上限时金额与计数按「未知」返回（不用 0 顶替）。
    /// <para>口径与单张详情一致：只统计有效（未作废）引用行，历史 / 无效 / 无法确认证据单独分桶，
    /// 绝不并入有效合计，也绝不换算、合并或改派到其他订单。</para>
    /// <para>ERP-413：派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围；显式 Id 混入任何
    /// 不存在 / 已删除 / 范围外订单即**整批**返回同一非披露错误，绝不返回部分行或计数。</para>
    /// </summary>
    [HttpGet("receipt-evidence-summaries")]
    public async Task<IActionResult> ReceiptEvidenceSummaries([FromQuery] SalesOrderReceiptEvidenceQuery query)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        if (scope is not null)
        {
            query.Normalize();
            await EnsureOrdersEvidenceAuthorizedAsync(query.OrderIds, scope);
        }

        return Ok(ApiResponse<SalesOrderReceiptEvidenceBatch>.Success(
            await SalesOrderReceiptEvidence.ForOrdersAsync(Db, query)));
    }

    /// <summary>
    /// 单张销售订单的销项发票证据（ERP-056，**只读派生**）：只按 ERP-055 的持久化发票证据行
    /// （<c>CustomerSalesInvoiceEvidences</c>）与其分摊行（<c>CustomerSalesInvoiceAllocations</c>）汇总
    /// 「发票含税总额分摊到本订单」的有效（已登记且未作废）分摊金额、发票张数与
    /// 未指向本单 / 订单金额未被有效发票证据分摊的上下文；草稿证据、已作废历史证据、
    /// 无效历史证据（客户 / 币种 / 金额等式或快照不一致）与无法确认证据（发票证据 / 订单不存在或已删除）
    /// 一律单独分桶列出，绝不并入有效合计。
    /// <para>本接口<strong>不是</strong>发票开具系统、<strong>不是</strong>税务申报或销项税金计算、
    /// <strong>不是</strong>应收余额或收款核销、<strong>不是</strong>客户对账单，也<strong>不是</strong>账龄表：
    /// 不判断是否已开票 / 已收款 / 已结清 / 逾期，且<strong>不写库</strong>
    /// （不改销售订单、发票证据与分摊行、收款单与收款引用行、客户信用、库存、装柜单证、佣金或费用退税记录）。</para>
    /// <para>ERP-413：证据派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围。</para>
    /// </summary>
    [HttpGet("{id:long}/invoice-evidence")]
    public async Task<IActionResult> InvoiceEvidence(long id)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        await EnsureOrderEvidenceAuthorizedAsync(id, scope);
        return Ok(ApiResponse<SalesOrderInvoiceEvidenceDetail>.Success(
            await SalesOrderInvoiceEvidence.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 一批销售订单的销项发票证据汇总（ERP-056，**只读派生**、有界批量）：列表页按页取 Id 一次请求取回本页汇总，
    /// 单次最多 200 张订单，绝不逐行查库；命中行数上限时金额与计数按「未知」返回（不用 0 顶替）。
    /// <para>口径与单张详情一致：只统计已登记（未作废）发票下的未删除分摊行，草稿 / 历史 / 无效 / 无法确认证据
    /// 单独分桶，绝不并入有效合计，也绝不换算、合并或改派到其他订单。</para>
    /// <para>ERP-413：派生之前先复核实时身份 / 既有「销售订单」菜单 / 权威客户范围；显式 Id 混入任何
    /// 不存在 / 已删除 / 范围外订单即**整批**返回同一非披露错误，绝不返回部分行或计数。</para>
    /// </summary>
    [HttpGet("invoice-evidence-summaries")]
    public async Task<IActionResult> InvoiceEvidenceSummaries([FromQuery] SalesOrderInvoiceEvidenceQuery query)
    {
        var scope = await EnsureExecutionEvidenceAuthorizedAsync();
        if (scope is not null)
        {
            query.Normalize();
            await EnsureOrdersEvidenceAuthorizedAsync(query.OrderIds, scope);
        }

        return Ok(ApiResponse<SalesOrderInvoiceEvidenceBatch>.Success(
            await SalesOrderInvoiceEvidence.ForOrdersAsync(Db, query)));
    }


    /// <summary>
    /// 客户销售订单价格历史（ERP-108，**只读派生**、分页有界）：按商品过滤「已审核、未删除」销售订单的
    /// 「未删除」明细价格，可再按客户与订单日期区间筛选，并应用业务员数据范围（硬边界）；
    /// 仅把「客户 + 商品 + 规格 + 单位 + 币种」完全一致的明细归为同一口径，保留原始单价与贸易条款，
    /// 口径不一致（单位 / 币种 / 规格 / 客户不同）的证据分组成行单列。
    /// <para>本接口<strong>不是</strong>定价工具、<strong>不写库</strong>：不改写销售订单 / 明细 / 价格 / 贸易条款，
    /// 不做汇率换算、不合并不同币种金额，也不执行迁移 / 生产 SQL / 真实数据库操作 / 部署。</para>
    /// </summary>
    [HttpGet("price-history")]
    public async Task<IActionResult> PriceHistory([FromQuery] CustomerSalesPriceHistoryQuery query)
    {
        var scope = await ResolveScopeAsync();
        return Ok(ApiResponse<CustomerSalesPriceHistoryView>.Success(
            await CustomerSalesPriceHistoryService.QueryAsync(Db, query, scope.AllowedCustomerIds)));
    }

    /// <summary>
    /// 创建（ERP-401 / ERP-420）：先复核**规范写入授权**（实时身份 + 既有「销售订单」菜单 + 权威客户范围）与
    /// 拟议客户范围（无来源的手工订单同样适用），显式来源再做**权威解析 + 确定性来源行锁（报价单 → PI）+ 原子事务**，
    /// 在锁内复核既有转换资格与唯一目标，<b>之后</b>才预约单据号与写入；显式来源 Id 全部无法解析时按「显式历史值」
    /// 原样保留（不构成实时链接）；未链接的手工订单不取任何来源锁，但表头与明细仍在同一原子事务内写入（ERP-421）。
    /// <para>ERP-422：客户端提交的数量 / 单价 / 金额 / 合计 / 定金一律由服务端按 <see cref="SalesOrderAmountRules"/>
    /// 唯一权威口径重算覆盖；新写入条款校验在<b>单号预约与任何字段改写之前</b>完成，非法请求既不消耗单号也不落库。</para>
    /// <para>ERP-423：客户 / 商品 / 可选业务员 / 可选目的港与既有有效单位口径在同一原子事务内、单号预约与任何
    /// 表头 / 明细赋值之前复核；引用失效（不存在 / 已删除 / 已停用 / 单位不支持）时原子拒绝且不消耗单号。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SalesOrder entity)
    {
        // ERP-420：写入授权先于单号预约与任何写入；无来源 / 历史无法解析来源路径同样 fail closed，不做进程内豁免。
        var writeScope = await EnsureCanonicalWriteAuthorizedAsync();
        SalesOrderMutationAuthorizationRules.EnsureProposedCustomerAllowed(writeScope, entity.CustomerId);

        var requestedQuotationId = SalesOrderSourceLineageRules.NormalizeId(entity.SourceQuotationId);
        var requestedPiId = SalesOrderSourceLineageRules.NormalizeId(entity.SourcePiId);
        var change = SalesOrderSourceLineageRules.ResolveChange(null, null, requestedQuotationId, requestedPiId);

        IDbContextTransaction? transaction = null;
        try
        {
            // ERP-421：无论手工 / 历史 / 已解析来源，表头与明细都在同一原子事务内写入。
            transaction = await SalesOrderMutationRules.BeginMutationTransactionAsync(Db);

            // ERP-422：新写入条款校验先于单号预约与任何字段 / 明细改写；失败整体回滚、不占用单号、不落任何数据。
            SalesOrderMutationRules.EnsureValidatedTerms(entity);

            var lineage = new SalesOrderSourceLineage();
            if (change.HasSource)
            {
                // 第一阶段：有界只读权威解析（不加锁）—— 无法解析的显式历史值不占用任何来源锁。
                lineage = await SalesOrderSourceLineageRules.ResolveAsync(Db, change, entity.CustomerId);
                if (!lineage.IsUnresolvedLegacy)
                {
                    // 第二阶段：原子事务内按确定性锁序加来源行锁，并**锁内重读**权威来源后才放行。
                    if (!await SalesOrderSourceLineageRules.LockSourcesAsync(Db, change.QuotationId, change.PiId))
                        throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.SourceNotFoundText);

                    lineage = await SalesOrderSourceLineageRules.ResolveAsync(Db, change, entity.CustomerId);
                    if (lineage.IsUnresolvedLegacy)
                        throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.SourceNotFoundText);

                    await SalesOrderSourceLineageRules.EnsureWriteAuthorizedAsync(
                        Db, CurrentUserId(), lineage, entity.CustomerId);
                    await SalesOrderSourceLineageRules.EnsureNewLinkEligibleAsync(Db, lineage, null);
                }
            }

            // ERP-423：来源血缘 / 转换资格复核通过后，再复核实时主数据引用（客户 / 商品 / 可选业务员 /
            // 可选目的港 / 既有有效单位口径）；仍先于单号预约与任何表头 / 明细赋值，失败整体回滚、不占单号、不落任何数据。
            await SalesOrderMasterReferenceRules.EnsureMasterReferencesAsync(Db, entity);

            entity.Id = 0;
            entity.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
            entity.Status = DocumentStatus.Pending;
            entity.CreatedAt = DateTime.Now;
            if (change.HasSource)
            {
                if (lineage.IsUnresolvedLegacy)
                    SalesOrderSourceLineageRules.ApplyExplicitValues(entity, requestedQuotationId,
                        entity.SourceQuotationNo, requestedPiId, entity.SourcePiNo);
                else
                    SalesOrderSourceLineageRules.Apply(entity, lineage);
            }

            // 与 Update 对齐：明细金额由服务端按「数量 × 单价」重算（唯一权威口径，忽略客户端金额）
            SalesOrderAmountRules.ApplyDetailAmounts(entity);
            Calculate(entity);
            SalesOrderMutationRules.EnsureValidatedTerms(entity);
            Db.SalesOrders.Add(entity);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.OrderNo }, "销售订单创建成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackLineageWriteAsync(transaction);
            throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.ConcurrentMutationText);
        }
        catch
        {
            await RollbackLineageWriteAsync(transaction);
            throw;
        }
    }

    /// <summary>回滚来源血缘写入事务并丢弃变更跟踪器中的半成品写入（内存库无事务时同样清理）。</summary>
    private async Task RollbackLineageWriteAsync(IDbContextTransaction? transaction)
    {
        if (transaction is not null) await transaction.RollbackAsync();
        ProformaInvoiceMutationRules.DiscardTrackedChanges(Db);
    }

    /// <summary>
    /// 更新（ERP-401 / ERP-420）：先复核**规范写入授权**（实时身份 + 既有「销售订单」菜单 + 权威客户范围）、
    /// 持久化订单归属（先于读取明细 / 暴露状态）与拟议客户范围（改派）；随后<b>无论手工 / 历史 / 已解析来源</b>
    /// 都在同一原子事务内按「报价单 → PI → 销售订单」确定性锁序加锁（手工 / 无法解析历史来源只取订单行锁），
    /// 锁内重读持久化来源 / 目标状态与实时权限并复核来源血缘（历史来源绝不静默清除；显式改绑需完整实时复核且
    /// 下游已有证据时冻结），最后才改写字段 / 明细 / 金额；加锁前读到的来源被并发改写时原子拒绝。
    /// <para>ERP-422：非法新写入条款在加锁之前即被拒绝；改写后的持久化条款在锁内再执行一次唯一权威校验，
    /// 失败时原始表头 / 明细 / 状态与下游证据保持不变。</para>
    /// <para>ERP-423：拟议客户 / 商品 / 可选业务员 / 可选目的港与既有有效单位口径在同一原子事务内、字段 / 明细
    /// 赋值之前复核；引用失效（不存在 / 已删除 / 已停用 / 单位不支持）时原子拒绝，原始表头 / 明细 / 状态不变。</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SalesOrder entity)
    {
        // ERP-420：持久化订单归属与拟议客户范围先于读取明细 / 暴露状态与任何写入；不做进程内豁免。
        var writeScope = await EnsureCanonicalWriteAuthorizedAsync();
        await EnsurePersistedOrderAllowedAsync(writeScope, id);
        SalesOrderMutationAuthorizationRules.EnsureProposedCustomerAllowed(writeScope, entity.CustomerId);

        var requestedQuotationId = SalesOrderSourceLineageRules.NormalizeId(entity.SourceQuotationId);
        var requestedPiId = SalesOrderSourceLineageRules.NormalizeId(entity.SourcePiId);

        IDbContextTransaction? transaction = null;
        try
        {
            // ERP-421：无论手工 / 历史 / 已解析来源，修改都在同一原子事务内先取确定性行锁。
            transaction = await SalesOrderMutationRules.BeginMutationTransactionAsync(Db);

            // ERP-422：新写入条款校验先于来源加锁与任何字段 / 明细改写；失败整体回滚、不留半成品变更。
            SalesOrderMutationRules.EnsureValidatedTerms(entity);

            var preliminaryRead = await Db.SalesOrders.AsNoTracking()
                .Where(o => o.Id == id && !o.IsDeleted)
                .Select(o => new { o.CustomerId, o.Status, o.SourceQuotationId, o.SourcePiId })
                .FirstOrDefaultAsync()
                ?? throw BusinessException.NotFound("销售订单不存在");
            SalesOrderMutationRules.EnsureEditable(preliminaryRead.Status);

            var preliminary = SalesOrderSourceLineageRules.ResolveChange(preliminaryRead.SourceQuotationId,
                preliminaryRead.SourcePiId, requestedQuotationId, requestedPiId);

            // 需要加锁的实时来源行（无法解析的历史值 / 手工订单不取来源锁，只取订单行锁）。
            var lockScope = preliminary.HasSource
                ? await SalesOrderMutationRules.ResolveLiveSourceLockScopeAsync(Db, preliminaryRead.CustomerId,
                    preliminary.QuotationId, preliminary.PiId)
                : SalesOrderMutationLockScope.None;

            // 显式改绑到可精确解析来源时先复核下游冻结（未删除的变更申请 / 销售出库一律冻结改绑）。
            if (preliminary.IsExplicitChange && lockScope.IsLiveSource
                && (preliminaryRead.SourceQuotationId is > 0 || preliminaryRead.SourcePiId is > 0))
                await SalesOrderSourceLineageRules.EnsureRebindNotFrozenAsync(Db, id);

            // 确定性锁序：报价单来源行 → PI 来源行 → 销售订单目标行（绝不反向获取下游锁）。
            if (!await SalesOrderSourceLineageRules.LockSourcesAsync(Db, lockScope.QuotationId, lockScope.PiId))
                throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.SourceNotFoundText);
            if (!await SalesOrderSourceLineageRules.LockSalesOrderRowAsync(Db, id))
                throw BusinessException.NotFound("销售订单不存在");

            // 锁内重新读取实时权限（身份 / 菜单 / 客户范围）与持久化归属，授权撤销 / 客户改派立即收敛。
            var lockedScope = await EnsureCanonicalWriteAuthorizedAsync();
            await EnsurePersistedOrderAllowedAsync(lockedScope, id);
            SalesOrderMutationAuthorizationRules.EnsureProposedCustomerAllowed(lockedScope, entity.CustomerId);

            // 锁内重读持久化表头 / 明细 / 状态 / 来源：并发方已提交的结果以此为准，绝不按陈旧状态放行。
            var existing = await Db.SalesOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("销售订单不存在");
            SalesOrderMutationRules.EnsureEditable(GetStatus(existing));
            if (!SalesOrderMutationRules.PersistedSourceUnchanged(preliminaryRead.SourceQuotationId,
                    preliminaryRead.SourcePiId, existing.SourceQuotationId, existing.SourcePiId))
                throw BusinessException.RuleConflict(SalesOrderMutationRules.SourceChangedUnderLockText);

            var change = SalesOrderSourceLineageRules.ResolveChange(existing.SourceQuotationId,
                existing.SourcePiId, requestedQuotationId, requestedPiId);
            var lineage = change.HasSource
                ? await SalesOrderSourceLineageRules.ResolveAsync(Db, change, entity.CustomerId)
                : new SalesOrderSourceLineage();

            if (change.HasSource)
            {
                if (lineage.IsUnresolvedLegacy)
                {
                    // 存在历史链接时禁止改绑到无法解析的来源（绝不静默放弃历史来源）。
                    var persistedLinkExists = existing.SourceQuotationId is > 0 || existing.SourcePiId is > 0;
                    if (change.IsExplicitChange && persistedLinkExists)
                        throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.RebindUnknownSourceText);
                }
                else
                {
                    // 本次加锁的来源范围必须覆盖锁内重新解析出的实时来源（来源已过期时绝不改写到未加锁的来源）。
                    if (!SalesOrderMutationRules.ScopeCovers(lockScope, lineage.Quotation?.Id,
                            lineage.ProformaInvoice?.Id))
                        throw BusinessException.RuleConflict(SalesOrderMutationRules.SourceChangedUnderLockText);

                    if (change.IsExplicitChange)
                    {
                        await SalesOrderSourceLineageRules.EnsureWriteAuthorizedAsync(
                            Db, CurrentUserId(), lineage, entity.CustomerId);
                        await SalesOrderSourceLineageRules.EnsureNewLinkEligibleAsync(Db, lineage, id);
                    }
                    else
                    {
                        // 历史链接未改动（或请求未给出 Id 的清空尝试）：只做持久化来源重查，绝不静默清除 / 改写。
                        await SalesOrderSourceLineageRules.EnsurePersistedSourceIntactAsync(Db, existing);
                    }
                }
            }

            // ERP-423：来源血缘 / 转换资格复核通过后，再于字段 / 明细赋值之前复核实时主数据引用
            // （客户 / 商品 / 可选业务员 / 可选目的港 / 既有有效单位口径）；失败即整体回滚、不留半成品变更。
            await SalesOrderMasterReferenceRules.EnsureMasterReferencesAsync(Db, entity);

            // 来源字段：权威血缘优先；显式历史值原样保留；未链接时保持调用方提交的自由文本（不构成链接）。
            if (change.HasSource && !lineage.IsUnresolvedLegacy)
            {
                SalesOrderSourceLineageRules.Apply(existing, lineage);
            }
            else
            {
                // 未携带可解析来源 Id：以提交前**最新持久化来源**为准（并发登记的来源绝不被静默清除）。
                var latest = await Db.SalesOrders.AsNoTracking()
                    .Where(o => o.Id == id)
                    .Select(o => new { o.SourceQuotationId, o.SourceQuotationNo, o.SourcePiId, o.SourcePiNo })
                    .FirstOrDefaultAsync();
                if (latest is not null && (latest.SourceQuotationId is > 0 || latest.SourcePiId is > 0))
                {
                    SalesOrderSourceLineageRules.ApplyExplicitValues(existing, latest.SourceQuotationId,
                        latest.SourceQuotationNo, latest.SourcePiId, latest.SourcePiNo);
                }
                else
                {
                    SalesOrderSourceLineageRules.ApplyExplicitValues(existing, change.QuotationId,
                        change.IsClearingAttempt ? existing.SourceQuotationNo : entity.SourceQuotationNo,
                        change.PiId,
                        change.IsClearingAttempt ? existing.SourcePiNo : entity.SourcePiNo);
                }
            }

            existing.OrderDate = entity.OrderDate;
            existing.CustomerId = entity.CustomerId;
            existing.SalesmanId = entity.SalesmanId;
            existing.Currency = entity.Currency;
            existing.ExchangeRate = entity.ExchangeRate;
            existing.DepositRatio = entity.DepositRatio;
            existing.PaymentTerms = entity.PaymentTerms;
            existing.DeliveryDate = entity.DeliveryDate;
            existing.ShippingMethod = entity.ShippingMethod;
            existing.PortId = entity.PortId;
            existing.Remark = entity.Remark;

            // 外贸合同与运输信息（ERP-008）
            existing.CustomerPoNo = entity.CustomerPoNo;
            existing.ContractNo = entity.ContractNo;
            existing.TradeTerms = entity.TradeTerms;
            existing.DestinationPort = entity.DestinationPort;
            existing.Consignee = entity.Consignee;
            existing.NotifyParty = entity.NotifyParty;
            existing.ShippingMarks = entity.ShippingMarks;
            // 来源追溯（报价单 / PI → 销售订单）由 ERP-401 血缘护栏按权威解析结果写入，绝不照抄提交文本。
            existing.ExportMode = entity.ExportMode;
            existing.CommissionRatio = entity.CommissionRatio;
            existing.BusinessNature = entity.BusinessNature;
            existing.SplitShipment = entity.SplitShipment;
            existing.InspectionRequirement = entity.InspectionRequirement;
            existing.PackagingRequirement = entity.PackagingRequirement;

            Db.SalesOrderDetails.RemoveRange(existing.Details);
            foreach (var d in entity.Details)
            {
                d.Id = 0;
                d.SalesOrderId = id;
                d.CreatedAt = DateTime.Now;
            }
            // 明细金额与合计一律由服务端按唯一权威口径重算（ERP-047：SalesOrderAmountRules）
            SalesOrderAmountRules.ApplyDetailAmounts(entity);
            existing.Details = entity.Details;
            Calculate(existing);
            // ERP-422：锁内对改写后的持久化条款再执行一次唯一权威校验，之后才允许提交事务。
            SalesOrderMutationRules.EnsureValidatedTerms(existing);
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "销售订单更新成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackLineageWriteAsync(transaction);
            throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.ConcurrentMutationText);
        }
        catch
        {
            await RollbackLineageWriteAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 提交（ERP-401 / ERP-420）：先复核**规范写入授权**（实时身份 + 既有「销售订单」菜单 + 权威客户范围）与
    /// 持久化订单归属（先于暴露状态）；未链接的手工订单保持既有流转；已登记来源血缘的订单先做持久化来源重查
    /// （来源必须仍可精确解析、同一来源不得存在其它目标），再在订单行锁 + 原子事务内流转状态。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
        => await RunLineageGuardedStatusChangeAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "提交成功");

    /// <summary>
    /// 审核（ERP-401 / ERP-420）：与 <see cref="Submit"/> 同一口径（同样先复核实时写入授权与持久化归属），
    /// 在订单行锁 + 原子事务内复核持久化来源血缘后审核。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
        => await RunLineageGuardedStatusChangeAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "审核通过");

    /// <summary>
    /// 带来源血缘复核的状态流转（ERP-421）：先复核**规范写入授权**与持久化订单归属（缺失 / 非法 / 已禁用身份、
    /// 缺菜单或范围外订单一律 fail closed，且先于读取明细 / 暴露状态）；随后<b>无论手工 / 历史 / 已解析来源</b>
    /// 都进入同一原子事务并按「报价单 → PI → 销售订单行」确定性锁序加锁（手工 / 无法解析的历史来源只取订单行锁），
    /// 锁内重新读取实时权限、持久化状态与来源后复核合法流转与持久化来源血缘，任一步失败整体回滚并丢弃半成品变更。
    /// <para>ERP-422：提交 / 审核除状态与来源血缘外，还在<b>同一把订单行锁内</b>复核已持久化条款（数量 / 单价 /
    /// 币种 / 汇率 / 比例 / 金额精度）；非法存储条款原子拒绝、状态与原始证据均不变。</para>
    /// <para>ERP-423：提交 / 审核还在<b>同一把订单行锁内</b>重查实时主数据引用（客户 / 商品 / 可选业务员 /
    /// 可选目的港 / 既有有效单位口径），来源失效时原子拒绝，绝不让无效引用提交运营需求。</para>
    /// </summary>
    private async Task<IActionResult> RunLineageGuardedStatusChangeAsync(long id, DocumentStatus from,
        DocumentStatus to, string message)
    {
        // ERP-420：写入授权 + 持久化归属先于读取明细 / 暴露状态与任何状态变更；不做进程内豁免。
        var writeScope = await EnsureCanonicalWriteAuthorizedAsync();
        await EnsurePersistedOrderAllowedAsync(writeScope, id);

        var preliminary = await Db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.CustomerId, o.Status, o.SourceQuotationId, o.SourcePiId })
            .FirstOrDefaultAsync()
            ?? throw BusinessException.NotFound("销售订单不存在");
        SalesOrderMutationRules.EnsureTransitionAllowed(preliminary.Status, from, to);

        IDbContextTransaction? transaction = null;
        try
        {
            // ERP-421：无论手工 / 历史 / 已解析来源，状态流转都在同一原子事务内先取确定性行锁。
            transaction = await SalesOrderMutationRules.BeginMutationTransactionAsync(Db);

            // 已解析实时来源按「报价单 → PI → 订单行」加锁；手工 / 无法解析的历史来源只取订单行锁。
            var lockScope = await SalesOrderMutationRules.TryResolveLiveSourceLockScopeAsync(Db,
                preliminary.CustomerId, preliminary.SourceQuotationId, preliminary.SourcePiId);
            if (!await SalesOrderSourceLineageRules.LockSourcesAsync(Db, lockScope.QuotationId, lockScope.PiId))
                throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.SourceNotFoundText);
            if (!await SalesOrderSourceLineageRules.LockSalesOrderRowAsync(Db, id))
                throw BusinessException.NotFound("销售订单不存在");

            // ERP-420：在已持有的订单行锁内重新读取实时权限与持久化归属（授权撤销 / 账号停用立即收敛）。
            var lockedScope = await EnsureCanonicalWriteAuthorizedAsync();
            await EnsurePersistedOrderAllowedAsync(lockedScope, id);

            // 锁内权威重读状态与来源：并发提交 / 审核 / 取消 / 改绑后的过期结果一律拒绝，绝不按陈旧状态放行。
            var locked = await Db.SalesOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("销售订单不存在");
            SalesOrderMutationRules.EnsureTransitionAllowed(GetStatus(locked), from, to);
            if (!SalesOrderMutationRules.PersistedSourceUnchanged(preliminary.SourceQuotationId,
                    preliminary.SourcePiId, locked.SourceQuotationId, locked.SourcePiId))
                throw BusinessException.RuleConflict(SalesOrderMutationRules.SourceChangedUnderLockText);

            await SalesOrderSourceLineageRules.EnsurePersistedSourceIntactAsync(Db, locked);
            // ERP-422：提交 / 审核在既有订单行锁内复核已持久化条款；非法存储条款原子拒绝、状态不变。
            SalesOrderMutationRules.EnsureValidatedTerms(locked);
            // ERP-423：提交 / 审核在同一把订单行锁内重查实时主数据引用，来源失效（客户 / 商品 / 可选业务员 /
            // 可选目的港被删除 / 停用或单位口径失效）一律原子拒绝，绝不让无效引用提交运营需求。
            await SalesOrderMasterReferenceRules.EnsureMasterReferencesAsync(Db, locked);
            SetStatus(locked, to);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, message));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackLineageWriteAsync(transaction);
            throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.ConcurrentMutationText);
        }
        catch
        {
            await RollbackLineageWriteAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 取消销售订单（ERP-347 / ERP-369 / ERP-420）：在可串行化事务内对订单行加更新锁，**锁内复核规范写入授权**
    /// （实时身份 + 既有「销售订单」菜单 + 权威客户范围）与持久化订单归属，把「拒绝判定」与「状态变更」做成一个原子步骤，
    /// 并与同单出库审核（ERP-343）、采购归属关联（ERP-346）、收款引用登记（ERP-053）以及预装柜需求证据链接的
    /// 提交 / 审核 / 取消（ERP-368 / ERP-369）串行化——并发场景下「履约 / 需求承诺生效」与「来源取消」不可能同时成功。
    /// <para>锁语句与预装柜审核 / 取消共用同一把上游订单行锁（<see cref="PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql"/>），
    /// 确定性锁序「上游销售订单行 → 订柜信息行 → 预装柜单行」保持不变（取消只取订单行锁，绝不反向获取下游锁）。</para>
    /// <para>只把状态改为已取消，<strong>不改动</strong>订单明细 / 金额 / 客户 / 币种等原始字段；
    /// 存在已审核且未冲销的出库、未删除且未取消的采购履约、有效客户收款引用证据，或未删除且已审核的预装柜
    /// 需求计划证据（来源明细链接）时拒绝，且本方法<strong>绝不</strong>静默取消采购 / 预装柜、冲销库存或财务，
    /// 冲销 / 作废 / 解除链接只走既有显式工作流。</para>
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireOrderCancellationLockAsync(id);

            // ERP-420：锁内复核规范写入授权（实时启用 / 未删除身份 + 既有菜单 + 权威客户范围）与持久化订单归属；
            // 写入路径不做进程内豁免。既有取消护栏（ValidateCancellationAsync）保持不变，继续在原位执行。
            var writeScope = await EnsureCanonicalWriteAuthorizedAsync();
            await EnsurePersistedOrderAllowedAsync(writeScope, id);

            var entity = await Db.SalesOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("销售订单不存在");

            await SalesOrderCancellationRules.ValidateCancellationAsync(Db, entity, CurrentUserId());

            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "已取消"));
    }

    /// <summary>
    /// 对销售订单行加更新锁（UPDLOCK, HOLDLOCK），把同单并发「出库审核 / 取消」以及「预装柜需求证据审核 / 取消」
    /// 串行化在同一事务内；锁语句与 ERP-368 预装柜流程共用同一常量（同一把来源行锁，确定性锁序起点）；
    /// 非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquireOrderCancellationLockAsync(long orderId)
    {
        if (!Db.Database.IsRelational()) return;
        await Db.Database
            .SqlQueryRaw<long>(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, orderId)
            .ToListAsync();
    }

    /// <summary>
    /// 删除（ERP-420 / ERP-421）：先复核**规范写入授权**（实时身份 + 既有「销售订单」菜单 + 权威客户范围）与持久化
    /// 订单归属（范围外 / 已删除 / 不存在返回同一非披露错误）；随后**无论手工 / 历史 / 已解析来源**都进入同一原子事务，
    /// 按「报价单 → PI → 销售订单行」确定性锁序加锁（手工 / 无法解析的历史来源只取订单行锁），锁内重新读取实时权限、
    /// 持久化状态与来源后复核「仅待提交可软删除」，任一步失败整体回滚并丢弃半成品变更；绝不物理删除或改写下游证据。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var writeScope = await EnsureCanonicalWriteAuthorizedAsync();
        await EnsurePersistedOrderAllowedAsync(writeScope, id);

        var preliminary = await Db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == id && !o.IsDeleted)
            .Select(o => new { o.CustomerId, o.Status, o.SourceQuotationId, o.SourcePiId })
            .FirstOrDefaultAsync()
            ?? throw BusinessException.NotFound("销售订单不存在");
        SalesOrderMutationRules.EnsureDeletable(preliminary.Status);

        IDbContextTransaction? transaction = null;
        try
        {
            // ERP-421：无论手工 / 历史 / 已解析来源，软删除都在同一原子事务内先取确定性行锁。
            transaction = await SalesOrderMutationRules.BeginMutationTransactionAsync(Db);

            var lockScope = await SalesOrderMutationRules.TryResolveLiveSourceLockScopeAsync(Db,
                preliminary.CustomerId, preliminary.SourceQuotationId, preliminary.SourcePiId);
            if (!await SalesOrderSourceLineageRules.LockSourcesAsync(Db, lockScope.QuotationId, lockScope.PiId))
                throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.SourceNotFoundText);
            if (!await SalesOrderSourceLineageRules.LockSalesOrderRowAsync(Db, id))
                throw BusinessException.NotFound("销售订单不存在");

            // 锁内重新读取实时权限与持久化归属（授权撤销 / 账号停用立即收敛）。
            var lockedScope = await EnsureCanonicalWriteAuthorizedAsync();
            await EnsurePersistedOrderAllowedAsync(lockedScope, id);

            // 锁内权威重读状态与来源：并发提交 / 审核 / 取消后的过期删除一律拒绝，已删除 / 已取消记录不可复活。
            var locked = await Db.SalesOrders.FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("销售订单不存在");
            SalesOrderMutationRules.EnsureDeletable(GetStatus(locked));
            if (!SalesOrderMutationRules.PersistedSourceUnchanged(preliminary.SourceQuotationId,
                    preliminary.SourcePiId, locked.SourceQuotationId, locked.SourcePiId))
                throw BusinessException.RuleConflict(SalesOrderMutationRules.SourceChangedUnderLockText);

            locked.IsDeleted = true;
            locked.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackLineageWriteAsync(transaction);
            throw BusinessException.RuleConflict(SalesOrderSourceLineageRules.ConcurrentMutationText);
        }
        catch
        {
            await RollbackLineageWriteAsync(transaction);
            throw;
        }
    }


    /// <summary>
    /// 打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/sales-order 提供）
    /// <para>ERP-424：先实时授权（实时身份 + 既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 + 权威客户范围），
    /// 再按**持久化 CustomerId** 复核归属（范围外 / 已删除 / 不存在同一非披露错误），最后才装载主表与**未删除**明细。</para>
    /// </summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var scope = await EnsureDocumentOutputAuthorizedAsync();
        await EnsureDocumentOutputOrderAllowedAsync(scope, id);

        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound(SalesOrderDocumentOutputAuthorizationRules.NotFoundText);
        entity.Details = entity.Details.Where(d => !d.IsDeleted).ToList();
        return Ok(ApiResponse<SalesOrder>.Success(entity));
    }

    /// <summary>
    /// 导出（JSON 单据）
    /// <para>ERP-424：先实时授权，并把权威客户范围**下推到数据库**（先于日期过滤与任何物化）；
    /// 明细只保留**未删除**行（与打印同口径），拒绝时绝不返回任何订单号 / 计数 / 明细。</para>
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var scope = await EnsureDocumentOutputAuthorizedAsync();
        var source = SalesOrderDocumentOutputAuthorizationRules.ApplyCustomerScope(
            Db.SalesOrders.AsNoTracking().Include(o => o.Details).Where(o => !o.IsDeleted), scope);
        if (start.HasValue) source = source.Where(o => o.OrderDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.OrderDate <= end.Value);
        var items = await source.OrderByDescending(o => o.Id).ToListAsync();
        foreach (var item in items)
            item.Details = item.Details.Where(d => !d.IsDeleted).ToList();
        return Ok(ApiResponse<List<SalesOrder>>.Success(items));
    }

    /// <summary>
    /// 带入单证预填（ERP-019；ERP-052 增加来源明细行快照预览）：按销售订单返回**未落库**的单证草稿
    /// （商业发票 / 装箱单 / 报关单 / 产地证 / 提单）+ 由订单明细构造的明细行预览，
    /// 并回传该订单已生成过的单证类型（前端置灰，避免重复生成）。不写库、不占用单证编号流水。
    /// <para>明细行只取订单明细的权威值（商品 / 规格 / 数量 / 单位 / 单价），箱数与重量留空（订单无此证据）；
    /// 订单明细非法 / 超过有界行数时明确拒绝，不静默丢弃、不臆造数值。</para>
    /// </summary>
    [HttpGet("{id:long}/trade-documents/prefill")]
    [TradeDocumentRequestAuthorizationFilter]
    public async Task<IActionResult> TradeDocumentPrefill(long id)
    {
        var scope = TradeDocumentRequestAuthorizationFilter.ScopeFrom(HttpContext);
        var order = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, default)
            ?? throw BusinessException.NotFound("销售订单不存在");
        // ERP-394：受限账号的来源客户必须在单证中心权威范围内（fail closed，不通过预填泄露范围外客户）。
        TradeDocumentAuthorizationRules.EnsureSourceScopeAllowed(scope, order.CustomerId);

        var customer = await TradeDocumentGeneration.LoadCustomerAsync(Db, order.CustomerId);
        var drafts = TradeDocumentGeneration.SalesOrderDocTypes
            .Select(docType => TradeDocumentGeneration.BuildFromSalesOrder(order, customer, docType))
            .ToList();

        // 来源明细与商品资料各**一次**有界查询（不逐行查库），供明细行快照预览使用
        var details = await TradeDocumentLineSnapshotRules.LoadSalesOrderDetailsAsync(Db, order.Id);
        var products = await TradeDocumentLineSnapshotRules.LoadProductsAsync(
            Db, TradeDocumentLineSnapshotRules.ProductIdsOf(details));

        var result = await TradeDocumentGeneration.PrefillAsync(Db,
            TradeDocumentGeneration.SalesOrderSourceType, order.Id, order.OrderNo,
            containerNo: null, salesOrderNo: order.OrderNo, loadingListNo: null, drafts: drafts,
            buildLines: docType => TradeDocumentLineSnapshotRules.BuildFromSalesOrder(
                order, details, products, docType, DraftCurrencyOf(drafts, docType)),
            targetScope: scope);

        return Ok(ApiResponse<TradeDocPrefillResult>.Success(result, "已按销售订单带入单证草稿与明细行快照预览"));
    }

    /// <summary>
    /// 生成单证（ERP-019；ERP-052 增加明细行快照；ERP-397 原子化）：按销售订单生成单证中心台账记录（默认商业发票 + 装箱单），
    /// 并在**同一事务**内写入由订单明细构造的行快照（商业发票含服务端计算行金额，装箱单不含价格口径）。
    /// <para>ERP-397：先取来源订单行锁并在锁内权威重读来源（状态 / 客户 / 金额 / 数量 / 单位），
    /// 再把「重复生成检测 → 单证编号预约 → 表头与明细行构造 → 写入」放在同一原子事务内；
    /// 已作废订单拒绝、来源明细非法 / 超限拒绝、同一订单 + 同一单证类型只允许一套完整单证
    /// （重复点击不会产生重复单证、失败整体回滚且不占用编号）；单证落库状态统一为「待制作」。</para>
    /// </summary>
    [HttpPost("{id:long}/trade-documents")]
    [TradeDocumentRequestAuthorizationFilter]
    public async Task<IActionResult> GenerateTradeDocuments(long id, [FromBody] TradeDocGenerateRequest? request)
    {
        var scope = TradeDocumentRequestAuthorizationFilter.ScopeFrom(HttpContext);

        // 生成前的有界预读（存在性 / 未作废 / 来源客户实时范围）：权威复核在来源行锁内再次执行（ERP-397）。
        var order = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, default)
            ?? throw BusinessException.NotFound("销售订单不存在");
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(TradeDocumentGenerationMutationRules.SourceCancelledText(
                TradeDocumentGeneration.SalesOrderSourceType));
        // ERP-394：受限账号的来源客户必须在单证中心权威范围内（fail closed，生成目标不得绕过目标授权）。
        TradeDocumentAuthorizationRules.EnsureSourceScopeAllowed(scope, order.CustomerId);

        // ERP-397：先取来源订单行锁（与取消 / 编辑 / 删除同一把锁）并在锁内权威重读来源，
        // 再在同一原子事务内完成重复检测、编号预约、表头与明细行构造与写入。
        var result = await TradeDocumentGeneration.GenerateAtomicAsync(Db,
            TradeDocumentGeneration.SalesOrderSourceType, order.Id, request?.DocTypes,
            reloadSource: ct => ReloadSalesOrderSourceAsync(order.Id, ct),
            targetScope: scope);

        var numbers = string.Join("、", result.Documents.Select(d => d.DocNo));
        return Ok(ApiResponse<TradeDocGenerateResult>.Success(result,
            $"已生成单证：{numbers}（明细行快照 {result.TotalLineCount} 行）"));
    }

    /// <summary>
    /// ERP-397：来源行锁内的**权威重读**（销售订单 → 客户档案 → 来源明细 → 商品资料），并绑定单证草稿 /
    /// 明细行快照构造；返回 <c>null</c> 表示订单已被并发删除（生成 fail closed）。
    /// <para>绝不重用加锁前的内存实体，也绝不按单号文本推断归属：表头客户 / 金额与明细行数量 / 单位
    /// 一律来自本次重读；锁内复核见 <see cref="TradeDocumentGenerationMutationRules"/>。</para>
    /// </summary>
    private async Task<TradeDocumentGenerationSource?> ReloadSalesOrderSourceAsync(
        long orderId, CancellationToken ct)
    {
        var order = await Db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted, ct);
        if (order is null) return null;

        var customer = await TradeDocumentGeneration.LoadCustomerAsync(Db, order.CustomerId, ct);
        var details = await TradeDocumentLineSnapshotRules.LoadSalesOrderDetailsAsync(Db, order.Id, ct);
        var products = await TradeDocumentLineSnapshotRules.LoadProductsAsync(
            Db, TradeDocumentLineSnapshotRules.ProductIdsOf(details), ct);

        // 单证草稿与明细行快照共用同一份映射：币种取自草稿，保证行快照与单证台账币种一致
        var drafts = TradeDocumentGeneration.SalesOrderDocTypes
            .Select(docType => TradeDocumentGeneration.BuildFromSalesOrder(order, customer, docType))
            .ToList();

        return new TradeDocumentGenerationSource
        {
            SourceType = TradeDocumentGeneration.SalesOrderSourceType,
            SourceId = order.Id,
            SourceNo = order.OrderNo,
            SalesOrderNo = order.OrderNo,
            ContainerNo = null,
            LoadingListNo = null,
            CustomerId = order.CustomerId,
            Status = order.Status,
            ExpectedAmount = order.TotalAmount,
            ExpectedLineQuantityTotal =
                TradeDocumentGenerationMutationRules.LineQuantityTotal(details.Select(d => d.Quantity)),
            AuthoritativeUnits = TradeDocumentGenerationMutationRules.LineUnits(
                details.Select(d => (d.ProductId, (string?)d.Unit)), products),
            BuildDraft = docType => TradeDocumentGeneration.BuildFromSalesOrder(order, customer, docType),
            BuildLines = docType => TradeDocumentLineSnapshotRules.BuildFromSalesOrder(
                order, details, products, docType, DraftCurrencyOf(drafts, docType)),
        };
    }

    /// <summary>取某单证类型的草稿币种（明细行快照与单证台账币种保持同一口径；缺失时回退空值由规则层规范化）</summary>
    private static string? DraftCurrencyOf(IEnumerable<TradeDocument> drafts, string docType)
        => drafts.FirstOrDefault(d => string.Equals(d.DocType, docType, StringComparison.Ordinal))?.Currency;

    /// <summary>导出列定义（含 ERP-008 外贸合同与追溯字段；Excel 导出菜单「销售订单导出」使用）</summary>
    private static readonly List<(string Key, string Title)> ExcelColumns = new()
    {
        ("OrderNo", "订单号"), ("OrderDate", "订单日期"), ("CustomerId", "客户Id"), ("SalesmanId", "业务员Id"),
        ("CustomerPoNo", "客户PO号"), ("ContractNo", "合同号"), ("TradeTerms", "价格条款"),
        ("DestinationPort", "目的港"), ("Consignee", "收货人"), ("NotifyParty", "通知人"), ("ShippingMarks", "唛头"),
        ("SourceQuotationNo", "来源报价单号"), ("SourcePiNo", "来源PI号"),
        ("ExportMode", "出口方式"), ("BusinessNature", "业务性质"), ("CommissionRatio", "佣金比例%"),
        ("SplitShipment", "分批出货"), ("InspectionRequirement", "验货要求"), ("PackagingRequirement", "包装要求"),
        ("Currency", "币种"), ("ExchangeRate", "汇率"), ("TotalAmount", "订单总额"),
        ("DepositRatio", "定金比例%"), ("DepositAmount", "定金金额"),
        ("PaymentTerms", "付款条件"), ("DeliveryDate", "交货日期"), ("ShippingMethod", "运输方式"),
        ("Status", "状态"), ("Remark", "备注"),
    };

    /// <summary>
    /// 导出销售订单为 Excel（含新增外贸合同与追溯字段）
    /// <para>ERP-424：先实时授权（实时身份 + 既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 + 权威客户范围），
    /// 并把客户范围**下推到数据库**（先于关键字 / 状态 / 日期过滤与任何物化）；列与文件名保持不变。</para>
    /// </summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] string? keyword, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var scope = await EnsureDocumentOutputAuthorizedAsync();
        var source = SalesOrderDocumentOutputAuthorizationRules.ApplyCustomerScope(
            Db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted), scope);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.CustomerPoNo.Contains(kw) || o.ContractNo.Contains(kw));
        }
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.OrderDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.OrderDate <= end.Value);

        var orders = await source.OrderByDescending(o => o.Id).ToListAsync();
        var rows = orders.Select(o => new Dictionary<string, object?>
        {
            ["OrderNo"] = o.OrderNo, ["OrderDate"] = o.OrderDate, ["CustomerId"] = o.CustomerId,
            ["SalesmanId"] = o.SalesmanId, ["CustomerPoNo"] = o.CustomerPoNo, ["ContractNo"] = o.ContractNo,
            ["TradeTerms"] = o.TradeTerms, ["DestinationPort"] = o.DestinationPort, ["Consignee"] = o.Consignee,
            ["NotifyParty"] = o.NotifyParty, ["ShippingMarks"] = o.ShippingMarks,
            ["SourceQuotationNo"] = o.SourceQuotationNo, ["SourcePiNo"] = o.SourcePiNo,
            ["ExportMode"] = o.ExportMode, ["BusinessNature"] = o.BusinessNature,
            ["CommissionRatio"] = o.CommissionRatio, ["SplitShipment"] = o.SplitShipment,
            ["InspectionRequirement"] = o.InspectionRequirement, ["PackagingRequirement"] = o.PackagingRequirement,
            ["Currency"] = o.Currency.ToString(), ["ExchangeRate"] = o.ExchangeRate,
            ["TotalAmount"] = o.TotalAmount, ["DepositRatio"] = o.DepositRatio, ["DepositAmount"] = o.DepositAmount,
            ["PaymentTerms"] = o.PaymentTerms, ["DeliveryDate"] = o.DeliveryDate,
            ["ShippingMethod"] = o.ShippingMethod, ["Status"] = o.Status.ToString(), ["Remark"] = o.Remark,
        }).ToList();

        var bytes = ExcelExporter.ExportRows("SalesOrders", rows, ExcelColumns);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalesOrders_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 合计口径（销售订单唯一权威算法）：总额 = Σ 明细数量×单价，定金金额 = 总额 × 定金比例%。
    /// 算法实现已抽到 <see cref="SalesOrderAmountRules.Calculate"/>（ERP-047）：报价单 / PI 转销售订单
    /// （ERP-010）与销售订单变更申请登记（ERP-047）复用同一份实现，避免出现第二套金额口径。
    /// </summary>
    public static void Calculate(SalesOrder entity) => SalesOrderAmountRules.Calculate(entity);

    /// <summary>
    /// 业务字段校验（佣金比例 0~100；历史单据不填时为 0，不受影响）。
    /// 实现位于 <see cref="SalesOrderAmountRules.Validate"/>，与合计算法同源。
    /// </summary>
    public static void Validate(SalesOrder entity) => SalesOrderAmountRules.Validate(entity);
}
