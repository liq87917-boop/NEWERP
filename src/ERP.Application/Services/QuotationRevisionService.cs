using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 报价单版本链服务（ERP-035：多轮议价版本留痕）。职责：
/// <list type="number">
/// <item><b>创建新版本</b>（<see cref="CreateRevisionAsync"/>）：把一张既有报价单整单复制为新的**草稿**版本，
/// 源版本一行不改（源版本从此成为只读历史）；</item>
/// <item><b>版本号分配</b>：链内单调递增（根 V1、下一版本 = 链内最大版本号 + 1），单号为「根单号 + <c>-R版本号</c>」；
/// 并发创建同版本号时由数据库唯一索引 <c>UX_Quotations_RevisionChain</c> 兜底拒绝，
/// 本层把该冲突转换为可读的业务错误（绝不允许链内出现重复版本号）；</item>
/// <item><b>链查询</b>（<see cref="LoadChainAsync"/>）：返回根单 + 全部版本的完整链（不隐藏历史版本）；</item>
/// <item><b>下游选择口径</b>（<see cref="LoadConvertedQuotationIdsAsync"/>）：
/// 已转 PI / 已转销售订单仍按「被显式选中的那一张报价单」判定，不因存在新版本而转移到别的版本。</item>
/// </list>
/// <para>边界：不复制审核状态、下游转换状态与已生成单据链接（新版本一律 <see cref="DocumentStatus.Pending"/> 草稿）；
/// 不回填、不改写历史版本；不新增单据号字轨（版本单号由根单号派生）。</para>
/// </summary>
public static class QuotationRevisionService
{
    /// <summary>并发冲突错误提示（唯一索引兜底时的对外文案）</summary>
    public const string ConcurrencyConflictMessage = "版本号正被其他请求占用（并发创建同一版本号），请重试";

