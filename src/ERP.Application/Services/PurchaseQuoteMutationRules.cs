using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 供应商比价（<see cref="PurchaseQuote"/>，<c>api/purchase/quotes</c>）与比价审批
/// （<c>api/purchase/quote-decisions</c>）的共享并发护栏与对比草稿校验（ERP-417）。
/// <para><b>唯一来源锁</b>：新增 / 修改 / 删除 / 批量删除 / 审批决定 / 转采购订单，都在重复检测与字段改写
/// <b>之前</b>先对 <b>比价行</b>取得排它行锁（<see cref="QuoteRowLockSql"/>，
/// 仅用 EF Core 基础 API 的「审计时间戳刷新」<c>UPDATE</c>，语义等价 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>，
/// 持有至事务结束）。批量删除 / 批次转单按 <b>Id 升序</b>确定性加锁（去重、仅正整数），绝不反向获取其它锁，
/// 因此不存在锁环；同一比价行的并发等价请求被串行化在同一原子事务内。</para>
/// <para><b>锁内权威复核</b>：取得行锁之后必须重新加载比价行（不复用加锁前的内存实体），由调用方复核
/// 「存在 / 未删除 + 实时身份 / 既有菜单 / 权威客户范围 + 已决定 / 已转换冻结」，任一不满足即原子拒绝。</para>
/// <para><b>对比草稿校验</b>（仅客户端可维护的待比较 / 已选中 / 已放弃草稿）：数量必须为正、价格为非负且都在 EF 精度
/// （<c>DECIMAL(18,4)</c>）内可表示；币种必须是受支持口径；选中标记与状态必须一致；含税总额一律由服务端按
/// 「单价 × 数量」在 EF 精度重算（绝不采信客户端 <c>TotalAmount</c>）；明确拒绝客户端伪造的
/// 「已转采购订单」状态、采购单号式 <c>RefOrderNo</c> 转换证据与审计字段。</para>
/// <para><b>边界</b>：本类只做锁定、事务、校验与字段映射；不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，
/// 不伪造任何授权，也绝不把空身份当作匿名或管理员；不改写审批决定历史、采购订单、库存 / 财务记录与来源单据。</para>
/// </summary>
public static class PurchaseQuoteMutationRules
{
    /// <summary>
    /// 比价行锁语句（契约 / 文档同源）：<c>UPDLOCK, HOLDLOCK</c> 语义等价可串行化行锁，持有至调用方事务结束。
    /// 实际执行走 <see cref="LockQuoteRowAsync"/> 的「审计时间戳刷新」UPDATE（仅用 EF Core 基础 API）。
    /// </summary>
    public const string QuoteRowLockSql =
        "SELECT Id FROM db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    public const int RowLockRetryAttempts = 8;

    private const int LockRetryAttempts = RowLockRetryAttempts;

    // ==================== 比价状态口径（与 PurchaseQuoteConversion 同源，常量值必须逐字一致） ====================

    /// <summary>比价状态：待比较（客户端可维护的「未决定」草稿态之一）。</summary>
    public const string PendingStatus = "待比较";

    /// <summary>比价状态：已选中。</summary>
    public const string SelectedStatus = "已选中";

    /// <summary>比价状态：已放弃。</summary>
    public const string DiscardedStatus = "已放弃";

    /// <summary>比价状态：已转采购订单（服务端转换写入，客户端一律不得伪造）。</summary>
    public const string ConvertedStatus = "已转采购订单";

    /// <summary>受支持的币种口径（与实体注释 / 既有数据一致；不引入汇率换算）。</summary>
    public static readonly IReadOnlyList<string> SupportedCurrencies = new[] { "CNY", "USD" };

    /// <summary>EF / 数据库存储精度：<c>DECIMAL(18,4)</c>（数量 / 单价 / 含税总额）。</summary>
    public const int DecimalPrecision = 18;

