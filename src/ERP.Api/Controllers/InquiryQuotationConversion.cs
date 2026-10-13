using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>询价单转报价单的唯一映射与资格守卫。</summary>
public static class InquiryQuotationConversion
{
    /// <summary>
    /// 「询价单 → 报价单」来源血缘锁序口径（接口 / 文档同源，ERP-403）：直接转换（<c>to-quotation</c>）
    /// 与普通报价单表单保存（带入预填后手工保存）共用同一把询价单来源行锁
    /// （<see cref="InquiryMutationRules.LockInquiryRowAsync"/>），因此二者在<b>同一原子事务</b>内串行化，
    /// 同一询价单至多生成一张有效报价单；普通保存绝不另取一把锁，也绝不反向获取上游锁。
    /// <para>ERP-474：草稿构造前先按 <see cref="InquiryCommercialValidationRules.ValidateOperational"/> 复核
    /// **来源询价单**的商业条款（至少一行有效明细 + 币种 / 汇率 / 有界整数天有效期 / 数量 · 单价 / 行金额精度），
    /// 非法来源在映射 / 占号 / 任何写入之前受控拒绝。</para>
    /// <para>ERP-475：草稿构造前另按 <see cref="InquiryMasterReferenceRules"/> 复核**来源询价单的实时主数据引用**
    /// （必填客户 / 可选业务员 / 明细商品一次有界批量查询）：失效引用同样在映射 / 占号 / 任何写入之前受控拒绝，
    /// 绝不消耗报价单号、绝不落库、绝不改写来源状态；带入预填（只读）与直接转换共用同一道复核。</para>
    /// </summary>
    public const string SourceLineageLockOrderText = SalesDocumentSourceLineageRules.LockOrderText;

    /// <summary>
    /// 只读构造（带入预填 / 直接转换共用的资格守卫入口）：按 <paramref name="inquiryId"/> 读取**权威**询价单
    /// （含明细），复核既有转换资格（重复生成 / 已完成 / 未审核 / 无有效明细），再映射为未落库的报价单草稿。
    /// 本方法**不落库、不占号、不改写来源状态**；写路径（转报价单）应在询价单来源行锁内改用
    /// <see cref="BuildDraftAsync(IErpDbContext, Inquiry)"/> 以锁内权威重读为准。
    /// </summary>
    public static async Task<Quotation> BuildDraftAsync(IErpDbContext db, long inquiryId)
    {
        var inquiry = await db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");

        var existing = await InquiryMutationRules.FindLiveQuotationAsync(db, inquiryId);
        InquiryMutationRules.EnsureQuotationConversionEligible(inquiry, existing,
            InquiryMutationRules.ActiveDetailCount(inquiry));

        return await BuildDraftAsync(db, inquiry);
    }

    /// <summary>
    /// 按**锁内权威重读**的询价单映射报价单草稿（不查资格、不落库、不占号、不改写来源）：
    /// 资格由调用方在询价单来源行锁内以 <see cref="InquiryMutationRules.EnsureQuotationConversionEligible"/>
    /// 复核后调用，保证「重复检测 / 单号生成 / 草稿构造」全部发生在同一原子事务内。
    /// <para>ERP-475：来源客户 / 可选业务员 / 明细商品的**实时主数据引用**在映射与占号之前复核，
    /// 失效来源一律受控拒绝且零副作用。</para>
    /// </summary>
    public static async Task<Quotation> BuildDraftAsync(IErpDbContext db, Inquiry inquiry)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(inquiry);

        // ERP-474：询价单来源条款按唯一权威契约在**映射 / 单号预约 / 任何写入之前**校验
        // （至少一行有效明细；币种 / 汇率 / 有效期（有界整数天）/ 数量 · 单价 / 行金额精度）——
        // 带入预填与直接转换共用同一道复核，非法来源绝不生成草稿、绝不改写来源状态。
        InquiryCommercialValidationRules.ValidateOperational(inquiry);

