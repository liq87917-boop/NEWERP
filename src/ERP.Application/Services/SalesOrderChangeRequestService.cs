using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单变更申请登记服务（ERP-047）。职责：
/// <list type="number">
/// <item><b>登记草稿</b>（<see cref="CreateDraftAsync"/>）：来源必须是存在且未删除、未作废的销售订单；
/// 服务端冻结来源主表与明细快照（含状态 / 最后更新时间 / 明细确定性签名），并生成申请单号；</item>
/// <item><b>编辑草稿</b>（<see cref="UpdateDraftAsync"/>）：仅草稿可编辑；拟议明细**按位置**与来源行对照
/// （前 N 行对应来源行，可标记移除；之后为新增行），来源快照列永不被覆盖；</item>
/// <item><b>金额与校验</b>：拟议明细金额、总额、定金金额一律由
/// <see cref="SalesOrderAmountRules"/>（销售订单唯一权威算法）重算，数量 / 单价 / 定金比例 / 佣金比例同口径校验，
/// 不接受客户端合计，也不引入第二套算法；</item>
/// <item><b>提交</b>（<see cref="SubmitAsync"/>）：冻结拟议快照并记录提交时间，之后不可编辑；
/// <b>取消</b>（<see cref="CancelAsync"/>）：必须填写原因，保留原始与拟议证据，绝不做硬删除；</item>
/// <item><b>读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> / <see cref="ListForSalesOrderAsync"/>）：
/// 分页 / 有界，明细与来源订单**批量装载**（无逐行查询），并显式提示「来源在快照之后是否已变化」，
/// <strong>不</strong>覆盖、<strong>不</strong>合并、<strong>不</strong>静默刷新拟议值。</item>
/// </list>
/// <para>边界（重要）：本服务<strong>不</strong>审核、<strong>不</strong>套用、<strong>不</strong>定义审批阈值，
/// 除本模块两张表外<strong>不</strong>写任何数据：来源销售订单（主表 / 明细）、报价单、销售出库、装柜与出运、
/// 收款与发票、佣金、库存与库存成本、单证中心与财务记录一律不变。</para>
/// </summary>
public static class SalesOrderChangeRequestService
{
    /// <summary>列表关键字长度上限（超长直接拒绝，避免全表无界模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>单一来源订单的申请清单条数上限（有界）</summary>
    public const int MaxPerSourceOrder = 200;

    /// <summary>来源销售订单候选单次返回上限（有界）</summary>
    public const int MaxSourceOptions = 200;

    // ==================== 1. 登记草稿 ====================

    /// <summary>
    /// 按来源销售订单登记一张变更申请草稿：冻结来源快照、按传入拟议值（缺省 = 来源值）生成拟议快照，
    /// 并由服务端按销售订单唯一权威算法重算金额。
    /// <para>本方法<strong>不</strong>改写来源销售订单，也不写任何下游记录。</para>
    /// </summary>
    public static async Task<SalesOrderChangeRequestDto> CreateDraftAsync(
        IErpDbContext db, IDocumentNumberService noService, SalesOrderChangeRequestSaveDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(noService);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.SalesOrderId <= 0)
            throw BusinessException.InvalidParameter("请选择要发起变更申请的销售订单");

        var order = await LoadSourceOrderAsync(db, dto.SalesOrderId, ct)
            ?? throw BusinessException.NotFound(
                $"销售订单（Id={dto.SalesOrderId}）不存在或已删除：只能对存在且未删除的销售订单登记变更申请");
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废（已取消）的销售订单不能登记变更申请");

        var reason = SalesOrderChangeRequestRules.NormalizeReason(dto.Reason);
        var requestNo = await noService.GenerateAsync(DocumentType.SalesOrderChangeRequest);

        var entity = new SalesOrderChangeRequest
        {
            RequestNo = requestNo,
            SalesOrderId = order.Id,
            Reason = reason,
            Status = SalesOrderChangeRequestRules.StatusDraft,
            CreatedAt = DateTime.Now
        };

        CaptureSourceSnapshot(entity, order);
        ApplyProposal(entity, dto);

        db.SalesOrderChangeRequests.Add(entity);
        await db.SaveChangesAsync(ct);