    /// <summary>EF / 数据库存储小数位：4。</summary>
    public const int DecimalScale = 4;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示绝对值（18 位有效数字、4 位小数）。</summary>
    public const decimal MaxDecimal18Scale4 = 99_999_999_999_999.9999m;

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-417 供应商比价生命周期与审批决定的确定性锁协议：修改 / 删除 / 批量删除 / 审批决定 / 转采购订单都在" +
        "「比价行锁」（db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE）内串行化；" +
        "批量删除与批次转单按比价行 Id 升序确定性加锁（去重、仅正整数），绝不反向获取其它锁。";

    /// <summary>边界文案（不新增权限 / 表列，不改写审批历史与库存 / 财务）。</summary>
    public const string BoundaryText =
        "本护栏只保护供应商比价生命周期、比价审批决定与比价 → 采购订单转换的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，" +
        "也不把空身份当作管理员；不改写审批决定历史（append-only，一比价行至多一条有效决定），不改写采购订单、库存与库存流水、" +
        "发票 / 退税 / 费用或财务记录，不删除历史证据；行锁只刷新比价行技术审计时间戳 UpdatedAt（非商业证据）。";

    /// <summary>并发方持续改写同一比价行时对外给出的拒绝文案。</summary>
    public const string ConcurrentMutationText =
        "目标比价行正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>持久化 <c>RowVersion</c> 与调用方持有的乐观令牌不一致（被并发改写）时的拒绝文案。</summary>
    public const string StaleRowVersionText =
        "该比价行已被并发操作改写（RowVersion 过期）：本次操作未生效，请刷新后重试（绝不覆盖赢家，原始证据均未改变）";

    /// <summary>比价行已存在审批决定后商业字段不可再改动的拒绝文案（仅允许安全非商业备注修改）。</summary>
    public const string DecidedImmutableText =
        "该比价行已存在审批决定（已批准选中供应商 / 已拒绝）：商业条款不得再修改，仅允许修改非商业备注" +
        "（fail closed，绝不改写审批依据的供应商 / 价格 / 数量 / 币种 / 选中状态 / 批次，也绝不删除）";

    /// <summary>已存在（实时审批生命周期追加的）归属审批决定的比价行不得删除的拒绝文案。</summary>
    public const string DecidedNoDeleteText =
        "该比价行已存在由实时审批生命周期追加的审批决定：不得删除（保留原始审批历史；如需废止请走既有审批流程，绝不硬删除）";

    /// <summary>
    /// 决定是否属于**由实时可信审批生命周期追加、带操作人归属**的决定（<c>CreatedBy</c> 由登录账号 / 请求决定人写入）。
    /// <para><b>删除冻结口径（ERP-417）</b>：只有归属决定才冻结所在比价行的软删除；未归属决定
    /// （历史 / 种子 / 导入数据，无 <c>CreatedBy</c>）保留 ERP-416 既有「本人行可软删除」契约——
    /// 软删除只隐藏比价行，<see cref="PurchaseQuoteDecision"/> 审批证据行绝不被删除 / 修改，血缘依旧完整，
    /// 也绝不存在任何硬删除入口。</para>
    /// </summary>
    public static bool IsAttributedDecision(PurchaseQuoteDecision? decision)
        => decision is not null && decision.CreatedBy is > 0;

    /// <summary>已转采购订单的比价行不可再修改 / 删除的拒绝文案。</summary>
    public const string ConvertedImmutableText =
        "该比价行已转采购订单：状态 / 商业条款 / 删除均不可再变更（保留原始转换与审批历史）";

    /// <summary>数量非法（非正 / 超出 EF 精度）的拒绝文案。</summary>
    public const string QuantityText =
        "需求数量必须为正数且不超过 DECIMAL(18,4) 可表示范围（最多 4 位小数）";

    /// <summary>单价非法（负数 / 超出 EF 精度）的拒绝文案。</summary>
    public const string PriceText =
        "报价单价必须为非负数且不超过 DECIMAL(18,4) 可表示范围（最多 4 位小数）";

    /// <summary>含税总额超出 EF 精度的拒绝文案。</summary>
    public const string TotalText =
        "服务端计算的报价总额超出 DECIMAL(18,4) 可表示范围：请缩小数量或单价";

    /// <summary>币种不受支持的拒绝文案。</summary>
    public const string CurrencyText = "币种不受支持：仅接受 CNY / USD";