    /// <summary>
    /// 以 <paramref name="sourceId"/> 为源创建新版本并返回新版本实体（已落库，含明细与权威复算后的合计）。
    /// </summary>
    /// <param name="db">数据访问上下文</param>
    /// <param name="sourceId">被复制的源报价单 Id（必须存在、未删除、未作废）</param>
    public static async Task<Quotation> CreateRevisionAsync(IErpDbContext db, long sourceId)
    {
        var source = await db.Quotations.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == sourceId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");

        if (source.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的报价单不能创建新版本");

        // 链根：源版本本身无根则源即根；否则取源记录的根单 Id / 根单号
        var rootId = source.RootQuotationId ?? source.Id;
        var rootNo = source.RootQuotationId is null
            ? source.QuotationNo
            : (string.IsNullOrWhiteSpace(source.RootQuotationNo)
                ? await ResolveRootNoAsync(db, rootId)
                : source.RootQuotationNo);

        // 版本号 = 链内最大版本号 + 1（链内历史行若为 0 / 未赋值，按初始版本 1 参与计算）
        var maxExisting = await db.Quotations.AsNoTracking()
            .Where(o => !o.IsDeleted && (o.Id == rootId || o.RootQuotationId == rootId))
            .MaxAsync(o => (int?)o.RevisionNumber) ?? 0;
        var revisionNumber = QuotationRevisionRules.NextRevisionNumber(maxExisting);

        // 单号 = 根单号 + "-R版本号"；被链外单据手工占用时继续递增（保持单调递增且不重复）
        string revisionNo;
        while (true)
        {
            revisionNo = QuotationRevisionRules.BuildRevisionNo(rootNo, revisionNumber);
            QuotationRevisionRules.EnsureRevisionNoFits(revisionNo);
            var taken = await db.Quotations.AsNoTracking()
                .AnyAsync(o => !o.IsDeleted && o.QuotationNo == revisionNo);
            if (!taken) break;
            revisionNumber++;
        }

        var revision = BuildRevision(source, revisionNumber, revisionNo, rootId, rootNo);
        db.Quotations.Add(revision);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (LooksLikeUniqueViolation(ex))
        {
            // 并发创建：另一个请求已插入同版本号 → 唯一索引拒绝本次插入，链内不会出现重复版本号
            throw BusinessException.RuleConflict(ConcurrencyConflictMessage);
        }
        return revision;
    }

    /// <summary>
    /// 读取 <paramref name="selected"/> 所属版本链的完整列表（按版本号升序，含根单与全部历史版本）。
    /// 历史报价单（无版本元数据）按初始版本 V1 返回，且不做任何写库。
    /// </summary>
    public static async Task<List<QuotationRevisionChainItem>> LoadChainAsync(IErpDbContext db, Quotation selected)
    {
        var rootId = selected.RootQuotationId ?? selected.Id;
        var rows = await db.Quotations.AsNoTracking()
            .Where(o => !o.IsDeleted && (o.Id == rootId || o.RootQuotationId == rootId))
            .OrderBy(o => o.RevisionNumber).ThenBy(o => o.Id)
            .ToListAsync();

        var rootNo = rows.FirstOrDefault(r => r.Id == rootId)?.QuotationNo;
        if (string.IsNullOrWhiteSpace(rootNo))
            rootNo = string.IsNullOrWhiteSpace(selected.RootQuotationNo) ? selected.QuotationNo : selected.RootQuotationNo;

        var converted = await LoadConvertedQuotationIdsAsync(db, rows.Select(r => r.Id).ToList());
        var latestId = rows.Count == 0
            ? selected.Id
            : rows.OrderByDescending(r => QuotationRevisionRules.EffectiveRevisionNumber(r.RevisionNumber))
                  .ThenByDescending(r => r.Id).First().Id;

        return rows.Select(r => new QuotationRevisionChainItem
        {
            Id = r.Id,
            QuotationNo = r.QuotationNo,
            RevisionNumber = QuotationRevisionRules.EffectiveRevisionNumber(r.RevisionNumber),
            RootQuotationId = rootId,
            RootQuotationNo = rootNo,
            PreviousRevisionId = r.PreviousRevisionId,
            PreviousRevisionNo = r.PreviousRevisionNo,
            IsInitialRevision = QuotationRevisionRules.IsInitialRevision(r.PreviousRevisionId),
            IsSelected = r.Id == selected.Id,
            IsLatest = r.Id == latestId,
            // 「已被后续版本取代」= 链内存在直接指向它的下一版本 → 该版本为只读历史
            Superseded = rows.Any(x => x.PreviousRevisionId == r.Id),
            Converted = converted.Contains(r.Id) || r.Status == DocumentStatus.Completed,
            Status = r.Status,
            QuotationDate = r.QuotationDate,
            ValidUntil = r.ValidUntil,
            TotalAmount = r.TotalAmount,
            TotalAmountCny = r.TotalAmountCny,
            Currency = r.Currency,
            CustomerName = r.CustomerName,
            SalesmanName = r.SalesmanName
        }).ToList();
    }

    /// <summary>该报价单是否已被后续版本取代（存在指向它的下一版本）→ 历史版本只读</summary>
    public static Task<bool> IsSupersededAsync(IErpDbContext db, long quotationId) =>
        db.Quotations.AsNoTracking().AnyAsync(o => !o.IsDeleted && o.PreviousRevisionId == quotationId);

    /// <summary>
    /// 已转出报价单 Id 集合（来源外键：已转 PI / 已转销售订单；口径与成交率报表一致，ERP-018 / ERP-035 共用）。
    /// 转换判定只看被显式选中的那一张报价单，不会因为存在更新的版本而指向别的版本。
    /// </summary>
    public static async Task<HashSet<long>> LoadConvertedQuotationIdsAsync(IErpDbContext db, IReadOnlyCollection<long> quotationIds)
    {
        var converted = new HashSet<long>();
        if (quotationIds.Count == 0) return converted;

        var ids = quotationIds.ToList();
        var piIds = await db.ProformaInvoices.AsNoTracking()
            .Where(p => !p.IsDeleted && p.QuotationId != null && ids.Contains(p.QuotationId.Value))
            .Select(p => p.QuotationId!.Value).ToListAsync();
        var orderIds = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.SourceQuotationId != null && ids.Contains(o.SourceQuotationId.Value))
            .Select(o => o.SourceQuotationId!.Value).ToListAsync();

        foreach (var id in piIds) converted.Add(id);
        foreach (var id in orderIds) converted.Add(id);
        return converted;
    }