        return await MapAsync(db, entity, ct);
    }

    // ==================== 2. 编辑草稿 ====================

    /// <summary>
    /// 编辑草稿的拟议值（含拟议明细整体替换）：仅草稿可编辑；来源快照列保持不变；
    /// 旧的「拟议新增行」按软删除留痕处理，来源行永远保留。
    /// </summary>
    public static async Task<SalesOrderChangeRequestDto> UpdateDraftAsync(
        IErpDbContext db, long id, SalesOrderChangeRequestSaveDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var entity = await LoadAsync(db, id, includeDetails: true, ct);
        SalesOrderChangeRequestRules.EnsureDraftEditable(entity.Status);

        if (dto.SalesOrderId > 0 && dto.SalesOrderId != entity.SalesOrderId)
            throw BusinessException.InvalidParameter(
                "不能把变更申请改挂到另一张销售订单（如需更换来源，请取消本申请后重新登记）");

        /* 原因先校验后写入：与拟议值一样，校验失败不给实体留下脏状态 */
        var reason = SalesOrderChangeRequestRules.NormalizeReason(dto.Reason);
        ApplyProposal(entity, dto);
        entity.Reason = reason;

        entity.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        return await MapAsync(db, entity, ct);
    }

    // ==================== 3. 提交 / 取消 ====================

    /// <summary>
    /// 提交变更申请：冻结拟议快照并记录提交时间；提交后不可编辑（本模块不提供「重新提交」）。
    /// <para>提交<strong>不</strong>代表批准、<strong>不</strong>代表套用，也<strong>不</strong>改写来源销售订单。</para>
    /// </summary>
    public static async Task<SalesOrderChangeRequestDto> SubmitAsync(
        IErpDbContext db, long id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entity = await LoadAsync(db, id, includeDetails: true, ct);
        SalesOrderChangeRequestRules.EnsureSubmittable(entity.Status);

        var now = DateTime.Now;
        entity.Status = SalesOrderChangeRequestRules.StatusSubmitted;
        entity.SubmittedAt = now;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return await MapAsync(db, entity, ct);
    }

    /// <summary>
    /// 取消变更申请（草稿与已提交都可取消，必须填写原因）：保留原始与拟议证据，不做硬删除。
    /// </summary>
    public static async Task<SalesOrderChangeRequestDto> CancelAsync(
        IErpDbContext db, long id, string? reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entity = await LoadAsync(db, id, includeDetails: true, ct);
        SalesOrderChangeRequestRules.EnsureCancellable(entity.Status);

        var normalized = SalesOrderChangeRequestRules.NormalizeCancelReason(reason);
        var now = DateTime.Now;
        entity.Status = SalesOrderChangeRequestRules.StatusCancelled;
        entity.CancelledAt = now;
        entity.CancelledReason = normalized;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return await MapAsync(db, entity, ct);
    }

    // ==================== 4. 来源快照（服务端权威写入，冻结后不可改写） ====================

    /// <summary>
    /// 冻结来源销售订单快照：主表值、状态、最后更新时间（<c>UpdatedAt ?? CreatedAt</c>）、
    /// 明细确定性签名、快照标记与逐行来源明细；同时把拟议值初始化为「与来源完全一致」（原样留痕基线）。
    /// <para>本方法只读取来源订单，绝不写回任何来源字段。</para>
    /// </summary>
    private static void CaptureSourceSnapshot(SalesOrderChangeRequest entity, SalesOrder order)
    {
        var sourceLines = order.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).ToList();
        var sourceUpdatedAt = order.UpdatedAt ?? order.CreatedAt;

        entity.SalesOrderNo = order.OrderNo ?? string.Empty;
        entity.SourceStatus = (int)order.Status;
        entity.SourceUpdatedAt = sourceUpdatedAt;
        entity.SourceDetailSignature = SalesOrderChangeRequestRules.BuildDetailSignature(sourceLines);
        entity.SourceSnapshotMarker = SalesOrderChangeRequestRules.BuildSnapshotMarker(
            order.OrderNo, entity.SourceStatus, sourceUpdatedAt, sourceLines.Count, order.TotalAmount);

        entity.SourceOrderDate = order.OrderDate;
        entity.SourceCustomerId = order.CustomerId;
        entity.SourceSalesmanId = order.SalesmanId;
        entity.SourceCurrency = order.Currency;
        entity.SourceExchangeRate = order.ExchangeRate;
        entity.SourceTotalAmount = order.TotalAmount;
        entity.SourceDepositRatio = order.DepositRatio;
        entity.SourceDepositAmount = order.DepositAmount;
        entity.SourcePaymentTerms = order.PaymentTerms ?? string.Empty;
        entity.SourceDeliveryDate = order.DeliveryDate;
        entity.SourceShippingMethod = order.ShippingMethod ?? string.Empty;
        entity.SourcePortId = order.PortId;
        entity.SourceRemark = order.Remark ?? string.Empty;
        entity.SourceCustomerPoNo = order.CustomerPoNo ?? string.Empty;
        entity.SourceContractNo = order.ContractNo ?? string.Empty;
        entity.SourceTradeTerms = order.TradeTerms ?? string.Empty;
        entity.SourceDestinationPort = order.DestinationPort ?? string.Empty;
        entity.SourceConsignee = order.Consignee ?? string.Empty;
        entity.SourceNotifyParty = order.NotifyParty ?? string.Empty;
        entity.SourceShippingMarks = order.ShippingMarks ?? string.Empty;
        entity.SourceQuotationNoSnapshot = order.SourceQuotationNo ?? string.Empty;
        entity.SourcePiNoSnapshot = order.SourcePiNo ?? string.Empty;
        entity.SourceExportMode = order.ExportMode ?? string.Empty;
        entity.SourceCommissionRatio = order.CommissionRatio;
        entity.SourceBusinessNature = order.BusinessNature ?? string.Empty;
        entity.SourceSplitShipment = order.SplitShipment;
        entity.SourceInspectionRequirement = order.InspectionRequirement ?? string.Empty;
        entity.SourcePackagingRequirement = order.PackagingRequirement ?? string.Empty;

        /* 拟议基线 = 来源快照：不传拟议值即为「原样留痕」，不产生任何隐性修改 */
        entity.ProposedOrderDate = entity.SourceOrderDate;
        entity.ProposedCustomerId = entity.SourceCustomerId;
        entity.ProposedSalesmanId = entity.SourceSalesmanId;
        entity.ProposedCurrency = entity.SourceCurrency;
        entity.ProposedExchangeRate = entity.SourceExchangeRate;
        entity.ProposedDepositRatio = entity.SourceDepositRatio;
        entity.ProposedPaymentTerms = entity.SourcePaymentTerms;
        entity.ProposedDeliveryDate = entity.SourceDeliveryDate;
        entity.ProposedShippingMethod = entity.SourceShippingMethod;
        entity.ProposedPortId = entity.SourcePortId;
        entity.ProposedRemark = entity.SourceRemark;
        entity.ProposedCustomerPoNo = entity.SourceCustomerPoNo;
        entity.ProposedContractNo = entity.SourceContractNo;
        entity.ProposedTradeTerms = entity.SourceTradeTerms;
        entity.ProposedDestinationPort = entity.SourceDestinationPort;
        entity.ProposedConsignee = entity.SourceConsignee;
        entity.ProposedNotifyParty = entity.SourceNotifyParty;
        entity.ProposedShippingMarks = entity.SourceShippingMarks;
        entity.ProposedExportMode = entity.SourceExportMode;
        entity.ProposedCommissionRatio = entity.SourceCommissionRatio;
        entity.ProposedBusinessNature = entity.SourceBusinessNature;
        entity.ProposedSplitShipment = entity.SourceSplitShipment;
        entity.ProposedInspectionRequirement = entity.SourceInspectionRequirement;
        entity.ProposedPackagingRequirement = entity.SourcePackagingRequirement;
        entity.ProposedTotalAmount = entity.SourceTotalAmount;
        entity.ProposedDepositAmount = entity.SourceDepositAmount;

        entity.Details = new List<SalesOrderChangeRequestDetail>();
        for (var i = 0; i < sourceLines.Count; i++)
        {
            var source = sourceLines[i];
            var row = new SalesOrderChangeRequestDetail
            {
                LineNo = i + 1,
                HasSourceLine = true,
                SourceProductId = source.ProductId,
                SourceProductName = source.ProductName ?? string.Empty,
                SourceSpec = source.Spec ?? string.Empty,
                SourceUnit = source.Unit ?? string.Empty,
                SourceQuantity = source.Quantity,
                SourceUnitPrice = source.UnitPrice,
                SourceAmount = source.Amount,
                SourceDeliveryDate = source.DeliveryDate,
                SourceRemark = source.Remark ?? string.Empty,
                CreatedAt = DateTime.Now
            };
            ResetProposalToSource(row);
            entity.Details.Add(row);
        }
    }

    /// <summary>把一行的拟议值重置为来源快照（移除行与「回到来源快照」共用；不发明任何新值）</summary>
    private static void ResetProposalToSource(SalesOrderChangeRequestDetail row)
    {
        row.ProposedProductId = row.SourceProductId;
        row.ProposedProductName = row.SourceProductName;
        row.ProposedSpec = row.SourceSpec;
        row.ProposedUnit = row.SourceUnit;
        row.ProposedQuantity = row.SourceQuantity;
        row.ProposedUnitPrice = row.SourceUnitPrice;
        row.ProposedAmount = row.SourceAmount;
        row.ProposedDeliveryDate = row.SourceDeliveryDate;
        row.ProposedRemark = row.SourceRemark;
    }

    // ==================== 5. 拟议值应用（缺省 = 沿用当前拟议值，即来源快照或上一次编辑） ====================

    /// <summary>
    /// 应用客户端提交的拟议主表与拟议明细：字段缺省一律沿用当前拟议值（来源基线或上一次编辑），
    /// 文本传空串表示显式清空；明细非空时必须逐行覆盖全部来源行（可标记移除），之后的行视为新增行；
    /// 最后由 <see cref="RecalculateProposal"/> 按销售订单唯一权威算法重算金额。
    /// <para><b>先校验、后写入</b>：全部合并与校验都在**脱离跟踪的拟议副本**上完成，
    /// 任何失败（越界数量 / 单价 / 比例、超长文本、无剩余明细行等）都不会给被跟踪实体留下脏状态，
    /// 只有全部通过后才把拟议列写回申请实体 —— 失败请求不会产生半截数据。</para>
    /// </summary>
    private static void ApplyProposal(SalesOrderChangeRequest entity, SalesOrderChangeRequestSaveDto dto)
    {
        var candidate = CloneForProposal(entity);

        ApplyProposedHeader(candidate, dto);
        ApplyProposedDetails(candidate, dto);

        if (!candidate.Details.Any(d => !d.IsDeleted && !d.ProposedRemoved))
            throw BusinessException.InvalidParameter(
                "拟议至少保留一行明细（如需整体撤销，请取消本申请而不是移除全部明细）");

        RecalculateProposal(candidate);

        CopyProposalFrom(entity, candidate);
    }

    /// <summary>
    /// 建立**脱离跟踪的拟议副本**：只复制拟议列与来源行骨架（来源快照列原样复制，只读对照用），
    /// 供合并且校验使用；副本上的任何修改都不会影响被跟踪实体。
    /// </summary>
    private static SalesOrderChangeRequest CloneForProposal(SalesOrderChangeRequest entity)
    {
        return new SalesOrderChangeRequest
        {
            Id = entity.Id,
            RequestNo = entity.RequestNo,
            SalesOrderId = entity.SalesOrderId,
            Reason = entity.Reason,
            Status = entity.Status,
            ProposedOrderDate = entity.ProposedOrderDate,
            ProposedCustomerId = entity.ProposedCustomerId,
            ProposedSalesmanId = entity.ProposedSalesmanId,
            ProposedCurrency = entity.ProposedCurrency,
            ProposedExchangeRate = entity.ProposedExchangeRate,
            ProposedTotalAmount = entity.ProposedTotalAmount,
            ProposedDepositRatio = entity.ProposedDepositRatio,
            ProposedDepositAmount = entity.ProposedDepositAmount,
            ProposedPaymentTerms = entity.ProposedPaymentTerms,
            ProposedDeliveryDate = entity.ProposedDeliveryDate,
            ProposedShippingMethod = entity.ProposedShippingMethod,
            ProposedPortId = entity.ProposedPortId,
            ProposedRemark = entity.ProposedRemark,
            ProposedCustomerPoNo = entity.ProposedCustomerPoNo,
            ProposedContractNo = entity.ProposedContractNo,
            ProposedTradeTerms = entity.ProposedTradeTerms,
            ProposedDestinationPort = entity.ProposedDestinationPort,
            ProposedConsignee = entity.ProposedConsignee,
            ProposedNotifyParty = entity.ProposedNotifyParty,
            ProposedShippingMarks = entity.ProposedShippingMarks,
            ProposedExportMode = entity.ProposedExportMode,
            ProposedCommissionRatio = entity.ProposedCommissionRatio,
            ProposedBusinessNature = entity.ProposedBusinessNature,
            ProposedSplitShipment = entity.ProposedSplitShipment,
            ProposedInspectionRequirement = entity.ProposedInspectionRequirement,
            ProposedPackagingRequirement = entity.ProposedPackagingRequirement,
            Details = entity.Details.Where(d => !d.IsDeleted).Select(CloneDetailForProposal).ToList()
        };
    }

    /// <summary>明细副本（拟议列 + 来源快照列；不带任何数据库跟踪）</summary>
    private static SalesOrderChangeRequestDetail CloneDetailForProposal(SalesOrderChangeRequestDetail row)
    {
        return new SalesOrderChangeRequestDetail
        {
            Id = row.Id,
            ChangeRequestId = row.ChangeRequestId,
            LineNo = row.LineNo,
            HasSourceLine = row.HasSourceLine,
            SourceProductId = row.SourceProductId,
            SourceProductName = row.SourceProductName,
            SourceSpec = row.SourceSpec,
            SourceUnit = row.SourceUnit,
            SourceQuantity = row.SourceQuantity,
            SourceUnitPrice = row.SourceUnitPrice,
            SourceAmount = row.SourceAmount,
            SourceDeliveryDate = row.SourceDeliveryDate,
            SourceRemark = row.SourceRemark,
            ProposedRemoved = row.ProposedRemoved,
            ProposedProductId = row.ProposedProductId,
            ProposedProductName = row.ProposedProductName,
            ProposedSpec = row.ProposedSpec,
            ProposedUnit = row.ProposedUnit,
            ProposedQuantity = row.ProposedQuantity,
            ProposedUnitPrice = row.ProposedUnitPrice,
            ProposedAmount = row.ProposedAmount,
            ProposedDeliveryDate = row.ProposedDeliveryDate,
            ProposedRemark = row.ProposedRemark
        };
    }

    /// <summary>应用拟议主表值（缺省 = 沿用当前拟议值；文本 null = 沿用、"" = 显式清空）</summary>
    private static void ApplyProposedHeader(SalesOrderChangeRequest entity, SalesOrderChangeRequestSaveDto dto)
    {
        entity.ProposedOrderDate = dto.OrderDate ?? entity.ProposedOrderDate;
        entity.ProposedCustomerId = dto.CustomerId > 0 ? dto.CustomerId : entity.ProposedCustomerId;
        entity.ProposedSalesmanId = dto.SalesmanId ?? entity.ProposedSalesmanId;
        entity.ProposedCurrency = dto.Currency ?? entity.ProposedCurrency;
        entity.ProposedExchangeRate = dto.ExchangeRate is > 0 ? dto.ExchangeRate.Value : entity.ProposedExchangeRate;
        entity.ProposedDepositRatio = dto.DepositRatio ?? entity.ProposedDepositRatio;
        entity.ProposedDeliveryDate = dto.DeliveryDate ?? entity.ProposedDeliveryDate;
        entity.ProposedPortId = dto.PortId ?? entity.ProposedPortId;
        entity.ProposedCommissionRatio = dto.CommissionRatio ?? entity.ProposedCommissionRatio;
        entity.ProposedSplitShipment = dto.SplitShipment ?? entity.ProposedSplitShipment;

        entity.ProposedPaymentTerms = MergeText(
            dto.PaymentTerms, entity.ProposedPaymentTerms, SalesOrderChangeRequestRules.MaxPaymentTermsLength, "拟议付款条件");
        entity.ProposedShippingMethod = MergeText(
            dto.ShippingMethod, entity.ProposedShippingMethod, SalesOrderChangeRequestRules.MaxShippingMethodLength, "拟议运输方式");
        entity.ProposedRemark = MergeText(
            dto.Remark, entity.ProposedRemark, SalesOrderChangeRequestRules.MaxRemarkLength, "拟议备注");
        entity.ProposedCustomerPoNo = MergeText(
            dto.CustomerPoNo, entity.ProposedCustomerPoNo, SalesOrderChangeRequestRules.MaxShortCodeLength, "拟议客户 PO 号");
        entity.ProposedContractNo = MergeText(
            dto.ContractNo, entity.ProposedContractNo, SalesOrderChangeRequestRules.MaxShortCodeLength, "拟议外销合同号");
        entity.ProposedTradeTerms = MergeText(
            dto.TradeTerms, entity.ProposedTradeTerms, SalesOrderChangeRequestRules.MaxShortCodeLength, "拟议价格条款");
        entity.ProposedDestinationPort = MergeText(
            dto.DestinationPort, entity.ProposedDestinationPort, SalesOrderChangeRequestRules.MaxPortTextLength, "拟议目的港");
        entity.ProposedConsignee = MergeText(
            dto.Consignee, entity.ProposedConsignee, SalesOrderChangeRequestRules.MaxPartyLength, "拟议收货人");
        entity.ProposedNotifyParty = MergeText(
            dto.NotifyParty, entity.ProposedNotifyParty, SalesOrderChangeRequestRules.MaxPartyLength, "拟议通知人");
        entity.ProposedShippingMarks = MergeText(
            dto.ShippingMarks, entity.ProposedShippingMarks, SalesOrderChangeRequestRules.MaxRemarkLength, "拟议唛头");
        entity.ProposedExportMode = MergeText(
            dto.ExportMode, entity.ProposedExportMode, SalesOrderChangeRequestRules.MaxModeLength, "拟议出口方式");
        entity.ProposedBusinessNature = MergeText(
            dto.BusinessNature, entity.ProposedBusinessNature, SalesOrderChangeRequestRules.MaxModeLength, "拟议业务性质");
        entity.ProposedInspectionRequirement = MergeText(
            dto.InspectionRequirement, entity.ProposedInspectionRequirement,
            SalesOrderChangeRequestRules.MaxRemarkLength, "拟议验货要求");
        entity.ProposedPackagingRequirement = MergeText(
            dto.PackagingRequirement, entity.ProposedPackagingRequirement,
            SalesOrderChangeRequestRules.MaxRemarkLength, "拟议包装要求");

        if (entity.ProposedExchangeRate <= 0)
            throw BusinessException.InvalidParameter("拟议汇率必须大于 0（缺省时沿用来源订单汇率）");
    }

    /// <summary>文本合并：null = 沿用当前值；否则去空白并按上限校验（空串 = 显式清空）</summary>
    private static string MergeText(string? incoming, string fallback, int maxLength, string fieldName)
        => incoming is null
            ? fallback
            : SalesOrderChangeRequestRules.NormalizeOptionalText(incoming, maxLength, fieldName);

    /// <summary>
    /// 应用拟议明细（整体替换语义）：
    /// <list type="number">
    /// <item>旧的「拟议新增行」按软删除留痕，来源行（<c>HasSourceLine = true</c>）永远保留且快照列不变；</item>
    /// <item>传入空集合 = 拟议回到来源快照（原样留痕），不会删除来源行；</item>
    /// <item>非空时必须先逐行覆盖全部来源行（按登记顺序，可标记移除），多出来的行视为新增行。</item>
    /// </list>
    /// </summary>
    private static void ApplyProposedDetails(SalesOrderChangeRequest entity, SalesOrderChangeRequestSaveDto dto)
    {
        var sourceRows = entity.Details.Where(d => d.HasSourceLine && !d.IsDeleted).OrderBy(d => d.LineNo).ToList();
        foreach (var stale in entity.Details.Where(d => !d.HasSourceLine && !d.IsDeleted).ToList())
        {
            stale.IsDeleted = true;
            stale.UpdatedAt = DateTime.Now;
        }

        var incoming = dto.Details ?? new List<SalesOrderChangeRequestDetailSaveDto>();
        if (incoming.Count == 0)
        {
            /* 空集合 = 拟议回到来源快照（原样留痕），不会删除来源行 */
            foreach (var row in sourceRows)
            {
                row.ProposedRemoved = false;
                ResetProposalToSource(row);
            }
            return;
        }

        if (incoming.Count > SalesOrderChangeRequestRules.MaxDetailLines)
            throw BusinessException.InvalidParameter(
                $"拟议明细行数不能超过 {SalesOrderChangeRequestRules.MaxDetailLines} 行");
        if (incoming.Count < sourceRows.Count)
            throw BusinessException.InvalidParameter(
                "拟议明细必须逐行包含全部来源明细行（要移除的行请标记「移除」，新增行请排在来源行之后）");

        for (var i = 0; i < incoming.Count; i++)
        {
            var item = incoming[i];
            if (i < sourceRows.Count)
            {
                ApplyProposedDetail(sourceRows[i], item);
                continue;
            }

            if (item.Removed)
                throw BusinessException.InvalidParameter("拟议新增行不能标记为「移除来源行」（新增行没有对应来源行）");

            var row = new SalesOrderChangeRequestDetail
            {
                ChangeRequestId = entity.Id,
                HasSourceLine = false,
                LineNo = i + 1,
                CreatedAt = DateTime.Now
            };
            ApplyProposedDetail(row, item);
            entity.Details.Add(row);
        }
    }

    /// <summary>应用单行拟议值：移除行一律回到来源快照（不发明值）；非移除行做有界校验</summary>
    private static void ApplyProposedDetail(SalesOrderChangeRequestDetail row, SalesOrderChangeRequestDetailSaveDto item)
    {
        if (item.Removed)
        {
            row.ProposedRemoved = true;
            ResetProposalToSource(row);
            return;
        }

        if (item.ProductId < 0)
            throw BusinessException.InvalidParameter("拟议明细商品 Id 不合法");

        row.ProposedRemoved = false;
        row.ProposedProductId = item.ProductId;
        row.ProposedProductName = SalesOrderChangeRequestRules.NormalizeOptionalText(
            item.ProductName, SalesOrderChangeRequestRules.MaxProductTextLength, "拟议明细商品名称");
        row.ProposedSpec = SalesOrderChangeRequestRules.NormalizeOptionalText(
            item.Spec, SalesOrderChangeRequestRules.MaxProductTextLength, "拟议明细规格");
        row.ProposedUnit = SalesOrderChangeRequestRules.NormalizeOptionalText(
            item.Unit, SalesOrderChangeRequestRules.MaxUnitLength, "拟议明细单位");
        row.ProposedQuantity = item.Quantity;
        row.ProposedUnitPrice = item.UnitPrice;
        row.ProposedDeliveryDate = item.DeliveryDate;
        row.ProposedRemark = SalesOrderChangeRequestRules.NormalizeOptionalText(
            item.Remark, SalesOrderChangeRequestRules.MaxRemarkLength, "拟议明细备注");
    }

    /// <summary>
    /// 把校验通过的拟议副本写回被跟踪实体（**先校验、后写入**的最后一步）：
    /// 只写拟议列（来源快照列永不变），旧的「拟议新增行」按软删除留痕，新的拟议新增行作为新行插入。
    /// </summary>
    private static void CopyProposalFrom(SalesOrderChangeRequest target, SalesOrderChangeRequest candidate)
    {
        target.ProposedOrderDate = candidate.ProposedOrderDate;
        target.ProposedCustomerId = candidate.ProposedCustomerId;
        target.ProposedSalesmanId = candidate.ProposedSalesmanId;
        target.ProposedCurrency = candidate.ProposedCurrency;
        target.ProposedExchangeRate = candidate.ProposedExchangeRate;
        target.ProposedDepositRatio = candidate.ProposedDepositRatio;
        target.ProposedPaymentTerms = candidate.ProposedPaymentTerms;
        target.ProposedDeliveryDate = candidate.ProposedDeliveryDate;
        target.ProposedShippingMethod = candidate.ProposedShippingMethod;
        target.ProposedPortId = candidate.ProposedPortId;
        target.ProposedRemark = candidate.ProposedRemark;
        target.ProposedCustomerPoNo = candidate.ProposedCustomerPoNo;
        target.ProposedContractNo = candidate.ProposedContractNo;
        target.ProposedTradeTerms = candidate.ProposedTradeTerms;
        target.ProposedDestinationPort = candidate.ProposedDestinationPort;
        target.ProposedConsignee = candidate.ProposedConsignee;
        target.ProposedNotifyParty = candidate.ProposedNotifyParty;
        target.ProposedShippingMarks = candidate.ProposedShippingMarks;
        target.ProposedExportMode = candidate.ProposedExportMode;
        target.ProposedCommissionRatio = candidate.ProposedCommissionRatio;
        target.ProposedBusinessNature = candidate.ProposedBusinessNature;
        target.ProposedSplitShipment = candidate.ProposedSplitShipment;
        target.ProposedInspectionRequirement = candidate.ProposedInspectionRequirement;
        target.ProposedPackagingRequirement = candidate.ProposedPackagingRequirement;
        target.ProposedTotalAmount = candidate.ProposedTotalAmount;
        target.ProposedDepositAmount = candidate.ProposedDepositAmount;

        var trackedSourceRows = target.Details
            .Where(d => d.HasSourceLine && !d.IsDeleted)
            .ToDictionary(d => d.LineNo);

        foreach (var stale in target.Details.Where(d => !d.HasSourceLine && !d.IsDeleted).ToList())
        {
            stale.IsDeleted = true;
            stale.UpdatedAt = DateTime.Now;
        }

        foreach (var row in candidate.Details.Where(d => !d.IsDeleted))
        {
            if (row.HasSourceLine && trackedSourceRows.TryGetValue(row.LineNo, out var tracked))
            {
                tracked.ProposedRemoved = row.ProposedRemoved;
                tracked.ProposedProductId = row.ProposedProductId;
                tracked.ProposedProductName = row.ProposedProductName;
                tracked.ProposedSpec = row.ProposedSpec;
                tracked.ProposedUnit = row.ProposedUnit;
                tracked.ProposedQuantity = row.ProposedQuantity;
                tracked.ProposedUnitPrice = row.ProposedUnitPrice;
                tracked.ProposedAmount = row.ProposedAmount;
                tracked.ProposedDeliveryDate = row.ProposedDeliveryDate;
                tracked.ProposedRemark = row.ProposedRemark;
                continue;
            }

            row.Id = 0;
            row.ChangeRequestId = target.Id;
            row.CreatedAt = DateTime.Now;
            row.UpdatedAt = null;
            target.Details.Add(row);
        }
    }

    // ==================== 6. 金额重算（销售订单唯一权威算法；不信任客户端合计） ====================
    /// <summary>
    /// 按 <see cref="SalesOrderAmountRules"/>（销售订单唯一权威算法）重算拟议明细金额、拟议总额与拟议定金金额，
    /// 并用同一套口径校验明细数量 / 单价、定金比例（0~100）与佣金比例（0~100）。
    /// <para>做法：把拟议值装配成一张**未落库**的销售订单草稿，交给同一套算法与校验处理，再把结果写回申请行 ——
    /// 系统内不存在第二套销售订单金额算法。</para>
    /// </summary>
    private static void RecalculateProposal(SalesOrderChangeRequest entity)
    {
        var rows = entity.Details.Where(d => !d.IsDeleted && !d.ProposedRemoved).OrderBy(d => d.LineNo).ToList();
        var draft = BuildProposedDraft(entity, rows);

        SalesOrderAmountRules.ValidateDepositRatio(draft.DepositRatio);
        SalesOrderAmountRules.ValidateDetailValues(draft.Details);
        SalesOrderAmountRules.ApplyDetailAmounts(draft);
        SalesOrderAmountRules.Calculate(draft);
        SalesOrderAmountRules.Validate(draft);

        for (var i = 0; i < rows.Count; i++) rows[i].ProposedAmount = draft.Details[i].Amount;
        entity.ProposedTotalAmount = draft.TotalAmount;
        entity.ProposedDepositAmount = draft.DepositAmount;
    }

    /// <summary>把拟议值装配成一张**未落库**的销售订单草稿（仅用于复用唯一权威算法与校验）</summary>
    private static SalesOrder BuildProposedDraft(
        SalesOrderChangeRequest entity, List<SalesOrderChangeRequestDetail> rows)
    {
        return new SalesOrder
        {
            OrderDate = entity.ProposedOrderDate,
            CustomerId = entity.ProposedCustomerId,
            SalesmanId = entity.ProposedSalesmanId,
            Currency = entity.ProposedCurrency,
            ExchangeRate = entity.ProposedExchangeRate,
            DepositRatio = entity.ProposedDepositRatio,
            CommissionRatio = entity.ProposedCommissionRatio,
            PaymentTerms = entity.ProposedPaymentTerms,
            DeliveryDate = entity.ProposedDeliveryDate,
            ShippingMethod = entity.ProposedShippingMethod,
            PortId = entity.ProposedPortId,
            Remark = entity.ProposedRemark,
            CustomerPoNo = entity.ProposedCustomerPoNo,
            ContractNo = entity.ProposedContractNo,
            TradeTerms = entity.ProposedTradeTerms,
            DestinationPort = entity.ProposedDestinationPort,
            Consignee = entity.ProposedConsignee,
            NotifyParty = entity.ProposedNotifyParty,
            ShippingMarks = entity.ProposedShippingMarks,
            ExportMode = entity.ProposedExportMode,
            BusinessNature = entity.ProposedBusinessNature,
            SplitShipment = entity.ProposedSplitShipment,
            InspectionRequirement = entity.ProposedInspectionRequirement,
            PackagingRequirement = entity.ProposedPackagingRequirement,
            Details = rows.Select(row => new SalesOrderDetail
            {
                ProductId = row.ProposedProductId,
                ProductName = row.ProposedProductName,
                Spec = row.ProposedSpec,
                Unit = row.ProposedUnit,
                Quantity = row.ProposedQuantity,
                UnitPrice = row.ProposedUnitPrice,
                DeliveryDate = row.ProposedDeliveryDate,
                Remark = row.ProposedRemark
            }).ToList()
        };
    }

    // ==================== 7. 读取（分页 / 有界；批量装载，无逐行查询） ====================

    /// <summary>变更申请详情（含来源快照、来源当前可用性与「来源是否已变化」提示；只读，不写库）</summary>
    public static async Task<SalesOrderChangeRequestDto> GetAsync(
        IErpDbContext db, long id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = await LoadAsync(db, id, includeDetails: true, ct);
        return await MapAsync(db, entity, ct);
    }

    /// <summary>
    /// 变更申请台账（分页、只读）：可按来源销售订单 Id / 状态 / 关键字过滤；
    /// 明细与来源订单**一次批量装载**，绝不逐行查询数据库。
    /// </summary>
    public static async Task<PagedResult<SalesOrderChangeRequestDto>> ListAsync(
        IErpDbContext db, SalesOrderChangeRequestQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = NormalizePaging(query.Page, query.PageSize);
        var keyword = NormalizeKeyword(query.Keyword);

        if (query.Status is not null && !SalesOrderChangeRequestRules.SupportedStatuses.Contains(query.Status.Value))
            throw BusinessException.InvalidParameter("状态筛选不合法（0 草稿 / 1 已提交 / 2 已取消）");

        var source = db.SalesOrderChangeRequests.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.SalesOrderId is not null)
        {
            if (query.SalesOrderId.Value <= 0)
                throw BusinessException.InvalidParameter("来源销售订单 Id 不合法");
            var salesOrderId = query.SalesOrderId.Value;
            source = source.Where(x => x.SalesOrderId == salesOrderId);
        }

        if (query.Status is not null)
        {
            var status = query.Status.Value;
            source = source.Where(x => x.Status == status);
        }

        if (keyword.Length > 0)
            source = source.Where(x => x.RequestNo.Contains(keyword)
                || x.SalesOrderNo.Contains(keyword)
                || x.Reason.Contains(keyword));

        var total = await source.CountAsync(ct);
        var rows = await source.OrderByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = await MapManyAsync(db, rows, ct);
        return new PagedResult<SalesOrderChangeRequestDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>指定来源销售订单的变更申请清单（**有界**，单据行操作入口用）</summary>
    public static async Task<List<SalesOrderChangeRequestDto>> ListForSalesOrderAsync(
        IErpDbContext db, long salesOrderId, int take = MaxPerSourceOrder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId <= 0) throw BusinessException.InvalidParameter("来源销售订单 Id 不合法");

        var size = take <= 0 ? MaxPerSourceOrder : Math.Min(take, MaxPerSourceOrder);
        var page = await ListAsync(db,
            new SalesOrderChangeRequestQuery { SalesOrderId = salesOrderId, Page = 1, PageSize = size }, ct);
        return page.Items;
    }

    /// <summary>
    /// 来源销售订单候选（只读、**有界**）：只返回存在且未删除的订单，关键字只匹配订单号；
    /// 已作废订单照实标注为不可选择，绝不按客户名 / 金额 / 日期相似度猜测来源。
    /// </summary>
    public static async Task<List<SalesOrderChangeRequestSourceOptionDto>> ListSourceOrderOptionsAsync(
        IErpDbContext db, string? keyword, int take = MaxSourceOptions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keywordText = NormalizeKeyword(keyword);
        var size = take <= 0 ? MaxSourceOptions : Math.Min(take, MaxSourceOptions);

        var source = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);
        if (keywordText.Length > 0) source = source.Where(o => o.OrderNo.Contains(keywordText));

        var rows = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync(ct);
        return rows.Select(o =>
        {
            var selectable = o.Status != DocumentStatus.Cancelled;
            var summary = $"客户 Id={o.CustomerId}；订单日期 {o.OrderDate:yyyy-MM-dd}；"
                + $"总额 {o.TotalAmount:0.########}；状态 {SalesOrderChangeRequestRules.DocumentStatusText(o.Status)}";
            return new SalesOrderChangeRequestSourceOptionDto(
                o.Id,
                o.OrderNo ?? string.Empty,
                SalesOrderChangeRequestRules.DocumentStatusText(o.Status),
                summary,
                selectable,
                selectable ? "可选择（存在且未删除、未作废）" : "不可选择：销售订单已作废（已取消）");
        }).ToList();
    }

    /// <summary>
    /// 变更申请模块元数据（只读）：状态 / 币种白名单、分页与上限、快照 / 金额 / 提交口径与边界声明，
    /// 供界面与接口同源显示。
    /// </summary>
    public static SalesOrderChangeRequestMetadataDto GetMetadata()
    {
        var statuses = SalesOrderChangeRequestRules.SupportedStatuses
            .Select(s => new SalesOrderChangeRequestOptionDto(
                s.ToString(),
                s == SalesOrderChangeRequestRules.StatusSubmitted
                    ? "已提交（仅登记，未批准、未套用）"
                    : SalesOrderChangeRequestRules.StatusText(s)))
            .ToList();

        var currencies = Enum.GetValues<Currency>()
            .Select(c => new SalesOrderChangeRequestOptionDto(c.ToString(), SalesOrderChangeRequestRules.CurrencyText(c)))
            .ToList();

        return new SalesOrderChangeRequestMetadataDto(
            statuses,
            currencies,
            SalesOrderChangeRequestQuery.MaxPageSize,
            MaxPerSourceOrder,
            SalesOrderChangeRequestRules.MaxDetailLines,
            SalesOrderChangeRequestRules.SnapshotPolicyText,
            SalesOrderChangeRequestRules.AmountPolicyText,
            SalesOrderChangeRequestRules.SubmitPolicyText,
            SalesOrderChangeRequestRules.ApprovalBoundaryText,
            SalesOrderChangeRequestRules.BoundaryText);
    }

    /// <summary>分页参数规整（页码下限 1；每页条数收敛到 1 ~ <see cref="SalesOrderChangeRequestQuery.MaxPageSize"/>）</summary>
    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedSize = pageSize < 1 ? SalesOrderChangeRequestQuery.DefaultPageSize : pageSize;
        if (normalizedSize > SalesOrderChangeRequestQuery.MaxPageSize)
            normalizedSize = SalesOrderChangeRequestQuery.MaxPageSize;
        return (normalizedPage, normalizedSize);
    }

    /// <summary>列表关键字规整（去首尾空白并按上限校验；超长直接拒绝，不静默截断）</summary>
    private static string NormalizeKeyword(string? keyword)
    {
        var value = (keyword ?? string.Empty).Trim();
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    /// <summary>装载变更申请（可选含明细；不存在或已删除一律按「不存在」处理）</summary>
    private static async Task<SalesOrderChangeRequest> LoadAsync(
        IErpDbContext db, long id, bool includeDetails, CancellationToken ct)
    {
        if (id <= 0) throw BusinessException.InvalidParameter("变更申请 Id 不合法");

        var source = db.SalesOrderChangeRequests.AsQueryable();
        if (includeDetails) source = source.Include(x => x.Details);

        return await source.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct)
            ?? throw BusinessException.NotFound($"变更申请不存在（Id={id}）");
    }

    /// <summary>装载来源销售订单（含明细；已删除订单返回 null，由调用方按「来源不可用」处理）</summary>
    private static async Task<SalesOrder?> LoadSourceOrderAsync(
        IErpDbContext db, long salesOrderId, CancellationToken ct)
    {
        if (salesOrderId <= 0) return null;
        return await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == salesOrderId && !o.IsDeleted, ct);
    }

    /// <summary>写路径返回投影：重新装载明细与来源订单后映射，保证返回值与台账 / 详情完全一致</summary>
    private static async Task<SalesOrderChangeRequestDto> MapAsync(
        IErpDbContext db, SalesOrderChangeRequest entity, CancellationToken ct)
    {
        var details = await db.SalesOrderChangeRequestDetails.AsNoTracking()
            .Where(d => d.ChangeRequestId == entity.Id && !d.IsDeleted)
            .OrderBy(d => d.LineNo)
            .ToListAsync(ct);
        var source = await LoadSourceOrderAsync(db, entity.SalesOrderId, ct);
        return Map(entity, details, source);
    }

    // ==================== 8. 映射（只读投影：来源 vs 拟议对照 + 来源变化提示） ====================

    /// <summary>
    /// 批量映射台账行：明细按申请 Id 一次装载，来源订单（含明细）按其 Id 一次装载 —— 无论多少行，
    /// 数据库往返次数恒定，绝不逐行查询。
    /// </summary>
    private static async Task<List<SalesOrderChangeRequestDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<SalesOrderChangeRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return new List<SalesOrderChangeRequestDto>();

        var requestIds = rows.Select(r => r.Id).ToList();
        var details = await db.SalesOrderChangeRequestDetails.AsNoTracking()
            .Where(d => requestIds.Contains(d.ChangeRequestId) && !d.IsDeleted)
            .ToListAsync(ct);
        var detailsByRequest = details
            .GroupBy(d => d.ChangeRequestId)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.LineNo).ToList());

        var sourceOrderIds = rows.Select(r => r.SalesOrderId).Distinct().ToList();
        var sourceOrders = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .Where(o => sourceOrderIds.Contains(o.Id))
            .ToListAsync(ct);
        var sourceById = sourceOrders.ToDictionary(o => o.Id);

        return rows.Select(row => Map(
            row,
            detailsByRequest.TryGetValue(row.Id, out var own) ? own : new List<SalesOrderChangeRequestDetail>(),
            sourceById.TryGetValue(row.SalesOrderId, out var source) ? source : null)).ToList();
    }

    /// <summary>
    /// 单条映射：来源可用性 / 来源是否已变化 / 主表与明细的「来源 vs 拟议」对照 / 差异规模统计。
    /// <para>差异只表示「拟议与来源不同」，<strong>不</strong>表示已批准或已套用；来源变化只提示、不刷新拟议值。</para>
    /// </summary>
    private static SalesOrderChangeRequestDto Map(
        SalesOrderChangeRequest entity,
        List<SalesOrderChangeRequestDetail> details,
        SalesOrder? currentSource)
    {
        var sourceAvailable = currentSource is not null && !currentSource.IsDeleted;
        var changedFields = new List<string>();
        if (sourceAvailable)
        {
            var current = currentSource!;
            if ((int)current.Status != entity.SourceStatus) changedFields.Add("状态");
            if ((current.UpdatedAt ?? current.CreatedAt) != entity.SourceUpdatedAt) changedFields.Add("最后更新时间");
            if (current.TotalAmount != entity.SourceTotalAmount) changedFields.Add("订单总额");
            if (SalesOrderChangeRequestRules.BuildDetailSignature(current.Details) != entity.SourceDetailSignature)
                changedFields.Add("明细签名");
        }

        var sourceChanged = sourceAvailable && changedFields.Count > 0;
        var comparisons = BuildHeaderComparisons(entity);
        var headerChangedCount = comparisons.Count(c => c.Changed);

        var views = details.Select(BuildDetailView).ToList();
        var addedLines = views.Count(v => !v.HasSourceLine);
        var removedLines = views.Count(v => v.HasSourceLine && v.ProposedRemoved);
        var changedLines = views.Count(v => v.HasSourceLine && !v.ProposedRemoved && v.Changed);

        return new SalesOrderChangeRequestDto(
            entity.Id,
            entity.RequestNo ?? string.Empty,
            entity.SalesOrderId,
            entity.SalesOrderNo ?? string.Empty,
            SalesOrderChangeRequestRules.DocumentStatusText(entity.SourceStatus),
            entity.SourceUpdatedAt,
            entity.SourceSnapshotMarker ?? string.Empty,
            sourceAvailable,
            SalesOrderChangeRequestRules.SourceAvailabilityText(sourceAvailable),
            sourceChanged,
            SalesOrderChangeRequestRules.SourceChangedText(sourceChanged, sourceAvailable),
            SalesOrderChangeRequestRules.SourceChangeDetailText(changedFields),
            entity.Reason ?? string.Empty,
            entity.Status,
            SalesOrderChangeRequestRules.StatusText(entity.Status),
            entity.Status == SalesOrderChangeRequestRules.StatusDraft,
            entity.Status == SalesOrderChangeRequestRules.StatusSubmitted,
            entity.Status == SalesOrderChangeRequestRules.StatusCancelled,
            SalesOrderChangeRequestRules.IsEditable(entity.Status),
            entity.SubmittedAt,
            entity.CancelledAt,
            entity.CancelledReason ?? string.Empty,
            new SalesOrderChangeRequestProposedHeaderDto(
                entity.ProposedOrderDate,
                entity.ProposedCustomerId,
                entity.ProposedSalesmanId,
                entity.ProposedCurrency,
                entity.ProposedExchangeRate,
                entity.ProposedDepositRatio,
                entity.ProposedPaymentTerms ?? string.Empty,
                entity.ProposedDeliveryDate,
                entity.ProposedShippingMethod ?? string.Empty,
                entity.ProposedPortId,
                entity.ProposedRemark ?? string.Empty,
                entity.ProposedCustomerPoNo ?? string.Empty,
                entity.ProposedContractNo ?? string.Empty,
                entity.ProposedTradeTerms ?? string.Empty,
                entity.ProposedDestinationPort ?? string.Empty,
                entity.ProposedConsignee ?? string.Empty,
                entity.ProposedNotifyParty ?? string.Empty,
                entity.ProposedShippingMarks ?? string.Empty,
                entity.ProposedExportMode ?? string.Empty,
                entity.ProposedCommissionRatio,
                entity.ProposedBusinessNature ?? string.Empty,
                entity.ProposedSplitShipment,
                entity.ProposedInspectionRequirement ?? string.Empty,
                entity.ProposedPackagingRequirement ?? string.Empty),
            entity.SourceTotalAmount,
            entity.ProposedTotalAmount,
            entity.SourceDepositAmount,
            entity.ProposedDepositAmount,
            comparisons,
            headerChangedCount,
            views,
            addedLines,
            removedLines,
            changedLines,
            entity.CreatedAt,
            entity.UpdatedAt,
            SalesOrderChangeRequestRules.ChangeSummaryText(headerChangedCount, changedLines, addedLines, removedLines),
            SalesOrderChangeRequestRules.ApprovalBoundaryText,
            SalesOrderChangeRequestRules.BoundaryText);
    }

    /// <summary>明细对照视图（新增 / 移除 / 已修改 / 未修改；数量与单价差异标志仅在「有来源行且未移除」时有意义）</summary>
    private static SalesOrderChangeRequestDetailViewDto BuildDetailView(SalesOrderChangeRequestDetail row)
    {
        var comparable = row.HasSourceLine && !row.ProposedRemoved;
        var quantityChanged = comparable && row.ProposedQuantity != row.SourceQuantity;
        var unitPriceChanged = comparable && row.ProposedUnitPrice != row.SourceUnitPrice;
        var changed = comparable
            && (row.ProposedProductId != row.SourceProductId
                || !Equivalent(row.ProposedProductName, row.SourceProductName)
                || !Equivalent(row.ProposedSpec, row.SourceSpec)
                || !Equivalent(row.ProposedUnit, row.SourceUnit)
                || quantityChanged
                || unitPriceChanged
                || row.ProposedDeliveryDate != row.SourceDeliveryDate
                || !Equivalent(row.ProposedRemark, row.SourceRemark));

        return new SalesOrderChangeRequestDetailViewDto(
            row.LineNo,
            row.HasSourceLine,
            row.SourceProductName ?? string.Empty,
            row.SourceSpec ?? string.Empty,
            row.SourceUnit ?? string.Empty,
            row.SourceQuantity,
            row.SourceUnitPrice,
            row.SourceAmount,
            row.SourceDeliveryDate,
            row.SourceRemark ?? string.Empty,
            row.ProposedRemoved,
            row.ProposedProductId,
            row.ProposedProductName ?? string.Empty,
            row.ProposedSpec ?? string.Empty,
            row.ProposedUnit ?? string.Empty,
            row.ProposedQuantity,
            row.ProposedUnitPrice,
            row.ProposedAmount,
            row.ProposedDeliveryDate,
            row.ProposedRemark ?? string.Empty,
            comparable ? changed : !row.HasSourceLine || row.ProposedRemoved,
            quantityChanged,
            unitPriceChanged,
            SalesOrderChangeRequestRules.DetailComparisonText(row.HasSourceLine, row.ProposedRemoved, changed));
    }

    /// <summary>主表「来源 vs 拟议」逐字段对照（26 项；差异只表示拟议与来源不同，不代表审批结果）</summary>
    private static List<SalesOrderChangeRequestFieldComparisonDto> BuildHeaderComparisons(SalesOrderChangeRequest e)
    {
        return new List<SalesOrderChangeRequestFieldComparisonDto>
        {
            Compare("orderDate", "订单日期", DateText(e.SourceOrderDate), DateText(e.ProposedOrderDate)),
            Compare("customerId", "客户 Id", e.SourceCustomerId.ToString(), e.ProposedCustomerId.ToString()),
            Compare("salesmanId", "业务员 Id", IdText(e.SourceSalesmanId), IdText(e.ProposedSalesmanId)),
            Compare("currency", "币种",
                SalesOrderChangeRequestRules.CurrencyText(e.SourceCurrency),
                SalesOrderChangeRequestRules.CurrencyText(e.ProposedCurrency)),
            Compare("exchangeRate", "汇率", Num(e.SourceExchangeRate), Num(e.ProposedExchangeRate)),
            Compare("totalAmount", "总额（服务端重算）", Num(e.SourceTotalAmount), Num(e.ProposedTotalAmount)),
            Compare("depositRatio", "定金比例%", Num(e.SourceDepositRatio), Num(e.ProposedDepositRatio)),
            Compare("depositAmount", "定金金额（服务端重算）", Num(e.SourceDepositAmount), Num(e.ProposedDepositAmount)),
            Compare("paymentTerms", "付款条件", Text(e.SourcePaymentTerms), Text(e.ProposedPaymentTerms)),
            Compare("deliveryDate", "交货日期", DateText(e.SourceDeliveryDate), DateText(e.ProposedDeliveryDate)),
            Compare("shippingMethod", "运输方式", Text(e.SourceShippingMethod), Text(e.ProposedShippingMethod)),
            Compare("portId", "目的港 Id", IdText(e.SourcePortId), IdText(e.ProposedPortId)),
            Compare("remark", "备注", Text(e.SourceRemark), Text(e.ProposedRemark)),
            Compare("customerPoNo", "客户 PO 号", Text(e.SourceCustomerPoNo), Text(e.ProposedCustomerPoNo)),
            Compare("contractNo", "外销合同号", Text(e.SourceContractNo), Text(e.ProposedContractNo)),
            Compare("tradeTerms", "价格条款", Text(e.SourceTradeTerms), Text(e.ProposedTradeTerms)),
            Compare("destinationPort", "目的港", Text(e.SourceDestinationPort), Text(e.ProposedDestinationPort)),
            Compare("consignee", "收货人", Text(e.SourceConsignee), Text(e.ProposedConsignee)),
            Compare("notifyParty", "通知人", Text(e.SourceNotifyParty), Text(e.ProposedNotifyParty)),
            Compare("shippingMarks", "唛头", Text(e.SourceShippingMarks), Text(e.ProposedShippingMarks)),
            Compare("exportMode", "出口方式", Text(e.SourceExportMode), Text(e.ProposedExportMode)),
            Compare("commissionRatio", "佣金比例%", Num(e.SourceCommissionRatio), Num(e.ProposedCommissionRatio)),
            Compare("businessNature", "业务性质", Text(e.SourceBusinessNature), Text(e.ProposedBusinessNature)),
            Compare("splitShipment", "分批出货", BoolText(e.SourceSplitShipment), BoolText(e.ProposedSplitShipment)),
            Compare("inspectionRequirement", "验货要求",
                Text(e.SourceInspectionRequirement), Text(e.ProposedInspectionRequirement)),
            Compare("packagingRequirement", "包装要求",
                Text(e.SourcePackagingRequirement), Text(e.ProposedPackagingRequirement))
        };
    }

    /// <summary>字段对照构造（空值显示为「（空）」；比较基于去空白后的原值，不受显示占位影响）</summary>
    private static SalesOrderChangeRequestFieldComparisonDto Compare(
        string field, string label, string sourceText, string proposedText)
        => new(field, label, Display(sourceText), Display(proposedText),
            !string.Equals(sourceText, proposedText, StringComparison.Ordinal));

    /// <summary>文本规范化（去首尾空白；空值按空串处理）</summary>
    private static string Text(string? value) => (value ?? string.Empty).Trim();

    /// <summary>空值显示占位（只影响显示，不影响差异判断）</summary>
    private static string Display(string value) => value.Length == 0 ? "（空）" : value;

    /// <summary>数值显示（最多 8 位小数、去尾零；确定性文本，便于人工核对）</summary>
    private static string Num(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>日期显示（yyyy-MM-dd；空值显示为空串）</summary>
    private static string DateText(DateTime? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>可空 Id 显示（空值显示为空串）</summary>
    private static string IdText(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>布尔显示（是 / 否）</summary>
    private static string BoolText(bool value) => value ? "是" : "否";

    /// <summary>忽略首尾空白的文本等价判定</summary>
    private static bool Equivalent(string? left, string? right)
        => string.Equals(Text(left), Text(right), StringComparison.Ordinal);
}