    /// <summary>状态不在客户端可维护口径内的拒绝文案。</summary>
    public const string StatusText = "比价状态非法：仅接受 待比较 / 已选中 / 已放弃";

    /// <summary>选中标记与状态不一致的拒绝文案。</summary>
    public const string SelectionText =
        "选中标记与比价状态不一致：仅当状态为「已选中」时才能勾选「选中该供应商」，反之必须取消选中";

    /// <summary>批次号缺失 / 过长的拒绝文案。</summary>
    public const string QuoteNoText = "比价批次号不能为空且不超过 50 个字符";

    /// <summary>客户端伪造「已转采购订单」状态的拒绝文案。</summary>
    public const string ConvertedForgedText =
        "「已转采购订单」状态由服务端转换写入，客户端不得伪造（请走既有「转采购订单」路由）";

    /// <summary>客户端伪造 <c>RefOrderNo</c> 转换证据的拒绝文案。</summary>
    public const string RefOrderEvidenceText =
        "不得把 RefOrderNo 指定为既有采购单号来伪造转换证据（转换链接由服务端写入；转换前该列仅表示关联销售订单号）";

    // ==================== 1. 关系型判定 / 事务 / 行锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => TradeDocumentMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 生命周期变更 / 审批决定 / 转换使用的原子事务（复用 ERP-395 既有口径）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时<strong>绝不嵌套</strong>（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => TradeDocumentMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>事务回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
        => TradeDocumentMutationRules.DiscardTrackedChanges(db);

    /// <summary>
    /// 合并需要加锁的比价行 Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeLockIds(IEnumerable<long>? ids)
        => TradeDocumentMutationRules.MergeDocumentLockIds(ids);