    /// <summary>
    /// 组装新版本草稿：复制可议价的主表字段与明细（商品 / 规格 / 单位 / 数量 / 单价 / 起订量 / 备注），
    /// 合计经 <see cref="QuotationLineRules.Normalize"/>（唯一权威口径）在服务端复算；
    /// <b>不复制</b>主键 / 单号 / 审核状态（一律草稿）/ 下游转换状态与已生成单据链接。
    /// <para>有效期（<see cref="Quotation.ValidUntil"/>）本身是可议价条款，原样继承，由业务员在草稿上调整；
    /// 报价日期取新版本创建当日。</para>
    /// </summary>
    private static Quotation BuildRevision(Quotation source, int revisionNumber, string revisionNo, long rootId, string rootNo)
    {
        var revision = new Quotation
        {
            QuotationNo = revisionNo,
            QuotationDate = DateTime.Today,
            ValidUntil = source.ValidUntil,
            CustomerId = source.CustomerId,
            CustomerName = source.CustomerName,
            ContactPerson = source.ContactPerson,
            ContactPhone = source.ContactPhone,
            ContactEmail = source.ContactEmail,
            InquiryId = source.InquiryId,
            InquiryNo = source.InquiryNo,
            TradeTerms = source.TradeTerms,
            PortOfLoading = source.PortOfLoading,
            PortOfDestination = source.PortOfDestination,
            PaymentTerms = source.PaymentTerms,
            LeadTime = source.LeadTime,
            Currency = source.Currency,
            ExchangeRate = source.ExchangeRate,
            SalesmanId = source.SalesmanId,
            SalesmanName = source.SalesmanName,
            Remark = source.Remark,
            Status = DocumentStatus.Pending,        // 审核状态不继承：新版本一律从草稿开始
            RootQuotationId = rootId,
            RootQuotationNo = rootNo,
            RevisionNumber = revisionNumber,
            PreviousRevisionId = source.Id,
            PreviousRevisionNo = source.QuotationNo,
            CreatedAt = DateTime.Now,
            Details = source.Details.Where(d => !d.IsDeleted)
                .OrderBy(d => d.SortNo).ThenBy(d => d.Id)
                .Select(CopyDetail).ToList()
        };
        QuotationLineRules.Normalize(revision);     // 服务端权威复算：行号 / 金额 / 合计 / 折人民币
        return revision;
    }

    /// <summary>复制一行明细（行号 / 金额 / 主键 / 外键由服务端重算，不继承源行）</summary>
    private static QuotationDetail CopyDetail(QuotationDetail d) => new()
    {
        ProductId = d.ProductId,
        ProductCode = d.ProductCode,
        ProductName = d.ProductName,
        Spec = d.Spec,
        Unit = d.Unit,
        Quantity = d.Quantity,
        UnitPrice = d.UnitPrice,
        Moq = d.Moq,
        Remark = d.Remark
    };

    /// <summary>根单号兜底解析（源版本记录里冗余根单号缺失时按根单 Id 查库；只读）</summary>
    private static async Task<string> ResolveRootNoAsync(IErpDbContext db, long rootId)
    {
        var rootNo = await db.Quotations.AsNoTracking()
            .Where(o => o.Id == rootId)
            .Select(o => o.QuotationNo).FirstOrDefaultAsync();
        return rootNo ?? string.Empty;
    }

    /// <summary>
    /// 是否为唯一索引 / 唯一约束冲突（SQL Server 2601 重复键 / 2627 违反唯一约束）。
    /// 说明：应用层不引用 <c>Microsoft.Data.SqlClient</c>，故按错误码与错误文案识别；
    /// 非唯一性冲突的 <see cref="DbUpdateException"/>（如结构缺失）原样上抛，不被误报成并发冲突。
    /// </summary>
    private static bool LooksLikeUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("UNIQUE", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