        // ERP-475：询价单来源的**实时主数据引用**（必填客户 / 可选业务员 / 明细商品一次有界批量查询）在映射草稿、
        // 预约报价单号与任何写入之前复核 —— 失效引用（零 / 负 / 缺失 / 已删除 / 已停用 / 非在职或非业务员）
        // 一律受控拒绝，绝不消耗报价单号、绝不落库、绝不改写来源询价单状态。
        var customer = await InquiryMasterReferenceRules
            .EnsureCustomerAvailableAsync(db, inquiry.CustomerId);
        var salesman = await InquiryMasterReferenceRules
            .EnsureSalesmanAvailableAsync(db, inquiry.SalesmanId);
        await InquiryMasterReferenceRules.EnsureDetailProductsAsync(db, inquiry.Details);

        var quotationDate = DateTime.Today;
        var quotation = new Quotation
        {
            QuotationDate = quotationDate,
            // ERP-474：有效期（天）已由 InquiryCommercialValidationRules 验证为 1 ~ 3650 的整数天，
            // 因此这里直接使用来源值，绝不静默回退默认天数。
            ValidUntil = quotationDate.AddDays(inquiry.ValidDays),
            CustomerId = inquiry.CustomerId,
            CustomerName = customer.CustomerName,
            ContactPerson = string.IsNullOrWhiteSpace(inquiry.ContactPerson) ? customer.ContactPerson : inquiry.ContactPerson,
            ContactPhone = string.IsNullOrWhiteSpace(inquiry.ContactPhone) ? customer.Phone : inquiry.ContactPhone,
            ContactEmail = customer.Email,
            InquiryId = inquiry.Id,
            InquiryNo = inquiry.InquiryNo,
            TradeTerms = customer.TradeTerms,
            PortOfDestination = customer.DestinationPort,
            PaymentTerms = customer.PaymentTerms,
            Currency = inquiry.Currency,
            ExchangeRate = inquiry.ExchangeRate,
            SalesmanId = inquiry.SalesmanId,
            SalesmanName = salesman?.EmployeeName ?? string.Empty,
            Remark = inquiry.Remark,
            Status = DocumentStatus.Pending,
            Details = inquiry.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id)
                .Select((d, index) => new QuotationDetail
                {
                    SortNo = index + 1,
                    ProductId = d.ProductId,
                    ProductName = d.ProductName,
                    Spec = d.Spec,
                    Unit = d.Unit,
                    Quantity = d.Quantity,
                    UnitPrice = d.UnitPrice,
                    Remark = d.Remark
                }).ToList()
        };
        // ERP-470：币种 / 汇率 / 数量 / 单价与行金额、合计一律由唯一权威契约校验并按币种精度派生
        // （绝不静默替换汇率、绝不采信来源行金额）：非法来源条款在单号预约与任何写入之前受控拒绝。
        QuotationCommercialValidationRules.NormalizeDerivedDraft(quotation);

        // ERP-472：询价单 → 报价单（直接转换 / 带入预填）先复核**实时主数据引用** —— 草稿承载的客户 / 业务员 /
        // 明细商品必须在生成草稿与预约单号之前解析到实时主数据（零 / 负 / 缺失 / 已删除 / 已停用一律受控拒绝）；
        // 被拒绝的转换绝不消耗报价单号、绝不落库、绝不改写来源询价单状态。
        await QuotationMasterReferenceRules.EnsureMasterReferencesAsync(db, quotation);
        return quotation;
    }

    /// <summary>
    /// 报价单草稿金额 / 合计的服务端权威复算（ERP-470：委托
    /// <see cref="QuotationCommercialValidationRules"/> 的唯一权威契约 —— 先校验币种 / 汇率 / 逐行数量 · 单价，
    /// 再按币种精度派生行金额 / 合计 / 折人民币）。校验失败即原样抛出，不改写任何字段。
    /// </summary>
    public static void Recalculate(Quotation quotation)
        => QuotationCommercialValidationRules.NormalizeDerivedDraft(quotation);
}