    /// <summary>
    /// 对 <b>比价行</b>加排它行锁：把「同单并发的生命周期变更 / 删除 / 批量删除 / 审批决定 / 转采购订单」
    /// 串行化在同一事务内。返回 <c>false</c> 表示该行不存在 / 已被并发删除。
    /// <para>实现与 ERP-395 父单证行锁同源（仅用 EF Core 基础 API）：在调用方事务内对比价行发一条
    /// 「审计时间戳刷新」<c>UPDATE</c>（<c>UpdatedAt</c> 为技术审计字段、不是商业证据），取得排它行锁（X 锁，
    /// 持有至事务结束）；并发方先提交使本地乐观令牌 <c>RowVersion</c> 过期时重读权威行后<b>有界重试</b>，
    /// 绝不把纯粹锁等待误报成业务拒绝。锁内必须由调用方重新加载权威行复核。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内权威重读判定）。</para>
    /// </summary>
    public static async Task<bool> LockQuoteRowAsync(IErpDbContext db, long quoteId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quoteId <= 0) return false;
        if (!IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var quote = await db.PurchaseQuotes
                .FirstOrDefaultAsync(q => q.Id == quoteId && !q.IsDeleted, ct);
            if (quote is null) return false;

            quote.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    try
                    {
                        await entry.ReloadAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return false; // 比价行已被并发事务删除
                    }
                }

                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }

    /// <summary>对一组比价行按 <b>Id 升序</b>确定性加锁（批量删除 / 批次转单）。非关系型提供程序跳过。</summary>
    public static async Task LockQuoteRowsAsync(IErpDbContext db, IEnumerable<long>? quoteIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeLockIds(quoteIds))
            await LockQuoteRowAsync(db, id, ct);
    }

    // ==================== 2. 对比草稿校验（纯内存判定，服务端唯一权威） ====================

    /// <summary>状态归一化：去首尾空白；空值按「待比较」处理（与实体默认值同源）。</summary>
    public static string NormalizeStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        return value.Length == 0 ? PendingStatus : value;
    }

    /// <summary>状态是否处于客户端可维护口径内（待比较 / 已选中 / 已放弃；「已转采购订单」为服务端写入）。</summary>
    public static bool IsClientStatus(string? status)
        => status is PendingStatus or SelectedStatus or DiscardedStatus;

    /// <summary>值是否在 <c>DECIMAL(18,4)</c> 精度内可表示（最多 4 位小数、18 位有效数字）。</summary>
    public static bool IsRepresentable(decimal value)
        => decimal.Round(value, DecimalScale) == value && Math.Abs(value) <= MaxDecimal18Scale4;

    /// <summary>服务端按「单价 × 数量」在 EF 精度重算含税总额（绝不采信客户端 <c>TotalAmount</c>）。</summary>
    public static decimal CalculateTotalAmount(decimal quotePrice, decimal quantity)
        => decimal.Round(quotePrice * quantity, DecimalScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 拒绝客户端伪造的转换证据：状态不得为「已转采购订单」；<c>RefOrderNo</c> 不得指向既有未删除采购单号
    /// （转换链接只由服务端 <c>PurchaseQuoteConversion.MarkConverted</c> 写入；转换前该列仅表示关联销售订单号）。
    /// </summary>
    public static async Task EnsureNoForgedConversionEvidenceAsync(IErpDbContext db, PurchaseQuote quote,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(quote);

        if (NormalizeStatus(quote.Status) == ConvertedStatus)
            throw BusinessException.RuleConflict(ConvertedForgedText);

        var refNo = (quote.RefOrderNo ?? string.Empty).Trim();
        if (refNo.Length == 0) return;

        var looksLikeConversion = await db.PurchaseOrders.AsNoTracking()
            .AnyAsync(o => o.OrderNo == refNo && !o.IsDeleted, ct);
        if (looksLikeConversion)
            throw BusinessException.RuleConflict(
                $"{RefOrderEvidenceText}（RefOrderNo「{refNo}」已是既有采购单号）");
    }

    /// <summary>
    /// 待比较草稿校验（服务端唯一权威）：批次号非空且有界；数量为正、价格为非负且都在 EF 精度内可表示；
    /// 币种受支持；状态处于客户端可维护口径且与选中标记一致；含税总额由服务端重算覆盖。
    /// 校验通过后 <paramref name="quote"/> 的状态 / 币种 / 总额 / 批次号被归一化为权威值。
    /// </summary>
    public static void ValidateDraft(PurchaseQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var quoteNo = (quote.QuoteNo ?? string.Empty).Trim();
        if (quoteNo.Length == 0 || quoteNo.Length > 50)
            throw BusinessException.InvalidParameter(QuoteNoText);

        var rawStatus = NormalizeStatus(quote.Status);
        if (!IsClientStatus(rawStatus))
            throw BusinessException.InvalidParameter(StatusText);

        if (quote.IsSelected != string.Equals(rawStatus, SelectedStatus, StringComparison.Ordinal))
            throw BusinessException.InvalidParameter(SelectionText);

        if (quote.Quantity <= 0 || !IsRepresentable(quote.Quantity))
            throw BusinessException.InvalidParameter(QuantityText);

        if (quote.QuotePrice < 0 || !IsRepresentable(quote.QuotePrice))
            throw BusinessException.InvalidParameter(PriceText);

        var currency = (quote.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (!SupportedCurrencies.Contains(currency, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(CurrencyText);

        var total = CalculateTotalAmount(quote.QuotePrice, quote.Quantity);
        if (!IsRepresentable(total))
            throw BusinessException.InvalidParameter(TotalText);

        quote.QuoteNo = quoteNo;
        quote.Status = rawStatus;
        quote.Currency = currency;
        quote.TotalAmount = total;
        quote.Remark = Clamp(quote.Remark, 500);
        if (quote.QuoteDate == default) quote.QuoteDate = DateTime.Today;
    }

    /// <summary>新增时重置客户端可伪造的审计 / 主键 / 软删除 / 并发令牌字段（一律以服务端为准）。</summary>
    public static void NormalizeForCreate(PurchaseQuote quote, long? actorUserId)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var now = DateTime.Now;
        quote.Id = 0;
        quote.IsDeleted = false;
        quote.RowVersion = null;
        quote.CreatedAt = now;
        quote.CreatedBy = actorUserId;
        quote.UpdatedAt = now;
        quote.UpdatedBy = actorUserId;
    }

    /// <summary>
    /// 已存在有效审批决定的比价行是否只改动了<b>非商业备注</b>（显式映射的唯一安全白名单字段）。
    /// 其余全部字段（批次号 / 商品 / 供应商 / 数量 / 单价 / 总额 / 币种 / 含税 / 交期 / 起订量 / 付款条件 /
    /// 选中标记 / 状态 / 客户 / 关联销售订单号 / 报价日期等）任一变化都判为非「仅备注」，
    /// 因而在已决定 / 已转换冻结口径下被拒绝，绝不覆盖审批依据或血缘。
    /// </summary>
    public static bool IsRemarkOnlyChange(PurchaseQuote stored, PurchaseQuote proposed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(proposed);

        return stored.QuoteDate == proposed.QuoteDate
               && stored.ProductId == proposed.ProductId
               && stored.Quantity == proposed.Quantity
               && stored.SupplierId == proposed.SupplierId
               && stored.QuotePrice == proposed.QuotePrice
               && stored.TotalAmount == proposed.TotalAmount
               && stored.TaxIncluded == proposed.TaxIncluded
               && stored.DeliveryDays == proposed.DeliveryDays
               && stored.MinOrderQty == proposed.MinOrderQty
               && stored.IsSelected == proposed.IsSelected
               && stored.CustomerId == proposed.CustomerId
               && TextEquals(stored.QuoteNo, proposed.QuoteNo)
               && TextEquals(stored.ProductName, proposed.ProductName)
               && TextEquals(stored.Spec, proposed.Spec)
               && TextEquals(stored.Unit, proposed.Unit)
               && TextEquals(stored.SupplierName, proposed.SupplierName)
               && TextEquals(stored.SupplierType, proposed.SupplierType)
               && TextEquals(stored.Currency, proposed.Currency)
               && TextEquals(stored.PaymentTerms, proposed.PaymentTerms)
               && TextEquals(stored.Status, proposed.Status)
               && TextEquals(stored.CustomerName, proposed.CustomerName)
               && TextEquals(stored.RefOrderNo, proposed.RefOrderNo);
    }

    /// <summary>
    /// 把待比较草稿的<b>可编辑商业字段</b>写入权威在库实体：绝不触碰主键 / 审计字段 / 软删除标记 /
    /// 并发令牌；<c>TotalAmount</c> 已在 <see cref="ValidateDraft"/> 由服务端重算。
    /// </summary>
    public static void ApplyEditableFields(PurchaseQuote stored, PurchaseQuote proposed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(proposed);

        stored.QuoteNo = proposed.QuoteNo;
        stored.QuoteDate = proposed.QuoteDate;
        stored.ProductId = proposed.ProductId;
        stored.ProductName = Clamp(proposed.ProductName, 200);
        stored.Spec = Clamp(proposed.Spec, 200);
        stored.Unit = Clamp(proposed.Unit, 20);
        stored.Quantity = proposed.Quantity;
        stored.SupplierId = proposed.SupplierId;
        stored.SupplierName = Clamp(proposed.SupplierName, 200);
        stored.SupplierType = Clamp(proposed.SupplierType, 20);
        stored.QuotePrice = proposed.QuotePrice;
        stored.TotalAmount = proposed.TotalAmount;
        stored.Currency = proposed.Currency;
        stored.TaxIncluded = proposed.TaxIncluded;
        stored.DeliveryDays = proposed.DeliveryDays;
        stored.MinOrderQty = proposed.MinOrderQty;
        stored.PaymentTerms = Clamp(proposed.PaymentTerms, 100);
        stored.IsSelected = proposed.IsSelected;
        stored.Status = proposed.Status;
        stored.CustomerId = proposed.CustomerId;
        stored.CustomerName = Clamp(proposed.CustomerName, 200);
        stored.RefOrderNo = Clamp(proposed.RefOrderNo, 50);
        stored.Remark = Clamp(proposed.Remark, 500);
    }

    /// <summary>安全非商业备注修改：只在允许的备注白名单字段上写入，绝不覆盖其它血缘字段。</summary>
    public static void ApplyRemarkOnly(PurchaseQuote stored, PurchaseQuote proposed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(proposed);
        stored.Remark = Clamp(proposed.Remark, 500);
    }

    private static bool TextEquals(string? left, string? right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static string Clamp(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

